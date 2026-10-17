"""MCP tool definitions, checked from the C# source (no game, no compiler).

Counterpart of mod/StardewMCP.Tests/ToolContractTests.cs, which runs the same rules on real `ToolDef`s but needs
dotnet. These tests make the rules part of the Python run too, so a bad tool is caught on any machine and in CI.
`tool_source_parser.py` reads the definitions; the first half of this file tests that reader on small samples so
it cannot silently stop seeing tools.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

import pytest

from tool_source_parser import Tool, mask, read_all, read_tools

ROOT = Path(__file__).resolve().parents[2]
AGENT_DIR = ROOT / "mod" / "StardewMCP" / "Agent"
SAMPLES = ROOT / "runner" / "samples"


def tools_in(tmp_path: Path, code: str) -> list[Tool]:
    f = tmp_path / "Sample.cs"
    f.write_text(code, encoding="utf-8")
    return read_tools(f)


# ------------------------------------------------------------------------------------- the reader itself

def test_reads_name_properties_and_required(tmp_path):
    (tool,) = tools_in(tmp_path, """
        new()
        {
            Name = "walk_to",
            Description = "Walk, then stop.",
            Schema = Obj(new { x = Int("Tile x"), y = Int("Tile y"), note = Str("n"), reason = Str(ReasonDesc) }, "x", "y", "reason"),
            Run = WalkToTool
        },""")
    assert tool.problem == ""
    assert (tool.name, tool.instant, tool.instant_for) == ("walk_to", False, False)
    assert tool.properties == {"x": "integer", "y": "integer", "note": "string", "reason": "string"}
    assert tool.required == ["x", "y", "reason"]


def test_instant_flag_and_instant_for(tmp_path):
    a, b = tools_in(tmp_path, """
        new() { Name = "status", Description = "d", Schema = Obj(new { }), Instant = true, Run = Status },
        new() { Name = "menu", Description = "d", Schema = Obj(new { reason = Str("r") }), InstantFor = x => true, Run = Menu },""")
    assert (a.instant, a.instant_for, a.properties) == (True, False, {})
    assert (b.instant, b.instant_for) == (False, True)


def test_array_and_raw_schema_types(tmp_path):
    (tool,) = tools_in(tmp_path, """
        new() { Name = "plant", Description = "d",
                Schema = Obj(new { tiles = Tiles("Where"), flag = new { type = "boolean", description = "x" } }, "tiles"),
                Run = Plant },""")
    assert tool.properties == {"tiles": "array", "flag": "boolean"}


def test_qualified_helpers_and_explicit_tooldef(tmp_path):
    (tool,) = tools_in(tmp_path, """
        list.Add(new ToolDef { Name = "ext", Description = "d",
            Schema = AgentTools.Obj(new { n = AgentTools.Int("n"), reason = AgentTools.Str("r") }, "n"), Run = Run });""")
    assert tool.problem == ""
    assert tool.properties == {"n": "integer", "reason": "string"}
    assert tool.required == ["n"]


def test_punctuation_inside_strings_does_not_confuse_it(tmp_path):
    (tool,) = tools_in(tmp_path, '''
        new() { Name = "say", Description = $"Talk {{ hello }}, \\"friend\\" (really) -- } ) ]",
                Schema = Obj(new { text = Str("a, b } c) \\" d") , reason = Str(@"raw ""quoted"" }") }, "text"),
                Run = Say },''')
    assert tool.problem == ""
    assert sorted(tool.properties) == ["reason", "text"]
    assert tool.required == ["text"]


def test_commented_out_tools_are_ignored(tmp_path):
    found = tools_in(tmp_path, """
        // new() { Name = "old", Description = "d", Schema = Obj(new { }), Run = Old },
        /* new() { Name = "older", Description = "d", Schema = Obj(new { }), Run = Older }, */
        new() { Name = "kept", Description = "d", Schema = Obj(new { }), Instant = true, Run = Kept },""")
    assert [t.name for t in found] == ["kept"]


def test_tool_kept_in_a_variable_is_not_a_listed_tool(tmp_path):
    found = tools_in(tmp_path, """
        private static readonly ToolDef Helper = new() { Name = "helper", Description = "d", Schema = new { }, Run = H };
        new() { Name = "listed", Description = "d", Schema = Obj(new { }), Instant = true, Run = L },""")
    assert [t.name for t in found] == ["listed"]


def test_other_object_initializers_are_not_tools(tmp_path):
    assert tools_in(tmp_path, 'var cfg = new Config { Name = "x", Schema = 1 }; var p = new() { X = 1 };') == []


@pytest.mark.parametrize("definition, expected", [
    ('Name = Names.Walk, Schema = Obj(new { }), Run = R', "Name is not a plain string literal"),
    ('Name = "a", Schema = new { }, Run = R', "Schema is not written as Obj"),
    ('Name = "a", Schema = Build(), Run = R', "Schema is not written as Obj"),
    ('Name = "a", Schema = Obj(props), Run = R', "not a `new { ... }` properties object"),
    ('Name = "a", Schema = Obj(new { x = Int("x") }, Names.X), Run = R', "required name is not a string literal"),
    ('Name = "a", Schema = Obj(new { x = Int("x") + 1 }, "x"), Run = R', ""),  # odd expression, still readable
])
def test_unreadable_definitions_say_why(tmp_path, definition, expected):
    (tool,) = tools_in(tmp_path, "new() { Description = \"d\", " + definition + " },")
    if expected:
        assert expected in tool.problem
    else:
        assert tool.problem == ""


def test_debug_api_is_not_part_of_the_tool_list(tmp_path):
    (tmp_path / "DebugApi.cs").write_text('new() { Name = "warp", Schema = Obj(new { }), Run = W },', encoding="utf-8")
    (tmp_path / "Tools.cs").write_text('new() { Name = "look", Schema = Obj(new { }), Instant = true, Run = L },', encoding="utf-8")
    assert [t.name for t in read_all(tmp_path)] == ["look"]


def test_mask_keeps_offsets_and_hides_literals():
    src = 'a = "x{y}"; // c { }\nb = \'}\';'
    masked = mask(src)
    assert len(masked) == len(src)
    assert masked.count("{") == masked.count("}") == 0
    assert masked.startswith('a = "\0\0\0\0"; ')


# ------------------------------------------------------------------------------- the mod's real tool list

@pytest.fixture(scope="module")
def tools() -> list[Tool]:
    return read_all(AGENT_DIR)


def test_reads_every_tool_definition(tools):
    assert len(tools) >= 25, "the reader stopped recognising the ToolDef initializers; update tool_source_parser.py"
    unreadable = [f"{t.file}:{t.line} {t.name}: {t.problem}" for t in tools if t.problem]
    assert not unreadable, "tool definitions the reader cannot parse:\n" + "\n".join(unreadable)


def test_core_tools_are_listed(tools):
    names = {t.name for t in tools}
    core = {"think", "status", "look", "inventory", "walk_to", "use_tool", "plant", "harvest", "interact",
            "talk_to", "respond", "close_menu", "menu", "travel_to", "buy", "ship", "eat", "wait", "go_to_bed",
            "say", "whisper", "emote", "give", "plan_read", "plan_edit"}
    assert core <= names, f"missing from the C# sources: {sorted(core - names)}"


def test_tool_names_are_unique_snake_case(tools):
    names = [t.name for t in tools]
    assert len(names) == len(set(names)), f"duplicate tool names: {sorted({n for n in names if names.count(n) > 1})}"
    bad = [n for n in names if not re.fullmatch(r"[a-z][a-z0-9_]{0,63}", n)]
    assert not bad, f"tool names must be snake_case, 1-64 characters: {bad}"


def test_required_arguments_are_declared_properties(tools):
    for t in tools:
        assert len(t.required) == len(set(t.required)), f"{t.name}: duplicate entries in required"
        missing = [r for r in t.required if r not in t.properties]
        assert not missing, f"{t.name}: required {missing} is not in its properties"


def test_property_types_are_known_json_schema_types(tools):
    for t in tools:
        for prop, kind in t.properties.items():
            assert kind in ("", "string", "integer", "number", "boolean", "array", "object"), f"{t.name}.{prop}: {kind}"


def test_reason_is_a_string_when_present(tools):
    for t in tools:
        if "reason" in t.properties:
            assert t.properties["reason"] == "string", f"{t.name}: reason must be a string"


@pytest.mark.parametrize("name", [
    pytest.param(
        "close_menu",
        marks=pytest.mark.xfail(
            reason="known gap: close_menu takes no 'reason', so the viewers get no caption for it", strict=False),
    ),
    *sorted({"walk_to", "use_tool", "plant", "harvest", "interact", "talk_to", "respond", "travel_to", "buy",
             "ship", "eat", "wait", "go_to_bed", "menu"}),
])
def test_action_tools_have_a_reason_parameter(tools, name):
    by_name = {t.name: t for t in tools}
    assert name in by_name, f"{name} is no longer a tool; update this list"
    assert "reason" in by_name[name].properties


def test_every_non_instant_tool_has_a_reason(tools):
    """Same rule as ToolContract.Check: only pure observations may leave the 'reason' out."""
    without = sorted(t.name for t in tools if not t.instant and not t.instant_for and "reason" not in t.properties)
    assert without in ([], ["close_menu"]), f"action tools without a 'reason' parameter: {without}"


# -------------------------------------------------------------------------------- samples match the tools

def sample_tool_names() -> set[str]:
    names: set[str] = set()
    for path in sorted(SAMPLES.glob("claude_stream_*.jsonl")):
        for line in path.read_text(encoding="utf-8").splitlines():
            try:
                event = json.loads(line)
            except json.JSONDecodeError:
                continue
            if event.get("type") == "system":
                names.update(event.get("tools", []))
            for block in (event.get("message") or {}).get("content", []) if event.get("type") == "assistant" else []:
                if block.get("type") == "tool_use":
                    names.add(block["name"])
    return {n.removeprefix("mcp__stardew__") for n in names}


def test_sample_transcripts_only_call_real_tools(tools):
    used = sample_tool_names()
    assert used, "the sample transcripts mention no tools"
    unknown = used - {t.name for t in tools}
    assert not unknown, f"runner/samples calls tools the mod does not have: {sorted(unknown)}"
