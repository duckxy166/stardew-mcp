using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// special_orders: the Special Orders board in Pelican Town. The board is opened with the game's own tile
/// action (press the action button on its tile) and an order is accepted by clicking the board's own Accept
/// button, so the game does its own bookkeeping. Order details (name, requester, objectives, rewards) are read
/// by reflection: the SpecialOrder type is not named here, so a renamed member shows up as "?" instead of
/// breaking the build.
/// <para>The accepted orders are reachable for a future quests tool (#14) through
/// <see cref="ActiveSpecialOrdersText"/>.</para>
/// </summary>
public static partial class AgentTools
{
    private static ToolDef SpecialOrdersTool() => new()
    {
        Name = "special_orders",
        Description = "The Special Orders board in Pelican Town (it opens in the fall of year 1): timed jobs from villagers that pay a reward. " +
                      "action=list walks to the board, opens it and shows the orders posted now: requester, what they want, reward and time limit. " +
                      "action=accept takes one by its index (0 = left, 1 = right); an accepted order is a job for the whole team. " +
                      "action=active shows the orders already accepted, with progress and days left (no walking). Leave the board with close_menu.",
        Schema = Obj(new
        {
            action = new { type = "string", @enum = new[] { "list", "accept", "active" }, description = "Default list" },
            index = Int("accept: the order's index as list shows it"),
            reason = Str(ReasonDesc)
        }),
        InstantFor = a => S(a, "action").Trim().Equals("active", StringComparison.OrdinalIgnoreCase),
        Run = SpecialOrdersRun
    };

    private static IEnumerator SpecialOrdersRun(JsonElement a, CallContext c)
    {
        var action = S(a, "action").Trim().ToLowerInvariant();
        if (action.Length == 0) action = "list";
        if (action == "active") { c.Result = ActiveSpecialOrdersText(); yield break; }
        if (action != "list" && action != "accept") { c.Fail("action must be list, accept or active."); yield break; }
        int index = I(a, "index", -1);
        if (action == "accept" && index < 0) { c.Fail("accept needs the order's index (0 or 1); see special_orders(action=list)."); yield break; }

        yield return SoOpenBoard(c);
        if (!c.Ok) yield break;
        var board = Game1.activeClickableMenu!;
        yield return Ticks(5);

        if (action == "list")
        {
            c.Result = SoBoardText(board);
            yield break;
        }

        var orders = SoBoardOrders(board);
        if (index >= orders.Count || orders[index] == null) { c.Fail($"No order at index {index}. {SoBoardText(board)}"); yield break; }
        var order = orders[index]!;
        var key = SoTxt(order, "questKey");
        if (SoActiveOrders().Any(o => SoTxt(o, "questKey") == key)) { c.Fail("That order is already active. See special_orders(action=active)."); yield break; }

        var button = SoAcceptButton(board, index);
        if (button == null) { c.Fail("The Accept button was not found on this board. Try menu() and menu(action=click, option=...)."); yield break; }
        if (!button.visible)
        {
            c.Fail("The board is not offering Accept for this order right now, usually because an order from this board was already taken (by you or a teammate).");
            yield break;
        }

        var summary = SoDescribe(order, posted: true);
        Click(board, button);
        for (int i = 0; i < 180 && !SoActiveOrders().Any(o => SoTxt(o, "questKey") == key); i++) yield return null;
        if (!SoActiveOrders().Any(o => SoTxt(o, "questKey") == key))
        {
            c.Fail("Pressed Accept, but the order did not become active. Check special_orders(action=active).");
            yield break;
        }
        c.Result = $"Accepted {summary}\nIt is now a job for the whole team. Track it with special_orders(action=active). The board is still open: close_menu to leave.";
    }

    // ------------------------------------------------------------------ opening the board

    /// <summary>Walks to the board tile in Town and presses the action button on it (the game's own SpecialOrders tile action).</summary>
    private static IEnumerator SoOpenBoard(CallContext c)
    {
        if (Game1.activeClickableMenu is SpecialOrdersBoard) yield break;
        if (Game1.activeClickableMenu != null || Game1.dialogueUp)
        {
            c.Fail("Close the open menu or dialogue first (close_menu), then try again.");
            yield break;
        }
        // Look at the Town map before walking there: no tile means the board has not opened yet.
        if (SoBoardTile(Game1.getLocationFromName("Town") ?? Loc) == null)
        {
            c.Fail("There is no Special Orders board in Town yet. It opens in the fall of year 1 (around day 58).");
            yield break;
        }
        yield return WaitIdle();
        if (Loc.Name != "Town")
        {
            var trip = new CallContext();
            yield return TravelTo("Town", trip);
            if (!trip.Ok) { c.Fail($"Couldn't get to Town, where the board is: {trip.Result}"); yield break; }
        }

        var tile = SoBoardTile(Loc);
        if (tile == null) { c.Fail("The Special Orders board tile was not found in Town."); yield break; }
        var walk = new CallContext();
        yield return WalkAdjacent(tile.Value, walk);
        if (!walk.Ok) { c.Fail($"Can't reach the board at ({tile.Value.X},{tile.Value.Y}): {walk.Result}"); yield break; }

        PressAction(tile.Value);
        for (int i = 0; i < 60 && Game1.activeClickableMenu is not SpecialOrdersBoard; i++) yield return null;
        if (Game1.activeClickableMenu is not SpecialOrdersBoard)
            c.Fail($"Pressed the board at ({tile.Value.X},{tile.Value.Y}) but it did not open.");
    }

    /// <summary>The board's action tile nearest to the player: Action property "SpecialOrders..." (not Qi's board).</summary>
    private static Point? SoBoardTile(GameLocation loc)
    {
        var me = P.TilePoint;
        var layer = loc.Map.Layers[0];
        Point? best = null;
        int bestD = int.MaxValue;
        for (int x = 0; x < layer.LayerWidth; x++)
        for (int y = 0; y < layer.LayerHeight; y++)
        {
            var act = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
            if (act == null || !act.StartsWith("SpecialOrders", StringComparison.OrdinalIgnoreCase) || act.Contains("Qi", StringComparison.OrdinalIgnoreCase))
                continue;
            int d = Math.Abs(x - me.X) + Math.Abs(y - me.Y);
            if (d < bestD) { bestD = d; best = new Point(x, y); }
        }
        return best;
    }

    // ------------------------------------------------------------------ reading the board

    /// <summary>The orders the open board shows: [left, right], null when empty. Falls back to the team's posted orders.</summary>
    private static List<object?> SoBoardOrders(IClickableMenu board)
    {
        var left = SoMem(board, "_leftOrder") ?? SoMem(board, "leftOrder");
        var right = SoMem(board, "_rightOrder") ?? SoMem(board, "rightOrder");
        if (left == null && right == null)
        {
            var type = SoTxt(board, "_boardType") + SoTxt(board, "boardType");
            var posted = ((IEnumerable)Game1.player.team.availableSpecialOrders).Cast<object>()
                .Where(o => SoTxt(o, "orderType") == type).Take(2).ToList();
            left = posted.ElementAtOrDefault(0);
            right = posted.ElementAtOrDefault(1);
        }
        return new List<object?> { left, right };
    }

    private static ClickableComponent? SoAcceptButton(IClickableMenu board, int index)
    {
        if (SoMem(board, index == 0 ? "acceptLeftQuestButton" : "acceptRightQuestButton") is ClickableComponent named) return named;
        return Buttons(board).Where(b => b.text.Contains("accept", StringComparison.OrdinalIgnoreCase))
            .Select(b => b.cc).OrderBy(cc => cc.bounds.X).ElementAtOrDefault(index);
    }

    private static string SoBoardText(IClickableMenu board)
    {
        var orders = SoBoardOrders(board);
        if (orders.All(o => o == null))
            return "The Special Orders board is open but no orders are posted right now; new ones appear as the weeks pass. close_menu to leave.";
        var sb = new StringBuilder("Special Orders board. An accepted order is a job for the whole team.\n");
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i] == null) continue;
            var button = SoAcceptButton(board, i);
            var offer = button == null ? "" : button.visible ? " [Accept available]" : " [Accept not offered right now]";
            sb.AppendLine($"index {i} ({(i == 0 ? "left" : "right")}){offer}: {SoDescribe(orders[i]!, posted: true)}");
        }
        sb.Append("Take one with special_orders(action=accept, index=N), or leave with close_menu.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ active orders (for quests, #14)

    /// <summary>The team's accepted special orders that are still running.</summary>
    private static List<object> SoActiveOrders()
    {
        var all = ((IEnumerable)Game1.player.team.specialOrders).Cast<object>();
        return all.Where(o => { var s = SoTxt(o, "questState"); return s.Length == 0 || s == "InProgress"; }).ToList();
    }

    /// <summary>
    /// Text for every accepted special order with objectives, progress, reward and days left. Public so the
    /// quests tool (#14) can include it.
    /// </summary>
    public static string ActiveSpecialOrdersText()
    {
        List<object> active;
        try { active = SoActiveOrders(); }
        catch (Exception ex) { return $"Could not read the special orders: {ex.Message}"; }
        if (active.Count == 0) return "You have no accepted special orders. The board in Town (special_orders action=list) posts new ones.";
        var sb = new StringBuilder("Active special orders (shared by the whole team):");
        foreach (var o in active) sb.Append("\n  ").Append(SoDescribe(o, posted: false));
        return sb.ToString();
    }

    private static string SoStatusLine()
    {
        var active = SoActiveOrders();
        if (active.Count == 0) return "";
        var parts = active.Select(o =>
        {
            var left = SoDaysLeft(o);
            return $"{SoName(o)}{(left != null ? $" ({left} days left)" : "")}";
        });
        return $"Special orders: {string.Join(" | ", parts)} (details: special_orders action=active)";
    }

    // ------------------------------------------------------------------ describing one order

    private static string SoName(object order) =>
        (SoSafe(() => SoCall(order, "GetName")?.ToString()) is { Length: > 0 } n ? n : SoTxt(order, "questName"));

    private static int? SoDaysLeft(object order) => SoSafe(() => SoCall(order, "GetDaysLeft") as int?);

    private static string SoDescribe(object order, bool posted)
    {
        var sb = new StringBuilder($"\"{SoName(order)}\"");
        var requester = SoTxt(order, "requester");
        if (requester.Length > 0) sb.Append($" from {requester}");
        var days = SoDaysLeft(order);
        var duration = (SoVal(SoMem(order, "questDuration")) ?? SoVal(SoMem(order, "duration")))?.ToString();
        if (days != null) sb.Append(posted ? $" · {days} days to finish" : $" · {days} days left");
        if (posted && !string.IsNullOrEmpty(duration)) sb.Append($" ({duration})");
        sb.Append($" · reward: {SoRewards(order)}");
        var desc = SoSafe(() => SoCall(order, "GetDescription")?.ToString()) ?? "";
        desc = desc.Replace("\n", " ").Replace("^", " ").Trim();
        if (desc.Length > 0) sb.Append("\n    ").Append(desc);
        foreach (var line in SoObjectives(order)) sb.Append("\n    - ").Append(line);
        return sb.ToString();
    }

    private static List<string> SoObjectives(object order)
    {
        var lines = new List<string>();
        if (SoMem(order, "objectives") is not IEnumerable objectives) return lines;
        foreach (var obj in objectives)
        {
            if (obj == null) continue;
            var text = SoSafe(() => SoCall(obj, "GetDescription")?.ToString()) ?? obj.GetType().Name;
            var cur = SoVal(SoMem(obj, "currentCount"));
            var max = SoVal(SoMem(obj, "maxCount"));
            lines.Add(max != null ? $"{text} ({cur ?? 0}/{max})" : text);
        }
        return lines;
    }

    private static string SoRewards(object order)
    {
        var parts = new List<string>();
        int money = SoSafe(() => SoCall(order, "GetMoneyReward") is int m ? m : 0);
        if (money > 0) parts.Add($"{money}g");
        if (SoMem(order, "rewards") is IEnumerable rewards)
        {
            foreach (var r in rewards)
            {
                if (r == null) continue;
                var kind = r.GetType().Name.Replace("Reward", "");
                switch (kind)
                {
                    case "Money":
                        if (money <= 0 && SoVal(SoMem(r, "amount")) is { } amount) parts.Add($"{amount}g");
                        break;
                    case "Item":
                        parts.Add(SoVal(SoMem(r, "item")) is Item it ? $"{it.DisplayName} x{it.Stack}" : "an item");
                        break;
                    case "Gems":
                        parts.Add($"{SoVal(SoMem(r, "amount"))} Qi Gems");
                        break;
                    case "Friendship":
                        parts.Add($"friendship with {SoTxt(r, "targetName")}");
                        break;
                    default:
                        parts.Add(kind);
                        break;
                }
            }
        }
        return parts.Count == 0 ? "?" : string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ reflection helpers

    /// <summary>A field or property by name, any visibility, searching base types; null when missing.</summary>
    private static object? SoMem(object o, string name)
    {
        var t = o.GetType();
        return AccessTools.Field(t, name)?.GetValue(o) ?? AccessTools.Property(t, name)?.GetValue(o);
    }

    /// <summary>The .Value of a net field (NetString, NetInt, NetEnum, NetRef...); anything else is returned as it is.</summary>
    private static object? SoVal(object? o)
    {
        if (o == null) return null;
        var p = o.GetType().GetProperties(EqAll).FirstOrDefault(x => x.Name == "Value" && x.GetIndexParameters().Length == 0);
        return p != null ? p.GetValue(o) : o;
    }

    private static string SoTxt(object o, string name) => SoVal(SoMem(o, name))?.ToString() ?? "";

    /// <summary>Calls a parameterless method by name; null when it does not exist.</summary>
    private static object? SoCall(object o, string method) =>
        o.GetType().GetMethod(method, EqAll, null, Type.EmptyTypes, null)?.Invoke(o, null);

    private static T SoSafe<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default!; }
    }
}
