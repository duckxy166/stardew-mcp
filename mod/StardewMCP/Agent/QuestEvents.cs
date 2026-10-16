using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Quests;

namespace StardewMCP.Agent;

/// <summary>
/// Quest events for the story log: quest_accepted, quest_completed, quest_failed and quest_taken_by_other, with the
/// quest name and giver in the text and in args (quest, giver, type, by). Each AI seat watches its own farmer's journal
/// and today's Help Wanted board, so an event lands in the log of the player it happened to. The events are written
/// to the player log (kind = the event name), from where the feed shows them; when /api/events (#42) exists it can
/// forward these kinds as they are. Scanning only reads the game, so it never changes a quest.
/// </summary>
internal static class QuestEvents
{
    private const int ScanEvery = 30;

    private class Tracked
    {
        public Quest Quest = null!;
        public string Name = "";
        public string Giver = "";
        public string Type = "story";
        /// <summary>A completed/failed event was already written for this quest.</summary>
        public bool Reported;
    }

    private class Seen
    {
        public int Ticks;
        public bool Warned;
        public bool Baselined;
        public Dictionary<string, Tracked> Quests = new();
        public int BoardDay = -1;
        public string BoardName = "";
        /// <summary>Today's board request was free the last time this seat looked.</summary>
        public bool BoardWasOpen;
    }

    private static readonly ConcurrentDictionary<string, Seen> Seats = new();

    /// <summary>Called every tick of the seat's screen from <see cref="AgentRuntime.Tick"/>.</summary>
    public static void Tick(AgentRuntime rt)
    {
        var id = rt.Log.Player;
        if (!Context.IsWorldReady)
        {
            Seats.TryRemove(id, out _);
            return;
        }
        if (Players.ById(id) is not { Role: SeatRole.Ai }) return;
        var seen = Seats.GetOrAdd(id, _ => new Seen());
        if (++seen.Ticks % ScanEvery != 0) return;
        try
        {
            Scan(rt, seen);
        }
        catch (Exception ex)
        {
            if (!seen.Warned)
            {
                seen.Warned = true;
                rt.Log.Add("system", text: $"Quest events paused: {ex.Message}");
            }
        }
    }

    private static void Scan(AgentRuntime rt, Seen seen)
    {
        var now = new Dictionary<string, Tracked>();
        foreach (var q in Game1.player.questLog)
        {
            if (q == null) continue;
            var name = q.GetName();
            var key = $"{name}|{Peek.Text(q, "id")}|{Peek.Int(q, "dayQuestAccepted")}";
            now[key] = new Tracked
            {
                Quest = q,
                Name = name,
                Giver = Peek.Text(q, "target", "npcName"),
                Type = Peek.Flag(q, "dailyQuest") == true ? "help_wanted" : "story"
            };
        }

        if (!seen.Baselined)
        {
            // The first look only records what is already there.
            seen.Quests = now;
            seen.Baselined = true;
        }
        else
        {
            foreach (var (key, t) in now)
            {
                bool done = t.Quest.completed.Value;
                if (seen.Quests.TryGetValue(key, out var before))
                {
                    t.Reported = before.Reported;
                    if (done && !t.Reported)
                    {
                        Emit(rt, "quest_completed", t);
                        t.Reported = true;
                    }
                }
                else
                {
                    Emit(rt, "quest_accepted", t);
                    if (done)
                    {
                        Emit(rt, "quest_completed", t);
                        t.Reported = true;
                    }
                }
            }
            foreach (var (key, t) in seen.Quests)
            {
                // Gone from the journal: finished (the quest still says so) or run out / cancelled.
                if (!now.ContainsKey(key) && !t.Reported)
                    Emit(rt, t.Quest.completed.Value ? "quest_completed" : "quest_failed", t);
            }
            seen.Quests = now;
        }

        ScanBoard(rt, seen);
    }

    /// <summary>A request this seat saw free that is now taken by someone else.</summary>
    private static void ScanBoard(AgentRuntime rt, Seen seen)
    {
        var board = AgentTools.BoardQuest();
        int today = Game1.Date.TotalDays;
        var name = board?.GetName() ?? "";
        if (board == null || seen.BoardDay != today || seen.BoardName != name)
        {
            seen.BoardDay = today;
            seen.BoardName = name;
            seen.BoardWasOpen = board != null && AgentTools.BoardState(board).state == "open";
            return;
        }
        if (!seen.BoardWasOpen) return;

        var (state, who) = AgentTools.BoardState(board);
        if (state == "open") return;
        seen.BoardWasOpen = false;
        if (state != "taken") return; // "mine": quest_accepted already says it
        var giver = Peek.Text(board, "target", "npcName");
        rt.Log.Add("quest_taken_by_other",
            text: $"{name}{(giver.Length > 0 ? $" (from {giver})" : "")} was taken by {who}",
            args: new { quest = name, giver, type = "help_wanted", by = who });
    }

    private static void Emit(AgentRuntime rt, string kind, Tracked t)
    {
        rt.Log.Add(kind,
            text: $"{t.Name}{(t.Giver.Length > 0 ? $" (from {t.Giver})" : "")}",
            args: new { quest = t.Name, giver = t.Giver, type = t.Type });
    }
}
