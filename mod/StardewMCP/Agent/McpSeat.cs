using System.Text.Json;
using System.Threading.Tasks;

namespace StardewMCP.Agent;

/// <summary>A split-screen seat seen through <see cref="IMcpSeat"/>, so <see cref="McpDispatcher"/> never touches the runtime.</summary>
internal sealed class McpSeat : IMcpSeat
{
    private readonly PlayerSlot _slot;

    public McpSeat(PlayerSlot slot) => _slot = slot;

    public string Id => _slot.Id;

    public bool Active => _slot.Active;

    public void MarkContact() => _slot.MarkContact();

    public Task<(bool ok, string text)> CallTool(ToolDef tool, JsonElement args) =>
        _slot.Runtime.Enqueue(tool, args, observation: true);

    public async Task<object> CallToolReply(ToolDef tool, JsonElement args)
    {
        var call = _slot.Runtime.EnqueueCall(tool, args, observation: true);
        var (ok, text) = await call.Done.Task;
        return ToolResult.McpReply(ok, text, call.Outcome);
    }

    public object ErrorReply(string reason, string text) => ToolResult.McpError(reason, text);
}
