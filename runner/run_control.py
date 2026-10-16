"""Run control for runner/run_days.py: pause, stop after the day, stop now.

The mod keeps the flags (POST /api/control, or F8 in the game); this module is the runner's side of it.

  pause            the running tool call finishes, then every new AI call waits with the game frozen
  resume           lift the pause
  stop_after_day   finish today (go_to_bed), back up, then exit before the next day starts
  stop_now         let the running tool call finish, then end the `claude` session and exit; the game
                   stays frozen until `resume`
  clear_stop       forget both stop requests (run_days.py does this when a batch starts)

From a second terminal (the runner and the game keep going):

    py runner/run_control.py pause
    py runner/run_control.py resume
    py runner/run_control.py stop_after_day
    py runner/run_control.py stop_now
    py runner/run_control.py status

Ctrl+C in the runner's own terminal no longer kills `claude` mid-action. The first press means
stop_after_day, the second stop_now, the third aborts at once (and ends the `claude` process).
The `claude` process is started in its own process group, so the console's Ctrl+C does not reach it.
On Windows, Python sees Ctrl+C only when `claude` next prints a line (it can be quiet during a long tool
call), so for an immediate request use the commands above from a second terminal.

A stop request is kept in this process too, so it survives a game restart (the mod forgets its flags).
Standard library only.
"""

from __future__ import annotations

import argparse
import json
import signal
import subprocess
import sys
import threading
import time
import urllib.request
from typing import Callable

API = "http://localhost:8770"
ACTIONS = ("pause", "resume", "stop_after_day", "stop_now", "clear_stop")


def _http_get(path: str, timeout: float = 5):
    with urllib.request.urlopen(API + path, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def _http_post(path: str, body: dict, timeout: float = 5):
    req = urllib.request.Request(API + path, data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


class RunControl:
    """The runner's view of the mod's run control. `api_get` / `api_post` are run_days.py's helpers."""

    def __init__(self, api_get: Callable = _http_get, api_post: Callable = _http_post, say: Callable = print,
                 poll_secs: float = 2.0, sleep: Callable = time.sleep, handle_sigint: bool = True,
                 kill_grace_secs: float = 10.0):
        self._get = api_get
        self._post = api_post
        self._say = say
        self.poll_secs = poll_secs
        self._sleep = sleep
        self._handle_sigint = handle_sigint
        self._kill_grace = kill_grace_secs
        self._lock = threading.Lock()
        self._stop_after_day = False   # sticky: set once seen, so a game restart cannot lose it
        self._stop_now = False
        self._unsent: set[str] = set()  # local requests (Ctrl+C) not yet delivered to the mod
        self._sigints = 0
        self._proc = None

    # ------------------------------------------------------------------ the mod

    def mod_state(self) -> dict | None:
        """GET /api/control, or None when the game does not answer. Seen stop flags become sticky."""
        try:
            st = self._get("/api/control")
        except Exception:
            return None
        if not isinstance(st, dict):
            return None
        with self._lock:
            self._stop_after_day = self._stop_after_day or bool(st.get("stopAfterDay"))
            self._stop_now = self._stop_now or bool(st.get("stopNow"))
        return st

    def request(self, action: str) -> bool:
        """Send an action to the mod; stop requests also stick locally. True when the mod took it."""
        if action not in ACTIONS:
            raise ValueError(f"unknown action {action!r}")
        with self._lock:
            if action == "stop_after_day":
                self._stop_after_day = True
            elif action == "stop_now":
                self._stop_now = True
            elif action == "clear_stop":
                self._stop_after_day = self._stop_now = False
                self._unsent.discard("stop_after_day")
                self._unsent.discard("stop_now")
        try:
            r = self._post("/api/control", {"action": action})
        except Exception:
            if action in ("stop_after_day", "stop_now"):
                with self._lock:
                    self._unsent.add(action)
            return False
        with self._lock:
            self._unsent.discard(action)
        return bool(isinstance(r, dict) and r.get("ok", True))

    def _flush_unsent(self) -> None:
        """Deliver stop requests that were only flagged locally (Ctrl+C, or the game was unreachable)."""
        with self._lock:
            todo = sorted(self._unsent)
        for action in todo:
            self.request(action)

    # ------------------------------------------------------------------ what run_days.py asks

    def start(self) -> None:
        """Call once when a batch starts: forget stale stop requests and take over Ctrl+C."""
        self.request("clear_stop")
        if self._handle_sigint:
            signal.signal(signal.SIGINT, self._on_sigint)
            self._say("Run control: Ctrl+C once = stop after this day, twice = stop after the current tool call, "
                      "three times = abort. From another terminal: py runner/run_control.py pause|resume|stop_after_day|stop_now")

    def stop_after_day_requested(self) -> bool:
        self.mod_state()
        return self._stop_after_day

    def stop_now_requested(self) -> bool:
        self.mod_state()
        return self._stop_now

    def stop_between_days(self) -> bool:
        """True when the batch must not start another day."""
        self.mod_state()
        return self._stop_after_day or self._stop_now

    def wait_while_paused(self) -> None:
        """Block while the game is paused (no new `claude` session should start meanwhile)."""
        announced = False
        while True:
            st = self.mod_state()
            if st is None or not st.get("paused") or self._stop_now:
                return
            if not announced:
                self._say("Paused. Press F8 in the game or run `py runner/run_control.py resume`; waiting...")
                announced = True
            self._sleep(self.poll_secs)

    # ------------------------------------------------------------------ the claude session

    @staticmethod
    def popen_kwargs(windows: bool | None = None) -> dict:
        """Start `claude` in its own process group so the console's Ctrl+C does not kill it mid-action."""
        if windows is None:
            windows = sys.platform == "win32"
        if windows:
            return {"creationflags": getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0x00000200)}
        return {"start_new_session": True}

    def watch(self, proc) -> threading.Thread:
        """Watch one `claude` process: on stop_now, end it as soon as the running tool call has finished."""
        self._proc = proc
        t = threading.Thread(target=self._watch, args=(proc,), name="run-control", daemon=True)
        t.start()
        return t

    def _watch(self, proc) -> None:
        while proc.poll() is None:
            self._flush_unsent()
            st = self.mod_state()
            if self._stop_now and (st is None or not st.get("actionRunning")):
                # The call that was running has returned (or the game is gone): nothing is cut mid-action.
                self._say("  stop_now: the current tool call finished, ending the claude session")
                self.terminate(proc)
                return
            self._sleep(self.poll_secs)

    def terminate(self, proc) -> None:
        if proc.poll() is not None:
            return
        proc.terminate()
        deadline = time.monotonic() + self._kill_grace
        while proc.poll() is None and time.monotonic() < deadline:
            time.sleep(0.1)
        if proc.poll() is None:
            proc.kill()

    def _on_sigint(self, signum=None, frame=None) -> None:
        """Ctrl+C. Only sets flags (no network in a signal handler); the watcher thread tells the mod."""
        self._sigints += 1
        if self._sigints == 1:
            with self._lock:
                self._stop_after_day = True
                self._unsent.add("stop_after_day")
            self._say("\nCtrl+C: finishing this day, then stopping. Press again to stop after the current tool call.")
        elif self._sigints == 2:
            with self._lock:
                self._stop_now = True
                self._unsent.add("stop_now")
            self._say("\nCtrl+C again: stopping after the current tool call. Press once more to abort now.")
        else:
            if self._proc is not None:
                self.terminate(self._proc)
            raise KeyboardInterrupt


def _cli(argv: list[str] | None = None) -> int:
    global API
    ap = argparse.ArgumentParser(description="Pause, resume or stop a run_days.py batch through the mod's /api/control.")
    ap.add_argument("action", choices=list(ACTIONS) + ["status"])
    ap.add_argument("--api", default=API, help="Base URL of the mod (default %(default)s)")
    args = ap.parse_args(argv)
    API = args.api.rstrip("/")
    try:
        if args.action == "status":
            print(json.dumps(_http_get("/api/control"), indent=2))
        else:
            print(json.dumps(_http_post("/api/control", {"action": args.action}), indent=2))
    except Exception as e:
        print(f"The game does not answer on {API}: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(_cli())
