using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// Marnie's ranch (#10): <c>buy_animal</c> buys an animal into a coop or barn, and <c>animals</c> looks after them:
/// pet, collect the products and fill the troughs with hay.
///
/// Marnie's counter opens <c>PurchaseAnimalsMenu</c>, which needs the mouse on a map of the farm. The tool skips the menu:
/// it builds the animal like the menu does, hands it to the chosen <see cref="AnimalHouse"/> and pays from the calling farmer's
/// wallet. Prices and housing come from the game's own animal data (<c>Game1.farmAnimalData</c>), with a built-in table as a
/// fallback. Care actions walk to the animal and call the game's own pet, tool and hopper code.
/// </summary>
public static partial class AgentTools
{
    internal static List<ToolDef> AnimalToolDefs() => new()
    {
        new()
        {
            Name = "buy_animal",
            Description = "Buy a farm animal from Marnie (animal shop, AnimalShop map in the Forest; you must be inside while she is there, about 9:00-16:00). " +
                          "type=list (or no type) shows what she sells, the price, the building each animal needs and whether you own one with room. " +
                          "type=<animal> pays from your own wallet and puts a baby animal into a finished Coop or Barn of yours (building picks which one). " +
                          "Chickens live in a Coop, cows in a Barn; Robin builds those (build tool). Young animals mature in a few days.",
            Schema = Obj(new
            {
                type = Str("Animal as buy_animal(type=list) shows it, e.g. Chicken, Cow, Duck, Goat; or \"list\""),
                building = Str("Optional: which building, by name (\"Coop\") or tile \"x,y\" as animals/look show it; default: the first one with room"),
                name = Str("Optional name for the animal; default a random name"),
                reason = Str(ReasonDesc)
            }),
            InstantFor = a =>
            {
                var t = S(a, "type").Trim();
                return t.Length == 0 || t.Equals("list", StringComparison.OrdinalIgnoreCase);
            },
            Run = BuyAnimal
        },
        new()
        {
            Name = "animals",
            Description = "Look after your farm animals. Without an action it lists them (where they live, pet today or not, product ready) and the hay in the silo. " +
                          "pet: walks to each animal in the place you are in (the Farm, or inside a coop or barn: travel_to its name) and pets it; pet them every day. " +
                          "collect: picks up eggs and other products from the floor of a coop or barn, and milks cows and goats (Milk Pail) or shears sheep and rabbits (Shears) when they have a product ready. " +
                          "feed: fills the troughs of the coop or barn you are in from the silo's hay (needs a Silo with hay; Marnie sells hay). Animals outside eat grass.",
            Schema = Obj(new
            {
                action = new { type = "string", @enum = new[] { "list", "pet", "collect", "feed" }, description = "Omit or list to see your animals" },
                name = Str("Optional: only this animal (its name or type) for pet and collect"),
                reason = Str(ReasonDesc)
            }),
            InstantFor = a =>
            {
                var act = S(a, "action").Trim();
                return act.Length == 0 || act.Equals("list", StringComparison.OrdinalIgnoreCase);
            },
            Run = AnimalsTool
        }
    };

    // ------------------------------------------------------------------ catalog

    private sealed record AnimalSpec(string TypeId, string Name, int Price, string House, string Required);

    /// <summary>Used only when the game's animal data can't be read.</summary>
    private static readonly AnimalSpec[] FallbackAnimals =
    {
        new("White Chicken", "Chicken", 800, "Coop", "Coop"),
        new("Duck", "Duck", 1200, "Coop", "Big Coop"),
        new("Rabbit", "Rabbit", 8000, "Coop", "Deluxe Coop"),
        new("White Cow", "Cow", 1500, "Barn", "Barn"),
        new("Goat", "Goat", 4000, "Barn", "Big Barn"),
        new("Sheep", "Sheep", 8000, "Barn", "Deluxe Barn"),
        new("Pig", "Pig", 16000, "Barn", "Deluxe Barn")
    };

    private static string HouseFamily(string typeId) =>
        typeId.Contains("Cow", StringComparison.OrdinalIgnoreCase) || typeId.Contains("Goat", StringComparison.OrdinalIgnoreCase) ||
        typeId.Contains("Sheep", StringComparison.OrdinalIgnoreCase) || typeId.Contains("Pig", StringComparison.OrdinalIgnoreCase) ? "Barn" : "Coop";

    /// <summary>What Marnie sells, read from the game's animal data (animals with a purchase price); one entry per name.</summary>
    private static List<AnimalSpec> AnimalCatalog()
    {
        var list = new List<AnimalSpec>();
        try
        {
            foreach (var (key, d) in RanchReflect.Entries(RanchReflect.GetStatic(typeof(Game1), "farmAnimalData")))
            {
                if (d == null) continue;
                int price = RanchReflect.Int(d, "PurchasePrice") ?? 0;
                if (price <= 0) continue;
                if (RanchReflect.CheckConditions(RanchReflect.Str(d, "UnlockCondition")) == false) continue;
                var raw = RanchReflect.Str(d, "ShopDisplayName");
                if (string.IsNullOrWhiteSpace(raw)) raw = RanchReflect.Str(d, "DisplayName");
                var house = RanchReflect.Str(d, "House");
                if (string.IsNullOrWhiteSpace(house)) house = HouseFamily(key);
                var needs = RanchReflect.Str(d, "RequiredBuilding");
                if (string.IsNullOrWhiteSpace(needs)) needs = house;
                list.Add(new AnimalSpec(key, RanchReflect.DisplayText(raw, key), price, house, needs));
            }
        }
        catch (Exception)
        {
            list.Clear();
        }
        if (list.Count == 0) list.AddRange(FallbackAnimals);
        return list.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(s => s.Price).ToList();
    }

    private static AnimalSpec? FindAnimal(List<AnimalSpec> all, string want) =>
        all.FirstOrDefault(s => s.Name.Equals(want, StringComparison.OrdinalIgnoreCase) || s.TypeId.Equals(want, StringComparison.OrdinalIgnoreCase))
        ?? all.FirstOrDefault(s => s.Name.Contains(want, StringComparison.OrdinalIgnoreCase) || s.TypeId.Contains(want, StringComparison.OrdinalIgnoreCase));

    private static string BuildingFamily(string buildingType) =>
        buildingType.Contains("Coop", StringComparison.OrdinalIgnoreCase) ? "Coop"
        : buildingType.Contains("Barn", StringComparison.OrdinalIgnoreCase) ? "Barn" : "";

    /// <summary>Coop and Barn are tier 1, Big tier 2, Deluxe tier 3.</summary>
    private static int BuildingTier(string buildingType) =>
        buildingType.StartsWith("Deluxe", StringComparison.OrdinalIgnoreCase) ? 3
        : buildingType.StartsWith("Big", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private sealed record AnimalHome(Building Building, AnimalHouse House, int Count, int Limit);

    /// <summary>Finished animal houses of the farm that meet an animal's housing requirement (Coop family and tier at least the required one).</summary>
    private static List<AnimalHome> HomesFor(AnimalSpec spec)
    {
        var homes = new List<AnimalHome>();
        var needs = string.IsNullOrWhiteSpace(spec.Required) ? spec.House : spec.Required;
        var family = BuildingFamily(needs);
        int tier = BuildingTier(needs);
        foreach (var b in Game1.getFarm().buildings)
        {
            var type = b.buildingType.Value;
            if (BuildingFamily(type) != family || BuildingTier(type) < tier) continue;
            if (RanchText.DaysOfConstructionLeft(b) > 0 || RanchText.DaysUntilUpgrade(b) > 0) continue;
            if (b.indoors.Value is not AnimalHouse house) continue;
            homes.Add(new AnimalHome(b, house, house.animals.Values.Count(), RanchReflect.Int(house, "animalLimit") ?? 0));
        }
        return homes;
    }

    private static string HomeLabel(AnimalHome h) => $"{h.Building.buildingType.Value} at ({h.Building.tileX.Value},{h.Building.tileY.Value})";

    private static string HomeRoom(AnimalHome h) => h.Limit > 0 ? $"{h.Count}/{h.Limit} animals" : $"{h.Count} animals";

    /// <summary>The building the farmer asked for (by name or by "x,y"), else the first with room. A problem text when there is none.</summary>
    private static (AnimalHome? home, string? problem) PickHome(List<AnimalHome> homes, string wanted)
    {
        var candidates = homes;
        wanted = wanted.Trim();
        if (wanted.Length > 0)
        {
            var m = Regex.Match(wanted, @"(\d+)\s*[, ]\s*(\d+)");
            candidates = m.Success
                ? homes.Where(h => h.Building.tileX.Value == int.Parse(m.Groups[1].Value) && h.Building.tileY.Value == int.Parse(m.Groups[2].Value)).ToList()
                : homes.Where(h => h.Building.buildingType.Value.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0)
                return (null, $"None of your suitable buildings matches \"{wanted}\". Suitable: {string.Join("; ", homes.Select(HomeLabel))}.");
        }
        var withRoom = candidates.FirstOrDefault(h => !h.House.isFull());
        if (withRoom == null)
            return (null, $"No room: {string.Join("; ", candidates.Select(h => $"{HomeLabel(h)} is full ({HomeRoom(h)})"))}. Build another one with Robin (build).");
        return (withRoom, null);
    }

    // ------------------------------------------------------------------ buy_animal

    private static IEnumerator BuyAnimal(JsonElement a, CallContext c)
    {
        var want = S(a, "type").Trim();
        var catalog = AnimalCatalog();
        if (want.Length == 0 || want.Equals("list", StringComparison.OrdinalIgnoreCase)) { c.Result = AnimalShopList(catalog); yield break; }

        var spec = FindAnimal(catalog, want);
        if (spec == null) { c.Fail($"Marnie doesn't sell \"{want}\". She sells: {string.Join(", ", catalog.Select(s => s.Name))}."); yield break; }

        var closed = ShopGate("AnimalShop", "Marnie", 900, 1600, "Marnie's animal shop");
        if (closed != null) { c.Fail(closed); yield break; }
        if (P.Money < spec.Price) { c.Fail($"A {spec.Name} costs {spec.Price:N0}g; you have {P.Money:N0}g."); yield break; }

        var homes = HomesFor(spec);
        if (homes.Count == 0)
        {
            c.Fail($"A {spec.Name} needs a finished {spec.Required} and you have none. " +
                   (spec.Required.StartsWith("Coop") || spec.Required.StartsWith("Barn")
                       ? "Robin builds it (build tool)."
                       : "Robin's upgrade of an existing coop or barn to a bigger one is not available in this mod yet."));
            yield break;
        }
        var (home, problem) = PickHome(homes, S(a, "building"));
        if (home == null) { c.Fail(problem ?? "No building for the animal."); yield break; }

        var name = S(a, "name").Trim();
        if (name.Length > 20) name = name.Substring(0, 20);
        if (name.Length == 0) name = Dialogue.randomName();

        var (animal, error) = TryAdopt(spec, home, name);
        if (animal == null) { c.Fail($"Could not add the {spec.Name}: {error} Nothing was charged."); yield break; }

        P.Money -= spec.Price;
        yield return Ticks(5);
        c.Result = $"Marnie sold you a {spec.Name} named {name} for {spec.Price:N0}g. It lives in the {HomeLabel(home)} ({home.Count + 1}" +
                   (home.Limit > 0 ? $"/{home.Limit}" : "") + " animals now). Money left " + P.Money + "g. " +
                   "It is a baby and grows up in a few days. Pet it and fill the troughs every day (animals tool).";
    }

    private static string AnimalShopList(List<AnimalSpec> catalog)
    {
        var sb = new StringBuilder($"Marnie sells these (you have {P.Money:N0}g). She is in the AnimalShop map (travel_to AnimalShop) about 9:00-16:00:\n");
        foreach (var s in catalog)
        {
            var homes = HomesFor(s);
            string housing;
            if (homes.Count == 0) housing = $"needs a {s.Required} (you have none)";
            else
            {
                var room = homes.Where(h => !h.House.isFull()).ToList();
                housing = room.Count > 0 ? $"room in {string.Join(", ", room.Select(h => $"{HomeLabel(h)} {HomeRoom(h)}"))}" : "your buildings for it are full";
            }
            sb.AppendLine($"  {s.Name}: {s.Price:N0}g · lives in a {s.Required} · {housing}{(P.Money < s.Price ? $" · you lack {s.Price - P.Money:N0}g" : "")}");
        }
        sb.Append("buy_animal(type=\"Chicken\", name=\"Clucky\") buys one. A Milk Pail or Shears (for milk and wool) and hay are also sold by Marnie.");
        return sb.ToString();
    }

    private static long NewAnimalId()
    {
        var mp = HarmonyLib.AccessTools.Field(typeof(Game1), "multiplayer")?.GetValue(null);
        if (mp != null)
        {
            var r = RanchReflect.TryCall(mp, "getNewID");
            if (r.found && r.error == null && r.value is long id && id != 0) return id;
        }
        return Random.Shared.NextInt64(1, long.MaxValue);
    }

    /// <summary>
    /// Creates the animal like Marnie's menu does and hands it to the animal house. Plain method: it needs try/catch around
    /// calls into the game, which an iterator can't have.
    /// </summary>
    private static (FarmAnimal? animal, string? error) TryAdopt(AnimalSpec spec, AnimalHome home, string name)
    {
        try
        {
            var animal = (FarmAnimal)Activator.CreateInstance(typeof(FarmAnimal), spec.TypeId, NewAnimalId(), P.UniqueMultiplayerID)!;
            RanchReflect.Set(animal, "Name", name);
            RanchReflect.Set(animal, "displayName", name);
            home.House.adoptAnimal(animal);
            if (!home.House.animals.Values.Contains(animal)) return (null, "the animal house did not take it.");
            if (animal.Position == Vector2.Zero) RanchReflect.TryCall(animal, "setRandomPosition", home.House);
            return (animal, null);
        }
        catch (Exception ex)
        {
            return (null, (ex.InnerException ?? ex).Message + ".");
        }
    }

    // ------------------------------------------------------------------ animals

    private static IEnumerator AnimalsTool(JsonElement a, CallContext c)
    {
        var action = S(a, "action").Trim().ToLowerInvariant();
        var name = S(a, "name").Trim();
        switch (action)
        {
            case "":
            case "list":
                c.Result = AnimalOverview();
                yield break;
            case "pet":
                yield return PetAnimals(name, c);
                yield break;
            case "collect":
                yield return CollectAnimals(name, c);
                yield break;
            case "feed":
                yield return FeedAnimals(c);
                yield break;
            default:
                c.Fail($"Unknown action \"{action}\". Use list, pet, collect or feed.");
                yield break;
        }
    }

    private static string AnimalOverview()
    {
        var all = RanchText.AllFarmAnimals();
        var mine = all.Where(t => RanchText.IsMine(t.animal)).ToList();
        if (all.Count == 0)
            return "No farm animals yet. Robin builds a Coop or Barn (build), then Marnie sells animals (buy_animal).";

        var sb = new StringBuilder($"Your farm animals: {mine.Count}");
        if (all.Count > mine.Count) sb.Append($" (and {all.Count - mine.Count} that belong to other players)");
        sb.AppendLine(".");
        foreach (var grp in mine.GroupBy(t => t.home))
        {
            if (grp.Key == null)
                sb.AppendLine("Outside on the Farm (they eat grass):");
            else
                sb.AppendLine($"{grp.Key.buildingType.Value} at ({grp.Key.tileX.Value},{grp.Key.tileY.Value}), enter with travel_to \"{grp.Key.GetIndoorsName()}\":");
            foreach (var t in grp)
                sb.AppendLine($"  {RanchText.AnimalLabel(t.animal)}{(grp.Key == null ? $" at ({t.animal.TilePoint.X},{t.animal.TilePoint.Y})" : "")}");
        }
        int unpet = mine.Count(t => !t.animal.wasPet.Value);
        if (unpet > 0) sb.AppendLine($"{unpet} not pet today: go to them and use animals(action=\"pet\").");
        var hay = RanchReflect.Int(Game1.getFarm(), "piecesOfHay");
        if (hay != null) sb.AppendLine($"Hay in the silo: {hay}.");
        sb.Append("pet, collect and feed work on the animals in the place you are standing in (the Farm, or inside the coop or barn).");
        return sb.ToString();
    }

    private static string NoAnimalsHere() =>
        $"There are no farm animals in {Loc.Name}. Animals live outside on the Farm or inside a coop or barn: animals lists where (travel_to the building's name).";

    /// <summary>The animals to work on: those called <paramref name="name"/> (by name, else by type; any owner), else your own that pass the filter.</summary>
    private static List<FarmAnimal> SelectAnimals(List<FarmAnimal> here, string name, Func<FarmAnimal, bool> filter)
    {
        if (name.Length == 0) return here.Where(x => RanchText.IsMine(x) && filter(x)).ToList();
        var exact = here.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        return here.Where(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || RanchText.TypeName(x).Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static bool TryPet(FarmAnimal animal)
    {
        try
        {
            animal.pet(P);
            return animal.wasPet.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerator PetAnimals(string name, CallContext c)
    {
        var start = Loc;
        var here = RanchText.AnimalsAt(start);
        if (here.Count == 0) { c.Fail(NoAnimalsHere()); yield break; }
        var targets = SelectAnimals(here, name, x => !x.wasPet.Value);
        if (targets.Count == 0)
        {
            c.Result = name.Length > 0 ? $"No animal called \"{name}\" here. Here: {string.Join(", ", here.Select(x => x.Name))}." : "All your animals here were already pet today.";
            yield break;
        }

        var lines = new List<string>();
        int petted = 0;
        foreach (var animal in targets.Take(30))
        {
            if (Game1.activeClickableMenu != null) { lines.Add("Stopped: a menu opened."); break; }
            if (Loc != start) break;
            if (animal.wasPet.Value) continue;

            bool done = false;
            string? why = null;
            for (int attempt = 0; attempt < 2 && !done; attempt++)
            {
                var sub = new CallContext();
                yield return WalkAdjacent(animal.TilePoint, sub);
                if (!sub.Ok) { why = sub.Result; continue; }
                var me = P.TilePoint;
                if (Math.Abs(me.X - animal.TilePoint.X) + Math.Abs(me.Y - animal.TilePoint.Y) > 2) { why = "it moved away"; continue; } // it walked off
                yield return WaitIdle();
                Face(animal.TilePoint);
                done = TryPet(animal);
                if (!done) why = "the game did not count the pet";
                yield return Ticks(10);
            }
            if (done) petted++;
            else lines.Add($"{animal.Name}: could not pet ({why}).");
        }

        var head = $"Pet {petted} of {targets.Count} animal(s) in {start.Name}.";
        c.Result = lines.Count == 0 ? head : head + "\n" + string.Join("\n", lines);
        if (petted == 0 && lines.Count > 0) c.Fail(c.Result);
    }

    // ------------------------------------------------------------------ collect

    /// <summary>The tool an animal's product needs ("Milk Pail", "Shears"), or null when it just drops on the floor.</summary>
    private static string? HarvestToolName(FarmAnimal animal)
    {
        var data = RanchReflect.TryCall(animal, "GetAnimalData");
        if (data.found && data.error == null && data.value != null)
        {
            var tool = RanchReflect.Str(data.value, "HarvestTool");
            if (!string.IsNullOrWhiteSpace(tool)) return tool;
            if (RanchReflect.TryRead(data.value, data.value.GetType(), "HarvestTool", out _)) return null; // the data says: no tool
        }
        var type = RanchText.TypeName(animal);
        if (type.Contains("Cow") || type.Contains("Goat")) return "Milk Pail";
        if (type.Contains("Sheep") || type.Contains("Rabbit")) return "Shears";
        return null;
    }

    /// <summary>Tiles in an animal house with something to pick up from the floor (eggs, truffles, ...); never the hay in the troughs.</summary>
    private static List<Point> FloorProducts(GameLocation loc)
    {
        var tiles = new List<Point>();
        if (loc is not AnimalHouse) return tiles;
        foreach (var kv in loc.Objects.Pairs)
        {
            var o = kv.Value;
            if (o == null || o.bigCraftable.Value || o.Name == "Hay") continue;
            if (o.Category == -5 || o.Category == -6 || o.Category == -18 || RanchReflect.Bool(o, "IsSpawnedObject") == true || RanchReflect.Bool(o, "isSpawnedObject") == true)
                tiles.Add(new Point((int)kv.Key.X, (int)kv.Key.Y));
        }
        return tiles;
    }

    private static IEnumerator CollectAnimals(string name, CallContext c)
    {
        var start = Loc;
        var here = RanchText.AnimalsAt(start);
        if (here.Count == 0) { c.Fail(NoAnimalsHere()); yield break; }

        var before = Snapshot();
        var lines = new List<string>();

        // Eggs and other products lie on the floor of the coop or barn.
        var floor = FloorProducts(start);
        int picked = 0;
        foreach (var tile in floor.Take(40))
        {
            if (Game1.activeClickableMenu != null) break;
            var sub = new CallContext();
            yield return WalkAdjacent(tile, sub);
            if (!sub.Ok) { lines.Add($"({tile.X},{tile.Y}) skipped: {sub.Result}"); continue; }
            yield return WaitIdle();
            if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();
            if (PressAction(tile)) picked++;
            yield return Ticks(8);
        }
        if (floor.Count > 0) lines.Add($"Picked up from the floor: {picked} of {floor.Count} spot(s).");

        // Milk and wool need a tool used on the animal.
        var ready = SelectAnimals(here, name, _ => true).Where(x => RanchText.ProductReady(x) && HarvestToolName(x) != null).ToList();
        int harvested = 0;
        foreach (var animal in ready.Take(20))
        {
            if (Game1.activeClickableMenu != null) { lines.Add("Stopped: a menu opened."); break; }
            if (Loc != start) break;
            if (P.Stamina <= 2) { lines.Add($"Stopped: energy too low ({(int)P.Stamina})."); break; }

            var toolName = HarvestToolName(animal)!;
            int slot = FindItem(toolName, it => it is Tool);
            if (slot < 0) { lines.Add($"{animal.Name} ({RanchText.TypeName(animal)}) needs a {toolName}: you don't have one (Marnie sells it)."); continue; }

            bool done = false;
            for (int attempt = 0; attempt < 2 && !done; attempt++)
            {
                var sub = new CallContext();
                yield return WalkAdjacent(animal.TilePoint, sub);
                if (!sub.Ok) { lines.Add($"{animal.Name}: {sub.Result}"); break; }
                P.CurrentToolIndex = slot;
                yield return Swing(animal.TilePoint);
                done = !RanchText.ProductReady(animal);
            }
            if (done) harvested++;
            else lines.Add($"{animal.Name}: the {toolName} did not get its product (it moved, or the game refused).");
        }
        if (ready.Count > 0) lines.Add($"Harvested {harvested} of {ready.Count} animal(s) with a tool.");

        var diff = Diff(before);
        if (floor.Count == 0 && ready.Count == 0)
        {
            var waiting = here.Count(x => RanchText.IsMine(x) && RanchText.ProductReady(x));
            c.Result = waiting > 0
                ? $"{waiting} animal(s) have a product ready but not here, or the right tool is missing. Check animals."
                : "Nothing to collect here: no products on the floor and no animal has milk or wool ready. Eggs and wool come overnight; babies don't produce yet.";
            yield break;
        }
        c.Result = string.Join("\n", lines) + (diff.Length > 0 ? "\n" + diff : "\nNo change in your inventory (is it full?).");
    }

    // ------------------------------------------------------------------ feed

    private static List<Point> TroughTiles(GameLocation loc)
    {
        var tiles = new List<Point>();
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (loc.doesTileHaveProperty(x, y, "Trough", "Back") != null)
                tiles.Add(new Point(x, y));
        return tiles;
    }

    /// <summary>The tile of the hay hopper (an action tile whose action mentions feed or hopper), or null.</summary>
    private static Point? HopperTile(GameLocation loc)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
            if (action != null && (action.Contains("feed", StringComparison.OrdinalIgnoreCase) || action.Contains("hopper", StringComparison.OrdinalIgnoreCase)))
                return new Point(x, y);
        }
        return null;
    }

    private static int HayInTroughs(GameLocation loc) => loc.Objects.Values.Count(o => o != null && o.Name == "Hay");

    private static string? FeedAllAnimalsFallback(GameLocation loc)
    {
        var r = RanchReflect.TryCall(loc, "feedAllAnimals");
        if (!r.found) return "this game version has no feedAllAnimals.";
        return r.error;
    }

    private static IEnumerator FeedAnimals(CallContext c)
    {
        if (Loc is not AnimalHouse house)
        {
            c.Fail("Hay goes in the troughs inside a coop or barn (travel_to its name; animals lists them). Animals outside eat grass.");
            yield break;
        }
        var troughs = TroughTiles(house);
        if (troughs.Count == 0) { c.Fail($"No hay troughs found in {house.Name}."); yield break; }

        int hay0 = HayInTroughs(house);
        if (hay0 >= troughs.Count) { c.Result = $"All {troughs.Count} troughs in {house.Name} already have hay."; yield break; }

        var silo = RanchReflect.Int(Game1.getFarm(), "piecesOfHay");
        if (silo != null && silo.Value <= 0)
        {
            c.Fail("The silo has no hay. Build a Silo with Robin (build), buy Hay from Marnie (buy), and cut grass with a Scythe (use_tool) to fill it.");
            yield break;
        }

        // The hopper dispenses the silo's hay into the troughs; walk to it and use it like a player does.
        var hopper = HopperTile(house);
        if (hopper != null)
        {
            var sub = new CallContext();
            yield return WalkAdjacent(hopper.Value, sub);
            if (sub.Ok)
            {
                if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();
                PressAction(hopper.Value);
                yield return Ticks(20);
                yield return WaitIdle(180);
            }
        }

        string note = hopper != null ? "" : " (no hopper tile found; the game's feed-all call was used)";
        if (!HayPlaced(house, hay0, silo))
        {
            var err = FeedAllAnimalsFallback(house);
            yield return Ticks(10);
            note = " (the game's feed-all call was used" + (err != null ? $", it said: {err}" : "") + ")";
        }
        int hay1 = HayInTroughs(house);
        if (!HayPlaced(house, hay0, silo))
        {
            c.Fail($"No hay was placed ({hay1}/{troughs.Count} troughs full). The silo may be empty or not built, or this hopper works differently: try interact on the hopper tile.");
            yield break;
        }

        var left = RanchReflect.Int(Game1.getFarm(), "piecesOfHay");
        c.Result = $"Filled the troughs in {house.Name}: {hay1}/{troughs.Count} have hay now (was {hay0}){note}." + (left != null ? $" Hay left in the silo: {left}." : "");
    }

    /// <summary>True when feeding worked: more hay lies in the troughs, or the silo's hay went down (in case troughs are shown another way).</summary>
    private static bool HayPlaced(GameLocation house, int hayBefore, int? siloBefore)
    {
        if (HayInTroughs(house) > hayBefore) return true;
        var siloNow = RanchReflect.Int(Game1.getFarm(), "piecesOfHay");
        return siloBefore != null && siloNow != null && siloNow.Value < siloBefore.Value;
    }
}
