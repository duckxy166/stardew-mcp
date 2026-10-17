using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace StardewMCP.Agent;

/// <summary>
/// Text views for #10: farm animals in <c>look</c>, buildings under construction, the house upgrade and the
/// "animals not pet today" note. Kept out of <see cref="WorldText"/> so that file only gets one-line hooks.
/// Everything that shared code calls (<see cref="AnimalAt"/>, <see cref="Status"/>, <see cref="FooterNote"/>,
/// <see cref="LookSection"/>) swallows its own errors: a surprise in the animal code must never break look or a tool result.
/// </summary>
public static class RanchText
{
    // ------------------------------------------------------------------ animals

    /// <summary>The animals that live on this map: free on the Farm, or inside a coop or barn.</summary>
    public static List<FarmAnimal> AnimalsAt(GameLocation? loc)
    {
        if (loc is Farm farm) return farm.animals.Values.ToList();
        if (loc is AnimalHouse house) return house.animals.Values.ToList();
        return new List<FarmAnimal>();
    }

    /// <summary>Every animal of the farm: outside, and inside each coop and barn.</summary>
    public static List<(FarmAnimal animal, Building? home)> AllFarmAnimals()
    {
        var list = new List<(FarmAnimal, Building?)>();
        var farm = Game1.getFarm();
        if (farm == null) return list;
        foreach (var a in farm.animals.Values) list.Add((a, null));
        foreach (var b in farm.buildings)
            if (b.indoors.Value is AnimalHouse house)
                foreach (var a in house.animals.Values) list.Add((a, b));
        return list;
    }

    /// <summary>True for the animals of the calling farmer (or animals nobody owns).</summary>
    public static bool IsMine(FarmAnimal a)
    {
        var owner = RanchReflect.Long(a, "ownerID");
        return owner == null || owner == 0 || owner == Game1.player.UniqueMultiplayerID;
    }

    public static string TypeName(FarmAnimal a) => RanchReflect.Str(a, "type") ?? a.GetType().Name;

    /// <summary>True when the animal has something to collect (milk, wool, ...). Asks the game first, else reads the product field.</summary>
    public static bool ProductReady(FarmAnimal a)
    {
        var r = RanchReflect.TryCall(a, "readyForHarvest");
        if (r.found && r.error == null && r.value is bool b) return b;
        var v = RanchReflect.Unwrap(RanchReflect.Get(a, "currentProduce"));
        return v switch
        {
            string s => s.Length > 0 && s != "-1" && s != "0",
            int i => i > 0,
            _ => false
        };
    }

    /// <summary>"Daisy (White Cow, not pet today, product ready)".</summary>
    public static string AnimalLabel(FarmAnimal a)
    {
        var parts = new List<string> { TypeName(a), a.wasPet.Value ? "pet today" : "not pet today" };
        if (ProductReady(a)) parts.Add("product ready");
        return $"{a.Name} ({string.Join(", ", parts)})";
    }

    /// <summary>Map symbol 'a' and label for an animal standing on this tile; null when there is none.</summary>
    public static (char sym, string? label)? AnimalAt(GameLocation loc, Point tile)
    {
        try
        {
            foreach (var a in AnimalsAt(loc))
                if (a.TilePoint == tile)
                    return ('a', AnimalLabel(a));
        }
        catch (Exception) { }
        return null;
    }

    // ------------------------------------------------------------------ construction and upgrades

    public static int DaysOfConstructionLeft(Building b) => RanchReflect.Int(b, "daysOfConstructionLeft") ?? 0;

    public static int DaysUntilUpgrade(Building b) => RanchReflect.Int(b, "daysUntilUpgrade") ?? 0;

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>Lines about buildings under construction and the house upgrade; empty when nothing is going on.</summary>
    private static IEnumerable<string> ConstructionLines()
    {
        var farm = Game1.getFarm();
        if (farm != null)
            foreach (var b in farm.buildings)
            {
                int left = DaysOfConstructionLeft(b), up = DaysUntilUpgrade(b);
                if (left > 0)
                    yield return $"Under construction: {b.buildingType.Value} at ({b.tileX.Value},{b.tileY.Value}), {Plural(left, "day")} left (Robin builds overnight; it can't be used yet).";
                else if (up > 0)
                    yield return $"Being upgraded: {b.buildingType.Value} at ({b.tileX.Value},{b.tileY.Value}), {Plural(up, "day")} left.";
            }
        var p = Game1.player;
        if (p.daysUntilHouseUpgrade.Value > 0)
            yield return $"House upgrade: {Plural(p.daysUntilHouseUpgrade.Value, "day")} left (level {p.HouseUpgradeLevel} to {p.HouseUpgradeLevel + 1}).";
    }

    // ------------------------------------------------------------------ hooks for shared text

    /// <summary>Extra lines for status: construction, house upgrade, animals. Empty when there is nothing to say.</summary>
    public static string Status()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var line in ConstructionLines()) sb.AppendLine(line);
            var mine = AllFarmAnimals().Where(t => IsMine(t.animal)).Select(t => t.animal).ToList();
            if (mine.Count > 0)
            {
                int unpet = mine.Count(a => !a.wasPet.Value), ready = mine.Count(ProductReady);
                sb.AppendLine($"Animals: {mine.Count} of yours; {unpet} not pet today; {ready} with a product ready (animals tool).");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Short warning for the end of every tool result: "3 animals not pet today". Empty when all are pet.</summary>
    public static string FooterNote()
    {
        try
        {
            int unpet = AllFarmAnimals().Count(t => IsMine(t.animal) && !t.animal.wasPet.Value);
            return unpet == 0 ? "" : $"Note: {Plural(unpet, "animal")} not pet today (animals tool).";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Lines for look: the map symbol 'a' (a farm animal; its name and state are under "Things in view") wherever animals are,
    /// and on the Farm the animal houses (with their animal counts) and buildings under construction.
    /// </summary>
    public static string LookSection(GameLocation loc)
    {
        try
        {
            var lines = new List<string>();
            if (AnimalsAt(loc).Count > 0) lines.Add("a = farm animal (name, type, pet today or not, product ready: animals tool).");
            if (loc is Farm farm)
            {
                var houses = new List<string>();
                foreach (var b in farm.buildings)
                {
                    int left = DaysOfConstructionLeft(b);
                    string where = $"{b.buildingType.Value} at ({b.tileX.Value},{b.tileY.Value})";
                    if (left > 0)
                        houses.Add($"  {where}: under construction, {Plural(left, "day")} left");
                    else if (b.indoors.Value is AnimalHouse house)
                        houses.Add($"  {where}: {Plural(house.animals.Values.Count(), "animal")}{(house.isFull() ? ", full" : "")}, enter with travel_to \"{b.GetIndoorsName()}\"");
                }
                if (houses.Count > 0) lines.Add("Farm buildings (animal houses and construction):\n" + string.Join("\n", houses));
            }
            return string.Join("\n", lines);
        }
        catch (Exception)
        {
            return "";
        }
    }
}
