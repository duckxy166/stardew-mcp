"""Tests for runner/game_watchdog.py: fake game, processes, clock and /api/state. No game, network or claude CLI."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import game_watchdog as gw  # noqa: E402

SAVE = "Duck_123"


# ---------------------------------------------------------------------- fakes


class FakeProc:
    """Stands in for the `claude` subprocess."""

    def __init__(self):
        self.killed = False
        self.pid = 4242

    def poll(self):
        return 0 if self.killed else None

    def kill(self):
        self.killed = True


class World:
    """A fake game that is also the clock and the process control. advance() lets time pass."""

    def __init__(self):
        self.t = 1000.0
        self.running = True
        self.answering = True
        self.loop_alive = True       # False = game loop blocked: HTTP still answers, heartbeat frozen
        self.clock_runs = True       # False = in-game time does not move
        self.ready = True
        self.frozen = False
        self.action: str | None = "walk_to"
        self.menu: str | None = None
        self.event_up = False
        self.day = 5
        self.save_day = 5            # the day the save on disk is at
        self.minutes = 360
        self.beat = 1
        self._acc = 0.0
        self.boot_secs = 20.0
        self._boot_done: float | None = None
        self.never_ready = False     # the relaunched game sits on the title screen
        self.launch_fails = False    # the process dies right after launch
        self.autoload_override: str | None = None
        self.loaded_save = SAVE
        self.config_path: Path | None = None
        self.config_at_launch: list[str] = []
        self.launches = 0
        self.kills = 0
        self.session_kills: list = []

    # clock
    def now(self) -> float:
        return self.t

    def sleep(self, secs: float) -> None:
        self.advance(secs)

    def advance(self, secs: float) -> None:
        self.t += secs
        if self.running and self.loop_alive:
            self.beat += max(1, int(secs * 4))
        if self.running and self.ready and self.clock_runs and not self.frozen:
            self._acc += secs
            while self._acc >= 7:
                self._acc -= 7
                self.minutes += 10
        if self._boot_done is not None and self.t >= self._boot_done:
            self._boot_done = None
            self._finish_boot()

    def _finish_boot(self) -> None:
        auto = self.autoload_override or (json.loads(self.config_path.read_text())["AutoLoadSave"] if self.config_path else "")
        self.answering = True
        if self.never_ready or not auto:
            self.ready = False  # title screen: the API answers, no world
            return
        self.loaded_save = auto
        self.ready = True
        self.day = self.save_day
        self.minutes = 360
        self.loop_alive = True
        self.clock_runs = True

    # game
    def crash(self) -> None:
        self.running = False
        self.answering = False

    def finish_day(self) -> None:
        self.save_day += 1
        self.day = self.save_day
        self.minutes = 360

    def state(self) -> dict:
        if not self.ready:
            return {"ready": False, "heartbeat": self.beat, "frozen": False, "players": []}
        return {
            "ready": True, "heartbeat": self.beat, "saveFolder": self.loaded_save, "day": self.day,
            "time": f"{self.minutes // 60:02}:{self.minutes % 60:02}", "frozen": self.frozen,
            "eventUp": self.event_up, "festival": False, "action": self.action,
            "players": [{"id": "p1", "action": self.action, "state": {"menu": self.menu}}],
        }

    def api_get(self, path: str) -> dict:
        if not self.running or not self.answering:
            raise OSError("connection refused")
        return self.state()

    # process control (what SystemProcesses does)
    def is_running(self) -> bool:
        return self.running

    def kill(self) -> None:
        self.kills += 1
        self.crash()

    def launch(self) -> None:
        self.launches += 1
        if self.config_path:
            self.config_at_launch.append(json.loads(self.config_path.read_text()).get("AutoLoadSave"))
        if self.launch_fails:
            self.running = False
            return
        self.running = True
        self.answering = False
        self.ready = False
        self._boot_done = self.t + self.boot_secs

    def kill_session(self, proc) -> None:
        if proc is not None:
            proc.kill()
            self.session_kills.append(proc)


def make_watchdog(tmp_path: Path, world: World, *, can_relaunch: bool = True, save_folder: str = "", **cfg_kw):
    log_dir = tmp_path / "logs"
    log_dir.mkdir(exist_ok=True)
    saves = tmp_path / "Saves"
    (saves / SAVE).mkdir(parents=True, exist_ok=True)
    config = tmp_path / "config.json"
    config.write_text(json.dumps({"HttpPort": 8770, "AutoLoadSave": "", "SplitScreen": True}, indent=2))
    world.config_path = config
    cfg = gw.WatchdogConfig(game_cmd="fake-smapi" if can_relaunch else "", game_process="StardewModdingAPI.exe",
                            settle_secs=0, save_folder=save_folder, **cfg_kw)
    said: list[str] = []
    wd = gw.Watchdog(cfg, world.api_get, world, log_dir, clock=world, say=said.append, kill_session=world.kill_session,
                     editor=gw.ModConfigEditor(config) if can_relaunch else None, saves_dir=saves,
                     smapi_log=None, threaded=False)
    wd.said = said  # type: ignore[attr-defined]
    return wd


def pump(wd: gw.Watchdog, world: World, seconds: float, step: float = 5.0) -> None:
    """Time passes; the poll thread looks every `step` seconds; the runner notices a fault like check() does."""
    for _ in range(int(seconds / step)):
        world.advance(step)
        wd.poll_once()
        wd.check()


def state(**kw) -> dict:
    base = {"ready": True, "day": 5, "time": "09:00", "frozen": False, "action": "walk_to", "heartbeat": 0, "players": []}
    base.update(kw)
    return base


def detector(**kw):
    cfg = gw.WatchdogConfig(game_process="StardewModdingAPI.exe", **kw)
    return cfg, gw.Detector(cfg, 0.0)


def run_for(det: gw.Detector, seconds: int, make_state, running=True, step=5):
    """Observe every `step` s; returns (time of the first fault, fault) or (None, None)."""
    for t in range(step, seconds + 1, step):
        f = det.observe(float(t), running, make_state(t))
        if f is not None:
            return t, f
    return None, None


# ---------------------------------------------------------------------- signal 1: process gone


def test_process_gone_needs_two_polls_in_a_row():
    _, det = detector()
    assert det.observe(5, False, None) is None
    f = det.observe(10, False, None)
    assert f is not None and f.signal == "process_gone"


def test_process_gone_streak_is_broken_by_a_sighting():
    _, det = detector()
    assert det.observe(5, False, None) is None
    assert det.observe(10, True, state(heartbeat=1)) is None
    assert det.observe(15, False, None) is None


def test_unknown_process_state_never_counts_as_gone():
    _, det = detector()
    for t in range(5, 600, 5):
        assert det.observe(t, None, state(heartbeat=t, time=f"09:{t % 60:02}")) is None


# ---------------------------------------------------------------------- signal 2: no answer


def test_no_answer_after_two_minutes():
    _, det = detector()
    assert det.observe(0, True, state()) is None
    t, f = run_for(det, 300, lambda t: None)
    assert f is not None and f.signal == "no_answer"
    assert 120 < t <= 130


def test_an_answer_resets_the_no_answer_timer():
    _, det = detector()
    det.observe(0, True, state(heartbeat=1))
    for t in range(5, 115, 5):                                   # silent for 110 s: still fine
        assert det.observe(t, True, None) is None
    assert det.observe(115, True, state(heartbeat=2, time="09:10")) is None
    for t in range(120, 240, 5):                                 # silent again, up to 120 s after the answer
        assert det.observe(t, True, None) is None
    fault = det.observe(240, True, None)                         # 125 s after it
    assert fault is not None and fault.signal == "no_answer"


def test_a_long_gap_between_polls_does_not_count_as_silence():
    """The PC slept or the runner was busy: nothing is known about that time, so the timers start over."""
    _, det = detector()
    det.observe(0, True, state(heartbeat=1))
    assert det.observe(500, True, None) is None


def test_stuck_heartbeat_means_blocked_game_loop():
    """HTTP answers (stale snapshot) but the loop is blocked: only the heartbeat shows it."""
    _, det = detector()
    t, f = run_for(det, 300, lambda t: state(heartbeat=7, frozen=True, action=None))
    assert f is not None and f.signal == "no_answer" and "heartbeat" in f.detail
    assert 120 < t <= 130


def test_counting_heartbeat_is_alive_even_while_frozen_for_ten_minutes():
    _, det = detector()
    assert run_for(det, 600, lambda t: state(heartbeat=t, frozen=True, action=None))[1] is None


def test_state_without_heartbeat_is_not_judged_by_it():
    _, det = detector()
    s = {"ready": True, "day": 5, "time": "09:00", "frozen": True, "action": None, "players": []}
    assert run_for(det, 600, lambda t: dict(s))[1] is None


# ---------------------------------------------------------------------- signal 3: stalled


def test_action_running_with_standing_clock_trips_after_three_minutes():
    _, det = detector()
    t, f = run_for(det, 400, lambda t: state(heartbeat=t))
    assert f is not None and f.signal == "stalled"
    assert 180 < t <= 190
    assert "walk_to" in f.detail


def test_moving_clock_never_trips():
    _, det = detector()
    assert run_for(det, 1200, lambda t: state(heartbeat=t, time=f"{6 + t // 100}:{t % 100:02}"))[1] is None


def test_day_change_counts_as_clock_progress():
    _, det = detector()
    assert run_for(det, 170, lambda t: state(heartbeat=t))[1] is None
    assert det.observe(175, True, state(heartbeat=175, day=6))is None
    assert run_for(det, 170, lambda t: None)[1] is None  # (no further stall evidence)


def test_thinking_time_never_counts():
    """frozen == true is the AI thinking: ten minutes of it, then the action only counts from its own start."""
    _, det = detector()
    assert run_for(det, 600, lambda t: state(heartbeat=t, frozen=True))[1] is None
    # the action starts at t=605; 170 s later it has not yet counted 180 s
    for t in range(605, 770, 5):
        assert det.observe(t, True, state(heartbeat=t, frozen=False)) is None
    assert det.observe(790, True, state(heartbeat=790, frozen=False)).signal == "stalled"


def test_short_freezes_between_actions_restart_the_stall_timer():
    _, det = detector()
    t = 0
    for _ in range(10):  # 100 s unfrozen, 5 s thinking, over and over: never 180 s in a row
        for _ in range(20):
            t += 5
            assert det.observe(t, True, state(heartbeat=t)) is None
        t += 5
        assert det.observe(t, True, state(heartbeat=t, frozen=True)) is None


def test_no_action_running_never_trips():
    _, det = detector()
    s = lambda t: state(heartbeat=t, action=None, players=[{"id": "p1", "action": None}, {"id": "p4", "action": None}])  # noqa: E731
    assert run_for(det, 900, s)[1] is None


def test_action_of_another_seat_counts():
    _, det = detector()
    s = lambda t: state(heartbeat=t, action=None, players=[{"id": "p1", "action": None}, {"id": "p3", "action": "wait"}])  # noqa: E731
    t, f = run_for(det, 400, s)
    assert f is not None and f.signal == "stalled" and "p3:wait" in f.detail


def test_world_not_ready_is_not_a_stall():
    _, det = detector()
    assert run_for(det, 900, lambda t: state(heartbeat=t, ready=False))[1] is None


@pytest.mark.parametrize("extra", [{"eventUp": True}, {"festival": True},
                                    {"players": [{"id": "p1", "action": "go_to_bed", "state": {"menu": "ShippingMenu"}}]}])
def test_cutscene_festival_and_night_menus_get_a_longer_allowance(extra):
    _, det = detector()
    assert run_for(det, 800, lambda t: state(heartbeat=t, **extra))[1] is None
    t, f = run_for(det, 1500, lambda t: state(heartbeat=t, **extra))
    assert f is not None and f.signal == "stalled"


def test_long_pause_between_polls_restarts_the_timers():
    """PC asleep for an hour: not a hang."""
    _, det = detector()
    det.observe(0, True, state(heartbeat=1))
    assert det.observe(3600, True, None) is None            # no answer, but we were not watching
    assert det.observe(3605, True, state(heartbeat=2)) is None
    assert det.observe(7200, True, state(heartbeat=2)) is None  # again: gap
    assert det.observe(7205, True, state(heartbeat=2)) is None


# ---------------------------------------------------------------------- the watchdog with a fake world


def arm(wd):
    wd.arm()
    return wd


def test_kill_smapi_is_seen_within_seconds_and_kills_the_ai_session(tmp_path):
    world = World()
    wd = arm(make_watchdog(tmp_path, world))
    proc = FakeProc()
    wd.attach(proc)
    pump(wd, world, 30)
    world.crash()
    with pytest.raises(gw.GameFault) as e:
        pump(wd, world, 60)
    assert e.value.signal == "process_gone"
    assert proc.killed
    assert world.t - 1000 <= 30 + 20


def test_blocked_game_loop_is_seen_within_about_two_minutes(tmp_path):
    world = World()
    wd = arm(make_watchdog(tmp_path, world))
    pump(wd, world, 30)
    world.loop_alive = False
    t0 = world.t
    with pytest.raises(gw.GameFault) as e:
        pump(wd, world, 400)
    assert e.value.signal == "no_answer"
    assert 120 <= world.t - t0 <= 140


def test_unanswered_api_with_a_live_process_is_seen_after_two_minutes(tmp_path):
    world = World()
    wd = arm(make_watchdog(tmp_path, world))
    pump(wd, world, 30)
    world.answering = False
    t0 = world.t
    with pytest.raises(gw.GameFault) as e:
        pump(wd, world, 400)
    assert e.value.signal == "no_answer"
    assert 120 <= world.t - t0 <= 140


def test_standing_clock_during_an_action_is_seen_after_three_minutes(tmp_path):
    world = World()
    wd = arm(make_watchdog(tmp_path, world))
    pump(wd, world, 30)
    world.clock_runs = False
    t0 = world.t
    with pytest.raises(gw.GameFault) as e:
        pump(wd, world, 600)
    assert e.value.signal == "stalled"
    assert 180 <= world.t - t0 <= 200


def test_ai_thinking_with_a_standing_clock_is_never_a_fault(tmp_path):
    world = World()
    wd = arm(make_watchdog(tmp_path, world))
    world.frozen = True
    world.action = None
    pump(wd, world, 3000)
    assert wd.fault is None


def test_a_fault_disarms_the_watchdog_and_check_raises_until_rearmed(tmp_path):
    world = World()
    wd = arm(make_watchdog(tmp_path, world))
    world.crash()
    with pytest.raises(gw.GameFault):
        pump(wd, world, 60)
    assert wd.poll_once() is None
    with pytest.raises(gw.GameFault):
        wd.check()
    wd.arm()
    wd.check()


def test_explain_failure_blames_a_dead_game(tmp_path):
    world = World()
    wd = arm(make_watchdog(tmp_path, world))
    wd.explain_failure()  # game is fine: nothing to say, the caller carries on with its own error
    world.crash()
    with pytest.raises(gw.GameFault) as e:
        wd.explain_failure()
    assert e.value.signal == "process_gone"


# ---------------------------------------------------------------------- files of a crashed attempt


def touch(d: Path, *names: str) -> None:
    for n in names:
        (d / n).write_text(n, encoding="utf-8")


def test_quarantine_names(tmp_path):
    touch(tmp_path, "day_004.jsonl", "diary_004.md", "claude_day_004_part1.jsonl",
          "day_005.jsonl", "diary_005.md", "diary_005_p2.md", "diary_005_p3.md", "journal_005.md",
          "claude_day_005_part1.jsonl", "claude_day_005_part2.jsonl", "day_006.jsonl",
          "watchdog.jsonl", "mcp.json", "smapi_day_005_attempt1.txt")
    moved = gw.quarantine(tmp_path, 5, 1, stamp="0000")
    names = sorted(p.name for p in tmp_path.iterdir())
    assert names == sorted([
        "day_004.jsonl", "diary_004.md", "claude_day_004_part1.jsonl",      # earlier days stay
        "day_005_attempt1.jsonl", "diary_005_attempt1.md",
        "diary_005_p2_attempt1.md", "diary_005_p3_attempt1.md", "journal_005_attempt1.md",
        "claude_day_005_attempt1_part1.jsonl", "claude_day_005_attempt1_part2.jsonl",
        "day_006_attempt1.jsonl",                                          # a later day is replayed too
        "watchdog.jsonl", "mcp.json", "smapi_day_005_attempt1.txt",        # not per-day files: untouched
    ])
    assert (tmp_path / "day_005_attempt1.jsonl").read_text() == "day_005.jsonl"
    assert (tmp_path / "diary_005_attempt1.md").read_text() == "diary_005.md"
    assert (tmp_path / "claude_day_005_attempt1_part2.jsonl").read_text() == "claude_day_005_part2.jsonl"
    assert not (tmp_path / "day_005.jsonl").exists()
    assert not (tmp_path / "diary_005.md").exists()
    assert (tmp_path / "day_004.jsonl").exists() and (tmp_path / "diary_004.md").exists()
    assert sorted(m.day for m in moved) == [5] * 7 + [6]


def test_quarantine_uses_a_stamp_for_the_whole_batch_when_one_name_is_taken(tmp_path):
    touch(tmp_path, "day_005.jsonl", "diary_005.md", "diary_005_attempt1.md")  # leftover of an earlier batch
    gw.quarantine(tmp_path, 5, 1, stamp="0000")
    assert sorted(p.name for p in tmp_path.iterdir()) == sorted([
        "diary_005_attempt1.md", "day_005_attempt1_0000.jsonl", "diary_005_attempt1_0000.md"])
    assert (tmp_path / "diary_005_attempt1.md").read_text() == "diary_005_attempt1.md"  # untouched


def test_quarantine_moves_exactly_the_per_day_files(tmp_path):
    touch(tmp_path, "day_005.jsonl", "diary_005.md", "diary_005_p2.md", "claude_day_005_part1.jsonl", "mcp.json", "watchdog.jsonl")
    gw.quarantine(tmp_path, 5, 1)
    assert sorted(p.name for p in tmp_path.iterdir()) == sorted([
        "day_005_attempt1.jsonl", "diary_005_attempt1.md", "diary_005_p2_attempt1.md",
        "claude_day_005_attempt1_part1.jsonl", "mcp.json", "watchdog.jsonl"])


def test_quarantine_twice_numbers_the_attempts_and_leaves_old_ones(tmp_path):
    touch(tmp_path, "day_005.jsonl", "diary_005.md")
    gw.quarantine(tmp_path, 5, 1)
    touch(tmp_path, "day_005.jsonl", "diary_005.md")
    gw.quarantine(tmp_path, 5, 2)
    assert sorted(p.name for p in tmp_path.iterdir()) == sorted([
        "day_005_attempt1.jsonl", "day_005_attempt2.jsonl", "diary_005_attempt1.md", "diary_005_attempt2.md"])


def test_quarantine_never_overwrites_files_of_an_earlier_batch(tmp_path):
    touch(tmp_path, "day_005_attempt1.jsonl")
    old = (tmp_path / "day_005_attempt1.jsonl").read_text()
    touch(tmp_path, "day_005.jsonl")
    moved = gw.quarantine(tmp_path, 5, 1, stamp="0102")
    assert (tmp_path / "day_005_attempt1.jsonl").read_text() == old
    assert moved[0].moved.name == "day_005_attempt1_0102.jsonl"


def test_restore_puts_back_only_finished_days(tmp_path):
    touch(tmp_path, "day_005.jsonl", "diary_005.md", "day_006.jsonl", "claude_day_005_part1.jsonl")
    moved = gw.quarantine(tmp_path, 5, 1)
    back = gw.restore(moved, 6)
    assert sorted(m.original.name for m in back) == ["claude_day_005_part1.jsonl", "day_005.jsonl", "diary_005.md"]
    assert (tmp_path / "day_005.jsonl").exists() and (tmp_path / "day_006_attempt1.jsonl").exists()
    assert not (tmp_path / "day_006.jsonl").exists()


# ---------------------------------------------------------------------- mod config.json


def test_config_editor_sets_keeps_other_keys_and_restores(tmp_path):
    p = tmp_path / "config.json"
    p.write_text(json.dumps({"HttpPort": 8770, "AutoLoadSave": "Old_1", "FarmhandNames": ["a", "b"]}, indent=2), encoding="utf-8")
    ed = gw.ModConfigEditor(p)
    assert ed.set("Duck_123") is True
    data = json.loads(p.read_text(encoding="utf-8"))
    assert data == {"HttpPort": 8770, "AutoLoadSave": "Duck_123", "FarmhandNames": ["a", "b"]}
    assert not (tmp_path / "config.json.tmp").exists()
    assert ed.set("Duck_123") is False  # already right: untouched
    assert ed.restore() is True
    assert json.loads(p.read_text(encoding="utf-8"))["AutoLoadSave"] == "Old_1"
    assert ed.restore() is False


def test_config_editor_restore_keeps_edits_made_meanwhile(tmp_path):
    p = tmp_path / "config.json"
    p.write_text(json.dumps({"AutoLoadSave": "", "ThoughtLanguage": "Thai"}), encoding="utf-8")
    ed = gw.ModConfigEditor(p)
    ed.set("Duck_123")
    data = json.loads(p.read_text())
    data["ThoughtLanguage"] = "English"
    p.write_text(json.dumps(data))
    ed.restore()
    assert json.loads(p.read_text()) == {"AutoLoadSave": "", "ThoughtLanguage": "English"}


def test_config_editor_adds_a_missing_key_and_matches_case_insensitively(tmp_path):
    p = tmp_path / "config.json"
    p.write_text("\ufeff" + json.dumps({"HttpPort": 1}), encoding="utf-8")  # SMAPI/Notepad may write a BOM
    ed = gw.ModConfigEditor(p)
    ed.set("S_1")
    assert json.loads(p.read_text(encoding="utf-8"))["AutoLoadSave"] == "S_1"
    ed.restore()
    assert json.loads(p.read_text(encoding="utf-8"))["AutoLoadSave"] == ""
    p.write_text(json.dumps({"autoloadsave": "x"}), encoding="utf-8")
    ed2 = gw.ModConfigEditor(p)
    ed2.set("y")
    assert json.loads(p.read_text()) == {"autoloadsave": "y"}


def test_config_editor_refuses_a_file_it_cannot_parse_and_leaves_it_alone(tmp_path):
    p = tmp_path / "config.json"
    text = '{ // a comment\n  "AutoLoadSave": ""\n}'
    p.write_text(text, encoding="utf-8")
    ed = gw.ModConfigEditor(p)
    with pytest.raises(ValueError):
        ed.set("Duck_123")
    with pytest.raises(ValueError):
        ed.check()
    assert p.read_text(encoding="utf-8") == text


# ---------------------------------------------------------------------- the day loop with crashes


class FakeDay:
    """play(attempt) for run_day: writes the files a real attempt would, then behaves as scripted."""

    def __init__(self, wd, world, script):
        self.wd, self.world, self.script = wd, world, list(script)
        self.calls: list[int] = []
        self.files_at_start: list[list[str]] = []

    def __call__(self, attempt: int) -> dict:
        wd, world = self.wd, self.world
        d = wd.log_dir
        self.calls.append(attempt)
        self.files_at_start.append(sorted(p.name for p in d.iterdir()))
        day = world.day
        step = self.script.pop(0)
        with (d / f"day_{day:03}.jsonl").open("a") as f:
            f.write(json.dumps({"attempt_wrote_this": attempt}) + "\n")
        (d / f"claude_day_{day:03}_part1.jsonl").write_text(f"transcript of play attempt {attempt}")
        pump(wd, world, 60)
        if step == "ok":
            (d / f"diary_{day:03}.md").write_text(f"diary written in attempt {attempt}")
            (d / f"diary_{day:03}_p2.md").write_text(f"p2 diary written in attempt {attempt}")
            world.finish_day()
            pump(wd, world, 10)
            return world.api_get("/api/state")
        (d / f"diary_{day:03}.md").write_text(f"diary of CRASHED attempt {attempt}")  # AI wrote it just before the crash
        if step == "kill":
            world.crash()
        elif step == "kill_after_night_save":
            (d / f"day_{day + 1:03}.jsonl").write_text("Save loaded\n")
            world.save_day = day + 1
            world.crash()
        elif step == "hang":
            world.loop_alive = False
        elif step == "stall":
            world.clock_runs = False
        pump(wd, world, 600)
        raise AssertionError("the watchdog never tripped")


def events(wd) -> list[dict]:
    return [json.loads(line) for line in (wd.log_dir / "watchdog.jsonl").read_text().splitlines()]


def test_crash_then_replay_logs_diaries_and_attempt_names(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    d = wd.log_dir
    (d / "diary_004.md").write_text("diary of day 4")  # a finished day must survive
    wd.ready(lambda: world.api_get("/api/state"))
    play = FakeDay(wd, world, ["kill", "ok"])

    result = wd.run_day(5, play)

    assert play.calls == [1, 2]
    assert result["day"] == 6
    assert world.launches == 1 and world.kills == 1
    # the crashed attempt's files, kept apart
    assert (d / "day_005_attempt1.jsonl").read_text() == '{"attempt_wrote_this": 1}\n'
    assert (d / "claude_day_005_attempt1_part1.jsonl").read_text() == "transcript of play attempt 1"
    assert (d / "diary_005_attempt1.md").read_text() == "diary of CRASHED attempt 1"
    # the live files are the replay's alone
    assert (d / "day_005.jsonl").read_text() == '{"attempt_wrote_this": 2}\n'
    assert (d / "claude_day_005_part1.jsonl").read_text() == "transcript of play attempt 2"
    assert (d / "diary_005.md").read_text() == "diary written in attempt 2"
    assert (d / "diary_004.md").read_text() == "diary of day 4"
    # the replay started clean: nothing of day 5 was lying around for the AI to find
    assert not [n for n in play.files_at_start[1] if "005" in n and "attempt" not in n]
    # AutoLoadSave was in place when the game started, and is put back at the end
    assert world.config_at_launch == [SAVE]
    wd.close()
    assert json.loads((tmp_path / "config.json").read_text())["AutoLoadSave"] == ""
    # the event log tells the story
    log = events(wd)
    assert [(e["event"], e.get("attempt")) for e in log if e["event"] in ("fault", "day_done")] == [("fault", 1), ("day_done", 2)]
    assert next(e for e in log if e["event"] == "fault")["signal"] == "process_gone"


@pytest.mark.parametrize("how,signal", [("kill", "process_gone"), ("hang", "no_answer"), ("stall", "stalled")])
def test_each_signal_triggers_the_same_recovery(tmp_path, how, signal):
    world = World()
    wd = make_watchdog(tmp_path, world)
    wd.ready(lambda: world.api_get("/api/state"))
    play = FakeDay(wd, world, [how, "ok"])
    wd.run_day(5, play)
    assert play.calls == [1, 2]
    assert world.launches == 1
    assert [e["signal"] for e in events(wd) if e["event"] == "fault"] == [signal]
    assert (wd.log_dir / "day_005_attempt1.jsonl").exists()


def test_hung_game_is_killed_before_the_relaunch(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    wd.ready(lambda: world.api_get("/api/state"))
    wd.run_day(5, FakeDay(wd, world, ["hang", "ok"]))
    assert world.kills == 1 and world.launches == 1


def test_ai_session_is_killed_on_a_fault(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    wd.ready(lambda: world.api_get("/api/state"))
    proc = FakeProc()

    def play(attempt):
        if attempt == 1:
            wd.attach(proc)
            world.crash()
            pump(wd, world, 60)
        return FakeDay(wd, world, ["ok"])(attempt)

    wd.run_day(5, play)
    assert proc.killed and proc in world.session_kills


def test_two_retries_then_the_third_crash_stops_the_batch(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    wd.ready(lambda: world.api_get("/api/state"))
    play = FakeDay(wd, world, ["kill", "hang", "stall"])

    with pytest.raises(gw.BatchStopped) as e:
        wd.run_day(5, play)

    msg = str(e.value)
    assert "day 5 failed 3 times" in msg and "Stopping the batch" in msg
    assert "process_gone" in msg and "no_answer" in msg and "stalled" in msg
    assert "mod" in msg  # points at a likely mod bug
    assert play.calls == [1, 2, 3]
    assert world.launches == 2          # two retries, no third relaunch
    d = wd.log_dir
    for n in (1, 2, 3):
        assert (d / f"day_005_attempt{n}.jsonl").exists()
        assert (d / f"claude_day_005_attempt{n}_part1.jsonl").exists()
        assert (d / f"diary_005_attempt{n}.md").exists()
    assert not (d / "diary_005.md").exists() and not (d / "day_005.jsonl").exists()
    assert not world.running  # the broken game is not left running


def test_retry_budget_is_per_day_and_configurable(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world, max_retries=1)
    wd.ready(lambda: world.api_get("/api/state"))
    play = FakeDay(wd, world, ["kill", "ok", "kill", "kill"])
    wd.run_day(5, play)  # one retry used on day 5
    world.advance(1)
    wd.ready(lambda: world.api_get("/api/state"))
    with pytest.raises(gw.BatchStopped):  # day 6: crash, retry, crash again -> stop (budget of day 6 is separate)
        wd.run_day(6, play)
    assert play.calls == [1, 2, 1, 2]


def test_crash_after_the_night_save_counts_the_day_as_done_and_keeps_its_diary(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    wd.ready(lambda: world.api_get("/api/state"))
    play = FakeDay(wd, world, ["kill_after_night_save"])

    result = wd.run_day(5, play)

    assert play.calls == [1]                       # not played again
    assert result["day"] == 6                      # the relaunched game is on day 6
    d = wd.log_dir
    assert (d / "diary_005.md").read_text() == "diary of CRASHED attempt 1"   # day 5 is real: its files are back
    assert (d / "day_005.jsonl").exists() and (d / "claude_day_005_part1.jsonl").exists()
    assert not (d / "diary_005_attempt1.md").exists()
    assert (d / "day_006_attempt1.jsonl").exists()  # day 6 had only the aborted timeline's lines
    assert wd.open_day == 6
    assert [e["event"] for e in events(wd)][-1] == "day_done_in_save"


def test_crash_found_between_days_is_recovered_in_ready(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    wd.ready(lambda: world.api_get("/api/state"))
    wd.run_day(5, FakeDay(wd, world, ["ok"]))      # day 6 begins
    world.crash()
    calls = []

    def wait_fn():
        calls.append(1)
        pump(wd, world, 60)  # the poll thread notices; check() inside the runner's wait raises
        return world.api_get("/api/state")

    state = wd.ready(wait_fn)
    assert state["day"] == 6 and world.launches == 1
    assert wd.crashes == {6: 1}


def test_failed_relaunch_uses_up_a_retry(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world, launch_timeout_secs=100, launch_grace_secs=10)
    wd.ready(lambda: world.api_get("/api/state"))
    world.never_ready = True  # AutoLoad does not work: the relaunched game stays on the title screen
    play = FakeDay(wd, world, ["kill"])

    with pytest.raises(gw.BatchStopped) as e:
        wd.run_day(5, play)

    assert world.launches == 2  # try 2 and try 3 were relaunches that never got ready; try 4 is refused
    assert "not_ready" in str(e.value) and "failed 3 times" in str(e.value)


def test_relaunch_that_never_starts_the_process_is_detected(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world, launch_timeout_secs=100, launch_grace_secs=10)
    wd.ready(lambda: world.api_get("/api/state"))
    world.launch_fails = True
    with pytest.raises(gw.BatchStopped) as e:
        wd.run_day(5, FakeDay(wd, world, ["kill"]))
    assert "launch_failed" in str(e.value)


def test_wrong_save_after_relaunch_stops_at_once_without_burning_retries(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    wd.ready(lambda: world.api_get("/api/state"))
    world.autoload_override = "Other_999"
    with pytest.raises(gw.BatchStopped) as e:
        wd.run_day(5, FakeDay(wd, world, ["kill", "ok"]))
    assert "Other_999" in str(e.value) and SAVE in str(e.value)
    assert world.launches == 1


def test_without_game_cmd_a_crash_stops_the_batch_and_leaves_the_game_alone(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world, can_relaunch=False)
    wd.ready(lambda: world.api_get("/api/state"))
    play = FakeDay(wd, world, ["hang"])
    with pytest.raises(gw.BatchStopped) as e:
        wd.run_day(5, play)
    assert "--game-cmd" in str(e.value)
    assert world.kills == 0 and world.launches == 0     # a hung game is not touched without a way to restart it
    assert (wd.log_dir / "day_005_attempt1.jsonl").exists()
    assert not (wd.log_dir / "diary_005.md").exists()


def test_save_folder_is_taken_from_the_loaded_save_and_checked(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    assert wd.expected_save == ""
    wd.ready(lambda: world.api_get("/api/state"))
    assert wd.expected_save == SAVE

    world2 = World()
    world2.loaded_save = "Ghost_404"          # SMAPI's idea of the name, not a folder in Saves
    wd2 = make_watchdog(tmp_path / "b", world2) if (tmp_path / "b").mkdir() is None else None
    with pytest.raises(gw.BatchStopped) as e:
        wd2.ready(lambda: world2.api_get("/api/state"))
    assert "Ghost_404" in str(e.value) and "--save-folder" in str(e.value)


def test_smoke_copies_are_not_taken_as_the_run_save(tmp_path):
    world = World()
    world.loaded_save = "smoke_Duck_123"
    wd = make_watchdog(tmp_path, world, save_folder="")
    wd._learn(world.state())
    assert wd.expected_save == ""


def test_smapi_log_of_the_crashed_attempt_is_kept(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    log = tmp_path / "SMAPI-latest.txt"
    log.write_text("[ERROR] boom")
    wd.smapi_log = log
    wd.ready(lambda: world.api_get("/api/state"))
    wd.run_day(5, FakeDay(wd, world, ["kill", "ok"]))
    assert (wd.log_dir / "smapi_day_005_attempt1.txt").read_text() == "[ERROR] boom"


def test_relaunch_test_reports_a_working_autoload(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world)
    msg = wd.relaunch_test()
    assert "AutoLoadSave works" in msg and SAVE in msg
    assert world.kills == 1 and world.launches == 1 and world.config_at_launch == [SAVE]
    wd.close()
    assert json.loads((tmp_path / "config.json").read_text())["AutoLoadSave"] == ""


def test_relaunch_test_fails_loudly_when_autoload_does_not_load(tmp_path):
    world = World()
    wd = make_watchdog(tmp_path, world, launch_timeout_secs=60)
    world.never_ready = True
    with pytest.raises(gw.GameFault) as e:
        wd.relaunch_test()
    assert e.value.signal == "not_ready"


# ---------------------------------------------------------------------- processes and command line


class Recorder:
    def __init__(self, returncode=0, stdout=""):
        self.calls: list[tuple] = []
        self.returncode, self.stdout = returncode, stdout

    def run(self, cmd, **kw):
        self.calls.append(("run", cmd))
        return subprocess.CompletedProcess(cmd, self.returncode, self.stdout, "")

    def popen(self, argv, **kw):
        self.calls.append(("popen", argv, kw))
        return object()


def test_windows_process_check_and_kill_use_tasklist_and_taskkill():
    rec = Recorder(stdout='"StardewModdingAPI.exe","1234","Console","1","300,000 K"\n')
    p = gw.SystemProcesses("StardewModdingAPI.exe", run=rec.run, popen=rec.popen, windows=True)
    assert p.is_running() is True
    assert rec.calls[0][1][:4] == ["tasklist", "/FI", "IMAGENAME eq StardewModdingAPI.exe", "/FO"]
    rec.stdout = "INFO: No tasks are running which match the specified criteria.\n"
    assert p.is_running() is False
    p.kill()
    assert rec.calls[-1][1] == ["taskkill", "/F", "/T", "/IM", "StardewModdingAPI.exe"]
    rec.returncode = 1
    assert p.is_running() is None  # tasklist itself failed: unknown, not 'gone'


def test_posix_process_check_uses_the_15_character_kernel_name():
    rec = Recorder(returncode=0)
    p = gw.SystemProcesses("StardewModdingAPI", run=rec.run, windows=False)
    assert p.is_running() is True
    assert rec.calls[0][1] == ["pgrep", "-x", "StardewModdingA"]
    rec.returncode = 1
    assert p.is_running() is False
    rec.returncode = 2
    assert p.is_running() is None
    p.kill()
    assert rec.calls[-1][1] == ["pkill", "-9", "-x", "StardewModdingA"]


def test_empty_process_name_disables_the_process_signal():
    assert gw.SystemProcesses("", windows=True).is_running() is None


def test_launch_on_windows_gets_its_own_console_and_working_dir():
    rec = Recorder()
    p = gw.SystemProcesses("x.exe", ["C:/Games/Stardew/StardewModdingAPI.exe", "--flag"], cwd="C:/Games/Stardew",
                           env={"A": "1"}, popen=rec.popen, windows=True)
    p.launch()
    _, argv, kw = rec.calls[0]
    assert argv == ["C:/Games/Stardew/StardewModdingAPI.exe", "--flag"]
    assert kw["cwd"] == "C:/Games/Stardew" and kw["env"] == {"A": "1"}
    assert kw["creationflags"] == 0x10 | 0x200


def test_launch_on_posix_detaches_from_the_runner():
    rec = Recorder()
    gw.SystemProcesses("x", ["/opt/stardew/StardewModdingAPI"], popen=rec.popen, windows=False).launch()
    kw = rec.calls[0][2]
    assert kw["start_new_session"] is True and kw["stdin"] == subprocess.DEVNULL


def test_launch_without_a_command_is_an_error():
    with pytest.raises(OSError):
        gw.SystemProcesses("x").launch()


def test_kill_tree_kills_children_and_process_and_tolerates_none_and_exited():
    rec = Recorder()
    gw.kill_tree(None, run=rec.run)
    done = FakeProc()
    done.killed = True
    gw.kill_tree(done, run=rec.run)
    assert rec.calls == []
    live = FakeProc()
    gw.kill_tree(live, run=rec.run, windows=True)
    assert rec.calls[0][1] == ["taskkill", "/PID", "4242", "/T", "/F"] and live.killed
    live2 = FakeProc()
    gw.kill_tree(live2, run=rec.run, windows=False)
    assert rec.calls[1][1] == ["pkill", "-9", "-P", "4242"] and live2.killed


def test_parse_game_cmd(tmp_path):
    exe = tmp_path / "Stardew Valley" / "StardewModdingAPI.exe"
    exe.parent.mkdir()
    exe.write_text("")
    assert gw.parse_game_cmd("") == []
    assert gw.parse_game_cmd(str(exe)) == [str(exe)]                 # spaces without quotes: it is just a file
    assert gw.parse_game_cmd(f'"{exe}"') == [str(exe)]
    assert gw.parse_game_cmd(f'"{exe}" --no-terminal', windows=False) == [str(exe), "--no-terminal"]
    assert gw.parse_game_cmd(r'"C:\Program Files\Stardew\StardewModdingAPI.exe" --x', windows=True) == [
        r"C:\Program Files\Stardew\StardewModdingAPI.exe", "--x"]
    assert gw.parse_game_cmd(r"C:\Games\SMAPI.exe", windows=True) == [r"C:\Games\SMAPI.exe"]


def parse(argv):
    ap = argparse.ArgumentParser()
    gw.add_args(ap)
    return ap.parse_args(argv)


def test_from_args_derives_the_mod_config_next_to_the_exe(tmp_path):
    exe = tmp_path / "StardewModdingAPI.exe"
    exe.write_text("")
    cfg = tmp_path / "Mods" / "StardewMCP" / "config.json"
    cfg.parent.mkdir(parents=True)
    cfg.write_text("{}")
    wd = gw.from_args(parse(["--game-cmd", str(exe), "--crash-retries", "1", "--stall-secs", "60"]), tmp_path / "logs", lambda p: {})
    assert isinstance(wd, gw.Watchdog) and wd.can_relaunch
    assert wd.editor.path == cfg
    assert wd.cfg.max_retries == 1 and wd.cfg.stall_secs == 60
    assert wd.procs.cwd == str(tmp_path)
    assert wd.procs.env["SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS"] == "0"


def test_from_args_fails_early_on_a_bad_setup(tmp_path):
    exe = tmp_path / "StardewModdingAPI.exe"
    exe.write_text("")
    with pytest.raises(SystemExit, match="config.json"):
        gw.from_args(parse(["--game-cmd", str(exe)]), tmp_path / "logs", lambda p: {})
    with pytest.raises(SystemExit, match="not a file"):
        gw.from_args(parse(["--game-cmd", str(tmp_path / "nope" / "SMAPI.exe")]), tmp_path / "logs", lambda p: {})
    bad = tmp_path / "bad.json"
    bad.write_text("{ nope")
    with pytest.raises(SystemExit, match="plain JSON"):
        gw.from_args(parse(["--game-cmd", str(exe), "--mod-config", str(bad)]), tmp_path / "logs", lambda p: {})


def test_from_args_without_game_cmd_is_detect_only_and_no_watchdog_is_null(tmp_path):
    wd = gw.from_args(parse([]), tmp_path / "logs", lambda p: {})
    assert isinstance(wd, gw.Watchdog) and not wd.can_relaunch
    assert isinstance(gw.from_args(parse(["--no-watchdog"]), tmp_path / "logs", lambda p: {}), gw.NullWatchdog)


def test_null_watchdog_changes_nothing():
    n = gw.NullWatchdog()
    assert n.ready(lambda: {"day": 3}) == {"day": 3}
    assert n.run_day(3, lambda attempt: {"day": 4, "attempt": attempt}) == {"day": 4, "attempt": 1}
    n.check(); n.explain_failure(); n.attach(None); n.detach(); n.close()
    assert n.armed is False


# ---------------------------------------------------------------------- the run_days.py glue


@pytest.fixture
def rd(monkeypatch, tmp_path):
    """runner/run_days.py with the clock, the API and the claude session replaced by the fake world."""
    import types

    import run_days

    world = World()
    monkeypatch.setattr(run_days, "api_get", world.api_get)
    monkeypatch.setattr(run_days, "time", types.SimpleNamespace(time=world.now, sleep=world.sleep))
    monkeypatch.setattr(run_days, "GUARD", gw.NullWatchdog())
    run_days.world = world  # type: ignore[attr-defined]
    yield run_days
    del run_days.world


def run_main(rd, monkeypatch, tmp_path, *extra):
    monkeypatch.setattr(sys, "argv", ["run_days.py", "--out", str(tmp_path), "--skip-smoke", *extra])
    rd.main()


def test_run_days_replays_the_day_after_a_crash_without_telling_the_ai(rd, monkeypatch, tmp_path):
    world = rd.world
    wd = make_watchdog(tmp_path, world)           # its log dir is tmp_path/logs, which is what --out gives main()
    day = FakeDay(wd, world, ["kill", "ok"])
    prompts: list[tuple[int, str]] = []
    backups: list[int] = []

    def session(args, out_dir, day_no, prompt, system_prompt, part):
        prompts.append((part, prompt))
        day(len(day.calls) + 1)
        return 1

    monkeypatch.setattr(gw, "from_args", lambda args, log_dir, api_get, api: wd)
    monkeypatch.setattr(rd, "run_session", session)
    monkeypatch.setattr(rd, "backup_save", lambda out_dir, d: backups.append(d))
    run_main(rd, monkeypatch, tmp_path, "--days", "1")

    assert rd.GUARD is wd
    assert [p for p, _ in prompts] == [1, 1]                      # the replay starts again with a fresh part 1
    assert prompts[0][1] == prompts[1][1]
    assert not any(w in prompts[1][1].lower() for w in ("crash", "relaunch", "restart", "hang"))
    assert world.launches == 1 and backups == [5]
    logs = tmp_path / "logs"
    assert (logs / "day_005_attempt1.jsonl").exists() and (logs / "claude_day_005_attempt1_part1.jsonl").exists()
    assert (logs / "diary_005.md").read_text() == "diary written in attempt 2"
    assert "CRASHED" in (logs / "diary_005_attempt1.md").read_text()


def test_run_days_stops_the_batch_after_the_retries(rd, monkeypatch, tmp_path):
    world = rd.world
    wd = make_watchdog(tmp_path, world)
    day = FakeDay(wd, world, ["kill", "hang", "stall"])
    monkeypatch.setattr(gw, "from_args", lambda args, log_dir, api_get, api: wd)
    monkeypatch.setattr(rd, "run_session", lambda *a: (day(len(day.calls) + 1), 1)[1])
    monkeypatch.setattr(rd, "backup_save", lambda out_dir, d: pytest.fail("a day that never finished must not be backed up"))
    with pytest.raises(gw.BatchStopped):
        run_main(rd, monkeypatch, tmp_path, "--days", "1")
    assert day.calls == [1, 2, 3]


def test_run_days_without_watchdog_plays_as_before(rd, monkeypatch, tmp_path):
    world = rd.world
    parts: list[int] = []

    def session(args, out_dir, day_no, prompt, system_prompt, part):
        parts.append(part)
        if part == 2:
            world.finish_day()
        return 1

    monkeypatch.setattr(rd, "run_session", session)
    monkeypatch.setattr(rd, "backup_save", lambda out_dir, d: None)
    run_main(rd, monkeypatch, tmp_path, "--days", "1", "--no-watchdog")
    assert isinstance(rd.GUARD, gw.NullWatchdog)
    assert parts == [1, 2]


def test_wait_ready_gives_up_with_the_old_message_without_watchdog(rd, monkeypatch):
    rd.world.ready = False
    with pytest.raises(SystemExit) as e:
        rd.wait_ready(timeout=30)
    assert "not ready" in str(e.value)


def test_wait_ready_reports_a_fault_when_the_watchdog_is_armed(rd, tmp_path):
    world = rd.world
    wd = make_watchdog(tmp_path, world)
    wd.ready(lambda: world.api_get("/api/state"))
    rd.GUARD = wd
    world.ready = False
    with pytest.raises(gw.GameFault) as e:
        rd.wait_ready(timeout=30)
    assert e.value.signal == "not_ready"
