"""Unit tests for runner/usage_limit.py. The fixtures are SYNTHETIC (see fixtures/README.md)."""

from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

import usage_limit as ul

FIX = Path(__file__).parent / "fixtures"
BKK = timezone(timedelta(hours=7))
NOW = datetime(2026, 10, 16, 22, 30, tzinfo=BKK)        # a Friday evening
RESET_3AM = datetime(2026, 10, 17, 3, 0, tzinfo=BKK)    # = epoch 1792180800 used by the fixtures


def scan(name):
    with open(FIX / name, encoding="utf-8") as f:
        return ul.scan_transcript(f, now=lambda: NOW)


def has_tzdata():
    try:
        from zoneinfo import ZoneInfo
        ZoneInfo("Asia/Bangkok")
        return True
    except Exception:
        return False


# --- parse_reset -----------------------------------------------------------------------------------------

@pytest.mark.parametrize("text, expected, source", [
    ("Claude AI usage limit reached|1792180800", RESET_3AM, "epoch"),
    ("Claude AI usage limit reached | 1792180800", RESET_3AM, "epoch"),
    ("usage limit reached|1792180800000", RESET_3AM, "epoch"),                     # milliseconds
    ("Your limit resets at 1792180800", RESET_3AM, "epoch"),
    ("limit reached, resets 2026-10-17T03:00:00+07:00", RESET_3AM, "iso"),
    ("limit reached, resets 2026-10-16T20:00:00Z", RESET_3AM, "iso"),
    ("5-hour limit reached ∙ resets 3am", RESET_3AM, "clock"),                # no tz: now's tz
    ("5-hour limit reached ∙ resets 3:00 AM", RESET_3AM, "clock"),
    ("resets at 03:00", RESET_3AM, "clock"),
    ("resets 10pm", NOW.replace(hour=22, minute=0) + timedelta(days=1), "clock"),  # 22:00 already passed
    ("resets at 11:30 PM", NOW.replace(hour=23, minute=30), "clock"),              # later today
    ("resets 12am", NOW.replace(hour=0, minute=0) + timedelta(days=1), "clock"),
    ("resets 3am (UTC)", datetime(2026, 10, 17, 3, 0, tzinfo=timezone.utc), "clock"),
    ("resets Oct 20, 9am", datetime(2026, 10, 20, 9, 0, tzinfo=BKK), "clock"),
    ("resets 20 Oct at 9:30", datetime(2026, 10, 20, 9, 30, tzinfo=BKK), "clock"),
    ("Weekly limit reached ∙ resets Mon 9am", datetime(2026, 10, 19, 9, 0, tzinfo=BKK), "clock"),
    ("weekly limit, resets Monday at 9:00 AM", datetime(2026, 10, 19, 9, 0, tzinfo=BKK), "clock"),
    ("resets Jan 2, 8am", datetime(2027, 1, 2, 8, 0, tzinfo=BKK), "clock"),         # rolls into next year
    ("You've hit your limit · resets in 2 hours 15 minutes", NOW + timedelta(hours=2, minutes=15), "relative"),
    ("limit reached, try again in 45 minutes", NOW + timedelta(minutes=45), "relative"),
    ("resets in 1h 30m", NOW + timedelta(hours=1, minutes=30), "relative"),
    ("retry after 90 seconds", NOW + timedelta(seconds=90), "relative"),
    ("resets in 2 days", NOW + timedelta(days=2), "relative"),
])
def test_parse_reset_formats(text, expected, source):
    got, src = ul.parse_reset(text, NOW)
    assert got is not None, text
    assert got == expected, (text, got, expected)
    assert src == source


@pytest.mark.skipif(not has_tzdata(), reason="needs a tz database for named zones")
def test_parse_reset_named_zone():
    got, src = ul.parse_reset("You've hit your limit · resets 3am (Asia/Bangkok)", NOW)
    assert got == RESET_3AM and src == "clock"
    got, _ = ul.parse_reset("resets 3am (America/Los_Angeles)", NOW)
    assert got is not None and got > NOW and got - NOW < timedelta(days=2)


def test_parse_reset_unknown_zone_falls_back_to_now_zone():
    got, _ = ul.parse_reset("resets 3am (Mars/Olympus_Mons)", NOW)
    assert got == RESET_3AM


@pytest.mark.parametrize("text", [
    "", "Claude usage limit reached. Please try again later.", "resets soon", "resets 3", "resets in a while",
    "resets Feb 31, 3am", "usage limit reached|123", "resets 25:99",
])
def test_parse_reset_gives_up_cleanly(text):
    assert ul.parse_reset(text, NOW) == (None, "none")


# --- detection -------------------------------------------------------------------------------------------

def test_detects_pipe_epoch_fixture():
    info, calls = scan("limit_pipe_epoch.jsonl")
    assert info and calls == 0
    assert info.reset_at == RESET_3AM and info.source == "epoch"
    assert "usage limit reached" in info.message


def test_detects_midsession_clock_fixture():
    info, calls = scan("limit_resets_clock_midsession.jsonl")
    assert info and calls == 3
    assert info.reset_at == RESET_3AM and info.source == "clock"


def test_rate_limit_event_reset_is_preferred():
    info, calls = scan("limit_rate_limit_event.jsonl")
    assert info and calls == 1
    assert info.reset_at == RESET_3AM and info.source == "event"


def test_unparseable_limit_is_detected_without_reset():
    info, _ = scan("limit_unparseable.jsonl")
    assert info is not None and info.reset_at is None and info.source == "none"


def test_plain_text_line_is_detected():
    info, calls = scan("limit_plain_line.txt")
    assert info and calls == 0 and info.reset_at == RESET_3AM


@pytest.mark.parametrize("name", ["error_not_limit.jsonl", "ok_day.jsonl", "not_a_limit_mentions.jsonl"])
def test_other_outcomes_are_not_limits(name):
    info, _ = scan(name)
    assert info is None


def test_watcher_ignores_human_wording_without_is_error():
    w = ul.LimitWatcher(lambda: NOW)
    ev = {"type": "result", "is_error": False, "result": "You've hit your limit · resets 3am"}
    assert w.feed(ev) is None


def test_watcher_strong_form_needs_no_is_error():
    w = ul.LimitWatcher(lambda: NOW)
    ev = {"type": "result", "result": "Claude AI usage limit reached|1792180800"}
    assert w.feed(ev) and w.info.reset_at == RESET_3AM


def test_watcher_rejected_rate_limit_event_alone_is_not_enough_without_error_result():
    w = ul.LimitWatcher(lambda: NOW)
    w.feed({"type": "rate_limit_event", "rate_limit_info": {"status": "allowed_warning", "resetsAt": 1792180800}})
    assert w.feed({"type": "result", "is_error": True, "result": "API Error: 500"}) is None


def test_watcher_rejected_event_plus_error_result_is_a_limit_even_with_odd_text():
    w = ul.LimitWatcher(lambda: NOW)
    w.feed({"type": "rate_limit_event", "rate_limit_info": {"status": "rejected", "resetsAt": 1792180800}})
    info = w.feed({"type": "result", "is_error": True, "result": "Something went wrong"})
    assert info and info.reset_at == RESET_3AM


def test_watcher_tolerates_junk_events():
    w = ul.LimitWatcher(lambda: NOW)
    for ev in ({}, {"type": "result"}, {"type": "result", "result": None, "is_error": True}, {"type": "assistant"},
               {"type": "rate_limit_event", "rate_limit_info": "x"}, {"type": "result", "errors": [1, "x"], "is_error": True}):
        assert w.feed(ev) is None


def test_watcher_reads_errors_list():
    w = ul.LimitWatcher(lambda: NOW)
    info = w.feed({"type": "result", "is_error": True, "errors": ["You've hit your limit · resets 3am"]})
    assert info and info.reset_at == RESET_3AM


def test_cli_main_reports(capsys):
    assert ul.main([str(FIX / "limit_pipe_epoch.jsonl"), str(FIX / "ok_day.jsonl")]) == 1
    out = capsys.readouterr().out
    assert "USAGE LIMIT after 0 tool calls" in out and "no usage limit detected (3 tool calls)" in out


# --- plan / status ---------------------------------------------------------------------------------------

def plan(reset, retry=1800, margin=60, now=NOW):
    return ul.plan_wait(ul.LimitInfo("m", reset), now, retry, margin)


def test_plan_wait_until_reset_plus_margin():
    p = plan(RESET_3AM)
    assert p.parsed and p.until == RESET_3AM + timedelta(seconds=60) and p.shown == RESET_3AM


def test_plan_wait_past_reset_waits_only_the_margin():
    p = plan(NOW - timedelta(minutes=5))
    assert p.parsed and p.until == NOW + timedelta(seconds=60)


def test_plan_wait_unknown_or_absurd_reset_retries_every_30_minutes():
    assert plan(None).until == NOW + timedelta(minutes=30) and not plan(None).parsed
    assert not plan(NOW + timedelta(days=30)).parsed


def test_status_text_thai():
    assert ul.status_text(plan(RESET_3AM), NOW, 1800) == "รอ usage limit รีเซ็ต (ถึง 17/10 03:00)"   # another date: show it
    same_day = NOW + timedelta(minutes=20)
    assert ul.status_text(plan(same_day), NOW, 1800) == "รอ usage limit รีเซ็ต (ถึง 22:50)"
    assert ul.status_text(plan(None), NOW, 1800) == "รอ usage limit รีเซ็ต (ไม่รู้เวลารีเซ็ต ลองใหม่ทุก 30 นาที ถัดไป 23:00)"


# --- waiter ----------------------------------------------------------------------------------------------

class Clock:
    def __init__(self, now=NOW):
        self.t = now
        self.slept = []

    def now(self):
        return self.t

    def sleep(self, s):
        self.slept.append(s)
        self.t += timedelta(seconds=s)


def make_waiter(clock, posts, **kw):
    return ul.LimitWaiter(lambda path, body: posts.append((path, body)), now=clock.now, sleep=clock.sleep,
                          log=lambda *_: None, **kw)


def test_wait_sleeps_in_chunks_until_reset_and_posts_status():
    clock, posts = Clock(), []
    hit = ul.UsageLimitHit(ul.LimitInfo("m", RESET_3AM, "clock"), 0)
    make_waiter(clock, posts).wait(hit, day=3, part=2)
    assert sum(clock.slept) == pytest.approx((RESET_3AM - NOW).total_seconds() + 60)
    assert max(clock.slept) <= ul.CHUNK_SECONDS
    assert clock.t == RESET_3AM + timedelta(seconds=60)
    assert all(p == "/api/meta" for p, _ in posts)
    bodies = [b for _, b in posts]
    assert bodies[0]["runnerStatus"] == "รอ usage limit รีเซ็ต (ถึง 17/10 03:00)"
    assert "day 3 part 2" in bodies[0]["event"]
    assert bodies[-1] == {"runnerStatus": "", "event": "Usage limit wait over; resuming day 3 part 2"}
    assert len(bodies) > 2 and all(b["runnerStatus"] == bodies[0]["runnerStatus"] for b in bodies[1:-1])   # heartbeats


def test_wait_unknown_reset_uses_retry_interval():
    clock, posts = Clock(), []
    make_waiter(clock, posts, retry_seconds=1800, margin_seconds=0).wait(ul.UsageLimitHit(ul.LimitInfo("m", None)), 1, 1)
    assert sum(clock.slept) == 1800
    assert "ไม่รู้เวลารีเซ็ต" in posts[0][1]["runnerStatus"]


def test_wait_clears_status_when_interrupted():
    clock, posts = Clock(), []

    def boom(s):
        raise KeyboardInterrupt

    w = ul.LimitWaiter(lambda p, b: posts.append(b), now=clock.now, sleep=boom, log=lambda *_: None)
    with pytest.raises(KeyboardInterrupt):
        w.wait(ul.UsageLimitHit(ul.LimitInfo("m", RESET_3AM)), 1, 1)
    assert posts[-1] == {"runnerStatus": ""}


def test_wait_survives_a_dead_api():
    clock = Clock()

    def post(path, body):
        raise OSError("game not running")

    w = ul.LimitWaiter(post, now=clock.now, sleep=clock.sleep, log=lambda *_: None, margin_seconds=0)
    w.wait(ul.UsageLimitHit(ul.LimitInfo("m", NOW + timedelta(minutes=10))), 1, 1)
    assert sum(clock.slept) == 600


def test_wait_gives_up_after_max_waits():
    clock = Clock()
    w = make_waiter(clock, [], max_waits=2)
    hit = ul.UsageLimitHit(ul.LimitInfo("m", None))
    w.wait(hit, 1, 1, attempt=2)
    with pytest.raises(SystemExit, match="still active after 2 waits"):
        w.wait(hit, 1, 1, attempt=3)


# --- run_part --------------------------------------------------------------------------------------------

def test_run_part_retries_same_part_with_continue_prompt_after_midsession_limit():
    clock = Clock()
    waiter = make_waiter(clock, [], margin_seconds=0)
    seen = []

    def run(prompt, attempt):
        seen.append((prompt, attempt))
        if attempt == 1:
            raise ul.UsageLimitHit(ul.LimitInfo("m", NOW + timedelta(minutes=5)), tool_calls=4)
        return 7

    assert ul.run_part(waiter, run, "first", lambda: "continue", day=2, part=1) == 7
    assert seen == [("first", 1), ("continue", 2)]


def test_run_part_keeps_prompt_when_nothing_was_played():
    waiter = make_waiter(Clock(), [], margin_seconds=0)
    seen = []

    def run(prompt, attempt):
        seen.append(prompt)
        if attempt < 3:
            raise ul.UsageLimitHit(ul.LimitInfo("m", None), tool_calls=0)
        return 0

    ul.run_part(waiter, run, "first", lambda: "continue", day=1, part=1)
    assert seen == ["first", "first", "first"]


def test_run_part_does_not_repeat_when_the_day_already_ended():
    waiter = make_waiter(Clock(), [], margin_seconds=0)
    calls = []

    def run(prompt, attempt):
        calls.append(attempt)
        raise ul.UsageLimitHit(ul.LimitInfo("m", None), tool_calls=1)

    assert ul.run_part(waiter, run, "p", lambda: "c", 1, 1, day_done=lambda: True) == 0
    assert calls == [1]


def test_run_part_passes_other_exceptions_through():
    waiter = make_waiter(Clock(), [])

    def run(prompt, attempt):
        raise SystemExit("Claude error, stopping: boom")

    with pytest.raises(SystemExit, match="boom"):
        ul.run_part(waiter, run, "p", lambda: "c", 1, 1)
