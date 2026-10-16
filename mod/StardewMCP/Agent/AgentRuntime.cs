using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>State shared by one running tool call.</summary>
public class CallContext
{
    public string Result = "";
    public bool Ok = true;

    public void Fail(string message)
    {
        Ok = false;
        Result = message;
    }
}

/// <summary>A tool call waiting for (or running on) the game thread.</summary>
public class PendingCall
{
    public ToolDef Tool = null!;
    public JsonElement Args;
    public TaskCompletionSource<(bool ok, string text)> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Runs agent tool calls on the game thread as coroutines (one at a time) and freezes the
/// in-game clock whenever no action is running, so the AI's thinking time costs no game time.
/// </summary>
public class AgentRuntime
{
    private readonly IMonitor _monitor;
    private readonly ConcurrentQueue<PendingCall> _queue = new();
    private readonly Stack<IEnumerator> _stack = new();
    private PendingCall? _current;
    private CallContext? _ctx;
    private Stopwatch _watch = new();
    private int _ticksInCall;
    private int _callStartDay;
    private int _globalTicks;
    private int _eventTicks;

    private bool _frozen;
    private int _frozenInterval;

    public AgentLog Log { get; }
    public ModConfig Config { get; }
    public IModHelper Helper { get; }

    /// <summary>True while an action that lets game time pass is running.</summary>
    public bool ActionRunning => _current != null && !_current.Tool.Instant;
    public bool Frozen => _frozen;
    public string? CurrentTool => _current?.Tool.Name;

    public AgentRuntime(IModHelper helper, IMonitor monitor, ModConfig config, AgentLog log)
    {
        Helper = helper;
        _monitor = monitor;
        Config = config;
        Log = log;
    }

    public Task<(bool ok, string text)> Enqueue(ToolDef tool, JsonElement args)
    {
        var call = new PendingCall { Tool = tool, Args = args };
        _queue.Enqueue(call);
        return call.Done.Task;
    }

    /// <summary>Called every game tick (UpdateTicked).</summary>
    public void Tick()
    {
        _globalTicks++;
        AutoHandleOverlays();

        if (_current == null && _queue.TryDequeue(out var next))
            Start(next);

        if (_current != null)
            Step();

        UpdateFreeze();
    }

    private void AutoHandleOverlays()
    {
        if (!Context.IsWorldReady && Game1.activeClickableMenu is not StardewValley.Menus.ShippingMenu && Game1.activeClickableMenu is not StardewValley.Menus.LevelUpMenu)
            return;

        if (_globalTicks % 60 == 0)
        {
            switch (Game1.activeClickableMenu)
            {
                case StardewValley.Menus.ShippingMenu sm:
                    Helper.Reflection.GetMethod(sm, "okClicked").Invoke();
                    break;
                case StardewValley.Menus.LevelUpMenu lm when lm.isActive:
                    if (lm.isProfessionChooser)
                    {
                        var profs = Helper.Reflection.GetField<List<int>>(lm, "professionsToChoose").GetValue();
                        Log.Add("system", text: $"Level up: profession choice auto-picked option 1 ({(profs.Count > 0 ? profs[0] : 0)})");
                        lm.receiveLeftClick(lm.leftProfession.bounds.Center.X, lm.leftProfession.bounds.Center.Y);
                    }
                    else
                        lm.okButtonClicked();
                    break;
                case StardewValley.Menus.NamingMenu nm:
                    var name = string.IsNullOrWhiteSpace(nm.textBox?.Text) ? StardewValley.Dialogue.randomName() : nm.textBox.Text;
                    Log.Add("system", text: $"Auto-named \"{name}\" in NamingMenu");
                    nm.doneNaming?.Invoke(name);
                    if (Game1.activeClickableMenu == nm) Game1.exitActiveMenu();
                    break;
            }
        }

        if (Game1.eventUp && Game1.CurrentEvent != null && !Game1.CurrentEvent.isFestival)
        {
            _eventTicks++;
            if (_eventTicks == 90)
            {
                Log.Add("system", text: $"Skipping cutscene ({Game1.CurrentEvent.id})");
                Game1.CurrentEvent.skipEvent();
            }
            else if (_eventTicks > 180 && _eventTicks % 60 == 0 && Game1.activeClickableMenu is StardewValley.Menus.DialogueBox db)
            {
                if (db.isQuestion && db.responses?.Length > 0) db.selectedResponse = 0;
                db.receiveLeftClick(0, 0);
            }
        }
        else
        {
            _eventTicks = 0;
        }
    }

    private void Start(PendingCall call)
    {
        _current = call;
        _ctx = new CallContext();
        _ticksInCall = 0;
        _callStartDay = Context.IsWorldReady ? Game1.Date.TotalDays : 0;
        _watch = Stopwatch.StartNew();
        _stack.Clear();

        if (!call.Tool.Instant)
            Log.Add("action", call.Tool.Name, AgentTools.Reason(call.Args), AgentTools.ArgsForLog(call.Args));

        try
        {
            if (call.Tool.NeedsWorld && !Context.IsWorldReady)
            {
                _ctx.Fail("The game world is not loaded (title screen or loading). Wait and try again.");
                Finish();
                return;
            }
            _stack.Push(call.Tool.Run(call.Args, _ctx));
        }
        catch (Exception ex)
        {
            _ctx.Fail($"Error: {ex.Message}");
            Finish();
        }
    }

    private void Step()
    {
        // Spike #37: while the host has paused the world, running actions do not advance (observations still run).
        if (!_current!.Tool.Instant && Context.IsWorldReady && Game1.HostPaused)
            return;
        _ticksInCall++;
        if (_ticksInCall > 60 * 60 * 6)
        {
            _ctx!.Fail("Action timed out after 6 minutes of real time.");
            Finish();
            return;
        }

        if (_current!.Tool.Name != "go_to_bed" && _callStartDay > 0 && Context.IsWorldReady &&
            Game1.Date.TotalDays > _callStartDay && Game1.activeClickableMenu == null && !Game1.fadeToBlack && !Game1.newDay)
        {
            Log.Add("system", text: $"Passed out / new day: farm day {AgentLog.FarmDay}");
            _ctx!.Result = $"You passed out (2:00 AM or exhausted)! It is now farm day {AgentLog.FarmDay}. Stop here.";
            Finish();
            return;
        }

        try
        {
            // Run until the coroutine yields a "wait one tick" (null) or finishes.
            while (_stack.Count > 0)
            {
                var top = _stack.Peek();
                if (!top.MoveNext())
                {
                    _stack.Pop();
                    continue;
                }
                if (top.Current is IEnumerator nested)
                {
                    _stack.Push(nested);
                    continue;
                }
                return; // yielded null: wait for next tick
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Tool {_current?.Tool.Name} crashed: {ex}", LogLevel.Error);
            _ctx!.Fail($"Error: {ex.Message}");
        }
        Finish();
    }

    private void Finish()
    {
        var call = _current!;
        var ctx = _ctx!;
        _stack.Clear();
        _current = null;
        _ctx = null;

        var text = ctx.Result;
        if (call.Tool.AppendStatus && Context.IsWorldReady)
            text = text.TrimEnd() + "\n" + AgentTools.Footer();

        if (!call.Tool.Instant || !ctx.Ok)
            Log.Add("result", call.Tool.Name, ctx.Result, ok: ctx.Ok, ms: _watch.ElapsedMilliseconds);

        call.Done.TrySetResult((ctx.Ok, text));
    }

    private void UpdateFreeze()
    {
        bool shouldFreeze = Config.FreezeTimeWhileThinking && Context.IsWorldReady && !ActionRunning && !Game1.newDay &&
                            Game1.activeClickableMenu is not StardewValley.Menus.ShippingMenu &&
                            Game1.activeClickableMenu is not StardewValley.Menus.LevelUpMenu;
        if (!shouldFreeze)
        {
            _frozen = false;
            return;
        }
        if (!_frozen)
        {
            _frozen = true;
            _frozenInterval = Math.Min(Game1.gameTimeInterval, Game1.realMilliSecondsPerGameTenMinutes - 200);
        }
        Game1.gameTimeInterval = _frozenInterval;
    }
}
