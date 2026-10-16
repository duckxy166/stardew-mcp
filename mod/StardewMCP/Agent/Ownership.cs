using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace StardewMCP.Agent;

/// <summary>A claim on one placed object (<see cref="Item"/> is set) or on a rectangle of ground.</summary>
/// <param name="Owner">Seat id (p1..p3).</param>
/// <param name="Location">GameLocation.NameOrUniqueName.</param>
/// <param name="Item">Qualified id of the claimed chest or machine, e.g. "(BC)130"; null for ground.</param>
/// <param name="Source">"claim" (the claim tool), "planted" (the seat planted a crop there) or "put" (it filled an unowned chest).</param>
public sealed record OwnerClaim(string Owner, string Location, int X, int Y, int W, int H, string? Item, string Source, int Day)
{
    public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;

    public bool Overlaps(int x, int y, int w, int h) => X < x + w && x < X + W && Y < y + h && y < Y + H;
}

/// <summary>Someone took something that another seat owns. The game allows it; the story log and the studio care.</summary>
public sealed record TheftEvent(string Taker, string Owner, string What, string Thing, string Location, int X, int Y, int Day, string Time);

/// <summary>
/// Who owns which chest, machine and patch of ground. Claims are made with the <c>claim</c> tool, or by planting
/// on unowned ground and filling an unowned chest. They are kept per save in the mod's data folder and are shown by
/// <c>look</c> ("Chest (owner: p2, ...)") and in /api/state. Taking from or harvesting something another seat owns
/// is allowed, as in the game, but is reported as a <c>theft</c> log entry and through <see cref="Theft"/>.
/// All screens run in one process, so this state is shared; every call is for the game thread.
/// </summary>
public static class Ownership
{
    public const string SourceClaim = "claim", SourcePlanted = "planted", SourcePut = "put";

    /// <summary>Longest side of a claimed area, claims per seat made by hand, and tiles per seat claimed by planting.</summary>
    public const int MaxSide = 30, MaxExplicitPerOwner = 40, MaxPlantedPerOwner = 2000;

    private static readonly object Lock = new();
    private static IMonitor? _monitor;
    private static string _dir = "";
    private static string? _loadedFor;
    private static bool _dirty;
    private static List<OwnerClaim> _claims = new();
    private static readonly List<TheftEvent> Thefts = new();
    // The tile each seat last pressed the action button on, so a chest menu can be traced back to its tile.
    private static readonly Dictionary<string, (string loc, int x, int y)> Touched = new();

    /// <summary>Raised on the game thread after the theft was logged. Perception (#40) subscribes to tell the owner.</summary>
    public static event Action<TheftEvent>? Theft;

    public static void Init(string dataDir, IMonitor monitor)
    {
        _dir = dataDir;
        _monitor = monitor;
    }

    // ------------------------------------------------------------------ lookup

    /// <summary>Seat id that owns the object or ground at this tile, or null.</summary>
    public static string? OwnerOf(GameLocation loc, int x, int y) => ClaimAt(loc, x, y)?.Owner;

    /// <summary>The most specific live claim on a tile: a claimed object, then a planted tile, then a claimed area.</summary>
    public static OwnerClaim? ClaimAt(GameLocation loc, int x, int y)
    {
        var name = loc.NameOrUniqueName;
        lock (Lock)
        {
            EnsureLoaded();
            OwnerClaim? best = null;
            int bestRank = int.MaxValue;
            foreach (var c in _claims)
            {
                if (!c.Contains(x, y) || c.Location != name) continue;
                int rank = c.Item != null ? 0 : c.W * c.H == 1 ? 1 : 2;
                if (rank >= bestRank || !IsLive(loc, c)) continue;
                best = c;
                bestRank = rank;
            }
            return best;
        }
    }

    /// <summary>" (owner: p2)" for the label of a tile, or "" when nobody owns it.</summary>
    public static string Suffix(GameLocation loc, int x, int y) => OwnerOf(loc, x, y) is { } o ? $" (owner: {o})" : "";

    /// <summary>"owner: p2, " for the start of a bracketed label, or "".</summary>
    public static string Tag(GameLocation loc, int x, int y) => OwnerOf(loc, x, y) is { } o ? $"owner: {o}, " : "";

    /// <summary>A claim on an object stops counting once that object is gone from its tile.</summary>
    private static bool IsLive(GameLocation loc, OwnerClaim c) =>
        c.Item == null || (loc.Objects.TryGetValue(new Vector2(c.X, c.Y), out var o) && o.QualifiedItemId == c.Item);

    private static string DescribeClaim(OwnerClaim c) =>
        c.Item != null ? $"{ItemRegistry.GetData(c.Item)?.DisplayName ?? "object"} at ({c.X},{c.Y})"
        : c.W * c.H == 1 ? $"planted tile ({c.X},{c.Y})"
        : $"{c.W}x{c.H} area at ({c.X},{c.Y})";

    // ------------------------------------------------------------------ claiming

    /// <summary>
    /// Claim a chest or machine (1x1 on a big craftable) or a rectangle of ground for <paramref name="owner"/>.
    /// Fails when part of it already belongs to someone else.
    /// </summary>
    public static bool TryClaim(string owner, GameLocation loc, int x, int y, int w, int h, string source, out string message)
    {
        w = Math.Clamp(w, 1, MaxSide);
        h = Math.Clamp(h, 1, MaxSide);
        int mapW = loc.Map.Layers[0].LayerWidth, mapH = loc.Map.Layers[0].LayerHeight;
        if (x < 0 || y < 0 || x + w > mapW || y + h > mapH)
        {
            message = $"{w}x{h} at ({x},{y}) is outside {loc.Name} ({mapW}x{mapH}).";
            return false;
        }

        string? item = null;
        if (w == 1 && h == 1 && loc.Objects.TryGetValue(new Vector2(x, y), out var obj))
        {
            if (!obj.QualifiedItemId.StartsWith("(BC)", StringComparison.Ordinal))
            {
                message = $"{obj.DisplayName} at ({x},{y}) can't be claimed. Claim a chest or machine, or an area of ground (width and height).";
                return false;
            }
            item = obj.QualifiedItemId;
        }

        var name = loc.NameOrUniqueName;
        lock (Lock)
        {
            EnsureLoaded();
            if (_claims.RemoveAll(c => c.Location == name && c.Item != null && c.Overlaps(x, y, w, h) && !IsLive(loc, c)) > 0)
                _dirty = true;

            foreach (var c in _claims)
            {
                if (c.Location != name || !c.Overlaps(x, y, w, h) || !IsLive(loc, c)) continue;
                if (c.Owner != owner)
                {
                    message = $"That overlaps {c.Owner}'s {DescribeClaim(c)}. Pick ground or objects nobody owns.";
                    return false;
                }
                if (c.X == x && c.Y == y && c.W == w && c.H == h && c.Item == item)
                {
                    message = $"Already yours: {DescribeClaim(c)}.";
                    return true;
                }
            }
            if (source != SourcePlanted && _claims.Count(c => c.Owner == owner && c.Source != SourcePlanted) >= MaxExplicitPerOwner)
            {
                message = $"You already hold {MaxExplicitPerOwner} claims. Release one with unclaim(x, y) first.";
                return false;
            }

            var claim = new OwnerClaim(owner, name, x, y, w, h, item, source, AgentLog.FarmDay);
            _claims.Add(claim);
            _dirty = true;
            message = $"You now own the {DescribeClaim(claim)}. Others can still take from it, but it will be logged as theft.";
            return true;
        }
    }

    /// <summary>Drop every claim of <paramref name="owner"/> that covers this tile.</summary>
    public static bool Release(string owner, GameLocation loc, int x, int y, out string message)
    {
        var name = loc.NameOrUniqueName;
        lock (Lock)
        {
            EnsureLoaded();
            var mine = _claims.Where(c => c.Owner == owner && c.Location == name && c.Contains(x, y)).ToList();
            if (mine.Count == 0)
            {
                message = $"You own nothing at ({x},{y}) on {loc.Name}.";
                return false;
            }
            foreach (var c in mine) _claims.Remove(c);
            _dirty = true;
            message = $"Released {string.Join(", ", mine.Select(DescribeClaim))}.";
            return true;
        }
    }

    /// <summary>A seat planted a crop here: unowned ground becomes its own. Ground inside someone's area stays theirs.</summary>
    public static void OnPlanted(string owner, GameLocation loc, int x, int y)
    {
        if (Players.ById(owner)?.Role != SeatRole.Ai) return;
        var name = loc.NameOrUniqueName;
        lock (Lock)
        {
            if (ClaimAt(loc, x, y) != null) return;
            if (_claims.Count(c => c.Owner == owner && c.Source == SourcePlanted) >= MaxPlantedPerOwner) return;
            _claims.Add(new OwnerClaim(owner, name, x, y, 1, 1, null, SourcePlanted, AgentLog.FarmDay));
            _dirty = true;
        }
    }

    // ------------------------------------------------------------------ chests

    /// <summary>Remember that a seat pressed the action button on this tile (a chest it opens is then known by tile).</summary>
    public static void NoteTouched(string seat, GameLocation loc, Point tile)
    {
        lock (Lock)
            Touched[seat] = (loc.NameOrUniqueName, tile.X, tile.Y);
    }

    /// <summary>The tile of the chest this seat has open, when it opened it with interact on this map.</summary>
    public static Point? ChestTile(string seat, GameLocation loc, Chest chest)
    {
        lock (Lock)
        {
            if (Touched.TryGetValue(seat, out var t) && t.loc == loc.NameOrUniqueName &&
                loc.Objects.TryGetValue(new Vector2(t.x, t.y), out var o) && ReferenceEquals(o, chest))
                return new Point(t.x, t.y);
        }
        return null;
    }

    /// <summary>A seat took items out of the chest it has open. Reports a theft when another seat owns the chest.</summary>
    public static void OnChestTake(string seat, GameLocation loc, Chest chest, string what)
    {
        if (ChestTile(seat, loc, chest) is not { } tile) return;
        var owner = OwnerOf(loc, tile.X, tile.Y);
        if (owner != null && owner != seat)
            ReportTheft(seat, owner, what, chest.DisplayName, loc, tile);
    }

    /// <summary>A seat put items into the chest it has open: an unowned chest becomes its own. Returns a note for the tool result, or null.</summary>
    public static string? OnChestPut(string seat, GameLocation loc, Chest chest)
    {
        if (Players.ById(seat)?.Role != SeatRole.Ai) return null;
        if (ChestTile(seat, loc, chest) is not { } tile) return null;
        if (OwnerOf(loc, tile.X, tile.Y) != null) return null;
        return TryClaim(seat, loc, tile.X, tile.Y, 1, 1, SourcePut, out var message) ? message : null;
    }

    // ------------------------------------------------------------------ things that can be taken

    /// <summary>"3 x Copper Bar" when the machine on this tile has finished output waiting, else null.</summary>
    public static string? ReadyOutput(GameLocation loc, int x, int y) =>
        loc.Objects.TryGetValue(new Vector2(x, y), out var o) && o is not Chest && o.readyForHarvest.Value && o.heldObject.Value is { } held
            ? $"{held.Stack} x {held.DisplayName}"
            : null;

    /// <summary>Name of the crop on this tile when it is ready to harvest, else null.</summary>
    public static string? RipeCrop(GameLocation loc, int x, int y) =>
        loc.terrainFeatures.TryGetValue(new Vector2(x, y), out var tf) && tf is HoeDirt dirt && dirt.crop != null && dirt.readyForHarvest()
            ? WorldText.CropName(dirt.crop)
            : null;

    // ------------------------------------------------------------------ theft

    /// <summary>
    /// Log a theft by <paramref name="taker"/> (seat id) from <paramref name="owner"/> and raise <see cref="Theft"/>.
    /// The log entry is kind "theft", stamped with the taker as its player, with args taker / owner / what / thing / location / x / y.
    /// </summary>
    public static TheftEvent? ReportTheft(string taker, string owner, string what, string thing, GameLocation loc, Point tile)
    {
        if (taker == owner) return null;
        var ev = new TheftEvent(taker, owner, what, thing, loc.NameOrUniqueName, tile.X, tile.Y, AgentLog.FarmDay, WorldText.FormatTime(Game1.timeOfDay));
        lock (Lock)
        {
            Thefts.Add(ev);
            if (Thefts.Count > 50) Thefts.RemoveAt(0);
        }

        var seat = Players.ById(taker);
        var victim = Players.ById(owner);
        var text = $"{(seat != null ? SocialTools.Label(seat) : taker)} took {what} from {(victim != null ? SocialTools.Label(victim) : owner)}'s {thing} at ({tile.X},{tile.Y}) on {loc.Name}";
        seat?.Runtime.Log.Add("theft", text: text, args: new { taker, owner, what, thing, location = ev.Location, x = tile.X, y = tile.Y });
        try { Theft?.Invoke(ev); }
        catch (Exception ex) { _monitor?.Log($"A theft subscriber failed: {ex.Message}", LogLevel.Warn); }
        return ev;
    }

    // ------------------------------------------------------------------ views

    /// <summary>One line for <c>status</c>: who you are and what you own.</summary>
    public static string SummaryFor(string seat)
    {
        lock (Lock)
        {
            EnsureLoaded();
            var mine = _claims.Where(c => c.Owner == seat).ToList();
            if (mine.Count == 0)
                return $"You are {seat}. You own nothing yet: claim your chest, machines and plot area with claim(x, y, width, height).";
            return $"You are {seat}. You own {mine.Count(c => c.Item != null)} chest/machine, " +
                   $"{mine.Count(c => c.Item == null && c.Source != SourcePlanted)} claimed area(s), {mine.Count(c => c.Source == SourcePlanted)} planted tile(s).";
        }
    }

    /// <summary>For /api/state: claims per owner (planted tiles only as a count) and the latest thefts.</summary>
    public static object Snapshot()
    {
        lock (Lock)
        {
            EnsureLoaded();
            var owners = _claims.GroupBy(c => c.Owner).ToDictionary(g => g.Key, g => new
            {
                objects = g.Where(c => c.Item != null).Select(c => new { location = c.Location, x = c.X, y = c.Y, item = c.Item }).ToList(),
                areas = g.Where(c => c.Item == null && c.Source != SourcePlanted).Select(c => new { location = c.Location, x = c.X, y = c.Y, w = c.W, h = c.H }).ToList(),
                plantedTiles = g.Count(c => c.Source == SourcePlanted)
            });
            return new { owners, thefts = Thefts.TakeLast(20).ToList() };
        }
    }

    // ------------------------------------------------------------------ persistence (one file per save)

    private static string PathFor(string save)
    {
        var bad = Path.GetInvalidFileNameChars();
        return Path.Combine(_dir, "ownership_" + string.Concat(save.Select(ch => bad.Contains(ch) ? '_' : ch)) + ".json");
    }

    /// <summary>Switch to the claims of the loaded save (saving the previous save's first). Call with <see cref="Lock"/> held.</summary>
    private static void EnsureLoaded()
    {
        var save = SaveFolder.Name;
        if (save == null || save == _loadedFor) return;

        FlushLocked();
        _loadedFor = save;
        _claims = new List<OwnerClaim>();
        _dirty = false;
        Thefts.Clear();
        Touched.Clear();
        try
        {
            var path = PathFor(save);
            if (File.Exists(path))
                _claims = JsonSerializer.Deserialize<List<OwnerClaim>>(File.ReadAllText(path), AgentLog.Json) ?? new List<OwnerClaim>();
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Could not read the ownership claims of {save}: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>Write the claims to disk when they changed (called a few times a second from the host tick).</summary>
    public static void Flush()
    {
        lock (Lock)
            FlushLocked();
    }

    private static void FlushLocked()
    {
        if (!_dirty || _loadedFor == null) return;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(PathFor(_loadedFor), JsonSerializer.Serialize(_claims, AgentLog.Json));
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Could not write the ownership claims: {ex.Message}", LogLevel.Warn);
        }
    }
}
