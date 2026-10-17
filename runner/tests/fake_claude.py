"""Fake `claude` command for the runner tests. Imitates only the stream-json lines run_days.py reads.

Not a real capture of anything: the usage-limit messages here are SYNTHETIC (see fixtures/README.md).

Behaviour comes from the environment, so one script serves every test:
  FAKE_CLAUDE_DIR       state dir: `calls` counter and `prompts.jsonl` (one JSON string per call)
  FAKE_CLAUDE_SCENARIO  JSON list of scenario names, one per call (the last one repeats)
  FAKE_CLAUDE_API       host:port of the fake game API (POST /test/next_day = the AI went to bed)
  FAKE_CLAUDE_DAYLOG    path of the day log the real mod would write (for the "continue" prompt)

Scenarios: ok | ok_nosleep | error | limit_epoch[:<seconds ahead>] | limit_epoch_mid | limit_relative |
           limit_unparseable | limit_plain | limit_after_sleep
"""

import http.client
import json
import os
import sys
import time

SID = "synthetic-fake-claude"


def emit(obj):
    print(json.dumps(obj, ensure_ascii=False), flush=True)


def init():
    emit({"type": "system", "subtype": "init", "session_id": SID, "model": "claude-haiku-5-5"})


def tool(n, name):
    emit({"type": "assistant", "message": {"role": "assistant", "content": [
        {"type": "tool_use", "id": f"toolu_synthetic_{n}", "name": f"mcp__stardew__{name}", "input": {"reason": "synthetic"}}]}})


def result(text, is_error, turns=1):
    emit({"type": "result", "subtype": "success", "is_error": is_error, "result": text, "num_turns": turns,
          "total_cost_usd": 0, "session_id": SID})


def day_log(*actions):
    path = os.environ.get("FAKE_CLAUDE_DAYLOG")
    if not path:
        return
    with open(path, "a", encoding="utf-8") as f:
        for i, tool_name in enumerate(actions):
            f.write(json.dumps({"kind": "action", "tool": tool_name, "gameTime": f"06{i:02}", "text": f"did {tool_name}"}) + "\n")


def go_to_bed():
    host, _, port = os.environ["FAKE_CLAUDE_API"].partition(":")
    conn = http.client.HTTPConnection(host, int(port), timeout=10)
    conn.request("POST", "/test/next_day", body="{}")
    conn.getresponse().read()
    conn.close()


def main():
    state = os.environ["FAKE_CLAUDE_DIR"]
    prompt = sys.argv[sys.argv.index("-p") + 1]
    calls_file = os.path.join(state, "calls")
    n = int(open(calls_file).read()) if os.path.exists(calls_file) else 0
    open(calls_file, "w").write(str(n + 1))
    with open(os.path.join(state, "prompts.jsonl"), "a", encoding="utf-8") as f:
        f.write(json.dumps(prompt, ensure_ascii=False) + "\n")

    scenarios = json.loads(os.environ["FAKE_CLAUDE_SCENARIO"])
    name = scenarios[min(n, len(scenarios) - 1)]
    kind, _, arg = name.partition(":")

    init()
    if kind == "ok":
        tool(1, "status"); tool(2, "walk_to"); day_log("status", "walk_to")
        tool(3, "go_to_bed"); day_log("go_to_bed")
        go_to_bed()
        result("Slept. Good night.", False, 3)
    elif kind == "ok_nosleep":
        tool(1, "status"); tool(2, "walk_to"); day_log("status", "walk_to")
        result("Stopped before bed.", False, 2)
    elif kind == "error":
        result("API Error: 500 Internal server error", True, 0)
        sys.exit(1)
    elif kind == "limit_epoch":
        ahead = int(arg or 7200)
        result(f"Claude AI usage limit reached|{int(time.time()) + ahead}", True, 0)
        sys.exit(1)
    elif kind == "limit_epoch_mid":
        tool(1, "status"); tool(2, "walk_to"); day_log("status", "walk_to")
        result(f"Claude AI usage limit reached|{int(time.time()) + 7200}", True, 2)
        sys.exit(1)
    elif kind == "limit_relative":
        result("You've hit your limit · resets in 2 hours", True, 0)
        sys.exit(1)
    elif kind == "limit_unparseable":
        result("Claude usage limit reached. Please try again later.", True, 0)
        sys.exit(1)
    elif kind == "limit_plain":
        print("Claude usage limit reached. Please try again later.", flush=True)
        sys.exit(1)
    elif kind == "limit_after_sleep":
        tool(1, "go_to_bed"); day_log("go_to_bed")
        go_to_bed()
        result("You've hit your limit · resets in 2 hours", True, 1)
        sys.exit(1)
    else:
        raise SystemExit(f"unknown fake scenario {name}")


if __name__ == "__main__":
    main()
