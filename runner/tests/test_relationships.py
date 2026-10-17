"""Tests for runner/relationships.py (relationships.md from the mod's /api/relationships report, issue #9).

No game, network or claude CLI: the report is a plain dict and the fetch callable is a fake.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

import relationships as rel

RUNNER = Path(__file__).resolve().parent.parent


def villager(name, points=0, met=True, birthday=None, **extra):
    v = {"name": name, "displayName": name, "location": "Town", "x": 10, "y": 20, "met": met,
         "points": points, "hearts": points // 250, "talkedToday": False, "giftsToday": 0, "giftsThisWeek": 0,
         "status": "friendly", "birthday": birthday or {"season": "", "day": 0},
         "tastes": {"loved": [], "liked": [], "neutral": [], "disliked": [], "hated": []}}
    v.update(extra)
    return v


def player(villagers, seat="p1", day=12, season="spring", day_of_month=12, known=0):
    return {"player": seat, "name": "Mia", "day": day, "season": season, "dayOfMonth": day_of_month,
            "year": 1, "knownTastes": known, "villagers": villagers}


def report(*players, ready=True):
    return {"ready": ready, "players": list(players)}


# ------------------------------------------------------------------ days_until

def test_days_until_same_day_is_zero():
    assert rel.days_until("spring", 12, "spring", 12) == 0


def test_days_until_later_this_season_and_later_seasons():
    assert rel.days_until("spring", 15, "spring", 12) == 3
    assert rel.days_until("summer", 1, "spring", 28) == 1
    assert rel.days_until("winter", 28, "spring", 1) == 111


def test_days_until_wraps_to_next_year():
    assert rel.days_until("spring", 1, "winter", 28) == 1
    assert rel.days_until("spring", 11, "spring", 12) == 111  # just missed it: next year


@pytest.mark.parametrize("args", [
    ("", 5, "spring", 1), ("spring", 0, "spring", 1), ("spring", 29, "spring", 1),
    ("spring", 5, "", 1), ("spring", 5, "monsoon", 1), ("spring", 5, "spring", 0),
])
def test_days_until_invalid_is_none(args):
    assert rel.days_until(*args) is None


def test_days_until_accepts_autumn_and_any_case():
    assert rel.days_until("Autumn", 3, "FALL", 1) == 2


# ------------------------------------------------------------------ file names

def test_file_name_p1_is_plain_and_others_get_the_seat():
    assert rel.file_name("p1") == "relationships.md"
    assert rel.file_name("") == "relationships.md"
    assert rel.file_name(None) == "relationships.md"
    assert rel.file_name("p2") == "relationships_p2.md"


def test_file_name_cannot_escape_the_folder():
    name = rel.file_name("../../etc/passwd")
    assert "/" not in name and ".." not in name


# ------------------------------------------------------------------ render

def test_render_without_villagers_is_the_hint():
    assert rel.render(player([])).strip() == rel.EMPTY


def test_render_header_and_friendships_sorted_by_points():
    text = rel.render(player([villager("Abigail", 500), villager("Pierre", 1250), villager("Leah", 0)], known=3))
    assert text.startswith("# Relationships (farm day 12, spring 12)")
    assert "You know 3 gift taste(s)" in text
    assert text.index("**Pierre**") < text.index("**Abigail**") < text.index("**Leah**")
    assert "**Pierre**: 5 hearts (1250 pts)" in text
    assert "**Abigail**: 2 hearts (500 pts)" in text
    assert "**Leah**: 0 hearts (0 pts)" in text


def test_render_singular_heart():
    assert "1 heart (250 pts)" in rel.render(player([villager("Sam", 250)]))


def test_render_birthdays_within_a_week_sorted_with_today_first():
    vs = [villager("Lewis", 100, birthday={"season": "spring", "day": 7}),       # passed -> next year
          villager("Sam", 100, birthday={"season": "spring", "day": 17}),         # in 5 days
          villager("Abigail", 100, birthday={"season": "spring", "day": 12}),     # today
          villager("Haley", 100, birthday={"season": "spring", "day": 13}),       # tomorrow
          villager("Elliott", 100, birthday={"season": "summer", "day": 1})]      # too far
    text = rel.render(player(vs))
    section = text.split("## Birthdays")[1].split("## Friendships")[0]
    assert section.index("Abigail") < section.index("Haley") < section.index("Sam")
    assert "Abigail: spring 12 (TODAY)" in section
    assert "Haley: spring 13 (in 1 day)" in section
    assert "Sam: spring 17 (in 5 days)" in section
    assert "Lewis" not in section and "Elliott" not in section


def test_render_birthday_wraps_over_the_year_end():
    vs = [villager("Penny", 100, birthday={"season": "fall", "day": 2})]
    text = rel.render(player(vs, season="winter", day_of_month=27))  # fall 2 is far away
    assert "## Birthdays" not in text
    vs = [villager("Penny", 100, birthday={"season": "spring", "day": 2})]
    text = rel.render(player(vs, season="winter", day_of_month=27))  # 3 days away across the new year
    assert "Penny: spring 2 (in 3 days)" in text


def test_render_marks_an_unmet_birthday_villager():
    vs = [villager("Shane", 0, met=False, birthday={"season": "spring", "day": 13})]
    text = rel.render(player(vs))
    assert "Shane: spring 13 (in 1 day) - not met yet" in text


def test_render_lists_unmet_villagers_separately_and_not_as_friends():
    text = rel.render(player([villager("Abigail", 250), villager("Shane", 0, met=False), villager("Alex", 0, met=False)]))
    assert "## Not met yet\nAlex, Shane" in text
    friends = text.split("## Friendships")[1].split("## Not met yet")[0]
    assert "Abigail" in friends and "Shane" not in friends


def test_render_shows_status_and_last_gift_and_only_known_tastes():
    v = villager("Emily", 1500, status="dating",
                 lastGift={"day": 9, "item": "Daffodil", "reaction": "likes it", "points": 45},
                 tastes={"loved": ["Amethyst"], "liked": ["Daffodil"], "neutral": ["Egg"],
                         "disliked": ["Clay"], "hated": ["Fish Taco"]})
    line = [ln for ln in rel.render(player([v])).splitlines() if "**Emily**" in ln][0]
    assert "dating" in line
    assert "last gift Daffodil on day 9: likes it (+45)" in line
    assert "Loves: Amethyst" in line and "Likes: Daffodil" in line
    assert "Dislikes: Clay" in line and "Hates: Fish Taco" in line
    assert "Egg" not in line  # neutral items are not worth a line


def test_render_negative_gift_points_keep_their_sign():
    v = villager("Emily", 100, lastGift={"day": 3, "item": "Clay", "reaction": "dislikes it", "points": -20})
    assert "(-20)" in rel.render(player([v]))


def test_render_does_not_show_a_plain_friendly_status():
    assert "friendly" not in rel.render(player([villager("Sam", 100)]))


def test_render_survives_missing_fields():
    text = rel.render({"villagers": [{"name": "Ghost"}], "day": 1})
    assert "## Not met yet\nGhost" in text  # no "met" flag counts as not met
    assert "## Friendships" not in text


def test_render_caps_a_huge_friend_list():
    vs = [villager(f"V{i:02d}", 10 * i) for i in range(rel.MAX_FRIENDS + 5)]
    text = rel.render(player(vs))
    assert f"... and 5 more" in text
    assert text.count("**V") == rel.MAX_FRIENDS


# ------------------------------------------------------------------ render_all / write_files

def test_render_all_not_ready_is_empty():
    assert rel.render_all(report(player([villager("Sam", 1)]), ready=False)) == {}
    assert rel.render_all({}) == {}
    assert rel.render_all(None) == {}


def test_render_all_one_file_per_seat():
    out = rel.render_all(report(player([villager("Sam", 1)], seat="p1"), player([villager("Leah", 1)], seat="p2")))
    assert set(out) == {"relationships.md", "relationships_p2.md"}
    assert "Sam" in out["relationships.md"] and "Leah" in out["relationships_p2.md"]


def test_write_files_creates_the_folder_and_files(tmp_path):
    log_dir = tmp_path / "runs" / "logs"
    written = rel.write_files(log_dir, report(player([villager("Sam", 300)])))
    assert written == [log_dir / "relationships.md"]
    assert "**Sam**" in (log_dir / "relationships.md").read_text(encoding="utf-8")


def test_write_files_overwrites_the_day_before(tmp_path):
    rel.write_files(tmp_path, report(player([villager("Sam", 300)])))
    rel.write_files(tmp_path, report(player([villager("Sam", 550)])))
    text = (tmp_path / "relationships.md").read_text(encoding="utf-8")
    assert "550 pts" in text and "300 pts" not in text


# ------------------------------------------------------------------ update (fetch)

def test_update_writes_what_the_fetch_returns(tmp_path):
    paths = rel.update(tmp_path, lambda: report(player([villager("Sam", 300)])))
    assert paths == [tmp_path / "relationships.md"]


def test_update_game_not_ready_writes_nothing_and_keeps_the_old_file(tmp_path):
    (tmp_path / "relationships.md").write_text("old\n", encoding="utf-8")
    assert rel.update(tmp_path, lambda: report(ready=False)) == []
    assert (tmp_path / "relationships.md").read_text(encoding="utf-8") == "old\n"


def test_update_swallows_a_fetch_error_and_prints_it(tmp_path, capsys):
    def boom():
        raise OSError("connection refused")

    assert rel.update(tmp_path, boom) == []
    assert "connection refused" in capsys.readouterr().out
    assert not (tmp_path / "relationships.md").exists()


def test_update_swallows_a_malformed_report(tmp_path):
    assert rel.update(tmp_path, lambda: {"ready": True, "players": [{"villagers": [{"birthday": {"day": "x"}}]}]}) == []


# ------------------------------------------------------------------ for_prompt

def test_for_prompt_without_a_file_is_the_hint(tmp_path):
    assert rel.for_prompt(tmp_path) == rel.EMPTY


def test_for_prompt_empty_file_is_the_hint(tmp_path):
    (tmp_path / "relationships.md").write_text("  \n", encoding="utf-8")
    assert rel.for_prompt(tmp_path) == rel.EMPTY


def test_for_prompt_returns_the_seats_file(tmp_path):
    rel.write_files(tmp_path, report(player([villager("Sam", 300)]), player([villager("Leah", 500)], seat="p2")))
    assert "Sam" in rel.for_prompt(tmp_path)
    assert "Leah" in rel.for_prompt(tmp_path, "p2")
    assert "Leah" not in rel.for_prompt(tmp_path, "p1")


# ------------------------------------------------------------------ the system prompt and run_days.py

def test_system_prompt_has_exactly_one_relationships_placeholder():
    template = (RUNNER / "system_prompt.md").read_text(encoding="utf-8")
    assert template.count("{RELATIONSHIPS}") == 1
    assert template.index("{RELATIONSHIPS}") < template.index("{MEMORY}")


def test_run_days_replaces_every_placeholder_of_the_template():
    template = (RUNNER / "system_prompt.md").read_text(encoding="utf-8")
    source = (RUNNER / "run_days.py").read_text(encoding="utf-8")
    lang_source = (RUNNER / "language.py").read_text(encoding="utf-8")
    mem_source = (RUNNER / "memory.py").read_text(encoding="utf-8")
    placeholders = set(re.findall(r"\{[A-Z_]+\}", template))
    assert "{RELATIONSHIPS}" in placeholders
    for name in placeholders:
        assert f'"{name}"' in source or f'"{name}"' in lang_source or name.strip("{}") in mem_source, f"never fills {name}"



def test_run_days_writes_the_file_before_building_the_prompt():
    source = (RUNNER / "run_days.py").read_text(encoding="utf-8")
    assert source.index("relationships.update(") < source.index("relationships.for_prompt(")
    assert '"/api/relationships"' in source


def test_prompt_with_a_generated_file_has_no_placeholder_left(tmp_path):
    rel.write_files(tmp_path, report(player([villager("Sam", 300)])))
    template = (RUNNER / "system_prompt.md").read_text(encoding="utf-8")
    prompt = template.replace("{RELATIONSHIPS}", rel.for_prompt(tmp_path))
    assert "{RELATIONSHIPS}" not in prompt and "**Sam**" in prompt
