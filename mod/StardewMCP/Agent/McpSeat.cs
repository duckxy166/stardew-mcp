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
}
