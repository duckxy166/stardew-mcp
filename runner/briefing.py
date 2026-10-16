"""Morning briefing for the part-1 prompt of a day (#29).

The mod serves one ordered briefing per AI seat at GET /api/briefing?seat=p1 (overnight results, shipping by
item, mail, weather, farm). This module fetches it, merges the runner's own optional inserts and builds the
part-1 prompt, so the AI does not spend its first turns on status/look to find out what changed.

Two kinds of SECTION PROVIDERS share one numbering, so the final text has one order:

  * mod side   Briefing.Register(order, name, () => text)   in mod/StardewMCP/Agent/Briefing.cs
  * runner side register(order, name, fn)                   here; fn(Context) returns text or None

Built-in orders on the mod side: 20 date, 30 money, 37 skills, 40 mail, 50 farm. Free slots for the runner or
the mod: 10 pass-out reflection (#22), 35 stats line (#25), 60 calendar (#11), 70 plan (#21). A runner section
with the same name as a mod section replaces it. Standard library only.
"""

from __future__ import annotations

import time
import urllib.error
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable


@dataclass(frozen=True)
class Section:
    order: int
    name: str
    text: str


@dataclass
class Context:
    """What a runner-side provider may look at."""

    day: int
    seat: str = "p1"
    out_dir: Path | None = None
    #: The mod's /api/state at the start of the day (pending journal day, money, ...); {} when unknown.
    state: dict = field(default_factory=dict)


Provider = Callable[[Context], "str | None"]

_PROVIDERS: dict[str, tuple[int, str, Provider]] = {}


def register(order: int, name: str, fn: Provider) -> None:
    """Add a runner-side section, or replace the one with this name. Lower order comes first."""
    _PROVIDERS[name.lower()] = (order, name, fn)


def unregister(name: str) -> None:
    _PROVIDERS.pop(name.lower(), None)


def clear() -> None:
    _PROVIDERS.clear()


def run_providers(ctx: Context) -> list[Section]:
    """Run the registered runner-side providers. One that raises is skipped (a briefing must never stop a day)."""
    sections = []
    for order, name, fn in list(_PROVIDERS.values()):
        try:
            text = fn(ctx)
        except Exception as e:  # noqa: BLE001 - any plug-in bug
            print(f"  briefing: section {name!r} failed: {type(e).__name__}: {e}")
            continue
        if text and text.strip():
            sections.append(Section(order, name, text.strip()))
    return sections


def mod_sections(data: dict | None) -> list[Section]:
    """The sections of a /api/briefing reply; a reply with only `text` becomes one section at order 0."""
    if not data:
        return []
    out = []
    for s in data.get("sections") or []:
        text = str(s.get("text") or "").strip()
        if text:
            out.append(Section(int(s.get("order", 0)), str(s.get("name") or "section"), text))
    if not out and str(data.get("text") or "").strip():
        out.append(Section(0, "briefing", str(data["text"]).strip()))
    return out


def merge(mod: Iterable[Section], extras: Iterable[Section]) -> list[Section]:
    """Order sections by (order, name); an extra with the name of a mod section replaces it."""
    by_name: dict[str, Section] = {}
    for s in mod:
        by_name[s.name.lower()] = s
    for s in extras:
        by_name[s.name.lower()] = s
    return sorted(by_name.values(), key=lambda s: (s.order, s.name.lower()))


def compose(data: dict | None, extras: Iterable[Section] = ()) -> str:
    """The briefing text: a header line, then every section in order. Empty string when there is nothing to say."""
    sections = merge(mod_sections(data), extras)
    if not sections:
        return ""
    day = (data or {}).get("day")
    header = f"Morning briefing, farm day {day}" if day else "Morning briefing"
    lines = [header] + [s.text for s in sections]
    if data is not None and data.get("ready") is False:
        lines.append("(The night was still being processed when this was read, so the overnight details may be missing. "
                     "Use status and look to check.)")
    return "\n".join(lines)


def fetch(api_get: Callable[[str], Any], seat: str = "p1", *, attempts: int = 15, delay: float = 1.0,
          sleep: Callable[[float], None] = time.sleep) -> dict | None:
    """Ask the mod for the briefing, retrying while it says the night is not processed yet.

    Returns the reply (also when it is still not ready after the last attempt), or None when the mod has no
    such endpoint (an older build, HTTP 404) or cannot be reached at all.
    """
    last: dict | None = None
    for i in range(attempts):
        try:
            data = api_get(f"/api/briefing?seat={seat}")
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            data = None
        except Exception:  # noqa: BLE001 - connection trouble: try again, then give up quietly
            data = None
        if isinstance(data, dict):
            last = data
            if data.get("ready") is not False:
                return data
        if i + 1 < attempts:
            sleep(delay)
    return last


def build(api_get: Callable[[str], Any], day: int, seat: str = "p1", *, out_dir: Path | None = None,
          attempts: int = 15, delay: float = 1.0, sleep: Callable[[float], None] = time.sleep) -> str:
    """Fetch the mod's briefing and merge the runner-side sections; "" when neither has anything."""
    data = fetch(api_get, seat, attempts=attempts, delay=delay, sleep=sleep)
    try:
        state = api_get("/api/state")
    except Exception:  # noqa: BLE001
        state = {}
    ctx = Context(day=day, seat=seat, out_dir=out_dir, state=state if isinstance(state, dict) else {})
    return compose(data, run_providers(ctx))


def morning_prompt(day: int, briefing: str | None) -> str:
    """The part-1 prompt of a day. Without a briefing it is the original "start with status and look" prompt."""
    if not briefing:
        return (f"วันนี้คือวันที่ {day} ของชาเลนจ์ 100 วัน เริ่มจาก status และ look แล้ว think แผนของวันเป็นภาษาไทย "
                "จากนั้นเล่นทั้งวันจนเข้านอนด้วย go_to_bed")
    return (f"วันนี้คือวันที่ {day} ของชาเลนจ์ 100 วัน นี่คือสรุปเช้านี้จากเกม "
            "(ผลขายเมื่อวาน จดหมาย อากาศ และฟาร์ม อ่านแล้วไม่ต้องเรียก status เพื่อหาข้อมูลเหล่านี้ซ้ำ):\n\n"
            f"{briefing}\n\n"
            "ใช้ look ดูรอบตัว แล้ว think แผนของวันเป็นภาษาไทย จากนั้นเล่นทั้งวันจนเข้านอนด้วย go_to_bed")
