using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// Whole-farm batch tools: farm_summary, water_all, harvest_all, clear_area, till_area and the area mode of plant.
/// One call is one intention ("water everything") instead of a look, a copy of coordinates and several tool calls.
/// They scan the current map themselves and then walk, swing and harvest with the same building blocks as
/// use_tool / plant / harvest (<see cref="WalkAdjacent"/>, <see cref="Swing"/>, <see cref="Harvest"/>, <see cref="Plant"/>).
/// Every batch stops, and says why, when a menu opens, energy drops under
/// <see cref="ModConfig.FarmBatchMinEnergy"/> or it is 00:30 or later. They never eat; the AI decides.
/// </summary>
public static partial class AgentTools
{
    /// <summary>The clock runs past midnight as 2400, 2410...; 00:30 is 2430.</summary>
    private const int FarmLateTime = 2430;
    private const int FarmSeasonDays = 28;
    /// <summary>Tiles handed to harvest / plant at a time, so the stop rules are checked often.</summary>
    private const int FarmChunk = 10;
    /// <summary>Tiles one water_all / harvest_all / plant call works through; a call that runs too long hits the 6 minute action timeout and loses its result.</summary>
    private const int FarmMaxTiles = 150;
    /// <summary>clear_area / till_area take at most this many tiles.</summary>
    private const int FarmAreaMax = 100;
    /// <summary>Tool swings one clear_area / till_area call may use.</summary>
    private const int FarmMaxSwings = 250;

    // ------------------------------------------------------------------ registry

    private static ToolDef FarmSummaryTool() => new()
    {
        Name = "farm_summary",
        Description = "Scan the WHOLE current map (not just what look shows) and summarise the farm: empty tilled soil, crops by type with days to harvest, " +
                      "unwatered, ready and dead crops, crops that won't mature before the season ends, watering can water and the nearest water tile, " +
                      "forage, and debris (weeds, stones, twigs, stumps, boulders). Counts come with bounding boxes. Costs no game time. Start your farm day with it.",
        Schema = Obj(new { }),
        Instant = true,
        Run = (a, c) => Instant(c, FarmSummary())
    };

    private static ToolDef WaterAllTool() => new()
    {
        Name = "water_all",
        Description = "Water every unwatered crop on the current map (dead crops and crops that are ready to harvest are skipped). " +
                      "Refills the watering can at the nearest water by itself when it runs dry. Stops, and tells you why, when energy is low, a menu opens or it is late; " +
                      "it never eats for you. Reports watered / still dry. If it is raining the rain has already done the work.",
        Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
        Run = WaterAll
    };

    private static ToolDef HarvestAllTool() => new()
    {
        Name = "harvest_all",
        Description = "Collect every ready crop and every forage item on the current map (by hand, then with the Scythe for crops that need it). " +
                      "Stops when the inventory is full, a menu opens or it is late, and says what was left. Fruit trees and bushes are not included.",
        Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
        Run = HarvestAll
    };

    private static ToolDef ClearAreaTool() => new()
    {
        Name = "clear_area",
        Description = "Clear a rectangle (x,y is its top-left tile, at most 100 tiles): Scythe for weeds and grass, Pickaxe for stones, Axe for twigs, tree stumps and young trees. " +
                      "Grown trees, fruit trees, objects and big boulders/stumps/logs that need an upgraded tool are left alone and reported. Stops on low energy, a menu or late night.",
        Schema = Obj(new { x = Int("Top-left tile x"), y = Int("Top-left tile y"), w = Int("Width in tiles"), h = Int("Height in tiles"), reason = Str(ReasonDesc) },
            "x", "y", "w", "h", "reason"),
        Run = ClearArea
    };

    private static ToolDef TillAreaTool() => new()
    {
        Name = "till_area",
        Description = "Prepare a field: clears the rectangle first (like clear_area), then hoes every tile that can be tilled (x,y is the top-left tile, at most 100 tiles). " +
                      "Reports tiles that could not be tilled and why (not diggable ground, a boulder, a tree...). Stops on low energy, a menu or late night.",
        Schema = Obj(new { x = Int("Top-left tile x"), y = Int("Top-left tile y"), w = Int("Width in tiles"), h = Int("Height in tiles"), reason = Str(ReasonDesc) },
            "x", "y", "w", "h", "reason"),
        Run = TillArea
    };

    // ------------------------------------------------------------------ shared helpers

    /// <summary>Greedy nearest-neighbour order (Manhattan distance, ties keep the given order) starting from <paramref name="from"/>. Walking costs game time.</summary>
    internal static List<Point> RouteOrder(IEnumerable<Point> tiles, Point from)
    {
        var left = tiles.ToList();
        var route = new List<Point>(left.Count);
        var at = from;
        while (left.Count > 0)
        {
            int best = 0, bestDist = int.MaxValue;
            for (int i = 0; i < left.Count; i++)
            {
                int d = Math.Abs(left[i].X - at.X) + Math.Abs(left[i].Y - at.Y);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            at = left[best];
            route.Add(at);
            left.RemoveAt(best);
        }
        return route;
    }

    /// <summary>
    /// The order use_tool visits its tiles in. A watering can keeps the caller's order when a water tile is in the list,
    /// because the caller put the refill where it wants it.
    /// </summary>
    internal static List<Point> FarmRoute(List<Point> tiles, Tool tool)
    {
        var loc = Loc;
        if (tool is WateringCan && tiles.Any(t => FarmIsWater(loc, t.X, t.Y))) return tiles;
        return RouteOrder(tiles, P.TilePoint);
    }

    /// <summary>Arguments for calling another tool's coroutine from inside a batch.</summary>
    private static JsonElement FarmArgs(object args) => JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement.Clone();

    private static int FarmDist(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static bool FarmIsWater(GameLocation loc, int x, int y) =>
        loc.isWaterTile(x, y) || loc.doesTileHaveProperty(x, y, "Water", "Back") != null;

    /// <summary>Why a batch must stop now, or null: left the map, a menu opened, energy under the configured limit, or 00:30 and later.</summary>
    private static string? FarmStopReason(GameLocation loc, bool needsEnergy = true)
    {
        if (Loc != loc) return "you left the map";
        if (Game1.activeClickableMenu != null) return "a menu/dialogue opened";
        if (needsEnergy && P.Stamina < Math.Max(0, Config.FarmBatchMinEnergy))
            return $"energy is low ({(int)P.Stamina}, limit {Config.FarmBatchMinEnergy}); eat something or go to bed";
        if (Game1.timeOfDay >= FarmLateTime) return $"it is late ({WorldText.FormatTime(Game1.timeOfDay)}); go to bed";
        return null;
    }

    private static List<Point> FarmTilesWhere(GameLocation loc, Func<GameLocation, int, int, bool> match)
    {
        var list = new List<Point>();
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (match(loc, x, y)) list.Add(new Point(x, y));
        return list;
    }

    private static HoeDirt? FarmDirt(GameLocation loc, int x, int y) =>
        loc.terrainFeatures.TryGetValue(new Vector2(x, y), out var tf) && tf is HoeDirt d ? d : null;

    /// <summary>A growing crop that is dry: not dead, not waiting for harvest, not watered.</summary>
    private static bool FarmCropNeedsWater(GameLocation loc, int x, int y) =>
        FarmDirt(loc, x, y) is { crop: not null } d && !d.crop.dead.Value && !d.readyForHarvest() && !d.isWatered();

    /// <summary>A ripe crop on this tile.</summary>
    private static bool FarmRipeCrop(GameLocation loc, int x, int y) =>
        FarmDirt(loc, x, y) is { crop: not null } d && !d.crop.dead.Value && d.readyForHarvest();

    /// <summary>A ripe crop or a forage item: what harvest_all picks up.</summary>
    private static bool FarmPickable(GameLocation loc, int x, int y) =>
        FarmRipeCrop(loc, x, y) || (loc.Objects.TryGetValue(new Vector2(x, y), out var o) && o.isForage());

    private static string FarmTilesText(IEnumerable<Point> tiles, int max = 8)
    {
        var all = tiles.ToList();
        var text = string.Join(" ", all.Take(max).Select(t => $"({t.X},{t.Y})"));
        return all.Count > max ? $"{text} (+{all.Count - max} more)" : text;
    }

    private static string FarmCounts(Dictionary<string, int> counts) =>
        string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));

    private static void FarmCount(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out var n);
        counts[key] = n + 1;
    }

    /// <summary>Count and bounding box of a set of tiles.</summary>
    private sealed class FarmBox
    {
        public int Count, MinX = int.MaxValue, MaxX = int.MinValue, MinY = int.MaxValue, MaxY = int.MinValue;

        public void Add(int x, int y)
        {
            Count++;
            MinX = Math.Min(MinX, x); MaxX = Math.Max(MaxX, x);
            MinY = Math.Min(MinY, y); MaxY = Math.Max(MaxY, y);
        }

        public override string ToString() => Count == 0 ? "0" : $"{Count} (x {MinX}-{MaxX}, y {MinY}-{MaxY})";
    }

    // ------------------------------------------------------------------ farm_summary

    private sealed class FarmCropStat
    {
        public int Total, Ready, Dead, Dry, Regrowing, Growing, TooLate;
        public int MinDays = int.MaxValue, MaxDays, LateMaxDays;
        public readonly FarmBox LateBox = new();
    }

    /// <summary>
    /// Nights until a growing crop can be harvested if it is watered every day, or null when it is fully grown
    /// (ready now, or waiting for its regrowth). The last growth phase is the harvestable one.
    /// </summary>
    private static int? FarmDaysToHarvest(Crop crop)
    {
        int last = crop.phaseDays.Count - 1;
        int phase = crop.currentPhase.Value;
        if (last < 0 || phase >= last) return null;
        int days = -crop.dayOfCurrentPhase.Value;
        for (int p = phase; p < last; p++) days += crop.phaseDays[p];
        return Math.Max(days, 0);
    }

    private static string FarmSummary()
    {
        var loc = Loc;
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        var me = P.TilePoint;
        int daysLeft = FarmSeasonDays - Game1.dayOfMonth;
        // Outdoor crops die when the season changes (the night of day 28); the greenhouse and indoor pots don't.
        bool seasonRule = loc is Farm && Game1.currentSeason != "winter";

        var emptySoil = new FarmBox();
        var ready = new FarmBox();
        var dry = new FarmBox();
        var dead = new FarmBox();
        var forage = new FarmBox();
        var crops = new SortedDictionary<string, FarmCropStat>();
        var debris = new SortedDictionary<string, FarmBox>();
        var clumps = loc.resourceClumps.ToList();
        var seenClumps = new HashSet<object>();
        Point? water = null;
        int waterDist = int.MaxValue;

        void Debris(string kind, int x, int y)
        {
            if (!debris.TryGetValue(kind, out var box)) debris[kind] = box = new FarmBox();
            box.Add(x, y);
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var v = new Vector2(x, y);
                if (loc.Objects.TryGetValue(v, out var obj))
                {
                    if (obj.IsWeeds()) Debris("weeds", x, y);
                    else if (obj.IsTwig()) Debris("twigs", x, y);
                    else if (obj.IsBreakableStone()) Debris("stones", x, y);
                    else if (obj.isForage()) forage.Add(x, y);
                }

                foreach (var rc in clumps)
                    if (rc.occupiesTile(x, y) && seenClumps.Add(rc))
                        Debris(WorldText.ClumpName(rc.parentSheetIndex.Value).ToLowerInvariant(), x, y);

                if (loc.terrainFeatures.TryGetValue(v, out var tf))
                {
                    switch (tf)
                    {
                        case Grass:
                            Debris("grass", x, y);
                            break;
                        case Tree tree:
                            if (tree.stump.Value) Debris("tree stumps", x, y);
                            else if (tree.growthStage.Value < 5) Debris("young trees", x, y);
                            break;
                        case HoeDirt dirt:
                            if (dirt.crop == null) { emptySoil.Add(x, y); break; }
                            var crop = dirt.crop;
                            var cropName = WorldText.CropName(crop);
                            if (!crops.TryGetValue(cropName, out var stat)) crops[cropName] = stat = new FarmCropStat();
                            stat.Total++;
                            if (crop.dead.Value) { stat.Dead++; dead.Add(x, y); break; }
                            if (dirt.readyForHarvest()) { stat.Ready++; ready.Add(x, y); break; }
                            if (!dirt.isWatered()) { stat.Dry++; dry.Add(x, y); }
                            if (FarmDaysToHarvest(crop) is int days)
                            {
                                stat.Growing++;
                                stat.MinDays = Math.Min(stat.MinDays, days);
                                stat.MaxDays = Math.Max(stat.MaxDays, days);
                                if (seasonRule && days > daysLeft)
                                {
                                    stat.TooLate++;
                                    stat.LateMaxDays = Math.Max(stat.LateMaxDays, days);
                                    stat.LateBox.Add(x, y);
                                }
                            }
                            else stat.Regrowing++;
                            break;
                    }
                }

                if (FarmIsWater(loc, x, y))
                {
                    int d = FarmDist(me, new Point(x, y));
                    if (d < waterDist) { waterDist = d; water = new Point(x, y); }
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Farm summary of {loc.Name} ({w}x{h}): {Game1.currentSeason} {Game1.dayOfMonth}, {daysLeft} days left in the season. You are at ({me.X},{me.Y}).");
        sb.AppendLine($"Empty tilled soil: {emptySoil}");
        if (crops.Count == 0)
            sb.AppendLine("Crops: none planted.");
        else
        {
            sb.AppendLine($"Crops: {crops.Values.Sum(s => s.Total)} planted");
            foreach (var kv in crops)
            {
                var s = kv.Value;
                var parts = new List<string> { $"{s.Ready} ready" };
                if (s.Growing > 0) parts.Add($"{s.Growing} growing ({(s.MinDays == s.MaxDays ? $"{s.MinDays}" : $"{s.MinDays}-{s.MaxDays}")} days to harvest if watered daily)");
                if (s.Regrowing > 0) parts.Add($"{s.Regrowing} regrowing");
                parts.Add($"{s.Dry} unwatered");
                if (s.Dead > 0) parts.Add($"{s.Dead} dead");
                sb.AppendLine($"  {kv.Key} x{s.Total}: {string.Join(", ", parts)}");
            }
        }
        sb.AppendLine($"Ready to harvest: {ready}");
        sb.AppendLine($"Unwatered crops: {dry}");
        sb.AppendLine($"Dead crops: {dead}");
        var late = crops.Where(kv => kv.Value.TooLate > 0).ToList();
        if (late.Count > 0)
        {
            sb.AppendLine($"Will NOT mature before the season ends ({daysLeft} days left):");
            foreach (var kv in late)
                sb.AppendLine($"  {kv.Key} x{kv.Value.TooLate}: needs up to {kv.Value.LateMaxDays} more days, {kv.Value.LateBox}");
        }
        sb.AppendLine($"Forage items: {forage}");

        var can = P.Items.OfType<WateringCan>().FirstOrDefault();
        sb.AppendLine(can == null ? "Watering can: you have none." : $"Watering can: {can.WaterLeft}/{can.waterCanMax} water.");
        sb.AppendLine(water is Point wt ? $"Nearest water tile: ({wt.X},{wt.Y}), {waterDist} tiles away." : $"Nearest water tile: none on {loc.Name}.");

        if (debris.Count == 0)
            sb.Append("Debris: none.");
        else
        {
            sb.AppendLine("Debris (clear_area removes weeds, grass, stones, twigs, tree stumps and young trees; large stumps, logs and boulders need an upgraded tool):");
            foreach (var kv in debris) sb.AppendLine($"  {kv.Key}: {kv.Value}");
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ water_all

    private static IEnumerator WaterAll(JsonElement a, CallContext c)
    {
        var loc = Loc;
        int slot = FindItem("Watering Can", it => it is WateringCan);
        if (slot < 0) { c.Fail("You don't have a Watering Can. Check inventory."); yield break; }
        var can = (WateringCan)P.Items[slot];

        var todo = FarmTilesWhere(loc, FarmCropNeedsWater);
        if (todo.Count == 0)
        {
            bool rain = loc is Farm && (Game1.isRaining || Game1.isLightning);
            c.Result = rain ? "It is raining: the rain watered every crop. Nothing to do." : $"No unwatered crops on {loc.Name}.";
            yield break;
        }

        var remaining = RouteOrder(todo, P.TilePoint);
        int watered = 0, wet = 0, refills = 0, visited = 0;
        string? stop = null;
        var problems = new List<string>();

        while (remaining.Count > 0)
        {
            if (visited >= FarmMaxTiles) { stop = $"batch limit of {FarmMaxTiles} tiles per call; call water_all again"; break; }
            stop = FarmStopReason(loc);
            if (stop != null) break;

            if (can.WaterLeft <= 0)
            {
                var refill = new CallContext();
                yield return FarmRefill(slot, refill);
                if (!refill.Ok) { stop = refill.Result; break; }
                refills++;
                remaining = RouteOrder(remaining, P.TilePoint); // plan again from the water
                continue;
            }

            var t = remaining[0];
            remaining.RemoveAt(0);
            visited++;
            if (!FarmCropNeedsWater(loc, t.X, t.Y)) { wet++; continue; } // watered meanwhile (sprinkler, rain)
            var walk = new CallContext();
            yield return WalkAdjacent(t, walk);
            if (!walk.Ok) { problems.Add($"({t.X},{t.Y}) skipped: {walk.Result}"); continue; }
            P.CurrentToolIndex = slot;
            yield return Swing(t);
            if (FarmCropNeedsWater(loc, t.X, t.Y)) problems.Add($"({t.X},{t.Y}) is still dry after watering");
            else watered++;
        }

        int dry = remaining.Count + problems.Count;
        var sb = new StringBuilder();
        sb.Append($"Watered {watered} of {todo.Count} crops on {loc.Name}");
        if (refills > 0) sb.Append($" ({refills} refill{(refills == 1 ? "" : "s")})");
        sb.Append(dry == 0 ? ". partial=false" : $". {dry} still dry, partial=true");
        sb.Append($". Watering can {can.WaterLeft}/{can.waterCanMax}.");
        if (wet > 0) sb.Append($" {wet} were already watered.");
        if (stop != null) sb.Append($"\nStopped: {stop}.");
        if (remaining.Count > 0) sb.Append($"\nNot reached: {FarmTilesText(remaining)}");
        foreach (var p in problems.Take(6)) sb.Append('\n').Append(p);
        if (problems.Count > 6) sb.Append($"\n(+{problems.Count - 6} more)");
        c.Result = sb.ToString();
    }

    /// <summary>Walk to the nearest water that can be reached and fill the can. Fails when none can be reached.</summary>
    private static IEnumerator FarmRefill(int slot, CallContext c)
    {
        var loc = Loc;
        var can = (WateringCan)P.Items[slot];
        var me = P.TilePoint;
        var waters = FarmTilesWhere(loc, FarmIsWater).OrderBy(t => FarmDist(me, t)).Take(6).ToList();
        if (waters.Count == 0) { c.Fail($"the can is empty and {loc.Name} has no water tile to refill it"); yield break; }
        foreach (var water in waters)
        {
            var walk = new CallContext();
            yield return WalkAdjacent(water, walk);
            if (!walk.Ok) continue;
            P.CurrentToolIndex = slot;
            yield return Swing(water);
            if (can.WaterLeft > 0) yield break;
        }
        c.Fail("the can is empty and no water tile could be reached to refill it");
    }

    // ------------------------------------------------------------------ harvest_all

    private static IEnumerator HarvestAll(JsonElement a, CallContext c)
    {
        var loc = Loc;
        var route = RouteOrder(FarmTilesWhere(loc, FarmPickable), P.TilePoint);
        if (route.Count == 0) { c.Result = $"Nothing to harvest on {loc.Name}: no ready crops or forage."; yield break; }

        var before = Snapshot();
        string? stop = null;
        int tried = 0;

        // Hand harvest through the existing harvest tool, ten tiles at a time.
        for (int i = 0; i < route.Count; i += FarmChunk)
        {
            if (tried >= FarmMaxTiles) { stop = $"batch limit of {FarmMaxTiles} tiles per call; call harvest_all again"; break; }
            stop = FarmStopReason(loc, needsEnergy: false);
            if (stop != null) break;
            var chunk = route.GetRange(i, Math.Min(FarmChunk, route.Count - i));
            tried += chunk.Count;
            yield return Harvest(FarmArgs(new { tiles = chunk.Select(t => new[] { t.X, t.Y }).ToList() }), new CallContext());
            if (Game1.activeClickableMenu != null) { stop = "a menu/dialogue opened"; break; }
            if (chunk.Any(t => FarmPickable(loc, t.X, t.Y)) && !P.Items.Any(it => it == null)) { stop = "the inventory is full"; break; }
        }

        // Crops that only a scythe can cut (kale, wheat...) stay ripe after the hand harvest.
        if (stop == null)
        {
            var ripe = route.Where(t => FarmRipeCrop(loc, t.X, t.Y)).ToList();
            int scythe = ripe.Count > 0 ? FindItem("Scythe", it => it is Tool) : -1;
            if (scythe >= 0)
            {
                foreach (var t in RouteOrder(ripe, P.TilePoint))
                {
                    stop = FarmStopReason(loc);
                    if (stop != null) break;
                    if (!FarmRipeCrop(loc, t.X, t.Y)) continue; // an earlier swing took it
                    var walk = new CallContext();
                    yield return WalkAdjacent(t, walk);
                    if (!walk.Ok) continue;
                    P.CurrentToolIndex = scythe;
                    yield return Swing(t);
                }
            }
        }

        yield return Ticks(30); // let dropped items fly into the inventory
        var left = route.Where(t => FarmPickable(loc, t.X, t.Y)).ToList();
        var diff = Diff(before);
        var sb = new StringBuilder();
        sb.Append($"Harvested {route.Count - left.Count} of {route.Count} ready tiles on {loc.Name}. {(diff.Length > 0 ? diff : "Nothing was collected.")}");
        if (stop != null) sb.Append($"\nStopped: {stop}.");
        if (left.Count > 0)
        {
            sb.Append($"\nLeft {left.Count}: {FarmTilesText(left)}");
            if (stop == null) sb.Append(" (unreachable, or they need a Scythe you don't have)");
        }
        c.Result = sb.ToString();
    }

    // ------------------------------------------------------------------ clear_area / till_area

    /// <summary>What covers a tile, as far as clearing and tilling are concerned.</summary>
    private enum FarmCover { None, Weeds, Grass, Twig, Stone, Stump, Sapling, Large, Tree, FruitTree, Tilled, Other }

    private static bool FarmClearable(FarmCover cover) =>
        cover is FarmCover.Weeds or FarmCover.Grass or FarmCover.Twig or FarmCover.Stone or FarmCover.Stump or FarmCover.Sapling;

    /// <summary>The cover of a tile with a label, and the clump itself for large obstacles (one clump spans several tiles).</summary>
    private static (FarmCover cover, string label, object? key) FarmCoverAt(GameLocation loc, int x, int y)
    {
        var v = new Vector2(x, y);
        if (loc.Objects.TryGetValue(v, out var obj))
        {
            if (obj.IsWeeds()) return (FarmCover.Weeds, "weeds", null);
            if (obj.IsTwig()) return (FarmCover.Twig, "twig", null);
            if (obj.IsBreakableStone()) return (FarmCover.Stone, "stone", null);
            return (FarmCover.Other, obj.isForage() ? $"{obj.DisplayName} (forage: harvest_all)" : obj.DisplayName, null);
        }
        foreach (var rc in loc.resourceClumps)
            if (rc.occupiesTile(x, y)) return (FarmCover.Large, WorldText.ClumpName(rc.parentSheetIndex.Value), rc);
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
        {
            switch (tf)
            {
                case Tree tree:
                    if (tree.stump.Value) return (FarmCover.Stump, "tree stump", null);
                    return tree.growthStage.Value >= 5 ? (FarmCover.Tree, "tree", null) : (FarmCover.Sapling, "young tree", null);
                case FruitTree:
                    return (FarmCover.FruitTree, "fruit tree", null);
                case HoeDirt:
                    return (FarmCover.Tilled, "tilled soil", null);
                case Grass:
                    return (FarmCover.Grass, "grass", null);
                default:
                    return (FarmCover.Other, tf.GetType().Name, null);
            }
        }
        foreach (var ltf in loc.largeTerrainFeatures)
            if (ltf.getBoundingBox().Contains(x * 64 + 32, y * 64 + 32)) return (FarmCover.Other, ltf is Bush ? "bush" : ltf.GetType().Name, null);
        return (FarmCover.None, "", null);
    }

    /// <summary>Why bare ground can't be hoed, or null when it can.</summary>
    private static string? FarmNotTillable(GameLocation loc, int x, int y)
    {
        if (loc.doesTileHaveProperty(x, y, "Diggable", "Back") == null) return "not diggable ground";
        if (!Pathfinder.IsWalkable(loc, x, y)) return "blocked (building, furniture, water, a person or an animal)";
        return null;
    }

    /// <summary>x,y,w,h of clear_area / till_area; null when fine, else the error to show.</summary>
    private static string? FarmRect(JsonElement a, out int x, out int y, out int w, out int h)
    {
        x = I(a, "x", 0); y = I(a, "y", 0); w = I(a, "w", 0); h = I(a, "h", 0);
        if (!Has(a, "x") || !Has(a, "y") || !Has(a, "w") || !Has(a, "h")) return "x, y, w and h are required (x,y is the top-left tile).";
        if (w < 1 || h < 1) return "w and h must be at least 1.";
        if (w * h > FarmAreaMax) return $"The area is {w * h} tiles; the maximum is {FarmAreaMax} per call (for example 10x10). Split it up.";
        return null;
    }

    private static List<Point> FarmAreaTiles(GameLocation loc, int x, int y, int w, int h)
    {
        var tiles = new List<Point>();
        for (int ty = y; ty < y + h; ty++)
            for (int tx = x; tx < x + w; tx++)
                if (loc.isTileOnMap(tx, ty)) tiles.Add(new Point(tx, ty));
        return tiles;
    }

    private sealed class FarmReport
    {
        public readonly Dictionary<string, int> Cleared = new();
        public readonly Dictionary<string, int> LeftAlone = new();
        public readonly List<string> Large = new();
        public readonly List<string> Problems = new();
        public readonly HashSet<string> MissingTools = new();
        public readonly Dictionary<string, List<Point>> Untillable = new();
        public int Tilled, AlreadyTilled, Swings;
        public string? Stop;

        public int ClearedCount => Cleared.Values.Sum();

        public void CannotTill(string reason, Point tile)
        {
            if (!Untillable.TryGetValue(reason, out var list)) Untillable[reason] = list = new List<Point>();
            list.Add(tile);
        }
    }

    /// <summary>Note what clear_area will leave alone: big obstacles, grown trees and objects.</summary>
    private static void FarmNoteLeftovers(GameLocation loc, List<Point> tiles, FarmReport rep)
    {
        var seen = new HashSet<object>();
        foreach (var t in tiles)
        {
            var (cover, label, key) = FarmCoverAt(loc, t.X, t.Y);
            if (cover == FarmCover.Large && key != null)
            {
                if (seen.Add(key)) rep.Large.Add($"{label} at ({t.X},{t.Y})");
            }
            else if (cover is FarmCover.Tree or FarmCover.FruitTree or FarmCover.Other)
                FarmCount(rep.LeftAlone, label);
        }
    }

    /// <summary>
    /// Clear every clearable tile: Scythe for weeds and grass, Pickaxe for stones, Axe for twigs, stumps and young trees.
    /// Tiles that could not be reached get one more try after the rest, since clearing opens paths.
    /// </summary>
    private static IEnumerator FarmClear(GameLocation loc, List<Point> tiles, FarmReport rep)
    {
        int scythe = FindItem("Scythe", it => it is Tool);
        int axe = FindItem("Axe", it => it is Axe);
        int pick = FindItem("Pickaxe", it => it is Pickaxe);

        var pending = tiles.Where(t => FarmClearable(FarmCoverAt(loc, t.X, t.Y).cover)).ToList();
        for (int pass = 0; pass < 2 && pending.Count > 0; pass++)
        {
            var retry = new List<Point>();
            int clearedAtStart = rep.ClearedCount;
            foreach (var t in RouteOrder(pending, P.TilePoint))
            {
                rep.Stop = FarmStopReason(loc);
                if (rep.Stop != null) yield break;

                var (cover, label, _) = FarmCoverAt(loc, t.X, t.Y);
                if (!FarmClearable(cover)) continue; // an earlier swing took it (scythes cut a whole arc)

                int slot;
                string need;
                switch (cover)
                {
                    case FarmCover.Weeds:
                    case FarmCover.Grass:
                        slot = scythe; need = "Scythe"; break;
                    case FarmCover.Stone:
                        slot = pick; need = "Pickaxe"; break;
                    default: // twig, tree stump, young tree
                        slot = axe; need = "Axe"; break;
                }
                if (slot < 0) { rep.MissingTools.Add(need); continue; }

                var walk = new CallContext();
                yield return WalkAdjacent(t, walk);
                if (!walk.Ok)
                {
                    if (pass == 0) retry.Add(t);
                    else rep.Problems.Add($"({t.X},{t.Y}) {label} skipped: {walk.Result}");
                    continue;
                }

                P.CurrentToolIndex = slot;
                int hits = 0;
                while (hits < 20 && FarmClearable(FarmCoverAt(loc, t.X, t.Y).cover))
                {
                    rep.Stop = rep.Swings >= FarmMaxSwings
                        ? $"swing limit of {FarmMaxSwings} per call; call again to continue"
                        : FarmStopReason(loc);
                    if (rep.Stop != null) break;
                    yield return Swing(t);
                    hits++;
                    rep.Swings++;
                }
                if (rep.Stop != null) yield break; // stopped before this tile was done; the reason is reported
                if (FarmClearable(FarmCoverAt(loc, t.X, t.Y).cover))
                    rep.Problems.Add($"({t.X},{t.Y}) {label} NOT cleared after {hits} hits");
                else
                    FarmCount(rep.Cleared, label);
            }
            if (retry.Count == 0 || rep.ClearedCount == clearedAtStart)
            {
                foreach (var t in retry) rep.Problems.Add($"({t.X},{t.Y}) skipped: no reachable tile next to it");
                break;
            }
            pending = retry;
        }
    }

    /// <summary>Hoe every bare tile that can be tilled and note why the others can't.</summary>
    private static IEnumerator FarmTill(GameLocation loc, List<Point> tiles, FarmReport rep)
    {
        int hoe = FindItem("Hoe", it => it is Hoe);
        if (hoe < 0) rep.MissingTools.Add("Hoe");

        foreach (var t in RouteOrder(tiles, P.TilePoint))
        {
            var (cover, label, _) = FarmCoverAt(loc, t.X, t.Y);
            if (cover == FarmCover.Tilled) { rep.AlreadyTilled++; continue; }
            if (cover != FarmCover.None) { rep.CannotTill(label, t); continue; }
            var why = FarmNotTillable(loc, t.X, t.Y);
            if (why != null) { rep.CannotTill(why, t); continue; }
            if (hoe < 0) { rep.CannotTill("no Hoe in inventory", t); continue; }

            rep.Stop = rep.Swings >= FarmMaxSwings
                ? $"swing limit of {FarmMaxSwings} per call; call again to continue"
                : FarmStopReason(loc);
            if (rep.Stop != null) yield break;

            var walk = new CallContext();
            yield return WalkAdjacent(t, walk);
            if (!walk.Ok) { rep.CannotTill("unreachable", t); continue; }
            P.CurrentToolIndex = hoe;
            yield return Swing(t);
            rep.Swings++;
            if (FarmCoverAt(loc, t.X, t.Y).cover == FarmCover.Tilled) rep.Tilled++;
            else rep.CannotTill("the Hoe had no effect", t);
        }
    }

    private static void FarmReportClearing(StringBuilder sb, FarmReport rep)
    {
        if (rep.Large.Count > 0) sb.Append("\nSkipped (need an upgraded tool): ").Append(string.Join("; ", rep.Large));
        if (rep.LeftAlone.Count > 0) sb.Append("\nLeft alone: ").Append(FarmCounts(rep.LeftAlone));
        if (rep.MissingTools.Count > 0) sb.Append("\nMissing tools: ").Append(string.Join(", ", rep.MissingTools));
        foreach (var p in rep.Problems.Take(6)) sb.Append('\n').Append(p);
        if (rep.Problems.Count > 6) sb.Append($"\n(+{rep.Problems.Count - 6} more)");
        if (rep.Stop != null) sb.Append($"\nStopped: {rep.Stop}.");
    }

    private static IEnumerator ClearArea(JsonElement a, CallContext c)
    {
        var err = FarmRect(a, out int x, out int y, out int w, out int h);
        if (err != null) { c.Fail(err); yield break; }
        var loc = Loc;
        var tiles = FarmAreaTiles(loc, x, y, w, h);
        if (tiles.Count == 0) { c.Fail($"The area ({x},{y}) {w}x{h} is outside {loc.Name}."); yield break; }

        var before = Snapshot();
        var rep = new FarmReport();
        FarmNoteLeftovers(loc, tiles, rep);
        yield return FarmClear(loc, tiles, rep);
        yield return Ticks(45); // let dropped items fly into the inventory

        var sb = new StringBuilder();
        sb.Append(rep.ClearedCount == 0
            ? $"Nothing was cleared in ({x},{y}) {w}x{h} on {loc.Name}."
            : $"Cleared {rep.ClearedCount} tile{(rep.ClearedCount == 1 ? "" : "s")} in ({x},{y}) {w}x{h}: {FarmCounts(rep.Cleared)}.");
        var diff = Diff(before);
        if (diff.Length > 0) sb.Append(' ').Append(diff);
        FarmReportClearing(sb, rep);
        c.Result = sb.ToString();
    }

    private static IEnumerator TillArea(JsonElement a, CallContext c)
    {
        var err = FarmRect(a, out int x, out int y, out int w, out int h);
        if (err != null) { c.Fail(err); yield break; }
        var loc = Loc;
        var tiles = FarmAreaTiles(loc, x, y, w, h);
        if (tiles.Count == 0) { c.Fail($"The area ({x},{y}) {w}x{h} is outside {loc.Name}."); yield break; }

        var before = Snapshot();
        var rep = new FarmReport();
        yield return FarmClear(loc, tiles, rep);
        if (rep.Stop == null) yield return FarmTill(loc, tiles, rep);
        yield return Ticks(45);

        int blocked = rep.Untillable.Values.Sum(l => l.Count);
        var sb = new StringBuilder();
        sb.Append($"Tilled {rep.Tilled + rep.AlreadyTilled} of {tiles.Count} tiles in ({x},{y}) {w}x{h} on {loc.Name} ({rep.Tilled} new, {rep.AlreadyTilled} already tilled).");
        if (blocked > 0) sb.Append($" {blocked} could not be tilled.");
        if (rep.ClearedCount > 0) sb.Append($"\nCleared first: {FarmCounts(rep.Cleared)}.");
        var diff = Diff(before);
        if (diff.Length > 0) sb.Append(' ').Append(diff);
        foreach (var kv in rep.Untillable) sb.Append($"\nCould not till, {kv.Key}: {FarmTilesText(kv.Value)}");
        FarmReportClearing(sb, rep);
        c.Result = sb.ToString();
    }

    // ------------------------------------------------------------------ plant: area mode

    /// <summary>
    /// The plant tool. With tiles it is the original <see cref="Plant"/>. Without tiles it plants on the empty tilled soil of an
    /// area [x,y,w,h], or of the whole map, nearest tile first, until the seeds run out, through the same <see cref="Plant"/> ten tiles at a time.
    /// </summary>
    private static IEnumerator PlantTool(JsonElement a, CallContext c)
    {
        if (TileList(a, "tiles").Count > 0) { yield return Plant(a, c); yield break; }

        var name = S(a, "item");
        int slot = FindItem(name, it => it is SObject);
        if (slot < 0) { c.Fail($"No \"{name}\" in inventory."); yield break; }
        var loc = Loc;

        string where = loc.Name;
        Func<GameLocation, int, int, bool> inArea = (_, _, _) => true;
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("area", out var area) && area.ValueKind == JsonValueKind.Array)
        {
            if (area.GetArrayLength() < 4) { c.Fail("area must be [x, y, w, h]."); yield break; }
            int ax = area[0].GetInt32(), ay = area[1].GetInt32(), aw = area[2].GetInt32(), ah = area[3].GetInt32();
            if (aw < 1 || ah < 1) { c.Fail("area width and height must be at least 1."); yield break; }
            inArea = (_, tx, ty) => tx >= ax && tx < ax + aw && ty >= ay && ty < ay + ah;
            where = $"({ax},{ay}) {aw}x{ah} on {loc.Name}";
        }

        var spots = FarmTilesWhere(loc, (l, tx, ty) => inArea(l, tx, ty) && FarmDirt(l, tx, ty) is { crop: null });
        if (spots.Count == 0) { c.Result = $"No empty tilled soil in {where}. Use till_area to prepare a field first."; yield break; }

        var route = RouteOrder(spots, P.TilePoint);
        int startStack = P.Items[slot].Stack;
        var failures = new List<string>();
        string? stop = null;
        int tried = 0;

        for (int i = 0; i < route.Count; i += FarmChunk)
        {
            if (P.Items[slot] == null || P.Items[slot].Stack <= 0) { stop = "ran out of seeds"; break; }
            if (tried >= FarmMaxTiles) { stop = $"batch limit of {FarmMaxTiles} tiles per call; call plant again"; break; }
            stop = FarmStopReason(loc, needsEnergy: false);
            if (stop != null) break;

            var chunk = route.GetRange(i, Math.Min(FarmChunk, route.Count - i));
            tried += chunk.Count;
            int stackBefore = P.Items[slot]?.Stack ?? 0;
            var sub = new CallContext();
            yield return Plant(FarmArgs(new { item = name, tiles = chunk.Select(t => new[] { t.X, t.Y }).ToList() }), sub);
            failures.AddRange(sub.Result.Split('\n').Where(l => l.Contains("could not plant")));
            if ((P.Items[slot]?.Stack ?? 0) == stackBefore)
            {
                stop = $"nothing could be planted in the last {chunk.Count} tiles (wrong season, not plantable on this soil, or unreachable)";
                break;
            }
        }

        int used = startStack - (P.Items[slot]?.Stack ?? 0);
        var sb = new StringBuilder($"Planted {used} {name} on {used} of {route.Count} empty tilled tiles in {where}.");
        if (P.Items[slot] is { } left) sb.Append($" {left.Stack} left.");
        if (stop != null) sb.Append($"\nStopped: {stop}.");
        foreach (var f in failures.Take(5)) sb.Append('\n').Append(f);
        if (failures.Count > 5) sb.Append($"\n(+{failures.Count - 5} more)");
        c.Result = sb.ToString();
    }
}
