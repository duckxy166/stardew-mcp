"""Run Claude Haiku through Stardew Valley, one Claude Code session per in-game day.

Usage (from a normal terminal where `claude` is logged in, with the game running and a save loaded):

    py runner/run_days.py --days 1
    py runner/run_days.py --until-day 100 --goal "..."

Each day:
  * builds the system prompt (rules + goal + the AI's lessons, plan and notes + recent diaries; see memory.py),
  * runs `claude -p` with only the in-game MCP server (http://localhost:8770/mcp),
  * saves Claude's full stream-json transcript next to the game logs (proof of a real run),
  * continues the same day in a new session if Haiku stopped before sleeping,
  * when the sessions of a day are used up, releases the world freeze (POST /api/release_freeze) so the clock runs
    to 2:00 AM and the farmer passes out; the next morning's first prompt says what happened and asks for the
    missed day's journal first (pass_out.py, #22),
  * backs up the save folder after the day ends.

After the last day of the batch, one short text-only session shortens lessons.md and notes.md (the old lists stay
as lessons_before_batchN.md) and writes a digest for each finished week. Use --skip-consolidation to skip it.
The mod's LogDir must be the same folder as <out>/logs, where this script reads diaries, lessons and plans.

Before the first day it runs runner/smoke.py (LLM-free tool checks on a copy of the save) and stops if
any case fails. That needs "EnableDebugApi": true in the mod's config.json; pass --skip-smoke to skip.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request
from datetime import datetime
from pathlib import Path

import briefing
import memory
import pass_out

HERE = Path(__file__).resolve().parent
API = "http://localhost:8770"
DEFAULT_OUT = HERE.parent / "runs"
DEFAULT_GOAL = (
    "Build the best farm you can in 100 days. You choose the strategy: what to grow, how to earn money, "
    "which villagers to befriend and what to upgrade."
)


def api_get(path: str):
    with urllib.request.urlopen(API + path, timeout=10) as r:
        return json.loads(r.read().decode("utf-8"))


def api_post(path: str, body: dict):
    req = urllib.request.Request(API + path, data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.loads(r.read().decode("utf-8"))


def game_state() -> dict:
    try:
        return api_get("/api/state")
    except Exception:
        return {"ready": False}


def wait_ready(timeout: int = 600) -> dict:
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = game_state()
        if s.get("ready"):
            return s
        time.sleep(3)
    raise SystemExit("Game is not ready (start Stardew via SMAPI and load the save).")


def recent_diaries(log_dir: Path, day: int, n: int = 3) -> str:
    parts = []
    for d in range(max(1, day - n), day):
        f = log_dir / f"diary_{d:03}.md"
        if f.exists():
            parts.append(f.read_text(encoding="utf-8").strip())
    return "\n\n".join(parts) if parts else "(This is your first day. No diaries yet.)"


def last_actions(log_dir: Path, day: int, n: int = 25) -> str:
    f = log_dir / f"day_{day:03}.jsonl"
    if not f.exists():
        return ""
    lines = f.read_text(encoding="utf-8").splitlines()[-n * 3:]
    out = []
    for line in lines:
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        if e.get("kind") in ("think", "action", "result"):
            text = (e.get("text") or "").replace("\n", " ")[:200]
            out.append(f"{e.get('gameTime')} {e['kind']} {e.get('tool') or ''}: {text}")
    return "\n".join(out[-n:])


def save_folder() -> Path | None:
    saves = Path(os.environ["APPDATA"]) / "StardewValley" / "Saves"
    try:
        info = api_get("/api/state")
    except Exception:
        info = {}
    cands = [p for p in saves.iterdir() if p.is_dir()]
    if not cands:
        return None
    return max(cands, key=lambda p: p.stat().st_mtime)  # the save just written


def run_session(args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int) -> int:
    mcp_cfg = out_dir / "mcp.json"
    mcp_cfg.write_text(json.dumps({"mcpServers": {"stardew": {"type": "http", "url": f"{API}/mcp"}}}), encoding="utf-8")
    sys_file = out_dir / f"system_day_{day:03}.md"
    sys_file.write_text(system_prompt, encoding="utf-8")

    transcript = out_dir / "logs" / f"claude_day_{day:03}_part{part}.jsonl"
    workdir = out_dir / "workdir"
    workdir.mkdir(exist_ok=True)

    cmd = [
        "claude", "-p", prompt,
        "--model", args.model,
        "--system-prompt-file", str(sys_file),
        "--mcp-config", str(mcp_cfg), "--strict-mcp-config",
        "--tools", "",
        "--allowedTools", "mcp__stardew__*",
        "--setting-sources", "project",
        "--output-format", "stream-json", "--verbose",
        "--max-turns", str(args.max_turns),
    ]
    env = dict(os.environ)
    env["MCP_TOOL_TIMEOUT"] = "600000"
    env["MCP_TIMEOUT"] = "60000"

    print(f"[{datetime.now():%H:%M:%S}] day {day} part {part}: starting {args.model}")
    session_id = ""
    tool_calls = 0
    with transcript.open("w", encoding="utf-8") as tf:
        proc = subprocess.Popen(cmd, cwd=workdir, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, encoding="utf-8", errors="replace", shell=False)
        assert proc.stdout
        for line in proc.stdout:
            tf.write(line)
            tf.flush()
            try:
                ev = json.loads(line)
            except json.JSONDecodeError:
                print("  " + line.rstrip())
                continue
            if ev.get("type") == "system" and ev.get("subtype") == "init":
                session_id = ev.get("session_id", "")
                model = ev.get("model", args.model)
                print(f"  session {session_id} model {model}")
                try:
                    api_post("/api/meta", {"model": "Claude Haiku 5.5", "modelId": model, "session": session_id,
                                           "run": f"day {day} · part {part}",
                                           "event": f"Claude session {session_id} started ({model}), day {day} part {part}"})
                except Exception:
                    pass
            elif ev.get("type") == "assistant":
                for c in ev.get("message", {}).get("content", []):
                    if c.get("type") == "tool_use":
                        tool_calls += 1
                        name = c["name"].replace("mcp__stardew__", "")
                        arg = c.get("input", {})
                        short = arg.get("text") or arg.get("reason") or ""
                        print(f"  #{tool_calls} {name} {short[:90]}")
            elif ev.get("type") == "result":
                print(f"  result: {ev.get('subtype')} turns={ev.get('num_turns')} cost=${ev.get('total_cost_usd', 0):.3f}")
                if ev.get("is_error") and tool_calls == 0:
                    raise SystemExit(f"Claude error, stopping: {ev.get('result')}")
        proc.wait()
    return tool_calls


def claude_text_session(args, out_dir: Path, label: str) -> memory.Session:
    """A memory.Session backed by `claude -p` with no tools and no MCP servers: returns the reply text.

    The material goes into the system prompt file, so a long lessons list never hits the command-line length limit.
    """
    def run(system_prompt: str, prompt: str) -> str:
        sys_file = out_dir / f"system_{label}.md"
        sys_file.write_text(system_prompt, encoding="utf-8")
        mcp_cfg = out_dir / "mcp_none.json"
        mcp_cfg.write_text(json.dumps({"mcpServers": {}}), encoding="utf-8")
        transcript = out_dir / "logs" / f"claude_{label}.jsonl"
        workdir = out_dir / "workdir"
        workdir.mkdir(exist_ok=True)
        cmd = [
            "claude", "-p", prompt,
            "--model", args.model,
            "--system-prompt-file", str(sys_file),
            "--mcp-config", str(mcp_cfg), "--strict-mcp-config",
            "--tools", "",
            "--setting-sources", "project",
            "--output-format", "stream-json", "--verbose",
            "--max-turns", "3",
        ]
        reply, failed = "", ""
        with transcript.open("a", encoding="utf-8") as tf:
            proc = subprocess.Popen(cmd, cwd=workdir, env=dict(os.environ), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", shell=False)
            assert proc.stdout
            for line in proc.stdout:
                tf.write(line)
                try:
                    ev = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if ev.get("type") == "result":
                    reply = ev.get("result") or ""
                    if ev.get("is_error"):
                        failed = reply or "claude reported an error"
            proc.wait()
        if failed or not reply.strip():
            raise RuntimeError(failed or "empty reply")
        return reply
    return run


def batch_end_memory(args, out_dir: Path, log_dir: Path, current_day: int):
    """Shorter lessons and notes, a digest per finished week (memory.consolidate_batch), printed one line each."""
    print(f"[{datetime.now():%H:%M:%S}] batch finished: consolidating lessons, notes and weekly digests")
    session = claude_text_session(args, out_dir, f"consolidate_day_{current_day:03}")
    for result in memory.consolidate_batch(log_dir, current_day, session):
        print("  " + result.describe())


def backup_save(out_dir: Path, day: int):
    src = save_folder()
    if not src:
        return
    dst = out_dir / "saves" / f"day_{day:03}_{src.name}"
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(src, dst)
    print(f"  save backed up → {dst}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--days", type=int, default=1, help="How many in-game days to play in this run")
    ap.add_argument("--until-day", type=int, default=0, help="Stop after this farm day number")
    ap.add_argument("--model", default="claude-haiku-5-5")
    ap.add_argument("--goal", default=DEFAULT_GOAL)
    ap.add_argument("--max-turns", type=int, default=600)
    ap.add_argument("--max-parts", type=int, default=4, help="Sessions per day if Haiku stops before sleeping")
    ap.add_argument("--pass-out-timeout", type=int, default=1800,
                    help="Seconds to let game time run when a day's sessions are used up, waiting for the farmer to pass out")
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    ap.add_argument("--skip-smoke", action="store_true", help="Do not run runner/smoke.py before the batch")
    ap.add_argument("--skip-consolidation", action="store_true", help="Do not shorten lessons.md and notes.md after the batch")
    args = ap.parse_args()

    out_dir = Path(args.out)
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    template = (HERE / "system_prompt.md").read_text(encoding="utf-8")

    if not args.skip_smoke:
        wait_ready()
        print("Running smoke tests before the batch (use --skip-smoke to skip)...")
        if subprocess.run([sys.executable, str(HERE / "smoke.py")]).returncode != 0:
            raise SystemExit("Smoke tests failed, not starting the batch. Fix the failing case above, or pass --skip-smoke.")

    days_played = 0
    stopped_early = False
    while True:
        state = wait_ready()
        # After a pass-out the mod writes its record once the morning has settled; wait for that so the prompt can use it.
        state = pass_out.wait_settled(game_state)
        if not state.get("ready"):
            state = wait_ready()
        day = state["day"]
        if args.until_day and day > args.until_day:
            break
        if not args.until_day and days_played >= args.days:
            break

        system_prompt = memory.inject(template, log_dir, args.goal, recent_diaries(log_dir, day))
        for part in range(1, args.max_parts + 1):
            if part == 1:
                # The morning briefing replaces "start with status and look" (falls back to it if the mod has none).
                prompt = briefing.morning_prompt(day, briefing.build(api_get, day, "p1", out_dir=out_dir))
                prompt = memory.with_reminder(prompt, state, log_dir)
                # The previous day ended by passing out: say what happened and ask for that day's journal first.
                prompt = pass_out.with_morning_notice(prompt, state, lambda d: last_actions(log_dir, d))
            else:
                prompt = (f"เล่นวันที่ {day} ต่อจากเดิม (session ก่อนหน้าหยุดก่อนเข้านอน) นี่คือสิ่งที่เพิ่งทำไป:\n"
                          f"{last_actions(log_dir, day)}\n\n{memory.continuation_context(log_dir, day)}\n\n"
                          "เช็ก status แล้วเล่นต่อจนถึงเวลานอน (เขียน journal ถ้ายังไม่ได้เขียน แล้วเข้านอนด้วย go_to_bed)")
                prompt += pass_out.pending_reminder(game_state())
            run_session(args, out_dir, day, prompt, system_prompt, part)
            time.sleep(3)
            s = wait_ready()
            if s["day"] > day:
                break
            print(f"  day {day} not finished after part {part}; continuing")

        s = wait_ready()
        if s["day"] <= day:
            # Every session of the day is used and the farmer is still up. The mod keeps the world frozen while no
            # action runs, so release it and let the clock reach 2:00 AM: the farmer passes out and a new day starts.
            s = pass_out.let_time_run_out(day, game_state, api_post, time.sleep, timeout=args.pass_out_timeout)
        if s.get("ready") and s["day"] > day:
            backup_save(out_dir, day)
            days_played += 1
        else:
            print(f"Day {day} still not finished after {args.max_parts} sessions and {args.pass_out_timeout}s of released "
                  "time — stopping for a human to check.")
            stopped_early = True
            break

    if days_played and not stopped_early and not args.skip_consolidation:
        batch_end_memory(args, out_dir, log_dir, wait_ready()["day"])


if __name__ == "__main__":
    main()
