using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// Whose thing is it? Chests and machines already carry the game's own <c>Object.owner</c> (the farmer who
/// placed them); a crop is owned by the seat that planted it (kept in the soil's modData, so it is saved with
/// the game). Taking from another seat's chest or harvesting its crop is allowed, as in the game, but writes a
/// <c>theft</c> entry to the thief's log stream for perception (#40) and the story log (#42).
/// </summary>
public static class Ownership
{
    private const string CropOwnerKey = "StardewMCP/owner";

    public static void Apply(IManifest manifest)
    {
        var harmony = new Harmony(manifest.UniqueID + ".ownership");
        harmony.Patch(AccessTools.Method(typeof(Crop), nameof(Crop.harvest)),
            postfix: new HarmonyMethod(typeof(Ownership), nameof(Harvest_Postfix)));
        harmony.Patch(AccessTools.Method(typeof(Chest), nameof(Chest.grabItemFromChest)),
            prefix: new HarmonyMethod(typeof(Ownership), nameof(GrabFromChest_Prefix)));
    }

    /// <summary>The AI seat whose farmer is <paramref name="farmerId"/>, or null (nobody, or the camera).</summary>
    public static PlayerSlot? SeatOf(long farmerId)
    {
        if (farmerId == 0) return null;
        var name = Game1.GetPlayer(farmerId)?.Name;
        return name == null ? null : Players.Slots.FirstOrDefault(s => s.Role == SeatRole.Ai && (s.State as ScreenState)?.Name == name);
    }

    /// <summary>Seat id (p1..p3) that placed this chest or machine, or null.</summary>
    public static string? OwnerOf(SObject obj) => SeatOf(obj.owner.Value)?.Id;

    /// <summary>Seat id that planted the crop on this soil, or null.</summary>
    public static string? OwnerOf(HoeDirt dirt) => dirt.modData.TryGetValue(CropOwnerKey, out var id) ? id : null;

    public static void ClaimCrop(HoeDirt dirt, string seat) => dirt.modData[CropOwnerKey] = seat;

    /// <summary>Chests and machines with a known owner in every location, for /api/state.</summary>
    public static List<object> OwnedObjects()
    {
        var list = new List<object>();
        Utility.ForEachLocation(loc =>
        {
            foreach (var obj in loc.objects.Values)
            {
                bool chest = obj is Chest { playerChest.Value: true };
                if (!chest && obj.GetMachineData() == null) continue;
                var owner = OwnerOf(obj);
                if (owner != null)
                    list.Add(new { kind = chest ? "chest" : "machine", name = obj.DisplayName, location = loc.NameOrUniqueName, tile = new[] { (int)obj.TileLocation.X, (int)obj.TileLocation.Y }, owner });
            }
            return true;
        });
        return list;
    }

    // ------------------------------------------------------------------ theft

    public sealed record TheftEvent(string Taker, string Owner, string What, string Thing, string Location, int X, int Y, int Day, string Time);

    public static event Action<TheftEvent>? Theft;

    private static void LogTheft(string what, string victim, string item, string location, int x, int y)
    {
        var thief = Players.Current;
        if (thief.Role != SeatRole.Ai || thief.Id == victim) return;
        thief.Runtime.Log.Add("theft",
            text: $"{thief.Id} took {item} from {victim}'s {what} at {location} ({x},{y})",
            args: new { victim, what, item, location, tile = new[] { x, y } });
        var ev = new TheftEvent(thief.Id, victim, item, what, location, x, y, AgentLog.FarmDay, WorldText.FormatTime(Game1.timeOfDay));
        try { Theft?.Invoke(ev); }
        catch { }
    }

    private static void Harvest_Postfix(Crop __instance, int xTile, int yTile, HoeDirt soil, StardewValley.Characters.JunimoHarvester junimoHarvester, bool __result)
    {
        if (!__result || junimoHarvester != null || soil == null || OwnerOf(soil) is not { } owner) return;
        LogTheft("crop", owner, WorldText.CropName(__instance), Game1.currentLocation?.NameOrUniqueName ?? "", xTile, yTile);
    }

    private static void GrabFromChest_Prefix(Chest __instance, Item item, Farmer who)
    {
        if (!who.IsLocalPlayer || OwnerOf(__instance) is not { } owner || !who.couldInventoryAcceptThisItem(item)) return;
        LogTheft("chest", owner, $"{item.Stack} x {item.DisplayName}", __instance.Location?.NameOrUniqueName ?? "", (int)__instance.TileLocation.X, (int)__instance.TileLocation.Y);
    }
}
