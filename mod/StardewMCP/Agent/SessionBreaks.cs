using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// Planned session breaks (#30). One <c>claude -p</c> session per in-game day keeps every old map and result in
/// its context, and each turn re-sends all of it. With breaks the mod tells the AI to continue in a new session
/// at fixed game times (<see cref="ModConfig.SessionBreakTimes"/>) or after too many calls or too much result text;
/// the runner then starts the next part with a short handoff (runner/sessions.py). A break is the mod's way to say
/// "stop here", never an error, and it does not count toward the runner's --max-parts.
///
/// Only MCP clients that opt in are affected: connect to <c>/mcp?breaks=1</c> (or set
/// <see cref="ModConfig.SessionBreaksForAll"/>). A session is one MCP <c>initialize</c> .. the next <c>initialize</c>.
/// After the notice, actions are refused (the AI should call think with its plan and end its turn); observations,
/// think, chat and go_to_bed still work. A client that ignores the notice five times is not held back any longer.
/// </summary>
public static class SessionBreaks
{
    /// <summary>The runner looks for this at the start of a line in tool results.</summary>
    public const string Marker = "Session break:";

    private const int MaxRefusals = 5;
    private const int NoBreakFrom = 2300; // late evening: the AI should get to bed, not restart

    private const string HowToContinue =
        "Do this now: call think with your plan for the rest of the day (what you are in the middle of, what comes next, what to remember), " +
        "then end your turn with a one-line reply and make no more tool calls. A new session starts by itself and receives your plan. " +
        "If it is already bedtime, call go_to_bed instead.";

    private sealed class Seat
    {
        public readonly object Lock = new();
        public bool Armed;          // this session wants breaks
        public bool Fresh = true;   // a new MCP session began; counters restart on the game thread
        public int Calls;
        public int ResultChars;
        public int Refusals;
        public bool Announced;
        public readonly HashSet<(int day, int time)> TimesDone = new();
    }

    private static readonly ConcurrentDictionary<string, Seat> Seats = new();

    private static Seat For(string player) => Seats.GetOrAdd(player, _ => new Seat());

    private static ModConfig Cfg => AgentTools.Config;

    /// <summary>Debug ops are the only tools without mail; they are not part of the AI's session.</summary>
    private static bool IsAgentTool(ToolDef tool) => tool.Mail;

    // ------------------------------------------------------------------ HTTP thread

    /// <summary>
    /// Called for every MCP request of a seat. <c>initialize</c> starts a new session; <c>?breaks=1</c> on the URL
    /// (or <see cref="ModConfig.SessionBreaksForAll"/>) turns breaks on for it.
    /// </summary>
    public static void Observe(string player, JsonElement request, string? breaksQuery)
    {
        bool wanted = Cfg.SessionBreaksForAll || breaksQuery == "1";
        var seat = For(player);
        lock (seat.Lock)
        {
            if (HasInitialize(request))
            {
                seat.Fresh = true;
                seat.Armed = wanted;
            }
            else if (wanted)
            {
                seat.Armed = true;
            }
        }
    }

    private static bool HasInitialize(JsonElement root)
    {
        static bool Is(JsonElement m) =>
            m.ValueKind == JsonValueKind.Object && m.TryGetProperty("method", out var v) &&
            v.ValueKind == JsonValueKind.String && v.GetString() == "initialize";
        return root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().Any(Is) : Is(root);
    }

    // ------------------------------------------------------------------ game thread (AgentRuntime lane)

    /// <summary>
    /// Before a call starts: once the notice has been given, refuse actions that let game time pass.
    /// go_to_bed is always allowed. Returns true with the text to give the AI instead.
    /// </summary>
    public static bool Refuse(PlayerLog log, PendingCall call, out string message)
    {
        message = "";
        if (!IsAgentTool(call.Tool))
            return false;
        var seat = For(log.Player);
        EnsureStarted(seat);
        lock (seat.Lock)
        {
            if (!seat.Armed || !seat.Announced || call.Instant || call.Tool.Name == "go_to_bed")
                return false;
            if (++seat.Refusals > MaxRefusals)
            {
                seat.Armed = false;
                log.Add("system", text: $"Session break ignored {MaxRefusals} times: not enforcing it for the rest of this session");
                return false;
            }
            message = $"{Marker} no more actions in this session. {HowToContinue}";
            return true;
        }
    }

    /// <summary>
    /// After a call finished: count it and, when a threshold is reached, add the break notice to its result.
    /// The call itself is never cut short; a long walk or wait finishes first.
    /// </summary>
    public static string Annotate(PlayerLog log, PendingCall call, string text)
    {
        if (!IsAgentTool(call.Tool))
            return text;
        var seat = For(log.Player);
        EnsureStarted(seat);
        lock (seat.Lock)
        {
            if (!seat.Armed || text.StartsWith(Marker, StringComparison.Ordinal))
                return text;
            seat.Calls++;
            seat.ResultChars += text.Length;
            if (seat.Announced || call.Tool.Name == "go_to_bed" || !Context.IsWorldReady)
                return text;
            var reason = Due(seat);
            if (reason == null)
                return text;
            seat.Announced = true;
            log.Add("system", text: $"{Marker} continue in a new session ({reason})");
            return text.TrimEnd() + $"\n{Marker} continue in a new session ({reason}). Your context is long and holds old maps that no longer match the world. {HowToContinue}";
        }
    }

    /// <summary>Why the AI should break now, or null. Never inside a menu or late in the evening.</summary>
    private static string? Due(Seat seat)
    {
        if (Game1.activeClickableMenu != null || Game1.timeOfDay >= NoBreakFrom)
            return null;

        int day = Game1.Date.TotalDays;
        var times = Cfg.SessionBreakTimes ?? Array.Empty<int>();
        foreach (var t in times.OrderBy(t => t))
        {
            if (Game1.timeOfDay < t || seat.TimesDone.Contains((day, t)))
                continue;
            foreach (var u in times.Where(u => Game1.timeOfDay >= u))
                seat.TimesDone.Add((day, u));
            return $"it is {WorldText.FormatTime(Game1.timeOfDay)}, past the planned break at {WorldText.FormatTime(t)}";
        }
        if (Cfg.SessionBreakToolCalls > 0 && seat.Calls >= Cfg.SessionBreakToolCalls)
            return $"{seat.Calls} tool calls in this session";
        if (Cfg.SessionBreakResultChars > 0 && seat.ResultChars >= Cfg.SessionBreakResultChars)
            return $"about {seat.ResultChars / 4000}k tokens of results in this session";
        return null;
    }

    /// <summary>First call after a new MCP session began: restart the counters and skip break times that already passed.</summary>
    private static void EnsureStarted(Seat seat)
    {
        lock (seat.Lock)
        {
            if (!seat.Fresh)
                return;
            seat.Fresh = false;
            seat.Calls = 0;
            seat.ResultChars = 0;
            seat.Refusals = 0;
            seat.Announced = false;
            seat.TimesDone.Clear();
            if (!Context.IsWorldReady)
                return;
            int day = Game1.Date.TotalDays;
            foreach (var t in (Cfg.SessionBreakTimes ?? Array.Empty<int>()).Where(t => Game1.timeOfDay >= t))
                seat.TimesDone.Add((day, t));
        }
    }
}
