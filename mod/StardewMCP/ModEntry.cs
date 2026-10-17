using System.IO;
using StardewMCP.Agent;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP;

/// <summary>
/// SMAPI entry point. Hosts the agents' MCP endpoints inside the game and drives each split-screen
/// seat's runtime from that screen's game loop (SMAPI raises UpdateTicked once per screen).
/// </summary>
public class ModEntry : Mod
{
    private ModConfig _config = new();
    private AgentLog _log = null!;
    private AgentHttp _http = null!;
    private bool _autoLoadDone;

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<ModConfig>();
        string logDir = string.IsNullOrWhiteSpace(_config.LogDir)
            ? Path.Combine(helper.DirectoryPath, "logs")
            : _config.LogDir;

        _log = new AgentLog(logDir, Monitor);
        AgentTools.Config = _config;
        Players.Init(helper, Monitor, _config, _log);
        WorldClock.Init(_config);
        PassOut.Init(_log);
        SplitScreen.Init(helper, Monitor, _config);
        SplitScreenPatches.Apply(ModManifest, _config);
        SocialTools.Apply(ModManifest);
        AgentTools.Apply(ModManifest);
        _http = new AgentHttp(_log, _config, Monitor, Path.Combine(helper.DirectoryPath, "assets"));

        helper.Events.GameLoop.GameLaunched += (_, _) => _http.Start(_config.HttpPort);
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            SplitScreen.OnReturnedToTitle();
            PassOut.OnReturnedToTitle();
        };
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayStarted += (_, _) => Briefing.OnDayStarted();

        helper.ConsoleCommands.Add(
            "mcp_freeze",
            "Freeze the world while an AI is thinking.\n\nUsage: mcp_freeze [on|off] (no argument toggles)",
            OnFreezeCommand);

        Monitor.Log($"Stardew MCP loaded{(_config.SplitScreen ? " (split-screen: 3 AIs + camera)" : "")}. Logs: {logDir}", LogLevel.Info);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        // Also raised on each farmhand screen when it joins (before its character exists);
        // SplitScreen logs those joins once they finish.
        SplitScreen.OnSaveLoaded();
        // The agents keep playing while the terminal or OBS window has focus.
        Game1.options.pauseWhenOutOfFocus = false;
        if (Context.ScreenId != 0)
            return;
        SaveFolder.OnSaveLoaded();
        Players.Host.Runtime.Log.Add("system", text: $"Save loaded: {Game1.player.Name} / {Game1.player.farmName} Farm");
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        var seat = Players.Current;
        seat.OnTick();

        if (seat.Screen == 0)
        {
            TryAutoLoad(e.Ticks);
            DebugApi.Tick();
            SplitScreen.HostTick();
        }
        else
            SplitScreen.GuestTick(seat.Screen);
        SplitScreen.ScreenTick();

        seat.Runtime.Tick();
        Briefing.Tick(seat, e.IsMultipleOf(300));
        if (e.IsMultipleOf(15))
            seat.State = ScreenState.Capture();

        if (seat.Screen == 0)
        {
            WorldClock.Tick();
            if (e.IsMultipleOf(15))
                _http.RefreshState();
        }
    }

    /// <summary>For unattended runs: load the configured save once the title screen is up.</summary>
    private void TryAutoLoad(uint ticks)
    {
        if (_autoLoadDone || string.IsNullOrEmpty(_config.AutoLoadSave))
            return;
        if (ticks <= 120 || Game1.activeClickableMenu is not TitleMenu)
            return;

        _autoLoadDone = true;
        SaveFolder.Pending = _config.AutoLoadSave;
        Monitor.Log($"Auto-loading save {_config.AutoLoadSave}", LogLevel.Info);
        SaveGame.Load(_config.AutoLoadSave);
        Game1.exitActiveMenu();
    }

    private void OnFreezeCommand(string command, string[] args)
    {
        _config.FreezeTimeWhileThinking = args.Length == 0
            ? !_config.FreezeTimeWhileThinking
            : args[0] != "off";
        Monitor.Log($"Freeze while thinking: {(_config.FreezeTimeWhileThinking ? "on" : "off")}", LogLevel.Info);
    }
}
