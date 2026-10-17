using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Robin's carpenter shop (#10): <c>build</c> orders a farm building and <c>upgrade_house</c> the next house level.
///
/// Robin's counter opens <c>CarpenterMenu</c>, which needs the mouse on a map preview. The tools skip the preview and call
/// what the menu calls: <c>Farm.buildStructure</c> (the game checks the footprint and starts construction) and the answer to
/// Robin's "Upgrade your house?" question (<c>upgrade_Yes</c>). The costs come from the game's own building data
/// (<c>Game1.buildingData</c>); the tool pays them from the calling farmer's wallet and inventory, as the menu does.
/// Robin is gated like the shop door and counter are: the farmer must stand in her shop while she is there and open.
/// </summary>
public static partial class AgentTools
{
    internal static List<ToolDef> BuildingToolDefs() => new()
    {
        new()
        {
            Name = "build",
            Description = "Order a farm building from Robin (the carpenter, ScienceHouse in the Mountain; you must be inside her shop while it is open, about 9:00-17:00, and she is not busy). " +
                          "building=list shows what she builds with gold and material costs, build days and whether you can afford each. " +
                          "building=<name> without x,y suggests free spots on your farm. building=<name> with x,y (the TOP-LEFT tile of its footprint on the Farm) pays from your own money and inventory " +
                          "and starts construction; the building can be used after its build days (Robin works overnight, one project at a time). House upgrades use upgrade_house; animals use buy_animal.",
            Schema = Obj(new
            {
                building = Str("Building name as build(building=list) shows it, e.g. Coop, Barn, Silo, Well, Shed; or \"list\""),
                x = Int("Top-left tile x of the footprint on the Farm"),
                y = Int("Top-left tile y of the footprint on the Farm"),
                reason = Str(ReasonDesc)
            }, "building", "reason"),
            InstantFor = a => !(Has(a, "x") && Has(a, "y")),
            Run = Build
        },
        new()
        {
            Name = "upgrade_house",
            Description = "Ask Robin to upgrade your house to the next level (level 1 adds the kitchen: 10,000g + 450 Wood; level 2: 65,000g + 100 Hardwood; level 3: 100,000g). " +
                          "You must be inside her shop while it is open and she is not busy. It costs your own money and inventory, takes 3 days, and you can't use the house meanwhile.",
            Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
            Run = UpgradeHouse
        }
    };

    // ------------------------------------------------------------------ catalog

    private sealed record BuildSpec(string Id, string Name, int Cost, List<(string ItemId, string Label, int Amount)> Materials, int Days, int Width, int Height);

    /// <summary>What Robin builds from scratch, read from the game's building data (upgrades such as Big Coop are not included).</summary>
    private static List<BuildSpec> RobinCatalog()
    {
        var list = new List<BuildSpec>();
        foreach (var kv in Game1.buildingData)
        {
            var d = kv.Value;
            if (d == null) continue;
            if (!(RanchReflect.Str(d, "Builder") ?? "").Equals("Robin", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(RanchReflect.Str(d, "BuildingToUpgrade"))) continue;
            if (RanchReflect.CheckConditions(RanchReflect.Str(d, "BuildCondition")) == false) continue;

            var mats = new List<(string ItemId, string Label, int Amount)>();
            if (d.BuildMaterials != null)
                foreach (var m in d.BuildMaterials)
                {
                    var id = RanchReflect.Str(m, "ItemId");
                    int amount = RanchReflect.Int(m, "Amount") ?? 0;
                    if (string.IsNullOrEmpty(id) || amount <= 0) continue;
                    mats.Add((id, ItemRegistry.GetData(id)?.DisplayName ?? id, amount));
                }
            if (!RanchReflect.TryPoint(d, "Size", out int w, out int h) || w <= 0 || h <= 0) { w = 3; h = 3; }
            var name = RanchReflect.DisplayText(RanchReflect.Str(d, "Name"), kv.Key);
            list.Add(new BuildSpec(kv.Key, name, d.BuildCost, mats, RanchReflect.Int(d, "BuildDays") ?? 0, w, h));
        }
        return list.OrderBy(s => s.Cost).ThenBy(s => s.Name).ToList();
    }

    private static BuildSpec? FindSpec(List<BuildSpec> all, string want) =>
        all.FirstOrDefault(s => s.Id.Equals(want, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
        ?? all.FirstOrDefault(s => s.Id.Contains(want, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(want, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesId(Item it, string id) => it.QualifiedItemId == id || it.QualifiedItemId == "(O)" + id;

    private static int CountItem(string id) => P.Items.Where(i => i != null && MatchesId(i, id)).Sum(i => i.Stack);

    private static void TakeItems(string id, int amount)
    {
        for (int i = 0; i < P.Items.Count && amount > 0; i++)
        {
            var it = P.Items[i];
            if (it == null || !MatchesId(it, id)) continue;
            int take = Math.Min(amount, it.Stack);
            it.Stack -= take;
            amount -= take;
            if (it.Stack <= 0) P.Items[i] = null;
        }
    }

    private static string CostText(int gold, IEnumerable<(string ItemId, string Label, int Amount)> mats)
    {
        var parts = new List<string>();
        if (gold > 0) parts.Add($"{gold:N0}g");
        parts.AddRange(mats.Select(m => $"{m.Amount} {m.Label}"));
        return parts.Count == 0 ? "free" : string.Join(" + ", parts);
    }

    /// <summary>What the calling farmer lacks for a cost, or "" when they can pay it.</summary>
    private static string Shortfall(int gold, IEnumerable<(string ItemId, string Label, int Amount)> mats)
    {
        var lacking = new List<string>();
        if (P.Money < gold) lacking.Add($"{gold - P.Money:N0}g more gold");
        foreach (var m in mats)
        {
            int have = CountItem(m.ItemId);
            if (have < m.Amount) lacking.Add($"{m.Amount - have} more {m.Label}");
        }
        return string.Join(", ", lacking);
    }

    // ------------------------------------------------------------------ shop gate

    /// <summary>
    /// Why a shop can't be used right now, or null. The doors already keep players out of closed shops, but a farmer can
    /// still be inside at closing time, and the shop owner must be there to serve.
    /// </summary>
    private static string? ShopGate(string map, string owner, int open, int close, string what, Func<string?>? busy = null)
    {
        if (Loc.Name != map) return $"{what} is the {map} map. travel_to {map} first (you are in {Loc.Name}).";
        var b = busy?.Invoke();
        if (b != null) return b;
        if (Game1.timeOfDay < open || Game1.timeOfDay >= close)
            return $"{what} is closed now ({WorldText.FormatTime(Game1.timeOfDay)}; open {WorldText.FormatTime(open)}-{WorldText.FormatTime(close)}). Use wait, or come back tomorrow.";
        if (!Loc.characters.Any(n => n.Name.Equals(owner, StringComparison.OrdinalIgnoreCase)))
            return $"{owner} isn't at the counter right now (the shop may be closed today, or {owner} is away). Try again later or on another day.";
        return null;
    }

    /// <summary>Robin works on one project at a time: a building under construction or upgrade, or your own house upgrade.</summary>
    private static string? RobinBusyReason()
    {
        var farm = Game1.getFarm();
        foreach (var b in farm.buildings)
        {
            int left = RanchText.DaysOfConstructionLeft(b), up = RanchText.DaysUntilUpgrade(b);
            if (left > 0) return $"Robin is busy building the {b.buildingType.Value} at ({b.tileX.Value},{b.tileY.Value}) ({left} day(s) left). She takes one project at a time: come back when it is finished.";
            if (up > 0) return $"Robin is busy upgrading the {b.buildingType.Value} ({up} day(s) left). She takes one project at a time: come back when it is finished.";
        }
        if (P.daysUntilHouseUpgrade.Value > 0)
            return $"Robin is busy upgrading your house ({P.daysUntilHouseUpgrade.Value} day(s) left). She takes one project at a time.";
        return null;
    }

    private static string? RobinGate() => ShopGate("ScienceHouse", "Robin", 900, 1700, "Robin's carpenter shop", RobinBusyReason);

    // ------------------------------------------------------------------ build

    private static IEnumerator Build(JsonElement a, CallContext c)
    {
        var want = S(a, "building").Trim();
        var catalog = RobinCatalog();
        if (catalog.Count == 0) { c.Fail("Could not read Robin's building list from the game data (Game1.buildingData has no Robin buildings)."); yield break; }
        if (want.Length == 0 || want.Equals("list", StringComparison.OrdinalIgnoreCase)) { c.Result = BuildList(catalog); yield break; }

        var spec = FindSpec(catalog, want);
        if (spec == null) { c.Fail($"Robin doesn't build \"{want}\". She builds: {string.Join(", ", catalog.Select(s => s.Name))}."); yield break; }
        if (!(Has(a, "x") && Has(a, "y"))) { c.Result = BuildSpots(spec); yield break; }

        int x = I(a, "x", -1), y = I(a, "y", -1);
        var closed = RobinGate();
        if (closed != null) { c.Fail(closed); yield break; }

        var lacking = Shortfall(spec.Cost, spec.Materials);
        if (lacking.Length > 0) { c.Fail($"Can't afford a {spec.Name} ({CostText(spec.Cost, spec.Materials)}): you need {lacking}."); yield break; }

        var farm = Game1.getFarm();
        int mw = farm.Map.Layers[0].LayerWidth, mh = farm.Map.Layers[0].LayerHeight;
        if (x < 0 || y < 0 || x + spec.Width > mw || y + spec.Height > mh)
        {
            c.Fail($"A {spec.Name} is {spec.Width}x{spec.Height} tiles from the top-left tile ({x},{y}); that goes outside the Farm map ({mw}x{mh}).");
            yield break;
        }

        int money0 = P.Money;
        var have0 = spec.Materials.Select(m => CountItem(m.ItemId)).ToList();
        var attempt = TryBuildStructure(farm, spec.Id, new Point(x, y));
        if (!attempt.found) { c.Fail("This game version has no buildStructure(string, Vector2, Farmer, out Building, ...) the mod can call. Nothing was built or charged."); yield break; }
        if (attempt.error != null) { c.Fail($"The game refused the order: {attempt.error} Nothing was charged."); yield break; }
        if (!attempt.ok)
        {
            var blockers = BuildBlockers(farm, x, y, spec.Width, spec.Height);
            c.Fail($"Robin can't build a {spec.Name} at ({x},{y}): the {spec.Width}x{spec.Height} footprint (x {x}-{x + spec.Width - 1}, y {y}-{y + spec.Height - 1}) must be free, buildable ground." +
                   (blockers.Length > 0 ? $" Probably in the way: {blockers}." : "") +
                   $" Nothing was charged. build(building=\"{spec.Name}\") without x,y suggests free spots.");
            yield break;
        }

        // The menu pays after buildStructure succeeds. If this game version already charged, only the rest is taken.
        int rest = Math.Max(0, spec.Cost - (money0 - P.Money));
        if (rest > 0) P.Money -= rest;
        for (int i = 0; i < spec.Materials.Count; i++)
        {
            int taken = have0[i] - CountItem(spec.Materials[i].ItemId);
            if (spec.Materials[i].Amount > taken) TakeItems(spec.Materials[i].ItemId, spec.Materials[i].Amount - taken);
        }

        int days = attempt.building != null ? RanchText.DaysOfConstructionLeft(attempt.building) : 0;
        if (days <= 0) days = spec.Days;
        var ready = days > 0 ? $"It will be ready in {days} day(s); Robin works overnight and nobody can use it until then." : "It is ready now.";
        yield return Ticks(5);
        c.Result = $"Robin is building the {spec.Name} at ({x},{y}) ({spec.Width}x{spec.Height} tiles). {ready} Paid {CostText(spec.Cost, spec.Materials)}. Money left {P.Money}g.";
    }

    /// <summary>
    /// Calls <c>buildStructure(string typeId, Vector2 tile, Farmer who, out Building constructed, ...)</c> by reflection, the call
    /// CarpenterMenu makes. Written as a plain method because the out argument can't be used inside an iterator.
    /// </summary>
    private static (bool found, bool ok, Building? building, string? error) TryBuildStructure(Farm farm, string typeId, Point tile)
    {
        foreach (var m in farm.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (m.Name != "buildStructure") continue;
            var ps = m.GetParameters();
            if (ps.Length < 4 || ps[0].ParameterType != typeof(string) || !ps[3].ParameterType.IsByRef) continue;
            var args = new object?[ps.Length];
            args[0] = typeId;
            args[1] = new Vector2(tile.X, tile.Y);
            args[2] = P;
            args[3] = null;
            for (int i = 4; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType == typeof(bool) ? false : null);
            try
            {
                var result = m.Invoke(farm, args);
                return (true, result is true, result is true ? args[3] as Building : null, null);
            }
            catch (TargetInvocationException ex)
            {
                return (true, false, null, (ex.InnerException ?? ex).Message);
            }
            catch (Exception ex)
            {
                return (true, false, null, ex.Message);
            }
        }
        return (false, false, null, null);
    }

    private static string BuildList(List<BuildSpec> catalog)
    {
        var sb = new StringBuilder($"Robin builds these (you have {P.Money:N0}g). x,y is the top-left tile of the footprint on the Farm; keep the tiles below it walkable so the door can be reached:\n");
        foreach (var s in catalog)
        {
            var lacking = Shortfall(s.Cost, s.Materials);
            sb.AppendLine($"  {s.Name}{(s.Name.Equals(s.Id, StringComparison.OrdinalIgnoreCase) ? "" : $" (id {s.Id})")}: {CostText(s.Cost, s.Materials)} · {s.Days} day(s) · {s.Width}x{s.Height} tiles · " +
                          (lacking.Length == 0 ? "you can afford it" : $"you lack {lacking}"));
        }
        var busy = RobinBusyReason();
        if (busy != null) sb.AppendLine(busy);
        sb.Append("Upgrades of built coops and barns (Big Coop, Deluxe Barn...) are not available yet. House upgrades: upgrade_house. build(building=\"Coop\") suggests free spots; add x,y to order it.");
        return sb.ToString();
    }

    private static string BuildSpots(BuildSpec spec)
    {
        List<Point> spots;
        try { spots = FreeSpots(Game1.getFarm(), spec.Width, spec.Height, 6); }
        catch (Exception ex) { return $"Could not scan the Farm for a free {spec.Width}x{spec.Height} area ({ex.Message}). Walk to the Farm and use look to pick a top-left tile."; }
        var cost = $"{spec.Name} costs {CostText(spec.Cost, spec.Materials)} and takes {spec.Days} day(s).";
        if (spots.Count == 0)
            return $"{cost}\nNo free {spec.Width}x{spec.Height} area of open ground found on the Farm: clear weeds, stones, trees and stumps first (use look on the Farm). x,y is the top-left tile.";
        return $"{cost}\nFree {spec.Width}x{spec.Height} areas on the Farm, nearest the shipping bin first (x,y = top-left tile): {string.Join(" ", spots.Select(p => $"({p.X},{p.Y})"))}\n" +
               "These come from the map (open tillable ground); the game has the last word when you order. Leave the tiles below the building free so you can reach its door.";
    }

    /// <summary>Top-left tiles of up to <paramref name="max"/> separate free areas, nearest the shipping bin first. Free means open tillable ground or grass on <c>look</c>.</summary>
    private static List<Point> FreeSpots(Farm farm, int w, int h, int max)
    {
        int mw = farm.Map.Layers[0].LayerWidth, mh = farm.Map.Layers[0].LayerHeight;
        var blocked = new int[mw + 1, mh + 1]; // 2D prefix sums of non-free tiles
        for (int y = 0; y < mh; y++)
        for (int x = 0; x < mw; x++)
        {
            char sym = WorldText.Describe(farm, x, y).sym;
            int bad = sym == ',' || sym == 'g' ? 0 : 1;
            blocked[x + 1, y + 1] = blocked[x, y + 1] + blocked[x + 1, y] - blocked[x, y] + bad;
        }

        var bin = farm.buildings.FirstOrDefault(b => b.buildingType.Value == "Shipping Bin");
        var anchor = bin != null ? new Point(bin.tileX.Value, bin.tileY.Value) : new Point(mw / 2, mh / 2);
        var candidates = new List<Point>();
        for (int y = 0; y + h <= mh; y++)
        for (int x = 0; x + w <= mw; x++)
            if (blocked[x + w, y + h] - blocked[x, y + h] - blocked[x + w, y] + blocked[x, y] == 0)
                candidates.Add(new Point(x, y));

        var picked = new List<Point>();
        foreach (var p in candidates.OrderBy(p => Math.Abs(p.X + w / 2 - anchor.X) + Math.Abs(p.Y + h / 2 - anchor.Y)))
        {
            // Skip areas that touch one already picked, so the suggestions are different places.
            if (picked.Any(q => p.X < q.X + w + 1 && q.X < p.X + w + 1 && p.Y < q.Y + h + 1 && q.Y < p.Y + h + 1)) continue;
            picked.Add(p);
            if (picked.Count >= max) break;
        }
        return picked;
    }

    /// <summary>The tiles of a footprint that are not open ground, as "(x,y) what", for the failure message.</summary>
    private static string BuildBlockers(Farm farm, int x, int y, int w, int h)
    {
        var bad = new List<string>();
        try
        {
            for (int ty = y; ty < y + h; ty++)
            for (int tx = x; tx < x + w; tx++)
            {
                var (sym, label) = WorldText.Describe(farm, tx, ty);
                if (sym == ',' || sym == 'g') continue;
                var what = label ?? sym switch { '.' => "ground that can't be built on", '#' => "blocked", '~' => "water", _ => sym.ToString() };
                bad.Add($"({tx},{ty}) {what}");
            }
        }
        catch (Exception) { }
        return bad.Count == 0 ? "" : string.Join(", ", bad.Take(6)) + (bad.Count > 6 ? $" and {bad.Count - 6} more" : "");
    }

    // ------------------------------------------------------------------ house upgrade

    /// <summary>The game's price of the next house level: gold, and the item id, amount and name of the material (none for the last level).</summary>
    private static (int gold, string? itemId, int count, string label) HouseUpgradeCost(int level) => level switch
    {
        0 => (10000, "388", 450, "Wood"),
        1 => (65000, "709", 100, "Hardwood"),
        _ => (100000, null, 0, "")
    };

    private static IEnumerator UpgradeHouse(JsonElement a, CallContext c)
    {
        int level = P.HouseUpgradeLevel;
        if (P.daysUntilHouseUpgrade.Value > 0) { c.Fail($"Your house upgrade is already underway: {P.daysUntilHouseUpgrade.Value} day(s) left."); yield break; }
        if (level >= 3) { c.Fail("Your house is fully upgraded (level 3)."); yield break; }

        var closed = RobinGate();
        if (closed != null) { c.Fail(closed); yield break; }

        var (gold, itemId, count, label) = HouseUpgradeCost(level);
        var mats = itemId == null
            ? new List<(string ItemId, string Label, int Amount)>()
            : new List<(string ItemId, string Label, int Amount)> { (itemId, label, count) };
        var lacking = Shortfall(gold, mats);
        if (lacking.Length > 0) { c.Fail($"Can't afford the level {level + 1} house upgrade ({CostText(gold, mats)}): you need {lacking}."); yield break; }

        int money0 = P.Money, have0 = itemId == null ? 0 : CountItem(itemId);
        // Robin's "Upgrade your house?" question, answered yes: the game pays, starts the 3 days and tells the other players.
        Loc.answerDialogueAction("upgrade_Yes", Array.Empty<string>());
        yield return Ticks(10);
        var said = "";
        if (Game1.activeClickableMenu is DialogueBox box)
        {
            said = (box.getCurrentString() ?? "").Replace("\n", " ").Replace("^", " ").Trim();
            box.closeDialogue();
            yield return Ticks(10);
            if (Game1.activeClickableMenu is DialogueBox) Game1.exitActiveMenu();
        }

        var note = "";
        bool started = P.daysUntilHouseUpgrade.Value > 0;
        if (!started && P.Money == money0 && (itemId == null || CountItem(itemId) == have0))
        {
            // The game's own answer did nothing (this version names it differently): apply the upgrade the way it does.
            P.daysUntilHouseUpgrade.Value = 3;
            P.Money -= gold;
            if (itemId != null) TakeItems(itemId, count);
            started = true;
            note = " (applied directly: the game's upgrade_Yes answer changed nothing)";
        }
        if (!started)
        {
            c.Fail($"The upgrade did not start. Money {money0}g to {P.Money}g; check status.");
            yield break;
        }
        c.Result = $"Robin will upgrade your house to level {level + 1} in {P.daysUntilHouseUpgrade.Value} days{note}. Paid {CostText(gold, mats)}. Money left {P.Money}g." +
                   (said.Length > 0 ? $" Robin: \"{said}\"" : "");
    }
}
