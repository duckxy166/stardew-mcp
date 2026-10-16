using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>Compact, LLM-friendly text views of the game world.</summary>
public static class WorldText
{
    public const string Legend =
        "@ you | . walkable | , tillable ground | # blocked | ~ water | D door/exit/ladder | A action tile (shop counter, etc) | " +
        "N villager | M monster | H building | b shipping bin | Z your bed | f furniture | O object/machine/chest | W weeds | w twig | S stone | " +
        "L large stump/log/boulder | T tree | t young tree/sapling | s tree stump | Y fruit tree | u bush | g grass | F forage item | " +
        "= tilled soil (empty) | c crop needs water | C crop watered | ! crop ready to harvest | x dead crop";

    public static string FormatTime(int t)
    {
        int h = t / 100, m = t % 100;
        return $"{h:00}:{m:00}";
    }

    public static string DirName(int d) => d switch { 0 => "up", 1 => "right", 2 => "down", 3 => "left", _ => "?" };

    public static string Status()
    {
        var p = Game1.player;
        var loc = Game1.currentLocation;
        var sb = new StringBuilder();
        sb.AppendLine($"Farm day {AgentLog.FarmDay} — {Game1.currentSeason} {Game1.dayOfMonth}, year {Game1.year} ({Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth)}) {FormatTime(Game1.timeOfDay)} · weather: {Weather()}");
        sb.AppendLine($"Location: {loc?.Name}{(loc is StardewValley.Locations.MineShaft ms ? $" (floor {ms.mineLevel})" : "")} at ({p.TilePoint.X},{p.TilePoint.Y}) facing {DirName(p.FacingDirection)}");
        sb.AppendLine($"Energy {(int)p.Stamina}/{p.MaxStamina} · Health {p.health}/{p.maxHealth} · Money {p.Money}g");
        var held = p.CurrentItem;
        var owned = Professions.Owned(p);
        if (owned.Length > 0) sb.AppendLine($"Professions: {owned}");
        sb.AppendLine($"Holding: {(held == null ? "nothing" : $"{held.DisplayName} x{held.Stack}")} (slot {p.CurrentToolIndex})");
        if (p.questLog.Count > 0)
        {
            var quests = p.questLog.Where(q => q != null && !q.completed.Value).Select(q => q.GetName()).Take(4).ToList();
            if (quests.Count > 0) sb.AppendLine($"Active quests: {string.Join(" | ", quests)}");
        }
        sb.Append(MenuState());
        return sb.ToString().TrimEnd();
    }

    public static string Weather()
    {
        if (Game1.isLightning) return "storm";
        if (Game1.isRaining) return "rain";
        if (Game1.isSnowing) return "snow";
        if (Game1.isDebrisWeather) return "windy";
        return "sunny";
    }

    public static string MenuState()
    {
        var sb = new StringBuilder();
        if (Game1.eventUp)
            sb.AppendLine("A cutscene/event is playing.");
        switch (Game1.activeClickableMenu)
        {
            case null:
                break;
            case DialogueBox box:
                var text = box.getCurrentString() ?? "";
                var speaker = box.characterDialogue?.speaker?.displayName;
                sb.AppendLine($"Dialogue open{(speaker != null ? $" ({speaker})" : "")}: \"{text.Replace("\n", " ").Replace("^", " ")}\"");
                if (box.isQuestion && box.responses != null)
                {
                    for (int i = 0; i < box.responses.Length; i++)
                        sb.AppendLine($"  option {i}: {box.responses[i].responseText}");
                    sb.AppendLine("Use respond(option) to answer.");
                }
                else
                    sb.AppendLine("Use respond() to continue the dialogue.");
                break;
            case ShopMenu shop:
                sb.AppendLine($"Shop menu open ({shop.ShopId}). Use shop_list / buy, then close_menu.");
                break;
            case LetterViewerMenu letter:
                var letterText = letter.mailMessage != null ? string.Join(" ", letter.mailMessage).Replace("\n", " ").Replace("^", " ") : "";
                sb.AppendLine($"Letter open: \"{letterText}\". Use close_menu (attached items and quests will be claimed automatically).");
                break;
            default:
                sb.AppendLine($"Menu open: {AgentTools.MenuName(Game1.activeClickableMenu)}. Use menu() to read and use it, or close_menu.");
                break;
        }
        return sb.ToString();
    }

    public static string Inventory()
    {
        var p = Game1.player;
        var sb = new StringBuilder();
        sb.AppendLine($"Money {p.Money}g · inventory ({p.Items.Count(i => i != null)}/{p.MaxItems} slots used):");
        for (int i = 0; i < p.Items.Count; i++)
        {
            var it = p.Items[i];
            if (it == null) continue;
            var extra = it is SObject o && o.Edibility > -300 ? $", edible +{o.staminaRecoveredOnConsumption()} energy" : "";
            var quality = it is not Tool && it.Quality > 0 ? $", {AgentTools.QualityName(it.Quality)}" : "";
            sb.AppendLine($"  slot {i}: {it.DisplayName} x{it.Stack} [{(it is Tool ? "tool" : it.getCategoryName())}{quality}{extra}]");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>One-character symbol and optional label for a tile.</summary>
    public static (char sym, string? label) Describe(GameLocation loc, int x, int y)
    {
        var p = Game1.player;
        var v = new Vector2(x, y);
        var pt = new Point(x, y);

        if (p.TilePoint == pt) return ('@', null);

        foreach (var npc in loc.characters)
        {
            if (npc.TilePoint != pt) continue;
            if (npc is StardewValley.Monsters.Monster mon) return ('M', $"{mon.Name} (HP {mon.Health})");
            if (npc.IsVillager) return ('N', npc.displayName);
        }

        if (loc is StardewValley.Locations.MineShaft mine)
        {
            int idx = mine.getTileIndexAt(new xTile.Dimensions.Location(x, y), "Buildings", "mine");
            if (idx == 173) return ('D', "Ladder down (interact to go to next floor)");
            if (idx == 174) return ('D', "Shaft down (interact to jump down floors)");
            if (idx == 115) return ('D', "Ladder up (interact to exit mine)");
            if (idx == 112) return ('A', $"Mine Elevator (unlocked up to floor {StardewValley.Locations.MineShaft.lowestLevelReached})");
        }

        foreach (var w in loc.warps)
            if (w.X == x && w.Y == y) return ('D', $"exit to {w.TargetName}");
        foreach (var d in loc.doors.Keys)
            if (d.X == x && d.Y == y) return ('D', $"door to {loc.doors[d]}");

        foreach (var b in loc.buildings)
        {
            var inside = b.GetIndoorsName();
            if (!string.IsNullOrEmpty(inside) && b.humanDoor.Value.X >= 0 && b.getPointForHumanDoor() == pt)
                return ('D', $"door to {inside}");
            if (!b.isTilePassable(v))
            {
                if (b.buildingType.Value == "Shipping Bin") return ('b', "Shipping Bin");
                return ('H', b.buildingType.Value);
            }
        }

        foreach (var f in loc.furniture)
        {
            if (f.GetBoundingBox().Contains(x * 64 + 32, y * 64 + 32))
            {
                if (f is BedFurniture) return ('Z', "Bed");
                if (!f.isPassable()) return ('f', f.DisplayName);
            }
        }

        if (loc.Objects.TryGetValue(v, out var obj))
        {
            if (obj.IsWeeds()) return ('W', "Weeds");
            if (obj.IsTwig()) return ('w', "Twig");
            if (obj.IsBreakableStone()) return ('S', "Stone");
            if (obj.isForage()) return ('F', obj.DisplayName);
            if (obj is Chest chest)
            {
                if (chest.giftbox.Value) return ('O', "Gift box (open it with interact)");
                if (chest.playerChest.Value) return ('O', $"Chest ({chest.Items.Count(i => i != null)} items — interact, then menu take/put)");
            }
            var held = obj.heldObject.Value != null ? $" (holding {obj.heldObject.Value.DisplayName}{(obj.readyForHarvest.Value ? ", READY" : "")})" : "";
            return ('O', obj.DisplayName + held);
        }

        foreach (var clump in loc.resourceClumps)
            if (clump.occupiesTile(x, y)) return ('L', ClumpName(clump.parentSheetIndex.Value));

        if (loc.terrainFeatures.TryGetValue(v, out var tf))
        {
            switch (tf)
            {
                case Tree tree:
                    if (tree.stump.Value) return ('s', "Tree stump");
                    return tree.growthStage.Value >= 5 ? ('T', "Tree") : ('t', "Young tree");
                case FruitTree ft:
                    return ('Y', "Fruit tree");
                case HoeDirt dirt:
                    if (dirt.crop == null) return ('=', null);
                    var crop = dirt.crop;
                    var cname = CropName(crop);
                    if (crop.dead.Value) return ('x', $"dead {cname}");
                    if (dirt.readyForHarvest()) return ('!', $"{cname} READY");
                    return dirt.isWatered() ? ('C', cname) : ('c', $"{cname} (dry)");
                case Grass:
                    return ('g', null);
            }
        }

        foreach (var ltf in loc.largeTerrainFeatures)
            if (ltf.getBoundingBox().Contains(x * 64 + 32, y * 64 + 32)) return ('u', ltf is Bush ? "Bush" : ltf.GetType().Name);

        foreach (var deb in loc.debris)
        {
            if (deb.item != null && deb.Chunks.Count > 0)
            {
                var c = deb.Chunks[0].position.Value;
                if ((int)(c.X / 64) == x && (int)(c.Y / 64) == y) return ('F', $"dropped {deb.item.DisplayName}");
            }
        }

        var action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
        if (action != null) return ('A', action);

        if (loc.isWaterTile(x, y)) return ('~', null);

        if (!Pathfinder.IsWalkable(loc, x, y)) return ('#', null);

        if (loc.doesTileHaveProperty(x, y, "Diggable", "Back") != null) return (',', null);
        return ('.', null);
    }

    public static string ClumpName(int index) => index switch
    {
        600 => "Large Stump",
        602 => "Large Log",
        672 => "Boulder",
        752 or 754 or 756 or 758 => "Mine Rock",
        _ => "Large Obstacle"
    };

    public static string CropName(Crop crop)
    {
        var id = crop.indexOfHarvest.Value;
        if (string.IsNullOrEmpty(id)) return "crop";
        var data = ItemRegistry.GetData(id);
        return data?.DisplayName ?? "crop";
    }

    public static string Look(int radius)
    {
        var loc = Game1.currentLocation;
        var p = Game1.player;
        int px = p.TilePoint.X, py = p.TilePoint.Y;
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        int x0 = Math.Max(0, px - radius), x1 = Math.Min(w - 1, px + radius);
        int y0 = Math.Max(0, py - radius), y1 = Math.Min(h - 1, py + radius);

        var labels = new Dictionary<string, List<(int x, int y)>>();
        var sb = new StringBuilder();
        sb.AppendLine($"Map {loc.Name} ({w}x{h}). You @ ({px},{py}) facing {DirName(p.FacingDirection)}. Showing x {x0}-{x1}, y {y0}-{y1}.");

        var tens = new StringBuilder("        ");
        var ones = new StringBuilder("        ");
        for (int x = x0; x <= x1; x++)
        {
            tens.Append(x % 10 == 0 || x == x0 ? (char)('0' + (x / 10) % 10) : ' ');
            ones.Append((char)('0' + x % 10));
        }
        sb.AppendLine(tens.ToString());
        sb.AppendLine(ones.ToString());

        for (int y = y0; y <= y1; y++)
        {
            sb.Append($"y{y,4} | ");
            for (int x = x0; x <= x1; x++)
            {
                var (sym, label) = Describe(loc, x, y);
                sb.Append(sym);
                if (label != null && sym != '@')
                {
                    var key = $"{sym} {label}";
                    if (!labels.TryGetValue(key, out var list)) labels[key] = list = new();
                    list.Add((x, y));
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("Things in view (nearest first):");
        foreach (var kv in labels.OrderBy(kv => kv.Value.Min(t => Math.Abs(t.x - px) + Math.Abs(t.y - py))))
        {
            var pts = kv.Value.OrderBy(t => Math.Abs(t.x - px) + Math.Abs(t.y - py)).ToList();
            var shown = string.Join(" ", pts.Take(10).Select(t => $"({t.x},{t.y})"));
            sb.AppendLine($"  {kv.Key} x{pts.Count}: {shown}{(pts.Count > 10 ? " ..." : "")}");
        }

        var exits = loc.warps.Select(wp => $"{wp.TargetName}").Concat(loc.doors.Values.Select(d => d)).Distinct().ToList();
        if (exits.Count > 0)
            sb.AppendLine($"Exits from this map lead to: {string.Join(", ", exits)} (use travel_to).");
        sb.AppendLine("Legend: " + Legend);
        return sb.ToString().TrimEnd();
    }
}
