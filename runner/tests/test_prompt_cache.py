"""The system prompt must stay byte-identical for as long as possible so the prompt cache keeps hitting.

Claude Code caches the request from the start up to a marker, so any text that changes between parts or days
(memory, lessons, briefing) has to sit after everything that does not. These tests read the real template.
"""

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import language
import run_days

TEMPLATE = (Path(run_days.__file__).parent / "system_prompt.md").read_text(encoding="utf-8")
GOAL = "Build the best farm you can."


def render(template: str, memory: str, goal: str = GOAL) -> str:
    template = language.fill_prompt(template, "Thai")
    return template.replace("{GOAL}", goal).replace("{MEMORY}", memory)


def common_prefix_length(a: str, b: str) -> int:
    n = 0
    for x, y in zip(a, b):
        if x != y:
            break
        n += 1
    return n


def test_memory_is_the_last_thing_in_the_template():
    assert TEMPLATE.count("{MEMORY}") == 1
    assert TEMPLATE.rstrip().endswith("{MEMORY}")


def test_every_placeholder_but_the_goal_comes_after_the_memory_heading():
    heading = TEMPLATE.index("# Memory")
    for m in re.finditer(r"\{[A-Z_]+\}", TEMPLATE):
        if m.group() != "{GOAL}" and m.group() not in language.PLACEHOLDERS:
            assert m.start() > heading, f"{m.group()} changes from day to day, so it must come after '# Memory'"



def test_other_days_and_parts_share_the_whole_prompt_up_to_the_memory():
    day_1 = render(TEMPLATE, "(This is your first day. No diaries yet.)")
    day_9 = render(TEMPLATE, "# Day 8 — spring 8 Y1\n\nขายผักได้ 400g")
    cut = day_1.index("# Memory")
    assert common_prefix_length(day_1, day_9) >= cut
    # the same day's parts get the very same prompt
    assert render(TEMPLATE, "same") == render(TEMPLATE, "same")


def test_the_stable_part_is_most_of_the_prompt():
    prompt = render(TEMPLATE, "x" * 300)
    assert prompt.index("# Memory") > 0.8 * len(prompt)


def test_the_template_tells_the_ai_about_session_breaks_in_the_stable_part():
    stable = TEMPLATE.split("# Memory")[0]
    assert "Session break:" in stable and "kinds" in stable and "diff" in stable
