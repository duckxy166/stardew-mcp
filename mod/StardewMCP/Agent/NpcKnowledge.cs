using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>The last gift one farmer gave a villager.</summary>
public class GiftRecord
{
    public int Day { get; set; }
    public string ItemId { get; set; } = "";
    /// <summary>The game's taste code: 0 love, 2 like, 4 dislike, 6 hate, 8 neutral.</summary>
    public int Taste { get; set; }
    public int Points { get; set; }
}

/// <summary>What one farmer has found out about the villagers. Saved next to the logs, so it survives restarts.</summary>
public class NpcBook
{
    /// <summary>Villager name → qualified item id → taste code. A taste is only here once this farmer gave that gift.</summary>
    public Dictionary<string, Dictionary<string, int>> Tastes { get; set; } = new();

    /// <summary>Villager name → the last gift this farmer gave them.</summary>
    public Dictionary<string, GiftRecord> LastGift { get; set; } = new();
}

/// <summary>A talk or gift that other players standing nearby could have seen.</summary>
public record PublicTalk(int Day, string Time, string Seat, string Who, string Kind, string[] SeenBy);

/// <summary>
/// Gift tastes are discovered, not omniscient (the maintainer's comment on #9): a farmer only knows a taste after
/// giving that gift through talk_to. Each farmer has their own book, so the AIs can trade or lie about tastes.
/// Also keeps the last talks and gifts that other players could see, so villagers() can show them.
/// </summary>
public static class NpcKnowledge
{
    private const int KeepTalks = 6;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, NpcBook> Books = new();
    private static readonly Dictionary<string, List<PublicTalk>> Talks = new(StringComparer.OrdinalIgnoreCase);

    // The name is part of the key: a split-screen farmhand may still be unnamed when the first state snapshot is taken.
    private static string KeyOf(Farmer who) => $"{SaveFolder.Name}|{who.UniqueMultiplayerID}|{who.Name}";

    /// <summary>The farmer's book, loaded from disk the first time it is asked for. Game thread only.</summary>
    public static NpcBook Book(Farmer who)
    {
        lock (Gate)
        {
            var key = KeyOf(who);
            if (!Books.TryGetValue(key, out var book))
                Books[key] = book = Load(who);
            return book;
        }
    }

    /// <summary>Record the taste a villager showed for a gift, and make it the villager's last gift.</summary>
    public static void Learn(Farmer who, string villager, string itemId, int taste, int day, int points)
    {
        var book = Book(who);
        lock (Gate)
        {
            if (!book.Tastes.TryGetValue(villager, out var items))
                book.Tastes[villager] = items = new Dictionary<string, int>();
            items[itemId] = taste;
            book.LastGift[villager] = new GiftRecord { Day = day, ItemId = itemId, Taste = taste, Points = points };
            Save(who, book);
        }
    }

    public static bool TryTaste(NpcBook book, string villager, string itemId, out int taste)
    {
        taste = -1;
        lock (Gate)
            return book.Tastes.TryGetValue(villager, out var items) && items.TryGetValue(itemId, out taste);
    }

    /// <summary>How many (villager, item) tastes the farmer has found out; shown per player in /api/state.</summary>
    public static int Count(Farmer who)
    {
        try
        {
            var book = Book(who);
            lock (Gate)
                return book.Tastes.Values.Sum(items => items.Count);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------ what other players could see

    public static void RecordPublic(string villager, PublicTalk talk)
    {
        lock (Gate)
        {
            if (!Talks.TryGetValue(villager, out var list))
                Talks[villager] = list = new List<PublicTalk>();
            list.Add(talk);
            if (list.Count > KeepTalks) list.RemoveAt(0);
        }
    }

    /// <summary>The latest talk or gift by someone else that <paramref name="viewer"/> was close enough to see, or null. Never shows hearts.</summary>
    public static string? SeenText(string villager, string viewer)
    {
        lock (Gate)
        {
            if (!Talks.TryGetValue(villager, out var list)) return null;
            var t = list.LastOrDefault(x => x.Seat != viewer && x.SeenBy.Contains(viewer));
            if (t == null) return null;
            return $"{t.Who} {(t.Kind == "gift" ? "gave them a gift" : "talked to them")} while you watched (day {t.Day} {t.Time})";
        }
    }

    // ------------------------------------------------------------------ disk

    private static string PathFor(Farmer who)
    {
        var dir = string.IsNullOrWhiteSpace(AgentTools.Config.LogDir)
            ? Path.Combine(AgentTools.Rt.Helper.DirectoryPath, "logs")
            : AgentTools.Config.LogDir;
        // Smoke-test saves keep to a "smoke" subfolder, like AgentLog.Dir, so they never mix with a real run.
        if (SaveFolder.Name?.StartsWith("smoke_", StringComparison.OrdinalIgnoreCase) == true)
            dir = Path.Combine(dir, "smoke");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"npc_{Safe(SaveFolder.Name ?? "save")}_{Safe(who.Name)}.json");
    }

    private static string Safe(string s) => new string(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_').ToArray());

    private static NpcBook Load(Farmer who)
    {
        try
        {
            var path = PathFor(who);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<NpcBook>(File.ReadAllText(path)) ?? new NpcBook();
        }
        catch (Exception)
        {
            // A damaged or unreadable file just starts a fresh book.
        }
        return new NpcBook();
    }

    private static void Save(Farmer who, NpcBook book)
    {
        try
        {
            File.WriteAllText(PathFor(who), JsonSerializer.Serialize(book));
        }
        catch (Exception)
        {
            // Knowledge stays in memory for this run if the disk write fails.
        }
    }
}
