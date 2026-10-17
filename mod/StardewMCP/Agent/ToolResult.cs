using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;

namespace StardewMCP.Agent;

/// <summary>
/// Reason codes for tool results: one fixed vocabulary instead of per-tool wording ("energy too low",
/// "Ran out of seeds."...). A tool sets one with <c>c.Fail(Reasons.X, message)</c> (failure) or
/// through <see cref="CallContext.Batch"/> (partly done). Tools that do not set one keep working:
/// their result has no reason and the reply carries no tag.
/// Keep this list in step with runner/tool_results.py (a test compares the two).
/// </summary>
public static class Reasons
{
    /// <summary>Reported for a result with no reason that did not fail. Tools never need to set it.</summary>
    public const string Ok = "ok";
    /// <summary>Only part of the request was done and no more specific reason is known.</summary>
    public const string Partial = "partial";
    public const string LowEnergy = "low_energy";
    public const string NoPath = "no_path";
    /// <summary>The thing asked for is not on this tile / map / screen (villager, shop, dialogue, target tile).</summary>
    public const string NotHere = "not_here";
    /// <summary>An item, tool, seed or ingredient is missing, or sold out.</summary>
    public const string NoItem = "no_item";
    public const string InventoryFull = "inventory_full";
    public const string MenuOpen = "menu_open";
    public const string TooLate = "too_late";
    public const string ShopClosed = "shop_closed";
    public const string WrongSeason = "wrong_season";
    public const string NeedsUpgrade = "needs_upgrade";
    /// <summary>The action stopped before it finished because something happened (passed out, a new day, a perception event).</summary>
    public const string Interrupted = "interrupted";
    public const string Timeout = "timeout";
    // Not in the issue's first list, but common enough to deserve a code:
    public const string NoMoney = "no_money";
    public const string InvalidArgs = "invalid_args";
    public const string NotFound = "not_found";
    public const string NotReady = "not_ready";
    /// <summary>The tool crashed (an exception inside the mod).</summary>
    public const string Error = "error";
    /// <summary>A failure whose tool sets no reason yet. Reported by the server, tools should set a real code instead.</summary>
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Ok, Partial, LowEnergy, NoPath, NotHere, NoItem, InventoryFull, MenuOpen, TooLate, ShopClosed,
        WrongSeason, NeedsUpgrade, Interrupted, Timeout, NoMoney, InvalidArgs, NotFound, NotReady, Error, Other
    };
}

/// <summary>Reason, partial flag and counts of a tool call; the extra members of <see cref="CallContext"/> live here to keep AgentRuntime.cs small.</summary>
public partial class CallContext
{
    /// <summary>One of <see cref="Reasons"/>, or null when the tool did not classify the result.</summary>
    public string? Reason;
    /// <summary>The call did some of what was asked, not all of it (see <see cref="Data"/> for how much).</summary>
    public bool Partial;
    /// <summary>Small machine-readable numbers for the result (done, remaining, skipped...). Shown to the model in structuredContent.data.</summary>
    public Dictionary<string, object>? Data;

    /// <summary>Fail with a reason code. <c>Fail(message)</c> still works and leaves the reason empty.</summary>
    public void Fail(string reason, string message)
    {
        Fail(message);
        Reason = reason;
    }

    /// <summary>
    /// Report a batch tool: <paramref name="done"/> of <paramref name="total"/> items done, <paramref name="skipped"/> tried and
    /// failed, the rest never attempted. Sets Data (done, remaining, skipped) and, when not everything was done, Partial plus a reason
    /// (<paramref name="stopReason"/> if the batch stopped early, else <paramref name="skipReason"/>, else "partial").
    /// Ok stays true: a batch that did part of its work is a normal result, not an error.
    /// </summary>
    public void Batch(int total, int done, int skipped, string? stopReason = null, string? skipReason = null)
    {
        int remaining = Math.Max(0, total - done - skipped);
        Data = new Dictionary<string, object> { ["done"] = done, ["remaining"] = remaining, ["skipped"] = skipped };
        if (remaining == 0 && skipped == 0) return;
        Partial = true;
        Reason ??= stopReason ?? skipReason ?? Reasons.Partial;
    }
}

/// <summary>Counts for a batch tool's loop: <c>Done++</c>, <c>Skip(why)</c>, <c>Stop = reason; break;</c>, then <c>Apply(c, total)</c>.</summary>
public sealed class BatchTally
{
    public int Done;
    public int Skipped;
    /// <summary>Why the loop stopped before reaching every item (energy, a menu...).</summary>
    public string? Stop;
    /// <summary>The first reason an item was skipped for.</summary>
    public string? SkipWhy;

    public void Skip(string? why = null)
    {
        Skipped++;
        SkipWhy ??= why;
    }

    public void Apply(CallContext c, int total) => c.Batch(total, Done, Skipped, Stop, SkipWhy);
}

/// <summary>What a finished call reports besides its text: a snapshot of the CallContext taken when the call ends.</summary>
public sealed record ToolOutcome(bool Ok, string? Reason, bool Partial, IReadOnlyDictionary<string, object>? Data)
{
    public static ToolOutcome From(CallContext c) =>
        new(c.Ok, c.Reason, c.Partial, c.Data is { Count: > 0 } ? new Dictionary<string, object>(c.Data) : null);

    /// <summary>The reason code to report: the tool's own, else "partial", "ok" or "other" from the flags.</summary>
    public string Code => Reason is { Length: > 0 } r ? r : Partial ? Reasons.Partial : Ok ? Reasons.Ok : Reasons.Other;
}

/// <summary>Builds the MCP reply for a finished tool call: tagged text plus structuredContent (MCP 2025-06-18).</summary>
public static class ToolResult
{
    /// <summary>
    /// "[partial · low_energy]", "[no_path]" or "" for a plain result. The tag only appears when the tool classified
    /// the result or did part of its work, so tools that have not adopted reason codes look exactly as before.
    /// </summary>
    public static string Tag(ToolOutcome o)
    {
        var parts = new List<string>(2);
        if (o.Partial) parts.Add(Reasons.Partial);
        if (o.Reason is { Length: > 0 } r && r != Reasons.Ok && r != Reasons.Partial) parts.Add(r);
        return parts.Count == 0 ? "" : "[" + string.Join(" · ", parts) + "]";
    }

    /// <summary>The structuredContent object: <c>{ok, reason, partial?, data?}</c>. <c>ok</c> is the MCP isError flag inverted.</summary>
    public static Dictionary<string, object> Structured(ToolOutcome o)
    {
        var d = new Dictionary<string, object> { ["ok"] = o.Ok, ["reason"] = o.Code };
        if (o.Partial) d["partial"] = true;
        if (o.Data is { Count: > 0 }) d["data"] = o.Data;
        return d;
    }

    /// <summary>The <c>result</c> of a tools/call reply. The text content keeps its wording and only gains the tag in front.</summary>
    public static object McpReply(bool ok, string text, ToolOutcome? outcome)
    {
        var o = outcome ?? new ToolOutcome(ok, null, false, null);
        var tag = Tag(o);
        return new
        {
            content = new[] { new { type = "text", text = tag.Length > 0 ? tag + " " + text : text } },
            isError = !o.Ok,
            structuredContent = Structured(o)
        };
    }

    /// <summary>A reply for a call that never reached a tool (unknown tool, seat not in the game).</summary>
    public static object McpError(string reason, string text) =>
        McpReply(false, text, new ToolOutcome(false, reason, false, null));
}

// ---------------------------------------------------------------------- per-day summary

/// <summary>Result counts of one tool or one player: how often calls failed or were partial, and why.</summary>
public class ResultCounts
{
    /// <summary>Logged results. Instant observations (look, status...) that succeed are not logged, so they are not counted.</summary>
    public int Calls { get; set; }
    public int Failed { get; set; }
    public int Partial { get; set; }
    /// <summary>Reason code -> results, for failed and partial results (and any result a tool gave a reason for).</summary>
    [JsonPropertyName("reasons")]
    public SortedDictionary<string, int> ByReason { get; set; } = new();

    public void Add(bool failed, bool wasPartial, string? reason)
    {
        Calls++;
        if (failed) Failed++;
        if (wasPartial) Partial++;
        var key = reason is { Length: > 0 } ? reason : failed ? Reasons.Other : wasPartial ? Reasons.Partial : null;
        if (key == null || key == Reasons.Ok) return;
        ByReason[key] = ByReason.TryGetValue(key, out var n) ? n + 1 : 1;
    }
}

public class PlayerResultSummary
{
    public ResultCounts Total { get; set; } = new();
    public SortedDictionary<string, ResultCounts> Tools { get; set; } = new();
}

/// <summary>day_XXX_summary.json: failure reasons per tool and per player for one in-game day, built from the day's JSONL log.</summary>
public class DaySummaryData
{
    public int Day { get; set; }
    public string Updated { get; set; } = "";
    public ResultCounts Total { get; set; } = new();
    public SortedDictionary<string, ResultCounts> Tools { get; set; } = new();
    public SortedDictionary<string, PlayerResultSummary> Players { get; set; } = new();
}

/// <summary>
/// Writes day_XXX_summary.json next to the day's log. It is rebuilt from the JSONL file each time, so it survives a game restart
/// and needs no state of its own. Written at most every 30 s while results come in, when the day number changes, and at day end.
/// </summary>
public static class DaySummary
{
    private static readonly object Gate = new();
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static int _day;
    private static DateTime _lastWrite = DateTime.MinValue;

    public static string FileName(int day) => $"day_{day:000}_summary.json";

    /// <summary>Call after a result line was logged for <paramref name="day"/>.</summary>
    public static void Touch(AgentLog log, int day, IMonitor? monitor = null)
    {
        if (day <= 0) return;
        int previous = 0;
        bool due;
        lock (Gate)
        {
            if (_day > 0 && _day != day) previous = _day;
            _day = day;
            due = DateTime.UtcNow - _lastWrite >= Interval;
            if (due) _lastWrite = DateTime.UtcNow;
        }
        if (previous > 0) Write(log, previous, monitor);
        if (due) Write(log, day, monitor);
    }

    public static void Write(AgentLog log, int day, IMonitor? monitor = null)
    {
        try
        {
            var source = log.DayFile(day);
            if (day <= 0 || !File.Exists(source)) return;
            var summary = Build(day, ReadLines(source));
            File.WriteAllText(Path.Combine(log.Dir, FileName(day)), JsonSerializer.Serialize(summary, AgentLog.Json), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            monitor?.Log($"Day summary failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>Count the "result" lines of one day's JSONL. Lines that are not valid JSON (a half-written last line) are skipped.</summary>
    public static DaySummaryData Build(int day, IEnumerable<string> jsonlLines)
    {
        var s = new DaySummaryData { Day = day, Updated = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") };
        foreach (var line in jsonlLines)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || Str(r, "kind") != "result") continue;
                var tool = Str(r, "tool") ?? "?";
                var player = Str(r, "player") ?? "p1";
                bool failed = r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False;
                bool wasPartial = r.TryGetProperty("partial", out var pEl) && pEl.ValueKind == JsonValueKind.True;
                var reason = Str(r, "reason");

                s.Total.Add(failed, wasPartial, reason);
                Get(s.Tools, tool).Add(failed, wasPartial, reason);
                if (!s.Players.TryGetValue(player, out var ps)) s.Players[player] = ps = new PlayerResultSummary();
                ps.Total.Add(failed, wasPartial, reason);
                Get(ps.Tools, tool).Add(failed, wasPartial, reason);
            }
            catch (JsonException)
            {
            }
        }
        return s;
    }

    private static ResultCounts Get(SortedDictionary<string, ResultCounts> map, string key)
    {
        if (!map.TryGetValue(key, out var c)) map[key] = c = new ResultCounts();
        return c;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The log file is appended to by the game while we read it, so share it.</summary>
    private static List<string> ReadLines(string path)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) != null) lines.Add(line);
        return lines;
    }
}
