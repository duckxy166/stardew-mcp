"""Reason codes and structured results of the Stardew MCP tools (issue #18).

Every tools/call reply from the mod carries, next to the text:

    structuredContent = {"ok": bool, "reason": str, "partial": true (only when partial), "data": {...} (optional)}

and the text starts with a tag when the tool gave a reason or did only part of its work:

    "[partial · low_energy] Stopped: energy too low (2). ..."
    "[no_item] You don't have a tool called ..."

The mod also logs `reason` / `partial` on result lines of day_XXX.jsonl and writes day_XXX_summary.json
(failure reasons per tool and per player). This module is the Python side of that contract: used by
smoke.py to check replies, and by run_days.py to print the day's failure reasons.
Standard library only.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

# Mirrors `Reasons.All` in mod/StardewMCP/Agent/ToolResult.cs (runner/tests/test_tool_results.py compares them).
REASONS = (
    "ok", "partial", "low_energy", "no_path", "not_here", "no_item", "inventory_full", "menu_open",
    "too_late", "shop_closed", "wrong_season", "needs_upgrade", "interrupted", "timeout",
    "no_money", "invalid_args", "not_found", "not_ready", "error", "other",
)

TAG_SEPARATOR = " · "  # " · "
_TAG = re.compile(r"^\[([a-z_]+(?: · [a-z_]+)*)\] ")


def parse_tag(text: str) -> tuple[str, ...]:
    """The codes in the tag at the start of a reply text: `[partial · low_energy] ...` -> ("partial", "low_energy"). () without a tag."""
    m = _TAG.match(text or "")
    return tuple(m.group(1).split(TAG_SEPARATOR)) if m else ()


def strip_tag(text: str) -> str:
    """The reply text without its tag (what the tool itself wrote)."""
    return _TAG.sub("", text or "", count=1)


def make_tag(partial: bool, reason: str | None) -> str:
    """The tag the mod puts in front of a reply: "" for a plain result, "[partial · low_energy]", "[no_path]"..."""
    parts = []
    if partial:
        parts.append("partial")
    if reason and reason not in ("ok", "partial"):
        parts.append(reason)
    return "[" + TAG_SEPARATOR.join(parts) + "]" if parts else ""


def tag_for_entry(entry: dict) -> str:
    """The tag for a `result` line of the JSONL log (its `reason` and `partial` fields; both are absent for plain results)."""
    return make_tag(bool(entry.get("partial")), entry.get("reason"))


def check_structured(result: dict) -> list[str]:
    """Problems in the structured part of a tools/call result. [] when it is fine or the mod build predates #18 (no structuredContent)."""
    sc = result.get("structuredContent")
    if sc is None:
        return []
    if not isinstance(sc, dict):
        return [f"structuredContent is not an object: {sc!r}"]
    problems = []
    is_error = result.get("isError")
    if not isinstance(sc.get("ok"), bool):
        problems.append(f"structuredContent.ok is not a bool: {sc.get('ok')!r}")
    elif isinstance(is_error, bool) and sc["ok"] == is_error:
        problems.append(f"structuredContent.ok ({sc['ok']}) contradicts isError ({is_error})")
    reason = sc.get("reason")
    if reason not in REASONS:
        problems.append(f"structuredContent.reason is not a known code: {reason!r}")
    if "partial" in sc and sc["partial"] is not True:
        problems.append(f"structuredContent.partial must be true when present: {sc['partial']!r}")
    if "data" in sc and not isinstance(sc["data"], dict):
        problems.append(f"structuredContent.data is not an object: {sc['data']!r}")
    content = result.get("content")
    text = content[0].get("text", "") if isinstance(content, list) and content and isinstance(content[0], dict) else ""
    codes = parse_tag(text)
    if sc.get("partial") is True and "partial" not in codes:
        problems.append(f"partial result without a [partial] tag: {text[:80]!r}")
    if reason in REASONS and reason not in ("ok", "partial", "other") and reason not in codes:
        problems.append(f"reason {reason!r} is not in the text tag {codes!r}")
    if reason == "ok" and codes:
        problems.append(f"plain ok result has a tag: {codes!r}")
    return problems


# ---------------------------------------------------------------------- day summary


def summary_path(log_dir: Path, day: int) -> Path:
    return log_dir / f"day_{day:03}_summary.json"


def load_day_summary(log_dir: Path, day: int) -> dict | None:
    """day_XXX_summary.json written by the mod, or None when it is missing or unreadable."""
    try:
        data = json.loads(summary_path(log_dir, day).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return data if isinstance(data, dict) else None


def _ranked(counts: dict, top: int) -> list[tuple[str, int]]:
    items = [(k, v) for k, v in counts.items() if isinstance(v, int) and v > 0]
    return sorted(items, key=lambda kv: (-kv[1], kv[0]))[:top]


def format_day_summary(summary: dict, top: int = 5) -> str:
    """One or two lines for the runner's console: totals, the most common reasons and the tools that fail most."""
    total = summary.get("total") or {}
    head = (f"day {summary.get('day', '?')} results: {total.get('calls', 0)} logged, "
            f"{total.get('failed', 0)} failed, {total.get('partial', 0)} partial")
    reasons = _ranked(total.get("reasons") or {}, top)
    if reasons:
        head += " | reasons: " + ", ".join(f"{k} x{v}" for k, v in reasons)
    bad_tools = _ranked({name: (c.get("failed", 0) + c.get("partial", 0)) for name, c in (summary.get("tools") or {}).items()
                         if isinstance(c, dict)}, top)
    if bad_tools:
        head += "\ntools that fail or stop early: " + ", ".join(f"{k} ({v})" for k, v in bad_tools)
    return head
