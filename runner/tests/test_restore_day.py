"""runner/restore_day.py on temp folders (no game)."""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import restore_day  # noqa: E402

SAVE = "Duck_123"


def write(path: Path, text: str = "x") -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return path


@pytest.fixture
def farm(tmp_path):
    """Day 5 is being played: backups after days 1..4, logs for days 1..5, the live save is 'day5'."""
    saves = tmp_path / "Saves"
    out = tmp_path / "runs"
    logs = out / "logs"
    write(saves / SAVE / SAVE, "day5")
    write(saves / SAVE / "SaveGameInfo", "day5")
    for d in range(1, 5):
        write(out / "saves" / f"day_{d:03}_{SAVE}" / SAVE, f"end of day {d}")
    for d in range(1, 6):
        write(logs / f"day_{d:03}.jsonl")
        write(logs / f"diary_{d:03}.md")
        write(logs / f"diary_{d:03}_p2.md")
        write(logs / f"claude_day_{d:03}_part1.jsonl")
        write(out / f"system_day_{d:03}.md")
    write(logs / "claude_day_005_attempt1_part1.jsonl")
    write(logs / "diary_005_attempt1.md")
    write(logs / "journal_004.md")
    write(logs / "smoke" / "day_009.jsonl")
    write(logs / "notes.txt")
    return saves, out, logs


def names(folder: Path) -> set:
    return {p.name for p in folder.iterdir()}


def test_restore_puts_the_backup_back_and_sets_later_files_aside(farm):
    saves, out, logs = farm
    abandoned = restore_day.restore(out, logs, saves, 3, say=lambda *_: None, stamp="T")
    assert (saves / SAVE / SAVE).read_text(encoding="utf-8") == "end of day 3"
    assert not (saves / SAVE / "SaveGameInfo").exists()  # exactly the backup, no leftovers of day 5

    # what the run wrote for days 4 and 5 is gone from the live folders ...
    left = names(logs)
    assert {"day_003.jsonl", "diary_003.md", "diary_003_p2.md", "claude_day_003_part1.jsonl", "diary_001.md"} <= left
    assert not [n for n in left if restore_day.day_of_file(n) and restore_day.day_of_file(n) > 3]
    assert "notes.txt" in left and "smoke" in left
    assert (out / "system_day_003.md").exists() and not (out / "system_day_004.md").exists()
    assert names(out / "saves") == {f"day_{d:03}_{SAVE}" for d in (1, 2, 3)}

    # ... and nothing was deleted
    assert abandoned == out / "abandoned" / "restore_day003_T"
    assert (abandoned / "current_save" / SAVE / SAVE).read_text(encoding="utf-8") == "day5"
    assert names(abandoned / "logs") >= {"day_004.jsonl", "day_005.jsonl", "diary_005_attempt1.md", "journal_004.md",
                                          "claude_day_005_attempt1_part1.jsonl", "diary_004_p2.md"}
    assert names(abandoned / "run") == {"system_day_004.md", "system_day_005.md"}
    assert names(abandoned / "backups") == {f"day_004_{SAVE}"}


def test_restoring_the_last_day_keeps_the_diaries_the_ai_already_read(farm):
    saves, out, logs = farm
    restore_day.restore(out, logs, saves, 4, say=lambda *_: None, stamp="T")
    assert (logs / "diary_004.md").exists() and (logs / "journal_004.md").exists()
    assert not (logs / "diary_005.md").exists()


def test_dry_run_changes_nothing(farm):
    saves, out, logs = farm
    before = sorted(str(p) for p in out.rglob("*")) + sorted(str(p) for p in saves.rglob("*"))
    said = []
    assert restore_day.restore(out, logs, saves, 2, dry_run=True, say=said.append) is None
    after = sorted(str(p) for p in out.rglob("*")) + sorted(str(p) for p in saves.rglob("*"))
    assert before == after
    assert any("would move" in s for s in said)


def test_missing_backup_lists_the_days_that_exist(farm):
    saves, out, logs = farm
    with pytest.raises(restore_day.RestoreError, match=r"no backup for day 9.*\[1, 2, 3, 4\]"):
        restore_day.restore(out, logs, saves, 9, say=lambda *_: None)


def test_day_must_be_positive(farm):
    saves, out, logs = farm
    with pytest.raises(restore_day.RestoreError):
        restore_day.restore(out, logs, saves, 0, say=lambda *_: None)


def test_two_saves_with_a_backup_for_the_day_need_a_choice(farm):
    saves, out, logs = farm
    write(out / "saves" / "day_003_Pam_7" / "Pam_7", "pam")
    with pytest.raises(restore_day.RestoreError, match="--save"):
        restore_day.restore(out, logs, saves, 3, say=lambda *_: None)
    restore_day.restore(out, logs, saves, 3, save=SAVE, say=lambda *_: None, stamp="T")
    assert (saves / SAVE / SAVE).read_text(encoding="utf-8") == "end of day 3"


def test_smoke_backups_are_never_restored(tmp_path):
    out = tmp_path / "runs"
    write(out / "saves" / "day_002_smoke_Duck_1" / "x")
    with pytest.raises(restore_day.RestoreError, match="no backup"):
        restore_day.find_backup(out, 2)


def test_failed_copy_puts_the_current_save_back(farm, monkeypatch):
    saves, out, logs = farm

    def boom(*a, **k):
        raise OSError("disk full")

    monkeypatch.setattr(restore_day.shutil, "copytree", boom)
    with pytest.raises(restore_day.RestoreError, match="left as it was"):
        restore_day.restore(out, logs, saves, 3, say=lambda *_: None, stamp="T")
    assert (saves / SAVE / SAVE).read_text(encoding="utf-8") == "day5"
    assert (logs / "day_005.jsonl").exists()  # logs are only moved after the save is back


def test_restore_works_when_the_save_folder_is_gone(farm):
    saves, out, logs = farm
    import shutil
    shutil.rmtree(saves / SAVE)
    restore_day.restore(out, logs, saves, 1, say=lambda *_: None, stamp="T")
    assert (saves / SAVE / SAVE).read_text(encoding="utf-8") == "end of day 1"


def test_day_file_names():
    f = restore_day.day_of_file
    assert f("day_007.jsonl") == 7 and f("diary_012_p3.md") == 12 and f("journal_003.md") == 3
    assert f("claude_day_007_attempt2_part1.jsonl") == 7 and f("system_day_100.md") == 100
    assert f("mcp.json") is None and f("notes.txt") is None and f("day_one.md") is None


def test_main_refuses_while_the_game_is_running(farm, monkeypatch, capsys):
    saves, out, logs = farm
    monkeypatch.setattr(restore_day, "game_is_running", lambda api: True)
    rc = restore_day.main(["--day", "3", "--out", str(out), "--saves-dir", str(saves), "--yes"])
    assert rc == 1
    assert "Close Stardew Valley" in capsys.readouterr().err
    assert (saves / SAVE / SAVE).read_text(encoding="utf-8") == "day5"


def test_main_happy_path(farm, monkeypatch):
    saves, out, logs = farm
    monkeypatch.setattr(restore_day, "game_is_running", lambda api: False)
    rc = restore_day.main(["--day", "3", "--out", str(out), "--saves-dir", str(saves), "--yes"])
    assert rc == 0
    assert (saves / SAVE / SAVE).read_text(encoding="utf-8") == "end of day 3"
    assert len(list((out / "abandoned").iterdir())) == 1


def test_main_asks_before_replacing_the_save(farm, monkeypatch):
    saves, out, logs = farm
    monkeypatch.setattr(restore_day, "game_is_running", lambda api: False)
    monkeypatch.setattr("builtins.input", lambda prompt="": "no")
    rc = restore_day.main(["--day", "3", "--out", str(out), "--saves-dir", str(saves)])
    assert rc == 1
    assert (saves / SAVE / SAVE).read_text(encoding="utf-8") == "day5"


def test_game_is_running_is_false_when_nothing_listens():
    assert restore_day.game_is_running("http://127.0.0.1:9") is False
