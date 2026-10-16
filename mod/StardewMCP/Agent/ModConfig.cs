namespace StardewMCP.Agent;

/// <summary>User-editable settings (config.json in the mod folder).</summary>
public class ModConfig
{
    /// <summary>Port for the MCP endpoint (/mcp), overlay (/overlay) and replay (/replay).</summary>
    public int HttpPort { get; set; } = 8770;

    /// <summary>Stop the in-game clock while no agent action is running (the AI is thinking).</summary>
    public bool FreezeTimeWhileThinking { get; set; } = true;

    /// <summary>Folder for per-day JSONL logs and diaries. Empty = "logs" inside the mod folder.</summary>
    public string LogDir { get; set; } = "";

    /// <summary>Save folder name (e.g. "Duck_123456789") to load automatically from the title screen. Empty = off.</summary>
    public string AutoLoadSave { get; set; } = "";

    /// <summary>Serve POST /api/debug/* (warp, set_time, add_item...) for runner/smoke.py. Keep off for normal play.</summary>
    public bool EnableDebugApi { get; set; } = false;

    /// <summary>Language the AI is told to use for on-screen text: thoughts, action reasons and the diary (e.g. "English").</summary>
    public string ThoughtLanguage { get; set; } = "Thai";

    /// <summary>
    /// Three-AI split-screen: after the save loads, join P2–P4 as local farmhands (needs 3 free
    /// cabins). P1–P3 are AIs on /mcp/p1../mcp/p3, P4 is the video's camera screen.
    /// </summary>
    public bool SplitScreen { get; set; } = false;

    /// <summary>Farmhand names for P2, P3 and P4. An existing farmhand with the same name is reused.</summary>
    public string[] FarmhandNames { get; set; } = { "Gemini", "GPT", "Camera" };

    /// <summary>Split-screen: an AI that thinks longer than this (seconds) stops holding the world frozen. 0 = never.</summary>
    public int ThinkTimeoutSeconds { get; set; } = 300;

    /// <summary>Split-screen: ignore the real keyboard, mouse and pad on P1 so nobody at the PC moves it or unpauses.</summary>
    public bool LockHostInput { get; set; } = true;

    /// <summary>Split-screen: frozen frames look like live ones (no Paused banner, HUD kept) and hide cursors, the gamepad hint and join messages.</summary>
    public bool CleanFrames { get; set; } = true;

    /// <summary>Split-screen: set each screen's zoom to keep about 20 tiles in view (1.5 × screen width / 960).</summary>
    public bool AutoZoom { get; set; } = true;

    /// <summary>Profession picked when the AI hasn't used choose_profession by bedtime: 0 = left option, 1 = right.</summary>
    public int DefaultProfession { get; set; } = 0;

    /// <summary>
    /// Session breaks (#30): game times (HHMM) at which a seat's AI is told to continue in a new session, so old maps and
    /// results leave its context. Only applies to MCP clients that ask for it with <c>/mcp?breaks=1</c> (runner/run_days.py does).
    /// Empty = no time breaks.
    /// </summary>
    public int[] SessionBreakTimes { get; set; } = { 1200, 1800 };

    /// <summary>Session breaks: also break after this many tool calls in one session. 0 = off.</summary>
    public int SessionBreakToolCalls { get; set; } = 150;

    /// <summary>Session breaks: also break once the results returned in one session add up to this many characters (about 4 per token). 0 = off.</summary>
    public int SessionBreakResultChars { get; set; } = 120000;

    /// <summary>Session breaks apply to every MCP client, not just those that connect with <c>?breaks=1</c>.</summary>
    public bool SessionBreaksForAll { get; set; } = false;
}
