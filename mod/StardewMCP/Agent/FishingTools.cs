using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;

namespace StardewMCP.Agent;

/// <summary>
/// The fish tool (issue #4): pick a shore spot, cast, wait for the bite, play the BobberBar minigame and
/// collect the catch and any treasure chest. The pure maths lives in <see cref="BobberMath"/> and
/// <see cref="CastMath"/>; the per-tick minigame driver is <see cref="BobberDriver"/> (called from
/// <see cref="FishingTick"/> on the seat's own screen, so it also works while the world is frozen).
/// </summary>
public static partial class AgentTools
{
    /// <summary>No new cast after 00:30: a cast, the wait for a bite and the reel-in can take 20+ game minutes, and bedtime is 01:00.</summary>
    private const int FishCutoffTime = 2430;

    private const int FishMinEnergy = 10;
    private const int FishRadius = 25;

    /// <summary>Ticks to wait for a bite before reeling in (60 ticks = 1 second = 1.4 game minutes).</summary>
    private const int FishBiteWaitTicks = 900;
    private const int FishCastTicks = FishBiteWaitTicks + 300;
    private const int FishMinigameTicks = 60 * 120;
    private const int FishCollectTicks = 600;

    /// <summary>Tiles the last cast landed off the aim, per seat and axis, fed back into the next aim.</summary>
    private static readonly Dictionary<int, int> FishBias = new();

    private static GameLocation? _splashLoc;
    private static long _splashAt;
    private static Point? _splashTile;

    private sealed class FishSpot
    {
        public Point Stand;
        public int Dir;
        /// <summary>Water tiles in a row in front of the stand tile.</summary>
        public int Run;
        /// <summary>Tiles out to land the bobber.</summary>
        public int Tiles;
        public bool Bubbles;
        public int Score;
    }

    private sealed class FishCastReport
    {
        public string Text = "";
        public bool Caught;
        public bool Land;
        public bool Fatal;
    }

    private static ToolDef FishTool() => new()
    {
        Name = "fish",
        Description = "Fish with the fishing rod in your inventory. You walk to the best shore spot within about 25 tiles (a long stretch of water; fish bubbles * first), " +
                      "cast, wait for a bite, win the reel-in minigame and collect the fish and any treasure chest. " +
                      "Each cast costs energy and game time. Stops early when energy is low, the inventory is full, or it is after 00:30.",
        Schema = Obj(new { tries = Int("How many casts to attempt, 1-5 (default 3)"), reason = Str(ReasonDesc) }, "reason"),
        Run = Fish
    };

    // ------------------------------------------------------------------ small helpers

    /// <summary>Inventory slot of the first fishing rod, or -1.</summary>
    internal static int RodSlot()
    {
        var items = P.Items;
        for (int i = 0; i < items.Count; i++)
            if (items[i] is FishingRod)
                return i;
        return -1;
    }

    private static int FreeSlots() => P.Items.Count(i => i == null);

    internal static bool FishingNatural(ModConfig cfg) =>
        string.Equals(cfg.FishingAssist?.Trim(), "natural", StringComparison.OrdinalIgnoreCase);

    /// <summary>Called every tick of every seat's screen; drives the BobberBar only while that seat runs the fish tool.</summary>
    public static void FishingTick(AgentRuntime rt)
    {
        if (Game1.activeClickableMenu is BobberBar bar && rt.CurrentTool == "fish")
            BobberDriver.Drive(bar, rt);
    }

    /// <summary>Extra tiles of cast distance: the game's own number when it can be called, else by Fishing level.</summary>
    private static int AddedDistance(FishingRod rod)
    {
        try
        {
            var method = Rt.Helper.Reflection.GetMethod(rod, "getAddedDistance", required: false);
            if (method != null)
                return Math.Clamp(method.Invoke<int>(P), 0, 8);
        }
        catch (Exception)
        {
            // fall through to the level table
        }
        return CastMath.AddedDistanceForLevel(P.fishingLevel.Value);
    }

    private static void SetCastPower(FishingRod rod, float power)
    {
        rod.castingPower = power;
        FishReflect.Set(rod, "castingTimerSpeed", 0f); // keep the power meter from drifting
    }

    /// <summary>The fish-bubble tile of a location, or null (read by name; cached for half a second because look asks per tile).</summary>
    internal static Point? SplashTile(GameLocation loc)
    {
        long now = Environment.TickCount64;
        if (ReferenceEquals(loc, _splashLoc) && now - _splashAt < 500)
            return _splashTile;
        _splashLoc = loc;
        _splashAt = now;
        _splashTile = null;
        if (FishReflect.Get(loc, "fishSplashPoint") is Point p && p.X >= 1 && p.Y >= 1)
            _splashTile = p;
        return _splashTile;
    }

    internal static bool IsFishSplash(GameLocation loc, int x, int y)
    {
        var s = SplashTile(loc);
        return s.HasValue && s.Value.X == x && s.Value.Y == y;
    }

    /// <summary>Bait and tackle on a rod, for status and inventory (empty for other items).</summary>
    internal static string RodNote(Item it)
    {
        if (it is not FishingRod rod)
            return "";
        var parts = new List<string>();
        if (FishReflect.Get(rod, "attachments") is IEnumerable slots)
            foreach (var o in slots)
                if (o is Item a)
                    parts.Add(a.Stack > 1 ? $"{a.DisplayName} x{a.Stack}" : a.DisplayName);
        return parts.Count > 0 ? ", attached: " + string.Join(" + ", parts) : ", no bait or tackle attached";
    }

    /// <summary>One status line about fishing, empty when the player has no rod.</summary>
    internal static string FishingLine()
    {
        int slot = RodSlot();
        if (slot < 0)
            return "";
        var rod = P.Items[slot];
        return $"Fishing: level {P.fishingLevel.Value} · rod {rod.DisplayName}{RodNote(rod)} · use the fish tool near water";
    }

    // ------------------------------------------------------------------ planning

    /// <summary>
    /// Best shore tile and direction within <see cref="FishRadius"/> tiles: the longest straight run of fishable water
    /// (so the cast cannot overshoot onto the far bank), fish bubbles first, then the shortest walk.
    /// </summary>
    private static (FishSpot? spot, string why) PlanFishingSpot(GameLocation loc, int added)
    {
        var me = P.TilePoint;
        Func<int, int, bool> fishable = (fx, fy) => loc.isTileOnMap(fx, fy) && loc.isTileFishable(fx, fy);
        var splash = SplashTile(loc);
        var found = new List<FishSpot>();

        for (int dx = -FishRadius; dx <= FishRadius; dx++)
        {
            for (int dy = -FishRadius; dy <= FishRadius; dy++)
            {
                int x = me.X + dx, y = me.Y + dy;
                if (!loc.isTileOnMap(x, y) || fishable(x, y))
                    continue;
                for (int d = 0; d < 4; d++)
                {
                    if (!fishable(x + CastMath.Dx[d], y + CastMath.Dy[d]))
                        continue;
                    int reach = CastMath.ReachTiles(d, added);
                    int run = CastMath.RunLength(x, y, d, reach + 2, fishable);
                    int tiles = CastMath.AimTiles(run, reach);
                    if (tiles == 0)
                        continue;

                    bool bubbles = false;
                    if (splash is Point sp)
                    {
                        bool onLine = d == 0 || d == 2 ? sp.X == x : sp.Y == y;
                        int k = d == 0 ? y - sp.Y : d == 1 ? sp.X - x : d == 2 ? sp.Y - y : x - sp.X;
                        if (onLine && k >= 2 && k <= Math.Min(run, reach))
                        {
                            tiles = k;
                            bubbles = true;
                        }
                    }
                    found.Add(new FishSpot
                    {
                        Stand = new Point(x, y),
                        Dir = d,
                        Run = run,
                        Tiles = tiles,
                        Bubbles = bubbles,
                        Score = 10 * Math.Min(tiles, 6) + (bubbles ? 60 : 0) - (Math.Abs(dx) + Math.Abs(dy))
                    });
                }
            }
        }

        if (found.Count == 0)
            return (null, $"[no_water] No fishable water within {FishRadius} tiles (needs at least 2 water tiles in a row from a shore you can stand on). " +
                          "Use look to find water (~), or travel_to a map with water (Beach, Town river, Mountain lake, the Farm pond).");

        found.Sort((p, q) => q.Score.CompareTo(p.Score));
        int tried = 0;
        foreach (var s in found)
        {
            if (!Pathfinder.IsWalkable(loc, s.Stand.X, s.Stand.Y))
                continue;
            if (s.Stand == me)
                return (s, "");
            if (++tried > 3)
                break;
            if (new Pathfinder().FindPath(loc, me.ToVector2(), s.Stand.ToVector2()) != null)
                return (s, "");
        }
        return (null, "[unreachable_water] There is water nearby, but no route to a shore tile you can stand on. Try walk_to a different side of it, or another map.");
    }

    // ------------------------------------------------------------------ the tool

    private static IEnumerator Fish(JsonElement a, CallContext c)
    {
        int tries = Math.Clamp(I(a, "tries", 3), 1, 5);
        int slot = RodSlot();
        if (slot < 0)
        {
            c.Fail("[no_rod] You have no fishing rod. Willy at the Beach (fish shop, 9:00-17:00) gives or sells one; check inventory.");
            yield break;
        }
        if (Game1.activeClickableMenu != null)
        {
            c.Fail("[menu_open] A menu or dialogue is open. Deal with it first (respond / close_menu).");
            yield break;
        }
        if (Game1.timeOfDay >= FishCutoffTime)
        {
            c.Fail($"[too_late] It is {WorldText.FormatTime(Game1.timeOfDay)}: too late to start fishing (no new casts after {WorldText.FormatTime(FishCutoffTime)}). Go to bed.");
            yield break;
        }
        if (P.Stamina <= FishMinEnergy)
        {
            c.Fail($"[low_energy] Energy is {(int)P.Stamina}. Eat something or go to bed before fishing.");
            yield break;
        }
        if (FreeSlots() == 0)
        {
            c.Fail("[inventory_full] No free inventory slot for a catch. Ship or drop something first.");
            yield break;
        }

        yield return WaitIdle();
        if (Game1.activeClickableMenu != null)
        {
            c.Fail("[menu_open] A menu or dialogue opened. Deal with it first (respond / close_menu).");
            yield break;
        }

        var loc = Loc;
        string assist = "perfect";
        if (FishingNatural(Config))
        {
            if (FishingPatches.EnsureApplied() && !FishingPatches.Disabled)
                assist = "natural";
            else
                Rt.Log.Add("system", text: $"fish: FishingAssist=natural is not available ({FishingPatches.Error ?? "disabled after a failed check"}); using perfect");
        }

        int added = AddedDistance((FishingRod)P.Items[slot]);
        var (spot, why) = PlanFishingSpot(loc, added);
        if (spot == null)
        {
            c.Fail(why);
            yield break;
        }
        Rt.Log.Add("system", text: $"fish: assist={assist}, spot ({spot.Stand.X},{spot.Stand.Y}) facing {WorldText.DirName(spot.Dir)}, water run {spot.Run}, aim {spot.Tiles}{(spot.Bubbles ? ", bubbles" : "")}, added distance {added}");

        var walk = new CallContext();
        yield return WalkTo(spot.Stand, walk);
        if (!walk.Ok)
        {
            c.Fail("[unreachable_water] " + walk.Result);
            yield break;
        }
        if (Game1.currentLocation != loc)
        {
            c.Fail("[left_map] You ended up on another map while walking to the water.");
            yield break;
        }

        var lines = new List<string>();
        var total = Snapshot();
        string stop = "";
        int casts = 0, caught = 0, landOnLand = 0;
        for (int n = 1; n <= tries; n++)
        {
            // Level-up and other menus after a catch close on their own (AutoHandleOverlays); wait for them.
            for (int t = 0; t < 600 && Game1.activeClickableMenu != null; t++)
                yield return null;
            if (Game1.activeClickableMenu != null) { stop = "a menu is still open"; break; }
            if (Game1.currentLocation != loc) { stop = "you left the map"; break; }
            if (Game1.timeOfDay >= FishCutoffTime) { stop = $"it is {WorldText.FormatTime(Game1.timeOfDay)}, too late for another cast"; break; }
            if (P.Stamina <= FishMinEnergy) { stop = $"energy is low ({(int)P.Stamina})"; break; }
            if (FreeSlots() == 0) { stop = "the inventory is full"; break; }

            yield return WaitIdle(300);
            if (P.UsingTool)
                P.completelyStopAnimatingOrDoingAction();

            var report = new FishCastReport();
            yield return FishCast(n, tries, slot, spot, added, report);
            casts++;
            lines.Add(report.Text);
            if (report.Caught) caught++;
            landOnLand = report.Land ? landOnLand + 1 : 0;
            if (report.Fatal) { stop = "the cast could not be played out"; break; }
            if (landOnLand >= 2) { stop = "two casts in a row landed on land; look for another spot"; break; }
        }

        var sb = new StringBuilder();
        sb.Append($"Fished from ({spot.Stand.X},{spot.Stand.Y}) facing {WorldText.DirName(spot.Dir)} over {spot.Run} water tiles{(spot.Bubbles ? " with fish bubbles" : "")}: {casts} cast(s), {caught} fish.");
        foreach (var l in lines)
            sb.Append('\n').Append(l);
        if (stop.Length > 0)
            sb.Append("\nStopped: ").Append(stop).Append('.');
        var diff = Diff(total);
        sb.Append('\n').Append(diff.Length > 0 ? diff : "Nothing was added to your inventory.");
        c.Result = sb.ToString();
    }

    /// <summary>One cast: swing, wait for the bite, hook, ride out the minigame (driven by <see cref="FishingTick"/>), collect.</summary>
    private static IEnumerator FishCast(int n, int tries, int slot, FishSpot spot, int added, FishCastReport r)
    {
        var loc = Loc;
        var before = Snapshot();
        float energy0 = P.Stamina;
        int level0 = P.fishingLevel.Value;

        P.CurrentToolIndex = slot;
        var rod = (FishingRod)P.Items[slot];
        var faceTile = new Point(spot.Stand.X + CastMath.Dx[spot.Dir], spot.Stand.Y + CastMath.Dy[spot.Dir]);
        Face(faceTile);
        P.lastClick = new Vector2(faceTile.X * 64 + 32, faceTile.Y * 64 + 32);

        int biasKey = Context.ScreenId * 2 + spot.Dir % 2;
        int bias = FishBias.GetValueOrDefault(biasKey);
        int aim = Math.Clamp(spot.Tiles - bias, 2, Math.Max(2, spot.Run));
        float power = CastMath.PowerFor(aim, CastMath.ReachTiles(spot.Dir, added));

        // Swing, then hold the chosen power until the cast is in the air (the game reads it ~350 ms after the swing).
        P.BeginUsingTool();
        yield return null;
        yield return null;
        bool inFlight = false;
        for (int t = 0; t < 45; t++)
        {
            SetCastPower(rod, power);
            if (rod.castedButBobberStillInAir || rod.isFishing || Game1.activeClickableMenu != null)
            {
                inFlight = true;
                break;
            }
            yield return null;
        }
        if (!inFlight && !P.UsingTool)
        {
            r.Text = $"Cast {n}/{tries}: the rod did not swing (the farmer was busy). Try again.";
            r.Fatal = true;
            yield break;
        }

        // Wait for the bobber to land, the bite, and the hook.
        bool sawAir = false, landedKnown = false, hooked = false, reeledIn = false;
        Point? landed = null;
        bool landedWater = true;
        BobberBar? bar = null;
        for (int waited = 0; waited < FishCastTicks; waited++)
        {
            if (Game1.activeClickableMenu is BobberBar b) { bar = b; break; }
            if (Game1.activeClickableMenu != null || rod.fishCaught) break;

            if (rod.castedButBobberStillInAir)
                sawAir = true;
            if (!landedKnown && ((sawAir && !rod.castedButBobberStillInAir) || rod.isFishing))
            {
                landedKnown = true;
                landed = new Point((int)(rod.bobber.X / 64f), (int)(rod.bobber.Y / 64f));
                landedWater = loc.isTileFishable(landed.Value.X, landed.Value.Y);
            }
            if (rod.isNibbling && !hooked)
            {
                hooked = true;
                rod.DoFunction(loc, (int)rod.bobber.X, (int)rod.bobber.Y, 1, P);
            }
            else if (!hooked && !reeledIn && waited >= FishBiteWaitTicks && rod.isFishing)
            {
                reeledIn = true; // no bite: using the rod again pulls the line in
                rod.DoFunction(loc, (int)rod.bobber.X, (int)rod.bobber.Y, 1, P);
            }
            if (landedKnown && !landedWater && waited > 30 && !rod.isFishing)
                break;
            if (waited > 60 && !P.UsingTool && !rod.isFishing && !rod.castedButBobberStillInAir && !rod.isCasting)
                break;
            yield return null;
        }

        // The minigame: FishingTick presses the bar for us every tick; here we just wait for it to close.
        if (bar != null)
        {
            for (int guard = 0; Game1.activeClickableMenu == bar && guard < FishMinigameTicks; guard++)
                yield return null;
        }

        // Pick up the result: the fish in hand (and its treasure chest), a trash catch, or nothing.
        bool fishCaught = false, junk = false, doneCalled = false, droppedTreasure = false, sawLevelMenu = false;
        int quality = 0, size = 0, settle = 0;
        var treasure = new List<string>();
        for (int t = 0; t < FishCollectTicks; t++)
        {
            if (Game1.activeClickableMenu is LevelUpMenu)
                sawLevelMenu = true;
            if (Game1.activeClickableMenu is ItemGrabMenu grab)
            {
                droppedTreasure |= TakeTreasure(grab, treasure);
                settle = 0;
                yield return null;
                continue;
            }
            if (rod.fishCaught && !doneCalled)
            {
                fishCaught = true;
                doneCalled = true;
                quality = FishReflect.GetInt(rod, "fishQuality");
                size = FishReflect.GetInt(rod, "fishSize");
                junk = FishReflect.GetBool(rod, "lastCatchWasJunk");
                rod.doneHoldingFish(P, false);
                for (int k = 0; k < 25; k++) // let the treasure menu or the pickup show up
                    yield return null;
                settle = 0;
                continue;
            }
            bool busy = Game1.activeClickableMenu != null || P.UsingTool;
            settle = busy ? 0 : settle + 1;
            if (settle >= 20)
                break;
            yield return null;
        }

        // Make sure the farmer is free again before the next cast.
        for (int t = 0; t < 120 && P.UsingTool && Game1.activeClickableMenu == null; t++)
            yield return null;
        bool reset = false;
        if (P.UsingTool && Game1.activeClickableMenu == null)
        {
            P.completelyStopAnimatingOrDoingAction();
            reset = true;
        }

        // ---- write the report
        var run = bar != null ? BobberDriver.Run(bar) : null;
        int along = 0;
        if (landed is Point lp)
        {
            along = spot.Dir switch
            {
                0 => spot.Stand.Y - lp.Y,
                1 => lp.X - spot.Stand.X,
                2 => lp.Y - spot.Stand.Y,
                _ => spot.Stand.X - lp.X
            };
            // Feed the miss back into the next aim (a land landing counts as at least one tile too far).
            if (aim >= 3)
                FishBias[biasKey] = Math.Clamp(bias + (landedWater ? along - aim : Math.Max(along - aim, 1)), -2, 2);
        }

        var diff = Diff(before);
        var parts = new List<string>();
        string stars = quality switch { 1 => "silver ★", 2 => "gold ★★", 4 => "iridium ★★★", _ => "" };
        if (fishCaught && junk)
            parts.Add("pulled up JUNK");
        else if (fishCaught)
        {
            var detail = new List<string>();
            if (stars.Length > 0) detail.Add(stars);
            if (size > 0) detail.Add($"{size} in");
            parts.Add("CAUGHT a fish" + (detail.Count > 0 ? " (" + string.Join(", ", detail) + ")" : ""));
        }
        else if (bar != null)
            parts.Add($"the fish ESCAPED (catch bar reached {(run?.LastProgress ?? 0f).ToString("0.00", CultureInfo.InvariantCulture)}, needs over 0.9)");
        else if (hooked)
            parts.Add("hooked something but it got away before the minigame");
        else if (landedKnown && !landedWater)
            parts.Add("the bobber LANDED ON LAND: the cast was wasted");
        else if (reeledIn)
            parts.Add($"no bite in {FishBiteWaitTicks / 60} seconds: reeled the line in");
        else
            parts.Add("nothing happened");

        if (run != null && run.Ticks > 0)
            parts.Add(run.OutTicks == 0 ? "perfect (the fish never left the bar)" : $"fish left the bar for {run.OutTicks} of {run.Ticks} ticks");
        if (run != null && run.SawTreasure)
            parts.Add(treasure.Count > 0 ? "treasure chest: " + string.Join(", ", treasure) + (droppedTreasure ? " (some fell at your feet: inventory full)" : "")
                                         : "a treasure chest appeared but was missed");
        else if (treasure.Count > 0)
            parts.Add("treasure: " + string.Join(", ", treasure) + (droppedTreasure ? " (some fell at your feet)" : ""));
        if (diff.Length > 0)
            parts.Add(diff);
        parts.Add($"energy -{Math.Max(0, (int)Math.Round(energy0 - P.Stamina))}");
        int level1 = P.fishingLevel.Value;
        if (level1 > level0)
            parts.Add($"Fishing level {level0} -> {level1}!");
        else if (sawLevelMenu)
            parts.Add("a level-up screen appeared");
        if (reset)
            parts.Add("the farmer was stuck and had to be reset");

        string aimText = landed is Point l2
            ? $"aimed {aim} tiles out (power {power.ToString("0.00", CultureInfo.InvariantCulture)}), bobber landed {along} out at ({l2.X},{l2.Y}){(landedWater ? "" : " on land")}"
            : $"aimed {aim} tiles out (power {power.ToString("0.00", CultureInfo.InvariantCulture)})";
        r.Text = $"Cast {n}/{tries}: {aimText}: " + string.Join(" · ", parts) + ".";
        r.Caught = fishCaught && !junk;
        r.Land = landedKnown && !landedWater;
        Rt.Log.Add("system", text: $"fish: cast {n} {(run != null ? run.Mode : "no minigame")}, bar ticks {run?.Ticks ?? 0}, pressed {run?.PressTicks ?? 0}");
    }

    /// <summary>Move everything from the treasure chest into the inventory; what does not fit falls at the farmer's feet. True when something fell.</summary>
    private static bool TakeTreasure(ItemGrabMenu grab, List<string> lines)
    {
        bool dropped = false;
        var items = grab.ItemsToGrabMenu.actualInventory;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null)
                continue;
            lines.Add($"{item.Stack} x {item.DisplayName}");
            if (P.addItemToInventory(item) is Item rest)
            {
                Game1.createItemDebris(rest, P.getStandingPosition(), P.FacingDirection);
                dropped = true;
            }
        }
        grab.exitThisMenu();
        return dropped;
    }
}

/// <summary>
/// Reads and writes game fields by name through plain reflection, so a field that differs between game
/// versions costs a feature (a stat in the report, bubbles on the map) instead of breaking the build.
/// Netcode wrappers (NetInt, NetPoint...) are unwrapped to their value.
/// </summary>
internal static class FishReflect
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static object? Get(object? obj, string name)
    {
        if (obj == null)
            return null;
        try
        {
            for (var t = obj.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Any);
                if (f != null)
                    return Unwrap(f.GetValue(obj));
                var p = t.GetProperty(name, Any);
                if (p != null && p.GetIndexParameters().Length == 0)
                    return Unwrap(p.GetValue(obj));
            }
        }
        catch (Exception)
        {
            // treated as "not there"
        }
        return null;
    }

    public static int GetInt(object? obj, string name, int def = 0)
    {
        try
        {
            return Get(obj, name) is IConvertible c ? c.ToInt32(CultureInfo.InvariantCulture) : def;
        }
        catch (Exception)
        {
            return def;
        }
    }

    public static bool GetBool(object? obj, string name, bool def = false) => Get(obj, name) is bool b ? b : def;

    public static bool Set(object? obj, string name, object value)
    {
        if (obj == null)
            return false;
        try
        {
            for (var t = obj.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Any);
                if (f != null)
                {
                    f.SetValue(obj, Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture));
                    return true;
                }
                var p = t.GetProperty(name, Any);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(obj, Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture));
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // treated as "could not set"
        }
        return false;
    }

    private static object? Unwrap(object? o)
    {
        if (o == null || o.GetType().Namespace != "Netcode")
            return o;
        try
        {
            var value = o.GetType().GetProperty("Value");
            return value != null ? value.GetValue(o) : o;
        }
        catch (Exception)
        {
            return o;
        }
    }
}

/// <summary>What the driver learned about one BobberBar, kept next to the menu object itself.</summary>
internal sealed class BarRun
{
    public int Ticks;
    public int OutTicks;
    public int PressTicks;
    public bool Pressing;
    public bool UseMouse;
    public bool Chasing;
    public bool Trap;
    public bool Started;
    public bool Faulted;
    public int ProbePresses;
    public int ProbeEffective;
    public float PrevSpeed = float.NaN;
    public bool PrevPressing;
    public bool SawTreasure;
    public bool TreasureCaught;
    public float LastProgress;
    public string Mode = "";
}

/// <summary>
/// Plays the BobberBar once per tick. "perfect" writes the bar position straight onto the fish; "natural"
/// only decides whether the use-tool button is held for the next update (see <see cref="FishingPatches"/>),
/// so the bar keeps the game's own physics.
/// </summary>
internal static class BobberDriver
{
    private static readonly ConditionalWeakTable<BobberBar, BarRun> Runs = new();

    public static BarRun Run(BobberBar bar) => Runs.GetOrCreateValue(bar);

    /// <summary>The held-button decision for the next update of this bar, when natural mode is steering it.</summary>
    public static bool TryGetPress(BobberBar bar, out bool pressed)
    {
        pressed = false;
        if (!Runs.TryGetValue(bar, out var run) || !run.UseMouse)
            return false;
        pressed = run.Pressing;
        return true;
    }

    public static void Drive(BobberBar bar, AgentRuntime rt)
    {
        var run = Runs.GetOrCreateValue(bar);
        if (run.Faulted)
            return;
        try
        {
            Step(bar, rt, run);
        }
        catch (Exception ex)
        {
            run.Faulted = true;
            run.UseMouse = false;
            rt.Log.Add("system", text: $"fish: the bobber driver stopped: {ex.Message}");
        }
    }

    private static void Step(BobberBar bar, AgentRuntime rt, BarRun run)
    {
        if (bar.fadeOut || bar.handledFishResult)
        {
            run.UseMouse = false; // the result is being handled
            return;
        }

        float h = bar.bobberBarHeight;
        float fish = bar.bobberPosition;
        float progress = bar.distanceFromCatching;
        if (!run.Started)
        {
            run.Started = true;
            run.Trap = HasTrapBobber(bar);
        }
        run.Ticks++;
        if (run.Ticks > 1 && !bar.bobberInBar)
            run.OutTicks++;
        run.LastProgress = progress;
        if (bar.treasure)
            run.SawTreasure = true;
        if (bar.treasureCaught)
            run.TreasureCaught = true;

        run.Chasing = BobberMath.ChaseTreasure(bar.treasure, bar.treasureAppearTimer, bar.treasureCaught, progress, run.Chasing);

        bool natural = AgentTools.FishingNatural(rt.Config) && FishingPatches.Applied && !FishingPatches.Disabled;
        if (natural)
        {
            Probe(bar, rt, run, h);
            natural = !FishingPatches.Disabled;
        }

        if (natural)
        {
            float aim = run.Chasing ? bar.treasurePosition : BobberMath.PredictFish(fish, bar.bobberSpeed);
            run.Pressing = BobberMath.ShouldPress(bar.bobberBarPos, bar.bobberBarSpeed, h, aim, bar.bobberInBar, run.Trap, run.Pressing);
            run.UseMouse = true;
            run.Mode = "natural";
            if (run.Pressing)
                run.PressTicks++;
        }
        else
        {
            float aim = run.Chasing ? bar.treasurePosition : fish;
            float pos = BobberMath.CenteredBarPos(aim, h);
            bar.bobberBarPos = pos;
            bar.bobberBarSpeed = 0f;
            bar.bobberInBar = BobberMath.FishInBar(fish, pos, h);
            run.UseMouse = false;
            run.Mode = "perfect";
        }
        run.PrevSpeed = bar.bobberBarSpeed;
        run.PrevPressing = run.UseMouse && run.Pressing;
    }

    /// <summary>
    /// Self-check for natural mode: a held button must make the bar speed drop by its acceleration.
    /// If 15 presses in a row change nothing, the swapped mouse state is not what BobberBar.update reads:
    /// switch to perfect for good instead of losing every fish.
    /// </summary>
    private static void Probe(BobberBar bar, AgentRuntime rt, BarRun run, float h)
    {
        if (float.IsNaN(run.PrevSpeed) || !run.PrevPressing)
            return;
        float pos = bar.bobberBarPos;
        if (pos <= 0f || pos >= BobberMath.MaxBarPos(h))
            return; // wall bounces flip the speed
        run.ProbePresses++;
        if (bar.bobberBarSpeed < run.PrevSpeed - 0.01f)
            run.ProbeEffective++;
        if (run.ProbePresses >= 15 && run.ProbeEffective == 0)
        {
            FishingPatches.Disabled = true;
            run.UseMouse = false;
            rt.Log.Add("system", text: "fish: FishingAssist=natural does not move the bar (Game1.oldMouseState is not what BobberBar.update reads?); using perfect from now on");
        }
    }

    private static bool HasTrapBobber(BobberBar bar)
    {
        if (FishReflect.Get(bar, "bobbers") is not IEnumerable list)
            return false;
        foreach (var o in list)
            if (o?.ToString() == "(O)691")
                return true;
        return false;
    }
}

/// <summary>
/// The only Harmony patch of the fishing tool, applied lazily the first time fish runs with
/// FishingAssist = "natural": around BobberBar.update, show the game a pressed or released mouse button
/// (it reads Game1.oldMouseState) and put the real state back afterwards. Never applied in "perfect" mode.
/// To disable it, set FishingAssist to "perfect" in config.json.
/// </summary>
internal static class FishingPatches
{
    private const string HarmonyId = "Duckxy.StardewMCP.fishing";

    private static bool _tried;
    private static MouseState _saved;
    private static bool _swapped;

    public static bool Applied { get; private set; }

    /// <summary>Set when the self-check in <see cref="BobberDriver"/> finds the presses have no effect.</summary>
    public static bool Disabled;

    public static string? Error { get; private set; }

    public static bool EnsureApplied()
    {
        if (_tried)
            return Applied;
        _tried = true;
        try
        {
            var update = AccessTools.DeclaredMethod(typeof(BobberBar), "update", new[] { typeof(GameTime) });
            if (update == null)
            {
                Error = "BobberBar.update(GameTime) was not found";
                return false;
            }
            new Harmony(HarmonyId).Patch(update,
                prefix: new HarmonyMethod(typeof(FishingPatches), nameof(Update_Prefix)),
                finalizer: new HarmonyMethod(typeof(FishingPatches), nameof(Update_Finalizer)));
            Applied = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        return Applied;
    }

    private static void Update_Prefix(BobberBar __instance)
    {
        _swapped = false;
        if (Disabled || !BobberDriver.TryGetPress(__instance, out bool pressed))
            return;
        _saved = Game1.oldMouseState;
        _swapped = true;
        Game1.oldMouseState = new MouseState(_saved.X, _saved.Y, _saved.ScrollWheelValue,
            pressed ? ButtonState.Pressed : ButtonState.Released, _saved.MiddleButton, _saved.RightButton, _saved.XButton1, _saved.XButton2);
    }

    private static Exception? Update_Finalizer(Exception? __exception)
    {
        if (_swapped)
        {
            Game1.oldMouseState = _saved;
            _swapped = false;
        }
        return __exception;
    }
}
