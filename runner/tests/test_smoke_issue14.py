"""Pure-text helpers of runner/smoke.py added for #14 (cook, quests, geodes, trash, TV cases). No game needed."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import smoke  # noqa: E402


COOK_LISTING = (
    "Cooking recipes you know (3):\n"
    "Cookable now:\n"
    "  Fried Egg: 1 any egg [you can cook 4]\n"
    "  Salad (makes 2): 1 Leek, 1 Dandelion, 1 Vinegar [you can cook 1]\n"
    "Missing ingredients:\n"
    "  Omelet: 1 any egg, 1 any milk\n"
    "Counts include the fridge."
)


def test_parse_cookable_reads_only_the_dishes_that_can_be_cooked():
    assert smoke.parse_cookable(COOK_LISTING) == {"Fried Egg": 4, "Salad": 1}


def test_parse_cookable_ignores_a_crafting_listing():
    assert smoke.parse_cookable("Craftable now:\n  Chest: 50 Wood [you can craft 3]") == {}


QUESTS = (
    "Quests, spring 12:\n"
    "Your journal (2):\n"
    "  1. [Help Wanted] Lumber - asked by Pierre\n"
    "     \"I need wood.\"\n"
    "     2 days left; reward 300g\n"
    "  2. [Story] Introductions\n"
    "Help Wanted board (Town), today: [Help Wanted] Fish Delivery - asked by Willy - still FREE. First come, first served: accept_quest walks to the board and takes it.\n"
    "     \"Catch me a carp.\"\n"
    "Special Orders (the team's, open for everyone):\n"
    "  [Special Order] Island Ingredients - asked by Caroline\n"
)


def test_parse_journal_lists_type_name_and_giver():
    assert smoke.parse_journal(QUESTS) == [
        {"type": "Help Wanted", "name": "Lumber", "who": "Pierre"},
        {"type": "Story", "name": "Introductions", "who": ""},
    ]


def test_parse_board_free():
    assert smoke.parse_board(QUESTS) == {"name": "Fish Delivery", "giver": "Willy", "state": "free", "by": ""}


def test_parse_board_taken_names_the_taker():
    text = "Help Wanted board (Town), today: [Help Wanted] Lumber - asked by Pierre - TAKEN by Gemini (p2). First come, first served: no other request today."
    assert smoke.parse_board(text) == {"name": "Lumber", "giver": "Pierre", "state": "taken", "by": "Gemini (p2)"}


def test_parse_board_mine_without_a_giver():
    text = "Help Wanted board (Town), today: [Help Wanted] Gathering - YOU took it."
    assert smoke.parse_board(text) == {"name": "Gathering", "giver": "", "state": "mine", "by": ""}


def test_parse_board_nothing_posted():
    assert smoke.parse_board("Help Wanted board (Town): nothing is posted today, or it could not be read.") is None


def test_parse_money_left():
    assert smoke.parse_money_left("Clint cracked 2x Geode for 50g. Found: 1x Quartz. 1950g left.") == 1950
    assert smoke.parse_money_left("nothing here") is None
