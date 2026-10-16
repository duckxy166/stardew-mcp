using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Run control for unattended batches: pause / resume, "stop after this day" and "stop now".
/// Driven by <c>POST /api/control {"action": ...}</c> (the runner, curl, a stream deck) and by a hotkey
/// (<see cref="ModConfig.PauseKey"/>, F8) that toggles the pause.
///
///   pause           the running action finishes, then every new AI call waits and the world stands still
///   resume          lift the pause (and a stop_now hold)
///   stop_after_day  only a flag: runner/run_days.py reads it between days and stops after go_to_bed
///   stop_now        the running action finishes, new AI calls are refused (so the AI session ends) and the
///                   world stays frozen until resume
///   clear_stop      clear stop_after_day and stop_now (the runner does this when a batch starts)
///
/// While held, <c>frozen</c> in /api/state is true, so the OBS recorder cuts the time and the watchdog
/// must read <c>paused</c> before it judges a stalled clock.
/// </summary>
public static class RunControl
{
    /// <summary>Calls from an AI that waited this long while held are answered with an error (clients time out at 10 min).</summary>
    public const int MaxHeldSeconds = 540;

    public const string StopMessage = "The operator stopped this run. Do not call any more tools; end your session now.";

    private static volatile bool _paused;
    private static volatile bool _stopAfterDay;
    private static volatile bool _stopNow;
    private static bool _keyDown;
    private static Keys _key = Keys.None;
    private static readonly ConcurrentQueue<string> _notes = new();

    public static bool Paused => _paused;
    public static bool StopAfterDay => _stopAfterDay;
    public static bool StopNow => _stopNow;

    /// <summary>New AI calls wait (pause) or are refused (stop_now), and the world is frozen once nothing is running.</summary>
    public static bool Holding => _paused || _stopNow;

    /// <summary>An AI seat has an action running (an action that lets game time pass).</summary>
    public static bool AnyActionRunning => Players.Slots.Any(s => s.Role == SeatRole.Ai && s.Runtime.ActionRunning);

    public static void Init(ModConfig config)
    {
        // An empty or unknown key name turns the hotkey off.
        _key = Enum.TryParse<Keys>(config.PauseKey, true, out var key) ? key : Keys.None;
    }

    /// <summary>Game thread, host screen's tick: the hotkey and the run-log lines queued by the HTTP thread.</summary>
    public static void Tick()
    {
        if (_key != Keys.None)
        {
            // Read the keyboard directly: SMAPI's input events are blank on P1 while LockHostInput is on.
            bool down = Keyboard.GetState().IsKeyDown(_key);
            if (down && !_keyDown)
            {
                if (Holding) Resume("hotkey");
                else Pause("hotkey");
            }
            _keyDown = down;
        }
        while (_notes.TryDequeue(out var line))
            Players.Host.Runtime.Log.Add("system", text: line);
    }

    private static void Pause(string by)
    {
        _paused = true;
        _notes.Enqueue($"Run paused ({by}): the current action finishes, then the world is frozen");
    }

    private static void Resume(string by)
    {
        _paused = false;
        _stopNow = false;
        _notes.Enqueue($"Run resumed ({by})");
    }

    /// <summary>
    /// True while the next call in <paramref name="queue"/> must keep waiting. Debug calls (not from an AI) pass.
    /// A client that gave up waiting would leave a stale call that runs on resume, so agent calls held
    /// longer than <see cref="MaxHeldSeconds"/> are answered with an error and dropped.
    /// </summary>
    public static bool ShouldHold(ConcurrentQueue<PendingCall> queue)
    {
        if (!Holding)
            return false;
        while (queue.TryPeek(out var head) && head.FromAgent)
        {
            if ((DateTime.UtcNow - head.Queued).TotalSeconds < MaxHeldSeconds)
                return true;
            if (queue.TryDequeue(out var stale))
                stale.Done.TrySetResult((false, "The run was paused for too long, so this call was dropped. Call it again."));
        }
        return false;
    }

    /// <summary>Current flags, for GET /api/control and the replies to POST.</summary>
    public static object Snapshot() => new
    {
        paused = _paused,
        stopAfterDay = _stopAfterDay,
        stopNow = _stopNow,
        // The world really stands still (not just asked to).
        held = Holding && WorldClock.Frozen,
        actionRunning = AnyActionRunning
    };

    /// <summary>HTTP thread: GET /api/control reads the flags, POST /api/control {"action": ...} changes them.</summary>
    public static (bool Ok, object Body) Handle(string method, string body)
    {
        if (method == "GET")
            return (true, Snapshot());
        if (method != "POST")
            return (false, new { ok = false, error = "Use GET or POST" });

        string? action = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
                action = a.GetString();
        }
        catch (JsonException)
        {
            return (false, new { ok = false, error = "Body must be JSON like {\"action\": \"pause\"}" });
        }

        switch (action?.Trim().ToLowerInvariant())
        {
            case "pause":
                Pause("api");
                break;
            case "resume":
                Resume("api");
                break;
            case "stop_after_day":
                _stopAfterDay = true;
                _notes.Enqueue("Stop requested: the run ends after this day's go_to_bed");
                break;
            case "stop_now":
                _stopNow = true;
                _notes.Enqueue("Stop requested: the current action finishes, then the AI session ends and the world stays frozen");
                break;
            case "clear_stop":
                _stopAfterDay = false;
                _stopNow = false;
                _notes.Enqueue("Stop requests cleared");
                break;
            default:
                return (false, new { ok = false, error = "action must be pause, resume, stop_after_day, stop_now or clear_stop" });
        }
        return (true, new
        {
            ok = true,
            action,
            paused = _paused,
            stopAfterDay = _stopAfterDay,
            stopNow = _stopNow
        });
    }
}

/// <summary>
/// Used by go_to_bed while it waits for the new day: an unexpected menu that stays open for more than
/// 10 seconds (the night normally only shows the menus listed in <see cref="Expected"/>) is logged and closed,
/// instead of leaving the call to time out after 5 minutes with the day half done.
/// </summary>
public sealed class UnknownMenuWatch
{
    /// <summary>10 seconds of stepped ticks. The action holds still while the world is frozen, so a pause never counts.</summary>
    public const int LimitTicks = 60 * 10;

    /// <summary>
    /// Menus that are part of the night: the shipping screen and level-ups (go_to_bed clicks them),
    /// the save progress screen, and the ready check that waits for the other farmers to go to bed.
    /// Matched by type name so this file needs no more game types.
    /// </summary>
    private static readonly string[] Expected = { "ShippingMenu", "LevelUpMenu", "SaveGameMenu", "ReadyCheckDialog" };

    private IClickableMenu? _menu;
    private int _ticks;
    private int _attempts;

    /// <summary>Call once per tick of the sleep wait.</summary>
    public void Tick(AgentRuntime rt)
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null || Expected.Contains(menu.GetType().Name))
        {
            _menu = null;
            _ticks = 0;
            _attempts = 0;
            return;
        }
        if (!ReferenceEquals(menu, _menu))
        {
            _menu = menu;
            _ticks = 0;
            _attempts = 0;
        }
        if (++_ticks < LimitTicks)
            return;

        _ticks = 0;
        _attempts++;
        var type = menu.GetType().Name;
        rt.Log.Add("system", text: $"Unknown menu {type} open for more than 10 s while sleeping: closing it (try {_attempts})");
        try
        {
            if (_attempts == 1)
                menu.exitThisMenu();
            else
                Game1.exitActiveMenu(); // exitThisMenu did not close it: force it
        }
        catch (Exception ex)
        {
            rt.Log.Add("system", text: $"Closing {type} failed: {ex.Message}");
        }
    }
}
