using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// Keeps what the AI reads small (#30): the <c>kinds</c> filter and <c>diff</c> mode of <c>look</c>,
/// a legend that only lists the symbols in view, and a one-line result footer.
/// <see cref="WorldText.Look"/> calls into this; the scan itself still uses <see cref="WorldText.Describe"/>.
/// </summary>
public static class Observation
{
    /// <summary>Default <c>look</c> radius: a 17x17 window instead of the old 25x25.</summary>
    public const int DefaultRadius = 8;

    // Which map symbols belong to which kind of thing. A symbol that no kind lists counts as "other",
    // so symbols added later still show up when no filter is given and can be asked for as "other".
    private static readonly Dictionary<string, string> KindSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crops"] = "cC!x",
        ["trees"] = "TtYu",
        ["debris"] = "WwSLs",
        ["forage"] = "F",
        ["npcs"] = "NM",
        ["doors"] = "DA",
        ["buildings"] = "HbZf",
        ["objects"] = "O",
    };

    public static string KindNames => string.Join(", ", KindSymbols.Keys.Append("other"));

    private static string KindOf(char sym)
    {
        foreach (var kv in KindSymbols)
            if (kv.Value.IndexOf(sym) >= 0)
                return kv.Key;
        return "other";
    }

    // ------------------------------------------------------------------ arguments

    /// <summary>The <c>kinds</c> argument as lower-case names; null means every kind (missing, empty or "all").</summary>
    public static HashSet<string>? Kinds(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("kinds", out var v))
            return null;
        var names = new List<string>();
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String)
                    names.Add(e.GetString() ?? "");
        }
        else if (v.ValueKind == JsonValueKind.String)
        {
            names.AddRange((v.GetString() ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
        var set = new HashSet<string>(names.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0));
        return set.Count == 0 || set.Contains("all") ? null : set;
    }

    /// <summary>A true boolean argument; the string "true" is accepted too because models sometimes send it.</summary>
    public static bool Flag(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True ||
         (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Does a "Things in view" entry (key starts with its map symbol) belong to a requested kind?</summary>
    public static bool Wants(HashSet<string>? kinds, string key) =>
        kinds == null || key.Length == 0 || kinds.Contains(KindOf(key[0]));

    /// <summary>Heading for the object list, naming the filter (and any kind name that doesn't exist).</summary>
    public static string ListHeading(HashSet<string>? kinds)
    {
        if (kinds == null) return "Things in view (nearest first):";
        var unknown = kinds.Where(k => k != "other" && !KindSymbols.ContainsKey(k)).ToList();
        var note = unknown.Count > 0 ? $"; unknown kinds ignored: {string.Join(", ", unknown)}; valid: {KindNames}" : "";
        return $"Things in view, only {string.Join(", ", kinds)}{note} (nearest first):";
    }

    // ------------------------------------------------------------------ legend and footer

    /// <summary>The legend entries for symbols that appear in the view (entries that don't parse are always kept).</summary>
    public static string LegendFor(IEnumerable<char> symbols)
    {
        var present = new HashSet<char>(symbols);
        var keep = WorldText.Legend.Split(" | ").Where(e => e.Length < 3 || e[1] != ' ' || present.Contains(e[0]));
        return string.Join(" | ", keep);
    }

    /// <summary>Joins the lines of a text into one (used so the result footer stays a single line).</summary>
    public static string OneLine(string text) =>
        string.Join(" | ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0));

    // ------------------------------------------------------------------ look(diff)

    private sealed class Snapshot
    {
        public string Map = "";
        public readonly Dictionary<(int x, int y), (char sym, string? label)> Cells = new();
    }

    // What each seat has seen on its current map, by player id. Touched only on that seat's game thread.
    private static readonly ConcurrentDictionary<string, Snapshot> Last = new();

    private static string Show(char sym, string? label) => label == null ? sym.ToString() : $"{sym} {label}";

    /// <summary>Store the tiles of a full look so the next <c>look(diff=true)</c> can compare against them.</summary>
    public static void Remember(GameLocation loc, Dictionary<(int x, int y), (char sym, string? label)> cells)
    {
        var player = AgentTools.Rt.Log.Player;
        if (!Last.TryGetValue(player, out var snap) || snap.Map != loc.Name)
            Last[player] = snap = new Snapshot { Map = loc.Name };
        foreach (var kv in cells)
            snap.Cells[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Only the tiles that changed since this seat last saw them on this map. Null when there is nothing to compare
    /// with (no earlier look on this map, or most of this view is new), so the caller shows the full view instead.
    /// </summary>
    public static string? Diff(GameLocation loc, int px, int py, int x0, int x1, int y0, int y1)
    {
        var player = AgentTools.Rt.Log.Player;
        if (!Last.TryGetValue(player, out var old) || old.Map != loc.Name)
            return null;

        var cells = new Dictionary<(int x, int y), (char sym, string? label)>();
        var changes = new Dictionary<string, List<(int x, int y)>>();
        int total = 0, fresh = 0, changed = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var now = WorldText.Describe(loc, x, y);
                cells[(x, y)] = now;
                total++;
                if (!old.Cells.TryGetValue((x, y), out var before)) { fresh++; continue; }
                // The farmer's own tile hides what is under it, before and after a move: not a change.
                if (now.sym == '@' || before.sym == '@') continue;
                if (before.sym == now.sym && before.label == now.label) continue;
                var key = $"{Show(before.sym, before.label)} → {Show(now.sym, now.label)}";
                if (!changes.TryGetValue(key, out var list)) changes[key] = list = new();
                list.Add((x, y));
                changed++;
            }
        }
        if (fresh * 2 > total)
            return null; // you moved far: most of this window was never looked at

        foreach (var kv in cells)
            old.Cells[kv.Key] = kv.Value;

        var sb = new StringBuilder();
        sb.AppendLine($"Map {loc.Name}. You @ ({px},{py}) facing {WorldText.DirName(Game1.player.FacingDirection)}. Showing x {x0}-{x1}, y {y0}-{y1}.");
        if (changed == 0)
        {
            sb.Append($"No changes since your last look ({total - fresh} of {total} tiles compared).");
        }
        else
        {
            sb.AppendLine($"Changes since your last look ({changed} tiles changed, {total - fresh} of {total} compared):");
            const int maxLines = 25;
            foreach (var kv in changes.OrderBy(kv => kv.Value.Min(t => Math.Abs(t.x - px) + Math.Abs(t.y - py))).Take(maxLines))
            {
                var pts = kv.Value.OrderBy(t => Math.Abs(t.x - px) + Math.Abs(t.y - py)).ToList();
                var shown = string.Join(" ", pts.Take(10).Select(t => $"({t.x},{t.y})"));
                sb.AppendLine($"  {kv.Key} x{pts.Count}: {shown}{(pts.Count > 10 ? " ..." : "")}");
            }
            if (changes.Count > maxLines)
                sb.AppendLine($"  ... and {changes.Count - maxLines} more kinds of change");
            sb.Append("Symbols are the look legend. Use look() for the full map.");
        }
        if (fresh > 0)
            sb.Append($"\n{fresh} tiles in this window were never seen before; look() without diff shows them.");
        return sb.ToString().TrimEnd();
    }
}
