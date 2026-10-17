"""Roll the farm back to the end of an earlier day, after a mod bug (not after an AI mistake).

    py runner/restore_day.py --day 6            # close the game first
    py runner/restore_day.py --day 6 --dry-run  # show what would happen

--day N means "the farm as it was when day N ended", i.e. the morning of day N+1. It copies the backup
<out>/saves/day_006_<save> back into the game's Saves folder and moves everything the run wrote for
days after N into <out>/abandoned/restore_day006_<time>/ (nothing is deleted):

  * the save that was replaced (current_save/<save>)
  * later backups (backups/)
  * logs, diaries, journals and Claude transcripts of days after N (logs/, run/)

Use it to fix the mod's own bugs: a stuck cutscene, a crashed night, a tool that ate the inventory. It is
not an undo button for the AI's choices: a bad trade or a lost crop is part of the challenge, and the
diaries the AI already read (up to day N) are kept as they are.

Then start the game, load the save and run run_days.py again; it continues with day N+1.
Standard library only.
"""

from __future__ import annotations

import argparse
import re
import shutil
import sys
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import save_backup  # noqa: E402

API = "http://localhost:8770"
DEFAULT_OUT = HERE.parent / "runs"

# Files the run writes per in-game day, including the per-attempt and per-part variants:
# day_007.jsonl, diary_007.md, diary_007_p2.md, journal_007.md, claude_day_007_part1.jsonl, system_day_007.md ...
DAY_FILE_RE = re.compile(r"^(?:claude_day|system_day|day|diary|journal)_(\d{3,})(?:[_.]|$)")


class RestoreError(Exception):
    """Nothing was changed (or the change was rolled back); the message says why."""


def game_is_running(api: str = API) -> bool:
    """The mod answers on its port, so the game is open."""
    try:
        with urllib.request.urlopen(api + "/api/state", timeout=2):
            return True
    except Exception:
        return False


def day_of_file(name: str) -> int | None:
    m = DAY_FILE_RE.match(name)
    return int(m.group(1)) if m else None


def find_backup(out_dir: Path, day: int, save: str | None = None) -> Path:
    """The backup of `day`. Several saves with a backup for that day need --save to choose."""
    root = out_dir / "saves"
    found = [(d, p) for d, p in save_backup.list_backups(root, save) if d == day]
    found = [(d, p) for d, p in found if not save_backup.is_smoke(save_backup.BACKUP_RE.match(p.name).group(2))]
    if not found:
        have = sorted({d for d, _ in save_backup.list_backups(root, save)})
        raise RestoreError(f"no backup for day {day} in {root}" + (f" (days with a backup: {have})" if have else " (no backups at all)"))
    if len(found) > 1:
        names = ", ".join(save_backup.BACKUP_RE.match(p.name).group(2) for _, p in found)
        raise RestoreError(f"day {day} has backups of several saves ({names}); pick one with --save")
    return found[0][1]


def files_after(day: int, folder: Path) -> list[Path]:
    """Per-day files directly in `folder` that belong to a day after `day`."""
    if not folder.is_dir():
        return []
    return sorted(p for p in folder.iterdir() if p.is_file() and (d := day_of_file(p.name)) is not None and d > day)


def plan(out_dir: Path, log_dir: Path, saves_dir: Path, day: int, save: str | None) -> dict:
    backup = find_backup(out_dir, day, save)
    save_name = save_backup.BACKUP_RE.match(backup.name).group(2)
    return {
        "backup": backup,
        "save_name": save_name,
        "target": saves_dir / save_name,
        "later_backups": [p for d, p in save_backup.list_backups(out_dir / "saves") if d > day],
        "log_files": files_after(day, log_dir),
        "run_files": files_after(day, out_dir) if out_dir != log_dir else [],
    }


def restore(out_dir: Path, log_dir: Path, saves_dir: Path, day: int, save: str | None = None,
            dry_run: bool = False, say=print, stamp: str | None = None) -> Path | None:
    """Do the restore. Returns the abandoned/ folder (None for a dry run)."""
    if day < 1:
        raise RestoreError("--day must be 1 or more")
    p = plan(out_dir, log_dir, saves_dir, day, save)
    say(f"Backup:           {p['backup']}")
    say(f"Restores into:    {p['target']}" + ("  (replaces the current save, which is kept in abandoned/)" if p["target"].exists() else ""))
    say(f"Moved aside:      {len(p['log_files'])} log/diary file(s) and {len(p['run_files'])} run file(s) after day {day}, "
        f"{len(p['later_backups'])} later backup(s)")
    if dry_run:
        for f in p["log_files"] + p["run_files"] + p["later_backups"]:
            say(f"  would move {f.name}")
        return None

    abandoned = out_dir / "abandoned" / f"restore_day{day:03}_{stamp or time.strftime('%Y%m%d-%H%M%S')}"
    abandoned.mkdir(parents=True, exist_ok=False)

    target: Path = p["target"]
    moved_save = None
    if target.exists():
        moved_save = abandoned / "current_save" / target.name
        moved_save.parent.mkdir(parents=True)
        shutil.move(str(target), str(moved_save))
    try:
        shutil.copytree(p["backup"], target)
    except Exception as e:  # put the original back so a failed restore changes nothing
        shutil.rmtree(target, ignore_errors=True)
        if moved_save is not None:
            shutil.move(str(moved_save), str(target))
        raise RestoreError(f"copying the backup failed ({e}); the save was left as it was") from e

    for sub, files in (("logs", p["log_files"]), ("run", p["run_files"]), ("backups", p["later_backups"])):
        for f in files:
            dest = abandoned / sub
            dest.mkdir(exist_ok=True)
            shutil.move(str(f), str(dest / f.name))
    say(f"Restored {p['save_name']} to the end of day {day}. Replaced files are in {abandoned}")
    say(f"Next: start the game, load {p['save_name']}, and run run_days.py; it continues with day {day + 1}.")
    return abandoned


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--day", type=int, required=True, help="Restore the farm as it was when this day ended (morning of day+1)")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="run_days.py output folder (default: runs/)")
    ap.add_argument("--log-dir", default="", help="Folder with day_*.jsonl and diary_*.md (default: <out>/logs)")
    ap.add_argument("--saves-dir", default="", help="The game's Saves folder (default: STARDEW_SAVES_DIR or the OS default)")
    ap.add_argument("--save", default="", help="Save folder name, when backups of several saves exist for that day")
    ap.add_argument("--dry-run", action="store_true", help="Only show what would be copied and moved")
    ap.add_argument("--yes", action="store_true", help="Do not ask for confirmation")
    ap.add_argument("--api", default=API, help="Mod URL, used to check that the game is closed (default %(default)s)")
    args = ap.parse_args(argv)

    out_dir = Path(args.out)
    log_dir = Path(args.log_dir) if args.log_dir else out_dir / "logs"
    try:
        saves_dir = Path(args.saves_dir) if args.saves_dir else save_backup.default_saves_dir()
        if not args.dry_run and game_is_running(args.api.rstrip("/")):
            raise RestoreError("the game is running (the mod answers on " + args.api + "). Close Stardew Valley first.")
        if not args.dry_run and not args.yes:
            print(f"This replaces the save in {saves_dir} with the backup of day {args.day} and sets later logs aside.")
            if input("Type yes to continue: ").strip().lower() != "yes":
                print("Cancelled.")
                return 1
        restore(out_dir, log_dir, saves_dir, args.day, args.save or None, dry_run=args.dry_run)
    except (RestoreError, save_backup.BackupError) as e:
        print(f"restore_day: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
