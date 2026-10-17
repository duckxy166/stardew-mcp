"""Language of the AI's on-screen text for run_days.py.

The mod's ThoughtLanguage setting (config.json, default "Thai") decides what language `think`, action reasons and
the diary are written in. This module makes the runner follow it: it fills the language placeholders of
system_prompt.md and writes the per-day prompts. Thai and English have hand-written texts; any other language
gets the English texts with the language name filled in, so the model still writes in that language.
"""

from __future__ import annotations

DEFAULT_LANGUAGE = "Thai"

# Placeholders of system_prompt.md that this module fills ({GOAL} and {MEMORY} stay for run_days.py).
PLACEHOLDERS = ("{LANGUAGE}", "{FIRST_PERSON}", "{REASON_EXAMPLE}", "{END_REPLY}")

_THAI = {
    "name": "Thai",
    "first_person": 'first person ("ผม")',
    "reason_example": ', e.g. "ถางหญ้ารอบบ้านเพื่อเปิดพื้นที่ปลูกผัก"',
    "end_reply": '"จบวัน"',
}
_ENGLISH = {
    "name": "English",
    "first_person": 'first person ("I")',
    "reason_example": ', e.g. "Clear the grass around the house to make room for crops"',
    "end_reply": '"End of day"',
}
_PACKS = {"thai": _THAI, "th": _THAI, "english": _ENGLISH, "en": _ENGLISH}


def normalize(name) -> str:
    """The language as the prompts should name it: trimmed, blank means Thai, Thai/English spelled canonically."""
    text = str(name or "").strip()
    if not text:
        return DEFAULT_LANGUAGE
    pack = _PACKS.get(text.lower())
    return pack["name"] if pack else text


def resolve(explicit, state) -> str:
    """--language wins; otherwise the mod's thoughtLanguage from /api/state; otherwise Thai."""
    if str(explicit or "").strip():
        return normalize(explicit)
    return normalize((state or {}).get("thoughtLanguage"))


def describe(lang: str, state) -> str:
    """One line for the console, with a warning when --language disagrees with the mod's ThoughtLanguage."""
    line = f"Thought language: {lang}"
    mod = normalize((state or {}).get("thoughtLanguage")) if (state or {}).get("thoughtLanguage") else ""
    if mod and mod.lower() != lang.lower():
        line += (f"\n  WARNING: the mod asks the AI for {mod} (ThoughtLanguage in config.json) but the prompts use {lang}; "
                 "set both to the same language.")
    return line


def _pack(lang: str) -> dict:
    lang = normalize(lang)
    pack = _PACKS.get(lang.lower())
    if pack:
        return pack
    # no hand-written texts: English instructions that name the language, no example sentence in another language
    return {"name": lang, "first_person": "first person", "reason_example": "", "end_reply": '"End of day"'}


def fill_prompt(template: str, lang: str) -> str:
    """Fill the language placeholders of system_prompt.md (do this before {GOAL}/{MEMORY}, which may hold any text)."""
    pack = _pack(lang)
    return (template.replace("{LANGUAGE}", pack["name"])
            .replace("{FIRST_PERSON}", pack["first_person"])
            .replace("{REASON_EXAMPLE}", pack["reason_example"])
            .replace("{END_REPLY}", pack["end_reply"]))


def first_prompt(lang: str, day: int) -> str:
    """First message of a day."""
    lang = normalize(lang)
    if lang.lower() in ("thai", "th"):
        return (f"วันนี้คือวันที่ {day} ของชาเลนจ์ 100 วัน เริ่มจาก status และ look แล้ว think แผนของวันเป็นภาษาไทย "
                "จากนั้นเล่นทั้งวันจนเข้านอนด้วย go_to_bed")
    return (f"Today is day {day} of the 100-day challenge. Start with status and look, then think your plan for the day "
            f"in {_pack(lang)['name']}. Then play the whole day until you go to bed with go_to_bed")


def continue_prompt(lang: str, day: int, recent: str) -> str:
    """Message of a later session of the same day (the previous one stopped before bed)."""
    lang = normalize(lang)
    if lang.lower() in ("thai", "th"):
        return (f"เล่นวันที่ {day} ต่อจากเดิม (session ก่อนหน้าหยุดก่อนเข้านอน) นี่คือสิ่งที่เพิ่งทำไป:\n"
                f"{recent}\n\nเช็ก status แล้วเล่นต่อจนเข้านอนด้วย go_to_bed")
    return (f"Continue day {day} where you left off (the previous session stopped before bed). Here is what you just did:\n"
            f"{recent}\n\nCheck status, then keep playing until you go to bed with go_to_bed. "
            f"Keep thinking and giving reasons in {_pack(lang)['name']}.")
