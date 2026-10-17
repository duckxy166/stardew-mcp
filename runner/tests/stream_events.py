"""Builders for Claude Code stream-json events, shaped like the real `claude -p --output-format stream-json` output.

Used by the session-break and usage-log tests; no game and no `claude` CLI needed.
"""

from __future__ import annotations

import json

BREAK_NOTICE = ("Session break: continue in a new session (it is 12:00, past the planned break at 12:00). "
                "Your context is long and holds old maps that no longer match the world. Do this now: call think ...")


def init(session_id="sess-1", model="claude-haiku-5-5") -> dict:
    return {"type": "system", "subtype": "init", "session_id": session_id, "model": model, "tools": []}


def assistant(msg_id, tool=None, tool_id=None, text=None, usage=None) -> dict:
    block = ({"type": "tool_use", "id": tool_id or f"toolu_{msg_id}", "name": f"mcp__stardew__{tool}", "input": {}}
             if tool else {"type": "text", "text": text or "ok"})
    msg = {"id": msg_id, "type": "message", "role": "assistant", "model": "claude-haiku-5-5", "content": [block]}
    if usage is not None:
        msg["usage"] = usage
    return {"type": "assistant", "message": msg, "session_id": "sess-1"}


def tool_result(tool_id, text, is_error=False, as_string=False) -> dict:
    body = text if as_string else [{"type": "text", "text": text}]
    block = {"type": "tool_result", "tool_use_id": tool_id, "content": body}
    if is_error:
        block["is_error"] = True
    return {"type": "user", "message": {"role": "user", "content": [block]}, "session_id": "sess-1"}


def usage(inp=0, out=0, cache_write=0, cache_read=0) -> dict:
    return {"input_tokens": inp, "output_tokens": out,
            "cache_creation_input_tokens": cache_write, "cache_read_input_tokens": cache_read}


def result(total_usage, turns=3, subtype="success", is_error=False, duration_ms=12000, session_id="sess-1") -> dict:
    return {"type": "result", "subtype": subtype, "is_error": is_error, "duration_ms": duration_ms, "duration_api_ms": 9000,
            "num_turns": turns, "result": "จบ", "session_id": session_id, "total_cost_usd": 0.01, "usage": total_usage}


def three_turn_part(with_break=False) -> list[dict]:
    """init, look, walk_to (failing), look with or without the break notice, final reply, result."""
    last = "Map Farm ...\n" + (BREAK_NOTICE if with_break else "[12:10 · Farm (1,2) · energy 200/270 · 100g]")
    return [
        init(),
        assistant("m1", tool="look", tool_id="t1", usage=usage(10, 20, 1000, 0)),
        tool_result("t1", "Map Farm ...\n[06:00 · Farm (1,2) · energy 270/270 · 100g]"),
        assistant("m2", tool="walk_to", tool_id="t2", usage=usage(5, 30, 200, 1000)),
        tool_result("t2", "No walkable path", is_error=True),
        assistant("m3", tool="look", tool_id="t3", usage=usage(5, 25, 100, 1200)),
        tool_result("t3", last),
        assistant("m4", text="จบวัน", usage=usage(3, 10, 0, 1300)),
        result(usage(23, 85, 1300, 3500), turns=4),
    ]


def jsonl(events) -> str:
    return "".join(json.dumps(e, ensure_ascii=False) + "\n" for e in events)
