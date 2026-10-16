"""Tests for runner/compare_runs.py: two generated runs compared at checkpoint days."""

from __future__ import annotations

import compare_runs
import stats
from stats_fixtures import write_stats, write_transcript


def _run(root, incomes, *, money_step=100, failed=2, calls=20, p1_mine=0, passed_out_days=(), player="p1"):
    """A run folder with logs/ and one stats file per entry of `incomes` (days 1..n)."""
    logs = root / "logs"
    for day, inc in enumerate(incomes, start=1):
        out = day in passed_out_days
        write_stats(logs, day, player, income=inc, money=money_step * day, failed=failed, calls=calls,
                    planted=1, harvested=1, died=0, hearts=day, mine=p1_mine * day,
                    skills=(day, 0, 0, 0, 0), ended_by="passed_out" if out else "bed", cause="2am" if out else None)
        write_transcript(logs, day, 1, turns=10, cost=0.5, output_tokens=1000, input_tokens=2000,
                         cache_read=0, cache_creation=0)
    return root


def _rows(root, player="p1"):
    return stats.collect(stats.find_log_dir(root), player)


def _table(comparison, day):
    for d, rows in comparison:
        if d == day:
            return {label: (a, b, diff) for label, a, b, diff in rows}
    raise AssertionError(f"no checkpoint {day}")


def test_at_checkpoint_snapshots_and_cumulative_totals(tmp_path):
    rows = _rows(_run(tmp_path / "a", [100, 200, 300], passed_out_days=(2,)))
    cp = compare_runs.at_checkpoint(rows, 2)
    assert cp["money"] == 200 and cp["hearts"] == 2 and cp["skills"] == 2  # the state on day 2
    assert cp["income"] == 300 and cp["planted"] == 2 and cp["calls"] == 40 and cp["failed"] == 4  # days 1-2 added up
    assert cp["fail_rate"] == 10.0
    assert cp["turns"] == 20 and cp["cost"] == 1.0 and cp["out_tokens"] == 2000
    assert cp["days_with_stats"] == 2 and cp["passed_out_days"] == 1


def test_at_checkpoint_none_when_not_reached(tmp_path):
    rows = _rows(_run(tmp_path / "a", [100, 200]))
    assert compare_runs.at_checkpoint(rows, 3) is None
    assert compare_runs.at_checkpoint({}, 1) is None


def test_at_checkpoint_uses_the_last_earlier_day_when_the_checkpoint_day_has_no_file(tmp_path):
    run = _run(tmp_path / "a", [100, 200, 300, 400])
    (run / "logs" / "stats_003.json").unlink()
    cp = compare_runs.at_checkpoint(_rows(run), 3)
    assert cp["money"] == 200 and cp["income"] == 300 and cp["days_with_stats"] == 2


def test_compare_shows_both_runs_and_the_difference(tmp_path):
    a = _rows(_run(tmp_path / "a", [100, 100, 100], failed=2, calls=20))
    b = _rows(_run(tmp_path / "b", [300, 300, 300], failed=1, calls=20, money_step=250))
    t = _table(compare_runs.compare(a, b, [3]), 3)
    assert t["Income g"] == ("300", "900", "+600")
    assert t["Money g"] == ("300", "750", "+450")
    assert t["Failed calls"] == ("6", "3", "-3")
    assert t["Failure rate"] == ("10.0%", "5.0%", "-5.0 pt")
    assert t["Out tokens k"] == ("3.0", "3.0", "+0.0")
    assert t["Cost USD"] == ("1.50", "1.50", "+0.00")
    assert t["Days with stats"] == ("3", "3", "+0")


def test_compare_marks_unreached_checkpoints_with_a_dash(tmp_path):
    a = _rows(_run(tmp_path / "a", [100, 100, 100]))
    b = _rows(_run(tmp_path / "b", [50]))
    comparison = compare_runs.compare(a, b, [1, 3, 7])
    assert _table(comparison, 1)["Income g"] == ("100", "50", "-50")
    assert _table(comparison, 3)["Income g"] == ("300", "-", "-")
    assert set(_table(comparison, 7)["Income g"]) == {"-"}


def test_render_is_markdown_with_a_section_per_checkpoint(tmp_path):
    a = _rows(_run(tmp_path / "a", [100, 100]))
    b = _rows(_run(tmp_path / "b", [200, 200]))
    text = compare_runs.render("runA/p1", "runB/p1", compare_runs.compare(a, b, [1, 2]))
    assert "## Day 1" in text and "## Day 2" in text
    assert "| Metric | A: runA/p1 | B: runB/p1 | B - A |" in text
    assert "| Income g | 100 | 200 | +100 |" in text


def test_main_compares_two_run_folders(tmp_path, capsys):
    _run(tmp_path / "haiku", [100, 100, 100])
    _run(tmp_path / "flash", [150, 150, 150])
    assert compare_runs.main([str(tmp_path / "haiku"), str(tmp_path / "flash"), "--days", "2", "3"]) == 0
    out = capsys.readouterr().out
    assert "A: haiku/p1" in out and "B: flash/p1" in out
    assert "## Day 2" in out and "## Day 3" in out and "| Income g | 200 | 300 | +100 |" in out


def test_main_compares_two_seats_of_one_run(tmp_path, capsys):
    root = tmp_path / "run"
    _run(root, [100, 100], player="p1")
    _run(root, [400, 400], player="p2")
    assert compare_runs.main([str(root), str(root), "--player-b", "p2", "--days", "2"]) == 0
    out = capsys.readouterr().out
    assert "A: run/p1" in out and "B: run/p2" in out and "| Income g | 200 | 800 | +600 |" in out


def test_main_default_checkpoints_and_missing_stats(tmp_path, capsys):
    _run(tmp_path / "a", [100] * 8)
    _run(tmp_path / "b", [100] * 8)
    assert compare_runs.main([str(tmp_path / "a"), str(tmp_path / "b")]) == 0
    out = capsys.readouterr().out
    assert "## Day 7" in out and "## Day 100" in out
    empty = tmp_path / "empty"
    empty.mkdir()
    assert compare_runs.main([str(tmp_path / "a"), str(empty)]) == 1
    assert "No stats_*.json" in capsys.readouterr().err
