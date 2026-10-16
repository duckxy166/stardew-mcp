"""briefing.py: ordering, plug-in sections, retries and the part-1 prompt. No game, no network, no claude CLI."""

import urllib.error

import briefing
import pytest
from briefing import Context, Section


@pytest.fixture(autouse=True)
def _clean_registry():
    briefing.clear()
    yield
    briefing.clear()


def reply(sections, ready=True, day=12):
    return {"seat": "p1", "day": day, "ready": ready, "text": "ignored when sections exist",
            "sections": [{"order": o, "name": n, "text": t} for o, n, t in sections]}


MOD = [
    (20, "date", "Farm day 12: spring 12. Weather today: rain."),
    (30, "money", "Money: 295g.\nShipped yesterday, best first: Parsnip x5 = 175g (35g each)."),
    (40, "mail", "Mail: 1 letter in the mailbox."),
    (50, "farm", "Crops: 12 planted; 6 ready to harvest."),
]


def fake_api(replies, state=None, calls=None):
    """api_get fake: /api/briefing pops the next reply (an exception instance is raised), /api/state returns state."""
    queue = list(replies)

    def api_get(path):
        if calls is not None:
            calls.append(path)
        if path.startswith("/api/briefing"):
            item = queue.pop(0) if len(queue) > 1 else queue[0]
            if isinstance(item, Exception):
                raise item
            return item
        if path == "/api/state":
            if state is None:
                raise OSError("no state")
            return state
        raise AssertionError(path)

    return api_get


def test_compose_orders_by_order_number_and_adds_header():
    data = reply(list(reversed(MOD)))
    text = briefing.compose(data)
    lines = text.splitlines()
    assert lines[0] == "Morning briefing, farm day 12"
    assert lines[1].startswith("Farm day 12")
    assert "Parsnip x5 = 175g" in text
    assert text.index("Money:") < text.index("Mail:") < text.index("Crops:")


def test_extras_slot_in_by_order_and_can_replace_a_mod_section():
    extras = [
        Section(10, "pass_out", "You passed out yesterday at 2:00. Write the missing journal first."),
        Section(35, "stats", "Yesterday +295g, average +210g."),
        Section(70, "plan", "Plan: water, then plant Cauliflower."),
        Section(50, "farm", "Farm summary from #15."),
    ]
    text = briefing.compose(reply(MOD), extras)
    order = [text.index(t) for t in ("passed out", "Farm day 12", "Money:", "Yesterday +295g", "Mail:", "Farm summary", "Plan:")]
    assert order == sorted(order)
    assert "Crops: 12 planted" not in text  # replaced by the extra named "farm"


def test_registered_runner_provider_runs_with_context_and_failures_are_skipped(capsys):
    seen = []
    briefing.register(70, "plan", lambda ctx: seen.append((ctx.day, ctx.seat, ctx.state)) or "Plan: harvest first.")
    briefing.register(35, "stats", lambda ctx: 1 / 0)
    briefing.register(10, "pass_out", lambda ctx: None)
    text = briefing.build(fake_api([reply(MOD)], state={"pendingJournalDay": 11}), day=12)
    assert text.endswith("Plan: harvest first.")
    assert seen == [(12, "p1", {"pendingJournalDay": 11})]
    assert "stats" in capsys.readouterr().out
    assert "ZeroDivisionError" not in text


def test_register_same_name_replaces():
    briefing.register(70, "plan", lambda ctx: "old")
    briefing.register(70, "Plan", lambda ctx: "new")
    sections = briefing.run_providers(Context(day=1))
    assert sections == [Section(70, "Plan", "new")]


def test_fetch_retries_until_ready_and_passes_the_seat():
    calls = []
    waits = []
    api = fake_api([reply(MOD, ready=False), reply(MOD, ready=False), reply(MOD)], calls=calls)
    data = briefing.fetch(api, "p2", attempts=5, delay=0.5, sleep=waits.append)
    assert data["ready"] is True
    assert calls == ["/api/briefing?seat=p2"] * 3
    assert waits == [0.5, 0.5]


def test_fetch_returns_last_reply_when_never_ready_and_compose_says_so():
    api = fake_api([reply(MOD, ready=False)])
    data = briefing.fetch(api, attempts=3, delay=0, sleep=lambda s: None)
    assert data is not None and data["ready"] is False
    assert "still being processed" in briefing.compose(data)


def test_fetch_gives_up_at_once_on_404_and_survives_connection_errors():
    err404 = urllib.error.HTTPError("http://x/api/briefing", 404, "Not Found", {}, None)
    waits = []
    assert briefing.fetch(fake_api([err404]), attempts=5, sleep=waits.append) is None
    assert waits == []
    flaky = fake_api([OSError("refused"), reply(MOD)])
    assert briefing.fetch(flaky, attempts=3, delay=0, sleep=lambda s: None)["ready"] is True
    assert briefing.fetch(fake_api([OSError("refused")]), attempts=2, delay=0, sleep=lambda s: None) is None


def test_reply_with_only_text_still_works():
    text = briefing.compose({"day": 3, "ready": True, "text": "Morning briefing, farm day 3\nMoney: 10g."})
    assert "Money: 10g." in text


def test_nothing_to_say_gives_empty_text_and_the_old_prompt():
    assert briefing.compose(None) == ""
    prompt = briefing.morning_prompt(7, "")
    assert "status และ look" in prompt and "วันที่ 7" in prompt


def test_morning_prompt_embeds_the_briefing_and_keeps_the_day_goal():
    text = briefing.compose(reply(MOD))
    prompt = briefing.morning_prompt(12, text)
    assert text in prompt
    assert "วันที่ 12" in prompt and "go_to_bed" in prompt
    assert "status และ look" not in prompt
