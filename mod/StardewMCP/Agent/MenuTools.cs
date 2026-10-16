using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace StardewMCP.Agent;

/// <summary>
/// The menu input. menu() reads the open menu; menu(action) clicks it like a player through a small adapter
/// per menu family. Every action is a click on the menu's own buttons and slots, or typing into its own text
/// box, so the game itself moves the items, warps and pays.
/// </summary>
public static partial class AgentTools
{
    private static ToolDef MenuTool() => new()
    {
        Name = "menu",
        Description = "Read or use the open menu: chests, fridge, shipping bin, fishing treasure, mine elevator, jukebox, number and name entry, and any other menu by its buttons. " +
                      "menu() shows what it holds and which actions work (game time stays frozen). menu(action=...) clicks it like a player: " +
                      "take / put an item between it and your inventory (count: default the whole stack; quality: only that quality), choose an option (a mine floor, a song), " +
                      "amount types a number and presses OK (e.g. money to send), type enters a name and presses OK, ok, cancel, " +
                      "click presses a button that menu() lists by its label.",
        Schema = Obj(new
        {
            action = new { type = "string", @enum = new[] { "take", "put", "choose", "amount", "type", "ok", "cancel", "click" }, description = "Omit to read the menu" },
            item = Str("take/put: item name"),
            count = Int("take/put: how many (default: the whole stack)"),
            quality = Str("take/put: only this quality: normal, silver, gold or iridium"),
            option = Str("choose: an option as menu() lists it, e.g. mine floor \"25\"; click: a button label"),
            value = Int("amount: the number to enter"),
            text = Str("type: the text to enter, e.g. a name"),
            reason = Str(ReasonDesc)
        }),
        InstantFor = a => S(a, "action").Trim().Length == 0,
        Run = Menu
    };

    private static IEnumerator Menu(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu is not { } menu) { c.Fail("No menu is open."); yield break; }
        var action = S(a, "action").Trim().ToLowerInvariant();
        if (action.Length == 0) { c.Result = ReadMenu(menu); yield break; }
        if (menu is DialogueBox or ShopMenu or LetterViewerMenu) { c.Fail(ReadMenu(menu)); yield break; }

        var before = Snapshot();
        IEnumerator? run = (action, menu) switch
        {
            ("take", ItemGrabMenu g) => Take(g, a, c),
            ("put", ItemGrabMenu g) => Put(g, a, c),
            ("choose", MineElevatorMenu m) => ChooseFloor(m, a, c),
            ("choose", ChooseFromListMenu m) => ChooseFromList(m, a, c),
            ("amount", NumberSelectionMenu m) => Amount(m, a, c),
            ("type", NamingMenu m) => TypeName(m, a, c),
            ("ok", _) => PressOk(menu, c),
            ("cancel", _) => Cancel(menu, c),
            ("click", _) => ClickLabel(menu, a, c),
            _ => null
        };
        if (run == null) { c.Fail($"\"{action}\" doesn't work in {MenuName(menu)}. {Actions(menu)}"); yield break; }
        yield return run;
        if (!c.Ok) yield break;
        yield return Ticks(10);
        var diff = Diff(before);
        var now = Game1.activeClickableMenu is { } open ? ReadMenu(open) : "The menu is closed.";
        c.Result = $"{c.Result} {diff}".Trim() + "\n" + now;
    }

    // ------------------------------------------------------------------ reading

    private static string ReadMenu(IClickableMenu menu)
    {
        if (menu is DialogueBox or ShopMenu or LetterViewerMenu)
            return WorldText.MenuState().Trim(); // these have their own tools

        var sb = new StringBuilder($"Menu: {MenuName(menu)}\n");
        var r = Rt.Helper.Reflection;
        switch (menu)
        {
            case ItemGrabMenu g:
                if (g.shippingBin)
                    sb.AppendLine($"Last shipped (take can get it back): {Names(new[] { Game1.getFarm().lastItemShipped })}");
                else if (g.showReceivingMenu)
                    sb.AppendLine($"Inside ({g.ItemsToGrabMenu.actualInventory.Count(i => i != null)}/{g.ItemsToGrabMenu.capacity} slots): {Names(g.ItemsToGrabMenu.actualInventory)}");
                if (g.behaviorFunction != null)
                    sb.AppendLine($"You can put in: {Names(P.Items.Where(i => i != null && g.inventory.highlightMethod(i)))}");
                break;
            case MineElevatorMenu m:
                sb.AppendLine($"Floors: {string.Join(", ", m.elevators.Select(e => e.name))} (0 is the mine entrance; you are on floor {Game1.CurrentMineLevel})");
                break;
            case ChooseFromListMenu m:
                var labels = ListLabels(m);
                sb.AppendLine($"Options: {string.Join(" | ", labels)} (showing: {labels.ElementAtOrDefault(r.GetField<int>(m, "index").GetValue())})");
                break;
            case NumberSelectionMenu m:
                int price = r.GetField<int>(m, "price").GetValue();
                sb.AppendLine($"\"{r.GetField<string>(m, "message").GetValue()}\" Now {r.GetField<int>(m, "currentValue").GetValue()}, " +
                              $"allowed {r.GetField<int>(m, "minValue").GetValue()}-{r.GetField<int>(m, "maxValue").GetValue()}{(price > 0 ? $", {price}g each" : "")}");
                break;
            case NamingMenu m:
                sb.AppendLine($"\"{m.title}\" The name box says \"{m.textBox.Text}\"");
                break;
            default:
                var buttons = Buttons(menu).Select(b => b.text).Distinct().ToList();
                sb.AppendLine($"Buttons: {(buttons.Count > 0 ? string.Join(", ", buttons) : "none with a label")}");
                break;
        }
        sb.Append(Actions(menu));
        return sb.ToString();
    }

    internal static string MenuName(IClickableMenu menu)
    {
        var name = menu switch
        {
            ItemGrabMenu { shippingBin: true } => "Shipping bin",
            ItemGrabMenu { context: Chest { fridge.Value: true } } => "Fridge",
            ItemGrabMenu { context: Chest ch } => ch.DisplayName,
            ItemGrabMenu { source: ItemGrabMenu.source_fishingChest } => "Fishing treasure",
            ItemGrabMenu { source: ItemGrabMenu.source_gift } => "Gift",
            ItemGrabMenu { context: Item it } => it.DisplayName,
            MineElevatorMenu => "Mine elevator",
            ChooseFromListMenu => "Jukebox",
            NumberSelectionMenu => "Number entry",
            NamingMenu => "Naming",
            _ => null
        };
        var type = menu.GetType().Name;
        return name == null ? type : $"{name} ({type})";
    }

    private static string Actions(IClickableMenu menu)
    {
        var list = menu switch
        {
            ItemGrabMenu g => new[] { g.showReceivingMenu || g.shippingBin ? "take(item, count?)" : "", g.behaviorFunction != null ? "put(item, count?)" : "" },
            MineElevatorMenu or ChooseFromListMenu => new[] { "choose(option)" },
            NumberSelectionMenu => new[] { "amount(value)" },
            NamingMenu => new[] { "type(text)" },
            _ => new[] { "click(option)" }
        };
        var ok = menu is NamingMenu || Field(menu, "okButton") != null ? "ok" : "";
        return "Actions: menu(action=...) " + string.Join(", ", list.Append(ok).Append("cancel").Where(s => s.Length > 0));
    }

    private static string Names(IEnumerable<Item?> items)
    {
        var names = items.Where(i => i != null).Select(i => $"{i!.DisplayName} x{i.Stack}").ToList();
        return names.Count == 0 ? "nothing" : string.Join(", ", names);
    }

    // ------------------------------------------------------------------ clicking

    private static void Click(IClickableMenu menu, ClickableComponent cc, bool right = false)
    {
        var p = cc.bounds.Center;
        if (right) menu.receiveRightClick(p.X, p.Y);
        else menu.receiveLeftClick(p.X, p.Y);
    }

    private static ClickableComponent? Field(IClickableMenu menu, string name) =>
        menu.GetType().GetField(name)?.GetValue(menu) as ClickableComponent;

    /// <summary>The menu's visible buttons with a readable label (inventory slots and other number-only names left out).</summary>
    private static List<(ClickableComponent cc, string text)> Buttons(IClickableMenu menu)
    {
        menu.populateClickableComponentList();
        return (menu.allClickableComponents ?? new List<ClickableComponent>())
            .Where(cc => cc.visible)
            .Select(cc => (cc, text: new[] { cc.label, (cc as ClickableTextureComponent)?.hoverText, cc.name }
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.All(char.IsDigit))?.Trim() ?? ""))
            .Where(b => b.text.Length > 0)
            .ToList();
    }

    private static IEnumerator PressOk(IClickableMenu menu, CallContext c)
    {
        var ok = menu is NamingMenu nm ? nm.doneNamingButton : Field(menu, "okButton");
        if (ok == null) { c.Fail($"{MenuName(menu)} has no OK button. {Actions(menu)}"); yield break; }
        Click(menu, ok);
        yield return Ticks(10);
        c.Result = "Pressed OK.";
    }

    /// <summary>The menu's cancel button, else the menu key (Escape), as a player closes it.</summary>
    private static IEnumerator Cancel(IClickableMenu menu, CallContext c)
    {
        if (Field(menu, "cancelButton") is { } cancel) Click(menu, cancel);
        else menu.receiveKeyPress(Keys.Escape);
        yield return Ticks(10);
        if (Game1.activeClickableMenu == menu) { c.Fail($"{MenuName(menu)} stayed open. {Actions(menu)}"); yield break; }
        c.Result = "Cancelled.";
    }

    private static IEnumerator ClickLabel(IClickableMenu menu, JsonElement a, CallContext c)
    {
        var want = S(a, "option").Trim();
        var buttons = Buttons(menu);
        var hit = buttons.FirstOrDefault(b => b.text.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (hit.cc == null && want.Length > 0) hit = buttons.FirstOrDefault(b => b.text.Contains(want, StringComparison.OrdinalIgnoreCase));
        if (hit.cc == null) { c.Fail($"No button \"{want}\". Buttons: {string.Join(", ", buttons.Select(b => b.text).Distinct())}"); yield break; }
        Click(menu, hit.cc);
        yield return Ticks(10);
        c.Result = $"Clicked {hit.text}.";
    }

    // ------------------------------------------------------------------ chests, fridge, shipping bin, treasure

    /// <summary>
    /// Count: the whole stack is one left click, fewer are one right click each (one item per click). A chest
    /// rebuilds its menu after every click, so the menu and the slot are looked up again each time.
    /// </summary>
    private static IEnumerator Take(ItemGrabMenu g, JsonElement a, CallContext c)
    {
        var name = S(a, "item");
        if (g.shippingBin)
        {
            var last = Game1.getFarm().lastItemShipped;
            if (last == null || FindIn(new List<Item> { last }, name) < 0) { c.Fail($"Only the last item shipped can be taken back ({Names(new[] { last })})."); yield break; }
            if (!P.couldInventoryAcceptThisItem(last)) { c.Fail("Your inventory is full."); yield break; }
            Click(g, g.lastShippedHolder);
            c.Result = $"Took back {last.Stack} x {last.DisplayName}.";
            yield break;
        }
        if (!g.showReceivingMenu) { c.Fail($"Nothing can be taken from {MenuName(g)}."); yield break; }

        int i = FindIn(g.ItemsToGrabMenu.actualInventory, name, QualityIs(a));
        if (i < 0) { c.Fail($"No \"{name}\" in {MenuName(g)}. Inside: {Names(g.ItemsToGrabMenu.actualInventory)}"); yield break; }
        var item = g.ItemsToGrabMenu.actualInventory[i];
        int count = Has(a, "count") ? Math.Clamp(I(a, "count", item.Stack), 1, item.Stack) : item.Stack;
        bool whole = count == item.Stack;
        int had = Snapshot().GetValueOrDefault(item.DisplayName);
        for (int k = 0; k < (whole ? 1 : count); k++)
        {
            if (Game1.activeClickableMenu is not ItemGrabMenu now) break;
            int slot = IndexOf(now.ItemsToGrabMenu.actualInventory, item);
            var cc = now.ItemsToGrabMenu.inventory.FirstOrDefault(s => s.name == slot.ToString());
            if (slot < 0 || cc == null) break;
            var one = item.getOne();
            if (!P.couldInventoryAcceptThisItem(whole ? item : one)) break;
            Click(now, cc, right: !whole);
            yield return null;
        }
        int took = Snapshot().GetValueOrDefault(item.DisplayName) - had;
        if (took <= 0) { c.Fail($"Nothing was taken (is your inventory full?). Inside: {Names(g.ItemsToGrabMenu.actualInventory)}"); yield break; }
        c.Result = $"Took {took} x {item.DisplayName}{(took < count ? $" ({count} asked; your inventory is full)" : "")}.";
    }

    private static IEnumerator Put(ItemGrabMenu g, JsonElement a, CallContext c)
    {
        if (g.behaviorFunction == null) { c.Fail($"Nothing can be put into {MenuName(g)}; take items or press ok."); yield break; }
        var name = S(a, "item");
        int slot = FindItem(name, QualityIs(a));
        if (slot < 0) { c.Fail($"No \"{name}\" in your inventory."); yield break; }
        var item = P.Items[slot];
        if (!g.inventory.highlightMethod(item)) { c.Fail($"{item.DisplayName} can't go into {MenuName(g)}."); yield break; }
        int count = Has(a, "count") ? Math.Clamp(I(a, "count", item.Stack), 1, item.Stack) : item.Stack;
        bool whole = count == item.Stack;
        int had = Snapshot().GetValueOrDefault(item.DisplayName);
        for (int k = 0; k < (whole ? 1 : count); k++)
        {
            if (Game1.activeClickableMenu is not ItemGrabMenu now || P.Items[slot] != item) break;
            var cc = now.inventory.inventory.FirstOrDefault(s => s.name == slot.ToString());
            if (cc == null) { c.Fail($"Slot {slot} is not shown in this menu."); yield break; }
            Click(now, cc, right: !whole);
            yield return null;
        }
        int put = had - Snapshot().GetValueOrDefault(item.DisplayName);
        if (put <= 0) { c.Fail($"{item.DisplayName} did not go in (is {MenuName(g)} full?)."); yield break; }
        c.Result = $"Put {put} x {item.DisplayName} into {MenuName(g)}{(put < count ? $" ({count} asked; it is full)" : "")}.";
    }

    private static int IndexOf(IList<Item> items, Item item)
    {
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }

    // ------------------------------------------------------------------ choosers

    private static IEnumerator ChooseFloor(MineElevatorMenu m, JsonElement a, CallContext c)
    {
        var want = new string(S(a, "option").Where(char.IsDigit).ToArray());
        var floor = m.elevators.FirstOrDefault(e => e.name == want);
        if (floor == null) { c.Fail($"No floor \"{S(a, "option")}\". Floors: {string.Join(", ", m.elevators.Select(e => e.name))}"); yield break; }
        var from = Loc;
        Click(m, floor);
        yield return Ticks(30);
        yield return WaitIdle();
        c.Result = Loc != from ? $"Rode the elevator to floor {Game1.CurrentMineLevel} ({Loc.Name})." : $"The elevator did not move (floor {want}).";
    }

    private static List<string> ListLabels(ChooseFromListMenu m)
    {
        var r = Rt.Helper.Reflection;
        bool jukebox = r.GetField<bool>(m, "isJukebox").GetValue();
        return r.GetField<List<string>>(m, "options").GetValue().Select(o => jukebox ? Utility.getSongTitleFromCueName(o) : o).ToList();
    }

    /// <summary>Step the list with its arrow button until the option shows, then press OK.</summary>
    private static IEnumerator ChooseFromList(ChooseFromListMenu m, JsonElement a, CallContext c)
    {
        var labels = ListLabels(m);
        var want = S(a, "option").Trim();
        int target = labels.FindIndex(l => l.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (target < 0 && want.Length > 0) target = labels.FindIndex(l => l.Contains(want, StringComparison.OrdinalIgnoreCase));
        if (target < 0) { c.Fail($"No option \"{want}\". Options: {string.Join(" | ", labels)}"); yield break; }
        var index = Rt.Helper.Reflection.GetField<int>(m, "index");
        for (int k = 0; k < labels.Count && index.GetValue() != target; k++)
            Click(m, m.forwardButton);
        Click(m, m.okButton);
        yield return Ticks(10);
        c.Result = $"Chose {labels[target]}.";
    }

    // ------------------------------------------------------------------ text entry

    /// <summary>Type through the box's own keyboard handlers; the gamepad on-screen keyboard (TextEntryMenu) is closed first.</summary>
    private static IEnumerator TypeInto(TextBox box, string text)
    {
        if (Game1.textEntry != null) Game1.closeTextEntry();
        box.SelectMe();
        for (int i = box.Text.Length; i > 0; i--) box.RecieveCommandInput('\b');
        foreach (var ch in text) box.RecieveTextInput(ch);
        yield return null; // the menu's update() reads the box
    }

    private static IEnumerator Amount(NumberSelectionMenu m, JsonElement a, CallContext c)
    {
        var r = Rt.Helper.Reflection;
        int min = r.GetField<int>(m, "minValue").GetValue(), max = r.GetField<int>(m, "maxValue").GetValue();
        int price = r.GetField<int>(m, "price").GetValue();
        int value = I(a, "value", int.MinValue);
        if (value < min || value > max) { c.Fail($"value must be {min}-{max}."); yield break; }
        if (price > 0 && (long)price * value > P.Money) { c.Fail($"{value} x {price}g is more than your {P.Money}g."); yield break; }

        yield return TypeInto(r.GetField<TextBox>(m, "numberSelectedBox").GetValue(), value.ToString());
        int shown = r.GetField<int>(m, "currentValue").GetValue();
        if (shown != value) { c.Fail($"The box shows {shown} instead of {value}."); yield break; }
        yield return PressOk(m, c);
        if (c.Ok) c.Result = $"Entered {value} and pressed OK.";
    }

    private static IEnumerator TypeName(NamingMenu m, JsonElement a, CallContext c)
    {
        var text = S(a, "text").Trim();
        if (text.Length < m.minLength) { c.Fail("text is empty."); yield break; }
        yield return TypeInto(m.textBox, text);
        var typed = m.textBox.Text;
        yield return PressOk(m, c);
        if (c.Ok) c.Result = $"Typed \"{typed}\" and pressed OK.";
    }
}
