"""Compare two runs at checkpoint days, from the daily stats the mod wrote (#25).

    py runner/compare_runs.py runs_haiku runs_flash
    py runner/compare_runs.py runs_a runs_b --days 7 28 56 100
    py runner/compare_runs.py runs_a runs_b --player-a p1 --player-b p2    # two seats of one run

A run is the folder the runner wrote (it holds `logs/`) or a logs folder itself. For every checkpoint day the
table shows, side by side, what each run had reached by then: the state on that day (money, hearts, skill
levels, deepest mine floor) and the totals up to it (income, crops, tool calls and failures, days passed out,
Claude turns, tokens and cost). A run that has not reached the checkpoint shows "-". There is no combined
score on purpose: read the columns.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import stats

DEFAULT_DAYS = [7, 28, 56, 100]

# (label, metric key) read as it was on the checkpoint day
STATE_METRICS = [("Money g", "money"), ("Hearts", "hearts"), ("Skill levels", "skills"), ("Mine floor", "mine")]
# (label, metric key) added up over the days up to the checkpoint
TOTAL_METRICS = [
    ("Income g", "income"), ("Planted", "planted"), ("Harvested", "harvested"), ("Died", "died"),
    ("Tool calls", "calls"), ("Failed calls", "failed"),
    ("Turns", "turns"), ("Out tokens k", "out_tokens"), ("In tokens k", "in_tokens"), ("Cost USD", "cost"),
]


def reached(rows: dict[int, dict], day: int) -> bool:
    return bool(rows) and max(rows) >= day


def at_checkpoint(rows: dict[int, dict], day: int) -> dict[str, float | None] | None:
    """Metrics of one run at a checkpoint day; None when the run has not reached it.

    State metrics come from the last day with stats on or before the checkpoint, totals add up every day up to it.
    """
    if not reached(rows, day):
        return None
    upto = sorted(d for d in rows if d <= day)
    out: dict[str, float | None] = {}
    for _, key in STATE_METRICS:
        out[key] = next((rows[d][key] for d in reversed(upto) if rows[d][key] is not None), None)
    for _, key in TOTAL_METRICS:
        vals = [rows[d][key] for d in upto if rows[d][key] is not None]
        out[key] = sum(vals) if vals else None
    out["days_with_stats"] = len(upto)
    out["passed_out_days"] = sum(1 for d in upto if rows[d]["passed_out"])
    calls, failed = out["calls"], out["failed"]
    out["fail_rate"] = (100.0 * failed / calls) if calls and failed is not None else None
    return out


def _fmt(key: str, v: float | None) -> str:
    if v is None:
        return "-"
    if key in ("out_tokens", "in_tokens"):
        return f"{v / 1000:.1f}"
    if key == "cost":
        return f"{v:.2f}"
    if key == "fail_rate":
        return f"{v:.1f}%"
    return str(stats._r(v)) if isinstance(v, float) else str(v)


def _diff(key: str, a: float | None, b: float | None) -> str:
    if a is None or b is None:
        return "-"
    d = b - a
    if key in ("out_tokens", "in_tokens"):
        return f"{d / 1000:+.1f}"
    if key == "cost":
        return f"{d:+.2f}"
    if key == "fail_rate":
        return f"{d:+.1f} pt"
    return f"{stats._r(d):+d}"


def compare(rows_a: dict[int, dict], rows_b: dict[int, dict], days: list[int]) -> list[tuple[int, list[tuple[str, str, str, str]]]]:
    """[(checkpoint day, [(metric label, A, B, B minus A)])] in the order of `days`."""
    labels = [("Days with stats", "days_with_stats"), ("Days passed out", "passed_out_days")]
    metrics = STATE_METRICS + TOTAL_METRICS[:6] + [("Failure rate", "fail_rate")] + TOTAL_METRICS[6:] + labels
    result = []
    for day in days:
        a, b = at_checkpoint(rows_a, day), at_checkpoint(rows_b, day)
        lines = []
        for label, key in metrics:
            va = a[key] if a else None
            vb = b[key] if b else None
            lines.append((label, _fmt(key, va), _fmt(key, vb), _diff(key, va, vb)))
        result.append((day, lines))
    return result


def render(name_a: str, name_b: str, comparison: list) -> str:
    out = []
    for day, lines in comparison:
        out += [f"## Day {day}", "", f"| Metric | A: {name_a} | B: {name_b} | B - A |", "|---|---|---|---|"]
        out += [f"| {label} | {a} | {b} | {d} |" for label, a, b, d in lines]
        out.append("")
    return "\n".join(out)


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("run_a", help="run folder (with logs/) or logs folder of run A")
    ap.add_argument("run_b", help="run folder (with logs/) or logs folder of run B")
    ap.add_argument("--days", type=int, nargs="+", default=DEFAULT_DAYS, help="checkpoint days (default: 7 28 56 100)")
    ap.add_argument("--player-a", default="p1", help="seat of run A (p1, p2, p3)")
    ap.add_argument("--player-b", default="p1", help="seat of run B")
    args = ap.parse_args(argv)

    rows = []
    for run, player in ((args.run_a, args.player_a), (args.run_b, args.player_b)):
        log_dir = stats.find_log_dir(run)
        found = stats.collect(log_dir, player)
        if not found:
            print(f"No stats_*.json for {player} in {log_dir}", file=sys.stderr)
            return 1
        rows.append(found)

    name_a = f"{Path(args.run_a).name or args.run_a}/{args.player_a}"
    name_b = f"{Path(args.run_b).name or args.run_b}/{args.player_b}"
    print(render(name_a, name_b, compare(rows[0], rows[1], sorted(set(args.days)))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
