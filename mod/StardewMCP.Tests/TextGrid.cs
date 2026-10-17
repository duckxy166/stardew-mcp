using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace StardewMCP.Tests;

/// <summary>
/// A walkable map drawn as text: '#' is blocked, anything else can be stood on. 'S' and 'G' only mark
/// a start and goal for <see cref="Find"/>. Everything outside the drawing is off the map.
/// </summary>
internal sealed class TextGrid : IWalkableGrid
{
    private readonly string[] _rows;

    /// <summary>How often each tile was asked about.</summary>
    public Dictionary<(int X, int Y), int> Probes { get; } = new();

    public TextGrid(params string[] rows) => _rows = rows;

    public int Width => _rows.Length == 0 ? 0 : _rows[0].Length;

    public int Height => _rows.Length;

    public bool IsWalkable(int x, int y)
    {
        Probes[(x, y)] = Probes.TryGetValue((x, y), out int n) ? n + 1 : 1;
        return Open(x, y);
    }

    /// <summary>Same answer as <see cref="IsWalkable"/> without counting the question.</summary>
    public bool Open(int x, int y) =>
        y >= 0 && y < _rows.Length && x >= 0 && x < _rows[y].Length && _rows[y][x] != '#';

    public Vector2 Find(char marker)
    {
        for (int y = 0; y < _rows.Length; y++)
        {
            int x = _rows[y].IndexOf(marker);
            if (x >= 0)
                return new Vector2(x, y);
        }
        throw new InvalidOperationException($"No '{marker}' on the map");
    }

    /// <summary>Length of the shortest 4-direction route, or null when there is none. Independent of the A* under test.</summary>
    public int? ShortestSteps(int fromX, int fromY, int toX, int toY)
    {
        if (fromX == toX && fromY == toY)
            return 0;
        if (!Open(toX, toY))
            return null;
        var dist = new Dictionary<(int, int), int> { [(fromX, fromY)] = 0 };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((fromX, fromY));
        var moves = new[] { (0, -1), (1, 0), (0, 1), (-1, 0) };
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var (dx, dy) in moves)
            {
                var next = (X: cur.X + dx, Y: cur.Y + dy);
                if (dist.ContainsKey(next) || !Open(next.X, next.Y))
                    continue;
                dist[next] = dist[cur] + 1;
                if (next.X == toX && next.Y == toY)
                    return dist[next];
                queue.Enqueue(next);
            }
        }
        return null;
    }
}
