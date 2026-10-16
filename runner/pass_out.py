"""Passing out (#22): let the clock run out, then make the AI learn from it in the next morning's journal.

The mod (PassOut.cs) notices a day that ended without go_to_bed and puts the facts into /api/state:

    pendingJournalDay    the farm day N whose journal is still owed (cleared by journal(day=N))
    pendingJournalDays   every such day, oldest first (only when there are any)
    passOut              the record of that day: cause (2am | hp0 | exhausted | other), location, tile, clock,
                         the action that was running, money before / after, items lost

Both are also inside each entry of state["players"], so the same code serves every seat (`player`).

This module (standard library only, no game, no network, no `claude` CLI; the API calls are passed in):

  * waits for the clock to run out when a day's sessions are used up       -> let_time_run_out(), after POST /api/release_freeze
  * waits for the night to settle so the record is there before the prompt -> wait_settled()
  * writes the Thai morning notice that asks for journal(day=N) first      -> morning_notice(), with_morning_notice()
  * reminds the AI mid-day when that journal is still missing              -> pending_reminder()
"""

from __future__ import annotations

import time
from typing import Callable

CAUSE_TH = {
    "2am": "ยังไม่ได้เข้านอนจนถึงตี 2",
    "hp0": "พลังชีวิตหมด",
    "exhausted": "พลังงานหมดจนหมดแรง",
    "other": "เหตุที่ไม่ทราบแน่ชัด",
}

GetState = Callable[[], dict]
Post = Callable[[str, dict], object]


# ---------------------------------------------------------------------- reading /api/state


def clock_label(time_of_day: int) -> str:
    """Game clock HHMM as a 24 h label: 2550 (the game counts past midnight) is "01:50", 900 is "09:00"."""
    t = int(time_of_day)
    return f"{t // 100 % 24:02}:{t % 100:02}"


def seat_view(state: dict, player: str = "p1") -> dict:
    """The player's entry in state["players"]; for p1 the top-level fields when the entry is missing (older mod)."""
    for p in state.get("players") or []:
        if p.get("id") == player:
            return p
    return state if player == "p1" else {}


def pending_days(state: dict, player: str = "p1") -> list[int]:
    """Farm days whose journal the player still owes after passing out, oldest first."""
    view = seat_view(state, player)
    days = view.get("pendingJournalDays")
    if days:
        return sorted(int(d) for d in days)
    day = view.get("pendingJournalDay")
    return [int(day)] if day else []


def pending_record(state: dict, player: str = "p1") -> dict | None:
    """The pass-out record of the oldest pending day, or None when nothing is pending."""
    days = pending_days(state, player)
    record = seat_view(state, player).get("passOut")
    if days and isinstance(record, dict) and record.get("day") == days[0]:
        return record
    return None


def menu_open(state: dict, player: str = "p1") -> bool:
    """A menu (shipping screen, level-up, dialogue...) is open on the player's screen."""
    screen = seat_view(state, player).get("state")
    return bool(isinstance(screen, dict) and screen.get("menu"))


# ---------------------------------------------------------------------- the morning notice


def describe_tool(rec: dict) -> str:
    """What the farmer was doing, from the record's tool fields."""
    tool = rec.get("tool")
    if not tool:
        return "ก่อนสลบยังไม่ได้ทำอะไรเลยทั้งวัน"
    if rec.get("toolRunning"):
        reason = f' — "{rec["reason"]}"' if rec.get("reason") else ""
        return f"ก่อนสลบกำลังทำ: {tool}{reason}"
    return (f"ก่อนสลบไม่ได้ทำอะไรอยู่ ยืนเฉย ๆ ตั้งแต่ {clock_label(rec.get('toolTime', 0))} "
            f"(งานสุดท้ายคือ {tool})")


def describe_money(rec: dict) -> str:
    before, after = int(rec.get("moneyBefore", 0)), int(rec.get("moneyAfter", 0))
    line = f"เงิน: {before}g → {after}g"
    if after < before:
        line += f" (เสียไป {before - after}g)"
    line += " ตัวเลขหลังรวมรายได้จากของที่ส่งขายข้ามคืนแล้ว"
    return line


def morning_notice(rec: dict, recent_actions: str = "", other_days: list[int] | tuple = ()) -> str:
    """Thai text for the first prompt of the day after a pass-out: what happened, then "write journal(day=N) first"."""
    day = rec["day"]
    cause = CAUSE_TH.get(rec.get("cause", "other"), CAUSE_TH["other"])
    tile = rec.get("tile") or []
    where = f"{rec.get('location', '?')}" + (f" ({tile[0]},{tile[1]})" if len(tile) >= 2 else "")
    lines = [
        f"เมื่อคืน (จบวันที่ {day}) คุณไม่ได้เข้านอน และสลบเพราะ{cause}",
        f"ตอนนั้นอยู่ที่ {where} เวลา {clock_label(rec.get('time', 0))} "
        f"พลังชีวิต {rec.get('health', '?')}/{rec.get('maxHealth', '?')} พลังงาน {rec.get('energy', '?')}/{rec.get('maxEnergy', '?')}",
        describe_money(rec),
    ]
    items = rec.get("itemsLost") or []
    lines.append("ของที่หายไป: " + (", ".join(items) if items else "ไม่มี"))
    lines.append(describe_tool(rec))
    if recent_actions.strip():
        lines.append(f"สิ่งที่ทำช่วงท้ายของวันที่ {day}:\n{recent_actions.strip()}")
    lines.append(
        f"สิ่งแรกที่ต้องทำก่อนเล่นต่อ: เรียก journal(day={day}, diary, lessons, plan) เขียนบันทึกของวันที่ {day} ให้ครบ "
        "โดยใน lessons ให้เขียนว่าทำไมถึงสลบ และครั้งหน้าจะทำอะไรต่างออกไป (เช่น เริ่มกลับบ้านก่อนตี 1) "
        f"แล้วค่อยวางแผนและเล่นวันที่ {rec.get('newDay', day + 1)} ต่อ"
    )
    if other_days:
        lines.append("ยังมี journal ที่ค้างอยู่ของวันที่ " + ", ".join(str(d) for d in other_days)
                     + " ด้วย เขียนให้ครบทีละวัน (journal(day=...))")
    return "\n".join(lines)


def with_morning_notice(prompt: str, state: dict, recent_actions: Callable[[int], str], player: str = "p1") -> str:
    """The first prompt of a day, with the pass-out notice in front when the previous day ended that way.

    `recent_actions(day)` gives the last actions of that day from the log (run_days.last_actions).
    """
    days = pending_days(state, player)
    if not days:
        return prompt
    rec = pending_record(state, player)
    if rec is None:  # the days are known but not the details: still ask for the journals
        listed = ", ".join(str(d) for d in days)
        notice = (f"เมื่อคืนคุณไม่ได้เข้านอนและสลบ สิ่งแรกที่ต้องทำก่อนเล่นต่อ: เรียก journal(day=N, diary, lessons, plan) "
                  f"เขียนบันทึกของวันที่ค้างอยู่ ({listed}) ให้ครบ โดยใน lessons ให้เขียนว่าทำไมถึงสลบและครั้งหน้าจะเลี่ยงอย่างไร")
    else:
        notice = morning_notice(rec, recent_actions(rec["day"]), days[1:])
    return f"{notice}\n\n{prompt}"


def pending_reminder(state: dict, player: str = "p1") -> str:
    """For a later part of the same day: a line when a pass-out journal is still unwritten ("" when nothing is owed)."""
    days = pending_days(state, player)
    if not days:
        return ""
    listed = ", ".join(str(d) for d in days)
    return (f"\n\nอย่าลืม: ยังไม่ได้เขียน journal ของวันที่ {listed} (วันที่สลบ) ให้เรียก journal(day=...) "
            "เขียนว่าทำไมถึงสลบก่อนทำอย่างอื่น")


# ---------------------------------------------------------------------- waiting on the game


def wait_settled(get_state: GetState, sleep: Callable[[float], None] = time.sleep, *, player: str = "p1",
                 timeout: float = 90.0, grace: float = 2.0, poll: float = 1.0,
                 now: Callable[[], float] = time.monotonic) -> dict:
    """Wait until the world is loaded and no menu is open on the player's screen, then a short grace.

    The mod writes the pass-out record when the morning has settled (no menu, no fade, the farmer can move), so
    reading /api/state right after a day change could miss it. Returns the last state, settled or not.
    """
    t0 = now()
    state = get_state()
    while not (state.get("ready") and not menu_open(state, player)) and now() - t0 < timeout:
        sleep(poll)
        state = get_state()
    sleep(grace)
    return get_state()


def let_time_run_out(day: int, get_state: GetState, post: Post, sleep: Callable[[float], None] = time.sleep, *,
                     player: str = "p1", timeout: float = 1800.0, poll: float = 3.0, repost_every: float = 30.0,
                     log: Callable[[str], None] = print, now: Callable[[], float] = time.monotonic) -> dict:
    """The day's sessions are used up and the day has not ended: release the freeze and wait for the farmer to pass out.

    Posts /api/release_freeze for the seat, then polls until the farm day is past `day`. While the game still reports
    `frozen` it posts again every `repost_every` seconds. Gives up after `timeout` seconds (real time; the game needs
    about 15 minutes from 6:00 AM to 2:00 AM) and returns the last state, so the caller can stop for a human.
    """
    try:
        post("/api/release_freeze", {"seat": player})
    except Exception as e:  # an old mod build has no such endpoint: nothing more we can do
        log(f"  could not release the freeze ({type(e).__name__}: {e}); is the mod up to date?")
        return get_state()
    log(f"  day {day} not finished: the freeze is released, waiting for the farmer to pass out (up to {int(timeout)}s)")

    t0 = last_post = now()
    state = get_state()
    while not (state.get("ready") and int(state.get("day", 0)) > day):
        if now() - t0 >= timeout:
            log(f"  still day {day} after {int(timeout)}s with time released")
            return state
        sleep(poll)
        state = get_state()
        if state.get("ready") and state.get("frozen") and now() - last_post >= repost_every:
            try:
                post("/api/release_freeze", {"seat": player})
            except Exception:
                pass
            last_post = now()
    log(f"  farm day {state.get('day')} started (the farmer passed out)")
    return state
