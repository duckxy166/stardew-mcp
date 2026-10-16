"""runner/save_backup.py and the backup path of run_days.py (temp folders only)."""

import os
import sys
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import save_backup  # noqa: E402
from save_backup import BackupError  # noqa: E402


def make_save(saves: Path, name: str, text: str = "x") -> Path:
    d = saves / name
    d.mkdir(parents=True)
    (d / name).write_text(text, encoding="utf-8")
    (d / "SaveGameInfo").write_text(text, encoding="utf-8")
    return d


def set_mtime(path: Path, t: float) -> None:
    os.utime(path, (t, t))


def test_loaded_save_is_chosen_even_when_a_smoke_copy_is_newer(tmp_path):
    saves = tmp_path / "Saves"
    real = make_save(saves, "Duck_123456")
    smoke = make_save(saves, "smoke_Duck_123456")
    set_mtime(real, time.time() - 3600)
    set_mtime(smoke, time.time())  # the old rule ("newest mtime") would have picked this one
    assert save_backup.pick_save_folder({"saveFolder": "Duck_123456"}, saves) == real


def test_another_real_save_with_a_newer_mtime_does_not_win(tmp_path):
    saves = tmp_path / "Saves"
    loaded = make_save(saves, "Duck_1")
    other = make_save(saves, "Pam_2")
    set_mtime(loaded, time.time() - 3600)
    set_mtime(other, time.time())
    assert save_backup.pick_save_folder({"saveFolder": "Duck_1"}, saves) == loaded


@pytest.mark.parametrize("name", ["smoke_Duck_1", "SMOKE_Duck_1", "smoke_x_9"])
def test_smoke_slots_are_never_backed_up(tmp_path, name):
    saves = tmp_path / "Saves"
    make_save(saves, name)
    with pytest.raises(BackupError, match="smoke"):
        save_backup.pick_save_folder({"saveFolder": name}, saves)
    with pytest.raises(BackupError, match="smoke"):
        save_backup.backup_folder(saves / name, tmp_path / "runs", 1)
    assert not (tmp_path / "runs" / "saves").exists() or not list((tmp_path / "runs" / "saves").iterdir())


@pytest.mark.parametrize("state", [{}, {"ready": False}, {"saveFolder": ""}, {"saveFolder": None}, {"saveFolder": 7}, None])
def test_no_savefolder_in_state_is_an_error_not_a_guess(tmp_path, state):
    saves = tmp_path / "Saves"
    make_save(saves, "Duck_1")  # a candidate exists, but we must not guess it
    with pytest.raises(BackupError, match="saveFolder"):
        save_backup.pick_save_folder(state, saves)


@pytest.mark.parametrize("name", ["..", "../Duck_1", "a/b", "a\\b", "."])
def test_save_folder_must_be_a_plain_name(tmp_path, name):
    saves = tmp_path / "Saves"
    make_save(saves, "Duck_1")
    with pytest.raises(BackupError):
        save_backup.pick_save_folder({"saveFolder": name}, saves)


def test_missing_folder_is_reported(tmp_path):
    with pytest.raises(BackupError, match="does not exist"):
        save_backup.pick_save_folder({"saveFolder": "Duck_1"}, tmp_path / "Saves")


def test_backup_copies_the_save_under_the_day_name(tmp_path):
    saves = tmp_path / "Saves"
    src = make_save(saves, "Duck_1", "day one")
    out = tmp_path / "runs"
    dst = save_backup.backup_folder(src, out, 7, keep=10, say=lambda *_: None)
    assert dst == out / "saves" / "day_007_Duck_1"
    assert (dst / "Duck_1").read_text(encoding="utf-8") == "day one"
    assert not [p for p in (out / "saves").iterdir() if p.name.startswith(".tmp")]


def test_backing_up_the_same_day_again_replaces_it(tmp_path):
    saves = tmp_path / "Saves"
    src = make_save(saves, "Duck_1", "first")
    out = tmp_path / "runs"
    save_backup.backup_folder(src, out, 3, say=lambda *_: None)
    (src / "Duck_1").write_text("second", encoding="utf-8")
    dst = save_backup.backup_folder(src, out, 3, say=lambda *_: None)
    assert (dst / "Duck_1").read_text(encoding="utf-8") == "second"


def test_only_the_newest_n_backups_are_kept(tmp_path):
    saves = tmp_path / "Saves"
    src = make_save(saves, "Duck_1")
    out = tmp_path / "runs"
    removed = []
    for day in range(1, 7):
        save_backup.backup_folder(src, out, day, keep=3, say=removed.append)
    kept = sorted(p.name for p in (out / "saves").iterdir())
    assert kept == ["day_004_Duck_1", "day_005_Duck_1", "day_006_Duck_1"]
    assert len(removed) == 3


def test_days_sort_numerically_not_alphabetically(tmp_path):
    saves = tmp_path / "Saves"
    src = make_save(saves, "Duck_1")
    out = tmp_path / "runs"
    for day in (8, 9, 10, 11):
        save_backup.backup_folder(src, out, day, keep=2, say=lambda *_: None)
    assert sorted(p.name for p in (out / "saves").iterdir()) == ["day_010_Duck_1", "day_011_Duck_1"]


def test_keep_zero_keeps_everything(tmp_path):
    saves = tmp_path / "Saves"
    src = make_save(saves, "Duck_1")
    out = tmp_path / "runs"
    for day in range(1, 5):
        save_backup.backup_folder(src, out, day, keep=0, say=lambda *_: None)
    assert len(list((out / "saves").iterdir())) == 4


def test_pruning_leaves_other_saves_and_unrelated_folders_alone(tmp_path):
    saves = tmp_path / "Saves"
    a = make_save(saves, "Duck_1")
    b = make_save(saves, "Pam_2")
    out = tmp_path / "runs"
    (out / "saves" / "notes").mkdir(parents=True)
    for day in range(1, 4):
        save_backup.backup_folder(a, out, day, keep=1, say=lambda *_: None)
        save_backup.backup_folder(b, out, day, keep=1, say=lambda *_: None)
    assert sorted(p.name for p in (out / "saves").iterdir()) == ["day_003_Duck_1", "day_003_Pam_2", "notes"]


def test_the_fresh_backup_is_never_the_one_pruned(tmp_path):
    saves = tmp_path / "Saves"
    src = make_save(saves, "Duck_1")
    out = tmp_path / "runs"
    for day in (5, 6, 7):
        save_backup.backup_folder(src, out, day, keep=0, say=lambda *_: None)
    # a replay back at day 2 while newer backups still exist: keep=1 would drop day 2 as "oldest"
    dst = save_backup.backup_folder(src, out, 2, keep=1, say=lambda *_: None)
    assert dst.exists()


def test_failed_copy_keeps_the_previous_backup(tmp_path, monkeypatch):
    saves = tmp_path / "Saves"
    src = make_save(saves, "Duck_1", "good")
    out = tmp_path / "runs"
    dst = save_backup.backup_folder(src, out, 1, say=lambda *_: None)

    def boom(*a, **k):
        raise OSError("disk full")

    monkeypatch.setattr(save_backup.shutil, "copytree", boom)
    with pytest.raises(BackupError, match="disk full"):
        save_backup.backup_folder(src, out, 1, say=lambda *_: None)
    assert (dst / "Duck_1").read_text(encoding="utf-8") == "good"


def test_saves_dir_comes_from_the_environment(tmp_path, monkeypatch):
    monkeypatch.setenv("STARDEW_SAVES_DIR", str(tmp_path / "S"))
    assert save_backup.default_saves_dir() == tmp_path / "S"


# ---- run_days.py glue: the verification case "real save + smoke_* copy"


def test_run_days_backs_up_the_loaded_save_not_the_newest_folder(tmp_path, monkeypatch, capsys):
    import run_days

    saves = tmp_path / "Saves"
    real = make_save(saves, "Duck_9", "real")
    smoke = make_save(saves, "smoke_Duck_9", "smoke")
    set_mtime(real, time.time() - 600)
    set_mtime(smoke, time.time())
    monkeypatch.setenv("STARDEW_SAVES_DIR", str(saves))
    monkeypatch.setattr(run_days, "game_state", lambda: {"ready": True, "saveFolder": "Duck_9", "day": 5})
    assert run_days.save_folder() == real

    out = tmp_path / "runs"
    run_days.backup_save(out, 4, keep=5)
    assert [p.name for p in (out / "saves").iterdir()] == ["day_004_Duck_9"]
    assert ((out / "saves" / "day_004_Duck_9") / "Duck_9").read_text(encoding="utf-8") == "real"


def test_run_days_does_not_back_up_when_a_smoke_save_is_loaded(tmp_path, monkeypatch, capsys):
    import run_days

    saves = tmp_path / "Saves"
    make_save(saves, "Duck_9")
    make_save(saves, "smoke_Duck_9")
    monkeypatch.setenv("STARDEW_SAVES_DIR", str(saves))
    monkeypatch.setattr(run_days, "game_state", lambda: {"ready": True, "saveFolder": "smoke_Duck_9"})
    out = tmp_path / "runs"
    run_days.backup_save(out, 4)
    assert not (out / "saves").exists()
    assert "NOT backed up" in capsys.readouterr().out


def test_run_days_does_not_guess_when_the_game_does_not_answer(tmp_path, monkeypatch, capsys):
    import run_days

    saves = tmp_path / "Saves"
    make_save(saves, "Duck_9")
    monkeypatch.setenv("STARDEW_SAVES_DIR", str(saves))
    monkeypatch.setattr(run_days, "game_state", lambda: {"ready": False})
    run_days.backup_save(tmp_path / "runs", 4)
    assert not (tmp_path / "runs" / "saves").exists()
