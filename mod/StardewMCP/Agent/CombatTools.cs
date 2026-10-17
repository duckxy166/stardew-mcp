using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.Tools;
using Monster = StardewValley.Monsters.Monster;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// Combat. fight() plays the weapon like a player: equip the best one, walk to the nearest monster, face it and
/// swing with the game's own <c>Farmer.BeginUsingTool</c>, so damage, knockback, loot and combat XP are the game's.
/// The AI decides when to fight and when to stop. The weapon, monster and health rules are small internal helpers
/// so other tools (mine survival, interrupts) use the same ones.
/// </summary>
public static partial class AgentTools
{
    /// <summary>Monsters farther than this many tiles are not fought.</summary>
    internal const int FightSightTiles = 12;

    /// <summary>fight() never fights on at or below this health, however high the maximum is.</summary>
    internal const int FightStopHealthFloor = 15;

    /// <summary>fight() also stops at this percent of the maximum health (30% of 100 is 30, of 125 is 37).</summary>
    internal const int FightStopHealthPercent = 30;

    /// <summary>Within this many tiles of its target the farmer stops walking and swings.</summary>
    private const float FightMeleeTiles = 2f;
    private const int FightSwingTicks = 18;
    private const int FightReplanTicks = 20;
    private const int FightPathSteps = 5;
    private const int FightIgnoreTicks = 90;

    private static ToolDef CombatTool() => new()
    {
        Name = "fight",
        Description = "Fight monsters near you (mostly in the Mines). You equip your best weapon, walk to the nearest monster within 12 tiles, face it and keep swinging, then go on to the next. " +
                      "Stops early when no monster is left in range, when your health is low (30% of your maximum, never less than 15) or when a menu opens. About 1.4 game minutes pass per second. " +
                      "Check Health in status before you start. With eat_below it eats the best health-restoring food from your inventory whenever your health falls below that percent, then keeps fighting. " +
                      "The result lists the monsters defeated, the damage you took, the food eaten and the loot.",
        Schema = Obj(new
        {
            seconds = Int("How long to fight, 3-25 (default 10)"),
            eat_below = Int("Optional health percent, 1-99: when your health is below it, eat the best health-restoring food you carry, then keep fighting"),
            reason = Str(ReasonDesc)
        }, "seconds", "reason"),
        Run = Fight
    };

    // ------------------------------------------------------------------ helpers (shared with other combat tools)

    /// <summary>
    /// Health at or below which fight stops: <c>max(15, 30% of the maximum health)</c>. One place to change the rule.
    /// Integer maths, so 30% of 100 is exactly 30 (health is a whole number, so "at or below 37.5" is "at or below 37").
    /// </summary>
    internal static int FightStopHealth(Farmer who) => Math.Max(FightStopHealthFloor, who.maxHealth * FightStopHealthPercent / 100);

    internal static bool IsHealthLow(Farmer who) => who.health <= FightStopHealth(who);

    internal static float TileDistance(Point a, Point b) => Vector2.Distance(a.ToVector2(), b.ToVector2());

    /// <summary>
    /// Inventory slot of the weapon to fight with: the melee weapon with the highest max damage, a scythe only when
    /// there is nothing else. -1 when the farmer carries no weapon.
    /// </summary>
    internal static int BestWeaponSlot(Farmer who)
    {
        int Damage(int slot) => ((MeleeWeapon)who.Items[slot]).maxDamage.Value;

        int best = -1, bestScythe = -1;
        for (int i = 0; i < who.Items.Count; i++)
        {
            if (who.Items[i] is not MeleeWeapon w) continue;
            if (w.Name.Contains("Scythe", StringComparison.OrdinalIgnoreCase))
            {
                if (bestScythe < 0 || Damage(i) > Damage(bestScythe)) bestScythe = i;
            }
            else if (best < 0 || Damage(i) > Damage(best))
                best = i;
        }
        if (best >= 0) return best;
        if (bestScythe >= 0) return bestScythe;
        for (int i = 0; i < who.Items.Count; i++)
            if (who.Items[i] is Tool t && t.Name.Contains("Scythe", StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Inventory slot of the edible item that restores the most health (edible by the eat tool's rule, Edibility above
    /// -300), or -1 when the farmer carries nothing that heals.
    /// </summary>
    internal static int BestHealingFoodSlot(Farmer who)
    {
        int best = -1, bestHealth = 0;
        for (int i = 0; i < who.Items.Count; i++)
        {
            if (who.Items[i] is not SObject food || food.Edibility <= -300) continue;
            int healing = food.healthRecoveredOnConsumption();
            if (healing > bestHealth) { best = i; bestHealth = healing; }
        }
        return best;
    }

    /// <summary>The living monster closest to <paramref name="from"/> within <paramref name="maxTiles"/> tiles, or null.</summary>
    internal static Monster? NearestMonster(GameLocation loc, Point from, float maxTiles, Func<Monster, bool>? skip = null)
    {
        Monster? best = null;
        float bestDist = maxTiles;
        foreach (var npc in loc.characters)
        {
            if (npc is not Monster mon || mon.Health <= 0 || (skip != null && skip(mon))) continue;
            float d = TileDistance(from, mon.TilePoint);
            if (d > maxTiles || (best != null && d >= bestDist)) continue;
            best = mon;
            bestDist = d;
        }
        return best;
    }

    /// <summary>
    /// The first steps of the shortest walkable route to a tile next to the monster (empty when already next to it),
    /// or null when no tile next to it can be reached.
    /// </summary>
    private static Stack<Point>? ApproachSteps(GameLocation loc, Monster mon, int maxSteps)
    {
        var me = P.TilePoint;
        var goal = mon.TilePoint;
        var neighbours = Dirs.Select(d => new Point(goal.X + d.X, goal.Y + d.Y))
            .Where(q => q == me || Pathfinder.IsWalkable(loc, q.X, q.Y))
            .OrderBy(q => Math.Abs(q.X - me.X) + Math.Abs(q.Y - me.Y))
            .Take(2)
            .ToList();

        List<Vector2>? best = null;
        foreach (var q in neighbours)
        {
            if (q == me) return new Stack<Point>();
            var path = new Pathfinder().FindPath(loc, me.ToVector2(), q.ToVector2());
            if (path != null && (best == null || path.Count < best.Count)) best = path;
        }
        if (best == null) return null;

        // PathFindController pops from the top of the stack, so push the route backwards.
        var steps = new Stack<Point>();
        for (int i = Math.Min(best.Count, maxSteps) - 1; i >= 0; i--)
            steps.Push(new Point((int)best[i].X, (int)best[i].Y));
        return steps;
    }

    // ------------------------------------------------------------------ fight

    private static IEnumerator Fight(JsonElement a, CallContext c)
    {
        int seconds = Math.Clamp(I(a, "seconds", 10), 3, 25);
        int eatBelow = Math.Clamp(I(a, "eat_below", 0), 0, 99); // 0 = never eat
        int budget = seconds * 60;
        var loc = Loc;

        yield return WaitIdle();
        int slot = BestWeaponSlot(P);
        if (slot < 0) { c.Fail("You have no weapon in your inventory (sword, club, dagger or scythe)."); yield break; }
        P.CurrentToolIndex = slot;
        var weapon = P.Items[slot];

        var before = Snapshot();
        int healthStart = P.health;
        var engaged = new Dictionary<Monster, string>(ReferenceEqualityComparer.Instance);
        var ignoreUntil = new Dictionary<Monster, int>(ReferenceEqualityComparer.Instance);
        int swings = 0, nextSwing = 0, nextPlan = 0;
        string stop = "";
        bool allClear = false;
        var eaten = new List<string>();
        bool noFood = false;

        int t = 0;
        for (; t < budget; t++)
        {
            if (Game1.activeClickableMenu != null || Game1.eventUp) { stop = "Stopped: a menu, dialogue or cutscene opened."; break; }
            if (Game1.currentLocation != loc || Game1.isWarping) { stop = "Stopped: you left the map."; break; }
            if (eatBelow > 0 && P.health * 100 < P.maxHealth * eatBelow && !P.UsingTool && !P.isEating)
            {
                // Before the low-health stop: food that heals is better than quitting. The game's own eating path, like the eat tool.
                int food = BestHealingFoodSlot(P);
                if (food < 0)
                    noFood = true;
                else
                {
                    var item = P.Items[food];
                    var foodName = item.DisplayName;
                    int stackBefore = item.Stack;
                    P.controller = null;
                    P.Halt();
                    yield return WaitIdle(60);
                    yield return EatSlot(food);
                    if (P.Items[food] == item && item.Stack >= stackBefore)
                    {
                        stop = $"Stopped: eating {foodName} did not work.";
                        break;
                    }
                    eaten.Add(foodName);
                    continue;
                }
            }
            if (IsHealthLow(P))
            {
                stop = $"Stopped: health {P.health}/{P.maxHealth} is at or below {FightStopHealth(P)}. Eat food or call leave_mine before fighting on.";
                break;
            }

            var me = P.TilePoint;
            // A monster with no walkable route is skipped for a while, unless it has come within reach by itself.
            var mon = NearestMonster(loc, me, FightSightTiles,
                m => ignoreUntil.TryGetValue(m, out var until) && t < until && TileDistance(me, m.TilePoint) > FightMeleeTiles);
            if (mon == null)
            {
                if (NearestMonster(loc, me, FightSightTiles) == null)
                {
                    stop = $"All clear: no monsters within {FightSightTiles} tiles.";
                    allClear = true;
                    break;
                }
                yield return null; // only monsters with no route in view: give them time to come closer
                continue;
            }
            engaged.TryAdd(mon, mon.Name);

            if (TileDistance(me, mon.TilePoint) <= FightMeleeTiles)
            {
                if (P.controller != null)
                {
                    P.controller = null;
                    P.Halt();
                }
                if (!P.UsingTool && P.CanMove && t >= nextSwing)
                {
                    if (P.CurrentToolIndex != slot) P.CurrentToolIndex = slot;
                    Face(mon.TilePoint);
                    P.lastClick = new Vector2(mon.StandingPixel.X, mon.StandingPixel.Y);
                    P.BeginUsingTool();
                    swings++;
                    nextSwing = t + FightSwingTicks;
                }
            }
            else if (!P.UsingTool && t >= nextPlan)
            {
                // The monster moves, so re-plan a short route every few ticks instead of walking a long one.
                var steps = ApproachSteps(loc, mon, FightPathSteps);
                if (steps == null)
                    ignoreUntil[mon] = t + FightIgnoreTicks;
                else if (steps.Count > 0)
                {
                    P.controller = new PathFindController(steps, P, loc) { NPCSchedule = false };
                    P.setRunning(true, true);
                }
                nextPlan = t + FightReplanTicks;
            }
            yield return null;
        }

        if (P.controller != null)
        {
            P.controller = null;
            if (!P.UsingTool) P.Halt();
        }
        yield return WaitIdle(90); // let the last swing finish
        if (stop.Length == 0 || allClear)
            yield return Ticks(30); // loot flies to the farmer

        if (stop.Length == 0) stop = TimeUpText(loc);
        var defeated = engaged
            .Where(kv => kv.Key.Health <= 0 || !loc.characters.Any(n => ReferenceEquals(n, kv.Key)))
            .GroupBy(kv => kv.Value)
            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)
            .ToList();

        var sb = new StringBuilder($"Fought {t / 60}s with {weapon.DisplayName} ({swings} swings). {stop}");
        sb.Append($"\nDefeated: {(defeated.Count > 0 ? string.Join(", ", defeated) : "none")}.");
        if (eaten.Count > 0) sb.Append($"\nAte: {string.Join(", ", eaten)}.");
        if (noFood) sb.Append("\neat_below: you carry no (more) food that restores health.");
        sb.Append($"\nHealth {healthStart} → {P.health}/{P.maxHealth}.");
        var diff = Diff(before);
        if (diff.Length > 0) sb.Append('\n').Append(diff);
        c.Result = sb.ToString();
    }

    /// <summary>What to tell the AI when the time ran out: whether anything is left to fight.</summary>
    private static string TimeUpText(GameLocation loc)
    {
        if (Game1.currentLocation != loc) return "Time is up.";
        var me = P.TilePoint;
        var near = NearestMonster(loc, me, FightSightTiles);
        if (near == null) return $"Time is up. All clear: no monsters within {FightSightTiles} tiles.";
        return $"Time is up. {near.Name} (HP {near.Health}) is still {TileDistance(me, near.TilePoint):0} tiles away at ({near.TilePoint.X},{near.TilePoint.Y}); call fight again to keep going.";
    }
}
