"""End-to-end test of run_days.main() with a fake `claude` command and a fake game API.

The fake claude returns a SYNTHETIC usage-limit error first and then succeeds. The wait is shortened by
replacing usage_limit's clock and sleep with a fake clock that jumps forward instead of sleeping.
"""

import json
import re
import sys
from datetime import datetime, timedelta
from pathlib import Path

import pytest

import run_days
import usage_limit

FAKE = Path(__file__).parent / "fake_claude.py"
STATUS_RE = re.compile(r"^รอ usage limit รีเซ็ต \(ถึง (\d\d/\d\d )?\d\d:\d\d\)$")


class FakeClock:
    """Replaces usage_limit._now/_sleep: sleeping advances the clock and records the request."""

    def __init__(self):
        self.offset = timedelta()
        self.slept: list[float] = []

    def now(self):
        return datetime.now().astimezone() + self.offset

    def sleep(self, seconds):
        self.slept.append(seconds)
        self.offset += timedelta(seconds=seconds)


class Run:
    def __init__(self, tmp_path, game, clock):
        self.tmp, self.game, self.clock = tmp_path, game, clock
        self.out = tmp_path / "runs"
        self.state = tmp_path / "fake_state"
        self.state.mkdir()

    @property
    def calls(self) -> int:
        f = self.state / "calls"
        return int(f.read_text()) if f.exists() else 0

    @property
    def prompts(self) -> list[str]:
        f = self.state / "prompts.jsonl"
        return [json.loads(l) for l in f.read_text(encoding="utf-8").splitlines()] if f.exists() else []

    def transcripts(self) -> list[str]:
        return sorted(p.name for p in (self.out / "logs").glob("claude_day_*.jsonl"))

    def main(self, *args):
        sys.argv = ["run_days.py", "--days", "1", "--skip-smoke", "--out", str(self.out), "--limit-margin-seconds", "0", *args]
        run_days.main()


@pytest.fixture
def run(tmp_path, game, monkeypatch):
    clock = FakeClock()
    r = Run(tmp_path, game, clock)
    monkeypatch.setattr(run_days, "API", game.url)
    monkeypatch.setattr(run_days, "CLAUDE_CMD", [sys.executable, str(FAKE)])
    monkeypatch.setattr(usage_limit, "_now", clock.now)
    monkeypatch.setattr(usage_limit, "_sleep", clock.sleep)
    monkeypatch.setattr(run_days.time, "sleep", lambda s: None)      # the 3 s pauses between parts
    for var in ("http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY", "all_proxy", "ALL_PROXY"):
        monkeypatch.delenv(var, raising=False)
    monkeypatch.setenv("NO_PROXY", "127.0.0.1,localhost")
    monkeypatch.setenv("no_proxy", "127.0.0.1,localhost")
    monkeypatch.setenv("FAKE_CLAUDE_DIR", str(r.state))
    monkeypatch.setenv("FAKE_CLAUDE_API", f"127.0.0.1:{game.port}")
    monkeypatch.setenv("FAKE_CLAUDE_DAYLOG", str(r.out / "logs" / "day_001.jsonl"))
    appdata = tmp_path / "appdata"
    (appdata / "StardewValley" / "Saves" / "Farm_1").mkdir(parents=True)
    monkeypatch.setenv("APPDATA", str(appdata))
    (r.out / "logs").mkdir(parents=True)

    def scenario(*names):
        monkeypatch.setenv("FAKE_CLAUDE_SCENARIO", json.dumps(list(names)))

    r.scenario = scenario
    return r


def test_limit_once_then_same_day_resumes_and_part_count_is_unchanged(run, game):
    # --max-parts 1: before this change the failed part used up the only part and the day was abandoned
    run.scenario("limit_epoch_mid", "ok")
    run.main("--max-parts", "1")

    assert run.calls == 2
    assert game.day == 2                                                    # day 1 finished in the retry
    assert run.transcripts() == ["claude_day_001_part1.jsonl", "claude_day_001_part1_retry1.jsonl"]   # no part2
    assert (run.out / "saves" / "day_001_Farm_1").is_dir()                  # the day was backed up

    first, second = run.prompts
    assert "ต่อจากเดิม" not in first
    assert "ต่อจากเดิม" in second and "walk_to" in second                   # "continue" prompt built from last_actions

    waited = sum(run.clock.slept)                                           # reset epoch was ~2 h away
    assert 7190 <= waited <= 7201 and max(run.clock.slept) <= usage_limit.CHUNK_SECONDS

    statuses = game.statuses()
    assert STATUS_RE.match(statuses[0]) and statuses[-1] == ""              # shown, then cleared
    assert any("Usage limit reached" in e and "day 1 part 1" in e for e in game.events())
    assert any("resuming day 1 part 1" in e for e in game.events())


def test_parts_that_hit_the_limit_do_not_count_toward_max_parts(run, game):
    # part 1 stops early (counts), part 2 hits the limit once (does not count) and then finishes the day
    run.scenario("ok_nosleep", "limit_relative", "ok")
    run.main("--max-parts", "2")

    assert run.calls == 3 and game.day == 2
    assert run.transcripts() == ["claude_day_001_part1.jsonl", "claude_day_001_part2.jsonl",
                                 "claude_day_001_part2_retry1.jsonl"]
    assert 7190 <= sum(run.clock.slept) <= 7201                             # "resets in 2 hours" parsed


def test_limit_before_any_action_retries_with_the_original_first_prompt(run, game):
    run.scenario("limit_unparseable", "ok")
    run.main("--max-parts", "1", "--limit-retry-minutes", "30")

    assert run.calls == 2 and game.day == 2
    assert run.prompts[0] == run.prompts[1] and "ต่อจากเดิม" not in run.prompts[1]
    assert len(run.clock.slept) == 6 and sum(run.clock.slept) == pytest.approx(1800, abs=1)   # unknown reset: 30 min
    assert "ลองใหม่ทุก 30 นาที" in game.statuses()[0]


def test_plain_text_limit_line_without_json_is_detected(run, game):
    run.scenario("limit_plain", "ok")
    run.main("--max-parts", "1", "--limit-retry-minutes", "10")
    assert run.calls == 2 and game.day == 2 and sum(run.clock.slept) == pytest.approx(600, abs=1)


def test_two_limits_in_a_row_keep_waiting_on_the_same_part(run, game):
    run.scenario("limit_epoch:600", "limit_epoch:600", "ok")
    run.main("--max-parts", "1")
    assert run.calls == 3 and game.day == 2
    assert run.transcripts() == ["claude_day_001_part1.jsonl", "claude_day_001_part1_retry1.jsonl",
                                 "claude_day_001_part1_retry2.jsonl"]


def test_limit_after_the_day_already_ended_is_not_repeated(run, game):
    run.scenario("limit_after_sleep", "ok")
    run.main("--max-parts", "1")
    assert run.calls == 1 and game.day == 2                                  # waited, saw the new day, moved on
    assert (run.out / "saves" / "day_001_Farm_1").is_dir()


def test_other_errors_still_stop_the_run(run, game):
    run.scenario("error", "ok")
    with pytest.raises(SystemExit, match="Claude error, stopping: API Error: 500"):
        run.main("--max-parts", "1")
    assert run.calls == 1 and run.clock.slept == [] and game.statuses() == []


def test_gives_up_after_max_limit_waits(run, game):
    run.scenario("limit_epoch:600")
    with pytest.raises(SystemExit, match="still active after 2 waits"):
        run.main("--max-parts", "1", "--max-limit-waits", "2")
    assert run.calls == 3
    assert game.statuses()[-1] == ""                                         # overlay status not left behind
