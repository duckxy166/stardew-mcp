using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace StardewMCP.Agent;

/// <summary>One AI seat as the MCP layer sees it. <c>McpSeat</c> adapts a split-screen seat; the unit tests use a fake.</summary>
public interface IMcpSeat
{
    string Id { get; }

    /// <summary>The seat's screen exists and is in the game loop.</summary>
    bool Active { get; }

    /// <summary>Any MCP traffic means an AI is attached to this seat: from now on its thinking freezes the world.</summary>
    void MarkContact();

    /// <summary>Run a tool for this seat; instant observations may answer during the seat's own action.</summary>
    Task<(bool ok, string text)> CallTool(ToolDef tool, JsonElement args);
}

/// <summary>What to answer one POST with: HTTP status, JSON body (null = empty body) and the Mcp-Session-Id header (null = none).</summary>
public readonly record struct McpReply(int Status, object? Body, string? SessionId);

/// <summary>
/// JSON-RPC handling of the MCP endpoint (initialize, ping, tools/list, tools/call), moved out of
/// AgentHttp so it can run without HTTP, the game or an <c>AgentRuntime</c>.
/// </summary>
public class McpDispatcher
{
    public const string ProtocolFallback = "2025-06-18";

    private readonly IReadOnlyDictionary<string, ToolDef> _tools;
    private readonly bool _splitScreen;

    public McpDispatcher(IReadOnlyDictionary<string, ToolDef> tools, bool splitScreen)
    {
        _tools = tools;
        _splitScreen = splitScreen;
    }

    /// <summary>
    /// One POST body: a single message or a batch (array). Messages without an id are notifications
    /// and get no answer; when nothing is left to answer the reply is 202 with no body.
    /// </summary>
    public async Task<McpReply> Handle(JsonElement root, IMcpSeat seat)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            var outs = new List<object>();
            foreach (var m in root.EnumerateArray())
            {
                var o = await Dispatch(m, seat);
                if (o != null) outs.Add(o);
            }
            return outs.Count == 0 ? new McpReply(202, null, null) : new McpReply(200, outs, null);
        }

        var reply = await Dispatch(root, seat);
        return reply == null ? new McpReply(202, null, null) : new McpReply(200, reply, "stardew-" + seat.Id);
    }

    /// <summary>One JSON-RPC message; null for a notification (no id).</summary>
    public async Task<object?> Dispatch(JsonElement msg, IMcpSeat seat)
    {
        // Any MCP traffic means an AI is attached to this seat: from now on its thinking freezes the world.
        seat.MarkContact();
        var method = msg.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        if (!msg.TryGetProperty("id", out var idEl)) return null; // notification
        object id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : (object)(idEl.GetString() ?? "");
        var p = msg.TryGetProperty("params", out var pe) ? pe : default;

        switch (method)
        {
            case "initialize":
                var ver = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var pv) ? pv.GetString() : ProtocolFallback;
                return Ok(id, new
                {
                    protocolVersion = ver ?? ProtocolFallback,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "stardew", version = "2.0.0" },
                    instructions = _splitScreen
                        ? $"You are player {seat.Id} on a three-player Stardew Valley farm. The whole world is frozen while you or another player is thinking, and only moves while actions run."
                        : "You are playing Stardew Valley. Game time is frozen while you think and only moves while an action runs."
                });
            case "ping":
                return Ok(id, new { });
            case "tools/list":
                return Ok(id, new
                {
                    tools = _tools.Values.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.Schema })
                });
            case "tools/call":
                var name = p.GetProperty("name").GetString() ?? "";
                var args = p.TryGetProperty("arguments", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement;
                if (!_tools.TryGetValue(name, out var tool))
                    return Ok(id, new { content = new[] { new { type = "text", text = $"Unknown tool {name}" } }, isError = true });
                if (!seat.Active)
                    return Ok(id, new { content = new[] { new { type = "text", text = $"Player {seat.Id} is not in the game yet (title screen, loading or still joining). Wait and try again." } }, isError = true });
                var (ok, text) = await seat.CallTool(tool, args);
                return Ok(id, new { content = new[] { new { type = "text", text } }, isError = !ok });
            default:
                return new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Method not found: {method}" } };
        }
    }

    private static object Ok(object id, object result) => new { jsonrpc = "2.0", id, result };
}
