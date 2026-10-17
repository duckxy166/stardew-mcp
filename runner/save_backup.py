"""Back up the save the game actually has loaded, and keep only the last N backups.

run_days.py used to back up "the save folder with the newest mtime", which picks the wrong one as soon
as a second save exists (for example the smoke_* copies that runner/smoke.py makes). The folder now
comes from /api/state `saveFolder` (the mod's Constants.SaveFolderName), and smoke_* slots are refused.

Backups live in <out>/saves/day_<NNN>_<save folder>. day_007_X is the save as it was when day 7 ended
(the morning of day 8); runner/restore_day.py --day 7 puts it back.

Known gap: the copy is not synchronised with the game's own save. If a backup is taken while the game
is still writing the night's save (a crash or a pass-out during the night), the copy can be incomplete.
run_days.py only backs up after go_to_bed has returned, i.e. after the save finished. Standard library only.
"""

from __future__ import annotations

import os
import re
import shutil
from pathlib import Path
from typing import Callable

SMOKE_PREFIX = "smoke_"
DEFAULT_KEEP = 10
BACKUP_RE = re.compile(r"^day_(\d+)_(.+)$")


class BackupError(Exception):
    """The save could not be chosen or copied; the message says why (it is shown to the user)."""


def default_saves_dir() -> Path:
    """STARDEW_SAVES_DIR, else the game's Saves folder for this OS (same rule as runner/smoke.py)."""
    env = os.environ.get("STARDEW_SAVES_DIR")
    if env:
        return Path(env)
    if os.name == "nt":
        appdata = os.environ.get("APPDATA")
        if not appdata:
            raise BackupError("APPDATA is not set; set STARDEW_SAVES_DIR to the game's Saves folder")
        return Path(appdata) / "StardewValley" / "Saves"
    return Path.home() / ".config" / "StardewValley" / "Saves"


def is_smoke(name: str) -> bool:
    return name.lower().startswith(SMOKE_PREFIX)


def pick_save_folder(state: dict, saves_dir: Path) -> Path:
    """The folder of the save the game has loaded, from /api/state. Raises BackupError instead of guessing."""
    name = state.get("saveFolder") if isinstance(state, dict) else None
    if not isinstance(name, str) or not name.strip():
        raise BackupError("/api/state has no saveFolder (no save loaded, or the game runs an older build of the mod)")
    name = name.strip()
    if is_smoke(name):
        raise BackupError(f"the loaded save is {name}, a smoke test copy; load the real save before backing up")
    if name in (".", "..") or "/" in name or "\\" in name:
        raise BackupError(f"saveFolder {name!r} is not a plain folder name")
    folder = saves_dir / name
    if not folder.is_dir():
        raise BackupError(f"save folder {folder} does not exist (set STARDEW_SAVES_DIR if your Saves folder is elsewhere)")
    return folder


def backup_name(day: int, save_name: str) -> str:
    return f"day_{day:03}_{save_name}"


def backup_folder(src: Path, out_dir: Path, day: int, keep: int = DEFAULT_KEEP,
                  say: Callable[[str], None] = print) -> Path:
    """Copy `src` to <out>/saves/day_<NNN>_<name> and drop older backups beyond `keep` (0 = keep all).

    The copy goes to a temporary folder first, so an interrupted copy never replaces a good backup.
    """
    if is_smoke(src.name):
        raise BackupError(f"refusing to back up {src.name}: smoke test copies are never backed up")
    root = out_dir / "saves"
    root.mkdir(parents=True, exist_ok=True)
    dst = root / backup_name(day, src.name)
    tmp = root / f".tmp_{dst.name}"
    if tmp.exists():
        shutil.rmtree(tmp)
    try:
        shutil.copytree(src, tmp)
    except OSError as e:
        shutil.rmtree(tmp, ignore_errors=True)
        raise BackupError(f"copying {src} failed: {e}") from e
    if dst.exists():
        shutil.rmtree(dst)
    tmp.rename(dst)
    for old in prune_backups(root, src.name, keep, protect=dst):
        say(f"  old backup removed: {old.name}")
    return dst


def list_backups(root: Path, save_name: str | None = None) -> list[tuple[int, Path]]:
    """(day, folder) of every backup in `root`, oldest day first; only `save_name` when given."""
    found = []
    if root.is_dir():
        for p in root.iterdir():
            m = BACKUP_RE.match(p.name)
            if p.is_dir() and m and (save_name is None or m.group(2) == save_name):
                found.append((int(m.group(1)), p))
    return sorted(found, key=lambda t: (t[0], t[1].name))


def prune_backups(root: Path, save_name: str, keep: int, protect: Path | None = None) -> list[Path]:
    """Delete all but the newest `keep` backups of one save (by day number). keep <= 0 keeps everything.

    `protect` (the backup just made) is never deleted, even if older days are numbered higher.
    """
    if keep <= 0:
        return []
    old = [p for _, p in list_backups(root, save_name)][:-keep]
    old = [p for p in old if p != protect]
    for p in old:
        shutil.rmtree(p)
    return old
