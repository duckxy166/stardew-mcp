"""Builders for the tests of runner/stats.py and runner/compare_runs.py.

Everything here is generated: a stats document shaped like the ones DayStats.cs writes, and stream-json lines
shaped like the ones `claude -p --output-format stream-json` prints. No real game data, no network.
"""

from __future__ import annotations

import json
from pathlib import Path


def stats_doc(day: int, *, income: int = 100, money: int = 500, planted: int = 0, harvested: int = 0, died: int = 0,
              unwatered: int = 0, energy: int = 20, health: int = 100, calls: int = 50, failed: int = 5,
              hearts: int = 3, mine: int = 0, skills: tuple = (1, 0, 0, 0, 0), ended_by: str = "bed",
              cause: str | None = None, partial: bool = False, failures: list | None = None,
              player: str = "p1") -> dict:
    """A stats document with the fields and nesting the mod writes (version 1)."""
    passed_out = ended_by == "passed_out"
    farming, mining, foraging, fishing, combat = skills
    return {
        "version": 1,
        "day": day,
        "date": f"Spring {day}, Year 1",
        "player": player,
        "farmer": "Tester",
        "partial": partial,
        "startTime": "6:00 AM",
        "weather": "Sunny",
        "endedBy": ended_by,
        "wentToBed": ended_by == "bed",
        "passedOut": passed_out,
        "passOutCause": cause if passed_out else None,
        "passOutSource": "heuristic" if passed_out and cause else None,
        "endTime": "1:40 AM",
        "money": {"start": money - income, "bedtime": money - income, "end": money},
        "income": {"earned": income, "shippingBin": income, "shippedItems": 2, "shippedByCategory": {}},
        "crops": {"planted": planted, "harvested": harvested, "died": died, "growing": planted,
                  "unwateredAtBedtime": unwatered},
        "energy": {"bedtime": energy, "max": 270},
        "health": {"bedtime": health, "max": 100},
        "toolCalls": {
            "total": calls, "failed": failed, "actions": calls - 10, "instant": 10,
            "byTool": {"move_to": {"n": calls, "failed": failed}},
            "failures": failures if failures is not None else (
                [{"tool": "move_to", "message": "no route to (#,#)", "count": failed}] if failed else []),
        },
        "skills": {"farming": farming, "mining": mining, "foraging": foraging, "fishing": fishing, "combat": combat},
        "hearts": {"total": hearts, "friendship": {"Lewis": 250}},
        "mine": {"deepestFloor": mine},
        "tools": {"hoe": 0},
        "buildings": {},
        "gameStats": {"fishCaught": 0},
    }


def write_stats(log_dir: Path, day: int, player: str = "p1", **kw) -> Path:
    """Write a stats file the way the mod names it: stats_NNN.json for p1, stats_NNN_pN.json for the others."""
    log_dir = Path(log_dir)
    log_dir.mkdir(parents=True, exist_ok=True)
    name = f"stats_{day:03}.json" if player == "p1" else f"stats_{day:03}_{player}.json"
    path = log_dir / name
    path.write_text(json.dumps(stats_doc(day, player=player, **kw), indent=2), encoding="utf-8")
    return path


def transcript_lines(*, turns: int = 10, cost: float = 0.25, tool_uses: int = 3, input_tokens: int = 100,
                     output_tokens: int = 2000, cache_read: int = 5000, cache_creation: int = 300,
                     duration_ms: int = 60000) -> list[str]:
    """The lines of one `claude -p` session: init, some assistant turns with tool calls, one result."""
    lines = [json.dumps({"type": "system", "subtype": "init", "session_id": "s-test", "model": "test-model"})]
    for i in range(tool_uses):
        lines.append(json.dumps({"type": "assistant", "message": {"content": [
            {"type": "text", "text": "ok"},
            {"type": "tool_use", "id": f"t{i}", "name": "mcp__stardew__status", "input": {}},
        ]}}))
    lines.append(json.dumps({
        "type": "result", "subtype": "success", "is_error": False, "num_turns": turns, "total_cost_usd": cost,
        "duration_ms": duration_ms,
        "usage": {"input_tokens": input_tokens, "output_tokens": output_tokens,
                  "cache_read_input_tokens": cache_read, "cache_creation_input_tokens": cache_creation},
    }))
    return lines


def write_transcript(log_dir: Path, day: int, part: int = 1, extra_lines: list[str] | None = None, **kw) -> Path:
    log_dir = Path(log_dir)
    log_dir.mkdir(parents=True, exist_ok=True)
    path = log_dir / f"claude_day_{day:03}_part{part}.jsonl"
    path.write_text("\n".join(transcript_lines(**kw) + (extra_lines or [])) + "\n", encoding="utf-8")
    return path
