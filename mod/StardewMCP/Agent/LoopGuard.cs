using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>Which rule of the loop guard fired.</summary>
public enum GuardKind
{
    /// <summary>The same call failed several times in a row: a hint is added to its result.</summary>
    Repeat,
    /// <summary>The same call kept failing, so it was refused without running.</summary>
    Refused,
    /// <summary>Many calls in a row changed nothing in the game.</summary>
    NoProgress
}

/// <summary>One hint (or refusal) produced by <see cref="LoopDetector"/>.</summary>
public sealed class GuardNote
{
    public GuardKind Kind { get; }
    /// <summary>Text for the model: appended to the call's result, or the whole result of a refused call.</summary>
    public string Text { get; }
    /// <summary>Failures in a row (repeat, refused) or calls in a row (no progress) that triggered it.</summary>
    public int Count { get; }

    /// <summary>Stable name for the day log: repeat | refused | no_progress.</summary>
    public string Type => Kind switch
    {
        GuardKind.Repeat => "repeat",
        GuardKind.Refused => "refused",
        _ => "no_progress"
    };

    public GuardNote(GuardKind kind, string text, int count)
    {
        Kind = kind;
        Text = text;
        Count = count;
    }
}

/// <summary>
/// The detection rules of the loop guard (#31), kept free of game types so they are easy to read:
/// everything comes in as strings, bools and ints. <see cref="LoopGuard"/> feeds it from the game.
///
/// Repeat detector: a ring buffer of the last calls (tool + normalized arguments + ok). The k-th identical call
/// in a row, after k-1 failures, gets a hint once k reaches <c>hintAfter</c> (3 by default); the call after
/// <c>blockAfter - 1</c> failures (the 5th by default) is refused without running. A different call that ran
/// (success or failure) or a success of the same call ends the streak. Successful instant calls (look, status,
/// think...) only observe, so they neither start nor end a streak.
///
/// No-progress detector: <c>noProgressAfter</c> calls in a row (25 by default) with an unchanged snapshot
/// (game time, inventory, position) get a hint. A successful action (not instant) counts as progress too.
///
/// Not unit-tested yet: the dev sandbox has no .NET SDK. runner/smoke.py case "loop_guard" covers it in game.
/// </summary>
public sealed class LoopDetector
{
    /// <summary>A refused call's result starts with this, so <see cref="After"/> can tell it from a call that ran.</summary>
    public const string RefusalPrefix = "Not run (repeated failure): ";

    private const int ReasonMax = 160;

    /// <summary>Arguments that are free text and differ on every call without changing what the call does.</summary>
    private static readonly HashSet<string> IgnoredArgs = new(StringComparer.Ordinal) { "reason", "diary" };

    private sealed class Entry
    {
        public string Key = "";
        public bool Ok;
        public string Reason = "";
    }

    private readonly List<Entry> _ring = new();
    private readonly int _ringSize;
    private readonly Func<int> _today;

    private string? _lastSnapshot;
    private int _quietCalls;

    private int _day = int.MinValue;
    private int _hints;
    private int _refusals;
    private int _noProgress;

    /// <summary>Identical failures in a row before the hint is added (0 = off).</summary>
    public int HintAfter { get; }
    /// <summary>The identical call after this many minus one failures in a row is refused (0 = off).</summary>
    public int BlockAfter { get; }
    /// <summary>Calls in a row with no progress before the "pick an action" hint (0 = off).</summary>
    public int NoProgressAfter { get; }

    /// <summary>Hints, refusals and no-progress hints on the current farm day.</summary>
    public int HintsToday => _day == _today() ? _hints : 0;
    /// <summary>Refused calls on the current farm day (also counted in <see cref="HintsToday"/>).</summary>
    public int RefusalsToday => _day == _today() ? _refusals : 0;
    /// <summary>No-progress hints on the current farm day (also counted in <see cref="HintsToday"/>).</summary>
    public int NoProgressHintsToday => _day == _today() ? _noProgress : 0;

    /// <param name="today">Current farm day; the per-day counters and the ring start over when it changes.</param>
    /// <param name="ringSize">Calls remembered for the repeat detector (at least <paramref name="blockAfter"/>).</param>
    public LoopDetector(int hintAfter, int blockAfter, int noProgressAfter, Func<int> today, int ringSize = 10)
    {
        HintAfter = hintAfter <= 0 ? 0 : Math.Max(2, hintAfter);
        BlockAfter = blockAfter <= 0 ? 0 : Math.Max(blockAfter, Math.Max(2, HintAfter + 1));
        NoProgressAfter = Math.Max(0, noProgressAfter);
        _ringSize = Math.Max(ringSize, BlockAfter);
        _today = today;
    }

    // ------------------------------------------------------------------ rules

    /// <summary>
    /// Call before running a call. Returns the refusal to answer with (and records the call as another failure)
    /// when the identical call has already failed <c>BlockAfter - 1</c> times in a row; otherwise null.
    /// </summary>
    public GuardNote? Before(string key)
    {
        Roll();
        if (BlockAfter <= 0) return null;
        int n = Trailing(key, out var reason);
        if (n + 1 < BlockAfter) return null;

        Push(key, false, reason);
        return Note(GuardKind.Refused,
            RefusalPrefix + RepeatText(n, reason) + " Do something else first; then you can retry it.", n);
    }

    /// <summary>
    /// Call after a call finished (it ran, or <see cref="Before"/> refused it). Returns the hints to append to
    /// its result: the repeat hint and/or the no-progress hint.
    /// </summary>
    /// <param name="snapshot">Opaque summary of game time, inventory and position at the end of the call.</param>
    public List<GuardNote> After(string key, bool instant, bool ok, string result, string snapshot)
    {
        Roll();
        var notes = new List<GuardNote>();
        bool refused = IsRefusal(result); // Before already recorded it and wrote its text

        if (!refused && !(instant && ok))
        {
            Push(key, ok, ok ? "" : Brief(result));
            if (!ok && HintAfter > 0)
            {
                int n = Trailing(key, out var reason);
                if (n >= HintAfter)
                {
                    var text = RepeatText(n, reason);
                    if (BlockAfter > 0 && n == BlockAfter - 1)
                        text += " The next identical attempt will not be run.";
                    notes.Add(Note(GuardKind.Repeat, text, n));
                }
            }
        }

        if (NoProgressAfter > 0)
        {
            bool progressed = (!instant && ok) || snapshot != _lastSnapshot;
            _lastSnapshot = snapshot;
            if (progressed)
                _quietCalls = 0;
            else if (++_quietCalls >= NoProgressAfter)
            {
                notes.Add(Note(GuardKind.NoProgress,
                    $"You haven't done anything for a while — {_quietCalls} calls in a row with no game time passing, no inventory change and no movement. Pick an action.",
                    _quietCalls));
                _quietCalls = 0;
            }
        }
        return notes;
    }

    public static bool IsRefusal(string text) => text.StartsWith(RefusalPrefix, StringComparison.Ordinal);

    // ------------------------------------------------------------------ helpers

    /// <summary>Identical failed calls at the end of the ring (the newest entries), and the newest one's reason.</summary>
    private int Trailing(string key, out string reason)
    {
        reason = "";
        int n = 0;
        for (int i = _ring.Count - 1; i >= 0; i--)
        {
            var e = _ring[i];
            if (e.Ok || e.Key != key) break;
            if (n == 0) reason = e.Reason;
            n++;
        }
        return n;
    }

    private void Push(string key, bool ok, string reason)
    {
        _ring.Add(new Entry { Key = key, Ok = ok, Reason = reason });
        if (_ring.Count > _ringSize) _ring.RemoveAt(0);
    }

    private GuardNote Note(GuardKind kind, string text, int count)
    {
        _hints++;
        if (kind == GuardKind.Refused) _refusals++;
        else if (kind == GuardKind.NoProgress) _noProgress++;
        return new GuardNote(kind, text, count);
    }

    /// <summary>A new farm day: the per-day counters start at zero and yesterday's failures are forgotten.</summary>
    private void Roll()
    {
        int day = _today();
        if (day == _day) return;
        _day = day;
        _hints = _refusals = _noProgress = 0;
        _ring.Clear();
        _quietCalls = 0;
    }

    private static string RepeatText(int n, string reason) =>
        $"You tried this {n} times and it failed each time: {(reason.Length == 0 ? "no reason given" : reason)}. Try something different.";

    /// <summary>First line of a result, without its final full stop, cut to a readable length.</summary>
    internal static string Brief(string text)
    {
        text = text.Trim();
        int nl = text.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) text = text.Substring(0, nl);
        text = text.TrimEnd('.', ' ');
        return text.Length > ReasonMax ? text.Substring(0, ReasonMax) + "..." : text;
    }

    /// <summary>
    /// Identity of a call: the tool plus its arguments in a canonical form, so the same call matches even when
    /// the model changes key order, letter case or the free-text <c>reason</c>/<c>diary</c>.
    /// </summary>
    public static string KeyOf(string tool, JsonElement args)
    {
        var sb = new StringBuilder(tool);
        sb.Append(' ');
        Canon(args, sb, true);
        return sb.ToString();
    }

    private static void Canon(JsonElement e, StringBuilder sb, bool top)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Undefined:
                break;
            case JsonValueKind.Object:
                var props = new List<JsonProperty>();
                foreach (var prop in e.EnumerateObject())
                    if (!(top && IgnoredArgs.Contains(prop.Name)))
                        props.Add(prop);
                props.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                sb.Append('{');
                foreach (var prop in props)
                {
                    sb.Append(prop.Name).Append(':');
                    Canon(prop.Value, sb, false);
                    sb.Append(',');
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                foreach (var item in e.EnumerateArray())
                {
                    Canon(item, sb, false);
                    sb.Append(',');
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append('"').Append((e.GetString() ?? "").Trim().ToLowerInvariant()).Append('"');
                break;
            case JsonValueKind.Number:
                sb.Append(e.TryGetDouble(out var number) ? number.ToString("R", CultureInfo.InvariantCulture) : e.GetRawText());
                break;
            default: // true, false, null
                sb.Append(e.GetRawText());
                break;
        }
    }
}

/// <summary>
/// One seat's loop guard (#31): feeds <see cref="LoopDetector"/> from the seat's tool calls, writes every hint
/// to the day log as kind "guard", and counts hints per farm day for the stats (#25: <c>Rt.Guard.HintsToday</c>).
/// Each seat's <see cref="AgentRuntime"/> owns one, so state is per player. The camera seat (P4) never
/// receives AI calls, so its guard stays idle. Runs on the seat's own game-thread tick.
/// </summary>
public sealed class LoopGuard
{
    private readonly LoopDetector _core;
    private readonly PlayerLog _log;
    private readonly IMonitor _monitor;

    /// <summary>Hints, refusals and no-progress hints on the current farm day.</summary>
    public int HintsToday => _core.HintsToday;
    /// <summary>Refused calls on the current farm day (also counted in <see cref="HintsToday"/>).</summary>
    public int RefusalsToday => _core.RefusalsToday;
    /// <summary>No-progress hints on the current farm day (also counted in <see cref="HintsToday"/>).</summary>
    public int NoProgressHintsToday => _core.NoProgressHintsToday;

    public LoopGuard(ModConfig config, PlayerLog log, IMonitor monitor)
    {
        _log = log;
        _monitor = monitor;
        _core = new LoopDetector(config.RepeatHintAfter, config.RepeatBlockAfter, config.NoProgressAfter, () => AgentLog.FarmDay);
    }

    /// <summary>
    /// Before a call runs: true, with the text to fail it with, when the identical call has failed too often in a row.
    /// </summary>
    public bool Refuse(PendingCall call, out string text)
    {
        text = "";
        if (!Applies(call)) return false;
        try
        {
            var note = _core.Before(LoopDetector.KeyOf(call.Tool.Name, call.Args));
            if (note == null) return false;
            LogNote(call, note);
            text = note.Text;
            return true;
        }
        catch (Exception ex)
        {
            _monitor.Log($"LoopGuard.Refuse failed: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    /// <summary>After a call finished: the hints to append to its result (empty when there are none).</summary>
    public string End(PendingCall call, bool ok, string result)
    {
        if (!Applies(call)) return "";
        try
        {
            var notes = _core.After(LoopDetector.KeyOf(call.Tool.Name, call.Args), call.Instant, ok, result, Snapshot());
            foreach (var note in notes)
                LogNote(call, note);
            return string.Join("\n", notes.Select(n => n.Text));
        }
        catch (Exception ex)
        {
            // Never let a guard bug leave an AI's call unanswered.
            _monitor.Log($"LoopGuard.End failed: {ex.Message}", LogLevel.Warn);
            return "";
        }
    }

    /// <summary>Only the AI's own calls in a loaded world. Debug ops (Mail = false) are smoke-test hooks, not play.</summary>
    private static bool Applies(PendingCall call) => Context.IsWorldReady && call.Tool.Mail;

    private void LogNote(PendingCall call, GuardNote note) =>
        _log.Add("guard", call.Tool.Name, note.Text, new { type = note.Type, count = note.Count });

    /// <summary>
    /// What "progress" means: game time (days and minutes), inventory (item names and stacks), location and tile
    /// of this seat's farmer. Any change since the previous call counts.
    /// </summary>
    private static string Snapshot()
    {
        var p = Game1.player;
        int minutes = Game1.Date.TotalDays * 1440 + Utility.ConvertTimeToMinutes(Game1.timeOfDay);
        var inventory = new HashCode();
        foreach (var item in p.Items)
        {
            inventory.Add(item?.DisplayName);
            inventory.Add(item?.Stack ?? 0);
        }
        return $"{minutes}|{inventory.ToHashCode()}|{Game1.currentLocation?.Name}|{p.TilePoint.X},{p.TilePoint.Y}";
    }
}
