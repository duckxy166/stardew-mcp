"""run_days.main(): how the runner decides a day is over, and what it carries from one day to the next.

The day loop is driven with a fake game. `wait_ready` reports the game's day, and the fake `run_session` plays
the part of an AI session: it either "goes to bed" (the game's day rolls over, a diary is written) or stops
early. A day counts as finished when the game reports a higher day number after a session, so these tests
cover the detection, the part limit, the backups and the memory handed to the next day.

When run_days.py gets new options or steps, adapt `play()` below; the scenarios themselves should stay valid.
"""

from __future__ import annotations

import shutil
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

import run_days

SAMPLES = Path(__file__).resolve().parents[1] / "samples"


class FakeGame:
    """The game as run_days sees it: a day number, and sessions that may roll it over."""

    def __init__(self, day: int, bed_on: set[tuple[int, int]] | None = None, bed_always: bool = False):
        self.day = day
        self.bed_on = bed_on or set()  # (day, part) sessions that end with go_to_bed
        self.bed_always = bed_always
        self.sessions: list[SimpleNamespace] = []
        self.backups: list[int] = []
        self.ready_checks = 0


class Run:
    """Outcome of one main() call."""

    def __init__(self, game: FakeGame, out_dir: Path, output: str):
        self.game, self.out_dir, self.output = game, out_dir, output

    @property
    def played(self) -> list[tuple[int, int]]:
        return [(s.day, s.part) for s in self.game.sessions]

    @property
    def backups(self) -> list[int]:
        return self.game.backups


@pytest.fixture
def play(tmp_path, monkeypatch, capsys):
    """play(game, "--days", "2", ...) runs run_days.main() against the fake game."""
    out_dir = tmp_path / "out"

    def run(game: FakeGame, *argv: str, smoke: int | None = None, prepare=None) -> Run:
        def wait_ready(timeout: int = 600):
            game.ready_checks += 1
            return {"ready": True, "day": game.day}

        def run_session(args, out, day, prompt, system_prompt, part):
            game.sessions.append(SimpleNamespace(args=args, day=day, part=part, prompt=prompt, system=system_prompt))
            if game.bed_always or (day, part) in game.bed_on:
                (out / "logs").mkdir(exist_ok=True)
                (out / "logs" / f"diary_{day:03}.md").write_text(f"# Day {day}\n\nDIARY-OF-DAY-{day}", encoding="utf-8")
                game.day += 1
            return 3

        monkeypatch.setattr(run_days, "wait_ready", wait_ready)
        monkeypatch.setattr(run_days, "run_session", run_session)
        monkeypatch.setattr(run_days, "backup_save", lambda out, day: game.backups.append(day))
        monkeypatch.setattr(run_days.time, "sleep", lambda s: None)
        if smoke is not None:
            monkeypatch.setattr(run_days.subprocess, "run", lambda cmd, **kw: SimpleNamespace(returncode=smoke, cmd=cmd))
        flags = ["--out", str(out_dir), *argv]
        if smoke is None:
            flags.append("--skip-smoke")
        monkeypatch.setattr(sys, "argv", ["run_days.py", *flags])
        if prepare:
            (out_dir / "logs").mkdir(parents=True, exist_ok=True)
            prepare(out_dir / "logs")
        run_days.main()
        return Run(game, out_dir, capsys.readouterr().out)

    return run


# ---------------------------------------------------------------------------------------- one day

def test_day_that_ends_in_the_first_session_is_backed_up_once(play):
    r = play(FakeGame(2, bed_on={(2, 1)}))
    assert r.played == [(2, 1)]
    assert r.backups == [2]
    assert r.game.day == 3


def test_first_prompt_names_the_day(play):
    r = play(FakeGame(7, bed_always=True))
    prompt = r.game.sessions[0].prompt
    assert "วันที่ 7" in prompt
    assert "go_to_bed" in prompt


def test_session_stopping_before_bed_continues_the_same_day(play):
    r = play(FakeGame(2, bed_on={(2, 2)}))
    assert r.played == [(2, 1), (2, 2)]
    assert r.backups == [2]
    assert "day 2 not finished after part 1; continuing" in r.output


def test_continuation_prompt_recaps_what_was_just_done(play):
    def logs(folder):
        shutil.copy(SAMPLES / "game_logs" / "day_002.jsonl", folder / "day_002.jsonl")

    r = play(FakeGame(2, bed_on={(2, 2)}), prepare=logs)
    part1, part2 = (s.prompt for s in r.game.sessions)
    assert "ต่อจากเดิม" not in part1
    assert "เล่นวันที่ 2 ต่อจากเดิม" in part2
    assert "06:10 action walk_to: ไปที่แปลงผัก" in part2
    assert "Auto-named" not in part2  # system lines are not recapped
    assert part2.rstrip().endswith("go_to_bed")


def test_every_part_gets_the_same_system_prompt_and_options(play):
    r = play(FakeGame(2, bed_on={(2, 3)}), "--model", "other-model", "--max-turns", "50")
    assert len(r.game.sessions) == 3
    assert len({s.system for s in r.game.sessions}) == 1
    assert {s.args.model for s in r.game.sessions} == {"other-model"}
    assert {s.args.max_turns for s in r.game.sessions} == {50}


def test_day_that_never_ends_stops_after_max_parts_without_a_backup(play):
    r = play(FakeGame(2), "--max-parts", "3")
    assert r.played == [(2, 1), (2, 2), (2, 3)]
    assert r.backups == []
    assert "Day 2 still not finished after 3 sessions" in r.output


def test_default_part_limit_is_four(play):
    r = play(FakeGame(2))
    assert [part for _, part in r.played] == [1, 2, 3, 4]
    assert r.backups == []


# ------------------------------------------------------------------------------------ several days

def test_days_option_plays_that_many_days(play):
    r = play(FakeGame(2, bed_always=True), "--days", "3")
    assert r.played == [(2, 1), (3, 1), (4, 1)]
    assert r.backups == [2, 3, 4]


def test_default_is_a_single_day(play):
    r = play(FakeGame(5, bed_always=True))
    assert r.played == [(5, 1)]


def test_until_day_is_inclusive_and_overrides_days(play):
    r = play(FakeGame(2, bed_always=True), "--until-day", "4", "--days", "1")
    assert r.played == [(2, 1), (3, 1), (4, 1)]
    assert r.backups == [2, 3, 4]
    assert r.game.day == 5


def test_nothing_is_played_when_the_game_is_already_past_until_day(play):
    r = play(FakeGame(5, bed_always=True), "--until-day", "3")
    assert r.played == []
    assert r.backups == []


def test_stuck_day_ends_a_multi_day_run(play):
    r = play(FakeGame(2, bed_on={(2, 1)}), "--days", "5", "--max-parts", "2")
    assert r.played == [(2, 1), (3, 1), (3, 2)]
    assert r.backups == [2]
    assert "Day 3 still not finished" in r.output


# ------------------------------------------------------------------------------------------ memory

def test_next_day_starts_with_the_diary_just_written(play):
    r = play(FakeGame(2, bed_always=True), "--days", "2")
    day2, day3 = (s.system for s in r.game.sessions)
    assert "DIARY-OF-DAY-2" not in day2
    assert "(This is your first day. No diaries yet.)" in day2
    assert "DIARY-OF-DAY-2" in day3
    assert "No diaries yet" not in day3


def test_memory_holds_the_last_three_diaries_oldest_first(play):
    r = play(FakeGame(2, bed_always=True), "--days", "6")
    last = r.game.sessions[-1]
    assert last.day == 7
    assert "DIARY-OF-DAY-3" not in last.system  # days 2-6 have diaries, only 4-6 are in the window
    positions = [last.system.index(f"DIARY-OF-DAY-{d}") for d in (4, 5, 6)]
    assert positions == sorted(positions)


def test_goal_and_memory_placeholders_are_filled(play):
    r = play(FakeGame(2, bed_always=True), "--goal", "Grow every crop in the game")
    system = r.game.sessions[0].system
    assert "Grow every crop in the game" in system
    assert "{GOAL}" not in system and "{MEMORY}" not in system


def test_default_goal_is_used_without_the_option(play):
    r = play(FakeGame(2, bed_always=True))
    assert run_days.DEFAULT_GOAL in r.game.sessions[0].system


def test_diaries_from_an_earlier_run_are_picked_up(play):
    def earlier(folder):
        (folder / "diary_001.md").write_text("# Day 1\n\nDIARY-FROM-LAST-WEEK", encoding="utf-8")

    r = play(FakeGame(2, bed_always=True), prepare=earlier)
    assert "DIARY-FROM-LAST-WEEK" in r.game.sessions[0].system


# --------------------------------------------------------------------------------------- smoke gate

def test_failing_smoke_test_stops_before_any_session(play):
    game = FakeGame(2, bed_always=True)
    with pytest.raises(SystemExit) as stop:
        play(game, smoke=1)
    assert "Smoke tests failed" in str(stop.value)
    assert game.sessions == []


def test_passing_smoke_test_lets_the_batch_start(play):
    r = play(FakeGame(2, bed_always=True), smoke=0)
    assert r.played == [(2, 1)]
    assert "Running smoke tests before the batch" in r.output


def test_skip_smoke_never_runs_it(play, monkeypatch):
    def boom(*a, **k):
        raise AssertionError("smoke.py must not run with --skip-smoke")

    monkeypatch.setattr(run_days.subprocess, "run", boom)
    assert play(FakeGame(2, bed_always=True)).played == [(2, 1)]
