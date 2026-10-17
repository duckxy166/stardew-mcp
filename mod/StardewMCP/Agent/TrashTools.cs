using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// Trash-can diving (#54). search_trash walks to a can and presses the action button on its "Garbage" tile, so the
/// game's own trash-can code runs: the daily loot roll and the villagers' reaction (a friendship loss when one
/// watches). Who saw it is read from what the game did (friendship that dropped), not guessed.
/// Story events are log entries of kind "event" (like heart events in NpcStory); /api/events does not exist yet.
/// </summary>
public static partial class AgentTools
{
    private const int TrashWitnessTiles = 7;

    private static ToolDef TrashToolDef() => new()
    {
        Name = "search_trash",
        Description = "Dig through a trash can (look shows them as A tiles named Garbage ...). You may find something, but a villager who sees you loses friendship with you, " +
                      "and the viewers see a trash_dive event with who saw it. Each can pays out once a day. Without x, y the nearest can on this map is used.",
        Schema = Obj(new { x = Int("Trash can tile x"), y = Int("Trash can tile y"), reason = Str(ReasonDesc) }, "reason"),
        Run = SearchTrash
    };

    /// <summary>Story event for the director and story log: kind "event", tool = the event type, args = its data.</summary>
    internal static void EmitStoryEvent(string type, string text, object? data = null) =>
        Rt.Log.Add("event", tool: type, text: text, args: data);

    private static string? GarbageId(GameLocation loc, int x, int y)
    {
        var action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
        if (action == null || !action.StartsWith("Garbage", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : "can";
    }

    private static Point? NearestTrashCan(GameLocation loc)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        var me = P.TilePoint;
        Point? best = null;
        int bestD = int.MaxValue;
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h; y++)
        {
            if (GarbageId(loc, x, y) == null) continue;
            int d = Math.Abs(x - me.X) + Math.Abs(y - me.Y);
            if (d < bestD) { bestD = d; best = new Point(x, y); }
        }
        return best;
    }

    private static IEnumerator SearchTrash(JsonElement a, CallContext c)
    {
        var loc = Loc;
        Point? tile = Has(a, "x") && Has(a, "y") ? new Point(I(a, "x", -1), I(a, "y", -1)) : NearestTrashCan(loc);
        if (tile == null) { c.Fail($"No trash can on {loc.Name}. Trash cans are in Town (A tiles named Garbage ...)."); yield break; }
        var t = tile.Value;
        var id = GarbageId(loc, t.X, t.Y);
        if (id == null) { c.Fail($"({t.X},{t.Y}) is not a trash can. look shows them as A Garbage ..."); yield break; }

        yield return WalkAdjacent(t, c);
        if (!c.Ok) yield break;

        // Friendship before, for villagers already met: the game's penalty is a drop in these points.
        var villagers = loc.characters.Where(n => n.IsVillager).ToList();
        var before = new Dictionary<string, int>();
        foreach (var n in villagers)
            if (P.friendshipData.TryGetValue(n.Name, out var f)) before[n.Name] = f.Points;
        var items = Snapshot();

        if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();
        bool pressed = PressAction(t);
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < 8; i++)
        {
            yield return Ticks(10);
            foreach (var n in villagers)
                if (before.TryGetValue(n.Name, out var b) && P.friendshipData.TryGetValue(n.Name, out var f) && f.Points < b)
                    seen[n.DisplayName] = f.Points - b;
        }
        yield return WaitIdle(120);

        var near = villagers.Where(n => Vector2.Distance(n.Tile, new Vector2(t.X, t.Y)) <= TrashWitnessTiles).Select(n => n.DisplayName).ToList();
        var players = Players.Slots.Where(s => s.Role == SeatRole.Ai && s.Id != Rt.Log.Player && s.Active && (s.State as ScreenState)?.Name != null)
            .Select(s => (s.State as ScreenState)!)
            .Where(st => st.Location == loc.Name && st.Tile != null && Vector2.Distance(new Vector2(st.Tile[0], st.Tile[1]), new Vector2(t.X, t.Y)) <= TrashWitnessTiles)
            .Select(st => st.Name!).ToList();

        var diff = Diff(items);
        var seenNames = seen.Keys.ToList();
        EmitStoryEvent("trash_dive",
            $"{P.Name} searched the {id} trash can" + (seenNames.Count > 0 ? $" and was seen by {string.Join(", ", seenNames)}" : ""),
            new { can = id, x = t.X, y = t.Y, location = loc.Name, seen_by = seenNames, friendship = seen, nearby = near, seen_by_players = players, found = diff });

        var found = diff.Length > 0 ? diff : (pressed ? "You found nothing (empty, or this can was already searched today)." : $"Nothing happened at ({t.X},{t.Y}).");
        var watch = seenNames.Count > 0
            ? $"Caught: {string.Join(", ", seen.Select(kv => $"{kv.Key} ({kv.Value} friendship)"))} saw you."
            : "No villager reacted.";
        var others = players.Count > 0 ? $" Players close by: {string.Join(", ", players)}." : "";
        c.Result = $"You searched the {id} trash can at ({t.X},{t.Y}). {found} {watch}{others}";
    }
}
