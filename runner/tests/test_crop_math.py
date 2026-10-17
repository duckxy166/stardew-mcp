"""Tests for the crop planning math behind the `crops` and `item_info` tools.

`CropMath` and `RecipeMath` in mod/StardewMCP/Agent/KnowledgeMath.cs have no game types, so the
functions below are a line-for-line Python port of them and the tests run with no game, no network
and no CLI. If you change the C# math, change this port and the expectations with it.

Run: python -m pytest -q runner/tests   (or: python -m unittest discover -s runner/tests)

The growth and price numbers below are test inputs, not claims about the game's data.
"""

from __future__ import annotations

import struct
import unittest
from typing import NamedTuple, Sequence

# ---------------------------------------------------------------------- port of KnowledgeMath.cs

DAYS_PER_SEASON = 28
SEASON_NAMES = ("spring", "summer", "fall", "winter")


class CropPlan(NamedTuple):
    in_season: bool
    days_available: int
    matures: bool
    last_plant_day: int
    harvests: int
    plantings: int
    tile_days: int


def season_index(name: str | None) -> int:
    return {"spring": 0, "summer": 1, "fall": 2, "autumn": 2, "winter": 3}.get((name or "").strip().lower(), -1)


def season_name(index: int) -> str:
    return SEASON_NAMES[index % 4]


def growth_days(days_in_phase: Sequence[int]) -> int:
    return sum(d for d in days_in_phase if d > 0)


def days_available(season: int, start_day: int, grows_in: Sequence[bool]) -> int:
    if season < 0 or season > 3 or len(grows_in) < 4 or not grows_in[season]:
        return 0
    days = DAYS_PER_SEASON - start_day
    s = season + 1
    while s < 4 and grows_in[s]:
        days += DAYS_PER_SEASON
        s += 1
    return max(days, 0)


def plan(season: int, start_day: int, grows_in: Sequence[bool], growth: int, regrow: int) -> CropPlan:
    avail = days_available(season, start_day, grows_in)
    in_season = 0 <= season <= 3 and len(grows_in) >= 4 and grows_in[season]
    if not in_season or growth <= 0:
        return CropPlan(in_season, avail, False, 0, 0, 0, 0)
    last_plant = start_day + (avail - growth)
    if growth > avail:
        return CropPlan(True, avail, False, last_plant, 0, 0, 0)
    if regrow > 0:
        harvests = 1 + (avail - growth) // regrow
        return CropPlan(True, avail, True, last_plant, harvests, 1, growth + (harvests - 1) * regrow)
    plantings = avail // growth
    return CropPlan(True, avail, True, last_plant, 1, plantings, plantings * growth)


def advance(season: int, day: int, days: int) -> tuple[int, int]:
    total = max(day + days, 1)
    return season + (total - 1) // DAYS_PER_SEASON, (total - 1) % DAYS_PER_SEASON + 1


def expected_yield(min_stack: int, max_stack: int, extra_chance: float) -> float:
    lo = max(1, min_stack)
    hi = max(lo, max_stack)
    return (lo + hi) / 2.0 + max(0.0, extra_chance)


def net_gold(p: CropPlan, yield_per_harvest: float, sell_price: int, seed_cost: int) -> float:
    return p.plantings * (p.harvests * yield_per_harvest * sell_price - seed_cost)


def gold_per_tile_per_day(p: CropPlan, net: float) -> float:
    return net / p.tile_days if p.tile_days > 0 else 0.0


def _f32(x: float) -> float:
    return struct.unpack("f", struct.pack("f", x))[0]


def price_at_quality(base_price: int, quality: int) -> int:
    """C# `(int)(basePrice * (1f + quality * 0.25f))`, with every step rounded to float32 like C#."""
    step = _f32(quality * _f32(0.25))
    return int(_f32(base_price * _f32(1.0 + step)))


def shop_price(listed_price: int, object_base_price: int) -> tuple[int, bool]:
    return (listed_price, False) if listed_price > 0 else (max(object_base_price, 0) * 2, True)


def qualify(item_id: str | None) -> str:
    item_id = (item_id or "").strip()
    return item_id if item_id.startswith("(") else "(O)" + item_id


def unqualify(item_id: str | None) -> str:
    item_id = (item_id or "").strip()
    return item_id[3:] if item_id[:3].upper() == "(O)" else item_id


def same_item(a: str | None, b: str | None) -> bool:
    return bool((a or "").strip()) and bool((b or "").strip()) and qualify(a).lower() == qualify(b).lower()


def ingredient_count(recipe: str | None, item_id: str, category: int) -> tuple[int, bool]:
    if not recipe:
        return (0, False)
    tokens = recipe.split("/")[0].split()
    via_category = 0
    wanted = unqualify(item_id)
    for i in range(0, len(tokens) - 1, 2):
        try:
            n = int(tokens[i + 1])
        except ValueError:
            continue
        if n <= 0:
            continue
        ident = unqualify(tokens[i])
        if ident.lower() == wanted.lower():
            return (n, False)
        if category != 0 and ident == str(category) and via_category == 0:
            via_category = n
    return (via_category, via_category > 0)


# ---------------------------------------------------------------------- tests

SPRING, SUMMER, FALL, WINTER = 0, 1, 2, 3
SPRING_ONLY = [True, False, False, False]
SUMMER_FALL = [False, True, True, False]


class SeasonTests(unittest.TestCase):
    def test_season_names(self) -> None:
        self.assertEqual([season_index(n) for n in ("spring", "Summer", " FALL ", "autumn", "winter")], [0, 1, 2, 2, 3])
        self.assertEqual(season_index("monsoon"), -1)
        self.assertEqual(season_index(None), -1)
        self.assertEqual(season_name(2), "fall")

    def test_growth_days_is_the_sum_of_the_phases(self) -> None:
        self.assertEqual(growth_days([1, 2, 4, 4, 1]), 12)
        self.assertEqual(growth_days([1, 1, 1, 1]), 4)
        self.assertEqual(growth_days([]), 0)
        self.assertEqual(growth_days([2, -1, 3]), 5)  # bad data is ignored, not subtracted


class MaturityBoundaryTests(unittest.TestCase):
    """A crop planted and watered on day d is ready on day d + growth and must be ready by day 28."""

    def test_twelve_days_left_is_the_boundary_for_a_12_day_crop(self) -> None:
        # The issue's 12 / 13 example: a 12-day crop still matures, a 13-day crop does not.
        # 12 days left is spring 16 (28 - 16), not spring 20 as the issue text says.
        ok = plan(SPRING, 16, SPRING_ONLY, 12, 0)
        self.assertTrue(ok.matures)
        self.assertEqual(ok.days_available, 12)
        self.assertEqual(ok.last_plant_day, 16)
        self.assertEqual(ok.harvests, 1)
        self.assertEqual(advance(SPRING, 16, 12), (SPRING, 28))  # ready on the last day of the season

        late = plan(SPRING, 16, SPRING_ONLY, 13, 0)
        self.assertFalse(late.matures)
        self.assertEqual(late.last_plant_day, 15)
        self.assertEqual((late.harvests, late.plantings, late.tile_days), (0, 0, 0))

    def test_one_day_later_the_12_day_crop_is_too_late(self) -> None:
        self.assertFalse(plan(SPRING, 17, SPRING_ONLY, 12, 0).matures)

    def test_spring_20_has_eight_days_left(self) -> None:
        # The issue's manual test is written for "Spring 20". 28 - 20 = 8 days remain there,
        # so only short crops mature: Cauliflower (12) does not, Parsnip (4) and Kale (6) do.
        self.assertEqual(days_available(SPRING, 20, SPRING_ONLY), 8)
        self.assertFalse(plan(SPRING, 20, SPRING_ONLY, 12, 0).matures)
        self.assertTrue(plan(SPRING, 20, SPRING_ONLY, 4, 0).matures)
        self.assertTrue(plan(SPRING, 20, SPRING_ONLY, 6, 0).matures)
        self.assertFalse(plan(SPRING, 20, SPRING_ONLY, 10, 3).matures)

    def test_planting_on_the_last_day_never_matures(self) -> None:
        self.assertEqual(days_available(SPRING, 28, SPRING_ONLY), 0)
        self.assertFalse(plan(SPRING, 28, SPRING_ONLY, 1, 0).matures)

    def test_out_of_season_crop_has_no_days(self) -> None:
        p = plan(SUMMER, 1, SPRING_ONLY, 4, 0)
        self.assertFalse(p.in_season)
        self.assertFalse(p.matures)
        self.assertEqual(p.days_available, 0)

    def test_bad_growth_data_gives_no_plan(self) -> None:
        self.assertFalse(plan(SPRING, 1, SPRING_ONLY, 0, 0).matures)


class HarvestCountTests(unittest.TestCase):
    def test_regrowing_crop_counts_every_regrow(self) -> None:
        # 10 days to grow, then every 3 days, from spring 1: ready 11, 14, 17, 20, 23, 26.
        p = plan(SPRING, 1, SPRING_ONLY, 10, 3)
        self.assertEqual(p.days_available, 27)
        self.assertEqual(p.harvests, 6)
        self.assertEqual(p.plantings, 1)
        self.assertEqual(p.tile_days, 10 + 5 * 3)

    def test_regrowing_crop_planted_late_gets_fewer_harvests(self) -> None:
        # From spring 16 a 10-day / 3-day crop is ready on 26, and 29 is too late: one harvest.
        p = plan(SPRING, 16, SPRING_ONLY, 10, 3)
        self.assertTrue(p.matures)
        self.assertEqual(p.harvests, 1)
        self.assertEqual(p.last_plant_day, 18)

    def test_single_harvest_crop_replants_back_to_back(self) -> None:
        p = plan(SPRING, 1, SPRING_ONLY, 4, 0)
        self.assertEqual(p.days_available, 27)
        self.assertEqual((p.harvests, p.plantings, p.tile_days), (1, 6, 24))

    def test_first_harvest_day_rolls_into_the_next_season(self) -> None:
        self.assertEqual(advance(SPRING, 20, 12), (SUMMER, 4))
        self.assertEqual(advance(SPRING, 1, 4), (SPRING, 5))
        self.assertEqual(advance(FALL, 20, 12), (WINTER, 4))
        self.assertEqual(advance(WINTER, 20, 12), (4, 4))  # season 4 = next year's spring


class MultiSeasonTests(unittest.TestCase):
    def test_crop_that_grows_in_two_seasons_keeps_growing_across_the_change(self) -> None:
        # A 14-day summer-and-fall crop planted on summer 20 has 8 + 28 days.
        p = plan(SUMMER, 20, SUMMER_FALL, 14, 0)
        self.assertEqual(p.days_available, 36)
        self.assertTrue(p.matures)
        self.assertGreaterEqual(p.last_plant_day, DAYS_PER_SEASON)  # any day in summer works
        self.assertEqual(advance(SUMMER, 20, 14), (FALL, 6))

    def test_same_crop_in_its_last_season_is_limited_to_that_season(self) -> None:
        p = plan(FALL, 20, SUMMER_FALL, 14, 0)
        self.assertEqual(p.days_available, 8)
        self.assertFalse(p.matures)

    def test_growth_does_not_continue_into_a_season_the_crop_skips(self) -> None:
        self.assertEqual(days_available(SPRING, 1, [True, False, True, False]), 27)

    def test_growth_does_not_wrap_past_winter(self) -> None:
        self.assertEqual(days_available(WINTER, 1, [True, True, True, True]), 27)


class MoneyTests(unittest.TestCase):
    def test_price_by_quality(self) -> None:
        # silver x1.25, gold x1.5, iridium x2, rounded down
        self.assertEqual([price_at_quality(35, q) for q in (0, 1, 2, 4)], [35, 43, 52, 70])
        self.assertEqual([price_at_quality(100, q) for q in (0, 1, 2, 4)], [100, 125, 150, 200])

    def test_expected_yield(self) -> None:
        self.assertEqual(expected_yield(1, 1, 0.0), 1.0)
        self.assertEqual(expected_yield(3, 3, 0.0), 3.0)
        self.assertEqual(expected_yield(1, 3, 0.0), 2.0)
        self.assertAlmostEqual(expected_yield(1, 1, 0.2), 1.2)
        self.assertEqual(expected_yield(0, 0, 0.0), 1.0)  # bad data still gives one item

    def test_profit_of_a_single_harvest_crop_is_independent_of_how_often_it_replants(self) -> None:
        p = plan(SPRING, 1, SPRING_ONLY, 4, 0)  # 6 plantings
        net = net_gold(p, 1.0, 35, 20)
        self.assertEqual(net, 6 * (35 - 20))
        self.assertAlmostEqual(gold_per_tile_per_day(p, net), (35 - 20) / 4)

    def test_profit_of_a_regrowing_crop_pays_for_the_seed_once(self) -> None:
        p = plan(SPRING, 1, SPRING_ONLY, 10, 3)  # 6 harvests, 25 tile days
        net = net_gold(p, 1.0, 40, 60)
        self.assertEqual(net, 6 * 40 - 60)
        self.assertAlmostEqual(gold_per_tile_per_day(p, net), (6 * 40 - 60) / 25)

    def test_crop_that_will_not_mature_earns_nothing(self) -> None:
        p = plan(SPRING, 20, SPRING_ONLY, 12, 0)
        self.assertEqual(net_gold(p, 1.0, 175, 80), 0)
        self.assertEqual(gold_per_tile_per_day(p, 0), 0.0)

    def test_stack_of_three_triples_the_income(self) -> None:
        p = plan(SUMMER, 1, [False, True, False, False], 13, 4)
        self.assertEqual(net_gold(p, 3.0, 50, 0), 3.0 * p.harvests * 50)

    def test_shop_price(self) -> None:
        self.assertEqual(shop_price(20, 10), (20, False))
        self.assertEqual(shop_price(-1, 10), (20, True))  # no listed price: twice the base value
        self.assertEqual(shop_price(0, 0), (0, True))


class ItemIdTests(unittest.TestCase):
    def test_qualify_and_unqualify(self) -> None:
        self.assertEqual(qualify("24"), "(O)24")
        self.assertEqual(qualify("(O)24"), "(O)24")
        self.assertEqual(qualify("(BC)13"), "(BC)13")
        self.assertEqual(unqualify("(O)24"), "24")
        self.assertEqual(unqualify("(o)24"), "24")
        self.assertEqual(unqualify("24"), "24")
        self.assertEqual(unqualify("(BC)13"), "(BC)13")  # not an object: keeps its type
        self.assertEqual(unqualify(None), "")

    def test_same_item_ignores_the_object_prefix(self) -> None:
        self.assertTrue(same_item("(O)472", "472"))
        self.assertTrue(same_item("472", "(O)472"))
        self.assertFalse(same_item("(O)472", "(O)473"))
        self.assertFalse(same_item("(BC)13", "(O)13"))
        self.assertFalse(same_item(None, "472"))
        self.assertFalse(same_item("", ""))


class RecipeTests(unittest.TestCase):
    # Same shape as the lines of Data/CraftingRecipes and Data/CookingRecipes ("ingredients/.../...").
    CRAFT = "388 2 390 1/Home/322/false/default/"
    COOK = "-5 1 245 1/10 10/194/default/"

    def test_direct_ingredient(self) -> None:
        self.assertEqual(ingredient_count(self.CRAFT, "388", 0), (2, False))
        self.assertEqual(ingredient_count(self.CRAFT, "(O)390", 0), (1, False))
        self.assertEqual(ingredient_count(self.CRAFT, "389", 0), (0, False))

    def test_category_ingredient(self) -> None:
        # -5 means any egg: an item of category -5 is used "via its category"
        self.assertEqual(ingredient_count(self.COOK, "176", -5), (1, True))
        self.assertEqual(ingredient_count(self.COOK, "245", -5), (1, False))  # the direct match wins
        self.assertEqual(ingredient_count(self.COOK, "176", 0), (0, False))
        self.assertEqual(ingredient_count(self.COOK, "176", -6), (0, False))

    def test_item_id_is_not_confused_with_a_longer_id(self) -> None:
        self.assertEqual(ingredient_count("1388 5", "388", 0), (0, False))

    def test_broken_lines_are_ignored(self) -> None:
        for bad in (None, "", "/Home/1", "388", "388 x", "388 0", "388 -2"):
            self.assertEqual(ingredient_count(bad, "388", 0), (0, False), bad)


if __name__ == "__main__":
    unittest.main()
