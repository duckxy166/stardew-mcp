"""run_days.py with the game and `claude` faked: the morning line reaches the prompt, the report is written."""

from __future__ import annotations

import sys

import run_days
from stats_fixtures import write_stats


def _play(tmp_path, monkeypatch, first_day: int, days: int):
    logs = tmp_path / "logs"
    state = {"day": first_day}
    prompts: list[tuple[int, int, str]] = []

    def fake_session(args, out_dir, day, prompt, system_prompt, part):
        prompts.append((day, part, prompt))
        write_stats(logs, day, income=100 * day)  # the mod writes the day's file when the day rolls over
        state["day"] = day + 1
        return 1

    monkeypatch.setattr(run_days, "wait_ready", lambda timeout=600: {"ready": True, "day": state["day"]})
    monkeypatch.setattr(run_days, "run_session", fake_session)
    monkeypatch.setattr(run_days, "backup_save", lambda out_dir, day: None)
    monkeypatch.setattr(run_days.time, "sleep", lambda s: None)
    monkeypatch.setattr(sys, "argv", ["run_days.py", "--days", str(days), "--out", str(tmp_path), "--skip-smoke"])
    run_days.main()
    return prompts


def test_first_prompt_of_a_day_carries_yesterdays_numbers(tmp_path, monkeypatch):
    write_stats(tmp_path / "logs", 1, income=100)
    prompts = _play(tmp_path, monkeypatch, first_day=2, days=2)
    assert [p[0] for p in prompts] == [2, 3]
    assert "สถิติเมื่อวาน (วันที่ 1)" in prompts[0][2]
    assert "รายได้ 100g" in prompts[0][2]
    assert "สถิติเมื่อวาน (วันที่ 2)" in prompts[1][2]
    assert "รายได้ 200g (เฉลี่ย 100g)" in prompts[1][2]
    assert prompts[0][2].count("go_to_bed") == 1  # the original instruction is still there


def test_no_morning_line_without_stats_and_the_batch_report_is_written(tmp_path, monkeypatch):
    prompts = _play(tmp_path, monkeypatch, first_day=1, days=2)
    assert "สถิติเมื่อวาน" not in prompts[0][2]  # day 1 has no yesterday
    assert "สถิติเมื่อวาน (วันที่ 1)" in prompts[1][2]
    report = (tmp_path / "batch_report.md").read_text(encoding="utf-8")
    assert report.startswith("# Batch report: days 1-2")
    assert "2 of 2 days with stats" in report
