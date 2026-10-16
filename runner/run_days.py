"""Run Claude Haiku through Stardew Valley, one Claude Code session per in-game day.

Usage (from a normal terminal where `claude` is logged in, with the game running and a save loaded):

    py runner/run_days.py --days 1
    py runner/run_days.py --until-day 100 --goal "..."

Each day:
  * builds the system prompt (rules + goal + recent diaries),
  * runs `claude -p` with only the in-game MCP server (http://localhost:8770/mcp),
  * saves Claude's full stream-json transcript next to the game logs (proof of a real run),
  * continues the same day in a new session if Haiku stopped before sleeping,
  * on a subscription usage limit waits for the reset and retries the same part (see usage_limit.py),
  * backs up the save folder after the day ends.

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

import usage_limit

HERE = Path(__file__).resolve().parent
API = "http://localhost:8770"
CLAUDE_CMD = ["claude"]  # tests swap in a fake
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


def continue_prompt(day: int, log_dir: Path) -> str:
    return (f"เล่นวันที่ {day} ต่อจากเดิม (session ก่อนหน้าหยุดก่อนเข้านอน) นี่คือสิ่งที่เพิ่งทำไป:\n"
            f"{last_actions(log_dir, day)}\n\nเช็ก status แล้วเล่นต่อจนเข้านอนด้วย go_to_bed")


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


def run_session(args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1) -> int:
    mcp_cfg = out_dir / "mcp.json"
    mcp_cfg.write_text(json.dumps({"mcpServers": {"stardew": {"type": "http", "url": f"{API}/mcp"}}}), encoding="utf-8")
    sys_file = out_dir / f"system_day_{day:03}.md"
    sys_file.write_text(system_prompt, encoding="utf-8")

    # a retry of the same part after a usage-limit wait must not overwrite the first attempt's transcript
    transcript = out_dir / "logs" / (f"claude_day_{day:03}_part{part}" + (f"_retry{attempt - 1}" if attempt > 1 else "") + ".jsonl")
    workdir = out_dir / "workdir"
    workdir.mkdir(exist_ok=True)

    cmd = [
        *CLAUDE_CMD, "-p", prompt,
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
    limit = usage_limit.LimitWatcher()
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
                limit.feed_line(line)
                continue
            limit.feed(ev)
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
                if ev.get("is_error") and tool_calls == 0 and not limit.info:
                    raise SystemExit(f"Claude error, stopping: {ev.get('result')}")
        proc.wait()
    if limit.info:  # subscription usage limit: the caller waits for the reset and retries this part
        raise usage_limit.UsageLimitHit(limit.info, tool_calls)
    return tool_calls


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
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    ap.add_argument("--skip-smoke", action="store_true", help="Do not run runner/smoke.py before the batch")
    usage_limit.add_args(ap)
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

    waiter = usage_limit.LimitWaiter.from_args(args, post=api_post)
    days_played = 0
    while True:
        state = wait_ready()
        day = state["day"]
        if args.until_day and day > args.until_day:
            break
        if not args.until_day and days_played >= args.days:
            break

        system_prompt = template.replace("{GOAL}", args.goal).replace("{MEMORY}", recent_diaries(log_dir, day))
        for part in range(1, args.max_parts + 1):
            if part == 1:
                prompt = (f"วันนี้คือวันที่ {day} ของชาเลนจ์ 100 วัน เริ่มจาก status และ look แล้ว think แผนของวันเป็นภาษาไทย "
                          "จากนั้นเล่นทั้งวันจนเข้านอนด้วย go_to_bed")
            else:
                prompt = continue_prompt(day, log_dir)
            # a usage-limit stop waits for the reset and retries this same part (it does not count toward --max-parts)
            usage_limit.run_part(waiter, lambda p, n: run_session(args, out_dir, day, p, system_prompt, part, n),
                                 prompt, lambda: continue_prompt(day, log_dir), day, part,
                                 day_done=lambda: wait_ready()["day"] > day)
            time.sleep(3)
            s = wait_ready()
            if s["day"] > day:
                break
            print(f"  day {day} not finished after part {part}; continuing")

        s = wait_ready()
        if s["day"] > day:
            backup_save(out_dir, day)
            days_played += 1
        else:
            print(f"Day {day} still not finished after {args.max_parts} sessions — stopping for a human to check.")
            break


if __name__ == "__main__":
    main()
