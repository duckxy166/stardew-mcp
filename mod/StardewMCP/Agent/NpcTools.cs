using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>One villager as a player sees them right now. Build it on the game thread.</summary>
internal class VillagerInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Location { get; set; } = "?";
    public int X { get; set; }
    public int Y { get; set; }
    public bool Met { get; set; }
    public int Points { get; set; }
    public int Hearts { get; set; }
    public bool TalkedToToday { get; set; }
    public int GiftsToday { get; set; }
    public int GiftsThisWeek { get; set; }
    /// <summary>friendly, dating, engaged, married... (lower case); empty before you met them.</summary>
    public string Status { get; set; } = "";
    public string BirthdaySeason { get; set; } = "";
    public int BirthdayDay { get; set; }
    /// <summary>0 = today; -1 = unknown.</summary>
    public int DaysToBirthday { get; set; } = -1;
}

/// <summary>
/// Pure helpers behind villagers(), talk_to and travel_to for people (#9): finding villagers, their
/// friendship state, birthdays, place aliases. Game thread only. Tool coroutines live in <see cref="AgentTools"/>.
/// </summary>
internal static class NpcInfo
{
    /// <summary>Points per heart.</summary>
    public const int PointsPerHeart = 250;

    private static readonly string[] SeasonNames = { "spring", "summer", "fall", "winter" };

    /// <summary>Items the game treats as more than a gift; they have no taste.</summary>
    public static readonly HashSet<string> SpecialGifts = new(StringComparer.OrdinalIgnoreCase) { "Bouquet", "Mermaid's Pendant" };

    /// <summary>Shop keepers whose name means their shop (travel_to Pierre). Any other villager means where they are now.</summary>
    public static readonly Dictionary<string, string> PlaceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pierre"] = "SeedShop",
        ["Clint"] = "Blacksmith",
        ["Robin"] = "ScienceHouse",
        ["Marnie"] = "AnimalShop",
        ["Willy"] = "FishShop",
        ["Gus"] = "Saloon"
    };

    // ------------------------------------------------------------------ finding people

    /// <summary>Every villager the player can befriend (one per name).</summary>
    public static List<NPC> Social() =>
        Utility.getAllCharacters()
            .Where(n => n != null && n is not Monster && n.CanSocialize)
            .GroupBy(n => n.Name)
            .Select(g => g.First())
            .ToList();

    /// <summary>The NPC called <paramref name="name"/> (name or display name, any case) anywhere in the world, or null.</summary>
    public static NPC? ByName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        var all = Utility.getAllCharacters().Where(n => n != null && n is not Monster).ToList();
        return all.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
               all.FirstOrDefault(n => n.displayName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool OnMap(NPC npc, GameLocation? loc) =>
        loc != null && npc.currentLocation != null && npc.currentLocation.NameOrUniqueName == loc.NameOrUniqueName;

    public static string WhereIs(NPC npc) =>
        npc.currentLocation == null ? "an unknown place" : $"{npc.currentLocation.NameOrUniqueName} ({npc.TilePoint.X},{npc.TilePoint.Y})";

    // ------------------------------------------------------------------ friendship state

    public static VillagerInfo Of(Farmer who, NPC npc)
    {
        var info = new VillagerInfo
        {
            Name = npc.Name,
            DisplayName = npc.displayName,
            Location = npc.currentLocation?.NameOrUniqueName ?? "?",
            X = npc.TilePoint.X,
            Y = npc.TilePoint.Y,
            BirthdaySeason = (Convert.ToString(npc.Birthday_Season) ?? "").Trim().ToLowerInvariant(),
            BirthdayDay = Convert.ToInt32(npc.Birthday_Day)
        };
        info.DaysToBirthday = DaysToBirthday(info.BirthdaySeason, info.BirthdayDay);
        if (who.friendshipData.TryGetValue(npc.Name, out var f))
        {
            info.Met = true;
            info.Points = f.Points;
            info.Hearts = f.Points / PointsPerHeart;
            info.TalkedToToday = f.TalkedToToday;
            info.GiftsToday = f.GiftsToday;
            info.GiftsThisWeek = f.GiftsThisWeek;
            info.Status = f.Status.ToString().ToLowerInvariant();
        }
        return info;
    }

    // ------------------------------------------------------------------ birthdays

    public static int SeasonIndex(string season)
    {
        var s = season.Trim().ToLowerInvariant();
        if (s == "autumn") s = "fall";
        return Array.IndexOf(SeasonNames, s);
    }

    /// <summary>Days from today until the next birthday: 0 = today, -1 = unknown. A year is 4 x 28 days.</summary>
    public static int DaysToBirthday(string season, int day)
    {
        int s = SeasonIndex(season);
        if (s < 0 || day < 1 || day > 28) return -1;
        int now = Math.Max(0, SeasonIndex(Game1.currentSeason)) * 28 + Game1.dayOfMonth;
        int birthday = s * 28 + day;
        return ((birthday - now) % 112 + 112) % 112;
    }

    public static string BirthdayText(VillagerInfo v)
    {
        if (v.BirthdayDay < 1 || v.DaysToBirthday < 0) return "birthday unknown";
        var s = $"birthday {v.BirthdaySeason} {v.BirthdayDay}";
        if (v.DaysToBirthday == 0) return s + " (TODAY: a gift counts 8x)";
        if (v.DaysToBirthday <= 3) return s + $" (in {v.DaysToBirthday} day{(v.DaysToBirthday == 1 ? "" : "s")})";
        return s;
    }

    // ------------------------------------------------------------------ gift tastes

    /// <summary>The game's taste codes: 0 love, 2 like, 4 dislike, 6 hate, anything else neutral.</summary>
    public static string TasteWord(int code) => code switch { 0 => "loved", 2 => "liked", 4 => "disliked", 6 => "hated", _ => "neutral" };

    public static string TasteVerb(int code) => code switch
    {
        0 => "loved",
        2 => "liked",
        4 => "disliked",
        6 => "hated",
        _ => "was neutral about"
    };

    public static string ItemName(string id)
    {
        try
        {
            return ItemRegistry.GetData(id)?.DisplayName ?? id;
        }
        catch (Exception)
        {
            return id;
        }
    }

    public static string Signed(int n) => n >= 0 ? $"+{n}" : n.ToString();

    // ------------------------------------------------------------------ text for the model

    public static string Line(VillagerInfo v)
    {
        var status = v.Status.Length > 0 && v.Status != "friendly" ? $" | {v.Status}" : "";
        return $"{v.DisplayName}: {v.Location} ({v.X},{v.Y}) | {v.Hearts} hearts ({v.Points} pts) | talked today: {(v.TalkedToToday ? "yes" : "no")}" +
               $" | gifts {v.GiftsToday} today, {v.GiftsThisWeek} this week | {BirthdayText(v)}{status}";
    }

    public static string Roster(Farmer who)
    {
        var infos = Social().Select(n => Of(who, n)).OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Villagers on farm day {AgentLog.FarmDay} ({Game1.currentSeason} {Game1.dayOfMonth}, {Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth)}). " +
                      "250 points = 1 heart. Talking: +20 points, once a day. Gifts: at most 1 per day and 2 per week per villager; a birthday gift counts 8x.");
        foreach (var met in infos.Where(i => i.Met))
            sb.AppendLine(Line(met));
        var unmet = infos.Where(i => !i.Met).ToList();
        if (unmet.Count > 0)
            sb.AppendLine("Not met yet (talk_to introduces you): " + string.Join(", ", unmet.Select(i => $"{i.DisplayName} ({i.Location} {i.X},{i.Y})")));
        sb.Append("villagers(name) shows one villager's gift tastes; talk_to(name) finds them and talks.");
        return sb.ToString();
    }

    /// <summary>Details for one villager: state, your last gift, what other players were seen doing, tastes found out so far.</summary>
    public static string Detail(Farmer who, NPC npc, string viewerSeat, bool revealTastes)
    {
        var v = Of(who, npc);
        var book = NpcKnowledge.Book(who);
        var sb = new StringBuilder();
        sb.AppendLine(Line(v));
        if (!v.Met) sb.AppendLine("You have not met them yet: talk_to introduces you.");

        if (book.LastGift.TryGetValue(npc.Name, out var last))
            sb.AppendLine($"Your last gift to them: {ItemName(last.ItemId)} on farm day {last.Day}; they {TasteVerb(last.Taste)} it ({Signed(last.Points)} points).");
        var seen = NpcKnowledge.SeenText(npc.Name, viewerSeat);
        if (seen != null) sb.AppendLine("Seen: " + seen + ".");

        if (book.Tastes.TryGetValue(npc.Name, out var known) && known.Count > 0)
        {
            sb.AppendLine("What you found out about their tastes by gifting:");
            foreach (var word in new[] { "loved", "liked", "neutral", "disliked", "hated" })
            {
                var names = known.Where(kv => TasteWord(kv.Value) == word).Select(kv => ItemName(kv.Key)).OrderBy(x => x).ToList();
                if (names.Count > 0) sb.AppendLine($"  {word}: {string.Join(", ", names)}");
            }
        }
        else
            sb.AppendLine("You do not know any of their tastes yet: gifts show them, and other players may tell you (or lie).");

        var stacks = who.Items.OfType<SObject>().GroupBy(i => i.QualifiedItemId).ToList();
        if (stacks.Count > 0)
        {
            sb.AppendLine(revealTastes ? "Their reaction to what you carry (RevealGiftTastes is on, straight from the game data):" : "Their reaction to what you carry:");
            foreach (var g in stacks)
            {
                var first = g.First();
                string taste;
                if (SpecialGifts.Contains(first.Name))
                    taste = "a special item, not a normal gift";
                else if (revealTastes)
                    taste = TasteWord((int)npc.getGiftTasteForThisItem(first));
                else if (NpcKnowledge.TryTaste(book, npc.Name, g.Key, out var code))
                    taste = TasteWord(code) + " (you gave it before)";
                else
                    taste = "unknown (give one to find out)";
                sb.AppendLine($"  {first.DisplayName} x{g.Sum(i => i.Stack)}: {taste}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ places

    /// <summary>
    /// Turn what the AI typed into a map name: a real map first, then a shop keeper's name (Pierre = SeedShop),
    /// then any villager (where they are now). Map is null when nothing fits; Note says what was assumed.
    /// </summary>
    public static (string? Map, string Note) ResolvePlace(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return (null, "");
        if (Game1.getLocationFromName(input) != null) return (input, "");

        var key = input.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ? input[..^2] : input;
        if (PlaceAliases.TryGetValue(key, out var shop) && Game1.getLocationFromName(shop) != null)
            return (shop, $"{key} works in {shop}.");

        var npc = ByName(key);
        if (npc?.currentLocation != null)
            return (npc.currentLocation.Name, $"{npc.displayName} was in {WhereIs(npc)} when you set off; they may have moved.");
        return (null, "");
    }

    /// <summary>
    /// When the door tile is a timed door ("LockedDoorWarp x y Map open close") and it is outside those hours,
    /// a sentence naming the opening hours; otherwise "". Best effort: the action text is parsed as the game writes it.
    /// </summary>
    public static string DoorHours(GameLocation loc, Point doorTile, string next)
    {
        try
        {
            var action = loc.doesTileHaveProperty(doorTile.X, doorTile.Y, "Action", "Buildings");
            if (action == null) return "";
            var parts = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6 || !parts[0].Equals("LockedDoorWarp", StringComparison.OrdinalIgnoreCase)) return "";
            if (!int.TryParse(parts[4], out var open) || !int.TryParse(parts[5], out var close)) return "";
            int now = Game1.timeOfDay;
            if (now >= open && now < close) return "";
            return $"{next} is closed now (it is {WorldText.FormatTime(now)}; it opens at {WorldText.FormatTime(open)} and closes at {WorldText.FormatTime(close)}). ";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ who is watching

    /// <summary>Other AI seats whose farmer stands on the same map within 12 tiles of <paramref name="self"/>: they can see what it does.</summary>
    public static string[] WhoCanSee(string mySeat, Farmer self)
    {
        var seen = new List<string>();
        foreach (var seat in Players.Slots.Where(s => s.Role == SeatRole.Ai && s.Id != mySeat && s.Active))
        {
            var name = (seat.State as ScreenState)?.Name;
            if (name == null) continue;
            var other = Game1.getOnlineFarmers().FirstOrDefault(f => f.Name == name);
            if (other?.currentLocation == null || self.currentLocation == null) continue;
            if (other.currentLocation.NameOrUniqueName != self.currentLocation.NameOrUniqueName) continue;
            if (Vector2.Distance(other.Tile, self.Tile) <= 12f) seen.Add(seat.Id);
        }
        return seen.ToArray();
    }
}

/// <summary>villagers() and the people-aware talk_to (#9). New tools live here, registered in <see cref="AgentTools.All"/>.</summary>
public static partial class AgentTools
{
    internal static List<ToolDef> VillagerTools() => new()
    {
        new()
        {
            Name = "villagers",
            Description = "Everyone you can befriend: where they are right now (map and tile), hearts and friendship points, whether you already talked to them today, " +
                          "gifts given today and this week (max 1 per day and 2 per week), birthday (flagged when it is today or within 3 days) and whether you are dating or married. " +
                          "With name: also your last gift to them, what you found out about their tastes and what they would make of the items you carry. " +
                          "Tastes are discovered by giving gifts, nobody knows them from the start. Costs no game time.",
            Schema = Obj(new { name = Str("Optional villager name for details, e.g. Abigail") }),
            Instant = true, AppendStatus = false,
            Run = VillagersTool
        },
        new()
        {
            Name = "talk_to",
            Description = "Find a villager, walk to them and talk. Optionally give them an item from your inventory as a gift. " +
                          "If they are on another map you travel there first (go=false: just tell me where they are). " +
                          "Reports where they were, your friendship points before and after, their first line and the reaction to a gift. " +
                          "Talking counts once a day; at most 1 gift per day and 2 per week. Time spent travelling is game time.",
            Schema = Obj(new
            {
                name = Str("Villager name, e.g. Pierre"),
                gift_item = Str("Optional item name to give as a gift"),
                go = new { type = "boolean", description = "Travel to them when they are on another map (default true)" },
                reason = Str(ReasonDesc)
            }, "name", "reason"),
            Run = TalkToNpc
        }
    };

    /// <summary>Not an MCP tool: the runner reaches it through GET /api/relationships, which queues it on each seat's observation lane.</summary>
    internal static ToolDef RelationshipsOp() => new()
    {
        Name = "relationships_report",
        Description = "Friendship data of this seat's farmer as JSON (runner only).",
        Schema = Obj(new { }),
        Instant = true, AppendStatus = false, Mail = false,
        Run = (a, c) => Instant(c, JsonSerializer.Serialize(NpcStory.Report(P, Rt.Log.Player), AgentLog.Json))
    };

    private static IEnumerator VillagersTool(JsonElement a, CallContext c)
    {
        var name = S(a, "name").Trim();
        if (name.Length == 0)
        {
            c.Result = NpcInfo.Roster(P);
            yield break;
        }
        var npc = NpcInfo.ByName(name);
        if (npc == null)
        {
            c.Fail($"No villager called \"{name}\". Call villagers() without a name for the list.");
            yield break;
        }
        c.Result = NpcInfo.Detail(P, npc, Rt.Log.Player, Config.RevealGiftTastes);
    }

    private static bool NpcFlag(JsonElement a, string name, bool def)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) return def;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => def };
    }

    /// <summary>Game minutes since the farm began; for "time spent" reports.</summary>
    private static int NpcMinutes() => Game1.Date.TotalDays * 1440 + Utility.ConvertTimeToMinutes(Game1.timeOfDay);

    private static (int Points, bool Talked, int GiftsToday) NpcFriendship(string name) =>
        P.friendshipData.TryGetValue(name, out var f) ? (f.Points, f.TalkedToToday, f.GiftsToday) : (0, false, 0);

    /// <summary>Why talking or gifting is pointless right now, or null. Only refuses what the game would refuse for sure.</summary>
    private static string? TalkRefusal(NPC npc, Item? gift)
    {
        if (!P.friendshipData.TryGetValue(npc.Name, out var f)) return null; // not met yet: the first talk introduces you
        if (gift == null)
        {
            return f.TalkedToToday
                ? $"You already talked to {npc.displayName} today: talking gives friendship once a day. Give a gift (gift_item) or come back tomorrow."
                : null;
        }
        if (f.GiftsToday >= 1)
            return $"You already gave {npc.displayName} a gift today (limit 1 per day). Try again tomorrow.";
        // Birthdays and spouses are exempt from the weekly limit in the vanilla game, so the game decides those.
        bool spouse = f.Status.ToString().Equals("Married", StringComparison.OrdinalIgnoreCase);
        bool birthday = NpcInfo.Of(P, npc).DaysToBirthday == 0;
        if (f.GiftsThisWeek >= 2 && !spouse && !birthday)
            return $"You already gave {npc.displayName} 2 gifts this week (the limit; it starts over on Monday). Talk to them instead, or wait.";
        return null;
    }

    private static IEnumerator TalkToNpc(JsonElement a, CallContext c)
    {
        var name = S(a, "name").Trim();
        if (name.Length == 0) { c.Fail("name is empty. villagers() lists who you can talk to."); yield break; }
        if (Game1.activeClickableMenu != null)
        {
            c.Fail("A menu or dialogue is open. Finish it with respond() or close_menu first.");
            yield break;
        }
        var npc = NpcInfo.ByName(name);
        if (npc == null) { c.Fail($"No villager called \"{name}\". villagers() lists who you can talk to."); yield break; }
        bool go = NpcFlag(a, "go", true);

        Item? giftItem = null;
        int giftSlot = -1;
        var gift = S(a, "gift_item").Trim();
        if (gift.Length > 0)
        {
            giftSlot = FindItem(gift, it => it is SObject);
            if (giftSlot < 0) { c.Fail($"No \"{gift}\" in inventory to gift."); yield break; }
            giftItem = P.Items[giftSlot];
        }

        var refusal = TalkRefusal(npc, giftItem);
        if (refusal != null) { c.Fail(refusal); yield break; }

        int startMinutes = NpcMinutes();
        bool traveled = false, opened = false;
        string failure = "";
        var before = (Points: 0, Talked: false, GiftsToday: 0);
        int tasteCode = -1;
        bool birthdayToday = false;

        // Find them (they walk around, so look them up again after every trip or failed approach), go to them, talk.
        for (int cycle = 0; cycle < 3 && !opened; cycle++)
        {
            npc = NpcInfo.ByName(name) ?? npc;
            if (!NpcInfo.OnMap(npc, Loc))
            {
                if (npc.currentLocation == null) { failure = "they can't be found right now"; break; }
                if (!go)
                {
                    c.Fail($"{npc.displayName} is not on this map ({Loc.Name}). {npc.displayName} is in {NpcInfo.WhereIs(npc)}. " +
                           $"travel_to {npc.currentLocation.Name}, or call talk_to with go=true to be taken there.");
                    yield break;
                }
                var trip = new CallContext();
                yield return TravelTo(npc.currentLocation.Name, trip);
                traveled = true;
                npc = NpcInfo.ByName(name) ?? npc;
                if (!NpcInfo.OnMap(npc, Loc))
                {
                    if (!trip.Ok) { failure = $"couldn't reach {npc.displayName} in {NpcInfo.WhereIs(npc)}: {trip.Result}"; break; }
                    failure = $"{npc.displayName} moved on to {NpcInfo.WhereIs(npc)}";
                    continue;
                }
            }

            for (int attempt = 0; attempt < 4 && !opened; attempt++)
            {
                npc = NpcInfo.ByName(name) ?? npc;
                if (!NpcInfo.OnMap(npc, Loc)) break; // they left this map: the outer loop follows them
                var walk = new CallContext();
                yield return WalkAdjacent(npc.TilePoint, walk);
                if (!walk.Ok) { failure = walk.Result.TrimEnd('.'); continue; }
                var me = P.TilePoint;
                if (Math.Abs(me.X - npc.TilePoint.X) + Math.Abs(me.Y - npc.TilePoint.Y) != 1) continue; // they moved
                Face(npc.TilePoint);
                if (giftSlot >= 0) P.CurrentToolIndex = giftSlot;
                else if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();

                before = NpcFriendship(npc.Name);
                if (giftItem != null)
                {
                    tasteCode = (int)npc.getGiftTasteForThisItem(giftItem);
                    birthdayToday = NpcInfo.Of(P, npc).DaysToBirthday == 0;
                }
                Game1.tryToCheckAt(npc.TilePoint.ToVector2(), P);
                yield return Ticks(20);
                opened = Game1.activeClickableMenu != null || Game1.dialogueUp;
            }
        }

        int spent = NpcMinutes() - startMinutes;
        var spentText = spent >= 10 ? $" Took {spent} game minutes (now {WorldText.FormatTime(Game1.timeOfDay)})." : "";
        if (!opened)
        {
            npc = NpcInfo.ByName(name) ?? npc;
            var why = failure.Length > 0 ? failure : $"they may be busy, asleep or moving; {npc.displayName} is in {NpcInfo.WhereIs(npc)} now";
            c.Fail($"Couldn't start a conversation with {npc.displayName} ({why}).{spentText}");
            yield break;
        }

        var after = NpcFriendship(npc.Name);
        int delta = after.Points - before.Points;
        var sb = new StringBuilder($"Talking to {npc.displayName} (friendship {P.getFriendshipHeartLevelForNPC(npc.Name)} hearts).");
        if (traveled) sb.Append($" You went to {Loc.Name}.");
        sb.Append($" Points {before.Points} -> {after.Points} ({NpcInfo.Signed(delta)}).");

        bool accepted = false;
        if (giftItem != null)
        {
            accepted = after.GiftsToday > before.GiftsToday || delta != 0;
            var itemName = giftItem.DisplayName;
            if (!accepted)
                sb.Append($" The gift ({itemName}) was not accepted.");
            else if (NpcInfo.SpecialGifts.Contains(giftItem.Name))
            {
                sb.Append($" You gave {itemName}.");
                if (giftItem.Name.Equals("Bouquet", StringComparison.OrdinalIgnoreCase))
                    NpcStory.Emit(Rt.Log, "bouquet_given", $"{P.Name} gave {npc.displayName} a bouquet", new { villager = npc.Name });
            }
            else
            {
                bool firstTime = !NpcKnowledge.TryTaste(NpcKnowledge.Book(P), npc.Name, giftItem.QualifiedItemId, out _);
                sb.Append($" Gift: {npc.displayName} {NpcInfo.TasteVerb(tasteCode)} {itemName}{(firstTime ? " (now you know this taste)" : "")}.");
                NpcKnowledge.Learn(P, npc.Name, giftItem.QualifiedItemId, tasteCode, AgentLog.FarmDay, delta);
                NpcStory.Emit(Rt.Log, "gift", $"{P.Name} gave {npc.displayName} {itemName}: {NpcInfo.TasteWord(tasteCode)}",
                    new { villager = npc.Name, item = itemName, taste = NpcInfo.TasteWord(tasteCode), points = delta, birthday = birthdayToday });
                if (birthdayToday)
                    NpcStory.Emit(Rt.Log, "birthday", $"{P.Name} gave {npc.displayName} a birthday gift ({itemName})", new { villager = npc.Name, item = itemName, points = delta });
            }
        }

        if (accepted || (!before.Talked && after.Talked))
            NpcKnowledge.RecordPublic(npc.Name, new PublicTalk(AgentLog.FarmDay, WorldText.FormatTime(Game1.timeOfDay), Rt.Log.Player,
                P.Name, accepted ? "gift" : "talk", NpcInfo.WhoCanSee(Rt.Log.Player, P)));

        if (Game1.activeClickableMenu is DialogueBox box)
        {
            var line = (box.getCurrentString() ?? "").Replace("\n", " ").Replace("^", " ").Trim();
            if (line.Length > 0) sb.Append($" {npc.displayName} says: \"{line}\"");
        }
        sb.Append(spentText);
        c.Result = sb.ToString();
    }
}
