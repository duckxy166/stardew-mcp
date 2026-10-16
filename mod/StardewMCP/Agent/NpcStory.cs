using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Story moments of one seat (#9): villager heart events that play out instead of being skipped, and the
/// friendship milestones the director and story log care about. One per <see cref="AgentRuntime"/>.
///
/// Heart events are opt-in (config PlayHeartEvents, default off). When on, an event counts as a heart event if its
/// key in Data/Events/&lt;map&gt; has a friendship precondition ("f Abigail 750"). Such an event runs with its seat
/// counted as acting (so the world keeps moving), its dialogue lines go to the log, lines are advanced after a short
/// pause, and the event stops at every question so the AI answers with respond(). Every other event is still
/// skipped by <see cref="AgentRuntime"/> as before.
/// </summary>
public class StoryWatcher
{
    private const int LineTicks = 150;               // a line stays up this long (2.5 s) before it is advanced
    private const int MaxPlayTicks = 60 * 60 * 5;    // a heart event that runs longer than 5 minutes is skipped
    private static readonly int[] Milestones = { 2, 4, 6, 8, 10 };

    private object? _event;
    private string _eventId = "", _npc = "";
    private int _hearts, _playTicks, _lineTicks, _questionTicks;
    private DialogueBox? _box;
    private string _lastLine = "";
    private bool _questionLogged;

    private readonly Dictionary<string, (int Points, string Status)> _seen = new();
    private string _seenKey = "";
    private int _scanTicks;

    /// <summary>A heart event is up and this watcher is handling it.</summary>
    public volatile bool Active;

    /// <summary>The event is running by itself: the seat counts as acting. False while a question waits for the AI.</summary>
    public volatile bool Playing;

    // ------------------------------------------------------------------ events

    /// <summary>
    /// Called every tick a non-festival event is up. Returns true when it is a heart event this watcher plays,
    /// so <see cref="AgentRuntime"/> leaves it alone instead of skipping it.
    /// </summary>
    public bool OnEvent(AgentRuntime rt)
    {
        var ev = Game1.CurrentEvent;
        if (!ReferenceEquals(ev, _event))
        {
            End(rt, "ended");
            Begin(rt, ev);
        }
        if (!Active) return false;
        Step(rt);
        return true;
    }

    /// <summary>Called every tick no event is up.</summary>
    public void OnNoEvent(AgentRuntime rt)
    {
        End(rt, "ended");
        _event = null;
    }

    private void Begin(AgentRuntime rt, Event? ev)
    {
        _event = ev;
        _box = null;
        _lastLine = "";
        _playTicks = _lineTicks = _questionTicks = 0;
        _questionLogged = false;

        var seat = Players.Current;
        if (ev == null || !rt.Config.PlayHeartEvents || seat.Role != SeatRole.Ai || !seat.Connected)
            return;
        _eventId = $"{ev.id}";
        var (npc, hearts) = NpcStory.HeartEvent(rt.Helper, Game1.currentLocation, _eventId);
        if (npc == null) return;

        _npc = npc;
        _hearts = hearts;
        Active = true;
        Playing = true;
        rt.Log.Add("system", text: $"Playing heart event {_eventId} ({_npc}, {_hearts} hearts) instead of skipping it");
        NpcStory.Emit(rt.Log, "heart_event_start", $"{Game1.player.Name} is watching a {_npc} heart event",
            new { eventId = _eventId, villager = _npc, hearts = _hearts });
    }

    private void End(AgentRuntime rt, string how)
    {
        if (!Active) return;
        Active = false;
        Playing = false;
        NpcStory.Emit(rt.Log, "heart_event_end", $"The {_npc} heart event {how}",
            new { eventId = _eventId, villager = _npc, hearts = _hearts, how });
    }

    private void Step(AgentRuntime rt)
    {
        bool frozen = WorldClock.WorldPaused;
        if (!frozen && ++_playTicks > MaxPlayTicks)
        {
            rt.Log.Add("system", text: $"Heart event {_eventId} ran for more than 5 minutes: skipping it");
            Game1.CurrentEvent?.skipEvent();
            End(rt, "was skipped (too long)");
            return;
        }

        if (Game1.activeClickableMenu is not DialogueBox box)
        {
            // Cutscene action without a dialogue box: let it run.
            _box = null;
            _lastLine = "";
            _lineTicks = _questionTicks = 0;
            _questionLogged = false;
            Playing = true;
            return;
        }

        var line = (box.getCurrentString() ?? "").Replace("\n", " ").Replace("^", " ").Trim();
        if (!ReferenceEquals(box, _box) || line != _lastLine)
        {
            _box = box;
            _lastLine = line;
            _lineTicks = _questionTicks = 0;
            _questionLogged = false;
            var speaker = box.characterDialogue?.speaker?.displayName;
            rt.Log.Add("story", text: speaker == null ? line : $"{speaker}: {line}", args: new { eventId = _eventId, villager = _npc });
        }

        if (box.isQuestion && box.responses?.Length > 0)
        {
            // A choice in somebody's story: the AI answers with respond(option). The seat is not acting, so the
            // world freezes while it thinks, like at any other decision.
            Playing = false;
            if (!_questionLogged)
            {
                _questionLogged = true;
                var options = string.Join(" | ", box.responses.Select((r, i) => $"{i}) {r.responseText}"));
                rt.Log.Add("story", text: $"Question: {options}", args: new { eventId = _eventId, villager = _npc });
            }
            int limit = rt.Config.ThinkTimeoutSeconds;
            if (limit > 0 && ++_questionTicks > limit * 60 + 300)
            {
                // The AI never answered (stuck or crashed): take the first option so the event can end.
                rt.Log.Add("system", text: $"No answer to the story question in {limit}s: choosing option 0");
                _questionTicks = 0;
                box.selectedResponse = 0;
                box.receiveLeftClick(0, 0);
            }
            return;
        }

        Playing = true;
        if (!frozen && !box.transitioning && ++_lineTicks > LineTicks)
        {
            _lineTicks = 0;
            box.receiveLeftClick(0, 0);
        }
    }

    /// <summary>
    /// respond() while a heart event runs but no dialogue box is up: let the story go on until the next line or
    /// question appears (or the event ends), instead of failing with "No dialogue is open".
    /// </summary>
    public IEnumerator WaitForNext(CallContext c)
    {
        for (int i = 0; i < 60 * 120 && Active; i++)
        {
            if (Game1.activeClickableMenu is DialogueBox) break;
            yield return null;
        }
        c.Result = Active ? "The story goes on." : "The event is over.";
    }

    // ------------------------------------------------------------------ milestones

    /// <summary>Called every tick of the seat's screen; looks at the friendships once a second.</summary>
    public void Tick(AgentRuntime rt)
    {
        if (!Context.IsWorldReady || ++_scanTicks % 60 != 0 || Players.Current.Role != SeatRole.Ai)
            return;
        var p = Game1.player;
        var key = $"{SaveFolder.Name}|{p.UniqueMultiplayerID}";
        bool baseline = key != _seenKey;   // first look at this save or farmer: remember, report nothing
        if (baseline)
        {
            _seen.Clear();
            _seenKey = key;
        }
        foreach (var kv in p.friendshipData.Pairs)
        {
            int points = kv.Value.Points;
            var status = kv.Value.Status.ToString();
            if (!baseline && _seen.TryGetValue(kv.Key, out var old))
                Compare(rt, p, kv.Key, old.Points, old.Status, points, status);
            _seen[kv.Key] = (points, status);
        }
    }

    private static void Compare(AgentRuntime rt, Farmer p, string villager, int oldPoints, string oldStatus, int points, string status)
    {
        foreach (int m in Milestones)
            if (oldPoints / NpcInfo.PointsPerHeart < m && points / NpcInfo.PointsPerHeart >= m)
                NpcStory.Emit(rt.Log, "friendship_milestone", $"{p.Name} reached {m} hearts with {villager}", new { villager, hearts = m });
        if (status == oldStatus) return;
        switch (status)
        {
            case "Dating":
                NpcStory.Emit(rt.Log, "dating_started", $"{p.Name} is now dating {villager}", new { villager });
                break;
            case "Engaged":
                NpcStory.Emit(rt.Log, "proposal", $"{p.Name} got engaged to {villager}", new { villager });
                break;
            case "Married":
                NpcStory.Emit(rt.Log, "wedding", $"{p.Name} married {villager}", new { villager });
                break;
        }
    }
}

/// <summary>Story events for the director and story log, heart-event lookup and the relationship report for the runner.</summary>
public static class NpcStory
{
    /// <summary>
    /// If event <paramref name="id"/> in the map's Data/Events asset has a friendship precondition ("f NPC points"),
    /// the villager and the hearts it needs; otherwise (null, 0). Best effort: any failure means "not a heart event".
    /// </summary>
    internal static (string? Npc, int Hearts) HeartEvent(IModHelper helper, GameLocation? loc, string id)
    {
        if (loc == null || id.Length == 0) return (null, 0);
        try
        {
            var events = helper.GameContent.Load<Dictionary<string, string>>($"Data/Events/{loc.Name}");
            foreach (var key in events.Keys)
            {
                var parts = key.Split('/');
                if (parts[0].Trim() != id) continue;
                foreach (var pre in parts.Skip(1))
                {
                    var t = pre.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length >= 3 && t[0] == "f" && int.TryParse(t[2], out var points))
                        return (t[1], points / NpcInfo.PointsPerHeart);
                }
            }
        }
        catch (Exception)
        {
            // No events asset for this map, or an unexpected format: treat as an ordinary cutscene.
        }
        return (null, 0);
    }

    /// <summary>
    /// A story event for the director and story log. Until /api/events (#42) exists it is a log entry:
    /// kind "event", tool = the event type (heart_event_start, heart_event_end, gift, birthday, friendship_milestone,
    /// bouquet_given, dating_started, proposal, wedding), args = its data.
    /// </summary>
    internal static void Emit(PlayerLog log, string type, string text, object? data = null) =>
        log.Add("event", tool: type, text: text, args: data);

    /// <summary>One farmer's friendships as the runner writes them into relationships.md. Game thread only.</summary>
    internal static object Report(Farmer who, string seat)
    {
        var book = NpcKnowledge.Book(who);
        var villagers = NpcInfo.Social()
            .OrderBy(n => n.displayName, StringComparer.OrdinalIgnoreCase)
            .Select(n =>
            {
                var v = NpcInfo.Of(who, n);
                var tastes = new Dictionary<string, List<string>>();
                if (book.Tastes.TryGetValue(n.Name, out var known))
                    foreach (var word in new[] { "loved", "liked", "neutral", "disliked", "hated" })
                    {
                        var names = known.Where(kv => NpcInfo.TasteWord(kv.Value) == word).Select(kv => NpcInfo.ItemName(kv.Key)).OrderBy(x => x).ToList();
                        if (names.Count > 0) tastes[word] = names;
                    }
                object? last = null;
                if (book.LastGift.TryGetValue(n.Name, out var g))
                    last = new { day = g.Day, item = NpcInfo.ItemName(g.ItemId), reaction = NpcInfo.TasteWord(g.Taste), points = g.Points };
                return new
                {
                    name = v.Name,
                    displayName = v.DisplayName,
                    location = v.Location,
                    x = v.X,
                    y = v.Y,
                    met = v.Met,
                    hearts = v.Hearts,
                    points = v.Points,
                    talkedToday = v.TalkedToToday,
                    giftsToday = v.GiftsToday,
                    giftsThisWeek = v.GiftsThisWeek,
                    status = v.Status,
                    birthday = new { season = v.BirthdaySeason, day = v.BirthdayDay },
                    tastes,
                    lastGift = last
                };
            })
            .ToList();
        return new
        {
            player = seat,
            name = who.Name,
            day = AgentLog.FarmDay,
            season = Game1.currentSeason,
            dayOfMonth = Game1.dayOfMonth,
            year = Game1.year,
            knownTastes = NpcKnowledge.Count(who),
            villagers
        };
    }

    /// <summary>
    /// GET /api/relationships: asks every AI seat (on its own game thread, in its observation lane) for its farmer's
    /// friendships. Seats that do not answer within 5 seconds are left out.
    /// </summary>
    public static async Task<object> ReportAsync()
    {
        var op = AgentTools.RelationshipsOp();
        var pending = Players.Slots
            .Where(s => s.Role == SeatRole.Ai && s.Active)
            .Select(s => s.Runtime.Enqueue(op, default, observation: true))
            .ToList();
        if (pending.Count > 0)
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(5000));

        var players = new List<JsonElement>();
        foreach (var task in pending)
        {
            if (!task.IsCompletedSuccessfully || !task.Result.ok) continue;
            using var doc = JsonDocument.Parse(task.Result.text);
            players.Add(doc.RootElement.Clone());
        }
        return new { ready = players.Count > 0, players };
    }
}
