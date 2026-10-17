using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// What happened when one AI farmer's day ended without going to bed (#22). Saved per seat in pass_out[_p2].jsonl,
/// logged as a "pass_out" entry on the new day's page, and served in /api/state until the journal for <see cref="Day"/> is written.
/// </summary>
public class PassOutRecord
{
    /// <summary>Save folder the record belongs to (farm days start again in a new save).</summary>
    public string Save { get; set; } = "";
    public string Player { get; set; } = "";
    /// <summary>The farm day that ended by passing out. This is the day whose journal is still owed (pending_journal_day).</summary>
    public int Day { get; set; }
    public int NewDay { get; set; }
    /// <summary>"2am" | "hp0" | "exhausted" | "other"; see <see cref="PassOut.Classify"/>.</summary>
    public string Cause { get; set; } = "other";
    public string Location { get; set; } = "";
    /// <summary>[x, y] tile where the farmer stood the last time the mod looked before the night.</summary>
    public int[] Tile { get; set; } = Array.Empty<int>();
    /// <summary>Game clock at that moment, HHMM like Game1.timeOfDay (2550 is 1:50 AM).</summary>
    public int Time { get; set; }
    /// <summary>The last action tool that ran (null when none ran that day).</summary>
    public string? Tool { get; set; }
    public string? Reason { get; set; }
    /// <summary>True when <see cref="Tool"/> was still running at the end; false when the farmer stood idle after it.</summary>
    public bool ToolRunning { get; set; }
    /// <summary>Game clock when <see cref="Tool"/> was last seen running.</summary>
    public int ToolTime { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int Energy { get; set; }
    public int MaxEnergy { get; set; }
    /// <summary>Money the last time the mod looked before the night; <see cref="MoneyAfter"/> also holds the shipping income that came in overnight.</summary>
    public int MoneyBefore { get; set; }
    public int MoneyAfter { get; set; }
    /// <summary>Items taken from the pack (Farmer.itemsLostLastDeath), as "Name xN". Empty when none were lost.</summary>
    public List<string> ItemsLost { get; set; } = new();
    public string At { get; set; } = "";
}

/// <summary>Pending journal state of one seat, as served in /api/state.</summary>
public sealed record PassOutView(int? PendingDay, List<int> PendingDays, PassOutRecord? Record);

/// <summary>
/// Notices a day that ended without go_to_bed and records why (#22). Runs on every AI seat's own screen tick,
/// because each farmer is a different <c>Game1.player</c>:
///   * every tick before the night it remembers where the farmer is, the clock, money, health, the running action;
///   * when the farm day changes and the farmer was not in bed, the day ended by passing out (2:00 AM, health 0 or exhaustion);
///   * once the morning has settled it saves a <see cref="PassOutRecord"/> with money after and the items lost.
/// The runner reads the record from /api/state and asks the AI to write the lost day's journal (journal(day=N)).
/// The "pending journal day" is not stored: it is the record's day for as long as AgentLog.HasJournal(day) is false,
/// so writing the journal clears it, even after a restart.
/// </summary>
public static class PassOut
{
    /// <summary>From this clock on (1:40 AM), a day that ends without bed counts as the 2:00 AM pass-out.</summary>
    private const int LateTime = 2540;

    /// <summary>Ticks to wait for the morning to settle (no menu, no fade, farmer can move) before the record is written anyway.</summary>
    private const int SettleTicks = 60 * 30;

    private static AgentLog? _log;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<PassOutRecord>> Cache = new();
    private static readonly Tracker[] Trackers = Enumerable.Range(0, Players.Seats).Select(_ => new Tracker()).ToArray();

    public static void Init(AgentLog log) => _log = log;

    /// <summary>What the mod saw of one farmer the last time before the night, and a pass-out waiting for the morning to settle.</summary>
    private sealed class Tracker
    {
        public int Day;
        public string Save = "";
        public string Location = "";
        public bool InMine, InFarmHouse, InBed, GoToBed, ZeroHp;
        public int TileX, TileY, Time, Money, Health, MaxHealth, MaxStamina, ToolTime;
        public float Stamina;
        public string? Tool, Reason;
        public bool ToolRunning;
        public string LostSig = "";

        public PassOutRecord? Pending;
        public int Waited;

        /// <summary>A new day starts: forget what the last one left (the position stays until the next Capture).</summary>
        public void ResetDay()
        {
            ZeroHp = false;
            Tool = null;
            Reason = null;
            ToolRunning = false;
            ToolTime = 0;
        }
    }

    // ------------------------------------------------------------------ per-tick watch

    /// <summary>Called from <see cref="AgentRuntime.Tick"/> on the seat's own screen.</summary>
    public static void Tick(AgentRuntime rt)
    {
        var seat = Players.Current;
        if (_log == null || seat.Role != SeatRole.Ai || !Context.IsWorldReady || Game1.player == null)
            return;

        var t = Trackers[seat.Screen];
        var p = Game1.player;
        int day = AgentLog.FarmDay;

        if (t.Pending != null)
        {
            Settle(t, rt, p);
            return;
        }

        if (t.Day != day)
        {
            // Exactly the next day of the same save, and the farmer was not asleep in bed: the day ended by passing out.
            bool nextDay = t.Day > 0 && day == t.Day + 1 && t.Save == (SaveFolder.Name ?? "");
            if (nextDay && !SleptInBed(t))
            {
                t.Pending = Describe(t, seat.Id, day);
                rt.Stats.NotePassOut(t.Pending.Cause);
                t.Waited = 0;
                return;
            }
            t.ResetDay();
        }

        // Stop looking once the night starts, so the snapshot stays what the farmer was doing before it.
        if (!Game1.newDay && Game1.activeClickableMenu is not ShippingMenu)
            Capture(t, rt, p, day);
    }

    /// <summary>The save was left: forget what was watched, so loading another day of it later is not taken for a pass-out.</summary>
    public static void OnReturnedToTitle()
    {
        foreach (var t in Trackers)
        {
            t.Day = 0;
            t.Pending = null;
            t.ResetDay();
        }
    }

    private static bool SleptInBed(Tracker t) => t.InBed || (t.GoToBed && t.InFarmHouse);

    private static void Capture(Tracker t, AgentRuntime rt, Farmer p, int day)
    {
        var loc = Game1.currentLocation;
        t.Day = day;
        t.Save = SaveFolder.Name ?? "";
        t.Location = loc?.Name ?? "";
        t.InMine = loc is MineShaft;
        t.InFarmHouse = loc is FarmHouse;
        t.TileX = p.TilePoint.X;
        t.TileY = p.TilePoint.Y;
        t.Time = Game1.timeOfDay;
        t.Money = p.Money;
        t.Health = p.health;
        t.MaxHealth = p.maxHealth;
        t.Stamina = p.Stamina;
        t.MaxStamina = p.MaxStamina;
        t.InBed = p.isInBed.Value;
        t.GoToBed = rt.CurrentTool == "go_to_bed";
        if (p.health <= 0) t.ZeroHp = true; // stays set for the rest of the day
        t.LostSig = Signature(p.itemsLostLastDeath);

        if (rt.ActionRunning)
        {
            t.Tool = rt.CurrentTool;
            t.Reason = rt.CurrentReason;
            t.ToolRunning = true;
            t.ToolTime = Game1.timeOfDay;
        }
        else
            t.ToolRunning = false;
    }

    private static PassOutRecord Describe(Tracker t, string player, int newDay) => new()
    {
        Save = t.Save,
        Player = player,
        Day = t.Day,
        NewDay = newDay,
        Cause = Classify(t.ZeroHp, t.Time, t.Stamina, t.InMine),
        Location = t.Location,
        Tile = new[] { t.TileX, t.TileY },
        Time = t.Time,
        Tool = t.Tool,
        Reason = string.IsNullOrWhiteSpace(t.Reason) ? null : t.Reason,
        ToolRunning = t.ToolRunning,
        ToolTime = t.ToolTime,
        Health = t.Health,
        MaxHealth = t.MaxHealth,
        Energy = (int)t.Stamina,
        MaxEnergy = t.MaxStamina,
        MoneyBefore = t.Money,
        At = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz")
    };

    /// <summary>
    /// Why the day ended without bed. Health 0 wins (a mine death can happen at any hour); then the clock, because
    /// the 2:00 AM pass-out is the usual one; then an empty energy bar. A last position in the mines with none of those is
    /// taken as a death too, in case the health reading was missed.
    /// </summary>
    internal static string Classify(bool zeroHp, int time, float stamina, bool inMine)
    {
        if (zeroHp) return "hp0";
        if (time >= LateTime) return "2am";
        if (stamina <= 0) return "exhausted";
        if (inMine) return "hp0";
        return "other";
    }

    /// <summary>The morning has settled (or we waited long enough): fill in the money after and the items lost, then save.</summary>
    private static void Settle(Tracker t, AgentRuntime rt, Farmer p)
    {
        t.Waited++;
        bool settled = Game1.activeClickableMenu == null && !Game1.fadeToBlack && !Game1.newDay && p.CanMove;
        if (!settled && t.Waited < SettleTicks)
            return;

        var rec = t.Pending!;
        t.Pending = null;
        rec.MoneyAfter = p.Money;
        // itemsLostLastDeath keeps the list of the last death, so only report it when that death is this one.
        bool died = t.ZeroHp || Signature(p.itemsLostLastDeath) != t.LostSig;
        rec.ItemsLost = died ? LostItems(p) : new List<string>();
        Commit(rt, rec);

        t.Day = 0; // the next tick starts the new day (ResetDay, then Capture)
    }

    private static List<string> LostItems(Farmer p)
    {
        var list = new List<string>();
        if (p.itemsLostLastDeath == null) return list;
        foreach (var item in p.itemsLostLastDeath)
            if (item != null)
                list.Add(item.Stack > 1 ? $"{item.DisplayName} x{item.Stack}" : item.DisplayName);
        return list;
    }

    private static string Signature(IEnumerable<Item>? items) =>
        items == null ? "" : string.Join("|", items.Where(i => i != null).Select(i => $"{i.DisplayName}x{i.Stack}"));

    private static void Commit(AgentRuntime rt, PassOutRecord rec)
    {
        try
        {
            lock (Gate)
            {
                Records(rec.Player).Add(rec);
                File.AppendAllText(_log!.MemoryFile("pass_out", rec.Player, "jsonl"), JsonSerializer.Serialize(rec, AgentLog.Json) + "\n", Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            rt.Log.Add("system", text: $"Could not save the pass-out record: {ex.Message}");
        }
        var summary = Summary(rec);
        rt.Log.Add("pass_out", text: summary, args: rec);
        rt.Log.Add("system", text: summary); // starts with "Passed out": the overlay treats it like the end of the day
    }

    /// <summary>"01:50" for 2550, "09:00" for 900.</summary>
    internal static string Clock(int time)
    {
        int h = time / 100 % 24, m = time % 100;
        return $"{h:00}:{m:00}";
    }

    internal static string Summary(PassOutRecord r)
    {
        var tile = r.Tile.Length >= 2 ? $"({r.Tile[0]},{r.Tile[1]})" : "";
        var sb = new StringBuilder($"Passed out ({r.Cause}) on farm day {r.Day} at {r.Location} {tile}, clock {Clock(r.Time)}");
        if (r.Tool == null)
            sb.Append("; no action had run that day");
        else if (r.ToolRunning)
            sb.Append($"; was running {r.Tool}");
        else
            sb.Append($"; idle since {Clock(r.ToolTime)} (last action {r.Tool})");
        sb.Append($"; money {r.MoneyBefore}g -> {r.MoneyAfter}g (overnight shipping income included)");
        if (r.ItemsLost.Count > 0)
            sb.Append($"; items lost: {string.Join(", ", r.ItemsLost)}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ records and the pending journal

    /// <summary>All records of a seat, loaded once per file. Call with <see cref="Gate"/> held.</summary>
    private static List<PassOutRecord> Records(string player)
    {
        var path = _log!.MemoryFile("pass_out", player, "jsonl"); // follows the smoke/real log folder switch
        if (Cache.TryGetValue(path, out var cached))
            return cached;

        var list = new List<PassOutRecord>();
        try
        {
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.Trim().Length == 0) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<PassOutRecord>(line, AgentLog.Json);
                        if (rec != null) list.Add(rec);
                    }
                    catch (JsonException) { }
                }
        }
        catch (IOException) { }
        Cache[path] = list;
        return list;
    }

    /// <summary>
    /// Records of this seat in the loaded save whose farm day has no journal yet (<see cref="AgentLog.HasJournal"/>),
    /// oldest first. Writing journal(day=N) is what removes a record from this list.
    /// </summary>
    public static List<PassOutRecord> Pending(string player)
    {
        var log = _log;
        var save = SaveFolder.Name;
        if (log == null || save == null)
            return new List<PassOutRecord>();
        lock (Gate)
            return Records(player).Where(r => r.Save == save && !log.HasJournal(r.Day, player)).OrderBy(r => r.Day).ToList();
    }

    /// <summary>
    /// For /api/state: the oldest day still owing a journal and its record. With nothing pending, the seat's latest record
    /// of this save (so the viewers and the runner can still see how the last pass-out went), or none.
    /// </summary>
    public static PassOutView View(string player)
    {
        var pending = Pending(player);
        if (pending.Count > 0)
            return new PassOutView(pending[0].Day, pending.Select(r => r.Day).ToList(), pending[0]);

        var save = SaveFolder.Name;
        if (_log == null || save == null)
            return new PassOutView(null, new List<int>(), null);
        lock (Gate)
            return new PassOutView(null, new List<int>(), Records(player).LastOrDefault(r => r.Save == save));
    }
}
