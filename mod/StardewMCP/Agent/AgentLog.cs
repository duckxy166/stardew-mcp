using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>One line in the run log / overlay feed.</summary>
public class LogEntry
{
    public long Seq { get; set; }
    public string Ts { get; set; } = "";
    public int Day { get; set; }
    public string Date { get; set; } = "";
    public string GameTime { get; set; } = "";
    public string Location { get; set; } = "";
    /// <summary>think | action | result | diary | system</summary>
    public string Kind { get; set; } = "";
    public string? Tool { get; set; }
    public string? Text { get; set; }
    public object? Args { get; set; }
    public bool? Ok { get; set; }
    public long? Ms { get; set; }
    /// <summary>Player money when the entry was written (overlay's day-card delta).</summary>
    public int? Money { get; set; }
    /// <summary>Player id (p1..p4) the entry belongs to; null for run-wide system lines.</summary>
    public string? Player { get; set; }
    /// <summary>Result lines: the tool's reason code (see <see cref="Reasons"/>), when it set one.</summary>
    public string? Reason { get; set; }
    /// <summary>Result lines: true when the call did only part of what was asked.</summary>
    public bool? Partial { get; set; }
}

/// <summary>Writes the proof-of-run log (JSONL per in-game day) and keeps a feed for the overlay.</summary>
public class AgentLog
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _feed = new();
    private readonly IMonitor _monitor;
    private long _seq;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _baseDir;

    /// <summary>Log folder. Smoke-test saves (smoke_*) log to a "smoke" subfolder so they never mix with a real run.</summary>
    public string Dir
    {
        get
        {
            if (SaveFolder.Name?.StartsWith("smoke_", StringComparison.OrdinalIgnoreCase) != true) return _baseDir;
            var dir = Path.Combine(_baseDir, "smoke");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Free-form metadata posted by the runner (model, session id...). Shown on the overlay.</summary>
    public Dictionary<string, string> Meta { get; } = new();

    public AgentLog(string dir, IMonitor monitor)
    {
        _baseDir = dir;
        _monitor = monitor;
        Directory.CreateDirectory(_baseDir);
        _seq = DateTime.UtcNow.Ticks / 10000; // monotonic across restarts
    }

    /// <summary>Days since the farm started, 1-based (spring 1 year 1 = day 1).</summary>
    public static int FarmDay => Context.IsWorldReady ? Game1.Date.TotalDays + 1 : 0;

    /// <summary>A view of this log that stamps every entry with one player's id.</summary>
    public PlayerLog For(string player) => new(this, player);

    public LogEntry Add(string kind, string? tool = null, string? text = null, object? args = null, bool? ok = null, long? ms = null, string? player = null, ToolOutcome? outcome = null)
    {
        var e = new LogEntry
        {
            Ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            Day = FarmDay,
            Date = Context.IsWorldReady ? $"{Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}" : "",
            GameTime = Context.IsWorldReady ? WorldText.FormatTime(Game1.timeOfDay) : "",
            Location = Game1.currentLocation?.Name ?? "",
            Kind = kind,
            Tool = tool,
            Text = text,
            Args = args,
            Ok = ok,
            Ms = ms,
            Money = Context.IsWorldReady ? Game1.player.Money : null,
            Player = player,
            Reason = outcome?.Reason,
            Partial = outcome is { Partial: true } ? true : null
        };

        lock (_lock)
        {
            e.Seq = ++_seq;
            _feed.Add(e);
            if (_feed.Count > 500)
                _feed.RemoveRange(0, _feed.Count - 500);
            try
            {
                File.AppendAllText(DayFile(e.Day), JsonSerializer.Serialize(e, Json) + "\n", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Log write failed: {ex.Message}", LogLevel.Warn);
            }
        }
        if (kind == "result")
            DaySummary.Touch(this, e.Day, _monitor);
        return e;
    }

    public List<LogEntry> FeedAfter(long seq)
    {
        lock (_lock)
            return _feed.Where(e => e.Seq > seq).ToList();
    }

    public string DayFile(int day) => Path.Combine(Dir, $"day_{day:000}.jsonl");

    /// <summary>Diary of one player; p1 (or a single-player run) keeps the original file name.</summary>
    public string DiaryFile(int day, string? player = null) =>
        Path.Combine(Dir, player is null or "p1" ? $"diary_{day:000}.md" : $"diary_{day:000}_{player}.md");

    public void WriteDiary(int day, string text, string? player = null)
    {
        var header = $"# Day {day} — {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}\n\n";
        File.WriteAllText(DiaryFile(day, player), header + text.Trim() + "\n", Encoding.UTF8);
        Add("diary", text: text, player: player);
    }

    public List<int> Days()
    {
        return Directory.GetFiles(Dir, "day_*.jsonl")
            .Select(f => Path.GetFileNameWithoutExtension(f).Substring(4))
            .Select(s => int.TryParse(s, out var d) ? d : 0)
            .Where(d => d > 0)
            .OrderBy(d => d)
            .ToList();
    }
}

/// <summary>One player's side of the shared <see cref="AgentLog"/>.</summary>
public class PlayerLog
{
    private readonly AgentLog _log;

    public string Player { get; }

    public PlayerLog(AgentLog log, string player)
    {
        _log = log;
        Player = player;
    }

    public LogEntry Add(string kind, string? tool = null, string? text = null, object? args = null, bool? ok = null, long? ms = null, ToolOutcome? outcome = null) =>
        _log.Add(kind, tool, text, args, ok, ms, Player, outcome);

    public void WriteDiary(int day, string text) => _log.WriteDiary(day, text, Player);
}
