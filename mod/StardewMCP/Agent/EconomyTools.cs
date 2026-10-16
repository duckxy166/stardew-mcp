using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// The shared-farm economy: <c>claim</c> / <c>unclaim</c> (who owns which chest, machine and plot, see
/// <see cref="Ownership"/>) and <c>pay</c>, which is a thin macro over the game's own money transfer in Lewis's ledger book.
/// </summary>
public static partial class AgentTools
{
    private static List<ToolDef> Economy() => new()
    {
        new()
        {
            Name = "claim",
            Description = "Claim something as yours on the map you are in: a chest or machine (give its tile) or an area of ground such as your plot (x, y is the top-left corner, with width and height). " +
                          "Everyone sees the owner in look. Others can still take from it (the game allows that), but it is logged as theft. Claims cannot overlap another player's.",
            Schema = Obj(new
            {
                x = Int("Tile x (top-left corner for an area)"),
                y = Int("Tile y"),
                width = Int($"Area width in tiles, 1-{Ownership.MaxSide} (default 1)"),
                height = Int($"Area height in tiles, 1-{Ownership.MaxSide} (default 1)")
            }, "x", "y"),
            Instant = true, AppendStatus = false, Run = Claim
        },
        new()
        {
            Name = "unclaim",
            Description = "Give up your claim on whatever of yours covers this tile (a chest, a machine or the area it lies in).",
            Schema = Obj(new { x = Int("Tile x"), y = Int("Tile y") }, "x", "y"),
            Instant = true, AppendStatus = false, Run = Unclaim
        },
        new()
        {
            Name = "pay",
            Description = "Pay another player from your own wallet (needs separate wallets). You walk to the ledger book in Lewis's house (Mayor's Manor) and use its Send money option, " +
                          "so it takes game time. The recipient is told with your reason, and the viewers see both wallets before and after.",
            Schema = Obj(new
            {
                to = Str("Player id (p1, p2...) or name"),
                amount = Int("Gold to send"),
                reason = Str($"What the payment is for, one short sentence IN {Lang.ToUpperInvariant()} (the recipient and the viewers see it)")
            }, "to", "amount", "reason"),
            Run = Pay
        }
    };

    private static IEnumerator Claim(JsonElement a, CallContext c)
    {
        if (!Has(a, "x") || !Has(a, "y")) { c.Fail("x and y are required."); yield break; }
        int x = I(a, "x", -1), y = I(a, "y", -1), w = I(a, "width", 1), h = I(a, "height", 1);
        if (!Ownership.TryClaim(Rt.Log.Player, Loc, x, y, w, h, Ownership.SourceClaim, out var message)) { c.Fail(message); yield break; }
        Rt.Log.Add("claim", text: message, args: new { x, y, width = Math.Clamp(w, 1, Ownership.MaxSide), height = Math.Clamp(h, 1, Ownership.MaxSide), location = Loc.Name });
        c.Result = message;
    }

    private static IEnumerator Unclaim(JsonElement a, CallContext c)
    {
        if (!Has(a, "x") || !Has(a, "y")) { c.Fail("x and y are required."); yield break; }
        int x = I(a, "x", -1), y = I(a, "y", -1);
        if (!Ownership.Release(Rt.Log.Player, Loc, x, y, out var message)) { c.Fail(message); yield break; }
        Rt.Log.Add("claim", text: message, args: new { x, y, released = true, location = Loc.Name });
        c.Result = message;
    }

    // ------------------------------------------------------------------ pay

    /// <summary>The game's money transfer: ledger book, Send money, the recipient, then the amount.</summary>
    private static IEnumerator Pay(JsonElement a, CallContext c)
    {
        var to = SocialTools.Target(S(a, "to"), c);
        if (to == null) yield break;
        int amount = I(a, "amount", 0);
        var memo = Reason(a).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (amount < 1) { c.Fail("amount must be at least 1."); yield break; }
        if (!P.team.useSeparateWallets.Value)
        {
            c.Fail("Wallets are shared on this farm, so there is nothing to pay: everyone spends the same money. (Separate wallets are switched on at Lewis's ledger book and start overnight.)");
            yield break;
        }
        if (amount > P.Money) { c.Fail($"You only have {P.Money}g."); yield break; }
        if (Game1.activeClickableMenu != null) { c.Fail("Close the open menu first (close_menu)."); yield break; }

        var recipient = SocialTools.NameOf(to);
        var other = Game1.getOnlineFarmers().FirstOrDefault(f => f.Name == recipient);
        if (recipient == null || other == null) { c.Fail($"{SocialTools.Label(to)} is not in the world right now."); yield break; }
        int myBefore = P.Money, theirBefore = other.Money;

        if (Loc is not ManorHouse)
        {
            var trip = new CallContext();
            yield return TravelTo("ManorHouse", trip);
            if (!trip.Ok) { c.Fail($"Couldn't get to Lewis's ledger book: {trip.Result}"); yield break; }
        }
        var book = FindLedger(Loc);
        if (book == null) { c.Fail($"No ledger book in {Loc.Name}."); yield break; }

        var send = new CallContext();
        yield return LedgerSend(book.Value, recipient, amount, send);
        if (!send.Ok)
        {
            CloseOpenMenu();
            c.Fail(send.Result);
            yield break;
        }

        // The game moves the money at once; the recipient's wallet reaches this screen over the game's network.
        for (int i = 0; i < 120 && (P.Money > myBefore - amount || other.Money < theirBefore + amount); i++)
            yield return null;
        int myAfter = P.Money, theirAfter = other.Money;
        if (myAfter > myBefore - amount)
        {
            CloseOpenMenu();
            c.Fail($"The ledger did not take the money (you still have {myAfter}g). Nothing was paid.");
            yield break;
        }

        var label = SocialTools.Label(to);
        var note = memo.Length > 0 ? $" ({memo})" : "";
        Rt.Log.Add("pay", text: $"{amount}g to {label}{note}",
            args: new { to = to.Id, amount, reason = memo, payer = new { before = myBefore, after = myAfter }, payee = new { before = theirBefore, after = theirAfter } });
        SocialTools.Deliver(to, $"[{WorldText.FormatTime(Game1.timeOfDay)}] {SocialTools.Label(Players.ById(Rt.Log.Player)!)} paid you {amount}g{note}.");
        c.Result = $"Paid {amount}g to {label}{note}. You: {myBefore}g → {myAfter}g. {label}: {theirBefore}g → {theirAfter}g" +
                   (theirAfter < theirBefore + amount ? " (their wallet may still be syncing)." : ".");
    }

    private static Point? FindLedger(GameLocation loc)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (loc.doesTileHaveProperty(x, y, "Action", "Buildings") == "LedgerBook")
                    return new Point(x, y);
        return null;
    }

    /// <summary>Walk the ledger's dialogues like a player: Send money, the recipient's name, then type the amount.</summary>
    private static IEnumerator LedgerSend(Point book, string recipient, int amount, CallContext c)
    {
        var sub = new CallContext();
        yield return Interact(Args(new { x = book.X, y = book.Y }), sub);
        if (!sub.Ok) { c.Fail($"Couldn't use the ledger book: {sub.Result}"); yield break; }

        int option = QuestionOption("send", exact: false);
        if (option < 0) { c.Fail($"The ledger offers no Send money option. {QuestionOptions()}"); yield break; }
        sub = new CallContext();
        yield return Respond(Args(new { option }), sub);
        if (!sub.Ok) { c.Fail($"Couldn't choose Send money: {sub.Result}"); yield break; }

        option = QuestionOption(recipient, exact: true);
        if (option < 0) option = QuestionOption(recipient, exact: false);
        if (option < 0) { c.Fail($"{recipient} is not offered as a recipient. {QuestionOptions()}"); yield break; }
        sub = new CallContext();
        yield return Respond(Args(new { option }), sub);
        if (!sub.Ok) { c.Fail($"Couldn't choose {recipient}: {sub.Result}"); yield break; }

        for (int i = 0; i < 60 && Game1.activeClickableMenu is not NumberSelectionMenu; i++)
            yield return null;
        if (Game1.activeClickableMenu is not NumberSelectionMenu entry) { c.Fail("The ledger did not ask for an amount."); yield break; }
        sub = new CallContext();
        yield return Amount(entry, Args(new { value = amount }), sub);
        if (!sub.Ok) { c.Fail($"Couldn't enter the amount: {sub.Result}"); yield break; }
    }

    /// <summary>Tool arguments for the existing tool coroutines.</summary>
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    /// <summary>Index of the open question's option whose text equals / contains <paramref name="text"/>, or -1.</summary>
    private static int QuestionOption(string text, bool exact)
    {
        if (Game1.activeClickableMenu is not DialogueBox box || !box.isQuestion || box.responses == null) return -1;
        for (int i = 0; i < box.responses.Length; i++)
        {
            var option = box.responses[i].responseText ?? "";
            if (exact ? option.Equals(text, StringComparison.OrdinalIgnoreCase) : option.Contains(text, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static string QuestionOptions() =>
        Game1.activeClickableMenu is DialogueBox { isQuestion: true, responses: not null } box
            ? "Options: " + string.Join(" | ", box.responses.Select(r => r.responseText))
            : "No question is open.";

    private static void CloseOpenMenu()
    {
        if (Game1.activeClickableMenu is DialogueBox box)
            box.closeDialogue();
        else if (Game1.activeClickableMenu != null)
            Game1.exitActiveMenu();
    }
}
