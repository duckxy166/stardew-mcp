using System;
using System.Collections.Generic;
using System.Globalization;

namespace StardewMCP.Agent;

/// <summary>What a crop can do if planted on a given day. Produced by <see cref="CropMath.Plan"/>.</summary>
/// <param name="InSeason">The crop grows in the season it is planted in.</param>
/// <param name="DaysAvailable">Growing days left from the planting day to the end of the last season the crop grows in.</param>
/// <param name="Matures">The crop is ready before it would die at a season change.</param>
/// <param name="LastPlantDay">Latest day of the starting season it can be planted and still mature (28 or more: any day).</param>
/// <param name="Harvests">Harvests from one planting (0 when it will not mature).</param>
/// <param name="Plantings">Back-to-back plantings that fit (always 1 for crops that regrow).</param>
/// <param name="TileDays">Days one tile is busy with these plantings.</param>
public sealed record CropPlan(bool InSeason, int DaysAvailable, bool Matures, int LastPlantDay, int Harvests, int Plantings, int TileDays);

/// <summary>
/// Planning math for the crops tool. No game types, so it can be tested without the game:
/// runner/tests/test_crop_math.py is a line-for-line Python port; keep the two in step.
/// </summary>
public static class CropMath
{
    public const int DaysPerSeason = 28;
    private static readonly string[] SeasonNames = { "spring", "summer", "fall", "winter" };

    /// <summary>0 spring, 1 summer, 2 fall, 3 winter; -1 when the name is not a season.</summary>
    public static int SeasonIndex(string? name)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "spring": return 0;
            case "summer": return 1;
            case "fall":
            case "autumn": return 2;
            case "winter": return 3;
            default: return -1;
        }
    }

    public static string SeasonName(int index) => SeasonNames[((index % 4) + 4) % 4];

    /// <summary>Days from planting to the first harvest: the sum of the crop's growth phases.</summary>
    public static int GrowthDays(IEnumerable<int> daysInPhase)
    {
        int total = 0;
        foreach (var d in daysInPhase)
            if (d > 0) total += d;
        return total;
    }

    /// <summary>
    /// Growing days a crop planted on <paramref name="startDay"/> of <paramref name="season"/> has before it dies.
    /// A crop planted and watered on day d is ready on day d + growth, so it must be ready by day 28.
    /// It keeps growing through the following seasons it also grows in (corn: summer and fall), never past winter.
    /// </summary>
    public static int DaysAvailable(int season, int startDay, IReadOnlyList<bool> growsIn)
    {
        if (season < 0 || season > 3 || growsIn.Count < 4 || !growsIn[season]) return 0;
        int days = DaysPerSeason - startDay;
        for (int s = season + 1; s < 4 && growsIn[s]; s++) days += DaysPerSeason;
        return Math.Max(days, 0);
    }

    /// <summary>Plan one crop. <paramref name="regrowDays"/> of 0 or less means it is harvested once and gone.</summary>
    public static CropPlan Plan(int season, int startDay, IReadOnlyList<bool> growsIn, int growthDays, int regrowDays)
    {
        int avail = DaysAvailable(season, startDay, growsIn);
        bool inSeason = season >= 0 && season <= 3 && growsIn.Count >= 4 && growsIn[season];
        if (!inSeason || growthDays <= 0)
            return new CropPlan(inSeason, avail, false, 0, 0, 0, 0);

        int lastPlant = startDay + (avail - growthDays); // planting later than this day is too late
        if (growthDays > avail)
            return new CropPlan(true, avail, false, lastPlant, 0, 0, 0);

        if (regrowDays > 0)
        {
            int harvests = 1 + (avail - growthDays) / regrowDays;
            return new CropPlan(true, avail, true, lastPlant, harvests, 1, growthDays + (harvests - 1) * regrowDays);
        }
        int plantings = avail / growthDays;
        return new CropPlan(true, avail, true, lastPlant, 1, plantings, plantings * growthDays);
    }

    /// <summary>The (season, day) that is <paramref name="days"/> days after day <paramref name="day"/> of <paramref name="season"/>.
    /// A season index of 4 or more means the next year.</summary>
    public static (int season, int day) Advance(int season, int day, int days)
    {
        int total = Math.Max(day + days, 1);
        return (season + (total - 1) / DaysPerSeason, (total - 1) % DaysPerSeason + 1);
    }

    /// <summary>Items per harvest: the average of the stack range plus the chance of an extra one.</summary>
    public static double ExpectedYield(int minStack, int maxStack, double extraChance)
    {
        int lo = Math.Max(1, minStack), hi = Math.Max(lo, maxStack);
        return (lo + hi) / 2.0 + Math.Max(0.0, extraChance);
    }

    /// <summary>Gold made on one tile with the plan, after paying for the seeds of every planting.</summary>
    public static double NetGold(CropPlan plan, double yieldPerHarvest, int sellPrice, int seedCost) =>
        plan.Plantings * (plan.Harvests * yieldPerHarvest * sellPrice - seedCost);

    public static double GoldPerTilePerDay(CropPlan plan, double net) => plan.TileDays > 0 ? net / plan.TileDays : 0.0;

    /// <summary>Sell price at a quality (0 normal, 1 silver, 2 gold, 4 iridium): the base price times 1 + 0.25 per quality step.</summary>
    public static int PriceAtQuality(int basePrice, int quality) => (int)(basePrice * (1f + quality * 0.25f));

    /// <summary>Price in a shop's item list. Without a fixed price the game sells an object for twice its base price.</summary>
    public static (int price, bool estimated) ShopPrice(int listedPrice, int objectBasePrice) =>
        listedPrice > 0 ? (listedPrice, false) : (Math.Max(objectBasePrice, 0) * 2, true);

    /// <summary>Qualified form of an item id: object ids such as "24" become "(O)24"; ids that carry a type already stay as they are.</summary>
    public static string Qualify(string? id)
    {
        id = (id ?? "").Trim();
        return id.StartsWith("(") ? id : "(O)" + id;
    }

    /// <summary>The bare object id: "(O)24" and "24" both give "24". Other item types keep their type prefix.</summary>
    public static string Unqualify(string? id)
    {
        id = (id ?? "").Trim();
        return id.StartsWith("(O)", StringComparison.OrdinalIgnoreCase) ? id.Substring(3) : id;
    }

    public static bool SameItem(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Qualify(a), Qualify(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads recipe lines of Data/CraftingRecipes and Data/CookingRecipes. Also game-free; ported in test_crop_math.py.</summary>
public static class RecipeMath
{
    /// <summary>
    /// How many of an item a recipe needs. The first "/" field lists ingredients as "id count id count ...";
    /// a negative id is a whole category (-5 any egg), matched against the item's category.
    /// Returns (0, false) when the recipe does not use the item.
    /// </summary>
    public static (int count, bool viaCategory) IngredientCount(string? recipe, string itemId, int category)
    {
        if (string.IsNullOrEmpty(recipe)) return (0, false);
        var tokens = recipe.Split('/')[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int viaCategory = 0;
        string wanted = CropMath.Unqualify(itemId);
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            if (!int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0) continue;
            string id = CropMath.Unqualify(tokens[i]);
            if (string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase)) return (n, false);
            if (category != 0 && id == category.ToString(CultureInfo.InvariantCulture) && viaCategory == 0) viaCategory = n;
        }
        return (viaCategory, viaCategory > 0);
    }
}
