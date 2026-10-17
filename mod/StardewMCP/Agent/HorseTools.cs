using System;
using System.Collections;
using System.Linq;
using System.Text.Json;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Horse (#54): mount and dismount through the horse's own checkAction (what the action button calls). The stable
/// is built with the buildings tools (#10) and naming the horse belongs to the naming/romance work (#53); this only
/// rides a horse that already exists. travel_to is unchanged: the game decides how fast a mounted farmer moves.
/// </summary>
public static partial class AgentTools
{
    private static ToolDef[] HorseToolDefs() => new ToolDef[]
    {
        new()
        {
            Name = "mount",
            Description = "Walk to a horse on this map and get on it (you need a Stable with a horse; build one first). Riding is faster than walking. A horse with no name asks for one first: name it with menu(type), then mount again.",
            Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
            Run = Mount
        },
        new()
        {
            Name = "dismount",
            Description = "Get off your horse. Horses also drop you at building doors.",
            Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
            Run = Dismount
        }
    };

    private static IEnumerator Mount(JsonElement a, CallContext c)
    {
        yield return WaitIdle();
        if (P.mount != null) { c.Result = $"You are already riding {P.mount.displayName}."; yield break; }
        var me = P.TilePoint;
        var horse = Loc.characters.OfType<Horse>().OrderBy(h => Math.Abs(h.TilePoint.X - me.X) + Math.Abs(h.TilePoint.Y - me.Y)).FirstOrDefault();
        if (horse == null)
        {
            var farmHorse = Game1.getFarm()?.characters.OfType<Horse>().FirstOrDefault();
            c.Fail(farmHorse != null && Loc.Name != "Farm"
                ? $"No horse on {Loc.Name}. {farmHorse.displayName} is on the Farm at ({farmHorse.TilePoint.X},{farmHorse.TilePoint.Y}); travel_to Farm first."
                : "You have no horse here. A horse comes with a Stable (build one at Robin's Carpenter shop).");
            yield break;
        }

        var sub = new CallContext();
        yield return WalkAdjacent(horse.TilePoint, sub);
        if (!sub.Ok) { c.Fail($"Can't reach the horse: {sub.Result}"); yield break; }
        if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();
        horse.checkAction(P, Loc);

        for (int i = 0; i < 150 && P.mount == null && Game1.activeClickableMenu == null; i++) yield return null;
        if (Game1.activeClickableMenu is NamingMenu)
        {
            c.Result = "The horse has no name yet: a naming menu opened. Use menu(action=\"type\", text=<name>), then call mount again.";
            yield break;
        }
        if (P.mount == null) { c.Fail("The horse did not let you on (it may belong to another player)."); yield break; }
        yield return Ticks(30);
        Rt.Log.Add("event", "mount", $"{P.Name} got on {P.mount?.displayName}", new { horse = P.mount?.displayName, location = Loc.Name });
        c.Result = $"You are riding {P.mount?.displayName} on {Loc.Name} at ({P.TilePoint.X},{P.TilePoint.Y}).";
    }

    private static IEnumerator Dismount(JsonElement a, CallContext c)
    {
        if (P.mount == null) { c.Result = "You are not riding."; yield break; }
        var name = P.mount.displayName;
        P.mount.checkAction(P, Loc); // the action button while riding gets off
        for (int i = 0; i < 150 && P.mount != null; i++) yield return null;
        if (P.mount != null) { c.Fail("Could not get off the horse here; try next to a free tile."); yield break; }
        yield return Ticks(20);
        Rt.Log.Add("event", "dismount", $"{P.Name} got off {name}", new { horse = name, location = Loc.Name });
        c.Result = $"You got off {name} at ({P.TilePoint.X},{P.TilePoint.Y}).";
    }
}
