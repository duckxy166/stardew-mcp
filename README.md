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
| `SessionBreakTimes` | `[1200,1800]` | Planned session breaks: from these game times a tool result carries `Session break:` and the AI is asked to continue in a new session (see below) |
| `SessionBreakToolCalls` | `150` | Planned break after this many tool calls in one session (0 = off) |
| `SessionBreakResultChars` | `120000` | Planned break after this many characters of tool results in one session, about 30k tokens (0 = off) |
| `SessionBreaksForAll` | `false` | Planned breaks for every MCP client; by default only clients that connect to `/mcp?breaks=1` (what `run_days.py` does) |

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
- Long days are split into parts. At the break times (and after too many calls or too much result text)
  the mod adds a `Session break:` line to a tool result, refuses further actions, and the AI writes its
  plan with `think` and ends its turn. `run_days.py` then starts the next part with that plan, the
  first `think` of the day and the last results. Planned breaks do not count toward `--max-parts`
  (`--max-breaks`, default 8, stops a loop; `--no-session-breaks` turns them off). The mod only counts
  characters, so the token limit is an estimate.
- Every part appends one row to `runs/usage.csv` (input, output and cache tokens, turns, tool calls,
  peak context). `py runner/usage_log.py summary` prints a table per day with the cache hit ratio,
  `check` compares the file with the saved transcripts, `rebuild` recreates it from them.
- `smoke.py` runs LLM-free checks of the tools (farm loop, shop, shipping, sleep) on a copy of your
  save, then reloads your save. `run_days.py` runs it before every batch and stops if a case fails.
  It needs `"EnableDebugApi": true` in `config.json`; see `py runner/smoke.py --list`. The debug
  endpoints are not MCP tools, so the AI cannot see or call them.
- `obs_recorder.py` pauses the OBS recording while the game is frozen, so the video only contains
  gameplay. Needs OBS with obs-websocket enabled.

`studio/` contains the YouTube / OBS layer (`overlay.html`, `replay.html`, and `director.py` which switches OBS scenes from `/api/events`).

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
