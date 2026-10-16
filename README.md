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
| `status`, `look`, `inventory` | Read the player, surroundings and items |
| `think` | Post a thought (shown on the overlay, costs no game time) |
| `walk_to`, `travel_to` | Pathfind to a tile, or to another location |
| `use_tool`, `plant`, `harvest` | Farm work on a target tile |
| `interact`, `talk_to`, `respond`, `close_menu` | Objects, villagers, dialogue and menus; `interact` can hold an item first (load a machine, use a totem) |
| `menu` | Read any open menu, then use it: take/put (chests), choose (mine elevator), amount (send money), type (names), ok/cancel, or click a listed button |
| `shop_list`, `buy`, `ship`, `eat` | Shopping, shipping bin, food |
| `wait`, `go_to_bed` | Let time pass, end the day |
| `say`, `whisper`, `emote`, `give` | Multi-player (split-screen): chat to everyone, tell one player privately, show an emote, hand an item to a nearby player. All cost no game time |
| `plan_read`, `plan_edit` | Shared plan board with an edit history (author and time) |

### Other endpoints

| URL | Use |
|---|---|
| `http://localhost:8770/overlay` | OBS browser source (thoughts + actions) |
| `http://localhost:8770/replay` | Day-by-day replay of the logs |
| `http://localhost:8770/api/state` | Raw game state JSON |
| `http://localhost:8770/api/stats` | Daily stats: the days that have a `stats_NNN.json`; `?n=3` returns day 3 of every AI seat |

## Configuration

`Mods/StardewMCP/config.json` is created on first launch:

| Setting | Default | Meaning |
|---|---|---|
| `HttpPort` | `8770` | Port for MCP, overlay and replay |
| `FreezeTimeWhileThinking` | `true` | Stop the clock while no action is running |
| `LogDir` | `""` | Log folder; empty = `logs/` in the mod folder |
| `AutoLoadSave` | `""` | Save folder to load automatically from the title screen |
| `EnableDebugApi` | `false` | Serve `/api/debug/*` test hooks for `runner/smoke.py`; leave off for normal play |
| `ThoughtLanguage` | `"Thai"` | Language the AI is asked to use for on-screen text (`think`, action reasons, diary), e.g. `"English"` |
| `SplitScreen` | `false` | Three AIs + a camera screen in split-screen co-op (see below) |
| `FarmhandNames` | `["Gemini","GPT","Camera"]` | Farmhand names for P2, P3, P4; an existing farmhand with that name is reused |
| `ThinkTimeoutSeconds` | `300` | Split-screen: an AI silent for longer stops holding the world frozen (0 = never) |
| `LockHostInput` | `true` | Split-screen: ignore the real keyboard/mouse on P1 once a save is loaded |
| `CleanFrames` | `true` | Split-screen: frozen frames look live (no Paused banner, HUD kept), no cursors, gamepad hint or join messages |
| `AutoZoom` | `true` | Split-screen: zoom each screen to about 20 tiles across |

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

`ThoughtLanguage` only changes what the MCP tools ask the AI to write. The overlay's own labels and
`runner/system_prompt.md` are still written for Thai viewers, so edit those too if you switch language.

## Unattended runs and recording

`runner/` contains Python scripts (standard library only):

- `run_days.py` runs one Claude Code session per in-game day, keeps diaries between days and
  backs up the save. Example: `py runner/run_days.py --days 3`
- `smoke.py` runs LLM-free checks of the tools (farm loop, shop, shipping, sleep) on a copy of your
  save, then reloads your save. `run_days.py` runs it before every batch and stops if a case fails.
  It needs `"EnableDebugApi": true` in `config.json`; see `py runner/smoke.py --list`. The debug
  endpoints are not MCP tools, so the AI cannot see or call them.
- `obs_recorder.py` pauses the OBS recording while the game is frozen, so the video only contains
  gameplay. Needs OBS with obs-websocket enabled.
- `stats.py` and `compare_runs.py` turn the daily stats into reports (see below).

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

## Build from source

```bash
cd mod/StardewMCP
dotnet build -c Release
```

[ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig) finds your
game folder, copies the mod into `Mods/` and creates a release zip in `bin/`.

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
