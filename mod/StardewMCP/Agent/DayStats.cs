using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewMCP.Agent;

/// <summary>
/// One AI seat's numbers for one in-game day (#25). Each seat has its own instance (<see cref="AgentRuntime.Stats"/>),
/// ticked on that seat's own screen:
///   * from the first moment of a farm day it counts tool calls, crops planted and harvested, and every second
///     remembers how the farmer stood (money, energy, health, clock, unwatered crops, the shipping bin);
///   * when the farm day changes (sleep or pass-out) and the morning has settled, it writes the day that just ended
///     to <c>stats_NNN.json</c> (<c>stats_NNN_p2.json</c> for the other seats) next to the logs, then starts the next day.
/// The camera seat (P4) and seats no AI has talked to write nothing. Served at GET /api/stats (see <see cref="Api"/>).
/// The runner (runner/stats.py) turns the files into batch_report.md and the morning line.
/// </summary>
public class DayStats
{
    /// <summary>Bump when a field changes meaning; runner/stats.py reads files of any version defensively.</summary>
    public const int Version = 1;

    /// <summary>Ticks between snapshots (about one second).</summary>
    private const int SnapshotEvery = 60;

    /// <summary>A day that starts after 6:10 AM was joined late (mod started or save loaded mid-day): its numbers are partial.</summary>
    private const int PartialAfter = 610;

    /// <summary>From 1:40 AM on, a day that ends without bed counts as the 2:00 AM pass-out (same threshold as #22).</summary>
    private const int LateTime = 2540;

    /// <summary>Energy at or below this at the last snapshot, with no bed, counts as an exhaustion pass-out.</summary>
    private const int ExhaustedStamina = -10;

    private const int MaxFailureKinds = 40;

    /// <summary>Game counters (Game1.stats) whose change over the day is recorded under gameStats.</summary>
    private static readonly string[] CountedStats =
    {
        "fishCaught", "monstersKilled", "itemsCrafted", "itemsShipped", "cropsShipped", "seedsSown", "timesUnconscious", "giftsGiven", "stepsTaken"
    };

    private static readonly JsonSerializerOptions FileJson = new(AgentLog.Json)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never // every field is always present, null when unknown
    };

    /// <summary>Names of the tools the AI can call; debug ops from the smoke tests are not counted.</summary>
    private static readonly Lazy<HashSet<string>> ToolNames = new(() => AgentTools.All().Select(t => t.Name).ToHashSet());

    private static AgentLog? _log;

    public static void Init(AgentLog log) => _log = log;

    private readonly PlayerLog _plog;
    private DayState? _day;
    private string _save = "";
    private int _ticks;
    private HashSet<string> _deadSeen = new();

    public DayStats(PlayerLog log) => _plog = log;

    private string Player => _plog.Player;

    // ------------------------------------------------------------------ hooks (all on the game thread)

    /// <summary>A tool call starts. Counted on the day it starts, so the go_to_bed that spans the night belongs to the day it ends.</summary>
    public void OnCallStart(string tool, bool instant)
    {
        if (_day == null || !ToolNames.Value.Contains(tool)) return;
        _day.Calls++;
        if (instant) _day.InstantCalls++; else _day.ActionCalls++;
        _day.For(tool).N++;
    }

    /// <summary>A tool call ended. <paramref name="startTotalDays"/> is Game1.Date.TotalDays when it started; a failure that
    /// ends after its day was already written is not counted.</summary>
    public void OnCallEnd(string tool, bool ok, string result, int startTotalDays)
    {
        var day = _day;
        if (day == null || !ToolNames.Value.Contains(tool)) return;
        if (tool == "go_to_bed" && !ok) day.WentToBed = false; // sleeping did not start after all
        if (ok || startTotalDays + 1 != day.Day) return;
        day.Failed++;
        day.For(tool).Failed++;
        var key = tool + "\u0001" + Normalize(result);
        if (day.FailureKinds.ContainsKey(key) || day.FailureKinds.Count < MaxFailureKinds)
            day.FailureKinds[key] = day.FailureKinds.GetValueOrDefault(key) + 1;
    }

    /// <summary>go_to_bed is about to answer the bed's sleep question: the day ends in bed. Takes the bedtime snapshot now.</summary>
    public void OnSleepStart()
    {
        if (_day == null) return;
        _day.WentToBed = true;
        Snapshot(force: true);
    }

    /// <summary>Seeds that took root (the plant tool counts these, not fertilizer).</summary>
    public void CropsPlanted(int n)
    {
        if (_day != null) _day.Planted += n;
    }

    /// <summary>The harvest tool acted on a tile; '!' is the map symbol of a crop that is ready.</summary>
    public void HarvestTile(char before, char after)
    {
        if (_day != null && before == '!' && after != '!') _day.Harvested++;
    }

    /// <summary>
    /// For a pass-out record that knows better than the heuristic (#22): the day in progress ends by passing out.
    /// Cause vocabulary: "2am" | "hp0" | "exhausted" | "other". Does nothing when the day is already written.
    /// </summary>
    public void NotePassOut(string cause)
    {
        if (_day == null) return;
        _day.PassOutCause = cause;
        _day.PassOutSource = "mod";
    }

    // ------------------------------------------------------------------ tick

    /// <summary>Called every tick of this seat's screen.</summary>
    public void Tick()
    {
        if (!Context.IsWorldReady || Game1.player == null) return;
        var slot = Players.ById(Player);
        if (slot is not { Role: SeatRole.Ai }) return;

        _ticks++;
        int farmDay = AgentLog.FarmDay;
        var save = SaveFolder.Name ?? "";
        if (_day == null || save != _save || farmDay < _day.Day)
        {
            _save = save; // a different save (or an older state of it) was loaded: start over without writing
            Begin(farmDay, null);
            return;
        }

        if (farmDay != _day.Day)
        {
            if (Game1.newDay || Game1.fadeToBlack) return; // the night is still running; wait for the morning
            Rollover(farmDay, slot);
            return;
        }

        if (_ticks % SnapshotEvery == 0) Snapshot(force: false);
    }

    private void Begin(int farmDay, FarmScan? scan)
    {
        var p = Game1.player;
        _day = new DayState
        {
            Day = farmDay,
            Date = $"{Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}",
            StartTime = Game1.timeOfDay,
            MoneyStart = p.Money,
            EarnedStart = Earned(p),
            StatsStart = ReadStats()
        };
        _deadSeen = (scan ?? SafeScan()).Dead;
        Snapshot(force: true);
    }

    private void Rollover(int newDay, PlayerSlot slot)
    {
        var scan = SafeScan();
        try
        {
            if (slot.Connected && _log != null)
                Write(_day!, scan);
        }
        catch (Exception ex)
        {
            _plog.Add("system", text: $"Day stats for day {_day!.Day} failed: {ex.Message}");
        }
        Begin(newDay, scan);
    }

    // ------------------------------------------------------------------ snapshot

    /// <summary>What the farmer looked like at the last look before the night. Skipped while a day change or fade is running,
    /// because the game resets energy and the clock there.</summary>
    private void Snapshot(bool force)
    {
        var day = _day;
        if (day == null) return;
        if (!force && (Game1.newDay || Game1.fadeToBlack)) return;
        try
        {
            var p = Game1.player;
            var s = day.Last;
            s.Time = Game1.timeOfDay;
            s.Stamina = (int)p.Stamina;
            s.MaxStamina = p.MaxStamina;
            s.Health = p.health;
            s.MaxHealth = p.maxHealth;
            s.Money = p.Money;
            s.InBed = p.isInBed.Value;
            s.Weather = WorldText.Weather();
            var scan = SafeScan();
            s.Growing = scan.Growing;
            s.Unwatered = scan.Unwatered;
            s.Bin = ReadBin(p);
        }
        catch (Exception ex)
        {
            _plog.Add("system", text: $"Day stats snapshot failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ writing

    private void Write(DayState d, FarmScan scan)
    {
        var p = Game1.player;
        var s = d.Last;

        // Who ended the day how. go_to_bed (or being seen in bed) means bed; otherwise the clock and energy tell
        // 2:00 AM from exhaustion. #22 can name the cause exactly through NotePassOut.
        bool slept = d.WentToBed || s.InBed;
        string? cause = d.PassOutCause;
        string? source = d.PassOutSource;
        if (cause == null && !slept)
        {
            if (s.Time >= LateTime) cause = "2am";
            else if (s.Stamina <= ExhaustedStamina) cause = "exhausted";
            source = cause == null ? null : "heuristic";
        }
        bool passedOut = !slept && cause != null;
        string endedBy = slept ? "bed" : passedOut ? "passed_out" : "unknown";

        int died = scan.Dead.Count(k => !_deadSeen.Contains(k));
        _deadSeen = scan.Dead;

        long earned = Earned(p) - d.EarnedStart;
        var bin = s.Bin ?? new BinInfo();
        var (hearts, friendship) = ReadFriends(p);
        var statNow = ReadStats();

        var doc = new
        {
            version = Version,
            day = d.Day,
            date = d.Date,
            player = Player,
            farmer = p.Name,
            partial = d.StartTime > PartialAfter,
            startTime = WorldText.FormatTime(d.StartTime),
            weather = s.Weather,
            endedBy,
            wentToBed = d.WentToBed,
            passedOut,
            passOutCause = passedOut ? cause : null,
            passOutSource = passedOut ? source : null,
            endTime = WorldText.FormatTime(s.Time),
            money = new { start = d.MoneyStart, bedtime = s.Money, end = p.Money },
            income = new
            {
                earned,
                shippingBin = bin.Value,
                shippedItems = bin.Items,
                shippedByCategory = bin.ByCategory
            },
            // planted and harvested are this seat's own work; died, growing and unwateredAtBedtime are farm-wide (crops have no owner).
            crops = new
            {
                planted = d.Planted,
                harvested = d.Harvested,
                died,
                growing = scan.Growing,
                unwateredAtBedtime = s.Unwatered
            },
            energy = new { bedtime = s.Stamina, max = s.MaxStamina },
            health = new { bedtime = s.Health, max = s.MaxHealth },
            toolCalls = new
            {
                total = d.Calls,
                failed = d.Failed,
                actions = d.ActionCalls,
                instant = d.InstantCalls,
                byTool = d.ByTool.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => (object)new { n = kv.Value.N, failed = kv.Value.Failed }),
                failures = d.FailureKinds
                    .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(15)
                    .Select(kv => { var parts = kv.Key.Split('\u0001'); return new { tool = parts[0], message = parts.Length > 1 ? parts[1] : "", count = kv.Value }; })
                    .ToList()
            },
            skills = new
            {
                farming = p.farmingLevel.Value,
                mining = p.miningLevel.Value,
                foraging = p.foragingLevel.Value,
                fishing = p.fishingLevel.Value,
                combat = p.combatLevel.Value
            },
            hearts = new { total = hearts, friendship },
            mine = new { deepestFloor = MineShaft.lowestLevelReached },
            tools = ReadToolLevels(p),
            buildings = ReadBuildings(),
            gameStats = CountedStats.ToDictionary(k => k, k => statNow.GetValueOrDefault(k) - d.StatsStart.GetValueOrDefault(k))
        };

        WriteStats(_log!, d.Day, JsonSerializer.Serialize(doc, FileJson), Player);
        _plog.Add("system", text: $"Stats for day {d.Day} saved ({endedBy}, {d.Calls} tool calls, {d.Failed} failed)");
    }

    // ------------------------------------------------------------------ reading the game

    private sealed class FarmScan
    {
        public int Growing, Unwatered;
        public HashSet<string> Dead = new();
    }

    private sealed class BinInfo
    {
        public int Value, Items;
        public SortedDictionary<string, object> ByCategory = new();
    }

    private static FarmScan SafeScan()
    {
        try { return Scan(); }
        catch (Exception) { return new FarmScan(); }
    }

    /// <summary>Living, dead and dry crops on the farm and in the greenhouse.</summary>
    private static FarmScan Scan()
    {
        var r = new FarmScan();
        foreach (var loc in new GameLocation?[] { Game1.getFarm(), Game1.getLocationFromName("Greenhouse") })
        {
            if (loc == null) continue;
            foreach (var kv in loc.terrainFeatures.Pairs)
            {
                if (kv.Value is not HoeDirt dirt || dirt.crop == null) continue;
                if (dirt.crop.dead.Value)
                {
                    r.Dead.Add($"{loc.Name}:{(int)kv.Key.X},{(int)kv.Key.Y}");
                    continue;
                }
                r.Growing++;
                if (!dirt.readyForHarvest() && !dirt.isWatered()) r.Unwatered++;
            }
        }
        return r;
    }

    /// <summary>What is in the shipping bin and what the game would pay for it (the bin is team-wide unless wallets are separate).</summary>
    private static BinInfo ReadBin(Farmer p)
    {
        var info = new BinInfo();
        var items = new Dictionary<string, int>();
        var value = new Dictionary<string, int>();
        foreach (var it in Game1.getFarm().getShippingBin(p))
        {
            if (it == null) continue;
            int v = it.sellToStorePrice() * it.Stack;
            info.Value += v;
            info.Items += it.Stack;
            var cat = it.getCategoryName();
            if (string.IsNullOrEmpty(cat)) cat = "Other";
            items[cat] = items.TryGetValue(cat, out var n) ? n + it.Stack : it.Stack;
            value[cat] = value.TryGetValue(cat, out var m) ? m + v : v;
        }
        foreach (var cat in items.Keys)
            info.ByCategory[cat] = new { items = items[cat], value = value[cat] };
        return info;
    }

    private static long Earned(Farmer p) => (long)p.totalMoneyEarned;

    private static Dictionary<string, long> ReadStats()
    {
        var d = new Dictionary<string, long>();
        foreach (var key in CountedStats)
        {
            try { d[key] = Game1.stats.Get(key); }
            catch (Exception) { d[key] = 0; }
        }
        return d;
    }

    /// <summary>Total hearts over everyone the farmer has met, and friendship points per villager.</summary>
    private static (int hearts, SortedDictionary<string, int> points) ReadFriends(Farmer p)
    {
        int hearts = 0;
        var points = new SortedDictionary<string, int>();
        foreach (var name in p.friendshipData.Keys.ToList())
        {
            int pts = p.friendshipData[name].Points;
            if (pts <= 0) continue;
            points[name] = pts;
            hearts += p.getFriendshipHeartLevelForNPC(name);
        }
        return (hearts, points);
    }

    private static SortedDictionary<string, int> ReadToolLevels(Farmer p)
    {
        var d = new SortedDictionary<string, int>();
        foreach (var t in p.Items.OfType<Tool>())
        {
            string? key = t switch { Axe => "axe", Pickaxe => "pickaxe", Hoe => "hoe", WateringCan => "wateringCan", _ => null };
            if (key != null) d[key] = Math.Max(d.TryGetValue(key, out var old) ? old : 0, t.upgradeLevel.Value);
        }
        return d;
    }

    private static SortedDictionary<string, int> ReadBuildings()
    {
        var d = new SortedDictionary<string, int>();
        foreach (var b in Game1.getFarm().buildings)
        {
            var name = b.buildingType.Value;
            d[name] = d.TryGetValue(name, out var n) ? n + 1 : 1;
        }
        return d;
    }

    /// <summary>First line of a failure message with every number replaced by '#', so "(12,30) skipped" and "(14,31) skipped" group together.</summary>
    private static string Normalize(string text)
    {
        var line = (text ?? "").Split('\n')[0].Trim();
        var sb = new StringBuilder();
        bool digit = false;
        foreach (var ch in line)
        {
            if (char.IsDigit(ch))
            {
                if (!digit) sb.Append('#');
                digit = true;
            }
            else
            {
                digit = false;
                sb.Append(ch);
            }
        }
        var s = sb.ToString();
        return s.Length <= 80 ? s : s[..80];
    }

    // ------------------------------------------------------------------ one day in progress

    private sealed class ToolCount
    {
        public int N, Failed;
    }

    private sealed class Snap
    {
        public int Time, Stamina, MaxStamina, Health, MaxHealth, Money, Growing, Unwatered;
        public bool InBed;
        public string Weather = "";
        public BinInfo? Bin;
    }

    private sealed class DayState
    {
        public int Day, StartTime, MoneyStart;
        public string Date = "";
        public long EarnedStart;
        public Dictionary<string, long> StatsStart = new();
        public int Calls, Failed, ActionCalls, InstantCalls, Planted, Harvested;
        public bool WentToBed;
        public string? PassOutCause, PassOutSource;
        public readonly Dictionary<string, ToolCount> ByTool = new();
        public readonly Dictionary<string, int> FailureKinds = new();
        public readonly Snap Last = new();

        public ToolCount For(string name)
        {
            if (!ByTool.TryGetValue(name, out var c)) ByTool[name] = c = new ToolCount();
            return c;
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary>stats_NNN.json for p1 (and single-player runs), stats_NNN_p2.json for the others, like the diaries.</summary>
    public static string StatsFile(AgentLog log, int day, string? player = null) =>
        Path.Combine(log.Dir, player is null or "p1" ? $"stats_{day:000}.json" : $"stats_{day:000}_{player}.json");

    /// <summary>Written to a temp file first, so a reader (the runner, /api/stats) never sees half a file.</summary>
    private static void WriteStats(AgentLog log, int day, string json, string? player)
    {
        var file = StatsFile(log, day, player);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>Farm days that have a stats file for any seat.</summary>
    public static List<int> StatsDays(AgentLog log) =>
        Directory.GetFiles(log.Dir, "stats_*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f).Split('_'))
            .Select(parts => parts.Length > 1 && int.TryParse(parts[1], out var d) ? d : 0)
            .Where(d => d > 0)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

    // ------------------------------------------------------------------ HTTP

    /// <summary>
    /// GET /api/stats lists the days that have stats files; GET /api/stats?n=3 returns day 3 of every AI seat
    /// as <c>{ day, players: { p1: {...}, p2: {...} } }</c>, the file contents as written.
    /// </summary>
    public static object Api(AgentLog log, string? n)
    {
        if (!int.TryParse(n, out var day))
            return new { days = StatsDays(log) };

        var players = new Dictionary<string, JsonElement>();
        foreach (var seat in Players.Slots.Where(s => s.Role == SeatRole.Ai))
        {
            var file = StatsFile(log, day, seat.Id);
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                players[seat.Id] = doc.RootElement.Clone();
            }
            catch (Exception)
            {
                // a file being replaced right now: leave the seat out, the next request sees it
            }
        }
        return new { day, players };
    }
}
