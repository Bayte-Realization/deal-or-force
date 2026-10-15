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
- **`Tools/game-mcp/server.mjs`** — a dependency-free MCP server exposing the same checks as
  tools: `build_game`, `run_tests`, `game_verify`, `capture_screenshots`, `list_technologies`,
  `localization_audit`. Registered as `deal-or-force-dev` in `.mcp.json`; MCP servers are read
  when the client window starts, so restart the window after changing the file.

Both are deliberately plain: no SDK, no installed package, nothing to keep patched.

The game's own release gate is `Tools/build.ps1` → `Tools/verify-build.ps1`, which runs the
in-game `ExportVerification` probe inside the exported executable. That probe is game code and
lives in `Tools/ExportVerification/`, compiled into the assembly on purpose — see the comment in
`DealOrForce.csproj`.

