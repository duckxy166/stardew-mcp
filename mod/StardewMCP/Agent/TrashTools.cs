using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Tools;

namespace StardewMCP.Agent;

/// <summary>
/// trash: throw items from the pack away, as dropping them on the inventory page's trash can does. Tools are never
/// trashed, and anything valuable (worth 100g or more in total, gold/iridium quality, weapons, rings, boots, hats,
/// Museum finds) needs confirm=true, so an AI can not lose its good stuff by tidying up. The game's own
/// Utility.trashItem runs for each piece, so a trash can upgrade refunds money as it does for a player.
/// </summary>
public static partial class AgentTools
{
    private const int TrashConfirmValue = 100;

    private static List<ToolDef> TrashToolDefs() => new()
    {
        new()
        {
            Name = "trash",
            Description = "Throw items from your pack away for good (the inventory trash can; an upgraded can refunds some money). " +
                          "item: item name. quantity: how many (default 1; more than you have trashes all). quality: only that quality (normal, silver, gold, iridium); without it the lowest quality goes first. " +
                          "Tools can not be trashed. Valuable things are refused unless you pass confirm=true: worth 100g or more in total, gold or iridium quality, weapons, rings, boots, hats and Museum finds (artifacts, minerals, gems). " +
                          "Selling (ship) or storing (chest) is usually better than trashing.",
            Schema = Obj(new
            {
                item = Str("Item name, e.g. \"Driftwood\""),
                quantity = Int("How many to trash (default 1)"),
                quality = new { type = "string", @enum = new[] { "normal", "silver", "gold", "iridium" }, description = "Only this quality (default: lowest first)" },
                confirm = new { type = "boolean", description = "Set true to trash something valuable (see the description)" },
                reason = Str(ReasonDesc)
            }, "item", "reason"),
            Run = Trash
        }
    };

    private static bool TrashFlag(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IEnumerator Trash(JsonElement a, CallContext c)
    {
        var want = S(a, "item").Trim();
        if (want.Length == 0)
        {
            c.Fail("Say which item to trash.");
            yield break;
        }
        var byQuality = QualityIs(a);
        bool Fits(Item it) => byQuality == null || byQuality(it);
        int first = FindIn(P.Items, want, Fits);
        if (first < 0)
        {
            c.Fail($"No \"{want}\" in your pack{(S(a, "quality").Trim().Length > 0 ? " with that quality" : "")}.");
            yield break;
        }

        var proto = P.Items[first];
        if (proto is Tool && proto is not MeleeWeapon)
        {
            c.Fail($"{proto.DisplayName} is a tool and is never trashed with this tool.");
            yield break;
        }
        if (!proto.canBeTrashed())
        {
            c.Fail($"{proto.DisplayName} can't be trashed (the game does not allow it).");
            yield break;
        }

        var qid = proto.QualifiedItemId;
        var stacks = P.Items.Where(i => i != null && i.QualifiedItemId == qid && Fits(i)).OrderBy(i => i.Quality).ToList();
        int have = stacks.Sum(i => i.Stack);
        int asked = Math.Max(1, I(a, "quantity", 1));
        int qty = Math.Min(asked, have);
        var plan = new List<(Item stack, int n)>();
        int left = qty;
        foreach (var s in stacks)
        {
            if (left <= 0) break;
            int n = Math.Min(s.Stack, left);
            plan.Add((s, n));
            left -= n;
        }

        // The guard: anything an AI would regret losing needs an explicit confirm.
        var risks = new List<string>();
        int value = plan.Sum(p => Math.Max(0, p.stack.sellToStorePrice()) * p.n);
        if (value >= TrashConfirmValue) risks.Add($"worth about {value}g");
        if (plan.Any(p => p.stack.Quality >= 2)) risks.Add("gold or iridium quality");
        if (proto is MeleeWeapon) risks.Add("a weapon");
        if (proto is Ring or Boots or Hat) risks.Add("equipment");
        if (MuseumKind(proto) is { } kind) risks.Add($"a Museum {kind}");
        if (risks.Count > 0 && !TrashFlag(a, "confirm"))
        {
            c.Fail($"Not trashed: {qty}x {proto.DisplayName} is {string.Join(", ", risks)}. Sell it (ship), store it (chest) or give it away; " +
                   "if you really want it gone, call trash again with confirm=true.");
            yield break;
        }

        var before = Snapshot();
        int money = P.Money;
        bool viaGame = true;
        int trashed = 0;
        foreach (var (stack, n) in plan)
        {
            if (!P.Items.Contains(stack)) continue;
            if (n >= stack.Stack)
            {
                viaGame &= TrashViaGame(stack);
                if (P.Items.Contains(stack)) P.removeItemFromInventory(stack);
            }
            else
            {
                var part = stack.getOne();
                part.Stack = n;
                viaGame &= TrashViaGame(part);
                stack.Stack -= n;
            }
            trashed += n;
        }
        yield return Ticks(2);

        if (trashed == 0)
        {
            c.Fail($"Nothing was trashed: the {proto.DisplayName} left your pack first.");
            yield break;
        }
        var refund = P.Money - money;
        var text = $"Trashed {trashed}x {proto.DisplayName}{(refund > 0 ? $" and got {refund}g back" : "")}.";
        if (asked > have) text += $" You only had {have}.";
        if (!viaGame) text += " (The game's trash routine was not available, so no refund was paid.)";
        var diff = Diff(before);
        c.Result = diff.Length > 0 ? text + " " + diff : text;
    }

    /// <summary>The game's Utility.trashItem for one piece (refund, sound, bookkeeping). False when it is missing or throws.</summary>
    private static bool TrashViaGame(Item item)
    {
        try
        {
            var method = AccessTools.Method(typeof(Utility), "trashItem", new[] { typeof(Item) });
            if (method == null) return false;
            method.Invoke(null, new object[] { item });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
