using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

// The Joja route (#26): the alternative to the Community Center bundles (#13), shipped together with them.
//
// joja(action=membership) answers Morris's question; joja(action=buy_project) opens the game's own JojaCDMenu and
// clicks the project's box, so the game takes the money and sets its own mail flags. Members of JojaCDMenu and the
// mail lists that could not be checked against the decompiled game are reached through LateBound (see CommunityTools.cs).
//
// The membership is global and irreversible: it closes the bundles for every player. So it needs confirm=true, names
// what the other players lose, is announced to everyone, is written to the plan board and the log, and raises the
// events joja_membership and joja_project_bought on the log feed (and so on /api/events).

/// <summary>One Joja Community Development project. <see cref="Flags"/> are the mail flags the game may use for it (candidates, see <see cref="JojaRoute"/>).</summary>
internal sealed record JojaProject(string Key, string Name, int Price, string Room, string Gives, string[] Flags, string CcFlag);

internal enum ProjectState { Open, Pending, Built, BuiltByCommunityCenter }

/// <summary>Facts, texts and bookkeeping of the Joja route, shared by the <c>joja</c> tool and the day-start check.</summary>
internal static class JojaRoute
{
    public const int MembershipPrice = 5000;
    public const string PlanSection = "Community Center vs Joja";
    private const string BoardAuthor = "mod";
    private const string MemberFlag = "JojaMember";

    /// <summary>
    /// The five projects. Each one is the counterpart of a Community Center room; the Flags are the game's mail ids as far as
    /// I remember them (jojaPantry... per room), plus the project-named spelling as a second guess. They are only used to
    /// show a project as built; a purchase goes through the game's own form, and the real keys are learned from it.
    /// </summary>
    public static readonly JojaProject[] Projects =
    {
        new("Bus", "Bus Repair", 40000, "Vault", "the bus to the Calico Desert", new[] { "jojaVault", "jojaBus" }, "ccVault"),
        new("Minecarts", "Minecarts", 15000, "Boiler Room", "the minecarts", new[] { "jojaBoilerRoom", "jojaMinecarts" }, "ccBoilerRoom"),
        new("Bridge", "Bridge Repair", 25000, "Crafts Room", "the bridge to the Quarry", new[] { "jojaCraftsRoom", "jojaBridge" }, "ccCraftsRoom"),
        new("Greenhouse", "Greenhouse", 35000, "Pantry", "the Greenhouse on the farm", new[] { "jojaPantry", "jojaGreenhouse" }, "ccPantry"),
        new("Panning", "Panning", 20000, "Fish Tank", "panning", new[] { "jojaFishTank", "jojaPanning" }, "ccFishTank")
    };

    public static int TotalPrice => Projects.Sum(p => p.Price);

    /// <summary>Mail keys that appeared when a project was bought in this session, by project key.</summary>
    private static readonly Dictionary<string, string[]> Learned = new();

    private static string? _route;

    /// <summary>The last recorded decision line, or null. A plan.md / lessons.md writer (#21) can read it.</summary>
    public static string? Decision { get; private set; }

    /// <summary>Raised with the decision line each time the route is decided (Joja membership, first bundle complete, Community Center complete).</summary>
    public static event Action<string>? DecisionRecorded;

    public static string Gold(int n) => n.ToString("N0", CultureInfo.InvariantCulture) + "g";

    public static bool InStore(GameLocation loc) => loc.Name.Equals("JojaMart", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ mail

    /// <summary>Entries of the farmer's mail lists: received ones, those that arrive tomorrow ("id%&amp;NL&amp;%" marks a letterless one) and unread ones.</summary>
    private static IEnumerable<(string list, string id)> MailEntries(Farmer f)
    {
        foreach (var list in new[] { "mailReceived", "mailForTomorrow", "mailbox" })
            if (LateBound.TryGet(f, list) is IEnumerable entries)
                foreach (var o in entries)
                    if (o is string s)
                        yield return (list, s.Split('%')[0]);
    }

    /// <summary>Every mail id of this farmer and the host, in any list.</summary>
    public static HashSet<string> MailKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in new[] { Game1.player, Game1.MasterPlayer }.Distinct())
            foreach (var (_, id) in MailEntries(f))
                keys.Add(id);
        return keys;
    }

    /// <summary>True when the game will deliver this mail (tomorrow, or it waits unread in the mailbox) but has not marked it received yet.</summary>
    public static bool Pending(Farmer who, string id) =>
        new[] { who, Game1.MasterPlayer }.Distinct().Any(f => MailEntries(f).Any(e => e.list != "mailReceived" && e.id == id));

    /// <summary>The farm took the Joja membership (received, or already on its way). It covers every player.</summary>
    public static bool IsMember(Farmer who) => BundleReader.HasMail(who, MemberFlag) || Pending(who, MemberFlag);

    /// <summary>Joja mail ids that appeared since <paramref name="before"/> (from <see cref="MailKeys"/>).</summary>
    public static string[] NewJojaKeys(HashSet<string> before) =>
        MailKeys().Where(k => !before.Contains(k) && k.Contains("joja", StringComparison.OrdinalIgnoreCase) && !k.Equals(MemberFlag, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static void Learn(JojaProject p, string[] keys)
    {
        if (keys.Length > 0) Learned[p.Key] = keys;
    }

    public static ProjectState StateOf(Farmer who, JojaProject p)
    {
        if (BundleReader.HasMail(who, p.CcFlag)) return ProjectState.BuiltByCommunityCenter;
        var flags = p.Flags.Concat(Learned.TryGetValue(p.Key, out var learned) ? learned : Array.Empty<string>()).ToList();
        if (flags.Any(f => BundleReader.HasMail(who, f))) return ProjectState.Built;
        return flags.Any(f => Pending(who, f)) ? ProjectState.Pending : ProjectState.Open;
    }

    /// <summary>Project by key or name ("Minecarts", "Bus Repair"), a unique part of it, or its 1-based number; null when unknown or ambiguous.</summary>
    public static JojaProject? Find(string want)
    {
        want = want.Trim();
        if (want.Length == 0) return null;
        if (int.TryParse(want, out var n) && n >= 1 && n <= Projects.Length) return Projects[n - 1];
        var exact = Projects.FirstOrDefault(p => p.Key.Equals(want, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var part = Projects.Where(p => p.Name.Contains(want, StringComparison.OrdinalIgnoreCase) || p.Room.Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
        return part.Count == 1 ? part[0] : null;
    }

    public static string ProjectNames => string.Join(", ", Projects.Select(p => p.Key));

    /// <summary>Why the membership can't be bought, or null when it can.</summary>
    public static string? MembershipBlocked(Farmer who)
    {
        if (IsMember(who))
            return "The farm already has the Joja membership (it covers every player). joja(action=projects) lists the gold projects.";
        if (BundleReader.HasMail(who, "ccIsComplete"))
            return "The Community Center is already complete, so there is no Joja membership to buy.";
        return null;
    }

    // ------------------------------------------------------------------ Morris's question

    /// <summary>The option that answers "yes", or -1 unless exactly one option can be told apart as yes (by its key, else by its text).</summary>
    public static int YesOption(DialogueBox box)
    {
        var rs = box.responses;
        if (rs == null) return -1;
        var byKey = Enumerable.Range(0, rs.Length).Where(i => (rs[i].responseKey ?? "").EndsWith("yes", StringComparison.OrdinalIgnoreCase)).ToList();
        if (byKey.Count == 1) return byKey[0];
        if (byKey.Count > 1) return -1;
        var byText = Enumerable.Range(0, rs.Length).Where(i => (rs[i].responseText ?? "").TrimStart().StartsWith("yes", StringComparison.OrdinalIgnoreCase)).ToList();
        return byText.Count == 1 ? byText[0] : -1;
    }

    // ------------------------------------------------------------------ the Joja Community Development form

    /// <summary>The game's own JojaCDMenu with the form texture. Not shown yet: assign it to Game1.activeClickableMenu.</summary>
    public static IClickableMenu OpenForm()
    {
        var type = typeof(IClickableMenu).Assembly.GetType("StardewValley.Menus.JojaCDMenu")
                   ?? throw new LateBoundException("The game has no JojaCDMenu class (the game version may differ from the one this mod was written for)");
        Texture2D form;
        try { form = Game1.content.Load<Texture2D>("LooseSprites\\JojaCDForm"); }
        catch (Exception ex) { throw new LateBoundException($"The Joja form picture (LooseSprites\\JojaCDForm) could not be loaded: {ex.Message}"); }
        try { return (IClickableMenu)Activator.CreateInstance(type, form)!; }
        catch (MissingMethodException) { throw new LateBoundException("JojaCDMenu has no constructor that takes the form texture in this game version."); }
        catch (TargetInvocationException ex) { throw new LateBoundException($"The Joja form could not be opened: {(ex.InnerException ?? ex).Message}"); }
    }

    /// <summary>
    /// The project's box on the form. The menu itself tells its prices (getPriceFromButtonNumber), and the five prices differ,
    /// so the box is found by price and never by an assumed order: a wrong guess would spend the wrong 40,000g.
    /// </summary>
    public static ClickableComponent FindBox(IClickableMenu menu, JojaProject p)
    {
        var boxes = (LateBound.Require(menu, "checkboxes") as IEnumerable)?.OfType<ClickableComponent>().ToList() ?? new List<ClickableComponent>();
        for (int i = 0; i < boxes.Count; i++)
            if (LateBound.Call(menu, "getPriceFromButtonNumber", i) is int price && price == p.Price)
                return boxes[i];
        throw new LateBoundException($"The Joja form has no box that costs {Gold(p.Price)} for {p.Name} (JojaCDMenu.checkboxes / getPriceFromButtonNumber differ in this game version). Open the form by hand with interact and use menu.");
    }

    // ------------------------------------------------------------------ texts

    private static List<string> OtherPlayers()
    {
        var me = AgentTools.Rt.Log.Player;
        return Players.Slots
            .Where(s => s.Role == SeatRole.Ai && s.Id != me && s.Active && (s.State as ScreenState)?.Name != null)
            .Select(s => $"{((ScreenState)s.State!).Name} ({s.Id})")
            .ToList();
    }

    private static List<(string name, string label)> Seats() => Players.Slots
        .Where(s => s.Role == SeatRole.Ai && s.Active && (s.State as ScreenState)?.Name != null)
        .Select(s => { var n = ((ScreenState)s.State!).Name!; return (n, $"{n} ({s.Id})"); })
        .ToList();

    /// <summary>Filled ingredient slots by donor name (from the donate_bundle ledger), and how many have no known donor.</summary>
    private static (Dictionary<string, List<string>> byName, int unknown) Donations(List<BundleInfo> all)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int unknown = 0;
        foreach (var b in all)
            foreach (var n in b.Needs.Where(x => x.Done))
            {
                var who = BundleLedger.Who(b.Index, n.Slot);
                if (who == null) { unknown++; continue; }
                int cut = who.LastIndexOf(" day ", StringComparison.Ordinal);
                var name = cut > 0 ? who[..cut] : who;
                if (!byName.TryGetValue(name, out var items)) byName[name] = items = new List<string>();
                items.Add(BundleReader.Describe(n));
            }
        return (byName, unknown);
    }

    private static string DonorEntry(string label, List<string>? items, bool compact) =>
        items == null || items.Count == 0 ? $"{label} nothing" : compact ? $"{label} {items.Count}" : $"{label} {items.Count} ({string.Join(", ", items)})";

    /// <summary>Who has donated what so far. The game keeps only "slot filled", so this is what donate_bundle recorded since the mod started.</summary>
    public static string RosterText(List<BundleInfo> all, bool compact)
    {
        if (!all.Any(b => b.Needs.Any(n => n.Done))) return "nothing donated yet";
        var (byName, unknown) = Donations(all);
        var parts = new List<string>();
        foreach (var (name, label) in Seats())
        {
            byName.TryGetValue(name, out var items);
            byName.Remove(name);
            parts.Add(DonorEntry(label, items, compact));
        }
        foreach (var kv in byName) parts.Add(DonorEntry(kv.Key, kv.Value, compact));
        if (unknown > 0) parts.Add($"{unknown} filled by hand or before this session");
        return string.Join("; ", parts);
    }

    private static string RoomLine(List<BundleInfo> all) =>
        string.Join(", ", all.GroupBy(b => b.Room).Select(g => $"{g.Key} {g.Count(b => b.Complete)}/{g.Count()}"));

    /// <summary>Gold still to be paid into the unfinished Vault-style (gold) bundles.</summary>
    private static int GoldOwed(List<BundleInfo> all) =>
        all.Where(b => !b.Complete).SelectMany(b => b.Needs.Where(n => !n.Done && n.Id == "-1")).Sum(n => n.Count);

    private static string ProjectList() =>
        string.Join(", ", Projects.Select(p => $"{p.Name} {Gold(p.Price)}"));

    /// <summary>What joja(action=membership) answers without confirm: nothing is bought, and everything everybody loses and gains is named.</summary>
    public static string MembershipWarning(Farmer who)
    {
        var all = BundleReader.Read().Where(b => b.Area >= 0).ToList();
        var others = OtherPlayers();
        var sb = new StringBuilder();
        sb.AppendLine("NOT BOUGHT. You did not pass confirm=true, so nothing happened.");
        sb.AppendLine($"The Joja membership costs {Gold(MembershipPrice)} from your own wallet (you have {Gold(who.Money)}). It cannot be undone, and it is not only yours: it applies to the whole farm.");
        sb.AppendLine("LOST, for every player" + (others.Count > 0 ? $" (you and {string.Join(", ", others)})" : "") + ", for good:");
        sb.AppendLine("  - The Community Center bundles close and the building becomes a Joja warehouse. Nobody can donate to a bundle again.");
        if (all.Count > 0)
        {
            int open = all.Count(b => !b.Complete);
            sb.AppendLine($"  - Progress now: {all.Count - open}/{all.Count} bundles complete ({RoomLine(all)}). The rewards of the {open} unfinished bundles are never paid.");
        }
        sb.AppendLine($"  - Items and gold already donated are not given back. Donated so far: {RosterText(all, compact: false)}.");
        sb.AppendLine("GAINED:");
        sb.AppendLine($"  - The Joja Community Development form in the JojaMart: {Projects.Length} projects paid in gold from the buyer's own wallet, each built the day after it is bought: {ProjectList()}; {Gold(TotalPrice)} for all of them.");
        sb.AppendLine($"The mod recommends neither route. The other players are not asked by the game or the mod: the plan board section \"{PlanSection}\" is where you can settle it with them first.");
        sb.Append("To buy it anyway: joja(action=\"membership\", confirm=true, reason=...) while standing in the JojaMart.");
        return sb.ToString();
    }

    /// <summary>The five projects with price and status, and for a farm without the membership the Community Center side for comparison.</summary>
    public static string ProjectsText(Farmer who)
    {
        bool member = BundleReader.HasMail(who, MemberFlag);
        bool pending = !member && Pending(who, MemberFlag);
        var sb = new StringBuilder();
        sb.AppendLine($"Joja Community Development projects. Joja membership: {(member ? "bought" : pending ? "bought (the game confirms it by mail tomorrow)" : "not bought")}. You have {Gold(who.Money)}.");
        foreach (var p in Projects)
        {
            var status = StateOf(who, p) switch
            {
                ProjectState.BuiltByCommunityCenter => "already repaired through the Community Center",
                ProjectState.Built => "built",
                ProjectState.Pending => "bought, built tomorrow",
                _ => member || pending ? "available" : "needs the membership"
            };
            sb.AppendLine($"  {p.Name} ({p.Key}): {Gold(p.Price)}, {p.Gives}; the counterpart of the {p.Room} room. {status}.");
        }
        sb.Append($"All five: {Gold(TotalPrice)}. A project is paid from the buyer's own wallet and built the day after it is bought: joja(action=\"buy_project\", project=...) while standing in the JojaMart.");
        if (!member && !pending)
        {
            var all = BundleReader.Read().Where(b => b.Area >= 0).ToList();
            sb.Append("\nThe membership (joja(action=\"membership\")) is permanent for the whole farm and closes the Community Center bundles for every player; without confirm it only shows what everyone would lose and gain.");
            if (all.Count > 0)
                sb.Append($"\nCommunity Center, for comparison: {all.Count(b => b.Complete)}/{all.Count} bundles complete ({RoomLine(all)}); gold still to pay into gold bundles: {Gold(GoldOwed(all))}. Donated so far: {RosterText(all, compact: false)}.");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ decision, events, plan board

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>
    /// Records the route decision once per kind: a "system" line in the day log (and so in the overlay and the story log), the
    /// <see cref="DecisionRecorded"/> event, and for a final decision the plan board section.
    /// </summary>
    private static void Decide(PlayerLog log, string route, string line, string? board, string boardAuthor)
    {
        _route = route;
        var text = $"DECISION (farm day {AgentLog.FarmDay}): {line}";
        Decision = text;
        log.Add("system", text: text);
        if (board != null) PlanBoard.Edit(boardAuthor, PlanSection, Clip(board, PlanBoard.MaxText));
        DecisionRecorded?.Invoke(text);
    }

    /// <summary>The membership was bought through the joja tool: event, decision, plan board and a notice for everyone.</summary>
    public static void OnMembershipBought(PlayerLog log, Farmer who, int paid)
    {
        int day = AgentLog.FarmDay;
        var label = $"{who.Name} ({log.Player})";
        log.Add("joja_membership", text: $"{who.Name} bought the Joja membership", args: new { player = log.Player, day, name = who.Name, paid });
        Decide(log, "joja", $"{label} bought the Joja membership for {Gold(paid)}. The Community Center bundles are closed for every player, for good; Joja gold projects replace them.",
            $"DECIDED on day {day}: {label} bought the Joja membership. The Community Center bundles are closed for everyone, for good. Gold projects: joja(action=projects) at the JojaMart.", label);
        SocialTools.Announce($"[Joja] {who.Name} bought the Joja membership on day {day}. The Community Center bundles are closed for everyone; the building is a Joja warehouse now.");
    }

    public static void OnProjectBought(PlayerLog log, Farmer who, JojaProject p, int paid, string[] flags)
    {
        int day = AgentLog.FarmDay;
        log.Add("joja_project_bought", text: $"{who.Name} bought the {p.Name} project for {Gold(paid)}",
            args: new { player = log.Player, day, project = p.Name, price = paid, flags });
    }

    /// <summary>
    /// Once per morning on the host: notes a route that was decided without the tool (a membership found, the first bundle done,
    /// the Community Center complete) and keeps the standing plan board section alive while the question is open.
    /// </summary>
    public static void OnDayStarted()
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || Context.ScreenId != 0) return;
        var log = Players.Host.Runtime.Log;
        try
        {
            var host = Game1.MasterPlayer;
            if (IsMember(host))
            {
                if (_route != "joja")
                    Decide(log, "joja", "the farm is on the Joja route: the membership is in place (found at the start of the day, not bought through the joja tool in this session).",
                        "DECIDED: the farm is on the Joja route. The Community Center bundles are closed for everyone, for good. Gold projects: joja(action=projects) at the JojaMart.", BoardAuthor);
                return;
            }

            var all = BundleReader.Read().Where(b => b.Area >= 0).ToList();
            if (BundleReader.HasMail(host, "ccIsComplete") || (all.Count > 0 && all.All(b => b.Complete)))
            {
                if (_route != "cc-complete")
                    Decide(log, "cc-complete", "the Community Center is complete; the Joja membership is no longer an option.",
                        "DECIDED: the Community Center is complete. The Joja membership is no longer an option.", BoardAuthor);
                return;
            }
            if (_route == null && all.Any(b => b.Complete))
                Decide(log, "cc-started", $"the Community Center route is under way: {all.Count(b => b.Complete)}/{all.Count} bundles complete ({RoomLine(all)}).", null, BoardAuthor);

            if (AgentTools.Config.SplitScreen) SeedBoard(all);
        }
        catch (Exception ex)
        {
            log.Add("system", text: $"Joja route check failed: {ex.Message}");
        }
    }

    /// <summary>The standing plan board section where the players can argue the choice. The mod keeps its facts fresh until a player edits it.</summary>
    private static void SeedBoard(List<BundleInfo> all)
    {
        var head = "Open question for every player: repair the town through the Community Center or through Joja? Nobody decides for you. " +
                   $"Community Center: bundles need items from all four seasons plus gold in the Vault; each bundle pays a reward. " +
                   $"Joja: {Gold(MembershipPrice)} membership (closes the bundles for everyone, for good), then {Projects.Length} gold projects ({Gold(TotalPrice)}), each built the day after buying. " +
                   "Write your position below; plan_edit replaces the section, so keep the other lines.";
        var roster = all.Count > 0 ? $" Bundles {all.Count(b => b.Complete)}/{all.Count}; donated: {RosterText(all, compact: true)}." : "";
        var text = Clip(head + roster, PlanBoard.MaxText);

        var current = PlanBoard.Get(PlanSection);
        if (current == null || (current.Author == BoardAuthor && current.Text != text))
            PlanBoard.Edit(BoardAuthor, PlanSection, text);
    }
}

public static partial class AgentTools
{
    private static List<ToolDef> JojaTools() => new()
    {
        new()
        {
            Name = "joja",
            Description = "The Joja route, the alternative to the Community Center bundles. " +
                          "action projects (game time stays frozen): the five Joja Community Development projects with price and status, and the Community Center's progress for comparison. " +
                          "action membership: the 5,000g Joja membership, answered by Morris in the JojaMart (travel_to JojaMart). It is permanent for the WHOLE farm and closes the Community Center bundles for every player. " +
                          "Without confirm=true it buys nothing and shows what everybody loses and gains; with confirm=true it really buys it. " +
                          "action buy_project (project Bus, Minecarts, Bridge, Greenhouse or Panning): pay a project from your own wallet through the game's Joja form; needs the membership and the JojaMart; it is built the next day.",
            Schema = Obj(new
            {
                action = new { type = "string", @enum = new[] { "membership", "projects", "buy_project" }, description = "projects, membership or buy_project" },
                project = Str("buy_project: Bus, Minecarts, Bridge, Greenhouse or Panning (see projects)"),
                confirm = new { type = "boolean", description = "membership only: true really buys the membership, which cannot be undone and closes the bundles for every player. Omit it to only read the warning." },
                reason = Str(ReasonDesc)
            }, "action"),
            InstantFor = a => S(a, "action").Trim().ToLowerInvariant() is "projects" or "" || (S(a, "action").Trim().ToLowerInvariant() == "membership" && !BoolArg(a, "confirm")),
            Run = (a, c) => CommunityGuard(JojaRun(a, c), c)
        }
    };

    /// <summary>Only an explicit true (a JSON true, or the text "true") counts: the membership is irreversible.</summary>
    private static bool BoolArg(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase)));

    private static JsonElement JsonArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IEnumerator JojaFail(CallContext c, string message)
    {
        c.Fail(message);
        yield break;
    }

    private static IEnumerator JojaProjects(CallContext c)
    {
        c.Result = JojaRoute.ProjectsText(P);
        yield break;
    }

    /// <summary>Picks the tool body without being an iterator itself, so <see cref="CommunityGuard"/> wraps the real coroutine.</summary>
    private static IEnumerator JojaRun(JsonElement a, CallContext c)
    {
        var action = S(a, "action").Trim().ToLowerInvariant();
        return action switch
        {
            "projects" or "" => JojaProjects(c),
            "membership" => JojaMembership(a, c),
            "buy_project" => JojaBuyProject(a, c),
            _ => JojaFail(c, $"Unknown action \"{action}\". Use projects, membership or buy_project.")
        };
    }

    /// <summary>Tiles to press the action button on for Morris: an Action tile that names him, then his own tile.</summary>
    private static List<Point> MorrisTiles()
    {
        var tiles = new List<Point>();
        var layer = Loc.Map.Layers[0];
        for (int y = 0; y < layer.LayerHeight; y++)
            for (int x = 0; x < layer.LayerWidth; x++)
                if (Loc.doesTileHaveProperty(x, y, "Action", "Buildings") is { } act && act.Contains("Morris", StringComparison.OrdinalIgnoreCase))
                    tiles.Add(new Point(x, y));
        if (Loc.characters.FirstOrDefault(n => n.Name == "Morris") is { } morris)
            tiles.Add(morris.TilePoint);
        return tiles;
    }

    private static IEnumerator JojaMembership(JsonElement a, CallContext c)
    {
        var blocked = JojaRoute.MembershipBlocked(P);
        if (blocked != null) { c.Fail(blocked); yield break; }
        if (!BoolArg(a, "confirm")) { c.Result = JojaRoute.MembershipWarning(P); yield break; }

        if (!JojaRoute.InStore(Loc)) { c.Fail($"Morris sells the membership inside the JojaMart; you are in {Loc.Name}. Use travel_to JojaMart first."); yield break; }
        if (Game1.activeClickableMenu != null || Game1.eventUp) { c.Fail("Close the open menu or finish the scene first."); yield break; }
        if (P.Money < JojaRoute.MembershipPrice) { c.Fail($"The membership costs {JojaRoute.Gold(JojaRoute.MembershipPrice)} and you have {JojaRoute.Gold(P.Money)}."); yield break; }
        var targets = MorrisTiles();
        if (targets.Count == 0) { c.Fail("Morris is not in the JojaMart right now (the store may be closed)."); yield break; }

        int moneyBefore = P.Money;
        var mailBefore = JojaRoute.MailKeys();
        DialogueBox? box = null;
        foreach (var tile in targets)
        {
            var walk = new CallContext();
            yield return WalkAdjacent(tile, walk);
            if (!walk.Ok) continue;
            if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();
            PressAction(tile);
            for (int i = 0; i < 90 && Game1.activeClickableMenu is not DialogueBox; i++) yield return null;
            box = Game1.activeClickableMenu as DialogueBox;
            if (box != null) break;
        }
        if (box == null) { c.Fail("Morris did not start a conversation. Stand next to him and use interact on him, then respond to his question yourself."); yield break; }

        // Morris may say a few lines before he asks; read on until the question shows.
        var carryOn = JsonArgs("{}");
        for (int step = 0; step < 12 && box != null && !(box.isQuestion && box.responses != null); step++)
        {
            yield return Ticks(10);
            if (box.isQuestion && box.responses != null) break;
            yield return Respond(carryOn, new CallContext());
            box = Game1.activeClickableMenu as DialogueBox;
        }
        if (box == null || !box.isQuestion || box.responses == null)
        {
            c.Fail("Morris did not ask about a membership (the conversation ended without a question). Nothing was bought.");
            yield break;
        }

        var said = (box.getCurrentString() ?? "").Replace("\n", " ").Replace("^", " ").Trim();
        bool fromMorris = box.characterDialogue?.speaker?.Name == "Morris";
        int yes = JojaRoute.YesOption(box);
        bool aboutMembership = fromMorris || said.Contains("member", StringComparison.OrdinalIgnoreCase) || said.Contains("Joja", StringComparison.OrdinalIgnoreCase);
        if (yes < 0 || !aboutMembership)
        {
            var options = string.Join("; ", box.responses.Select((r, i) => $"option {i}: {r.responseText}"));
            c.Fail($"Morris asked: \"{said}\" ({options}). I could not tell which option buys the membership, so I answered nothing. The dialogue is still open: answer it yourself with respond(option).");
            yield break;
        }

        var answer = new CallContext();
        yield return Respond(JsonArgs($"{{\"option\":{yes}}}"), answer);
        if (!answer.Ok) { c.Fail(answer.Result); yield break; }
        for (int i = 0; i < 6 && Game1.activeClickableMenu is DialogueBox { isQuestion: false }; i++)
            yield return Respond(carryOn, new CallContext());
        yield return Ticks(30);

        int paid = moneyBefore - P.Money;
        bool member = JojaRoute.IsMember(P);
        if (!member && paid < JojaRoute.MembershipPrice)
        {
            c.Fail($"Morris's answer did not buy the membership (money {JojaRoute.Gold(moneyBefore)} → {JojaRoute.Gold(P.Money)}, no JojaMember mail). {WorldText.MenuState().Trim()}".Trim());
            yield break;
        }

        var newKeys = JojaRoute.NewJojaKeys(mailBefore);
        JojaRoute.OnMembershipBought(Rt.Log, P, paid);
        c.Result = $"You bought the Joja membership for {JojaRoute.Gold(paid)}{(member ? "" : " (the JojaMember mail was not found yet; the game confirms it tomorrow)")}. " +
                   "The Community Center bundles are closed for every player, for good; every player was told. " +
                   $"The gold projects are next: joja(action=\"projects\") and joja(action=\"buy_project\") at the JojaMart. Money left {JojaRoute.Gold(P.Money)}." +
                   (newKeys.Length > 0 ? $" (mail: {string.Join(", ", newKeys)})" : "");
    }

    private static IEnumerator JojaBuyProject(JsonElement a, CallContext c)
    {
        var want = S(a, "project").Trim();
        if (want.Length == 0) { c.Fail($"project is required: {JojaRoute.ProjectNames} (see projects)."); yield break; }
        var project = JojaRoute.Find(want);
        if (project == null) { c.Fail($"No project \"{want}\". Projects: {JojaRoute.ProjectNames}."); yield break; }
        if (!JojaRoute.IsMember(P)) { c.Fail("The Joja projects need the Joja membership first. joja(action=\"membership\") shows what it costs every player."); yield break; }

        switch (JojaRoute.StateOf(P, project))
        {
            case ProjectState.Built: c.Fail($"The {project.Name} project is already built."); yield break;
            case ProjectState.Pending: c.Fail($"The {project.Name} project is already bought; it is built tomorrow."); yield break;
            case ProjectState.BuiltByCommunityCenter: c.Fail($"The {project.Name} is already repaired through the Community Center."); yield break;
        }
        if (!JojaRoute.InStore(Loc)) { c.Fail($"The Joja form is in the JojaMart; you are in {Loc.Name}. Use travel_to JojaMart first."); yield break; }
        if (Game1.activeClickableMenu != null || Game1.eventUp) { c.Fail("Close the open menu or finish the scene first."); yield break; }
        if (P.Money < project.Price) { c.Fail($"{project.Name} costs {JojaRoute.Gold(project.Price)} and you have {JojaRoute.Gold(P.Money)}."); yield break; }

        int moneyBefore = P.Money;
        var mailBefore = JojaRoute.MailKeys();
        var menu = JojaRoute.OpenForm();
        var box = JojaRoute.FindBox(menu, project);
        Game1.activeClickableMenu = menu;
        try
        {
            yield return Ticks(10);
            Click(menu, box);
            yield return Ticks(20);
        }
        finally
        {
            if (Game1.activeClickableMenu == menu)
            {
                menu.exitThisMenu();
                if (Game1.activeClickableMenu == menu) Game1.exitActiveMenu();
            }
        }
        // The form may close with a line from Morris.
        var carryOn = JsonArgs("{}");
        for (int i = 0; i < 4 && Game1.activeClickableMenu is DialogueBox { isQuestion: false }; i++)
            yield return Respond(carryOn, new CallContext());
        yield return Ticks(10);

        int paid = moneyBefore - P.Money;
        if (paid <= 0)
        {
            var open = Game1.activeClickableMenu != null ? " " + WorldText.MenuState().Trim() : "";
            c.Fail($"The Joja form did not take the payment for {project.Name}, so nothing was bought (the project may already be bought, or the box was not accepted).{open}");
            yield break;
        }

        var flags = JojaRoute.NewJojaKeys(mailBefore);
        JojaRoute.Learn(project, flags);
        JojaRoute.OnProjectBought(Rt.Log, P, project, paid, flags);
        c.Result = $"Bought the Joja {project.Name} project for {JojaRoute.Gold(paid)}: {project.Gives} is built tomorrow. Money left {JojaRoute.Gold(P.Money)}." +
                   (flags.Length > 0 ? $" (mail: {string.Join(", ", flags)})" : "");
    }
}
