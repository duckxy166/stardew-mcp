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
///                    POST /mcp is p1 (single-player runs)
///   GET  /overlay    OBS browser-source overlay (Thai thoughts + actions)
///   GET  /replay     day-by-day replay of the logs
///   GET  /api/feed   overlay feed, /api/days, /api/day?n=, /api/state
///   GET  /api/briefing?seat=p1  morning briefing of one AI seat (JSON; see Briefing.cs)
///   POST /api/meta   runner metadata (model, session) shown on the overlay
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
    private const string ProtocolFallback = "2025-06-18";

    // Snapshot of game state for the overlay, refreshed on the game thread.
    public volatile string StateJson = "{}";

    public AgentHttp(AgentLog log, ModConfig config, IMonitor monitor, string assetDir)
    {
        _log = log;
        _config = config;
        _monitor = monitor;
        _assetDir = assetDir;
        _tools = AgentTools.All().ToDictionary(t => t.Name);
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

        if (root.ValueKind == JsonValueKind.Array)
        {
            var outs = new List<object>();
            foreach (var m in root.EnumerateArray())
            {
                var o = await Dispatch(m, seat);
                if (o != null) outs.Add(o);
            }
            if (outs.Count == 0) { res.StatusCode = 202; res.Close(); return; }
            await Json(res, outs);
            return;
        }

        var reply = await Dispatch(root, seat);
        if (reply == null) { res.StatusCode = 202; res.Close(); return; }
        res.Headers["Mcp-Session-Id"] = "stardew-" + seat.Id;
        await Json(res, reply);
    }

    private async Task<object?> Dispatch(JsonElement msg, PlayerSlot seat)
    {
        // Any MCP traffic means an AI is attached to this seat: from now on its thinking freezes the world.
        seat.MarkContact();
        var method = msg.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        if (!msg.TryGetProperty("id", out var idEl)) return null; // notification
        object id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : (object)(idEl.GetString() ?? "");
        var p = msg.TryGetProperty("params", out var pe) ? pe : default;

        switch (method)
        {
            case "initialize":
                var ver = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var pv) ? pv.GetString() : ProtocolFallback;
                return Ok(id, new
                {
                    protocolVersion = ver ?? ProtocolFallback,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "stardew", version = "2.0.0" },
                    instructions = _config.SplitScreen
                        ? $"You are player {seat.Id} on a three-player Stardew Valley farm. The whole world is frozen while you or another player is thinking, and only moves while actions run."
                        : "You are playing Stardew Valley. Game time is frozen while you think and only moves while an action runs."
                });
            case "ping":
                return Ok(id, new { });
            case "tools/list":
                return Ok(id, new
                {
                    tools = _tools.Values.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.Schema })
                });
            case "tools/call":
                var name = p.GetProperty("name").GetString() ?? "";
                var args = p.TryGetProperty("arguments", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement;
                if (!_tools.TryGetValue(name, out var tool))
                    return Ok(id, new { content = new[] { new { type = "text", text = $"Unknown tool {name}" } }, isError = true });
                if (!seat.Active)
                    return Ok(id, new { content = new[] { new { type = "text", text = $"Player {seat.Id} is not in the game yet (title screen, loading or still joining). Wait and try again." } }, isError = true });
                var (ok, text) = await seat.Runtime.Enqueue(tool, args, observation: true);
                return Ok(id, new { content = new[] { new { type = "text", text } }, isError = !ok });
            default:
                return new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Method not found: {method}" } };
        }
    }

    private static object Ok(object id, object result) => new { jsonrpc = "2.0", id, result };

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

    /// <summary>Refresh the overlay state snapshot (call on the host screen's tick, ~4x/sec).</summary>
    public void RefreshState()
    {
        var players = Players.Slots
            .Where(s => s.Screen == 0 || s.Active)
            .Select(s => new
            {
                id = s.Id,
                role = s.Role == SeatRole.Ai ? "ai" : "camera",
                active = s.Active,
                connected = s.Connected,
                state = s.State,
                action = s.Runtime.CurrentTool,
                thinkingSeconds = WorldClock.ThinkingSeconds(s),
                timedOut = s.TimedOut
            })
            .ToArray();

        if (!Context.IsWorldReady)
        {
            StateJson = JsonSerializer.Serialize(new { ready = false, frozen = WorldClock.Frozen, players }, AgentLog.Json);
            return;
        }
        var p = Game1.player;
        StateJson = JsonSerializer.Serialize(new
        {
            ready = true,
            saveFolder = SaveFolder.Name,
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
            action = Players.Host.Runtime.CurrentTool,
            dialogue = Game1.activeClickableMenu is StardewValley.Menus.DialogueBox box
                ? new { speaker = box.characterDialogue?.speaker?.displayName, question = box.isQuestion }
                : null,
            screens = GameRunner.instance.gameInstances.Count,
            plan = PlanBoard.Snapshot(),
            players
        }, AgentLog.Json);
    }
}
