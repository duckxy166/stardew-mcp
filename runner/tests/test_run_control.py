"""runner/run_control.py against a fake of the mod's /api/control (no game, no network)."""

import json
import signal
import sys
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import run_control  # noqa: E402
from run_control import RunControl  # noqa: E402


class FakeMod:
    """Same rules as RunControl.cs: pause/resume, stop flags, and whether an action is running."""

    def __init__(self):
        self.paused = False
        self.stop_after_day = False
        self.stop_now = False
        self.action_running = False
        self.up = True
        self.posts = []

    def get(self, path):
        assert path == "/api/control"
        if not self.up:
            raise OSError("game is down")
        return {"paused": self.paused, "stopAfterDay": self.stop_after_day, "stopNow": self.stop_now,
                "held": (self.paused or self.stop_now) and not self.action_running,
                "actionRunning": self.action_running}

    def post(self, path, body):
        assert path == "/api/control"
        if not self.up:
            raise OSError("game is down")
        action = body["action"]
        self.posts.append(action)
        if action == "pause":
            self.paused = True
        elif action == "resume":
            self.paused = False
            self.stop_now = False
        elif action == "stop_after_day":
            self.stop_after_day = True
        elif action == "stop_now":
            self.stop_now = True
        elif action == "clear_stop":
            self.stop_after_day = self.stop_now = False
        else:
            return {"ok": False}
        return {"ok": True}


class FakeProc:
    def __init__(self):
        self.returncode = None
        self.terminated = 0
        self.killed = 0

    def poll(self):
        return self.returncode

    def terminate(self):
        self.terminated += 1
        self.returncode = -15

    def kill(self):
        self.killed += 1
        self.returncode = -9


def make(mod, **kw):
    kw.setdefault("say", lambda *a, **k: None)
    kw.setdefault("sleep", lambda s: None)
    kw.setdefault("handle_sigint", False)
    return RunControl(mod.get, mod.post, **kw)


def test_stop_after_day_is_read_from_the_mod_and_sticks_after_a_game_restart():
    mod = FakeMod()
    ctl = make(mod)
    assert not ctl.stop_after_day_requested()
    mod.post("/api/control", {"action": "stop_after_day"})
    assert ctl.stop_after_day_requested()
    mod.stop_after_day = False  # the game was restarted by the watchdog and forgot its flags
    assert ctl.stop_after_day_requested()
    assert ctl.stop_between_days()


def test_unreachable_game_is_not_a_stop_request():
    mod = FakeMod()
    mod.up = False
    ctl = make(mod)
    assert ctl.mod_state() is None
    assert not ctl.stop_between_days()


def test_start_clears_stale_stop_requests_but_not_a_pause():
    mod = FakeMod()
    mod.stop_after_day = mod.stop_now = mod.paused = True
    ctl = make(mod)
    ctl.start()
    assert mod.posts == ["clear_stop"]
    assert not ctl.stop_between_days()
    assert mod.paused  # an operator's pause before the batch is respected


def test_request_keeps_a_stop_locally_when_the_game_is_unreachable_and_delivers_it_later():
    mod = FakeMod()
    mod.up = False
    ctl = make(mod)
    assert ctl.request("stop_after_day") is False
    assert ctl.stop_after_day_requested()
    mod.up = True
    ctl._flush_unsent()
    assert mod.stop_after_day


def test_request_rejects_unknown_actions():
    with pytest.raises(ValueError):
        make(FakeMod()).request("explode")


def test_wait_while_paused_returns_when_resumed():
    mod = FakeMod()
    mod.paused = True
    sleeps = []

    def sleep(s):
        sleeps.append(s)
        if len(sleeps) == 3:
            mod.post("/api/control", {"action": "resume"})

    said = []
    ctl = make(mod, sleep=sleep, say=said.append)
    ctl.wait_while_paused()
    assert len(sleeps) == 3
    assert len(said) == 1 and "Paused" in said[0]


def test_wait_while_paused_returns_at_once_on_stop_now_or_without_a_game():
    mod = FakeMod()
    mod.paused = mod.stop_now = True
    ctl = make(mod, sleep=lambda s: pytest.fail("must not wait"))
    ctl.wait_while_paused()
    mod.up = False
    make(mod, sleep=lambda s: pytest.fail("must not wait")).wait_while_paused()


def test_watcher_waits_for_the_running_call_then_ends_the_session():
    mod = FakeMod()
    mod.action_running = True
    proc = FakeProc()
    ctl = make(mod, poll_secs=0.01, sleep=time.sleep)
    t = ctl.watch(proc)
    time.sleep(0.1)
    assert proc.terminated == 0  # nothing happens without a request
    mod.post("/api/control", {"action": "stop_now"})
    time.sleep(0.15)
    assert proc.terminated == 0, "a running tool call must be allowed to finish"
    mod.action_running = False
    t.join(2)
    assert not t.is_alive()
    assert proc.terminated == 1 and proc.killed == 0


def test_watcher_ends_when_the_session_ends_by_itself():
    mod = FakeMod()
    proc = FakeProc()
    ctl = make(mod, poll_secs=0.01, sleep=time.sleep)
    t = ctl.watch(proc)
    proc.returncode = 0
    t.join(2)
    assert not t.is_alive() and proc.terminated == 0


def test_stop_after_day_does_not_touch_the_session():
    mod = FakeMod()
    proc = FakeProc()
    ctl = make(mod, poll_secs=0.01, sleep=time.sleep)
    t = ctl.watch(proc)
    mod.post("/api/control", {"action": "stop_after_day"})
    time.sleep(0.15)
    assert proc.terminated == 0
    proc.returncode = 0
    t.join(2)


def test_pause_does_not_touch_the_session():
    mod = FakeMod()
    proc = FakeProc()
    ctl = make(mod, poll_secs=0.01, sleep=time.sleep)
    t = ctl.watch(proc)
    mod.post("/api/control", {"action": "pause"})
    time.sleep(0.15)
    assert proc.terminated == 0
    proc.returncode = 0
    t.join(2)


def test_terminate_kills_a_session_that_ignores_terminate():
    class Stubborn(FakeProc):
        def terminate(self):
            self.terminated += 1

    proc = Stubborn()
    make(FakeMod(), kill_grace_secs=0.2).terminate(proc)
    assert proc.terminated == 1 and proc.killed == 1


def test_ctrl_c_escalates_stop_after_day_then_stop_now_then_abort():
    mod = FakeMod()
    proc = FakeProc()
    ctl = make(mod)
    ctl._proc = proc
    ctl._on_sigint()
    assert ctl._stop_after_day and not ctl._stop_now and proc.terminated == 0
    ctl._on_sigint()
    assert ctl._stop_now and proc.terminated == 0
    with pytest.raises(KeyboardInterrupt):
        ctl._on_sigint()
    assert proc.terminated == 1


def test_ctrl_c_stop_now_reaches_the_mod_through_the_watcher_and_ends_the_session():
    mod = FakeMod()
    mod.action_running = True
    proc = FakeProc()
    ctl = make(mod, poll_secs=0.01, sleep=time.sleep)
    t = ctl.watch(proc)
    ctl._on_sigint()
    ctl._on_sigint()
    time.sleep(0.15)
    assert mod.stop_after_day and mod.stop_now  # delivered, so the mod refuses new calls
    assert proc.terminated == 0
    mod.action_running = False
    t.join(2)
    assert proc.terminated == 1


def test_stop_now_with_the_game_gone_ends_the_session_at_once():
    mod = FakeMod()
    mod.up = False
    proc = FakeProc()
    ctl = make(mod, poll_secs=0.01, sleep=time.sleep)
    ctl._on_sigint()
    ctl._on_sigint()
    t = ctl.watch(proc)
    t.join(2)
    assert proc.terminated == 1


def test_start_installs_and_the_handler_is_ours():
    old = signal.getsignal(signal.SIGINT)
    try:
        ctl = RunControl(FakeMod().get, FakeMod().post, say=lambda *a: None, handle_sigint=True)
        ctl.start()
        assert signal.getsignal(signal.SIGINT) == ctl._on_sigint
    finally:
        signal.signal(signal.SIGINT, old)


def test_popen_kwargs_put_claude_in_its_own_process_group():
    assert RunControl.popen_kwargs(windows=False) == {"start_new_session": True}
    assert RunControl.popen_kwargs(windows=True)["creationflags"] == 0x200


def test_cli_posts_the_action(monkeypatch, capsys):
    sent = []
    monkeypatch.setattr(run_control, "_http_post", lambda path, body: sent.append((path, body)) or {"ok": True})
    assert run_control._cli(["pause"]) == 0
    assert sent == [("/api/control", {"action": "pause"})]
    assert json.loads(capsys.readouterr().out) == {"ok": True}


def test_cli_reports_a_closed_game(monkeypatch, capsys):
    def boom(path, timeout=5):
        raise OSError("refused")

    monkeypatch.setattr(run_control, "_http_get", boom)
    assert run_control._cli(["status"]) == 1
    assert "does not answer" in capsys.readouterr().err
