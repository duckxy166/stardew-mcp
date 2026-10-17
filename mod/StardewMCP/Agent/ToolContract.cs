using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StardewMCP.Agent;

/// <summary>
/// Rules every tool in <c>tools/list</c> has to follow, checked on the serialized JSON Schema the AI
/// clients receive. AgentHttp logs the findings at startup, and the unit tests run the same checks
/// (see mod/StardewMCP.Tests). Uses no game types.
/// </summary>
public static class ToolContract
{
    /// <summary>MCP clients accept [a-zA-Z0-9_-]{1,64}; our tools are snake_case.</summary>
    private static readonly Regex NameRule = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);

    /// <summary>The JSON Schema types the tool schemas use.</summary>
    private static readonly string[] SchemaTypes = { "string", "integer", "number", "boolean", "array", "object" };

    /// <summary>Problems in the tool list as one line each; empty when it is fine.</summary>
    public static List<string> Check(IEnumerable<ToolDef> tools)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            var name = tool.Name ?? "";
            if (!NameRule.IsMatch(name))
                problems.Add($"tool name \"{name}\" must be snake_case, 1-64 characters");
            else if (!seen.Add(name))
                problems.Add($"{name}: duplicate tool name");
            if (string.IsNullOrWhiteSpace(tool.Description))
                problems.Add($"{name}: description is empty");
            if (tool.Run == null)
                problems.Add($"{name}: no Run coroutine");

            if (tool.Schema == null)
            {
                problems.Add($"{name}: no input schema");
                continue;
            }
            JsonElement schema;
            try { schema = JsonSerializer.SerializeToElement(tool.Schema, tool.Schema.GetType()); }
            catch (Exception ex)
            {
                problems.Add($"{name}: schema can't be turned into JSON ({ex.Message})");
                continue;
            }
            problems.AddRange(CheckSchema(name, schema));

            // Actions show the viewers why the AI does them; only pure observations may skip it.
            if (!tool.Instant && !HasReason(schema))
                problems.Add($"{name}: action tool (not Instant) has no string 'reason' parameter");
        }
        return problems;
    }

    /// <summary>
    /// Structure of one tool's <c>inputSchema</c>: an object schema whose properties have known types,
    /// arrays that say what they hold, and a <c>required</c> list naming only properties that exist.
    /// </summary>
    public static List<string> CheckSchema(string tool, JsonElement schema)
    {
        var problems = new List<string>();
        if (schema.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{tool}: schema is not a JSON object");
            return problems;
        }
        if (!TryString(schema, "type", out var type) || type != "object")
            problems.Add($"{tool}: top-level schema type must be \"object\"");

        JsonElement props = default;
        bool hasProps = schema.TryGetProperty("properties", out props) && props.ValueKind == JsonValueKind.Object;
        if (!hasProps)
            problems.Add($"{tool}: schema has no 'properties' object");
        else
            foreach (var prop in props.EnumerateObject())
                CheckProperty(tool, prop.Name, prop.Value, problems);

        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array)
                problems.Add($"{tool}: 'required' must be an array");
            else
            {
                var names = new HashSet<string>();
                foreach (var r in required.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.String)
                    {
                        problems.Add($"{tool}: 'required' holds a non-string entry");
                        continue;
                    }
                    var n = r.GetString() ?? "";
                    if (!names.Add(n))
                        problems.Add($"{tool}: '{n}' is listed twice in 'required'");
                    if (hasProps && !props.TryGetProperty(n, out _))
                        problems.Add($"{tool}: required '{n}' is not in properties");
                }
            }
        }
        return problems;
    }

    private static void CheckProperty(string tool, string name, JsonElement prop, List<string> problems)
    {
        if (prop.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{tool}.{name}: property schema is not an object");
            return;
        }
        if (!TryString(prop, "type", out var type) || !SchemaTypes.Contains(type))
            problems.Add($"{tool}.{name}: 'type' must be one of {string.Join(", ", SchemaTypes)}");
        if (type == "array" && !(prop.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object))
            problems.Add($"{tool}.{name}: array has no 'items' schema");
        if (prop.TryGetProperty("enum", out var values) && (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0))
            problems.Add($"{tool}.{name}: 'enum' must be a non-empty array");
    }

    private static bool HasReason(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
        && props.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.Object
        && TryString(reason, "type", out var type) && type == "string";

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return false;
        value = v.GetString() ?? "";
        return true;
    }
}
