# Stardew MCP

SMAPI mod (C#, net6.0) that hosts an MCP server inside Stardew Valley so AI agents can play.
MIT licensed, open source.

## Project goal: three AIs play together

Three different AIs (Claude, Gemini, GPT) share one farm in split-screen co-op and play as
characters for YouTube videos: they talk, plan, split work, help each other, slack off, lie,
get caught and betray each other. The story must emerge from the game, not from a script.

### Requirements

**Players and backends**
- One game instance, split-screen local co-op with **4 screens** in the game's even 2×2 grid:
  P1 = host, P2/P3 = local farmhands, each driven by an AI; **P4 = camera / summary screen**
  for the video (no AI). P4's farmer is parked out of the way and excluded from the scheduler,
  perception and interrupts; what P4 shows is chosen by studio through a generic mod API.
- Each AI farmer has its own MCP endpoint: `/mcp/p1`, `/mcp/p2`, `/mcp/p3`.
- The save needs 3 cabins (new co-op farm with starting cabins); the mod joins P2–P4 itself
  (`GameRunner.AddGameInstance`, no gamepads) and locks physical keyboard/mouse input on P1.
- Backends are the CLIs the user already subscribes to: `claude` (Claude), `agy` (Gemini),
  `opencode` (GPT). Each session gets only the stardew MCP tools (no shell, no file edits).
  A direct API backend may be added later per player if a CLI proves unreliable.
- Filmed models: P1 Claude Haiku 5.5 (`claude-haiku-5-5`), P2 Gemini 3.8 Flash (`agy`, `gemini-3.8-flash-*`),
  P3 GPT-6 Luna (`opencode`, `gpt-6-luna`; needs an OpenAI-compatible provider in the user's opencode config).
  The 3-day warm-up uses P1 and P2 only.
- Characters **emerge from experience** on top of the base model, not from hand-written personas (#48):
  each AI writes its own `self` note (overwritten every evening, about 150 words) and a one-line `people`
  note per other player. Only the owner and the viewers see them (overlay, like `think`); the viewers get
  a before → after card each evening at bedtime. Writing is reminded, not forced. A persona file with an
  optional secret goal stays an extra if the emerged characters turn out flat.

**Time: event-driven, no fixed rounds**
- Each AI acts independently, one action at a time; nobody waits for a turn.
- The world (clock *and* movement) is frozen whenever any AI is thinking, i.e. between its last
  tool result and its next action. Thinking speed never costs game time, so a farmer standing
  still has chosen to (`wait`, slacking), never "the model is slow".
- A thinking timeout (config) makes a stuck or crashed AI's farmer idle so the world can continue.
- Day shape: morning meeting (chat + plan board only, world frozen, message cap) → free play →
  evening private diary → everyone goes to bed.

**Communication (three levels)**
- `say`: real in-game chat box + text above the farmer's head. All AIs and viewers see it.
- `whisper`: to one player only; viewers see it too.
- `think`: private thought, viewers only (overlay). This is where secret plans and lies show.
- `emote` (game emotes), `give` (hand an item to another player).
- Shared plan board (`plan_read`, `plan_edit`): every edit is logged with author and time.

**Perception and interrupts**
- Observations of nearby players show what they are *actually* doing (current tool + time spent),
  next to what they claimed on the plan board or in chat, so lies can be caught.
- These events interrupt a running long action (walk, wait, farm work) and return control to the
  AI with the event list: being @mentioned or whispered to, another player coming into view,
  someone taking from your chest or harvesting your plot, receiving a gift, weather or crop damage.

**Things to compete over**
- Separate wallets (game multiplayer option), a personal plot and chest per player.

**Separation: game vs. YouTube vs. AI orchestration**
- `mod/` is gameplay only: per-player MCP, scheduler, chat, plan board, `/api/state`, and an
  event stream `/api/events` (SSE). It never references OBS or video concerns.
- `studio/` is the YouTube layer and optional: overlay (per-player think/chat/whisper), replay,
  OBS director, recorder, story log for editing. It only reads the mod's HTTP API.
  `studio/overlay.html` and `studio/replay.html` live here.
- `runner/` launches and supervises the three AI sessions and the day loop.

**Video**
- OBS scenes: "all three" plus "focus P1/P2/P3" (crop of that player's screen, others as
  picture-in-picture). The director switches scenes from `/api/events`: focus whoever speaks,
  whispers or triggers a key event (theft, gift, caught slacking); grid during plain work.
- Record at 2560×1440 (DLDSR 1.78x, exclusive fullscreen): 1280×720 per screen at effective
  zoom 1.0, the sharpest setting. Launch with `SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS=0` so the game
  keeps rendering when OBS has focus. Fallback: 1080p borderless + OBS point upscale.
- Per-screen base zoom = 1.5 × (screen width / 960), keeping about 20 tiles in view.
- Frozen frames must look like live ones: no "Paused" banner, HUD kept, no gamepad hint,
  join/hosting chat lines or cursor. The recorder keeps cutting frozen time, holding a few
  seconds for chat bubbles and thoughts.

### Phases

0. ~~Spike: split-screen with mod-driven farmers~~ (#37), **done: GO**. Findings and API names
   are in the #37 comments; throwaway code on `spike/37-splitscreen` and `spike/37-layout`.
1. Multi-player core (#38): split-screen setup (join P2–P4, focus/sleep fixes, P1 input lock,
   recording-artefact patches, zoom), per-player endpoints and runtime, freeze-while-anyone-thinks
   scheduler (`netWorldState.IsPaused`), thinking timeout, per-player logs.
2. Social tools (#39): say, whisper, think, emote, give, plan board.
3. Perception and interrupts (#40).
4. Runner for claude / agy / opencode with day phases and per-seat memory (#41). Before filming, a 3-day, 2-seat
   warm-up (Haiku + Gemini Flash) tests the emergent-persona mechanism (#48).
5. Studio (#42): `/api/events`, overlay and replay moved out, OBS director and scenes, recorder.

## Layout

```
mod/StardewMCP/
  ModEntry.cs          SMAPI entry: config, events, auto-load, mcp_freeze command
  Pathfinder.cs        A* walking routes; IsWalkable uses the game's own collision check
  Agent/
    AgentHttp.cs       HttpListener on localhost:8770: /mcp, /overlay, /replay, /api/*
    AgentRuntime.cs    Runs tool calls on the game thread as coroutines; freezes time between calls
    AgentTools.cs      Tool definitions (ToolDef) and their coroutines
    MenuTools.cs       menu: reads any open menu and clicks it through per-menu adapters (part of AgentTools)
    SocialTools.cs     say / whisper / emote / give / plan board; per-seat inbox appended to the next tool result
    WorldText.cs       Text renderings of status / surroundings / inventory for the model
    AgentLog.cs        Per-day JSONL logs and diaries
    ModConfig.cs       config.json settings
    DebugApi.cs        /api/debug/* and /api/debug/p{N}/* test hooks (warp, build_cabins, capture...), off by default
    Players.cs         The 4 split-screen seats (P1-P3 AI, P4 camera) and their per-screen state
    WorldClock.cs      Freezes the world while any connected AI thinks (host pause); thinking timeout
    DayStats.cs        Per-seat daily stats (stats_NNN[_pN].json at day rollover, GET /api/stats)
    SplitScreen.cs     Joins P2-P4 without gamepads, per-screen zoom, gamepad-hint cleanup
    SplitScreenPatches.cs  Harmony: P1 input lock, clean frozen frames, hidden join lines, frame capture
studio/
  overlay.html         OBS browser-source overlay
  replay.html          Day-by-day replay of the logs
  director.py          OBS scene switcher driven by /api/events (SSE)
  test_director.py     Unit tests for director scene picking
runner/
  smoke.py             LLM-free tool tests on a smoke_* copy of the save (gate for run_days.py)
  run_days.py          One `claude -p` session per in-game day
  obs_recorder.py      Pauses OBS recording while the game is frozen
  stats.py             batch_report.md, usage_NNN.json, the Thai morning stats line (from the mod's stats files)
  compare_runs.py      Two runs side by side at checkpoint days
  tests/               pytest for stats.py and compare_runs.py (generated fixtures, no game)
  system_prompt.md     System prompt used by run_days.py
```

## How it works

- SMAPI raises `UpdateTicked` once per split-screen screen (`Context.ScreenId`); `Game1.player`,
  `currentLocation` and menus are per screen. Each seat has its own `AgentRuntime`, ticked on its
  screen; `AgentTools.Rt` is the seat of the screen being updated.
- MCP requests arrive on the HTTP thread; the seat's `AgentRuntime` runs actions one at a time and
  instant observations in a second lane, so `look`/`status` answer during the seat's own action.
- Tools are `IEnumerator` coroutines: `yield return null` waits a tick, `yield return <IEnumerator>`
  runs a nested step. Report results through `CallContext` (`Result`, `Fail`).
- `Instant` tools only observe and keep the world frozen. `WorldClock` freezes the world while any
  connected AI has no action running; running actions hold still meanwhile.
- The model makes decisions; mechanics (pathing, aiming, button presses) are automated in the mod.

## Build

```
cd mod/StardewMCP
dotnet build -c Release                                    # builds, deploys to Mods/, makes zip
dotnet build -p:EnableModDeploy=false -p:EnableModZip=false   # compile check only
```

## Rules

- Keep everything original: don't copy code from other mods or repos unless their license
  allows it, and credit them if so.
- Never commit game files, saves, logs or personal paths.
- New game features follow `docs/coverage.md`: reuse the game's own entry points, and update the
  feature's row in the same PR.
- Reference: https://stardewvalleywiki.com/Modding:Modder_Guide/APIs
