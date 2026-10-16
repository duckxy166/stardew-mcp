using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace StardewMCP.Agent;

/// <summary>
/// watch_tv: use the TV in the room you are in. interact opens the channel question, the dialogue tool answers it, and
/// every page the TV shows is read back to the AI. The Queen of Sauce teaches a cooking recipe on her day; the tool says
/// which recipe is new afterwards. The weather and fortune channels overlap the outlook of the calendar tool (#11);
/// the TV stays the way to learn tomorrow's weather and luck in the game's own words.
/// </summary>
public static partial class AgentTools
{
    private static List<ToolDef> TvToolDefs() => new()
    {
        new()
        {
            Name = "watch_tv",
            Description = "Watch a channel on the TV in the room you are in (your farmhouse has one). Channels: weather (tomorrow's forecast), fortune (tomorrow's luck), " +
                          "queen of sauce (airs on Sundays; teaches a cooking recipe), livin' off the land (farming tips). Leave channel empty to list the channels on right now. " +
                          "Watching takes a little game time and costs no energy. The result gives the text the TV showed and any recipe you learned.",
            Schema = Obj(new
            {
                channel = Str("weather, fortune, queen (of sauce) or livin (off the land); empty lists the channels"),
                reason = Str(ReasonDesc)
            }),
            Run = WatchTv
        }
    };

    /// <summary>Index of the channel in the TV's answers: by alias first (weather, luck, recipe...), else by words of the text.</summary>
    private static int TvPick(IList<string> options, string channel)
    {
        var want = channel.Trim().ToLowerInvariant();
        if (want.Length == 0) return -1;
        bool Mentions(params string[] words) => words.Any(w => want.Contains(w));
        string[] keys;
        if (Mentions("weather", "forecast", "rain")) keys = new[] { "weather" };
        else if (Mentions("fortune", "luck", "spirit")) keys = new[] { "fortune" };
        else if (Mentions("queen", "sauce", "recipe", "cook")) keys = new[] { "queen", "sauce" };
        else if (Mentions("livin", "land", "tip", "farming")) keys = new[] { "livin", "land" };
        else keys = new[] { want };
        for (int i = 0; i < options.Count; i++)
        {
            var text = options[i].ToLowerInvariant();
            if (text.Contains("leave")) continue;
            if (keys.Any(k => text.Contains(k))) return i;
        }
        return -1;
    }

    private static int TvLeave(IList<string> options)
    {
        for (int i = options.Count - 1; i >= 0; i--)
            if (options[i].Contains("leave", StringComparison.OrdinalIgnoreCase))
                return i;
        return options.Count - 1;
    }

    private static IEnumerator WatchTv(JsonElement a, CallContext c)
    {
        var channel = S(a, "channel").Trim();
        var tv = Loc.furniture.OfType<TV>().FirstOrDefault();
        if (tv == null)
        {
            c.Fail($"There is no TV in {Loc.Name}. Your farmhouse has one.");
            yield break;
        }
        if (Game1.activeClickableMenu != null)
        {
            c.Fail("A menu or dialogue is open. close_menu or respond first.");
            yield break;
        }

        var box0 = tv.GetBoundingBox();
        var tile = new Point(box0.X / 64, box0.Y / 64);
        var recipesBefore = P.cookingRecipes.Keys.ToHashSet();
        var sub = new CallContext();
        yield return Interact(ArgsOf(new { x = tile.X, y = tile.Y, reason = Reason(a) }), sub);
        if (!sub.Ok)
        {
            c.Fail(sub.Result);
            yield break;
        }
        for (int i = 0; i < 90 && Game1.activeClickableMenu is not DialogueBox; i++) yield return null;
        if (Game1.activeClickableMenu is not DialogueBox menu || !menu.isQuestion || menu.responses == null)
        {
            if (Game1.activeClickableMenu != null) yield return CloseMenu(default, new CallContext());
            c.Fail("The TV did not show its channel list (it may be off or another player is using it).");
            yield break;
        }

        var options = menu.responses.Select(r => r.responseText).ToList();
        int pick = TvPick(options, channel);
        if (channel.Length == 0 || pick < 0)
        {
            // Nothing (valid) chosen: report the channels and switch the TV off again.
            yield return Respond(ArgsOf(new { option = TvLeave(options) }), new CallContext());
            if (Game1.activeClickableMenu != null) yield return CloseMenu(default, new CallContext());
            var listing = string.Join(", ", options.Where(o => !o.Contains("leave", StringComparison.OrdinalIgnoreCase)));
            if (channel.Length == 0) c.Result = $"Channels on the TV right now: {listing}. Call watch_tv with one of them.";
            else c.Fail($"No channel like \"{channel}\" is on right now. On: {listing}.");
            yield break;
        }

        var chosen = options[pick];
        yield return Respond(ArgsOf(new { option = pick }), new CallContext());

        // Every page the TV shows is a dialogue box; read it, then press through to the next one.
        var pages = new List<string>();
        for (int step = 0; step < 60; step++)
        {
            int waited = 0;
            while (Game1.activeClickableMenu is not DialogueBox && waited < (pages.Count == 0 ? 300 : 120))
            {
                waited++;
                yield return null;
            }
            if (Game1.activeClickableMenu is not DialogueBox page)
                break;
            var text = Peek.Clean(page.getCurrentString());
            if (text.Length > 0 && (pages.Count == 0 || pages[^1] != text)) pages.Add(text);
            if (page.isQuestion)
            {
                // Not expected after a channel; leave rather than answer something unknown.
                yield return CloseMenu(default, new CallContext());
                break;
            }
            yield return Respond(ArgsOf(new { option = -1 }), new CallContext());
        }
        if (Game1.activeClickableMenu is DialogueBox) yield return CloseMenu(default, new CallContext());
        yield return WaitIdle(120);

        var learned = P.cookingRecipes.Keys.Where(k => !recipesBefore.Contains(k)).Select(k =>
        {
            try { return new CraftingRecipe(k, true).DisplayName; }
            catch (Exception) { return k; }
        }).ToList();
        if (pages.Count == 0 && learned.Count == 0)
        {
            c.Fail($"Watched {chosen}, but no readable text came up.");
            yield break;
        }
        var result = $"Watched {chosen}: " + string.Join(" / ", pages.Select(p => $"\"{p}\""));
        if (learned.Count > 0) result += $" You learned the recipe for {string.Join(", ", learned)}: cook lists it.";
        c.Result = result;
    }
}
