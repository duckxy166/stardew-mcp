using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// Night events (#54): the crop fairy, the witch, a meteorite, a strange capsule, the owl, Qi's plane... They are
/// <c>Game1.farmEvent</c>s that play while the farmers sleep, so the AIs never saw them. This watches
/// <c>Game1.farmEvent</c> on each seat's screen, builds a one-line description with what landed where, queues it for
/// that seat (shown in the next tool result's footer) and logs a "night_event" story entry once per night.
/// </summary>
internal static class NightEvents
{
    private class Seat
    {
        public bool Active;
        public string Name = "";
        public int? Behavior;
        public HashSet<string> Before = new();
    }

    private static readonly Dictionary<string, Seat> Seats = new();
    private static readonly Dictionary<string, List<string>> Pending = new();
    private static string _loggedKey = "";
    private static long _loggedAt;

    /// <summary>Per tick of a seat's screen (one line in AgentRuntime.Tick).</summary>
    public static void Tick(AgentRuntime rt)
    {
        if (!Context.IsWorldReady) return;
        var player = rt.Log.Player;
        if (!Seats.TryGetValue(player, out var seat)) Seats[player] = seat = new Seat();
        var ev = Game1.farmEvent;
        if (ev != null && !seat.Active)
        {
            seat.Active = true;
            seat.Name = ev.GetType().Name;
            seat.Behavior = FunnyReflect.Int(ev, "behavior");
            seat.Before = Landmarks().Select(l => l.key).ToHashSet();
        }
        else if (ev == null && seat.Active)
        {
            seat.Active = false;
            Finish(rt, player, seat);
        }
    }

    /// <summary>Notes for this seat since its last result, once ("" when none). Appended to the footer.</summary>
    public static string PendingText(string player)
    {
        if (!Pending.TryGetValue(player, out var list) || list.Count == 0) return "";
        var text = "Overnight: " + string.Join(" ", list);
        list.Clear();
        return text;
    }

    private static void Finish(AgentRuntime rt, string player, Seat seat)
    {
        var fresh = Landmarks().Where(l => !seat.Before.Contains(l.key)).ToList();
        var line = Describe(seat.Name, fresh);
        if (!Pending.TryGetValue(player, out var list)) Pending[player] = list = new();
        list.Add(line);

        // Every seat sees the same event: log it once.
        var key = seat.Name + "@" + Game1.uniqueIDForThisGame;
        if (key == _loggedKey && Environment.TickCount64 - _loggedAt < 120000) return;
        _loggedKey = key;
        _loggedAt = Environment.TickCount64;
        rt.Log.Add("event", "night_event", line, new
        {
            kind = seat.Name,
            behavior = seat.Behavior,
            landed = fresh.Select(l => new { what = l.what, x = l.x, y = l.y }).ToList()
        });
    }

    private static string Describe(string eventClass, List<(string key, string what, int x, int y)> fresh)
    {
        string where = string.Join(", ", fresh.Select(l => $"{l.what} at ({l.x},{l.y})"));
        switch (eventClass)
        {
            case "FairyEvent":
                return "The crop fairy visited the farm in the night and made crops grow. Look for ready crops (!) and harvest them.";
            case "WitchEvent":
                return "A witch flew over the farm in the night and cast a spell. Check your animals (a coop chicken can turn into a Void Chicken).";
            case "QiPlaneEvent":
                return "A mysterious plane flew over the farm in the night.";
            case "WorldChangeEvent":
                return "Something changed in the valley overnight; look around Pelican Town.";
            case "BirthingEvent":
            case "PlayerCoupleBirthingEvent":
                return "A baby was born overnight.";
            case "SoundInTheNightEvent":
                return fresh.Count > 0
                    ? $"Something landed on the farm in the night: {where}. Mine or interact with it."
                    : "Strange sounds in the night (crickets, dogs, an earthquake...); nothing new on the farm.";
            default:
                return fresh.Count > 0 ? $"A night event ({eventClass}) left: {where}." : $"A night event played ({eventClass}).";
        }
    }

    /// <summary>Things a night event leaves on the farm: meteorite, strange capsule, owl statue.</summary>
    private static List<(string key, string what, int x, int y)> Landmarks()
    {
        var list = new List<(string, string, int, int)>();
        var farm = Game1.getFarm();
        if (farm == null) return list;
        foreach (var clump in farm.resourceClumps)
            if (clump.parentSheetIndex.Value == 622 && (FunnyReflect.Value(clump, "Tile") ?? FunnyReflect.Value(clump, "tile")) is Vector2 at)
                list.Add(($"meteor:{at.X},{at.Y}", "Meteorite", (int)at.X, (int)at.Y));
        foreach (var kv in farm.Objects.Pairs)
        {
            var n = kv.Value.Name ?? "";
            string? what = n.Contains("Capsule", StringComparison.OrdinalIgnoreCase) ? "Strange Capsule"
                : n.Contains("Owl", StringComparison.OrdinalIgnoreCase) ? "Owl Statue" : null;
            if (what != null) list.Add(($"{what}:{kv.Key.X},{kv.Key.Y}", what, (int)kv.Key.X, (int)kv.Key.Y));
        }
        return list;
    }
}
