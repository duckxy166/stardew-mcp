using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// One HTTP listener for everything:
///   POST /mcp/p1..p3 MCP (JSON-RPC over streamable HTTP, JSON responses only), one per AI seat;
///                    POST /mcp is p1 (single-player runs); add ?breaks=1 to get planned session breaks (SessionBreaks.cs)
///   GET  /overlay    OBS browser-source overlay (Thai thoughts + actions)
///   GET  /replay     day-by-day replay of the logs
///   GET  /api/feed   overlay feed, /api/days, /api/day?n=, /api/state
///   GET  /api/memory each AI seat's lessons, plan and notes (what the journal tool wrote)
///   GET  /api/briefing?seat=p1  morning briefing of one AI seat (JSON; see Briefing.cs)
///   GET  /api/relationships  every AI farmer's friendships (the runner writes relationships.md from it)
///   GET  /api/stats  days with stats files; /api/stats?n=3 is day 3 of every AI seat (stats_NNN.json)
///   POST /api/meta   runner metadata (model, session) shown on the overlay
///   POST /api/release_freeze  runner: stop freezing the world for a seat (body {"seat":"p1"|"all","on":true}) until its farmer
///                    passes out; ends by itself on the next farm day (#22)
///   GET/POST /api/control   run control: pause, resume, stop_after_day, stop_now, clear_stop
///   POST /api/debug/{op}, /api/debug/p{N}/{op}   smoke-test hooks (EnableDebugApi only)
/// </summary>
public class AgentHttp
{
    private readonly AgentLog _log;
    private readonly ModConfig _config;
    private readonly IMonitor _monitor;
    private readonly string _assetDir;
    private readonly Dictionary<string, ToolDef> _tools;
    private readonly Dictionary<string, ToolDef> _debug = DebugApi.All();
    private HttpListener? _listener;
    private readonly McpDispatcher _mcp; // JSON-RPC handling, see McpDispatcher.cs

    // Snapshot of game state for the overlay, refreshed on the game thread.
    public volatile string StateJson = "{}";

    // Counts snapshots. The HTTP thread keeps answering with the last snapshot even when the game loop is
    // blocked, so runner/game_watchdog.py reads this "heartbeat" to tell a live game from a hung one.
    private long _beat;

    public AgentHttp(AgentLog log, ModConfig config, IMonitor monitor, string assetDir)
    {
        _log = log;
        _config = config;
        _monitor = monitor;
        _assetDir = assetDir;
        _tools = AgentTools.All().ToDictionary(t => t.Name);
        _mcp = new McpDispatcher(_tools, _config.SplitScreen);
        foreach (var problem in ToolContract.Check(_tools.Values))
            _monitor.Log("Tool contract: " + problem, LogLevel.Warn);
    }

    public void Start(int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            // 127.0.0.1 may need a URL ACL on some machines; localhost alone is enough.
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
        }
        _monitor.Log(_config.SplitScreen
            ? $"Agent MCP at http://localhost:{port}/mcp/p1, /mcp/p2, /mcp/p3 · overlay /overlay · replay /replay"
            : $"Agent MCP at http://localhost:{port}/mcp · overlay /overlay · replay /replay", LogLevel.Info);
        if (_config.EnableDebugApi)
            _monitor.Log("Debug API is ON (/api/debug/*). Turn EnableDebugApi off for normal play.", LogLevel.Warn);
        Task.Run(Loop);
    }

    public void Stop() => _listener?.Stop();

    private async Task Loop()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Access-Control-Allow-Origin"] = "*";
        try
        {
            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.StartsWith("/api/debug/"))
            {
                var rest = path.Substring("/api/debug/".Length);
                var seat = SeatPrefix(ref rest) ?? Players.Host;
                await HandleDebug(req, res, seat, rest);
                return;
            }
            if (path == "/mcp" || path.StartsWith("/mcp/"))
            {
                var rest = path == "/mcp" ? "" : path.Substring("/mcp/".Length);
                var seat = rest.Length == 0 ? Players.Host : Players.ById(rest);
                if (seat is not { Role: SeatRole.Ai } || (seat.Screen > 0 && !_config.SplitScreen))
                {
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }
                if (req.HttpMethod == "POST")
                    await HandleMcp(req, res, seat);
                else
                {
                    res.StatusCode = req.HttpMethod == "DELETE" ? 200 : 405;
                    res.Close();
                }
                return;
            }
            switch (path)
            {
                case "/overlay":
                    await File(res, "overlay.html");
                    return;
                case "/replay":
                case "":
                    await File(res, "replay.html");
                    return;
                case "/api/feed":
                    long.TryParse(req.QueryString["after"], out var after);
                    await Json(res, new
                    {
                        state = JsonDocument.Parse(StateJson).RootElement,
                        thoughtLanguage = ThoughtLanguageName,
                        meta = _log.Meta,
                        entries = _log.FeedAfter(after)
                    });
                    return;
                case "/api/state":
                    await Text(res, StateJson, "application/json");
                    return;
                case "/api/briefing":
                    var (briefStatus, briefBody) = await Briefing.Serve(req.QueryString["seat"], _config.SplitScreen);
                    if (briefStatus != 200) { res.StatusCode = briefStatus; res.Close(); return; }
                    await Text(res, briefBody, "application/json");
                    return;
                case "/api/relationships":
                    await Json(res, await NpcStory.ReportAsync());
                    return;
                case "/api/stats":
                    await Json(res, DayStats.Api(_log, req.QueryString["n"]));
                    return;
                case "/api/control":
                    // pause | resume | stop_after_day | stop_now | clear_stop (GET reads the flags)
                    var controlBody = "";
                    if (req.HttpMethod == "POST")
                        using (var cr = new StreamReader(req.InputStream, Encoding.UTF8))
                            controlBody = await cr.ReadToEndAsync();
                    var control = RunControl.Handle(req.HttpMethod, controlBody);
                    res.StatusCode = control.Ok ? 200 : 400;
                    await Json(res, control.Body);
                    return;
                case "/api/days":
                    await Json(res, _log.Days());
                    return;
                case "/api/day":
                    int.TryParse(req.QueryString["n"], out var n);
                    var log = System.IO.File.Exists(_log.DayFile(n)) ? await System.IO.File.ReadAllTextAsync(_log.DayFile(n), Encoding.UTF8) : "";
                    var diaries = new Dictionary<string, string>();
                    foreach (var seat in Players.Slots.Where(s => s.Role == SeatRole.Ai))
                        if (System.IO.File.Exists(_log.DiaryFile(n, seat.Id)))
                            diaries[seat.Id] = await System.IO.File.ReadAllTextAsync(_log.DiaryFile(n, seat.Id), Encoding.UTF8);
                    await Json(res, new
                    {
                        day = n,
                        diary = diaries.GetValueOrDefault("p1", ""),
                        diaries,
                        entries = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement)
                    });
                    return;
                case "/api/memory":
                    await Json(res, Players.Slots.Where(s => s.Role == SeatRole.Ai).ToDictionary(s => s.Id, s => _log.ReadMemory(s.Id)));
                    return;
                case "/api/release_freeze" when req.HttpMethod == "POST":
                    await ReleaseFreeze(req, res);
                    return;
                case "/api/meta" when req.HttpMethod == "POST":
                    using (var r = new StreamReader(req.InputStream, Encoding.UTF8))
                    {
                        var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(await r.ReadToEndAsync()) ?? new();
                        lock (_log.Meta)
                            foreach (var kv in meta) _log.Meta[kv.Key] = kv.Value;
                        if (meta.TryGetValue("event", out var ev)) _log.Add("system", text: ev);
                    }
                    await Json(res, new { ok = true });
                    return;
                default:
                    res.StatusCode = 404;
                    res.Close();
                    return;
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"HTTP error: {ex.Message}", LogLevel.Warn);
            try { res.StatusCode = 500; res.Close(); } catch { }
        }
    }

    // ------------------------------------------------------------------ release the freeze (#22)

    /// <summary>
    /// The runner is done with a seat for the day (its sessions are used up) but the day is not over. A seat with no action
    /// running holds the world frozen for ever, so release it: game time runs on until the farmer passes out and the next
    /// day starts. Body (all optional): {"seat": "p1" (default, like /mcp) | "p2" | "p3" | "all", "on": false to take it back}.
    /// The release belongs to the current farm day; MCP traffic from the seat ends it too.
    /// </summary>
    private async Task ReleaseFreeze(HttpListenerRequest req, HttpListenerResponse res)
    {
        string body;
        using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
        JsonElement args;
        try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone(); }
        catch (JsonException) { res.StatusCode = 400; res.Close(); return; }

        var who = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("seat", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() ?? "" : "";
        bool on = !(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("on", out var ov) && ov.ValueKind == JsonValueKind.False);

        List<PlayerSlot> seats;
        if (who.Equals("all", StringComparison.OrdinalIgnoreCase))
            seats = Players.Slots.Where(s => s.Role == SeatRole.Ai).ToList();
        else if ((who.Length == 0 ? Players.Host : Players.ById(who)) is { Role: SeatRole.Ai } one)
            seats = new List<PlayerSlot> { one };
        else
        {
            await Json(res, new { ok = false, text = $"Unknown seat \"{who}\"; use p1, p2, p3 or all" });
            return;
        }
        if (!Context.IsWorldReady)
        {
            await Json(res, new { ok = false, text = "The game world is not loaded" });
            return;
        }

        foreach (var seat in seats)
        {
            seat.SetReleased(on);
            seat.Runtime.Log.Add("system", text: on
                ? $"Freeze released for {seat.Id}: game time runs on until the farmer passes out or the AI acts again"
                : $"Freeze release for {seat.Id} taken back");
        }
        await Json(res, new { ok = true, released = on, seats = seats.Select(s => s.Id), day = AgentLog.FarmDay });
    }

    // ------------------------------------------------------------------ debug (smoke tests)

    /// <summary>Strip a leading "p{N}/" seat from <paramref name="rest"/>; null when there is none.</summary>
    private static PlayerSlot? SeatPrefix(ref string rest)
    {
        int slash = rest.IndexOf('/');
        if (slash <= 0 || Players.ById(rest.Substring(0, slash)) is not { } seat)
            return null;
        rest = rest.Substring(slash + 1);
        return seat;
    }

    private async Task HandleDebug(HttpListenerRequest req, HttpListenerResponse res, PlayerSlot seat, string op)
    {
        if (!_config.EnableDebugApi || !_debug.TryGetValue(op, out var tool))
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }
        if (req.HttpMethod != "POST") { res.StatusCode = 405; res.Close(); return; }

        string body;
        using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
        JsonElement args;
        try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone(); }
        catch (JsonException) { res.StatusCode = 400; res.Close(); return; }

        if (!seat.Active)
        {
            await Json(res, new { ok = false, text = $"{seat.Id} is not in the game" });
            return;
        }
        var (ok, text) = await seat.Runtime.Enqueue(tool, args);
        await Json(res, new { ok, text });
    }

    // ------------------------------------------------------------------ MCP

    private async Task HandleMcp(HttpListenerRequest req, HttpListenerResponse res, PlayerSlot seat)
    {
        string body;
        using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        SessionBreaks.Observe(seat.Id, root, req.QueryString["breaks"]); // "initialize" starts a session; /mcp?breaks=1 opts in (#30)

        var reply = await _mcp.Handle(root, new McpSeat(seat));
        if (reply.Body == null) { res.StatusCode = reply.Status; res.Close(); return; }
        if (reply.SessionId != null) res.Headers["Mcp-Session-Id"] = reply.SessionId;
        await Json(res, reply.Body);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task Json(HttpListenerResponse res, object o) =>
        await Text(res, JsonSerializer.Serialize(o, AgentLog.Json), "application/json");

    private static async Task Text(HttpListenerResponse res, string s, string type)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        res.ContentType = type + "; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private async Task File(HttpListenerResponse res, string name)
    {
        var path = Path.Combine(_assetDir, name);
        if (!System.IO.File.Exists(path)) { res.StatusCode = 404; res.Close(); return; }
        await Text(res, await System.IO.File.ReadAllTextAsync(path, Encoding.UTF8), "text/html");
    }

    /// <summary>ThoughtLanguage from config.json (blank = "Thai"); the overlay, replay and runner follow it.</summary>
    private string ThoughtLanguageName =>
        string.IsNullOrWhiteSpace(_config.ThoughtLanguage) ? "Thai" : _config.ThoughtLanguage.Trim();

    /// <summary>Refresh the overlay state snapshot (call on the host screen's tick, ~4x/sec).</summary>
    public void RefreshState()
    {
        var players = Players.Slots
            .Where(s => s.Screen == 0 || s.Active)
            .Select(s =>
            {
                var pass = PassOut.View(s.Id); // #22: a pass-out whose journal is still owed
                return new
                {
                    id = s.Id,
                    role = s.Role == SeatRole.Ai ? "ai" : "camera",
                    active = s.Active,
                    connected = s.Connected,
                    state = s.State,
                    action = s.Runtime.CurrentTool,
                    thinkingSeconds = WorldClock.ThinkingSeconds(s),
                    timedOut = s.TimedOut,
                    released = s.Released,
                    pendingJournalDay = pass.PendingDay,
                    pendingJournalDays = pass.PendingDays.Count > 0 ? pass.PendingDays : null,
                    passOut = pass.Record
                };
            })
            .ToArray();

        if (!Context.IsWorldReady)
        {
            StateJson = JsonSerializer.Serialize(new { ready = false, heartbeat = ++_beat, frozen = WorldClock.Frozen, players }, AgentLog.Json);
            return;
        }
        var p = Game1.player;
        var hostPass = PassOut.View(Players.Host.Id);
        StateJson = JsonSerializer.Serialize(new
        {
            ready = true,
            heartbeat = ++_beat,
            saveFolder = SaveFolder.Name,
            thoughtLanguage = ThoughtLanguageName,
            day = AgentLog.FarmDay,
            season = Game1.currentSeason,
            dayOfMonth = Game1.dayOfMonth,
            year = Game1.year,
            time = WorldText.FormatTime(Game1.timeOfDay),
            weather = WorldText.Weather(),
            location = Game1.currentLocation?.Name,
            money = p.Money,
            energy = (int)p.Stamina,
            maxEnergy = p.MaxStamina,
            frozen = WorldClock.Frozen,
            thinking = WorldClock.ThinkingSeats,
            // The clock does not run in cutscenes and festivals; the watchdog must not read that as a hang.
            eventUp = Game1.eventUp,
            festival = Game1.CurrentEvent != null && Game1.CurrentEvent.isFestival,
            action = Players.Host.Runtime.CurrentTool,
            dialogue = Game1.activeClickableMenu is StardewValley.Menus.DialogueBox box
                ? new { speaker = box.characterDialogue?.speaker?.displayName, question = box.isQuestion }
                : null,
            screens = GameRunner.instance.gameInstances.Count,
            // Run control (RunControl.cs). A watchdog must not read a paused game as hung.
            paused = RunControl.Paused,
            stopAfterDay = RunControl.StopAfterDay,
            stopNow = RunControl.StopNow,
            plan = PlanBoard.Snapshot(),
            owned = Ownership.OwnedObjects(),
            pendingJournalDay = hostPass.PendingDay,
            pendingJournalDays = hostPass.PendingDays.Count > 0 ? hostPass.PendingDays : null,
            passOut = hostPass.Record,
            players
        }, AgentLog.Json);
    }
}
