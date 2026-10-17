# Stardew MCP

A [SMAPI](https://smapi.io) mod that lets an AI agent play Stardew Valley. The mod runs an
[MCP](https://modelcontextprotocol.io) server **inside the game**, so Claude (or any MCP client)
can look around, walk, farm, talk to villagers, shop and go to bed on its own.

- **Game time freezes while the AI thinks**, so a slow model loses no in-game time.
- **Stream-ready overlay** for OBS showing the AI's thoughts and actions live.
- **Replay page** and per-day JSONL logs + diaries for every run.
- **Runner scripts** to play many days unattended and record only the gameplay.

> ภาษาไทยอยู่ด้านล่าง ↓

## Support the project

If you enjoy this project, you can support development here:

- ☕ **Ko-fi:** _coming soon_ <!-- TODO(Duckxy): add link -->
- 💖 **GitHub Sponsors:** _coming soon_ <!-- TODO(Duckxy): add link -->

## Requirements

- Stardew Valley 1.6+
- [SMAPI](https://smapi.io) 4.0+
- An MCP client, e.g. [Claude Code](https://claude.com/claude-code)

## Install

1. Install SMAPI and run the game once through it.
2. Download the latest `StardewMCP` zip from the Releases page.
3. Unzip it into `Stardew Valley/Mods/`.
4. Start the game through SMAPI. The console should show
   `Agent MCP at http://localhost:8770/mcp`.

## Connect an AI

With the game running and a save loaded:

```bash
claude mcp add --transport http stardew http://localhost:8770/mcp
```

Then ask Claude something like *"Water my crops, then go talk to Abigail."*

Any client that speaks MCP over streamable HTTP can use the same URL.

### Tools

| Tool | What it does |
|---|---|
| `status`, `look`, `inventory` | Read the player, surroundings and items. `look` shows a 17x17 window (radius 8) by default; `kinds` (e.g. `["crops","debris"]`) limits the object list and `diff=true` lists only the tiles that changed since the last look |
| `think` | Post a thought (shown on the overlay, costs no game time) |
| `walk_to`, `travel_to` | Pathfind to a tile, or to another location (a shop owner's or villager's name also works; a closed door says when it opens) |
| `farm_summary` | Whole-map farm report: empty tilled soil, crops by type with days to harvest, unwatered, ready and dead crops, crops that won't mature this season, forage, debris, watering can and nearest water. Costs no game time |
| `water_all`, `harvest_all` | Water every dry crop (refills at the nearest water, stops on low energy) and collect every ready crop and forage item. Both stop with a reason (low energy, menu, late hour, full inventory) and say what is left |
| `clear_area`, `till_area` | Clear a rectangle (up to 100 tiles) with the right tool, and till it with the Hoe; large stumps and boulders are skipped and reported. `plant` also takes `area: [x, y, w, h]` (or no tiles at all = every empty tilled tile on the map) |
| `use_tool`, `plant`, `harvest` | Farm work on a target tile |
| `fight` | Fight the monsters near you: equips the best weapon, walks to the nearest one and swings for a few seconds; stops when all clear, health is at 30% of max (at least 15) or a menu opens; `eat_below` (HP %) eats the best healing food on the way |
| `leave_mine` | Get out of the Mines from any floor: climbs the up ladders, or warps to the Mine entrance when health is low, it is 11 PM or the ladder cannot be reached. In the mines the footer shows HP, and any other long action is cut short with `Interrupted:` when a monster within 3 tiles hurts you |
| `interact`, `respond`, `close_menu` | Objects, dialogue and menus; `interact` can hold an item first (load a machine, use a totem); `close_menu` accepts a letter's quest and takes treasure or gift items that fit |
| `villagers` | Everyone: where they are, hearts, talked/gifts today and this week, birthdays; with a name also the gift tastes found out so far |
| `talk_to` | Find a villager (travels across maps unless `go=false`), talk, optionally give a gift; reports points before/after and their reaction |
| `bouquet`, `mermaid_pendant` | Ask a villager to date you (8 hearts), later propose (10 hearts, upgraded house); `villagers(name)` shows what is still missing |
| `propose`, `answer_proposal` | Propose marriage to another player (split-screen); the other player answers the Yes/No box with `answer_proposal` |
| `name` | Name the new pet, farm animal, horse or child the open naming box asks for (a box nobody answers gets a random name at bedtime) |
| `menu` | Read any open menu, then use it: take/put (chests), choose (mine elevator), amount (send money), type (names), ok/cancel, or click a listed button |
| `craft`, `place`, `chest` | Craft from the recipes you know (`craft()` lists them), put items, machines and chests on the ground, store or take items in a chest or the fridge in one call (by name and quality, several items, "everything") |
| `cook` | Cook the dishes you know in the farmhouse kitchen from your pack and the fridge (`cook()` lists them) |
| `quests`, `accept_quest` | Quest journal with story, giver, progress and reward; today's Help Wanted request and who took it; Special Orders; `share` posts yours to the plan board; `accept_quest` takes the Help Wanted request |
| `crack_geode`, `trash`, `watch_tv` | Clint cracks geodes (25g each); throw items away (valuables need `confirm`); watch a TV channel (weather, fortune, Queen of Sauce, tips) |
| `shop_list`, `buy`, `sell`, `ship`, `eat` | Shopping, selling to an open shop for gold at once, shipping bin (pays overnight), food |
| `equip` | Put on a ring, boots or hat from the inventory (the old one goes back), or attach bait/tackle to the fishing rod; `status` and `inventory` show what is worn and what is on the rod |
| `special_orders` | The Special Orders board in Town: `list` the posted orders, `accept` one by index, `active` shows the accepted ones with progress |
| `build`, `upgrade_house` | Robin the carpenter: `build(building=list)` shows costs, `build(building, x, y)` orders a Coop, Barn, Silo, Well or Shed on the Farm; `upgrade_house` buys the next house level. Paid from the caller's own money and items |
| `buy_animal`, `animals` | Marnie's animals: `buy_animal(type=list)` and `buy_animal(type, building?, name?)`; `animals` lists yours and does `pet`, `collect` (eggs, milk, wool) and `feed` (hay troughs). Status warns when animals are not pet today |
| `calendar` | Today and the next days: festivals, birthdays, the next festival, and the main shops' hours and closed days (no game time) |
| `festival` | On a festival day: `start` talks to Lewis and answers yes (waits for the other players), `leave` walks out; `talk_to` also reaches festival villagers and booths |
| `fish` | Walk to the best shore for the rod, cast `tries` times, play the BobberBar minigame and pick up treasure. Costs energy and game time per cast; one result line per cast. Needs a fishing rod (Willy, at the Beach) |
| `crops`, `item_info` | Game knowledge read from the game's own data, no game time: `crops` is a season planner (growth and regrow days, last day to plant, harvests that fit, seed and sell prices, gold per tile per day, marks the seeds you carry); `item_info` gives an object's sell prices by quality, what it restores, shops that sell it and recipes that use it |
| `wait`, `go_to_bed` | Let time pass, end the day (`go_to_bed` refuses until today's `journal` is written) |
| `journal` | The AI's memory across days, costs no game time: a diary for the viewers, lessons (appended), a plan with Season / Week / Today sections (replaced section by section) and optional facts. Written to `diary_NNN.md`, `lessons.md`, `plan.md`, `notes.md` in the log folder; `day` catches up a missed day |
| `say`, `whisper`, `emote`, `give` | Multi-player (split-screen): chat to everyone, tell one player privately, show an emote, hand an item to a nearby player. All cost no game time |
| `wear`, `wardrobe` | Put on a hat, shirt or pants from the inventory; see what you wear and carry (rings and boots are `equip`, #27) |
| `search_trash` | Dig through a trash can; a watching villager costs friendship and a `trash_dive` event with `seen_by` is logged |
| `mount`, `dismount` | Ride the horse (needs a Stable); also night events show up as an `Overnight:` line after `go_to_bed` |
| `plan_read`, `plan_edit` | Shared plan board with an edit history (author and time) |
| `bundles` | Community Center bundles room by room: what each needs, what is donated (and by whom), the reward, and which of your items fit. Costs no game time |
| `donate_bundle` | Donate an inventory item (or gold, for the Vault) to the first Community Center bundle that needs it, through the game's own bundle page |
| `joja` | The Joja route at the JojaMart: `projects` lists the five Community Development projects with price and state, `membership` buys the permanent 5,000g membership only with `confirm=true` (without it: a warning of what every player loses and gains), `buy_project` pays one project through the game's Joja form |
| `museum` | `list` the items the Museum still accepts, `donate` one (or all) to the next free display spot, collect `rewards` from Gunther |

### Tool results

Every tool reply keeps its plain text and adds a machine-readable part (MCP `structuredContent`). `isError` is unchanged: it is `true` only when the tool failed.

```json
{ "content": [{ "type": "text", "text": "[partial · low_energy] Stopped: energy too low (2). ..." }],
  "isError": false,
  "structuredContent": { "ok": true, "reason": "low_energy", "partial": true, "data": { "done": 12, "remaining": 28, "skipped": 0 } } }
```

- The text starts with a short tag when the tool did only part of its work (`[partial · reason]`) or failed for a known reason (`[no_path]`). Plain successes have no tag.
- `reason` is one of: `ok`, `partial`, `low_energy`, `no_path`, `not_here`, `no_item`, `inventory_full`, `menu_open`, `too_late`, `shop_closed`, `wrong_season`, `needs_upgrade`, `interrupted`, `timeout`, plus `no_money`, `invalid_args`, `not_found`, `not_ready`, `error` and `other` (a failure that has no specific code yet). `too_late` and `wrong_season` are reserved: the game gives no reliable signal, so no tool sets them yet.
- `use_tool`, `plant` and `harvest` are batch tools: they always report `data` (`done`, `remaining` = tiles not reached, `skipped` = tiles tried and failed) and set `partial` when `remaining` or `skipped` is above 0. `reason` then says why the batch stopped (or, failing that, why tiles were skipped).
- The common failures (`walk_to`, `travel_to`, `use_tool`, `plant`, `harvest`, `interact`, `talk_to`, `buy`, `ship`, `eat`, `go_to_bed`, `shop_list`, `think`) have codes; other tools still return text only (`reason` is `other` on failure).
- Logs: result lines of `day_NNN.jsonl` carry `reason` and `partial` (the text there stays untagged). Next to each day's log the mod writes `day_NNN_summary.json` with the failure reasons per tool and per player; `run_days.py` prints it after every day. The overlay marks partial results `◐` and shows the reason, the replay shows a reason badge.

### Other endpoints

| URL | Use |
|---|---|
| `http://localhost:8770/overlay` | OBS browser source (thoughts + actions) |
| `http://localhost:8770/replay` | Day-by-day replay of the logs |
| `http://localhost:8770/api/state` | Raw game state JSON (includes `saveFolder`, `paused`, `stopAfterDay`, `stopNow`). After a day that ended without `go_to_bed` (2:00 AM, health 0), `passOut` says why and `pendingJournalDay` is the day whose journal is still owed until `journal(day=N)` is written |
| `http://localhost:8770/api/control` | Run control: `POST {"action": "pause" \| "resume" \| "stop_after_day" \| "stop_now" \| "clear_stop"}`; `GET` reads the flags |
| `http://localhost:8770/api/memory` | Each AI seat's lessons, plan and notes (what `journal` wrote) |
| `http://localhost:8770/api/relationships` | Every AI farmer's friendships (hearts, birthdays, gift tastes found out); `runner/run_days.py` turns it into `relationships.md` |
| `POST http://localhost:8770/api/release_freeze` | Runner only: stop an idle seat from freezing the world (`{"seat": "p1"}`, or `"all"`; `"on": false` takes it back) so game time runs until the farmer passes out. Ends with the farm day |
| `http://localhost:8770/api/briefing?seat=p1` | Morning briefing of one AI seat: overnight results, shipping by item, mail, weather, farm (the runner puts it in the first prompt of a day) |
| `http://localhost:8770/api/stats` | Daily stats: the days that have a `stats_NNN.json`; `?n=3` returns day 3 of every AI seat |

## Configuration

`Mods/StardewMCP/config.json` is created on first launch:

| Setting | Default | Meaning |
|---|---|---|
| `HttpPort` | `8770` | Port for MCP, overlay and replay |
| `FreezeTimeWhileThinking` | `true` | Stop the clock while no action is running |
| `LogDir` | `""` | Log folder; empty = `logs/` in the mod folder |
| `AutoLoadSave` | `""` | Save folder to load automatically from the title screen |
| `FarmBatchMinEnergy` | `10` | `water_all`, `harvest_all`, `clear_area`, `till_area` and `plant` area mode stop when energy drops below this (0 = only stop at 0) |
| `EnableDebugApi` | `false` | Serve `/api/debug/*` test hooks for `runner/smoke.py`; leave off for normal play |
<<<<<<< HEAD
| `FishingAssist` | `"perfect"` | How the `fish` tool plays the fishing minigame: `"perfect"` keeps the fish inside the bar (the safe fallback); `"natural"` presses the mouse button like a player would, so it can lose fish |
| `ThoughtLanguage` | `"Thai"` | Language the AI is asked to use for on-screen text (`think`, action reasons, journal), e.g. `"English"` |
=======
| `ThoughtLanguage` | `"Thai"` | Language of the AI's on-screen text (`think`, action reasons, diary); the overlay, replay and `runner/` prompts follow it (see below) |
>>>>>>> origin/fix/issue-34
| `SplitScreen` | `false` | Three AIs + a camera screen in split-screen co-op (see below) |
| `FarmhandNames` | `["Gemini","GPT","Camera"]` | Farmhand names for P2, P3, P4; an existing farmhand with that name is reused |
| `ThinkTimeoutSeconds` | `300` | Split-screen: an AI silent for longer stops holding the world frozen (0 = never) |
| `PlayHeartEvents` | `false` | Let heart events (story cutscenes) play and stop at their questions for `respond`, instead of skipping them. Experimental, see `docs/coverage.md` |
| `RevealGiftTastes` | `false` | `villagers(name)` shows the game's real gift tastes. Off: a farmer only knows the tastes they found out by gifting |
| `RepeatHintAfter` | `3` | Loop guard: add a "try something different" hint when the identical call has failed this many times in a row (0 = off) |
| `RepeatBlockAfter` | `5` | Loop guard: refuse the Nth identical call in a row without running it when the N-1 before it failed (0 = off) |
| `NoProgressAfter` | `25` | Loop guard: add a "pick an action" hint after this many calls in a row with no game time, inventory or position change (0 = off) |
| `LockHostInput` | `true` | Split-screen: ignore the real keyboard/mouse on P1 once a save is loaded |
| `CleanFrames` | `true` | Split-screen: frozen frames look live (no Paused banner, HUD kept), no cursors, gamepad hint or join messages |
| `AutoZoom` | `true` | Split-screen: zoom each screen to about 20 tiles across |
| `SessionBreakTimes` | `[1200,1800]` | Planned session breaks: from these game times a tool result carries `Session break:` and the AI is asked to continue in a new session (see below) |
| `SessionBreakToolCalls` | `150` | Planned break after this many tool calls in one session (0 = off) |
| `SessionBreakResultChars` | `120000` | Planned break after this many characters of tool results in one session, about 30k tokens (0 = off) |
| `SessionBreaksForAll` | `false` | Planned breaks for every MCP client; by default only clients that connect to `/mcp?breaks=1` (what `run_days.py` does) |
| `PauseKey` | `"F8"` | Key (an XNA `Keys` name) that toggles pause / resume while the game window has focus; empty = off |

The SMAPI console command `mcp_freeze [on|off]` toggles time freezing at runtime.

### Split-screen (three AIs)

With `SplitScreen: true`, load a farm that has **3 free cabins** (a co-op farm with starting cabins).
The mod joins P2–P4 itself, no gamepads needed, in the game's 2×2 grid:

| Seat | Endpoint | Role |
|---|---|---|
| P1 (host) | `/mcp/p1` (or `/mcp`) | AI |
| P2 | `/mcp/p2` | AI |
| P3 | `/mcp/p3` | AI |
| P4 | none | camera / summary screen for the video |

The whole world (clock, farmers, NPCs) is frozen while any connected AI is thinking, so a slow model
never loses game time. `/api/state` lists every seat with its current action and thinking time.
The first time the game starts its local co-op server, Windows Firewall may ask once; allow it.

AI players notice each other. `look` and `status` list the other players in view with what each is *actually*
doing (running tool, its reason, game minutes so far) next to what it last said and wrote on the plan board.
An @name or @pN in `say`, a whisper, a gift, a player coming into view or entering your house, a theft from
your chest or plot, rain starting, or crows on the farm are added to your next tool result as `Events:` and
stop a running `wait`, `walk_to`, `travel_to` or farm action, so a model never sleeps through them.

`ThoughtLanguage` sets one language for everything viewers read. The MCP tools ask the AI to write in it, the
overlay and replay pick their labels from it (`thoughtLanguage` in `/api/feed`, and in `/api/state` once a save is
loaded; `?lang=English` on either page overrides it), and `runner/run_days.py` fills it into the system prompt and
the daily prompts (`--language` overrides it). `"Thai"` and `"English"` have full UI tables; any other language
(e.g. `"Japanese"`) gets thoughts in that language with an English UI. To add a table, copy the `en` block in the
`i18n` section of `studio/overlay.html` and `studio/replay.html`, and add texts to `runner/language.py`.

## Unattended runs and recording

`runner/` contains Python scripts (standard library only):

- `run_days.py` runs one Claude Code session per in-game day, keeps the AI's memory between days
  (its lessons, plan, facts and recent diaries go into the next day's system prompt) and backs up the
  save. Example: `py runner/run_days.py --days 3` (add `--language English` to override ThoughtLanguage). It reads those files from `<out>/logs`, so set the
  mod's `LogDir` to that folder. After the last day of a batch, one short text-only session shortens
  `lessons.md` and `notes.md` (the old lists stay as `lessons_before_batchN.md`) and writes a digest
  of each finished week (`--skip-consolidation` skips it), so run in batches of about a week.
  If the subscription's usage limit ends a session, the runner waits for the reset (or retries every
  30 minutes when the time can't be read), shows "รอ usage limit รีเซ็ต (ถึง 03:00)" on the overlay and
  resumes the same day; that part doesn't count toward `--max-parts`. Logic and options (`--limit-*`,
  `--max-limit-waits`) are in `runner/usage_limit.py`; check a saved transcript with
  `py runner/usage_limit.py runs/logs/claude_day_001_part1.jsonl`.
- Long days are split into parts. At the break times (and after too many calls or too much result text)
  the mod adds a `Session break:` line to a tool result, refuses further actions, and the AI writes its
  plan with `think` and ends its turn. `run_days.py` then starts the next part with that plan, the
  first `think` of the day and the last results. Planned breaks do not count toward `--max-parts`
  (`--max-breaks`, default 8, stops a loop; `--no-session-breaks` turns them off). The mod only counts
  characters, so the token limit is an estimate.
- Every part appends one row to `runs/usage.csv` (input, output and cache tokens, turns, tool calls,
  peak context). `py runner/usage_log.py summary` prints a table per day with the cache hit ratio,
  `check` compares the file with the saved transcripts, `rebuild` recreates it from them.
- `memory.py` is the memory logic behind that (placeholders, consolidation); its tests run without the
  game: `py -m pytest runner/tests`.
- `relationships.py` (used by `run_days.py`) writes `relationships.md` next to the diaries each day from `/api/relationships`
  and puts it in the system prompt as `{RELATIONSHIPS}`. It is generated from game data, never written by the AI.
- `smoke.py` runs LLM-free checks of the tools (farm loop, shop, shipping, sleep) on a copy of your
  save, then reloads your save. `run_days.py` runs it before every batch and stops if a case fails.
  It needs `"EnableDebugApi": true` in `config.json`; see `py runner/smoke.py --list`. The debug
  endpoints are not MCP tools, so the AI cannot see or call them.
- `obs_recorder.py` pauses the OBS recording while the game is frozen, so the video only contains
  gameplay. Needs OBS with obs-websocket enabled.
- `game_watchdog.py` is used by `run_days.py` to survive a game crash or hang (below).
- `stats.py` and `compare_runs.py` turn the daily stats into reports (see below).

### Watchdog: crash and hang recovery

Stardew only saves overnight, so after a crash the only way on is to start the game again and replay
the day from the morning. `run_days.py` watches the game while it plays and does that by itself:

| Signal | Meaning |
|---|---|
| process gone | the SMAPI process is not running (two checks in a row) |
| no answer | `/api/state` fails for more than 2 minutes, or its `heartbeat` stops counting for that long (the game loop is blocked while the HTTP thread still answers) |
| stalled | an action is running (`frozen: false`) but the game clock has not moved for 3 minutes. Thinking time never counts; cutscenes, festivals and the overnight menus get 15 minutes |

On a fault it kills the `claude` session and the game, moves the attempt's files aside, starts the game
again, waits until the save is loaded and plays the day again with a fresh part-1 session. The AI is not
told. After 2 replays of the same day the third crash stops the batch with a message (probably a mod bug).

```
py runner/run_days.py --days 3 --game-cmd "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\StardewModdingAPI.exe"
```

- `--game-cmd` (or env `STARDEW_GAME_CMD`) is the command that starts SMAPI; the working directory is its folder.
  Without it faults are still detected but the batch stops with a message instead of relaunching.
  `--no-watchdog` turns everything off.
- `--game-process` (default `StardewModdingAPI.exe` on Windows, `StardewModdingAPI` elsewhere) is the name used to
  find and kill the game. Windows uses `tasklist` / `taskkill /F /T`, other systems `pgrep` / `pkill`.
- **`AutoLoadSave`**: the mod reads `config.json` once at start, so before every relaunch the runner writes the run's
  save folder name into `AutoLoadSave` (all other keys stay as they are, the file is replaced atomically) and puts the
  old value back when the runner ends. The save is the one loaded when the batch starts (`/api/state.saveFolder`), or
  `--save-folder`. `--mod-config` names the file if it is not `Mods/StardewMCP/config.json` next to `--game-cmd`.
  The mod stays on the title screen and logs an error if that folder does not exist in the game's `Saves` folder.
- Other flags: `--crash-retries` (2), `--no-answer-secs` (120), `--stall-secs` (180), `--launch-timeout` (600).
- Files of a crashed attempt are kept: `day_005.jsonl` becomes `day_005_attempt1.jsonl`,
  `claude_day_005_part1.jsonl` becomes `claude_day_005_attempt1_part1.jsonl`, and the day's diaries/journals become
  `diary_005_attempt1.md`, so nothing a crashed attempt wrote reaches the AI's memory. `watchdog.jsonl` in the log
  folder lists every fault, relaunch and moved file. If the game crashed after the night save, the relaunched game is
  already on the next day: the day counts as done and its files are put back.
- Check the setup without any AI: `py runner/game_watchdog.py --relaunch-test --game-cmd "..."` kills the game, starts
  it again and checks that the save auto-loads.
- With `"EnableDebugApi": true`, `POST /api/debug/hang` with `{"seconds": 300}` blocks the whole game loop, to see the
  "no answer" signal fire for real. `/api/state` also carries `heartbeat`, `eventUp` and `festival` for the watchdog.

`studio/` contains the YouTube / OBS layer (`overlay.html`, `replay.html`, and `director.py` which switches OBS scenes from `/api/events`).

### Daily stats

When a farm day ends (sleep or pass-out) the mod writes `stats_NNN.json` next to the logs
(`stats_NNN_p2.json` for the other AI seats; the camera seat writes nothing). It holds money at
the start, bedtime and end of the day, income (the game's money earned, so shipping is included),
the shipping bin by category, crops planted / harvested / died and how many were unwatered at
bedtime, energy and health at bedtime, whether the day ended in bed or by passing out, tool calls
total / failed (per tool, plus the most common failure messages), skill levels, total hearts, the
deepest mine floor, tool upgrade levels and a few game counters. A day the mod joined late is marked
`partial`. Unwatered crops do not know about sprinklers or tomorrow's rain.

The files feed three things:

- `batch_report.md`, written by `run_days.py` in the run folder next to `logs/`: one row per day of the batch, totals, averages, how the days
  ended, failure hotspots, and a link to `lessons.md` when one is there. It is rewritten after every
  day; `py runner/stats.py report runs --from 1 --to 7` rebuilds it from the files. Turns,
  tokens and cost per day come from the session transcripts (`usage_NNN.json`).
- A line `run_days.py` adds to the first prompt of each day with yesterday's numbers next to the average of the three
  days before, e.g. `รายได้ 320g (เฉลี่ย 180g) · ล้มเหลว 4 ครั้ง (เฉลี่ย 9)`. It is left out on day 1 and
  when yesterday has no stats. `py runner/stats.py morning runs --day 8` prints it.
- `py runner/compare_runs.py runs_a runs_b --days 7 28 56 100` compares two runs (or two seats with
  `--player-a` / `--player-b`) at checkpoint days: where each stood and what it had totalled by then.
  There is no combined score on purpose.

### Pause, stop, backups and restore

Pause or stop a batch without killing `claude` in the middle of an action:

| Want | Do | What happens |
|---|---|---|
| Pause | `F8` in the game window, or `py runner/run_control.py pause` | The running tool call finishes, then every new AI call waits and the whole world is frozen (`frozen` is true, so `obs_recorder.py` cuts the time). The overlay shows "หยุดชั่วคราว". `F8` or `run_control.py resume` continues |
| Stop after today | `py runner/run_control.py stop_after_day`, or Ctrl+C once in the runner | The day ends with `go_to_bed`, the save is backed up, and the runner exits before the next day |
| Stop now | `py runner/run_control.py stop_now`, or Ctrl+C twice | The running tool call finishes, new calls are refused, the `claude` session ends and the runner exits. The game stays frozen until `resume`. Run `run_days.py` again to continue the day |
| Abort | Ctrl+C a third time | Ends `claude` at once |

`run_days.py` starts `claude` in its own process group, so Ctrl+C in the runner's terminal no longer reaches it.
A call that waits longer than 9 minutes during a pause is answered with an error (MCP clients time out
at 10 minutes), so the AI simply calls again and no stale call runs after `resume`.
A watchdog (#24) must read `paused` in `/api/state` before it treats a standing clock as a hang.

**Backups.** After every finished day `run_days.py` copies the save named by `saveFolder` in `/api/state`
(the save the game really has loaded) to `runs/saves/day_NNN_<save>`. A `smoke_*` copy is never backed up,
and nothing is backed up if `saveFolder` is missing (no guessing by modification time). The newest
`--keep-backups N` backups are kept (default 10, `0` keeps all). The Saves folder is `STARDEW_SAVES_DIR`, else the OS
default (`%APPDATA%\StardewValley\Saves` on Windows, `~/.config/StardewValley/Saves` elsewhere).
The copy is not synchronised with the game's own save, so a backup taken while the game is still writing
the night's save could be incomplete; the runner only backs up after `go_to_bed` has returned.

**Restore.** `py runner/restore_day.py --day 6` (game closed) puts the backup of day 6 back and sets everything the
run wrote for later days aside in `runs/abandoned/restore_day006_<time>/` (the replaced save, later backups, logs,
diaries, journals and transcripts; nothing is deleted). `--day 6` means the farm as it was when day 6 ended,
the morning of day 7; `--dry-run` shows the plan. Use it to repair a **mod** bug (a stuck cutscene, a crashed night).
It is not an undo button for the AI's own choices: those are part of the challenge.

If an unknown menu stays open for more than 10 seconds while `go_to_bed` waits for the new day (the shipping screen,
level-ups, the save screen and the ready check are expected), the mod logs its type and closes it with
`exitThisMenu` instead of letting the call run into its 5 minute timeout.

## Build from source

```bash
cd mod/StardewMCP
dotnet build -c Release
```

[ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig) finds your
game folder, copies the mod into `Mods/` and creates a release zip in `bin/`.

### Tests

Tests that don't need the game (the mod build does, because ModBuildConfig references its DLLs):

```bash
dotnet test mod/StardewMCP.Tests                                  # C#: pathfinding, tool schemas, MCP requests
pip install pytest && python -m pytest -q runner/tests studio     # Python: runner and studio
```

See [CONTRIBUTING.md](CONTRIBUTING.md#tests) for what they cover.

## Contributing

Pull requests and issues are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

## Credits

Originally forked from [Hunter-Thompson/stardew-mcp](https://github.com/Hunter-Thompson/stardew-mcp),
which first bridged Stardew Valley to MCP through a Go server and a WebSocket mod. This project has
since been rewritten as a standalone mod with its own in-game MCP server, and the original Go server
and WebSocket code have been removed.

## License

[MIT](LICENSE). Stardew Valley is a trademark of ConcernedApe; this project is a fan-made mod and
is not affiliated with ConcernedApe. No game files are included.

---

## ภาษาไทย

Mod สำหรับ Stardew Valley ที่ให้ AI (เช่น Claude) เล่นเกมเองได้ ผ่าน MCP server ที่รันอยู่ในเกม

- เวลาในเกมหยุดเดินระหว่างที่ AI กำลังคิด
- มี overlay สำหรับ OBS แสดงความคิดและการกระทำของ AI แบบสด ๆ
- มีหน้า replay และ log/ไดอารี่รายวัน
- มีสคริปต์ให้ AI เล่นต่อเนื่องหลายวัน และอัดวิดีโอเฉพาะช่วงที่เล่นจริง

**ติดตั้ง:** ลง SMAPI → แตกไฟล์ zip จากหน้า Releases ไว้ใน `Stardew Valley/Mods/` → เปิดเกมผ่าน SMAPI

**ต่อกับ Claude Code:** เปิดเกม โหลดเซฟ แล้วรัน
`claude mcp add --transport http stardew http://localhost:8770/mcp`

**สนับสนุนผู้พัฒนา:** ดูหัวข้อ *Support the project* ด้านบน

**เครดิต:** เริ่มต้นจากการ fork [Hunter-Thompson/stardew-mcp](https://github.com/Hunter-Thompson/stardew-mcp)
แล้วเขียนใหม่ทั้งหมดเป็น mod ที่มี MCP server ในตัว

**สัญญาอนุญาต:** MIT — ดาวน์โหลด ใช้ แก้ไข และนำไปพัฒนาต่อได้ฟรี แค่คงข้อความ license ไว้
