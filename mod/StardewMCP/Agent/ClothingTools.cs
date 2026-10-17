using System;
using System.Collections;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewValley;
using StardewValley.Objects;

namespace StardewMCP.Agent;

/// <summary>
/// Clothing (#54): wear a hat, shirt or pants from the inventory, and see what you wear. Clothes come from shops
/// (shop_list / buy sell them where they stock them), dressers (interact, then menu take), drops and gifts.
/// Rings and boots belong to the equip tool (#27); wear is clothing only.
/// </summary>
public static partial class AgentTools
{
    private static ToolDef[] ClothingToolDefs() => new ToolDef[]
    {
        new()
        {
            Name = "wardrobe",
            Description = "What you are wearing (hat, shirt, pants) and the clothes in your inventory. Your look shows who you are on screen. Costs no game time.",
            Schema = Obj(new { }),
            Instant = true, AppendStatus = false,
            Run = (a, c) => Instant(c, WardrobeText())
        },
        new()
        {
            Name = "wear",
            Description = "Put on a hat, shirt or pants from your inventory; what you wore before goes back into that inventory slot. " +
                          "Find clothes in shops (shop_list / buy), in a dresser (interact with it, then menu take), as drops or gifts. Rings and boots are not clothing.",
            Schema = Obj(new { item = Str("Clothing item name from your inventory"), reason = Str(ReasonDesc) }, "item", "reason"),
            Run = Wear
        }
    };

    /// <summary>"shirt", "pants" or "hat" for a wearable item, else null.</summary>
    private static string? ClothingSlot(Item? it)
    {
        if (it is Hat) return "hat";
        if (it is Clothing)
        {
            // clothesType is 0 = shirt, 1 = pants (a NetInt or a NetEnum, so read it as a number).
            var t = FunnyReflect.Int(it, "clothesType");
            return t == 1 ? "pants" : "shirt";
        }
        return null;
    }

    private static string WornText(string slot)
    {
        var field = slot == "hat" ? "hat" : slot == "shirt" ? "shirtItem" : "pantsItem";
        return (FunnyReflect.Value(P, field) as Item)?.DisplayName ?? "";
    }

    private static string WardrobeText()
    {
        var sb = new StringBuilder("Wearing:\n");
        var hat = WornText("hat");
        var shirt = WornText("shirt");
        var pants = WornText("pants");
        sb.AppendLine($"  hat: {(hat.Length > 0 ? hat : "none")}");
        sb.AppendLine($"  shirt: {(shirt.Length > 0 ? shirt : $"plain shirt style {FunnyReflect.Value(P, "shirt")}")}");
        sb.AppendLine($"  pants: {(pants.Length > 0 ? pants : $"plain pants style {FunnyReflect.Value(P, "pants")}")}");
        var spare = Enumerable.Range(0, P.Items.Count).Where(i => ClothingSlot(P.Items[i]) != null)
            .Select(i => $"slot {i}: {P.Items[i].DisplayName} ({ClothingSlot(P.Items[i])})").ToList();
        sb.Append(spare.Count > 0 ? "Clothes in inventory: " + string.Join(", ", spare) : "No clothes in your inventory.");
        return sb.ToString();
    }

    private static IEnumerator Wear(JsonElement a, CallContext c)
    {
        var name = S(a, "item").Trim();
        int slot = FindItem(name, it => ClothingSlot(it) != null);
        if (slot < 0) { c.Fail($"No hat, shirt or pants called \"{name}\" in your inventory.\n{WardrobeText()}"); yield break; }
        yield return WaitIdle();
        var item = P.Items[slot];
        var kind = ClothingSlot(item)!;
        var field = kind == "hat" ? "hat" : kind == "shirt" ? "shirtItem" : "pantsItem";
        var old = FunnyReflect.Value(P, field) as Item;

        // Swap like the equipment slots of the inventory page: the old piece takes the new piece's place.
        if (!FunnyReflect.SetValue(P, field, item)) { c.Fail($"Could not put on {item.DisplayName}: this game version has no {field} slot I can set."); yield break; }
        P.Items[slot] = old!;
        FunnyReflect.Call(P, "UpdateClothing");
        yield return Ticks(5);

        var was = old != null ? $" (took off {old.DisplayName})" : "";
        Rt.Log.Add("event", "wear", $"{P.Name} put on {item.DisplayName}", new { slot = kind, item = item.DisplayName, replaced = old?.DisplayName });
        c.Result = $"You put on {item.DisplayName} as your {kind}{was}.";
    }
}
