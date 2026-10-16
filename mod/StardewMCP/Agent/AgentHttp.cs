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
///   POST /mcp        MCP (JSON-RPC over streamable HTTP, JSON responses only)
///   GET  /overlay    OBS browser-source overlay (Thai thoughts + actions)
///   GET  /replay     day-by-day replay of the logs
///   GET  /api/feed   overlay feed, /api/days, /api/day?n=, /api/state
///   POST /api/meta   runner metadata (model, session) shown on the overlay
/// </summary>
public class AgentHttp
{
    private readonly AgentRuntime _rt;
    private readonly IMonitor _monitor;
    private readonly string _assetDir;
    private readonly Dictionary<string, ToolDef> _tools;
    private readonly Dictionary<string, ToolDef> _debug = DebugApi.All();
    private HttpListener? _listener;
    private const string ProtocolFallback = "2025-06-18";

    // Snapshot of game state for the overlay, refreshed on the game thread.
    public volatile string StateJson = "{}";

    public AgentHttp(AgentRuntime rt, IMonitor monitor, string assetDir)
    {
        _rt = rt;
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
        _monitor.Log($"Agent MCP at http://localhost:{port}/mcp · overlay /overlay · replay /replay", LogLevel.Info);
        if (_rt.Config.EnableDebugApi)
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
            if (_rt.Config.EnableDebugApi && path.StartsWith("/api/debug/spike/"))
            {
                await HandleSpikeControl(req, res, path.Substring("/api/debug/spike/".Length));
                return;
            }
            if (_rt.Config.EnableDebugApi && path.StartsWith("/api/debug/p") && path.Length > 13 && path[13] == '/')
            {
                await HandleSpikePlayer(req, res, path[12] - '1', path.Substring(14));
                return;
            }
            if (path.StartsWith("/api/debug/"))
            {
                await HandleDebug(req, res, path.Substring("/api/debug/".Length));
                return;
            }
            switch (path)
            {
                case "/mcp" when req.HttpMethod == "POST":
                    await HandleMcp(req, res);
                    return;
                case "/mcp":
                    res.StatusCode = req.HttpMethod == "DELETE" ? 200 : 405;
                    res.Close();
                    return;
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
                        meta = _rt.Log.Meta,
                        entries = _rt.Log.FeedAfter(after)
                    });
                    return;
                case "/api/state":
                    await Text(res, StateJson, "application/json");
                    return;
                case "/api/days":
                    await Json(res, _rt.Log.Days());
                    return;
                case "/api/day":
                    int.TryParse(req.QueryString["n"], out var n);
                    var log = System.IO.File.Exists(_rt.Log.DayFile(n)) ? await System.IO.File.ReadAllTextAsync(_rt.Log.DayFile(n), Encoding.UTF8) : "";
                    var diary = System.IO.File.Exists(_rt.Log.DiaryFile(n)) ? await System.IO.File.ReadAllTextAsync(_rt.Log.DiaryFile(n), Encoding.UTF8) : "";
                    await Json(res, new { day = n, diary, entries = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement) });
                    return;
                case "/api/meta" when req.HttpMethod == "POST":
                    using (var r = new StreamReader(req.InputStream, Encoding.UTF8))
                    {
                        var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(await r.ReadToEndAsync()) ?? new();
                        lock (_rt.Log.Meta)
                            foreach (var kv in meta) _rt.Log.Meta[kv.Key] = kv.Value;
                        if (meta.TryGetValue("event", out var ev)) _rt.Log.Add("system", text: ev);
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

    private async Task HandleDebug(HttpListenerRequest req, HttpListenerResponse res, string op)
    {
        if (!_rt.Config.EnableDebugApi || !_debug.TryGetValue(op, out var tool))
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

        var (ok, text) = await _rt.Enqueue(tool, args);
        await Json(res, new { ok, text });
    }

    // ------------------------------------------------------------------ spike #37 (split-screen)

    private static async Task<JsonElement> Body(HttpListenerRequest req)
    {
        using var r = new StreamReader(req.InputStream, Encoding.UTF8);
        var body = await r.ReadToEndAsync();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone();
    }

    private async Task HandleSpikeControl(HttpListenerRequest req, HttpListenerResponse res, string op)
    {
        var args = await Body(req);
        await Json(res, await SplitScreenSpike.Control(() => SplitScreenSpike.RunOp(op, args)));
    }

    /// <summary>POST /api/debug/p{N}/{tool}: run an MCP tool or debug op on screen N-1.</summary>
    private async Task HandleSpikePlayer(HttpListenerRequest req, HttpListenerResponse res, int screen, string name)
    {
        var rt = screen >= 0 && screen < 4 ? SplitScreenSpike.Runtimes[screen] : null;
        ToolDef? tool = name == "spike_dialogue" ? SplitScreenSpike.DialogueOp : name == "spike_zoom" ? SplitScreenSpike.ZoomOp : null;
        if (rt == null || !(tool != null || _tools.TryGetValue(name, out tool) || _debug.TryGetValue(name, out tool)))
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }
        var args = await Body(req);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, text) = await rt.Enqueue(tool!, args);
        await Json(res, new { ok, text, ms = sw.ElapsedMilliseconds });
    }

    // ------------------------------------------------------------------ MCP

    private async Task HandleMcp(HttpListenerRequest req, HttpListenerResponse res)
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
                var o = await Dispatch(m);
                if (o != null) outs.Add(o);
            }
            if (outs.Count == 0) { res.StatusCode = 202; res.Close(); return; }
            await Json(res, outs);
            return;
        }

        var reply = await Dispatch(root);
        if (reply == null) { res.StatusCode = 202; res.Close(); return; }
        res.Headers["Mcp-Session-Id"] = "stardew";
        await Json(res, reply);
    }

    private async Task<object?> Dispatch(JsonElement msg)
    {
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
                    instructions = "You are playing Stardew Valley. Game time is frozen while you think and only moves while an action runs."
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
                var (ok, text) = await _rt.Enqueue(tool, args);
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

    /// <summary>Refresh the overlay state snapshot (call on the game thread, ~4x/sec).</summary>
    public void RefreshState()
    {
        if (!Context.IsWorldReady)
        {
            StateJson = JsonSerializer.Serialize(new { ready = false, frozen = _rt.Frozen }, AgentLog.Json);
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
            frozen = _rt.Frozen,
            action = _rt.CurrentTool,
            dialogue = Game1.activeClickableMenu is StardewValley.Menus.DialogueBox box
                ? new { speaker = box.characterDialogue?.speaker?.displayName, question = box.isQuestion }
                : null
        }, AgentLog.Json);
    }
}
