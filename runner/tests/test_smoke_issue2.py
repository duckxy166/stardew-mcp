"""Pure-text helpers of runner/smoke.py added for #2 (backpack and tool-upgrade cases). No game needed."""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import smoke  # noqa: E402


def test_pack_slots_reads_the_pack_size():
    text = "Money 2000g · inventory (3/12 slots used):\n  slot 0: Axe x1 [tool]"
    assert smoke.pack_slots(text) == 12
    assert smoke.pack_slots("Money 5g · inventory (30/36 slots used):") == 36


def test_pack_slots_rejects_other_text():
    with pytest.raises(smoke.Smoke):
        smoke.pack_slots("No inventory here")


def test_parse_trade_listing_finds_the_first_gold_plus_trade_item_line():
    listing = (
        "Shop ClintUpgrade — you have 30000g\n"
        "  Copper Axe: 2000g + 5x Copper Bar\n"
        "  Copper Pickaxe: 2000g + 5x Copper Bar\n"
    )
    assert smoke.parse_trade_listing(listing) == ("Copper Axe", 2000, 5, "Copper Bar")


def test_parse_trade_listing_ignores_plain_prices_and_handles_stock():
    plain = "Shop SeedShop — you have 500g\n  Parsnip Seeds: 20g\n  Hashbrowns (Recipe): 50g [recipe: buying teaches it]\n"
    assert smoke.parse_trade_listing(plain) is None
    stocked = "Shop X — you have 1g\n  Iron Hoe: 5000g + 5x Iron Bar (stock 1)\n"
    assert smoke.parse_trade_listing(stocked) == ("Iron Hoe", 5000, 5, "Iron Bar")


def test_trade_items_have_object_ids():
    assert set(smoke.BAR_IDS) == {"Copper Bar", "Iron Bar", "Gold Bar", "Iridium Bar"}


def test_backpack_tile_label_is_found_in_look_output():
    look = (
        "Things in view (nearest first):\n"
        "  A Backpack upgrade to 24 slots (2000g; interact, then respond) x1: (4,19)\n"
        "  A OpenShop x1: (4,17)\n"
    )
    found = [(label, tiles) for label, tiles in smoke.parse_things(look, "A") if label.startswith("Backpack upgrade")]
    assert found == [("Backpack upgrade to 24 slots (2000g; interact, then respond)", [(4, 19)])]
