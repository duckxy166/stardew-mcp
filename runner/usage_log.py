"""Token accounting for run_days.py (#30): one row per `claude -p` part in runs/usage.csv.

The player runs on a subscription, so there is no money cap; what matters is how much of the 5-hour and
weekly usage limit a day uses (#23) and whether a change (smaller `look`, session breaks, a stable system
prompt) reduces it. Each row comes from the stream-json transcript of one part:

  * input / output / cache tokens: the `result` event's `usage`, the total of every turn of the session
    (without a `result` event, for example after a crash, the sum over the assistant messages, result_seen=0);
  * turns, tool calls and failed tool calls;
  * peak_context_tokens: the largest prompt (input + cache) of any turn, so you can see how close a part
    came to the context limit;
  * planned_break: the part ended with the mod's "Session break:" notice.

Commands (standard library only):

    py runner/usage_log.py summary            # per-day totals and averages from runs/usage.csv
    py runner/usage_log.py check              # do the rows match the transcripts in runs/logs?
    py runner/usage_log.py rebuild            # write usage.csv again from runs/logs/claude_day_*_part*.jsonl

`rebuild` also works on transcripts from before this file existed, so a one-session day can be compared
with a day split into parts (add --no-session-breaks to run_days.py for the one-session baseline).
"""

from __future__ import annotations

import argparse
import csv
import json
import re
import sys
import time
from pathlib import Path

import sessions

HERE = Path(__file__).resolve().parent
DEFAULT_OUT = HERE.parent / "runs"

FIELDS = [
    "time", "day", "part", "planned_break", "session_id", "model", "subtype", "is_error", "result_seen",
    "game_time_start", "game_time_end", "turns", "tool_calls", "tool_errors",
    "input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "total_tokens",
    "peak_context_tokens", "duration_s",
]
_TOKEN_FIELDS = ("input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens")
# What `check` compares between a row and its transcript.
CHECKED = _TOKEN_FIELDS + ("total_tokens", "turns", "tool_calls", "tool_errors", "peak_context_tokens")


def _num(value) -> int:
    try:
        return int(value or 0)
    except (TypeError, ValueError):
        return 0


def _context_of(usage: dict) -> int:
    """Tokens one request carried in: new input plus everything read from or written to the cache."""
    return _num(usage.get("input_tokens")) + _num(usage.get("cache_creation_input_tokens")) + _num(usage.get("cache_read_input_tokens"))


class UsageTracker:
    """Feed it the stream-json events of one `claude -p` run, then ask for the row."""

    def __init__(self) -> None:
        self.session_id = ""
        self.model = ""
        self.tool_calls = 0
        self.tool_errors = 0
        self.peak_context = 0
        self.break_seen = False
        self.result: dict | None = None
        self._tool_ids: set[str] = set()
        self._messages: dict[str, dict] = {}  # assistant message id -> its latest usage

    def feed(self, ev: dict) -> None:
        kind = ev.get("type")
        if kind == "system" and ev.get("subtype") == "init":
            self.session_id = ev.get("session_id") or self.session_id
            self.model = ev.get("model") or self.model
        elif kind == "assistant":
            msg = ev.get("message") or {}
            usage = msg.get("usage")
            if isinstance(usage, dict):
                self.peak_context = max(self.peak_context, _context_of(usage))
                self._messages[msg.get("id") or f"anon-{len(self._messages)}"] = usage
            if msg.get("model") and not self.model:
                self.model = msg["model"]
            for block in msg.get("content") or []:
                if isinstance(block, dict) and block.get("type") == "tool_use":
                    key = block.get("id") or f"anon-{self.tool_calls}"
                    if key not in self._tool_ids:
                        self._tool_ids.add(key)
                        self.tool_calls += 1
        elif kind == "user":
            content = (ev.get("message") or {}).get("content")
            if isinstance(content, list):
                self.tool_errors += sum(1 for b in content if isinstance(b, dict) and b.get("type") == "tool_result" and b.get("is_error"))
            if sessions.saw_break(ev):
                self.break_seen = True
        elif kind == "result":
            self.result = ev
            self.session_id = ev.get("session_id") or self.session_id

    def _totals(self) -> dict[str, int]:
        if self.result and isinstance(self.result.get("usage"), dict):
            usage = self.result["usage"]
            totals = {k: _num(usage.get(k)) for k in _TOKEN_FIELDS}
        else:
            totals = {k: sum(_num(u.get(k)) for u in self._messages.values()) for k in _TOKEN_FIELDS}
        totals["total_tokens"] = sum(totals[k] for k in _TOKEN_FIELDS)
        return totals

    def row(self, day: int, part: int, game_time_start: str = "", game_time_end: str = "", wall_seconds: float = 0.0) -> dict:
        r = self.result or {}
        duration = _num(r.get("duration_ms")) / 1000 if r.get("duration_ms") else wall_seconds
        row = {
            "time": time.strftime("%Y-%m-%dT%H:%M:%S"),
            "day": day,
            "part": part,
            "planned_break": int(self.break_seen),
            "session_id": self.session_id,
            "model": self.model,
            "subtype": r.get("subtype", "") if r else "no_result",
            "is_error": int(bool(r.get("is_error"))) if r else 1,
            "result_seen": int(self.result is not None),
            "game_time_start": game_time_start,
            "game_time_end": game_time_end,
            "turns": _num(r.get("num_turns")) if r else len(self._messages),
            "tool_calls": self.tool_calls,
            "tool_errors": self.tool_errors,
            "peak_context_tokens": self.peak_context,
            "duration_s": round(duration, 1),
        }
        row.update(self._totals())
        return {k: row[k] for k in FIELDS}


def parse_transcript(path: Path) -> UsageTracker:
    """Replay a saved transcript (claude_day_NNN_partK.jsonl) through a tracker."""
    tracker = UsageTracker()
    with Path(path).open(encoding="utf-8", errors="replace") as f:
        for line in f:
            try:
                ev = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(ev, dict):
                tracker.feed(ev)
    return tracker


# ---------------------------------------------------------------------- the csv


def append_row(csv_path: Path, row: dict) -> None:
    """Add a row, writing the header first. A file with an older column set is kept as usage.<time>.old.csv."""
    csv_path = Path(csv_path)
    csv_path.parent.mkdir(parents=True, exist_ok=True)
    if csv_path.exists() and csv_path.stat().st_size > 0:
        with csv_path.open(encoding="utf-8", newline="") as f:
            header = next(csv.reader(f), [])
        if header != FIELDS:
            csv_path.rename(csv_path.with_name(f"{csv_path.stem}.{time.strftime('%Y%m%d%H%M%S')}.old{csv_path.suffix}"))
    new = not csv_path.exists() or csv_path.stat().st_size == 0
    with csv_path.open("a", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=FIELDS)
        if new:
            w.writeheader()
        w.writerow({k: row.get(k, "") for k in FIELDS})


def read_rows(csv_path: Path) -> list[dict]:
    csv_path = Path(csv_path)
    if not csv_path.exists():
        return []
    with csv_path.open(encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def day_totals(rows: list[dict]) -> dict[int, dict]:
    """Per farm day: parts, planned breaks, turns, tool calls, tokens and the largest context of any part."""
    days: dict[int, dict] = {}
    for r in rows:
        d = days.setdefault(_num(r.get("day")), {"parts": 0, "planned_breaks": 0, "turns": 0, "tool_calls": 0,
                                                 "tool_errors": 0, "peak_context_tokens": 0, **{k: 0 for k in _TOKEN_FIELDS}, "total_tokens": 0})
        d["parts"] += 1
        d["planned_breaks"] += _num(r.get("planned_break"))
        for k in ("turns", "tool_calls", "tool_errors", *_TOKEN_FIELDS, "total_tokens"):
            d[k] += _num(r.get(k))
        d["peak_context_tokens"] = max(d["peak_context_tokens"], _num(r.get("peak_context_tokens")))
    return days


def cache_hit_ratio(t: dict) -> float:
    """Share of the prompt tokens that were served from the prompt cache."""
    prompt = t["input_tokens"] + t["cache_creation_input_tokens"] + t["cache_read_input_tokens"]
    return t["cache_read_input_tokens"] / prompt if prompt else 0.0


def day_line(csv_path: Path, day: int) -> str:
    """One line for the console when a day is done."""
    t = day_totals(read_rows(csv_path)).get(day)
    if not t:
        return f"  usage: no rows for day {day} in {csv_path}"
    return (f"  usage day {day}: {t['parts']} part(s), {t['planned_breaks']} planned break(s), "
            f"{t['turns']} turns, {t['tool_calls']} tool calls, {t['total_tokens']:,} tokens "
            f"(in {t['input_tokens']:,} · out {t['output_tokens']:,} · cache write {t['cache_creation_input_tokens']:,} · "
            f"cache read {t['cache_read_input_tokens']:,}), peak context {t['peak_context_tokens']:,}, cache hit {cache_hit_ratio(t):.0%}")


def summary(rows: list[dict]) -> str:
    days = day_totals(rows)
    if not days:
        return "No usage rows yet."
    head = f"{'day':>4} {'parts':>5} {'breaks':>6} {'turns':>6} {'calls':>6} {'total tokens':>13} {'out':>9} {'peak ctx':>9} {'cache hit':>9}"
    lines = [head, "-" * len(head)]
    for d in sorted(days):
        t = days[d]
        lines.append(f"{d:>4} {t['parts']:>5} {t['planned_breaks']:>6} {t['turns']:>6} {t['tool_calls']:>6} {t['total_tokens']:>13,} "
                     f"{t['output_tokens']:>9,} {t['peak_context_tokens']:>9,} {cache_hit_ratio(t):>9.0%}")
    n = len(days)

    def avg(key: str) -> float:
        return sum(t[key] for t in days.values()) / n

    lines.append("-" * len(head))
    lines.append(f"{n} day(s): average {avg('total_tokens'):,.0f} tokens/day, {avg('tool_calls'):.0f} tool calls/day, "
                 f"{avg('parts'):.1f} parts/day, {sum(t['total_tokens'] for t in days.values()):,} tokens in all")
    return "\n".join(lines)


# ---------------------------------------------------------------------- transcripts <-> csv

_TRANSCRIPT = re.compile(r"claude_day_(\d+)_part(\d+)\.jsonl$")


def transcripts(log_dir: Path) -> dict[tuple[int, int], Path]:
    found = {}
    for p in Path(log_dir).glob("claude_day_*_part*.jsonl"):
        m = _TRANSCRIPT.search(p.name)
        if m:
            found[(int(m.group(1)), int(m.group(2)))] = p
    return found


def check(csv_path: Path, log_dir: Path) -> list[str]:
    """Differences between the csv and the transcripts (empty list = they match). The last row of a (day, part) wins."""
    rows: dict[tuple[int, int], dict] = {}
    for r in read_rows(csv_path):
        rows[(_num(r.get("day")), _num(r.get("part")))] = r
    files = transcripts(log_dir)
    problems = []
    for key in sorted(rows.keys() | files.keys()):
        label = f"day {key[0]} part {key[1]}"
        if key not in files:
            problems.append(f"{label}: row without a transcript")
            continue
        if key not in rows:
            problems.append(f"{label}: transcript without a row")
            continue
        expected = parse_transcript(files[key]).row(key[0], key[1])
        for field in CHECKED:
            if _num(rows[key].get(field)) != _num(expected[field]):
                problems.append(f"{label}: {field} is {rows[key].get(field)} in the csv but {expected[field]} in the transcript")
    return problems


def rebuild(csv_path: Path, log_dir: Path) -> int:
    """Write the csv again from the transcripts (replaces it). Returns the number of rows."""
    csv_path = Path(csv_path)
    if csv_path.exists():
        csv_path.rename(csv_path.with_name(f"{csv_path.stem}.{time.strftime('%Y%m%d%H%M%S')}.old{csv_path.suffix}"))
    files = transcripts(log_dir)
    for (day, part), path in sorted(files.items()):
        row = parse_transcript(path).row(day, part)
        row["time"] = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(path.stat().st_mtime))
        append_row(csv_path, row)
    return len(files)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("command", choices=["summary", "check", "rebuild"])
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="run folder holding usage.csv and logs/ (default: runs/)")
    args = ap.parse_args(argv)
    out = Path(args.out)
    csv_path, log_dir = out / "usage.csv", out / "logs"
    if args.command == "summary":
        print(summary(read_rows(csv_path)))
        return 0
    if args.command == "rebuild":
        print(f"wrote {rebuild(csv_path, log_dir)} row(s) to {csv_path}")
        return 0
    problems = check(csv_path, log_dir)
    print("\n".join(problems) if problems else f"OK: {csv_path} matches the transcripts in {log_dir}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
