"""Planned session breaks for run_days.py (#30): helpers that need no game and no `claude` CLI.

A day played in one `claude -p` session drags every old map and tool result along, and each turn re-sends
all of it. The mod can end a session on purpose: at fixed game times, or after too many calls or too much
result text (mod/StardewMCP/Agent/SessionBreaks.cs). It adds a line starting with "Session break:" to a tool
result, the AI writes its plan with `think` and ends its turn, and this module helps the runner start the
next part:

  * mcp_url          the MCP URL that opts in to breaks (`/mcp?breaks=1`);
  * saw_break        spots the notice in a stream-json event of the transcript;
  * PartCounter      counts the parts of a day; planned breaks do not count toward --max-parts;
  * continuation_prompt / handoff_text   the first message of the next part: the day's plan, the latest
                     `think` and the last few results (read from the mod's day log, `think` entries);
  * add_context_provider   a hook for more handoff text, for example the journal and plan of #21:
                     `sessions.add_context_provider(memory.continuation_context)` once that is merged.

Standard library only.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

# The mod starts its notice and its refusals with this text (SessionBreaks.Marker).
BREAK_MARKER = "Session break:"
_BREAK_LINE = re.compile(r"^" + re.escape(BREAK_MARKER), re.MULTILINE)

DEFAULT_MAX_BREAKS = 8


def mcp_url(api: str, breaks: bool = True) -> str:
    """The seat's MCP URL. `?breaks=1` asks the mod for planned session breaks on this connection."""
    return f"{api}/mcp?breaks=1" if breaks else f"{api}/mcp"


# ---------------------------------------------------------------------- reading the transcript


def tool_result_texts(ev: dict) -> list[str]:
    """Texts of the tool results in one stream-json event (`user` events carry them)."""
    if ev.get("type") != "user":
        return []
    content = (ev.get("message") or {}).get("content")
    if not isinstance(content, list):
        return []
    texts: list[str] = []
    for block in content:
        if not isinstance(block, dict) or block.get("type") != "tool_result":
            continue
        body = block.get("content")
        if isinstance(body, str):
            texts.append(body)
        elif isinstance(body, list):
            texts.extend(b.get("text", "") for b in body if isinstance(b, dict) and isinstance(b.get("text"), str))
    return texts


def saw_break(ev: dict) -> bool:
    """True when a tool result in this event carries the mod's session break notice."""
    return any(_BREAK_LINE.search(t) for t in tool_result_texts(ev))


# ---------------------------------------------------------------------- counting parts


@dataclass
class PartResult:
    """What run_session reports about one part."""
    tool_calls: int
    planned_break: bool


class PartCounter:
    """The parts of one in-game day.

    A part that ended in a planned break is free (up to `max_breaks`, a guard against a loop); a part that
    simply stopped before bedtime counts toward `max_parts`, as before.
    """

    def __init__(self, max_parts: int, max_breaks: int = DEFAULT_MAX_BREAKS):
        self.max_parts = max_parts
        self.max_breaks = max_breaks
        self.unplanned = 0
        self.breaks = 0

    def after_part(self, planned_break: bool) -> bool:
        """Record how a part ended without the day finishing. True if another part may start."""
        if planned_break and self.breaks < self.max_breaks:
            self.breaks += 1
            return True
        self.unplanned += 1
        return self.unplanned < self.max_parts


# ---------------------------------------------------------------------- the handoff to the next part

ContextProvider = Callable[[Path, int], str]
_providers: list[ContextProvider] = []


def add_context_provider(fn: ContextProvider) -> None:
    """Register `fn(log_dir, day) -> str`; its text is added to every handoff (blank text is skipped)."""
    if fn not in _providers:
        _providers.append(fn)


def clear_context_providers() -> None:
    _providers.clear()


def read_think_entries(log_dir: Path, day: int, player: str = "p1") -> list[dict]:
    """`think` entries of one player from the mod's day log (day_NNN.jsonl), oldest first."""
    f = Path(log_dir) / f"day_{day:03}.jsonl"
    if not f.exists():
        return []
    out = []
    for line in f.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(e, dict):
            continue
        if e.get("kind") == "think" and e.get("player") in (None, player) and (e.get("text") or "").strip():
            out.append(e)
    return out


def _clip(text: str, n: int) -> str:
    text = " ".join(text.split())
    return text if len(text) <= n else text[: n - 1] + "…"


def handoff_text(log_dir: Path, day: int, player: str = "p1", clip: int = 600) -> str:
    """The day's plan (first `think`), the latest `think` and whatever the context providers add."""
    thinks = read_think_entries(log_dir, day, player)
    lines: list[str] = []
    if thinks:
        first, last = thinks[0], thinks[-1]
        if first is last:
            lines.append(f"ความคิดล่าสุดของคุณ ({last.get('gameTime', '')}): {_clip(last['text'], clip)}")
        else:
            lines.append(f"แผนของวันนี้ (think แรกของวัน {first.get('gameTime', '')}): {_clip(first['text'], clip)}")
            lines.append(f"think ล่าสุด ({last.get('gameTime', '')}): {_clip(last['text'], clip)}")
    for provider in _providers:
        try:
            extra = provider(Path(log_dir), day)
        except Exception as e:  # a broken provider must not stop the day
            print(f"  (handoff provider {getattr(provider, '__name__', provider)} failed: {e})")
            continue
        if extra and extra.strip():
            lines.append(extra.strip())
    return "\n".join(lines)


def continuation_prompt(day: int, log_dir: Path, planned: bool, recent: str = "", player: str = "p1") -> str:
    """First message of part 2, 3...: why the last part ended, the handoff and the last few results."""
    if planned:
        head = (f"เล่นวันที่ {day} ต่อ (session ใหม่) session ก่อนหน้าหยุดตามแผน (Session break) เพื่อล้าง context "
                "ไม่ได้เกิดปัญหาอะไร นี่คือสิ่งที่ต้องรู้เพื่อเล่นต่อ:")
        tail = "เช็ก status แล้วเล่นต่อตามแผนจนเข้านอนด้วย go_to_bed"
    else:
        head = f"เล่นวันที่ {day} ต่อจากเดิม (session ก่อนหน้าหยุดก่อนเข้านอน) นี่คือสิ่งที่เพิ่งทำไป:"
        tail = "เช็ก status แล้วเล่นต่อจนเข้านอนด้วย go_to_bed"
    parts = [head]
    handoff = handoff_text(log_dir, day, player)
    if handoff:
        parts.append(handoff)
    if recent.strip():
        parts.append("ผลล่าสุด:\n" + recent.strip())
    parts.append(tail)
    return "\n\n".join(parts)
