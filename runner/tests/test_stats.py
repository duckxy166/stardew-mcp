"""Tests for runner/stats.py: stats files, usage from transcripts, the morning line and batch_report.md."""

from __future__ import annotations

import json

import pytest

import stats
from stats_fixtures import stats_doc, write_stats, write_transcript


# ---------------------------------------------------------------------- files


def test_stats_path_names_match_the_mod(tmp_path):
    assert stats.stats_path(tmp_path, 3).name == "stats_003.json"
    assert stats.stats_path(tmp_path, 12, "p2").name == "stats_012_p2.json"
    assert stats.usage_path(tmp_path, 7).name == "usage_007.json"


def test_find_log_dir_prefers_logs_subfolder(tmp_path):
    (tmp_path / "logs").mkdir()
    assert stats.find_log_dir(tmp_path) == tmp_path / "logs"
    assert stats.find_log_dir(tmp_path / "logs") == tmp_path / "logs"
    other = tmp_path / "plain"
    other.mkdir()
    assert stats.find_log_dir(other) == other


def test_stats_days_per_player_and_ignores_other_files(tmp_path):
    write_stats(tmp_path, 2)
    write_stats(tmp_path, 1)
    write_stats(tmp_path, 1, "p2")
    write_stats(tmp_path, 5, "p3")
    (tmp_path / "usage_001.json").write_text("{}", encoding="utf-8")
    (tmp_path / "stats_009.json.tmp").write_text("{}", encoding="utf-8")
    (tmp_path / "stats_notes.json").write_text("{}", encoding="utf-8")
    assert stats.stats_days(tmp_path) == [1, 2]
    assert stats.stats_days(tmp_path, "p2") == [1]
    assert stats.stats_days(tmp_path, "p3") == [5]


def test_load_stats_missing_corrupt_and_wrong_shape(tmp_path):
    assert stats.load_stats(tmp_path, 1) is None
    (tmp_path / "stats_001.json").write_text("{not json", encoding="utf-8")
    assert stats.load_stats(tmp_path, 1) is None
    (tmp_path / "stats_002.json").write_text("[1, 2]", encoding="utf-8")
    assert stats.load_stats(tmp_path, 2) is None
    write_stats(tmp_path, 3)
    assert stats.load_stats(tmp_path, 3)["day"] == 3


def test_wait_for_stats_returns_as_soon_as_the_file_appears(tmp_path):
    sleeps = []

    def fake_sleep(s):
        sleeps.append(s)
        if len(sleeps) == 2:
            write_stats(tmp_path, 4)

    got = stats.wait_for_stats(tmp_path, 4, sleep=fake_sleep)
    assert got is not None and got["day"] == 4
    assert len(sleeps) == 2


def test_wait_for_stats_gives_up_after_the_timeout(tmp_path):
    sleeps = []
    assert stats.wait_for_stats(tmp_path, 4, timeout=2.0, poll=0.5, sleep=sleeps.append) is None
    assert sum(sleeps) == pytest.approx(2.0)


# ---------------------------------------------------------------------- usage from transcripts


def test_summarize_transcripts_adds_up_the_parts(tmp_path):
    write_transcript(tmp_path, 3, 1, turns=10, cost=0.25, tool_uses=3, input_tokens=100, output_tokens=2000,
                     cache_read=5000, cache_creation=300)
    write_transcript(tmp_path, 3, 2, turns=4, cost=0.10, tool_uses=2, input_tokens=50, output_tokens=500,
                     cache_read=1000, cache_creation=0, extra_lines=["not json", "[1]", ""])
    write_transcript(tmp_path, 4, 1, turns=99)  # another day, not counted
    u = stats.summarize_transcripts(tmp_path, 3)
    assert u["parts"] == 2 and u["results"] == 2
    assert u["turns"] == 14
    assert u["costUsd"] == pytest.approx(0.35)
    assert u["toolUses"] == 5
    assert u["inputTokens"] == 150 and u["outputTokens"] == 2500
    assert u["cacheReadTokens"] == 6000 and u["cacheCreationTokens"] == 300


def test_summarize_transcripts_none_without_files(tmp_path):
    assert stats.summarize_transcripts(tmp_path, 1) is None
    assert stats.write_usage(tmp_path, 1) is None
    assert not stats.usage_path(tmp_path, 1).exists()


def test_write_usage_round_trips(tmp_path):
    write_transcript(tmp_path, 2, 1, turns=7, cost=0.5)
    written = stats.write_usage(tmp_path, 2)
    assert stats.load_usage(tmp_path, 2) == written
    assert stats.load_usage(tmp_path, 2)["turns"] == 7


def test_load_usage_prefers_the_sidecar_and_falls_back_to_transcripts(tmp_path):
    write_transcript(tmp_path, 1, 1, turns=7)
    assert stats.load_usage(tmp_path, 1)["turns"] == 7  # no usage_001.json yet: summarised on the fly
    assert not stats.usage_path(tmp_path, 1).exists()
    stats.usage_path(tmp_path, 1).write_text(json.dumps({"turns": 99}), encoding="utf-8")
    assert stats.load_usage(tmp_path, 1)["turns"] == 99
    assert stats.load_usage(tmp_path, 2) is None


def test_part_numbers_sort_numerically(tmp_path):
    for part in (10, 2, 1):
        write_transcript(tmp_path, 1, part)
    files = sorted(tmp_path.glob("claude_day_001_part*.jsonl"), key=stats._part_number)
    assert [stats._part_number(f) for f in files] == [1, 2, 10]


# ---------------------------------------------------------------------- metrics


def test_day_metrics_reads_the_mod_document(tmp_path):
    doc = stats_doc(5, income=320, money=1500, planted=8, harvested=6, died=1, unwatered=2, energy=12, calls=80,
                    failed=4, hearts=9, mine=15, skills=(2, 1, 0, 3, 0), ended_by="passed_out", cause="2am")
    m = stats.day_metrics(doc, {"turns": 20, "outputTokens": 3000, "inputTokens": 100, "cacheReadTokens": 900,
                                "cacheCreationTokens": 0, "costUsd": 0.4})
    assert (m["day"], m["income"], m["money"], m["planted"], m["harvested"], m["died"], m["unwatered"]) == \
        (5, 320, 1500, 8, 6, 1, 2)
    assert (m["energy"], m["calls"], m["failed"], m["hearts"], m["mine"], m["skills"]) == (12, 80, 4, 9, 15, 6)
    assert m["passed_out"] is True and m["pass_out_cause"] == "2am" and m["ended_by"] == "passed_out"
    assert m["turns"] == 20 and m["out_tokens"] == 3000 and m["in_tokens"] == 1000 and m["cost"] == 0.4
    assert m["skill_levels"]["fishing"] == 3


def test_day_metrics_tolerates_missing_and_odd_values():
    m = stats.day_metrics(None)
    assert m["income"] is None and m["calls"] is None and m["skills"] is None and m["passed_out"] is None
    assert m["partial"] is False and m["date"] == ""
    odd = stats.day_metrics({"income": {"earned": "lots"}, "toolCalls": {"total": True}, "skills": [1, 2]})
    assert odd["income"] is None and odd["calls"] is None and odd["skills"] is None


def test_collect_reads_stats_and_usage(tmp_path):
    write_stats(tmp_path, 1, income=10)
    write_stats(tmp_path, 3, income=30)
    write_transcript(tmp_path, 3, 1, turns=5)
    rows = stats.collect(tmp_path)
    assert sorted(rows) == [1, 3]
    assert rows[1]["turns"] is None and rows[3]["turns"] == 5
    assert sorted(stats.collect(tmp_path, days=[3, 4])) == [3]


# ---------------------------------------------------------------------- morning line


def _history(tmp_path, incomes, faileds, **kw):
    for i, (inc, fail) in enumerate(zip(incomes, faileds), start=1):
        write_stats(tmp_path, i, income=inc, failed=fail, **kw)


def test_morning_line_matches_the_issue_example(tmp_path):
    _history(tmp_path, [100, 200, 240, 320], [10, 9, 8, 4])
    line = stats.morning_line(tmp_path, 5)
    assert "รายได้ 320g (เฉลี่ย 180g)" in line
    assert "ล้มเหลว 4 ครั้ง (เฉลี่ย 9)" in line
    assert "รายได้ 320g (เฉลี่ย 180g) · " in line  # the parts are joined with a middle dot
    assert line.startswith("สถิติเมื่อวาน (วันที่ 4) เทียบเฉลี่ย 3 วันก่อนหน้า: ")


def test_morning_line_empty_on_day_one_or_without_yesterday(tmp_path):
    write_stats(tmp_path, 1)
    assert stats.morning_line(tmp_path, 1) == ""
    assert stats.morning_line(tmp_path, 3) == ""  # day 2 has no stats
    assert stats.morning_line(tmp_path / "missing", 2) == ""


def test_morning_line_without_earlier_days_has_no_average(tmp_path):
    write_stats(tmp_path, 1, income=250, failed=3)
    line = stats.morning_line(tmp_path, 2)
    assert "รายได้ 250g" in line and "ล้มเหลว 3 ครั้ง" in line
    assert "เฉลี่ย" not in line


def test_morning_line_with_fewer_than_three_earlier_days(tmp_path):
    _history(tmp_path, [100, 300], [6, 2])
    line = stats.morning_line(tmp_path, 3)
    assert "เทียบเฉลี่ย 1 วันก่อนหน้า" in line
    assert "รายได้ 300g (เฉลี่ย 100g)" in line


def test_morning_line_uses_only_the_three_days_before_yesterday(tmp_path):
    _history(tmp_path, [9000, 100, 200, 300, 500], [50, 4, 4, 4, 1])
    line = stats.morning_line(tmp_path, 6)
    assert "รายได้ 500g (เฉลี่ย 200g)" in line  # days 2-4, not day 1


def test_morning_line_skips_partial_days_in_the_average(tmp_path):
    write_stats(tmp_path, 1, income=100)
    write_stats(tmp_path, 2, income=5, partial=True)
    write_stats(tmp_path, 3, income=300)
    line = stats.morning_line(tmp_path, 4)
    assert "รายได้ 300g (เฉลี่ย 100g)" in line
    assert "เทียบเฉลี่ย 1 วันก่อนหน้า" in line


def test_morning_line_flags_a_partial_yesterday(tmp_path):
    write_stats(tmp_path, 1, income=40, partial=True)
    assert "[ข้อมูลไม่ครบวัน]" in stats.morning_line(tmp_path, 2)


def test_morning_line_mentions_trouble_only_when_there_is_some(tmp_path):
    write_stats(tmp_path, 1, died=0, unwatered=0, harvested=0)
    calm = stats.morning_line(tmp_path, 2)
    assert "พืชตาย" not in calm and "ไม่ได้รดน้ำ" not in calm and "หมดสติ" not in calm and "เก็บเกี่ยว" not in calm
    write_stats(tmp_path, 2, died=2, unwatered=5, harvested=7, ended_by="passed_out", cause="2am")
    rough = stats.morning_line(tmp_path, 3)
    assert "พืชตาย 2" in rough
    assert "ไม่ได้รดน้ำก่อนนอน 5 ต้น" in rough
    assert "เก็บเกี่ยว 7" in rough
    assert "หมดสติ" in rough


def test_morning_line_rounds_half_up(tmp_path):
    _history(tmp_path, [100, 101, 500], [1, 1, 1])
    # the two earlier days average 100.5; Python's round() would give 100
    assert "รายได้ 500g (เฉลี่ย 101g)" in stats.morning_line(tmp_path, 4)


def test_morning_line_reads_the_requested_seat(tmp_path):
    write_stats(tmp_path, 1, "p1", income=111)
    write_stats(tmp_path, 1, "p2", income=222)
    assert "รายได้ 111g" in stats.morning_line(tmp_path, 2)
    assert "รายได้ 222g" in stats.morning_line(tmp_path, 2, "p2")


def test_morning_for_prompt_never_raises(tmp_path, monkeypatch, capsys):
    write_stats(tmp_path, 1)
    assert stats.morning_for_prompt(tmp_path, 2).startswith("สถิติเมื่อวาน")

    def boom(*a, **k):
        raise ValueError("bad file")

    monkeypatch.setattr(stats, "morning_line", boom)
    assert stats.morning_for_prompt(tmp_path, 2) == ""
    assert "no morning line" in capsys.readouterr().out


# ---------------------------------------------------------------------- batch report


def _row(markdown: str, first_cell: str) -> list[str]:
    for line in markdown.splitlines():
        if line.startswith("| ") and line.split("|")[1].strip() == first_cell:
            return [c.strip() for c in line.strip().strip("|").split("|")]
    raise AssertionError(f"no row starting with {first_cell!r} in:\n{markdown}")


def _report(tmp_path, days, **kw):
    path = stats.write_batch_report(tmp_path, tmp_path / "logs", days, **kw)
    return path, path.read_text(encoding="utf-8")


def test_batch_report_has_a_row_per_day_with_totals_and_averages(tmp_path):
    logs = tmp_path / "logs"
    write_stats(logs, 1, income=100, money=600, planted=4, harvested=0, calls=40, failed=8, hearts=2, mine=0)
    write_stats(logs, 2, income=300, money=900, planted=6, harvested=3, calls=60, failed=2, hearts=5, mine=10)
    write_transcript(logs, 1, 1, turns=10, output_tokens=2000, input_tokens=500, cache_read=0, cache_creation=0)
    write_transcript(logs, 2, 1, turns=30, output_tokens=4000, input_tokens=1500, cache_read=0, cache_creation=0)
    path, md = _report(tmp_path, [1, 2])
    assert path == tmp_path / "batch_report.md"
    assert md.startswith("# Batch report: days 1-2")
    header = [c.strip() for c in next(l for l in md.splitlines() if l.startswith("| Day")).strip("|").split("|")]
    d1, d2, total, avg = _row(md, "1"), _row(md, "2"), _row(md, "**Total**"), _row(md, "**Average**")
    col = {name: i for i, name in enumerate(header)}
    assert len(d1) == len(header) == len(total) == len(avg)
    assert d1[col["Income g"]] == "100" and d2[col["Income g"]] == "300"
    assert total[col["Income g"]] == "400" and avg[col["Income g"]] == "200.0"
    assert total[col["Money g"]] == "900"  # the last day, not a sum
    assert total[col["Planted"]] == "10" and total[col["Failed"]] == "10" and total[col["Calls"]] == "100"
    assert total[col["Hearts"]] == "5" and total[col["Mine"]] == "10"
    assert d1[col["Turns"]] == "10" and total[col["Turns"]] == "40" and avg[col["Turns"]] == "20.0"
    assert d1[col["Out k"]] == "2.0" and total[col["Out k"]] == "6.0" and avg[col["Out k"]] == "3.0"
    assert d2[col["In k"]] == "1.5"
    assert d1[col["Date"]] == "Spring 1, Year 1" and d1[col["Ended"]] == "bed"


def test_batch_report_lists_days_without_stats_and_leaves_gaps_empty(tmp_path):
    logs = tmp_path / "logs"
    write_stats(logs, 1, income=50)
    write_stats(logs, 3, income=70)
    _, md = _report(tmp_path, [1, 2, 3])
    assert "2 of 3 days with stats" in md
    assert "## Days without stats" in md and "\n2 (" in md
    assert _row(md, "**Total**")[3] == "120"


def test_batch_report_without_any_stats_still_renders(tmp_path):
    (tmp_path / "logs").mkdir()
    _, md = _report(tmp_path, [1, 2])
    assert "0 of 2 days with stats" in md
    assert "## Days without stats" in md
    assert stats.write_batch_report(tmp_path, tmp_path / "logs", []) is None


def test_batch_report_marks_partial_days_and_pass_outs(tmp_path):
    logs = tmp_path / "logs"
    write_stats(logs, 1, partial=True)
    write_stats(logs, 2, ended_by="passed_out", cause="2am")
    write_stats(logs, 3, ended_by="passed_out", cause=None)
    write_stats(logs, 4, ended_by="unknown")
    _, md = _report(tmp_path, [1, 2, 3, 4])
    assert _row(md, "1*")[2] == "bed"
    assert _row(md, "2")[2] == "passed out (2am)"
    assert _row(md, "3")[2] == "passed out"
    assert _row(md, "4")[2] == "unknown"
    assert "## How the days ended" in md
    assert "bed 1" in md and "passed out 2" in md and "2am 1" in md


def test_batch_report_failure_hotspots_sum_over_days(tmp_path):
    logs = tmp_path / "logs"
    write_stats(logs, 1, failed=5, failures=[{"tool": "move_to", "message": "no route to (#,#)", "count": 3},
                                             {"tool": "plant", "message": "dirt | not tilled", "count": 2}])
    write_stats(logs, 2, failed=4, failures=[{"tool": "move_to", "message": "no route to (#,#)", "count": 4}])
    _, md = _report(tmp_path, [1, 2])
    section = md.split("## Failure hotspots")[1].split("##")[0]
    rows = [l for l in section.splitlines() if l.startswith("| ") and "Tool" not in l]
    assert rows[0].startswith("| move_to | no route to (#,#) | 7 |")
    assert "plant" in rows[1] and "dirt \\| not tilled" in rows[1]


def test_batch_report_no_failures(tmp_path):
    logs = tmp_path / "logs"
    write_stats(logs, 1, failed=0)
    _, md = _report(tmp_path, [1])
    assert "No failed tool calls recorded." in md


def test_batch_report_links_lessons_only_when_present(tmp_path):
    write_stats(tmp_path / "logs", 1)
    _, md = _report(tmp_path, [1])
    assert "(not generated for this batch)" in md and "](lessons.md)" not in md
    (tmp_path / "lessons.md").write_text("# lessons\n", encoding="utf-8")
    _, md = _report(tmp_path, [1])
    assert "[lessons.md](lessons.md)" in md


def test_batch_report_single_day_title_and_seat(tmp_path):
    write_stats(tmp_path / "logs", 4, "p2")
    _, md = _report(tmp_path, [4], player="p2")
    assert md.startswith("# Batch report: day 4") and "Player p2" in md
    assert _row(md, "4")[0] == "4"


def test_batch_report_skill_levels_come_from_the_last_day(tmp_path):
    logs = tmp_path / "logs"
    write_stats(logs, 1, skills=(1, 0, 0, 0, 0))
    write_stats(logs, 2, skills=(2, 1, 0, 0, 0))
    _, md = _report(tmp_path, [1, 2])
    assert "farming 2 · mining 1" in md
    assert _row(md, "**Total**")[3 + [c[0] for c in stats._COLUMNS].index("Skills")] == "3"


# ---------------------------------------------------------------------- finish_day and the command line


def test_finish_day_writes_usage_and_the_report(tmp_path, capsys):
    logs = tmp_path / "logs"
    write_stats(logs, 1)
    write_transcript(logs, 1, 1, turns=12)
    stats.finish_day(tmp_path, logs, 1, [1])
    assert stats.load_usage(logs, 1)["turns"] == 12
    assert (tmp_path / "batch_report.md").exists()
    assert "batch report" in capsys.readouterr().out


def test_finish_day_updates_the_report_as_days_are_added(tmp_path):
    logs = tmp_path / "logs"
    write_stats(logs, 1, income=10)
    stats.finish_day(tmp_path, logs, 1, [1])
    write_stats(logs, 2, income=30)
    stats.finish_day(tmp_path, logs, 2, [1, 2])
    md = (tmp_path / "batch_report.md").read_text(encoding="utf-8")
    assert "days 1-2" in md and _row(md, "**Total**")[3] == "40"


def test_finish_day_never_raises(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(stats.time, "sleep", lambda s: None)
    logs = tmp_path / "logs"
    logs.mkdir()
    # no stats file at all: warns, still writes the report
    stats.finish_day(tmp_path, logs, 1, [1])
    assert "no stats_001.json" in capsys.readouterr().out
    assert (tmp_path / "batch_report.md").exists()
    # an output folder that cannot be written
    blocker = tmp_path / "blocker"
    blocker.write_text("a file, not a folder", encoding="utf-8")
    stats.finish_day(blocker, logs, 1, [1])
    assert "could not write the report" in capsys.readouterr().out


def test_cli_report_and_morning(tmp_path, capsys):
    logs = tmp_path / "run" / "logs"
    _history(logs, [100, 200, 240, 320], [10, 9, 8, 4])
    assert stats.main(["report", str(tmp_path / "run")]) == 0
    md = (tmp_path / "run" / "batch_report.md").read_text(encoding="utf-8")
    assert "days 1-4" in md
    assert stats.main(["report", str(tmp_path / "run"), "--from", "2", "--to", "3"]) == 0
    assert "days 2-3" in (tmp_path / "run" / "batch_report.md").read_text(encoding="utf-8")
    capsys.readouterr()
    assert stats.main(["morning", str(tmp_path / "run"), "--day", "5"]) == 0
    assert "รายได้ 320g (เฉลี่ย 180g)" in capsys.readouterr().out
    assert stats.main(["morning", str(tmp_path / "run"), "--day", "1"]) == 0
    assert "no line" in capsys.readouterr().out


def test_cli_report_fails_without_stats(tmp_path, capsys):
    assert stats.main(["report", str(tmp_path)]) == 1
    assert "No stats_*.json" in capsys.readouterr().err
