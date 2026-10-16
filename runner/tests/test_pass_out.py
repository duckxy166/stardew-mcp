"""pass_out.py: reading the pass-out record from /api/state, the Thai morning notice, and waiting for the clock to run out.

No game, no network, no `claude` CLI: states are dicts shaped like the mod's /api/state (AgentHttp.RefreshState and
PassOutRecord), and the HTTP calls, the clock and sleep are fakes.
"""

from __future__ import annotations

import json

import pass_out
import run_days

# The record exactly as PassOut.cs serializes it (AgentLog.Json: camelCase, nulls left out).
RECORD = {
    "save": "Duck_123", "player": "p1", "day": 5, "newDay": 6, "cause": "2am",
    "location": "Farm", "tile": [45, 20], "time": 2550,
    "tool": "use_tool", "reason": "รดน้ำต้นพาร์สนิป", "toolRunning": False, "toolTime": 1430,
    "health": 80, "maxHealth": 100, "energy": 12, "maxEnergy": 270,
    "moneyBefore": 2000, "moneyAfter": 1800, "itemsLost": [], "at": "2026-01-01T00:00:00.000+00:00",
}


def pending_state(record=RECORD, days=None, **extra):
    """/api/state of the morning after: p1 owes the journal of record["day"]."""
    day = record["day"]
    seat = {"id": "p1", "pendingJournalDay": day, "passOut": record, "state": {"menu": None}}
    if days:
        seat["pendingJournalDays"] = days
    state = {"ready": True, "day": day + 1, "pendingJournalDay": day, "passOut": record, "players": [seat]}
    state.update(extra)
    return state


# ---------------------------------------------------------------------- reading the state


def test_clock_label_counts_past_midnight():
    assert pass_out.clock_label(2550) == "01:50"
    assert pass_out.clock_label(2600) == "02:00"
    assert pass_out.clock_label(2400) == "00:00"
    assert pass_out.clock_label(900) == "09:00"
    assert pass_out.clock_label(1830) == "18:30"


def test_pending_days_none_one_and_many():
    assert pass_out.pending_days({"ready": True, "day": 3, "players": [{"id": "p1"}]}) == []
    assert pass_out.pending_days(pending_state()) == [5]
    many = pending_state(days=[7, 5])
    assert pass_out.pending_days(many) == [5, 7]  # oldest first


def test_pending_days_reads_the_requested_seat():
    state = {"players": [{"id": "p1"}, {"id": "p2", "pendingJournalDay": 4, "pendingJournalDays": [4]}]}
    assert pass_out.pending_days(state, "p1") == []
    assert pass_out.pending_days(state, "p2") == [4]
    assert pass_out.pending_days(state, "p3") == []


def test_p1_falls_back_to_the_top_level_fields():
    state = {"ready": True, "pendingJournalDay": 2, "passOut": dict(RECORD, day=2)}
    assert pass_out.pending_days(state) == [2]
    assert pass_out.pending_record(state)["day"] == 2
    assert pass_out.pending_days(state, "p2") == []


def test_pending_record_needs_a_pending_day():
    assert pass_out.pending_record(pending_state()) == RECORD
    # the mod keeps the latest record after the journal is written, but then nothing is pending
    done = {"ready": True, "players": [{"id": "p1", "passOut": RECORD}]}
    assert pass_out.pending_record(done) is None
    # a record for another day than the oldest pending one is not the one to show
    assert pass_out.pending_record(pending_state(days=[3, 5])) is None


def test_menu_open():
    assert not pass_out.menu_open({"players": [{"id": "p1", "state": {"menu": None}}]})
    assert pass_out.menu_open({"players": [{"id": "p1", "state": {"menu": "ShippingMenu"}}]})
    assert not pass_out.menu_open({"players": [{"id": "p1"}]})
    assert not pass_out.menu_open({"ready": False})


# ---------------------------------------------------------------------- the morning notice


def test_notice_says_what_happened_and_asks_for_the_journal_first():
    text = pass_out.morning_notice(RECORD, "21:00 action walk_to: กลับบ้าน")
    assert text.startswith("เมื่อคืน (จบวันที่ 5) คุณไม่ได้เข้านอน และสลบเพราะยังไม่ได้เข้านอนจนถึงตี 2")
    assert "Farm (45,20)" in text and "01:50" in text
    assert "พลังชีวิต 80/100" in text and "พลังงาน 12/270" in text
    assert "เงิน: 2000g → 1800g (เสียไป 200g)" in text
    assert "ของที่หายไป: ไม่มี" in text
    assert "ยืนเฉย ๆ ตั้งแต่ 14:30" in text and "use_tool" in text
    assert "สิ่งที่ทำช่วงท้ายของวันที่ 5:\n21:00 action walk_to: กลับบ้าน" in text
    assert "journal(day=5, diary, lessons, plan)" in text
    assert "วันที่ 6" in text
    assert text.index("journal(day=5") > text.index("สิ่งที่ทำช่วงท้าย")  # the ask comes last


def test_notice_for_a_death_in_the_mines_lists_the_lost_items():
    rec = dict(RECORD, cause="hp0", location="UndergroundMine23", tile=[10, 8], time=1415, health=0,
               tool="use_tool", reason="สู้กับค้างคาว", toolRunning=True, itemsLost=["Copper Ore x14", "Salad"],
               moneyBefore=900, moneyAfter=810)
    text = pass_out.morning_notice(rec)
    assert "สลบเพราะพลังชีวิตหมด" in text
    assert "UndergroundMine23 (10,8) เวลา 14:15" in text
    assert "ก่อนสลบกำลังทำ: use_tool — \"สู้กับค้างคาว\"" in text
    assert "ของที่หายไป: Copper Ore x14, Salad" in text
    assert "เสียไป 90g" in text
    assert "สิ่งที่ทำช่วงท้าย" not in text  # no log lines given


def test_notice_does_not_claim_a_loss_when_the_money_did_not_drop():
    rec = dict(RECORD, moneyBefore=500, moneyAfter=650)  # shipping income came in overnight
    text = pass_out.morning_notice(rec)
    assert "เงิน: 500g → 650g" in text
    assert "เสียไป" not in text
    assert "รวมรายได้จากของที่ส่งขายข้ามคืน" in text


def test_notice_without_any_action_and_for_unknown_causes():
    rec = dict(RECORD, cause="something-new", tool=None, reason=None)
    rec.pop("tool"), rec.pop("reason")
    text = pass_out.morning_notice(rec)
    assert "สลบเพราะเหตุที่ไม่ทราบแน่ชัด" in text
    assert "ก่อนสลบยังไม่ได้ทำอะไรเลยทั้งวัน" in text


def test_notice_mentions_other_days_that_still_need_a_journal():
    text = pass_out.morning_notice(RECORD, other_days=[6, 7])
    assert "ยังมี journal ที่ค้างอยู่ของวันที่ 6, 7" in text


def test_with_morning_notice_puts_the_notice_in_front_of_the_day_prompt():
    asked = []

    def actions(day):
        asked.append(day)
        return "20:00 action walk_to: x"

    out = pass_out.with_morning_notice("PROMPT", pending_state(), actions)
    assert asked == [5]  # the actions of the lost day, not of today
    assert out.endswith("\n\nPROMPT")
    assert out.startswith("เมื่อคืน (จบวันที่ 5)")
    assert "20:00 action walk_to: x" in out


def test_with_morning_notice_leaves_a_normal_morning_alone():
    state = {"ready": True, "day": 4, "players": [{"id": "p1"}]}
    assert pass_out.with_morning_notice("PROMPT", state, lambda d: "never asked") == "PROMPT"


def test_with_morning_notice_without_details_still_asks_for_the_journals():
    state = {"ready": True, "players": [{"id": "p1", "pendingJournalDays": [3, 4]}]}
    out = pass_out.with_morning_notice("PROMPT", state, lambda d: "")
    assert "ค้างอยู่ (3, 4)" in out and "journal(day=N" in out and out.endswith("PROMPT")


def test_pending_reminder():
    assert pass_out.pending_reminder({"ready": True, "players": [{"id": "p1"}]}) == ""
    text = pass_out.pending_reminder(pending_state())
    assert "ยังไม่ได้เขียน journal ของวันที่ 5" in text
    assert text.startswith("\n\n")  # appended to a prompt


def test_the_notice_reads_the_logs_run_days_writes_for_the_lost_day(tmp_path):
    entries = [
        {"kind": "action", "tool": "walk_to", "text": "เดินกลับบ้าน", "gameTime": "23:50"},
        {"kind": "result", "tool": "walk_to", "text": "Arrived at (3,11).", "gameTime": "23:58"},
        {"kind": "think", "text": "ยังไม่ง่วง ขอรดน้ำอีกรอบ", "gameTime": "24:00"},
    ]
    (tmp_path / "day_005.jsonl").write_text("\n".join(json.dumps(e) for e in entries) + "\n", encoding="utf-8")
    out = pass_out.with_morning_notice("PROMPT", pending_state(), lambda d: run_days.last_actions(tmp_path, d))
    assert "23:50 action walk_to: เดินกลับบ้าน" in out
    assert "24:00 think : ยังไม่ง่วง ขอรดน้ำอีกรอบ" in out


# ---------------------------------------------------------------------- waiting on the game


class FakeGame:
    """A fake clock, sleep, state and POST: each get_state() moves the fake clock on and plays the next scripted state."""

    def __init__(self, states, fail_post=False):
        self.states = list(states)
        self.now = 0.0
        self.posts = []
        self.lines = []
        self.fail_post = fail_post

    def sleep(self, seconds):
        self.now += seconds

    def clock(self):
        return self.now

    def get_state(self):
        return self.states.pop(0) if len(self.states) > 1 else self.states[0]

    def post(self, path, body):
        self.posts.append((path, body, self.now))
        if self.fail_post:
            raise OSError("connection refused")
        return {"ok": True}

    def run(self, day=5, **kwargs):
        return pass_out.let_time_run_out(day, self.get_state, self.post, self.sleep, now=self.clock,
                                         log=self.lines.append, **kwargs)


def up(day, frozen=False):
    return {"ready": True, "day": day, "frozen": frozen}


def test_release_then_wait_until_the_day_changes():
    game = FakeGame([up(5, frozen=True), up(5), up(5), up(6)])
    state = game.run(player="p1", poll=3.0)
    assert state["day"] == 6
    assert game.posts == [("/api/release_freeze", {"seat": "p1"}, 0.0)]  # once: it is no longer frozen
    assert any("passed out" in line for line in game.lines)


def test_release_again_while_the_game_still_says_frozen():
    game = FakeGame([up(5, frozen=True)] * 30 + [up(6)])
    game.run(poll=3.0, repost_every=30.0)
    times = [t for _, _, t in game.posts]
    assert len(times) >= 3 and times[0] == 0.0
    assert all(b - a >= 30.0 for a, b in zip(times, times[1:]))  # never more often than repost_every


def test_gives_up_after_the_timeout_and_returns_the_last_state():
    game = FakeGame([up(5)])
    state = game.run(timeout=60.0, poll=5.0)
    assert state["day"] == 5
    assert game.now >= 60.0
    assert any("still day 5" in line for line in game.lines)


def test_a_state_that_is_not_ready_is_not_a_new_day():
    game = FakeGame([up(5), {"ready": False}, {"ready": False, "day": 6}, up(6)])
    assert game.run(poll=1.0)["ready"] is True


def test_old_mod_without_the_endpoint_is_reported_not_raised():
    game = FakeGame([up(5)], fail_post=True)
    state = game.run()
    assert state["day"] == 5
    assert any("could not release the freeze" in line for line in game.lines)
    assert game.now == 0.0  # it did not sit waiting


def test_wait_settled_waits_for_menus_to_close_then_a_grace():
    shipping = {"ready": True, "day": 6, "players": [{"id": "p1", "state": {"menu": "ShippingMenu"}}]}
    morning = pending_state()
    game = FakeGame([{"ready": False}, shipping, shipping, morning])
    state = pass_out.wait_settled(game.get_state, game.sleep, now=game.clock, poll=1.0, grace=2.0)
    assert pass_out.pending_days(state) == [5]  # the record is there after the grace
    assert game.now == 3 + 2.0  # three polls, then the grace


def test_wait_settled_gives_up_after_the_timeout():
    stuck = {"ready": True, "day": 6, "players": [{"id": "p1", "state": {"menu": "ShippingMenu"}}]}
    game = FakeGame([stuck])
    state = pass_out.wait_settled(game.get_state, game.sleep, now=game.clock, poll=1.0, timeout=10.0, grace=0.0)
    assert state is stuck
    assert 10.0 <= game.now < 12.0
