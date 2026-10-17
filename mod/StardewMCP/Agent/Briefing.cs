using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;

namespace StardewMCP.Agent;

/// <summary>
/// The morning briefing (#29): one ordered block per AI seat with what happened overnight, mail, shipping by item,
/// today's weather and the farm. Served at <c>GET /api/briefing?seat=p1</c> for the runner's part-1 prompt.
///
/// The block is a list of SECTION PROVIDERS. Each one has an order number and a name and returns text (or null to
/// stay silent); they run on the game thread of the seat's own screen, so <see cref="Game1.player"/> is that seat's
/// farmer. Other features plug in with one line, for example
/// <c>Briefing.Register(60, "calendar", () =&gt; CalendarTools.TodayText());</c>. Registering a name that already exists
/// replaces it, so a richer farm summary can take over the built-in "farm" section.
///
/// Built-in sections (order): 20 date, 30 money, 37 skills, 40 mail, 50 farm. Free slots for the other issues:
/// 10 pass-out reflection (#22), 35 stats line (#25), 60 calendar (#11), 70 plan (#21).
///
/// Overnight changes need a "before" picture, so every seat keeps a snapshot: taken exactly at bedtime by go_to_bed
/// (<see cref="CaptureForSleep"/>) and refreshed every 5 seconds during the day (so a pass-out has one too). Once the
/// next morning has settled the snapshot is compared with the new state and the result is kept for that day.
/// </summary>
public static class Briefing
{
    // ------------------------------------------------------------------ section providers

    private sealed record Provider(int Order, string Name, Func<string?> Text);

    private static readonly object Gate = new();
    private static readonly List<Provider> Providers = new();
    private static readonly SeatBrief[] Seats = Enumerable.Range(0, Players.Seats).Select(_ => new SeatBrief()).ToArray();
    private static bool _warned;

    static Briefing()
    {
        Register(20, "date", DateText);
        Register(30, "money", MoneyText);
        Register(37, "skills", SkillsText);
        Register(40, "mail", MailText);
        Register(50, "farm", FarmText);
    }

    /// <summary>
    /// Add a section, or replace the one with the same name. <paramref name="text"/> runs on the game thread of the
    /// seat being briefed (<see cref="Players.Current"/>) and returns the section's text, or null/empty to skip it.
    /// </summary>
    public static void Register(int order, string name, Func<string?> text)
    {
        lock (Gate)
        {
            Providers.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Providers.Add(new Provider(order, name, text));
        }
    }

    public static void Unregister(string name)
    {
        lock (Gate)
            Providers.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Run every provider in order for the seat of the current screen.</summary>
    public static List<(int Order, string Name, string Text)> BuildSections()
    {
        List<Provider> ordered;
        lock (Gate)
            ordered = Providers.OrderBy(p => p.Order).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var sections = new List<(int, string, string)>();
        foreach (var p in ordered)
        {
            string? text;
            try { text = p.Text(); }
            catch (Exception ex)
            {
                Players.Current.Runtime.Log.Add("system", text: $"Briefing section \"{p.Name}\" failed: {ex.Message}");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(text))
                sections.Add((p.Order, p.Name, text.Trim()));
        }
        return sections;
    }

    // ------------------------------------------------------------------ per-seat state

    private sealed class SeatBrief
    {
        /// <summary>The last picture of the seat before the night (see class summary).</summary>
        public Snapshot? Before;
        /// <summary>go_to_bed took the bedtime picture; the 5-second refresh must not overwrite it.</summary>
        public bool Sealed;
        /// <summary>A new day has started and the overnight report is not built yet.</summary>
        public bool Pending;
        public int PendingTicks;
        public Report? Overnight;
    }

    private sealed class BinLine
    {
        public string Name = "";
        public int Quality;
        public int Qty;
        public int Gold;
    }

    private sealed record CropSnap(string Name, bool Dead, bool Ready, bool Watered);

    private sealed record BuildSnap(string Type, bool UnderConstruction, bool Upgrading);

    private sealed class Snapshot
    {
        public string Save = "";
        public int Day;
        public int Money;
        public List<BinLine> Bin = new();
        public List<string> Mail = new();
        public int[] Skills = new int[5];
        public string? ToolName;
        public int ToolDays;
        public Dictionary<(int X, int Y), CropSnap> Crops = new();
        public Dictionary<string, BuildSnap> Buildings = new();
    }

    /// <summary>What changed between bedtime and this morning.</summary>
    private sealed class Report
    {
        public int Day;
        public int MoneyBefore;
        public int MoneyAfter;
        public bool SharedBin;
        public List<BinLine> Shipped = new();
        public List<string> NewMail = new();
        public List<string> LevelUps = new();
        public List<string> FarmLines = new();
        public bool ToolFinished;
    }

    // ------------------------------------------------------------------ hooks (ModEntry, go_to_bed)

    /// <summary>SMAPI DayStarted, raised on every screen: the overnight report is built once the morning has settled.</summary>
    public static void OnDayStarted()
    {
        var seat = Players.Current;
        if (seat.Role != SeatRole.Ai) return;
        var b = Seats[seat.Screen];
        b.Pending = true;
        b.PendingTicks = 0;
    }

    /// <summary>Every tick of an AI seat's screen. <paramref name="refresh"/> is true about every 5 seconds.</summary>
    public static void Tick(PlayerSlot seat, bool refresh)
    {
        if (seat.Role != SeatRole.Ai || !Context.IsWorldReady) return;
        var b = Seats[seat.Screen];
        try
        {
            // A day change without the event (or before it) is noticed here too.
            if (!b.Pending && b.Before != null && b.Before.Day < AgentLog.FarmDay)
            {
                b.Pending = true;
                b.PendingTicks = 0;
            }

            if (b.Pending)
            {
                b.PendingTicks++;
                // Wait for the night menus and fades to finish (and the farmhand's copy of the farm to sync).
                bool quiet = Game1.activeClickableMenu == null && !Game1.fadeToBlack && !Game1.newDay && !Game1.isWarping;
                if (b.PendingTicks >= 90 && (quiet || b.PendingTicks >= 900))
                    Settle(seat, b);
                return;
            }

            if (!refresh || b.Sealed || NightFlow()) return;
            b.Before = Take();
        }
        catch (Exception ex)
        {
            if (!_warned)
                seat.Runtime.Log.Add("system", text: $"Briefing snapshot failed (further errors are not logged): {ex}");
            _warned = true;
        }
    }

    /// <summary>go_to_bed calls this right before the farmer sleeps: the exact picture the night is compared with.</summary>
    public static void CaptureForSleep()
    {
        var b = Seats[Players.Current.Screen];
        try
        {
            b.Before = Take();
            b.Sealed = true;
        }
        catch (Exception ex)
        {
            Players.Current.Runtime.Log.Add("system", text: $"Briefing bedtime snapshot failed: {ex.Message}");
        }
    }

    /// <summary>go_to_bed calls this when sleeping did not work, so the day goes on with fresh snapshots.</summary>
    public static void Unseal() => Seats[Players.Current.Screen].Sealed = false;

    private static bool NightFlow() =>
        Game1.fadeToBlack || Game1.newDay || Game1.activeClickableMenu is ShippingMenu or LevelUpMenu;

    private static void Settle(PlayerSlot seat, SeatBrief b)
    {
        var after = Take();
        var before = b.Before;
        b.Overnight = before != null && before.Save == after.Save && before.Day < after.Day ? Diff(before, after) : null;
        b.Before = after;
        b.Sealed = false;
        b.Pending = false;
        if (b.Overnight is { } r)
            seat.Runtime.Log.Add("system", text: $"Overnight report for day {r.Day}: shipped {r.Shipped.Sum(l => l.Gold)}g in {r.Shipped.Count} line(s), money {r.MoneyBefore}g → {r.MoneyAfter}g, {r.NewMail.Count} new letter(s)");
    }

    // ------------------------------------------------------------------ the HTTP side

    private static readonly ToolDef Tool = new()
    {
        Name = "briefing",
        Description = "Morning briefing for /api/briefing (an HTTP endpoint for the runner, not an MCP tool)",
        Schema = new { },
        Instant = true, AppendStatus = false, Mail = false,
        Run = RunTool
    };

    /// <summary>
    /// Answer GET /api/briefing?seat=p1 as JSON {seat, day, ready, text, sections:[{order,name,text}]}.
    /// <c>ready</c> is false while the night is still being processed; the caller should ask again in a second.
    /// Returns a 404 status for a seat that is not an AI seat.
    /// </summary>
    public static async Task<(int Status, string Body)> Serve(string? seatId, bool splitScreen)
    {
        var seat = string.IsNullOrWhiteSpace(seatId) ? Players.Host : Players.ById(seatId.Trim());
        if (seat is not { Role: SeatRole.Ai } || (seat.Screen > 0 && !splitScreen))
            return (404, "");
        if (!seat.Active)
            return (200, NotReady(seat.Id, "the seat is not in the game yet"));

        var (ok, text) = await seat.Runtime.Enqueue(Tool, JsonDocument.Parse("{}").RootElement, observation: true);
        return (200, ok ? text : NotReady(seat.Id, text));
    }

    private static string NotReady(string seat, string reason) =>
        JsonSerializer.Serialize(new { seat, ready = false, reason, text = "", sections = Array.Empty<object>() }, AgentLog.Json);

    private static IEnumerator RunTool(JsonElement args, CallContext c)
    {
        var seat = Players.Current;
        var b = Seats[seat.Screen];
        // Ready once the overnight report was built (or there was nothing to compare with).
        bool waiting = b.Pending || NightFlow() || (b.Before != null && b.Before.Day < AgentLog.FarmDay && b.Overnight?.Day != AgentLog.FarmDay);
        var sections = BuildSections();

        var sb = new StringBuilder($"Morning briefing, farm day {AgentLog.FarmDay} ({seat.Id})");
        foreach (var s in sections)
            sb.Append('\n').Append(s.Text);

        c.Result = JsonSerializer.Serialize(new
        {
            seat = seat.Id,
            day = AgentLog.FarmDay,
            ready = !waiting,
            text = sb.ToString(),
            sections = sections.Select(s => new { order = s.Order, name = s.Name, text = s.Text })
        }, AgentLog.Json);
        yield break;
    }

    // ------------------------------------------------------------------ mailbox hint for the footer

    /// <summary>"📬 N letters in the mailbox" while the current farmer has mail waiting, else "".</summary>
    public static string MailHint()
    {
        int n = Game1.player.mailbox.Count;
        return n > 0 ? $"📬 {n} {(n == 1 ? "letter" : "letters")} in the mailbox" : "";
    }

    // ------------------------------------------------------------------ taking and comparing pictures

    private static Snapshot Take()
    {
        var p = Game1.player;
        var snap = new Snapshot
        {
            Save = SaveFolder.Name ?? "",
            Day = AgentLog.FarmDay,
            Money = p.Money,
            Bin = ShippingBinLines(p),
            Skills = new[] { p.farmingLevel.Value, p.fishingLevel.Value, p.foragingLevel.Value, p.miningLevel.Value, p.combatLevel.Value },
            ToolName = p.toolBeingUpgraded.Value?.DisplayName,
            ToolDays = p.daysLeftForToolUpgrade.Value,
            Crops = ScanCrops(),
            Buildings = ScanBuildings()
        };
        foreach (var id in p.mailbox)
            snap.Mail.Add(id);
        return snap;
    }

    /// <summary>What the farmer's shipping bin holds, grouped by item and quality, with the price the game would pay.</summary>
    private static List<BinLine> ShippingBinLines(Farmer p)
    {
        var groups = new Dictionary<string, BinLine>();
        foreach (var item in Game1.getFarm().getShippingBin(p))
        {
            if (item == null) continue;
            string key = $"{item.QualifiedItemId}|{item.Quality}";
            if (!groups.TryGetValue(key, out var line))
                groups[key] = line = new BinLine { Name = item.DisplayName, Quality = item.Quality };
            line.Qty += item.Stack;
            line.Gold += Math.Max(0, item.sellToStorePrice()) * item.Stack;
        }
        return groups.Values.OrderByDescending(l => l.Gold).ToList();
    }

    private static Dictionary<(int X, int Y), CropSnap> ScanCrops()
    {
        var crops = new Dictionary<(int X, int Y), CropSnap>();
        foreach (var pair in Game1.getFarm().terrainFeatures.Pairs)
        {
            if (pair.Value is not HoeDirt dirt || dirt.crop == null) continue;
            var crop = dirt.crop;
            bool dead = crop.dead.Value;
            crops[((int)pair.Key.X, (int)pair.Key.Y)] = new CropSnap(WorldText.CropName(crop), dead, !dead && dirt.readyForHarvest(), dirt.isWatered());
        }
        return crops;
    }

    private static Dictionary<string, BuildSnap> ScanBuildings()
    {
        var buildings = new Dictionary<string, BuildSnap>();
        foreach (var b in Game1.getFarm().buildings)
            buildings[$"{b.tileX.Value},{b.tileY.Value}"] = new BuildSnap(b.buildingType.Value, b.isUnderConstruction(), b.daysUntilUpgrade.Value > 0);
        return buildings;
    }

    private static Report Diff(Snapshot before, Snapshot after)
    {
        var r = new Report
        {
            Day = after.Day,
            MoneyBefore = before.Money,
            MoneyAfter = after.Money,
            SharedBin = Players.SplitScreen && !Game1.player.team.useSeparateWallets.Value,
            Shipped = before.Bin,
            NewMail = after.Mail.Where(id => !before.Mail.Contains(id)).ToList()
        };

        for (int i = 0; i < before.Skills.Length; i++)
            if (after.Skills[i] > before.Skills[i])
                r.LevelUps.Add($"{Professions.SkillName(i)} {before.Skills[i]} → {after.Skills[i]}");

        // Crops: newly ready, died (kept as dead plants), or gone from the tile (crows, lightning).
        var ready = new List<(string Name, int X, int Y)>();
        var died = new List<(string Name, int X, int Y)>();
        var gone = new List<(string Name, int X, int Y)>();
        foreach (var kv in after.Crops)
            if (kv.Value.Ready && !(before.Crops.TryGetValue(kv.Key, out var was) && was.Ready))
                ready.Add((kv.Value.Name, kv.Key.X, kv.Key.Y));
        foreach (var kv in before.Crops)
        {
            if (!after.Crops.TryGetValue(kv.Key, out var now))
                gone.Add((kv.Value.Name, kv.Key.X, kv.Key.Y));
            else if (now.Dead && !kv.Value.Dead)
                died.Add((kv.Value.Name, kv.Key.X, kv.Key.Y));
        }
        if (ready.Count > 0) r.FarmLines.Add($"{ready.Count} crop(s) became ready overnight: {Describe(ready)}.");
        if (died.Count > 0) r.FarmLines.Add($"{died.Count} crop(s) died overnight (usually the season changed): {Describe(died)}.");
        if (gone.Count > 0) r.FarmLines.Add($"{gone.Count} crop(s) vanished overnight (eaten by crows or struck by lightning): {Describe(gone)}. A scarecrow protects nearby crops.");

        foreach (var kv in after.Buildings)
        {
            if (!before.Buildings.TryGetValue(kv.Key, out var was)) continue;
            if (was.UnderConstruction && !kv.Value.UnderConstruction)
                r.FarmLines.Add($"Construction finished: {kv.Value.Type}.");
            else if (was.Upgrading && !kv.Value.Upgrading)
                r.FarmLines.Add($"Building upgrade finished: {was.Type} → {kv.Value.Type}.");
        }

        r.ToolFinished = before.ToolName != null && before.ToolDays > 0 && after.ToolName != null && after.ToolDays <= 0;
        return r;
    }

    // ------------------------------------------------------------------ built-in sections

    private static Report? Overnight()
    {
        var r = Seats[Players.Current.Screen].Overnight;
        return r != null && r.Day == AgentLog.FarmDay ? r : null;
    }

    private static string? DateText()
    {
        var sb = new StringBuilder();
        sb.Append($"Farm day {AgentLog.FarmDay}: {Game1.currentSeason} {Game1.dayOfMonth}, year {Game1.year} ({Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth)}). ");
        var today = WorldText.Weather();
        sb.Append($"Weather today: {today}");
        if (today is "rain" or "storm")
            sb.Append(" (the rain waters crops outside: no need to use the watering can today)");
        sb.Append('.');
        var tomorrow = TomorrowWeather();
        if (tomorrow != null)
            sb.Append($" Tomorrow: {tomorrow}.");
        sb.Append($" Luck: {LuckText(Game1.player.DailyLuck)}.");
        return sb.ToString();
    }

    private static string LuckText(double luck)
    {
        var label = luck >= 0.07 ? "very lucky" : luck >= 0.02 ? "lucky" : luck <= -0.07 ? "very unlucky" : luck <= -0.02 ? "unlucky" : "neutral";
        return $"{label} ({luck.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)})";
    }

    private static string? MoneyText()
    {
        var p = Game1.player;
        var r = Overnight();
        var sb = new StringBuilder($"Money: {p.Money}g");
        if (r != null)
            sb.Append($" (at bedtime {r.MoneyBefore}g, {(r.MoneyAfter - r.MoneyBefore >= 0 ? "+" : "")}{r.MoneyAfter - r.MoneyBefore}g overnight)");
        sb.Append('.');
        if (r == null) return sb.ToString();

        if (r.Shipped.Count == 0)
            sb.Append("\nShipped yesterday: nothing.");
        else
        {
            sb.Append("\nShipped yesterday, best first: ").Append(string.Join(", ", r.Shipped.Select(BinText)));
            sb.Append($". Total {r.Shipped.Sum(l => l.Gold)}g (the game's shop prices; professions can change the final sum).");
            if (r.SharedBin)
                sb.Append(" The wallet and bin are shared, so this includes everything the other players shipped.");
        }
        return sb.ToString();
    }

    private static string BinText(BinLine l)
    {
        var quality = l.Quality switch { 1 => " (silver)", 2 => " (gold)", 4 => " (iridium)", _ => "" };
        var each = l.Qty > 1 ? $" ({l.Gold / l.Qty}g each)" : "";
        return $"{l.Name}{quality} x{l.Qty} = {l.Gold}g{each}";
    }

    private static string? SkillsText()
    {
        var lines = new List<string>();
        var r = Overnight();
        if (r != null && r.LevelUps.Count > 0)
            lines.Add("Skill level-ups: " + string.Join(", ", r.LevelUps) + ".");
        if (Players.Current.Runtime.PendingProfessions.Count > 0)
            lines.Add("A profession choice is waiting: read status, then choose_profession.");
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string? MailText()
    {
        var p = Game1.player;
        int n = p.mailbox.Count;
        if (n == 0) return "Mail: none.";
        var ids = p.mailbox.Take(6).ToList();
        var sb = new StringBuilder($"Mail: {n} {(n == 1 ? "letter" : "letters")} in the mailbox (ids: {string.Join(", ", ids)}{(n > ids.Count ? ", ..." : "")}).");
        var r = Overnight();
        if (r != null && r.NewMail.Count > 0)
            sb.Append($" {r.NewMail.Count} arrived overnight.");
        sb.Append(" Letters can hold rewards, recipes and quests: walk to the mailbox next to your house on the Farm and use interact on it; attached items are claimed when you close each letter.");
        return sb.ToString();
    }

    private static string? FarmText()
    {
        var lines = new List<string>();
        var crops = ScanCrops();
        if (crops.Count == 0)
            lines.Add("Crops: none planted.");
        else
        {
            var ready = crops.Where(kv => kv.Value.Ready).Select(kv => (kv.Value.Name, kv.Key.X, kv.Key.Y)).ToList();
            var dry = crops.Where(kv => !kv.Value.Dead && !kv.Value.Ready && !kv.Value.Watered).Select(kv => (kv.Value.Name, kv.Key.X, kv.Key.Y)).ToList();
            var dead = crops.Where(kv => kv.Value.Dead).Select(kv => (kv.Value.Name, kv.Key.X, kv.Key.Y)).ToList();
            lines.Add($"Crops: {crops.Count} planted; {Count(ready, "ready to harvest")}; {Count(dry, "need water")}; {Count(dead, "dead")}.");
        }

        var r = Overnight();
        if (r != null)
            lines.AddRange(r.FarmLines.Select(l => "Overnight: " + l));

        var p = Game1.player;
        var tool = p.toolBeingUpgraded.Value;
        if (tool != null)
        {
            int days = p.daysLeftForToolUpgrade.Value;
            lines.Add(days > 0
                ? $"Tool upgrade: {tool.DisplayName} is being upgraded at Clint's, {days} day(s) left."
                : $"Tool upgrade{(r is { ToolFinished: true } ? " finished overnight" : "")}: {tool.DisplayName} is ready. Pick it up from Clint at the Blacksmith (travel_to Blacksmith, talk to him).");
        }
        var building = Game1.getFarm().buildings.Where(b => b.isUnderConstruction()).Select(b => b.buildingType.Value).ToList();
        if (building.Count > 0)
            lines.Add($"Still under construction: {string.Join(", ", building)}.");
        return string.Join("\n", lines);
    }

    private static string Count(List<(string Name, int X, int Y)> tiles, string what) =>
        tiles.Count == 0 ? $"0 {what}" : $"{tiles.Count} {what} ({Describe(tiles)})";

    /// <summary>"Parsnip x6 in x 58-60, y 17-19; Potato x1 (59,20)": counts by crop with where they are.</summary>
    private static string Describe(List<(string Name, int X, int Y)> tiles)
    {
        var groups = tiles.GroupBy(t => t.Name).OrderByDescending(g => g.Count()).ToList();
        var parts = groups.Take(4).Select(g =>
        {
            var pts = g.ToList();
            var where = pts.Count <= 3
                ? string.Join(" ", pts.Select(t => $"({t.X},{t.Y})"))
                : $"in x {pts.Min(t => t.X)}-{pts.Max(t => t.X)}, y {pts.Min(t => t.Y)}-{pts.Max(t => t.Y)}";
            return $"{g.Key} x{pts.Count} {where}";
        });
        return string.Join("; ", parts) + (groups.Count > 4 ? "; ..." : "");
    }

    // ------------------------------------------------------------------ tomorrow's weather

    /// <summary>
    /// Tomorrow's forecast, or null when the game's field could not be found. The member is looked up by name with plain
    /// reflection so a wrong guess costs one line in the briefing instead of a build error; see the PR's unverified list.
    /// </summary>
    private static string? TomorrowWeather()
    {
        try
        {
            object? raw = ReadMember(null, typeof(Game1), "weatherForTomorrow");
            if (raw == null)
            {
                // Stardew 1.6 keeps weather per location context.
                var world = Game1.netWorldState.Value;
                var method = world.GetType().GetMethod("GetWeatherForLocation", new[] { typeof(string) });
                var weather = method?.Invoke(world, new object[] { "Default" });
                raw = ReadMember(weather, weather?.GetType(), "WeatherForTomorrow");
            }
            return raw switch
            {
                string s when s.Length > 0 => ForecastName(s),
                int i => ForecastName(i switch { 0 => "sun", 1 => "rain", 2 => "wind", 3 => "storm", 4 => "festival", 5 => "snow", 6 => "wedding", _ => "" }),
                _ => null
            };
        }
        catch (Exception ex)
        {
            if (!_warned)
                Players.Current.Runtime.Log.Add("system", text: $"Briefing: tomorrow's weather is not available ({ex.Message})");
            _warned = true;
            return null;
        }
    }

    private static string? ForecastName(string id)
    {
        var s = id.ToLowerInvariant();
        if (s.Length == 0) return null;
        if (s.Contains("green")) return "green rain";
        if (s.Contains("storm") || s.Contains("thunder") || s.Contains("lightning")) return "storm";
        if (s.Contains("rain")) return "rain";
        if (s.Contains("snow")) return "snow";
        if (s.Contains("wind") || s.Contains("debris")) return "windy";
        if (s.Contains("sun") || s.Contains("clear")) return "sunny";
        if (s.Contains("festival")) return "a festival";
        if (s.Contains("wedding")) return "a wedding";
        return id;
    }

    /// <summary>Value of a field or property by name (static when <paramref name="target"/> is null); net fields are unwrapped to their Value.</summary>
    private static object? ReadMember(object? target, Type? type, string name)
    {
        if (type == null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.IgnoreCase;
        object? value = type.GetField(name, flags)?.GetValue(target) ?? type.GetProperty(name, flags)?.GetValue(target);
        if (value == null || value is string || value.GetType().IsPrimitive) return value;
        return value.GetType().GetProperty("Value")?.GetValue(value) ?? value;
    }
}
