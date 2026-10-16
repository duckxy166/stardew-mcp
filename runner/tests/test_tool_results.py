"""Tests for runner/tool_results.py and the pieces of smoke.py / run_days.py that use it. No game, no network."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

import pytest

RUNNER = Path(__file__).resolve().parent.parent
REPO = RUNNER.parent
sys.path.insert(0, str(RUNNER))

import run_days  # noqa: E402
import smoke  # noqa: E402
import tool_results as tr  # noqa: E402

TOOL_RESULT_CS = REPO / "mod" / "StardewMCP" / "Agent" / "ToolResult.cs"
PARTIAL = "[partial · low_energy]"


def reply(text: str, is_error: bool, structured: dict | None) -> dict:
    result = {"content": [{"type": "text", "text": text}], "isError": is_error}
    if structured is not None:
        result["structuredContent"] = structured
    return result


# ---------------------------------------------------------------------- vocabulary


def csharp_reasons() -> tuple[dict[str, str], list[str]]:
    """The `Reasons` constants (name -> code) and the names listed in `Reasons.All`, parsed from ToolResult.cs."""
    src = TOOL_RESULT_CS.read_text(encoding="utf-8")
    block = src[src.index("public static class Reasons"):src.index("public partial class CallContext")]
    consts = dict(re.findall(r'public const string (\w+) = "([a-z_]+)";', block))
    listed = re.search(r"All = new\[\]\s*\{(.*?)\};", block, re.S)
    assert listed, "Reasons.All not found"
    return consts, re.findall(r"\w+", listed.group(1))


def test_python_codes_match_the_csharp_vocabulary():
    consts, listed = csharp_reasons()
    assert list(tr.REASONS) == [consts[name] for name in listed]


def test_every_csharp_constant_is_listed_in_all():
    consts, listed = csharp_reasons()
    assert sorted(listed) == sorted(consts)


def test_issue_codes_are_present():
    for code in ("ok", "partial", "low_energy", "no_path", "not_here", "no_item", "inventory_full", "menu_open",
                 "too_late", "shop_closed", "wrong_season", "needs_upgrade", "interrupted", "timeout"):
        assert code in tr.REASONS


# ---------------------------------------------------------------------- tags


def test_parse_and_strip_tag():
    assert tr.parse_tag(f"{PARTIAL} Stopped: energy too low") == ("partial", "low_energy")
    assert tr.parse_tag("[no_path] No walkable path") == ("no_path",)
    assert tr.parse_tag("[partial] 2 of 3 tiles") == ("partial",)
    assert tr.parse_tag("Arrived at (3,4).") == ()
    assert tr.parse_tag("[08:30 · Farm (3,4) · energy 200/270 · 500g]") == ()  # the status footer is not a tag
    assert tr.strip_tag(f"{PARTIAL} Stopped: energy too low") == "Stopped: energy too low"
    assert tr.strip_tag("Arrived at (3,4).") == "Arrived at (3,4)."


@pytest.mark.parametrize("partial,reason,expected", [
    (False, None, ""),
    (False, "ok", ""),
    (True, "low_energy", PARTIAL),
    (True, "partial", "[partial]"),
    (True, None, "[partial]"),
    (False, "no_path", "[no_path]"),
])
def test_make_tag(partial, reason, expected):
    assert tr.make_tag(partial, reason) == expected
    assert tr.parse_tag(expected + " text") == tuple(re.findall(r"[a-z_]+", expected))


def test_tag_for_entry_reads_log_fields():
    assert tr.tag_for_entry({"kind": "result", "ok": True, "text": "x"}) == ""
    assert tr.tag_for_entry({"kind": "result", "ok": False, "reason": "no_item"}) == "[no_item]"
    assert tr.tag_for_entry({"kind": "result", "ok": True, "partial": True, "reason": "low_energy"}) == PARTIAL


# ---------------------------------------------------------------------- structured results


def test_check_structured_accepts_the_documented_shapes():
    assert tr.check_structured(reply("Farm day 3", False, {"ok": True, "reason": "ok"})) == []
    assert tr.check_structured(reply("[no_item] No tool", True, {"ok": False, "reason": "no_item"})) == []
    assert tr.check_structured(reply("something failed", True, {"ok": False, "reason": "other"})) == []
    partial = {"ok": True, "reason": "low_energy", "partial": True, "data": {"done": 3, "remaining": 37, "skipped": 0}}
    assert tr.check_structured(reply(f"{PARTIAL} Stopped", False, partial)) == []
    assert tr.check_structured(reply("[partial] 2/3", False, {"ok": True, "reason": "partial", "partial": True})) == []


def test_check_structured_ignores_older_mod_builds():
    assert tr.check_structured(reply("Farm day 3", False, None)) == []


@pytest.mark.parametrize("text,is_error,structured,fragment", [
    ("x", False, {"ok": False, "reason": "ok"}, "contradicts isError"),
    ("x", False, {"ok": "yes", "reason": "ok"}, "not a bool"),
    ("[bad_code] x", True, {"ok": False, "reason": "bad_code"}, "not a known code"),
    ("[no_item] x", True, {"ok": False, "reason": "no_item", "partial": False}, "partial must be true"),
    ("[no_item] x", True, {"ok": False, "reason": "no_item", "data": [1]}, "data is not an object"),
    ("Stopped", False, {"ok": True, "reason": "low_energy", "partial": True}, "without a [partial] tag"),
    ("[partial] Stopped", False, {"ok": True, "reason": "low_energy", "partial": True}, "not in the text tag"),
    ("[no_path] x", False, {"ok": True, "reason": "ok"}, "has a tag"),
])
def test_check_structured_reports_problems(text, is_error, structured, fragment):
    problems = tr.check_structured(reply(text, is_error, structured))
    assert any(fragment in p for p in problems), problems


# ---------------------------------------------------------------------- day summary


SUMMARY = {
    "day": 5,
    "total": {"calls": 120, "failed": 9, "partial": 4, "reasons": {"low_energy": 3, "no_path": 5, "other": 1, "menu_open": 0}},
    "tools": {
        "use_tool": {"calls": 30, "failed": 1, "partial": 3, "reasons": {"low_energy": 3}},
        "travel_to": {"calls": 12, "failed": 5, "partial": 0, "reasons": {"no_path": 5}},
        "eat": {"calls": 4, "failed": 0, "partial": 0, "reasons": {}},
    },
}


def test_format_day_summary():
    text = tr.format_day_summary(SUMMARY)
    first, second = text.split("\n")
    assert first == "day 5 results: 120 logged, 9 failed, 4 partial | reasons: no_path x5, low_energy x3, other x1"
    assert second == "tools that fail or stop early: travel_to (5), use_tool (4)"


def test_format_day_summary_without_failures():
    text = tr.format_day_summary({"day": 2, "total": {"calls": 10, "failed": 0, "partial": 0, "reasons": {}}, "tools": {}})
    assert text == "day 2 results: 10 logged, 0 failed, 0 partial"


def test_load_day_summary(tmp_path):
    assert tr.load_day_summary(tmp_path, 5) is None
    (tmp_path / "day_005_summary.json").write_text(json.dumps(SUMMARY), encoding="utf-8")
    assert tr.load_day_summary(tmp_path, 5) == SUMMARY
    (tmp_path / "day_006_summary.json").write_text("{not json", encoding="utf-8")
    assert tr.load_day_summary(tmp_path, 6) is None


def test_report_failures_prints_the_summary(tmp_path, capsys):
    run_days.report_failures(tmp_path, 5)
    assert capsys.readouterr().out == ""
    (tmp_path / "day_005_summary.json").write_text(json.dumps(SUMMARY), encoding="utf-8")
    run_days.report_failures(tmp_path, 5)
    out = capsys.readouterr().out
    assert "day 5 results: 120 logged" in out and "\n  tools that fail or stop early" in out


# ---------------------------------------------------------------------- run_days.last_actions keeps working with reasons


def test_last_actions_shows_the_tag_but_old_lines_are_unchanged(tmp_path):
    lines = [
        {"kind": "action", "tool": "use_tool", "gameTime": "09:00", "text": "clear stones"},
        {"kind": "result", "tool": "use_tool", "gameTime": "09:40", "text": "Stopped: energy too low (2).\n(3,4) cleared", "ok": True,
         "partial": True, "reason": "low_energy"},
        {"kind": "result", "tool": "walk_to", "gameTime": "09:50", "text": "No walkable path to (1,1).", "ok": False, "reason": "no_path"},
        {"kind": "result", "tool": "walk_to", "gameTime": "10:00", "text": "Arrived at (3,4).", "ok": True},
    ]
    (tmp_path / "day_007.jsonl").write_text("\n".join(json.dumps(x) for x in lines) + "\nnot json\n", encoding="utf-8")
    out = run_days.last_actions(tmp_path, 7).splitlines()
    assert out == [
        "09:00 action use_tool: clear stones",
        f"09:40 result use_tool: {PARTIAL} Stopped: energy too low (2). (3,4) cleared",
        "09:50 result walk_to: [no_path] No walkable path to (1,1).",
        "10:00 result walk_to: Arrived at (3,4).",
    ]


# ---------------------------------------------------------------------- smoke.py


class FakeGame:
    """Stands in for the mod's /mcp and /api/debug endpoints. `structured=False` behaves like a build older than #18."""

    def __init__(self, structured: bool = True, broken: bool = False):
        self.structured = structured
        self.broken = broken
        self.stamina = 270

    def request(self, path, body=None, timeout=600):
        if path.startswith("/api/debug/"):
            if path.endswith("set_stamina"):
                self.stamina = body["n"]
            return {"ok": True, "text": "done"}
        params = body["params"]
        tool, args = params["name"], params["arguments"]
        if tool == "status":
            result = reply("Farm day 3 — spring 3", False, {"ok": True, "reason": "ok"} if self.structured else None)
        elif tool == "use_tool" and args["tool"] == "Smoke Test Tool":
            result = reply("[no_item] You don't have a tool called \"Smoke Test Tool\". Check inventory.", True,
                           {"ok": False, "reason": "no_item"} if self.structured else None)
        elif tool == "use_tool" and self.stamina <= 2:
            tag = "[partial] " if self.broken else PARTIAL + " "
            result = reply(tag + "Stopped: energy too low (2).", False,
                           {"ok": True, "reason": "low_energy", "partial": True, "data": {"done": 0, "remaining": 3, "skipped": 0}}
                           if self.structured else None)
        else:
            result = reply("ok", False, None)
        return {"jsonrpc": "2.0", "id": body["id"], "result": result}


@pytest.fixture
def game(monkeypatch):
    def install(**kw):
        fake = FakeGame(**kw)
        monkeypatch.setattr(smoke, "_request", fake.request)
        monkeypatch.setattr(smoke, "reset", lambda location="Farm": None)
        return fake
    return install


def test_call_seat_still_returns_ok_and_text(game):
    game()
    assert smoke.call_seat("p1", "status", {}) == (True, "Farm day 3 — spring 3")
    ok, text = smoke.call_seat("p1", "use_tool", {"tool": "Smoke Test Tool", "tiles": [[1, 1]]})
    assert ok is False and text.startswith("[no_item] ")


def test_call_raw_rejects_a_contradicting_structured_result(monkeypatch):
    bad = {"jsonrpc": "2.0", "id": 1, "result": reply("[no_item] x", True, {"ok": True, "reason": "no_item"})}
    monkeypatch.setattr(smoke, "_request", lambda *a, **k: bad)
    with pytest.raises(smoke.Smoke, match="contradicts isError"):
        smoke.call_raw("p1", "use_tool", {})


def test_tool_reasons_case_passes_on_a_new_build(game):
    game()
    smoke.tool_reasons()


def test_tool_reasons_case_skips_on_an_old_build(game):
    game(structured=False)
    with pytest.raises(smoke.SkipCase):
        smoke.tool_reasons()


def test_tool_reasons_case_fails_when_the_tag_is_wrong(game):
    game(broken=True)
    with pytest.raises(smoke.Smoke):
        smoke.tool_reasons()


def test_tool_reasons_case_is_registered_once():
    assert [c.name for c in smoke.CASES].count("tool_reasons") == 1
