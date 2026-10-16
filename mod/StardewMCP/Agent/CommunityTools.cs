using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

// Community Center bundles and Museum donations (#13).
//
// The bundle page is driven through the game's own JunimoNoteMenu by clicks (the game deposits the item, pays the
// reward, plays the room-complete scene); success is read back from Game1.netWorldState.Value.Bundles, never from
// the UI. Members of JunimoNoteMenu, Bundle and LibraryMuseum that this code could not check against the decompiled
// game are reached through LateBound: a missing member becomes a clear "tool failed" line instead of a crash.
// BundleReader, BundleLedger and LateBound are written to be reused by the Joja alternative (#26).

/// <summary>A game member this mod looks up by name at run time was not there (the game version differs).</summary>
internal sealed class LateBoundException : Exception
{
    public LateBoundException(string message) : base(message) { }
}

/// <summary>Reads, writes and calls game members by name, for the few ones that could not be checked at compile time.</summary>
internal static class LateBound
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Read a field or property by name (any visibility, base classes included); false when there is none.</summary>
    public static bool TryRead(object? target, Type type, string name, out object? value)
    {
        value = null;
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, Any);
            if (f != null && (f.IsStatic || target != null))
            {
                value = f.GetValue(f.IsStatic ? null : target);
                return true;
            }
            var p = t.GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0 && p.GetMethod is { } getter && (getter.IsStatic || target != null))
            {
                value = getter.Invoke(getter.IsStatic ? null : target, null);
                return true;
            }
        }
        return false;
    }

    /// <summary>The member's value, or null when it is missing or null.</summary>
    public static object? TryGet(object? target, string name) =>
        target != null && TryRead(target, target.GetType(), name, out var v) ? v : null;

    public static object? TryGetStatic(Type type, string name) =>
        TryRead(null, type, name, out var v) ? v : null;

    /// <summary>The member's value (may be null); throws <see cref="LateBoundException"/> when the member does not exist.</summary>
    public static object? Require(object target, string name)
    {
        if (!TryRead(target, target.GetType(), name, out var v))
            throw new LateBoundException($"{target.GetType().Name} has no member \"{name}\" (the game version may differ from the one this mod was written for)");
        return v;
    }

    /// <summary>Write a field or property by name; false when there is none or it can't be written.</summary>
    public static bool TrySet(object? target, string name, object? value)
    {
        if (target == null) return false;
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, Any);
            if (f != null && !f.IsStatic && !f.IsInitOnly)
            {
                f.SetValue(target, value);
                return true;
            }
            var p = t.GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0 && p.SetMethod is { IsStatic: false } setter)
            {
                setter.Invoke(target, new[] { value });
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Call a method by name (any visibility, static or instance, ignoring case; missing optional arguments are filled in).
    /// False when no overload takes these arguments; a failure inside the game method is rethrown as <see cref="LateBoundException"/>.
    /// </summary>
    public static bool TryCall(object? target, Type type, string name, out object? result, params object?[] args)
    {
        result = null;
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var m in t.GetMethods(Any))
            {
                if (!m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || m.IsGenericMethodDefinition) continue;
                if (!m.IsStatic && target == null) continue;
                var ps = m.GetParameters();
                if (args.Length > ps.Length) continue;

                var full = new object?[ps.Length];
                bool fits = true;
                for (int i = 0; i < ps.Length && fits; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (pt.IsByRef) { fits = false; break; }
                    if (i < args.Length)
                    {
                        fits = args[i] == null ? !pt.IsValueType || Nullable.GetUnderlyingType(pt) != null : pt.IsInstanceOfType(args[i]);
                        full[i] = args[i];
                    }
                    else if (ps[i].HasDefaultValue)
                        full[i] = ps[i].DefaultValue;
                    else
                        fits = false;
                }
                if (!fits) continue;

                try
                {
                    result = m.Invoke(m.IsStatic ? null : target, full);
                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    throw new LateBoundException($"{type.Name}.{m.Name} failed: {(ex.InnerException ?? ex).Message}");
                }
            }
        }
        return false;
    }

    public static object? Call(object target, string name, params object?[] args)
    {
        if (TryCall(target, target.GetType(), name, out var r, args)) return r;
        throw new LateBoundException($"{target.GetType().Name} has no method \"{name}\" taking {args.Length} argument(s) of these types (the game version may differ from the one this mod was written for)");
    }
}

/// <summary>What one bundle ingredient asks for. <see cref="Id"/> is the bundle data's own id: -1 gold, below -1 an item category, else an object id.</summary>
internal sealed record BundleNeed(int Slot, string Id, int Count, int Quality, bool Done);

/// <summary>One bundle, parsed from <c>BundleData</c> and the donated flags in <c>Bundles</c>.</summary>
internal sealed class BundleInfo
{
    public int Index;
    public string Room = "";
    /// <summary>Position of <see cref="Room"/> in <see cref="BundleReader.Rooms"/>, or -1 for a room this mod does not drive (the Abandoned Joja Mart).</summary>
    public int Area = -1;
    public string Name = "";
    public string Reward = "";
    /// <summary>How many of <see cref="Needs"/> must be filled (some bundles take 4 of 6).</summary>
    public int Required;
    public List<BundleNeed> Needs = new();

    public int Filled => Needs.Count(n => n.Done);
    public bool Complete => Filled >= Required;
}

/// <summary>
/// Reads the Community Center bundles from the world state and describes them. Shared with the Joja alternative (#26),
/// which closes the same bundles: <see cref="ClosedReason"/> tells the two routes apart.
/// </summary>
internal static class BundleReader
{
    /// <summary>The six rooms in the order the game numbers their areas (0-5).</summary>
    public static readonly string[] Rooms = { "Pantry", "Crafts Room", "Fish Tank", "Boiler Room", "Vault", "Bulletin Board" };

    private static readonly Dictionary<int, string> Categories = new()
    {
        [-2] = "any Gem", [-4] = "any Fish", [-5] = "any Egg", [-6] = "any Milk", [-7] = "any Cooked dish", [-12] = "any Mineral",
        [-14] = "any Meat", [-15] = "any Metal", [-16] = "any Building material", [-18] = "any Animal product",
        [-19] = "any Fertilizer", [-20] = "any Junk", [-21] = "any Bait", [-22] = "any Tackle", [-25] = "any Ingredient",
        [-26] = "any Artisan good", [-27] = "any Syrup", [-28] = "any Monster loot", [-74] = "any Seed", [-75] = "any Vegetable",
        [-79] = "any Fruit", [-80] = "any Flower", [-81] = "any Forage"
    };

    /// <summary>The player's own mail flag, or the host's (world-wide unlocks such as the Joja membership live on the host).</summary>
    public static bool HasMail(Farmer who, string id) =>
        who.mailReceived.Contains(id) || Game1.MasterPlayer.mailReceived.Contains(id);

    public static bool IsJoja(Farmer who) => HasMail(who, "JojaMember");

    /// <summary>Why the bundles can't be used right now, or null when they can.</summary>
    public static string? ClosedReason(Farmer who)
    {
        if (IsJoja(who))
            return "This farm took a Joja membership, so the Community Center bundles are closed for good.";
        if (Game1.currentLocation is CommunityCenter || HasMail(who, "ccDoorUnlock") || HasMail(who, "canReadJunimoText") || HasMail(who, "ccIsComplete"))
            return null;
        return "The Community Center is not open yet: its door has not been unlocked (the Community Center scene), and the Wizard teaches you to read the Junimo notes.";
    }

    /// <summary>Every bundle, rooms in game order. Empty when the world has no bundle data.</summary>
    public static List<BundleInfo> Read()
    {
        var world = Game1.netWorldState.Value;
        var list = new List<BundleInfo>();
        foreach (var kv in world.BundleData.Pairs)
        {
            var keyParts = kv.Key.Split('/');
            if (keyParts.Length < 2 || !int.TryParse(keyParts[1], out var index)) continue;
            var f = kv.Value.Split('/');
            if (f.Length < 3) continue;

            var info = new BundleInfo
            {
                Index = index,
                Room = keyParts[0],
                Area = Array.IndexOf(Rooms, keyParts[0]),
                Name = f[0],
                Reward = f[1]
            };
            var flags = Flags(index);
            var t = f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int slot = i / 3;
                int.TryParse(t[i + 1], out var count);
                int.TryParse(t[i + 2], out var quality);
                info.Needs.Add(new BundleNeed(slot, t[i], Math.Max(1, count), quality, slot < flags.Length && flags[slot]));
            }
            info.Required = f.Length > 4 && int.TryParse(f[4], out var req) && req > 0 ? Math.Min(req, info.Needs.Count) : info.Needs.Count;
            list.Add(info);
        }
        return list.OrderBy(b => b.Area < 0 ? 99 : b.Area).ThenBy(b => b.Index).ToList();
    }

    /// <summary>The donated flags of one bundle (one per ingredient); empty when the bundle is unknown.</summary>
    public static bool[] Flags(int index) =>
        Game1.netWorldState.Value.Bundles.TryGetValue(index, out var f) && f != null ? f : Array.Empty<bool>();

    /// <summary>True when this item can fill the ingredient (item or category, quality at least the asked one). The stack size is checked separately.</summary>
    public static bool Fits(Item item, BundleNeed need)
    {
        if (need.Done || need.Id == "-1" || item.Quality < need.Quality) return false;
        if (need.Id.StartsWith("(")) return item.QualifiedItemId == need.Id;
        if (int.TryParse(need.Id, out var cat) && cat < -1) return item.Category == cat;
        return item.QualifiedItemId == "(O)" + need.Id;
    }

    public static bool BundleMatches(BundleInfo b, string want)
    {
        if (want.Length == 0) return true;
        if (int.TryParse(want, out var n) && n == b.Index) return true;
        return b.Name.Equals(want, StringComparison.OrdinalIgnoreCase) || b.Name.Contains(want, StringComparison.OrdinalIgnoreCase);
    }

    public static bool RoomMatches(string room, string want) =>
        room.Equals(want, StringComparison.OrdinalIgnoreCase) || room.Contains(want, StringComparison.OrdinalIgnoreCase);

    public static string ItemName(string id)
    {
        if (id == "-1") return "Gold";
        if (int.TryParse(id, out var n) && n < -1) return Categories.TryGetValue(n, out var cat) ? cat : $"category {n}";
        return ItemRegistry.GetData(id.StartsWith("(") ? id : "(O)" + id)?.DisplayName ?? id;
    }

    public static string Describe(BundleNeed n)
    {
        if (n.Id == "-1") return $"{n.Count}g";
        var quality = n.Quality switch { 1 => " (silver or better)", 2 => " (gold or better)", 4 => " (iridium)", _ => "" };
        return $"{(n.Count > 1 ? n.Count + " x " : "")}{ItemName(n.Id)}{quality}";
    }

    /// <summary>"O 465 20" in bundle data: type letter, item id, count.</summary>
    public static string RewardText(string reward)
    {
        var p = reward.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return reward;
        string? prefix = p[0] switch { "O" or "R" => "(O)", "BO" => "(BC)", "F" => "(F)", "W" => "(W)", "H" => "(H)", "B" => "(B)", _ => null };
        var count = p.Length > 2 && int.TryParse(p[2], out var n) && n > 1 ? n + " x " : "";
        var name = prefix == null ? null : ItemRegistry.GetData(prefix + p[1])?.DisplayName;
        return count + (name ?? reward);
    }

    /// <summary>"(you have it)" when one inventory stack covers the ingredient, "(you hold 3 of 5)" when it is short, else "".</summary>
    private static string Carry(Farmer who, BundleNeed n)
    {
        if (n.Id == "-1") return who.Money >= n.Count ? " (you can pay it)" : "";
        int best = who.Items.Where(i => i != null && Fits(i, n)).Select(i => i.Stack).DefaultIfEmpty(0).Max();
        if (best >= n.Count) return " (you have it)";
        return best > 0 ? $" (you hold {best} of {n.Count})" : "";
    }

    /// <summary>The text of the bundles tool: rooms, bundles, what is donated (and by whom, when this mod saw it) and what is missing.</summary>
    public static string Report(IReadOnlyList<BundleInfo> all, Farmer who, string? room)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Community Center: {all.Count(b => b.Complete)}/{all.Count} bundles complete. Donate with donate_bundle(item) while standing inside the Community Center.");
        if (!HasMail(who, "canReadJunimoText"))
            sb.AppendLine("(You can't read Junimo script yet: the Wizard teaches it. The data below is exact anyway.)");
        foreach (var g in all.GroupBy(b => b.Room))
        {
            if (room != null && !RoomMatches(g.Key, room)) continue;
            sb.AppendLine($"== {g.Key}: {g.Count(b => b.Complete)}/{g.Count()} bundles complete");
            foreach (var b in g)
            {
                if (b.Complete)
                {
                    sb.AppendLine($"  [{b.Index}] {b.Name} COMPLETE → {RewardText(b.Reward)}");
                    continue;
                }
                var done = b.Needs.Where(n => n.Done)
                    .Select(n => Describe(n) + (BundleLedger.Who(b.Index, n.Slot) is { } w ? $" (by {w})" : ""));
                var open = b.Needs.Where(n => !n.Done).ToList();
                int remaining = b.Required - b.Filled;
                var lead = remaining < open.Count ? $"needs {remaining} more of: " : "needs: ";
                var doneText = done.Any() ? " | done: " + string.Join(", ", done) : "";
                sb.AppendLine($"  [{b.Index}] {b.Name} {b.Filled}/{b.Required} → {RewardText(b.Reward)}{doneText} | {lead}{string.Join(", ", open.Select(n => Describe(n) + Carry(who, n)))}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Who donated what. The game keeps only "slot filled" flags, so this remembers the donations made through
/// donate_bundle in memory (lost on restart); a donation made by hand is not listed.
/// </summary>
internal static class BundleLedger
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(int bundle, int slot), (string who, int day)> Donors = new();

    public static void Record(int bundle, int slot, string who, int day)
    {
        lock (Gate) Donors[(bundle, slot)] = (who, day);
    }

    public static string? Who(int bundle, int slot)
    {
        lock (Gate) return Donors.TryGetValue((bundle, slot), out var d) ? $"{d.who} day {d.day}" : null;
    }
}

public static partial class AgentTools
{
    private static List<ToolDef> CommunityTools() => new()
    {
        new()
        {
            Name = "bundles",
            Description = "Community Center bundles, room by room: what each bundle needs (item, quality, count), what is already donated and by whom, the reward, " +
                          "and which of your inventory items fit a missing slot. Needs the Community Center to be open (the Wizard teaches you to read the notes). " +
                          "Game time stays frozen.",
            Schema = Obj(new { room = Str("Optional: only this room (Pantry, Crafts Room, Fish Tank, Boiler Room, Vault, Bulletin Board)") }),
            Instant = true,
            Run = (a, c) => CommunityGuard(BundlesRun(a, c), c)
        },
        new()
        {
            Name = "donate_bundle",
            Description = "Donate an item from your inventory to the first Community Center bundle that needs it. You must be inside the Community Center (travel_to CommunityCenter). " +
                          "It uses the game's own bundle page, so rewards and room-complete scenes happen as for a player. " +
                          "item \"gold\" pays a Vault bundle. bundle (name or number from bundles) picks the bundle if several take the item. " +
                          "Reports the bundle's progress and any bundle or room it completes.",
            Schema = Obj(new
            {
                item = Str("Item name from your inventory, or \"gold\" for a Vault bundle"),
                bundle = Str("Optional: bundle name or number (see bundles)"),
                reason = Str(ReasonDesc)
            }, "item", "reason"),
            Run = (a, c) => CommunityGuard(DonateBundleRun(a, c), c)
        },
        new()
        {
            Name = "museum",
            Description = "The Museum (ArchaeologyHouse, Gunther). action list (default): the items in your inventory that the Museum still accepts. " +
                          "donate: put an item (or \"all\") on the next free display spot; you must be inside the Museum (travel_to ArchaeologyHouse). " +
                          "rewards: talk to Gunther and take the rewards you have earned. list keeps game time frozen.",
            Schema = Obj(new
            {
                action = new { type = "string", @enum = new[] { "list", "donate", "rewards" }, description = "list (default), donate or rewards" },
                item = Str("donate: item name from your inventory, or \"all\""),
                reason = Str(ReasonDesc)
            }),
            InstantFor = a => S(a, "action").Trim().ToLowerInvariant() is "" or "list",
            Run = (a, c) => CommunityGuard(MuseumRun(a, c), c)
        }
    };

    /// <summary>Runs a tool body and turns a missing game member (<see cref="LateBoundException"/>) into a plain tool failure.</summary>
    private static IEnumerator CommunityGuard(IEnumerator inner, CallContext c)
    {
        try
        {
            while (true)
            {
                bool more;
                try { more = inner.MoveNext(); }
                catch (LateBoundException ex) { c.Fail(ex.Message); more = false; }
                if (!more) yield break;
                yield return inner.Current;
            }
        }
        finally
        {
            (inner as IDisposable)?.Dispose();
        }
    }

    // ------------------------------------------------------------------ bundles

    private static IEnumerator BundlesRun(JsonElement a, CallContext c)
    {
        var closed = BundleReader.ClosedReason(P);
        if (closed != null) { c.Fail(closed); yield break; }
        var all = BundleReader.Read();
        if (all.Count == 0) { c.Fail("The game has no bundle data yet."); yield break; }
        var room = S(a, "room").Trim();
        if (room.Length > 0 && !all.Any(b => BundleReader.RoomMatches(b.Room, room)))
        {
            c.Fail($"No room \"{room}\". Rooms: {string.Join(", ", all.Select(b => b.Room).Distinct())}");
            yield break;
        }
        c.Result = BundleReader.Report(all, P, room.Length > 0 ? room : null);
    }

    private sealed record DonationPick(BundleInfo Bundle, BundleNeed Need, int InvSlot);

    /// <summary>Inventory slots holding an item called <paramref name="name"/>: exact name matches, else partial ones.</summary>
    private static List<int> StacksNamed(string name)
    {
        name = name.Trim().ToLowerInvariant();
        var exact = new List<int>();
        var loose = new List<int>();
        for (int i = 0; i < P.Items.Count; i++)
        {
            var it = P.Items[i];
            if (it == null) continue;
            var dn = it.DisplayName.ToLowerInvariant();
            var n = it.Name.ToLowerInvariant();
            if (dn == name || n == name) exact.Add(i);
            else if (dn.Contains(name) || n.Contains(name)) loose.Add(i);
        }
        return exact.Count > 0 ? exact : loose;
    }

    /// <summary>The first open bundle slot (rooms in game order) that the named item, or gold, can fill. Lower-quality stacks go first.</summary>
    private static (DonationPick? pick, string? error) PickDonation(List<BundleInfo> all, string want, bool gold, string bundleArg)
    {
        var open = all.Where(b => b.Area >= 0 && !b.Complete && BundleReader.BundleMatches(b, bundleArg)).ToList();
        if (open.Count == 0)
            return (null, bundleArg.Length > 0 ? $"No unfinished bundle matches \"{bundleArg}\". Use bundles to see them." : "Every bundle in the Community Center is already complete.");

        var stacks = gold ? new List<int>() : StacksNamed(want);
        if (!gold && stacks.Count == 0)
            return (null, $"No \"{want}\" in your inventory.");

        foreach (var b in open)
        {
            var missing = b.Needs.Where(x => !x.Done).ToList();
            foreach (var n in missing)
            {
                if (gold)
                {
                    if (n.Id == "-1") return (new DonationPick(b, n, -1), null);
                    continue;
                }
                foreach (var slot in stacks.OrderBy(s => P.Items[s].Quality))
                {
                    var stack = P.Items[slot];
                    if (BundleReader.Fits(stack, n) && stack.Stack >= n.Count) return (new DonationPick(b, n, slot), null);
                }
            }
        }

        if (gold) return (null, "No unfinished bundle takes gold.");
        return (null, $"\"{want}\" fits no open bundle slot: either no bundle needs it, its quality is too low, or you hold fewer than the bundle asks. Use bundles to see what is needed.");
    }

    private static IEnumerator DonateBundleRun(JsonElement a, CallContext c)
    {
        var closed = BundleReader.ClosedReason(P);
        if (closed != null) { c.Fail(closed); yield break; }
        if (Loc is not CommunityCenter)
        {
            c.Fail($"Bundles are donated inside the Community Center; you are in {Loc.Name}. Use travel_to CommunityCenter first.");
            yield break;
        }
        if (Game1.activeClickableMenu != null || Game1.eventUp)
        {
            c.Fail("Close the open menu or finish the scene first.");
            yield break;
        }

        var all = BundleReader.Read();
        if (all.Count == 0) { c.Fail("The game has no bundle data yet."); yield break; }
        var want = S(a, "item").Trim();
        if (want.Length == 0) { c.Fail("item is required: an inventory item name, or \"gold\"."); yield break; }
        bool gold = want.Equals("gold", StringComparison.OrdinalIgnoreCase) || want.Equals("money", StringComparison.OrdinalIgnoreCase);

        var (pick, error) = PickDonation(all, want, gold, S(a, "bundle").Trim());
        if (pick == null) { c.Fail(error!); yield break; }
        var b = pick.Bundle;
        var need = pick.Need;
        if (gold && P.Money < need.Count) { c.Fail($"{b.Name} costs {need.Count}g and you have {P.Money}g."); yield break; }

        var itemName = gold ? "gold" : P.Items[pick.InvSlot].DisplayName;
        int doneBefore = BundleReader.Flags(b.Index).Count(x => x);
        var before = Snapshot();
        bool success = false;
        bool rewardTaken = false;

        var menu = OpenJunimoMenu(b.Area);
        Game1.activeClickableMenu = menu;
        try
        {
            // The menu ignores clicks while its screen swipe plays.
            for (int i = 0; i < 180 && LateBound.TryGetStatic(typeof(JunimoNoteMenu), "canClick") is false; i++) yield return null;
            yield return Ticks(6);

            Click(menu, FindBundleComponent(menu, b.Index));
            for (int i = 0; i < 90 && LateBound.TryGet(menu, "specificBundlePage") is not true; i++) yield return null;
            if (LateBound.TryGet(menu, "specificBundlePage") is not true)
            {
                c.Fail($"The {b.Name} bundle page did not open.");
                yield break;
            }
            yield return Ticks(6);

            if (gold)
            {
                if (LateBound.TryGet(menu, "purchaseButton") is not ClickableComponent buy)
                {
                    c.Fail($"The {b.Name} page has no purchase button.");
                    yield break;
                }
                Click(menu, buy);
                for (int i = 0; i < 30 && !success; i++)
                {
                    yield return null;
                    success = BundleReader.Flags(b.Index).Count(x => x) > doneBefore;
                }
            }
            else
            {
                var invMenu = LateBound.TryGet(menu, "inventory") as InventoryMenu;
                var slotCc = invMenu?.inventory.FirstOrDefault(s => s.name == pick.InvSlot.ToString());
                if (slotCc == null)
                {
                    c.Fail($"Your {itemName} (slot {pick.InvSlot}) is not shown on the bundle page.");
                    yield break;
                }
                Click(menu, slotCc); // pick the stack up
                yield return Ticks(3);
                if (LateBound.TryGet(menu, "heldItem") == null)
                {
                    c.Fail($"Could not pick up {itemName}.");
                    yield break;
                }

                // Empty slots first; the game matches the item to its ingredient itself, so any free slot works.
                var slots = IngredientSlots(menu).OrderBy(s => LateBound.TryGet(s, "item") == null ? 0 : 1).ToList();
                if (slots.Count == 0)
                {
                    c.Fail($"The {b.Name} page shows no ingredient slots.");
                    yield break;
                }
                foreach (var slot in slots)
                {
                    Click(menu, slot);
                    for (int i = 0; i < 12 && !success; i++)
                    {
                        yield return null;
                        success = BundleReader.Flags(b.Index).Count(x => x) > doneBefore;
                    }
                    if (success || LateBound.TryGet(menu, "heldItem") == null) break;
                }
            }

            if (success && BundleReader.Flags(b.Index).Count(x => x) >= b.Required)
            {
                // The game plays the completion animation, then returns to the room's page.
                for (int i = 0; i < 600 && LateBound.TryGet(menu, "specificBundlePage") is true; i++) yield return null;
                yield return Ticks(40);
                if (Game1.activeClickableMenu == menu && LateBound.TryGet(menu, "presentButton") is ClickableComponent gift && gift.visible)
                {
                    // Best effort: the gift icon on the room's page opens the reward grab menu.
                    Click(menu, gift);
                    for (int i = 0; i < 60 && Game1.activeClickableMenu is not ItemGrabMenu; i++) yield return null;
                    if (Game1.activeClickableMenu is ItemGrabMenu)
                    {
                        var rewardBefore = Snapshot();
                        yield return CommunityTakeAll();
                        rewardTaken = Diff(rewardBefore).Length > 0;
                    }
                }
            }
        }
        finally
        {
            ReturnJunimoHeldItem(menu);
            if (Game1.activeClickableMenu == menu)
            {
                menu.exitThisMenu();
                if (Game1.activeClickableMenu == menu) Game1.exitActiveMenu();
            }
            else if (Game1.activeClickableMenu is ItemGrabMenu rewards && rewards.ItemsToGrabMenu.actualInventory.All(i => i == null))
                Game1.exitActiveMenu();
        }
        if (!c.Ok) yield break;
        yield return Ticks(10);

        var after = BundleReader.Read();
        var now = after.FirstOrDefault(x => x.Index == b.Index) ?? b;
        if (!success)
        {
            var diff = Diff(before);
            c.Fail($"Nothing was donated to {b.Name}: the game did not take {itemName}. {diff}".Trim());
            yield break;
        }

        var filled = now.Needs.Where(n => n.Done && !b.Needs[n.Slot].Done).ToList();
        foreach (var n in filled) BundleLedger.Record(b.Index, n.Slot, P.Name, AgentLog.FarmDay);
        var sb = new StringBuilder();
        sb.Append(gold ? $"Paid {need.Count}g into {b.Name} ({b.Room})" : $"Donated {itemName} to {b.Name} ({b.Room})");
        sb.Append($": {now.Filled}/{now.Required} filled.");
        Rt.Log.Add("bundle_donated", text: $"{P.Name} donated {itemName} to {b.Name}",
            args: new { player = Rt.Log.Player, bundle = b.Name, room = b.Room, item = itemName, filled = now.Filled, required = now.Required });

        if (now.Complete)
        {
            var reward = BundleReader.RewardText(b.Reward);
            sb.Append($" Bundle complete! Reward: {reward}{(rewardTaken ? " (in your inventory now)" : "; if it is not in your inventory, open the Junimo note in this room and click the gift")}.");
            Rt.Log.Add("bundle_completed", text: $"{P.Name} completed the {b.Name} bundle",
                args: new { player = Rt.Log.Player, bundle = b.Name, room = b.Room, reward });
            var roomBundles = after.Where(x => x.Room == b.Room).ToList();
            if (roomBundles.All(x => x.Complete))
            {
                sb.Append($" The {b.Room} is complete!");
                Rt.Log.Add("room_completed", text: $"{P.Name} completed the {b.Room}", args: new { player = Rt.Log.Player, room = b.Room });
            }
            if (after.All(x => x.Complete)) sb.Append(" The whole Community Center is complete!");
        }
        else
        {
            var left = now.Needs.Where(n => !n.Done).Select(BundleReader.Describe).ToList();
            int remaining = now.Required - now.Filled;
            sb.Append(left.Count == 0 ? "" : $" Still needs {(remaining < left.Count ? $"{remaining} more of" : "")}: {string.Join(", ", left)}.");
        }
        var inv = Diff(before);
        if (inv.Length > 0) sb.Append(' ').Append(inv);
        c.Result = sb.ToString();
    }

    /// <summary>The bundle page for one room, opened as the game does when a player uses the room's Junimo note.</summary>
    private static IClickableMenu OpenJunimoMenu(int area)
    {
        var dict = new Dictionary<int, bool[]>();
        foreach (var kv in Game1.netWorldState.Value.Bundles.Pairs)
            dict[kv.Key] = kv.Value;
        object? menu;
        try
        {
            menu = Activator.CreateInstance(typeof(JunimoNoteMenu), BindingFlags.Public | BindingFlags.Instance | BindingFlags.OptionalParamBinding,
                null, new object[] { area, dict }, null);
        }
        catch (MissingMethodException)
        {
            throw new LateBoundException("JunimoNoteMenu has no (area, bundles) constructor in this game version; stand at the room's Junimo note and use interact instead.");
        }
        catch (TargetInvocationException ex)
        {
            throw new LateBoundException($"The bundle page could not be opened: {(ex.InnerException ?? ex).Message}");
        }
        return (IClickableMenu)menu!;
    }

    private static ClickableComponent FindBundleComponent(IClickableMenu menu, int index)
    {
        if (LateBound.Require(menu, "bundles") is IEnumerable list)
            foreach (var o in list)
                if (o is ClickableComponent cc && LateBound.TryGet(o, "bundleIndex") is int bi && bi == index)
                    return cc;
        throw new LateBoundException($"Bundle {index} is not on the opened room page (JunimoNoteMenu.bundles / Bundle.bundleIndex differ in this game version).");
    }

    private static List<ClickableComponent> IngredientSlots(IClickableMenu menu) =>
        (LateBound.TryGet(menu, "ingredientSlots") as IEnumerable)?.OfType<ClickableComponent>().ToList() ?? new List<ClickableComponent>();

    /// <summary>Whatever the menu still holds on the cursor goes back into the inventory (or the ground), so closing the menu loses nothing.</summary>
    private static void ReturnJunimoHeldItem(IClickableMenu menu)
    {
        if (LateBound.TryGet(menu, "heldItem") is not Item held) return;
        LateBound.TrySet(menu, "heldItem", null);
        if (P.addItemToInventory(held) is Item rest)
            Game1.createItemDebris(rest, P.getStandingPosition(), P.FacingDirection);
    }

    /// <summary>Click every item of the open reward/grab menu into the inventory until it is empty or the inventory is full.</summary>
    private static IEnumerator CommunityTakeAll()
    {
        for (int i = 0; i < 60; i++)
        {
            if (Game1.activeClickableMenu is not ItemGrabMenu now) yield break;
            var list = now.ItemsToGrabMenu.actualInventory;
            var item = list.FirstOrDefault(x => x != null);
            if (item == null || !P.couldInventoryAcceptThisItem(item)) yield break;
            int slot = IndexOf(list, item);
            var cc = now.ItemsToGrabMenu.inventory.FirstOrDefault(s => s.name == slot.ToString());
            if (cc == null) yield break;
            Click(now, cc);
            yield return Ticks(3);
        }
    }

    // ------------------------------------------------------------------ museum

    private static IEnumerator MuseumRun(JsonElement a, CallContext c)
    {
        var action = S(a, "action").Trim().ToLowerInvariant();
        if (action.Length == 0) action = "list";
        switch (action)
        {
            case "list":
                var museum = Game1.getLocationFromName("ArchaeologyHouse") as LibraryMuseum;
                if (museum == null) { c.Fail("The Museum (ArchaeologyHouse) is not available."); yield break; }
                c.Result = MuseumListText(museum);
                break;
            case "donate":
                yield return MuseumDonate(a, c);
                break;
            case "rewards":
                yield return MuseumRewards(c);
                break;
            default:
                c.Fail($"Unknown action \"{action}\". Use list, donate or rewards.");
                break;
        }
    }

    /// <summary>LibraryMuseum.isItemSuitableForDonation: true for an artifact or mineral that is not on display yet.</summary>
    private static bool MuseumAccepts(LibraryMuseum museum, Item item)
    {
        // Looked up by name: depending on the game version it takes the Item or its item id.
        foreach (object arg in new object[] { item, item.QualifiedItemId })
            if (LateBound.TryCall(museum, museum.GetType(), "isItemSuitableForDonation", out var r, arg) && r is bool accepted)
                return accepted;
        throw new LateBoundException("LibraryMuseum.isItemSuitableForDonation takes neither an Item nor an item id in this game version.");
    }

    /// <summary>Rewards Gunther holds for this player (LibraryMuseum.getRewardsForPlayer), or -1 when that can't be asked.</summary>
    private static int MuseumRewardCount(LibraryMuseum museum)
    {
        try
        {
            if (LateBound.TryCall(museum, museum.GetType(), "getRewardsForPlayer", out var r, P) && r is ICollection list)
                return list.Count;
        }
        catch (LateBoundException) { }
        return -1;
    }

    private static string MuseumListText(LibraryMuseum museum)
    {
        var sb = new StringBuilder();
        var rows = new List<string>();
        for (int i = 0; i < P.Items.Count; i++)
        {
            var it = P.Items[i];
            if (it is SObject && MuseumAccepts(museum, it))
                rows.Add($"{it.DisplayName} x{it.Stack} (slot {i})");
        }
        sb.Append(rows.Count == 0
            ? "The Museum accepts none of the items you carry. It takes one of each artifact and mineral that is not on display yet."
            : $"The Museum accepts: {string.Join(", ", rows)}. It keeps one of each; donate with museum(action=donate, item=...) inside the Museum (travel_to ArchaeologyHouse).");
        int rewards = MuseumRewardCount(museum);
        if (rewards > 0) sb.Append($"\nGunther has {rewards} reward(s) waiting for you: museum(action=rewards) inside the Museum.");
        return sb.ToString();
    }

    /// <summary>
    /// Puts items on the next free display spot, the way MuseumMenu does (museumPieces gets the item id, the stack goes down by one).
    /// Left out on purpose: MuseumMenu's UI, its global chat line and its sounds beyond one.
    /// </summary>
    private static IEnumerator MuseumDonate(JsonElement a, CallContext c)
    {
        if (Loc is not LibraryMuseum museum)
        {
            c.Fail($"Donate inside the Museum; you are in {Loc.Name}. Use travel_to ArchaeologyHouse first.");
            yield break;
        }
        if (Game1.activeClickableMenu != null || Game1.eventUp) { c.Fail("Close the open menu or finish the scene first."); yield break; }
        var want = S(a, "item").Trim();
        if (want.Length == 0) { c.Fail("item is required: an inventory item name, or \"all\"."); yield break; }

        var items = new List<Item>();
        if (want.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var carried in P.Items)
                if (carried is SObject && MuseumAccepts(museum, carried)) items.Add(carried);
            if (items.Count == 0) { c.Fail("The Museum accepts none of the items you carry (museum action=list)."); yield break; }
        }
        else
        {
            int slot = FindItem(want, x => x is SObject);
            if (slot < 0) { c.Fail($"No \"{want}\" in your inventory."); yield break; }
            var named = P.Items[slot];
            if (!MuseumAccepts(museum, named))
            {
                c.Fail($"The Museum does not need {named.DisplayName}: it only takes artifacts and minerals, one of each, that are not on display yet.");
                yield break;
            }
            items.Add(named);
        }

        var pieces = LateBound.Require(museum, "museumPieces");
        if (pieces == null) { c.Fail("The Museum has no display list in this game version."); yield break; }
        int rewardsBefore = MuseumRewardCount(museum);
        var donated = new List<string>();
        string? stopped = null;
        foreach (var it in items)
        {
            if (!MuseumAccepts(museum, it)) continue; // an earlier piece of the same kind went up
            var spot = FreeMuseumSpot(museum, pieces);
            if (spot == null) { stopped = "The Museum has no free display spot left."; break; }
            var name = it.DisplayName;
            LateBound.Call(pieces, "Add", spot.Value, it.ItemId);
            it.Stack--;
            if (it.Stack <= 0) P.removeItemFromInventory(it);
            Game1.playSound("stoneStep");
            donated.Add(name);
            Rt.Log.Add("museum_donated", text: $"{P.Name} donated {name} to the Museum",
                args: new { player = Rt.Log.Player, item = name, x = (int)spot.Value.X, y = (int)spot.Value.Y });
            yield return Ticks(8);
        }
        if (donated.Count == 0) { c.Fail(stopped ?? "Nothing was donated."); yield break; }

        TryCheckArchaeologyAchievements();
        var text = $"Donated to the Museum: {string.Join(", ", donated)}.";
        if (stopped != null) text += " " + stopped;
        int rewardsAfter = MuseumRewardCount(museum);
        if (rewardsAfter > 0 && rewardsAfter > rewardsBefore) text += $" Gunther has {rewardsAfter} reward(s) for you now: museum(action=rewards).";
        c.Result = text;
    }

    /// <summary>The first display tile (row by row) that LibraryMuseum.isTileSuitableForMuseumPiece accepts and nothing stands on yet.</summary>
    private static Vector2? FreeMuseumSpot(LibraryMuseum museum, object pieces)
    {
        var layer = museum.Map.Layers[0];
        for (int y = 0; y < layer.LayerHeight; y++)
            for (int x = 0; x < layer.LayerWidth; x++)
            {
                if (LateBound.Call(museum, "isTileSuitableForMuseumPiece", x, y) is not true) continue;
                var v = new Vector2(x, y);
                if (LateBound.Call(pieces, "ContainsKey", v) is not true) return v;
            }
        return null;
    }

    /// <summary>MuseumMenu asks the stats to check the archaeology achievements after each piece; best effort.</summary>
    private static void TryCheckArchaeologyAchievements()
    {
        try { LateBound.TryCall(Game1.stats, Game1.stats.GetType(), "checkForArchaeologyAchievements", out _); }
        catch (LateBoundException) { }
    }

    /// <summary>Gunther's "Collect" answer opens a grab menu with the earned rewards; take them all.</summary>
    private static IEnumerator MuseumRewards(CallContext c)
    {
        if (Loc is not LibraryMuseum museum)
        {
            c.Fail($"Gunther is in the Museum; you are in {Loc.Name}. Use travel_to ArchaeologyHouse first.");
            yield break;
        }
        if (Game1.activeClickableMenu != null || Game1.eventUp) { c.Fail("Close the open menu or finish the scene first."); yield break; }
        if (MuseumRewardCount(museum) == 0)
        {
            c.Fail("Gunther has no reward for you yet. Rewards come at donation milestones; museum(action=list) shows what you can donate.");
            yield break;
        }

        // Gunther answers at his desk: an Action tile that names him, else his own tile.
        Point? at = null;
        var layer = museum.Map.Layers[0];
        for (int y = 0; y < layer.LayerHeight && at == null; y++)
            for (int x = 0; x < layer.LayerWidth; x++)
                if (museum.doesTileHaveProperty(x, y, "Action", "Buildings") is { } act && act.Contains("Gunther", StringComparison.OrdinalIgnoreCase))
                {
                    at = new Point(x, y);
                    break;
                }
        if (at == null && museum.characters.FirstOrDefault(n => n.Name == "Gunther") is { } gunther) at = gunther.TilePoint;
        if (at == null) { c.Fail("Gunther is not in the Museum right now (it may be closed)."); yield break; }

        var before = Snapshot();
        yield return WalkAdjacent(at.Value, c);
        if (!c.Ok) yield break;
        PressAction(at.Value);
        for (int i = 0; i < 90 && Game1.activeClickableMenu is not DialogueBox; i++) yield return null;
        yield return Ticks(15);

        var box = Game1.activeClickableMenu as DialogueBox;
        if (box == null || !box.isQuestion || box.responses == null)
        {
            var said = box != null ? box.getCurrentString() : "nothing opened";
            c.Fail($"Gunther did not offer a choice ({said}). Stand next to his desk and use interact on it, then menu/respond.");
            yield break;
        }
        int option = Array.FindIndex(box.responses, r => r.responseKey.Equals("Collect", StringComparison.OrdinalIgnoreCase)
                                                        || r.responseText.Contains("collect", StringComparison.OrdinalIgnoreCase));
        if (option < 0)
        {
            box.closeDialogue();
            c.Fail("Gunther did not offer to hand out rewards (none waiting).");
            yield break;
        }

        using (var doc = JsonDocument.Parse($"{{\"option\":{option}}}"))
        {
            var pickOption = doc.RootElement.Clone();
            yield return Respond(pickOption, c);
        }
        if (!c.Ok) yield break;
        for (int i = 0; i < 90 && Game1.activeClickableMenu is not ItemGrabMenu; i++) yield return null;
        if (Game1.activeClickableMenu is ItemGrabMenu)
        {
            yield return CommunityTakeAll();
            if (Game1.activeClickableMenu is ItemGrabMenu g && g.ItemsToGrabMenu.actualInventory.All(i => i == null))
            {
                g.exitThisMenu();
                yield return Ticks(10);
            }
        }

        var diff = Diff(before);
        if (diff.Length == 0)
        {
            c.Fail("No reward reached your inventory." + (Game1.activeClickableMenu is ItemGrabMenu ? " The reward menu is still open: free inventory space, then menu(action=take) or close_menu." : ""));
            yield break;
        }
        var leftover = Game1.activeClickableMenu is ItemGrabMenu ? " Some rewards are still in the open menu (inventory full?): menu(action=take) after freeing space." : "";
        Rt.Log.Add("museum_rewards", text: $"{P.Name} collected the Museum rewards", args: new { player = Rt.Log.Player });
        c.Result = $"Collected the Museum rewards from Gunther. {diff}{leftover}".Trim();
    }
}
