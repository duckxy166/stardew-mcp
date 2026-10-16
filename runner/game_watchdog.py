"""Crash / hang watchdog for unattended runs (run_days.py): detect, relaunch, replay the day.

Stardew only saves overnight, so after a crash the only recovery is to start the game again, let it
auto-load the run's save and replay the day from the morning. This module does that without telling
the AI anything went wrong.

Three signals (a poll thread watches them while a day is being played):

  process_gone  the SMAPI process is not running (2 polls in a row);
  no_answer     GET /api/state fails for more than --no-answer-secs (120 s), or it answers but the
                snapshot's "heartbeat" has stopped counting for that long (the HTTP thread outlives a
                blocked game loop and keeps serving a stale snapshot, so the heartbeat is what shows
                the hang);
  stalled       an action is running (frozen == false) but the clock ("day" + "time") has not moved for
                more than --stall-secs (180 s). Thinking time (frozen == true) never counts. Cutscenes,
                festivals and the overnight menus, where the clock legitimately stands still, get a longer
                allowance (900 s).

Recovery (max --crash-retries, default 2, per in-game day):

  1. kill the AI session (the `claude` process) and the game;
  2. move this attempt's files out of the way: day_XXX.jsonl -> day_XXX_attemptN.jsonl,
     claude_day_XXX_partM.jsonl -> claude_day_XXX_attemptN_partM.jsonl, and the day's diaries/journals
     -> diary_XXX_attemptN.md (so nothing written in a crashed attempt reaches the AI's memory);
  3. write AutoLoadSave into the mod's config.json, start the game with --game-cmd, wait until the world is
     ready and check that /api/state reports the expected saveFolder;
  4. play the day again with a fresh part-1 session. If the relaunched game is already on the next day
     (it crashed after the night save), the day counts as done and its files are put back.

The third crash of one day stops the batch (it is probably a mod bug). Everything that touches the
outside world (HTTP, processes, clock, files) is injectable, see tests/test_game_watchdog.py.

Standard library only. Run `py runner/game_watchdog.py --relaunch-test --game-cmd ...` to check the
kill / relaunch / AutoLoadSave cycle by hand, without any AI.
"""

from __future__ import annotations

import argparse
import atexit
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import threading
import time
import urllib.request
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional

DEFAULT_API = "http://localhost:8770"
IS_WINDOWS = os.name == "nt"
NIGHT_MENUS = ("ShippingMenu", "SaveGameMenu", "LevelUpMenu")
AUTOLOAD_KEY = "AutoLoadSave"


def default_process_name() -> str:
    return "StardewModdingAPI.exe" if IS_WINDOWS else "StardewModdingAPI"


def game_data_dir() -> Path:
    if IS_WINDOWS:
        return Path(os.environ.get("APPDATA", str(Path.home()))) / "StardewValley"
    return Path.home() / ".config" / "StardewValley"


def default_log(msg: str) -> None:
    print(f"[{datetime.now():%H:%M:%S}] watchdog: {msg}", flush=True)


# ---------------------------------------------------------------------- errors


class GameFault(Exception):
    """The game crashed or hung. `signal` is process_gone, no_answer, stalled, not_ready or launch_failed."""

    def __init__(self, signal: str, detail: str = ""):
        super().__init__(f"{signal}: {detail}" if detail else signal)
        self.signal = signal
        self.detail = detail


class BatchStopped(SystemExit):
    """Recovery is not possible or the retry budget is spent: end the batch with this message."""


# ---------------------------------------------------------------------- settings


@dataclass
class WatchdogConfig:
    api: str = DEFAULT_API
    game_cmd: str = ""               # command line that starts SMAPI; empty = detect only, never relaunch
    game_process: str = ""           # process name of SMAPI; empty = no "process gone" signal
    mod_config: str = ""             # Mods/StardewMCP/config.json (default: next to the exe in --game-cmd)
    save_folder: str = ""            # AutoLoadSave value; empty = the save that is loaded at the start
    saves_dir: str = ""              # Saves folder, to check the name early (default: the game's own)
    smapi_log: str = ""              # SMAPI-latest.txt, copied next to the crashed attempt's logs
    max_retries: int = 2             # replays per day; the next crash stops the batch
    poll_secs: float = 5.0
    no_answer_secs: float = 120.0
    stall_secs: float = 180.0
    slow_stall_secs: float = 900.0   # cutscene, festival or overnight menus open
    max_poll_gap_secs: float = 60.0  # a longer pause between polls (PC asleep) restarts all timers
    gone_confirmations: int = 2
    launch_timeout_secs: float = 600.0
    launch_grace_secs: float = 45.0  # the process must be visible this long after launch
    kill_wait_secs: float = 30.0
    settle_secs: float = 5.0         # pause after killing the game, so files and the port are free


# ---------------------------------------------------------------------- detection


def running_action(state: dict) -> Optional[str]:
    """The tool an AI seat is running right now, or None."""
    if state.get("action"):
        return str(state["action"])
    for p in state.get("players") or []:
        if isinstance(p, dict) and p.get("action"):
            return f"{p.get('id', '?')}:{p['action']}"
    return None


def clock_may_stand_still(state: dict) -> bool:
    """Cutscene, festival or an overnight menu: the clock legitimately stops, so give it more time."""
    if state.get("eventUp") or state.get("festival"):
        return True
    for p in state.get("players") or []:
        menu = ((p or {}).get("state") or {}).get("menu") if isinstance(p, dict) else None
        if menu in NIGHT_MENUS:
            return True
    return False


class Detector:
    """The three signals as a pure function of (time, process running?, /api/state or None)."""

    def __init__(self, cfg: WatchdogConfig, now: float):
        self.cfg = cfg
        self.reset(now)

    def reset(self, now: float) -> None:
        self.last_seen = now
        self.last_answer = now
        self.beat: object = None
        self.beat_changed = now
        self.gone = 0
        self.key: object = None
        self.key_changed = now

    def observe(self, now: float, process_running: Optional[bool], state: Optional[dict]) -> Optional[GameFault]:
        cfg = self.cfg

        # Signal 1: the process is gone. None = could not ask, which never counts.
        if process_running is False:
            self.gone += 1
            if self.gone >= cfg.gone_confirmations:
                return GameFault("process_gone", f"{cfg.game_process or 'the game'} is not running")
        else:
            self.gone = 0

        # Not watching for a while (PC asleep, runner busy): what we remember about the clock is meaningless.
        if now - self.last_seen > cfg.max_poll_gap_secs:
            self.last_answer = self.beat_changed = self.key_changed = now
        self.last_seen = now

        # Signal 2: no answer, or an answer that is not alive.
        if state is None:
            silent = now - self.last_answer
            if silent > cfg.no_answer_secs:
                return GameFault("no_answer", f"/api/state has not answered for {silent:.0f}s")
            return None
        self.last_answer = now
        beat = state.get("heartbeat")
        if beat is not None:
            if beat != self.beat:
                self.beat = beat
                self.beat_changed = now
            elif now - self.beat_changed > cfg.no_answer_secs:
                return GameFault("no_answer", f"the game loop stopped ticking (heartbeat stuck at {beat} for "
                                              f"{now - self.beat_changed:.0f}s)")

        # Signal 3: an action is running (the world is not frozen by thinking) but the clock does not move.
        action = running_action(state)
        active = bool(state.get("ready")) and state.get("frozen") is False and action is not None
        key = (state.get("day"), state.get("time"))
        if not active or key != self.key:
            self.key = key
            self.key_changed = now
            return None
        limit = cfg.slow_stall_secs if clock_may_stand_still(state) else cfg.stall_secs
        stuck = now - self.key_changed
        if stuck > limit:
            return GameFault("stalled", f"{action} has been running for {stuck:.0f}s but the clock stays at "
                                        f"day {key[0]} {key[1]}")
        return None


# ---------------------------------------------------------------------- processes


class Clock:
    def now(self) -> float:
        return time.monotonic()

    def sleep(self, secs: float) -> None:
        time.sleep(secs)


def parse_game_cmd(cmd: str, windows: bool = IS_WINDOWS) -> list[str]:
    """Split --game-cmd into argv. A plain path to an existing file is used as is (spaces need no quotes)."""
    cmd = (cmd or "").strip()
    if not cmd:
        return []
    if len(cmd) > 1 and cmd[0] == cmd[-1] == '"':
        cmd = cmd[1:-1]
        if Path(cmd).is_file():
            return [cmd]
    elif Path(cmd).is_file():
        return [cmd]
    parts = shlex.split(cmd, posix=not windows)
    if windows:
        parts = [p[1:-1] if len(p) >= 2 and p[0] == p[-1] == '"' else p for p in parts]
    return parts


def looks_like_path(arg: str) -> bool:
    return "://" not in arg and ("/" in arg or "\\" in arg)


class SystemProcesses:
    """The real game process: find it by name, kill it, start it. Windows (taskkill) and POSIX (pkill)."""

    def __init__(self, name: str, argv: Optional[list[str]] = None, cwd: Optional[str] = None,
                 env: Optional[dict] = None, run: Callable = subprocess.run,
                 popen: Callable = subprocess.Popen, windows: bool = IS_WINDOWS):
        self.name = name
        self.argv = argv or []
        self.cwd = cwd
        self.env = env
        self._run = run
        self._popen = popen
        self.windows = windows

    def _posix_name(self) -> str:
        return self.name[:15]  # pgrep/pkill match the kernel's 15-character process name

    def is_running(self) -> Optional[bool]:
        """True / False, or None when the question could not be asked (never treated as 'gone')."""
        if not self.name:
            return None
        try:
            if self.windows:
                r = self._run(["tasklist", "/FI", f"IMAGENAME eq {self.name}", "/FO", "CSV", "/NH"],
                              capture_output=True, text=True, timeout=20)
                if r.returncode != 0:
                    return None
                return f'"{self.name.lower()}"' in (r.stdout or "").lower()
            r = self._run(["pgrep", "-x", self._posix_name()], capture_output=True, text=True, timeout=20)
            return {0: True, 1: False}.get(r.returncode)
        except (OSError, subprocess.SubprocessError):
            return None

    def kill(self) -> None:
        if not self.name:
            return
        cmd = (["taskkill", "/F", "/T", "/IM", self.name] if self.windows
               else ["pkill", "-9", "-x", self._posix_name()])
        try:
            self._run(cmd, capture_output=True, text=True, timeout=30)
        except (OSError, subprocess.SubprocessError):
            pass

    def launch(self) -> None:
        if not self.argv:
            raise OSError("no --game-cmd")
        if self.windows:
            # Own console window and process group, so the game outlives this terminal and Ctrl+C here.
            flags = 0x00000010 | 0x00000200  # CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP
            self._popen(self.argv, cwd=self.cwd, env=self.env, creationflags=flags, close_fds=True)
        else:
            self._popen(self.argv, cwd=self.cwd, env=self.env, stdin=subprocess.DEVNULL,
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True,
                        close_fds=True)


def kill_tree(proc, run: Callable = subprocess.run, windows: bool = IS_WINDOWS) -> None:
    """Stop a subprocess and its children (the `claude` CLI may start helpers). Safe on None / exited."""
    if proc is None or proc.poll() is not None:
        return
    try:
        cmd = (["taskkill", "/PID", str(proc.pid), "/T", "/F"] if windows
               else ["pkill", "-9", "-P", str(proc.pid)])
        run(cmd, capture_output=True, text=True, timeout=20)
    except (OSError, subprocess.SubprocessError):
        pass
    try:
        proc.kill()
    except OSError:
        pass


# ---------------------------------------------------------------------- files of a crashed attempt

# Files the game writes per in-game day (Agent/AgentLog.cs): the log, and the diaries of every seat. A journal
# file with the same shape is handled too. Names that already carry "_attemptN" do not match.
_DAY_FILE = re.compile(r"^(?P<stem>(?:day|diary|journal)_(?P<day>\d+)(?:_p\d+)?)(?P<ext>\.[A-Za-z0-9]+)$")
# The runner's own transcript of one AI session (run_days.py).
_TRANSCRIPT = re.compile(r"^claude_day_(?P<day>\d+)_part(?P<part>\d+)(?P<ext>\.jsonl)$")


@dataclass
class Moved:
    original: Path
    moved: Path
    day: int


def quarantine(log_dir: Path, from_day: int, attempt: int, stamp: Optional[str] = None) -> list[Moved]:
    """Rename every per-day file of day >= from_day to carry `_attemptN`; returns what moved.

    day_005.jsonl -> day_005_attempt1.jsonl, diary_005_p2.md -> diary_005_p2_attempt1.md,
    claude_day_005_part1.jsonl -> claude_day_005_attempt1_part1.jsonl.
    """
    log_dir = Path(log_dir)
    if not log_dir.is_dir():
        return []
    plan: list[tuple[Path, int, Callable[[str], str]]] = []
    for f in sorted(log_dir.iterdir()):
        if not f.is_file():
            continue
        m = _DAY_FILE.match(f.name)
        if m and int(m["day"]) >= from_day:
            plan.append((f, int(m["day"]), lambda tag, m=m: f"{m['stem']}_{tag}{m['ext']}"))
            continue
        t = _TRANSCRIPT.match(f.name)
        if t and int(t["day"]) >= from_day:
            plan.append((f, int(t["day"]), lambda tag, t=t, f=f:
                         f"claude_day_{t['day']}_{tag}_part{t['part']}{t['ext']}"))
    if not plan:
        return []

    # A name can be taken by an earlier batch that replayed the same day; never overwrite its files.
    stamp = stamp or datetime.now().strftime("%H%M%S")
    tags = [f"attempt{attempt}", f"attempt{attempt}_{stamp}"] + [f"attempt{attempt}_{stamp}_{i}" for i in range(2, 50)]
    tag = next((t for t in tags if not any((log_dir / name(t)).exists() for _, _, name in plan)), tags[-1])
    moved = []
    for f, day, name in plan:
        dst = log_dir / name(tag)
        try:
            f.rename(dst)
        except OSError:
            try:  # e.g. a viewer holds the file open on Windows: copy it, then try to empty the original
                shutil.copy2(f, dst)
                f.unlink()
            except OSError as e:
                default_log(f"could not move {f.name} out of the way: {e}")
                continue
        moved.append(Moved(f, dst, day))
    return moved


def restore(moved: list[Moved], below_day: int) -> list[Moved]:
    """Put back the files of days that turned out to be finished (< below_day)."""
    back = []
    for m in moved:
        if m.day < below_day and m.moved.exists() and not m.original.exists():
            try:
                m.moved.rename(m.original)
                back.append(m)
            except OSError as e:
                default_log(f"could not restore {m.original.name}: {e}")
    return back


class EventLog:
    """Append-only JSONL of what the watchdog did (faults, relaunches, moved files)."""

    def __init__(self, path: Path):
        self.path = Path(path)

    def write(self, event: str, **fields) -> None:
        rec = {"ts": datetime.now().isoformat(timespec="seconds"), "event": event, **fields}
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with self.path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")
        except OSError as e:
            default_log(f"could not write {self.path.name}: {e}")


# ---------------------------------------------------------------------- the mod's config.json


class ModConfigEditor:
    """Sets AutoLoadSave in the mod's config.json before a relaunch and puts the old value back at the end.

    Safe by construction: plain JSON only (anything else is refused, never rewritten), every other key is
    kept, the file is replaced atomically, nothing is written when the value already matches, and only
    this one key is restored (so edits you make by hand in between survive).
    """

    _UNSET = object()

    def __init__(self, path: Path):
        self.path = Path(path)
        self._original: object = self._UNSET

    def _load(self) -> dict:
        try:
            data = json.loads(self.path.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError) as e:
            raise ValueError(f"cannot read {self.path} as plain JSON ({e}); set \"{AUTOLOAD_KEY}\" in it yourself") from e
        if not isinstance(data, dict):
            raise ValueError(f"{self.path} is not a JSON object")
        return data

    def _key(self, data: dict) -> str:
        return next((k for k in data if k.lower() == AUTOLOAD_KEY.lower()), AUTOLOAD_KEY)

    def _write(self, data: dict) -> None:
        tmp = self.path.with_name(self.path.name + ".tmp")
        tmp.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        os.replace(tmp, self.path)

    def check(self) -> None:
        self._load()

    def current(self) -> str:
        data = self._load()
        return str(data.get(self._key(data)) or "")

    def set(self, value: str) -> bool:
        """Make AutoLoadSave `value`. Returns True when the file changed."""
        data = self._load()
        key = self._key(data)
        if self._original is self._UNSET:
            self._original = data.get(key, "")
        if data.get(key) == value:
            return False
        data[key] = value
        self._write(data)
        return True

    def restore(self) -> bool:
        if self._original is self._UNSET:
            return False
        data = self._load()
        key = self._key(data)
        original, self._original = self._original, self._UNSET
        if data.get(key) == original:
            return False
        data[key] = original
        self._write(data)
        return True


# ---------------------------------------------------------------------- the watchdog


class NullWatchdog:
    """Used with --no-watchdog: every hook does nothing."""

    enabled = False
    armed = False

    def ready(self, wait_fn: Callable[[], dict]) -> dict:
        return wait_fn()

    def run_day(self, day: int, play: Callable[[int], dict]) -> dict:
        return play(1)

    def check(self) -> None:
        pass

    def explain_failure(self) -> None:
        pass

    def attach(self, proc) -> None:
        pass

    def detach(self) -> None:
        pass

    def close(self) -> None:
        pass


class Watchdog:
    enabled = True

    def __init__(self, cfg: WatchdogConfig, api_get: Callable[[str], dict], procs, log_dir,
                 clock: Optional[Clock] = None, say: Callable[[str], None] = default_log,
                 kill_session: Callable = kill_tree, editor: Optional[ModConfigEditor] = None,
                 saves_dir: Optional[Path] = None, smapi_log: Optional[Path] = None, threaded: bool = True):
        self.cfg = cfg
        self.api_get = api_get
        self.procs = procs
        self.clock = clock or Clock()
        self.say = say
        self.kill_session = kill_session
        self.editor = editor
        self.saves_dir = Path(saves_dir) if saves_dir else None
        self.smapi_log = Path(smapi_log) if smapi_log else None
        self.threaded = threaded
        self.log_dir = Path(log_dir)
        self.events = EventLog(self.log_dir / "watchdog.jsonl")

        self.detector = Detector(cfg, self.clock.now())
        self.fault: Optional[GameFault] = None
        self.armed = False
        self.expected_save = cfg.save_folder
        self.open_day: Optional[int] = None           # first day whose files are not final yet
        self.crashes: dict[int, int] = {}              # failed tries per day
        self.history: dict[int, list[tuple[int, str, str]]] = {}
        self._session = None
        self._lock = threading.Lock()
        self._thread: Optional[threading.Thread] = None
        self._stop = threading.Event()
        self._started = False

    # -- setup ------------------------------------------------------------------------------

    @property
    def can_relaunch(self) -> bool:
        return bool(self.cfg.game_cmd and self.editor)

    def _validate_setup(self) -> None:
        if not self.can_relaunch:
            self.say("detect-only: no --game-cmd, so a crash or hang stops the batch with a message instead of a relaunch")
            return
        if not self.expected_save:
            raise BatchStopped("watchdog: cannot tell which save to auto-load. Load your save in the game before "
                               "starting the runner, or pass --save-folder with its folder name from the Saves directory.")
        saves = self.saves_dir
        if saves is not None and saves.is_dir() and not (saves / self.expected_save).is_dir():
            raise BatchStopped(f"watchdog: there is no save folder '{self.expected_save}' in {saves}. Pass the exact "
                               "folder name with --save-folder (and --saves-dir if your saves are somewhere else).")
        self.say(f"on: after a crash or hang I relaunch the game with AutoLoadSave='{self.expected_save}' and replay the day "
                 f"(max {self.cfg.max_retries} retries per day)")

    def _learn(self, state: dict) -> None:
        """Remember the day and, once, which save is the run's."""
        day = state.get("day")
        if isinstance(day, int) and day > 0:
            self.open_day = day
        save = str(state.get("saveFolder") or "")
        if not self.expected_save and save and not save.lower().startswith("smoke_"):
            self.expected_save = save

    def _note_ready(self, state: dict) -> None:
        self._learn(state)
        if not self._started:
            self._started = True
            self._validate_setup()
            self.start()
            self.arm()

    # -- polling ----------------------------------------------------------------------------

    def arm(self) -> None:
        with self._lock:
            self.fault = None
            self.detector.reset(self.clock.now())
            self.armed = True

    def disarm(self) -> None:
        with self._lock:
            self.armed = False

    def attach(self, proc) -> None:
        with self._lock:
            self._session = proc

    def detach(self) -> None:
        with self._lock:
            self._session = None

    def _fetch(self) -> Optional[dict]:
        try:
            state = self.api_get("/api/state")
        except Exception:
            return None
        return state if isinstance(state, dict) else None

    def poll_once(self) -> Optional[GameFault]:
        """One look at the game. On a fault: remember it and kill the AI session so the runner notices."""
        if not self.armed:
            return None
        state = self._fetch()
        running = self.procs.is_running()
        with self._lock:
            if not self.armed:
                return None
            fault = self.detector.observe(self.clock.now(), running, state)
            if fault is None:
                return None
            self.armed = False
            self.fault = fault
            session = self._session
        self.say(f"fault detected ({fault}); stopping the AI session")
        self.kill_session(session)
        return fault

    def _loop(self) -> None:
        while not self._stop.wait(self.cfg.poll_secs):
            try:
                self.poll_once()
            except Exception as e:  # the watchdog must never die quietly
                self.say(f"poll error: {e!r}")

    def start(self) -> None:
        if self._thread is not None or not self.threaded:
            return
        self._thread = threading.Thread(target=self._loop, name="game-watchdog", daemon=True)
        self._thread.start()
        atexit.register(self.close)

    def close(self) -> None:
        self._stop.set()
        self.armed = False
        if self.editor is not None:
            try:
                if self.editor.restore():
                    self.say(f"restored {AUTOLOAD_KEY} in {self.editor.path.name}")
            except (OSError, ValueError) as e:
                self.say(f"could not restore {AUTOLOAD_KEY}: {e}")

    def check(self) -> None:
        """Raise GameFault if the poll thread has seen a crash or hang."""
        if self.fault is not None:
            raise self.fault

    def explain_failure(self) -> None:
        """The AI CLI failed before its first tool call. A dead game does that: recover instead of giving up."""
        self.check()
        if self.armed and self.procs.is_running() is False:
            raise GameFault("process_gone", f"{self.cfg.game_process or 'the game'} is not running")

    # -- the day loop -----------------------------------------------------------------------

    def ready(self, wait_fn: Callable[[], dict]) -> dict:
        """wait_fn() (the runner's wait_ready) that also recovers from a crash found while waiting."""
        while True:
            try:
                self.check()
                state = wait_fn()
                self.check()
            except GameFault as fault:
                self._recover(fault)
                continue
            self._note_ready(state)
            return state

    def run_day(self, day: int, play: Callable[[int], dict]) -> dict:
        """play(attempt) plays one in-game day and returns the game state after it. A crash or hang anywhere
        inside it raises GameFault; this method then recovers and calls play again (up to --crash-retries)."""
        self.open_day = day
        while True:
            attempt = self.crashes.get(day, 0) + 1
            try:
                self.arm()
                result = play(attempt)
                self.check()
            except GameFault as fault:
                state = self._recover(fault)
                loaded = int(state.get("day") or 0)
                if loaded > day:  # it crashed after the night save: the day is done
                    self.events.write("day_done_in_save", day=day, attempt=attempt, loaded_day=loaded)
                    self.open_day = loaded
                    return state
                if loaded < day:
                    raise BatchStopped(f"watchdog: after the relaunch the save is on day {loaded}, older than the day "
                                       f"being played ({day}). Check the save folder and the backups in runs/saves.")
                continue
            self.events.write("day_done", day=day, attempt=attempt)
            if isinstance(result, dict) and isinstance(result.get("day"), int):
                self.open_day = result["day"]
            else:
                self.open_day = day + 1
            return result

    # -- recovery ---------------------------------------------------------------------------

    def _kill_game(self) -> None:
        self.procs.kill()
        deadline = self.clock.now() + self.cfg.kill_wait_secs
        while self.procs.is_running():
            if self.clock.now() > deadline:
                raise BatchStopped(f"watchdog: {self.cfg.game_process} would not stop. Close it by hand and start the batch again.")
            self.clock.sleep(1)
        self.clock.sleep(self.cfg.settle_secs)

    def _archive(self, day: int, attempt: int) -> list[Moved]:
        moved = quarantine(self.log_dir, day, attempt)
        copied = ""
        if self.smapi_log is not None and self.smapi_log.is_file():
            dst = self.log_dir / f"smapi_day_{day:03}_attempt{attempt}.txt"
            try:
                shutil.copy2(self.smapi_log, dst)
                copied = dst.name
            except OSError:
                pass
        self.events.write("archived", day=day, attempt=attempt, files=[m.moved.name for m in moved], smapi_log=copied)
        if moved:
            self.say(f"day {day} attempt {attempt}: its logs and diaries are kept as *_attempt{attempt}.* and dropped from the run")
        return moved

    def _give_up_message(self, day: int, tries: int) -> str:
        lines = [f"watchdog: day {day} failed {tries} times (max {self.cfg.max_retries} retries per day). Stopping the batch."]
        for n, signal, detail in self.history.get(day, []):
            lines.append(f"  attempt {n}: {signal} - {detail}")
        lines.append("A replay that keeps crashing usually means a bug in the mod (or a wrong --game-cmd / save). The failed attempts' "
                     f"files are in {self.log_dir} as *_attemptN.* (SMAPI logs: smapi_day_{day:03}_attemptN.txt); "
                     f"{self.events.path} lists what happened.")
        return "\n".join(lines)

    def _recover(self, fault: GameFault) -> dict:
        """Handle a fault: stop the AI session and the game, set the attempt's files aside, relaunch, check the save.
        Returns the state of the relaunched game. Raises BatchStopped when the day's retries are used up."""
        if self.open_day is None:
            raise BatchStopped(f"watchdog: the game failed before any day started ({fault})")
        moved_all: list[Moved] = []
        while True:
            day = self.open_day
            self.disarm()
            with self._lock:
                session, self._session = self._session, None
            self.kill_session(session)
            tries = self.crashes[day] = self.crashes.get(day, 0) + 1
            self.history.setdefault(day, []).append((tries, fault.signal, fault.detail))
            self.say(f"day {day}, attempt {tries} failed: {fault}")
            self.events.write("fault", day=day, attempt=tries, signal=fault.signal, detail=fault.detail)

            if not self.can_relaunch:
                self._archive(day, tries)
                raise BatchStopped(
                    f"watchdog: the game crashed or hung on day {day} ({fault}) and no relaunch is configured, so I stop here. "
                    "Start the game and load the save, then run the batch again; it replays the day from the morning. "
                    f"This attempt's files were kept as *_attempt{tries}.* in {self.log_dir} (if the game had already saved "
                    "the next day, rename that day's diary back by hand). Pass --game-cmd to let the runner relaunch the game itself.")

            self._kill_game()
            moved_all += self._archive(day, tries)
            if tries > self.cfg.max_retries:
                raise BatchStopped(self._give_up_message(day, tries))

            try:
                state = self._relaunch()
            except GameFault as f:  # the relaunch itself failed: that is another failed try of this day
                fault = f
                continue

            loaded = int(state.get("day") or 0)
            if loaded < day:  # the night save did not happen: the day before has to be played again
                moved_all += self._archive(loaded, tries)
                self.open_day = loaded
            restore(moved_all, loaded)
            self.events.write("relaunched", day=day, attempt=tries, loaded_day=loaded, save=state.get("saveFolder"))
            self.say(f"game is back on day {loaded} ({state.get('saveFolder')}); "
                     + ("replaying the day from the morning" if loaded <= day else "the day had been saved already"))
            self.arm()
            return state

    def _relaunch(self) -> dict:
        assert self.editor is not None
        try:
            if self.editor.set(self.expected_save):
                self.say(f"set {AUTOLOAD_KEY}='{self.expected_save}' in {self.editor.path} (put back when the runner exits)")
        except (OSError, ValueError) as e:
            raise BatchStopped(f"watchdog: cannot write {AUTOLOAD_KEY} to {self.editor.path}: {e}")
        try:
            self.procs.launch()
        except OSError as e:
            raise GameFault("launch_failed", f"could not start the game: {e}")
        self.say("game started, waiting for the world")
        t0 = self.clock.now()
        while True:
            state = self._fetch()
            if state and state.get("ready"):
                break
            elapsed = self.clock.now() - t0
            if elapsed > self.cfg.launch_grace_secs and self.procs.is_running() is False:
                raise GameFault("launch_failed", f"{self.cfg.game_process} is not running {elapsed:.0f}s after the relaunch")
            if elapsed > self.cfg.launch_timeout_secs:
                raise GameFault("not_ready", f"no loaded world {elapsed:.0f}s after the relaunch "
                                             f"(is {AUTOLOAD_KEY} '{self.expected_save}' correct?)")
            self.clock.sleep(min(3.0, self.cfg.poll_secs))
        loaded = str(state.get("saveFolder") or "")
        if loaded.lower() != self.expected_save.lower():
            raise BatchStopped(f"watchdog: the relaunched game loaded '{loaded}' instead of '{self.expected_save}'. "
                               f"{AUTOLOAD_KEY} did not do what it should; not replaying on the wrong save.")
        return state

    def relaunch_test(self) -> str:
        """Kill the game, relaunch it, check the save auto-loads. For a manual check of the setup; returns a summary."""
        if not self.can_relaunch:
            raise BatchStopped("relaunch test needs --game-cmd")
        state = self._fetch()
        if not state or not state.get("ready"):
            raise BatchStopped("relaunch test: load your save in the game first (/api/state is not ready)")
        self._learn(state)
        self._validate_setup()
        self.say("killing the game and relaunching it ...")
        self._kill_game()
        back = self._relaunch()
        return (f"{AUTOLOAD_KEY} works: after the relaunch '{back.get('saveFolder')}' is loaded, farm day {back.get('day')}, "
                f"{back.get('time')}")


# ---------------------------------------------------------------------- command line


def add_args(ap: argparse.ArgumentParser) -> None:
    g = ap.add_argument_group("watchdog (crash and hang recovery)")
    g.add_argument("--no-watchdog", action="store_true", help="Do not watch the game (a crash then ends the run after 10 minutes)")
    g.add_argument("--game-cmd", default=os.environ.get("STARDEW_GAME_CMD", ""),
                   help="Command that starts SMAPI, e.g. the full path of StardewModdingAPI.exe (env STARDEW_GAME_CMD). "
                        "Without it a crash or hang stops the batch with a message instead of a relaunch.")
    g.add_argument("--game-process", default=default_process_name(), help="Process name of SMAPI; '' disables the process check")
    g.add_argument("--mod-config", default="", help="Mod config.json to put AutoLoadSave in (default: Mods/StardewMCP/config.json next to --game-cmd)")
    g.add_argument("--save-folder", default="", help="Save folder name to auto-load (default: the save loaded when the batch starts)")
    g.add_argument("--saves-dir", default="", help="Saves folder, used to check --save-folder early (default: the game's own)")
    g.add_argument("--crash-retries", type=int, default=2, help="Times a day is replayed after a crash or hang before the batch stops")
    g.add_argument("--no-answer-secs", type=float, default=120, help="/api/state silent or frozen this long = hang")
    g.add_argument("--stall-secs", type=float, default=180, help="An action runs but the game clock stands still this long = hang")
    g.add_argument("--launch-timeout", type=float, default=600, help="Seconds a relaunched game may take to load the save")


def from_args(args, log_dir, api_get: Callable[[str], dict], api: str = DEFAULT_API):
    """Build the watchdog from the parsed command line (NullWatchdog with --no-watchdog)."""
    if args.no_watchdog:
        return NullWatchdog()
    cfg = WatchdogConfig(api=api, game_cmd=args.game_cmd, game_process=args.game_process, mod_config=args.mod_config,
                         save_folder=args.save_folder, saves_dir=args.saves_dir, max_retries=args.crash_retries,
                         no_answer_secs=args.no_answer_secs, stall_secs=args.stall_secs,
                         launch_timeout_secs=args.launch_timeout)
    argv = parse_game_cmd(cfg.game_cmd)
    editor = None
    cwd = None
    if argv:
        exe = Path(argv[0])
        if looks_like_path(argv[0]):
            if not exe.is_file():
                raise SystemExit(f"watchdog: --game-cmd points to {exe}, which is not a file")
            cwd = str(exe.parent)
        mod_config = Path(cfg.mod_config) if cfg.mod_config else (
            exe.parent / "Mods" / "StardewMCP" / "config.json" if looks_like_path(argv[0]) else None)
        if mod_config is None:
            raise SystemExit("watchdog: pass --mod-config (the mod's config.json), it cannot be derived from this --game-cmd")
        if not mod_config.is_file():
            raise SystemExit(f"watchdog: {mod_config} not found; pass --mod-config with the path of the mod's config.json")
        editor = ModConfigEditor(mod_config)
        try:
            editor.check()
        except ValueError as e:
            raise SystemExit(f"watchdog: {e}")
    env = dict(os.environ)
    env.setdefault("SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS", "0")  # keep rendering while OBS has focus
    procs = SystemProcesses(cfg.game_process, argv, cwd=cwd, env=env)
    saves = Path(cfg.saves_dir) if cfg.saves_dir else game_data_dir() / "Saves"
    smapi_log = Path(cfg.smapi_log) if cfg.smapi_log else game_data_dir() / "ErrorLogs" / "SMAPI-latest.txt"
    return Watchdog(cfg, api_get, procs, log_dir, editor=editor, saves_dir=saves, smapi_log=smapi_log)


def http_get_json(base: str) -> Callable[[str], dict]:
    def get(path: str) -> dict:
        with urllib.request.urlopen(base + path, timeout=10) as r:
            return json.loads(r.read().decode("utf-8"))
    return get


def main(argv: Optional[list[str]] = None) -> int:
    ap = argparse.ArgumentParser(description="Check the watchdog's kill / relaunch / AutoLoadSave cycle without an AI.")
    add_args(ap)
    ap.add_argument("--relaunch-test", action="store_true", required=True,
                    help="Kill the game, start it again with --game-cmd and check that the save auto-loads")
    ap.add_argument("--out", default=str(Path(__file__).resolve().parent.parent / "runs"))
    args = ap.parse_args(argv)
    wd = from_args(args, Path(args.out) / "logs", http_get_json(DEFAULT_API))
    if isinstance(wd, NullWatchdog):
        raise SystemExit("--relaunch-test and --no-watchdog contradict each other")
    try:
        print(wd.relaunch_test())
        return 0
    except (BatchStopped, GameFault) as e:
        print(f"FAILED: {e}", file=sys.stderr)
        return 1
    finally:
        wd.close()


if __name__ == "__main__":
    sys.exit(main())
