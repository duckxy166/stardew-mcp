using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// THROWAWAY spike for issue #37: local split-screen with mod-driven farmers.
///   - one AgentRuntime per screen (index = Context.ScreenId), ticked from that screen's UpdateTicked
///   - POST /api/debug/p{N}/{tool}   runs an MCP tool (or debug op) on screen N-1
///   - POST /api/debug/spike/{op}    host-side control ops: cabins, spawn, freeze, screens, window, fps
///   - secondary screens auto-pick a farmhand slot and auto-finish character creation
/// </summary>
public static class SplitScreenSpike
{
    public static readonly AgentRuntime?[] Runtimes = new AgentRuntime?[4];
    private static IModHelper _helper = null!;
    private static IMonitor _monitor = null!;

    private static readonly ConcurrentQueue<(Func<object> run, TaskCompletionSource<object> done)> _control = new();

    // Per-screen snapshot, written on each screen's tick, read by the HTTP thread.
    private static readonly object[] _snap = new object[4];

    // Frame counters (screen 0 only).
    private static int _frames, _updates;
    private static readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
    public static double Fps, Ups;
    private static readonly int[] _joinTicks = new int[4];

    public static void Init(IModHelper helper, IMonitor monitor, ModConfig config, string logDir, AgentRuntime host)
    {
        _helper = helper;
        _monitor = monitor;
        Runtimes[0] = host;
        for (int i = 1; i < 4; i++)
            Runtimes[i] = new AgentRuntime(helper, monitor, config, new AgentLog(Path.Combine(logDir, $"p{i + 1}"), monitor));
    }

    public static Task<object> Control(Func<object> run)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _control.Enqueue((run, tcs));
        return tcs.Task;
    }

    /// <summary>Called from UpdateTicked for every screen.</summary>
    public static void Tick(uint ticks)
    {
        int s = Context.ScreenId;
        if (s == 0)
        {
            _updates++;
            // A joining farmhand instance gets fresh Options again when it enters loadingMode, and SMAPI raises
            // no UpdateTicked for it while loading; with the window unfocused its update is then skipped forever
            // (Game1._update: !IsActive && pauseWhenOutOfFocus && multiplayerMode == 0). Fix it from the host.
            foreach (var g in GameRunner.instance.gameInstances)
                if (g.instanceOptions is { pauseWhenOutOfFocus: true } o)
                    o.pauseWhenOutOfFocus = false;
            while (_control.TryDequeue(out var c))
            {
                try { c.done.TrySetResult(c.run()); }
                catch (Exception ex) { c.done.TrySetResult(new { ok = false, error = ex.ToString() }); }
            }
            if (_fpsWatch.ElapsedMilliseconds >= 2000)
            {
                double sec = _fpsWatch.Elapsed.TotalSeconds;
                Fps = _frames / sec;
                Ups = _updates / sec;
                _frames = _updates = 0;
                _fpsWatch.Restart();
            }
        }
        else
        {
            AutoJoin(s);
        }

        if (ticks % 10 == 0)
            _snap[s] = Snapshot(s);
    }

    public static void OnRendered()
    {
        if (Context.ScreenId == 0) _frames++;
    }

    private static object Snapshot(int s)
    {
        var rt = Runtimes[s];
        var g = Game1.game1;
        return new
        {
            screen = s,
            instanceIndex = g?.instanceIndex,
            playerIndex = g?.instancePlayerOneIndex.ToString(),
            window = g == null ? null : new { g.localMultiplayerWindow.X, g.localMultiplayerWindow.Y, g.localMultiplayerWindow.Width, g.localMultiplayerWindow.Height },
            zoom = Game1.options?.zoomLevel,
            uiScale = Game1.options?.uiScale,
            ready = Context.IsWorldReady,
            isMain = Context.IsMainPlayer,
            player = Context.IsWorldReady ? Game1.player.Name : null,
            id = Context.IsWorldReady ? Game1.player.UniqueMultiplayerID : 0,
            location = Game1.currentLocation?.Name,
            tile = Context.IsWorldReady ? new[] { Game1.player.TilePoint.X, Game1.player.TilePoint.Y } : null,
            pos = Context.IsWorldReady ? new[] { (int)Game1.player.Position.X, (int)Game1.player.Position.Y } : null,
            moving = Context.IsWorldReady && Game1.player.isMoving(),
            time = Context.IsWorldReady ? Game1.timeOfDay : 0,
            gameTimeInterval = Game1.gameTimeInterval,
            hostPaused = Context.IsWorldReady && Game1.HostPaused,
            menu = Game1.activeClickableMenu?.GetType().Name,
            dialogue = Game1.activeClickableMenu is DialogueBox db ? db.getCurrentString() : null,
            action = rt?.CurrentTool,
            client = ClientInfo(),
            server = s == 0 && Game1.server != null ? Game1.server.GetType().Name + " connected=" + Game1.server.connected() : null,
        };
    }

    private static object? ClientInfo()
    {
        var c = (Game1.activeClickableMenu as FarmhandMenu)?.client ?? Game1.client;
        if (c == null) return null;
        object? F(string n) { try { return _helper.Reflection.GetField<object>(c, n, required: false)?.GetValue()?.ToString(); } catch { return null; } }
        return new
        {
            type = c.GetType().FullName,
            c.connectionStarted, c.timedOut, c.readyToPlay, c.hasHandshaked, c.connectionMessage,
            serverDiscovered = F("serverDiscovered"), retryAttempts = F("retryAttempts"), address = F("address"),
            farmhands = c.availableFarmhands?.Count,
            drawn = (Game1.activeClickableMenu as FarmhandMenu)?.drawn,
        };
    }

    /// <summary>Per-screen op (POST /api/debug/p{N}/spike_dialogue): open a dialogue box on that screen only.</summary>
    public static readonly ToolDef DialogueOp = new()
    {
        Name = "spike_dialogue", Description = "debug", Schema = new { }, Instant = true, AppendStatus = false,
        Run = (a, c) => DialogueRun(c),
    };

    private static System.Collections.IEnumerator DialogueRun(CallContext c)
    {
        Game1.drawObjectDialogue($"Spike: dialogue for {Game1.player.Name} on screen {Context.ScreenId}");
        yield return null;
        yield return null;
        c.Result = $"menu on screen {Context.ScreenId}: {Game1.activeClickableMenu?.GetType().Name}";
    }

    /// <summary>Per-screen op (POST /api/debug/p{N}/spike_zoom {zoom, ui}): set that screen's base zoom / UI scale.</summary>
    public static readonly ToolDef ZoomOp = new()
    {
        Name = "spike_zoom", Description = "debug", Schema = new { }, Instant = true, AppendStatus = false,
        Run = (a, c) => ZoomRun(a, c),
    };

    private static System.Collections.IEnumerator ZoomRun(JsonElement a, CallContext c)
    {
        float? F(string n) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
        SplitScreenLayout.SetZoom(F("zoom"), F("ui"));
        yield return null; // Game1.Update applies desired zoom/UI scale and rebuilds this screen's render targets
        yield return null;
        var g = Game1.game1;
        c.Result = JsonSerializer.Serialize(new
        {
            screen = Context.ScreenId,
            baseZoom = Game1.options.baseZoomLevel, effectiveZoom = Game1.options.zoomLevel,
            baseUi = Game1.options.baseUIScale, effectiveUi = Game1.options.uiScale,
            window = new[] { g.localMultiplayerWindow.X, g.localMultiplayerWindow.Y, g.localMultiplayerWindow.Width, g.localMultiplayerWindow.Height },
            viewportTiles = new[] { Game1.viewport.Width / 64f, Game1.viewport.Height / 64f },
            screenTarget = new[] { Game1.game1.screen?.Width ?? 0, Game1.game1.screen?.Height ?? 0 },
        });
    }

    // ------------------------------------------------------------------ joining

    private static readonly string[] Names = { "Claude", "Gemini", "GPT", "P4" };

    /// <summary>Secondary screens: pick a farmhand in FarmhandMenu, then finish CharacterCustomization.</summary>
    private static void AutoJoin(int s)
    {
        // A fresh instance has default options: with pauseWhenOutOfFocus it skips its whole update while
        // the window is unfocused (multiplayerMode is still 0), so the farmhand menu never opens.
        if (Game1.options != null && Game1.options.pauseWhenOutOfFocus)
            Game1.options.pauseWhenOutOfFocus = false;
        switch (Game1.activeClickableMenu)
        {
            case FarmhandMenu fm:
                if (fm.approvingFarmhand) return;
                var slots = fm.MenuSlots.OfType<FarmhandMenu.FarmhandSlot>().ToList();
                if (slots.Count == 0) { _joinTicks[s] = 0; return; }
                if (++_joinTicks[s] < 30) return; // let the menu settle for half a second
                // Prefer an uncustomized (new) farmhand so creation is exercised; else the first one.
                var pick = slots.FirstOrDefault(sl => !sl.Farmer.isCustomized.Value) ?? slots[0];
                _monitor.Log($"[spike] screen {s}: picking farmhand slot '{pick.Farmer.Name}' (customized={pick.Farmer.isCustomized.Value}) of {slots.Count}", LogLevel.Info);
                _joinTicks[s] = 0;
                pick.Activate();
                return;

            case CharacterCustomization cc when cc.source == CharacterCustomization.Source.NewFarmhand:
                if (++_joinTicks[s] < 30) return;
                _joinTicks[s] = 0;
                var name = Names[s];
                _helper.Reflection.GetField<TextBox>(cc, "nameBox").GetValue().Text = name;
                _helper.Reflection.GetField<TextBox>(cc, "favThingBox").GetValue().Text = "Farming";
                Game1.player.Name = name;
                Game1.player.displayName = name;
                Game1.player.favoriteThing.Value = "Farming";
                if (string.IsNullOrEmpty(Game1.player.farmName.Value))
                    Game1.player.farmName.Value = Game1.MasterPlayer.farmName.Value;
                _monitor.Log($"[spike] screen {s}: finishing character creation as {name}", LogLevel.Info);
                _helper.Reflection.GetMethod(cc, "optionButtonClick").Invoke("OK");
                return;
        }
    }

    // ------------------------------------------------------------------ control ops (host screen)

    public static object RunOp(string op, JsonElement a)
    {
        switch (op)
        {
            case "screens":
                return new { ok = true, instances = GameRunner.instance.gameInstances.Count, fps = Math.Round(Fps, 1), ups = Math.Round(Ups, 1), backBuffer = new[] { Game1.graphics.PreferredBackBufferWidth, Game1.graphics.PreferredBackBufferHeight }, client = new[] { Game1.game1.Window.ClientBounds.Width, Game1.game1.Window.ClientBounds.Height }, screens = _snap.Where(x => x != null).ToArray() };

            case "cabins":
                return BuildCabins(I(a, "n", 2));

            case "spawn":
            {
                var used = GameRunner.instance.gameInstances.Select(g => g.instancePlayerOneIndex).ToHashSet();
                for (var idx = PlayerIndex.Two; idx <= PlayerIndex.Four; idx++)
                {
                    if (used.Contains(idx)) continue;
                    GameRunner.instance.AddGameInstance(idx);
                    return new { ok = true, added = idx.ToString(), instances = GameRunner.instance.gameInstances.Count };
                }
                return new { ok = false, error = "no free PlayerIndex" };
            }

            case "freeze":
            {
                bool on = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("on", out var v) && v.ValueKind == JsonValueKind.True;
                Game1.netWorldState.Value.IsPaused = on;
                return new { ok = true, paused = Game1.netWorldState.Value.IsPaused };
            }

            case "window":
            {
                int w = I(a, "w", 1280), h = I(a, "h", 720);
                Game1.graphics.PreferredBackBufferWidth = w;
                Game1.graphics.PreferredBackBufferHeight = h;
                Game1.graphics.ApplyChanges();
                Game1.game1.Window_ClientSizeChanged(null, null);
                return new { ok = true, backBuffer = new[] { Game1.graphics.PreferredBackBufferWidth, Game1.graphics.PreferredBackBufferHeight }, client = new[] { Game1.game1.Window.ClientBounds.Width, Game1.game1.Window.ClientBounds.Height } };
            }

            case "layout":
            {
                if (a.ValueKind == JsonValueKind.Object)
                {
                    if (a.TryGetProperty("grid3", out var g3)) SplitScreenLayout.Grid3 = g3.GetBoolean();
                    if (a.TryGetProperty("hidePaused", out var hp)) SplitScreenLayout.HidePausedBanner = hp.GetBoolean();
                    if (a.TryGetProperty("hideJoin", out var hj)) SplitScreenLayout.HideJoinChat = hj.GetBoolean();
                    if (a.TryGetProperty("hideCursor", out var hc)) SplitScreenLayout.HideCursor = hc.GetBoolean();
                    if (a.TryGetProperty("lockP1", out var lk)) SplitScreenLayout.LockP1Input = lk.GetBoolean();
                }
                Game1.game1.Window_ClientSizeChanged(null, null);
                return new { ok = true, SplitScreenLayout.Grid3, SplitScreenLayout.HidePausedBanner, SplitScreenLayout.HideJoinChat, SplitScreenLayout.HideCursor, SplitScreenLayout.LockP1Input };
            }

            case "capture":
            {
                string path = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("path", out var p) ? p.GetString()! : Path.Combine(Path.GetTempPath(), "stardew-capture.png");
                SplitScreenLayout.LastCapture = null;
                SplitScreenLayout.PendingCapture = path;
                return new { ok = true, pending = path };
            }

            case "input":
            {
                var rawM = Microsoft.Xna.Framework.Input.Mouse.GetState();
                var rawK = Microsoft.Xna.Framework.Input.Keyboard.GetState();
                var gm = Game1.input.GetMouseState();
                return new
                {
                    ok = true, screen = Context.ScreenId, locked = SplitScreenLayout.LockP1Input,
                    rawMouse = new[] { rawM.X, rawM.Y }, rawKeys = rawK.GetPressedKeys().Select(k => k.ToString()).ToArray(),
                    gameMouse = new[] { gm.X, gm.Y }, gameKeys = Game1.input.GetKeyboardState().GetPressedKeys().Select(k => k.ToString()).ToArray(),
                    gameMouseXY = new[] { Game1.getMouseX(), Game1.getMouseY() },
                    focused = Game1.game1.IsActive,
                    p1 = new[] { (int)Game1.player.Position.X, (int)Game1.player.Position.Y },
                    paused = Game1.HostPaused,
                };
            }

            case "capture_status":
                return new { ok = true, last = SplitScreenLayout.LastCapture };

            case "chat_clear":
                GameRunner.instance.ExecuteForInstances(_ => Game1.chatBox?.messages.Clear());
                return new { ok = true };

            case "fullscreen":
            {
                // Exclusive fullscreen at a given mode (e.g. a DLDSR one). Sets options directly, so
                // startup_preferences is not written (Options.setWindowedOption would save it).
                Game1.options.preferredResolutionX = I(a, "w", 2560);
                Game1.options.preferredResolutionY = I(a, "h", 1440);
                Game1.options.fullscreen = true;
                Game1.options.windowedBorderlessFullscreen = false;
                Game1.toggleNonBorderlessWindowedFullscreen();
                return new { ok = true, Game1.graphics.IsFullScreen, Game1.graphics.HardwareModeSwitch, backBuffer = new[] { Game1.graphics.PreferredBackBufferWidth, Game1.graphics.PreferredBackBufferHeight } };
            }

            case "borderless":
            {
                Game1.options.preferredResolutionX = 1920;
                Game1.options.preferredResolutionY = 1080;
                Game1.options.fullscreen = false;
                Game1.options.windowedBorderlessFullscreen = true;
                Game1.toggleFullscreen();
                return new { ok = true, Game1.graphics.IsFullScreen, Game1.graphics.HardwareModeSwitch, backBuffer = new[] { Game1.graphics.PreferredBackBufferWidth, Game1.graphics.PreferredBackBufferHeight } };
            }

            case "focus":
            {
                var w = Game1.game1.Window;
                return new { ok = true, active = GameRunner.instance.IsActive, client = new[] { w.ClientBounds.Width, w.ClientBounds.Height }, Game1.graphics.IsFullScreen, Game1.graphics.HardwareModeSwitch };
            }

            case "nosleep":
                // MonoGame sleeps 20 ms per frame while the window is unfocused (OBS/terminal in front).
                GameRunner.instance.InactiveSleepTime = TimeSpan.Zero;
                return new { ok = true };

            case "remove":
            {
                int idx = I(a, "screen", -1);
                if (idx <= 0 || idx >= GameRunner.instance.gameInstances.Count) return new { ok = false, error = "screen must be 1..n-1" };
                GameRunner.instance.RemoveGameInstance(GameRunner.instance.gameInstances[idx]);
                return new { ok = true };
            }

            default:
                return new { ok = false, error = $"unknown op {op}" };
        }
    }

    private static object BuildCabins(int n)
    {
        var farm = Game1.getFarm();
        var built = new List<string>();
        for (int y = 8; y < farm.Map.Layers[0].LayerHeight - 6 && built.Count < n; y++)
        {
            for (int x = 8; x < farm.Map.Layers[0].LayerWidth - 8 && built.Count < n; x++)
            {
                var tile = new Vector2(x, y);
                // Cheap pre-check so buildStructure does not spam "can't build here" messages.
                // Debris (weeds, stones, twigs) blocks building on an early farm: clear it on this smoke copy.
                bool clear = true;
                for (int dy = 0; dy < 4 && clear; dy++)
                    for (int dx = 0; dx < 5 && clear; dx++)
                    {
                        var t = new Vector2(x + dx, y + dy);
                        if (farm.doesTileHaveProperty(x + dx, y + dy, "Buildable", "Back") == null && farm.doesTileHaveProperty(x + dx, y + dy, "Diggable", "Back") == null) clear = false;
                        else if (farm.getBuildingAt(t) != null || farm.resourceClumps.Any(r => r.occupiesTile(x + dx, y + dy))) clear = false;
                    }
                if (!clear) continue;
                for (int dy = 0; dy < 4; dy++)
                    for (int dx = 0; dx < 5; dx++)
                    {
                        farm.objects.Remove(new Vector2(x + dx, y + dy));
                        farm.terrainFeatures.Remove(new Vector2(x + dx, y + dy));
                    }
                if (!farm.buildStructure("Cabin", tile, Game1.player, out var b, magicalConstruction: true, skipSafetyChecks: true)) continue;
                if (b.GetIndoors() is Cabin cabin && !cabin.HasOwner) cabin.CreateFarmhand();
                built.Add($"({x},{y}) owner={(b.GetIndoors() as Cabin)?.HasOwner}");
                x += 6;
            }
        }
        int free = 0;
        Utility.ForEachLocation(l => { if (l is Cabin c && (!c.HasOwner || !c.IsOwnerActivated)) free++; return true; });
        return new { ok = built.Count == n, built, freeCabins = free };
    }

    private static int I(JsonElement a, string name, int def) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
}
