"""runner/language.py: the runner follows the mod's ThoughtLanguage. No game, network or claude CLI needed."""

import re
from pathlib import Path

import language

THAI = re.compile("[฀-๿]")
TEMPLATE = (Path(__file__).resolve().parent.parent / "system_prompt.md").read_text(encoding="utf-8")


def filled(lang):
    return language.fill_prompt(TEMPLATE, lang)


def test_resolve_precedence_and_defaults():
    assert language.resolve("English", {"thoughtLanguage": "Thai"}) == "English"
    assert language.resolve(None, {"thoughtLanguage": "english"}) == "English"
    assert language.resolve("", {"thoughtLanguage": " Japanese "}) == "Japanese"
    assert language.resolve(None, {}) == "Thai"          # older mod build without the field
    assert language.resolve(None, None) == "Thai"
    assert language.resolve("thai", None) == "Thai"


def test_describe_warns_on_mismatch_only():
    assert "WARNING" not in language.describe("Thai", {"thoughtLanguage": "Thai"})
    assert "WARNING" not in language.describe("Thai", {})
    assert "WARNING" in language.describe("English", {"thoughtLanguage": "Thai"})


def test_template_has_no_hardcoded_thai_and_only_known_placeholders():
    assert not THAI.search(TEMPLATE)
    for ph in language.PLACEHOLDERS:
        assert ph in TEMPLATE


def test_thai_system_prompt_keeps_the_old_wording():
    text = filled("Thai")
    assert text.startswith("You are **Claude Haiku 5.5**, playing Stardew Valley by yourself in a 100-day challenge. Thai YouTube viewers watch")
    assert "# Thai output (viewers read this)" in text
    assert "must be **in Thai**, natural and a little playful, first person (\"ผม\")." in text
    assert 'e.g. "ถางหญ้ารอบบ้านเพื่อเปิดพื้นที่ปลูกผัก".' in text
    assert 'reply with "จบวัน" and stop' in text
    assert not any(ph in text for ph in language.PLACEHOLDERS)


def test_thai_day_prompts_are_unchanged():
    assert language.first_prompt("Thai", 3) == (
        "วันนี้คือวันที่ 3 ของชาเลนจ์ 100 วัน เริ่มจาก status และ look แล้ว think แผนของวันเป็นภาษาไทย "
        "จากนั้นเล่นทั้งวันจนเข้านอนด้วย go_to_bed")
    assert language.continue_prompt("Thai", 3, "A\nB") == (
        "เล่นวันที่ 3 ต่อจากเดิม (session ก่อนหน้าหยุดก่อนเข้านอน) นี่คือสิ่งที่เพิ่งทำไป:\n"
        "A\nB\n\nเช็ก status แล้วเล่นต่อจนเข้านอนด้วย go_to_bed")


def test_english_has_no_thai_anywhere():
    for text in (filled("English"), language.first_prompt("English", 2), language.continue_prompt("English", 2, "x")):
        assert not THAI.search(text)
    assert "# English output (viewers read this)" in filled("English")
    assert "**in English**" in filled("English")
    assert "English" in language.first_prompt("English", 2)


def test_language_without_texts_names_the_language_and_has_no_thai():
    text = filled("Japanese")
    assert not THAI.search(text)
    assert "# Japanese output (viewers read this)" in text
    assert "**in Japanese**" in text
    assert not any(ph in text for ph in language.PLACEHOLDERS)
    assert "in Japanese" in language.first_prompt("Japanese", 1)
    assert "Japanese" in language.continue_prompt("Japanese", 1, "x")
    assert not THAI.search(language.first_prompt("Japanese", 1) + language.continue_prompt("Japanese", 1, "x"))


def test_goal_and_memory_stay_untouched_and_last():
    text = filled("English")
    assert "{GOAL}" in text and text.rstrip().endswith("{MEMORY}")
    # a goal or diary that happens to contain a language placeholder is not rewritten (fill happens first)
    out = text.replace("{GOAL}", "say {LANGUAGE}").replace("{MEMORY}", "{END_REPLY}")
    assert "say {LANGUAGE}" in out and out.rstrip().endswith("{END_REPLY}")
