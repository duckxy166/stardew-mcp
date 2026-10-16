using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace StardewMCP.Agent;

/// <summary>What the filming save looks like right now (see docs/filming-save.md for how to make it).</summary>
public sealed record SetupReport(bool SeparateWallets, int Cabins, int CabinsWithFarmhand, string[] Problems);

/// <summary>
/// Farm-wide rules for several AI farmers: a startup check of the save (co-op, separate wallets, 3 cabins) and
/// the end-of-day rule. The day ends when every farmer is in bed, and the game passes each farmer out at 02:00
/// on its own, so one AI that never sleeps cannot hold the day forever. The mod only warns that AI at
/// <see cref="ModConfig.BedtimeWarnTime"/> and never holds the other seats for it (<see cref="WorldClock"/> only
/// freezes the world for AIs that are thinking, and stops that after its thinking timeout).
/// </summary>
public static class FarmRules
{
    private static IMonitor _monitor = null!;
    private static ModConfig _config = null!;
    private static string? _lastSetup;
    private static readonly Stopwatch SinceReady = new();
    private static readonly int[] WarnedDay = Enumerable.Repeat(-1, Players.Seats).ToArray();

    /// <summary>The latest startup check (for /api/state); null until the world has been ready for a few seconds.</summary>
    public static volatile SetupReport? Setup;

    public static void Init(IMonitor monitor, ModConfig config)
    {
        _monitor = monitor;
        _config = config;
    }

    // ------------------------------------------------------------------ startup check

    /// <summary>Read the save's setup. Game thread, world loaded.</summary>
    public static SetupReport CheckSetup()
    {
        int cabins = 0, withFarmhand = 0;
        Utility.ForEachLocation(l =>
        {
            if (l is Cabin c)
            {
                cabins++;
                if (c.HasOwner) withFarmhand++;
            }
            return true;
        });

        bool wallets = Game1.player.team.useSeparateWallets.Value;
        var problems = new List<string>();
        if (!wallets)
            problems.Add("wallets are shared: every farmer spends the same money, and pay has nothing to move. " +
                         "Create the filming save with separate wallets, or switch at Lewis's ledger book (takes effect overnight).");
        if (cabins < Players.Seats - 1)
            problems.Add($"the farm has {cabins} cabin(s) but the three AI farmhands need {Players.Seats - 1}. Start a co-op farm with 3 starting cabins.");
        return new SetupReport(wallets, cabins, withFarmhand, problems.ToArray());
    }

    /// <summary>Host screen, a few times a second: save claims, and log the setup check once per distinct result.</summary>
    public static void HostTick(bool sample)
    {
        Ownership.Flush();
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
        {
            SinceReady.Reset();
            _lastSetup = null;
            Setup = null;
            Array.Fill(WarnedDay, -1);
            return;
        }
        SinceReady.Start();
        // Cabins and the team settings are in place once the first ticks after loading have run.
        if (!sample || SinceReady.ElapsedMilliseconds < 3000)
            return;

        var report = CheckSetup();
        Setup = report;
        var key = string.Join("|", report.Problems);
        if (key == _lastSetup)
            return;
        _lastSetup = key;

        if (report.Problems.Length == 0)
        {
            _monitor.Log($"Setup check: separate wallets on, {report.Cabins} cabins ({report.CabinsWithFarmhand} with a farmhand).", LogLevel.Info);
            return;
        }
        foreach (var problem in report.Problems)
        {
            var text = "Setup check: " + problem;
            _monitor.Log(text, LogLevel.Warn);
            Players.Host.Runtime.Log.Add("system", text: text);
        }
    }

    // ------------------------------------------------------------------ everyone sleeps

    /// <summary>Who is in bed right now: the AI seats that are in the game, by the farmer synced to this screen.</summary>
    public static List<(string id, bool inBed)> SleepStates()
    {
        var farmers = Game1.getOnlineFarmers();
        var list = new List<(string, bool)>();
        foreach (var s in Players.Slots)
        {
            if (s.Role != SeatRole.Ai || !s.Active || s.State is not ScreenState { Ready: true, Name: { } name })
                continue;
            var farmer = farmers.FirstOrDefault(f => f.Name == name);
            if (farmer != null)
                list.Add((s.Id, farmer.isInBed.Value));
        }
        return list;
    }

    /// <summary>A line for <c>status</c> from bedtime on (or once anyone is in bed); "" earlier in the day.</summary>
    public static string SleepLine()
    {
        var states = SleepStates();
        if (states.Count == 0) return "";
        bool late = IsBedtimeConfigured() && Game1.timeOfDay >= _config.BedtimeWarnTime;
        if (!late && !states.Any(s => s.inBed)) return "";
        string Names(bool bed) => string.Join(", ", states.Where(s => s.inBed == bed).Select(s => s.id));
        var inBed = Names(true);
        var up = Names(false);
        return $"Bed: {(inBed.Length > 0 ? inBed : "nobody")} in bed" + (up.Length > 0 ? $", {up} not yet" : "") +
               ". The day ends when everyone is in bed; at 02:00 the game passes out whoever is still up (you lose some gold).";
    }

    /// <summary>For /api/state: who is in bed and the configured warning time.</summary>
    public static object SleepSnapshot() => new
    {
        warnTime = IsBedtimeConfigured() ? _config.BedtimeWarnTime : 0,
        seats = SleepStates().Select(s => new { id = s.id, inBed = s.inBed }).ToList()
    };

    /// <summary>Warn times before 18:00 or past 25:50 are treated as off, so a typo can't nag all day.</summary>
    private static bool IsBedtimeConfigured() => _config.BedtimeWarnTime is >= 1800 and <= 2550;

    /// <summary>Every screen's tick: tell this seat's AI once a day that it is past bedtime and still up.</summary>
    public static void SeatTick(PlayerSlot seat)
    {
        if (seat.Role != SeatRole.Ai || !seat.Connected || !Context.IsWorldReady || !IsBedtimeConfigured())
            return;
        if (Game1.timeOfDay < _config.BedtimeWarnTime || Game1.player.isInBed.Value)
            return;
        int day = Game1.Date.TotalDays;
        if (WarnedDay[seat.Screen] == day)
            return;
        WarnedDay[seat.Screen] = day;

        var others = SleepStates().Where(s => s.id != seat.Id).ToList();
        var waiting = others.Where(s => s.inBed).Select(s => s.id).ToList();
        var text = $"[{WorldText.FormatTime(Game1.timeOfDay)}] It is past bedtime and you are not in bed. The day only ends when everyone is in bed" +
                   (waiting.Count > 0 ? $" ({string.Join(", ", waiting)} already {(waiting.Count == 1 ? "is" : "are")})" : "") +
                   ". At 02:00 the game passes you out where you stand and you lose some gold. Finish up and use go_to_bed.";
        SocialTools.Deliver(seat, text);
        seat.Runtime.Log.Add("system", text: $"{seat.Id} is past bedtime and not in bed (warned)");
    }
}
