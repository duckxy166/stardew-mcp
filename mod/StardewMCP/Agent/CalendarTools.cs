using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>A festival on fixed days of the year. Hours are 0 for multi-day festivals that have no start event.</summary>
internal record FestivalInfo(string Season, int FirstDay, int LastDay, string Name, string Where, int Start, int End, string Tip);

/// <summary>A shop's counter hours. Closed days count from Monday = 0 to Sunday = 6.</summary>
internal record ShopInfo(string Name, string Location, int Open, int Close, int[] ClosedDays);

/// <summary>
/// Static calendar data (#11): the vanilla festival dates, villager birthdays and shop hours. They are fixed in
/// the base game, so they live in code instead of being read from the game's data. Only entries known for certain
/// are listed; the game's own doors stay the final word on whether a shop is open.
/// </summary>
internal static class CalendarData
{
    public static readonly string[] Seasons = { "spring", "summer", "fall", "winter" };
    public static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    public static readonly FestivalInfo[] Festivals =
    {
        new("spring", 13, 13, "Egg Festival", "Town", 900, 1400, "Egg hunt once Lewis starts it (most eggs wins a Straw Hat). Pierre's booth sells Strawberry Seeds."),
        new("spring", 15, 17, "Desert Festival", "Calico Desert", 0, 0, "Multi-day; needs the bus to the desert to be repaired."),
        new("spring", 24, 24, "Flower Dance", "Forest", 900, 1400, "Ask a villager to dance (talk_to), then Lewis starts the dance."),
        new("summer", 11, 11, "Luau", "Beach", 900, 1400, "Add one item to the soup pot (interact while holding it); a better item pleases the Governor more."),
        new("summer", 20, 21, "Trout Derby", "", 0, 0, "Multi-day fishing competition."),
        new("summer", 28, 28, "Dance of the Moonlight Jellies", "Beach", 2200, 2400, "A viewing event; Lewis starts it."),
        new("fall", 16, 16, "Stardew Valley Fair", "Town", 900, 1500, "The games use Star Tokens, bought with gold at the fair. Lewis judges the grange display."),
        new("fall", 27, 27, "Spirit's Eve", "Town", 2200, 2400, "A haunted maze with a Golden Pumpkin at its end."),
        new("winter", 8, 8, "Festival of Ice", "Forest", 900, 1400, "Ice-fishing contest at the lake."),
        new("winter", 12, 13, "Squidfest", "", 0, 0, "Multi-day fishing festival."),
        new("winter", 15, 17, "Night Market", "Beach", 0, 0, "Multi-day evening market on the beach."),
        new("winter", 25, 25, "Feast of the Winter Star", "Town", 900, 1400, "Secret gift exchange: you give a gift to your secret friend."),
    };

    public static readonly (string Name, string Season, int Day)[] Birthdays =
    {
        ("Kent", "spring", 4), ("Lewis", "spring", 7), ("Vincent", "spring", 10), ("Haley", "spring", 14),
        ("Pam", "spring", 18), ("Shane", "spring", 20), ("Pierre", "spring", 26), ("Emily", "spring", 27),
        ("Jas", "summer", 4), ("Gus", "summer", 8), ("Maru", "summer", 10), ("Alex", "summer", 13),
        ("Sam", "summer", 17), ("Demetrius", "summer", 19), ("Dwarf", "summer", 22), ("Willy", "summer", 24),
        ("Leo", "summer", 26), ("Penny", "fall", 2), ("Elliott", "fall", 5), ("Jodi", "fall", 11),
        ("Abigail", "fall", 13), ("Sandy", "fall", 15), ("Marnie", "fall", 18), ("Robin", "fall", 21),
        ("George", "fall", 24), ("Krobus", "winter", 1), ("Linus", "winter", 3), ("Caroline", "winter", 7),
        ("Sebastian", "winter", 10), ("Harvey", "winter", 14), ("Wizard", "winter", 17), ("Evelyn", "winter", 20),
        ("Leah", "winter", 23), ("Clint", "winter", 26),
    };

    /// <summary>Counter hours of the main shops in year 1. A shop whose hours are not known for certain is left out.</summary>
    public static readonly ShopInfo[] Shops =
    {
        new("Pierre's General Store", "SeedShop", 900, 1700, new[] { 2 }),
        new("Carpenter's Shop (Robin)", "ScienceHouse", 900, 1700, new[] { 1 }),
        new("Blacksmith (Clint)", "Blacksmith", 900, 1600, new[] { 4 }),
        new("Marnie's Ranch", "AnimalShop", 900, 1600, new[] { 0, 1 }),
        new("Willy's Fish Shop", "FishShop", 900, 1700, Array.Empty<int>()),
        new("Stardrop Saloon (Gus)", "Saloon", 1200, 2400, Array.Empty<int>()),
        new("Adventurer's Guild", "AdventureGuild", 1400, 2600, Array.Empty<int>()),
        new("JojaMart", "JojaMart", 900, 2300, Array.Empty<int>()),
    };

    public static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"9:00", "17:00", "midnight", "2:00 AM".</summary>
    public static string Clock(int t)
    {
        if (t == 2400) return "midnight";
        return $"{t / 100 % 24}:{t % 100:00}" + (t > 2400 ? " AM" : "");
    }

    /// <summary>The date <paramref name="ahead"/> days from today: season, day of month, year, weekday (Mon = 0).</summary>
    public static (string Season, int Day, int Year, int Weekday) DateAhead(int ahead)
    {
        int s = Math.Max(0, Array.FindIndex(Seasons, x => x.Equals(Game1.currentSeason, StringComparison.OrdinalIgnoreCase)));
        int index = (Game1.year - 1) * 112 + s * 28 + (Game1.dayOfMonth - 1) + ahead;
        int day = index % 28 + 1;
        return (Seasons[index / 28 % 4], day, index / 112 + 1, (day - 1) % 7); // day 1 of every season is a Monday
    }

    public static FestivalInfo? FestivalOn(string season, int day) =>
        Festivals.FirstOrDefault(f => f.Season == season && day >= f.FirstDay && day <= f.LastDay);

    public static FestivalInfo? Today()
    {
        var d = DateAhead(0);
        return FestivalOn(d.Season, d.Day);
    }

    public static IEnumerable<string> BirthdaysOn(string season, int day) =>
        Birthdays.Where(b => b.Season == season && b.Day == day).Select(b => b.Name);

    /// <summary>One festival for a text listing; <paramref name="now"/> is the clock time, or -1 for a future day.</summary>
    public static string Describe(FestivalInfo f, int now)
    {
        var where = f.Where.Length > 0 ? $" at {f.Where}" : "";
        if (f.Start <= 0)
            return $"{f.Name}{where}, {Cap(f.Season)} {f.FirstDay}{(f.LastDay > f.FirstDay ? "-" + f.LastDay : "")}. {f.Tip}";
        var hours = $"{Clock(f.Start)}-{Clock(f.End)}";
        var when = now < 0 ? hours : now < f.Start ? $"starts at {Clock(f.Start)}" : now < f.End ? $"in progress until {Clock(f.End)}" : "is over for today";
        return $"{f.Name}{where}, {when}" + (now < 0 ? "" : $" ({hours})") + $". {f.Tip}";
    }
}

/// <summary>
/// Calendar awareness (#11): the calendar tool, festival start/leave, selling to an open shop, and the helpers
/// status/look/talk_to use. Kept apart from the rest of <see cref="AgentTools"/>.
/// </summary>
public static partial class AgentTools
{
    private static IEnumerable<ToolDef> CalendarToolDefs() => new ToolDef[]
    {
        new()
        {
            Name = "calendar",
            Description = "Today and the next days (default 7): festivals, villager birthdays, the next festival, and each main shop's opening hours and closed days with whether it is open right now. " +
                          "Check it before a trip so you don't arrive at a closed door. Game time stays frozen.",
            Schema = Obj(new { days = Int("How many days ahead to list, 1-28 (default 7)") }),
            Instant = true, AppendStatus = false,
            Run = (a, c) => Instant(c, CalendarText(Math.Clamp(I(a, "days", 7), 1, 28)))
        },
        new()
        {
            Name = "festival",
            Description = "On a festival day, standing in the festival's map during its hours. start = walk to Lewis, talk to him and answer yes to begin the main event (egg hunt, dance, luau...); " +
                          "with several farmers the game waits until everyone is ready. leave = walk out of the festival to another map (default Farm) and confirm the leave question. " +
                          "Use talk_to for the festival's villagers and booths (shops) and interact with holding for the Luau soup pot. calendar shows when and where each festival is.",
            Schema = Obj(new
            {
                action = new { type = "string", @enum = new[] { "start", "leave" }, description = "start or leave" },
                to = Str("leave: destination map (default Farm)"),
                reason = Str(ReasonDesc)
            }, "action", "reason"),
            Run = FestivalTool
        },
        new()
        {
            Name = "sell",
            Description = "Sell items from your inventory to the open shop menu and get the gold right away (ship only pays overnight). Open a shop that buys the item first: interact with its counter during opening hours. " +
                          "The shop decides what it buys. Lowest quality is sold first. Close the menu when you are done.",
            Schema = Obj(new { item = Str("Item name"), quantity = Int("How many (default 1)"), reason = Str(ReasonDesc) }, "item", "reason"),
            Run = Sell
        },
    };

    // ------------------------------------------------------------------ calendar text

    private static string TomorrowWeather()
    {
        string id = Game1.weatherForTomorrow ?? "";
        return id switch
        {
            "Sun" => "sunny",
            "Rain" => "rain (crops are watered for you)",
            "Storm" => "storm (crops are watered for you)",
            "Wind" => "windy",
            "Snow" => "snow",
            "GreenRain" => "green rain",
            "Festival" => "sunny (festival day)",
            "Wedding" => "sunny (wedding day)",
            "" => "unknown",
            _ => id
        };
    }

    /// <summary>Today's daily luck as words. The game rolls luck each morning, so only today's value is known.</summary>
    private static string LuckText()
    {
        double luck = P.DailyLuck;
        var (label, advice) = luck switch
        {
            > 0.07 => ("very good", "a good day for mining and fishing"),
            > 0.02 => ("good", "a good day for mining and fishing"),
            >= -0.02 => ("average", "no luck bonus or penalty"),
            >= -0.07 => ("bad", "mining and fishing go worse; favor farm chores"),
            _ => ("very bad", "mining and fishing go worse; favor farm chores")
        };
        return $"{label} ({luck.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}), {advice}";
    }

    /// <summary>The lines status adds under its date line: a festival today, tomorrow's weather and today's luck.</summary>
    internal static string OutlookLine()
    {
        var sb = new StringBuilder();
        var fest = CalendarData.Today();
        if (fest != null) sb.AppendLine($"Festival today: {CalendarData.Describe(fest, Game1.timeOfDay)} (calendar has more)");
        sb.Append($"Tomorrow: {TomorrowWeather()} · Luck today: {LuckText()}");
        return sb.ToString();
    }

    private static string CalendarText(int days)
    {
        var sb = new StringBuilder();
        var today = CalendarData.DateAhead(0);
        int now = Game1.timeOfDay;
        sb.AppendLine($"Today is {CalendarData.Cap(today.Season)} {today.Day}, year {today.Year} ({CalendarData.DayNames[today.Weekday]}), {WorldText.FormatTime(now)}. " +
                      $"Weather: {WorldText.Weather()} today, {TomorrowWeather()} tomorrow.");

        var fest = CalendarData.FestivalOn(today.Season, today.Day);
        if (fest != null)
        {
            sb.AppendLine("Festival today: " + CalendarData.Describe(fest, now));
            if (fest.Start > 0) sb.AppendLine("  Villagers go to the festival, so most shops are usually closed while it runs.");
        }
        foreach (var who in CalendarData.BirthdaysOn(today.Season, today.Day))
            sb.AppendLine($"Birthday today: {who}. A gift they like counts 8 times for friendship.");

        sb.AppendLine("Shops (counter hours; the game's doors are the final word, a locked door stays locked outside hours):");
        foreach (var shop in CalendarData.Shops)
        {
            var closed = shop.ClosedDays.Length > 0 ? ", closed " + string.Join(" and ", shop.ClosedDays.Select(d => CalendarData.DayNames[d])) : "";
            string status = shop.ClosedDays.Contains(today.Weekday) ? "CLOSED today"
                : now < shop.Open ? $"opens at {CalendarData.Clock(shop.Open)}"
                : now >= shop.Close ? "closed for the rest of the day"
                : $"open now, closes at {CalendarData.Clock(shop.Close)}";
            sb.AppendLine($"  {shop.Name} ({shop.Location}): {CalendarData.Clock(shop.Open)}-{CalendarData.Clock(shop.Close)}{closed}. Today: {status}");
        }

        sb.AppendLine($"Coming up (next {days} days):");
        int listed = 0;
        for (int k = 1; k <= days; k++)
        {
            var d = CalendarData.DateAhead(k);
            var parts = new List<string>();
            foreach (var starting in CalendarData.Festivals.Where(x => x.Season == d.Season && x.FirstDay == d.Day))
                parts.Add("FESTIVAL " + CalendarData.Describe(starting, -1));
            foreach (var who in CalendarData.BirthdaysOn(d.Season, d.Day))
                parts.Add($"{who}'s birthday");
            if (parts.Count == 0) continue;
            listed++;
            sb.AppendLine($"  {CalendarData.DayNames[d.Weekday]} {CalendarData.Cap(d.Season)} {d.Day} (in {k} day{(k == 1 ? "" : "s")}): {string.Join("; ", parts)}");
        }
        if (listed == 0) sb.AppendLine("  No festivals or birthdays.");

        for (int k = 1; k <= 112; k++)
        {
            var d = CalendarData.DateAhead(k);
            var next = CalendarData.Festivals.FirstOrDefault(x => x.Season == d.Season && x.FirstDay == d.Day);
            if (next == null) continue;
            sb.AppendLine($"Next festival: {next.Name}, {CalendarData.DayNames[d.Weekday]} {CalendarData.Cap(d.Season)} {d.Day}, in {k} day{(k == 1 ? "" : "s")}. {CalendarData.Describe(next, -1)}");
            break;
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ festivals

    private static readonly NPC[] NoActors = Array.Empty<NPC>();

    /// <summary>The villagers of a festival in progress. At a festival they are in the event's actor list, not in the map's characters.</summary>
    internal static IEnumerable<NPC> FestivalActors()
    {
        if (Game1.eventUp && Game1.CurrentEvent is { isFestival: true } ev)
            return ev.actors;
        return NoActors;
    }

    internal static NPC? FestivalActor(string name) =>
        FestivalActors().FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || n.displayName.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Press the action button on a festival actor through the festival event itself (used when the plain tile check did nothing).</summary>
    internal static bool FestivalAction(Point tile)
    {
        if (Game1.eventUp && Game1.CurrentEvent is { isFestival: true } ev)
            return ev.checkAction(new xTile.Dimensions.Location(tile.X, tile.Y), Game1.viewport, P);
        return false;
    }

    /// <summary>What status shows while a festival is on, instead of the generic cutscene line; null outside festivals.</summary>
    internal static string? FestivalLine()
    {
        if (!(Game1.eventUp && Game1.CurrentEvent is { isFestival: true })) return null;
        var name = CalendarData.Today()?.Name ?? "festival";
        return $"A festival is in progress ({name}). talk_to its villagers and booths, festival(start) starts the main event with Lewis, festival(leave) walks out.";
    }

    private static string NoFestivalNow()
    {
        var f = CalendarData.Today();
        if (f != null && f.Start > 0)
            return $"No festival is running where you are. Today is the {f.Name}: be at {f.Where} between {CalendarData.Clock(f.Start)} and {CalendarData.Clock(f.End)} (travel_to {f.Where}).";
        return "No festival is running. calendar shows the next one.";
    }

    private static JsonElement FestivalJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Index of the "Yes" option of a question dialogue, or -1.</summary>
    private static int FestivalYesOption(DialogueBox box)
    {
        var options = box.responses;
        if (options == null) return -1;
        for (int i = 0; i < options.Length; i++)
        {
            var key = options[i].responseKey ?? "";
            var text = (options[i].responseText ?? "").Trim().TrimEnd('.', '!');
            if (key.Equals("yes", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static IEnumerator FestivalTool(JsonElement a, CallContext c)
    {
        var action = S(a, "action").Trim().ToLowerInvariant();
        if (action != "start" && action != "leave") { c.Fail("action must be \"start\" or \"leave\"."); yield break; }
        if (!(Game1.eventUp && Game1.CurrentEvent is { isFestival: true })) { c.Fail(NoFestivalNow()); yield break; }
        if (action == "start") yield return FestivalStart(c);
        else yield return FestivalLeave(a, c);
    }

    /// <summary>Talk to Lewis and answer yes. In multiplayer the game then opens a ready check that waits for every farmer.</summary>
    private static IEnumerator FestivalStart(CallContext c)
    {
        bool saidYes = false;
        if (Game1.activeClickableMenu is not ReadyCheckDialog)
        {
            if (Game1.activeClickableMenu == null)
            {
                var talk = new CallContext();
                yield return TalkTo(FestivalJson("{\"name\":\"Lewis\"}"), talk);
                if (!talk.Ok) { c.Fail($"{talk.Result} Lewis hosts the festival: be in the festival's map during its hours."); yield break; }
            }
            for (int step = 0; step < 8 && Game1.activeClickableMenu is DialogueBox box; step++)
            {
                var answer = new CallContext();
                if (box.isQuestion)
                {
                    int yes = FestivalYesOption(box);
                    if (yes < 0) break; // a question without a yes: the AI answers it with respond
                    saidYes = true;
                    yield return Respond(FestivalJson($"{{\"option\":{yes}}}"), answer);
                }
                else
                    yield return Respond(FestivalJson("{}"), answer);
                if (!answer.Ok) break;
            }
        }

        // The ready check closes when every farmer has said yes (or someone cancels).
        for (int i = 0; i < 1200 && Game1.activeClickableMenu is ReadyCheckDialog; i++) yield return null;
        if (Game1.activeClickableMenu is ReadyCheckDialog)
            c.Result = "Waiting for the other players to be ready (ready check). Call festival(start) again to keep waiting, or close_menu to cancel.";
        else if (saidYes)
            c.Result = ("You told Lewis you are ready. " + WorldText.MenuState().Trim()).Trim();
        else
            c.Result = ("You talked to Lewis; there was no yes/no question to answer. " + WorldText.MenuState().Trim()).Trim();
    }

    /// <summary>Walk out like travel_to does; the game asks "leave the festival?" on the way, which is answered yes.</summary>
    private static IEnumerator FestivalLeave(JsonElement a, CallContext c)
    {
        var dest = S(a, "to").Trim();
        if (dest.Length == 0) dest = "Farm";
        if (Game1.getLocationFromName(dest) == null) { c.Fail($"Unknown location \"{dest}\"."); yield break; }
        if (Game1.activeClickableMenu != null) { c.Fail("Close the open menu or dialogue first (close_menu or respond)."); yield break; }

        var start = Loc;
        var walk = new CallContext();
        yield return TravelTo(dest, walk);
        for (int i = 0; i < 3 && Loc == start && Game1.activeClickableMenu is DialogueBox { isQuestion: true } box; i++)
        {
            int yes = FestivalYesOption(box);
            if (yes < 0) break;
            yield return Respond(FestivalJson($"{{\"option\":{yes}}}"), new CallContext());
            for (int t = 0; t < 300 && Loc == start; t++) yield return null;
            yield return WaitIdle();
        }
        if (Loc != start && !Loc.Name.Equals(dest, StringComparison.OrdinalIgnoreCase))
            yield return TravelTo(dest, walk = new CallContext());
        if (Loc == start)
        {
            c.Fail($"Still in the festival. {(walk.Ok ? "" : walk.Result + " ")}{WorldText.MenuState().Trim()}".Trim());
            yield break;
        }
        c.Result = $"Left the festival. You are now in {Loc.Name} at ({P.TilePoint.X},{P.TilePoint.Y}).";
    }

    // ------------------------------------------------------------------ sell

    private static int SellCount(string qualifiedId) =>
        P.Items.Where(i => i != null && i.QualifiedItemId == qualifiedId).Sum(i => i.Stack);

    /// <summary>
    /// Sells through the shop menu itself: a click on the inventory slot is the game's own sell path (the shop pays,
    /// buyback and sounds included). A whole stack is a left click; fewer items are right clicks, one item each.
    /// </summary>
    private static IEnumerator Sell(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu is not ShopMenu shop) { c.Fail("No shop is open. interact with a shop counter (A tile) during opening hours first."); yield break; }
        if (shop.readOnly) { c.Fail("This shop menu is read-only; nothing can be sold here."); yield break; }
        var name = S(a, "item").Trim();
        int want = Math.Clamp(I(a, "quantity", 1), 1, 999);

        // Move whatever the shop menu is holding back into the inventory before clicking slots.
        void PutAwayHeld()
        {
            if (shop.heldItem is not Item held) return;
            shop.heldItem = null;
            if (P.addItemToInventory(held) is Item rest)
                Game1.createItemDebris(rest, P.getStandingPosition(), P.FacingDirection);
        }
        PutAwayHeld();

        bool Sellable(Item it) => it != null && shop.highlightItemToSell(it);
        int first = FindItem(name, Sellable);
        if (first < 0)
        {
            int any = FindItem(name);
            if (any < 0) { c.Fail($"No \"{name}\" in inventory."); yield break; }
            var buys = P.Items.Where(Sellable).Select(i => i.DisplayName).Distinct().Take(15).ToList();
            c.Fail($"{P.Items[any].DisplayName} can't be sold in this shop. " +
                   (buys.Count > 0 ? $"You can sell here: {string.Join(", ", buys)}." : "Nothing in your inventory can be sold here."));
            yield break;
        }

        var id = P.Items[first].QualifiedItemId;
        var display = P.Items[first].DisplayName;
        int moneyBefore = ShopMenu.getPlayerCurrencyAmount(P, shop.currency);
        int sold = 0;
        bool singleBlocked = false;
        while (sold < want && Game1.activeClickableMenu == shop)
        {
            // The lowest quality first, so better items stay for gifts and bundles.
            int slot = -1;
            for (int i = 0; i < P.Items.Count; i++)
            {
                var it = P.Items[i];
                if (it == null || it.QualifiedItemId != id || !shop.highlightItemToSell(it)) continue;
                if (slot < 0 || it.Quality < P.Items[slot].Quality) slot = i;
            }
            if (slot < 0) break;
            var cc = shop.inventory.inventory.FirstOrDefault(s => s.name == slot.ToString());
            if (cc == null) break;

            bool whole = want - sold >= P.Items[slot].Stack;
            int before = SellCount(id);
            Click(shop, cc, right: !whole);
            yield return null;
            int gone = before - SellCount(id);
            if (gone <= 0) { singleBlocked = !whole; break; }
            sold += gone;
        }
        PutAwayHeld();
        yield return Ticks(10);

        var cur = CurrencyUnit(shop.currency);
        int money = ShopMenu.getPlayerCurrencyAmount(P, shop.currency);
        if (sold == 0)
        {
            c.Fail(singleBlocked
                ? $"The shop did not take a single {display}; it may sell whole stacks only. Try quantity {P.Items[first].Stack} (the whole stack)."
                : $"Nothing was sold: {display} is not taken by this shop right now.");
            yield break;
        }
        var shortNote = sold < want ? $" ({want} asked; you had {sold} that this shop takes)" : "";
        c.Result = $"Sold {sold}x {display} for {money - moneyBefore}{cur}.{shortNote} Money now {money}{cur}.";
    }
}
