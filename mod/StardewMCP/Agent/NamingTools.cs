using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Naming boxes (#53): a new pet, farm animal, horse or child opens a <see cref="NamingMenu"/>. The AI names it with
/// name(text) instead of getting a random name. A box nobody answers is named at random at bedtime (go_to_bed), at
/// 1:00 AM, or when the seat has no AI or its AI has not answered for the thinking timeout plus a minute. This is the
/// same fallback idea as <see cref="Professions"/>.
/// </summary>
public static class Naming
{
    /// <summary>Game clock (HHMM; 2500 is 1:00 AM) after which a naming box nobody answered gets a random name.</summary>
    public const int LastCall = 2500;

    /// <summary>The longest name name(text) accepts.</summary>
    public const int MaxLength = 20;

    private sealed class Watch
    {
        public NamingMenu? Menu;
        public int Seconds;
    }

    // Per seat: the box we are currently waiting on and how many seconds it has been open.
    private static readonly ConcurrentDictionary<string, Watch> Open = new();

    /// <summary>What is being named, from the box's title ("Name your new calf:" becomes "Name your new calf").</summary>
    internal static string What(NamingMenu nm)
    {
        var title = (nm.title ?? "").Replace("\n", " ").Replace("^", " ").Trim().TrimEnd(':').Trim();
        return title.Length > 0 ? title : "something new";
    }

    /// <summary>The "Naming:" line in status and in every tool result's footer while a box is open.</summary>
    internal static string StatusLine(NamingMenu nm)
    {
        var typed = nm.textBox?.Text ?? "";
        return $"Naming: \"{What(nm)}\"{(typed.Length > 0 ? $" (the box says \"{typed}\")" : "")}. " +
               "Use name(text) to choose the name; if you don't, it gets a random name at bedtime.";
    }

    internal static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

    /// <summary>
    /// Called about once a second, on the seat's own screen, while a <see cref="NamingMenu"/> is open (from
    /// <see cref="AgentRuntime"/>). It does not name anything unless the fallback applies.
    /// </summary>
    internal static void Observe(AgentRuntime rt, NamingMenu nm)
    {
        var seat = rt.Log.Player;
        var watch = Open.GetOrAdd(seat, _ => new Watch());
        if (!ReferenceEquals(watch.Menu, nm))
        {
            watch.Menu = nm;
            watch.Seconds = 0;
            rt.Log.Add("system", text: $"Naming box opened: \"{What(nm)}\"");
        }
        watch.Seconds++;

        var slot = Players.Current;
        if (slot.Role != SeatRole.Ai || !slot.Connected)
        {
            AutoName(rt, nm, "no AI on this seat");
            return;
        }

        // The overnight sequence (a baby's name) is answered through go_to_bed, which hands the box to the AI.
        bool sleeping = Game1.player.isInBed.Value || Game1.newDay || rt.CurrentTool == "go_to_bed";
        if (!sleeping && Game1.timeOfDay >= LastCall)
        {
            AutoName(rt, nm, "not named before 1:00 AM");
            return;
        }
        int limit = rt.Config.ThinkTimeoutSeconds;
        if (limit > 0 && watch.Seconds > limit + 60)
            AutoName(rt, nm, $"nobody answered in {limit + 60}s");
    }

    /// <summary>Give the open box a random name (the game's own name list) and log why.</summary>
    internal static void AutoName(AgentRuntime rt, NamingMenu nm, string why) =>
        Complete(rt, nm, StardewValley.Dialogue.randomName(), chosen: false, why);

    /// <summary>
    /// Finish the box with <paramref name="name"/> the way the old auto-handler did: through the box's own
    /// doneNaming callback (the OK button is only a click on top of that), then close the menu if it is still up.
    /// </summary>
    internal static void Complete(AgentRuntime rt, NamingMenu nm, string name, bool chosen, string why)
    {
        var what = What(nm);
        Open.TryRemove(rt.Log.Player, out _);
        nm.doneNaming?.Invoke(name);
        if (Game1.activeClickableMenu == nm) Game1.exitActiveMenu();
        rt.Log.Add("system", text: chosen ? $"Named \"{name}\" ({what})" : $"Auto-named \"{name}\" ({what}): {why}");
        NpcStory.Emit(rt.Log, "named", $"{Game1.player.Name} named {what}: {name}",
            new { what, name, player = rt.Log.Player, chosen });
    }

    /// <summary>Bedtime: an open box gets a random name, like the profession default.</summary>
    internal static void ResolveDefaults(AgentRuntime rt, string why)
    {
        if (Game1.activeClickableMenu is NamingMenu nm)
            AutoName(rt, nm, why);
    }

    /// <summary>
    /// go_to_bed's wait loop: a naming box came up during the night (a new child). Ends the call so the AI can
    /// answer with name(text); the night goes on afterwards.
    /// </summary>
    internal static bool NightPrompt(CallContext c)
    {
        if (Game1.activeClickableMenu is not NamingMenu nm) return false;
        c.Result = $"A naming box came up during the night (\"{What(nm)}\"). Choose the name with name(text); you wake up after that.";
        return true;
    }
}

/// <summary>The name tool (#53). Registered in <see cref="AgentTools.All"/>.</summary>
public static partial class AgentTools
{
    internal static List<ToolDef> NamingTools() => new()
    {
        new()
        {
            Name = "name",
            Description = "Choose the name the open naming box asks for: a new pet, farm animal, horse or child. The status shows a \"Naming:\" line while a box is open. " +
                          $"At most {Naming.MaxLength} characters. Costs no game time. A box you never answer gets a random name at bedtime.",
            Schema = Obj(new { text = Str("The name") }, "text"),
            Instant = true,
            Run = NameIt
        }
    };

    private static IEnumerator NameIt(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu is not NamingMenu nm)
        {
            c.Fail("No naming box is open. The status shows a \"Naming:\" line while one is.");
            yield break;
        }
        var text = Naming.Clean(S(a, "text"));
        if (text.Length < Math.Max(1, nm.minLength))
        {
            c.Fail($"The name must have at least {Math.Max(1, nm.minLength)} character(s).");
            yield break;
        }
        if (text.Length > Naming.MaxLength)
        {
            c.Fail($"\"{text}\" is {text.Length} characters; the limit is {Naming.MaxLength}. Shorten it.");
            yield break;
        }
        var what = Naming.What(nm);
        Naming.Complete(Rt, nm, text, chosen: true, "chosen by the AI");
        yield return null; // let the menu close before the footer reads the screen
        c.Result = $"Named \"{what}\": {text}.";
    }
}
