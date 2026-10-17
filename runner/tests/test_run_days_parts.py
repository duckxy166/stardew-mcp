"""run_days.py with a fake `claude` and a fake game: planned session breaks, usage.csv and --max-parts.

Nothing here starts a process, opens a socket or needs the game: subprocess.Popen, the game state API and
sleep are replaced.
"""

import json
import sys
from argparse import Namespace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import pytest

import run_days
import sessions
import stream_events as se
import usage_log


class FakeProc:
    """Stands in for subprocess.Popen: stdout yields the canned stream-json lines."""

    launched: list[list[str]] = []

    def __init__(self, cmd, **kwargs):
        FakeProc.launched.append(cmd)
        self.stdout = iter(FakeProc.script.pop(0))

    def wait(self):
        return 0


def lines(events) -> list[str]:
    return se.jsonl(events).splitlines(keepends=True)


@pytest.fixture
def fake(monkeypatch, tmp_path):
    FakeProc.launched = []
    FakeProc.script = []
    monkeypatch.setattr(run_days.subprocess, "Popen", FakeProc)
    monkeypatch.setattr(run_days.time, "sleep", lambda s: None)
    monkeypatch.setattr(run_days, "game_state", lambda: {"ready": True, "day": 1, "time": "09:00"})
    monkeypatch.setattr(run_days, "api_post", lambda path, body: {})
    sessions.clear_context_providers()
    return tmp_path


def make_args(**over):
    base = dict(model="claude-haiku-5-5", max_turns=600, no_session_breaks=False)
    base.update(over)
    return Namespace(**base)


def test_a_part_with_a_break_notice_is_reported_and_logged(fake):
    FakeProc.script = [lines(se.three_turn_part(with_break=True))]
    (fake / "logs").mkdir()
    res = run_days.run_session(make_args(), fake, 2, "go", "system", 1)
    assert res == sessions.PartResult(tool_calls=3, planned_break=True)
    rows = usage_log.read_rows(fake / "usage.csv")
    assert len(rows) == 1
    assert rows[0]["day"] == "2" and rows[0]["part"] == "1" and rows[0]["planned_break"] == "1"
    assert rows[0]["total_tokens"] == str(23 + 85 + 1300 + 3500)
    assert rows[0]["game_time_start"] == "09:00"
    assert usage_log.check(fake / "usage.csv", fake / "logs") == []  # totals match the saved transcript


def test_the_mcp_url_opts_in_unless_disabled(fake):
    (fake / "logs").mkdir()
    for flag, expected in ((False, "http://localhost:8770/mcp?breaks=1"), (True, "http://localhost:8770/mcp")):
        FakeProc.script = [lines(se.three_turn_part())]
        run_days.run_session(make_args(no_session_breaks=flag), fake, 1, "go", "system", 1)
        cfg = json.loads((fake / "mcp.json").read_text(encoding="utf-8"))
        assert cfg["mcpServers"]["stardew"]["url"] == expected


def test_a_part_without_a_notice_is_not_a_planned_break(fake):
    FakeProc.script = [lines(se.three_turn_part())]
    (fake / "logs").mkdir()
    assert run_days.run_session(make_args(), fake, 1, "go", "system", 1).planned_break is False


def run_main(monkeypatch, tmp_path, flags, planned, day_after_part):
    """Run main() for one day. Session i ends a planned break if planned[i]; the game then reports farm day day_after_part[i]."""
    sessions_run = []

    def fake_run_session(args, out_dir, day, prompt, system_prompt, part):
        sessions_run.append((day, part, prompt))
        return sessions.PartResult(1, planned[len(sessions_run) - 1])

    def fake_wait_ready(timeout=600):
        return {"ready": True, "day": day_after_part[len(sessions_run) - 1] if sessions_run else 1}

    monkeypatch.setattr(run_days, "run_session", fake_run_session)
    monkeypatch.setattr(run_days, "wait_ready", fake_wait_ready)
    monkeypatch.setattr(run_days, "backup_save", lambda out_dir, day: None)
    monkeypatch.setattr(run_days.time, "sleep", lambda s: None)
    monkeypatch.setattr(sys, "argv", ["run_days.py", "--skip-smoke", "--out", str(tmp_path), "--days", "1"] + flags)
    run_days.main()
    return sessions_run


def test_planned_breaks_do_not_use_up_max_parts(monkeypatch, tmp_path):
    # two planned breaks, then the day ends in part 3; --max-parts 1 would have stopped a no-break run after part 1
    runs = run_main(monkeypatch, tmp_path, ["--max-parts", "1"], planned=[True, True, False], day_after_part=[1, 1, 2])
    assert [p for _, p, _ in runs] == [1, 2, 3]
    assert "Session break" in runs[1][2] and "Session break" in runs[2][2]


def test_unplanned_stops_still_end_the_day_at_max_parts(monkeypatch, tmp_path, capsys):
    runs = run_main(monkeypatch, tmp_path, ["--max-parts", "2"], planned=[False, False, False], day_after_part=[1, 1, 1])
    assert [p for _, p, _ in runs] == [1, 2]
    assert "still not finished after 2 sessions" in capsys.readouterr().out
    assert "Session break" not in runs[1][2]


def test_the_break_cap_stops_a_loop(monkeypatch, tmp_path):
    runs = run_main(monkeypatch, tmp_path, ["--max-parts", "2", "--max-breaks", "2"], planned=[True] * 10, day_after_part=[1] * 10)
    # parts 1 and 2 end in free breaks; the 3rd and 4th breaks are over the cap and count as the 2 allowed parts
    assert [p for _, p, _ in runs] == [1, 2, 3, 4]


def test_the_day_total_is_printed_when_the_day_ends(monkeypatch, tmp_path, capsys):
    (tmp_path / "logs").mkdir()
    usage_log.append_row(tmp_path / "usage.csv", usage_log.UsageTracker().row(1, 1))
    run_main(monkeypatch, tmp_path, [], planned=[False], day_after_part=[2])
    assert "usage day 1: 1 part(s)" in capsys.readouterr().out
