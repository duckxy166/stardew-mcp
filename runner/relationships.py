"""relationships.md: what each farmer's friendships look like, written from game data (issue #9).

The mod serves GET /api/relationships (every AI farmer's hearts, talked/gift counters, birthdays, gift tastes found
out so far and the last gift reaction per villager). This module turns that into a short markdown file per seat and
returns it for the {RELATIONSHIPS} placeholder of runner/system_prompt.md.

The file is generated, never written by the AI: lessons.md / plan.md are the AI's own notes (journal tool, #21),
and the AI's opinions of villagers belong there. Nothing here talks to the game except through the `fetch` callable
the caller passes in, so the tests need no game, network or claude CLI.
"""

from __future__ import annotations

import re
from pathlib import Path
from typing import Callable

SEASONS = ["spring", "summer", "fall", "winter"]
DAYS_PER_SEASON = 28
YEAR_DAYS = len(SEASONS) * DAYS_PER_SEASON
POINTS_PER_HEART = 250
UPCOMING_DAYS = 7      # birthdays this close are listed first
MAX_FRIENDS = 40       # lines in the friendships list (the game has about 34 villagers)

EMPTY = "(No relationship data yet. Call `villagers` to see everyone, where they are and how well you know them.)"


def file_name(seat: str = "p1") -> str:
    """p1 keeps the plain name (single-player runs); the other seats get their id."""
    seat = re.sub(r"[^A-Za-z0-9_-]", "", seat or "") or "p1"
    return "relationships.md" if seat == "p1" else f"relationships_{seat}.md"


def _season_index(season: str) -> int:
    s = (season or "").strip().lower()
    if s == "autumn":
        s = "fall"
    return SEASONS.index(s) if s in SEASONS else -1


def days_until(season: str, day: int, today_season: str, today_day: int) -> int | None:
    """Days from today until the next (season, day); 0 = today. A year is 4 x 28 days. None if either date is invalid."""
    s, t = _season_index(season), _season_index(today_season)
    if s < 0 or t < 0 or not 1 <= day <= DAYS_PER_SEASON or not 1 <= today_day <= DAYS_PER_SEASON:
        return None
    return ((s * DAYS_PER_SEASON + day) - (t * DAYS_PER_SEASON + today_day)) % YEAR_DAYS


def _plural(n: int, word: str) -> str:
    return f"{n} {word}" if n == 1 else f"{n} {word}s"


def _birthday(v: dict, today: tuple[str, int]) -> tuple[str, int | None]:
    b = v.get("birthday") or {}
    season, day = b.get("season") or "", int(b.get("day") or 0)
    if not season or day < 1:
        return "", None
    return f"{season} {day}", days_until(season, day, today[0], today[1])


def _taste_lines(v: dict) -> list[str]:
    tastes = v.get("tastes") or {}
    out = []
    for word, label in (("loved", "Loves"), ("liked", "Likes"), ("disliked", "Dislikes"), ("hated", "Hates")):
        items = tastes.get(word) or []
        if items:
            out.append(f"{label}: {', '.join(items)}")
    return out


def _friend_line(v: dict) -> str:
    name = v.get("displayName") or v.get("name") or "?"
    points = int(v.get("points") or 0)
    hearts = int(v.get("hearts") if v.get("hearts") is not None else points // POINTS_PER_HEART)
    parts = [f"**{name}**: {_plural(hearts, 'heart')} ({points} pts)"]
    status = v.get("status") or ""
    if status and status != "friendly":
        parts.append(status)
    last = v.get("lastGift")
    if last:
        pts = int(last.get("points") or 0)
        sign = f"+{pts}" if pts >= 0 else str(pts)
        parts.append(f"last gift {last.get('item')} on day {last.get('day')}: {last.get('reaction')} ({sign})")
    parts.extend(_taste_lines(v))
    return "- " + "; ".join(parts)


def render(player: dict) -> str:
    """The markdown for one seat's entry of the /api/relationships report."""
    villagers = player.get("villagers") or []
    if not villagers:
        return EMPTY + "\n"
    today = (player.get("season") or "", int(player.get("dayOfMonth") or 0))
    lines = [f"# Relationships (farm day {player.get('day')}, {today[0]} {today[1]})",
             "Generated from game data after each day. 250 points = 1 heart. Only tastes you found out by gifting are listed.",
             f"You know {int(player.get('knownTastes') or 0)} gift taste(s) so far."]

    upcoming = []
    for v in villagers:
        text, left = _birthday(v, today)
        if left is not None and left <= UPCOMING_DAYS:
            upcoming.append((left, v.get("displayName") or v.get("name"), text, v.get("met")))
    if upcoming:
        lines += ["", f"## Birthdays in the next {UPCOMING_DAYS} days (a gift counts 8x)"]
        for left, name, text, met in sorted(upcoming, key=lambda u: (u[0], str(u[1]))):
            when = "TODAY" if left == 0 else f"in {_plural(left, 'day')}"
            lines.append(f"- {name}: {text} ({when}){'' if met else ' - not met yet'}")

    met = sorted((v for v in villagers if v.get("met")), key=lambda v: (-int(v.get("points") or 0), str(v.get("displayName"))))
    if met:
        lines += ["", "## Friendships (most points first)"]
        lines += [_friend_line(v) for v in met[:MAX_FRIENDS]]
        if len(met) > MAX_FRIENDS:
            lines.append(f"- ... and {len(met) - MAX_FRIENDS} more")

    unmet = sorted(str(v.get("displayName") or v.get("name")) for v in villagers if not v.get("met"))
    if unmet:
        lines += ["", "## Not met yet", ", ".join(unmet)]
    return "\n".join(lines) + "\n"


def render_all(report: dict) -> dict[str, str]:
    """File name -> markdown, one per seat in the report. Empty when the game was not ready."""
    if not report or not report.get("ready"):
        return {}
    out = {}
    for player in report.get("players") or []:
        out[file_name(player.get("player") or "p1")] = render(player)
    return out


def write_files(log_dir: Path, report: dict) -> list[Path]:
    """Write one relationships file per seat into log_dir; returns the paths written."""
    written = []
    for name, text in render_all(report).items():
        path = Path(log_dir) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        written.append(path)
    return written


def update(log_dir: Path, fetch: Callable[[], dict]) -> list[Path]:
    """Fetch the report and write the files. A game that does not answer must never stop the run: returns []."""
    try:
        return write_files(log_dir, fetch())
    except Exception as ex:  # noqa: BLE001 - the runner prints it and plays on with the last file
        print(f"  (relationships.md not updated: {ex})")
        return []


def for_prompt(log_dir: Path, seat: str = "p1") -> str:
    """Text for the {RELATIONSHIPS} placeholder: the seat's file, or a hint when there is none yet."""
    path = Path(log_dir) / file_name(seat)
    if not path.exists():
        return EMPTY
    text = path.read_text(encoding="utf-8").strip()
    return text or EMPTY
