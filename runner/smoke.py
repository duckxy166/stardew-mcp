"""LLM-free smoke tests for the Stardew MCP tools. Run before every unattended batch.

Usage (game running with `EnableDebugApi: true` in the mod's config.json):

    py runner/smoke.py                 # run every case on a copy of the current save
    py runner/smoke.py --only shop_buy # run one case
    py runner/smoke.py --list

What it does:
  1. copies the run's save folder to a `smoke_*` slot and loads that copy in the game
     (the real save is only read; its files are hashed before and after to prove it),
  2. runs fixed cases that call the MCP tools directly and set up their own state through
     /api/debug/*,
  3. fails a case on a bad tool result shape or on new ERROR lines in the SMAPI log,
  4. loads the original save again and deletes the smoke copy.

Exit code is 0 only if every case passed, so run_days.py can use it as a gate.
Standard library only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

API = "http://localhost:8770"
SMOKE_PREFIX = "smoke_"


# ---------------------------------------------------------------------- paths


def game_data_dir() -> Path:
    if os.name == "nt":
        return Path(os.environ["APPDATA"]) / "StardewValley"
    return Path.home() / ".config" / "StardewValley"


def saves_dir() -> Path:
    return game_data_dir() / "Saves"


def smapi_log_path() -> Path:
    return game_data_dir() / "ErrorLogs" / "SMAPI-latest.txt"


# ---------------------------------------------------------------------- http


class Smoke(Exception):
    """A failed assertion inside a case."""


class SkipCase(Exception):
    """The case cannot run in the current game state (not a failure)."""


def _request(path: str, body: dict | None = None, timeout: float = 600):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(API + path, data=data, headers={"Content-Type": "application/json"},
                                 method="POST" if body is not None else "GET")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def state() -> dict:
    try:
        return _request("/api/state", timeout=10)
    except Exception:
        return {"ready": False}


_rpc_id = 0


def call_seat(seat: str, tool: str, args: dict) -> tuple[bool, str]:
    """Call an MCP tool on one seat's endpoint (p1, p2, p3). Returns (ok, text). Also checks the result shape."""
    global _rpc_id
    _rpc_id += 1
    msg = {"jsonrpc": "2.0", "id": _rpc_id, "method": "tools/call", "params": {"name": tool, "arguments": args}}
    reply = _request("/mcp" if seat == "p1" else f"/mcp/{seat}", msg)
    result = reply.get("result")
    if not isinstance(result, dict):
        raise Smoke(f"{tool}: no result object in reply: {str(reply)[:200]}")
    content = result.get("content")
    if not (isinstance(content, list) and content and content[0].get("type") == "text"
            and isinstance(content[0].get("text"), str)):
        raise Smoke(f"{tool}: bad result shape (content): {str(result)[:200]}")
    if not isinstance(result.get("isError"), bool):
        raise Smoke(f"{tool}: bad result shape (isError): {str(result)[:200]}")
    text = content[0]["text"]
    if not text.strip():
        raise Smoke(f"{tool}: empty result text")
    return (not result["isError"]), text


def call(tool: str, /, **args) -> tuple[bool, str]:
    """call_seat for p1, the seat the single-player cases use."""
    return call_seat("p1", tool, args)


def ok_seat(seat: str, tool: str, /, **args) -> str:
    good, text = call_seat(seat, tool, args)
    if not good:
        raise Smoke(f"{seat} {tool}({args}) failed: {text.splitlines()[0][:200]}")
    return text


def ok(tool: str, /, **args) -> str:
    return ok_seat("p1", tool, **args)


def debug_seat(seat: str, op: str, **args) -> str:
    try:
        reply = _request(f"/api/debug/{op}" if seat == "p1" else f"/api/debug/{seat}/{op}", args)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            raise SystemExit("Debug API is off. Set \"EnableDebugApi\": true in Mods/StardewMCP/config.json and restart the game.")
        raise
    if not reply.get("ok"):
        raise Smoke(f"debug {seat} {op}({args}) failed: {reply.get('text')}")
    return reply.get("text", "")


def debug(op: str, **args) -> str:
    return debug_seat("p1", op, **args)


# ---------------------------------------------------------------------- save handling


def hash_dir(path: Path) -> str:
    h = hashlib.sha256()
    for f in sorted(p for p in path.rglob("*") if p.is_file()):
        h.update(str(f.relative_to(path)).encode("utf-8"))
        h.update(f.read_bytes())
    return h.hexdigest()


def copy_save(src: Path, dst: Path) -> None:
    """Copy a save folder under a new name. The game loads Saves/<name>/<name>, so rename the inner files too."""
    if dst.exists():
        raise SystemExit(f"{dst} already exists")
    dst.mkdir(parents=True)
    for f in src.iterdir():
        if not f.is_file():
            continue
        name = f.name
        if name == src.name:
            name = dst.name
        elif name == src.name + "_old":
            name = dst.name + "_old"
        shutil.copy2(f, dst / name)


def remove_smoke_saves() -> None:
    """Delete every smoke_* save folder. Sleeping in the copy makes the game write its own smoke_<id> folder too."""
    for p in saves_dir().iterdir():
        if p.is_dir() and p.name.startswith(SMOKE_PREFIX):
            shutil.rmtree(p, ignore_errors=True)


def pick_source_save(explicit: str | None) -> Path:
    saves = saves_dir()
    if explicit:
        p = saves / explicit
        if not p.is_dir():
            raise SystemExit(f"No save folder {p}")
        return p
    s = state()
    if s.get("ready") and s.get("saveFolder") and not str(s["saveFolder"]).startswith(SMOKE_PREFIX):
        return saves / s["saveFolder"]
    cands = [p for p in saves.iterdir() if p.is_dir() and not p.name.startswith(SMOKE_PREFIX)]
    if not cands:
        raise SystemExit("No saves found.")
    return max(cands, key=lambda p: p.stat().st_mtime)


def load_and_wait(name: str, timeout: int = 300) -> dict:
    debug("load_save", name=name)
    t0 = time.time()
    time.sleep(3)
    while time.time() - t0 < timeout:
        s = state()
        if s.get("ready") and s.get("saveFolder") == name:
            time.sleep(2)  # let the first day-start events settle
            return s
        time.sleep(2)
    raise SystemExit(f"Timed out waiting for save {name} to load.")


# ---------------------------------------------------------------------- SMAPI log


class SmapiLog:
    ERROR = re.compile(r"^\[\d\d:\d\d:\d\d ERROR ")

    def __init__(self, path: Path):
        self.path = path
        self.pos = path.stat().st_size if path.exists() else 0

    def new_errors(self) -> list[str]:
        if not self.path.exists():
            return []
        size = self.path.stat().st_size
        if size < self.pos:  # log rotated (game restarted)
            self.pos = 0
        with self.path.open("rb") as f:
            f.seek(self.pos)
            text = f.read().decode("utf-8", errors="replace")
        self.pos = size
        return [ln for ln in text.splitlines() if self.ERROR.match(ln)]


# ---------------------------------------------------------------------- helpers for cases


def reset(location: str = "Farm") -> None:
    """Known-good player state before each case: full health/energy, morning, enough money, in `location`."""
    debug("set_time", time=900)
    debug("set_stamina", n=270)
    debug("set_health", n=100)
    debug("set_money", n=2000)
    if fresh_state().get("location") != location:
        ok("travel_to", location=location, reason="smoke test setup")


def parse_tillable_runs(look_text: str, length: int) -> list[list[list[int]]]:
    """Find horizontal runs of `length` tillable tiles (',') in a `look` map. Returns [[x,y], ...] lists."""
    m = re.search(r"Showing x (\d+)-(\d+), y (\d+)-(\d+)", look_text)
    if not m:
        return []
    x0 = int(m.group(1))
    runs = []
    for line in look_text.splitlines():
        row = re.match(r"y\s*(\d+) \| (.*)$", line)
        if not row:
            continue
        y, cells = int(row.group(1)), row.group(2)
        i = 0
        while i <= len(cells) - length:
            if all(ch == "," for ch in cells[i:i + length]):
                runs.append([[x0 + i + k, y] for k in range(length)])
                i += length
            else:
                i += 1
    return runs


def parse_things(look_text: str, symbol: str) -> list[tuple[str, list[tuple[int, int]]]]:
    """'Things in view' lines for one map symbol, e.g. 'A': [(label, [(x,y), ...]), ...] nearest first."""
    out = []
    for line in look_text.splitlines():
        m = re.match(rf"\s+{re.escape(symbol)} (.+?) x\d+: (.*)$", line)
        if m:
            out.append((m.group(1), [(int(a), int(b)) for a, b in re.findall(r"\((\d+),(\d+)\)", m.group(2))]))
    return out


def inventory_count(name: str) -> int:
    """Total stack size of items called exactly `name` in the `inventory` tool output."""
    total = 0
    for line in ok("inventory").splitlines():
        m = re.match(r"\s*slot \d+: (.+?) x(\d+) \[", line)
        if m and m.group(1).lower() == name.lower():
            total += int(m.group(2))
    return total


def open_shop(location: str, hhmm: int) -> str:
    """Set the time, travel to `location` and interact with its action tiles until a shop opens. Returns shop_list."""
    debug("set_time", time=hhmm)
    ok("travel_to", location=location, reason="smoke test")
    look = ok("look", radius=12)
    counters = [t for _, tiles in parse_things(look, "A") for t in tiles]
    for x, y in counters:
        ok("interact", x=x, y=y, reason="smoke test: find the shop counter")
        good, text = call("shop_list")
        if good:
            return text
    raise Smoke(f"no action tile in {location} opened a shop (tried {counters[:6]})")


def fresh_state() -> dict:
    """/api/state is a snapshot refreshed about 4 times a second; wait for it to catch up after a tool call."""
    time.sleep(0.6)
    return state()


# ---------------------------------------------------------------------- cases


@dataclass
class Case:
    name: str
    fn: Callable[[], None]
    skip: str = ""


CASES: list[Case] = []


def case(name: str, skip: str = ""):
    def deco(fn):
        CASES.append(Case(name, fn, skip))
        return fn
    return deco


@case("observe_tools")
def observe_tools():
    reset("Farm")
    for tool in ("status", "look", "inventory"):
        ok(tool)
    ok("think", text="smoke test")


@case("travel")
def travel():
    reset("Farm")
    text = ok("travel_to", location="FarmHouse", reason="smoke test")
    if "Arrived in FarmHouse" not in text:
        raise Smoke(f"expected arrival in FarmHouse, got: {text.splitlines()[0]}")
    text = ok("travel_to", location="Farm", reason="smoke test")
    if "Arrived in Farm" not in text:
        raise Smoke(f"expected arrival on Farm, got: {text.splitlines()[0]}")


@case("shop_buy")
def shop_buy():
    if fresh_state().get("dayOfMonth", 0) % 7 == 3:
        raise SkipCase("Pierre's is closed on Wednesdays; run the gate on another day to cover the shop")
    reset("Farm")
    open_shop("SeedShop", 1000)  # Pierre's is open 9:00-17:00 (closed Wednesdays)

    before = inventory_count("Parsnip Seeds")
    money = fresh_state().get("money")
    text = ok("buy", item="Parsnip Seeds", quantity=2, reason="smoke test")
    m = re.search(r"for (\d+)g", text)
    if not m:
        raise Smoke(f"buy result has no price: {text.splitlines()[0]}")
    after = inventory_count("Parsnip Seeds")
    if after != before + 2:
        raise Smoke(f"expected {before + 2} Parsnip Seeds after buying 2, have {after}")
    if fresh_state().get("money") != money - int(m.group(1)):
        raise Smoke("money did not drop by the reported price")
    ok("close_menu")


@case("farm_loop")
def farm_loop():
    reset("Farm")
    debug("add_item", id="(O)472", qty=5)  # Parsnip Seeds
    look = ok("look", radius=10)
    runs = parse_tillable_runs(look, 3)
    if not runs:
        raise Smoke("no run of 3 tillable tiles near the farm entrance")
    tiles = runs[0]

    ok("use_tool", tool="Hoe", tiles=tiles, reason="smoke test")
    text = ok("plant", item="Parsnip Seeds", tiles=tiles, reason="smoke test")
    m = re.search(r"Planted (\d+)/(\d+)", text)
    if not m or m.group(1) != m.group(2):
        raise Smoke(f"expected every seed planted, got: {text.splitlines()[0]}")
    ok("use_tool", tool="Watering Can", tiles=tiles, reason="smoke test")

    day = fresh_state().get("day")
    ok("go_to_bed", diary="Smoke test diary: tilled, planted and watered three parsnips.", reason="smoke test")
    now = fresh_state().get("day")
    if now != day + 1:
        raise Smoke(f"day did not advance (was {day}, now {now})")


@case("ship_and_sleep")
def ship_and_sleep():
    reset("Farm")
    debug("add_item", id="(O)24", qty=3)  # Parsnip
    ok("ship", item="Parsnip", quantity=3, reason="smoke test")
    day = fresh_state().get("day")
    text = ok("go_to_bed", diary="Smoke test diary: shipped three parsnips and went to sleep.", reason="smoke test")
    m = re.search(r"shipping income (-?\d+)g", text)
    if not m or int(m.group(1)) <= 0:
        raise Smoke(f"expected positive shipping income, got: {text.splitlines()[0]}")
    if fresh_state().get("day") != day + 1:
        raise Smoke("day did not advance")


@case("buy_recipe")
def buy_recipe():
    """#7: a bought recipe is learned, not handed over as the dish."""
    reset("Farm")
    debug("set_time", time=1300)
    wednesday = fresh_state().get("dayOfMonth", 0) % 7 == 3
    for shop_id in ("Saloon", "SeedShop"):  # Gus sells cooking recipes, Pierre some crafting ones
        if shop_id == "SeedShop" and wednesday:
            continue  # Pierre's door is locked on Wednesdays
        ok("travel_to", location=shop_id, reason="smoke test")
        debug("open_shop", id=shop_id)  # set_time doesn't move the owner to the counter
        listing = ok("shop_list")
        recipes = [m.group(1) for m in re.finditer(r"^\s+(.+?): (\d+)g.*\[recipe", listing, re.M)]
        if recipes:
            break
        ok("close_menu")
    else:
        raise SkipCase("neither the Saloon nor Pierre's sells a recipe today")

    listed = recipes[0]
    dish = re.sub(r"\s*\(?Recipe\)?$", "", listed)  # shops list "Hashbrowns (Recipe)"
    before = inventory_count(dish)
    money = fresh_state().get("money")
    text = ok("buy", item=listed, quantity=1, reason="smoke test")
    if not text.startswith("Learned recipe:"):
        raise Smoke(f"expected 'Learned recipe', got: {text.splitlines()[0]}")
    if inventory_count(dish) != before:
        raise Smoke(f"buying {listed} put the dish in the inventory")
    if fresh_state().get("money", money) >= money:
        raise Smoke("money did not drop after buying a recipe")
    ok("close_menu")


@case("choose_profession")
def choose_profession():
    """#8: level-5 profession choices wait for the AI; an unmade choice gets the default at bedtime."""
    reset("Farm")
    if "Professions:" in ok("status"):
        raise SkipCase("this save already has professions; run the gate on an earlier save")
    debug("set_skill", skill="farming", level=5)
    debug("set_skill", skill="fishing", level=5)
    ok("go_to_bed", diary="Smoke test diary: reached farming and fishing level 5 today.", reason="smoke test")

    status = ok("status")
    for skill in ("Farming 5", "Fishing 5"):
        if f"Pending profession choice (permanent): {skill}" not in status:
            raise Smoke(f"no pending {skill} choice in status:\n{status}")
    text = ok("choose_profession", option=1, skill="Farming", reason="smoke test")
    if "Tiller" not in text:
        raise Smoke(f"expected Tiller for Farming option 1, got: {text.splitlines()[0]}")
    good, _ = call("choose_profession", option=1, skill="Mining", reason="smoke test")
    if good:
        raise Smoke("choose_profession accepted a skill with no pending choice")

    ok("go_to_bed", diary="Smoke test diary: chose Tiller and left the fishing choice to the default.", reason="smoke test")
    status = ok("status")
    m = re.search(r"^Professions: (.+)$", status, re.M)
    if not m or "Tiller" not in m.group(1) or "Fisher" not in m.group(1):
        raise Smoke(f"expected Tiller (chosen) and Fisher (default) in status, got: {m.group(0) if m else status}")
    if "Pending profession choice" in status:
        raise Smoke("a profession choice is still pending after bedtime")


def place_near(item_id: str) -> tuple[int, int]:
    """Place an object (debug) in the middle of a run of three free tiles near the player, so a side stays reachable."""
    for run in parse_tillable_runs(ok("look", radius=10), 3):
        x, y = run[1]
        try:
            debug("place_object", id=item_id, x=x, y=y)
            return x, y
        except Smoke:
            continue
    raise Smoke(f"no free tile near the player for {item_id}")


def question_options() -> dict[str, int]:
    """Options of the open question dialogue, by text, from the status footer."""
    return {m.group(2).strip(): int(m.group(1)) for m in re.finditer(r"option (\d+): (.+)", ok("status"))}


@case("totem")
def totem():
    """interact(holding) with no tile uses the item where you stand (performUseAction): a Warp Totem: Farm."""
    reset("Farm")
    ok("travel_to", location="FarmHouse", reason="smoke test")
    debug("add_item", id="(O)688", qty=1)  # Warp Totem: Farm
    text = ok("interact", holding="Warp Totem: Farm", reason="smoke test")
    if "You are now in Farm" not in text or inventory_count("Warp Totem: Farm") != 0:
        raise Smoke(f"the totem did not warp home and get used up: {text.splitlines()[0]}")


@case("furnace")
def furnace():
    """interact(holding) loads a machine through checkAction; interact without holding collects the output."""
    reset("Farm")
    debug("add_item", id="(O)378", qty=10)  # Copper Ore
    debug("add_item", id="(O)382", qty=2)  # Coal
    x, y = place_near("(BC)13")  # Furnace
    ore, coal, bars = inventory_count("Copper Ore"), inventory_count("Coal"), inventory_count("Copper Bar")
    text = ok("interact", x=x, y=y, holding="Copper Ore", reason="smoke test")
    if "holding Copper Bar" not in text:
        raise Smoke(f"furnace did not take the ore: {text.splitlines()[0]}")
    if inventory_count("Copper Ore") != ore - 5 or inventory_count("Coal") != coal - 1:
        raise Smoke(f"expected 5 ore and 1 coal used: {text.splitlines()[0]}")
    ok("wait", minutes=40, reason="smoke test: smelting")  # a copper bar takes 30 minutes
    text = ok("interact", x=x, y=y, reason="smoke test")
    if inventory_count("Copper Bar") != bars + 1:
        raise Smoke(f"expected one Copper Bar collected: {text.splitlines()[0]}")
    if inventory_count("Copper Ore") != ore - 5:
        raise Smoke("collecting without holding loaded more ore")


@case("chest")
def chest():
    """A chest opens an ItemGrabMenu; menu put/take click its slots (whole stacks and single items)."""
    reset("Farm")
    debug("add_item", id="(O)24", qty=5)  # Parsnip
    x, y = place_near("(BC)130")  # Chest
    have = inventory_count("Parsnip")
    try:
        ok("interact", x=x, y=y, reason="smoke test: open the chest")
        text = ok("menu")
        if "Chest (ItemGrabMenu)" not in text or "put(" not in text:
            raise Smoke(f"menu() does not show an open chest:\n{text}")
        text = ok("menu", action="put", item="Parsnip", count=2, reason="smoke test")
        if "Inside (1/" not in text or "Parsnip x2" not in text or inventory_count("Parsnip") != have - 2:
            raise Smoke(f"put 2 Parsnip did not land in the chest:\n{text}")
        ok("menu", action="put", item="Parsnip", reason="smoke test")  # the rest of the stack
        if inventory_count("Parsnip") != 0:
            raise Smoke("put without count left Parsnips in the inventory")
        ok("menu", action="take", item="Parsnip", count=1, reason="smoke test")
        if inventory_count("Parsnip") != 1:
            raise Smoke(f"take 1 gave {inventory_count('Parsnip')}")
        text = ok("menu", action="take", item="Parsnip", reason="smoke test")
        if inventory_count("Parsnip") != have or "Inside (0/" not in text:
            raise Smoke(f"take without count did not empty the chest:\n{text}")
        if call("menu", action="take", item="Parsnip", reason="smoke test")[0]:
            raise Smoke("take from an empty chest succeeded")
        if "The menu is closed." not in ok("menu", action="ok", reason="smoke test"):
            raise Smoke("menu ok did not close the chest")
    finally:
        call("close_menu")


@case("mine_elevator")
def mine_elevator():
    """The MineElevator tile opens MineElevatorMenu; menu choose rides to a floor and back."""
    reset("Farm")
    _, x, y = p1_tile()
    debug("set_mine_level", n=10)
    debug("warp", location="Mine", x=17, y=4)  # where the elevator drops you; the landslide may still block the path
    try:
        elevators = [t for label, tiles in parse_things(ok("look", radius=12), "A") if label == "MineElevator" for t in tiles]
        if not elevators:
            raise Smoke("no MineElevator tile in the Mine")
        ok("interact", x=elevators[0][0], y=elevators[0][1], reason="smoke test")
        text = ok("menu")
        if "Mine elevator" not in text or "Floors: 0, 5, 10" not in text:
            raise Smoke(f"menu() does not show the elevator floors:\n{text}")
        text = ok("menu", action="choose", option="5", reason="smoke test")
        if "UndergroundMine5" not in text:
            raise Smoke(f"choose 5 did not ride to floor 5: {text.splitlines()[0]}")
        up = [t for label, tiles in parse_things(ok("look", radius=12), "A") if label.startswith("Mine Elevator") for t in tiles]
        if not up:
            raise Smoke("no elevator on floor 5")
        ok("interact", x=up[0][0], y=up[0][1], reason="smoke test")
        text = ok("menu", action="choose", option="0", reason="smoke test")
        if "(Mine)" not in text:
            raise Smoke(f"choose 0 did not ride back to the entrance: {text.splitlines()[0]}")
    finally:
        call("close_menu")
        debug("warp", location="Farm", x=x, y=y)


# The warp tiles below are the usual door-side tiles of each interior; adjust them if a save puts you in a wall.
CC_TILE = (32, 22)
MUSEUM_TILE = (3, 9)


def bundle_line(text: str, name: str) -> tuple[str, int, int] | None:
    """(whole line, filled, required) of an unfinished bundle in `bundles` output, e.g. '[0] Spring Crops 1/4 → ...'."""
    for line in text.splitlines():
        m = re.match(rf"\s*\[\d+\] {re.escape(name)} (\d+)/(\d+)", line)
        if m:
            return line, int(m.group(1)), int(m.group(2))
    return None


@case("bundles")
def bundles():
    """bundles reads the Community Center (needs its door unlocked; the data itself exists from day 1)."""
    reset("Farm")
    _, x, y = p1_tile()
    debug("warp", location="CommunityCenter", x=CC_TILE[0], y=CC_TILE[1])
    try:
        good, text = call("bundles")
        if not good:
            raise SkipCase(f"bundles are closed on this save: {text.splitlines()[0]}")
        for room in ("Pantry", "Crafts Room", "Fish Tank", "Boiler Room", "Vault", "Bulletin Board"):
            if f"== {room}:" not in text:
                raise Smoke(f"bundles does not list the {room}:\n{text[:600]}")
        if "Spring Crops" not in text:
            raise Smoke(f"bundles does not list the Spring Crops bundle:\n{text[:600]}")
        good, text = call("bundles", room="Pantry")
        if not good or "== Crafts Room" in text or "Spring Crops" not in text:
            raise Smoke(f"bundles(room=Pantry) should list only the Pantry:\n{text[:600]}")
        if call("bundles", room="Nowhere")[0]:
            raise Smoke("bundles accepted an unknown room")
    finally:
        debug("warp", location="Farm", x=x, y=y)


@case("bundle_donate")
def bundle_donate():
    """donate_bundle opens the Pantry's note, drops a Parsnip into Spring Crops and leaves the menu closed."""
    reset("Farm")
    _, x, y = p1_tile()
    debug("warp", location="CommunityCenter", x=CC_TILE[0], y=CC_TILE[1])
    try:
        good, text = call("bundles", room="Pantry")
        if not good:
            raise SkipCase(f"bundles are closed on this save: {text.splitlines()[0]}")
        if "can't read Junimo script" in text:
            raise SkipCase("the Wizard has not taught this farmer Junimo script yet; run the gate on a later save")
        line = bundle_line(text, "Spring Crops")
        if line is None:
            raise SkipCase("the Spring Crops bundle is already complete on this save")
        _, filled, required = line
        if "Parsnip" not in line[0].split("needs", 1)[-1]:
            raise SkipCase("a Parsnip is already in the Spring Crops bundle")
        have = inventory_count("Parsnip")
        debug("add_item", id="(O)24", qty=1)  # Parsnip
        text = ok("donate_bundle", item="Parsnip", bundle="Spring Crops", reason="smoke test")
        if f"{filled + 1}/{required}" not in text:
            raise Smoke(f"expected Spring Crops at {filled + 1}/{required}: {text.splitlines()[0]}")
        if call("menu")[0]:
            raise Smoke("a menu was left open after donate_bundle")
        if inventory_count("Parsnip") != have:
            raise Smoke(f"the donated Parsnip should be gone; have {inventory_count('Parsnip')}, expected {have}")
        after = bundle_line(ok("bundles", room="Pantry"), "Spring Crops")
        if after is not None and after[1] != filled + 1:
            raise Smoke(f"bundles shows {after[1]} filled after the donation, expected {filled + 1}")
        if have == 0 and call("donate_bundle", item="Parsnip", bundle="Spring Crops", reason="smoke test")[0]:
            raise Smoke("donating a Parsnip that is not in the inventory succeeded")
    finally:
        call("close_menu")
        debug("warp", location="Farm", x=x, y=y)


@case("museum")
def museum():
    """museum list shows an artifact the Museum accepts; donate puts it on display and it stops being offered."""
    reset("Farm")
    _, x, y = p1_tile()
    debug("warp", location="ArchaeologyHouse", x=MUSEUM_TILE[0], y=MUSEUM_TILE[1])
    try:
        debug("add_item", id="(O)96", qty=1)  # Dwarf Scroll I
        text = ok("museum")
        if "Dwarf Scroll I" not in text:
            raise SkipCase("the Museum already displays a Dwarf Scroll I on this save")
        have = inventory_count("Dwarf Scroll I")
        text = ok("museum", action="donate", item="Dwarf Scroll I", reason="smoke test")
        if "Donated to the Museum: Dwarf Scroll I" not in text:
            raise Smoke(f"donate did not report the piece: {text.splitlines()[0]}")
        if inventory_count("Dwarf Scroll I") != have - 1:
            raise Smoke("the donated Dwarf Scroll I is still in the inventory")
        if "Dwarf Scroll I" in ok("museum", action="list"):
            raise Smoke("the Museum still offers a piece that is already on display")
        if have == 1 and call("museum", action="donate", item="Dwarf Scroll I", reason="smoke test")[0]:
            raise Smoke("donating a Dwarf Scroll I that is not in the inventory succeeded")
    finally:
        call("close_menu")
        debug("warp", location="Farm", x=x, y=y)


# ---------------------------------------------------------------------- multi-seat (social tools)
# These need split-screen (SplitScreen: true in config.json) and run last: once p2/p3 have talked to the mod
# they count as connected AIs, and an idle one holds the world frozen until it is disconnected again.


def seat_active(seat: str) -> bool:
    return any(p.get("id") == seat and p.get("active") for p in state().get("players", []))


def ensure_seats() -> None:
    """Make sure p2 and p3 are in the game; build the cabins on the smoke copy when the save has none."""
    def wait(seconds: int) -> bool:
        t0 = time.time()
        while time.time() - t0 < seconds:
            if seat_active("p2") and seat_active("p3"):
                return True
            time.sleep(2)
        return False

    if wait(25):
        return
    if state().get("screens", 1) <= 1:
        debug("build_cabins", n=3)
    if not wait(120):
        raise SkipCase('p2/p3 did not join: set "SplitScreen": true in config.json (and give the save 3 cabins)')


def hear(seat: str, tool: str = "status") -> str:
    """Call a cheap observation on a seat; its result carries any new Messages: block."""
    return ok_seat(seat, tool)


def p1_tile() -> tuple[str, int, int]:
    p1 = next(p for p in fresh_state()["players"] if p["id"] == "p1")["state"]
    return p1["location"], p1["tile"][0], p1["tile"][1]


def chat_lines(seat: str) -> str:
    return debug_seat(seat, "chat_log")


def wait_chat(seat: str, needle: str, timeout: float = 4) -> None:
    """The game relays chat over its own network; give it a moment to reach the other screen."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if needle in chat_lines(seat):
            return
        time.sleep(0.3)
    raise Smoke(f'{seat} chat box lacks "{needle}"')


def unfreeze() -> None:
    """A warp needs the world running, and any connected AI that is not in an action freezes it."""
    for seat in ("p1", "p2", "p3"):
        debug_seat(seat, "disconnect")


def social(fn):
    """Run a multi-seat case, then disconnect p2/p3 so they stop holding the world frozen."""
    def run():
        ensure_seats()
        try:
            fn()
        finally:
            for seat in ("p2", "p3"):
                try:
                    debug_seat(seat, "disconnect")
                except Exception:
                    pass
    run.__name__ = fn.__name__
    return run


@case("chat")
@social
def chat():
    reset("Farm")
    text = ok_seat("p1", "say", text="hello from p1 SMOKE1")
    if "other player" not in text:
        raise Smoke(f"say did not report listeners: {text.splitlines()[0]}")
    wait_chat("p1", "SMOKE1")
    wait_chat("p2", "SMOKE1")  # the real in-game chat box on the other screen
    heard = hear("p2")
    if "Messages:" not in heard or "SMOKE1" not in heard or "(p1)" not in heard:
        raise Smoke(f"p2's next result lacks p1's line:\n{heard}")
    if "SMOKE1" in hear("p2"):
        raise Smoke("p2 got the same chat line twice")

    ok_seat("p2", "say", text="hi p1, p2 here SMOKE2")
    back = hear("p1", "inventory")
    if "SMOKE2" not in back or "(p2)" not in back:
        raise Smoke(f"p1's next result lacks p2's reply:\n{back}")

    for bad in ({"text": "  "}, {}):
        if call_seat("p1", "say", bad)[0]:
            raise Smoke(f"say accepted {bad}")


@case("whisper")
@social
def whisper():
    reset("Farm")
    ok_seat("p3", "status")  # p3 is connected but must never learn about this
    ok_seat("p1", "whisper", to="p2", text="the WSECRET is under the rock")
    heard = hear("p2")
    if "whispers" not in heard or "WSECRET" not in heard or "(p1)" not in heard:
        raise Smoke(f"p2 did not get the whisper:\n{heard}")
    p3 = hear("p3") + hear("p3", "inventory")
    if "WSECRET" in p3 or "whisper" in p3.lower():
        raise Smoke("p3 got a hint of a whisper meant for p2")
    time.sleep(1)
    for seat in ("p1", "p2", "p3"):
        if "WSECRET" in chat_lines(seat):
            raise Smoke(f"whisper text reached {seat}'s game chat box")
    feed = _request("/api/feed")["entries"]
    seen = [e for e in feed if e.get("kind") == "whisper" and e.get("player") == "p1" and (e.get("args") or {}).get("to") == "p2"]
    if not seen or "WSECRET" not in seen[-1].get("text", ""):
        raise Smoke("viewers' feed has no whisper entry p1 -> p2")
    for bad in ({"to": "p1", "text": "x"}, {"to": "nobody", "text": "x"}, {"to": "p2", "text": ""}):
        if call_seat("p1", "whisper", bad)[0]:
            raise Smoke(f"whisper accepted {bad}")


@case("plan_board")
@social
def plan_board():
    ok_seat("p1", "plan_edit", section="Smoke crops", text="p1 plans parsnips")
    ok_seat("p2", "plan_edit", section="Smoke water", text="p2 waters at noon")
    ok_seat("p2", "plan_edit", section="Smoke crops", text="p2 changed it to potatoes")
    board = ok_seat("p3", "plan_read", history=20)
    for needle in ("p2 changed it to potatoes", "p2 waters at noon"):
        if needle not in board:
            raise Smoke(f'plan_read lacks "{needle}":\n{board}')
    hist = [ln for ln in board.splitlines() if re.match(r"\s+#\d+ day \d+ \S+ ", ln) and "Smoke crops" in ln]
    if not any("(p1)" in ln for ln in hist) or not any("(p2)" in ln for ln in hist):
        raise Smoke(f'history should show p1 and p2 editing "Smoke crops":\n{board}')
    if not any(s.get("section") == "Smoke crops" for s in fresh_state()["plan"]["sections"]):
        raise Smoke("/api/state has no plan board")
    for section in ("Smoke crops", "Smoke water"):
        ok_seat("p1", "plan_edit", section=section, text="")
    if "p2 waters at noon" in ok_seat("p1", "plan_read", history=0):
        raise Smoke("cleared section is still on the board")


@case("emote")
@social
def emote():
    text = ok_seat("p1", "emote", name="happy")
    if "happy" not in text:
        raise Smoke(f"unexpected emote result: {text}")
    good, text = call_seat("p1", "emote", {"name": "no-such-emote"})
    if good or "Names:" not in text:
        raise Smoke(f"unknown emote should fail and list names: {text}")


@case("give")
@social
def give():
    reset("Farm")
    debug("add_item", id="(O)24", qty=3)  # Parsnip
    loc, x, y = p1_tile()
    unfreeze()
    debug_seat("p2", "warp", location="Town", x=10, y=10)
    good, text = call_seat("p1", "give", {"to": "p2", "item": "Parsnip", "count": 1})
    if good or "too far" not in text:
        raise Smoke(f"give to a far player should fail with 'too far', got: {text.splitlines()[0]}")

    unfreeze()
    debug_seat("p2", "warp", location=loc, x=x + 1, y=y)

    seen = []  # every inventory result, since each one also carries (and consumes) p2's new messages

    def count(seat: str) -> int:
        text = ok_seat(seat, "inventory")
        seen.append(text)
        for line in text.splitlines():
            m = re.match(r"\s*slot \d+: Parsnip x(\d+) ", line)
            if m:
                return int(m.group(1))
        return 0

    before2 = count("p2")
    text = ok_seat("p1", "give", to="Gemini", item="Parsnip", count=2)
    t0 = time.time()
    while count("p2") != before2 + 2 and time.time() - t0 < 4:
        time.sleep(0.3)
    if count("p2") != before2 + 2 or count("p1") != 1:
        raise Smoke(f"expected p2 +2 and p1 left with 1 Parsnip, got p2 {count('p2')} (was {before2}), p1 {count('p1')}. {text}")
    if not any("gave you 2 x Parsnip" in t for t in seen + [hear("p2")]):
        raise Smoke("p2 was not told about the gift")
    if call_seat("p1", "give", {"to": "p2", "item": "Parsnip", "count": 5})[0]:
        raise Smoke("give accepted more than p1 has")
    if call_seat("p1", "give", {"to": "p2", "item": "Axe"})[0]:
        raise Smoke("give accepted a tool")

    for i in range(16, 90):  # fill p2's pack with one of everything
        try:
            debug_seat("p2", "add_item", id=f"(O){i}", qty=1)
        except Smoke as e:
            if "Inventory is full" in str(e):
                break
    debug("add_item", id="(O)388", qty=1)  # Wood: p2 has none, so it can't stack into an existing slot
    good, text = call_seat("p1", "give", {"to": "p2", "item": "Wood"})
    if good or "inventory is full" not in text:
        raise Smoke(f"give to a full pack should fail, got: {text.splitlines()[0]}")


@case("send_money")
@social
def send_money():
    """Separate wallets: ledger book -> SendMoney -> recipient -> DigitEntryMenu, typed with menu amount."""
    reset("Farm")
    debug("separate_wallets")
    debug("set_money", n=2000)
    p2 = next(p for p in fresh_state()["players"] if p["id"] == "p2")["state"]
    p2_money = p2["money"]
    ok("travel_to", location="ManorHouse", reason="smoke test")
    books = [t for label, tiles in parse_things(ok("look", radius=15), "A") if label == "LedgerBook" for t in tiles]
    if not books:
        raise Smoke("no LedgerBook tile in Lewis's house")
    try:
        ok("interact", x=books[0][0], y=books[0][1], reason="smoke test")
        send = next((i for text, i in question_options().items() if "send" in text.lower()), None)
        if send is None:
            raise Smoke("the ledger offers no Send money option")
        ok("respond", option=send, reason="smoke test")
        to = question_options().get(p2["name"])
        if to is None:
            raise Smoke(f"{p2['name']} is not a recipient: {question_options()}")
        ok("respond", option=to, reason="smoke test")
        if "Number entry" not in ok("menu"):
            raise Smoke("no amount menu after choosing the recipient")
        ok("menu", action="amount", value=150, reason="smoke test")
        t0 = time.time()
        while time.time() - t0 < 5:
            s = fresh_state()
            now2 = next(p for p in s["players"] if p["id"] == "p2")["state"]["money"]
            if s.get("money") == 1850 and now2 == p2_money + 150:
                return
        raise Smoke(f"expected p1 1850g and p2 {p2_money + 150}g, got p1 {s.get('money')}g and p2 {now2}g")
    finally:
        call("close_menu")


CASES.append(Case("clint_pickup", lambda: None, skip="needs the tool upgrade flow (#2)"))


# ---------------------------------------------------------------------- runner


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(errors="replace")  # game text may hold characters the console codepage lacks
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", action="append", help="Run only this case (repeatable)")
    ap.add_argument("--list", action="store_true", help="List cases and exit")
    ap.add_argument("--save", help="Save folder to copy (default: the loaded save, else the newest)")
    ap.add_argument("--keep", action="store_true", help="Keep the smoke_* copy afterwards")
    ap.add_argument("--no-restore", action="store_true", help="Leave the smoke copy loaded instead of reloading the original save")
    ap.add_argument("--smapi-log", default=str(smapi_log_path()))
    args = ap.parse_args()

    if args.list:
        for c in CASES:
            print(f"{c.name}{'  (skipped: ' + c.skip + ')' if c.skip else ''}")
        return 0

    chosen = [c for c in CASES if not args.only or c.name in args.only]
    if args.only and len(chosen) != len(set(args.only)):
        raise SystemExit(f"Unknown case in {args.only}; see --list")

    try:
        _request("/api/state", timeout=10)
    except Exception as e:
        raise SystemExit(f"Cannot reach the game at {API} ({e}). Start Stardew via SMAPI first.")

    original = state().get("saveFolder") if state().get("ready") else None
    src = pick_source_save(args.save)
    smoke_name = f"{SMOKE_PREFIX}{src.name}_{time.strftime('%H%M%S')}"
    dst = saves_dir() / smoke_name
    if not args.keep:
        remove_smoke_saves()  # leftovers from an earlier aborted run
    before_hash = hash_dir(src)

    print(f"Copying save {src.name} -> {smoke_name}")
    copy_save(src, dst)
    log = SmapiLog(Path(args.smapi_log))

    failures = 0
    try:
        load_and_wait(smoke_name)
        log.new_errors()  # ignore load-time noise from before the first case
        for c in chosen:
            if c.skip:
                print(f"  SKIP {c.name}: {c.skip}")
                continue
            t0 = time.time()
            try:
                c.fn()
                errs = log.new_errors()
                if errs:
                    raise Smoke("SMAPI log has new errors:\n    " + "\n    ".join(errs[:5]))
                print(f"  PASS {c.name} ({time.time() - t0:.0f}s)")
            except SkipCase as e:
                print(f"  SKIP {c.name}: {e}")
            except Smoke as e:
                failures += 1
                log.new_errors()
                print(f"  FAIL {c.name}: {e}")
            except Exception as e:  # network / unexpected
                failures += 1
                print(f"  FAIL {c.name}: {type(e).__name__}: {e}")
    finally:
        if not args.no_restore:
            restore = original or src.name
            print(f"Reloading {restore} (the last saved state, normally the start of the day)")
            try:
                load_and_wait(restore)
            except SystemExit as e:
                print(f"  could not reload the original save: {e}")
                failures += 1
        if not args.keep and not args.no_restore:
            remove_smoke_saves()

    if hash_dir(src) != before_hash:
        failures += 1
        print(f"FAIL the real save {src.name} changed during the smoke run!")
    else:
        print(f"Real save {src.name} unchanged.")

    print("SMOKE PASSED" if failures == 0 else f"SMOKE FAILED ({failures} problem(s))")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
