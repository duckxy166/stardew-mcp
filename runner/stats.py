"""Daily stats for runs (#25): read what the mod measured, summarise what Claude spent, report it.

The mod writes `stats_NNN.json` (`stats_NNN_p2.json` for the other seats) next to the logs when a farm day rolls
over, see mod/StardewMCP/Agent/DayStats.cs. This module adds three things on top of those files:

  * `usage_NNN.json`: turns, tokens and cost of the day's `claude -p` sessions, read from their stream-json
    transcripts (`claude_day_NNN_partK.jsonl`),
  * `batch_report.md`: one table row per day of a batch, with totals, averages and failure hotspots,
  * the morning line: yesterday's numbers next to the average of the 3 days before, in Thai, for the first
    prompt of the day.

Everything is tolerant of missing files and missing fields: an older mod build, a day played by hand or a
crashed run leaves gaps, and a gap must never stop a run. Standard library only.

Command line (the runner calls the same functions):

    py runner/stats.py report runs --from 1 --to 7     # rewrite runs/batch_report.md for days 1-7
    py runner/stats.py morning runs --day 8            # print the morning line for day 8
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
import time
from pathlib import Path
from typing import Callable

MORNING_WINDOW = 3
REPORT_NAME = "batch_report.md"
# batch_report.md links the batch's lessons consolidation when one of these files exists next to it.
LESSONS_FILES = ("lessons.md",)

_STATS_NAME = re.compile(r"^stats_(\d+)(?:_(p\d+))?\.json$")
_PART_NAME = re.compile(r"_part(\d+)\.jsonl$")


# ---------------------------------------------------------------------- files


def stats_path(log_dir: Path, day: int, player: str = "p1") -> Path:
    name = f"stats_{day:03}.json" if player == "p1" else f"stats_{day:03}_{player}.json"
    return Path(log_dir) / name


def usage_path(log_dir: Path, day: int) -> Path:
    return Path(log_dir) / f"usage_{day:03}.json"


def find_log_dir(path: str | Path) -> Path:
    """A run folder (runs/) holds the logs in `logs/`; a log folder is used as it is."""
    p = Path(path)
    return p / "logs" if (p / "logs").is_dir() else p


def _read_json(path: Path) -> dict | None:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return data if isinstance(data, dict) else None


def load_stats(log_dir: Path, day: int, player: str = "p1") -> dict | None:
    """The mod's stats for one day, or None when the file is missing or unreadable."""
    return _read_json(stats_path(log_dir, day, player))


def load_usage(log_dir: Path, day: int) -> dict | None:
    """usage_NNN.json, or a fresh summary of the day's transcripts when the runner has not written it (yet)."""
    return _read_json(usage_path(log_dir, day)) or summarize_transcripts(log_dir, day)


def stats_days(log_dir: Path, player: str = "p1") -> list[int]:
    """Farm days that have a stats file for `player`, ascending."""
    days = []
    for f in Path(log_dir).glob("stats_*.json"):
        m = _STATS_NAME.match(f.name)
        if m and (m.group(2) or "p1") == player:
            days.append(int(m.group(1)))
    return sorted(days)


def wait_for_stats(log_dir: Path, day: int, player: str = "p1", timeout: float = 6.0, poll: float = 0.5,
                   sleep: Callable[[float], None] | None = None) -> dict | None:
    """The mod writes the file on the first tick of the new day; wait briefly in case the runner is faster."""
    waited = 0.0
    while True:
        data = load_stats(log_dir, day, player)
        if data is not None or waited >= timeout:
            return data
        (sleep or time.sleep)(poll)
        waited += poll


# ---------------------------------------------------------------------- usage from the claude transcripts


def _part_number(f: Path) -> int:
    m = _PART_NAME.search(f.name)
    return int(m.group(1)) if m else 0


def summarize_transcripts(log_dir: Path, day: int) -> dict | None:
    """Add up the `result` events of the day's `claude_day_NNN_partK.jsonl` transcripts; None when there are none."""
    files = sorted(Path(log_dir).glob(f"claude_day_{day:03}_part*.jsonl"), key=_part_number)
    if not files:
        return None
    out = {"parts": len(files), "results": 0, "turns": 0, "costUsd": 0.0, "durationMs": 0, "toolUses": 0,
           "inputTokens": 0, "outputTokens": 0, "cacheReadTokens": 0, "cacheCreationTokens": 0}
    for f in files:
        try:
            lines = f.read_text(encoding="utf-8", errors="replace").splitlines()
        except OSError:
            continue
        for line in lines:
            try:
                ev = json.loads(line)
            except ValueError:
                continue
            if not isinstance(ev, dict):
                continue
            if ev.get("type") == "assistant":
                content = (ev.get("message") or {}).get("content")
                if isinstance(content, list):
                    out["toolUses"] += sum(1 for c in content if isinstance(c, dict) and c.get("type") == "tool_use")
            elif ev.get("type") == "result":
                out["results"] += 1
                out["turns"] += int(_num(ev.get("num_turns")) or 0)
                out["costUsd"] += float(_num(ev.get("total_cost_usd")) or 0)
                out["durationMs"] += int(_num(ev.get("duration_ms")) or 0)
                usage = ev.get("usage") if isinstance(ev.get("usage"), dict) else {}
                out["inputTokens"] += int(_num(usage.get("input_tokens")) or 0)
                out["outputTokens"] += int(_num(usage.get("output_tokens")) or 0)
                out["cacheReadTokens"] += int(_num(usage.get("cache_read_input_tokens")) or 0)
                out["cacheCreationTokens"] += int(_num(usage.get("cache_creation_input_tokens")) or 0)
    out["costUsd"] = round(out["costUsd"], 4)
    return out


def write_usage(log_dir: Path, day: int) -> dict | None:
    """Summarise the day's transcripts into usage_NNN.json. Returns the summary, or None when there is no transcript."""
    summary = summarize_transcripts(log_dir, day)
    if summary is not None:
        usage_path(log_dir, day).write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return summary


# ---------------------------------------------------------------------- metrics


def _num(v) -> float | int | None:
    return v if isinstance(v, (int, float)) and not isinstance(v, bool) else None


def dig(d, *keys):
    """d[k1][k2]... or None when any step is missing."""
    cur = d
    for k in keys:
        if not isinstance(cur, dict):
            return None
        cur = cur.get(k)
    return cur


def day_metrics(stats: dict | None, usage: dict | None = None) -> dict:
    """The numbers the report, the morning line and compare_runs.py read, flattened. Missing values are None."""
    s = stats or {}
    skills = dig(s, "skills")
    skill_levels = {k: v for k, v in skills.items() if _num(v) is not None} if isinstance(skills, dict) else {}
    u = usage or {}
    tokens_in = [_num(u.get(k)) for k in ("inputTokens", "cacheReadTokens", "cacheCreationTokens")]
    ended_by = s.get("endedBy") if isinstance(s.get("endedBy"), str) else None
    return {
        "day": _num(s.get("day")),
        "date": s.get("date") if isinstance(s.get("date"), str) else "",
        "partial": s.get("partial") is True,
        "ended_by": ended_by,
        "passed_out": (ended_by == "passed_out") if ended_by is not None else None,
        "pass_out_cause": dig(s, "passOutCause"),
        "income": _num(dig(s, "income", "earned")),
        "bin_value": _num(dig(s, "income", "shippingBin")),
        "money": _num(dig(s, "money", "end")),
        "planted": _num(dig(s, "crops", "planted")),
        "harvested": _num(dig(s, "crops", "harvested")),
        "died": _num(dig(s, "crops", "died")),
        "unwatered": _num(dig(s, "crops", "unwateredAtBedtime")),
        "energy": _num(dig(s, "energy", "bedtime")),
        "health": _num(dig(s, "health", "bedtime")),
        "calls": _num(dig(s, "toolCalls", "total")),
        "failed": _num(dig(s, "toolCalls", "failed")),
        "hearts": _num(dig(s, "hearts", "total")),
        "mine": _num(dig(s, "mine", "deepestFloor")),
        "skills": sum(skill_levels.values()) if skill_levels else None,
        "skill_levels": skill_levels,
        "turns": _num(u.get("turns")),
        "out_tokens": _num(u.get("outputTokens")),
        "in_tokens": sum(t or 0 for t in tokens_in) if any(t is not None for t in tokens_in) else None,
        "cost": _num(u.get("costUsd")),
    }


def collect(log_dir: Path, player: str = "p1", days: list[int] | None = None) -> dict[int, dict]:
    """day -> metrics for every day that has a stats file (or only `days`)."""
    out = {}
    for d in (days if days is not None else stats_days(log_dir, player)):
        s = load_stats(log_dir, d, player)
        if s is not None:
            out[d] = day_metrics(s, load_usage(log_dir, d))
    return out


def _r(x: float) -> int:
    """Round half up (Python's round() goes to even: round(0.5) == 0)."""
    return int(math.floor(x + 0.5))


def _mean(values: list) -> float | None:
    vals = [v for v in values if v is not None]
    return sum(vals) / len(vals) if vals else None


def _fmt(v, digits: int = 0) -> str:
    if v is None:
        return "-"
    if digits:
        return f"{v:.{digits}f}"
    return str(_r(v)) if isinstance(v, float) else str(v)


# ---------------------------------------------------------------------- morning line


def morning_line(log_dir: Path, day: int, player: str = "p1", window: int = MORNING_WINDOW) -> str:
    """One Thai line: yesterday's numbers next to the average of the `window` days before yesterday.

    Empty when yesterday has no stats (day 1, an older mod build, a day played by hand). Days the mod marked
    partial (joined mid-day) are left out of the average.
    """
    yesterday = day - 1
    if yesterday < 1:
        return ""
    ys = load_stats(log_dir, yesterday, player)
    if ys is None:
        return ""
    y = day_metrics(ys, load_usage(log_dir, yesterday))

    earlier = []
    for d in range(max(1, yesterday - window), yesterday):
        s = load_stats(log_dir, d, player)
        if s is not None and s.get("partial") is not True:
            earlier.append(day_metrics(s, load_usage(log_dir, d)))

    def avg(key: str) -> int | None:
        m = _mean([e[key] for e in earlier])
        return None if m is None else _r(m)

    def pair(label: str, key: str, unit: str = "", avg_unit: str = "") -> str | None:
        v = y[key]
        if v is None:
            return None
        text = f"{label} {_fmt(v)}{unit}"
        a = avg(key)
        return text + (f" (เฉลี่ย {a}{avg_unit})" if a is not None else "")

    parts: list[str | None] = [pair("รายได้", "income", "g", "g")]
    if (y["harvested"] or 0) > 0 or (avg("harvested") or 0) > 0:
        parts.append(pair("เก็บเกี่ยว", "harvested"))
    parts.append(pair("ล้มเหลว", "failed", " ครั้ง"))
    if (y["died"] or 0) > 0:
        parts.append(f"พืชตาย {_fmt(y['died'])}")
    if (y["unwatered"] or 0) > 0:
        parts.append(f"ไม่ได้รดน้ำก่อนนอน {_fmt(y['unwatered'])} ต้น")
    if y["money"] is not None:
        parts.append(f"เงินสิ้นวัน {_fmt(y['money'])}g")
    if y["passed_out"]:
        parts.append("หมดสติ (ไม่ได้เข้านอนเอง)")
    shown = [p for p in parts if p]
    if not shown:
        return ""

    head = f"สถิติเมื่อวาน (วันที่ {yesterday})"
    if earlier:
        head += f" เทียบเฉลี่ย {len(earlier)} วันก่อนหน้า"
    if y["partial"]:
        head += " [ข้อมูลไม่ครบวัน]"
    return f"{head}: " + " · ".join(shown)


def morning_for_prompt(log_dir: Path, day: int, player: str = "p1") -> str:
    """morning_line() for the runner: any problem with the files gives an empty line instead of an exception."""
    try:
        return morning_line(log_dir, day, player)
    except Exception as e:  # noqa: BLE001 - a bad stats file must not stop the run
        print(f"  [stats] no morning line: {type(e).__name__}: {e}")
        return ""


# ---------------------------------------------------------------------- batch report

# (header, metric key, how the Total row combines the days, how the Average row does)
_COLUMNS = [
    ("Income g", "income", "sum", "mean"),
    ("Money g", "money", "last", None),
    ("Planted", "planted", "sum", "mean"),
    ("Harvested", "harvested", "sum", "mean"),
    ("Died", "died", "sum", "mean"),
    ("Unwatered", "unwatered", "sum", "mean"),
    ("Energy", "energy", None, "mean"),
    ("Calls", "calls", "sum", "mean"),
    ("Failed", "failed", "sum", "mean"),
    ("Hearts", "hearts", "last", None),
    ("Mine", "mine", "last", None),
    ("Skills", "skills", "last", None),
    ("Turns", "turns", "sum", "mean"),
    ("Out k", "out_tokens", "ktok_sum", "ktok_mean"),
    ("In k", "in_tokens", "ktok_sum", "ktok_mean"),
]


def _combine(values: list, how: str | None) -> str:
    vals = [v for v in values if v is not None]
    if how is None or not vals:
        return "-"
    if how == "sum":
        return _fmt(sum(vals))
    if how == "mean":
        return _fmt(_mean(vals), 1)
    if how == "last":
        return _fmt(vals[-1])
    if how == "ktok_sum":
        return _fmt(sum(vals) / 1000, 1)
    if how == "ktok_mean":
        return _fmt(_mean(vals) / 1000, 1)
    return "-"


def _cell(metrics: dict, key: str) -> str:
    v = metrics[key]
    if key in ("out_tokens", "in_tokens") and v is not None:
        return _fmt(v / 1000, 1)
    return _fmt(v)


def _table(header: list[str], rows: list[list[str]]) -> list[str]:
    lines = ["| " + " | ".join(header) + " |", "|" + "|".join("---" for _ in header) + "|"]
    lines += ["| " + " | ".join(r) + " |" for r in rows]
    return lines


def failure_hotspots(all_stats: list[dict], top: int = 10) -> list[tuple[str, str, int]]:
    """(tool, message, count) summed over the days, most failures first. Messages come from the mod with numbers
    replaced by '#'; when the tools return reason codes (#18) the message can become the code."""
    counts: dict[tuple[str, str], int] = {}
    for s in all_stats:
        fails = dig(s, "toolCalls", "failures")
        if not isinstance(fails, list):
            continue
        for f in fails:
            if isinstance(f, dict) and isinstance(f.get("tool"), str) and _num(f.get("count")):
                key = (f["tool"], str(f.get("message") or ""))
                counts[key] = counts.get(key, 0) + int(f["count"])
    ranked = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    return [(t, m, n) for (t, m), n in ranked[:top]]


def render_batch_report(days: list[int], rows: dict[int, dict], raw: dict[int, dict], player: str = "p1",
                        lessons: str | None = None, generated: str | None = None) -> str:
    """Markdown for a batch: `days` in play order, `rows` day -> day_metrics, `raw` day -> the stats file as read."""
    have = [d for d in days if d in rows]
    missing = [d for d in days if d not in rows]
    title_days = f"days {days[0]}-{days[-1]}" if len(days) > 1 else f"day {days[0]}" if days else "no days"
    out = [f"# Batch report: {title_days}", ""]
    out.append(f"Player {player} · {len(have)} of {len(days)} days with stats" + (f" · generated {generated}" if generated else ""))
    out.append("")

    header = ["Day", "Date", "Ended"] + [c[0] for c in _COLUMNS]
    body = []
    for d in have:
        m = rows[d]
        ended = m["ended_by"] or "-"
        if m["passed_out"] and m["pass_out_cause"]:
            ended = f"passed out ({m['pass_out_cause']})"
        elif m["ended_by"] == "passed_out":
            ended = "passed out"
        mark = "*" if m["partial"] else ""
        body.append([f"{d}{mark}", m["date"] or "-", ended] + [_cell(m, c[1]) for c in _COLUMNS])
    series = [[rows[d][c[1]] for d in have] for c in _COLUMNS]
    body.append(["**Total**", "", ""] + [_combine(v, c[2]) for v, c in zip(series, _COLUMNS)])
    body.append(["**Average**", "", ""] + [_combine(v, c[3]) for v, c in zip(series, _COLUMNS)])
    out += _table(header, body)
    out.append("")
    out.append("`*` marks a day the mod joined late (started after 6:10 AM): its numbers are partial. "
               "Income is the game's money earned over the day (shipping pays overnight); Total of Money/Hearts/Mine/Skills "
               "is the last day. Unwatered counts dry crops at bedtime and does not know about sprinklers or tomorrow's rain.")
    out.append("")

    if have:
        ended_counts: dict[str, int] = {}
        causes: dict[str, int] = {}
        for d in have:
            m = rows[d]
            ended_counts[m["ended_by"] or "unknown"] = ended_counts.get(m["ended_by"] or "unknown", 0) + 1
            if m["passed_out"]:
                c = str(m["pass_out_cause"] or "unknown")
                causes[c] = causes.get(c, 0) + 1
        text = ", ".join(f"{k.replace('_', ' ')} {v}" for k, v in sorted(ended_counts.items()))
        if causes:
            text += " (" + ", ".join(f"{k} {v}" for k, v in sorted(causes.items())) + ")"
        out += ["## How the days ended", "", text, ""]

        last = rows[have[-1]]["skill_levels"]
        if last:
            out += ["## Skill levels at the end", "", " · ".join(f"{k} {v}" for k, v in last.items()), ""]

        hot = failure_hotspots([raw[d] for d in have])
        out += ["## Failure hotspots", ""]
        if hot:
            out += _table(["Tool", "Message", "Count"], [[t, m.replace("|", "\\|") or "-", str(n)] for t, m, n in hot])
        else:
            out.append("No failed tool calls recorded.")
        out.append("")

    if missing:
        out += ["## Days without stats", "",
                ", ".join(str(d) for d in missing) +
                " (the mod was not running or no AI was connected to this seat, or the mod build is older than #25).", ""]

    out += ["## Lessons", ""]
    out.append(f"Lessons consolidation: [{lessons}]({lessons})" if lessons else "Lessons consolidation: (not generated for this batch)")
    out.append("")
    return "\n".join(out)


def write_batch_report(out_dir: Path, log_dir: Path, days: list[int], player: str = "p1") -> Path | None:
    """Write out_dir/batch_report.md for the batch's days. None when the batch has no days."""
    if not days:
        return None
    out_dir = Path(out_dir)
    rows, raw = {}, {}
    for d in days:
        s = load_stats(log_dir, d, player)
        if s is not None:
            raw[d] = s
            rows[d] = day_metrics(s, load_usage(log_dir, d))
    lessons = next((n for n in LESSONS_FILES if (out_dir / n).is_file()), None)
    text = render_batch_report(days, rows, raw, player, lessons, time.strftime("%Y-%m-%d %H:%M"))
    path = out_dir / REPORT_NAME
    path.write_text(text, encoding="utf-8")
    return path


def finish_day(out_dir: Path, log_dir: Path, day: int, batch_days: list[int], player: str = "p1") -> None:
    """Called by run_days.py after a day ended: save the day's usage and refresh batch_report.md.
    Never raises: a missing file or a bad transcript must not stop the run."""
    try:
        if wait_for_stats(log_dir, day, player) is None:
            print(f"  [stats] no stats_{day:03}.json (is the mod build from #25 or later?)")
        write_usage(log_dir, day)
        path = write_batch_report(out_dir, log_dir, batch_days, player)
        if path:
            print(f"  [stats] batch report → {path}")
    except Exception as e:  # noqa: BLE001 - reporting is best effort
        print(f"  [stats] could not write the report: {type(e).__name__}: {e}")


# ---------------------------------------------------------------------- command line


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    rp = sub.add_parser("report", help="rewrite batch_report.md for a range of farm days")
    rp.add_argument("run", help="run folder (runs/) or its logs folder")
    rp.add_argument("--from", dest="first", type=int, help="first day (default: the first day with stats)")
    rp.add_argument("--to", dest="last", type=int, help="last day (default: the last day with stats)")
    rp.add_argument("--player", default="p1")
    mp = sub.add_parser("morning", help="print the morning line for a day")
    mp.add_argument("run")
    mp.add_argument("--day", type=int, required=True)
    mp.add_argument("--player", default="p1")
    args = ap.parse_args(argv)

    log_dir = find_log_dir(args.run)
    if args.cmd == "morning":
        print(morning_line(log_dir, args.day, args.player) or "(no line: yesterday has no stats)")
        return 0
    have = stats_days(log_dir, args.player)
    if not have:
        print(f"No stats_*.json for {args.player} in {log_dir}", file=sys.stderr)
        return 1
    first = args.first if args.first is not None else have[0]
    last = args.last if args.last is not None else have[-1]
    days = list(range(first, last + 1))
    out_dir = log_dir.parent if log_dir.name == "logs" else log_dir
    path = write_batch_report(out_dir, log_dir, days, args.player)
    print(f"Wrote {path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
