using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// Registry of the daily-life tools of issue #14, each in its own partial-class file:
/// cook (CookingTools.cs), quests / accept_quest (QuestTools.cs, QuestEvents.cs), crack_geode (GeodeTools.cs),
/// trash (TrashTools.cs) and watch_tv (TvTools.cs).
/// </summary>
public static partial class AgentTools
{
    private static IEnumerable<ToolDef> QolToolDefs() =>
        CookToolDefs().Concat(QuestToolDefs()).Concat(GeodeToolDefs()).Concat(TrashToolDefs()).Concat(TvToolDefs());

    /// <summary>Tiles of <paramref name="loc"/> whose Buildings-layer Action property satisfies <paramref name="match"/> (the kitchen, the Help Wanted board).</summary>
    private static List<Point> FindActionTiles(GameLocation loc, Func<string, bool> match)
    {
        var found = new List<Point>();
        var layer = loc.Map.Layers[0];
        for (int y = 0; y < layer.LayerHeight; y++)
        {
            for (int x = 0; x < layer.LayerWidth; x++)
            {
                var action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
                if (action != null && match(action)) found.Add(new Point(x, y));
            }
        }
        return found;
    }

    /// <summary>"Gemini (p2)": a farmer's name with the seat that plays them, as the chat and plan board name players.</summary>
    private static string SeatLabel(Farmer f)
    {
        var slot = Players.Slots.FirstOrDefault(s => (s.State as ScreenState)?.Name == f.Name);
        return slot == null ? f.Name : $"{f.Name} ({slot.Id})";
    }

    /// <summary>The farmers who are playing: everyone online except the camera seat's farmer.</summary>
    private static IEnumerable<Farmer> PlayingFarmers()
    {
        var camera = Players.Slots.Where(s => s.Role == SeatRole.Camera).Select(s => (s.State as ScreenState)?.Name).Where(n => n != null).ToList();
        foreach (var f in Game1.getOnlineFarmers())
            if (!camera.Contains(f.Name))
                yield return f;
    }
}
