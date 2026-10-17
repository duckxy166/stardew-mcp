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

/// <summary>A tool call waiting for (or running on) the game thread.</summary>
public class PendingCall
{
    public ToolDef Tool = null!;
    public JsonElement Args;
    /// <summary>Observes only: keeps the world frozen and may answer during the seat's own action.</summary>
    public bool Instant;
    /// <summary>Came in through an AI's MCP endpoint (not the debug API): <see cref="RunControl"/> pauses and refuses these.</summary>
    public bool FromAgent;
    public DateTime Queued = DateTime.UtcNow;
    public TaskCompletionSource<(bool ok, string text)> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Reason code, partial flag and counts of the finished call; set just before <see cref="Done"/> completes.</summary>
    public ToolOutcome? Outcome;
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

    /// <summary>This seat's numbers for the day in progress; written as stats_NNN.json when the day rolls over (#25).</summary>
    public DayStats Stats { get; }

    /// <summary>Profession choices (skill, level) waiting for choose_profession, and the game day they were offered.</summary>
    public List<Microsoft.Xna.Framework.Point> PendingProfessions { get; } = new();
    public int PendingProfessionDay { get; set; }
    public ModConfig Config { get; }
    public IModHelper Helper { get; }

    /// <summary>Heart events this seat plays instead of skipping them, and friendship milestones (NpcStory.cs).</summary>
    public StoryWatcher Story { get; } = new();

    /// <summary>Stuck/loop detector for this seat's calls: repeated failures, no progress. Hints per day: <c>Guard.HintsToday</c>. See LoopGuard.cs.</summary>
    public LoopGuard Guard { get; }

    /// <summary>True while an action that lets game time pass is running (or a heart event plays by itself).</summary>
    public bool ActionRunning => _actionLane.Current is { Instant: false } || Story.Playing;
    public string? CurrentTool => _actionLane.Current?.Tool.Name;
    /// <summary>The call in the action lane (check <see cref="PendingCall.Instant"/>), or null. <see cref="Perception"/> reads it.</summary>
    public PendingCall? CurrentCall => _actionLane.Current;

    /// <summary>
    /// Stop the running action now and give <paramref name="result"/> to the AI as its result (not a failure).
    /// False when no non-instant action is running. Used by <see cref="Perception"/>; any other event source can use it too.
    /// </summary>
    public bool Interrupt(string result) => _actionLane.Interrupt(result);

    /// <summary>go_to_bed is past the day change and waiting for this farmer to wake up (needs the world running).</summary>
    public bool WakingUp => _actionLane.Current?.Tool.Name == "go_to_bed" && Context.IsWorldReady && Game1.Date.TotalDays > _actionLane.CallStartDay;
    /// <summary>The "reason" the running action was given (what the viewers see), or null when no action runs. Used by the pass-out record.</summary>
    public string? CurrentReason => _actionLane.Current is { Instant: false } c ? AgentTools.Reason(c.Args) : null;

    public AgentRuntime(IModHelper helper, IMonitor monitor, ModConfig config, PlayerLog log)
    {
        Helper = helper;
        _monitor = monitor;
        Config = config;
        Log = log;
        Stats = new DayStats(log);
        _actionLane = new Lane(this, pausable: true);
        _observeLane = new Lane(this, pausable: false);

        Guard = new LoopGuard(config, log, monitor);
    }

    /// <summary>
    /// Queue a call. With <paramref name="observation"/>, tools marked <see cref="ToolDef.Instant"/>
    /// go to the observation lane; everything else, including debug ops, waits its turn in the action lane.
    /// </summary>
    public Task<(bool ok, string text)> Enqueue(ToolDef tool, JsonElement args, bool observation = false) =>
        EnqueueCall(tool, args, observation).Done.Task;

    /// <summary>Like <see cref="Enqueue"/>, but returns the call so the caller can read its <see cref="PendingCall.Outcome"/> after <see cref="PendingCall.Done"/>.</summary>
    public PendingCall EnqueueCall(ToolDef tool, JsonElement args, bool observation = false)
    {
        var call = new PendingCall { Tool = tool, Args = args, Instant = tool.Instant || tool.InstantFor?.Invoke(args) == true, FromAgent = observation };
        if (observation && RunControl.StopNow)
        {
            call.Outcome = new ToolOutcome(false, Reasons.Interrupted, false, null);
            call.Done.TrySetResult((false, RunControl.StopMessage));
            return call;
        }
        (observation && call.Instant ? _observations : _actions).Enqueue(call);
        return call;
    }

    /// <summary>Called every tick of this seat's screen (UpdateTicked).</summary>
    public void Tick()
    {
        _globalTicks++;
        AutoHandleOverlays();
        PassOut.Tick(this);
        QuestEvents.Tick(this);
        AgentTools.FishingTick(this); // plays an open fishing minigame while this seat runs the fish tool
        SocialTools.Tick(Log.Player);
        Perception.Tick(this);
        Story.Tick(this);
        Romance.Tick(this);
        Stats.Tick();
        NightEvents.Tick(this);
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
                    // Not named here any more (#53): the AI chooses with name(text); NamingTools.cs has the fallbacks.
                    Naming.Observe(this, nm);
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
        public int CallStartDay { get; private set; }
        /// <summary>This farmer's health on the previous tick of the running action, for the damage interrupt.</summary>
        private int _lastHealth;

        public PendingCall? Current { get; private set; }

        /// <param name="pausable">Action lane: non-instant coroutines hold still while the world is frozen.</param>
        public Lane(AgentRuntime rt, bool pausable)
        {
            _rt = rt;
            _pausable = pausable;
        }

        public void Tick(ConcurrentQueue<PendingCall> queue)
        {
            if (Current == null && !RunControl.ShouldHold(queue) && queue.TryDequeue(out var next))
                Start(next);
            if (Current != null)
                Step();
        }

        private void Start(PendingCall call)
        {
            Current = call;
            _ctx = new CallContext();
            _ticksInCall = 0;
            CallStartDay = Context.IsWorldReady ? Game1.Date.TotalDays : 0;
            _lastHealth = Context.IsWorldReady ? Game1.player.health : 0;
            _watch = Stopwatch.StartNew();
            _stack.Clear();
            _rt.Stats.OnCallStart(call.Tool.Name, call.Instant);

            if (SessionBreaks.Refuse(_rt.Log, call, out var breakMessage)) // after a planned session break, no more actions (#30)
            {
                _ctx.Fail(breakMessage);
                Finish();
                return;
            }

            if (!call.Instant)
                _rt.Log.Add("action", call.Tool.Name, AgentTools.Reason(call.Args), AgentTools.ArgsForLog(call.Args));

            try
            {
                if (call.Tool.NeedsWorld && !Context.IsWorldReady)
                {
                    _ctx.Fail(Reasons.NotReady, "The game world is not loaded (title screen or loading). Wait and try again.");
                    Finish();
                    return;
                }

                // Loop guard: the same call already failed again and again, so don't run it a fifth time.
                if (_rt.Guard.Refuse(call, out var refusal))
                {
                    _ctx.Fail(refusal);
                    Finish();
                    return;
                }

                _stack.Push(call.Tool.Run(call.Args, _ctx));
            }
            catch (Exception ex)
            {
                _ctx.Fail(Reasons.Error, $"Error: {ex.Message}");
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
                _ctx!.Fail(Reasons.Timeout, "Action timed out after 6 minutes of real time.");
                Finish();
                return;
            }

            if (Current!.Tool.Name != "go_to_bed" && CallStartDay > 0 && Context.IsWorldReady &&
                Game1.Date.TotalDays > CallStartDay && Game1.activeClickableMenu == null && !Game1.fadeToBlack && !Game1.newDay)
            {
                _rt.Log.Add("system", text: $"Passed out / new day: farm day {AgentLog.FarmDay}");
                _ctx!.Result = $"You passed out (2:00 AM or exhausted)! It is now farm day {AgentLog.FarmDay}. Stop here.";
                _ctx.Reason = Reasons.Interrupted;
                _ctx.Partial = true;
                Finish();
                return;
            }

            // Hurt by a monster close by: end the call so the AI can react (fight, eat, leave) instead of finishing a long action.
            if (!Current!.Instant && Context.IsWorldReady && AgentTools.DamageInterrupt(Current.Tool.Name, ref _lastHealth) is { } hurt)
            {
                _ctx!.Result = hurt;
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
                _ctx!.Fail(Reasons.Error, $"Error: {ex.Message}");
            }
            Finish();
        }

        /// <summary>Drop the running action where it stands. Disposing the coroutines runs their finally blocks (e.g. a harvest still reports its thefts).</summary>
        public bool Interrupt(string result)
        {
            if (Current == null || Current.Instant) return false;
            while (_stack.Count > 0)
            {
                try { (_stack.Pop() as IDisposable)?.Dispose(); }
                catch (Exception ex) { _rt._monitor.Log($"Tool {Current.Tool.Name} failed while being interrupted: {ex.Message}", LogLevel.Warn); }
            }
            _ctx!.Ok = true;
            _ctx.Result = result;
            Finish();
            return true;
        }

        private void Finish()
        {
            var call = Current!;
            var ctx = _ctx!;
            _stack.Clear();
            Current = null;
            _ctx = null;

            var text = ctx.Result;
            // Loop guard: "you tried this 3 times", "you haven't done anything for a while".
            var hint = _rt.Guard.End(call, ctx.Ok, ctx.Result);
            if (hint.Length > 0) text = text.TrimEnd() + "\n" + hint;

            var mail = call.Tool.Mail ? Perception.TakeMail(_rt.Log.Player) : "";
            if (mail.Length > 0) text = text.TrimEnd() + "\n" + mail;
            if (call.Tool.AppendStatus && Context.IsWorldReady)
                text = text.TrimEnd() + "\n" + Observation.OneLine(AgentTools.Footer()); // one line, however many menus are open
            text = SessionBreaks.Annotate(_rt.Log, call, text); // planned session break notice (#30)

            _rt.Stats.OnCallEnd(call.Tool.Name, ctx.Ok, ctx.Result, CallStartDay);
            var outcome = ToolOutcome.From(ctx);
            if (!call.Instant || !ctx.Ok)
                _rt.Log.Add("result", call.Tool.Name, ctx.Result, ok: ctx.Ok, ms: _watch.ElapsedMilliseconds, outcome: outcome);

            call.Outcome = outcome;
            call.Done.TrySetResult((ctx.Ok, text));
        }
    }
}
