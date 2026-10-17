using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Locations;
using StardewValley.Objects;

namespace StardewMCP.Agent;

/// <summary>
/// cook: make dishes from the cooking recipes you know, in a kitchen. It is the craft tool's logic (CraftingTools.cs) with
/// CraftingRecipe(key, isCookingRecipe: true), and the ingredients come from your pack and from the fridge of the house
/// you stand in (the fridge and any mini-fridge), as at the game's own stove.
/// </summary>
public static partial class AgentTools
{
    private static List<ToolDef> CookToolDefs() => new()
    {
        new()
        {
            Name = "cook",
            Description = "Cook dishes from the cooking recipes you know (costs no energy). You must be inside your farmhouse (or a cabin) with a kitchen, i.e. the house is upgraded once; " +
                          "you walk to the stove yourself. Ingredients are taken from your pack and from the fridge there. " +
                          "cook() lists your cooking recipes with ingredients and how many you can cook right now; cook(recipe, quantity) cooks that many. " +
                          "Dishes restore energy and health: use eat. New recipes come from the TV (watch_tv), villager letters, shops and friendship.",
            Schema = Obj(new
            {
                recipe = Str("Dish name, e.g. \"Fried Egg\"; empty or \"list\" lists your cooking recipes"),
                quantity = Int("How many times to cook it (default 1)"),
                reason = Str(ReasonDesc)
            }),
            InstantFor = a => { var r = S(a, "recipe").Trim(); return r.Length == 0 || r.Equals("list", StringComparison.OrdinalIgnoreCase); },
            Run = Cook
        }
    };

    // ------------------------------------------------------------------ kitchen and fridge

    /// <summary>The house you stand in, its stove tile when the map marks one, and what is wrong (empty when you can cook here).</summary>
    private static (FarmHouse? house, Point? stove, string problem) CookKitchen()
    {
        if (Loc is not FarmHouse house)
            return (null, null, $"Cooking needs a kitchen: go inside your farmhouse first (you are in {Loc.Name}). The fridge there is used too.");
        // The stove tile carries the kitchen Action. The upgrade level is the fallback: level 0 houses have no kitchen.
        var stoves = FindActionTiles(house, act => act.Contains("kitchen", StringComparison.OrdinalIgnoreCase));
        Point? stove = stoves.Count > 0 ? stoves[0] : null;
        int level = Peek.Int(house, "upgradeLevel") ?? -1;
        if (stove == null && level == 0)
            return (house, null, "This house has no kitchen yet. Upgrade the farmhouse at Robin's carpenter shop first.");
        return (house, stove, "");
    }

    /// <summary>The fridge of <paramref name="house"/> and any mini-fridge standing in it.</summary>
    private static List<Chest> CookFridges(GameLocation house)
    {
        var list = new List<Chest>();
        if (Peek.Get(house, "fridge") is Chest fridge) list.Add(fridge);
        foreach (var o in house.Objects.Values)
            if (o is Chest { fridge.Value: true } mini && !list.Contains(mini))
                list.Add(mini);
        return list;
    }

    /// <summary>The fridges of your own home, for listing recipes while you are somewhere else.</summary>
    private static List<Chest> HomeFridges()
    {
        try { return CookFridges(Utility.getHomeOfFarmer(P)); }
        catch (Exception) { return new List<Chest>(); }
    }

    private static List<Item> FridgeItems(IEnumerable<Chest> fridges) =>
        fridges.SelectMany(f => f.Items).Where(i => i != null).ToList();

    private static Dictionary<string, int> FridgeCounts(IEnumerable<Chest> fridges) =>
        FridgeItems(fridges).GroupBy(i => i.DisplayName).ToDictionary(g => g.Key, g => g.Sum(i => i.Stack));

    private static string FridgeChange(Dictionary<string, int> before, Dictionary<string, int> after)
    {
        var parts = new List<string>();
        foreach (var kv in before)
        {
            after.TryGetValue(kv.Key, out var now);
            if (now != kv.Value) parts.Add($"{kv.Key} {now - kv.Value}");
        }
        return parts.Count == 0 ? "" : "Fridge change: " + string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ cook

    private static IEnumerator Cook(JsonElement a, CallContext c)
    {
        CraftingRecipe.InitShared();
        var want = S(a, "recipe").Trim();
        if (want.Length == 0 || want.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var home = Loc is FarmHouse here ? CookFridges(here) : HomeFridges();
            var elsewhere = Loc is FarmHouse ? "" : $" You are in {Loc.Name}: cook works inside your farmhouse.";
            c.Result = RecipeList(true, FridgeItems(home)) +
                       (home.Count > 0 ? "\nCounts include the fridge." : "\nCounts include your pack only.") + elsewhere;
            yield break;
        }

        var key = FindRecipeKey(want, true);
        if (key == null)
        {
            c.Fail($"You don't know a cooking recipe called \"{want}\". cook() lists the dishes you know.");
            yield break;
        }

        var (house, stove, problem) = CookKitchen();
        if (house == null || problem.Length > 0)
        {
            c.Fail(problem);
            yield break;
        }
        // Stand at the stove like a player would. Failing to find a free tile next to it is not a reason to refuse: the house is enough.
        if (stove is { } st && Math.Abs(P.TilePoint.X - st.X) + Math.Abs(P.TilePoint.Y - st.Y) > 2)
            yield return WalkAdjacent(st, new CallContext());
        if (Loc != house)
        {
            c.Fail("You left the house before cooking.");
            yield break;
        }

        int qty = Math.Clamp(I(a, "quantity", 1), 1, 99);
        var fridges = CookFridges(house);
        var before = Snapshot();
        var fridgeBefore = FridgeCounts(fridges);
        string name = key, why = "";
        int made = 0, perCraft = 1;
        for (int n = 0; n < qty; n++)
        {
            var cr = new CraftingRecipe(key, true);
            name = cr.DisplayName;
            perCraft = Math.Max(1, cr.numberProducedPerCraft);
            if (!cr.doesFarmerHaveIngredientsInInventory(FridgeItems(fridges)))
            {
                why = $"missing ingredients (needs {Ingredients(cr)})";
                break;
            }
            Item created = cr.createItem();
            // Check the room before the ingredients are used up, so a full pack never eats them.
            if (!P.couldInventoryAcceptThisItem(created))
            {
                why = "your inventory is full";
                break;
            }
            // Pack first, then the fridges: the same order the game's kitchen uses.
            cr.consumeIngredients(fridges.Select(f => f.Items).Cast<IInventory>().ToList());
            P.addItemToInventoryBool(created);
            CookRecord(created.ItemId);
            made++;
        }
        yield return Ticks(2);

        if (made == 0)
        {
            c.Fail($"Could not cook {name}: {why}.");
            yield break;
        }
        var text = $"Cooked {made}x {name}{(perCraft > 1 ? $" ({made * perCraft} dishes)" : "")}.";
        if (made < qty) text += $" Stopped after {made} of {qty}: {why}.";
        var parts = new[] { Diff(before), FridgeChange(fridgeBefore, FridgeCounts(fridges)) }.Where(s => s.Length > 0);
        c.Result = text + " " + string.Join(" ", parts) + " Eat a dish with eat.";
    }

    /// <summary>What the cooking page does after a dish: count it for the recipe and check the cooking achievements. Skipped quietly if the game names them differently.</summary>
    private static void CookRecord(string itemId)
    {
        try { Rt.Helper.Reflection.GetMethod(P, "cookedRecipe", false)?.Invoke(itemId); }
        catch (Exception) { /* the dish is made either way */ }
        try { Rt.Helper.Reflection.GetMethod(Game1.stats, "checkForCookingAchievements", false)?.Invoke(); }
        catch (Exception) { /* achievements are a bonus */ }
    }
}
