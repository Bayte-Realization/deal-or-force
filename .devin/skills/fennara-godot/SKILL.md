---
name: fennara-godot
description: Godot 4.8 inspection, scene editing, runtime playtesting, and visual capture workflow using Fennara MCP, godot-ai, godot-mcp-toolkit, and Ziva bridge in Deal or Force.
allowed-tools:
  - mcp_list_tools
  - mcp_call_tool
  - read
  - exec
---

# Godot 4.8 & Fennara MCP Workflow for Deal or Force

1. **Always check `fennara_status` first** to verify daemon and editor connectivity.
2. **Never hand-edit `.tscn`, `.tres`, or `.res` as plain text** when Godot-aware tools (`run_scene_edit_script`, `project_settings`) are available.
3. **C# Diagnostics**: After completing a batch of C# edits, run `script_diagnostics` with `scan_project: true` or `dotnet build DealOrForce.csproj`.
4. **Runtime Playtesting**:
   - Check `runtime_session` (`action: "status"`), then `start` with `scene_path: "res://Scenes/MainMenu.tscn"` or `"res://Scenes/Main.tscn"`.
   - Drive and capture evidence using `runtime_script` (never include `@tool` in runtime scripts; extend `RefCounted` with `func run(ctx: Variant) -> void`).
   - Stop the `runtime_session` promptly when finished.
   - Reusable runtime probes live in `.fennara/scripts/runtime/`.
