# Contributing

Thanks for helping out! Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an issue with:

- what you asked the AI to do and what happened instead,
- your SMAPI log (upload it at <https://smapi.io/log> and paste the link),
- game, SMAPI and mod versions.

## Pull requests

1. Fork the repo and create a branch from `main`.
2. Build with `dotnet build` in `mod/StardewMCP` and try the change in game.
3. Run the [tests](#tests) that don't need the game.
4. Keep changes focused; match the style of the surrounding code.
5. Describe what you changed and how you tested it.

## Tests

Two test suites run without Stardew Valley:

```bash
dotnet test mod/StardewMCP.Tests                                  # C#: xUnit, .NET 6 SDK
pip install pytest && python -m pytest -q runner/tests studio     # Python: runner and studio
```

The mod project itself needs the game's DLLs (ModBuildConfig finds them in your game folder), so it
can't be built on CI or on a machine without the game. `mod/StardewMCP.Tests` therefore does **not**
reference it. It compiles the mod's game-free source files directly (see the `<Compile Include=...>`
list in its `.csproj`) and tests them: route search on text maps, tool argument helpers, the rules for
tool schemas, MCP request handling with a fake seat.

When you add logic that could run without `Game1`, `GameLocation` or `Farmer`, put it in a file of its
own that uses no game types, link that file from the test project and add a test. Keep the move small:
leave a one-line forwarder where the old code was. Logic that does need the game is checked in game
(and by `runner/smoke.py`).

`runner/tests/` replays the synthetic transcripts, logs and diaries in `runner/samples/` through
`run_days.py`, and reads the tool definitions out of the C# source to check their schemas. Samples must
stay invented: no real saves, logs, paths or accounts.

## Adding an agent tool

Tools live in `mod/StardewMCP/Agent/AgentTools.cs`. Each tool is a `ToolDef` with a name,
description, JSON input schema and a coroutine that runs on the game thread. Mark a tool
`Instant` if it only reads state, so game time stays frozen while it runs. Every other tool takes a
`reason` string so viewers can read why the AI does it. Names are snake_case, and `required` may only
list declared properties; `ToolContract` (logged at startup) and `runner/tests/test_tool_sources.py`
check this.

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
