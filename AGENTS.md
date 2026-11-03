<!-- fennara-agents-start -->
# Fennara MCP Guidelines

This project uses Fennara MCP for Godot-aware inspection, editing, runtime error capture, diagnostics, scene validation, screenshots, and project settings.

When working on Godot-specific files or behavior, always read `addons/fennara/ai/guidelines.md` first. This includes work involving `.tscn`, `.tres`, `.res`, `.gd`, `.cs`, `.gdshader`, `project.godot`, scenes, nodes, resources, shaders, project settings, gameplay, UI, animation, rendering, Fennara addon behavior, or Fennara MCP behavior.

For specialized visual, runtime, operational, or client-specific work, use `addons/fennara/ai/index.md` to find the one relevant page. Do not read every linked page by default. Live tool schemas define exact calls and results.
<!-- fennara-agents-end -->

# Development tooling

Publication target (user instruction, 30 October 2026): publish game changes to
`EntertAlpha`. Preserve the historical `ReimAlpha` branch; do not reset it or
force-push without a specific user-approved historical target. An active shared
worktree can contain newer, unverified edits beyond a published build checkpoint.

Two tools exist so the daily loop is one command instead of four remembered ones. Use them
instead of re-deriving the commands.

- **`Tools/dev.ps1`** — the whole loop behind switches:
  `-Build`, `-Test [-TestArgs '--identity-only']`, `-Import`, `-Game [-CaptureDirectory <dir>] [-Headless]`, `-All`,
  and `-Doctor`: what starts with the game (engine, SDK, every autoload, the language count,
  the optional Luau/JVM adapters, content and save sizes). Autoloads are the first frames of
  every start, so a new runtime is visible there instead of being felt as a mystery stall.
  It finds the Godot executable itself and reports the numbers that matter (test count, failures,
  `EXPORT_VERIFY failures=N`). Example: `powershell -ExecutionPolicy Bypass -File Tools\dev.ps1 -All`.
- **`Tools/audit-localization.ps1`** — the translation tables checked for the three silent
  mistakes: the same English key translated twice and differently (the later file wins, so one
  button reads two ways), a dropped `{0}`, and an empty value. `-Fail` makes it a gate.
  Reports to `tmp_logs/localization-audit.csv`; 611 conflicting keys were found on the first run
  and are listed there for translating one at a time.
- **`Tools/game-capture.ps1`** — look at a screen a player reaches by clicking.
  `-Game` above runs the exported executable and can only photograph what its own probe
  reaches. This runs the game out of the editor project with Fennara's runtime helper switched
  on, so the game reports readiness, photographs itself, and then takes orders from a small
  GDScript file (`-Script res://path.gd`, `extends RefCounted`, `func run(ctx)`): press
  buttons by text, wait, capture. `ctx` is documented in
  `addons/fennara/runtime/runtime_script_context.gd`; `click_button` and `capture` are async
  and need `await`. Example:
  `powershell -ExecutionPolicy Bypass -File Tools\game-capture.ps1 -Script res://tools/probe.gd`.
  Images land under `tmp_playtest/captures/<stamp>/`. The game is left running, so more
  scripts can be sent to it; close its window when done. The switch that makes it work is
  `FENNARA_RT_SPEC`: Fennara's own `runtime_session` does not set it on this machine, which is
  why every runtime session times out at startup.
  On 30 October 2026 it worked twice and then stalled four times running after a rebuild: the game
  log stops right after `[MCPRegistry] runtime port 6570 registered` and never reaches the
  `[godot_ai game_helper] registered mcp capture` line that the working runs had, so the script's
  readiness gate waits out its 240 seconds. Killing every Godot process and every `godot-ai` daemon
  first rescued the earlier attempts and did not rescue those four. Known flake: check for the
  missing line before blaming the game.
  On 31 October 2026 the same stall was reproduced through **Fennara's own MCP runtime_session**
  (`startup_timeout`, exit 1, identical six log lines), so it is not the script: the game itself
  does not finish starting. The cause is now known and it is Godot's own extension loading, not a
  flake and not antivirus: from 4.4 Godot copies every GDExtension to `~lib<name>.dll` next to it
  and loads THAT copy, so the original can be replaced later. A running editor holds those copies
  (`~libfennara…`, `~libgodotsteam…`, `~liblimboai…`, `~libziva_agent…` — their creation time is the
  editor's start time), so a second Godot started from the same binaries cannot refresh them: the
  copy fails, `Can't open GDExtension dynamic library` follows, `MCPRuntimeServer` binds port 6570
  and the process stops before any `[folders]`/`[mods]`/`[dof]` line. Every failed attempt leaves a
  full-size `~RF*.TMP` beside the DLL — 88 of them, 401 MB, by 31 October.
  **So: the game cannot be captured while an editor has this project open.** `Tools/game-capture.ps1`
  now refuses in one second and names the editor's PID, and `dev.ps1 -Game` runs the same editor
  binary, so it is in the same position. The ways out, in order of preference: ask the editor's owner
  for a window with it closed (keep the PIDs you started and close only those, never another agent's
  session); or export the game and photograph the exported build, which carries the template DLL
  variants and its own copies.
- **`Tools/game-mcp/server.mjs`** — a dependency-free MCP server exposing the same checks as
  tools: `build_game`, `run_tests`, `game_verify`, `capture_screenshots`, `list_technologies`,
  `localization_audit`. Registered as `deal-or-force-dev` in `.mcp.json`; MCP servers are read
  when the client window starts, so restart the window after changing the file.
- **`Tools/probe-project.ps1`** — builds `tmp\probe_run\`: the real project reached through
  directory junctions, with only `BootGuard` as an autoload, so the in-game `ExportVerification`
  probe can photograph the game without the Fennara runtime helper.
- **Editor start-up, and why it used to crawl**: the editor walks `res://` and follows
  directory junctions, so anything scratch inside the project is scanned — and imported, if it
  is an image. The probe project used to live in `tmp_run/` next to the real folders, which made
  every editor start scan the whole project a second time; `tmp_playtest/` added 542 PNGs to
  import on every run; `Tools/BtaBridge` alone is **45 225 files** and `Tools` in total is
  **69 015**. Every scratch folder now carries an empty `.gdignore` (`tmp`, `tmp_logs`,
  `tmp_playtest`, `.fennara`, `Tools/*/bin|obj`, `Tools/BtaBridge`, `Tools/UniversalModder`,
  `Tools/game-mcp`, `Tools/knowledge-mcp`), which makes Godot skip the folder entirely. Keep new
  scratch directories out of `res://`, or give them a `.gdignore` on the same day they appear.
  Closing Godot before opening the editor also matters: a second editor on the same project and
  the `godot-ai` daemons left over from earlier sessions compete for the same files.
- **`Tools/editor-doctor.ps1`** — the same answer as numbers: how many files each top folder puts
  in front of the editor, how large `.godot/imported` has grown, whether the C# assembly is older
  than the newest `.cs` (the editor then compiles on start), and which editors are running.
  `-Clean` deletes the import cache and the editor's file cache, drops `.import` sidecars inside
  ignored scratch folders, and rebuilds in one slow pass; `-Time` measures a headless editor
  start. Both refuse to run while an editor holds the project, because it locks every GDExtension
  DLL and the run would fail on the copy.
- **`Tools/close-godot.ps1`** — list every Godot process by kind (`editor`, `run`, `daemon`) and
  close the ones asked for: `-Daemons` (Fennara helpers, recreated on demand), `-Runs`
  (command-line probes and tests), `-Editors` (unsaved work is lost; asks first), `-All -Yes`.
- **Never stop a Godot process you did not start. This is a shared machine and the editor is
  somebody's working session.** On 30 October 2026 an agent ran
  `Get-Process *odot* | Stop-Process` repeatedly to clear file locks before game captures. That
  pattern matches the user's editor as well as the agent's own game runs, so the editor closed
  under them again and again — while the capture it was meant to unblock still failed, because the
  lock was never the cause (see the `game-capture.ps1` note: the game does not finish starting at
  all, and the log says `Error copying library: libfennara...dll`). Destructive and useless at the
  same time. The rules that follow from it: keep the PIDs you launched and close only those;
  `-Daemons` is the only kind an agent may close on its own initiative; everything else — the
  editor, other agents' runs — is asked for, not taken. Long simulation runs (`SimBench`) do not
  need the editor closed at all. For a run that must not touch the project's locks, use
  `Tools/probe-project.ps1`, which reaches the real project through directory junctions and
  autoloads only `BootGuard`.
- **Bug reports** — the button `Report a problem` in Settings and the console command `report`
  both call `Scripts/SupportReportGame.cs` → `Scripts/Simulation/SupportReport.cs`, which writes
  `reports/DealOrForce-report-YYYYMMDD-HHMMSS.zip`: `report.txt` (build, machine, language,
  world), the last three logs, `settings.json`, and the newest save when it is under 32 MB.
  Nothing in it touches Godot, so `Tools/FormableTests --support-report-only` exercises the same
  code without the engine.
- **`Tools/FormableTests --replay-save <file.bdfsave> [days]`** — the player's own campaign, run
  forward. A crash report says "it broke on 24 July"; this loads that save **read-only**, builds
  the province topology, unpauses, and ticks day by day, stopping at the first day the simulation
  refuses with the day, the exception type and the top stack frames (the in-game snapshot keeps no
  stack). A save is evidence, not a fixture: it is never written back. Prints `REPLAY_LOADED` /
  `REPLAY_WORLD` (nations, movements, offices, open wars, regions, chronicle), then `REPLAY_OK` or
  `REPLAY_CRASH`. Use it before and after fixing a runtime report; it is the difference between
  "should be fixed" and "your save now runs".
- **`Tools/SimBench <days> <seed> --soak` and `... --kin`** — ten-year measurement, not a smoke test:
  `--soak` watches the world's health (`Soak PASS`/`FAIL`, alerts, wars, GDP, radicalism) and `--kin`
  counts what the kin-statecraft ladder actually did in a world nobody is playing — agitations
  quiet and caught, volunteer drafts, proxies founded, offices opened/closed/shut/exposed — then
  judges broken invariants only (an office with no living host, a proxy with no living sponsor).
  Flows are counted incrementally by chronicle sequence, because the chronicle is a 2048-line ring
  buffer and reading it at the end reports the last two thousand lines as if they were ten years.
  This is the run that showed the computer catching itself every single time
  (`docs/KIN_AI_ALWAYS_CAUGHT_2026_10_30_RU.md`); a mechanic with one outcome looks exactly like
  a working mechanic in a log.
- **`Tools/SimBench <days> <seed> --wars`** — every war older than a year, every 90 days: score,
  occupied ground, pending offer, the last line of negotiation, and the numbers the decision is
  made of (budget against each side's floor, `TimeToStop`, acceptance of a white peace and of a
  status quo, war support). Written because `LastNegotiation` alone misleads: an offer between two
  computers is resolved on the spot and leaves the field empty. This is the report that found five
  separate reasons a war could never end (`docs/WARS_THAT_NEVER_ENDED_2026_10_30_RU.md`).
- **Moved out of `res://` on 27 October 2026**: `Tools/BtaBridge` (**45 225 files**) and
  `Tools/UniversalModder` now live in `D:\Godot Projects\deal-or-force-tools\`. Neither is used
  by Godot — BtaBridge bridges another game's mod format and UniversalModder is an upstream clone
  — and together they were more than half of everything the editor walked. `addons/at-icons`
  (7 428 files) is `.gdignore`d with its plugin switched off in `project.godot`: no script, scene
  or resource referenced it.
- **`Tools/knowledge-mcp/server.mjs`** — the same kind of dependency-free MCP server for the
  repository's own memory: `search_docs` (grep docs/game/Tools), `plan_status` (master-plan rows
  with the status vocabulary), `list_defines` (simulation tunables with their rationale),
  `backlog_ideas`, `recent_work`, `data_stats`. Registered as `deal-or-force-knowledge`.
- **`Tools/harness-sync.mjs`** — keeps skill copies in step across harnesses
  (`.agents` → `.cursor`/`.devin`). Reports skills missing from a harness and copies them with
  `--apply`; differing copies are deliberate adaptations (fennara-godot is Antigravity's lazy-tool
  version) and are reported, never overwritten. Run it after adding a new skill.
- **`Tools/mcp-survey.mjs`** — read-only inventory of every MCP server and agent skill on this
  machine: a matrix of 23 servers across 11 harness views, which ones are switched off, which
  are configured differently in two places, and the same matrix for skills. `harness-sync`
  only sees project skill folders, so a skill written into `~/.config/opencode/skills` is
  invisible to it — that is how two project skills went missing from the repository.
  `--emit <harness>` prints paste-ready JSON for what that harness lacks; `--json` gives the
  matrix to a script. There is deliberately no `--apply`: a config outside this project
  belongs to whoever owns it.
- **`Tools/game-folders.ps1`** — puts everything the game writes into one folder,
  `Documents\Bayte Realization\Deal or Force` (`saves/`, `settings/`, `logs/`, `screenshots/`,
  `mods/`, `dlc/`, `reports/`). The game's own half is code (`Scripts/GamePaths.cs`,
  `Scripts/Simulation/GameFolders.cs`); the script moves Godot's `user://` folder in and
  replaces it with a directory junction, so engine files land in the same place. Idempotent;
  needs Godot closed because Windows will not move an open `godot.log`. `-WhatIf` prints the plan.
- **External AI tooling** — `docs/AI_TOOLING_ASSESSMENT_RU.md` records the verdict on
  REA, universal-modder and Co-op Anything. None of their MCP servers are registered in
  `.mcp.json`. REA reverse-engineers binaries and needs Node ^22.19 / ^24.11 / >=26.
  On 23 October 2026, at the user's request, REA 6.7.0 was installed outside the project
  for Codex only, using an existing bundled Node 24.22.1; the system Node remains 22.13.
  See `docs/DEVELOPMENT_PASS_2026_10_23_RU.md` for scope and verification.
  universal-modder's fal MCP is a paid asset service; its `um` CLI, for
  another game only, runs from the existing clone with
  `uv run --project Tools\UniversalModder\upstream`. Co-op Anything is a catalog of other
  games' co-op hooks. The multiplayer checklist for this game stays in
  `docs/COOP_ANYTHING_WORKFLOW_RU.md`. Read that assessment before installing any of the three.

All of these are deliberately plain: no SDK, no installed package, nothing to keep patched.

Long runs do not survive a shell that is killed at 30 seconds. `Start-Process` children die
with the shell that made them; a detached process created through WMI does not:
`Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='cmd.exe /c <script.bat>'}`.
That is how a build, a test suite, or a 1825-day SimBench run is started here, with the
output written to a log and read afterwards.

The game's own release gate is `Tools/build.ps1` → `Tools/verify-build.ps1`, which runs the
in-game `ExportVerification` probe inside the exported executable. That probe is game code and
lives in `Tools/ExportVerification/`, compiled into the assembly on purpose — see the comment in
`DealOrForce.csproj`.

# Agent skills

Local shared skills live in `.agents/skills/` (Antigravity), `.cursor/skills/` (Cursor/Devin) and
`.devin/skills/` (Devin): `deal-or-force-dev`, `deal-or-force-modding`, `fennara-godot`, plus
`dof-verify` (which checks prove which change), `dof-balance-ab` (multi-seed SimBench A/B with
`--define`), `dof-mechanic-block` (acceptance pipeline for a new mechanic) and `dof-plan-sync`
(read the plan, record a pass). `Tools/harness-sync.mjs` keeps the copies in step.

`.agents/` is deliberately local and ignored by Git. It is not needed to build or
play the game. Cloning the public repository does not install those optional
assistant instructions; keep existing local copies when updating. If the local
source is absent, harness-sync reports that it has nothing to synchronise.

