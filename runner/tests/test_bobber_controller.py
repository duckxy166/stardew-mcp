"""Tests and a simulation for the fishing minigame controller (mod/StardewMCP/Agent/BobberController.cs).

The C# side is game-free math (BobberMath, CastMath). This file mirrors it line for line in Python,
checks that the constants still match the C# source, and plays the controller against a model of the
BobberBar minigame, so the control law can be tuned without starting the game.

What the simulation is and is not: the bar physics (acceleration, in-bar test, wall bounce, catch
progress) follow the maintainer's decompile notes on issue #4. The fish movement is an approximation
written from memory of BobberBar.update, and the fish is kept inside [0, 524], the part of the track a
bar can actually cover (the real fish can sit at 532, 8 px below the lowest bar window, where no
controller can help). So the success rates show that the control law is sound, not what the game will
do: that needs the in-game test.

Run: python -m pytest runner/tests  (or: python -m unittest discover -s runner/tests)
"""

from __future__ import annotations

import os
import random
import re
import unittest

CS_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "mod", "StardewMCP", "Agent", "BobberController.cs")

# ---- mirror of BobberMath -------------------------------------------------------------------

TRACK_LENGTH = 568.0
FISH_MAX = 532.0
CENTER_OFFSET = 30.0
BASE_ACCEL = 0.25
IN_BAR_FACTOR = 0.6
TRAP_BOBBER_FACTOR = 0.3
LOOKAHEAD = 6.0
DEADBAND = 2.0
TREASURE_START_PROGRESS = 0.55
TREASURE_STOP_PROGRESS = 0.35


def clamp(v: float, lo: float, hi: float) -> float:
    return lo if v < lo else hi if v > hi else v


def max_bar_pos(h: float) -> float:
    return TRACK_LENGTH - h


def fish_in_bar(fish: float, bar: float, h: float) -> bool:
    return fish + 12.0 <= bar - 32.0 + h and fish - 16.0 >= bar - 32.0


def centered_bar_pos(fish: float, h: float) -> float:
    return clamp(fish - h / 2.0 + CENTER_OFFSET, 0.0, max_bar_pos(h))


def accel(in_bar: bool, trap: bool) -> float:
    return BASE_ACCEL * ((TRAP_BOBBER_FACTOR if trap else IN_BAR_FACTOR) if in_bar else 1.0)


def predict_fish(fish: float, speed: float) -> float:
    return clamp(fish + speed * LOOKAHEAD, 0.0, FISH_MAX)


def rest_position(bar: float, speed: float, a: float) -> float:
    return bar + speed * abs(speed) / (2.0 * a)


def should_press(bar: float, speed: float, h: float, aim_fish: float, in_bar: bool, trap: bool, was_pressing: bool) -> bool:
    goal = centered_bar_pos(aim_fish, h)
    error = rest_position(bar, speed, accel(in_bar, trap)) - goal
    if abs(error) < DEADBAND:
        return was_pressing
    return error > 0.0


def chase_treasure(has_treasure: bool, appear_timer: float, caught: bool, progress: float, was_chasing: bool) -> bool:
    if not has_treasure or caught or appear_timer > 0.0:
        return False
    return progress > (TREASURE_STOP_PROGRESS if was_chasing else TREASURE_START_PROGRESS)


# ---- mirror of CastMath ---------------------------------------------------------------------

DX = (0, 1, 0, -1)
DY = (-1, 0, 1, 0)
LANDING_OFFSET_TILES = 0.25


def reach_tiles(direction: int, added_distance: int) -> int:
    return added_distance + (4 if direction in (1, 3) else 3)


def added_distance_for_level(level: int) -> int:
    return 4 if level >= 15 else 3 if level >= 8 else 2 if level >= 4 else 1 if level >= 1 else 0


def run_length(x: int, y: int, direction: int, max_scan: int, fishable) -> int:
    run = 0
    for i in range(1, max_scan + 1):
        if not fishable(x + DX[direction] * i, y + DY[direction] * i):
            break
        run += 1
    return run


def aim_tiles(run: int, reach: int) -> int:
    if run < 2 or reach < 2:
        return 0
    far = min(reach, run - 1 if run >= 3 else run)
    return max(2, far)


def power_for(tiles: int, reach: float) -> float:
    return clamp((tiles + LANDING_OFFSET_TILES) / reach, 0.05, 1.0)


# ---- the minigame model ---------------------------------------------------------------------

FISH_TOP = 524.0  # lowest fish position a bar can still cover


class BobberBarSim:
    """BobberBar.update as far as the controller cares: bar physics (exact) and fish motion (approximate)."""

    def __init__(self, rng: random.Random, difficulty: float, motion: int, bar_height: float, trap: bool = False):
        self.r = rng
        self.difficulty = difficulty
        self.motion = motion  # 0 mixed, 1 dart, 2 smooth, 3 sinker, 4 floater
        self.h = bar_height
        self.trap = trap
        self.fish = 508.0
        self.fish_speed = 0.0
        self.fish_target = -1.0
        self.floater_sinker = 0.0
        self.bar = TRACK_LENGTH - bar_height
        self.bar_speed = 0.0
        self.in_bar = False
        self.progress = 0.3
        self.in_bar_ticks = 0
        self.ticks = 0

    def _move_fish(self) -> None:
        r, d, m = self.r, self.difficulty, self.motion
        if r.random() < d * (20 if m == 2 else 1) / 4000.0 and (m != 2 or self.fish_target == -1.0):
            below = 548.0 - self.fish
            above = self.fish
            pct = min(99.0, d + r.randint(10, 44)) / 100.0
            self.fish_target = self.fish + r.randint(int(min(-above, below)), int(below)) * pct
        if m == 4:
            self.floater_sinker = max(self.floater_sinker - 0.01, -1.5)
        elif m == 3:
            self.floater_sinker = min(self.floater_sinker + 0.01, 1.5)
        if abs(self.fish - self.fish_target) > 3.0 and self.fish_target != -1.0:
            acc = (self.fish_target - self.fish) / (r.randint(10, 29) + (100.0 - min(100.0, d)))
            self.fish_speed += (acc - self.fish_speed) / 5.0
        elif m != 2 and r.random() < d / 2000.0:
            self.fish_target = self.fish + (r.randint(-100, -51) if r.random() < 0.5 else r.randint(50, 100))
        else:
            self.fish_target = -1.0
        if m == 1 and r.random() < d / 3000.0 and (self.fish_target == -1.0 or abs(self.fish_target - self.fish) < 50.0):
            self.fish_target += r.randint(-100, -51) if r.random() < 0.5 else r.randint(50, 100)
        self.fish = clamp(self.fish + self.fish_speed + (self.floater_sinker if m in (3, 4) else 0.0), 0.0, FISH_TOP)

    def step(self, pressed: bool) -> None:
        self._move_fish()
        g = -BASE_ACCEL if pressed else BASE_ACCEL
        top = max_bar_pos(self.h)
        if pressed and (self.bar == 0.0 or self.bar == top):
            self.bar_speed = 0.0
        if self.in_bar:
            g *= TRAP_BOBBER_FACTOR if self.trap else IN_BAR_FACTOR
        self.bar_speed += g
        self.bar += self.bar_speed
        if self.bar + self.h > TRACK_LENGTH:
            self.bar = top
            self.bar_speed = -self.bar_speed * 2.0 / 3.0
        elif self.bar < 0.0:
            self.bar = 0.0
            self.bar_speed = -self.bar_speed * 2.0 / 3.0
        self.in_bar = fish_in_bar(self.fish, self.bar, self.h)
        self.progress += 0.002 if self.in_bar else -0.003
        self.in_bar_ticks += self.in_bar
        self.ticks += 1


def play(seed: int, difficulty: float, motion: int, bar_height: float, trap: bool = False, max_ticks: int = 6000):
    """Run the controller until the catch bar fills (True) or empties (False). Returns (caught, ticks, toggles)."""
    sim = BobberBarSim(random.Random(seed), difficulty, motion, bar_height, trap)
    pressing = False
    toggles = 0
    while 0.0 < sim.progress < 1.0 and sim.ticks < max_ticks:
        aim = predict_fish(sim.fish, sim.fish_speed)
        now = should_press(sim.bar, sim.bar_speed, sim.h, aim, sim.in_bar, trap, pressing)
        toggles += now != pressing
        pressing = now
        sim.step(pressing)
    return sim.progress >= 1.0, sim.ticks, toggles


# ---- tests ----------------------------------------------------------------------------------


class ConstantsMatchCSharp(unittest.TestCase):
    """The Python mirror must not drift from the C# it mirrors."""

    def test_constants(self) -> None:
        with open(CS_FILE, encoding="utf-8") as f:
            source = f.read()
        found = {m.group(1): float(m.group(2)) for m in re.finditer(r"public const float (\w+) = (-?[\d.]+)f;", source)}
        expected = {
            "TrackLength": TRACK_LENGTH,
            "FishMax": FISH_MAX,
            "CenterOffset": CENTER_OFFSET,
            "BaseAccel": BASE_ACCEL,
            "InBarFactor": IN_BAR_FACTOR,
            "TrapBobberFactor": TRAP_BOBBER_FACTOR,
            "Lookahead": LOOKAHEAD,
            "Deadband": DEADBAND,
            "TreasureStartProgress": TREASURE_START_PROGRESS,
            "TreasureStopProgress": TREASURE_STOP_PROGRESS,
            "LandingOffsetTiles": LANDING_OFFSET_TILES,
        }
        for name, value in expected.items():
            self.assertIn(name, found, f"{name} missing from BobberController.cs")
            self.assertAlmostEqual(found[name], value, places=6, msg=name)


class BarMathTests(unittest.TestCase):
    def test_in_bar_window_edges(self) -> None:
        h, bar = 120.0, 200.0
        # window is [bar-16, bar+h-44]
        self.assertTrue(fish_in_bar(bar - 16.0, bar, h))
        self.assertFalse(fish_in_bar(bar - 16.5, bar, h))
        self.assertTrue(fish_in_bar(bar + h - 44.0, bar, h))
        self.assertFalse(fish_in_bar(bar + h - 43.5, bar, h))

    def test_centered_bar_puts_fish_mid_window(self) -> None:
        for h in (96.0, 120.0, 176.0):
            for fish in (100.0, 250.0, 400.0):
                bar = centered_bar_pos(fish, h)
                lo, hi = bar - 16.0, bar + h - 44.0
                self.assertTrue(fish_in_bar(fish, bar, h))
                self.assertAlmostEqual(fish - lo, hi - fish, places=4)

    def test_centered_bar_stays_on_track(self) -> None:
        self.assertEqual(centered_bar_pos(0.0, 120.0), 0.0)
        self.assertEqual(centered_bar_pos(530.0, 120.0), max_bar_pos(120.0))

    def test_accel_scaling(self) -> None:
        self.assertAlmostEqual(accel(False, False), 0.25)
        self.assertAlmostEqual(accel(True, False), 0.15)
        self.assertAlmostEqual(accel(True, True), 0.075)

    def test_press_direction(self) -> None:
        h = 120.0
        # bar well below the fish and at rest: press (accelerate up)
        self.assertTrue(should_press(400.0, 0.0, h, 100.0, False, False, False))
        # bar well above the fish and at rest: release (accelerate down)
        self.assertFalse(should_press(10.0, 0.0, h, 400.0, False, False, True))
        # moving up fast toward a close target: it would overshoot, so release to brake
        goal = centered_bar_pos(300.0, h)
        self.assertFalse(should_press(goal + 5.0, -6.0, h, 300.0, False, False, True))
        # moving down fast toward a close target: press to brake
        self.assertTrue(should_press(goal - 5.0, 6.0, h, 300.0, False, False, False))

    def test_deadband_keeps_previous_decision(self) -> None:
        h = 120.0
        goal = centered_bar_pos(300.0, h)
        self.assertTrue(should_press(goal + 0.5, 0.0, h, 300.0, True, False, True))
        self.assertFalse(should_press(goal + 0.5, 0.0, h, 300.0, True, False, False))

    def test_treasure_hysteresis(self) -> None:
        self.assertFalse(chase_treasure(False, 0.0, False, 0.9, False))
        self.assertFalse(chase_treasure(True, 100.0, False, 0.9, False))  # not appeared yet
        self.assertFalse(chase_treasure(True, 0.0, True, 0.9, True))  # already caught
        self.assertFalse(chase_treasure(True, 0.0, False, 0.5, False))  # not full enough to start
        self.assertTrue(chase_treasure(True, 0.0, False, 0.6, False))
        self.assertTrue(chase_treasure(True, 0.0, False, 0.5, True))  # keeps going while above the stop level
        self.assertFalse(chase_treasure(True, 0.0, False, 0.3, True))


class CastMathTests(unittest.TestCase):
    def test_reach(self) -> None:
        self.assertEqual(reach_tiles(1, 0), 4)
        self.assertEqual(reach_tiles(3, 2), 6)
        self.assertEqual(reach_tiles(0, 0), 3)
        self.assertEqual(reach_tiles(2, 4), 7)

    def test_added_distance_by_level(self) -> None:
        self.assertEqual([added_distance_for_level(n) for n in (0, 1, 3, 4, 7, 8, 14, 15)], [0, 1, 1, 2, 2, 3, 3, 4])

    def test_run_length(self) -> None:
        water = {(5, 3), (5, 4), (5, 5), (5, 6)}  # a vertical channel; stand at (5, 2) looking down
        fishable = lambda x, y: (x, y) in water  # noqa: E731
        self.assertEqual(run_length(5, 2, 2, 10, fishable), 4)
        self.assertEqual(run_length(5, 2, 2, 3, fishable), 3)
        self.assertEqual(run_length(5, 2, 0, 10, fishable), 0)

    def test_aim_keeps_margin_before_far_bank(self) -> None:
        self.assertEqual(aim_tiles(1, 5), 0)  # one tile of water: the minimum cast would overshoot
        self.assertEqual(aim_tiles(2, 5), 2)
        self.assertEqual(aim_tiles(3, 5), 2)
        self.assertEqual(aim_tiles(8, 5), 5)  # wide water: as far as the rod reaches
        self.assertEqual(aim_tiles(8, 3), 3)
        self.assertEqual(aim_tiles(6, 9), 5)

    def test_power_lands_inside_the_aimed_tile(self) -> None:
        for reach in (3, 4, 5, 8):
            for tiles in range(2, reach + 1):
                p = power_for(tiles, reach)
                self.assertGreater(p, 0.0)
                self.assertLessEqual(p, 1.0)
                landed = p * reach  # tiles out, in the model range = power * reach
                self.assertGreaterEqual(landed, tiles - 1e-6)
                self.assertLess(landed, tiles + 1.0 + 1e-6)


class ControllerSimulation(unittest.TestCase):
    def rate(self, difficulty: float, bar_height: float, trap: bool = False) -> float:
        wins = total = 0
        for motion in range(5):
            for seed in range(24):
                caught, _, _ = play(seed * 7 + motion, difficulty, motion, bar_height, trap)
                wins += caught
                total += 1
        return wins / total

    def test_easy_and_medium_fish_are_always_caught(self) -> None:
        for difficulty in (15, 35, 55):
            for h in (96.0, 128.0, 176.0):
                self.assertGreaterEqual(self.rate(difficulty, h), 0.97, f"difficulty {difficulty}, bar {h}")

    def test_hard_fish_are_mostly_caught_with_a_decent_bar(self) -> None:
        self.assertGreaterEqual(self.rate(80, 128.0), 0.80)
        self.assertGreaterEqual(self.rate(80, 176.0), 0.95)

    def test_trap_bobber_does_not_break_the_controller(self) -> None:
        self.assertGreaterEqual(self.rate(55, 128.0, trap=True), 0.95)

    def test_buttons_do_not_flicker(self) -> None:
        toggles = ticks = 0
        for motion in range(5):
            for seed in range(10):
                _, t, g = play(seed * 7 + motion, 55, motion, 128.0)
                toggles += g
                ticks += t
        self.assertLess(toggles / ticks, 0.15)  # fewer than 15 press/release changes per 100 ticks

    def test_a_better_bar_never_hurts(self) -> None:
        self.assertGreaterEqual(self.rate(80, 176.0), self.rate(80, 96.0))


if __name__ == "__main__":
    unittest.main()
