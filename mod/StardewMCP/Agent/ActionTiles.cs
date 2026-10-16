using StardewValley;

namespace StardewMCP.Agent;

/// <summary>Readable names for map tile actions (the "Action" tile property) that <c>look</c> shows on A tiles.</summary>
public static class ActionTiles
{
    /// <summary>
    /// The label for a tile action string such as "BuyBackpack". Actions without a friendlier name keep their
    /// raw string, which is what the AI and runner/smoke.py already match on (for example "MineElevator").
    /// </summary>
    public static string Label(string action)
    {
        var name = action.Split(' ')[0];
        switch (name)
        {
            // Pierre's backpack is a tile action that opens the Backpack_Purchase question, not a shop item.
            case "BuyBackpack":
                return Game1.player.MaxItems switch
                {
                    < 24 => "Backpack upgrade to 24 slots (2000g; interact, then respond)",
                    < 36 => "Backpack upgrade to 36 slots (10000g; interact, then respond)",
                    _ => "Backpack (already 36 slots)"
                };
            default:
                return action;
        }
    }
}
