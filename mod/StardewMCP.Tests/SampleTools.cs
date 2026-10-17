using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StardewMCP.Agent;

namespace StardewMCP.Tests;

/// <summary>
/// A small tool set built like the real one (AgentTools.Obj/Str/Int/Tiles from ToolArgs.cs), for the
/// contract tests. The real list can't be built here because it needs the game; see ToolContract.cs.
/// </summary>
internal static class SampleTools
{
    private static IEnumerator Noop(JsonElement args, CallContext ctx)
    {
        yield break;
    }

    public static ToolDef Look() => new()
    {
        Name = "look",
        Description = "ASCII map of the area around you.",
        Schema = AgentTools.Obj(new { radius = AgentTools.Int("Tiles around you") }),
        Instant = true,
        Run = Noop
    };

    public static ToolDef WalkTo() => new()
    {
        Name = "walk_to",
        Description = "Walk to a tile.",
        Schema = AgentTools.Obj(new { x = AgentTools.Int("Tile x"), y = AgentTools.Int("Tile y"), reason = AgentTools.Str("Why") }, "x", "y", "reason"),
        Run = Noop
    };

    public static ToolDef Harvest() => new()
    {
        Name = "harvest",
        Description = "Harvest crops on the given tiles.",
        Schema = AgentTools.Obj(new { tiles = AgentTools.Tiles("List of [x,y] tiles"), reason = AgentTools.Str("Why") }, "tiles", "reason"),
        Run = Noop
    };

    /// <summary>An action with an enum parameter and a read mode, like the real menu tool.</summary>
    public static ToolDef Menu() => new()
    {
        Name = "menu",
        Description = "Read or use the open menu.",
        Schema = AgentTools.Obj(new
        {
            action = new { type = "string", @enum = new[] { "take", "put", "ok" }, description = "Omit to read the menu" },
            reason = AgentTools.Str("Why")
        }),
        InstantFor = a => AgentTools.S(a, "action").Length == 0,
        Run = Noop
    };

    public static Dictionary<string, ToolDef> All() =>
        new[] { Look(), WalkTo(), Harvest(), Menu() }.ToDictionary(t => t.Name);
}
