using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// equip: rings, boots and hats go into the farmer's equipment slots (the old item goes back into the
/// inventory); bait and tackle go onto a fishing rod through the rod's own attach logic. The status and
/// inventory tools show the result via <see cref="EquipmentText"/>.
/// Members of the game that could not be checked against the decompiled game are reached by reflection
/// and listed in the PR, so a wrong guess fails with a message at run time instead of breaking the build.
/// </summary>
public static partial class AgentTools
{
    private const int EqBaitCategory = -21;   // Object.baitCategory
    private const int EqTackleCategory = -22; // Object.tackleCategory
    private const BindingFlags EqAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static ToolDef EquipTool() => new()
    {
        Name = "equip",
        Description = "Put on an item from your inventory. A ring goes on your left or right hand, boots and hats into their slot; whatever was there goes back into your inventory. " +
                      "Bait or tackle is attached to your fishing rod (bait in the first slot, tackle in the next free one); what was attached goes back into your inventory. " +
                      "status and inventory show what you wear and what is on the rod.",
        Schema = Obj(new
        {
            item = Str("Item name from your inventory, e.g. \"Glow Ring\", \"Sneakers\", \"Cowboy Hat\", \"Bait\""),
            slot = new { type = "string", @enum = new[] { "left", "right" }, description = "Rings only: which hand. Default: the empty one, else the left." },
            count = Int("Bait only: how many to attach (default: the whole stack; keep some if you want it for crab pots)"),
            reason = Str(ReasonDesc)
        }, "item", "reason"),
        Run = EquipItem
    };

    private static bool EqIsEquippable(Item it) =>
        it is Ring or Boots or Hat || (it is SObject o && (o.Category == EqBaitCategory || o.Category == EqTackleCategory));

    private static string EqEquippableNames()
    {
        var names = P.Items.Where(i => i != null && EqIsEquippable(i)).Select(i => i.DisplayName).Distinct().ToList();
        return names.Count == 0 ? "nothing" : string.Join(", ", names);
    }

    private static IEnumerator EquipItem(JsonElement a, CallContext c)
    {
        var name = S(a, "item").Trim();
        if (name.Length == 0) { c.Fail("Give the item to equip: a ring, boots, hat, bait or tackle from your inventory."); yield break; }
        yield return WaitIdle();
        int slot = FindItem(name, EqIsEquippable);
        if (slot < 0) { c.Fail($"No ring, boots, hat, bait or tackle called \"{name}\" in your inventory. Equippable now: {EqEquippableNames()}."); yield break; }
        EqDo(a, c, slot);
        if (!c.Ok) yield break;
        yield return Ticks(5);
    }

    // ------------------------------------------------------------------ equipment slots

    /// <summary>Equips the item in inventory slot <paramref name="slot"/>; sets the result or fails <paramref name="c"/>. No yields, so it can use try/catch.</summary>
    private static void EqDo(JsonElement a, CallContext c, int slot)
    {
        var item = P.Items[slot];
        try
        {
            switch (item)
            {
                case Ring ring:
                {
                    var want = S(a, "slot").Trim().ToLowerInvariant();
                    bool bothFull = P.leftRing.Value != null && P.rightRing.Value != null;
                    bool left = want.StartsWith("l") || (!want.StartsWith("r") && (P.leftRing.Value == null || P.rightRing.Value != null));
                    var field = left ? P.leftRing : P.rightRing;
                    var old = EqPutOn(field, typeof(Ring), () => field.Value, v => field.Value = (Ring?)v, ring);
                    P.Items[slot] = old;
                    var hint = bothFull && want.Length == 0 ? " Both hands were full, so the left ring was replaced; pass slot=right to replace the right one." : "";
                    c.Result = EqDone(ring, left ? "left ring" : "right ring", old, slot) + hint;
                    break;
                }
                case Boots boots:
                {
                    var field = P.boots;
                    var old = EqPutOn(field, typeof(Boots), () => field.Value, v => field.Value = (Boots?)v, boots);
                    P.Items[slot] = old;
                    c.Result = EqDone(boots, "boots", old, slot);
                    break;
                }
                case Hat hat:
                {
                    var field = P.hat;
                    var old = EqPutOn(field, typeof(Hat), () => field.Value, v => field.Value = (Hat?)v, hat);
                    P.Items[slot] = old;
                    c.Result = EqDone(hat, "hat", old, slot);
                    break;
                }
                case SObject bait:
                    EqAttach(slot, bait, a, c);
                    break;
                default:
                    c.Fail($"{item.DisplayName} can't be equipped.");
                    break;
            }
        }
        catch (Exception ex)
        {
            c.Fail($"Could not equip {item.DisplayName}: {EqInner(ex).Message}");
        }
    }

    private static string EqDone(Item item, string where, Item? old, int slot)
    {
        string desc;
        try { desc = (item.getDescription() ?? "").Replace("\n", " ").Replace("^", " ").Trim(); }
        catch { desc = ""; }
        if (desc.Length > 140) desc = desc[..139] + "…";
        var back = old != null ? $" {old.DisplayName} went back into your inventory (slot {slot})." : "";
        return $"Equipped {item.DisplayName} as your {where}.{(desc.Length > 0 ? $" ({desc})" : "")}{back}";
    }

    /// <summary>
    /// Swaps <paramref name="incoming"/> into an equipment field and returns the item that was there.
    /// The game's own Farmer.Equip&lt;T&gt;(T, NetRef&lt;T&gt;) does the swap and runs onUnequip/onEquip; if this
    /// game version has no such method, the field is set by hand and the same two hooks are called.
    /// </summary>
    private static Item? EqPutOn(object netRef, Type itemType, Func<Item?> get, Action<Item?> set, Item incoming)
    {
        var equip = typeof(Farmer).GetMethods(EqAll).FirstOrDefault(m =>
            m.Name == "Equip" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 &&
            m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.Name.StartsWith("NetRef"));
        if (equip != null)
            return equip.MakeGenericMethod(itemType).Invoke(P, new object?[] { incoming, netRef }) as Item;

        var old = get();
        EqCallHook(old, "onUnequip");
        set(incoming);
        EqCallHook(incoming, "onEquip");
        return old;
    }

    /// <summary>Calls item.onEquip / onUnequip whatever its parameters are in this game version: (), (Farmer) or (Farmer, GameLocation).</summary>
    private static void EqCallHook(Item? item, string name)
    {
        if (item == null) return;
        foreach (var m in item.GetType().GetMethods(EqAll).Where(m => m.Name == name))
        {
            var ps = m.GetParameters();
            if (ps.Length == 0) { m.Invoke(item, null); return; }
            if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(typeof(Farmer))) { m.Invoke(item, new object?[] { P }); return; }
            if (ps.Length == 2 && ps[0].ParameterType.IsAssignableFrom(typeof(Farmer)) && ps[1].ParameterType.IsAssignableFrom(typeof(GameLocation)))
            {
                m.Invoke(item, new object?[] { P, Loc });
                return;
            }
        }
    }

    private static Exception EqInner(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    // ------------------------------------------------------------------ bait and tackle

    /// <summary>The item slots of a fishing rod (slot 0 is bait, the rest tackle); empty slots are null.</summary>
    private static List<SObject?> EqAttachments(Tool rod)
    {
        var list = new List<SObject?>();
        if (AccessTools.Field(rod.GetType(), "attachments")?.GetValue(rod) is IEnumerable slots)
            foreach (var s in slots) list.Add(s as SObject);
        return list;
    }

    private static string EqRodText(Tool rod)
    {
        var slots = EqAttachments(rod);
        if (slots.Count == 0) return $"{rod.DisplayName}: no bait or tackle slots";
        var parts = slots.Select((s, i) => $"{(i == 0 ? "bait" : "tackle")} {(s == null ? "none" : $"{s.DisplayName} x{s.Stack}")}");
        return $"{rod.DisplayName}: {string.Join(" · ", parts)}";
    }

    /// <summary>What the farmer wears, and with <paramref name="rods"/> what is on each fishing rod in the inventory.</summary>
    internal static string EquipmentText(Farmer p, bool rods)
    {
        static string Nm(Item? i) => i == null ? "none" : i.DisplayName;
        var sb = new StringBuilder($"Equipped: left ring {Nm(p.leftRing.Value)} · right ring {Nm(p.rightRing.Value)} · boots {Nm(p.boots.Value)} · hat {Nm(p.hat.Value)}");
        if (rods)
            foreach (var rod in p.Items.OfType<FishingRod>())
                sb.Append("\nFishing rod ").Append(EqRodText(rod));
        return sb.ToString();
    }

    /// <summary>Extra lines for the status tool: equipment and accepted special orders. Never throws.</summary>
    internal static string StatusExtras(Farmer p)
    {
        var lines = new List<string>();
        try { lines.Add(EquipmentText(p, rods: false)); } catch { /* reflection or API mismatch: leave the line out */ }
        try { var so = SoStatusLine(); if (so.Length > 0) lines.Add(so); } catch { /* same */ }
        return string.Join("\n", lines);
    }

    /// <summary>Extra lines for the inventory tool: equipment and what is on the fishing rod(s). Never throws.</summary>
    internal static string InventoryExtras(Farmer p)
    {
        try { return EquipmentText(p, rods: true); }
        catch { return ""; }
    }

    /// <summary>Tries the rod's own attach logic; true when the rod took <paramref name="offer"/>.</summary>
    private static bool EqTryAttach(Tool rod, SObject offer, out SObject? replaced, out string why)
    {
        replaced = null;
        why = "";
        var type = rod.GetType();
        MethodInfo? Find(string name, int arity) => type.GetMethods(EqAll).FirstOrDefault(m =>
            m.Name == name && m.GetParameters().Length == arity && m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(SObject)));

        try
        {
            var attach1 = Find("attach", 1);
            var attach2 = Find("attach", 2);
            if (attach1 != null)
            {
                var can1 = Find("canThisBeAttached", 1);
                if (can1 != null && can1.Invoke(rod, new object[] { offer }) is false) { why = "it has no suitable slot for that"; return false; }
                replaced = attach1.Invoke(rod, new object[] { offer }) as SObject;
            }
            else if (attach2 != null)
            {
                // Versions where attach takes the slot: take the first slot the rod says can hold it.
                var can2 = Find("canThisBeAttached", 2);
                int slots = EqAttachments(rod).Count, found = -1;
                for (int s = 0; s < slots && found < 0; s++)
                    if (can2 == null || can2.Invoke(rod, new object[] { offer, s }) is true) found = s;
                if (found < 0) { why = "it has no suitable slot for that"; return false; }
                replaced = attach2.Invoke(rod, new object[] { offer, found }) as SObject;
            }
            else
            {
                why = "this game version has no attach method the mod can call";
                return false;
            }
        }
        catch (Exception ex)
        {
            why = EqInner(ex).Message;
            return false;
        }

        if (ReferenceEquals(replaced, offer)) { replaced = null; why = "it refused the item"; return false; }
        // attach returns the item that was replaced; null means an empty slot, or a rod that ignored the call.
        if (replaced == null && !EqAttachments(rod).Any(x => x != null && x.QualifiedItemId == offer.QualifiedItemId))
        {
            why = "it did not keep the item";
            return false;
        }
        return true;
    }

    private static void EqAttach(int slot, SObject item, JsonElement a, CallContext c)
    {
        var rods = P.Items.OfType<FishingRod>().ToList();
        if (rods.Count == 0) { c.Fail("You have no fishing rod to put it on. Willy's Fish Shop (the Beach) sells rods."); return; }
        if (P.CurrentItem is FishingRod held && rods.Remove(held)) rods.Insert(0, held);

        int count = Has(a, "count") ? Math.Clamp(I(a, "count", item.Stack), 1, item.Stack) : item.Stack;
        bool whole = count >= item.Stack;
        var offer = item;
        if (!whole) { offer = (SObject)item.getOne(); offer.Stack = count; }

        var failures = new List<string>();
        foreach (var rod in rods)
        {
            if (!EqTryAttach(rod, offer, out var replaced, out var why)) { failures.Add($"{rod.DisplayName}: {why}"); continue; }

            if (whole) P.Items[slot] = null; else item.Stack -= count;
            var back = EqGiveBack(replaced, slot);
            int at = EqAttachments(rod).FindIndex(x => x != null && x.QualifiedItemId == offer.QualifiedItemId);
            c.Result = $"Attached {count} x {offer.DisplayName} to your {rod.DisplayName} ({(at == 0 ? "bait slot" : "tackle slot")}).{back} Now: {EqRodText(rod)}.";
            return;
        }
        c.Fail($"No fishing rod of yours takes {item.DisplayName}: {string.Join("; ", failures)}. Cheaper rods have fewer bait/tackle slots.");
    }

    /// <summary>Puts an item taken off the rod back into the inventory (its old slot when free), dropping it at the feet if the bag is full.</summary>
    private static string EqGiveBack(Item? old, int preferredSlot)
    {
        if (old == null) return "";
        if (P.Items[preferredSlot] == null)
        {
            P.Items[preferredSlot] = old;
            return $" {old.DisplayName} x{old.Stack} went back into your inventory (slot {preferredSlot}).";
        }
        if (P.addItemToInventoryBool(old)) return $" {old.DisplayName} x{old.Stack} went back into your inventory.";
        Game1.createItemDebris(old, P.getStandingPosition(), P.FacingDirection);
        return $" Your inventory is full: {old.DisplayName} x{old.Stack} was dropped at your feet.";
    }
}
