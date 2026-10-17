using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// Game knowledge read from the game's own data (Game1.cropData, Game1.objectData, Data/Shops, the recipe files),
/// so it is always right and covers modded content too. Both tools are pure observations (Instant, no game time).
/// The planning arithmetic lives in <see cref="CropMath"/> and <see cref="RecipeMath"/>.
/// </summary>
public static class KnowledgeTools
{
    private const int MaxRecipes = 12, MaxShops = 8, MaxCandidates = 12;

    public static List<ToolDef> All() => new()
    {
        new()
        {
            Name = "crops",
            Description = "Season planner for crops, read from the game's own data. For every crop that grows in a season (default: now) it gives the seed, growth and regrow days, " +
                          "the last day to plant so it still matures before the season ends, how many harvests fit, seed and sell prices and a rough gold per tile per day. " +
                          "It marks seeds you carry and tells you which crops would die at the season change. Check it before buying or planting seeds. Costs no game time.",
            Schema = AgentTools.Obj(new { season = AgentTools.Str("spring, summer, fall or winter (default: the current season; another season is planned from its day 1)") }),
            Instant = true, AppendStatus = false,
            Run = (a, c) => Report(c, "crops", () => CropsReport(a))
        },
        new()
        {
            Name = "item_info",
            Description = "Look up an object by name: what it sells for at each quality, whether you can eat it and what it restores, its category, which shops list it and for how much, " +
                          "the crafting and cooking recipes that use it, and the crop it grows into or comes from. Works for objects (crops, seeds, forage, fish, artisan goods, food, minerals), " +
                          "not for tools, weapons or furniture. Costs no game time.",
            Schema = AgentTools.Obj(new { name = AgentTools.Str("Item name, e.g. Parsnip or Parsnip Seeds") }, "name"),
            Instant = true, AppendStatus = false,
            Run = (a, c) => Report(c, "item_info", () => ItemInfoReport(a))
        }
    };

    /// <summary>Runs a report; a data mismatch (a modded or changed game file) becomes a clear failure, not a crash.</summary>
    private static IEnumerator Report(CallContext c, string tool, Func<(bool ok, string text)> build)
    {
        (bool ok, string text) r;
        try { r = build(); }
        catch (Exception ex) { r = (false, $"{tool} could not read the game data ({ex.GetType().Name}: {ex.Message}). Tell the player; the game data may differ from what the mod expects."); }
        if (r.ok) c.Result = r.text; else c.Fail(r.text);
        yield break;
    }

    private static string Num(double v, string format = "0.#") => v.ToString(format, CultureInfo.InvariantCulture);

    private static string Plural(int n, string word) => n == 1 ? $"{n} {word}" : $"{n} {word}s";

    // ------------------------------------------------------------------ crops

    private sealed class CropRow
    {
        public string Seed = "", Harvest = "", Offers = "";
        public int Growth, Regrow, Sell, Have, SeedCost = -1;
        public bool Raised;
        public double Yield, Net, PerDay;
        public CropPlan Plan = null!;
    }

    /// <summary>Effects of the fertilizers and soils a farmer can carry (the wiki's numbers), matched by the item's English name.</summary>
    private static readonly (string name, string effect)[] Boosters =
    {
        ("Speed-Gro", "crops grow 10% faster"),
        ("Deluxe Speed-Gro", "crops grow 25% faster"),
        ("Hyper Speed-Gro", "crops grow 33% faster"),
        ("Basic Fertilizer", "better crop quality (more Silver)"),
        ("Quality Fertilizer", "better crop quality (more Gold)"),
        ("Deluxe Fertilizer", "best crop quality (Gold and some Iridium)"),
        ("Basic Retaining Soil", "the soil stays watered overnight 33% of the time"),
        ("Quality Retaining Soil", "the soil stays watered overnight 66% of the time"),
        ("Deluxe Retaining Soil", "the soil always stays watered overnight")
    };

    private static (bool ok, string text) CropsReport(JsonElement a)
    {
        if (Game1.cropData == null || Game1.objectData == null) return (false, "The crop data is not loaded yet.");
        int current = CropMath.SeasonIndex(Game1.currentSeason);
        int season = current;
        var requested = AgentTools.S(a, "season").Trim();
        if (requested.Length > 0)
        {
            season = CropMath.SeasonIndex(requested);
            if (season < 0) return (false, $"Unknown season \"{requested}\". Use spring, summer, fall or winter.");
        }
        if (season < 0) return (false, "Cannot tell the current season; pass season=spring, summer, fall or winter.");

        bool today = season == current;
        int startDay = today ? Math.Clamp(Game1.dayOfMonth, 1, CropMath.DaysPerSeason) : 1;
        string seasonName = CropMath.SeasonName(season);
        var carried = CarriedObjects();
        var objects = Game1.objectData;

        var ready = new List<CropRow>();
        var late = new List<CropRow>();
        var bySeed = new Dictionary<string, CropRow>();
        foreach (var kv in Game1.cropData)
        {
            var data = kv.Value;
            if (data == null) continue;
            var grows = new bool[4];
            if (data.Seasons != null)
                foreach (var s in data.Seasons)
                {
                    int i = CropMath.SeasonIndex(s.ToString());
                    if (i >= 0) grows[i] = true;
                }
            if (!grows[season]) continue;
            int growth = data.DaysInPhase == null ? 0 : CropMath.GrowthDays(data.DaysInPhase);
            if (growth <= 0) continue;

            string harvestId = CropMath.Unqualify(data.HarvestItemId);
            int sell = objects.TryGetValue(harvestId, out var harvestData) && harvestData != null ? harvestData.Price : 0;
            int seedBase = objects.TryGetValue(kv.Key, out var seedData) && seedData != null ? seedData.Price : 0;

            var offers = ShopOffers(CropMath.Qualify(kv.Key), seedBase, "SeedShop", "Joja");
            var gold = offers.Where(o => o.Trade.Length == 0 && o.Unit == "g").ToList();
            int seedCost = gold.Count > 0 ? gold.Min(o => o.Price) : -1;

            var plan = CropMath.Plan(season, startDay, grows, growth, data.RegrowDays);
            double perHarvest = CropMath.ExpectedYield(data.HarvestMinStack, data.HarvestMaxStack, data.ExtraHarvestChance);
            double net = CropMath.NetGold(plan, perHarvest, sell, Math.Max(seedCost, 0));
            carried.TryGetValue(kv.Key, out int have);
            var row = new CropRow
            {
                Seed = SafeName(CropMath.Qualify(kv.Key), kv.Key),
                Harvest = SafeName(CropMath.Qualify(harvestId), harvestId),
                Offers = string.Join(", ", offers.Select(OfferText)),
                Growth = growth, Regrow = data.RegrowDays, Sell = sell, Have = have, SeedCost = seedCost,
                Raised = data.IsRaised, Yield = perHarvest, Plan = plan, Net = net,
                PerDay = CropMath.GoldPerTilePerDay(plan, net)
            };
            (plan.Matures ? ready : late).Add(row);
            bySeed[kv.Key] = row;
        }

        var sb = new StringBuilder();
        sb.AppendLine(today
            ? $"Crops for {seasonName}. Today is {seasonName} {startDay}, so a crop planted and watered today has {CropMath.DaysPerSeason - startDay} growing days before {seasonName} ends."
            : $"Crops for {seasonName}, planned from day 1 of {seasonName} (today is {Game1.currentSeason} {Game1.dayOfMonth}).");
        sb.AppendLine("Numbers assume you water every day (rain does it for you), normal quality and the cheapest seed price below. g/tile/day = net gold / days the tile is busy.");

        AppendCarried(sb, carried, bySeed, season, startDay, today);

        if (ready.Count == 0 && late.Count == 0)
            sb.AppendLine($"No crop grows outdoors in {seasonName}. The Greenhouse ignores seasons.");

        if (ready.Count > 0)
        {
            sb.AppendLine($"Will mature if planted {(today ? "today" : "on day 1")} (best gold per tile per day first):");
            foreach (var r in ready.OrderByDescending(x => x.PerDay).ThenBy(x => x.Harvest))
                AppendReady(sb, r, season, startDay, today);
        }
        if (late.Count > 0)
        {
            sb.AppendLine($"Too late, would die when the season changes if planted {(today ? "today" : "on day 1")}:");
            foreach (var r in late.OrderBy(x => x.Growth).ThenBy(x => x.Harvest))
                sb.AppendLine($"- {r.Harvest} (seeds: {r.Seed}){(r.Have > 0 ? $" — you carry {r.Have}" : "")}: needs {Plural(r.Growth, "day")}, only {r.Plan.DaysAvailable} left.");
        }

        if (ready.Concat(late).Any(x => x.Raised))
            sb.AppendLine("Trellis crops [trellis] block walking: leave a path between the rows.");
        var boosters = BoosterLines();
        if (boosters.Count > 0)
        {
            sb.AppendLine("Fertilizer you carry (apply with plant before the seeds; the day counts above do not include Speed-Gro):");
            foreach (var line in boosters) sb.AppendLine("  " + line);
        }
        sb.AppendLine("Prices come from the game data; 'about' means the shop gave no fixed price and it is estimated as twice the seed's base value, so confirm with shop_list. " +
                      "A Tiller sells crops for 10% more. Crops in the Greenhouse ignore seasons.");
        return (true, sb.ToString().TrimEnd());
    }

    private static void AppendCarried(StringBuilder sb, Dictionary<string, int> carried, Dictionary<string, CropRow> bySeed, int season, int startDay, bool today)
    {
        var lines = new List<string>();
        foreach (var kv in carried)
        {
            if (!Game1.cropData.TryGetValue(kv.Key, out var data) || data == null) continue;
            if (bySeed.TryGetValue(kv.Key, out var row))
                lines.Add(row.Plan.Matures
                    ? $"{row.Seed} x{kv.Value}: {PlantText(season, row.Plan.LastPlantDay, startDay, today).ToLowerInvariant()}"
                    : $"{row.Seed} x{kv.Value}: too late this season");
            else
                lines.Add($"{SafeName(CropMath.Qualify(kv.Key), kv.Key)} x{kv.Value}: does not grow in {CropMath.SeasonName(season)} (grows in {SeasonsText(data.Seasons?.Select(x => x.ToString()))})");
        }
        if (lines.Count > 0)
            sb.AppendLine("Seeds you carry — " + string.Join("; ", lines) + ".");
    }

    private static void AppendReady(StringBuilder sb, CropRow r, int season, int startDay, bool today)
    {
        var when = today ? "if planted today" : "if planted on day 1";
        var first = CropMath.Advance(season, startDay, r.Growth);
        sb.AppendLine($"- {r.Harvest} (seeds: {r.Seed}{(r.Have > 0 ? $", you carry {r.Have}" : "")}){(r.Raised ? " [trellis]" : "")}");
        sb.Append("    ");
        sb.Append($"{Plural(r.Growth, "day")} to grow, ");
        sb.Append(r.Regrow > 0
            ? $"then a harvest every {r.Regrow} days ({Plural(r.Plan.Harvests, "harvest")} {when}). "
            : $"one harvest per planting ({Plural(r.Plan.Plantings, "planting")} fit back to back). ");
        sb.Append($"{PlantText(season, r.Plan.LastPlantDay, startDay, today)}; first harvest {DayLabel(first.season, first.day)} {when}. ");
        sb.Append($"Sells {(r.Sell > 0 ? r.Sell + "g" : "for an unknown price")}{(Math.Abs(r.Yield - 1.0) > 0.001 ? $" x{Num(r.Yield, "0.##")} per harvest" : "")}. ");
        sb.Append(r.Offers.Length > 0 ? $"Seed: {r.Offers}. " : "Seed: not in Pierre's or Joja's stock (festival, Traveling Cart or another source), cost not counted. ");
        sb.AppendLine($"About {Num(r.PerDay)}g per tile per day (net {Num(r.Net, "0")}g per tile).");
    }

    /// <summary>The last day to plant, or "any day" when the crop still matures if planted on the last day of the season.</summary>
    private static string PlantText(int season, int lastPlantDay, int startDay, bool today)
    {
        if (lastPlantDay >= CropMath.DaysPerSeason) return "Plant any day this season";
        return $"Plant by {CropMath.SeasonName(season)} {lastPlantDay}{(today && lastPlantDay == startDay ? " (today is the last day)" : "")}";
    }

    private static string DayLabel(int season, int day) =>
        $"{CropMath.SeasonName(season)} {day}{(season >= 4 ? " (next year)" : "")}";

    private static string SeasonsText(IEnumerable<string>? seasons) =>
        seasons == null ? "no season" : string.Join("/", seasons.Select(x => x.ToLowerInvariant()));

    private static Dictionary<string, int> CarriedObjects()
    {
        var have = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in Game1.player.Items)
        {
            if (it == null) continue;
            var id = CropMath.Unqualify(it.QualifiedItemId);
            have.TryGetValue(id, out int n);
            have[id] = n + it.Stack;
        }
        return have;
    }

    private static List<string> BoosterLines()
    {
        var lines = new List<string>();
        foreach (var it in Game1.player.Items)
        {
            if (it == null) continue;
            foreach (var (name, effect) in Boosters)
                if (string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add($"{it.DisplayName} x{it.Stack}: {effect}");
                    break;
                }
        }
        return lines;
    }

    // ------------------------------------------------------------------ shops

    private sealed record ShopOffer(string Shop, int Price, bool Estimated, string Unit, string Trade);

    /// <summary>Every shop (or only those named) whose item list holds the item by id, with the price the list gives.</summary>
    private static List<ShopOffer> ShopOffers(string qualifiedId, int objectBasePrice, params string[] onlyShops)
    {
        var offers = new List<ShopOffer>();
        foreach (var shop in DataLoader.Shops(Game1.content))
        {
            if (onlyShops.Length > 0 && !onlyShops.Contains(shop.Key)) continue;
            if (shop.Value?.Items == null) continue;
            foreach (var entry in shop.Value.Items)
            {
                if (entry == null || !CropMath.SameItem(entry.ItemId, qualifiedId)) continue;
                var (price, estimated) = CropMath.ShopPrice(entry.Price, objectBasePrice);
                var trade = string.IsNullOrEmpty(entry.TradeItemId) ? "" : SafeName(entry.TradeItemId, entry.TradeItemId);
                offers.Add(new ShopOffer(shop.Key, price, estimated, CurrencyUnit(shop.Value.Currency), trade));
                break; // one mention per shop
            }
        }
        return offers;
    }

    private static string CurrencyUnit(int currency) => currency switch
    {
        0 => "g",
        1 => " star tokens",
        2 => " Qi coins",
        4 => " Qi gems",
        _ => " (other currency)"
    };

    private static string ShopLabel(string shopId) => shopId switch
    {
        "SeedShop" => "Pierre's (SeedShop)",
        "Joja" => "Joja Mart (Joja)",
        _ => shopId
    };

    private static string OfferText(ShopOffer o) =>
        $"{ShopLabel(o.Shop)} {(o.Estimated ? "about " : "")}{o.Price}{o.Unit}{(o.Trade.Length > 0 ? $" + {o.Trade}" : "")}";

    /// <summary>Display name of an item id, or the fallback when the game does not know it.</summary>
    private static string SafeName(string id, string fallback)
    {
        try { return ItemRegistry.GetData(id)?.DisplayName ?? fallback; }
        catch { return fallback; }
    }

    // ------------------------------------------------------------------ item_info

    private sealed record ObjRef(string Id, string Display, string Name);

    private static (bool ok, string text) ItemInfoReport(JsonElement a)
    {
        if (Game1.objectData == null) return (false, "The object data is not loaded yet.");
        var query = AgentTools.S(a, "name").Trim();
        if (query.Length == 0) return (false, "Give the item name, e.g. item_info(name=\"Parsnip\").");

        var (matches, exact) = FindObjects(query);
        if (matches.Count == 0)
            return (false, $"No object called \"{query}\". item_info covers objects (crops, seeds, forage, fish, food, artisan goods, minerals), not tools, weapons or furniture; check the spelling.");
        if (!exact && matches.Count > 1)
        {
            var names = matches.Select(m => m.Display).Distinct().OrderBy(n => n).Take(MaxCandidates);
            return (false, $"Several items match \"{query}\": {string.Join(", ", names)}{(matches.Count > MaxCandidates ? ", ..." : "")}. Ask again with the full name.");
        }

        var parts = matches.Take(3).Select(Describe).ToList();
        if (matches.Count > 3) parts.Add($"({matches.Count - 3} more objects are also called \"{query}\".)");
        return (true, string.Join("\n\n", parts));
    }

    /// <summary>Objects whose shown name, English name or id equals the query; else those whose names contain it.</summary>
    private static (List<ObjRef> matches, bool exact) FindObjects(string query)
    {
        string q = query.ToLowerInvariant(), id = CropMath.Unqualify(query).ToLowerInvariant();
        var exact = new List<ObjRef>();
        var containing = new List<ObjRef>();
        foreach (var kv in Game1.objectData)
        {
            var data = kv.Value;
            if (data == null) continue;
            var internalName = data.Name ?? "";
            var display = SafeName(CropMath.Qualify(kv.Key), internalName.Length > 0 ? internalName : kv.Key);
            string d = display.ToLowerInvariant(), n = internalName.ToLowerInvariant();
            var r = new ObjRef(kv.Key, display, internalName);
            if (d == q || n == q || kv.Key.ToLowerInvariant() == id) exact.Add(r);
            else if (d.Contains(q) || n.Contains(q)) containing.Add(r);
        }
        return exact.Count > 0 ? (exact, true) : (containing, false);
    }

    private static string Describe(ObjRef o)
    {
        var data = Game1.objectData[o.Id];
        string qid = CropMath.Qualify(o.Id);
        var item = TryCreate(qid);
        string category = "";
        try { category = item?.getCategoryName() ?? ""; } catch { }

        var sb = new StringBuilder();
        sb.AppendLine($"{o.Display} (id {o.Id}){(category.Length > 0 ? " — " + category : "")}");

        if (data.Price > 0)
            sb.AppendLine($"Sells for {data.Price}g at normal quality; Silver {CropMath.PriceAtQuality(data.Price, 1)}g, Gold {CropMath.PriceAtQuality(data.Price, 2)}g, " +
                          $"Iridium {CropMath.PriceAtQuality(data.Price, 4)}g where the item comes in those qualities. Professions (Tiller, Artisan...) change the real payout.");
        else
            sb.AppendLine("Has no sell price (base price 0).");

        if (data.Edibility > -300)
            sb.AppendLine(item is SObject food
                ? $"Edible: restores {food.staminaRecoveredOnConsumption()} energy and {food.healthRecoveredOnConsumption()} health (use the eat tool)."
                : "Edible.");
        else
            sb.AppendLine("Not edible.");

        var offers = ShopOffers(qid, data.Price);
        if (offers.Count == 0)
            sb.AppendLine("Shops: no shop lists it by item id (random stock, festivals and the Traveling Cart may still sell it).");
        else
        {
            sb.AppendLine("Shops: " + string.Join("; ", offers.Take(MaxShops).Select(OfferText)) + (offers.Count > MaxShops ? $"; and {offers.Count - MaxShops} more" : "") +
                          (offers.Any(x => x.Estimated) ? " ('about' = estimated as twice the base value; confirm with shop_list)." : "."));
        }

        AppendCropLinks(sb, o);
        AppendRecipes(sb, "Crafting", "Data/CraftingRecipes", false, o.Id, data.Category);
        AppendRecipes(sb, "Cooking", "Data/CookingRecipes", true, o.Id, data.Category);
        return sb.ToString().TrimEnd();
    }

    private static Item? TryCreate(string qualifiedId)
    {
        try { return ItemRegistry.Create(qualifiedId); }
        catch { return null; }
    }

    /// <summary>"Plants into X" for seeds and "Grows from Y" for harvests, so the AI knows to call crops.</summary>
    private static void AppendCropLinks(StringBuilder sb, ObjRef o)
    {
        if (Game1.cropData == null) return;
        if (Game1.cropData.TryGetValue(o.Id, out var seedOf) && seedOf != null)
            sb.AppendLine($"Seed: plants into {SafeName(CropMath.Qualify(CropMath.Unqualify(seedOf.HarvestItemId)), seedOf.HarvestItemId ?? "?")}, " +
                          $"{CropLine(seedOf.Seasons?.Select(x => x.ToString()), seedOf.DaysInPhase, seedOf.RegrowDays)}. Use crops for the planting plan.");
        foreach (var kv in Game1.cropData)
        {
            if (kv.Value == null || kv.Key == o.Id) continue;
            if (!string.Equals(CropMath.Unqualify(kv.Value.HarvestItemId), o.Id, StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine($"Harvest: grown from {SafeName(CropMath.Qualify(kv.Key), kv.Key)}, {CropLine(kv.Value.Seasons?.Select(x => x.ToString()), kv.Value.DaysInPhase, kv.Value.RegrowDays)}.");
        }
    }

    private static string CropLine(IEnumerable<string>? seasons, IEnumerable<int>? daysInPhase, int regrow)
    {
        int growth = daysInPhase == null ? 0 : CropMath.GrowthDays(daysInPhase);
        return $"grows in {SeasonsText(seasons)}, {Plural(growth, "day")}{(regrow > 0 ? $", then every {regrow} days" : "")}";
    }

    private static void AppendRecipes(StringBuilder sb, string label, string asset, bool cooking, string itemId, int category)
    {
        var found = new List<(string name, int count, bool viaCategory, bool known)>();
        try
        {
            var recipes = Game1.content.Load<Dictionary<string, string>>(asset);
            foreach (var kv in recipes)
            {
                var (count, viaCategory) = RecipeMath.IngredientCount(kv.Value, itemId, category);
                if (count <= 0) continue;
                bool known = cooking ? Game1.player.cookingRecipes.ContainsKey(kv.Key) : Game1.player.craftingRecipes.ContainsKey(kv.Key);
                found.Add((kv.Key, count, viaCategory, known));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{label} recipes: could not be read ({ex.GetType().Name}).");
            return;
        }
        if (found.Count == 0) { sb.AppendLine($"{label} recipes: none use it."); return; }
        var shown = found.OrderByDescending(f => f.known).ThenBy(f => f.name).Take(MaxRecipes)
            .Select(f => $"{f.name} (x{f.count}{(f.viaCategory ? ", any item of its category" : "")}{(f.known ? ", you know it" : "")})");
        sb.AppendLine($"{label} recipes using it: {string.Join(", ", shown)}{(found.Count > MaxRecipes ? $", and {found.Count - MaxRecipes} more" : "")}.");
    }
}
