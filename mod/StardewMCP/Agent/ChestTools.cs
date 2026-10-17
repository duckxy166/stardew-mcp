using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Tools;

namespace StardewMCP.Agent;

/// <summary>
/// chest: look into, fill or empty a storage chest (or the fridge) in one call. It is a macro over the primitives:
/// interact opens the chest (walking next to it), then the menu tool's own take / put clicks move the items, then the
/// menu is closed. Nothing here writes to Chest.Items directly, so whatever hooks the primitives (ownership, theft
/// events) sees these moves too.
/// </summary>
public static partial class AgentTools
{
    private static ToolDef ChestToolDef() => new()
    {
        Name = "chest",
        Description = "Look into, fill or empty a storage chest or the fridge in one call: you walk next to it, open it, move the items and close it again. " +
                      "action: list (default), store (your pack into the chest) or take (chest into your pack). " +
                      "item: one item name, or \"everything\" (store: all except tools, seeds and cooked dishes, so tidying the pack is one call; take: all). " +
                      "items: several names at once. quality: normal, silver, gold or iridium to move only that quality (default every quality). " +
                      "quantity: how many of each named item (default all). The result says what moved, per quality, and what did not fit. " +
                      "x, y is the chest tile; leave it out to use a chest menu you already opened with interact.",
        Schema = Obj(new
        {
            x = Int("Chest tile x"),
            y = Int("Chest tile y"),
            action = new { type = "string", @enum = new[] { "list", "store", "take" }, description = "list (default), store or take" },
            item = Str("Item name, or \"everything\""),
            items = new { type = "array", items = new { type = "string" }, description = "Several item names" },
            quality = new { type = "string", @enum = new[] { "normal", "silver", "gold", "iridium" }, description = "Only this quality (default: all)" },
            quantity = Int("How many of each named item (default: all)"),
            reason = Str(ReasonDesc)
        }, "reason"),
        Run = ChestTool
    };

    // ------------------------------------------------------------------ quality

    internal static string QualityName(int quality) => quality switch
    {
        0 => "normal",
        1 => "silver",
        2 => "gold",
        4 => "iridium",
        _ => $"quality {quality}"
    };

    private static int? QualityValue(string text) => text.Trim().ToLowerInvariant() switch
    {
        "normal" or "basic" or "regular" or "0" => 0,
        "silver" or "1" => 1,
        "gold" or "2" => 2,
        "iridium" or "4" => 4,
        _ => null
    };

    /// <summary>
    /// Filter for the optional "quality" argument of menu take / put and chest. Null when the argument is absent;
    /// an unknown quality name matches nothing instead of silently matching everything.
    /// </summary>
    internal static Func<Item, bool>? QualityIs(JsonElement a)
    {
        var text = S(a, "quality").Trim();
        if (text.Length == 0) return null;
        var wanted = QualityValue(text);
        return it => wanted != null && it.Quality == wanted;
    }

    // ------------------------------------------------------------------ small helpers

    private static JsonElement ArgsOf(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone();

    private static List<string> StrList(JsonElement a, string name)
    {
        var list = new List<string>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                    list.Add(e.GetString()!.Trim());
        return list;
    }

    private static bool IsAll(string name) =>
        name.Equals("everything", StringComparison.OrdinalIgnoreCase) || name.Equals("all", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Parsnip (gold)"; tools have no quality.</summary>
    private static string StackLabel(Item it) => it is Tool ? it.DisplayName : $"{it.DisplayName} ({QualityName(it.Quality)})";

    private static string Stacks(IEnumerable<Item> items)
    {
        var parts = items.Where(i => i != null)
            .GroupBy(i => (i.QualifiedItemId, i.Quality))
            .Select(g => (label: StackLabel(g.First()), count: g.Sum(i => i.Stack)))
            .OrderBy(x => x.label)
            .Select(x => $"{x.count}x {x.label}")
            .ToList();
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>"12x Parsnip (normal), 3x Parsnip (gold)": counts per label, in the order first seen.</summary>
    private static string Tally(List<(string label, int count)> parts) =>
        string.Join(", ", parts.GroupBy(p => p.label).Select(g => $"{g.Sum(p => p.count)}x {g.Key}"));

    private static int CountOf(IEnumerable<Item?> items, string qid, int quality) =>
        items.Where(i => i != null && i.QualifiedItemId == qid && i.Quality == quality).Sum(i => i!.Stack);

    private static string ChestLabel(ItemGrabMenu g) => g.context switch
    {
        Chest { fridge.Value: true } => "Fridge",
        Chest ch => ch.DisplayName,
        _ => "Chest"
    };

    /// <summary>What the "everything" mode of store leaves in the pack: tools, seeds (category -74) and cooked dishes (category -7).</summary>
    private static bool KeepOutOfChest(Item it) => it is Tool || it.Category == -74 || it.Category == -7;

    // ------------------------------------------------------------------ the tool

    private static IEnumerator ChestTool(JsonElement a, CallContext c)
    {
        var action = S(a, "action", "list").Trim().ToLowerInvariant();
        if (action.Length == 0) action = "list";
        if (action != "list" && action != "store" && action != "take")
        {
            c.Fail($"Unknown action \"{action}\". Use list, store or take.");
            yield break;
        }

        int? quality = null;
        var qText = S(a, "quality").Trim();
        if (qText.Length > 0)
        {
            quality = QualityValue(qText);
            if (quality == null) { c.Fail($"Unknown quality \"{qText}\". Use normal, silver, gold or iridium."); yield break; }
        }
        int? quantity = Has(a, "quantity") ? Math.Max(1, I(a, "quantity", 1)) : null;

        var names = StrList(a, "items");
        var single = S(a, "item").Trim();
        if (single.Length > 0) names.Add(single);
        bool everything = names.Any(IsAll);
        names.RemoveAll(IsAll);
        if (action != "list" && !everything && names.Count == 0)
        {
            c.Fail($"Say what to {action}: item, items, or \"everything\".");
            yield break;
        }

        bool opened = false;
        var spot = "";
        if (Has(a, "x") && Has(a, "y"))
        {
            if (Game1.activeClickableMenu != null)
            {
                c.Fail("A menu is open. close_menu first, or leave out x and y to use the chest menu that is open.");
                yield break;
            }
            var tile = new Point(I(a, "x", -1), I(a, "y", -1));
            // interact walks next to the tile and presses the action button; it is the same path a player's hands take.
            var sub = new CallContext();
            yield return Interact(ArgsOf(new { x = tile.X, y = tile.Y, reason = Reason(a) }), sub);
            if (!sub.Ok) { c.Fail(sub.Result); yield break; }
            for (int i = 0; i < 120 && Game1.activeClickableMenu == null; i++) yield return null;
            opened = true;
            spot = $" at ({tile.X},{tile.Y})";
            if (Game1.activeClickableMenu is not ItemGrabMenu { context: Chest })
            {
                var problem = Game1.activeClickableMenu == null
                    ? $"Nothing opened at ({tile.X},{tile.Y}). It is not a chest or the fridge, or another player is using it."
                    : $"({tile.X},{tile.Y}) opened {MenuName(Game1.activeClickableMenu)}, which is not a chest. Use menu() for it.";
                if (Game1.activeClickableMenu != null) yield return CloseMenu(default, new CallContext());
                c.Fail(problem);
                yield break;
            }
        }
        else if (Game1.activeClickableMenu is not ItemGrabMenu { context: Chest })
        {
            c.Fail("Give the chest's tile (x, y), or open a chest with interact first.");
            yield break;
        }

        yield return ChestWork(action, names, everything, quality, quantity, spot, c);
        if (opened && Game1.activeClickableMenu != null)
            yield return CloseMenu(default, new CallContext());
    }

    /// <summary>The work with the chest menu open. Sets <paramref name="c"/>; the caller closes the menu.</summary>
    private static IEnumerator ChestWork(string action, List<string> names, bool everything, int? quality, int? quantity, string spot, CallContext c)
    {
        var menu = (ItemGrabMenu)Game1.activeClickableMenu!;
        var label = ChestLabel(menu);
        var problems = new List<string>();

        if (action == "list")
        {
            var inside = menu.ItemsToGrabMenu.actualInventory;
            var shown = PlanMoves(inside, names, false, quality, null, null, false, problems).Select(j => j.item);
            bool filtered = names.Count > 0 || quality != null;
            int used = inside.Count(i => i != null);
            c.Result = $"{label}{spot}: {used}/{menu.ItemsToGrabMenu.capacity} slots used. {(filtered ? "Matching" : "Inside")}: {Stacks(shown)}." +
                       (problems.Count > 0 ? " " + string.Join(" ", problems) : "");
            yield break;
        }

        bool store = action == "store";
        if (store && menu.behaviorFunction == null) { c.Fail($"Nothing can be put into {label}."); yield break; }
        if (!store && !menu.showReceivingMenu) { c.Fail($"Nothing can be taken from {label}."); yield break; }

        IList<Item> source = menu.ItemsToGrabMenu.actualInventory;
        Func<Item, bool>? allowed = null;
        if (store)
        {
            source = P.Items;
            allowed = it => menu.inventory.highlightMethod(it);
        }
        var jobs = PlanMoves(source, names, everything, quality, quantity, allowed, store, problems);

        var moved = new List<(string label, int count)>();
        var left = new List<(string label, int count)>();
        string? firstFail = null;
        bool blocked = false;
        foreach (var (item, count) in jobs)
        {
            var itemLabel = StackLabel(item);
            if (blocked) { left.Add((itemLabel, count)); continue; }
            if (Game1.activeClickableMenu is not ItemGrabMenu cur || cur.context is not Chest)
            {
                problems.Add("The chest menu closed before everything was moved.");
                left.Add((itemLabel, count));
                blocked = true;
                continue;
            }
            // The game would only play a "cancel" sound; stop early and say why instead.
            if (!store && !P.couldInventoryAcceptThisItem(item.getOne()))
            {
                left.Add((itemLabel, count));
                blocked = true;
                continue;
            }

            string qid = item.QualifiedItemId;
            int q = item.Quality;
            int had = CountOf(P.Items, qid, q);
            // The menu tool's own take / put, picking the stack by name and quality.
            var args = ArgsOf(new { item = item.DisplayName, quality = QualityName(q), count });
            var sub = new CallContext();
            yield return store ? Put(cur, args, sub) : Take(cur, args, sub);
            int now = CountOf(P.Items, qid, q);
            int delta = store ? had - now : now - had;
            if (delta > 0) moved.Add((itemLabel, delta));
            if (delta < count)
            {
                left.Add((itemLabel, count - delta));
                if (!sub.Ok) firstFail ??= sub.Result.Split('\n')[0];
            }
        }
        yield return Ticks(5);

        var sb = new StringBuilder();
        if (moved.Count > 0)
            sb.Append(store ? $"Stored {Tally(moved)} in {label}{spot}." : $"Took {Tally(moved)} from {label}{spot}.");
        if (left.Count > 0)
            sb.Append($" Did not move ({(store ? "the chest is full or does not take it" : "your inventory is full")}): {Tally(left)}.");
        foreach (var p in problems) sb.Append(' ').Append(p);
        if (firstFail != null && left.Count > 0) sb.Append($" ({firstFail})");
        if (Game1.activeClickableMenu is ItemGrabMenu end && end.context is Chest)
            sb.Append($" {label} now {end.ItemsToGrabMenu.actualInventory.Count(i => i != null)}/{end.ItemsToGrabMenu.capacity} slots used.");

        if (moved.Count == 0) c.Fail(("Nothing " + (store ? "stored." : "taken.") + " " + sb).Trim());
        else c.Result = sb.ToString().Trim();
    }

    // ------------------------------------------------------------------ planning

    /// <summary>
    /// Which stacks of <paramref name="source"/> to move and how many of each. With <paramref name="everything"/> or no
    /// names that is every stack (minus the keepers when <paramref name="skipKeepers"/>); otherwise the stacks of each
    /// named item, limited by quality and by <paramref name="quantity"/> per name. What went wrong is added to <paramref name="problems"/>.
    /// </summary>
    private static List<(Item item, int count)> PlanMoves(IList<Item> source, List<string> names, bool everything, int? quality, int? quantity,
        Func<Item, bool>? allowed, bool skipKeepers, List<string> problems)
    {
        var jobs = new List<(Item item, int count)>();
        var live = source.Where(i => i != null).ToList();
        bool Fits(Item it) => quality == null || it.Quality == quality;

        if (everything || names.Count == 0)
        {
            foreach (var it in live)
            {
                if (!Fits(it) || (skipKeepers && KeepOutOfChest(it)) || (allowed != null && !allowed(it))) continue;
                jobs.Add((it, it.Stack));
            }
            return jobs;
        }

        foreach (var name in names)
        {
            var matches = MatchByName(live, name, problems);
            if (matches == null) continue;
            var fit = matches.Where(Fits).ToList();
            if (fit.Count == 0)
            {
                problems.Add(quality == null ? $"No \"{name}\" found." : $"No {QualityName(quality.Value)} \"{name}\" found.");
                continue;
            }
            int remaining = quantity ?? int.MaxValue;
            foreach (var it in fit)
            {
                if (remaining <= 0) break;
                if (jobs.Any(j => ReferenceEquals(j.item, it))) continue;
                if (allowed != null && !allowed(it)) { problems.Add($"{StackLabel(it)} can't go into this chest."); continue; }
                int n = Math.Min(it.Stack, remaining);
                jobs.Add((it, n));
                remaining -= n;
            }
        }
        return jobs;
    }

    /// <summary>Stacks called <paramref name="name"/>: exact name first, else a partial match that must be one item.</summary>
    private static List<Item>? MatchByName(List<Item> live, string name, List<string> problems)
    {
        var exact = live.Where(i => i.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) || i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        var part = live.Where(i => i.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        var kinds = part.Select(i => i.DisplayName).Distinct().ToList();
        if (kinds.Count > 1)
        {
            problems.Add($"\"{name}\" matches several items ({string.Join(", ", kinds.Take(6))}); name one exactly.");
            return null;
        }
        if (part.Count == 0)
        {
            problems.Add($"No \"{name}\" found.");
            return null;
        }
        return part;
    }
}
