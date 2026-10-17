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
import threading
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from tool_results import check_structured

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


def call_raw(seat: str, tool: str, args: dict) -> dict:
    """Call an MCP tool on one seat's endpoint (p1, p2, p3). Returns the whole result object after checking its shape."""
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
    if not content[0]["text"].strip():
        raise Smoke(f"{tool}: empty result text")
    problems = check_structured(result)  # reason code, partial flag and tag (#18); nothing to check on older mod builds
    if problems:
        raise Smoke(f"{tool}: bad structured result: {'; '.join(problems)} in {str(result)[:200]}")
    return result


def call_seat(seat: str, tool: str, args: dict) -> tuple[bool, str]:
    """call_raw, reduced to (ok, text)."""
    result = call_raw(seat, tool, args)
    return (not result["isError"]), result["content"][0]["text"]


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
    # Events about other seats (arrivals, weather) must not cut a case's walk short; "caught_slacking" switches them on itself.
    debug("perception", interrupts=0, clear=1)
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


def parse_cookable(listing: str) -> dict[str, int]:
    """Dishes a `cook()` listing says can be cooked now, with how many times: {'Fried Egg': 4}."""
    return {m.group(1): int(m.group(3))
            for m in re.finditer(r"^  (.+?)(?: \(makes (\d+)\))?: .*\[you can cook (\d+)\]$", listing, re.M)}


def parse_journal(text: str) -> list[dict]:
    """Journal entries of a `quests` result: [{'type': 'Help Wanted', 'name': 'Lumber', 'who': 'Pierre'}, ...]."""
    out = []
    for m in re.finditer(r"^  \d+\. \[(Help Wanted|Story)\] (.+?)(?: - (?:asked by|about) (.+))?$", text, re.M):
        out.append({"type": m.group(1), "name": m.group(2), "who": m.group(3) or ""})
    return out


def parse_board(text: str) -> dict | None:
    """Today's Help Wanted line of a `quests` result: {'name', 'giver', 'state': 'free'|'taken'|'mine', 'by'}, or None when nothing is posted."""
    m = re.search(r"^Help Wanted board \(Town\), today: \[Help Wanted\] (.+?)(?: - asked by (.+?))? - "
                  r"(YOU took it|TAKEN by (.+?)\.|still FREE)", text, re.M)
    if not m:
        return None
    state = "mine" if m.group(3).startswith("YOU") else "taken" if m.group(3).startswith("TAKEN") else "free"
    return {"name": m.group(1), "giver": m.group(2) or "", "state": state, "by": m.group(4) or ""}


def parse_money_left(text: str) -> int | None:
    """'1950g left.' at the end of a crack_geode result."""
    m = re.search(r"(\d+)g left\.", text)
    return int(m.group(1)) if m else None


def inventory_count(name: str) -> int:
    """Total stack size of items called exactly `name` in the `inventory` tool output."""
    total = 0
    for line in ok("inventory").splitlines():
        m = re.match(r"\s*slot \d+: (.+?) x(\d+) \[", line)
        if m and m.group(1).lower() == name.lower():
            total += int(m.group(2))
    return total


def inventory_stacks(name: str) -> dict[str, int]:
    """Stack sizes of items called exactly `name` by quality ('normal', 'silver', 'gold', 'iridium'), from `inventory`."""
    out: dict[str, int] = {}
    for line in ok("inventory").splitlines():
        m = re.match(r"\s*slot \d+: (.+?) x(\d+) \[(.*)\]$", line)
        if m and m.group(1).lower() == name.lower():
            tags = [t.strip() for t in m.group(3).split(",")]
            quality = next((t for t in tags if t in ("silver", "gold", "iridium")), "normal")
            out[quality] = out.get(quality, 0) + int(m.group(2))
    return out


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


def pack_slots(inventory_text: str) -> int:
    """Pack size from the `inventory` tool: 'Money 500g · inventory (3/12 slots used):'."""
    m = re.search(r"inventory \(\d+/(\d+) slots used\)", inventory_text)
    if not m:
        raise Smoke(f"inventory shows no pack size: {inventory_text.splitlines()[0]}")
    return int(m.group(1))


# Object ids of the bars Clint asks for in a tool upgrade.
BAR_IDS = {"Copper Bar": "334", "Iron Bar": "335", "Gold Bar": "336", "Iridium Bar": "337"}


def parse_trade_listing(listing: str) -> tuple[str, int, int, str] | None:
    """First `shop_list` line that costs gold plus a trade item: (name, gold, count, trade item), else None.

    Lines look like '  Copper Axe: 2000g + 5x Copper Bar' (a stock suffix may follow)."""
    m = re.search(r"^\s+(.+?): (\d+)g \+ (\d+)x (.+?)(?: \(stock \d+\))?\s*$", listing, re.M)
    if not m:
        return None
    return m.group(1), int(m.group(2)), int(m.group(3)), m.group(4)


def fresh_state() -> dict:
    """/api/state is a snapshot refreshed about 4 times a second; wait for it to catch up after a tool call."""
    time.sleep(0.6)
    return state()


def release_freeze(seat: str = "all") -> dict:
    """POST /api/release_freeze, which run_days.py uses when a day's sessions are used up: an idle seat stops holding the world frozen."""
    reply = _request("/api/release_freeze", {"seat": seat}, timeout=10)
    if not reply.get("ok"):
        raise Smoke(f"release_freeze({seat}) failed: {reply.get('text')}")
    return reply


def journal_and_sleep(diary: str) -> str:
    """#21: go_to_bed refuses until today's journal is written, so every case that sleeps writes one first."""
    ok("journal", diary=diary, lessons="Smoke lesson: water the crops before bed.", plan="## Today\nSmoke plan: water the parsnips.")
    return ok("go_to_bed", reason="smoke test")


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


@case("knowledge")
def knowledge():
    """#16: crops and item_info read the game's own data. Both are instant: no game time passes."""
    reset("Farm")
    debug("add_item", id="(O)472", qty=3)  # Parsnip Seeds, so crops has a carried seed to mark
    spring = ok("crops", season="spring")
    for needle in ("Parsnip", "Cauliflower"):
        if needle not in spring:
            raise Smoke(f"crops(spring) has no {needle!r}:\n{spring[:600]}")
    if "Will mature" not in spring:  # a spring season planned from day 1 always has crops that mature
        raise Smoke(f"crops(spring) lists no crop that matures:\n{spring[:600]}")
    if not re.search(r"you carry \d+", spring):
        raise Smoke(f"crops(spring) does not mark the Parsnip Seeds in the inventory:\n{spring[:600]}")
    summer = ok("crops", season="summer")
    if "Melon" not in summer or "Cauliflower" in summer:
        raise Smoke(f"crops(summer) should list Melon and not Cauliflower:\n{summer[:600]}")
    ok("crops")  # the current season
    good, text = call("crops", season="monsoon")
    if good:
        raise Smoke(f"crops accepted a made-up season: {text[:200]}")

    info = ok("item_info", name="Parsnip")
    if "35g" not in info:
        raise Smoke(f"item_info Parsnip should show the 35g base price:\n{info[:600]}")
    seeds = ok("item_info", name="Parsnip Seeds")
    if "plants into Parsnip" not in seeds or "4 days" not in seeds:
        raise Smoke(f"item_info Parsnip Seeds should link to the crop:\n{seeds[:600]}")
    good, text = call("item_info", name="no such thing at all")
    if good:
        raise Smoke(f"item_info accepted an unknown item: {text[:200]}")


def guard_notes(tool: str) -> list[dict]:
    """The loop guard's "guard" lines for one tool, player p1, from the viewers' feed."""
    return [e for e in _request("/api/feed")["entries"]
            if e.get("kind") == "guard" and e.get("tool") == tool and e.get("player") == "p1"]


@case("loop_guard_repeat")
def loop_guard_repeat():
    """#31: the same failing call gets a hint on the 3rd try and is refused without running on the 5th."""
    reset("Farm")
    call("close_menu")  # no shop or menu is open, so `buy` fails the same way every time; this call also ends any earlier streak
    results = []
    for i in range(1, 6):
        # the AI's `reason` differs on every call: it must not make the calls look different
        good, text = call("buy", item="Parsnip Seeds", quantity=1, reason=f"smoke test {i}")
        if good:
            raise Smoke(f"buy #{i} succeeded with no shop open: {text.splitlines()[0]}")
        results.append(text)
    for i in (0, 1):
        if "You tried this" in results[i] or results[i].startswith("Not run"):
            raise Smoke(f"buy #{i + 1} should fail plainly, got: {results[i].splitlines()[0]}")
    for i, n in ((2, 3), (3, 4)):
        if f"You tried this {n} times and it failed each time: No shop is open" not in results[i]:
            raise Smoke(f"buy #{i + 1} lacks the repeat hint for {n} tries:\n{results[i]}")
    if not results[4].startswith("Not run (repeated failure): You tried this 4 times"):
        raise Smoke(f"buy #5 should be refused without running, got:\n{results[4]}")

    types = [(n.get("args") or {}).get("type") for n in guard_notes("buy")[-3:]]
    if types != ["repeat", "repeat", "refused"]:
        raise Smoke(f"expected guard log lines repeat, repeat, refused for buy, got {types}")

    ok("close_menu")  # any other call that ran ends the streak, so the same call may be tried again
    good, text = call("buy", item="Parsnip Seeds", quantity=1, reason="smoke test after another call")
    if good or "You tried this" in text or text.startswith("Not run"):
        raise Smoke(f"buy after another call should fail plainly again, got: {text.splitlines()[0]}")


@case("loop_guard_idle")
def loop_guard_idle():
    """#31: calls that only observe, with the clock frozen, get the no-progress hint (the 25th by default)."""
    reset("Farm")
    ok("close_menu")  # a successful action resets the no-progress counter
    if not fresh_state().get("frozen"):
        raise SkipCase('the clock is not frozen while the AI thinks: set "FreezeTimeWhileThinking": true in config.json')
    hinted = 0
    for i in range(1, 31):
        if "You haven't done anything for a while" in ok("look", radius=5):
            hinted = i
            break
    if not hinted:
        raise Smoke("30 look calls in a row got no no-progress hint")
    if hinted < 20:
        raise Smoke(f"the no-progress hint came too early, after {hinted} looks")
    if not any((n.get("args") or {}).get("type") == "no_progress" for n in guard_notes("look")):
        raise Smoke("the no-progress hint is not in the guard log")


@case("travel")
def travel():
    reset("Farm")
    text = ok("travel_to", location="FarmHouse", reason="smoke test")
    if "Arrived in FarmHouse" not in text:
        raise Smoke(f"expected arrival in FarmHouse, got: {text.splitlines()[0]}")
    text = ok("travel_to", location="Farm", reason="smoke test")
    if "Arrived in Farm" not in text:
        raise Smoke(f"expected arrival on Farm, got: {text.splitlines()[0]}")


@case("farm_batch")
def farm_batch():
    """till_area -> farm_summary -> plant(area) -> farm_summary -> water_all, on three fresh tiles."""
    if fresh_state().get("weather") in ("rain", "storm"):
        raise SkipCase("rain waters new crops by itself; run this case on a dry day")
    reset("Farm")
    debug("add_item", id="(O)472", qty=9)  # Parsnip Seeds

    def summary_count(text: str, label: str) -> int:
        m = re.search(rf"^{re.escape(label)}: (\d+)", text, re.MULTILINE)
        if not m:
            raise Smoke(f"farm_summary has no '{label}' line: {text[:200]}")
        return int(m.group(1))

    runs = parse_tillable_runs(ok("look", radius=10), 3)
    if not runs:
        raise Smoke("no run of 3 tillable tiles near the farm entrance")
    x, y = runs[0][0]

    first = ok("farm_summary")
    if "Farm summary of Farm" not in first:
        raise Smoke(f"unexpected farm_summary header: {first.splitlines()[0]}")
    soil, dry = summary_count(first, "Empty tilled soil"), summary_count(first, "Unwatered crops")

    text = ok("till_area", x=x, y=y, w=3, h=1, reason="smoke test")
    m = re.search(r"Tilled (\d+) of (\d+) tiles", text)
    if not m or m.group(1) != "3" or m.group(2) != "3":
        raise Smoke(f"expected 3 of 3 tiles tilled, got: {text.splitlines()[0]}")
    if summary_count(ok("farm_summary"), "Empty tilled soil") != soil + 3:
        raise Smoke("farm_summary did not count the 3 newly tilled tiles as empty soil")

    text = ok("plant", item="Parsnip Seeds", area=[x, y, 3, 1], reason="smoke test")
    if "Planted 3 " not in text:
        raise Smoke(f"expected 3 seeds planted in the area, got: {text.splitlines()[0]}")
    after_plant = ok("farm_summary")
    if summary_count(after_plant, "Empty tilled soil") != soil:
        raise Smoke("planted tiles still counted as empty soil")
    if summary_count(after_plant, "Unwatered crops") != dry + 3:
        raise Smoke("farm_summary did not count the 3 new crops as unwatered")

    text = ok("water_all", reason="smoke test")
    m = re.search(r"Watered (\d+) of (\d+) crops", text)
    if not m or m.group(1) != m.group(2) or int(m.group(1)) < 3 or "partial=false" not in text:
        raise Smoke(f"expected every dry crop watered, got: {text.splitlines()[0]}")
    if summary_count(ok("farm_summary"), "Unwatered crops") != 0:
        raise Smoke("crops are still unwatered after water_all")


@case("villagers")
def villagers():
    reset("Farm")
    text = ok("villagers")
    if "Villagers on farm day" not in text or "Pierre" not in text:
        raise Smoke(f"villagers() should list the roster with Pierre: {text[:200]}")
    one = ok("villagers", name="Pierre")
    if "Pierre" not in one or "heart" not in one:
        raise Smoke(f"villagers(name=Pierre) has no details: {one[:200]}")
    good, text = call("villagers", name="Nobody Here")
    if good:
        raise Smoke("villagers accepted an unknown name")
    # Pierre is never on the Farm, so go=false must say where he is instead of walking off.
    good, text = call("talk_to", name="Pierre", go=False, reason="smoke test")
    if good or "not on this map" not in text:
        raise Smoke(f"talk_to(go=false) should report where Pierre is: {text[:200]}")
    if fresh_state().get("location") != "Farm":
        raise Smoke("talk_to(go=false) moved the farmer")
    if fresh_state().get("dayOfMonth", 0) % 7 == 3:
        raise SkipCase("Pierre's is closed on Wednesdays; the shop alias part needs another day")
    text = ok("travel_to", location="Pierre", reason="smoke test")  # a shop owner's name is a place alias
    if "Arrived in SeedShop" not in text:
        raise Smoke(f"travel_to(Pierre) should arrive in SeedShop, got: {text.splitlines()[0]}")


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
    journal_and_sleep("Smoke test diary: tilled, planted and watered three parsnips.")
    now = fresh_state().get("day")
    if now != day + 1:
        raise Smoke(f"day did not advance (was {day}, now {now})")


@case("ship_and_sleep")
def ship_and_sleep():
    reset("Farm")
    debug("add_item", id="(O)24", qty=3)  # Parsnip
    ok("ship", item="Parsnip", quantity=3, reason="smoke test")
    day = fresh_state().get("day")
    text = journal_and_sleep("Smoke test diary: shipped three parsnips and went to sleep.")
    m = re.search(r"shipping income (-?\d+)g", text)
    if not m or int(m.group(1)) <= 0:
        raise Smoke(f"expected positive shipping income, got: {text.splitlines()[0]}")
    if fresh_state().get("day") != day + 1:
        raise Smoke("day did not advance")


def memory_of(seat: str = "p1") -> dict:
    """What the journal tool wrote for a seat: {"lessons", "plan", "notes"} (GET /api/memory)."""
    return _request("/api/memory", timeout=10)[seat]


def lesson_lines(text: str, day: int, *, of_day: bool) -> list[str]:
    """Lines of lessons.md that belong to `day` (of_day=True) or to any other day."""
    return [ln for ln in text.splitlines() if ln.strip() and ln.startswith(f"- Day {day}: ") == of_day]


@case("journal")
def journal():
    """#21: go_to_bed needs today's journal; lessons are appended, the plan is replaced section by section."""
    reset("Farm")
    day = fresh_state().get("day")
    good, text = call("go_to_bed", reason="smoke test")
    if good or "journal" not in text:
        raise Smoke(f"go_to_bed without a journal should fail and name the journal tool, got: {text.splitlines()[0]}")
    if fresh_state().get("day") != day:
        raise Smoke("go_to_bed without a journal still ended the day")

    diary = "Smoke test diary: checked that the journal is needed before sleeping."
    for bad in ({"diary": "short", "lessons": "x", "plan": "y"},
                {"diary": diary, "lessons": "  ", "plan": "y"},
                {"diary": diary, "lessons": "x", "plan": ""},
                {"diary": diary, "lessons": "x", "plan": "y", "day": day + 1},
                {"diary": diary, "lessons": "x", "plan": "y", "day": 0}):
        if call("journal", **bad)[0]:
            raise Smoke(f"journal accepted {bad}")
    if call("go_to_bed", reason="smoke test")[0]:
        raise Smoke("a rejected journal still unlocked go_to_bed")

    fact = f"SMOKE-NOTE-{int(time.time())} Pierre closes at 17:00"  # new every run: notes.md keeps facts from earlier runs
    other_lessons = lesson_lines(memory_of()["lessons"], day, of_day=False)
    text = ok("journal", diary=diary,
              lessons="- Smoke lesson A: water first.\n2. Smoke lesson B: sell before 17:00.",
              plan="## Season\nSMOKE-SEASON goal\n## Today\nSMOKE-TODAY-1",
              notes=f"shops: {fact}")
    if f"farm day {day}" not in text or "go_to_bed" not in text:
        raise Smoke(f"unexpected journal result: {text.splitlines()[0]}")
    mem = memory_of()
    mine = lesson_lines(mem["lessons"], day, of_day=True)
    if mine != [f"- Day {day}: Smoke lesson A: water first.", f"- Day {day}: Smoke lesson B: sell before 17:00."]:
        raise Smoke(f"lessons.md should hold today's two lessons as '- Day N: ...' lines, today's lines are: {mine}")
    if lesson_lines(mem["lessons"], day, of_day=False) != other_lessons:
        raise Smoke("journal changed the lessons of other days")
    if "SMOKE-SEASON goal" not in mem["plan"] or "SMOKE-TODAY-1" not in mem["plan"]:
        raise Smoke(f"plan.md lacks the Season and Today sections:\n{mem['plan']}")
    if f"[shops, day {day}] {fact}" not in mem["notes"]:
        raise Smoke(f"notes.md lacks the fact:\n{mem['notes']}")

    # Writing again the same day replaces that day's lessons, replaces the sections it names, keeps the others, adds no duplicate fact.
    ok("journal", diary=diary, lessons="Smoke lesson C: one is enough.", plan="## Today\nSMOKE-TODAY-2",
       notes=f"shops: {fact}")
    mem = memory_of()
    if lesson_lines(mem["lessons"], day, of_day=True) != [f"- Day {day}: Smoke lesson C: one is enough."]:
        raise Smoke("a second journal the same day should replace that day's lessons, not add to them")
    if "SMOKE-TODAY-2" not in mem["plan"] or "SMOKE-TODAY-1" in mem["plan"] or "SMOKE-SEASON goal" not in mem["plan"]:
        raise Smoke(f"plan.md should have the new Today and the old Season:\n{mem['plan']}")
    if mem["notes"].count(fact) != 1:
        raise Smoke(f"the same fact was noted twice:\n{mem['notes']}")

    ok("go_to_bed", reason="smoke test")
    if fresh_state().get("day") != day + 1:
        raise Smoke("day did not advance after journal + go_to_bed")
    good, text = call("go_to_bed", reason="smoke test")
    if good or "journal" not in text:
        raise Smoke("yesterday's journal must not unlock today's go_to_bed")

    # Catching up a missed day (#22): allowed for a past day, and it does not unlock today.
    text = ok("journal", diary=diary, lessons="Smoke lesson D: caught up late.", plan="## Today\nSMOKE-TODAY-3", day=day)
    if f"farm day {day}" not in text or "still to be written" not in text:
        raise Smoke(f"a catch-up journal should say today's is still missing: {text}")
    if f"- Day {day}: Smoke lesson D: caught up late." not in memory_of()["lessons"]:
        raise Smoke("the catch-up lesson is not filed under the missed day")
    if call("go_to_bed", reason="smoke test")[0]:
        raise Smoke("a catch-up journal for an old day unlocked today's go_to_bed")


@case("pass_out")
def pass_out():
    """#22: stay up until 2:00 AM -> the freeze is released, the day rolls over, a pass_out record says why
    (cause 2am, money dropped) and journal(day=N) clears pending_journal_day. Needs an empty shipping bin
    (overnight income would hide the money lost), which holds right after the `journal` case slept."""
    reset("Farm")
    day = fresh_state().get("day")
    if _request("/api/release_freeze", {"seat": "p4"}, timeout=10).get("ok"):
        raise Smoke("release_freeze accepted the camera seat")

    debug("set_money", n=2000)
    debug("set_time", time=2550)  # 1:50 AM: the next ten-minute step is 2:00 AM, which passes the farmer out
    s = fresh_state()
    if s.get("pendingJournalDay") is not None:
        raise Smoke(f"a fresh smoke save should owe no journal, state says {s.get('pendingJournalDay')}")

    release_freeze("all")
    time.sleep(1)  # /api/state refreshes about 4 times a second
    s = fresh_state()
    p1 = next(p for p in s["players"] if p["id"] == "p1")
    if s.get("frozen") or not p1.get("released"):
        raise Smoke(f"release_freeze did not unfreeze p1 (frozen {s.get('frozen')}, released {p1.get('released')})")

    # The game clock runs from 1:50 to 2:00 AM; the farmer passes out and the next farm day starts.
    t0 = time.time()
    while time.time() - t0 < 240:
        s = state()
        if s.get("ready") and s.get("day", day) > day:
            break
        time.sleep(2)
    else:
        raise Smoke(f"farm day {day} did not end within 240s of released time (clock {s.get('time')}, frozen {s.get('frozen')})")

    # The record is written once the morning has settled.
    t0 = time.time()
    while time.time() - t0 < 90 and state().get("pendingJournalDay") is None:
        time.sleep(1)
    s = state()
    rec = s.get("passOut") or {}
    if s.get("pendingJournalDay") != day or rec.get("day") != day:
        raise Smoke(f"expected pendingJournalDay {day} and a passOut record for it, got {s.get('pendingJournalDay')} / {rec}")
    if rec.get("cause") != "2am":
        raise Smoke(f"expected cause 2am, got {rec.get('cause')} ({rec})")
    if not rec.get("location") or len(rec.get("tile") or []) != 2:
        raise Smoke(f"the record lacks location and tile: {rec}")
    if rec.get("moneyBefore") != 2000 or not rec.get("moneyAfter", 2000) < 2000:
        raise Smoke(f"expected the money to drop from 2000g, got {rec.get('moneyBefore')}g -> {rec.get('moneyAfter')}g")
    p1 = next(p for p in s["players"] if p["id"] == "p1")
    if p1.get("released") or p1.get("pendingJournalDay") != day:
        raise Smoke(f"the release should end with the farm day and p1 should owe day {day}: {p1}")
    entries = _request(f"/api/day?n={day + 1}", timeout=10)["entries"]
    if not any(e.get("kind") == "pass_out" and e.get("player") == "p1" for e in entries):
        raise Smoke("no pass_out entry in the new day's log")

    # Writing the missed day's journal clears the flag; the record itself stays for the viewers.
    ok("journal", diary="Smoke test diary: stayed up past 2 AM on purpose and passed out.",
       lessons="Smoke lesson: go home before 1 AM.", plan="## Today\nSmoke plan: sleep on time.", day=day)
    s = fresh_state()
    if s.get("pendingJournalDay") is not None or s.get("pendingJournalDays"):
        raise Smoke(f"journal(day={day}) did not clear the pending journal: {s.get('pendingJournalDay')}")


@case("briefing")
def morning_briefing():
    reset("Farm")
    debug("add_item", id="(O)24", qty=5)  # Parsnip, 35g each
    ok("ship", item="Parsnip", quantity=5, reason="smoke test")
    debug("add_mail", id="smoke_letter")
    if "📬" not in ok("inventory"):
        raise Smoke("the footer does not show the mailbox hint while a letter is waiting")
    journal_and_sleep("Smoke test diary: shipped five parsnips and left a letter in the mailbox.")
    data = {}
    for _ in range(30):  # the report is built about 1.5 s after the morning starts
        data = _request("/api/briefing?seat=p1", timeout=10)
        if data.get("ready"):
            break
        time.sleep(1)
    if not data.get("ready"):
        raise Smoke(f"briefing never became ready: {str(data)[:200]}")
    text = data.get("text", "")
    # 175g for normal-quality Parsnips; a profession bonus can raise it, so only the shape is checked.
    for pattern in (r"Parsnip x5 = \d+g", r"Mail: \d+ letters?", r"Weather today: \w+", r"Crops: "):
        if not re.search(pattern, text):
            raise Smoke(f"briefing misses /{pattern}/:\n{text}")


# Fields every stats_NNN.json has (mod/StardewMCP/Agent/DayStats.cs); runner/stats.py reads them.
STATS_FIELDS = {
    "money": ["start", "bedtime", "end"],
    "income": ["earned", "shippingBin"],
    "crops": ["planted", "harvested", "died", "unwateredAtBedtime"],
    "energy": ["bedtime"],
    "health": ["bedtime"],
    "toolCalls": ["total", "failed"],
    "skills": ["farming", "mining", "foraging", "fishing", "combat"],
    "hearts": ["total"],
    "mine": ["deepestFloor"],
}


@case("daily_stats")
def daily_stats():
    reset("Farm")
    debug("add_item", id="(O)24", qty=2)  # Parsnip
    ok("ship", item="Parsnip", quantity=2, reason="smoke test")
    day = fresh_state().get("day")
    journal_and_sleep("Smoke test diary: checking the daily stats.")

    doc = None
    for _ in range(30):  # the mod writes the file on the first settled tick of the new day
        doc = _request(f"/api/stats?n={day}").get("players", {}).get("p1")
        if doc:
            break
        time.sleep(0.5)
    if not doc:
        raise Smoke(f"/api/stats?n={day} has no p1 stats after going to bed")
    for section, keys in STATS_FIELDS.items():
        if not isinstance(doc.get(section), dict):
            raise Smoke(f"stats day {day}: missing section {section}")
        for key in keys:
            if key not in doc[section]:
                raise Smoke(f"stats day {day}: missing {section}.{key}")
    for key in ("passedOut", "passOutCause", "endedBy", "partial"):
        if key not in doc:
            raise Smoke(f"stats day {day}: missing {key}")
    if doc["endedBy"] != "bed" or doc["passedOut"]:
        raise Smoke(f"stats day {day}: expected to end in bed, got endedBy={doc['endedBy']} passedOut={doc['passedOut']}")
    if doc["toolCalls"]["total"] < 2:
        raise Smoke(f"stats day {day}: expected the ship and go_to_bed calls to be counted, got {doc['toolCalls']['total']}")
    if doc["income"]["shippingBin"] <= 0:
        raise Smoke(f"stats day {day}: shipped parsnips but the bin value is {doc['income']['shippingBin']}")
    if doc["income"]["earned"] <= 0:
        raise Smoke(f"stats day {day}: expected income from the shipped parsnips, got {doc['income']['earned']}")
    if day not in _request("/api/stats").get("days", []):
        raise Smoke(f"/api/stats does not list day {day}")


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


@case("backpack")
def backpack():
    """#2: the backpack is Pierre's BuyBackpack tile, not a shop item: look labels it, interact asks, respond buys."""
    if fresh_state().get("dayOfMonth", 0) % 7 == 3:
        raise SkipCase("Pierre's is closed on Wednesdays; run the gate on another day")
    reset("Farm")
    slots = pack_slots(ok("inventory"))
    if slots >= 36:
        raise SkipCase("the pack already has 36 slots; run the gate on an earlier save")
    price = 2000 if slots == 12 else 10000
    debug("set_money", n=price + 500)
    debug("set_time", time=1000)
    ok("travel_to", location="SeedShop", reason="smoke test")
    try:
        tiles = [(label, t) for label, ts in parse_things(ok("look", radius=12), "A") if label.startswith("Backpack upgrade") for t in ts]
        if not tiles:
            raise Smoke("look shows no 'Backpack upgrade' tile in Pierre's shop")
        label, (x, y) = tiles[0]
        if f"{price}g" not in label:
            raise Smoke(f"the tile label should show the {price}g price: {label}")
        ok("interact", x=x, y=y, reason="smoke test")
        options = question_options()
        purchase = next((i for text, i in options.items() if "purchase" in text.lower()), None)
        if purchase is None:
            raise Smoke(f"no Purchase option in the backpack question: {options}")
        ok("respond", option=purchase, reason="smoke test")
        t0 = time.time()
        while pack_slots(ok("inventory")) == slots and time.time() - t0 < 8:
            time.sleep(0.5)
        now = pack_slots(ok("inventory"))
        if now != slots + 12:
            raise Smoke(f"expected {slots + 12} slots after the purchase, have {now}")
        if fresh_state().get("money") != 500:
            raise Smoke(f"expected {price}g spent (500g left), money is {fresh_state().get('money')}g")
    finally:
        call("close_menu")


@case("tool_upgrade")
def tool_upgrade():
    """#2/#7: Clint's upgrade shop asks for gold plus bars (a trade item); buy pays both through the game's own purchase."""
    reset("Farm")
    debug("set_money", n=30000)
    try:
        debug("open_shop", id="ClintUpgrade")  # set_time doesn't move Clint to the counter
    except Smoke as e:
        raise SkipCase(f"could not open the upgrade shop here: {e}")
    try:
        found = parse_trade_listing(ok("shop_list"))
        if not found:
            raise SkipCase("Clint offers no upgrade with a trade item (every tool already upgraded?)")
        name, gold, count, bar = found
        if bar not in BAR_IDS:
            raise SkipCase(f"unknown trade item {bar}")
        debug("add_item", id=f"(O){BAR_IDS[bar]}", qty=count)
        bars, money = inventory_count(bar), fresh_state().get("money")
        ok("buy", item=name, quantity=1, reason="smoke test")
        if inventory_count(bar) != bars - count:
            raise Smoke(f"expected {count} {bar} taken as payment, {bars} -> {inventory_count(bar)}")
        if fresh_state().get("money") != money - gold:
            raise Smoke(f"expected {gold}g spent, money {money} -> {fresh_state().get('money')}")
    finally:
        call("close_menu")


@case("calendar")
def calendar():
    """#11: calendar lists the shops and the next festival; status shows tomorrow's weather and luck."""
    reset("Farm")
    now = fresh_state()
    text = ok("calendar")
    for needle in ("Today is ", "Shops (", "Next festival:"):
        if needle not in text:
            raise Smoke(f"calendar is missing \"{needle}\": {text.splitlines()[0]}")
    if now.get("season") == "spring" and now.get("dayOfMonth", 99) < 13 and "Egg Festival" not in text:
        raise Smoke("calendar does not mention the Egg Festival (spring 13)")
    status = ok("status")
    for needle in ("Tomorrow:", "Luck today:"):
        if needle not in status:
            raise Smoke(f"status is missing \"{needle}\"")
    if "Festival today:" not in text:  # an ordinary day: there is no festival to start
        good, _ = call("festival", action="start", reason="smoke test")
        if good:
            raise Smoke("festival(start) succeeded on a day without a festival")


@case("sell_to_shop")
def sell_to_shop():
    """#11: sell pays gold at once through the shop menu (ship only pays overnight)."""
    if fresh_state().get("dayOfMonth", 0) % 7 == 3:
        raise SkipCase("Pierre's is closed on Wednesdays; run the gate on another day to cover selling")
    reset("Farm")
    debug("add_item", id="(O)24", qty=3)  # Parsnip
    open_shop("SeedShop", 1000)  # Pierre's is open 9:00-17:00 (closed Wednesdays)

    before = inventory_count("Parsnip")
    money = fresh_state().get("money")
    text = ok("sell", item="Parsnip", quantity=1, reason="smoke test")
    m = re.search(r"Sold (\d+)x .+? for (\d+)g", text)
    if not m or int(m.group(2)) <= 0:
        raise Smoke(f"sell result has no positive price: {text.splitlines()[0]}")
    sold = int(m.group(1))
    if inventory_count("Parsnip") != before - sold:
        raise Smoke(f"expected {before - sold} Parsnip after selling {sold}, have {inventory_count('Parsnip')}")
    if fresh_state().get("money") != money + int(m.group(2)):
        raise Smoke("money did not rise by the reported price")

    good, text = call("sell", item="Axe", reason="smoke test")  # tools are never bought
    if good:
        raise Smoke(f"selling an Axe should fail, got: {text.splitlines()[0]}")
    ok("close_menu")


@case("choose_profession")
def choose_profession():
    """#8: level-5 profession choices wait for the AI; an unmade choice gets the default at bedtime."""
    reset("Farm")
    if "Professions:" in ok("status"):
        raise SkipCase("this save already has professions; run the gate on an earlier save")
    debug("set_skill", skill="farming", level=5)
    debug("set_skill", skill="fishing", level=5)
    journal_and_sleep("Smoke test diary: reached farming and fishing level 5 today.")

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

    journal_and_sleep("Smoke test diary: chose Tiller and left the fishing choice to the default.")
    status = ok("status")
    m = re.search(r"^Professions: (.+)$", status, re.M)
    if not m or "Tiller" not in m.group(1) or "Fisher" not in m.group(1):
        raise Smoke(f"expected Tiller (chosen) and Fisher (default) in status, got: {m.group(0) if m else status}")
    if "Pending profession choice" in status:
        raise Smoke("a profession choice is still pending after bedtime")


@case("equip")
def equip():
    """#27: a ring goes on the left hand, bait onto the rod, and status/inventory show both. Skips if the debug API cannot make the items."""
    reset("Farm")
    try:
        debug("add_item", id="(O)516")            # Small Glow Ring
        debug("add_item", id="(T)FiberglassRod")  # a rod with bait and tackle slots
        debug("add_item", id="(O)685", qty=10)    # Bait
    except Smoke as e:
        raise SkipCase(f"the debug API could not add the test items: {e}")

    text = ok("equip", item="Small Glow Ring", slot="left", reason="smoke test")
    if not text.startswith("Equipped Small Glow Ring"):
        raise Smoke(f"expected 'Equipped Small Glow Ring', got: {text.splitlines()[0]}")
    status = ok("status")
    if not re.search(r"^Equipped: left ring Small Glow Ring \u00b7 right ring ", status, re.M):
        raise Smoke(f"status does not show the ring on the left hand:\n{status}")
    if inventory_count("Small Glow Ring") != 0:
        raise Smoke("the equipped ring is still in the inventory")

    text = ok("equip", item="Bait", reason="smoke test")
    if not text.startswith("Attached 10 x Bait"):
        raise Smoke(f"expected 'Attached 10 x Bait', got: {text.splitlines()[0]}")
    inv = ok("inventory")
    if not re.search(r"^Fishing rod .*: bait Bait x10", inv, re.M):
        raise Smoke(f"inventory does not show the bait on the rod:\n{inv}")
    if inventory_count("Bait") != 0:
        raise Smoke("the attached bait is still in the inventory")


@case("special_orders")
def special_orders():
    """#27: the board lists its orders and one can be accepted. Skips while the board is not open yet or offers nothing."""
    reset("Farm")
    if "special orders" not in ok("special_orders", action="active").lower():
        raise Smoke("special_orders(active) did not answer about special orders")
    good, text = call("special_orders", action="list", reason="smoke test")
    try:
        if not good:
            if "no Special Orders board" in text:
                raise SkipCase("the Special Orders board is not open yet (fall of year 1); run the gate on a later save")
            raise Smoke(f"special_orders(list) failed: {text.splitlines()[0]}")
        if "no orders are posted" in text:
            raise SkipCase("the board has no orders posted")
        indexes = re.findall(r"^index (\d+) ", text, re.M)
        if not indexes:
            raise Smoke(f"the board text lists no 'index N' order:\n{text}")
        good, text = call("special_orders", action="accept", index=int(indexes[0]), reason="smoke test")
        if not good and "not offering Accept" in text:
            raise SkipCase("the board does not offer Accept (an order from it was already taken)")
        if not good or not text.startswith("Accepted"):
            raise Smoke(f"expected 'Accepted ...', got: {text.splitlines()[0]}")
        if "no accepted special orders" in ok("special_orders", action="active"):
            raise Smoke("the accepted order is not in special_orders(active)")
    finally:
        call("close_menu")


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


@case("wear")
def wear():
    """#54: wear puts a hat on from the inventory and gives the old one back; wardrobe shows it."""
    reset("Farm")
    try:
        debug("add_item", id="(H)0", qty=1)  # Cowboy Hat
    except Smoke as e:
        raise SkipCase(f"could not add a hat through the debug API: {e}")
    text = ok("wear", item="Cowboy Hat", reason="smoke test")
    if "put on" not in text or "Cowboy Hat" not in ok("wardrobe"):
        raise Smoke(f"the hat was not worn: {text.splitlines()[0]}")


@case("search_trash")
def search_trash():
    """#54: search_trash presses the Garbage tile; the result says who reacted (a trash_dive event is logged)."""
    reset("Farm")
    ok("travel_to", location="Town", reason="smoke test")
    good, text = call("search_trash", reason="smoke test")
    if not good:
        raise SkipCase(f"no reachable trash can: {text.splitlines()[0]}")
    if "trash can" not in text or ("saw you" not in text and "No villager reacted" not in text):
        raise Smoke(f"unexpected search_trash result: {text.splitlines()[0]}")


@case("mount")
def mount():
    """#54: mount with a horse gets on, dismount gets off; without a stable it says so."""
    reset("Farm")
    good, text = call("mount", reason="smoke test")
    if not good:
        if "no horse" in text.lower() or "Stable" in text:
            raise SkipCase("this save has no horse (build a Stable with #10 first)")
        raise Smoke(f"mount failed: {text.splitlines()[0]}")
    if "riding" not in text and "naming" not in text:
        raise Smoke(f"mount gave: {text.splitlines()[0]}")
    ok("dismount", reason="smoke test")


@case("night_events", skip="night events are random and play while asleep; check the next morning's go_to_bed result for an 'Overnight:' line")
def night_events():
    pass


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


@case("craft")
def craft():
    """craft() lists the known recipes; craft(recipe, quantity) goes through CraftingRecipe: ingredients used, items arrive."""
    reset("Farm")
    debug("add_item", id="(O)388", qty=100)  # Wood
    debug("add_item", id="(O)390", qty=50)  # Stone
    listing = ok("craft")
    if "Craftable now:" not in listing or "Missing ingredients:" not in listing:
        raise Smoke(f"craft() does not list recipes:\n{listing[:400]}")
    if call("craft", recipe="Not A Real Recipe", reason="smoke test")[0]:
        raise Smoke("craft accepted a recipe the player does not know")
    ready = re.findall(r"^  (.+?)(?: \(makes (\d+)\))?: .*\[you can craft (\d+)\]$", listing, re.M)
    if not ready:
        raise SkipCase("no known recipe can be crafted with 100 Wood and 50 Stone")
    names = [r[0] for r in ready]
    name = next((n for n in ("Wood Fence", "Chest", "Torch", "Wood Path", "Stone Fence") if n in names), names[0])
    text = ok("craft", recipe=name, quantity=1, reason="smoke test")
    if not text.startswith("Crafted 1x") or not re.search(r"\+\d+", text) or not re.search(r"-\d+", text):
        raise Smoke(f"craft {name} did not show an item gained and ingredients used: {text.splitlines()[0]}")


@case("place")
def place():
    """place puts items on free ground through Utility.tryToPlaceItem and says why a tile did not work."""
    reset("Farm")
    debug("add_item", id="(BC)130")  # Chest
    debug("add_item", id="(BC)130")  # a second one, for the occupied-tile check
    debug("add_item", id="(BC)13")  # Furnace
    runs = parse_tillable_runs(ok("look", radius=10), 3)
    if not runs:
        raise Smoke("no run of 3 free tiles near the player")
    left, _, right = runs[0]
    text = ok("place", item="Chest", tiles=[left], reason="smoke test")
    if not text.startswith("Placed 1/1"):
        raise Smoke(f"chest was not placed: {text.splitlines()[0]}")
    text = ok("place", item="Furnace", tiles=[right], reason="smoke test")
    if not text.startswith("Placed 1/1"):
        raise Smoke(f"furnace was not placed: {text.splitlines()[0]}")
    things = {pt: label for label, pts in parse_things(ok("look", radius=10), "O") for pt in pts}
    if not things.get(tuple(left), "").startswith("Chest") or not things.get(tuple(right), "").startswith("Furnace"):
        raise Smoke(f"look does not show the placed items at {left} and {right}: {things}")
    good, text = call("place", item="Chest", tiles=[left], reason="smoke test")
    if good or "already has" not in text:
        raise Smoke(f"placing on an occupied tile should fail with 'already has': {text.splitlines()[0]}")


def tool_count() -> int:
    return sum("[tool" in line for line in ok("inventory").splitlines())


@case("chest_tool")
def chest_tool():
    """chest walks to a chest and stores/takes by name and quality, in one call, through menu take/put."""
    reset("Farm")
    if inventory_stacks("Cauliflower"):
        raise SkipCase("the pack already holds Cauliflower; the case counts exact stacks")
    debug("add_item", id="(O)190", qty=5)  # Cauliflower (normal)
    debug("add_item", id="(O)190", qty=3, quality=2)  # Cauliflower (gold)
    debug("add_item", id="(O)472", qty=3)  # Parsnip Seeds, which "everything" keeps
    if inventory_stacks("Cauliflower") != {"normal": 5, "gold": 3}:
        raise Smoke(f"setup: expected 5 normal and 3 gold Cauliflower, the inventory shows {inventory_stacks('Cauliflower')}")
    seeds, tools = inventory_count("Parsnip Seeds"), tool_count()
    x, y = place_near("(BC)130")  # Chest
    at = {"x": x, "y": y, "reason": "smoke test"}
    try:
        if call("chest", action="list", reason="smoke test")[0]:
            raise Smoke("chest without a tile and without an open chest menu should fail")
        text = ok("chest", action="store", item="Cauliflower", quality="gold", **at)
        if "Stored 3x Cauliflower (gold)" not in text or inventory_stacks("Cauliflower") != {"normal": 5}:
            raise Smoke(f"storing only the gold Cauliflower went wrong:\n{text}")
        text = ok("chest", action="list", **at)
        if "3x Cauliflower (gold)" not in text:
            raise Smoke(f"list does not show the gold Cauliflower:\n{text}")
        text = ok("chest", action="store", items=["Cauliflower"], **at)
        if "5x Cauliflower (normal)" not in text or inventory_stacks("Cauliflower"):
            raise Smoke(f"storing the rest of the Cauliflower went wrong:\n{text}")
        text = ok("chest", action="take", item="Cauliflower", quality="gold", quantity=2, **at)
        if "Took 2x Cauliflower (gold)" not in text or inventory_stacks("Cauliflower") != {"gold": 2}:
            raise Smoke(f"taking 2 gold Cauliflower went wrong:\n{text}")
        text = ok("chest", action="store", item="everything", **at)
        if tool_count() != tools or inventory_count("Parsnip Seeds") != seeds:
            raise Smoke(f"store everything must keep tools and seeds:\n{text}")
        text = ok("chest", action="take", item="everything", **at)
        if inventory_stacks("Cauliflower") != {"normal": 5, "gold": 3}:
            raise Smoke(f"take everything did not bring every Cauliflower back:\n{text}")
        if "Menu open" in ok("status"):
            raise Smoke("the chest tool left a menu open")
    finally:
        call("close_menu")


@case("craft_place_chest")
def craft_place_chest():
    """craft a Chest, place it, then use it: the whole new-tool chain on one item."""
    reset("Farm")
    debug("add_item", id="(O)388", qty=120)  # Wood
    if "Chest" not in ok("craft"):
        raise SkipCase("this player does not know the Chest recipe")
    chests = inventory_count("Chest")
    text = ok("craft", recipe="Chest", quantity=99, reason="smoke test")
    if not text.startswith("Crafted") or "Stopped after" not in text:
        raise Smoke(f"crafting 99 chests should make a few and stop on ingredients: {text.splitlines()[0]}")
    if inventory_count("Chest") <= chests:
        raise Smoke("no Chest item arrived")
    runs = parse_tillable_runs(ok("look", radius=10), 3)
    if not runs:
        raise Smoke("no run of 3 free tiles near the player")
    x, y = runs[0][1]
    ok("place", item="Chest", tiles=[[x, y]], reason="smoke test")
    debug("add_item", id="(O)24", qty=2)  # Parsnip
    ok("chest", x=x, y=y, action="store", item="Parsnip", reason="smoke test")
    if inventory_count("Parsnip") != 0:
        raise Smoke("the crafted and placed chest did not take the Parsnips")


@case("cook")
def cook():
    """cook() lists the cooking recipes; cook(recipe, quantity) makes dishes in the farmhouse kitchen (CraftingRecipe, cooking)."""
    reset("Farm")
    ok("travel_to", location="FarmHouse", reason="smoke test")
    listing = ok("cook")
    if "Cooking recipes you know" not in listing or "Cookable now:" not in listing:
        raise Smoke(f"cook() does not list the cooking recipes:\n{listing[:400]}")
    if call("cook", recipe="Not A Real Dish", reason="smoke test")[0]:
        raise Smoke("cook accepted a recipe the player does not know")
    if "Fried Egg" not in listing:
        raise SkipCase("this player does not know Fried Egg")
    debug("add_item", id="(O)176", qty=4)  # Egg
    eggs, dishes = inventory_count("Egg"), inventory_count("Fried Egg")
    good, text = call("cook", recipe="Fried Egg", quantity=2, reason="smoke test")
    if not good and "no kitchen" in text:
        raise SkipCase("the farmhouse has no kitchen yet (house upgrade 1)")
    if not good or not text.startswith("Cooked 2x Fried Egg"):
        raise Smoke(f"cook Fried Egg x2 failed: {text.splitlines()[0]}")
    if inventory_count("Fried Egg") != dishes + 2 or inventory_count("Egg") != eggs - 2:
        raise Smoke(f"expected +2 Fried Egg and -2 Egg: {text.splitlines()[0]}")
    good, text = call("cook", recipe="Fried Egg", quantity=99, reason="smoke test")
    if not good or "Stopped after" not in text:
        raise Smoke(f"cooking 99 should cook what the eggs allow and stop on ingredients: {text.splitlines()[0]}")
    ok("eat", item="Fried Egg", reason="smoke test")


@case("quests")
def quests():
    """quests lists the journal and the Help Wanted board; share=true posts the journal to the plan board."""
    reset("Farm")
    text = ok("quests")
    if "Your journal (" not in text or "Help Wanted board (Town)" not in text:
        raise Smoke(f"quests does not show the journal and the board:\n{text[:500]}")
    section = None
    try:
        text = ok("quests", share=True)
        m = re.search(r'Posted to the plan board, section "(.+?)"', text)
        if not m:
            raise Smoke(f"share=true did not post to the plan board:\n{text[-300:]}")
        section = m.group(1)
        if section not in ok("plan_read"):
            raise Smoke(f"plan_read does not show the section {section!r}")
    finally:
        if section:
            call("plan_edit", section=section, text="")


@case("accept_quest")
def accept_quest():
    """accept_quest walks to the Town board and presses Accept; the request then shows in the journal as taken by you."""
    reset("Farm")
    board = parse_board(ok("quests"))
    if board is None:
        raise SkipCase("no Help Wanted request is posted today")
    if board["state"] != "free":
        raise SkipCase(f"today's request is already {board['state']} ({board['by'] or 'you'})")
    text = ok("accept_quest", reason="smoke test")
    if not text.startswith("Accepted"):
        raise Smoke(f"accept_quest did not accept: {text.splitlines()[0]}")
    after = ok("quests")
    if not any(e["type"] == "Help Wanted" and e["name"] == board["name"] for e in parse_journal(after)):
        raise Smoke(f"the journal does not list {board['name']!r} after accepting:\n{after[:500]}")
    again = parse_board(after)
    if again is None or again["state"] != "mine":
        raise Smoke(f"the board should say you took it: {again}")
    if "already took" not in ok("accept_quest", reason="smoke test"):
        raise Smoke("a second accept_quest should say you already took it")


@case("crack_geode")
def crack_geode():
    """crack_geode pays Clint 25g and draws the treasure; it refuses away from the Blacksmith and without money."""
    reset("Farm")
    debug("add_item", id="(O)535", qty=3)  # Geode
    geodes = inventory_count("Geode")
    if f"{geodes}x Geode" not in ok("crack_geode"):
        raise Smoke("crack_geode() does not list the geodes in the pack")
    good, text = call("crack_geode", item="Geode", reason="smoke test")
    if good or "Blacksmith" not in text:
        raise Smoke(f"cracking away from the Blacksmith should fail naming it: {text.splitlines()[0]}")
    debug("set_time", time=1000)
    good, text = call("travel_to", location="Blacksmith", reason="smoke test")
    if not good:
        raise SkipCase(f"the Blacksmith is not reachable today: {text.splitlines()[0]}")
    good, text = call("crack_geode", item="Geode", quantity=2, reason="smoke test")
    if not good and "Clint is not here" in text:
        raise SkipCase("Clint is not in the Blacksmith at this time")
    if not good or not text.startswith("Clint cracked 2x Geode for 50g"):
        raise Smoke(f"crack_geode x2 failed: {text.splitlines()[0]}")
    if inventory_count("Geode") != geodes - 2 or parse_money_left(text) != 1950:
        raise Smoke(f"expected 2 geodes used and 1950g left: {text.splitlines()[0]}")
    debug("set_money", n=10)
    good, text = call("crack_geode", item="Geode", reason="smoke test")
    if good or "not enough money" not in text:
        raise Smoke(f"cracking without money should fail: {text.splitlines()[0]}")


@case("trash")
def trash():
    """trash removes items, never tools, and wants confirm=true for valuables (gold quality)."""
    reset("Farm")
    debug("add_item", id="(O)168", qty=5)  # Trash
    junk = inventory_count("Trash")
    text = ok("trash", item="Trash", quantity=2, reason="smoke test")
    if not text.startswith("Trashed 2x Trash") or inventory_count("Trash") != junk - 2:
        raise Smoke(f"trashing 2 Trash went wrong: {text.splitlines()[0]}")
    good, text = call("trash", item="Axe", confirm=True, reason="smoke test")
    if good or "tool" not in text or tool_count() == 0:
        raise Smoke(f"a tool must never be trashed, even with confirm: {text.splitlines()[0]}")
    if inventory_stacks("Cauliflower"):
        raise SkipCase("the pack already holds Cauliflower; the case counts exact stacks")
    debug("add_item", id="(O)190", qty=1, quality=2)  # Cauliflower (gold)
    good, text = call("trash", item="Cauliflower", quality="gold", reason="smoke test")
    if good or "confirm=true" not in text or inventory_stacks("Cauliflower") != {"gold": 1}:
        raise Smoke(f"gold quality should need confirm and stay in the pack: {text.splitlines()[0]}")
    text = ok("trash", item="Cauliflower", quality="gold", confirm=True, reason="smoke test")
    if not text.startswith("Trashed 1x Cauliflower") or inventory_stacks("Cauliflower"):
        raise Smoke(f"confirmed trash did not remove the Cauliflower: {text.splitlines()[0]}")


@case("watch_tv")
def watch_tv():
    """watch_tv lists the channels, then watches one and reads the text the TV showed."""
    reset("Farm")
    ok("travel_to", location="FarmHouse", reason="smoke test")
    good, text = call("watch_tv", reason="smoke test")
    if not good and "no TV" in text:
        raise SkipCase("the farmhouse has no TV")
    if not good or "Channels on the TV right now" not in text:
        raise Smoke(f"watch_tv() did not list the channels: {text.splitlines()[0]}")
    if "Menu open" in ok("status"):
        raise Smoke("listing the channels left the TV question open")
    text = ok("watch_tv", channel="weather", reason="smoke test")
    if not text.startswith("Watched") or '"' not in text:
        raise Smoke(f"watching the weather channel showed no text: {text.splitlines()[0]}")
    if "Menu open" in ok("status"):
        raise Smoke("watch_tv left a dialogue open")


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


@case("fish")
def fish():
    """fish refuses without a rod and late at night; with a rod on the Beach it walks to the shore and casts."""
    reset("Farm")
    _, x, y = p1_tile()
    try:
        if inventory_count("Bamboo Pole") == 0:
            good, text = call("fish", tries=1, reason="smoke test")
            if good or "[no_rod]" not in text:
                raise Smoke(f"fish without a rod should fail with [no_rod]: {text.splitlines()[0]}")
            debug("add_item", id="(T)BambooPole", qty=1)
        ok("travel_to", location="Beach", reason="smoke test")
        text = ok("fish", tries=2, reason="smoke test")
        if "Cast 1/2" not in text:
            raise Smoke(f"fish did not report a first cast:\n{text}")
        debug("set_time", time=2450)
        good, text = call("fish", tries=1, reason="smoke test")
        if good or "[too_late]" not in text:
            raise Smoke(f"fish after 00:30 should fail with [too_late]: {text.splitlines()[0]}")
    finally:
        call("close_menu")
        debug("set_time", time=900)
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


# The Joja route (#26). joja_membership is irreversible for the farm, which is fine here: it only ever happens on the
# smoke_* copy, and the original save is reloaded afterwards. It runs before joja_buy_project, which needs the membership.
JOJA_MEMBERSHIP_PRICE = 5000
JOJA_PROJECTS = {"Bus Repair": 40000, "Minecarts": 15000, "Bridge Repair": 25000, "Greenhouse": 35000, "Panning": 20000}


def joja_is_member(projects_text: str) -> bool:
    m = re.search(r"Joja membership: (not bought|bought)", projects_text)
    if not m:
        raise Smoke(f"joja projects does not say whether the membership is bought:\n{projects_text[:400]}")
    return m.group(1) == "bought"


def joja_line(projects_text: str, name: str) -> str:
    for line in projects_text.splitlines():
        if line.strip().startswith(f"{name} ("):
            return line
    raise Smoke(f"joja projects does not list {name}:\n{projects_text[:600]}")


def reach_joja_mart() -> None:
    good, text = call("travel_to", location="JojaMart", reason="smoke test setup")
    if not good:
        raise SkipCase(f"cannot reach the JojaMart now: {text.splitlines()[0][:160]}")


@case("joja_info")
def joja_info():
    """joja projects lists the five projects with their prices; membership without confirm buys nothing and warns."""
    reset("Farm")
    text = ok("joja", action="projects")
    for name, price in JOJA_PROJECTS.items():
        if not re.search(rf"{re.escape(name)} \(\w+\): {price:,}g,", joja_line(text, name)):
            raise Smoke(f"{name} should cost {price:,}g:\n{joja_line(text, name)}")
    listed = re.findall(r"^\s+[A-Z][\w ]+ \(\w+\): [\d,]+g, ", text, re.M)
    if len(listed) != 5:
        raise Smoke(f"joja projects should list 5 projects, found {len(listed)}:\n{text[:600]}")
    if joja_is_member(text):
        raise SkipCase("this save already has the Joja membership")

    money = fresh_state().get("money")
    for args in ({"action": "membership"}, {"action": "membership", "confirm": False}):
        good, warning = call("joja", **args)
        if not good and "already complete" in warning:
            raise SkipCase("the Community Center is already complete on this save")
        if not good or "NOT BOUGHT" not in warning or "Community Center" not in warning or "LOST" not in warning:
            raise Smoke(f"membership without confirm should only warn ({args}): {warning[:300]}")
    if fresh_state().get("money") != money:
        raise Smoke("membership without confirm changed the wallet")
    if joja_is_member(ok("joja", action="projects")):
        raise Smoke("membership without confirm bought the membership")
    good, text = call("joja", action="buy_project", project="Minecarts", reason="smoke test")
    if good or "membership" not in text:
        raise Smoke(f"buy_project should need the membership first: {text[:200]}")
    if call("joja", action="buy_project", project="Nonsense", reason="smoke test")[0]:
        raise Smoke("buy_project accepted an unknown project")


@case("joja_membership")
def joja_membership():
    """joja membership with confirm=true: Morris sells it, 5,000g leave the wallet, the bundles close for everyone, it is announced."""
    reset("Farm")
    _, x, y = p1_tile()
    if joja_is_member(ok("joja", action="projects")):
        raise SkipCase("this save already has the Joja membership")
    good, text = call("joja", action="membership")
    if not good:
        raise SkipCase(f"the membership is not available on this save: {text.splitlines()[0][:160]}")
    reach_joja_mart()
    try:
        debug("set_money", n=60000)
        good, text = call("joja", action="membership", confirm=True, reason="smoke test")
        if not good:
            raise Smoke(f"membership with confirm failed: {text[:400]}")
        if "bought the Joja membership" not in text:
            raise Smoke(f"membership did not report the purchase: {text[:300]}")
        if fresh_state().get("money") != 60000 - JOJA_MEMBERSHIP_PRICE:
            raise Smoke(f"the membership should cost {JOJA_MEMBERSHIP_PRICE}g; money is {fresh_state().get('money')}, was 60000")
        if call("menu")[0]:
            raise Smoke("a menu was left open after the membership")
        if not joja_is_member(ok("joja", action="projects")):
            raise Smoke("projects does not show the membership after buying it")
        good, text = call("bundles")
        if good or "Joja" not in text:
            raise Smoke(f"the bundles should be closed after the membership: {text[:200]}")
        if call("joja", action="membership", confirm=True, reason="smoke test")[0]:
            raise Smoke("a second membership was sold")
        board = ok("plan_read")
        if "Community Center vs Joja" not in board or "Joja membership" not in board:
            raise Smoke(f"the plan board has no decision section:\n{board[:500]}")
        feed = _request("/api/feed")["entries"]
        if not any(e.get("kind") == "joja_membership" for e in feed):
            raise Smoke("the feed has no joja_membership event")
        if not any(e.get("kind") == "system" and "DECISION" in (e.get("text") or "") for e in feed):
            raise Smoke("the feed has no DECISION line")
    finally:
        call("close_menu")
        debug("warp", location="Farm", x=x, y=y)


@case("joja_buy_project")
def joja_buy_project():
    """buy_project pays the Minecarts through the game's own Joja form: 15,000g leave the wallet and the project shows as bought."""
    reset("Farm")
    _, x, y = p1_tile()
    text = ok("joja", action="projects")
    if not joja_is_member(text):
        raise SkipCase("needs the Joja membership (joja_membership buys it earlier in the same run)")
    if not joja_line(text, "Minecarts").rstrip().endswith("available."):
        raise SkipCase(f"the Minecarts are not open for purchase on this save: {joja_line(text, 'Minecarts').strip()[-60:]}")
    reach_joja_mart()
    try:
        debug("set_money", n=20000)
        text = ok("joja", action="buy_project", project="Minecarts", reason="smoke test")
        if "Minecarts" not in text or "15,000g" not in text:
            raise Smoke(f"buy_project did not report the Minecarts and the price: {text[:300]}")
        if fresh_state().get("money") != 20000 - JOJA_PROJECTS["Minecarts"]:
            raise Smoke(f"Minecarts should cost 15,000g; money is {fresh_state().get('money')}, was 20000")
        if call("menu")[0]:
            raise Smoke("the Joja form was left open after buy_project")
        line = joja_line(ok("joja", action="projects"), "Minecarts").rstrip()
        if not (line.endswith("bought, built tomorrow.") or line.endswith("built.")):
            raise Smoke(f"projects does not show the Minecarts as bought:\n{line}")
        if call("joja", action="buy_project", project="Minecarts", reason="smoke test")[0]:
            raise Smoke("the Minecarts were sold twice")
        debug("set_money", n=100)
        good, text = call("joja", action="buy_project", project="Panning", reason="smoke test")
        if good or "100g" not in text:
            raise Smoke(f"buy_project with too little gold should fail and say so: {text[:200]}")
        feed = _request("/api/feed")["entries"]
        if not any(e.get("kind") == "joja_project_bought" for e in feed):
            raise Smoke("the feed has no joja_project_bought event")
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

    if not wait(25):
        if state().get("screens", 1) <= 1:
            debug("build_cabins", n=3)
        if not wait(120):
            raise SkipCase('p2/p3 did not join: set "SplitScreen": true in config.json (and give the save 3 cabins)')
    debug("separate_wallets")  # the filming save has separate wallets; the smoke copy of a shared one needs the switch


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


@case("journal_seats")
@social
def journal_seats():
    """#21: each seat has its own journal and lessons files; p1's journal does not unlock p2's go_to_bed."""
    reset("Farm")
    diary = "Smoke test diary: the seats keep separate journals."
    ok_seat("p1", "journal", diary=diary, lessons="SMOKE-P1-LESSON", plan="## Today\nSMOKE-P1-PLAN")
    good, text = call_seat("p2", "go_to_bed", {"reason": "smoke test"})
    if good or "journal" not in text:
        raise Smoke(f"p2 slept on p1's journal, or the failure does not name the journal: {text.splitlines()[0]}")
    ok_seat("p2", "journal", diary=diary, lessons="SMOKE-P2-LESSON", plan="## Today\nSMOKE-P2-PLAN")
    p1, p2 = memory_of("p1"), memory_of("p2")
    if "SMOKE-P2" in p1["lessons"] + p1["plan"] or "SMOKE-P1" in p2["lessons"] + p2["plan"]:
        raise Smoke(f"journals of p1 and p2 mixed:\np1 {p1}\np2 {p2}")
    if "SMOKE-P1-LESSON" not in p1["lessons"] or "SMOKE-P2-LESSON" not in p2["lessons"]:
        raise Smoke("a seat's lesson is missing from its own lessons file")


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
    debug("clear_items")  # earlier cases leave p1's pack full
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

    before1, before2 = count("p1"), count("p2")  # earlier cases may leave p1 holding Parsnips
    text = ok_seat("p1", "give", to="Gemini", item="Parsnip", count=2)
    t0 = time.time()
    while count("p2") != before2 + 2 and time.time() - t0 < 4:
        time.sleep(0.3)
    if count("p2") != before2 + 2 or count("p1") != before1 - 2:
        raise Smoke(f"expected p2 +2 and p1 -2 Parsnip, got p2 {count('p2')} (was {before2}), p1 {count('p1')} (was {before1}). {text}")
    if not any("gave you 2 x Parsnip" in t for t in seen + [hear("p2")]):
        raise Smoke("p2 was not told about the gift")
    if call_seat("p1", "give", {"to": "p2", "item": "Parsnip", "count": before1})[0]:
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


def seat_money(seat: str) -> int:
    return next(p for p in fresh_state()["players"] if p["id"] == seat)["state"]["money"]


@case("wallets")
@social
def wallets():
    """Each seat spends and sees its own money: a purchase by p2 must not touch p1's wallet."""
    if fresh_state().get("dayOfMonth", 0) % 7 == 3:
        raise SkipCase("Pierre's is closed on Wednesdays; run the gate on another day to cover the shop")
    reset("Farm")
    unfreeze()
    debug_seat("p2", "set_money", n=500)
    debug_seat("p3", "set_money", n=300)
    debug("set_money", n=2000)
    time.sleep(1)  # farmhand wallets reach the host through the game's net sync
    if [seat_money(s) for s in ("p1", "p2", "p3")] != [2000, 500, 300]:
        raise Smoke(f"wallets are not separate: {[seat_money(s) for s in ('p1', 'p2', 'p3')]}")
    for seat, g in (("p2", 500), ("p3", 300)):
        if f"Money {g}g" not in ok_seat(seat, "status"):
            raise Smoke(f"{seat} status does not show its own {g}g")
    unfreeze()  # idle p2 would hold p3's purchase frozen

    debug_seat("p3", "open_shop", id="SeedShop")  # p3: the give case leaves p2's pack full
    try:
        text = ok_seat("p3", "buy", item="Parsnip Seeds", quantity=2)
    finally:
        ok_seat("p3", "close_menu")
    paid = int(re.search(r"for (\d+)g", text).group(1))
    if (seat_money("p1"), seat_money("p2"), seat_money("p3")) != (2000, 500, 300 - paid):
        raise Smoke(f"p3 paid {paid}g but wallets are {[seat_money(s) for s in ('p1', 'p2', 'p3')]}")


def theft_entries(thief: str) -> list[dict]:
    return [e for e in _request("/api/feed")["entries"] if e.get("kind") == "theft" and e.get("player") == thief]


@case("ownership")
@social
def ownership():
    """p1 owns a chest and a ripe crop; p2 takes from the chest and harvests the crop: two theft entries, owner shown."""
    reset("Farm")
    unfreeze()
    runs = parse_tillable_runs(ok("look", radius=10), 3)
    if not runs:
        raise Smoke("no run of 3 tillable tiles near the farm entrance")
    chest_tile, crop_tile = runs[0][0], runs[0][1]
    debug("place_chest", x=chest_tile[0], y=chest_tile[1])
    debug("add_item", id="(O)472", qty=1)  # Parsnip Seeds
    ok("use_tool", tool="Hoe", tiles=[crop_tile], reason="smoke test")
    ok("plant", item="Parsnip Seeds", tiles=[crop_tile], reason="smoke test")
    debug("grow_crops")

    look = ok("look", radius=10)
    if "Chest (1 items, owner p1" not in look or "Parsnip READY (owner p1)" not in look:
        raise Smoke(f"look lacks the owners:\n{look}")
    if not any(o["kind"] == "chest" and o["owner"] == "p1" and o["tile"] == chest_tile for o in fresh_state().get("owned", [])):
        raise Smoke("/api/state does not list p1's chest")

    unfreeze()
    # Land p2 on the run's third tile: the farm is overgrown after earlier cases, so walking from beside p1 may not reach the chest.
    debug_seat("p2", "warp", location="Farm", x=runs[0][2][0], y=runs[0][2][1])
    if theft_entries("p2"):
        raise Smoke("p2 has theft entries before it took anything")
    ok_seat("p2", "interact", x=chest_tile[0], y=chest_tile[1], reason="smoke test")
    try:
        ok_seat("p2", "menu", action="take", item="Parsnip", reason="smoke test")
    finally:
        ok_seat("p2", "close_menu")
    ok_seat("p2", "harvest", tiles=[crop_tile], reason="smoke test")
    unfreeze()  # p2 is now an idle AI and would hold the world (and p1's next action) frozen

    kinds = {(e["args"]["what"], e["args"]["victim"]) for e in theft_entries("p2")}
    if kinds != {("chest", "p1"), ("crop", "p1")}:
        raise Smoke(f"expected p2 to steal from p1's chest and crop, got {kinds}")

    # the owner doing the same is not a theft
    debug("add_item", id="(O)472", qty=1)
    ok("use_tool", tool="Hoe", tiles=[crop_tile], reason="smoke test")
    ok("plant", item="Parsnip Seeds", tiles=[crop_tile], reason="smoke test")
    debug("grow_crops")
    ok("harvest", tiles=[crop_tile], reason="smoke test")
    if theft_entries("p1"):
        raise Smoke("p1 harvesting its own crop was logged as theft")


@case("pay")
@social
def pay():
    reset("Farm")
    unfreeze()
    debug_seat("p2", "set_money", n=100)
    debug("set_money", n=1000)
    time.sleep(1)  # p2's wallet reaches the host through the game's net sync
    text = ok_seat("p1", "pay", to="Gemini", amount=250, reason="PAYREASON for the seeds")
    if "paid 250g" not in text or "now have 750g" not in text:
        raise Smoke(f"unexpected pay result: {text}")
    if (seat_money("p1"), seat_money("p2")) != (750, 350):
        raise Smoke(f"wallets after pay: p1 {seat_money('p1')}, p2 {seat_money('p2')}")
    heard = hear("p2")
    if "(p1) paid you 250g" not in heard or "PAYREASON" not in heard or "now have 350g" not in heard:
        raise Smoke(f"p2 was not told about the payment:\n{heard}")
    entry = [e for e in _request("/api/feed")["entries"] if e.get("kind") == "pay" and e.get("player") == "p1"]
    a = (entry[-1].get("args") or {}) if entry else {}
    if (a.get("to"), a.get("amount"), a.get("from"), a.get("receiver")) != ("p2", 250, {"before": 1000, "after": 750}, {"before": 100, "after": 350}):
        raise Smoke(f"viewers' feed has no correct pay entry: {entry[-1:] }")
    for bad in ({"to": "p2", "amount": 751}, {"to": "p2", "amount": 0}, {"to": "p2", "amount": -5}, {"to": "p1", "amount": 5}, {"to": "nobody", "amount": 5}):
        if call_seat("p1", "pay", bad)[0]:
            raise Smoke(f"pay accepted {bad}")
    if (seat_money("p1"), seat_money("p2")) != (750, 350):
        raise Smoke("a rejected pay still moved money")


@case("bedtime")
@social
def bedtime():
    """At the configured bedtime an AI that is not in bed is told once that the others wait for it."""
    reset("Farm")
    unfreeze()
    for seat in ("p1", "p2", "p3"):
        ok_seat(seat, "status")  # connect, so the seat counts as an AI that is up
    debug("set_time", time=2210)
    t0 = time.time()
    heard = ""
    while "bedtime" not in heard and time.time() - t0 < 6:
        time.sleep(1)
        heard = hear("p2")
    if "go_to_bed" not in heard:
        raise Smoke(f"p2 got no bedtime warning:\n{heard}")
    time.sleep(2.5)
    if "bedtime" in hear("p2"):
        raise Smoke("the bedtime warning was sent twice in one day")


@case("sleep_together")
@social
def sleep_together():
    """All three AIs go to bed at 20:00: the day must end by itself, not wait for the camera farmer's 2:00 AM pass-out."""
    import threading
    reset("Farm")
    unfreeze()
    for seat, x in (("p1", 63), ("p2", 65)):  # in front of the farmhouse: earlier cases leave chests that can box a farmer in
        debug_seat(seat, "warp", location="Farm", x=x, y=15)
    debug("set_time", time=2000)
    day = fresh_state()["day"]
    results: dict[str, tuple] = {}

    def sleep(seat: str) -> None:
        try:
            diary = f"Smoke test diary of {seat}: the whole team went to bed together."
            ok_seat(seat, "journal", diary=diary, lessons="Smoke lesson: sleep together.", plan="## Today\nSmoke plan: sleep.")
            results[seat] = call_seat(seat, "go_to_bed", {"reason": "smoke test"})
        except Exception as e:  # reported below
            results[seat] = (False, f"{type(e).__name__}: {e}")

    threads = [threading.Thread(target=sleep, args=(seat,)) for seat in ("p1", "p2", "p3")]
    t0 = time.time()
    for t in threads:
        t.start()
    latest = "00:00"
    while any(t.is_alive() for t in threads):
        s = state()
        if s.get("ready") and s.get("day") == day:
            latest = max(latest, s.get("time", ""))
        time.sleep(0.5)
    for t in threads:
        t.join()
    bad = {seat: r for seat, r in results.items() if not r[0]}
    if bad or len(results) != 3:
        raise Smoke(f"go_to_bed failed: {bad or results}")
    if fresh_state().get("day") != day + 1:
        raise Smoke(f"day did not advance (was {day})")
    if latest >= "24:00":
        raise Smoke(f"the day only ended at {latest}: something is still waiting for 2:00 AM")
    if time.time() - t0 > 120:
        raise Smoke(f"everyone slept but go_to_bed took {time.time() - t0:.0f}s: an idle seat held a farmer in bed")


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


@case("caught_slacking")
@social
def caught_slacking():
    """
    p2 says and writes on the plan board that it is watering, then stands in a long wait. p1's look shows what p2 is
    actually doing next to those claims. A plain say leaves the wait alone; an @mention stops it and hands p2 the message.
    """
    reset("Farm")
    loc, px, py = p1_tile()
    unfreeze()
    debug_seat("p2", "warp", location=loc, x=px + 2, y=py)
    ok_seat("p2", "plan_edit", section="Smoke work", text="p2 waters the parsnips all morning")
    ok_seat("p2", "say", text="SLACK1 off to water the parsnips")

    outcome: dict = {}

    def nap() -> None:
        try:
            outcome["call"] = call_seat("p2", "wait", {"minutes": 120, "reason": "smoke test: napping"})
        except Exception as e:  # reported on the main thread
            outcome["error"] = e

    napper = threading.Thread(target=nap, daemon=True)
    try:
        debug("perception", interrupts=1, clear=1)  # p2 arriving next to p1 was news; only what happens from now on counts
        napper.start()

        look, t0 = "", time.time()
        while time.time() - t0 < 10 and "wait 120 min" not in look:
            look = ok_seat("p1", "look", radius=12)
            time.sleep(0.4)
        line = next((ln for ln in look.splitlines() if "(p2)" in ln and "Actually doing" in ln), "")
        if "Other players in view" not in look or not line:
            raise Smoke(f"p1's look does not list p2 with what it is doing:\n{look[-600:]}")
        for needle, what in (("wait 120 min (standing still)", "the running tool"), ("napping", "its reason"),
                             ("SLACK1 off to water", "its last say"), ("waters the parsnips all morning", "its plan board section")):
            if needle not in line:
                raise Smoke(f"the line about p2 lacks {what} ({needle}):\n{line}")
        if "Other players in view" not in ok_seat("p1", "status"):
            raise Smoke("status does not list the players in view")

        ok_seat("p1", "say", text="SLACK2 hello there")
        time.sleep(1.5)
        if not napper.is_alive():
            raise Smoke(f"a say without @ stopped p2's wait: {outcome}")

        ok_seat("p1", "say", text="@p2 SLACK3 I can see you napping")
        napper.join(15)
        if napper.is_alive():
            raise Smoke("p2's wait did not stop when p1 mentioned it with @p2")
        if "error" in outcome:
            raise Smoke(f"p2's wait call failed: {outcome['error']}")
        good, text = outcome["call"]
        if not good or "Interrupted" not in text or "wait" not in text or "mentioned you" not in text:
            raise Smoke(f"p2's wait should end with an Interrupted result naming the mention:\n{text}")
        if "SLACK3" not in text:
            raise Smoke(f"the interrupted result lacks the message that caused it:\n{text}")
        if "Waited until" in text:
            raise Smoke("the wait ran to its end instead of being interrupted")
    finally:
        try:
            if napper.is_alive():
                ok_seat("p1", "say", text="@p2 smoke test over")  # interrupts are still on: this ends the wait
                napper.join(10)
        except Exception:
            pass
        debug("perception", interrupts=0, clear=1)
        try:
            ok_seat("p2", "plan_edit", section="Smoke work", text="")
        except Exception:
            pass


# ---------------------------------------------------------------------- romance and naming (#53)


def feed_events(kind_tool: str) -> list[dict]:
    """Story events (kind "event", the type in `tool`) of the overlay feed."""
    return [e for e in _request("/api/feed?after=0")["entries"] if e.get("kind") == "event" and e.get("tool") == kind_tool]


@case("naming")
def naming():
    """A naming box is answered with name(text); an unanswered one gets a random name (what go_to_bed does)."""
    reset("Farm")
    good, text = call("name", text="Whoever")
    if good or "No naming box" not in text:
        raise Smoke(f"name without a box should fail with 'No naming box', got: {text.splitlines()[0]}")
    debug("open_naming", title="Name your smoke calf:")
    try:
        status = ok("status")
        if "Naming:" not in status or "name(text)" not in status:
            raise Smoke(f"status has no Naming: line while a naming box is open:\n{status[:400]}")
        if call("name", text="x" * 25)[0]:
            raise Smoke("name accepted 25 characters")
        if call("name", text="   ")[0]:
            raise Smoke("name accepted an empty name")
        ok("name", text="Smokey")
        got = debug("last_named")
        if got != "Smokey":
            raise Smoke(f"the box's doneNaming got {got!r}, expected 'Smokey'")
        if "Naming:" in ok("status"):
            raise Smoke("status still shows Naming: after the box was answered")
        named = [e for e in feed_events("named") if (e.get("args") or {}).get("name") == "Smokey"]
        if not named or (named[-1].get("args") or {}).get("player") != "p1":
            raise Smoke("the feed has no 'named' event (what, name, player) for Smokey")

        # Nobody answers: bedtime picks a random name.
        debug("open_naming", title="Name your smoke pup:")
        random_name = debug("resolve_naming").strip()
        if random_name in ("", "(none)"):
            raise Smoke("resolve_naming left the box without a name")
        if "Naming:" in ok("status"):
            raise Smoke("status still shows Naming: after the random name")
        auto = [e for e in feed_events("named") if (e.get("args") or {}).get("chosen") is False]
        if not auto:
            raise Smoke("the feed has no 'named' event for the random name (chosen=false)")
    finally:
        call("close_menu")


@case("bouquet_dating")
def bouquet_dating():
    """villagers shows the romance requirements; bouquet refuses what the game refuses, then starts dating at 8 hearts."""
    reset("Farm")
    detail = ok("villagers", name="Abigail")
    if "Romance:" not in detail:
        raise Smoke(f"villagers(name=Abigail) has no Romance: line:\n{detail[:300]}")
    good, text = call("bouquet", name="Pierre", reason="smoke test")
    if good or "can't be dated" not in text:
        raise Smoke(f"bouquet for a villager who can't be dated should say so, got: {text.splitlines()[0]}")
    if call("bouquet", name="Nobody Here", reason="smoke test")[0]:
        raise Smoke("bouquet accepted an unknown name")
    if call("mermaid_pendant", name="Abigail", reason="smoke test")[0]:
        raise Smoke("mermaid_pendant worked before dating")

    if "dating Abigail" in detail or "engaged to Abigail" in detail or "married to Abigail" in detail:
        raise SkipCase("Abigail is already dating or married on this save; pick a fresh smoke copy")
    try:
        debug("set_friendship", npc="Abigail", points=250)
        if "can date" not in ok("villagers"):
            raise Smoke("the villagers roster marks nobody as 'can date' although Abigail is met")
        good, text = call("bouquet", name="Abigail", reason="smoke test")
        if good or "hearts" not in text:
            raise Smoke(f"bouquet at 1 heart should be refused with the hearts needed, got: {text.splitlines()[0]}")
        debug("set_friendship", npc="Abigail", points=2000)
        debug("add_item", id="(O)458", qty=1)  # Bouquet
    except Smoke as e:
        raise SkipCase(f"cannot set up 8 hearts and a Bouquet through the debug API: {e}")

    text = ok("bouquet", name="Abigail", reason="smoke test")
    if "now dating Abigail" not in text:
        raise Smoke(f"expected dating to start, got: {text[:300]}")
    if "dating" not in ok("villagers", name="Abigail").lower():
        raise Smoke("villagers does not show that you are dating Abigail")
    t0 = time.time()
    while time.time() - t0 < 4 and not feed_events("bouquet_given"):
        time.sleep(0.3)
    if not feed_events("bouquet_given") and not feed_events("dating_started"):
        raise Smoke("the feed has neither a bouquet_given nor a dating_started event")


@case("propose_player")
@social
def propose_player():
    """A player proposes to another: the other sees a Yes/No box, is told in the next result and answers with answer_proposal."""
    import threading

    reset("Farm")
    if call_seat("p1", "propose", {"to": "p1", "reason": "smoke test"})[0]:
        raise Smoke("propose accepted the player itself")
    unfreeze()  # a connected idle seat would hold the world still, and with it p1's walk
    loc, x, y = p1_tile()
    try:
        debug_seat("p2", "warp", location=loc, x=x + 1, y=y)
        debug("add_item", id="(O)801", qty=1)  # Wedding Ring
    except Smoke as e:
        raise SkipCase(f"cannot set up the ring and the neighbour through the debug API: {e}")

    result: list[tuple[bool, str]] = []

    def propose():
        try:
            result.append(call_seat("p1", "propose", {"to": "p2", "reason": "smoke test"}))
        except Exception as e:  # reported below, from the main thread
            result.append((False, f"{type(e).__name__}: {e}"))

    worker = threading.Thread(target=propose, daemon=True)
    worker.start()
    try:
        t0 = time.time()
        asked = False
        while time.time() - t0 < 60 and not asked and worker.is_alive():
            asked = any((e.get("args") or {}).get("answer") == "asked" for e in feed_events("proposal"))
            time.sleep(0.5)
        if not asked:
            worker.join(5)
            text = result[0][1] if result else "(no answer yet)"
            if "got no proposal" in text:
                raise SkipCase(f"the game started no player proposal in this save: {text.splitlines()[0]}")
            raise Smoke(f"no 'proposal' event and propose says: {text[:300]}")

        told = hear("p2")
        if "proposes" not in told or "answer_proposal" not in told:
            raise Smoke(f"p2 was not told about the proposal in its next result:\n{told[:400]}")
        said = ok_seat("p2", "answer_proposal", accept=False, reason="smoke test")
        if "said no" not in said:
            raise Smoke(f"answer_proposal(accept=false) should say so, got: {said[:200]}")
        worker.join(30)
        if worker.is_alive() or not result:
            raise Smoke("p1's propose did not return after p2 answered")
        if "said no" not in result[0][1]:
            raise Smoke(f"p1 should be told that p2 said no, got: {result[0][1][:300]}")
        if not [e for e in feed_events("proposal") if (e.get("args") or {}).get("answer") == "declined"]:
            raise Smoke("the feed has no proposal event with answer=declined")
    finally:
        for seat in ("p1", "p2"):
            call_seat(seat, "close_menu", {})


# ---------------------------------------------------------------------- context control (#30)


def mcp_initialize(query: str = "") -> None:
    """Start a new MCP session on p1, as an MCP client does first. `?breaks=1` opts that session in to session breaks."""
    global _rpc_id
    _rpc_id += 1
    msg = {"jsonrpc": "2.0", "id": _rpc_id, "method": "initialize",
           "params": {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "0"}}}
    reply = _request("/mcp" + query, msg)
    if not isinstance(reply.get("result"), dict):
        raise Smoke(f"initialize{query}: no result in reply: {str(reply)[:200]}")


@case("look_kinds_diff")
def look_kinds_diff():
    """look: smaller default window, the kinds filter on the object list, and diff mode (#30)."""
    reset("Farm")
    full = ok("look")
    m = re.search(r"Showing x (\d+)-(\d+), y (\d+)-(\d+)", full)
    if not m:
        raise Smoke(f"look has no 'Showing x..y' line:\n{full[:300]}")
    width, height = int(m.group(2)) - int(m.group(1)) + 1, int(m.group(4)) - int(m.group(3)) + 1
    if width > 17 or height > 17:
        raise Smoke(f"default look window is {width}x{height}, expected at most 17x17 (radius 8)")

    only = ok("look", kinds=["debris"])
    if "Things in view, only debris" not in only:
        raise Smoke(f"look(kinds=['debris']) did not say it is filtered:\n{only[-400:]}")
    listed = [mm.group(1) for mm in re.finditer(r"^\s+(\S) .+ x\d+: ", only, re.M)]
    stray = [sym for sym in listed if sym not in "WwSLs"]
    if stray:
        raise Smoke(f"look(kinds=['debris']) listed other things: {stray}")
    if "unknown kinds ignored" in only:
        raise Smoke("'debris' was reported as an unknown kind")
    if "unknown kinds ignored: nonsense" not in ok("look", kinds=["nonsense"]):
        raise Smoke("an unknown kind should be named in the list heading")

    # diff: till three tiles near the player, then ask for what changed
    look = ok("look")
    me = re.search(r"You @ \((\d+),(\d+)\)", look)
    runs = parse_tillable_runs(look, 3)
    if not me or not runs:
        raise Smoke("no run of 3 tillable tiles near the farm entrance")
    px, py = int(me.group(1)), int(me.group(2))
    tiles = min(runs, key=lambda r: abs(r[1][0] - px) + abs(r[1][1] - py))
    ok("use_tool", tool="Hoe", tiles=tiles, reason="smoke test")
    diff = ok("look", diff=True)
    if "Diff: nothing earlier to compare" in diff:
        raise Smoke(f"look(diff=true) fell back to the full view right after a look:\n{diff[:300]}")
    if "Changes since your last look" not in diff or ", \u2192 = x3" not in diff:
        raise Smoke(f"expected 3 tiles changing from ',' to '=', got:\n{diff[:500]}")
    if len(diff.splitlines()) > 12:
        raise Smoke(f"a diff of 3 tiles should be short, got {len(diff.splitlines())} lines")
    again = ok("look", diff=True)
    if "No changes since your last look" not in again:
        raise Smoke(f"a second diff straight away should report no changes, got:\n{again[:300]}")


@case("session_break")
def session_break():
    """A session that opted in (`/mcp?breaks=1`) gets a 'Session break:' notice at the planned time, then actions are refused (#30)."""
    reset("Farm")
    call("close_menu")
    try:
        mcp_initialize("?breaks=1")
        ok("status")  # first call of the new session: counters restart here, at 9:00
        debug("set_time", time=1205)
        text = ok("look")
        if not re.search(r"^Session break:", text, re.M):
            raise Smoke(f"no 'Session break:' line after passing 12:00:\n{text[-400:]}")
        good, refused = call("use_tool", tool="Hoe", tiles=[[0, 0]], reason="smoke test: should be refused by the session break")
        if good or not refused.startswith("Session break:"):
            raise Smoke(f"an action after the notice should be refused, got ok={good}: {refused[:200]}")
        ok("think", text="smoke test: plan for the rest of the day")  # the AI must still be able to write its plan
        ok("status")
        if len([ln for ln in ok("look").splitlines() if ln.startswith("Session break:")]) != 0:
            raise Smoke("the notice should be given once, not on every call")
    finally:
        mcp_initialize()  # a new session without ?breaks=1: breaks off again for the cases after this one


@case("run_control")
def run_control():
    """POST /api/control: pause holds a new call until resume; stop_now refuses calls until resume (#32)."""
    import threading

    def control(action: str) -> dict:
        return _request("/api/control", {"action": action}, timeout=10)

    reset("Farm")
    try:
        control("clear_stop")
        control("pause")
        held: dict = {}

        def call_status():
            try:
                held["text"] = ok("status")
            except Exception as e:  # Smoke or network: report it from the case's thread
                held["error"] = e

        t = threading.Thread(target=call_status, daemon=True)
        t.start()
        time.sleep(1.5)
        if not t.is_alive():
            raise Smoke(f"a new call was answered while paused: {held}")
        s = fresh_state()
        if not (s.get("paused") and s.get("frozen")):
            raise Smoke(f"paused but /api/state says paused={s.get('paused')} frozen={s.get('frozen')}")
        control("resume")
        t.join(15)
        if t.is_alive() or "error" in held:
            raise Smoke(f"the held call did not complete after resume: {held}")

        control("stop_now")
        good, text = call("status")
        if good or "stopped" not in text:
            raise Smoke(f"stop_now should refuse new calls, got ok={good}: {text.splitlines()[0]}")
        control("resume")
        ok("status")  # resume lifts the stop_now hold too
        control("stop_after_day")
        if not _request("/api/control", timeout=10).get("stopAfterDay"):
            raise Smoke("stop_after_day was not recorded")
    finally:
        for action in ("resume", "clear_stop"):
            try:
                control(action)
            except Exception:
                pass


def enter_shop(location: str, owner: str, hhmm: int = 1000) -> None:
    """Walk into a shop at `hhmm`; SkipCase when the door is locked or its owner isn't there (closed day)."""
    debug("set_time", time=hhmm)
    good, text = call("travel_to", location=location, reason="smoke test")
    if not good:
        raise SkipCase(f"{location} is not reachable now ({text.splitlines()[0][:120]})")
    if owner not in ok("look", radius=14):
        raise SkipCase(f"{owner} is not in {location} today (closed day)")


@case("ranch_build")
def ranch_build():
    """#10: build list, free-spot suggestions, an order Robin accepts, then 'busy' while she builds; upgrade_house guards."""
    reset("Farm")
    good, text = call("upgrade_house", reason="smoke test")
    if good:
        raise Smoke("upgrade_house worked away from Robin's shop")
    if not any(w in text for w in ("ScienceHouse", "fully upgraded", "underway")):
        raise Smoke(f"upgrade_house refusal does not say why: {text.splitlines()[0]}")

    listing = ok("build", building="list")
    for name in ("Coop", "Barn", "Silo"):
        if name not in listing:
            raise Smoke(f"build list has no {name}: {listing[:300]}")
    price = re.search(r"Coop[^\n:]*: ([\d,]+)g", listing)
    if not price:
        raise Smoke(f"build list shows no Coop price: {listing[:300]}")
    cost = int(price.group(1).replace(",", ""))

    spots = ok("build", building="Coop")
    if "No free" in spots:
        raise SkipCase("no free area for a Coop on this farm; clear some ground")
    m = re.search(r"top-left tile\): \((\d+),(\d+)\)", spots)
    if not m:
        raise Smoke(f"build(Coop) suggested no spot: {spots[:300]}")
    x, y = int(m.group(1)), int(m.group(2))

    enter_shop("ScienceHouse", "Robin")
    if "busy" in ok("build", building="list"):
        raise SkipCase("Robin is already building something in this save")
    debug("set_money", n=cost + 500)
    debug("add_item", id="388", qty=400)  # Wood
    debug("add_item", id="390", qty=200)  # Stone
    money = fresh_state().get("money")
    text = ok("build", building="Coop", x=x, y=y, reason="smoke test")
    if "Robin is building" not in text:
        raise Smoke(f"build did not start: {text.splitlines()[0]}")
    if fresh_state().get("money") != money - cost:
        raise Smoke(f"expected money {money - cost}g after the Coop, got {fresh_state().get('money')}g")
    if "Under construction" not in ok("status"):
        raise Smoke("status does not show the Coop under construction")
    good, text = call("build", building="Barn", x=x, y=y, reason="smoke test")
    if good or "busy" not in text:
        raise Smoke(f"Robin should be busy with the Coop, got: {text.splitlines()[0]}")


@case("ranch_animals")
def ranch_animals():
    """#10: Marnie's list, the shop gate, and (when a finished coop has room) buy, pet, collect and feed."""
    reset("Farm")
    listing = ok("buy_animal", type="list")
    if "Chicken" not in listing:
        raise Smoke(f"buy_animal list has no Chicken: {listing[:300]}")
    price = int(re.search(r"Chicken: ([\d,]+)g", listing).group(1).replace(",", ""))
    ok("animals")
    good, _ = call("buy_animal", type="Chicken", reason="smoke test")
    if good:
        raise Smoke("buy_animal worked away from Marnie's shop")
    if "room in Coop" not in listing and "room in Big Coop" not in listing and "room in Deluxe Coop" not in listing:
        raise SkipCase("no finished coop with room on this farm (Robin builds one over a night); gate and list were checked")

    enter_shop("AnimalShop", "Marnie")
    debug("set_money", n=price + 500)
    money = fresh_state().get("money")
    text = ok("buy_animal", type="Chicken", name="Smokey", reason="smoke test")
    if "Smokey" not in text:
        raise Smoke(f"buy_animal result does not name the animal: {text.splitlines()[0]}")
    if fresh_state().get("money") != money - price:
        raise Smoke(f"expected money {money - price}g after the chicken, got {fresh_state().get('money')}g")
    overview = ok("animals")
    if "Smokey" not in overview:
        raise Smoke(f"animals does not list the new chicken:\n{overview}")
    house = re.search(r'enter with travel_to "([^"]+)"', overview)
    if not house:
        raise Smoke("animals does not say how to enter the coop")
    ok("travel_to", location=house.group(1), reason="smoke test")
    text = ok("animals", action="pet", reason="smoke test")
    if not re.search(r"Pet [1-9]\d* of", text):
        raise Smoke(f"no animal was pet: {text.splitlines()[0]}")
    ok("animals", action="collect", reason="smoke test")  # nothing to collect from a baby: only the result shape is checked
    call("animals", action="feed", reason="smoke test")  # needs a silo with hay: result shape only


@case("tool_reasons")
def tool_reasons():
    """Reason codes (#18): a tool that fails says why in a [tag] and in structuredContent, a batch that stops early says partial."""
    reset("Farm")
    plain = call_raw("p1", "status", {})
    if "structuredContent" not in plain:
        raise SkipCase("this mod build returns no structuredContent (older than #18)")
    sc = plain["structuredContent"]
    if sc.get("ok") is not True or sc.get("reason") != "ok" or plain["content"][0]["text"].startswith("["):
        raise Smoke(f"a plain ok result should be untagged with reason ok: {str(plain)[:200]}")

    # A known failure: a tool the player does not have -> no_item, tagged, isError.
    bad = call_raw("p1", "use_tool", {"tool": "Smoke Test Tool", "tiles": [[1, 1]], "reason": "smoke test"})
    text = bad["content"][0]["text"]
    if bad["isError"] is not True or bad["structuredContent"] != {"ok": False, "reason": "no_item"} or not text.startswith("[no_item] "):
        raise Smoke(f"use_tool with a missing tool should fail as [no_item]: {str(bad)[:300]}")

    # A batch that stops early: with 2 energy use_tool stops before the first tile (the tiles are never visited).
    debug("set_stamina", n=2)
    try:
        part = call_raw("p1", "use_tool", {"tool": "Hoe", "tiles": [[1, 1], [2, 1], [3, 1]], "reason": "smoke test"})
    finally:
        debug("set_stamina", n=270)
    sc = part["structuredContent"]
    text = part["content"][0]["text"]
    if not text.startswith("[partial \u00b7 low_energy] "):
        raise Smoke(f"expected the tag [partial \u00b7 low_energy], got: {text.splitlines()[0]}")
    data = sc.get("data") or {}
    if sc.get("partial") is not True or sc.get("reason") != "low_energy" or data.get("remaining") != 3 or data.get("done") != 0:
        raise Smoke(f"expected partial/low_energy with remaining 3, got: {sc}")


CASES.append(Case("clint_pickup", lambda: None, skip="the two-day wait and Clint's counter pickup are not automated yet (#2)"))


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
        debug("perception", interrupts=0)  # a loaded save switches interrupts on; the cases walk past other seats
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
