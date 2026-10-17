"""The synthetic fixtures in runner/samples/ must stay realistic and free of personal data.

The other runner tests build on them (a fake `claude` replays the transcripts, the game-log helpers read the
logs), so a sample that drifts from the real formats would make those tests pass for the wrong reason.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
SAMPLES = ROOT / "runner" / "samples"
TRANSCRIPTS = sorted(SAMPLES.glob("claude_stream_*.jsonl"))
GAME_LOGS = sorted((SAMPLES / "game_logs").glob("day_*.jsonl"))
DIARIES = sorted((SAMPLES / "game_logs").glob("diary_*.md"))

# Only the "noise" sample has lines that are not JSON (stderr is merged into stdout by run_days.py).
NOISY = {"claude_stream_stderr_noise.jsonl"}
LOG_KINDS = {"think", "action", "result", "diary", "system"}


def json_events(path: Path) -> list[dict]:
    events = []
    for line in path.read_text(encoding="utf-8").splitlines():
        try:
            events.append(json.loads(line))
        except json.JSONDecodeError:
            pass
    return events


def test_samples_exist():
    assert len(TRANSCRIPTS) == 4
    assert GAME_LOGS and DIARIES


# ---------------------------------------------------------------------------------------- transcripts

@pytest.mark.parametrize("path", TRANSCRIPTS, ids=lambda p: p.name)
def test_transcript_lines_are_json(path):
    lines = path.read_text(encoding="utf-8").splitlines()
    not_json = []
    for line in lines:
        try:
            json.loads(line)
        except json.JSONDecodeError:
            not_json.append(line)
    if path.name in NOISY:
        assert any(line.strip() for line in not_json), "the noise sample should contain non-JSON output"
    else:
        assert not_json == []


@pytest.mark.parametrize("path", TRANSCRIPTS, ids=lambda p: p.name)
def test_transcript_starts_with_init_and_ends_with_result(path):
    events = json_events(path)
    assert (events[0]["type"], events[0].get("subtype")) == ("system", "init")
    assert events[-1]["type"] == "result"
    assert [e["type"] for e in events].count("result") == 1
    assert events[0]["session_id"]
    assert events[0]["model"]


@pytest.mark.parametrize("path", TRANSCRIPTS, ids=lambda p: p.name)
def test_transcript_uses_one_session_id(path):
    ids = {e["session_id"] for e in json_events(path) if "session_id" in e}
    assert len(ids) == 1


def test_session_ids_differ_between_transcripts():
    ids = [json_events(p)[0]["session_id"] for p in TRANSCRIPTS]
    assert len(set(ids)) == len(ids)


@pytest.mark.parametrize("path", TRANSCRIPTS, ids=lambda p: p.name)
def test_every_tool_call_gets_an_answer(path):
    calls, answers = [], []
    for event in json_events(path):
        content = (event.get("message") or {}).get("content", [])
        if not isinstance(content, list):
            continue
        for block in content:
            if block.get("type") == "tool_use":
                calls.append(block["id"])
                assert block["name"].startswith("mcp__stardew__")
                assert isinstance(block["input"], dict)
            elif block.get("type") == "tool_result":
                answers.append(block["tool_use_id"])
    assert len(calls) == len(set(calls))
    assert answers == calls  # answered in order, nothing left dangling


def test_full_day_transcript_is_a_finished_day():
    events = json_events(SAMPLES / "claude_stream_day.jsonl")
    calls = [b for e in events if e["type"] == "assistant" for b in e["message"]["content"] if b["type"] == "tool_use"]
    assert len(calls) == 6
    assert calls[-1]["name"] == "mcp__stardew__go_to_bed"
    assert calls[-1]["input"]["diary"]
    result = events[-1]
    assert (result["subtype"], result["is_error"], result["num_turns"]) == ("success", False, 7)


def test_failure_samples_describe_their_failure():
    err = json_events(SAMPLES / "claude_stream_error.jsonl")[-1]
    assert err["is_error"] and "Invalid API key" in err["result"]
    limit = json_events(SAMPLES / "claude_stream_max_turns.jsonl")[-1]
    assert limit["is_error"] and limit["subtype"] == "error_max_turns"


# ---------------------------------------------------------------------------------------------- game logs

@pytest.mark.parametrize("path", GAME_LOGS, ids=lambda p: p.name)
def test_game_log_entries_have_the_mod_fields(path):
    entries = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
    assert entries
    day = int(re.search(r"day_(\d+)", path.name).group(1))
    for e in entries:
        assert {"seq", "ts", "day", "gameTime", "kind"} <= e.keys()
        assert e["kind"] in LOG_KINDS
        assert e["day"] == day
        assert re.fullmatch(r"\d{2}:\d{2}", e["gameTime"])
        if e["kind"] in ("action", "result"):
            assert e["tool"]
    assert [e["seq"] for e in entries] == sorted({e["seq"] for e in entries}), "seq must increase"


def test_game_log_has_every_kind_the_runner_reads():
    kinds = {json.loads(line)["kind"] for p in GAME_LOGS for line in p.read_text(encoding="utf-8").splitlines()}
    assert {"think", "action", "result"} <= kinds  # what run_days.last_actions keeps
    assert "system" in kinds and "diary" in kinds  # what it must skip


@pytest.mark.parametrize("path", DIARIES, ids=lambda p: p.name)
def test_diary_starts_with_its_day_heading(path):
    number = int(re.search(r"diary_(\d+)", path.name).group(1))
    assert path.read_text(encoding="utf-8").startswith(f"# Day {number} ")


# ------------------------------------------------------------------------------------------- hygiene

def sample_files() -> list[Path]:
    return sorted(p for p in SAMPLES.rglob("*") if p.is_file())


@pytest.mark.parametrize("path", sample_files(), ids=lambda p: str(p.relative_to(SAMPLES)))
def test_samples_contain_no_personal_data(path):
    text = path.read_text(encoding="utf-8")
    forbidden = [r"/home/", r"/Users/", r"[A-Za-z]:\\", r"AppData", r"\bC:/", r"[\w.+-]+@[\w-]+\.[\w.]+",
                 r"sk-[A-Za-z0-9]{10,}", r"https?://(?!localhost)"]
    for pattern in forbidden:
        assert not re.search(pattern, text), f"{path.name} matches {pattern!r}"


def test_system_prompt_has_the_placeholders_run_days_fills():
    prompt = (ROOT / "runner" / "system_prompt.md").read_text(encoding="utf-8")
    assert prompt.count("{GOAL}") == 1
    assert prompt.count("{MEMORY}") == 1
