"""Reads the MCP tool definitions out of the mod's C# source, without the game or a compiler.

The real tool list (`AgentTools.All()`) needs the Stardew assemblies to build, so CI cannot run it. The
definitions are written in one regular shape, so this reads them as text:

    new() { Name = "walk_to", Description = ..., Schema = Obj(new { x = Int(..), reason = Str(..) }, "x", "reason"),
            Instant = true, Run = ... }

It understands a tool when that `new()` / `new ToolDef { ... }` initializer (an element of the tool list, not
one assigned to a variable) has a literal `Name`, a `Schema` written with `Obj(new { ... }, "required", ...)`
(also `AgentTools.Obj`), and `Instant = true` or nothing.
Anything else comes back in `Tool.problem` so the test fails loudly instead of skipping the tool silently.
This is a lint, not a C# parser: the xUnit tests (mod/StardewMCP.Tests) check the same rules on real
schemas, and the mod logs the same findings at startup.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

NUL = "\0"


@dataclass
class Tool:
    name: str
    file: str
    line: int
    instant: bool = False
    instant_for: bool = False
    properties: dict[str, str] = field(default_factory=dict)  # property name -> JSON type ("" when not obvious)
    required: list[str] = field(default_factory=list)
    problem: str = ""  # why the definition could not be read; empty when it was


# ---------------------------------------------------------------------------------------------- lexing

def _string_end(src: str, i: int) -> int:
    """Index of the closing quote of the string literal that starts at src[i] ($, @ or " prefix)."""
    j = i
    while src[j] in "$@":
        j += 1
    prefix = src[i:j]
    verbatim, interpolated = "@" in prefix, "$" in prefix
    j += 1  # opening quote
    depth = 0
    n = len(src)
    while j < n:
        c = src[j]
        if depth == 0:
            if c == '"':
                if verbatim and src[j + 1:j + 2] == '"':
                    j += 2
                    continue
                return j
            if c == "\\" and not verbatim:
                j += 2
                continue
            if interpolated and c == "{":
                if src[j + 1:j + 2] == "{":
                    j += 2
                    continue
                depth = 1
            elif interpolated and c == "}" and src[j + 1:j + 2] == "}":
                j += 2
                continue
            j += 1
        else:  # inside an interpolation hole: code, with its own strings and braces
            if c == '"' or (c in "$@" and re.match(r'[$@]{1,2}"', src[j:j + 3])):
                j = _string_end(src, j) + 1
            elif c == "'":
                j = _char_end(src, j) + 1
            else:
                depth += (c == "{") - (c == "}")
                j += 1
    return n - 1


def _char_end(src: str, i: int) -> int:
    j = i + 1
    while j < len(src) and src[j] != "'":
        j += 2 if src[j] == "\\" else 1
    return j


def mask(src: str) -> str:
    """Same length as `src`: comments become spaces and string/char literal interiors become NULs, so the
    braces, parentheses and commas inside them cannot confuse the structure scan. Offsets stay valid for `src`."""
    out = list(src)

    def blank(a: int, b: int, ch: str) -> None:
        for k in range(a, b):
            if out[k] != "\n":
                out[k] = ch

    i, n = 0, len(src)
    while i < n:
        two = src[i:i + 2]
        c = src[i]
        if two == "//":
            j = src.find("\n", i)
            j = n if j < 0 else j
            blank(i, j, " ")
            i = j
        elif two == "/*":
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j, " ")
            i = j
        elif c == "'":
            j = _char_end(src, i)
            blank(i + 1, j, NUL)
            i = j + 1
        elif c == '"' or (c in "$@" and re.match(r'[$@]{1,2}"', src[i:i + 3])):
            q = i
            while src[q] in "$@":
                q += 1
            j = _string_end(src, i)
            blank(q + 1, j, NUL)
            i = j + 1
        else:
            i += 1
    return "".join(out)


# ---------------------------------------------------------------------------------------------- structure

def _matching(code: str, open_at: int) -> int:
    """Index of the bracket closing code[open_at]; -1 when unbalanced."""
    pairs = {"(": ")", "[": "]", "{": "}"}
    stack = []
    for i in range(open_at, len(code)):
        c = code[i]
        if c in pairs:
            stack.append(pairs[c])
        elif c in ")]}":
            if not stack or stack.pop() != c:
                return -1
            if not stack:
                return i
    return -1


def _split_top(code: str, start: int, end: int) -> list[tuple[int, int]]:
    """(start, end) of each top-level comma separated part of code[start:end], empty parts dropped."""
    parts, depth, last = [], 0, start
    for i in range(start, end):
        c = code[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == "," and depth == 0:
            parts.append((last, i))
            last = i + 1
    parts.append((last, end))
    return [(a, b) for a, b in parts if code[a:b].strip()]


def _literal(src: str, code: str, a: int, b: int) -> str | None:
    """The text of a plain "..." literal that fills code[a:b], else None."""
    part = code[a:b].strip()
    if len(part) >= 2 and part[0] == '"' and part[-1] == '"' and set(part[1:-1]) <= {NUL}:
        lo = a + code[a:b].index('"')
        return src[lo + 1:lo + len(part) - 1]
    return None


_TOOL_OPEN = re.compile(r"\bnew\s*(?:ToolDef\s*)?(?:\(\s*\)\s*)?$")
_MEMBER = re.compile(r"\s*(@?\w+)\s*=(?!=)\s*")
_OBJ = re.compile(r"(?:\bAgentTools\s*\.\s*)?\bObj\s*\(")


def _kind(src: str, code: str, a: int, b: int) -> str:
    expr = code[a:b].strip()
    for prefix, kind in (("Str(", "string"), ("AgentTools.Str(", "string"), ("Int(", "integer"),
                         ("AgentTools.Int(", "integer"), ("Tiles(", "array"), ("AgentTools.Tiles(", "array")):
        if expr.startswith(prefix):
            return kind
    if expr.startswith("new"):
        m = re.search(r'\btype\s*=\s*"', code[a:b])
        if m:
            lo = a + m.end()
            hi = code.index('"', lo)
            return src[lo:hi]
    return ""


def _read_schema(tool: Tool, src: str, code: str, a: int, b: int) -> None:
    m = _OBJ.search(code, a, b)
    if not m or code[a:m.start()].strip():
        tool.problem = "Schema is not written as Obj(new { ... }, \"required\", ...)"
        return
    close = _matching(code, m.end() - 1)
    if close < 0 or close > b:
        tool.problem = "unbalanced Obj( ... )"
        return
    args = _split_top(code, m.end(), close)
    if not args:
        tool.problem = "Obj() without a properties object"
        return
    first = code[args[0][0]:args[0][1]].strip()
    if not re.match(r"new\s*(?:\[\s*\]\s*)?\{", first):
        tool.problem = "Obj's first argument is not a `new { ... }` properties object"
        return
    lo = code.index("{", args[0][0])
    hi = _matching(code, lo)
    for pa, pb in _split_top(code, lo + 1, hi):
        member = _MEMBER.match(code, pa, pb)
        if member:
            tool.properties[member.group(1).lstrip("@")] = _kind(src, code, member.end(), pb)
        elif re.fullmatch(r"\s*@?\w+\s*", code[pa:pb]):
            tool.properties[code[pa:pb].strip().lstrip("@")] = ""
        else:
            tool.problem = f"cannot read property `{code[pa:pb].strip()[:40]}`"
            return
    for ra, rb in args[1:]:
        name = _literal(src, code, ra, rb)
        if name is None:
            tool.problem = "a required name is not a string literal"
            return
        tool.required.append(name)


def read_tools(path: Path) -> list[Tool]:
    """Every ToolDef initializer in one C# file."""
    src = path.read_text(encoding="utf-8")
    code = mask(src)
    tools: list[Tool] = []
    for brace in (m.start() for m in re.finditer(r"\{", code)):
        window = max(0, brace - 40)
        opener = _TOOL_OPEN.search(code[window:brace])
        if not opener:
            continue
        before = code[:window + opener.start()].rstrip()
        if before.endswith("=") and not before.endswith("=="):
            continue  # `ToolDef x = new() { ... }` is a helper kept in a variable, not an entry of the tool list
        end = _matching(code, brace)
        if end < 0:
            continue
        members: dict[str, tuple[int, int]] = {}
        for a, b in _split_top(code, brace + 1, end):
            m = _MEMBER.match(code, a, b)
            if m:
                members[m.group(1)] = (m.end(), b)
        if not {"Name", "Schema", "Run"} <= members.keys():
            continue  # some other object initializer
        line = src.count("\n", 0, brace) + 1
        name = _literal(src, code, *members["Name"])
        tool = Tool(name=name or "", file=path.name, line=line)
        if name is None:
            tool.name = code[slice(*members["Name"])].strip()
            tool.problem = "Name is not a plain string literal"
        else:
            if "Instant" in members:
                tool.instant = code[slice(*members["Instant"])].strip() == "true"
            tool.instant_for = "InstantFor" in members
            _read_schema(tool, src, code, *members["Schema"])
        tools.append(tool)
    return tools


def read_all(agent_dir: Path) -> list[Tool]:
    """Tools in every Agent/*.cs file except DebugApi.cs (its ops are test hooks, never MCP tools)."""
    tools: list[Tool] = []
    for path in sorted(agent_dir.glob("*.cs")):
        if path.name != "DebugApi.cs":
            tools.extend(read_tools(path))
    return tools
