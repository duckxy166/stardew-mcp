using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Quests;

namespace StardewMCP.Agent;

/// <summary>
/// quests and accept_quest. quests is the shared view of everything players can work towards: the journal (Help Wanted
/// requests and the main story), today's Help Wanted board with who already took it, the team's Special Orders and
/// the Community Center / Joja line. It can post the player's quests to the plan board, and plan_read shows deadline
/// hints from <see cref="QuestPlanHint"/>. accept_quest walks to the Town board and presses Accept like a player.
/// The Quest classes keep details under version-dependent names, so those are read through <see cref="Peek"/>.
/// </summary>
public static partial class AgentTools
{
    private static List<ToolDef> QuestToolDefs() => new()
    {
        new()
        {
            Name = "quests",
            Description = "Your quests and the shared goals. Journal: every open quest with its type (Help Wanted or Story), who asked, the story text, the objective and progress, days left and reward. " +
                          "Help Wanted board: today's request in Town and whether another player already took it (first come, first served, one request a day). " +
                          "Also the team's Special Orders and the Community Center / Joja line. share=true also posts your open quests to the plan board for the others.",
            Schema = Obj(new { share = new { type = "boolean", description = "Also write your open quests to the shared plan board (default false)" } }),
            Instant = true,
            Run = (a, c) => Instant(c, QuestsText(a))
        },
        new()
        {
            Name = "accept_quest",
            Description = "Take today's Help Wanted request: you walk to the board in Town, open it and press Accept. Only one request is posted a day, it goes to whoever takes it first, " +
                          "and you can take one a day. quests shows whether it is still free and what it asks for.",
            Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
            Run = AcceptQuest
        }
    };

    // ------------------------------------------------------------------ reading quests

    /// <summary>Progress counters of the quest subclasses (monster slaying, collecting, fishing): done and total member names.</summary>
    private static readonly (string done, string total)[] QuestCounters =
    {
        ("numberKilled", "numberToKill"),
        ("numberCollected", "number"),
        ("numberFished", "numberToFish")
    };

    /// <summary>Today's Help Wanted request (the board's quest), or null when none is posted or it can not be read.</summary>
    internal static Quest? BoardQuest()
    {
        if (Peek.GetStatic(typeof(Game1), "questOfTheDay") is not Quest quest) return null;
        // The board shows "no request today" when the quest has no objective text.
        return Peek.Get(quest, "currentObjective") is string objective && objective.Length == 0 ? null : quest;
    }

    private static bool SameQuest(Quest a, Quest b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.GetName() != b.GetName() || Peek.Flag(a, "dailyQuest") != true) return false;
        // Another player's copy of the request they took today; an older one of the same name does not count.
        var day = Peek.Int(a, "dayQuestAccepted");
        return day == null || day == Game1.Date.TotalDays;
    }

    /// <summary>"open" (nobody took it), "mine" (you took it) or "taken" (by <c>who</c>), for today's board quest.</summary>
    internal static (string state, string who) BoardState(Quest board)
    {
        if (P.questLog.Any(q => q != null && SameQuest(q, board))) return ("mine", "you");
        var takers = PlayingFarmers()
            .Where(f => f.UniqueMultiplayerID != P.UniqueMultiplayerID && f.questLog.Any(q => q != null && SameQuest(q, board)))
            .Select(SeatLabel)
            .ToList();
        if (takers.Count > 0) return ("taken", string.Join(" and ", takers));
        if (Peek.Flag(board, "accepted") == true) return ("taken", "another player");
        return ("open", "");
    }

    private static string QuestFlavour(Quest q) => Peek.Clean(Peek.Text(q, "targetMessage")).Replace("@", P.Name);

    private static string QuestTask(Quest q) => Peek.Clean(Peek.Text(q, "GetDescription()", "questDescription")).Replace("@", P.Name);

    /// <summary>Objective, progress, days left and reward of a quest, as short phrases.</summary>
    private static List<string> QuestFacts(Quest q, string task)
    {
        var facts = new List<string>();
        var objective = Peek.Clean(Peek.Text(q, "currentObjective")).Replace("@", P.Name);
        if (objective.Length > 0 && objective != task) facts.Add($"objective: {Peek.Clip(objective, 140)}");
        foreach (var (done, total) in QuestCounters)
        {
            var d = Peek.Int(q, done);
            var t = Peek.Int(q, total);
            if (d != null && t is > 0) facts.Add($"progress {d}/{t}");
        }
        var days = Peek.Int(q, "GetDaysLeft()", "daysLeft");
        if (days is > 0) facts.Add($"{days} day{(days == 1 ? "" : "s")} left");
        var gold = Peek.Int(q, "GetMoneyReward()", "moneyReward");
        if (gold is > 0)
            facts.Add($"reward {gold}g");
        else
        {
            var reward = Peek.Clean(Peek.Text(q, "rewardDescription"));
            if (reward.Length > 0) facts.Add($"reward: {Peek.Clip(reward, 80)}");
        }
        return facts;
    }

    private static string QuestEntry(Quest q, int n)
    {
        bool daily = Peek.Flag(q, "dailyQuest") == true;
        var giver = Peek.Text(q, "target", "npcName");
        var sb = new StringBuilder($"  {n}. [{(daily ? "Help Wanted" : "Story")}] {q.GetName()}");
        if (giver.Length > 0) sb.Append(daily ? $" - asked by {giver}" : $" - about {giver}");
        var flavour = QuestFlavour(q);
        var task = QuestTask(q);
        if (flavour.Length > 0) sb.Append($"\n     \"{Peek.Clip(flavour, 200)}\"");
        if (task.Length > 0 && task != flavour) sb.Append($"\n     Task: {Peek.Clip(task, 160)}");
        var facts = QuestFacts(q, task);
        if (facts.Count > 0) sb.Append("\n     " + string.Join("; ", facts));
        return sb.ToString();
    }

    private static string BoardText()
    {
        var board = BoardQuest();
        if (board == null) return "Help Wanted board (Town): nothing is posted today, or it could not be read.";
        var (state, who) = BoardState(board);
        var giver = Peek.Text(board, "target", "npcName");
        var sb = new StringBuilder($"Help Wanted board (Town), today: [Help Wanted] {board.GetName()}");
        if (giver.Length > 0) sb.Append($" - asked by {giver}");
        sb.Append(state switch
        {
            "mine" => " - YOU took it.",
            "taken" => $" - TAKEN by {who}. First come, first served: no other request today.",
            _ => " - still FREE. First come, first served: accept_quest walks to the board and takes it."
        });
        var flavour = QuestFlavour(board);
        var task = QuestTask(board);
        if (flavour.Length > 0) sb.Append($"\n     \"{Peek.Clip(flavour, 200)}\"");
        if (task.Length > 0 && task != flavour) sb.Append($"\n     Task: {Peek.Clip(task, 160)}");
        var facts = QuestFacts(board, task);
        if (facts.Count > 0) sb.Append("\n     " + string.Join("; ", facts));
        return sb.ToString();
    }

    private static List<string> SpecialOrderLines()
    {
        var lines = new List<string>();
        try
        {
            foreach (var order in Peek.Items(Peek.Get(Peek.Get(P, "team"), "specialOrders")))
            {
                var state = Peek.Get(order, "questState")?.ToString();
                if (state != null && state != "InProgress") continue;
                var name = Peek.Text(order, "GetName()");
                if (name.Length == 0) name = Peek.Text(order, "questKey");
                if (name.Length == 0) continue;
                var sb = new StringBuilder($"  [Special Order] {name}");
                var who = Peek.Text(order, "requester");
                if (who.Length > 0) sb.Append($" - asked by {who}");
                var desc = Peek.Clean(Peek.Text(order, "GetDescription()"));
                if (desc.Length > 0) sb.Append($"\n     \"{Peek.Clip(desc, 200)}\"");
                var facts = new List<string>();
                foreach (var objective in Peek.Items(Peek.Get(order, "objectives")).Take(4))
                {
                    var text = Peek.Clean(Peek.Text(objective, "GetDescription()"));
                    if (text.Length == 0) continue;
                    var have = Peek.Int(objective, "GetCount()", "currentCount");
                    var need = Peek.Int(objective, "GetMaxCount()", "maxCount");
                    facts.Add(have != null && need is > 0 ? $"{text} ({have}/{need})" : text);
                }
                if (facts.Count > 0) sb.Append("\n     objectives: " + string.Join("; ", facts));
                var extra = new List<string>();
                var days = Peek.Int(order, "GetDaysLeft()");
                if (days is > 0) extra.Add($"{days} day{(days == 1 ? "" : "s")} left");
                var gold = Peek.Int(order, "GetMoneyReward()");
                if (gold is > 0) extra.Add($"reward {gold}g");
                if (extra.Count > 0) sb.Append("\n     " + string.Join("; ", extra));
                lines.Add(sb.ToString());
                if (lines.Count >= 6) break;
            }
        }
        catch (Exception)
        {
            // the section is skipped
        }
        return lines;
    }

    /// <summary>One line on the main questline's big goals (not entries of the journal): the Community Center or Joja.</summary>
    private static string LongTermLine()
    {
        try
        {
            var mail = Game1.MasterPlayer.mailReceived;
            if (mail.Contains("JojaMember")) return "  [Story] Joja: the farm joined Joja, so the Community Center bundles are closed.";
            if (mail.Contains("ccIsComplete")) return "  [Story] Community Center: restored.";
            var rooms = new[] { "ccPantry", "ccCraftsRoom", "ccFishTank", "ccBoilerRoom", "ccVault", "ccBulletin" };
            return $"  [Story] Community Center: {rooms.Count(r => mail.Contains(r))}/{rooms.Length} rooms restored (the long goal of the main questline).";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string QuestsText(JsonElement a)
    {
        var sb = new StringBuilder($"Quests, {Game1.currentSeason} {Game1.dayOfMonth}:\n");
        var mine = P.questLog.Where(q => q != null && !q.completed.Value).ToList();
        sb.AppendLine($"Your journal ({mine.Count}):");
        if (mine.Count == 0) sb.AppendLine("  none");
        for (int i = 0; i < mine.Count && i < 12; i++) sb.AppendLine(QuestEntry(mine[i], i + 1));
        if (mine.Count > 12) sb.AppendLine($"  ... and {mine.Count - 12} more");
        sb.AppendLine(BoardText());
        var orders = SpecialOrderLines();
        if (orders.Count > 0)
        {
            sb.AppendLine("Special Orders (the team's, open for everyone):");
            foreach (var o in orders) sb.AppendLine(o);
        }
        var longTerm = LongTermLine();
        if (longTerm.Length > 0) sb.AppendLine(longTerm);
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("share", out var share) && share.ValueKind == JsonValueKind.True)
            sb.AppendLine(ShareQuests(mine));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Posts the player's open quests (and today's board status) to the plan board, as plan_edit would.</summary>
    private static string ShareQuests(List<Quest> mine)
    {
        var parts = new List<string>();
        foreach (var q in mine.Take(6))
        {
            var giver = Peek.Text(q, "target", "npcName");
            var days = Peek.Int(q, "GetDaysLeft()", "daysLeft");
            parts.Add($"{q.GetName()}{(giver.Length > 0 ? $" ({giver})" : "")}{(days is > 0 ? $", {days}d left" : "")}");
        }
        var text = parts.Count > 0 ? "Open: " + string.Join("; ", parts) + "." : "No open quests.";
        var board = BoardQuest();
        if (board != null)
        {
            var (state, who) = BoardState(board);
            text += $" Board: {board.GetName()} - {(state == "open" ? "still free" : state == "mine" ? "I took it" : "taken by " + who)}.";
        }
        text = Peek.Clip(text, PlanBoard.MaxText);
        var section = Peek.Clip($"Quests of {P.Name}", 40);
        var edit = PlanBoard.Edit(SeatLabel(P), section, text);
        if (edit == null) return $"Not posted: the plan board already has {PlanBoard.MaxSections} sections. Clear one with plan_edit.";
        Rt.Log.Add("plan", text: text, args: new { section, edit = edit.Id });
        return $"Posted to the plan board, section \"{section}\" (edit #{edit.Id}).";
    }

    /// <summary>
    /// Lines for the end of plan_read: the open Help Wanted request nobody took, one taken by someone else, and
    /// accepted requests about to run out. Empty when there is nothing to say (or the world is not loaded).
    /// </summary>
    internal static string QuestPlanHint()
    {
        try
        {
            if (!Context.IsWorldReady) return "";
            var lines = new List<string>();
            var board = BoardQuest();
            if (board != null)
            {
                var (state, who) = BoardState(board);
                var giver = Peek.Text(board, "target", "npcName");
                var by = giver.Length > 0 ? $"{giver}'s " : "";
                if (state == "open")
                    lines.Add($"- {by}Help Wanted request \"{board.GetName()}\" is on the Town board and nobody has taken it (first come, first served; accept_quest).");
                else if (state == "taken")
                    lines.Add($"- {by}Help Wanted request \"{board.GetName()}\" was taken by {who}.");
            }
            foreach (var f in PlayingFarmers())
            {
                foreach (var q in f.questLog)
                {
                    if (q == null || q.completed.Value || Peek.Flag(q, "dailyQuest") != true) continue;
                    var days = Peek.Int(q, "GetDaysLeft()", "daysLeft");
                    if (days is > 0 and <= 2)
                        lines.Add($"- {SeatLabel(f)}'s quest \"{q.GetName()}\" ends in {days} day{(days == 1 ? "" : "s")}.");
                }
            }
            return lines.Count == 0 ? "" : "Quests (from the game):\n" + string.Join("\n", lines.Take(5));
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ accept_quest

    private static IEnumerator AcceptQuest(JsonElement a, CallContext c)
    {
        var board = BoardQuest();
        if (board == null)
        {
            c.Fail("No Help Wanted request is posted today (the board changes every morning).");
            yield break;
        }
        var name = board.GetName();
        var (state, who) = BoardState(board);
        if (state == "mine")
        {
            c.Result = $"You already took \"{name}\". quests shows it.";
            yield break;
        }
        if (state == "taken")
        {
            c.Fail($"\"{name}\" was already taken by {who}. Help Wanted is first come, first served: no other request is posted today.");
            yield break;
        }
        if (Peek.Flag(P, "acceptedDailyQuest") == true)
        {
            c.Fail("You already accepted a Help Wanted request today (one a day).");
            yield break;
        }
        if (Game1.activeClickableMenu != null)
        {
            c.Fail("A menu or dialogue is open. close_menu or respond first.");
            yield break;
        }

        int logBefore = P.questLog.Count;
        if (Loc.Name != "Town")
        {
            var trip = new CallContext();
            yield return TravelTo("Town", trip);
            if (!trip.Ok)
            {
                c.Fail($"Could not get to the board in Town: {trip.Result}");
                yield break;
            }
        }

        // The board and the calendar are both "Billboard" tiles; the Help Wanted one says "daily". Try the likely tiles in turn.
        var tiles = FindActionTiles(Loc, act => act.StartsWith("Billboard", StringComparison.OrdinalIgnoreCase));
        if (tiles.Count == 0)
        {
            c.Fail($"Could not find the Help Wanted board on {Loc.Name}.");
            yield break;
        }
        var me = P.TilePoint;
        var ordered = tiles
            .OrderBy(t => Loc.doesTileHaveProperty(t.X, t.Y, "Action", "Buildings")?.Contains("daily", StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
            .ThenBy(t => Math.Abs(t.X - me.X) + Math.Abs(t.Y - me.Y))
            .ToList();

        var tried = new List<Point>();
        string last = "the board did not open";
        bool accepted = false;
        foreach (var t in ordered)
        {
            if (tried.Count >= 4) break;
            if (tried.Any(p => Math.Abs(p.X - t.X) + Math.Abs(p.Y - t.Y) <= 1)) continue;
            tried.Add(t);

            var sub = new CallContext();
            yield return Interact(ArgsOf(new { x = t.X, y = t.Y, reason = Reason(a) }), sub);
            if (!sub.Ok) { last = sub.Result; continue; }
            for (int i = 0; i < 90 && Game1.activeClickableMenu is not Billboard; i++) yield return null;
            if (Game1.activeClickableMenu is not Billboard menu)
            {
                last = $"({t.X},{t.Y}) did not open a board";
                continue;
            }
            if (Peek.Flag(menu, "dailyQuestBoard", "dailyQuest") == false)
            {
                last = $"({t.X},{t.Y}) is the calendar board";
                yield return CloseMenu(default, new CallContext());
                continue;
            }
            var button = Field(menu, "acceptQuestButton") ?? (AccessTools.Field(menu.GetType(), "acceptQuestButton")?.GetValue(menu) as ClickableComponent);
            if (button == null)
            {
                last = "the board has no Accept button";
                yield return CloseMenu(default, new CallContext());
                continue;
            }
            yield return Ticks(15);
            Click(menu, button);
            yield return Ticks(15);
            accepted = P.questLog.Any(q => q != null && SameQuest(q, board)) || P.questLog.Count > logBefore;
            if (!accepted) last = "pressed Accept but the quest did not reach your journal (someone may just have taken it)";
            yield return CloseMenu(default, new CallContext());
            break;
        }

        if (!accepted)
        {
            c.Fail($"Could not accept \"{name}\": {last}.");
            yield break;
        }
        var entry = P.questLog.LastOrDefault(q => q != null && SameQuest(q, board));
        c.Result = $"Accepted \"{name}\" from the Help Wanted board.\n" + (entry != null ? QuestEntry(entry, 1) : "quests shows the details.");
    }
}
