"""run_days.py: reading a `claude -p --output-format stream-json` session, and the diary / recent-action memory.

`run_session` is driven with a fake `subprocess.Popen` that replays the synthetic transcripts in runner/samples/,
so no `claude` binary, network or game is needed. What these tests pin down is how the runner reads the
stream (tool counting, session id, error handling) and what it writes (transcript file, launch command).
"""

from __future__ import annotations

import io
import json
import subprocess
from pathlib import Path
from types import SimpleNamespace

import pytest

import run_days

SAMPLES = Path(__file__).resolve().parents[1] / "samples"
GAME_LOGS = SAMPLES / "game_logs"


# --------------------------------------------------------------------------------------- fake claude

class FakeProc:
    """What run_session uses of a Popen: an iterable stdout and wait()."""

    def __init__(self, text: str):
        self.stdout = io.StringIO(text)
        self.waited = False

    def wait(self):
        self.waited = True
        return 0


class Session:
    """Everything one fake `claude` run did, for the assertions."""

    def __init__(self, out_dir: Path):
        self.out_dir = out_dir
        self.cmd: list[str] = []
        self.popen_kwargs: dict = {}
        self.proc: FakeProc | None = None
        self.posts: list[tuple[str, dict]] = []
        self.post_error = False
        self.stream = ""
        self.returned: int | None = None
        self.prompt = ""
        self.system_prompt = ""

    def transcript(self, day: int = 2, part: int = 1) -> Path:
        return self.out_dir / "logs" / f"claude_day_{day:03}_part{part}.jsonl"


@pytest.fixture
def play(tmp_path, monkeypatch):
    """play("claude_stream_day.jsonl") runs run_session against that sample and returns the Session."""
    out_dir = tmp_path / "out"
    (out_dir / "logs").mkdir(parents=True)
    session = Session(out_dir)

    def popen(cmd, **kwargs):
        session.cmd = list(cmd)
        session.popen_kwargs = kwargs
        session.proc = FakeProc(session.stream)
        return session.proc

    def api_post(path, body):
        session.posts.append((path, body))
        if session.post_error:
            raise OSError("game is not listening")
        return {}

    monkeypatch.setattr(run_days.subprocess, "Popen", popen)
    monkeypatch.setattr(run_days, "api_post", api_post)

    def run(sample: str, *, day: int = 2, part: int = 1, model: str = "test-model", max_turns: int = 600,
            prompt: str = "play the day", system_prompt: str = "You are a farmer.", post_error: bool = False) -> Session:
        session.stream = (SAMPLES / sample).read_text(encoding="utf-8")
        session.post_error = post_error
        session.prompt, session.system_prompt = prompt, system_prompt
        args = SimpleNamespace(model=model, max_turns=max_turns)
        session.returned = run_days.run_session(args, out_dir, day, prompt, system_prompt, part)
        return session

    return run


# ------------------------------------------------------------------------------------ stream parsing

def test_counts_tool_calls_and_prints_each_one(play, capsys):
    s = play("claude_stream_day.jsonl")
    assert s.returned == 6
    out = capsys.readouterr().out
    for number, name in enumerate(["think", "status", "look", "walk_to", "use_tool", "go_to_bed"], start=1):
        assert f"  #{number} {name}" in out
    # The caption is the thought for `think`, the reason for actions, and nothing for the rest.
    assert "  #1 think วันนี้จะถางหญ้าแล้วปลูกพาร์สนิป" in out
    assert "  #4 walk_to ไปที่แปลงผัก" in out
    assert "mcp__stardew__" not in out


def test_prints_the_result_line(play, capsys):
    play("claude_stream_day.jsonl")
    assert "  result: success turns=7 cost=$0.042" in capsys.readouterr().out


def test_announces_the_session_with_its_id_and_model(play, capsys):
    play("claude_stream_day.jsonl", day=2, part=1, model="test-model")
    out = capsys.readouterr().out
    assert "day 2 part 1: starting test-model" in out
    assert "  session sample-session-0001 model sample-model" in out  # the model the stream reports wins


def test_reports_the_session_to_the_game_meta_endpoint(play):
    s = play("claude_stream_day.jsonl", day=2, part=1)
    assert len(s.posts) == 1
    path, body = s.posts[0]
    assert path == "/api/meta"
    assert body["session"] == "sample-session-0001"
    assert body["modelId"] == "sample-model"
    assert body["run"] == "day 2 · part 1"
    assert "sample-session-0001" in body["event"]


def test_meta_failure_is_ignored(play):
    # The overlay label is a nicety: a game that refuses /api/meta must not abort the session.
    s = play("claude_stream_day.jsonl", post_error=True)
    assert s.returned == 6
    assert len(s.posts) == 1  # it did try
    assert s.transcript().exists()


def test_transcript_is_saved_line_for_line(play):
    s = play("claude_stream_day.jsonl", day=2, part=1)
    saved = s.transcript(2, 1).read_text(encoding="utf-8").splitlines()
    assert saved == (SAMPLES / "claude_stream_day.jsonl").read_text(encoding="utf-8").splitlines()


def test_transcript_name_has_day_and_part(play):
    s = play("claude_stream_day.jsonl", day=12, part=3)
    assert s.transcript(12, 3).exists()
    assert not s.transcript(12, 1).exists()


def test_lines_that_are_not_json_are_echoed_and_still_saved(play, capsys):
    s = play("claude_stream_stderr_noise.jsonl")
    out = capsys.readouterr().out
    assert "  Warning: MCP server 'stardew' reconnecting (sample stderr line)" in out
    assert "  not json at all {" in out
    saved = s.transcript().read_text(encoding="utf-8").splitlines()
    assert saved == (SAMPLES / "claude_stream_stderr_noise.jsonl").read_text(encoding="utf-8").splitlines()


def test_noise_does_not_change_the_tool_count_or_session(play):
    s = play("claude_stream_stderr_noise.jsonl")
    assert s.returned == 1
    assert s.posts[0][1]["session"] == "sample-session-0002"


def test_hitting_max_turns_is_returned_not_raised(play, capsys):
    s = play("claude_stream_max_turns.jsonl")
    assert s.returned == 2  # main() then starts the next part of the day
    assert "result: error_max_turns turns=600" in capsys.readouterr().out


def test_an_error_before_any_tool_call_stops_the_runner(play):
    with pytest.raises(SystemExit) as stop:
        play("claude_stream_error.jsonl")
    assert "Invalid API key" in str(stop.value)


def test_waits_for_the_process_to_exit(play):
    s = play("claude_stream_day.jsonl")
    assert s.proc is not None and s.proc.waited


# ---------------------------------------------------------------------------------- what is launched

def test_launch_command_is_locked_to_the_game_tools(play):
    s = play("claude_stream_day.jsonl", model="test-model", max_turns=321, prompt="PROMPT")
    cmd = s.cmd
    assert cmd[0] == "claude"
    assert cmd[cmd.index("-p") + 1] == "PROMPT"
    assert cmd[cmd.index("--model") + 1] == "test-model"
    assert cmd[cmd.index("--max-turns") + 1] == "321"
    assert cmd[cmd.index("--output-format") + 1] == "stream-json"
    assert "--verbose" in cmd  # stream-json needs it
    assert "--strict-mcp-config" in cmd
    assert cmd[cmd.index("--tools") + 1] == ""  # no shell, no file edits
    assert cmd[cmd.index("--allowedTools") + 1] == "mcp__stardew__*"


def test_mcp_config_points_at_the_game(play):
    s = play("claude_stream_day.jsonl")
    config_path = Path(s.cmd[s.cmd.index("--mcp-config") + 1])
    assert config_path == s.out_dir / "mcp.json"
    server = json.loads(config_path.read_text(encoding="utf-8"))["mcpServers"]["stardew"]
    assert server["type"] == "http"
    assert server["url"] == run_days.API + "/mcp"


def test_system_prompt_is_passed_as_a_file(play):
    s = play("claude_stream_day.jsonl", day=7, system_prompt="RULES FOR DAY SEVEN")
    system_file = Path(s.cmd[s.cmd.index("--system-prompt-file") + 1])
    assert system_file == s.out_dir / "system_day_007.md"
    assert system_file.read_text(encoding="utf-8") == "RULES FOR DAY SEVEN"


def test_runs_in_its_own_workdir_with_long_tool_timeouts(play):
    s = play("claude_stream_day.jsonl")
    assert Path(s.popen_kwargs["cwd"]) == s.out_dir / "workdir"
    assert (s.out_dir / "workdir").is_dir()
    assert s.popen_kwargs["env"]["MCP_TOOL_TIMEOUT"] == "600000"
    assert s.popen_kwargs["stderr"] == subprocess.STDOUT  # stderr lines land in the transcript too
    assert s.popen_kwargs["shell"] is False


# ---------------------------------------------------------------------------------- recent_diaries

def write_diaries(folder: Path, days: dict[int, str]) -> Path:
    folder.mkdir(parents=True, exist_ok=True)
    for day, text in days.items():
        (folder / f"diary_{day:03}.md").write_text(text, encoding="utf-8")
    return folder


FIRST_DAY = "(This is your first day. No diaries yet.)"


def test_first_day_has_no_memory(tmp_path):
    assert run_days.recent_diaries(tmp_path, 1) == FIRST_DAY


def test_day_without_any_diary_file_has_no_memory(tmp_path):
    assert run_days.recent_diaries(tmp_path, 5) == FIRST_DAY


def test_memory_is_the_previous_day_not_the_current_one():
    memory = run_days.recent_diaries(GAME_LOGS, 2)
    assert memory.startswith("# Day 1 ")
    assert "# Day 2" not in memory


def test_sample_diaries_come_back_oldest_first_and_separated():
    memory = run_days.recent_diaries(GAME_LOGS, 3)
    first, second = memory.split("\n\n# Day 2")
    assert first.startswith("# Day 1 ")
    assert "ไถดินได้สองช่อง" in second


def test_only_the_last_three_days_are_kept(tmp_path):
    write_diaries(tmp_path, {d: f"# Day {d}\n\nnote {d}" for d in range(1, 10)})
    memory = run_days.recent_diaries(tmp_path, 10)
    assert [f"# Day {d}" in memory for d in range(1, 10)] == [False] * 6 + [True] * 3


def test_window_size_is_adjustable(tmp_path):
    write_diaries(tmp_path, {d: f"# Day {d}" for d in range(1, 6)})
    assert run_days.recent_diaries(tmp_path, 6, n=1) == "# Day 5"
    assert run_days.recent_diaries(tmp_path, 6, n=2) == "# Day 4\n\n# Day 5"


def test_days_without_a_diary_are_skipped(tmp_path):
    write_diaries(tmp_path, {1: "# Day 1", 3: "# Day 3"})
    assert run_days.recent_diaries(tmp_path, 4) == "# Day 1\n\n# Day 3"


def test_diary_whitespace_is_trimmed(tmp_path):
    write_diaries(tmp_path, {1: "\n\n# Day 1\n\ntext\n\n\n"})
    assert run_days.recent_diaries(tmp_path, 2) == "# Day 1\n\ntext"


def test_diaries_of_other_players_or_formats_are_not_read(tmp_path):
    write_diaries(tmp_path, {1: "# Day 1"})
    (tmp_path / "diary_p2_001.md").write_text("# someone else", encoding="utf-8")
    (tmp_path / "diary_002.txt").write_text("not markdown", encoding="utf-8")
    assert run_days.recent_diaries(tmp_path, 3) == "# Day 1"


# ----------------------------------------------------------------------------------- last_actions

def write_log(folder: Path, day: int, entries: list) -> None:
    lines = [e if isinstance(e, str) else json.dumps(e, ensure_ascii=False) for e in entries]
    (folder / f"day_{day:03}.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8")


def test_no_log_file_means_no_recap(tmp_path):
    assert run_days.last_actions(tmp_path, 4) == ""


def test_recap_keeps_thoughts_actions_and_results_only():
    lines = run_days.last_actions(GAME_LOGS, 2).splitlines()
    assert len(lines) == 6  # 8 entries, minus the system line and the diary
    assert [line.split()[1] for line in lines] == ["think", "action", "result", "action", "result", "think"]
    assert not any("Auto-named" in line or "วันนี้ไถดินได้สองช่อง" in line for line in lines)


def test_recap_line_format():
    lines = run_days.last_actions(GAME_LOGS, 2).splitlines()
    assert lines[1] == "06:10 action walk_to: ไปที่แปลงผัก"
    assert lines[2] == "06:40 result walk_to: Arrived at (64,15)."
    assert lines[0].startswith("06:10 think")
    assert lines[0].endswith("วันนี้จะถางหญ้าแล้วปลูกพาร์สนิป")


def test_recap_flattens_multiline_text():
    lines = run_days.last_actions(GAME_LOGS, 2).splitlines()
    assert lines[4] == "07:10 result use_tool: (64,16) tilled (65,16) tilled"


def test_recap_cuts_long_text():
    last = run_days.last_actions(GAME_LOGS, 2).splitlines()[-1]
    prefix = "07:10 think"
    assert last.startswith(prefix)
    text = last.split(": ", 1)[1]
    assert len(text) == 200


def test_recap_keeps_only_the_latest_n(tmp_path):
    write_log(tmp_path, 3, [{"kind": "action", "tool": "wait", "gameTime": f"{h:02}:00", "text": f"step {h}"}
                            for h in range(6, 16)])
    lines = run_days.last_actions(tmp_path, 3, n=4).splitlines()
    assert [line.split(": ")[1] for line in lines] == ["step 12", "step 13", "step 14", "step 15"]


def test_recap_skips_lines_that_are_not_json(tmp_path):
    write_log(tmp_path, 3, [
        {"kind": "action", "tool": "look", "gameTime": "06:00", "text": "first"},
        "{this line was cut off by a crash",
        {"kind": "result", "tool": "look", "gameTime": "06:01", "text": "second"},
    ])
    assert run_days.last_actions(tmp_path, 3).splitlines() == ["06:00 action look: first", "06:01 result look: second"]


def test_recap_handles_entries_without_tool_or_text(tmp_path):
    write_log(tmp_path, 3, [{"kind": "think", "gameTime": "06:00"}])
    assert run_days.last_actions(tmp_path, 3) == "06:00 think : "


def test_recap_reads_the_requested_day_only(tmp_path):
    write_log(tmp_path, 2, [{"kind": "action", "tool": "look", "gameTime": "06:00", "text": "yesterday"}])
    write_log(tmp_path, 3, [{"kind": "action", "tool": "look", "gameTime": "06:00", "text": "today"}])
    assert run_days.last_actions(tmp_path, 3) == "06:00 action look: today"
