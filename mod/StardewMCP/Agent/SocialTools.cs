using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Tools;

namespace StardewMCP.Agent;

/// <summary>One edit of the shared plan board.</summary>
public record PlanEdit(int Id, int Day, string Time, string Author, string Section, string Text);

/// <summary>The plan every player can read and edit. Every edit is kept with author and time.</summary>
public static class PlanBoard
{
    public const int MaxSections = 12, MaxText = 600;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, PlanEdit> Sections = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PlanEdit> History = new();

    public static PlanEdit? Edit(string author, string section, string text)
    {
        lock (Lock)
        {
            if (text.Length == 0)
                Sections.Remove(section);
            else if (!Sections.ContainsKey(section) && Sections.Count >= MaxSections)
                return null;
            var e = new PlanEdit(History.Count + 1, AgentLog.FarmDay, WorldText.FormatTime(Game1.timeOfDay), author, section, text);
            if (text.Length > 0) Sections[section] = e;
            History.Add(e);
            return e;
        }
    }

    public static PlanEdit? Get(string section)
    {
        lock (Lock)
            return Sections.TryGetValue(section, out var e) ? e : null;
    }

    public static object Snapshot()
    {
        lock (Lock)
            return new { sections = Sections.Values.ToList(), history = History.TakeLast(50).ToList() };
    }

    public static string Render(int history)
    {
        lock (Lock)
        {
            var sb = new StringBuilder();
            if (Sections.Count == 0) sb.AppendLine("Plan board is empty. Use plan_edit(section, text) to write on it.");
            foreach (var s in Sections.Values)
                sb.AppendLine($"## {s.Section} (by {s.Author}, day {s.Day} {s.Time})\n{s.Text}");
            var shown = History.TakeLast(history).ToList();
            sb.AppendLine($"History, oldest first ({shown.Count} of {History.Count} edits):");
            foreach (var h in shown)
                sb.AppendLine($"  #{h.Id} day {h.Day} {h.Time} {h.Author} {(h.Text.Length == 0 ? "cleared" : "set")} \"{h.Section}\"" +
                              (h.Text.Length == 0 ? "" : $": {Clip(h.Text, 80)}"));
            return sb.ToString().TrimEnd();
        }
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}

/// <summary>
/// Social tools: say, whisper, emote, give and the shared plan board. Kept apart from
/// <see cref="AgentTools"/>. Chat and whispers land in the target's inbox and ride on that
/// seat's next tool result (see <see cref="TakeMail"/>); nothing here interrupts a running action.
/// </summary>
public static class SocialTools
{
    private const int MaxChat = 240, MaxInbox = 30, MaxMailLines = 20;

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> Inbox = new();
    // Items handed over by another seat; added to the receiver's inventory on the receiver's own tick.
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<(Item item, string from)>> Gifts = new();

    // Speech bubbles by farmer id. The game only draws them for NPCs, so a draw patch shows these above farmers.
    private static readonly ConcurrentDictionary<long, (string text, long until)> Bubbles = new();

    public static void Apply(IManifest manifest)
    {
        new Harmony(manifest.UniqueID + ".social").Patch(AccessTools.Method(typeof(Farmer), nameof(Farmer.draw), new[] { typeof(SpriteBatch) }),
            postfix: new HarmonyMethod(typeof(SocialTools), nameof(DrawBubble_Postfix)));
    }

    private static void DrawBubble_Postfix(Farmer __instance, SpriteBatch b)
    {
        if (!Bubbles.TryGetValue(__instance.UniqueMultiplayerID, out var bubble)) return;
        if (Environment.TickCount64 > bubble.until) { Bubbles.TryRemove(__instance.UniqueMultiplayerID, out _); return; }
        var at = Game1.GlobalToLocal(new Vector2(__instance.StandingPixel.X, __instance.StandingPixel.Y - 192));
        SpriteText.drawStringWithScrollCenteredAt(b, bubble.text, (int)at.X, (int)at.Y, "", 1f, null, 1, 0.99f);
    }

    // All of these are marked Instant: talking costs no game time, so they run even while the world is
    // frozen (a morning meeting is chat only) and never count as "acting" in WorldClock. They log their own entries.
    public static List<ToolDef> All() => new()
    {
        new()
        {
            Name = "say",
            Description = "Say something out loud: it appears in the in-game chat box and above your head. Every player hears it, at any distance, and so do the viewers. Keep it short.",
            Schema = AgentTools.Obj(new { text = AgentTools.Str("What you say") }, "text"),
            Instant = true, AppendStatus = false, Run = Say
        },
        new()
        {
            Name = "whisper",
            Description = "Tell one player something privately. Nobody else in the game learns it happened; viewers do see it.",
            Schema = AgentTools.Obj(new { to = AgentTools.Str("Player id (p1, p2...) or name"), text = AgentTools.Str("What you whisper") }, "to", "text"),
            Instant = true, AppendStatus = false, Run = Whisper
        },
        new()
        {
            Name = "emote",
            Description = $"Show an emote bubble above your head. Names: {string.Join(", ", EmoteNames())}.",
            Schema = AgentTools.Obj(new { name = AgentTools.Str("Emote name") }, "name"),
            Instant = true, AppendStatus = false, Run = Emote
        },
        new()
        {
            Name = "give",
            Description = "Hand an item from your inventory to another player standing within 4 tiles of you (same map). Tools can't be given.",
            Schema = AgentTools.Obj(new { to = AgentTools.Str("Player id (p1, p2...) or name"), item = AgentTools.Str("Item name"), count = AgentTools.Int("How many (default 1)") }, "to", "item"),
            Instant = true, AppendStatus = false, Run = Give
        },
        new()
        {
            Name = "plan_read",
            Description = "Read the shared plan board (sections everyone can see) and its edit history: who changed what, and when.",
            Schema = AgentTools.Obj(new { history = AgentTools.Int("How many recent edits to list, 0-100 (default 10)") }),
            Instant = true, NeedsWorld = false, AppendStatus = false,
            Run = (a, c) => AgentTools.Instant(c, PlanBoard.Render(Math.Clamp(AgentTools.I(a, "history", 10), 0, 100)))
        },
        new()
        {
            Name = "plan_edit",
            Description = "Write a section of the shared plan board, replacing that section. Empty text deletes it. Everyone can read it and every edit is logged with your name.",
            Schema = AgentTools.Obj(new { section = AgentTools.Str("Section title, e.g. \"Today\" or \"Crops\""), text = AgentTools.Str($"New content, up to {PlanBoard.MaxText} characters") }, "section", "text"),
            Instant = true, AppendStatus = false, Run = PlanEditTool
        }
    };

    private static IEnumerable<string> EmoteNames() => Farmer.EMOTES.Where(e => !e.hidden).Select(e => e.emoteString);

    // ------------------------------------------------------------------ seats

    private static string Me => AgentTools.Rt.Log.Player;

    private static string? NameOf(PlayerSlot s) => (s.State as ScreenState)?.Name;

    /// <summary>"Gemini (p2)": how players refer to each other.</summary>
    private static string Label(PlayerSlot s) => $"{NameOf(s) ?? "?"} ({s.Id})";

    private static IEnumerable<PlayerSlot> OtherAis() =>
        Players.Slots.Where(s => s.Role == SeatRole.Ai && s.Id != Me && s.Active && NameOf(s) != null);

    /// <summary>The AI seat named by <paramref name="to"/> (id or name); otherwise fails <paramref name="c"/> with the valid choices.</summary>
    private static PlayerSlot? Target(string to, CallContext c)
    {
        to = to.Trim();
        var seat = OtherAis().FirstOrDefault(s => s.Id.Equals(to, StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(NameOf(s), to, StringComparison.OrdinalIgnoreCase));
        if (seat == null)
            c.Fail($"No player \"{to}\". Players you can address: {string.Join(", ", OtherAis().Select(Label))}.");
        return seat;
    }

    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ------------------------------------------------------------------ inbox

    private static void Deliver(PlayerSlot to, string line)
    {
        var q = Inbox.GetOrAdd(to.Id, _ => new());
        q.Enqueue(line);
        while (q.Count > MaxInbox) q.TryDequeue(out _);
    }

    /// <summary>Messages for this seat since its last tool result, as a compact block ("" when none).</summary>
    public static string TakeMail(string player)
    {
        if (!Inbox.TryGetValue(player, out var q) || q.IsEmpty) return "";
        var lines = new List<string>();
        while (q.TryDequeue(out var l)) lines.Add(l);
        var shown = lines.TakeLast(MaxMailLines).ToList();
        var sb = new StringBuilder("Messages:");
        if (shown.Count < lines.Count) sb.Append($"\n  ({lines.Count - shown.Count} older not shown)");
        foreach (var l in shown) sb.Append("\n  ").Append(l);
        return sb.ToString();
    }

    /// <summary>Per-tick work on the seat's own screen: add items other players gave it.</summary>
    public static void Tick(string player)
    {
        if (!Gifts.TryGetValue(player, out var q) || q.IsEmpty || !Context.IsWorldReady) return;
        var me = Game1.player;
        while (q.TryDequeue(out var g))
        {
            var label = $"{g.item.Stack} x {g.item.DisplayName}";
            if (me.addItemToInventoryBool(g.item))
                Deliver(Players.ById(player)!, $"{g.from} gave you {label}.");
            else
            {
                Game1.createItemDebris(g.item, me.getStandingPosition(), me.FacingDirection);
                Deliver(Players.ById(player)!, $"{g.from} gave you {label}, but your inventory was full: it fell at your feet.");
            }
        }
    }

    /// <summary>
    /// A notice from the game to everybody (for example the Joja membership, #26): the in-game chat box and every other AI's inbox.
    /// Unlike <see cref="Say"/> it shows no bubble and is not logged here; the caller logs the event itself.
    /// </summary>
    public static void Announce(string text)
    {
        text = Clip(Clean(text), MaxChat);
        var me = Game1.player;
        Game1.Multiplayer.sendChatMessage(LocalizedContentManager.CurrentLanguageCode, text, Multiplayer.AllPlayers);
        Game1.chatBox?.receiveChatMessage(me.UniqueMultiplayerID, 0, LocalizedContentManager.CurrentLanguageCode, text);
        foreach (var s in OtherAis()) Deliver(s, $"[{WorldText.FormatTime(Game1.timeOfDay)}] {text}");
    }

    // ------------------------------------------------------------------ tools

    private static IEnumerator Say(JsonElement a, CallContext c)
    {
        var text = Clip(Clean(AgentTools.S(a, "text")), MaxChat);
        if (text.Length == 0) { c.Fail("text is empty"); yield break; }
        var me = Game1.player;

        Game1.Multiplayer.sendChatMessage(LocalizedContentManager.CurrentLanguageCode, text, Multiplayer.AllPlayers);
        Game1.chatBox?.receiveChatMessage(me.UniqueMultiplayerID, 0, LocalizedContentManager.CurrentLanguageCode, text);
        Bubbles[me.UniqueMultiplayerID] = (Clip(text, 60), Environment.TickCount64 + Math.Clamp(1500 + text.Length * 60, 2500, 7000));

        var from = Label(Players.ById(Me)!);
        var heard = OtherAis().ToList();
        foreach (var s in heard) Deliver(s, $"[{WorldText.FormatTime(Game1.timeOfDay)}] {from}: {text}");
        AgentTools.Rt.Log.Add("say", text: text);
        c.Result = $"You said: \"{text}\" ({heard.Count} other player{(heard.Count == 1 ? "" : "s")} heard it).";
    }

    private static IEnumerator Whisper(JsonElement a, CallContext c)
    {
        var text = Clip(Clean(AgentTools.S(a, "text")), MaxChat);
        if (text.Length == 0) { c.Fail("text is empty"); yield break; }
        var to = Target(AgentTools.S(a, "to"), c);
        if (to == null) yield break;

        Deliver(to, $"[{WorldText.FormatTime(Game1.timeOfDay)}] {Label(Players.ById(Me)!)} whispers: {text}");
        AgentTools.Rt.Log.Add("whisper", text: text, args: new { to = to.Id });
        c.Result = $"You whispered to {Label(to)}: \"{text}\". Only they will see it.";
    }

    private static IEnumerator Emote(JsonElement a, CallContext c)
    {
        var name = AgentTools.S(a, "name").Trim().ToLowerInvariant();
        var match = Farmer.EMOTES.FirstOrDefault(e => !e.hidden && e.emoteString == name);
        if (match == null) { c.Fail($"Unknown emote \"{name}\". Names: {string.Join(", ", EmoteNames())}."); yield break; }
        Game1.player.netDoEmote(match.emoteString);
        AgentTools.Rt.Log.Add("emote", text: name);
        c.Result = $"You show the \"{name}\" emote.";
        yield break;
    }

    private static IEnumerator Give(JsonElement a, CallContext c)
    {
        var to = Target(AgentTools.S(a, "to"), c);
        if (to == null) yield break;
        var me = Game1.player;
        int count = Math.Max(1, AgentTools.I(a, "count", 1));

        int slot = AgentTools.FindItem(AgentTools.S(a, "item"), it => it is not Tool);
        if (slot < 0) { c.Fail($"You have no giftable item called \"{AgentTools.S(a, "item")}\" (tools can't be given)."); yield break; }
        var held = me.Items[slot];
        var same = me.Items.Where(i => i != null && i.QualifiedItemId == held.QualifiedItemId && i.Quality == held.Quality).ToList();
        int have = same.Sum(i => i.Stack);
        if (have < count) { c.Fail($"You only have {have} x {held.DisplayName}."); yield break; }

        // The other seat's farmer is a synced copy: read where it is, never write to it.
        var other = Game1.getOnlineFarmers().FirstOrDefault(f => f.Name == NameOf(to));
        if (other?.currentLocation == null) { c.Fail($"{Label(to)} is not in the world right now."); yield break; }
        float dist = Vector2.Distance(other.Tile, me.Tile);
        if (other.currentLocation.NameOrUniqueName != Game1.currentLocation.NameOrUniqueName || dist > 4.5f)
        {
            var where = other.currentLocation.NameOrUniqueName == Game1.currentLocation.NameOrUniqueName
                ? $"{dist:0} tiles away" : $"on {other.currentLocation.NameOrUniqueName}";
            c.Fail($"{Label(to)} is too far to hand an item ({where}, at ({other.TilePoint.X},{other.TilePoint.Y})). Walk next to them first (within 4 tiles).");
            yield break;
        }

        var gift = held.getOne();
        gift.Stack = count;
        if (!other.couldInventoryAcceptThisItem(gift)) { c.Fail($"{Label(to)}'s inventory is full."); yield break; }

        for (int left = count; left > 0;)
        {
            var s = same.First(i => i.Stack > 0);
            int take = Math.Min(left, s.Stack);
            s.Stack -= take;
            left -= take;
            if (s.Stack <= 0) me.removeItemFromInventory(s);
        }
        Gifts.GetOrAdd(to.Id, _ => new()).Enqueue((gift, Label(Players.ById(Me)!)));
        AgentTools.Rt.Log.Add("give", text: $"{count} x {gift.DisplayName}", args: new { to = to.Id });
        c.Result = $"You gave {count} x {gift.DisplayName} to {Label(to)}.";
    }

    private static IEnumerator PlanEditTool(JsonElement a, CallContext c)
    {
        var section = Clip(Clean(AgentTools.S(a, "section")), 40);
        var text = AgentTools.S(a, "text").Trim();
        if (section.Length == 0) { c.Fail("section is empty"); yield break; }
        if (text.Length > PlanBoard.MaxText) { c.Fail($"text is {text.Length} characters; the limit is {PlanBoard.MaxText}. Shorten it or split it into sections."); yield break; }

        var e = PlanBoard.Edit(Label(Players.ById(Me)!), section, text);
        if (e == null) { c.Fail($"The board already has {PlanBoard.MaxSections} sections. Reuse or clear one."); yield break; }
        AgentTools.Rt.Log.Add("plan", text: text, args: new { section, edit = e.Id });
        c.Result = text.Length == 0 ? $"Cleared section \"{section}\" (edit #{e.Id})." : $"Section \"{section}\" saved (edit #{e.Id}).";
    }
}
