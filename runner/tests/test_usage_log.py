"""Tests for runner/usage_log.py: one csv row per part, totals that match the transcript's result event."""

import csv
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import usage_log
import stream_events as se


def tracker_for(events) -> usage_log.UsageTracker:
    t = usage_log.UsageTracker()
    for ev in events:
        t.feed(ev)
    return t


def test_row_uses_the_result_events_totals():
    row = tracker_for(se.three_turn_part()).row(day=3, part=2, game_time_start="06:00", game_time_end="12:10")
    assert row["day"] == 3 and row["part"] == 2
    assert (row["input_tokens"], row["output_tokens"]) == (23, 85)
    assert (row["cache_creation_input_tokens"], row["cache_read_input_tokens"]) == (1300, 3500)
    assert row["total_tokens"] == 23 + 85 + 1300 + 3500
    assert row["turns"] == 4
    assert row["tool_calls"] == 3
    assert row["tool_errors"] == 1
    assert row["session_id"] == "sess-1" and row["model"] == "claude-haiku-5-5"
    assert row["subtype"] == "success" and row["is_error"] == 0 and row["result_seen"] == 1
    assert row["duration_s"] == 12.0
    assert (row["game_time_start"], row["game_time_end"]) == ("06:00", "12:10")
    assert list(row) == usage_log.FIELDS


def test_result_event_totals_win_over_the_sum_of_assistant_messages():
    # Streamed assistant events can repeat or miss a message; the result event is the authoritative total.
    events = [
        se.init(),
        se.assistant("m1", tool="look", tool_id="t1", usage=se.usage(1, 1, 1, 1)),
        se.tool_result("t1", "Map Farm ..."),
        se.assistant("m2", text="done", usage=se.usage(1, 1, 1, 1)),
        se.result(se.usage(500, 600, 700, 800), turns=2),
    ]
    row = tracker_for(events).row(1, 1)
    assert (row["input_tokens"], row["output_tokens"]) == (500, 600)
    assert (row["cache_creation_input_tokens"], row["cache_read_input_tokens"]) == (700, 800)
    assert row["total_tokens"] == 500 + 600 + 700 + 800


def test_peak_context_is_the_largest_prompt_of_any_turn():
    row = tracker_for(se.three_turn_part()).row(1, 1)
    # turn 3: 5 new + 100 cache write + 1200 cache read; turn 4: 3 + 0 + 1300
    assert row["peak_context_tokens"] == 1305


def test_planned_break_is_detected_from_the_tool_results():
    assert tracker_for(se.three_turn_part(with_break=True)).row(1, 1)["planned_break"] == 1
    assert tracker_for(se.three_turn_part(with_break=False)).row(1, 1)["planned_break"] == 0


def test_repeated_assistant_events_of_one_message_do_not_double_count_tool_calls():
    ev = se.assistant("m1", tool="look", tool_id="t1", usage=se.usage(1, 1, 0, 0))
    t = tracker_for([se.init(), ev, ev, se.result(se.usage(1, 1, 0, 0), turns=1)])
    assert t.row(1, 1)["tool_calls"] == 1


def test_without_a_result_event_it_sums_the_assistant_messages():
    events = [e for e in se.three_turn_part() if e["type"] != "result"]
    row = tracker_for(events).row(1, 1, wall_seconds=42.0)
    assert row["result_seen"] == 0 and row["is_error"] == 1 and row["subtype"] == "no_result"
    assert row["input_tokens"] == 10 + 5 + 5 + 3
    assert row["output_tokens"] == 20 + 30 + 25 + 10
    assert row["cache_read_input_tokens"] == 0 + 1000 + 1200 + 1300
    assert row["turns"] == 4
    assert row["duration_s"] == 42.0


def test_a_message_split_into_several_events_counts_its_usage_once():
    u = se.usage(10, 5, 0, 100)
    events = [se.assistant("m1", text="a", usage=u), se.assistant("m1", tool="look", tool_id="t1", usage=u)]
    row = tracker_for(events).row(1, 1)
    assert (row["input_tokens"], row["cache_read_input_tokens"]) == (10, 100)


def test_error_result_is_recorded():
    t = tracker_for([se.init(), se.result(se.usage(), turns=0, subtype="error_max_turns", is_error=True)])
    row = t.row(1, 1)
    assert row["is_error"] == 1 and row["subtype"] == "error_max_turns" and row["total_tokens"] == 0


def test_garbage_does_not_crash():
    t = usage_log.UsageTracker()
    for ev in ({}, {"type": "assistant"}, {"type": "assistant", "message": {"usage": "x", "content": [None]}},
               {"type": "user", "message": {"content": [3]}}, {"type": "result", "usage": None}):
        t.feed(ev)
    assert t.row(1, 1)["total_tokens"] == 0


# ---------------------------------------------------------------------- csv


def test_append_row_writes_one_header_and_one_row_per_part(tmp_path):
    path = tmp_path / "runs" / "usage.csv"
    for part in (1, 2):
        usage_log.append_row(path, tracker_for(se.three_turn_part()).row(1, part))
    rows = list(csv.DictReader(path.open(encoding="utf-8")))
    assert [r["part"] for r in rows] == ["1", "2"]
    assert path.read_text(encoding="utf-8").count("day,part") == 1
    assert list(rows[0]) == usage_log.FIELDS


def test_an_old_column_layout_is_kept_aside(tmp_path):
    path = tmp_path / "usage.csv"
    path.write_text("day,tokens\n1,5\n", encoding="utf-8")
    usage_log.append_row(path, tracker_for(se.three_turn_part()).row(2, 1))
    old = list(tmp_path.glob("usage.*.old.csv"))
    assert len(old) == 1 and "1,5" in old[0].read_text(encoding="utf-8")
    assert len(usage_log.read_rows(path)) == 1


def test_day_totals_add_up_parts_and_take_the_largest_context():
    t1, t2 = tracker_for(se.three_turn_part(with_break=True)), tracker_for(se.three_turn_part())
    rows = [t1.row(7, 1), t2.row(7, 2), t1.row(8, 1)]
    totals = usage_log.day_totals(rows)
    assert totals[7]["parts"] == 2 and totals[7]["planned_breaks"] == 1
    assert totals[7]["total_tokens"] == 2 * (23 + 85 + 1300 + 3500)
    assert totals[7]["tool_calls"] == 6 and totals[7]["peak_context_tokens"] == 1305
    assert totals[8]["parts"] == 1


def test_csv_round_trip_keeps_the_numbers():
    row = tracker_for(se.three_turn_part()).row(1, 1)
    csv_rows = [{k: str(v) for k, v in row.items()}]  # csv gives back strings
    assert usage_log.day_totals(csv_rows)[1]["total_tokens"] == row["total_tokens"]


def test_day_line_and_summary_mention_the_totals(tmp_path):
    path = tmp_path / "usage.csv"
    usage_log.append_row(path, tracker_for(se.three_turn_part(with_break=True)).row(4, 1))
    usage_log.append_row(path, tracker_for(se.three_turn_part()).row(4, 2))
    line = usage_log.day_line(path, 4)
    assert "2 part(s)" in line and "1 planned break(s)" in line
    assert f"{2 * 4908:,} tokens" in line  # 23 + 85 + 1300 + 3500 per part
    assert "no rows" in usage_log.day_line(path, 99)
    text = usage_log.summary(usage_log.read_rows(path))
    assert "average 9,816 tokens/day" in text and "2.0 parts/day" in text
    assert usage_log.summary([]) == "No usage rows yet."


# ---------------------------------------------------------------------- csv vs transcripts


def write_transcript(log_dir: Path, day: int, part: int, events) -> Path:
    log_dir.mkdir(parents=True, exist_ok=True)
    p = log_dir / f"claude_day_{day:03}_part{part}.jsonl"
    p.write_text(se.jsonl(events), encoding="utf-8")
    return p


def test_check_passes_when_rows_come_from_the_transcripts(tmp_path):
    logs = tmp_path / "logs"
    for part in (1, 2):
        events = se.three_turn_part(with_break=(part == 1))
        write_transcript(logs, 5, part, events)
        usage_log.append_row(tmp_path / "usage.csv", tracker_for(events).row(5, part))
    assert usage_log.check(tmp_path / "usage.csv", logs) == []


def test_check_reports_a_changed_total_and_missing_files(tmp_path):
    logs = tmp_path / "logs"
    events = se.three_turn_part()
    write_transcript(logs, 5, 1, events)
    row = tracker_for(events).row(5, 1)
    row["output_tokens"] += 1000
    usage_log.append_row(tmp_path / "usage.csv", row)
    usage_log.append_row(tmp_path / "usage.csv", tracker_for(events).row(5, 2))  # no transcript for part 2
    write_transcript(logs, 6, 1, events)                                          # no row for day 6
    problems = usage_log.check(tmp_path / "usage.csv", logs)
    assert any("day 5 part 1: output_tokens" in p for p in problems)
    assert any("day 5 part 2: row without a transcript" in p for p in problems)
    assert any("day 6 part 1: transcript without a row" in p for p in problems)


def test_rebuild_recreates_the_csv_from_old_transcripts(tmp_path):
    logs = tmp_path / "logs"
    write_transcript(logs, 1, 1, se.three_turn_part())
    write_transcript(logs, 1, 2, se.three_turn_part(with_break=True))
    write_transcript(logs, 2, 1, se.three_turn_part())
    (logs / "claude_consolidate.jsonl").write_text("{}\n", encoding="utf-8")  # not a day part
    csv_path = tmp_path / "usage.csv"
    csv_path.write_text("stale\n", encoding="utf-8")
    assert usage_log.rebuild(csv_path, logs) == 3
    rows = usage_log.read_rows(csv_path)
    assert [(r["day"], r["part"]) for r in rows] == [("1", "1"), ("1", "2"), ("2", "1")]
    assert usage_log.check(csv_path, logs) == []
    assert len(list(tmp_path.glob("usage.*.old.csv"))) == 1


def test_command_line(tmp_path, capsys):
    logs = tmp_path / "logs"
    write_transcript(logs, 1, 1, se.three_turn_part())
    assert usage_log.main(["rebuild", "--out", str(tmp_path)]) == 0
    assert usage_log.main(["check", "--out", str(tmp_path)]) == 0
    assert usage_log.main(["summary", "--out", str(tmp_path)]) == 0
    out = capsys.readouterr().out
    assert "wrote 1 row(s)" in out and "OK:" in out and "average" in out
    (tmp_path / "usage.csv").write_text(
        (tmp_path / "usage.csv").read_text(encoding="utf-8").replace(",85,", ",86,"), encoding="utf-8")
    assert usage_log.main(["check", "--out", str(tmp_path)]) == 1
