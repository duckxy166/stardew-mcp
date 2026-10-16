# Test fixtures for the usage-limit detector

**Every file here is SYNTHETIC.** None of them is a capture from a real `claude` run: they were written by
hand to imitate the message shapes the detector is meant to tolerate. The real wording of Claude Code's
usage-limit message has to be confirmed on a machine that actually hits the limit (see below).

| File | Imitates |
|---|---|
| `limit_pipe_epoch.jsonl` | Older machine-readable form: `result` event, `is_error: true`, text `Claude AI usage limit reached\|<epoch seconds>` (epoch 1792180800 = 2026-10-17 03:00 +07:00). Session ended with 0 tool calls. |
| `limit_resets_clock_midsession.jsonl` | Newer human-readable form: an `assistant` event with an `error` field, then a `result` event with `is_error: true` and `You've hit your limit · resets 3am (Asia/Bangkok)`. Limit hit after 3 tool calls. |
| `limit_rate_limit_event.jsonl` | A `rate_limit_event` with `rate_limit_info.status: "rejected"` and `resetsAt`, then `5-hour limit reached ∙ resets 3am`. The `rate_limit_event` shape is a guess from memory of newer CLI versions. |
| `limit_unparseable.jsonl` | A limit message with no reset time at all (the runner must fall back to retrying every 30 minutes). |
| `limit_plain_line.txt` | The limit printed as a plain, non-JSON line on stdout/stderr (older CLIs). |
| `error_not_limit.jsonl` | An unrelated `is_error` result (`API Error: 500`). Must NOT be treated as a limit: the runner keeps its old "stop with a message" behaviour. |
| `ok_day.jsonl` | A normal successful session. |
| `not_a_limit_mentions.jsonl` | A successful result whose text contains the words "limit reached" and "resets" in a farming sense. Must NOT match. |

## Confirming the real format

The runner already saves every session's stream as `runs/logs/claude_day_<DDD>_part<P>.jsonl` (a retry after a
wait is saved as `..._part<P>_retry<N>.jsonl`). After the first real usage-limit stop, check what the detector
made of it:

    py runner/usage_limit.py runs/logs/claude_day_001_part1.jsonl

If it says "no usage limit detected" for a transcript that ended on a limit message, or the parsed reset time
is wrong, copy the last `result` line (and any `rate_limit_event` / `assistant` line before it) into a new
`real_*.jsonl` fixture here and extend `runner/usage_limit.py` and `runner/tests/test_usage_limit.py`.
You can also provoke the message on purpose: `claude -p hi --output-format stream-json --verbose` while the
account is limited.
