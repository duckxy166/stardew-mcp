using System;
using System.Diagnostics;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>What a split-screen seat is used for.</summary>
public enum SeatRole
{
    /// <summary>A farmer driven by an AI through <c>/mcp/p{N}</c>.</summary>
    Ai,
    /// <summary>The video's camera / summary screen: no AI, never blocks the world.</summary>
    Camera
}

/// <summary>One split-screen seat (screen index = SMAPI <c>Context.ScreenId</c>) and its agent runtime.</summary>
public class PlayerSlot
{
    public int Screen { get; }
    public string Id => $"p{Screen + 1}";
    public SeatRole Role { get; }
    public AgentRuntime Runtime { get; }

    /// <summary>Game ticks seen on this screen; the HTTP thread uses it to tell whether the screen exists.</summary>
    public long LastTick;
    private readonly Stopwatch _sinceTick = new();

    /// <summary>True once an MCP client has talked to this seat; only connected AIs can hold the world frozen.</summary>
    public volatile bool Connected;

    /// <summary>Set by <see cref="WorldClock"/> when this AI thought longer than the configured timeout.</summary>
    public volatile bool TimedOut;

    /// <summary>Latest per-screen state for /api/state, written on this screen's tick.</summary>
    public volatile object? State;

    public PlayerSlot(int screen, SeatRole role, AgentRuntime runtime)
    {
        Screen = screen;
        Role = role;
        Runtime = runtime;
    }

    /// <summary>This screen ticked within the last two seconds (it exists and is in the game loop).</summary>
    public bool Active => _sinceTick.IsRunning && _sinceTick.ElapsedMilliseconds < 2000;

    public void OnTick()
    {
        LastTick++;
        _sinceTick.Restart();
    }

    private volatile int _releasedDay;

    /// <summary>
    /// True while the runner has released this seat's freeze (POST /api/release_freeze): the seat no longer holds the
    /// world still, so game time runs on until the farmer passes out (#22). The release belongs to one farm day and
    /// ends by itself when the day changes; MCP traffic from the seat also ends it.
    /// </summary>
    public bool Released => _releasedDay > 0 && _releasedDay == AgentLog.FarmDay;

    /// <summary>Release (or take back the release of) this seat's freeze for the current farm day. Needs a loaded world.</summary>
    public void SetReleased(bool on) => _releasedDay = on ? AgentLog.FarmDay : 0;

    /// <summary>Called when a request for this seat's AI arrives.</summary>
    public void MarkContact()
    {
        Connected = true;
        TimedOut = false;
        _releasedDay = 0;
    }
}

/// <summary>What one screen looks like right now, captured on that screen's own tick.</summary>
public class ScreenState
{
    public bool Ready { get; init; }
    public string? Name { get; init; }
    public string? Location { get; init; }
    public int[]? Tile { get; init; }
    public int Money { get; init; }
    public int Energy { get; init; }
    public int MaxEnergy { get; init; }
    public string? Menu { get; init; }
    public string? Dialogue { get; init; }

    /// <summary>Must run on the game thread during that screen's update.</summary>
    public static ScreenState Capture()
    {
        var menu = Game1.activeClickableMenu;
        if (!Context.IsWorldReady)
            return new ScreenState { Ready = false, Menu = menu?.GetType().Name };
        var p = Game1.player;
        return new ScreenState
        {
            Ready = true,
            Name = p.Name,
            Location = Game1.currentLocation?.Name,
            Tile = new[] { p.TilePoint.X, p.TilePoint.Y },
            Money = p.Money,
            Energy = (int)p.Stamina,
            MaxEnergy = p.MaxStamina,
            Menu = menu?.GetType().Name,
            Dialogue = menu is StardewValley.Menus.DialogueBox box ? box.characterDialogue?.speaker?.displayName ?? "" : null
        };
    }
}

/// <summary>The four split-screen seats: P1 host, P2/P3 farmhands (AIs), P4 camera.</summary>
public static class Players
{
    public const int Seats = 4;

    public static PlayerSlot[] Slots { get; private set; } = Array.Empty<PlayerSlot>();

    public static void Init(IModHelper helper, IMonitor monitor, ModConfig config, AgentLog log)
    {
        Slots = Enumerable.Range(0, Seats)
            .Select(i =>
            {
                var role = i == Seats - 1 ? SeatRole.Camera : SeatRole.Ai;
                return new PlayerSlot(i, role, new AgentRuntime(helper, monitor, config, log.For($"p{i + 1}")));
            })
            .ToArray();
    }

    /// <summary>The seat of the screen being updated. Only valid on the game thread.</summary>
    public static PlayerSlot Current => Slots[Math.Clamp(Context.ScreenId, 0, Seats - 1)];

    public static PlayerSlot Host => Slots[0];

    /// <summary>Seat by id ("p1".."p4"), or null.</summary>
    public static PlayerSlot? ById(string id) => Slots.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True while the game runs more than one local screen.</summary>
    public static bool SplitScreen => GameRunner.instance?.gameInstances.Count > 1;
}
