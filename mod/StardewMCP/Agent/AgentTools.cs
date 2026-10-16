using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

public delegate IEnumerator ToolRunner(JsonElement args, CallContext ctx);

public class ToolDef
{
    public string Name = null!;
    public string Description = null!;
    public object Schema = null!;
    public ToolRunner Run = null!;
    /// <summary>Instant tools (observations, thinking) do not unfreeze game time.</summary>
    public bool Instant;
    /// <summary>Instant for these arguments only, e.g. menu() reads while menu(action) acts.</summary>
    public Func<JsonElement, bool>? InstantFor;
    public bool NeedsWorld = true;
    public bool AppendStatus = true;
    /// <summary>Chat and whispers waiting for the player are appended to this tool's result (false for debug ops).</summary>
    public bool Mail = true;
}

/// <summary>The actions the AI can take. Mechanics (walking, aiming, button presses) are automated; decisions are the AI's.</summary>
public static partial class AgentTools
{
    /// <summary>Runtime of the seat whose screen is updating (tools run on that screen's tick).</summary>
    public static AgentRuntime Rt => Players.Current.Runtime;

    /// <summary>Shared settings; set once at startup so tool schemas can be built on any thread.</summary>
    public static ModConfig Config = new();
    private static Farmer P => Game1.player;
    private static GameLocation Loc => Game1.currentLocation;

    // ------------------------------------------------------------------ schema helpers

    internal static object Str(string desc) => new { type = "string", description = desc };
    internal static object Int(string desc) => new { type = "integer", description = desc };
    private static object Tiles(string desc) => new
    {
        type = "array",
        description = desc,
        items = new { type = "array", items = new { type = "integer" }, minItems = 2, maxItems = 2 }
    };
    internal static object Obj(object props, params string[] required) => new { type = "object", properties = props, required };

    /// <summary>Language for text shown to viewers (config ThoughtLanguage).</summary>
    private static string Lang =>
        string.IsNullOrWhiteSpace(Config.ThoughtLanguage) ? "Thai" : Config.ThoughtLanguage.Trim();

    private static string ReasonDesc => Lang.Equals("Thai", StringComparison.OrdinalIgnoreCase)
        ? "Short reason IN THAI shown to viewers on screen (e.g. \"ถางหญ้าเพื่อเตรียมแปลงปลูก\")"
        : $"Short reason IN {Lang.ToUpperInvariant()} shown to viewers on screen";

    public static string Reason(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";

    public static object? ArgsForLog(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, JsonElement>();
        foreach (var p in args.EnumerateObject())
            if (p.Name != "reason") d[p.Name] = p.Value.Clone();
        return d.Count > 0 ? d : null;
    }

    internal static string S(JsonElement a, string name, string def = "") =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;

    internal static int I(JsonElement a, string name, int def) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    private static bool Has(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static List<Point> TileList(JsonElement a, string name)
    {
        var list = new List<Point>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 2)
                    list.Add(new Point(t[0].GetInt32(), t[1].GetInt32()));
        return list;
    }

    private static string StatusText()
    {
        var prof = Professions.PendingText(Rt, P);
        return prof.Length > 0 ? WorldText.Status() + "\n" + prof : WorldText.Status();
    }

    public static string Footer()
    {
        var sb = new StringBuilder($"[{WorldText.FormatTime(Game1.timeOfDay)} · {Loc?.Name} ({P.TilePoint.X},{P.TilePoint.Y}) · energy {(int)P.Stamina}/{P.MaxStamina} · {P.Money}g]");
        var menu = WorldText.MenuState().Trim();
        if (menu.Length > 0) sb.Append('\n').Append(menu);
        var prof = Professions.PendingText(Rt, P);
        if (prof.Length > 0) sb.Append('\n').Append(prof);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ registry

    public static List<ToolDef> All()
    {
        return new List<ToolDef>(SocialTools.All().Concat(Core()));
    }

    private static List<ToolDef> Core()
    {
        return new List<ToolDef>
        {
            new()
            {
                Name = "think",
                Description = $"Say what you are thinking/planning, IN {Lang.ToUpperInvariant()}, 1-3 sentences. It is shown to viewers on screen. Call this before each new plan and whenever something surprising happens. Game time is frozen while you think.",
                Schema = Obj(new { text = Str($"Your thought in {Lang}") }, "text"),
                Instant = true, NeedsWorld = false, AppendStatus = false,
                Run = Think
            },
            new()
            {
                Name = "status",
                Description = "Current day, time, weather, location, position, energy, money, held item and any open dialogue/menu.",
                Schema = Obj(new { }),
                Instant = true, AppendStatus = false,
                Run = (a, c) => Instant(c, StatusText())
            },
            new()
            {
                Name = "look",
                Description = "ASCII map of the area around you with coordinates, plus a list of everything in view (trees, stones, crops, doors, villagers...) with exact (x,y) tiles. Use it to choose targets.",
                Schema = Obj(new { radius = Int("Tiles around you, 5-20 (default 12)") }),
                Instant = true,
                Run = (a, c) => Instant(c, WorldText.Look(Math.Clamp(I(a, "radius", 12), 5, 20)))
            },
            new()
            {
                Name = "inventory",
                Description = "Your inventory slots, item names, counts, and which items are edible.",
                Schema = Obj(new { }),
                Instant = true,
                Run = (a, c) => Instant(c, WorldText.Inventory())
            },
            new()
            {
                Name = "walk_to",
                Description = "Walk to a tile on the current map (pathfinding is automatic). If the tile is blocked you stop next to it. Time passes while walking.",
                Schema = Obj(new { x = Int("Tile x"), y = Int("Tile y"), reason = Str(ReasonDesc) }, "x", "y", "reason"),
                Run = WalkToTool
            },
            new()
            {
                Name = "use_tool",
                Description = "Use a tool on one or more tiles, in order. You automatically walk next to each tile, face it and swing. " +
                              "Axe/pickaxe/scythe keep swinging until the target (tree, stone, weeds, stump...) is gone or max_hits is reached. " +
                              "Hoe tills each tile once. Watering can waters each tile once; use it on a water tile (~) to refill. Stops early if energy runs out.",
                Schema = Obj(new
                {
                    tool = Str("Tool name: Axe, Pickaxe, Hoe, Watering Can, Scythe"),
                    tiles = Tiles("List of [x,y] tiles, max 40"),
                    max_hits = Int("Max swings per tile for axe/pickaxe (default 20)"),
                    reason = Str(ReasonDesc)
                }, "tool", "tiles", "reason"),
                Run = UseTool
            },
            new()
            {
                Name = "plant",
                Description = "Plant seeds (or apply fertilizer) on tilled soil tiles (=). You walk to each tile automatically.",
                Schema = Obj(new { item = Str("Seed/fertilizer name, e.g. \"Parsnip Seeds\""), tiles = Tiles("List of [x,y] tilled tiles, max 40"), reason = Str(ReasonDesc) }, "item", "tiles", "reason"),
                Run = Plant
            },
            new()
            {
                Name = "harvest",
                Description = "Harvest ready crops (!) or pick up forage items (F) on the given tiles.",
                Schema = Obj(new { tiles = Tiles("List of [x,y] tiles, max 40"), reason = Str(ReasonDesc) }, "tiles", "reason"),
                Run = Harvest
            },
            new()
            {
                Name = "interact",
                Description = "Walk next to a tile and press the action button on it: open doors, shop counters (A tiles), chests, machines, mailbox, read signs, check objects. " +
                              "With holding, hold that item while pressing: load a machine with it (e.g. Copper Ore into a Furnace), give it to the villager on that tile. " +
                              "With holding and no tile, use the item where you stand (totems, books). Without holding you hold a tool, so nothing is loaded or gifted by accident.",
                Schema = Obj(new
                {
                    x = Int("Tile x"),
                    y = Int("Tile y"),
                    holding = Str("Optional item from your inventory to hold while pressing"),
                    reason = Str(ReasonDesc)
                }, "reason"),
                Run = Interact
            },
            new()
            {
                Name = "talk_to",
                Description = "Walk to a villager on this map and talk to them. Optionally give them an item from your inventory as a gift.",
                Schema = Obj(new { name = Str("Villager name, e.g. Pierre"), gift_item = Str("Optional item name to give as a gift"), reason = Str(ReasonDesc) }, "name", "reason"),
                Run = TalkTo
            },
            new()
            {
                Name = "respond",
                Description = "Continue the open dialogue box, or answer a question by option number.",
                Schema = Obj(new { option = Int("Option number for questions; omit to just continue"), reason = Str(ReasonDesc) }),
                Run = Respond
            },
            new()
            {
                Name = "close_menu",
                Description = "Close the open menu, shop, letter or dialogue.",
                Schema = Obj(new { }),
                Run = CloseMenu
            },
            MenuTool(),
            new()
            {
                Name = "travel_to",
                Description = "Walk to another map through doors and exits: Farm, FarmHouse, BusStop, Town, SeedShop (Pierre's), Forest (Marnie), Beach, Mountain, Mine, Saloon, Blacksmith, etc. Locked doors stay locked outside opening hours.",
                Schema = Obj(new { location = Str("Destination map name"), reason = Str(ReasonDesc) }, "location", "reason"),
                Run = TravelTo
            },
            new()
            {
                Name = "shop_list",
                Description = "List items for sale in the open shop menu with prices.",
                Schema = Obj(new { }),
                Instant = true,
                Run = ShopList
            },
            new()
            {
                Name = "buy",
                Description = "Buy an item from the open shop menu.",
                Schema = Obj(new { item = Str("Item name as listed by shop_list"), quantity = Int("How many"), reason = Str(ReasonDesc) }, "item", "quantity", "reason"),
                Run = Buy
            },
            new()
            {
                Name = "ship",
                Description = "Put items into the farm shipping bin (walks to the bin). Shipped items are sold overnight.",
                Schema = Obj(new { item = Str("Item name"), quantity = Int("How many (default: all)"), reason = Str(ReasonDesc) }, "item", "reason"),
                Run = Ship
            },
            new()
            {
                Name = "eat",
                Description = "Eat an edible item from your inventory to restore energy.",
                Schema = Obj(new { item = Str("Item name"), reason = Str(ReasonDesc) }, "item", "reason"),
                Run = Eat
            },
            EquipTool(), SpecialOrdersTool(),
            new()
            {
                Name = "wait",
                Description = "Let game time pass (e.g. until a shop opens).",
                Schema = Obj(new { minutes = Int("Game minutes, 10-240"), reason = Str(ReasonDesc) }, "minutes", "reason"),
                Run = Wait
            },
            new()
            {
                Name = "choose_profession",
                Description = "Make a pending profession choice (offered at skill level 5 and 10, shown in status). It is permanent and changes what pays off, so read both options first.",
                Schema = Obj(new { option = Int("0 or 1, as numbered in status"), skill = Str("Skill name if more than one choice is pending (Farming, Fishing, Foraging, Mining, Combat)"), reason = Str(ReasonDesc) }, "option", "reason"),
                Run = ChooseProfession
            },
            new()
            {
                Name = "go_to_bed",
                Description = $"End the day: walk home to your bed and sleep. Write your diary for today IN {Lang.ToUpperInvariant()} first (what you did, what went well/badly, plan for tomorrow). Crops grow and shipped items are sold overnight.",
                Schema = Obj(new { diary = Str($"Today's diary in {Lang}, 3-8 sentences, written as yourself (Claude Haiku) farming"), reason = Str(ReasonDesc) }, "diary", "reason"),
                Run = GoToBed
            }
        };
    }

    // ------------------------------------------------------------------ simple tools

    internal static IEnumerator Instant(CallContext c, string text)
    {
        c.Result = text;
        yield break;
    }

    private static IEnumerator Think(JsonElement a, CallContext c)
    {
        var text = S(a, "text").Trim();
        if (text.Length == 0)
        {
            c.Fail("text is empty");
            yield break;
        }
        Rt.Log.Add("think", text: text);
        c.Result = "ok";
    }

    private static IEnumerator WalkToTool(JsonElement a, CallContext c)
    {
        var target = new Point(I(a, "x", -1), I(a, "y", -1));
        if (Pathfinder.IsWalkable(Loc, target.X, target.Y))
            yield return WalkTo(target, c);
        else
            yield return WalkAdjacent(target, c);
        if (c.Ok)
            c.Result = $"Arrived at ({P.TilePoint.X},{P.TilePoint.Y}).";
    }

    private static IEnumerator Wait(JsonElement a, CallContext c)
    {
        int minutes = Math.Clamp(I(a, "minutes", 30), 10, 240);
        int start = Utility.ConvertTimeToMinutes(Game1.timeOfDay);
        int day = Game1.Date.TotalDays;
        while (Game1.Date.TotalDays == day && Utility.ConvertTimeToMinutes(Game1.timeOfDay) - start < minutes)
            yield return null;
        c.Result = $"Waited until {WorldText.FormatTime(Game1.timeOfDay)}.";
    }

    // ------------------------------------------------------------------ movement helpers

    /// <summary>Wait until the farmer can act again (tool animations, warps, fades).</summary>
    private static IEnumerator WaitIdle(int maxTicks = 600)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            if (!P.UsingTool && P.CanMove && !Game1.isWarping && !Game1.fadeToBlack && !P.isEating && Game1.activeClickableMenu == null)
                yield break;
            if (Game1.activeClickableMenu != null && !P.UsingTool && !Game1.isWarping)
                yield break; // a menu/dialogue opened: let the AI handle it
            yield return null;
        }
    }

    private static IEnumerator Ticks(int n)
    {
        for (int i = 0; i < n; i++) yield return null;
    }

    public static IEnumerator WalkTo(Point target, CallContext c)
    {
        yield return WaitIdle();
        var loc = Loc;
        var start = P.TilePoint;
        if (start == target) yield break;

        var path = new Pathfinder().FindPath(loc, start.ToVector2(), target.ToVector2());
        if (path == null)
        {
            c.Fail($"No walkable path to ({target.X},{target.Y}) on {loc.Name}.");
            yield break;
        }

        var stack = new Stack<Point>();
        for (int i = path.Count - 1; i >= 0; i--) stack.Push(new Point((int)path[i].X, (int)path[i].Y));
        P.controller = new PathFindController(stack, P, loc) { NPCSchedule = false };
        P.setRunning(true, true);

        int limit = 60 * 5 + path.Count * 40;
        int stuck = 0;
        var last = P.Position;
        for (int t = 0; t < limit; t++)
        {
            if (Game1.currentLocation != loc) yield break; // walked through a warp
            // Stepped onto a warp: stop pushing into it, or the game re-warps (restarting the fade) every tick.
            if (Game1.isWarping) { P.controller = null; yield break; }
            if (P.controller == null) break;
            if (Vector2.DistanceSquared(P.Position, last) < 0.01f) stuck++; else stuck = 0;
            last = P.Position;
            if (stuck > 90)
            {
                // Something moved into the way (villager, animal): re-plan once from here.
                P.controller = null;
                P.Halt();
                var re = new Pathfinder().FindPath(loc, P.TilePoint.ToVector2(), target.ToVector2());
                if (re == null) break;
                var s2 = new Stack<Point>();
                for (int i = re.Count - 1; i >= 0; i--) s2.Push(new Point((int)re[i].X, (int)re[i].Y));
                P.controller = new PathFindController(s2, P, loc) { NPCSchedule = false };
                stuck = -200;
            }
            yield return null;
        }
        P.controller = null;
        P.Halt();
        if (P.TilePoint != target && Game1.currentLocation == loc)
            c.Fail($"Could not reach ({target.X},{target.Y}); stopped at ({P.TilePoint.X},{P.TilePoint.Y}).");
    }

    private static readonly Point[] Dirs = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) }; // up right down left

    /// <summary>Walk to the best tile next to target and face it.</summary>
    public static IEnumerator WalkAdjacent(Point target, CallContext c)
    {
        var loc = Loc;
        var me = P.TilePoint;
        int dist = Math.Abs(me.X - target.X) + Math.Abs(me.Y - target.Y);
        if (dist != 1)
        {
            Point? best = null;
            int bestLen = int.MaxValue;
            foreach (var d in Dirs)
            {
                var n = new Point(target.X + d.X, target.Y + d.Y);
                if (!Pathfinder.IsWalkable(loc, n.X, n.Y)) continue;
                if (n == me) { best = n; bestLen = 0; break; }
                var path = new Pathfinder().FindPath(loc, me.ToVector2(), n.ToVector2());
                if (path != null && path.Count < bestLen) { best = n; bestLen = path.Count; }
            }
            if (best == null)
            {
                c.Fail($"No reachable tile next to ({target.X},{target.Y}).");
                yield break;
            }
            yield return WalkTo(best.Value, c);
            if (!c.Ok) yield break;
        }
        Face(target);
    }

    private static void Face(Point target)
    {
        var me = P.TilePoint;
        int dx = target.X - me.X, dy = target.Y - me.Y;
        int dir = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? 1 : 3) : (dy > 0 ? 2 : 0);
        P.faceDirection(dir);
        P.FarmerSprite.StopAnimation();
    }

    // ------------------------------------------------------------------ items

    internal static int FindItem(string name, Func<Item, bool>? filter = null) => FindIn(P.Items, name, filter);

    /// <summary>Index of the item called <paramref name="name"/> (exact name first, else the first partial match), or -1.</summary>
    private static int FindIn(IList<Item> items, string name, Func<Item, bool>? filter = null)
    {
        name = name.Trim().ToLowerInvariant();
        int partial = -1;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null || (filter != null && !filter(it))) continue;
            var dn = it.DisplayName.ToLowerInvariant();
            var n = it.Name.ToLowerInvariant();
            if (dn == name || n == name) return i;
            if (partial < 0 && (dn.Contains(name) || n.Contains(name))) partial = i;
        }
        return partial;
    }

    private static Dictionary<string, int> Snapshot() =>
        P.Items.Where(i => i != null).GroupBy(i => i.DisplayName).ToDictionary(g => g.Key, g => g.Sum(i => i.Stack));

    private static string Diff(Dictionary<string, int> before)
    {
        var after = Snapshot();
        var parts = new List<string>();
        foreach (var kv in after)
        {
            before.TryGetValue(kv.Key, out var b);
            if (kv.Value != b) parts.Add($"{kv.Key} {(kv.Value - b > 0 ? "+" : "")}{kv.Value - b}");
        }
        foreach (var kv in before)
            if (!after.ContainsKey(kv.Key)) parts.Add($"{kv.Key} -{kv.Value}");
        return parts.Count == 0 ? "" : "Inventory change: " + string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ tool use

    private enum Target { None, Object, Clump, Tree, Grass, Dirt, Other }

    private static Target TargetAt(GameLocation loc, Point t)
    {
        var v = t.ToVector2();
        if (loc.Objects.ContainsKey(v)) return Target.Object;
        if (loc.resourceClumps.Any(rc => rc.occupiesTile(t.X, t.Y))) return Target.Clump;
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
        {
            if (tf is Tree || tf is FruitTree) return Target.Tree;
            if (tf is Grass) return Target.Grass;
            if (tf is HoeDirt) return Target.Dirt;
            return Target.Other;
        }
        return Target.None;
    }

    private static IEnumerator Swing(Point target)
    {
        yield return WaitIdle();
        Face(target);
        P.lastClick = new Vector2(target.X * 64 + 32, target.Y * 64 + 32);
        P.BeginUsingTool();
        yield return Ticks(6);
        yield return WaitIdle();
    }

    private static IEnumerator UseTool(JsonElement a, CallContext c)
    {
        var toolName = S(a, "tool");
        var tiles = TileList(a, "tiles").Take(40).ToList();
        int maxHits = Math.Clamp(I(a, "max_hits", 20), 1, 60);
        if (tiles.Count == 0) { c.Fail("No tiles given."); yield break; }

        int slot = FindItem(toolName, it => it is Tool);
        if (slot < 0) { c.Fail($"You don't have a tool called \"{toolName}\". Check inventory."); yield break; }
        P.CurrentToolIndex = slot;
        var tool = (Tool)P.Items[slot];
        bool repeatTool = tool is Axe || tool is Pickaxe;
        var before = Snapshot();
        var lines = new List<string>();
        var loc = Loc;

        foreach (var t in tiles)
        {
            if (P.Stamina <= 2)
            {
                lines.Add($"Stopped: energy too low ({(int)P.Stamina}). Eat something or go to bed.");
                break;
            }
            if (Game1.activeClickableMenu != null) { lines.Add("Stopped: a menu/dialogue opened."); break; }

            var sub = new CallContext();
            yield return WalkAdjacent(t, sub);
            if (!sub.Ok) { lines.Add($"({t.X},{t.Y}) skipped: {sub.Result}"); continue; }
            P.CurrentToolIndex = slot;

            var (sym, label) = WorldText.Describe(loc, t.X, t.Y);
            var kind = TargetAt(loc, t);

            if (tool is WateringCan can)
            {
                bool water = loc.isWaterTile(t.X, t.Y) || loc.doesTileHaveProperty(t.X, t.Y, "Water", "Back") != null;
                if (!water && can.WaterLeft <= 0) { lines.Add($"({t.X},{t.Y}) skipped: watering can is EMPTY — refill it on a water tile (~)."); continue; }
                yield return Swing(t);
                lines.Add(water ? $"({t.X},{t.Y}) refilled watering can ({can.WaterLeft}/{can.waterCanMax})" : $"({t.X},{t.Y}) watered{(loc.terrainFeatures.TryGetValue(t.ToVector2(), out var tf2) && tf2 is HoeDirt hd && hd.isWatered() ? "" : " (nothing to water here)")} · can {can.WaterLeft}/{can.waterCanMax}");
                continue;
            }

            if (tool is Hoe)
            {
                if (kind == Target.Dirt) { lines.Add($"({t.X},{t.Y}) already tilled"); continue; }
                yield return Swing(t);
                var ok = loc.terrainFeatures.TryGetValue(t.ToVector2(), out var tf3) && tf3 is HoeDirt;
                lines.Add(ok ? $"({t.X},{t.Y}) tilled" : $"({t.X},{t.Y}) could not till ({label ?? sym.ToString()}) — clear it first or pick a ',' tile");
                continue;
            }

            if (!repeatTool)
            {
                yield return Swing(t);
                lines.Add($"({t.X},{t.Y}) used {tool.DisplayName} on {label ?? "tile"}");
                continue;
            }

            if (kind == Target.None || kind == Target.Dirt || kind == Target.Grass)
            {
                lines.Add($"({t.X},{t.Y}) nothing for {tool.DisplayName} here ({label ?? sym.ToString()})");
                continue;
            }

            int hits = 0;
            while (hits < maxHits && TargetAt(loc, t) != Target.None && P.Stamina > 2 && Game1.activeClickableMenu == null)
            {
                yield return Swing(t);
                hits++;
            }
            var after = TargetAt(loc, t);
            lines.Add(after == Target.None
                ? $"({t.X},{t.Y}) {label} cleared in {hits} hits"
                : $"({t.X},{t.Y}) {label} NOT cleared after {hits} hits (still: {WorldText.Describe(loc, t.X, t.Y).label}){(kind == Target.Clump ? " — may need an upgraded tool" : "")}");
        }

        yield return Ticks(45); // let dropped items fly into the inventory
        var diff = Diff(before);
        c.Result = string.Join("\n", lines) + (diff.Length > 0 ? "\n" + diff : "");
    }

    private static IEnumerator Plant(JsonElement a, CallContext c)
    {
        var name = S(a, "item");
        var tiles = TileList(a, "tiles").Take(40).ToList();
        int slot = FindItem(name, it => it is SObject);
        if (slot < 0) { c.Fail($"No \"{name}\" in inventory."); yield break; }
        var loc = Loc;
        var lines = new List<string>();
        int planted = 0;

        foreach (var t in tiles)
        {
            if (P.Items[slot] == null || P.Items[slot].Stack <= 0) { lines.Add("Ran out of seeds."); break; }
            if (!loc.terrainFeatures.TryGetValue(t.ToVector2(), out var tf) || tf is not HoeDirt dirt)
            {
                lines.Add($"({t.X},{t.Y}) not tilled soil — use the Hoe first");
                continue;
            }
            var sub = new CallContext();
            yield return WalkAdjacent(t, sub);
            if (!sub.Ok) { lines.Add($"({t.X},{t.Y}) skipped: {sub.Result}"); continue; }

            P.CurrentToolIndex = slot;
            var item = (SObject)P.Items[slot];
            var hadCrop = dirt.crop != null;
            var hadFert = dirt.fertilizer.Value;
            Utility.tryToPlaceItem(loc, item, t.X * 64 + 32, t.Y * 64 + 32);
            yield return Ticks(8);
            bool ok = (!hadCrop && dirt.crop != null) || dirt.fertilizer.Value != hadFert;
            if (ok) planted++;
            else lines.Add($"({t.X},{t.Y}) could not plant {item.DisplayName} (occupied, wrong season, or not plantable here)");
        }
        c.Result = $"Planted {planted}/{tiles.Count} tiles." + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "");
    }

    private static IEnumerator Harvest(JsonElement a, CallContext c)
    {
        var tiles = TileList(a, "tiles").Take(40).ToList();
        var before = Snapshot();
        var loc = Loc;
        var lines = new List<string>();
        foreach (var t in tiles)
        {
            if (Game1.activeClickableMenu != null) { lines.Add("Stopped: a menu opened (inventory full?)."); break; }
            var sub = new CallContext();
            yield return WalkAdjacent(t, sub);
            if (!sub.Ok) { lines.Add($"({t.X},{t.Y}) skipped: {sub.Result}"); continue; }
            var label = WorldText.Describe(loc, t.X, t.Y).label;
            loc.checkAction(new xTile.Dimensions.Location(t.X, t.Y), Game1.viewport, P);
            yield return Ticks(20);
            yield return WaitIdle(120);
            lines.Add($"({t.X},{t.Y}) {label ?? "empty"} → now: {WorldText.Describe(loc, t.X, t.Y).label ?? "empty"}");
        }
        yield return Ticks(30);
        var diff = Diff(before);
        c.Result = string.Join("\n", lines) + "\n" + (diff.Length > 0 ? diff : "Nothing was collected.");
    }

    /// <summary>True while interact presses the action button; see <see cref="JustRightClick_Prefix"/>.</summary>
    private static bool _pressingAction;

    public static void Apply(IManifest manifest) =>
        new Harmony(manifest.UniqueID + ".input").Patch(AccessTools.Method(typeof(Game1), nameof(Game1.didPlayerJustRightClick)),
            prefix: new HarmonyMethod(typeof(AgentTools), nameof(JustRightClick_Prefix)));

    /// <summary>Player chests, the shipping bin, fish ponds, bushes and furniture only answer a fresh action-button press.</summary>
    private static bool JustRightClick_Prefix(ref bool __result)
    {
        if (!_pressingAction) return true;
        __result = true;
        return false;
    }

    /// <summary>Game1.tryToCheckAt with the action button seen as just pressed.</summary>
    private static bool PressAction(Point tile)
    {
        _pressingAction = true;
        try { return Game1.tryToCheckAt(tile.ToVector2(), P); }
        finally { _pressingAction = false; }
    }

    /// <summary>The action button, as in Game1.pressActionButton: check the tile with the held item, or use the held item in place.</summary>
    private static IEnumerator Interact(JsonElement a, CallContext c)
    {
        var holding = S(a, "holding").Trim();
        int slot = holding.Length > 0 ? FindItem(holding) : -1;
        if (holding.Length > 0 && slot < 0) { c.Fail($"No \"{holding}\" in inventory."); yield break; }
        bool atTile = Has(a, "x") && Has(a, "y");
        if (!atTile && slot < 0) { c.Fail("Give the tile (x, y), or holding to use an item where you stand."); yield break; }

        var before = Snapshot();
        var startLoc = Loc;
        var t = new Point(I(a, "x", -1), I(a, "y", -1));
        if (atTile)
        {
            yield return WalkAdjacent(t, c);
            if (!c.Ok) yield break;
        }
        else
            yield return WaitIdle();

        // Without holding, put away a held object so nothing gets loaded or gifted by accident.
        if (slot >= 0) P.CurrentToolIndex = slot;
        else if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();
        var held = P.CurrentItem?.DisplayName;
        var was = atTile ? WorldText.Describe(Loc, t.X, t.Y).label : null;

        string what;
        if (atTile)
            what = PressAction(t) ? $"Interacted with ({t.X},{t.Y})" : $"Nothing happened at ({t.X},{t.Y})";
        else if (P.ActiveObject is { } obj and not StardewValley.Objects.Furniture && obj.performUseAction(Loc))
        {
            P.reduceActiveItemByOne();
            what = $"Used {held}";
        }
        else
        {
            c.Fail($"{held} can't be used where you stand.");
            yield break;
        }
        yield return Ticks(30);
        yield return WaitIdle(atTile ? 180 : 600);
        if (!atTile)
        {
            // After its animation a totem hides the farmer and warps after a delayed fade.
            for (int i = 0; i < 600 && (!Game1.displayFarmer || Game1.globalFade || P.freezePause > 0); i++) yield return null;
            yield return WaitIdle();
        }

        if (slot >= 0 && atTile) what += $" holding {held}";
        var now = atTile && Loc == startLoc ? WorldText.Describe(Loc, t.X, t.Y).label : was;
        var changed = now != was ? $" Now: {now ?? "empty"}." : "";
        var moved = Loc != startLoc ? $" You are now in {Loc.Name}." : "";
        c.Result = $"{what}.{changed}{moved} {Diff(before)}".Trim();
    }

    private static IEnumerator TalkTo(JsonElement a, CallContext c)
    {
        var name = S(a, "name");
        var npc = Loc.characters.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || n.displayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (npc == null) { c.Fail($"{name} is not on this map ({Loc.Name})."); yield break; }

        var gift = S(a, "gift_item");
        int giftSlot = -1;
        if (gift.Length > 0)
        {
            giftSlot = FindItem(gift, it => it is SObject);
            if (giftSlot < 0) { c.Fail($"No \"{gift}\" in inventory to gift."); yield break; }
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var sub = new CallContext();
            yield return WalkAdjacent(npc.TilePoint, sub);
            if (!sub.Ok) continue;
            var me = P.TilePoint;
            if (Math.Abs(me.X - npc.TilePoint.X) + Math.Abs(me.Y - npc.TilePoint.Y) != 1) continue; // they moved
            Face(npc.TilePoint);
            if (giftSlot >= 0) P.CurrentToolIndex = giftSlot;
            else if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();
            Game1.tryToCheckAt(npc.TilePoint.ToVector2(), P);
            yield return Ticks(20);
            if (Game1.activeClickableMenu != null || Game1.dialogueUp)
            {
                c.Result = $"Talking to {npc.displayName} (friendship {P.getFriendshipHeartLevelForNPC(npc.Name)} hearts).";
                yield break;
            }
        }
        c.Fail($"Couldn't start a conversation with {npc.displayName} (they may be busy, moving, or you already talked today).");
    }

    private static int FirstToolSlot()
    {
        for (int i = 0; i < P.Items.Count; i++) if (P.Items[i] is Tool) return i;
        return 0;
    }

    private static IEnumerator Respond(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu is not DialogueBox box) { c.Fail("No dialogue is open."); yield break; }
        var startText = box.getCurrentString();
        bool question = box.isQuestion;
        int option = I(a, "option", -1);
        if (question && (box.responses == null || option < 0 || option >= box.responses.Length))
        {
            c.Fail($"This is a question; pass option 0-{(box.responses?.Length ?? 1) - 1}.");
            yield break;
        }
        for (int i = 0; i < 40; i++)
        {
            if (Game1.activeClickableMenu != box) break;
            if (!box.transitioning)
            {
                if (question) box.selectedResponse = option;
                box.receiveLeftClick(0, 0);
            }
            yield return Ticks(10);
            if (Game1.activeClickableMenu != box || box.getCurrentString() != startText || (question && box.transitioning)) break;
        }
        yield return Ticks(30);
        c.Result = Game1.activeClickableMenu == null ? "Dialogue closed." : "Dialogue continued.";
    }

    private static IEnumerator CloseMenu(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu == null) { c.Result = "No menu open."; yield break; }
        if (Game1.activeClickableMenu is DialogueBox box)
            box.closeDialogue();
        else
            Game1.activeClickableMenu.exitThisMenu();
        yield return Ticks(20);
        if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
        c.Result = "Menu closed.";
    }

    // ------------------------------------------------------------------ travel

    private static IEnumerator TravelTo(JsonElement a, CallContext c)
    {
        var dest = S(a, "location").Trim();
        var target = Game1.getLocationFromName(dest);
        if (target == null) { c.Fail($"Unknown location \"{dest}\"."); yield break; }
        yield return TravelTo(target.Name, c);
        if (c.Ok) c.Result = $"Arrived in {Loc.Name} at ({P.TilePoint.X},{P.TilePoint.Y}).";
    }

    /// <summary>A way out of a map: a door/building door (press action on it) or an edge warp (walk off the map).</summary>
    private record Exit(string Target, Point Tile, bool IsDoor, Warp? Warp);

    private static List<Exit> Exits(GameLocation loc)
    {
        var list = new List<Exit>();
        foreach (var kv in loc.doors.Pairs)
            list.Add(new Exit(kv.Value, kv.Key, true, null));
        foreach (var b in loc.buildings)
        {
            var inside = b.GetIndoorsName();
            if (!string.IsNullOrEmpty(inside) && b.humanDoor.Value.X >= 0)
                list.Add(new Exit(inside, b.getPointForHumanDoor(), true, null));
        }
        foreach (var w in loc.warps)
            list.Add(new Exit(w.TargetName, new Point(w.X, w.Y), false, w));
        return list;
    }

    /// <summary>Breadth-first search over maps connected by exits.</summary>
    private static List<string>? FindRoute(string from, string to)
    {
        var prev = new Dictionary<string, string> { [from] = "" };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur.Equals(to, StringComparison.OrdinalIgnoreCase))
            {
                var route = new List<string>();
                for (var n = cur; n != ""; n = prev[n]) route.Insert(0, n);
                return route;
            }
            var loc = Game1.getLocationFromName(cur);
            if (loc == null) continue;
            foreach (var e in Exits(loc))
            {
                if (prev.ContainsKey(e.Target) || Game1.getLocationFromName(e.Target) == null) continue;
                prev[e.Target] = cur;
                queue.Enqueue(e.Target);
            }
        }
        return null;
    }

    private static IEnumerator TravelTo(string dest, CallContext c)
    {
        yield return WaitIdle();
        var route = FindRoute(Loc.Name, dest);
        if (route == null) { c.Fail($"No known route from {Loc.Name} to {dest}."); yield break; }

        for (int hop = 1; hop < route.Count; hop++)
        {
            yield return WaitIdle();
            var loc = Loc;
            var next = route[hop];
            var me = P.TilePoint;
            var exit = Exits(loc).Where(e => e.Target == next)
                .OrderBy(e => Math.Abs(e.Tile.X - me.X) + Math.Abs(e.Tile.Y - me.Y)).FirstOrDefault();
            if (exit == null) { c.Fail($"No exit from {loc.Name} to {next}."); yield break; }

            var sub = new CallContext();
            if (exit.IsDoor)
            {
                // Stand below the door and open it; the game checks opening hours/locks.
                var d = exit.Tile;
                var stand = new Point(d.X, d.Y + 1);
                if (Pathfinder.IsWalkable(loc, stand.X, stand.Y)) yield return WalkTo(stand, sub);
                else yield return WalkAdjacent(d, sub);
                if (Loc == loc)
                {
                    if (!sub.Ok) { c.Fail($"Can't reach the door to {next}: {sub.Result}"); yield break; }
                    Face(d);
                    Game1.tryToCheckAt(d.ToVector2(), P);
                }
            }
            else
            {
                // Edge exits sit just off the walkable map: get as close as possible, then step through.
                var near = Pathfinder.IsWalkable(loc, exit.Tile.X, exit.Tile.Y) ? exit.Tile : NearestWalkable(loc, exit.Tile, 3);
                if (near == null) { c.Fail($"Can't find a walkable tile near the exit to {next}."); yield break; }
                yield return WalkTo(near.Value, sub);
                if (Loc == loc && !Game1.isWarping)
                {
                    // The last step onto an edge exit is often blocked by the map edge itself, so being right next to it is enough.
                    var here = P.TilePoint;
                    bool atExit = Math.Abs(here.X - exit.Tile.X) + Math.Abs(here.Y - exit.Tile.Y) <= 1;
                    if (!sub.Ok && !atExit) { c.Fail($"Can't reach the exit to {next}: {sub.Result}"); yield break; }
                    var w = exit.Warp!;
                    Game1.warpFarmer(w.TargetName, w.TargetX, w.TargetY, false);
                }
            }

            for (int i = 0; i < 300 && Loc == loc; i++) yield return null;
            yield return WaitIdle();
            if (Loc == loc)
            {
                var msg = Game1.activeClickableMenu != null ? WorldText.MenuState().Trim() : "the door may be locked (check opening hours)";
                c.Fail($"Couldn't go from {loc.Name} to {next}: {msg}");
                yield break;
            }
            if (Loc.Name != next) { c.Fail($"Ended up in {Loc.Name} instead of {next}."); yield break; }
            yield return Ticks(20);
        }
    }

    private static Point? NearestWalkable(GameLocation loc, Point p, int radius)
    {
        Point? best = null;
        int bestD = int.MaxValue;
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        {
            var q = new Point(p.X + dx, p.Y + dy);
            if (!Pathfinder.IsWalkable(loc, q.X, q.Y)) continue;
            int d = Math.Abs(dx) + Math.Abs(dy);
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    // ------------------------------------------------------------------ shop / ship / eat

    private static IEnumerator ShopList(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu is not ShopMenu shop) { c.Fail("No shop is open. interact with a shop counter (A tile) during opening hours first."); yield break; }
        var cur = CurrencyUnit(shop.currency);
        var sb = new StringBuilder($"Shop {shop.ShopId} — you have {ShopMenu.getPlayerCurrencyAmount(P, shop.currency)}{cur}\n");
        foreach (var s in shop.forSale)
        {
            if (!shop.itemPriceAndStock.TryGetValue(s, out var st)) continue;
            var stock = st.Stock == int.MaxValue ? "" : $" (stock {st.Stock})";
            var trade = st.TradeItem != null ? $" + {st.TradeItemCount ?? 5}x {ItemRegistry.GetData(st.TradeItem)?.DisplayName}" : "";
            var recipe = s.IsRecipe ? " [recipe: buying teaches it]" : "";
            sb.AppendLine($"  {s.DisplayName}: {st.Price}{cur}{trade}{stock}{recipe}");
        }
        c.Result = sb.ToString().TrimEnd();
    }

    private static string CurrencyUnit(int currency) => currency switch
    {
        1 => " star tokens",
        2 => " Qi coins",
        4 => " Qi gems",
        _ => "g"
    };

    /// <summary>Buys through the game's own ShopMenu.tryToPurchaseItem, so recipes are learned, trade items
    /// are consumed, purchase actions run and limited stock is synced, exactly like a click in the menu.</summary>
    private static IEnumerator Buy(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu is not ShopMenu shop) { c.Fail("No shop is open."); yield break; }
        if (shop.readOnly) { c.Fail("This shop menu is read-only; nothing can be bought here."); yield break; }
        var name = S(a, "item").Trim().ToLowerInvariant();
        int qty = Math.Clamp(I(a, "quantity", 1), 1, 999);
        var sal = shop.forSale.FirstOrDefault(s => s.DisplayName.ToLowerInvariant() == name)
                  ?? shop.forSale.FirstOrDefault(s => s.DisplayName.ToLowerInvariant().Contains(name));
        if (sal == null || !shop.itemPriceAndStock.TryGetValue(sal, out var st)) { c.Fail($"\"{name}\" is not sold here. Use shop_list."); yield break; }
        if (shop.canPurchaseCheck != null && !shop.canPurchaseCheck(shop.forSale.IndexOf(sal))) { c.Fail($"{sal.DisplayName} can't be bought right now."); yield break; }

        // Friendly pre-checks; the game's purchase would only play a "cancel" sound.
        if (sal.IsRecipe) qty = 1;
        qty = Math.Min(qty, st.Stock);
        if (qty <= 0) { c.Fail($"{sal.DisplayName} is sold out."); yield break; }
        var cur = CurrencyUnit(shop.currency);
        int have = ShopMenu.getPlayerCurrencyAmount(P, shop.currency);
        if (have < st.Price * qty) { c.Fail($"Not enough money: {qty}x {sal.DisplayName} costs {st.Price * qty}{cur}, you have {have}{cur}."); yield break; }
        if (st.TradeItem != null)
        {
            int need = (st.TradeItemCount ?? 5) * qty;
            if (!shop.HasTradeItem(st.TradeItem, need)) { c.Fail($"{sal.DisplayName} also needs {need}x {ItemRegistry.GetData(st.TradeItem)?.DisplayName ?? st.TradeItem}."); yield break; }
        }
        // Move the shop's held item into the inventory; whatever doesn't fit is dropped at the player's feet.
        bool dropped = false;
        void PutAwayHeld()
        {
            if (shop.heldItem is not Item held) return;
            shop.heldItem = null;
            if (P.addItemToInventory(held) is Item rest)
            {
                Game1.createItemDebris(rest, P.getStandingPosition(), P.FacingDirection);
                dropped = true;
            }
        }

        // A leftover held item (e.g. from a manual click) would be stacked onto; put it away before the space check.
        PutAwayHeld();
        var probe = sal.GetSalableInstance();
        probe.Stack = qty * sal.Stack;
        if (!sal.IsRecipe && !sal.IsInfiniteStock() && !probe.CanBuyItem(P)) { c.Fail($"Can't buy {sal.DisplayName}: inventory is full or you can't hold it."); yield break; }

        var purchase = Rt.Helper.Reflection.GetMethod(shop, "tryToPurchaseItem");
        int maxChunk = Math.Max(1, sal.GetSalableInstance().maximumStackSize());
        int bought = 0, spent = 0;
        while (bought < qty)
        {
            int n = Math.Min(qty - bought, maxChunk);
            int before = ShopMenu.getPlayerCurrencyAmount(P, shop.currency), stockBefore = st.Stock;
            bool soldOut = purchase.Invoke<bool>(sal, null, n, 0, 0);
            int paid = before - ShopMenu.getPlayerCurrencyAmount(P, shop.currency);
            if (soldOut)
            {
                // Same bookkeeping the menu does after a click that empties the stock.
                shop.itemPriceAndStock.Remove(sal);
                shop.forSale.Remove(sal);
            }
            // Free items charge nothing: count them only if something visibly happened.
            bool got = paid > 0 || shop.heldItem != null || st.Stock < stockBefore ||
                       (sal.IsRecipe && sal is Item r && (P.cookingRecipes.ContainsKey(r.BaseName) || P.craftingRecipes.ContainsKey(r.BaseName)));
            PutAwayHeld();
            if (!got) break;
            bought += n;
            spent += paid;
            if (soldOut || Game1.activeClickableMenu != shop) break;
        }
        yield return Ticks(10);

        if (bought == 0) { c.Fail($"The shop refused to sell {sal.DisplayName}."); yield break; }
        var left = $"Money left {ShopMenu.getPlayerCurrencyAmount(P, shop.currency)}{cur}.";
        if (sal.IsRecipe)
        {
            var key = (sal as Item)?.BaseName ?? sal.Name;
            bool known = P.cookingRecipes.ContainsKey(key) || P.craftingRecipes.ContainsKey(key);
            c.Result = known ? $"Learned recipe: {key} for {spent}{cur}. {left}" : $"Paid {spent}{cur} for {sal.DisplayName}, but the recipe is not in your recipe list. {left}";
            yield break;
        }
        var note = dropped ? " Inventory was full: the rest was dropped at your feet." : "";
        c.Result = $"Bought {bought * sal.Stack}x {sal.DisplayName} for {spent}{cur}.{note} {left}";
    }

    private static IEnumerator Ship(JsonElement a, CallContext c)
    {
        if (Loc is not Farm farm) { c.Fail("The shipping bin is on the Farm. travel_to Farm first."); yield break; }
        var bin = farm.buildings.FirstOrDefault(b => b.buildingType.Value == "Shipping Bin");
        if (bin == null) { c.Fail("No shipping bin found."); yield break; }
        var name = S(a, "item");
        int slot = FindItem(name);
        if (slot < 0) { c.Fail($"No \"{name}\" in inventory."); yield break; }
        var item = P.Items[slot];
        if (item is Tool || !item.canBeShipped()) { c.Fail($"{item.DisplayName} can't be shipped."); yield break; }

        var binTile = new Point(bin.tileX.Value, bin.tileY.Value);
        var sub = new CallContext();
        yield return WalkAdjacent(binTile, sub);
        if (!sub.Ok) yield return WalkAdjacent(new Point(binTile.X + 1, binTile.Y), sub = new CallContext());
        if (!sub.Ok) { c.Fail($"Can't reach the shipping bin: {sub.Result}"); yield break; }

        int qty = Has(a, "quantity") ? Math.Clamp(I(a, "quantity", item.Stack), 1, item.Stack) : item.Stack;
        var toShip = item.getOne();
        toShip.Stack = qty;
        item.Stack -= qty;
        if (item.Stack <= 0) P.Items[slot] = null;
        farm.getShippingBin(P).Add(toShip);
        farm.lastItemShipped = toShip;
        farm.showShipment(toShip, false);
        yield return Ticks(20);
        int value = toShip.sellToStorePrice() * qty;
        c.Result = $"Shipped {qty}x {toShip.DisplayName} (worth about {value}g, paid overnight).";
    }

    private static IEnumerator Eat(JsonElement a, CallContext c)
    {
        var name = S(a, "item");
        int slot = FindItem(name, it => it is SObject o && o.Edibility > -300);
        if (slot < 0) { c.Fail($"No edible \"{name}\" in inventory."); yield break; }
        yield return WaitIdle();
        var before = (int)P.Stamina;
        var itemName = P.Items[slot].DisplayName;
        P.CurrentToolIndex = slot;
        P.eatHeldObject();
        yield return Ticks(30);
        for (int i = 0; i < 400 && P.isEating; i++) yield return null;
        yield return WaitIdle();
        c.Result = $"Ate {itemName}: energy {before} → {(int)P.Stamina}.";
    }

    // ------------------------------------------------------------------ sleep

    /// <summary>Press OK on the end-of-day shipping screen once. okClicked restarts the 800 ms outro fade,
    /// so clicking again during the outro (two clickers a second apart) keeps the menu open forever.</summary>
    public static void ClickShippingOk(ShippingMenu sm)
    {
        if (!Rt.Helper.Reflection.GetField<bool>(sm, "outro").GetValue())
            Rt.Helper.Reflection.GetMethod(sm, "okClicked").Invoke();
    }

    private static IEnumerator ChooseProfession(JsonElement a, CallContext c)
    {
        var pending = Rt.PendingProfessions;
        if (pending.Count == 0) { c.Fail("No profession choice is pending. They come with skill levels 5 and 10."); yield break; }
        var skillName = S(a, "skill");
        Point choice;
        if (skillName.Length > 0)
        {
            int skill = Professions.SkillIndex(skillName);
            var match = pending.Where(p => p.X == skill).OrderBy(p => p.Y).ToList();
            if (match.Count == 0) { c.Fail($"No pending choice for \"{skillName}\". Pending: {string.Join(", ", pending.Select(p => $"{Professions.SkillName(p.X)} {p.Y}"))}."); yield break; }
            choice = match[0];
        }
        else choice = pending.OrderBy(p => p.Y).First();
        var text = Professions.Choose(Rt, P, choice, I(a, "option", -1), out var err);
        if (text == null) { c.Fail(err); yield break; }
        Rt.Log.Add("system", text: $"Profession chosen: {text}");
        c.Result = text;
    }

    private static IEnumerator GoToBed(JsonElement a, CallContext c)
    {
        var diary = S(a, "diary").Trim();
        if (diary.Length < 20) { c.Fail($"Write a real diary entry in {Lang} first (diary parameter)."); yield break; }

        int dayBefore = Game1.Date.TotalDays;
        int moneyBefore = P.Money;
        Rt.Log.WriteDiary(AgentLog.FarmDay, diary);
        Professions.ResolveDefaults(Rt, P, "not chosen before bed");

        if (Loc is not FarmHouse)
        {
            var sub = new CallContext();
            yield return TravelTo("FarmHouse", sub);
            if (!sub.Ok) { c.Fail($"Couldn't get home: {sub.Result}"); yield break; }
        }
        var house = (FarmHouse)Loc;
        var spot = house.GetPlayerBedSpot();
        var walk = new CallContext();
        yield return WalkTo(spot, walk);
        if (Game1.activeClickableMenu is DialogueBox db)
            db.closeDialogue();
        else if (Game1.activeClickableMenu != null)
            Game1.exitActiveMenu();
        Game1.dialogueUp = false;
        P.isInBed.Value = true;
        house.answerDialogueAction("Sleep_Yes", Array.Empty<string>());
        Rt.Log.Add("system", text: "Sleeping...");

        // Wait for the new day; click through end-of-day screens.
        for (int t = 0; t < 60 * 60 * 5; t++)
        {
            switch (Game1.activeClickableMenu)
            {
                case ShippingMenu sm when t % 60 == 0:
                    ClickShippingOk(sm);
                    break;
                case LevelUpMenu lm when t % 60 == 0 && lm.isActive:
                    if (lm.isProfessionChooser)
                        Professions.Defer(lm, Rt);
                    else
                        lm.okButtonClicked();
                    break;
            }
            if (Game1.Date.TotalDays > dayBefore && Context.IsWorldReady && Game1.activeClickableMenu == null && !Game1.fadeToBlack && !Game1.newDay && P.CanMove)
                break;
            yield return null;
        }
        yield return Ticks(60);

        if (Game1.Date.TotalDays <= dayBefore) { c.Fail("Tried to sleep but the day did not change."); yield break; }
        Rt.Log.Add("system", text: $"New day: farm day {AgentLog.FarmDay}");
        c.Result = $"Good morning! It's now farm day {AgentLog.FarmDay}. Money {moneyBefore}g → {P.Money}g (shipping income {P.Money - moneyBefore}g). Your day is over — stop here.";
    }
}
