# Language Strategy Plan - Deal or Force

This plan details the usage of various programming languages within the game to maximize performance, maintainability, and moddability.

## Strategy Summary

- **C# (.NET 8/Godot)**: Core engine logic, performance-critical simulation systems, game state management, and high-level AI orchestration.
- **Lua/Luau**: Dynamic event systems, high-frequency gameplay triggers, UI interaction scripting, and moddable gameplay effects. This provides high flexibility for designers without requiring a full recompile.
- **F#**: Complex rule-based systems, policy logic, and deterministic decision trees. Already utilized for core simulation rules. Its functional nature is ideal for the game's deterministic campaign requirements.
- **Nim/Odin**: High-performance mathematical or graph utilities (as needed for performance bottlenecks).
- **Kotlin**: Allowed for auxiliary tools or non-BTA integration.
- **Rust**: **Allowed since 29 October 2026** (the author lifted the veto). Same rule as every
  other language: a measured reason, a managed reference implementation and exact-equivalence
  tests. The earlier ownership-kernel benchmark did not beat C#, so "it is native" is not an
  argument by itself.
- **Zig / Go / Python**: newcomer tracks in `LanguageKit` (gdzig, graphics.gd, py4godot), pinned
  and written, not yet executed on 4.8-beta2 — see `LanguageKit/reports/languages-2026-10-29.md`.
  Python in particular is upstream's own "demo" stage: no campaign authority there.

## Architectural Integration

1.  **C# (Foundation)**: Handles the backbone of the campaign (e.g., `Scripts/Simulation/`).
2.  **Luau (Moddability Layer)**: Registered via `WorkerModBackend`. Allows external scripts to define features and effects without touching core assemblies.
3.  **F# (Rule Logic)**: Used in the simulation layer to define complex state transformations, keeping logic pure and testable.

## Development Path

- Ongoing refinement of `WorkerModBackend` to support multiple language adapters seamlessly.
- Transitioning logic from monolithic C# components to dedicated F# rules or Lua triggers where appropriate for flexibility.
