using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;

namespace StardewMCP.Tests;

public class PathfinderTests
{
    private static List<Vector2>? Route(TextGrid map, Vector2 start, Vector2 goal) => new Pathfinder().FindPath(map, start, goal);

    private static List<(int X, int Y)> Steps(IEnumerable<Vector2> route) => route.Select(t => ((int)t.X, (int)t.Y)).ToList();

    /// <summary>The route must start next to <paramref name="start"/>, move one tile at a time, only touch open tiles and end on <paramref name="goal"/>.</summary>
    private static void AssertValidRoute(TextGrid map, Vector2 start, Vector2 goal, List<Vector2> route)
    {
        Assert.NotEmpty(route);
        var previous = (X: (int)start.X, Y: (int)start.Y);
        foreach (var tile in Steps(route))
        {
            Assert.Equal(1, Math.Abs(tile.X - previous.X) + Math.Abs(tile.Y - previous.Y));
            Assert.True(map.Open(tile.X, tile.Y), $"route steps on blocked tile ({tile.X},{tile.Y})");
            previous = tile;
        }
        Assert.Equal((X: (int)goal.X, Y: (int)goal.Y), previous);
    }

    [Fact]
    public void Already_at_the_goal_gives_an_empty_route()
    {
        var map = new TextGrid("...");
        var route = Route(map, new Vector2(1, 0), new Vector2(1, 0));
        Assert.NotNull(route);
        Assert.Empty(route!);
    }

    [Fact]
    public void Straight_corridor_lists_each_step_without_the_start_and_ends_on_the_goal()
    {
        var map = new TextGrid("S...G");
        var route = Route(map, map.Find('S'), map.Find('G'));
        Assert.NotNull(route);
        Assert.Equal(new List<(int X, int Y)> { (1, 0), (2, 0), (3, 0), (4, 0) }, Steps(route!));
    }

    [Fact]
    public void Goes_around_a_wall_the_short_way()
    {
        var map = new TextGrid(
            "S.#..",
            "..#..",
            "..#..",
            "....G");
        var route = Route(map, map.Find('S'), map.Find('G'));
        Assert.NotNull(route);
        AssertValidRoute(map, map.Find('S'), map.Find('G'), route!);
        Assert.Equal(7, route!.Count); // down 3, right 4: the only gap is on the bottom row
    }

    [Fact]
    public void Unreachable_goal_gives_null()
    {
        var map = new TextGrid(
            "S.#.G",
            "..#..",
            "..#..");
        Assert.Null(Route(map, map.Find('S'), map.Find('G')));
    }

    [Fact]
    public void Goal_on_a_blocked_tile_gives_null()
    {
        var map = new TextGrid("S.#");
        Assert.Null(Route(map, map.Find('S'), new Vector2(2, 0)));
    }

    [Fact]
    public void Goal_off_the_map_gives_null()
    {
        var map = new TextGrid("S..");
        Assert.Null(Route(map, map.Find('S'), new Vector2(5, 0)));
        Assert.Null(Route(map, map.Find('S'), new Vector2(-1, 0)));
    }

    [Fact]
    public void Start_tile_itself_need_not_be_walkable()
    {
        // The farmer can stand in a doorway the collision check calls blocked; the route still leads out.
        var map = new TextGrid("#..G");
        var route = Route(map, new Vector2(0, 0), map.Find('G'));
        Assert.NotNull(route);
        Assert.Equal(3, route!.Count);
    }

    [Fact]
    public void Every_tile_is_asked_about_at_most_once_per_search()
    {
        var map = new TextGrid(
            "S....",
            ".###.",
            ".#G#.",
            "..#..");
        Route(map, map.Find('S'), map.Find('G')); // G is walled in, so the search gives up after visiting every reachable tile
        Assert.NotEmpty(map.Probes);
        Assert.All(map.Probes, probe => Assert.Equal(1, probe.Value));
    }

    [Fact]
    public void Same_input_gives_the_same_route()
    {
        var map = new TextGrid(
            "S.....",
            "......",
            "......",
            ".....G");
        var first = Route(map, map.Find('S'), map.Find('G'));
        var second = Route(map, map.Find('S'), map.Find('G'));
        Assert.NotNull(first);
        Assert.Equal(Steps(first!), Steps(second!));
    }

    [Fact]
    public void Big_open_map_is_solved_with_a_route_of_the_manhattan_length()
    {
        var rows = Enumerable.Repeat(new string('.', 120), 120).ToArray();
        var map = new TextGrid(rows);
        var route = Route(map, new Vector2(0, 0), new Vector2(119, 119));
        Assert.NotNull(route);
        AssertValidRoute(map, new Vector2(0, 0), new Vector2(119, 119), route!);
        Assert.Equal(238, route!.Count);
    }

    [Fact]
    public void Routes_match_a_breadth_first_search_on_random_maps()
    {
        var random = new Random(20240607);
        for (int round = 0; round < 300; round++)
        {
            const int size = 14;
            var rows = new string[size];
            for (int y = 0; y < size; y++)
                rows[y] = new string(Enumerable.Range(0, size).Select(_ => random.NextDouble() < 0.3 ? '#' : '.').ToArray());
            var map = new TextGrid(rows);
            var start = new Vector2(random.Next(size), random.Next(size));
            var goal = new Vector2(random.Next(size), random.Next(size));

            var route = Route(map, start, goal);
            int? expected = map.ShortestSteps((int)start.X, (int)start.Y, (int)goal.X, (int)goal.Y);

            if (expected == null)
                Assert.Null(route);
            else
            {
                Assert.NotNull(route);
                Assert.Equal(expected.Value, route!.Count);
                if (route.Count > 0)
                    AssertValidRoute(map, start, goal, route);
            }
        }
    }
}
