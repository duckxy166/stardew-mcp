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
    /// <summary>Observes only: keeps the world frozen and may answer during the seat's own action.</summary>
    public bool Instant;
    public TaskCompletionSource<(bool ok, string text)> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Runs one seat's tool calls on the game thread as coroutines. Actions run one at a time in
/// order; instant observations (look, status, think...) run in their own lane so they answer
/// even while that seat's own action is running. Freezing the world is <see cref="WorldClock"/>'s job.
/// </summary>
public class AgentRuntime
{
    private readonly IMonitor _monitor;
    private readonly ConcurrentQueue<PendingCall> _actions = new();
    private readonly ConcurrentQueue<PendingCall> _observations = new();
    private readonly Lane _actionLane;
    private readonly Lane _observeLane;
    private int _globalTicks;
    private int _eventTicks;

    public PlayerLog Log { get; }

    /// <summary>Profession choices (skill, level) waiting for choose_profession, and the game day they were offered.</summary>
    public List<Microsoft.Xna.Framework.Point> PendingProfessions { get; } = new();
    public int PendingProfessionDay { get; set; }
    public ModConfig Config { get; }
    public IModHelper Helper { get; }

    /// <summary>Heart events this seat plays instead of skipping them, and friendship milestones (NpcStory.cs).</summary>
    public StoryWatcher Story { get; } = new();

    /// <summary>True while an action that lets game time pass is running (or a heart event plays by itself).</summary>
    public bool ActionRunning => _actionLane.Current is { Instant: false } || Story.Playing;
    public string? CurrentTool => _actionLane.Current?.Tool.Name;

    public AgentRuntime(IModHelper helper, IMonitor monitor, ModConfig config, PlayerLog log)
    {
        Helper = helper;
        _monitor = monitor;
        Config = config;
        Log = log;
        _actionLane = new Lane(this, pausable: true);
        _observeLane = new Lane(this, pausable: false);
    }

    /// <summary>
    /// Queue a call. With <paramref name="observation"/>, tools marked <see cref="ToolDef.Instant"/>
    /// go to the observation lane; everything else, including debug ops, waits its turn in the action lane.
    /// </summary>
    public Task<(bool ok, string text)> Enqueue(ToolDef tool, JsonElement args, bool observation = false)
    {
        var call = new PendingCall { Tool = tool, Args = args, Instant = tool.Instant || tool.InstantFor?.Invoke(args) == true };
        (observation && call.Instant ? _observations : _actions).Enqueue(call);
        return call.Done.Task;
    }

    /// <summary>Called every tick of this seat's screen (UpdateTicked).</summary>
    public void Tick()
    {
        _globalTicks++;
        AutoHandleOverlays();
        SocialTools.Tick(Log.Player);
        Story.Tick(this);
        _actionLane.Tick(_actions);
        _observeLane.Tick(_observations);
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
                    AgentTools.ClickShippingOk(sm);
                    break;
                case StardewValley.Menus.LevelUpMenu lm when lm.isActive:
                    if (lm.isProfessionChooser)
                        Professions.Defer(lm, this);
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
            if (Story.OnEvent(this))
            {
                // A heart event the StoryWatcher plays (opt-in PlayHeartEvents): not skipped, not auto-answered.
            }
            else if (_eventTicks == 90)
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
            Story.OnNoEvent(this);
        }
    }

    /// <summary>Runs queued calls one at a time, stepping the current coroutine once per tick.</summary>
    private class Lane
    {
        private readonly AgentRuntime _rt;
        private readonly bool _pausable;
        private readonly Stack<IEnumerator> _stack = new();
        private CallContext? _ctx;
        private Stopwatch _watch = new();
        private int _ticksInCall;
        private int _callStartDay;

        public PendingCall? Current { get; private set; }

        /// <param name="pausable">Action lane: non-instant coroutines hold still while the world is frozen.</param>
        public Lane(AgentRuntime rt, bool pausable)
        {
            _rt = rt;
            _pausable = pausable;
        }

        public void Tick(ConcurrentQueue<PendingCall> queue)
        {
            if (Current == null && queue.TryDequeue(out var next))
                Start(next);
            if (Current != null)
                Step();
        }

        private void Start(PendingCall call)
        {
            Current = call;
            _ctx = new CallContext();
            _ticksInCall = 0;
            _callStartDay = Context.IsWorldReady ? Game1.Date.TotalDays : 0;
            _watch = Stopwatch.StartNew();
            _stack.Clear();

            if (!call.Instant)
                _rt.Log.Add("action", call.Tool.Name, AgentTools.Reason(call.Args), AgentTools.ArgsForLog(call.Args));

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
            // While the world is frozen (an AI is thinking) a running action holds still, so its
            // stuck/re-plan counters and timeouts don't advance either.
            if (_pausable && !Current!.Instant && WorldClock.WorldPaused)
                return;

            _ticksInCall++;
            if (_ticksInCall > 60 * 60 * 6)
            {
                _ctx!.Fail("Action timed out after 6 minutes of real time.");
                Finish();
                return;
            }

            if (Current!.Tool.Name != "go_to_bed" && _callStartDay > 0 && Context.IsWorldReady &&
                Game1.Date.TotalDays > _callStartDay && Game1.activeClickableMenu == null && !Game1.fadeToBlack && !Game1.newDay)
            {
                _rt.Log.Add("system", text: $"Passed out / new day: farm day {AgentLog.FarmDay}");
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
                _rt._monitor.Log($"Tool {Current?.Tool.Name} crashed: {ex}", LogLevel.Error);
                _ctx!.Fail($"Error: {ex.Message}");
            }
            Finish();
        }

        private void Finish()
        {
            var call = Current!;
            var ctx = _ctx!;
            _stack.Clear();
            Current = null;
            _ctx = null;

            var text = ctx.Result;
            var mail = call.Tool.Mail ? SocialTools.TakeMail(_rt.Log.Player) : "";
            if (mail.Length > 0) text = text.TrimEnd() + "\n" + mail;
            if (call.Tool.AppendStatus && Context.IsWorldReady)
                text = text.TrimEnd() + "\n" + AgentTools.Footer();

            if (!call.Instant || !ctx.Ok)
                _rt.Log.Add("result", call.Tool.Name, ctx.Result, ok: ctx.Ok, ms: _watch.ElapsedMilliseconds);

            call.Done.TrySetResult((ctx.Ok, text));
        }
    }
}
