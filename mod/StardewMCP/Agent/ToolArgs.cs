using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace StardewMCP.Agent;

// Argument and schema helpers for the tools, moved here from AgentTools.cs (Tiles, Has and TileList went
// from private to internal so the tests can call them). They only use System.Text.Json and Point, so the
// unit tests (mod/StardewMCP.Tests) compile this file as is.

public static partial class AgentTools
{
    // ------------------------------------------------------------------ schema helpers

    internal static object Str(string desc) => new { type = "string", description = desc };
    internal static object Int(string desc) => new { type = "integer", description = desc };
    internal static object Tiles(string desc) => new
    {
        type = "array",
        description = desc,
        items = new { type = "array", items = new { type = "integer" }, minItems = 2, maxItems = 2 }
    };
    internal static object Obj(object props, params string[] required) => new { type = "object", properties = props, required };

    // ------------------------------------------------------------------ argument helpers

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

    internal static bool Has(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    internal static List<Point> TileList(JsonElement a, string name)
    {
        var list = new List<Point>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 2)
                    list.Add(new Point(t[0].GetInt32(), t[1].GetInt32()));
        return list;
    }
}
