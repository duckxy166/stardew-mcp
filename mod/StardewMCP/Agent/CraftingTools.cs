using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// craft and place, plus the chest tool (see ChestTools.cs). craft makes items from the recipes the farmer knows,
/// place puts an item on the ground. Crafted machines, chests and sprinklers are ordinary inventory items, so
/// place is the step that puts them in the world; interact(holding) then loads a machine and chest/menu use a chest.
/// </summary>
public static partial class AgentTools
{
    private static List<ToolDef> CraftingToolDefs() => new()
    {
        new()
        {
            Name = "craft",
            Description = "Craft items from the crafting recipes you know, using ingredients in your inventory (costs no energy). " +
                          "craft() lists your recipes with their ingredients and how many you can make right now. craft(recipe, quantity) makes that many " +
                          "(one craft can make several items, e.g. torches). Crafted machines, chests, sprinklers and fences are inventory items: put them in the world with place. " +
                          "Cooking recipes are not included.",
            Schema = Obj(new
            {
                recipe = Str("Recipe name, e.g. \"Chest\" or \"Furnace\"; empty or \"list\" lists your recipes"),
                quantity = Int("How many crafts (default 1)"),
                reason = Str(ReasonDesc)
            }),
            InstantFor = a => { var r = S(a, "recipe").Trim(); return r.Length == 0 || r.Equals("list", StringComparison.OrdinalIgnoreCase); },
            Run = Craft
        },
        new()
        {
            Name = "place",
            Description = "Put an item from your inventory on the ground at one or more tiles: machines (Furnace...), chests, sprinklers, scarecrows, fences, paths, torches, furniture, bombs, crab pots. " +
                          "You walk next to each tile automatically. The tile must be free and the item must be allowed there (a crab pot needs water, a sprinkler goes on open ground). " +
                          "Seeds and fertilizer on tilled soil also work here, but plant is the usual tool for them.",
            Schema = Obj(new
            {
                item = Str("Item name, e.g. \"Chest\" or \"Furnace\""),
                tiles = Tiles("List of [x,y] tiles, max 40"),
                reason = Str(ReasonDesc)
            }, "item", "tiles", "reason"),
            Run = Place
        },
        ChestToolDef()
    };

    // ------------------------------------------------------------------ craft

    private static IEnumerator Craft(JsonElement a, CallContext c)
    {
        CraftingRecipe.InitShared();
        var want = S(a, "recipe").Trim();
        if (want.Length == 0 || want.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            c.Result = RecipeList();
            yield break;
        }

        var key = FindRecipeKey(want);
        if (key == null)
        {
            c.Fail($"You don't know a recipe called \"{want}\". craft() lists the recipes you know.");
            yield break;
        }

        int qty = Math.Clamp(I(a, "quantity", 1), 1, 99);
        var before = Snapshot();
        string name = key, why = "";
        int made = 0, perCraft = 1;
        for (int n = 0; n < qty; n++)
        {
            var cr = new CraftingRecipe(key, false);
            name = cr.DisplayName;
            perCraft = Math.Max(1, cr.numberProducedPerCraft);
            if (!cr.doesFarmerHaveIngredientsInInventory())
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
            cr.consumeIngredients(null);
            P.addItemToInventoryBool(created);
            // What the crafting page does after a craft: count it (recipe counters feed the crafting achievements).
            if (P.craftingRecipes.ContainsKey(key)) P.craftingRecipes[key] += perCraft;
            made++;
        }
        if (made > 0) Game1.stats.checkForCraftingAchievements();
        yield return Ticks(2);

        if (made == 0)
        {
            c.Fail($"Could not craft {name}: {why}.");
            yield break;
        }
        var items = made * perCraft;
        var text = $"Crafted {made}x {name}{(perCraft > 1 ? $" ({items} items)" : "")}.";
        if (made < qty) text += $" Stopped after {made} of {qty}: {why}.";
        var diff = Diff(before);
        c.Result = diff.Length > 0 ? text + " " + diff : text;
    }

    /// <summary>Key of the known recipe called <paramref name="want"/> (key or display name, exact first, else the first partial match).</summary>
    private static string? FindRecipeKey(string want)
    {
        string? partial = null;
        foreach (var key in P.craftingRecipes.Keys.ToList())
        {
            CraftingRecipe cr;
            try { cr = new CraftingRecipe(key, false); }
            catch (Exception) { continue; }
            if (key.Equals(want, StringComparison.OrdinalIgnoreCase) || cr.DisplayName.Equals(want, StringComparison.OrdinalIgnoreCase))
                return key;
            if (partial == null && (key.Contains(want, StringComparison.OrdinalIgnoreCase) || cr.DisplayName.Contains(want, StringComparison.OrdinalIgnoreCase)))
                partial = key;
        }
        return partial;
    }

    private static string RecipeList()
    {
        var ready = new List<string>();
        var missing = new List<string>();
        foreach (var key in P.craftingRecipes.Keys.ToList())
        {
            CraftingRecipe cr;
            try { cr = new CraftingRecipe(key, false); }
            catch (Exception) { continue; }
            int can = cr.getCraftableCount((IList<Item>?)null);
            var makes = cr.numberProducedPerCraft > 1 ? $" (makes {cr.numberProducedPerCraft})" : "";
            var line = $"  {cr.DisplayName}{makes}: {Ingredients(cr)}";
            if (can > 0) ready.Add($"{line} [you can craft {can}]");
            else missing.Add(line);
        }
        var sb = new StringBuilder($"Crafting recipes you know ({ready.Count + missing.Count}):\n");
        sb.AppendLine("Craftable now:");
        sb.AppendLine(ready.Count == 0 ? "  none" : string.Join("\n", ready));
        sb.AppendLine("Missing ingredients:");
        sb.Append(missing.Count == 0 ? "  none" : string.Join("\n", missing));
        return sb.ToString();
    }

    private static string Ingredients(CraftingRecipe cr) =>
        string.Join(", ", cr.recipeList.Select(kv => $"{kv.Value} {IngredientName(kv.Key)}"));

    /// <summary>Recipe ingredient keys are item ids, or a negative category id for "any item of this kind".</summary>
    private static string IngredientName(string key)
    {
        if (int.TryParse(key, out var category) && category < 0)
        {
            return category switch
            {
                -4 => "any fish",
                -5 => "any egg",
                -6 => "any milk",
                -75 => "any vegetable",
                -79 => "any fruit",
                -80 => "any flower",
                -81 => "any forage green",
                _ => $"any item of category {category}"
            };
        }
        return ItemRegistry.GetData(key)?.DisplayName ?? key;
    }

    // ------------------------------------------------------------------ place

    private static IEnumerator Place(JsonElement a, CallContext c)
    {
        var name = S(a, "item").Trim();
        var tiles = TileList(a, "tiles").Take(40).ToList();
        if (tiles.Count == 0) { c.Fail("No tiles given."); yield break; }
        int slot = FindItem(name, it => it is SObject);
        if (slot < 0) { c.Fail($"No placeable \"{name}\" in inventory. Craft it (craft) or buy it first."); yield break; }

        var first = P.Items[slot];
        string qid = first.QualifiedItemId, label = first.DisplayName;
        bool IsIt(Item it) => it is SObject && it.QualifiedItemId == qid;
        var loc = Loc;
        var lines = new List<string>();
        int placed = 0;

        foreach (var t in tiles)
        {
            if (Game1.activeClickableMenu != null) { lines.Add("Stopped: a menu/dialogue opened."); break; }
            if (FindItem(label, IsIt) < 0) { lines.Add($"Ran out of {label}."); break; }
            if (loc.Objects.TryGetValue(t.ToVector2(), out var there))
            {
                lines.Add($"({t.X},{t.Y}) already has {there.DisplayName}");
                continue;
            }
            var sub = new CallContext();
            yield return WalkAdjacent(t, sub);
            if (!sub.Ok) { lines.Add($"({t.X},{t.Y}) skipped: {sub.Result}"); continue; }
            if (Loc != loc) { lines.Add("Stopped: you left the map."); break; }

            slot = FindItem(label, IsIt);
            if (slot < 0) { lines.Add($"Ran out of {label}."); break; }
            P.CurrentToolIndex = slot;
            int had = CountOfId(P.Items, qid);
            Utility.tryToPlaceItem(loc, (SObject)P.Items[slot], t.X * 64 + 32, t.Y * 64 + 32);
            yield return Ticks(8);
            if (CountOfId(P.Items, qid) < had) placed++;
            else lines.Add($"({t.X},{t.Y}) could not place {label} (blocked, not placeable here, or not allowed on this map)");
        }

        var text = $"Placed {placed}/{tiles.Count} {label}." + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "");
        if (placed > 0 && first is Chest) text += "\nUse the chest tool with its (x, y) to fill or empty it.";
        if (placed == 0) c.Fail(text);
        else c.Result = text;
    }

    /// <summary>Total stack size of the item with this qualified id, whatever its quality.</summary>
    private static int CountOfId(IEnumerable<Item?> items, string qid) =>
        items.Where(i => i != null && i.QualifiedItemId == qid).Sum(i => i!.Stack);
}
