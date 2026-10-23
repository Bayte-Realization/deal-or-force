---
name: fennara-godot
description: >-
  Inspect, edit, and play Deal or Force inside Godot 4.8 through Fennara MCP,
  with godot-ai and Ziva as the existing editor bridges. Use when launching
  the game, capturing the campaign UI, editing scenes, or checking editor
  connectivity. Read addons/fennara/ai/guidelines.md before Godot tool calls.
---

# Fennara on Deal or Force

Editor binary: `C:\GodotEngineNet 4.8-dev\Godot_v4.8-dev7_mono_win64.exe`. Project path is this repository. Fennara and godot-ai are already configured. Do not start a second copy of either bridge.

1. Call `fennara_status`. If the editor is scanning or importing, wait and check once. Do not poll.
2. Do not hand-edit `.tscn`, `.tres`, or `.res`. Use `run_scene_edit_script` and `project_settings`.
3. After a batch of C# edits, one `script_diagnostics` project scan or `dotnet build DealOrForce.csproj`.
4. Play: `runtime_session` status, then start `res://Scenes/MainMenu.tscn`. Drive it with `runtime_script` (`extends RefCounted`, `func run(ctx)`, no `@tool`). Poland is the first-campaign country: «Одиночная игра» → «Выбрать страну на карте» → search `POL` → «ИГРАТЬ ЗА POL». The campaign starts paused. Stop the session when the observation is done.
5. Reuse probes in `.fennara/scripts/runtime/` instead of rewriting them.
6. If `runtime_session` times out twice and the editor log is stuck on an unfinished action, stop retrying and say so.
