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
    private readonly object _memoryLock = new();
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

    /// <summary>
    /// A player's own long-term memory file (lessons, plan, notes...). p1 (or a single-player run) keeps the plain
    /// name like the diary; other seats get a "_p2" suffix. Per-seat character notes (#48: self, people) belong here too.
    /// </summary>
    public string MemoryFile(string stem, string? player = null, string ext = "md") =>
        Path.Combine(Dir, player is null or "p1" ? $"{stem}.{ext}" : $"{stem}_{player}.{ext}");

    /// <summary>What the player learned, one "- Day N: ..." line per lesson; appended by the journal tool.</summary>
    public string LessonsFile(string? player = null) => MemoryFile("lessons", player);

    /// <summary>The player's plan (## Season, ## Week, ## Today); replaced by the journal tool.</summary>
    public string PlanFile(string? player = null) => MemoryFile("plan", player);

    /// <summary>Facts the player found out, one "- [topic, day N] ..." line each; appended by the journal tool.</summary>
    public string NotesFile(string? player = null) => MemoryFile("notes", player);

    /// <summary>"spring 3 Y1" for a 1-based farm day (28 days a season, 4 seasons a year).</summary>
    public static string DateLabel(int day)
    {
        int t = Math.Max(day, 1) - 1;
        string[] seasons = { "spring", "summer", "fall", "winter" };
        return $"{seasons[t / 28 % 4]} {t % 28 + 1} Y{t / 112 + 1}";
    }

    private readonly HashSet<string> _journals = new();
    private string? _journalsFrom;

    private string JournalsFile => Path.Combine(Dir, "journals.txt");

    private static string JournalKey(int day, string? player) => $"{SaveFolder.Name}|{player ?? "p1"}|{day}";

    private void LoadJournals()
    {
        var path = JournalsFile;
        if (_journalsFrom == path) return; // the real log folder and the smoke one are told apart
        _journalsFrom = path;
        _journals.Clear();
        if (File.Exists(path))
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                if (line.Length > 0) _journals.Add(line);
    }

    /// <summary>
    /// True once the player's journal for <paramref name="day"/> of the loaded save is written (see <see cref="MarkJournal"/>).
    /// Remembered in journals.txt, so it survives a game restart, and keyed by save folder, so a reloaded
    /// or fresh save never inherits another run's days. go_to_bed requires today's.
    /// </summary>
    public bool HasJournal(int day, string? player = null)
    {
        if (day <= 0) return false;
        lock (_memoryLock)
        {
            LoadJournals();
            return _journals.Contains(JournalKey(day, player));
        }
    }

    /// <summary>Record that the player's journal for <paramref name="day"/> is complete. Call it after the diary, lessons and plan are written.</summary>
    public void MarkJournal(int day, string? player = null)
    {
        lock (_memoryLock)
        {
            LoadJournals();
            var key = JournalKey(day, player);
            if (_journals.Add(key))
                File.AppendAllText(JournalsFile, key + "\n", Encoding.UTF8);
        }
    }

    public void WriteDiary(int day, string text, string? player = null)
    {
        var header = $"# Day {day} — {DateLabel(day)}\n\n";
        lock (_memoryLock)
            File.WriteAllText(DiaryFile(day, player), header + text.Trim() + "\n", Encoding.UTF8);
        // A catch-up diary (day < today) is logged on today's page; args say which day it belongs to.
        Add("diary", text: text, args: day == FarmDay ? (object?)null : new { day }, player: player);
    }

    /// <summary>
    /// Add one day's lessons to lessons.md as "- Day N: text" lines. Lines already written for that day are replaced,
    /// so calling the journal twice in a day never duplicates. Returns the number of lines in the file.
    /// </summary>
    public int WriteLessons(int day, IEnumerable<string> lessons, string? player = null)
    {
        lock (_memoryLock)
        {
            var prefix = $"- Day {day}: ";
            var lines = ReadLines(LessonsFile(player)).Where(l => !l.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            lines.AddRange(lessons.Select(l => prefix + l));
            File.WriteAllText(LessonsFile(player), string.Join("\n", lines) + "\n", Encoding.UTF8);
            return lines.Count;
        }
    }

    /// <summary>Replace plan.md with <paramref name="plan"/>.</summary>
    public void WritePlan(string plan, string? player = null)
    {
        lock (_memoryLock)
            File.WriteAllText(PlanFile(player), plan.Trim() + "\n", Encoding.UTF8);
    }

    /// <summary>
    /// Append facts to notes.md as "- [topic, day N] text". A fact that is already there (same text) is skipped.
    /// Returns (facts added, lines in the file).
    /// </summary>
    public (int added, int total) AppendNotes(int day, IEnumerable<(string topic, string text)> notes, string? player = null)
    {
        lock (_memoryLock)
        {
            var lines = ReadLines(NotesFile(player));
            int added = 0;
            foreach (var (topic, text) in notes)
            {
                if (lines.Any(l => l.EndsWith("] " + text, StringComparison.OrdinalIgnoreCase))) continue;
                lines.Add($"- [{topic}, day {day}] {text}");
                added++;
            }
            if (added > 0)
                File.WriteAllText(NotesFile(player), string.Join("\n", lines) + "\n", Encoding.UTF8);
            return (added, lines.Count);
        }
    }

    /// <summary>Text of a memory file ("" when it does not exist yet).</summary>
    public string ReadMemoryText(string path)
    {
        lock (_memoryLock)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : ""; }
            catch (IOException) { return ""; }
        }
    }

    /// <summary>A player's lessons, plan and notes, as served by /api/memory.</summary>
    public object ReadMemory(string? player = null) => new
    {
        lessons = ReadMemoryText(LessonsFile(player)),
        plan = ReadMemoryText(PlanFile(player)),
        notes = ReadMemoryText(NotesFile(player))
    };

    private List<string> ReadLines(string path) =>
        ReadMemoryText(path).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();

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

    /// <summary>True once this player's journal for <paramref name="day"/> is written (go_to_bed needs today's).</summary>
    public bool HasJournal(int day) => _log.HasJournal(day, Player);

    /// <summary>Record that this player's journal for <paramref name="day"/> is complete.</summary>
    public void MarkJournal(int day) => _log.MarkJournal(day, Player);

    public int WriteLessons(int day, IEnumerable<string> lessons) => _log.WriteLessons(day, lessons, Player);

    public string ReadPlan() => _log.ReadMemoryText(_log.PlanFile(Player));

    public void WritePlan(string plan) => _log.WritePlan(plan, Player);

    public (int added, int total) AppendNotes(int day, IEnumerable<(string topic, string text)> notes) => _log.AppendNotes(day, notes, Player);
}
