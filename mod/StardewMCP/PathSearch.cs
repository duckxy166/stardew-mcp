using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace StardewMCP;

/// <summary>
/// Which tiles a route may step on. The game side answers with its own collision check
/// (<c>Pathfinder.IsWalkable</c>); the unit tests answer from a plain text map.
/// </summary>
public interface IWalkableGrid
{
    bool IsWalkable(int x, int y);
}

// Game-free half of Pathfinder: the search itself only needs Point and Vector2, so the unit
// tests (mod/StardewMCP.Tests) compile this file without any Stardew assembly.
public partial class Pathfinder
{
    /// <summary>Tiles expanded before giving up; big maps (Farm, Forest) need tens of thousands.</summary>
    private const int ExpandLimit = 60000;

    private static readonly Point[] Moves = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };

    private readonly Dictionary<Point, bool> _walkable = new();

    /// <summary>
    /// Route from <paramref name="start"/> to <paramref name="goal"/> as the tiles to step on in
    /// order, excluding the start tile and ending on the goal. Empty when already there, null when
    /// the goal can't be stood on or can't be reached.
    /// </summary>
    public List<Vector2>? FindPath(IWalkableGrid grid, Vector2 start, Vector2 goal)
    {
        var from = new Point((int)start.X, (int)start.Y);
        var to = new Point((int)goal.X, (int)goal.Y);
        if (from == to)
            return new List<Vector2>();
        if (!Walkable(grid, to))
            return null;

        var steps = new Dictionary<Point, int> { [from] = 0 };
        var parent = new Dictionary<Point, Point>();
        var done = new HashSet<Point>();
        var frontier = new PriorityQueue<Point, int>();
        frontier.Enqueue(from, Distance(from, to));

        while (frontier.TryDequeue(out var tile, out _))
        {
            if (tile == to)
                return Unwind(parent, from, to);
            if (!done.Add(tile))
                continue; // stale queue entry
            if (done.Count > ExpandLimit)
                break;

            int cost = steps[tile] + 1;
            foreach (var move in Moves)
            {
                var next = new Point(tile.X + move.X, tile.Y + move.Y);
                if (done.Contains(next) || (steps.TryGetValue(next, out int known) && known <= cost))
                    continue;
                if (!Walkable(grid, next))
                    continue;
                steps[next] = cost;
                parent[next] = tile;
                frontier.Enqueue(next, cost + Distance(next, to));
            }
        }
        return null;
    }

    /// <summary>Cached <c>IsWalkable</c>, since one search probes the same tiles repeatedly.</summary>
    private bool Walkable(IWalkableGrid grid, Point tile)
    {
        if (!_walkable.TryGetValue(tile, out bool ok))
            _walkable[tile] = ok = grid.IsWalkable(tile.X, tile.Y);
        return ok;
    }

    private static int Distance(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static List<Vector2> Unwind(Dictionary<Point, Point> parent, Point from, Point to)
    {
        var route = new List<Vector2>();
        for (var tile = to; tile != from; tile = parent[tile])
            route.Add(tile.ToVector2());
        route.Reverse();
        return route;
    }
}
