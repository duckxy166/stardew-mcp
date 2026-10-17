using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// Dating, engagement and marriage (#53): what the game asks of the player (shown in villagers), the refusals that
/// save a wasted trip, and the proposal between two players. Villager romance goes through the game's own gift path
/// (the same talk_to code from #9), so friendship, status and wedding date are set by the game, never by this mod.
/// Game thread only.
/// </summary>
internal static class Romance
{
    public const int DatingPoints = 2000;    // 8 hearts
    public const int MarriagePoints = 2500;  // 10 hearts
    public const string Bouquet = "Bouquet", Pendant = "Mermaid's Pendant", Ring = "Wedding Ring";

    private static int Hearts(int points) => points / NpcInfo.PointsPerHeart;

    // ------------------------------------------------------------------ villager state

    /// <summary>friendly, dating, engaged, married... (lower case); "" before the farmer met them.</summary>
    internal static string StatusOf(Farmer who, string npc) =>
        who.friendshipData.TryGetValue(npc, out var f) ? f.Status.ToString().ToLowerInvariant() : "";

    private static int PointsWith(Farmer who, string npc) => who.friendshipData.TryGetValue(npc, out var f) ? f.Points : 0;

    internal static bool Carrying(Farmer who, string itemName) =>
        who.Items.Any(i => i != null && i.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase));

    /// <summary>House upgrade level (0 = no kitchen yet), or -1 when it can't be read. Advisory: the game makes the call.</summary>
    internal static int HouseLevel(Farmer who)
    {
        try
        {
            return Utility.getHomeOfFarmer(who)?.upgradeLevel ?? 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string House(Farmer who)
    {
        int level = HouseLevel(who);
        return level < 0 ? "house level unknown" : level >= 1 ? "[ok: the house is upgraded]" : "[missing: the house has no kitchen yet, upgrade it at Robin's first]";
    }

    private static string Mark(bool ok, string have) => ok ? $"[ok: {have}]" : $"[missing: {have}]";

    // ------------------------------------------------------------------ text for villagers()

    /// <summary>Short tag on a roster line.</summary>
    internal static string Brief(VillagerInfo v) =>
        v.Datable && v.Met && (v.Status is "" or "friendly") ? " | can date" : "";

    /// <summary>The farmer's own relationships, one line ("" when there are none).</summary>
    internal static string SelfLine(Farmer who)
    {
        var parts = who.friendshipData.Pairs
            .Where(kv => kv.Value.Status.ToString() is "Dating" or "Engaged" or "Married" or "Roommate")
            .Select(kv => $"{kv.Value.Status.ToString().ToLowerInvariant()} {NpcInfo.ByName(kv.Key)?.displayName ?? kv.Key}")
            .ToList();
        parts.AddRange(PlayerPartners(Players.Current.Id));
        return parts.Count == 0 ? "" : "Your relationships: " + string.Join(", ", parts) + ".";
    }

    /// <summary>What dating or marrying this villager still needs.</summary>
    internal static string Requirements(Farmer who, NPC npc, VillagerInfo v)
    {
        if (!v.Datable) return $"Romance: {v.DisplayName} can't be dated or married.";
        if (!v.Met)
            return $"Romance: talk_to {v.DisplayName} first. Dating needs {Hearts(DatingPoints)} hearts and a Bouquet; marriage needs {Hearts(MarriagePoints)} hearts and a Mermaid's Pendant.";
        switch (v.Status)
        {
            case "married":
                return $"Romance: you are married to {v.DisplayName}.";
            case "engaged":
                return $"Romance: you are engaged to {v.DisplayName}. The wedding is held about 3 days after the proposal; the game plays it by itself.";
            case "dating":
                return $"Romance: you are dating {v.DisplayName}. To propose, mermaid_pendant(\"{v.Name}\") needs: " +
                       $"{Mark(v.Points >= MarriagePoints, $"{v.Hearts} of {Hearts(MarriagePoints)} hearts")}, " +
                       $"{Mark(Carrying(who, Pendant), Carrying(who, Pendant) ? "you carry a Mermaid's Pendant" : "you have no Mermaid's Pendant")}, {House(who)}. " +
                       "If they accept you are engaged.";
            default:
                return $"Romance: you can ask {v.DisplayName} to date. bouquet(\"{v.Name}\") needs: " +
                       $"{Mark(v.Points >= DatingPoints, $"{v.Hearts} of {Hearts(DatingPoints)} hearts")}, " +
                       $"{Mark(Carrying(who, Bouquet), Carrying(who, Bouquet) ? "you carry a Bouquet" : "you have no Bouquet, check Pierre's shop")}. " +
                       "Marriage comes later: 10 hearts, a Mermaid's Pendant and an upgraded house.";
        }
    }

    // ------------------------------------------------------------------ refusals (only what the game would refuse for sure)

    private static string? NotDatable(NPC npc) =>
        npc.datable.Value ? null : $"{npc.displayName} can't be dated or married: only the villagers marked \"can date\" in villagers() can.";

    internal static string? BouquetRefusal(Farmer who, NPC npc)
    {
        var no = NotDatable(npc);
        if (no != null) return no;
        switch (StatusOf(who, npc.Name))
        {
            case "": return $"You haven't met {npc.displayName} yet: talk_to introduces you.";
            case "dating": return $"You are already dating {npc.displayName}. The next step is mermaid_pendant at 10 hearts.";
            case "engaged": return $"You are already engaged to {npc.displayName}.";
            case "married": return $"You are married to {npc.displayName}.";
        }
        int points = PointsWith(who, npc.Name);
        if (points < DatingPoints)
            return $"{npc.displayName} has {Hearts(points)} hearts with you ({points} points); dating needs {Hearts(DatingPoints)} hearts ({DatingPoints} points). " +
                   "Talk to them and give gifts (talk_to) first.";
        if (!Carrying(who, Bouquet))
            return "You have no Bouquet in your inventory. Pierre's shop (shop_list, buy) is the place to look.";
        return null;
    }

    internal static string? PendantRefusal(Farmer who, NPC npc)
    {
        var no = NotDatable(npc);
        if (no != null) return no;
        switch (StatusOf(who, npc.Name))
        {
            case "": return $"You haven't met {npc.displayName} yet: talk_to introduces you.";
            case "engaged": return $"You are already engaged to {npc.displayName}. The wedding is held by itself a few days after the proposal.";
            case "married": return $"You are married to {npc.displayName}.";
            case "dating": break;
            default: return $"You are not dating {npc.displayName} yet. Start with bouquet(\"{npc.Name}\") (8 hearts and a Bouquet).";
        }
        int points = PointsWith(who, npc.Name);
        if (points < MarriagePoints)
            return $"{npc.displayName} has {Hearts(points)} hearts with you ({points} points); a proposal needs {Hearts(MarriagePoints)} hearts ({MarriagePoints} points).";
        if (!Carrying(who, Pendant))
            return "You have no Mermaid's Pendant in your inventory.";
        return null;
    }

    /// <summary>One sentence about what a bouquet or pendant changed, from the friendship status before and after.</summary>
    internal static string Outcome(Farmer who, NPC npc, string before, string after)
    {
        if (after != before)
        {
            return after switch
            {
                "dating" => $" You are now dating {npc.displayName}!",
                "engaged" => $" You are now engaged to {npc.displayName}! The wedding is held about 3 days from now; the game plays it by itself.",
                "married" => $" You are now married to {npc.displayName}!",
                _ => $" Your status with {npc.displayName} is now {after}."
            };
        }
        var hint = after == "dating" && HouseLevel(who) == 0
            ? " Your house has no kitchen yet (house level 0): upgrade it first (Robin), then try again."
            : "";
        return $" Your status with {npc.displayName} is still {(after.Length == 0 ? "unchanged" : after)}: read their reaction above, villagers(\"{npc.Name}\") shows what is missing.{hint}";
    }

    // ------------------------------------------------------------------ proposals between players

    internal enum PromptKind { None, Waiting, Question }

    /// <summary>One player's marriage proposal to another. Seats are "p1".."p3".</summary>
    internal sealed class Offer
    {
        public string From = "", To = "", Item = "";
        public int Day;
        /// <summary>"" while open; then accepted, declined or unknown.</summary>
        public volatile string Answer = "";
        public bool Announced, Married;
    }

    private static readonly object Gate = new();
    private static readonly List<Offer> Offers = new();
    private static readonly int[] SecondTicks = new int[Players.Seats];
    private static bool _watchFailed;

    internal static Offer Begin(string proposer, string to, string item)
    {
        var offer = new Offer { From = proposer, To = to, Item = item, Day = Game1.Date.TotalDays };
        lock (Gate)
        {
            Offers.RemoveAll(o => (o.From == proposer && o.Answer.Length == 0) || Game1.Date.TotalDays - o.Day > 12);
            Offers.Add(offer);
        }
        return offer;
    }

    internal static void Withdraw(Offer offer)
    {
        lock (Gate) Offers.Remove(offer);
    }

    /// <summary>The open proposal addressed to this seat, or null.</summary>
    internal static Offer? IncomingFor(string seat)
    {
        lock (Gate) return Offers.LastOrDefault(o => o.To == seat && o.Answer.Length == 0);
    }

    /// <summary>"engaged to Gemini (p2)" lines for the seat's accepted proposals.</summary>
    internal static List<string> PlayerPartners(string seat)
    {
        lock (Gate)
        {
            return Offers
                .Where(o => o.Answer == "accepted" && (o.From == seat || o.To == seat))
                .Select(o =>
                {
                    var other = Players.ById(o.From == seat ? o.To : o.From);
                    return $"{(o.Married ? "married" : "engaged")} {(other == null ? "another player" : SocialTools.Label(other))}";
                })
                .ToList();
        }
    }

    internal static Farmer? FarmerOf(PlayerSlot? slot)
    {
        var name = (slot?.State as ScreenState)?.Name;
        return name == null ? null : Game1.getOnlineFarmers().FirstOrDefault(f => f.Name == name);
    }

    /// <summary>The friendship status between two farmers (stored in the team, not in friendshipData), lower case; "" when unreadable.</summary>
    internal static string TeamStatus(Farmer a, Farmer b)
    {
        try
        {
            return a.team.GetFriendship(a.UniqueMultiplayerID, b.UniqueMultiplayerID).Status.ToString().ToLowerInvariant();
        }
        catch (Exception)
        {
            return "";
        }
    }

    internal static string InferAnswer(Farmer me, Farmer? other)
    {
        if (other == null) return "unknown";
        var status = TeamStatus(me, other);
        if (status.Length == 0) return "unknown";
        return status is "engaged" or "married" ? "accepted" : "declined";
    }

    /// <summary>
    /// Record the answer once and tell the story log. The caller is whichever seat noticed first: the one that
    /// answered, or the proposer when its box closed.
    /// </summary>
    internal static void Resolve(AgentRuntime rt, Offer offer, string answer)
    {
        lock (Gate)
        {
            if (offer.Answer.Length == 0) offer.Answer = answer;
            if (offer.Announced || answer == "unknown") return;
            offer.Announced = true;
        }
        var proposer = Players.ById(offer.From);
        var receiver = Players.ById(offer.To);
        NpcStory.Emit(rt.Log, "proposal",
            $"{(proposer == null ? offer.From : SocialTools.Label(proposer))} proposed to {(receiver == null ? offer.To : SocialTools.Label(receiver))}: {answer}",
            new { kind = "player", @from = offer.From, to = offer.To, item = offer.Item, answer });
    }

    /// <summary>The menu is a Yes/No box or a proposal box. Found by class name where the game's classes are unverified.</summary>
    internal static PromptKind KindOf(IClickableMenu? menu)
    {
        if (menu == null) return PromptKind.None;
        var name = menu.GetType().Name;
        if (name.Contains("PendingProposal", StringComparison.Ordinal)) return PromptKind.Waiting;
        if (menu is ConfirmationDialog || name.Contains("Proposal", StringComparison.Ordinal)) return PromptKind.Question;
        return PromptKind.None;
    }

    /// <summary>The seat's screen currently shows a Yes/No or proposal box (read from its screen state).</summary>
    internal static bool ShowsPrompt(PlayerSlot slot)
    {
        var menu = (slot.State as ScreenState)?.Menu;
        return menu != null && (menu == nameof(ConfirmationDialog) || menu.Contains("Proposal", StringComparison.Ordinal));
    }

    private static string MessageOf(IClickableMenu menu)
    {
        try
        {
            var text = AgentTools.Rt.Helper.Reflection.GetField<string>(menu, "message", required: false)?.GetValue();
            if (!string.IsNullOrWhiteSpace(text)) return text.Replace("\n", " ").Replace("^", " ").Trim();
        }
        catch (Exception)
        {
            // The text is only a convenience: fall back to the class name below.
        }
        return menu.GetType().Name;
    }

    /// <summary>The status/footer line for a proposal box, or null when the menu is not one (WorldText.MenuState uses it).</summary>
    internal static string? MenuLine(IClickableMenu? menu)
    {
        switch (KindOf(menu))
        {
            case PromptKind.Waiting:
                return "Waiting for the answer to your proposal. close_menu withdraws it.";
            case PromptKind.Question:
                var offer = IncomingFor(Players.Current.Id);
                var proposer = offer == null ? null : Players.ById(offer.From);
                var who = proposer == null ? "" : $" ({SocialTools.Label(proposer)} is proposing to you)";
                return $"Question box{who}: \"{MessageOf(menu!)}\" Use answer_proposal(accept=true) for yes or accept=false for no.";
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ engagement to wedding (players)

    /// <summary>Once a second on each AI seat's own screen: has a proposal this seat made turned into a marriage?</summary>
    internal static void Tick(AgentRuntime rt)
    {
        if (!Context.IsWorldReady) return;
        var slot = Players.Current;
        if (slot.Role != SeatRole.Ai || ++SecondTicks[slot.Screen] % 60 != 0) return;

        List<Offer> watching;
        lock (Gate)
            watching = Offers.Where(o => o.From == slot.Id && o.Answer == "accepted" && !o.Married).ToList();
        if (watching.Count == 0) return;

        try
        {
            foreach (var offer in watching)
            {
                if (Game1.Date.TotalDays - offer.Day > 12) { offer.Married = true; continue; } // no wedding in sight: stop looking
                var other = FarmerOf(Players.ById(offer.To));
                if (other == null || TeamStatus(Game1.player, other) != "married") continue;

                offer.Married = true;
                NpcStory.Emit(rt.Log, "wedding", $"{Game1.player.Name} married {other.Name}",
                    new { kind = "player", @from = offer.From, to = offer.To });
                SocialTools.Notify(offer.From, $"You are now married to {SocialTools.Label(Players.ById(offer.To)!)}.");
                SocialTools.Notify(offer.To, $"You are now married to {SocialTools.Label(Players.ById(offer.From)!)}.");
            }
        }
        catch (Exception ex)
        {
            if (!_watchFailed)
            {
                _watchFailed = true;
                rt.Log.Add("system", text: $"Wedding watch failed (player marriage will not be reported): {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ the night (children)

    /// <summary>
    /// go_to_bed's wait loop: a question came up during the night (the game's baby question). Ends the call so the AI
    /// can answer it with respond(option). Text-only boxes (the birth) are read out to the log and clicked through,
    /// or the night would wait for a key press nobody makes.
    /// </summary>
    internal static bool NightQuestion(CallContext c, AgentRuntime rt, int tick)
    {
        if (Game1.activeClickableMenu is not DialogueBox box) return false;
        if (box.isQuestion && box.responses != null && box.responses.Length > 0)
        {
            c.Result = "A question came up during the night (below). Answer it with respond(option); you wake up after that.";
            return true;
        }
        if (tick % 90 == 0 && !box.transitioning)
        {
            var line = (box.getCurrentString() ?? "").Replace("\n", " ").Replace("^", " ").Trim();
            if (line.Length > 0) rt.Log.Add("system", text: $"Night text: {line}");
            box.receiveLeftClick(0, 0);
        }
        return false;
    }
}

/// <summary>bouquet, mermaid_pendant, propose and answer_proposal (#53). Registered in <see cref="AgentTools.All"/>.</summary>
public static partial class AgentTools
{
    internal static List<ToolDef> RomanceTools() => new()
    {
        new()
        {
            Name = "bouquet",
            Description = "Ask a villager to date you by giving them a Bouquet from your inventory. Needs a villager marked \"can date\" in villagers(), " +
                          "8 hearts (2000 points) and a Bouquet. You walk to them like talk_to does. If they accept, you are dating. villagers(name) shows what is still missing.",
            Schema = Obj(new
            {
                name = Str("Villager name, e.g. Abigail"),
                go = new { type = "boolean", description = "Travel to them when they are on another map (default true)" },
                reason = Str(ReasonDesc)
            }, "name", "reason"),
            Run = (a, c) => Courting(a, c, bouquet: true)
        },
        new()
        {
            Name = "mermaid_pendant",
            Description = "Propose to a villager you are dating by giving them a Mermaid's Pendant from your inventory. Needs 10 hearts (2500 points), the pendant and a house " +
                          "that has been upgraded at least once (a kitchen). If they accept you are engaged and the game holds the wedding about 3 days later by itself.",
            Schema = Obj(new
            {
                name = Str("Villager name, e.g. Abigail"),
                go = new { type = "boolean", description = "Travel to them when they are on another map (default true)" },
                reason = Str(ReasonDesc)
            }, "name", "reason"),
            Run = (a, c) => Courting(a, c, bouquet: false)
        },
        new()
        {
            Name = "propose",
            Description = "Propose marriage to another player. You need a Wedding Ring (or a Mermaid's Pendant) in your inventory; you walk next to them and hold it out like the game does. " +
                          "They see a Yes/No box and are told in their next result; they answer with answer_proposal. Waits up to 90 seconds for the answer. Both wallets stay separate.",
            Schema = Obj(new { to = Str("Player id (p1, p2...) or name"), reason = Str(ReasonDesc) }, "to", "reason"),
            Run = ProposeToPlayer
        },
        new()
        {
            Name = "answer_proposal",
            Description = "Answer the Yes/No box open on your screen, for example another player's marriage proposal. accept=true says yes, accept=false says no.",
            Schema = Obj(new { accept = new { type = "boolean", description = "true = yes, false = no" }, reason = Str(ReasonDesc) }, "accept"),
            Run = AnswerProposal
        }
    };

    /// <summary>bouquet / mermaid_pendant: check what the game would refuse, then give the item through talk_to (the game's gift path).</summary>
    private static IEnumerator Courting(JsonElement a, CallContext c, bool bouquet)
    {
        var asked = S(a, "name").Trim();
        if (asked.Length == 0) { c.Fail("name is empty. villagers() marks who can be dated with \"can date\"."); yield break; }
        var npc = NpcInfo.ByName(asked);
        if (npc == null || !npc.CanSocialize) { c.Fail($"No villager called \"{asked}\". villagers() lists who you can befriend."); yield break; }

        var refusal = bouquet ? Romance.BouquetRefusal(P, npc) : Romance.PendantRefusal(P, npc);
        if (refusal != null) { c.Fail(refusal); yield break; }

        var before = Romance.StatusOf(P, npc.Name);
        var gift = ArgsOf(new { name = npc.Name, gift_item = bouquet ? Romance.Bouquet : Romance.Pendant, go = NpcFlag(a, "go", true), reason = Reason(a) });
        yield return TalkToNpc(gift, c);
        if (!c.Ok) yield break;

        // The game sets dating or engaged when it takes the gift; give it a moment, then report what changed.
        yield return Ticks(10);
        c.Result = c.Result.TrimEnd() + Romance.Outcome(P, npc, before, Romance.StatusOf(P, npc.Name));
    }

    /// <summary>Walk next to another player, hold the ring or pendant out (the action button on their tile) and wait for the answer.</summary>
    private static IEnumerator ProposeToPlayer(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu != null) { c.Fail("A menu or dialogue is open. Finish it with respond() or close_menu first."); yield break; }
        var target = SocialTools.Target(S(a, "to"), c);
        if (target == null) yield break;
        var label = SocialTools.Label(target);
        var other = Romance.FarmerOf(target);
        if (other?.currentLocation == null) { c.Fail($"{label} is not in the world right now."); yield break; }

        var item = Romance.Ring;
        int slot = FindItem(item, it => it is SObject);
        if (slot < 0) { item = Romance.Pendant; slot = FindItem(item, it => it is SObject); }
        if (slot < 0) { c.Fail($"You need a {Romance.Ring} or a {Romance.Pendant} in your inventory to propose to a player."); yield break; }

        if (other.currentLocation.NameOrUniqueName != Loc.NameOrUniqueName)
        {
            c.Fail($"{label} is on {other.currentLocation.NameOrUniqueName} at ({other.TilePoint.X},{other.TilePoint.Y}) and you are on {Loc.NameOrUniqueName}. travel_to their map first.");
            yield break;
        }

        bool beside = false;
        for (int attempt = 0; attempt < 3 && !beside; attempt++)
        {
            other = Romance.FarmerOf(target) ?? other;
            if (other.currentLocation?.NameOrUniqueName != Loc.NameOrUniqueName) break;
            var walk = new CallContext();
            yield return WalkAdjacent(other.TilePoint, walk);
            if (!walk.Ok) { c.Fail(walk.Result); yield break; }
            var me = P.TilePoint;
            var they = other.TilePoint;
            beside = Math.Abs(me.X - they.X) + Math.Abs(me.Y - they.Y) == 1;
        }
        if (!beside) { c.Fail($"Couldn't get next to {label}: they keep moving or left the map. Try again when they stand still."); yield break; }
        Face(other.TilePoint);

        var myLabel = SocialTools.Label(Players.ById(Rt.Log.Player)!);
        P.CurrentToolIndex = slot;
        var offer = Romance.Begin(Rt.Log.Player, target.Id, item);
        PressAction(other.TilePoint);
        yield return Ticks(30);

        // The game may first ask the proposer to confirm. Calling propose is the confirmation.
        for (int step = 0; step < 2 && Game1.activeClickableMenu is DialogueBox q && q.isQuestion && q.responses != null; step++)
        {
            int yes = Array.FindIndex(q.responses, r => string.Equals(r.responseKey, "Yes", StringComparison.OrdinalIgnoreCase));
            if (yes < 0) yes = Array.FindIndex(q.responses, r => (r.responseText ?? "").StartsWith("Yes", StringComparison.OrdinalIgnoreCase));
            if (yes < 0) break;
            yield return Respond(ArgsOf(new { option = yes }), new CallContext());
            yield return Ticks(20);
        }

        var kind = Romance.KindOf(Game1.activeClickableMenu);
        bool delivered = Romance.ShowsPrompt(target);
        if (kind == Romance.PromptKind.None && !delivered)
        {
            Romance.Withdraw(offer);
            var shown = WorldText.MenuState().Trim();
            c.Fail($"{label} got no proposal: the game did not start one" + (shown.Length > 0 ? $" and says:\n{shown}" : ".") +
                   $" It takes a {Romance.Ring} or {Romance.Pendant} held out to a player who is free to marry.");
            yield break;
        }

        SocialTools.Notify(target.Id, $"{myLabel} holds out a {item} and proposes to you!" +
            (delivered ? " A Yes/No box is open on your screen: " : " If a Yes/No box is open on your screen, ") +
            "answer_proposal(accept=true) says yes and accept=false says no.");
        Rt.Log.Add("system", text: $"Proposal to {target.Id} with {item}");
        NpcStory.Emit(Rt.Log, "proposal", $"{P.Name} asked {other.Name} to marry them",
            new { kind = "player", @from = Rt.Log.Player, to = target.Id, item, answer = "asked" });

        bool watchBox = kind != Romance.PromptKind.None;
        for (int t = 0; t < 60 * 90 && offer.Answer.Length == 0; t++)
        {
            if (watchBox && Romance.KindOf(Game1.activeClickableMenu) == Romance.PromptKind.None) break; // the game closed our waiting box
            yield return null;
        }
        if (offer.Answer.Length == 0 && watchBox && Romance.KindOf(Game1.activeClickableMenu) == Romance.PromptKind.None)
        {
            yield return Ticks(60); // the answer travels over the network; give the friendship status time to follow
            if (offer.Answer.Length == 0) Romance.Resolve(Rt, offer, Romance.InferAnswer(P, Romance.FarmerOf(target)));
        }

        switch (offer.Answer)
        {
            case "accepted":
                c.Result = $"{label} said yes! You are engaged. The game holds the wedding a few days later; you will be told when it happens.";
                break;
            case "declined":
                c.Result = $"{label} said no.";
                break;
            case "unknown":
                c.Result = $"The proposal to {label} is over, but I can't tell whether they said yes. Ask them.";
                break;
            default:
                c.Result = $"{label} has not answered yet. The proposal stays open; close_menu withdraws it. You will be told in a later result if they answer.";
                break;
        }
    }

    /// <summary>Click yes or no on the Yes/No box (a ConfirmationDialog's OK/cancel buttons, or the choices of a question dialogue).</summary>
    private static IEnumerator AnswerProposal(JsonElement a, CallContext c)
    {
        if (!Has(a, "accept")) { c.Fail("accept is required: true says yes, false says no."); yield break; }
        bool yes = NpcFlag(a, "accept", false);
        var menu = Game1.activeClickableMenu;
        var offer = Romance.IncomingFor(Rt.Log.Player);

        if (menu is DialogueBox { isQuestion: true } box && box.responses != null && box.responses.Length > 0)
        {
            int pick = Array.FindIndex(box.responses, r => string.Equals(r.responseKey, yes ? "Yes" : "No", StringComparison.OrdinalIgnoreCase));
            if (pick < 0)
                pick = Array.FindIndex(box.responses, r => (r.responseText ?? "").StartsWith(yes ? "Yes" : "No", StringComparison.OrdinalIgnoreCase));
            if (pick < 0) { c.Fail("This question has no plain yes/no choice. Answer it with respond(option)."); yield break; }
            var sub = new CallContext();
            yield return Respond(ArgsOf(new { option = pick }), sub);
            if (!sub.Ok) { c.Fail(sub.Result); yield break; }
        }
        else if (menu != null && Romance.KindOf(menu) == Romance.PromptKind.Question)
        {
            var button = Field(menu, yes ? "okButton" : "cancelButton");
            if (button == null) { c.Fail($"{MenuName(menu)} has no {(yes ? "OK" : "cancel")} button. {Actions(menu)}"); yield break; }
            Click(menu, button);
            yield return Ticks(20);
        }
        else
        {
            c.Fail("No Yes/No box is open on your screen. status shows what is open.");
            yield break;
        }

        var proposer = offer == null ? null : Players.ById(offer.From);
        if (offer != null)
        {
            Romance.Resolve(Rt, offer, yes ? "accepted" : "declined");
            SocialTools.Notify(offer.From, $"{SocialTools.Label(Players.ById(Rt.Log.Player)!)} said {(yes ? "yes" : "no")} to your proposal.");
        }
        c.Result = yes
            ? (proposer != null ? $"You said yes to {SocialTools.Label(proposer)}. You are engaged; the game holds the wedding a few days later." : "You said yes.")
            : (proposer != null ? $"You said no to {SocialTools.Label(proposer)}." : "You said no.");
    }
}

/// <summary>Test hooks for runner/smoke.py (POST /api/debug/{op}); wired into <see cref="DebugApi.All"/>. Not MCP tools.</summary>
internal static class RomanceDebug
{
    private static string? _lastNamed;

    /// <summary>set_friendship {npc, points}: friendship points with a villager (8 hearts = 2000), meeting them if needed.</summary>
    internal static IEnumerator SetFriendship(JsonElement a, CallContext c)
    {
        var npc = NpcInfo.ByName(AgentTools.S(a, "npc"));
        if (npc == null) { c.Fail("npc is required (e.g. \"Abigail\")"); yield break; }
        var me = Game1.player;
        if (!me.friendshipData.TryGetValue(npc.Name, out var f))
        {
            f = new Friendship();
            me.friendshipData.Add(npc.Name, f);
        }
        f.Points = Math.Clamp(AgentTools.I(a, "points", 0), 0, 3500);
        c.Result = $"{npc.Name}: {f.Points} points, status {f.Status}, datable {npc.datable.Value}";
        yield break;
    }

    /// <summary>open_naming {title}: open a NamingMenu like a new calf would; last_named reports the name it was given.</summary>
    internal static IEnumerator OpenNaming(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
        _lastNamed = null;
        var title = AgentTools.S(a, "title", "Name your smoke calf:");
        Game1.activeClickableMenu = new NamingMenu(name => _lastNamed = name, title, "");
        c.Result = $"Opened a naming box: {title}";
        yield break;
    }

    internal static IEnumerator LastNamed(JsonElement a, CallContext c)
    {
        c.Result = _lastNamed ?? "(none)";
        yield break;
    }

    /// <summary>resolve_naming: what go_to_bed does with an open naming box.</summary>
    internal static IEnumerator ResolveNaming(JsonElement a, CallContext c)
    {
        Naming.ResolveDefaults(AgentTools.Rt, "smoke test");
        c.Result = _lastNamed ?? "(none)";
        yield break;
    }
}
