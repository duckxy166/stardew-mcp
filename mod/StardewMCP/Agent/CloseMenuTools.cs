using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// What close_menu does for the player before it closes a menu that would otherwise lose something:
/// a letter's quest is accepted (its attached items are handed over by the letter's own exit), and a one-shot
/// grab menu (fishing treasure, gift, fish pond output) gives up what fits in the inventory. Chests are left alone.
/// </summary>
public static partial class AgentTools
{
    /// <summary>Claims the menu's contents; whatever it did is added to <paramref name="notes"/> for close_menu's result.</summary>
    private static IEnumerator ClaimBeforeClose(IClickableMenu? menu, List<string> notes)
    {
        if (menu is LetterViewerMenu letter)
            AcceptLetterQuest(letter, notes);
        else if (menu is ItemGrabMenu { shippingBin: false, showReceivingMenu: true } grab && grab.source != ItemGrabMenu.source_chest)
            yield return ClaimGrabMenu(notes);
    }

    /// <summary>
    /// The letter's own AcceptQuest, as its "Accept quest" button does. A letter without a quest should do nothing;
    /// if the game throws anyway, the letter still closes and the AI is told.
    /// </summary>
    private static void AcceptLetterQuest(LetterViewerMenu letter, List<string> notes)
    {
        int quests = P.questLog.Count;
        try { letter.AcceptQuest(); }
        catch (Exception ex)
        {
            notes.Add($"Could not accept a quest from the letter ({ex.GetType().Name}).");
            return;
        }
        if (P.questLog.Count > quests)
            notes.Add($"Accepted the quest: {P.questLog[P.questLog.Count - 1].GetName()}.");
    }

    /// <summary>
    /// Takes every item that fits by clicking its slot, the way <c>menu take</c> does, so the game runs the
    /// menu's own grab handler and removes the item from the source. Items that don't fit are named, not lost silently.
    /// </summary>
    private static IEnumerator ClaimGrabMenu(List<string> notes)
    {
        var before = Snapshot();
        var skipped = new List<Item>();
        for (int guard = 0; guard < 80; guard++)
        {
            if (Game1.activeClickableMenu is not ItemGrabMenu now) break;
            var items = now.ItemsToGrabMenu.actualInventory;
            int slot = -1;
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && IndexOf(skipped, items[i]) < 0 && P.couldInventoryAcceptThisItem(items[i])) { slot = i; break; }
            if (slot < 0) break;

            var item = items[slot];
            var cc = now.ItemsToGrabMenu.inventory.FirstOrDefault(s => s.name == slot.ToString());
            if (cc == null) { skipped.Add(item); continue; }
            Click(now, cc);
            yield return null;
            // A click that took nothing must not be repeated forever.
            if (Game1.activeClickableMenu is ItemGrabMenu after && IndexOf(after.ItemsToGrabMenu.actualInventory, item) >= 0)
                skipped.Add(item);
        }

        var got = Diff(before);
        if (got.Length > 0) notes.Add($"Took what the menu held. {got}.");
        if (Game1.activeClickableMenu is ItemGrabMenu rest)
        {
            var left = rest.ItemsToGrabMenu.actualInventory.Where(i => i != null).ToList();
            if (left.Count > 0) notes.Add($"Left behind (no room in your inventory): {Names(left)}.");
        }
    }
}
