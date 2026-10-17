"""Tests for runner/sessions.py: break detection, part counting and the handoff to the next part."""

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import pytest

import sessions
import stream_events as se


# ---------------------------------------------------------------------- mcp url and detection


def test_mcp_url_opts_in_to_breaks():
    assert sessions.mcp_url("http://localhost:8770") == "http://localhost:8770/mcp?breaks=1"
    assert sessions.mcp_url("http://localhost:8770", breaks=False) == "http://localhost:8770/mcp"


def test_saw_break_in_list_content():
    assert sessions.saw_break(se.tool_result("t", se.BREAK_NOTICE))


def test_saw_break_in_string_content():
    assert sessions.saw_break(se.tool_result("t", se.BREAK_NOTICE, as_string=True))


def test_saw_break_when_the_notice_follows_the_result_and_footer():
    text = "Planted 3/3\n[12:10 · Farm (1,2) · energy 200/270 · 100g]\n" + se.BREAK_NOTICE
    assert sessions.saw_break(se.tool_result("t", text))


def test_saw_break_in_a_refusal():
    assert sessions.saw_break(se.tool_result("t", "Session break: no more actions in this session. Do this now: ...", is_error=True))


@pytest.mark.parametrize("text", [
    "Map Farm ...",
    "Abigail says: There was a session break: at the library",  # not at the start of a line
    "Abigail says: Session break: the library is closed",  # same words, capitalised, but mid-line
    "Map Farm ...\n[06:00 · Farm (1,2)] | Session break: it is only a tile label",  # footer is one line
    "",
])
def test_normal_results_are_not_breaks(text):
    assert not sessions.saw_break(se.tool_result("t", text))


def test_only_user_events_can_carry_a_break():
    ev = se.assistant("m1", text="Session break: I will stop")
    assert not sessions.saw_break(ev)
    assert sessions.tool_result_texts(ev) == []


def test_malformed_events_do_not_crash():
    assert not sessions.saw_break({})
    assert not sessions.saw_break({"type": "user"})
    assert not sessions.saw_break({"type": "user", "message": {"content": "plain string"}})
    assert not sessions.saw_break({"type": "user", "message": {"content": [None, 3, {"type": "tool_result"}]}})


# ---------------------------------------------------------------------- part counting


def test_unplanned_parts_count_toward_max_parts():
    c = sessions.PartCounter(max_parts=3)
    assert c.after_part(False)       # part 1 stopped early: part 2 may start
    assert c.after_part(False)       # part 2 stopped early: part 3 may start
    assert not c.after_part(False)   # part 3 stopped early: that was the last allowed


def test_planned_breaks_do_not_count():
    c = sessions.PartCounter(max_parts=2)
    for _ in range(5):
        assert c.after_part(True)
    assert c.unplanned == 0
    assert c.after_part(False)       # one unplanned stop is still fine with max_parts=2
    assert not c.after_part(False)


def test_planned_breaks_are_capped_so_a_loop_cannot_run_forever():
    c = sessions.PartCounter(max_parts=2, max_breaks=3)
    assert [c.after_part(True) for _ in range(3)] == [True, True, True]
    assert c.after_part(True)        # the 4th break now counts as an unplanned part (1 of 2)
    assert not c.after_part(True)    # and the 5th ends the day's attempts
    assert c.breaks == 3


def test_a_day_with_two_planned_breaks_and_max_parts_one_still_gets_three_parts():
    c = sessions.PartCounter(max_parts=1)
    assert c.after_part(True)
    assert c.after_part(True)
    assert not c.after_part(False)   # the third part stopped without bedtime and without a notice


# ---------------------------------------------------------------------- handoff


def write_day_log(log_dir: Path, day: int, entries: list[dict]) -> None:
    (log_dir / f"day_{day:03}.jsonl").write_text("".join(json.dumps(e, ensure_ascii=False) + "\n" for e in entries), encoding="utf-8")


@pytest.fixture(autouse=True)
def no_providers():
    sessions.clear_context_providers()
    yield
    sessions.clear_context_providers()


def test_handoff_has_the_morning_plan_and_the_latest_think(tmp_path):
    write_day_log(tmp_path, 4, [
        {"kind": "think", "gameTime": "06:10", "player": "p1", "text": "แผนวันนี้: ปลูกพาร์สนิปแล้วไปหาปิแอร์"},
        {"kind": "action", "gameTime": "07:00", "player": "p1", "tool": "use_tool", "text": "ถางหญ้า"},
        {"kind": "think", "gameTime": "12:01", "player": "p1", "text": "ครึ่งวันแล้ว ต้องรดน้ำแปลงที่ 2 ต่อ"},
    ])
    text = sessions.handoff_text(tmp_path, 4)
    assert "ปลูกพาร์สนิป" in text and "06:10" in text
    assert "รดน้ำแปลงที่ 2" in text and "12:01" in text


def test_handoff_with_one_think_prints_it_once(tmp_path):
    write_day_log(tmp_path, 1, [{"kind": "think", "gameTime": "06:00", "player": "p1", "text": "เริ่มวันใหม่"}])
    text = sessions.handoff_text(tmp_path, 1)
    assert text.count("เริ่มวันใหม่") == 1


def test_handoff_ignores_other_players_and_blank_thoughts(tmp_path):
    write_day_log(tmp_path, 2, [
        {"kind": "think", "gameTime": "06:00", "player": "p2", "text": "ความคิดของเกมินี"},
        {"kind": "think", "gameTime": "06:05", "player": "p1", "text": "   "},
        {"kind": "think", "gameTime": "06:10", "text": "เก่ากว่า ไม่มี player"},
        "not a dict line",
    ])
    (tmp_path / "day_002.jsonl").write_text((tmp_path / "day_002.jsonl").read_text(encoding="utf-8") + "{broken json\n", encoding="utf-8")
    text = sessions.handoff_text(tmp_path, 2)
    assert "เกมินี" not in text
    assert "ไม่มี player" in text


def test_handoff_without_a_log_is_empty(tmp_path):
    assert sessions.handoff_text(tmp_path, 9) == ""


def test_handoff_clips_long_thoughts(tmp_path):
    write_day_log(tmp_path, 3, [{"kind": "think", "gameTime": "06:00", "player": "p1", "text": "ก" * 5000}])
    assert len(sessions.handoff_text(tmp_path, 3)) < 800


def test_context_provider_text_is_added_and_failures_are_survived(tmp_path):
    write_day_log(tmp_path, 5, [{"kind": "think", "gameTime": "06:00", "player": "p1", "text": "แผน"}])
    seen = []

    def journal(log_dir, day):
        seen.append((log_dir, day))
        return "\n## Today\nขายผักที่ถังส่งของ\n"

    def broken(log_dir, day):
        raise RuntimeError("boom")

    sessions.add_context_provider(journal)
    sessions.add_context_provider(journal)  # registering twice must not repeat the text
    sessions.add_context_provider(broken)
    sessions.add_context_provider(lambda log_dir, day: "   ")
    text = sessions.handoff_text(tmp_path, 5)
    assert text.count("ขายผักที่ถังส่งของ") == 1
    assert seen == [(tmp_path, 5)]


def test_planned_prompt_says_the_break_was_on_purpose(tmp_path):
    write_day_log(tmp_path, 6, [{"kind": "think", "gameTime": "12:00", "player": "p1", "text": "เหลือรดน้ำ"}])
    prompt = sessions.continuation_prompt(6, tmp_path, planned=True, recent="12:00 result use_tool: ok")
    assert "วันที่ 6" in prompt and "Session break" in prompt
    assert "เหลือรดน้ำ" in prompt and "12:00 result use_tool: ok" in prompt
    assert prompt.rstrip().endswith("go_to_bed")


def test_unplanned_prompt_keeps_the_old_wording(tmp_path):
    prompt = sessions.continuation_prompt(6, tmp_path, planned=False, recent="x")
    assert "ก่อนเข้านอน" in prompt and "Session break" not in prompt
    assert "ผลล่าสุด" in prompt


def test_prompt_without_history_is_still_usable(tmp_path):
    prompt = sessions.continuation_prompt(1, tmp_path, planned=True)
    assert "วันที่ 1" in prompt and "go_to_bed" in prompt and "ผลล่าสุด" not in prompt
