using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// crack_geode: Clint at the Blacksmith breaks geodes open for 25g each. The tool pays and draws the treasure with the
/// game's own Utility.getTreasureFromGeode (what the geode menu uses), so the odds are the game's. It does not click through
/// the GeodeMenu; the menu's animation and sound are skipped.
/// </summary>
public static partial class AgentTools
{
    private const int GeodeFee = 25;

    /// <summary>Qualified ids of what Clint opens: Geode, Frozen Geode, Magma Geode, Omni Geode, Artifact Trove, Golden Coconut.</summary>
    private static readonly string[] GeodeIds = { "(O)535", "(O)536", "(O)537", "(O)749", "(O)275", "(O)791" };

    private static bool IsGeode(Item it) => GeodeIds.Contains(it.QualifiedItemId);

    private static List<ToolDef> GeodeToolDefs() => new()
    {
        new()
        {
            Name = "crack_geode",
            Description = $"Have Clint crack geodes open ({GeodeFee}g each). Go to the Blacksmith (travel_to, check his opening hours) first. " +
                          "item: Geode, Frozen Geode, Magma Geode, Omni Geode, Artifact Trove or Golden Coconut; leave it empty to list the geodes you carry. " +
                          "quantity: how many (default 1). The result lists what came out. Artifacts, minerals and gems belong in the Museum (Gunther), one of each kind, before you sell them.",
            Schema = Obj(new
            {
                item = Str("Geode name; empty lists the geodes you carry"),
                quantity = Int("How many to crack (default 1)"),
                reason = Str(ReasonDesc)
            }),
            InstantFor = a => S(a, "item").Trim().Length == 0,
            Run = CrackGeode
        }
    };

    private static IEnumerator CrackGeode(JsonElement a, CallContext c)
    {
        var want = S(a, "item").Trim();
        var carried = P.Items.Where(i => i != null && IsGeode(i)).ToList();
        if (want.Length == 0)
        {
            c.Result = carried.Count == 0
                ? "You carry no geodes. Mine them (stones in the mines), fish or dig them up, then bring them to Clint at the Blacksmith."
                : $"Geodes you carry: {Stacks(carried)}. Clint cracks one for {GeodeFee}g at the Blacksmith (crack_geode with the name).";
            yield break;
        }

        if (Loc.Name != "Blacksmith")
        {
            c.Fail($"Clint works in the Blacksmith; you are in {Loc.Name}. travel_to Blacksmith first (check his opening hours).");
            yield break;
        }
        if (!Loc.characters.Any(n => n.Name == "Clint"))
        {
            c.Fail("Clint is not here right now (the shop is closed or he is away). Come back during opening hours.");
            yield break;
        }
        int first = FindIn(P.Items, want, IsGeode);
        if (first < 0)
        {
            c.Fail($"No geode called \"{want}\" in your pack. " + (carried.Count == 0 ? "You carry none." : $"You carry: {Stacks(carried)}."));
            yield break;
        }

        int qty = Math.Clamp(I(a, "quantity", 1), 1, 99);
        var qid = P.Items[first].QualifiedItemId;
        var label = P.Items[first].DisplayName;
        var found = new List<(string label, int count)>();
        var museum = new List<string>();
        int cracked = 0, paid = 0;
        string why = "";
        bool countWorks = true;
        for (int n = 0; n < qty; n++)
        {
            int slot = FindItem(label, it => it.QualifiedItemId == qid);
            if (slot < 0) { why = $"no more {label}"; break; }
            if (P.Money < GeodeFee) { why = $"not enough money (Clint charges {GeodeFee}g each)"; break; }
            var stack = P.Items[slot];
            // The treasure comes from the game's table; the roll depends on the geodes-cracked counter.
            Item? treasure = Utility.getTreasureFromGeode(stack.getOne());
            if (treasure == null) { why = "the geode gave nothing"; break; }
            // Check the room before the geode and the money are spent.
            if (!P.couldInventoryAcceptThisItem(treasure)) { why = "your inventory is full"; break; }

            P.Money -= GeodeFee;
            paid += GeodeFee;
            stack.Stack--;
            if (stack.Stack <= 0) P.removeItemFromInventory(stack);
            P.addItemToInventoryBool(treasure);
            cracked++;
            // The counter seeds the next roll: without it every geode in this call would give the same thing.
            if (!CountGeodeCracked()) countWorks = false;

            found.Add((treasure.DisplayName, Math.Max(1, treasure.Stack)));
            if (MuseumKind(treasure) is { } kind)
            {
                var entry = $"{treasure.DisplayName} ({kind})";
                if (!museum.Contains(entry)) museum.Add(entry);
            }
        }
        yield return Ticks(2);

        if (cracked == 0)
        {
            c.Fail($"Could not crack {label}: {why}.");
            yield break;
        }
        var sb = new StringBuilder($"Clint cracked {cracked}x {label} for {paid}g. Found: {Tally(found)}.");
        if (cracked < qty) sb.Append($" Stopped after {cracked} of {qty}: {why}.");
        if (museum.Count > 0) sb.Append($" For the Museum (Gunther takes one of each kind): {string.Join(", ", museum)}.");
        if (!countWorks && cracked > 1) sb.Append(" Note: the geodes-cracked counter could not be advanced, so the finds may repeat.");
        sb.Append($" {P.Money}g left.");
        c.Result = sb.ToString();
    }

    /// <summary>"artifact", "mineral" or "gem" for finds the Museum collects, else null.</summary>
    private static string? MuseumKind(Item it)
    {
        if (it.Category == -2) return "gem";
        if (it.Category == -12) return "mineral";
        return Peek.Text(ItemRegistry.GetData(it.QualifiedItemId), "ObjectType") == "Arch" ? "artifact" : null;
    }

    /// <summary>Stats.GeodesCracked + 1, as the geode menu does after each geode. Reflection keeps a renamed counter from breaking the build.</summary>
    private static bool CountGeodeCracked()
    {
        try
        {
            var prop = AccessTools.Property(typeof(Stats), "GeodesCracked");
            if (prop == null || prop.GetGetMethod(true) == null || prop.GetSetMethod(true) == null) return false;
            var current = Convert.ToUInt64(prop.GetValue(Game1.stats));
            prop.SetValue(Game1.stats, Convert.ChangeType(current + 1, prop.PropertyType));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
