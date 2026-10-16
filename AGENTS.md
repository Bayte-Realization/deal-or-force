<!-- fennara-agents-start -->
# Fennara MCP Guidelines

This project uses Fennara MCP for Godot-aware inspection, editing, runtime error capture, diagnostics, scene validation, screenshots, and project settings.

When working on Godot-specific files or behavior, always read `addons/fennara/ai/guidelines.md` first. This includes work involving `.tscn`, `.tres`, `.res`, `.gd`, `.cs`, `.gdshader`, `project.godot`, scenes, nodes, resources, shaders, project settings, gameplay, UI, animation, rendering, Fennara addon behavior, or Fennara MCP behavior.

For specialized visual, runtime, operational, or client-specific work, use `addons/fennara/ai/index.md` to find the one relevant page. Do not read every linked page by default. Live tool schemas define exact calls and results.
<!-- fennara-agents-end -->

# Development tooling

Two tools exist so the daily loop is one command instead of four remembered ones. Use them
instead of re-deriving the commands.

- **`Tools/dev.ps1`** — the whole loop behind switches:
  `-Build`, `-Test [-TestArgs '--identity-only']`, `-Import`, `-Game [-CaptureDirectory <dir>] [-Headless]`, `-All`.
  It finds the Godot executable itself and reports the numbers that matter (test count, failures,
  `EXPORT_VERIFY failures=N`). Example: `powershell -ExecutionPolicy Bypass -File Tools\dev.ps1 -All`.
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
- **`Tools/game-mcp/server.mjs`** — a dependency-free MCP server exposing the same checks as
  tools: `build_game`, `run_tests`, `game_verify`, `capture_screenshots`, `list_technologies`,
  `localization_audit`. Registered as `deal-or-force-dev` in `.mcp.json`; MCP servers are read
  when the client window starts, so restart the window after changing the file.
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

Shared skills live in `.agents/skills/` (Antigravity), `.cursor/skills/` (Cursor/Devin) and
`.devin/skills/` (Devin): `deal-or-force-dev`, `deal-or-force-modding`, `fennara-godot`, plus
`dof-verify` (which checks prove which change), `dof-balance-ab` (multi-seed SimBench A/B with
`--define`), `dof-mechanic-block` (acceptance pipeline for a new mechanic) and `dof-plan-sync`
(read the plan, record a pass). `Tools/harness-sync.mjs` keeps the copies in step.

