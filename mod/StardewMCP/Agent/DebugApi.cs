using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Test hooks for runner/smoke.py, served at POST /api/debug/{op} only when
/// ModConfig.EnableDebugApi is true. They are deliberately not MCP tools, so the player AI
/// never sees them. Each op runs on the game thread through the normal AgentRuntime queue.
/// </summary>
public static class DebugApi
{
    private static string? _pendingLoad;
    private static bool _exiting;
    private static int _titleTicks;

    private static string S(JsonElement a, string name, string def = "") =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;

    private static int I(JsonElement a, string name, int def) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    private static bool Has(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number;

    public static Dictionary<string, ToolDef> All()
    {
        ToolDef Op(string name, ToolRunner run, bool needsWorld = true) =>
            new() { Name = name, Description = "debug", Schema = new { }, Instant = true, NeedsWorld = needsWorld, AppendStatus = false, Mail = false, Run = run };

        return new[]
        {
            Op("warp", Warp),
            Op("set_time", SetTime),
            Op("add_item", AddItem),
            Op("set_money", SetMoney),
            Op("add_mail", AddMail),
            Op("set_skill", SetSkill),
            Op("set_health", SetHealth),
            Op("set_stamina", SetStamina),
            Op("open_shop", OpenShop),
            Op("place_object", PlaceObject),
            Op("set_mine_level", SetMineLevel),
            Op("separate_wallets", SeparateWallets),
            Op("load_save", LoadSave, needsWorld: false),
            Op("build_cabins", BuildCabins),
            Op("capture", Capture, needsWorld: false),
            Op("chat_log", ChatLog),
            Op("disconnect", Disconnect),
        }.ToDictionary(t => t.Name);
    }

    // ------------------------------------------------------------------ ops

    private static IEnumerator Warp(JsonElement a, CallContext c)
    {
        var name = S(a, "location");
        if (Game1.getLocationFromName(name) == null) { c.Fail($"Unknown location \"{name}\""); yield break; }
        int x = I(a, "x", 0), y = I(a, "y", 0);

        if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
        Game1.warpFarmer(name, x, y, false);
        for (int i = 0; i < 600 && (Game1.isWarping || Game1.fadeToBlack || Game1.currentLocation?.Name != name); i++)
            yield return null;
        if (Game1.currentLocation?.Name != name) { c.Fail($"Warp to {name} did not finish"); yield break; }
        yield return null;
        c.Result = $"Warped to {name} ({Game1.player.TilePoint.X},{Game1.player.TilePoint.Y})";
    }

    private static IEnumerator SetTime(JsonElement a, CallContext c)
    {
        int t = I(a, "time", -1);
        if (t < 600 || t > 2600 || t % 10 != 0 || t % 100 >= 60) { c.Fail("time must be HHMM in 600..2600 (e.g. 900, 1830)"); yield break; }
        Game1.timeOfDay = t;
        c.Result = $"Time set to {WorldText.FormatTime(t)}";
    }

    private static IEnumerator AddItem(JsonElement a, CallContext c)
    {
        var id = S(a, "id").Trim();
        int qty = Math.Max(1, I(a, "qty", 1));
        if (id.Length == 0) { c.Fail("id is required (e.g. \"(O)472\" or \"472\")"); yield break; }
        if (!id.StartsWith("(")) id = "(O)" + id;

        Item? item;
        try { item = ItemRegistry.Create(id, qty); }
        catch (Exception ex) { c.Fail($"Cannot create {id}: {ex.Message}"); yield break; }
        if (item == null) { c.Fail($"Cannot create {id}"); yield break; }

        if (!Game1.player.addItemToInventoryBool(item)) { c.Fail("Inventory is full"); yield break; }
        c.Result = $"Added {qty} x {item.DisplayName}";
        yield break;
    }

    private static IEnumerator SetMoney(JsonElement a, CallContext c)
    {
        if (!Has(a, "n")) { c.Fail("n is required"); yield break; }
        Game1.player.Money = Math.Max(0, I(a, "n", 0));
        c.Result = $"Money set to {Game1.player.Money}g";
        yield break;
    }

    /// <summary>Put a letter in the mailbox (any id works for the briefing and footer checks; opening it needs a real Data/Mail id).</summary>
    private static IEnumerator AddMail(JsonElement a, CallContext c)
    {
        var id = S(a, "id").Trim();
        if (id.Length == 0) { c.Fail("id is required (a mail id, e.g. \"smoke_letter\")"); yield break; }
        Game1.player.mailbox.Add(id);
        c.Result = $"Mailbox now holds {Game1.player.mailbox.Count} letter(s)";
        yield break;
    }

    /// <summary>Open a shop by id even when its owner is away (set_time does not move NPCs).</summary>
    private static IEnumerator OpenShop(JsonElement a, CallContext c)
    {
        var id = S(a, "id");
        if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
        if (!Utility.TryOpenShopMenu(id, Game1.currentLocation, forceOpen: true) || Game1.activeClickableMenu is not ShopMenu)
        {
            c.Fail($"Could not open shop \"{id}\"");
            yield break;
        }
        c.Result = $"Opened shop {id}";
    }

    private static readonly int[] XpForLevel = { 0, 100, 380, 770, 1300, 2150, 3300, 4800, 6900, 10000, 15000 };

    private static IEnumerator SetSkill(JsonElement a, CallContext c)
    {
        var skill = S(a, "skill").ToLowerInvariant();
        int level = Math.Clamp(I(a, "level", 0), 0, 10);
        var p = Game1.player;
        int index, old = skill switch
        {
            "farming" => p.farmingLevel.Value, "fishing" => p.fishingLevel.Value, "foraging" => p.foragingLevel.Value,
            "mining" => p.miningLevel.Value, "combat" => p.combatLevel.Value, _ => 0
        };
        switch (skill)
        {
            case "farming": index = 0; p.farmingLevel.Value = level; break;
            case "fishing": index = 1; p.fishingLevel.Value = level; break;
            case "foraging": index = 2; p.foragingLevel.Value = level; break;
            case "mining": index = 3; p.miningLevel.Value = level; break;
            case "combat": index = 4; p.combatLevel.Value = level; break;
            default: c.Fail("skill must be farming, fishing, foraging, mining or combat"); yield break;
        }
        p.experiencePoints[index] = XpForLevel[level];
        // Queue tonight's level-up screens (profession choices at 5 and 10) for the levels gained, like real play.
        for (int l = old + 1; l <= level; l++)
            if (!p.newLevels.Contains(new Microsoft.Xna.Framework.Point(index, l)))
                p.newLevels.Add(new Microsoft.Xna.Framework.Point(index, l));
        c.Result = $"{skill} level set to {level}";
        yield break;
    }

    private static IEnumerator SetHealth(JsonElement a, CallContext c)
    {
        if (!Has(a, "n")) { c.Fail("n is required"); yield break; }
        Game1.player.health = Math.Clamp(I(a, "n", 0), 1, Game1.player.maxHealth);
        c.Result = $"Health set to {Game1.player.health}/{Game1.player.maxHealth}";
        yield break;
    }

    private static IEnumerator SetStamina(JsonElement a, CallContext c)
    {
        if (!Has(a, "n")) { c.Fail("n is required"); yield break; }
        Game1.player.Stamina = Math.Clamp(I(a, "n", 0), 0, Game1.player.MaxStamina);
        c.Result = $"Energy set to {(int)Game1.player.Stamina}/{Game1.player.MaxStamina}";
        yield break;
    }

    /// <summary>Put a machine, chest or other placeable item on a tile through the game's own placementAction.</summary>
    private static IEnumerator PlaceObject(JsonElement a, CallContext c)
    {
        var id = S(a, "id").Trim();
        int x = I(a, "x", -1), y = I(a, "y", -1);
        var loc = Game1.currentLocation;
        if (ItemRegistry.Create(id) is not StardewValley.Object obj) { c.Fail($"{id} is not a placeable object (e.g. \"(BC)13\")"); yield break; }
        if (loc.Objects.ContainsKey(new Microsoft.Xna.Framework.Vector2(x, y)) || !obj.placementAction(loc, x * 64, y * 64, Game1.player))
        { c.Fail($"Could not place {obj.DisplayName} at ({x},{y}) on {loc.Name}"); yield break; }
        c.Result = $"Placed {obj.DisplayName} at ({x},{y})";
    }

    /// <summary>Deepest mine floor reached, which decides the elevator's floors.</summary>
    private static IEnumerator SetMineLevel(JsonElement a, CallContext c)
    {
        StardewValley.Locations.MineShaft.lowestLevelReached = Math.Clamp(I(a, "n", 0), 0, 120);
        c.Result = $"Lowest mine level {StardewValley.Locations.MineShaft.lowestLevelReached}";
        yield break;
    }

    /// <summary>Switch to separate wallets now instead of overnight, the same call the ledger book's choice makes at night.</summary>
    private static IEnumerator SeparateWallets(JsonElement a, CallContext c)
    {
        if (!Context.IsMainPlayer) { c.Fail("Run separate_wallets on p1 (the host)"); yield break; }
        StardewValley.Locations.ManorHouse.SeparateWallets();
        c.Result = Game1.player.team.useSeparateWallets.Value ? $"Separate wallets on; you have {Game1.player.Money}g" : "Wallets are still shared";
    }

    private static IEnumerator LoadSave(JsonElement a, CallContext c)
    {
        var name = S(a, "name").Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
        { c.Fail("name must be a save folder name"); yield break; }
        if (!Directory.Exists(Path.Combine(Constants.SavesPath, name))) { c.Fail($"No save folder \"{name}\""); yield break; }

        _pendingLoad = name;
        _exiting = false;
        _titleTicks = 0;
        c.Result = $"Loading {name}; poll /api/state until saveFolder matches and ready is true";
        yield break;
    }

    /// <summary>Smoke saves only: add free cabins (with farmhand slots) so split-screen can join P2-P4.</summary>
    private static IEnumerator BuildCabins(JsonElement a, CallContext c)
    {
        if (!Context.IsMainPlayer) { c.Fail("Run build_cabins on p1 (the host)"); yield break; }
        if (SaveFolder.Name?.StartsWith("smoke_", StringComparison.OrdinalIgnoreCase) != true)
        { c.Fail("build_cabins only runs on smoke_* save copies"); yield break; }

        int want = Math.Clamp(I(a, "n", 3), 1, 7);
        var farm = Game1.getFarm();
        var built = new List<string>();
        int w = farm.Map.Layers[0].LayerWidth, h = farm.Map.Layers[0].LayerHeight;
        for (int y = 8; y < h - 6 && built.Count < want; y++)
        {
            for (int x = 8; x < w - 8 && built.Count < want; x++)
            {
                // A cabin is 5x3 plus a front row; only use plain buildable ground.
                bool ok = true;
                for (int dy = 0; dy < 4 && ok; dy++)
                    for (int dx = 0; dx < 5 && ok; dx++)
                    {
                        int tx = x + dx, ty = y + dy;
                        if (farm.doesTileHaveProperty(tx, ty, "Buildable", "Back") == null && farm.doesTileHaveProperty(tx, ty, "Diggable", "Back") == null)
                            ok = false;
                        else if (farm.getBuildingAt(new Microsoft.Xna.Framework.Vector2(tx, ty)) != null || farm.resourceClumps.Any(r => r.occupiesTile(tx, ty)))
                            ok = false;
                    }
                if (!ok) continue;
                for (int dy = 0; dy < 4; dy++)
                    for (int dx = 0; dx < 5; dx++)
                    {
                        var t = new Microsoft.Xna.Framework.Vector2(x + dx, y + dy);
                        farm.objects.Remove(t);
                        farm.terrainFeatures.Remove(t);
                    }
                if (!farm.buildStructure("Cabin", new Microsoft.Xna.Framework.Vector2(x, y), Game1.player, out var b, magicalConstruction: true, skipSafetyChecks: true))
                    continue;
                if (b.GetIndoors() is StardewValley.Locations.Cabin cabin && !cabin.HasOwner)
                    cabin.CreateFarmhand();
                built.Add($"({x},{y})");
                x += 6;
            }
        }
        if (built.Count < want) { c.Fail($"Built {built.Count} of {want} cabins: {string.Join(" ", built)}"); yield break; }
        c.Result = $"Built {built.Count} cabins at {string.Join(" ", built)}";
    }

    /// <summary>Forget that an AI is attached to this seat, so an idle seat stops holding the world frozen.</summary>
    private static IEnumerator Disconnect(JsonElement a, CallContext c)
    {
        Players.Current.Connected = false;
        c.Result = $"{Players.Current.Id} disconnected";
        yield break;
    }

    /// <summary>What this screen's chat box shows, one line per message (to prove a line reached this player's screen).</summary>
    private static IEnumerator ChatLog(JsonElement a, CallContext c)
    {
        var lines = Game1.chatBox?.messages.Select(m => string.Concat(m.message.Select(sn => sn.message))) ?? Enumerable.Empty<string>();
        c.Result = string.Join("\n", lines) is { Length: > 0 } t ? t : "(chat box is empty)";
        yield break;
    }

    /// <summary>Save the next composed game frame (every screen, never the desktop) as PNG.</summary>
    private static IEnumerator Capture(JsonElement a, CallContext c)
    {
        var path = S(a, "path");
        if (path.Length == 0) path = Path.Combine(Path.GetTempPath(), "stardew-capture.png");
        SplitScreenPatches.LastCapture = null;
        SplitScreenPatches.PendingCapture = path;
        for (int i = 0; i < 120 && SplitScreenPatches.LastCapture == null; i++)
            yield return null;
        var last = SplitScreenPatches.LastCapture;
        if (last == null || last.StartsWith("error")) { c.Fail(last ?? "No frame was drawn"); yield break; }
        c.Result = last;
    }

    // ------------------------------------------------------------------ save switching

    /// <summary>Called every tick from ModEntry: leave the current save, then load the requested one from the title screen.</summary>
    public static void Tick()
    {
        if (_pendingLoad == null) return;

        if (Context.IsWorldReady)
        {
            if (!_exiting)
            {
                _exiting = true;
                Game1.ExitToTitle();
            }
            return;
        }

        if (Game1.activeClickableMenu is TitleMenu && ++_titleTicks > 60)
        {
            var name = _pendingLoad;
            _pendingLoad = null;
            _exiting = false;
            SaveFolder.Pending = name;
            SaveGame.Load(name);
            Game1.exitActiveMenu();
        }
    }
}

/// <summary>
/// The folder name of the loaded save. SMAPI's own save name drops everything after the first underscore of a
/// custom folder name (smoke_x_123 becomes smoke_123), so remember what we asked the game to load.
/// </summary>
public static class SaveFolder
{
    /// <summary>Set right before SaveGame.Load; becomes <see cref="Loaded"/> when the save finishes loading.</summary>
    public static string? Pending;
    public static string? Loaded;

    public static string? Name => Context.IsWorldReady ? Loaded ?? Constants.SaveFolderName : null;

    public static void OnSaveLoaded()
    {
        Loaded = Pending;
        Pending = null;
    }
}
