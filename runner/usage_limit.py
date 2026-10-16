"""Subscription usage-limit handling for runner/run_days.py (issue #23).

The player AI runs on a Claude subscription, which has a 5-hour and a weekly usage limit. When the limit
is hit, the `claude -p` session ends with an error. The game world stays frozen while no action runs, so
the right reaction is to wait for the reset and then continue the SAME in-game day, without counting the
failed part against --max-parts.

This module has no game or network code, only:
  * LimitWatcher     feed it every stdout line of one `claude -p --output-format stream-json` run; it says
                     whether the run ended because of a usage limit and when the limit resets
  * parse_reset      tolerant reset-time parser (epoch, ISO time, "in 2 hours", "3am (Asia/Bangkok)", ...)
  * LimitWaiter      sleeps until the reset (clock and sleep are injectable) and posts a status to /api/meta
  * run_part         retry wrapper used by run_days.py: same day, same part number, until the part ran

IMPORTANT: the exact wording of the CLI's limit message could not be captured from a real run when this was
written. The detector therefore accepts several known/likely shapes (see runner/tests/fixtures/README.md,
which labels every sample as synthetic). Check it against a real transcript with:

    py runner/usage_limit.py runs/logs/claude_day_001_part1.jsonl

Standard library only.
"""

from __future__ import annotations

import json
import re
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone, tzinfo
from typing import Callable, Iterable

# Fallbacks and bounds
DEFAULT_RETRY_MINUTES = 30      # when the reset time cannot be parsed
DEFAULT_MARGIN_SECONDS = 60     # extra wait after the announced reset, so the window has really reopened
DEFAULT_MAX_WAITS = 350         # per part; 350 x 30 min is a little over the 7-day weekly window
CHUNK_SECONDS = 300             # long waits sleep in chunks (survives sleep/hibernate, refreshes the status)
MAX_AHEAD = timedelta(days=8)   # a parsed reset further away than this is not trusted

STATUS_KEY = "runnerStatus"     # /api/meta field shown by studio/overlay.html; "" clears it


# ---------------------------------------------------------------------------------------------------------
# clock (module level so tests can replace them)

def _now() -> datetime:
    return datetime.now().astimezone()


def _sleep(seconds: float) -> None:
    time.sleep(seconds)


# ---------------------------------------------------------------------------------------------------------
# message detection

# "Claude AI usage limit reached|1760000000": the old machine-readable form. Unmistakable, so it is accepted
# even when the event does not carry is_error.
_PIPE_EPOCH = re.compile(r"usage limit reached\s*\|\s*(\d{9,13})", re.I)

# Human-readable forms. For events and lines they are only trusted together with is_error (result events) or
# on non-JSON output lines, so a model that merely writes "limit" in a diary is never mistaken for a limit.
_LIMIT_TEXT = re.compile(
    r"usage limit (?:reached|exceeded|hit)"
    r"|(?:5|five)[- ]hour limit"
    r"|week(?:ly)? limit"
    r"|(?:session|daily|monthly|opus|sonnet|haiku|usage) limit (?:reached|hit|exceeded|will reset|resets?)"
    r"|limit reached\W{0,8}resets?"
    r"|you(?:'|’)?ve (?:hit|reached) your (?:[\w-]+ )?limit"
    r"|you have (?:hit|reached) your (?:[\w-]+ )?limit"
    r"|(?:hit|reached|exceeded) your (?:[\w-]+ )?usage limit"
    r"|your (?:usage )?limit (?:will )?resets?"
    r"|out of (?:extra )?usage",
    re.I,
)


def looks_like_limit(text: str) -> bool:
    return bool(text) and bool(_LIMIT_TEXT.search(text) or _PIPE_EPOCH.search(text))


@dataclass(frozen=True)
class LimitInfo:
    message: str                     # the text the limit was recognised from
    reset_at: datetime | None        # None when no reset time could be parsed
    source: str = "none"             # epoch | event | iso | relative | clock | none


class UsageLimitHit(Exception):
    """Raised by run_days.run_session when the session ended because of the usage limit."""

    def __init__(self, info: LimitInfo, tool_calls: int = 0):
        super().__init__(info.message)
        self.info = info
        self.tool_calls = tool_calls


def _event_texts(ev: dict) -> list[str]:
    """Every text field of a stream-json event that may carry an error message."""
    out: list[str] = []
    for key in ("result", "error", "message"):
        v = ev.get(key)
        if isinstance(v, str):
            out.append(v)
        elif isinstance(v, dict):
            for c in v.get("content") or []:
                if isinstance(c, dict) and isinstance(c.get("text"), str):
                    out.append(c["text"])
    errs = ev.get("errors")
    if isinstance(errs, list):
        out.extend(e for e in errs if isinstance(e, str))
    return out


class LimitWatcher:
    """Looks at the events of ONE session. Call feed() per parsed event and feed_line() per non-JSON line."""

    def __init__(self, now: Callable[[], datetime] | None = None):
        self._now = now or (lambda: _now())
        self.info: LimitInfo | None = None
        self._rejected = False                 # a rate_limit_event said the request was rejected
        self._event_reset: datetime | None = None
        self._hint_texts: list[str] = []       # error-flagged assistant texts that look like a limit

    def feed(self, ev: dict) -> LimitInfo | None:
        if self.info or not isinstance(ev, dict):
            return self.info
        kind = ev.get("type")
        if kind == "rate_limit_event":
            # Believed shape in newer CLIs: {"type":"rate_limit_event","rate_limit_info":{"status":"rejected",
            # "resetsAt":<epoch>,"rateLimitType":"five_hour"}}. Unconfirmed, used only as an extra hint.
            rli = ev.get("rate_limit_info") or ev.get("rateLimitInfo") or {}
            if isinstance(rli, dict):
                status = str(rli.get("status", "")).lower()
                if status and status not in ("allowed", "allowed_warning", "ok", "warning"):
                    self._rejected = True
                    self._event_reset = _hint_to_dt(rli.get("resetsAt", rli.get("resets_at")), self._now())
        elif kind == "assistant" and ev.get("error"):
            self._hint_texts.extend(t for t in _event_texts(ev) if looks_like_limit(t))
        elif kind == "result":
            texts = _event_texts(ev)
            is_error = bool(ev.get("is_error")) or str(ev.get("subtype", "")).startswith("error")
            strong = [t for t in texts if _PIPE_EPOCH.search(t)]
            weak = [t for t in texts if looks_like_limit(t)] or self._hint_texts
            if strong:
                self._hit(strong[0], strong + texts)
            elif is_error and (weak or self._rejected):
                self._hit((weak or texts or ["usage limit (rate_limit_event)"])[0], weak + self._hint_texts + texts)
        return self.info

    def feed_line(self, line: str) -> LimitInfo | None:
        """A line of CLI output that is not JSON (older versions print the limit as plain text)."""
        if not self.info and looks_like_limit(line):
            self._hit(line.strip(), [line])
        return self.info

    def _hit(self, message: str, texts: Iterable[str]) -> None:
        now = self._now()
        reset, source = self._event_reset, "event"
        if reset is None:
            reset, source = None, "none"
            for t in texts:
                reset, source = parse_reset(t, now)
                if reset:
                    break
        self.info = LimitInfo(message=message.strip()[:300], reset_at=reset, source=source)


def detect_usage_limit(ev: dict, now: Callable[[], datetime] | None = None) -> LimitInfo | None:
    """Stateless convenience: does this single event end a session because of the usage limit?"""
    return LimitWatcher(now).feed(ev)


# ---------------------------------------------------------------------------------------------------------
# reset-time parsing

_MONTHS = {m: i for i, m in enumerate(("jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"), 1)}
_WEEKDAYS = {"mon": 0, "tue": 1, "wed": 2, "thu": 3, "fri": 4, "sat": 5, "sun": 6}

_RESET_WORD = re.compile(r"\b(?:resets?|until|available again|try again|retry)\b", re.I)
_RESET_EPOCH = re.compile(r"reset\w*\D{0,15}(\d{10,13})\b", re.I)
_ISO = re.compile(r"\b(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?\s*(Z|[+-]\d{2}:?\d{2})?", re.I)
_REL = re.compile(
    r"\b(?:in|after)\s+((?:\d+(?:\.\d+)?\s*(?:days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b[\s,]*(?:and\s+)?)+)", re.I)
_REL_PART = re.compile(r"(\d+(?:\.\d+)?)\s*(days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b", re.I)
_TIME_AMPM = re.compile(r"\b(1[0-2]|0?[1-9])(?::([0-5]\d))?\s*([ap])\.?m\b", re.I)
_TIME_24 = re.compile(r"\b([01]?\d|2[0-3]):([0-5]\d)(?::[0-5]\d)?\b(?!\s*[ap]\.?m)", re.I)
_DATE_MD = re.compile(r"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+(\d{1,2})(?:st|nd|rd|th)?\b", re.I)
_DATE_DM = re.compile(r"\b(\d{1,2})(?:st|nd|rd|th)?\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\b", re.I)
_WEEKDAY = re.compile(r"\b(mon|tue|wed|thu|fri|sat|sun)(?:day|sday|nesday|rsday|urday)?\b", re.I)
_TZ_NAME = re.compile(r"\(\s*([A-Za-z]+(?:/[A-Za-z_+\-0-9]+)+|UTC|GMT|Z)\s*\)")


def _epoch_to_dt(value: float, tz: tzinfo | None) -> datetime | None:
    if value > 1e12:       # milliseconds
        value /= 1000.0
    if not 1e9 <= value <= 4e9:
        return None
    return datetime.fromtimestamp(value, tz) if tz else datetime.fromtimestamp(value).astimezone()


def _hint_to_dt(value, now: datetime) -> datetime | None:
    """A machine-readable reset time: epoch number/string or an ISO string."""
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, (int, float)):
        return _epoch_to_dt(float(value), now.tzinfo)
    if isinstance(value, str):
        v = value.strip()
        if v.replace(".", "", 1).isdigit():
            return _epoch_to_dt(float(v), now.tzinfo)
        got, _ = parse_reset(v, now)
        return got
    return None


def _find_tz(text: str, default: tzinfo | None) -> tzinfo | None:
    m = _TZ_NAME.search(text)
    if not m:
        return default
    name = m.group(1)
    if name.upper() in ("UTC", "GMT", "Z"):
        return timezone.utc
    try:
        from zoneinfo import ZoneInfo
        return ZoneInfo(name)
    except Exception:       # unknown name, or no tz database (Windows without the tzdata package)
        return default


def _iso_candidate(text: str, now: datetime) -> datetime | None:
    m = _ISO.search(text)
    if not m:
        return None
    y, mo, d, h, mi = (int(m.group(i)) for i in range(1, 6))
    s = int(m.group(6) or 0)
    z = m.group(7)
    if z:
        if z.upper() == "Z":
            tz: tzinfo | None = timezone.utc
        else:
            sign = -1 if z[0] == "-" else 1
            digits = z[1:].replace(":", "")
            tz = timezone(sign * timedelta(hours=int(digits[:2]), minutes=int(digits[2:])))
    else:
        tz = _find_tz(text, now.tzinfo)
    try:
        return datetime(y, mo, d, h, mi, s, tzinfo=tz)
    except ValueError:
        return None


def _relative_candidate(text: str, now: datetime) -> datetime | None:
    m = _REL.search(text)
    if not m:
        return None
    total = timedelta()
    for num, unit in _REL_PART.findall(m.group(1)):
        n = float(num)
        u = unit[0].lower()
        total += timedelta(days=n) if u == "d" else timedelta(hours=n) if u == "h" else \
            timedelta(minutes=n) if u == "m" else timedelta(seconds=n)
    return now + total if total > timedelta() else None


def _clock_candidate(text: str, now: datetime) -> datetime | None:
    """'3am', '3:30 PM', 'at 03:00', optionally with 'Oct 17', '17 Oct' or 'Mon' and '(Asia/Bangkok)'."""
    m = _TIME_AMPM.search(text)
    if m:
        hour = int(m.group(1)) % 12 + (12 if m.group(3).lower() == "p" else 0)
        minute = int(m.group(2) or 0)
    else:
        m = _TIME_24.search(text)
        if not m:
            return None
        hour, minute = int(m.group(1)), int(m.group(2))

    tz = _find_tz(text, now.tzinfo)
    local_now = now.astimezone(tz) if tz else now

    def at(day) -> datetime | None:
        try:
            return datetime(day.year, day.month, day.day, hour, minute, tzinfo=tz)
        except ValueError:
            return None

    md = _DATE_MD.search(text)
    dm = _DATE_DM.search(text)
    if md or dm:
        month = _MONTHS[(md.group(1) if md else dm.group(2)).lower()]
        day_no = int(md.group(2) if md else dm.group(1))
        for year in (local_now.year, local_now.year + 1):
            try:
                cand = datetime(year, month, day_no, hour, minute, tzinfo=tz)
            except ValueError:
                return None
            if cand > local_now:
                return cand
        return None

    wd = _WEEKDAY.search(text)
    if wd:
        want = _WEEKDAYS[wd.group(1).lower()]
        for ahead in range(0, 8):
            cand = at(local_now + timedelta(days=ahead))
            if cand and cand.weekday() == want and cand > local_now:
                return cand
        return None

    cand = at(local_now)
    if cand and cand <= local_now:
        cand = at(local_now + timedelta(days=1))
    return cand


def parse_reset(text: str, now: datetime | None = None) -> tuple[datetime | None, str]:
    """Reset time announced in a limit message as (aware datetime, source), or (None, "none").

    Understands, in this order: `|<epoch>` and `resets <epoch>`; an ISO time; "in 2 hours 15 minutes";
    a clock time such as "3am", "3:30 PM" or "03:00", with optional date/weekday and "(Asia/Bangkok)".
    A clock time without a date means its next occurrence. Never raises on odd input.
    """
    try:
        now = now or _now()
        if now.tzinfo is None:
            now = now.astimezone()
        if not text:
            return None, "none"
        m = _PIPE_EPOCH.search(text) or _RESET_EPOCH.search(text)
        if m:
            dt = _epoch_to_dt(float(m.group(1)), now.tzinfo)
            if dt:
                return dt, "epoch"
        r = _RESET_WORD.search(text)
        tail = text[r.start():] if r else text
        for source, fn in (("iso", _iso_candidate), ("relative", _relative_candidate), ("clock", _clock_candidate)):
            dt = fn(tail, now)
            if dt:
                return dt, source
    except Exception:
        pass
    return None, "none"


# ---------------------------------------------------------------------------------------------------------
# waiting

@dataclass(frozen=True)
class WaitPlan:
    until: datetime          # when to try again (reset + margin, or now + retry interval)
    parsed: bool             # True when based on a reset time from the message
    shown: datetime          # the time to show to viewers (the reset itself when known)


def plan_wait(info: LimitInfo, now: datetime, retry_seconds: float, margin_seconds: float) -> WaitPlan:
    margin = timedelta(seconds=margin_seconds)
    r = info.reset_at
    if r is not None and r - now <= MAX_AHEAD:
        # A reset that is already in the past (clock skew, or the window just reopened): just the margin.
        return WaitPlan(until=max(r, now) + margin, parsed=True, shown=max(r, now))
    until = now + timedelta(seconds=retry_seconds)
    return WaitPlan(until=until, parsed=False, shown=until)


def status_text(plan: WaitPlan, now: datetime, retry_seconds: float) -> str:
    """Thai status line for the overlay, e.g. 'รอ usage limit รีเซ็ต (ถึง 03:00)'."""
    shown = plan.shown.astimezone(now.tzinfo)
    same_day = shown.date() == now.date()
    stamp = f"{shown:%H:%M}" if same_day else f"{shown:%d/%m %H:%M}"
    if plan.parsed:
        return f"รอ usage limit รีเซ็ต (ถึง {stamp})"
    return f"รอ usage limit รีเซ็ต (ไม่รู้เวลารีเซ็ต ลองใหม่ทุก {retry_seconds / 60:g} นาที ถัดไป {stamp})"


class LimitWaiter:
    def __init__(self, post: Callable[[str, dict], object] | None = None, *,
                 retry_seconds: float = DEFAULT_RETRY_MINUTES * 60,
                 margin_seconds: float = DEFAULT_MARGIN_SECONDS,
                 max_waits: int = DEFAULT_MAX_WAITS,
                 chunk_seconds: float = CHUNK_SECONDS,
                 now: Callable[[], datetime] | None = None,
                 sleep: Callable[[float], None] | None = None,
                 log: Callable[[str], None] = print):
        self.post = post
        self.retry_seconds = retry_seconds
        self.margin_seconds = margin_seconds
        self.max_waits = max_waits
        self.chunk_seconds = chunk_seconds
        self._now = now or (lambda: _now())
        self._sleep = sleep or (lambda s: _sleep(s))
        self.log = log

    @classmethod
    def from_args(cls, args, post: Callable[[str, dict], object] | None = None) -> "LimitWaiter":
        return cls(post, retry_seconds=args.limit_retry_minutes * 60, margin_seconds=args.limit_margin_seconds,
                   max_waits=args.max_limit_waits)

    def _post(self, status: str, event: str | None = None) -> None:
        if not self.post:
            return
        body = {STATUS_KEY: status}
        if event:
            body["event"] = event
        try:
            self.post("/api/meta", body)
        except Exception:
            pass        # the overlay is optional; never let it stop the run

    def wait(self, hit: UsageLimitHit, day: int, part: int, attempt: int = 1) -> None:
        """Sleep until the limit should have reset. Raises SystemExit after max_waits waits in a row."""
        if attempt > self.max_waits:
            raise SystemExit(f"Usage limit still active after {self.max_waits} waits (day {day} part {part}), "
                             "stopping for a human to check.")
        now = self._now()
        plan = plan_wait(hit.info, now, self.retry_seconds, self.margin_seconds)
        text = status_text(plan, now, self.retry_seconds)
        known = f"resets {plan.shown:%Y-%m-%d %H:%M %Z}" if plan.parsed else "reset time unknown"
        self.log(f"[{now:%H:%M:%S}] usage limit reached on day {day} part {part} ({known}); "
                 f"waiting until {plan.until:%Y-%m-%d %H:%M:%S}, then resuming the same day "
                 f"(wait {attempt}/{self.max_waits}). Message: {hit.info.message[:120]}")
        self._post(text, f"Usage limit reached (day {day} part {part}); {known}. Waiting, then resuming the same day.")
        done = False
        try:
            while True:
                remaining = (plan.until - self._now()).total_seconds()
                if remaining <= 0:
                    break
                self._sleep(min(remaining, self.chunk_seconds))
                if (plan.until - self._now()).total_seconds() > 0:
                    self._post(text)        # keep the status alive (e.g. after a game restart)
            done = True
        finally:
            # always clear the overlay status, even on Ctrl+C; log the "resuming" event only after a full wait
            self._post("", f"Usage limit wait over; resuming day {day} part {part}" if done else None)
        self.log(f"[{self._now():%H:%M:%S}] wait over, resuming day {day} part {part}")


def add_args(ap) -> None:
    """The three runner options, registered with one call from run_days.main()."""
    ap.add_argument("--limit-retry-minutes", type=float, default=DEFAULT_RETRY_MINUTES,
                    help="Usage limit hit and the reset time can't be parsed: retry this often")
    ap.add_argument("--limit-margin-seconds", type=float, default=DEFAULT_MARGIN_SECONDS,
                    help="Extra seconds to wait after the announced usage-limit reset")
    ap.add_argument("--max-limit-waits", type=int, default=DEFAULT_MAX_WAITS,
                    help="Give up (stop for a human) after this many usage-limit waits in a row for one part")


def run_part(waiter: LimitWaiter, run: Callable[[str, int], object], prompt: str,
             resume_prompt: Callable[[], str], day: int, part: int,
             day_done: Callable[[], bool] | None = None):
    """Run one part; if the usage limit ends it, wait for the reset and run the SAME part again.

    run(prompt, attempt) is run_days.run_session; it raises UsageLimitHit. The part number is not touched, so
    a limit never counts toward --max-parts. When the failed attempt had already played (tool calls were
    made), the retry gets the "continue" prompt built from the day log; otherwise it keeps its prompt.
    day_done() is checked after each wait: if the game already moved on (the session slept and only the
    closing message hit the limit), the part is not repeated.
    """
    attempt = 1
    while True:
        try:
            return run(prompt, attempt)
        except UsageLimitHit as hit:
            waiter.wait(hit, day, part, attempt)
            if day_done and day_done():
                return 0
            if hit.tool_calls:
                prompt = resume_prompt()
            attempt += 1


# ---------------------------------------------------------------------------------------------------------
# check a saved transcript: py runner/usage_limit.py runs/logs/claude_day_001_part1.jsonl

def scan_transcript(lines: Iterable[str], now: Callable[[], datetime] | None = None) -> tuple[LimitInfo | None, int]:
    """Run the detector over saved stream-json lines; returns (limit info or None, tool calls seen)."""
    w = LimitWatcher(now)
    tool_calls = 0
    for line in lines:
        line = line.strip()
        if not line:
            continue
        try:
            ev = json.loads(line)
        except json.JSONDecodeError:
            w.feed_line(line)
            continue
        if isinstance(ev, dict):
            if ev.get("type") == "assistant":
                for c in (ev.get("message") or {}).get("content") or []:
                    if isinstance(c, dict) and c.get("type") == "tool_use":
                        tool_calls += 1
            w.feed(ev)
    return w.info, tool_calls


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 2
    rc = 0
    for path in argv:
        with open(path, encoding="utf-8", errors="replace") as f:
            info, calls = scan_transcript(f)
        if info is None:
            print(f"{path}: no usage limit detected ({calls} tool calls)")
            continue
        rc = 1
        when = f"{info.reset_at:%Y-%m-%d %H:%M:%S %Z} (from {info.source})" if info.reset_at else "not parsed (runner retries every 30 min)"
        print(f"{path}: USAGE LIMIT after {calls} tool calls\n  message: {info.message}\n  reset:   {when}")
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
