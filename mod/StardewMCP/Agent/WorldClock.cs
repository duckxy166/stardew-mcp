using System;
using System.Diagnostics;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Freezes the world whenever an AI is thinking, so thinking speed never costs game time.
/// An AI seat is "thinking" when its MCP client has connected and it has no action running.
/// In split-screen the whole world stops (host pause: clock, farmers, NPCs); in a single-player
/// run only the clock stops, as before. Runs on the host screen's tick.
/// </summary>
public static class WorldClock
{
    private static ModConfig _config = null!;
    private static readonly Stopwatch[] _thinking = Enumerable.Range(0, Players.Seats).Select(_ => new Stopwatch()).ToArray();
    private static bool _clockFrozen;
    private static int _frozenInterval;
    private static bool _weSetPause;

    /// <summary>True while the world (or, single-player, the clock) is frozen.</summary>
    public static bool Frozen { get; private set; }

    /// <summary>True while the host pause is holding every screen still.</summary>
    public static bool WorldPaused => Context.IsWorldReady && Game1.HostPaused;

    /// <summary>Seats currently holding the world frozen.</summary>
    public static string[] ThinkingSeats { get; private set; } = Array.Empty<string>();

    public static void Init(ModConfig config)
    {
        _config = config;
        RunControl.Init(config);
    }

    /// <summary>Seconds this seat has been thinking, or null when it isn't.</summary>
    public static double? ThinkingSeconds(PlayerSlot slot) =>
        _thinking[slot.Screen].IsRunning ? Math.Round(_thinking[slot.Screen].Elapsed.TotalSeconds, 1) : null;

    /// <summary>Called on the host screen's tick, after the host runtime ticked.</summary>
    public static void Tick()
    {
        RunControl.Tick();
        bool split = Players.SplitScreen;
        var thinking = Players.Slots.Where(s => IsThinking(s, split)).ToArray();
        ThinkingSeats = thinking.Select(s => s.Id).ToArray();

        bool freeze = _config.FreezeTimeWhileThinking && Context.IsWorldReady && thinking.Length > 0 &&
                      !Game1.newDay && !NightMenuOpen();
        // Paused / stopped by the operator: let the running actions finish, then stand still whoever is thinking.
        if (RunControl.Holding)
            freeze = Context.IsWorldReady && !Game1.newDay && !NightMenuOpen() && !RunControl.AnyActionRunning;

        if (split)
        {
            ReleaseClock();
            SetPause(freeze);
        }
        else
        {
            SetPause(false);
            if (freeze) HoldClock(); else ReleaseClock();
        }
        Frozen = freeze;
    }

    private static bool IsThinking(PlayerSlot slot, bool split)
    {
        var watch = _thinking[slot.Screen];
        bool candidate = slot.Role == SeatRole.Ai && !slot.Runtime.ActionRunning &&
                         (split ? slot.Connected && slot.Active : slot.Screen == 0);
        if (!candidate || slot.TimedOut)
        {
            watch.Reset();
            return false;
        }

        if (RunControl.Holding)
        {
            // Held by the operator: that time is not the AI's thinking time, so it can't run into the timeout.
            watch.Stop();
            return true;
        }

        watch.Start();
        int limit = _config.ThinkTimeoutSeconds;
        if (split && limit > 0 && watch.Elapsed.TotalSeconds > limit)
        {
            // A stuck or crashed AI must not hold the other two forever; its farmer just stands there.
            slot.TimedOut = true;
            watch.Reset();
            slot.Runtime.Log.Add("system", text: $"{slot.Id} thought for more than {limit}s: no longer freezing the world until it acts again");
            return false;
        }
        return true;
    }

    /// <summary>The overnight menus need the world running to finish.</summary>
    private static bool NightMenuOpen() =>
        Players.Slots.Any(s => s.State is ScreenState { Menu: nameof(ShippingMenu) or nameof(LevelUpMenu) });

    private static void SetPause(bool on)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;
        var world = Game1.netWorldState.Value;
        if (on && !world.IsPaused)
        {
            world.IsPaused = true;
            _weSetPause = true;
        }
        else if (!on && world.IsPaused && _weSetPause)
        {
            world.IsPaused = false;
            _weSetPause = false;
        }
    }

    private static void HoldClock()
    {
        if (!_clockFrozen)
        {
            _clockFrozen = true;
            _frozenInterval = Math.Min(Game1.gameTimeInterval, Game1.realMilliSecondsPerGameTenMinutes - 200);
        }
        Game1.gameTimeInterval = _frozenInterval;
    }

    private static void ReleaseClock() => _clockFrozen = false;
}
