"""run_days.main() with run control: stop_after_day, stop_now and pause, against fakes (no game, no claude)."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_days  # noqa: E402
from run_control import RunControl  # noqa: E402
from test_run_control import FakeMod  # noqa: E402


class Harness:
    def __init__(self, monkeypatch, tmp_path, argv):
        self.mod = FakeMod()
        self.day = 1
        self.sessions = []   # (day, part)
        self.backups = []
        self.on_session = None
        self.ctl = RunControl(self.mod.get, self.mod.post, say=lambda *a, **k: None,
                              sleep=lambda s: None, handle_sigint=False)
        monkeypatch.setattr(run_days, "CTL", self.ctl)
        monkeypatch.setattr(run_days, "wait_ready", lambda timeout=600: {"ready": True, "day": self.day})
        monkeypatch.setattr(run_days, "game_state", lambda: {"ready": True, "day": self.day})
        monkeypatch.setattr(run_days, "run_session", self.run_session)
        monkeypatch.setattr(run_days, "backup_save", lambda out, day, keep=10: self.backups.append(day))
        monkeypatch.setattr(run_days.time, "sleep", lambda s: None)
        monkeypatch.setattr(sys, "argv", ["run_days.py", "--skip-smoke", "--out", str(tmp_path), *argv])

    def run_session(self, args, out_dir, day, prompt, system_prompt, part):
        self.sessions.append((day, part))
        if self.on_session:
            self.on_session(day, part)
        return 3

    def go_to_bed(self):
        self.day += 1


def test_stop_after_day_during_day_3_finishes_day_3_and_exits_before_day_4(monkeypatch, tmp_path, capsys):
    h = Harness(monkeypatch, tmp_path, ["--days", "10"])

    def session(day, part):
        if day == 3:
            h.mod.post("/api/control", {"action": "stop_after_day"})  # curl / run_control.py during day 3
        h.go_to_bed()  # the AI still ends day 3 with go_to_bed

    h.on_session = session
    run_days.main()
    assert [d for d, _ in h.sessions] == [1, 2, 3]
    assert h.backups == [1, 2, 3]
    assert "Stopped as requested after 3 day(s)" in capsys.readouterr().out


def test_stop_after_day_still_finishes_a_day_that_needs_two_sessions(monkeypatch, tmp_path):
    h = Harness(monkeypatch, tmp_path, ["--days", "10"])

    def session(day, part):
        if part == 1:
            h.mod.post("/api/control", {"action": "stop_after_day"})  # session 1 ends before bed
        else:
            h.go_to_bed()

    h.on_session = session
    run_days.main()
    assert h.sessions == [(1, 1), (1, 2)]
    assert h.backups == [1]


def test_stop_now_ends_the_batch_without_starting_another_session_or_backing_up_the_unfinished_day(monkeypatch, tmp_path, capsys):
    h = Harness(monkeypatch, tmp_path, ["--days", "10"])

    def session(day, part):
        if day == 2:
            h.mod.post("/api/control", {"action": "stop_now"})  # the watcher ended claude; the day is unfinished
        else:
            h.go_to_bed()

    h.on_session = session
    run_days.main()
    assert h.sessions == [(1, 1), (2, 1)]
    assert h.backups == [1]
    assert "day 2 is not finished" in capsys.readouterr().out


def test_stop_now_after_the_last_call_was_go_to_bed_still_backs_up_that_day(monkeypatch, tmp_path, capsys):
    h = Harness(monkeypatch, tmp_path, ["--days", "10"])

    def session(day, part):
        h.go_to_bed()
        h.mod.post("/api/control", {"action": "stop_now"})

    h.on_session = session
    run_days.main()
    assert h.sessions == [(1, 1)]
    assert h.backups == [1]
    assert "day 1 had finished" in capsys.readouterr().out


def test_stale_stop_requests_from_an_earlier_run_are_cleared_at_start(monkeypatch, tmp_path):
    h = Harness(monkeypatch, tmp_path, ["--days", "2"])
    h.mod.stop_after_day = True
    h.on_session = lambda day, part: h.go_to_bed()
    run_days.main()
    assert [d for d, _ in h.sessions] == [1, 2]


def test_a_pause_between_days_holds_the_next_session_until_resume(monkeypatch, tmp_path):
    h = Harness(monkeypatch, tmp_path, ["--days", "2"])
    log = []

    def session(day, part):
        log.append(("session", day, h.mod.paused))
        if day == 1:
            h.mod.post("/api/control", {"action": "pause"})
        h.go_to_bed()

    def sleep(s):  # the operator resumes a little later
        log.append(("waiting",))
        h.mod.post("/api/control", {"action": "resume"})

    h.on_session = session
    h.ctl._sleep = sleep
    run_days.main()
    assert log == [("session", 1, False), ("waiting",), ("session", 2, False)]


def test_keep_backups_is_passed_on(monkeypatch, tmp_path):
    h = Harness(monkeypatch, tmp_path, ["--days", "1", "--keep-backups", "4"])
    seen = []
    monkeypatch.setattr(run_days, "backup_save", lambda out, day, keep=10: seen.append(keep))
    h.on_session = lambda day, part: h.go_to_bed()
    run_days.main()
    assert seen == [4]
