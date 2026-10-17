"""memory.py: file naming, placeholder injection, same-day continuation and the journal reminder.

No game, no network, no `claude` CLI: the files are written the way the mod's journal tool writes them.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

import memory

RUNNER = Path(__file__).resolve().parent.parent

PLAN = """\
## Season
Save 10000g for the house upgrade by Summer 1.

## Week
Plant parsnips Monday, water every day.

## Today
Harvest parsnips planted on day 1 (ready day 5), then buy seeds at Pierre's.
"""


def write(path: Path, text: str) -> Path:
    path.write_text(text, encoding="utf-8")
    return path


def test_seat_files_follow_the_mods_naming(tmp_path):
    assert memory.lessons_path(tmp_path) == tmp_path / "lessons.md"
    assert memory.lessons_path(tmp_path, "p1") == tmp_path / "lessons.md"
    assert memory.lessons_path(tmp_path, "p2") == tmp_path / "lessons_p2.md"
    assert memory.plan_path(tmp_path, "p3") == tmp_path / "plan_p3.md"
    assert memory.notes_path(tmp_path) == tmp_path / "notes.md"
    # same shape as AgentLog.DiaryFile
    assert memory.diary_path(tmp_path, 3) == tmp_path / "diary_003.md"
    assert memory.diary_path(tmp_path, 3, "p2") == tmp_path / "diary_003_p2.md"


def test_read_text_missing_file_is_empty(tmp_path):
    assert memory.read_text(tmp_path / "nope.md") == ""


def test_write_text_replaces_without_leaving_temp_files(tmp_path):
    f = tmp_path / "plan.md"
    memory.write_text(f, "old")
    memory.write_text(f, "new\n\n")
    assert f.read_text(encoding="utf-8") == "new\n"
    assert [p.name for p in tmp_path.iterdir()] == ["plan.md"]


def test_plan_section(tmp_path):
    assert memory.plan_section(PLAN, "Today").startswith("Harvest parsnips")
    assert memory.plan_section(PLAN, "season") == "Save 10000g for the house upgrade by Summer 1."
    assert memory.plan_section(PLAN, "Month") == ""
    assert memory.plan_section("no headings at all", "Today") == ""
    # a heading of the AI's own stays inside its section
    nested = "## Week\nw\n## Today\nfirst\n## Crops\nparsnips\nsecond\n"
    assert memory.plan_section(nested, "Today") == "first\n## Crops\nparsnips\nsecond"
    assert memory.plan_section(nested, "Week") == "w"


def test_tail_lines_keeps_the_newest(tmp_path):
    text = "\n".join(f"- Day {i}: lesson" for i in range(1, 201))
    out = memory.tail_lines(text, 150)
    assert "older lines left out: 50" in out
    assert "- Day 200: lesson" in out and "- Day 51: lesson" in out and "- Day 50: lesson" not in out
    assert memory.tail_lines("- one\n- two", 150) == "- one\n- two"


def test_inject_fills_every_placeholder_in_the_real_system_prompt(tmp_path):
    import language
    template = (RUNNER / "system_prompt.md").read_text(encoding="utf-8")
    template = language.fill_prompt(template, "Thai").replace("{RELATIONSHIPS}", "")
    found = set(re.findall(r"\{([A-Z]+)\}", template))
    assert found == set(memory.PLACEHOLDERS), "system_prompt.md and memory.PLACEHOLDERS must list the same placeholders"
    out = memory.inject(template, tmp_path, "Win.", "(This is your first day. No diaries yet.)")
    assert not re.search(r"\{[A-Z]+\}", out)
    assert memory.NO_LESSONS in out and memory.NO_PLAN in out and memory.NO_NOTES in out
    assert "Win." in out and "No diaries yet" in out



def test_day_two_prompt_carries_day_ones_lessons_plan_and_notes(tmp_path):
    """The issue's check: the system prompt of day 2 contains day 1's lessons and plan."""
    write(tmp_path / "lessons.md", "- Day 1: รดน้ำก่อนไปร้านเสมอ\n- Day 1: หินต้องใช้ Pickaxe\n")
    write(tmp_path / "plan.md", PLAN)
    write(tmp_path / "notes.md", "- [shops, day 1] Pierre is closed on Wednesday\n")
    template = (RUNNER / "system_prompt.md").read_text(encoding="utf-8")
    out = memory.inject(template, tmp_path, "Win.", "# Day 1 — spring 1 Y1\n\ndiary text")
    assert "- Day 1: รดน้ำก่อนไปร้านเสมอ" in out
    assert "- Day 1: หินต้องใช้ Pickaxe" in out
    assert "Harvest parsnips planted on day 1 (ready day 5)" in out
    assert "Save 10000g for the house upgrade by Summer 1." in out
    assert "Pierre is closed on Wednesday" in out
    assert "diary text" in out
    assert memory.NO_LESSONS not in out and memory.NO_PLAN not in out


def test_inject_is_single_pass(tmp_path):
    """Text the AI wrote must not be read as a placeholder of its own."""
    write(tmp_path / "lessons.md", "- Day 1: do not write {PLAN} or {GOAL} in a lesson")
    write(tmp_path / "plan.md", "## Today\nreal plan")
    out = memory.inject("L={LESSONS}\nP={PLAN}\nG={GOAL}", tmp_path, "the goal", "d")
    assert "do not write {PLAN} or {GOAL} in a lesson" in out
    assert out.count("real plan") == 1
    assert "G=the goal" in out


def test_inject_uses_the_seats_own_files(tmp_path):
    write(tmp_path / "lessons.md", "- Day 1: p1 lesson")
    write(tmp_path / "lessons_p2.md", "- Day 1: p2 lesson")
    out = memory.inject("{LESSONS}", tmp_path, "g", "d", player="p2")
    assert "p2 lesson" in out and "p1 lesson" not in out


def test_inject_limits_a_long_lessons_file(tmp_path):
    write(tmp_path / "lessons.md", "\n".join(f"- Day {i}: x" for i in range(1, 301)))
    out = memory.inject("{LESSONS}", tmp_path, "g", "d")
    assert "older lines left out: 150" in out and "- Day 300: x" in out and "- Day 1: x" not in out


def test_memory_shows_weekly_digests_before_the_recent_diaries(tmp_path):
    write(tmp_path / "digest_week_001.md", "Week one: planted parsnips.")
    write(tmp_path / "digest_week_002.md", "Week two: bought a cow.")
    write(tmp_path / "digest_week_001_p2.md", "p2's own week one.")
    out = memory.render_memory(tmp_path, "RECENT DIARIES")
    assert out.index("Week one") < out.index("Week two") < out.index("RECENT DIARIES")
    assert "days 1-7" in out and "days 8-14" in out
    assert "p2's own" not in out
    assert memory.render_memory(tmp_path, "RECENT DIARIES", "p2").count("p2's own week one.") == 1
    assert memory.render_memory(tmp_path / "empty", "ONLY DIARIES") == "ONLY DIARIES"


def test_last_think_and_continuation_context(tmp_path):
    entries = [
        {"kind": "think", "player": "p1", "text": "first thought"},
        {"kind": "action", "player": "p1", "tool": "walk_to"},
        {"kind": "think", "player": "p2", "text": "someone else"},
        {"kind": "think", "player": "p1", "text": "ไปเก็บพาร์สนิปที่แปลงเหนือ"},
        {"kind": "result", "player": "p1", "tool": "walk_to", "text": "Arrived"},
    ]
    (tmp_path / "day_005.jsonl").write_text("\n".join(json.dumps(e, ensure_ascii=False) for e in entries) + "\nnot json\n", encoding="utf-8")
    write(tmp_path / "plan.md", PLAN)
    assert memory.last_think(tmp_path, 5) == "ไปเก็บพาร์สนิปที่แปลงเหนือ"
    assert memory.last_think(tmp_path, 5, "p2") == "someone else"
    assert memory.last_think(tmp_path, 6) == ""
    ctx = memory.continuation_context(tmp_path, 5)
    assert "Harvest parsnips planted on day 1" in ctx            # the Today section...
    assert "Save 10000g" not in ctx                              # ...and only that section
    assert "ไปเก็บพาร์สนิปที่แปลงเหนือ" in ctx
    assert memory.continuation_context(tmp_path / "nothing", 5) == ""


def test_journal_reminder():
    spring3 = {"season": "spring", "dayOfMonth": 3}
    assert memory.journal_reminder(spring3, PLAN) == ""
    assert "no plan yet" in memory.journal_reminder(spring3, "")
    assert "Monday" in memory.journal_reminder({"season": "spring", "dayOfMonth": 7}, PLAN)      # tomorrow is day 8
    assert "## Week" in memory.journal_reminder({"season": "spring", "dayOfMonth": 21}, PLAN)
    assert memory.journal_reminder({"season": "spring", "dayOfMonth": 8}, PLAN) == ""
    eve = memory.journal_reminder({"season": "fall", "dayOfMonth": 28}, PLAN)
    assert "first day of winter" in eve and "## Season" in eve
    assert "first day of spring" in memory.journal_reminder({"season": "winter", "dayOfMonth": 28}, PLAN)
    assert memory.journal_reminder({}, PLAN) == ""
    assert memory.journal_reminder({"season": "spring", "dayOfMonth": "x"}, PLAN) == ""


def test_with_reminder_appends_only_when_there_is_one(tmp_path):
    state = {"season": "spring", "dayOfMonth": 7}
    assert memory.with_reminder("PROMPT", state, tmp_path).startswith("PROMPT\n\n")  # no plan file yet: reminder
    write(tmp_path / "plan.md", PLAN)
    assert "Monday" in memory.with_reminder("PROMPT", state, tmp_path)
    assert memory.with_reminder("PROMPT", {"season": "spring", "dayOfMonth": 3}, tmp_path) == "PROMPT"
