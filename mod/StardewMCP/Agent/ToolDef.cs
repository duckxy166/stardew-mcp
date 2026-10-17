using System;
using System.Collections;
using System.Text.Json;

namespace StardewMCP.Agent;

// Moved here unchanged from AgentTools.cs and AgentRuntime.cs: they use no game types, so the unit
// tests (mod/StardewMCP.Tests) compile this file to build fake tools for the MCP contract tests.

/// <summary>State shared by one running tool call. Reason, Partial and Data are in ToolResult.cs.</summary>
public partial class CallContext
{
    public string Result = "";
    public bool Ok = true;

    public void Fail(string message)
    {
        Ok = false;
        Result = message;
    }
}

public delegate IEnumerator ToolRunner(JsonElement args, CallContext ctx);

public class ToolDef
{
    public string Name = null!;
    public string Description = null!;
    public object Schema = null!;
    public ToolRunner Run = null!;
    /// <summary>Instant tools (observations, thinking) do not unfreeze game time.</summary>
    public bool Instant;
    /// <summary>Instant for these arguments only, e.g. menu() reads while menu(action) acts.</summary>
    public Func<JsonElement, bool>? InstantFor;
    public bool NeedsWorld = true;
    public bool AppendStatus = true;
    /// <summary>Chat and whispers waiting for the player are appended to this tool's result (false for debug ops).</summary>
    public bool Mail = true;
}
