---
name: deal-or-force-dev
description: Architecture, verification, C# simulation, UI skinning, and headless benchmark workflows for Deal or Force (Godot 4.8 .NET + .NET 10 grand strategy game). Use whenever writing, reviewing, or testing C# simulation systems, UI panels, or localization in Deal or Force.
allowed-tools:
  - read
  - grep
  - find_file_by_name
  - edit
  - write
  - exec
---

# Deal or Force — Development & Verification Workflow

## 1. Core Architectural Boundary
- **Engine-Neutral Simulation (`Scripts/Simulation/*.cs`)**: Zero dependency on `Godot.Node` or scene frames. Deterministic given a seed. All state changes go through `ICampaignCommand` in `Campaign.Commands`.
- **Presentation (`Scripts/DealOrForceGame.cs`, `Scripts/ui/*.cs`, `Scripts/gfx/*.cs`)**: Projects `Campaign` state onto the 3D/2D globe and UI panels. Never stores duplicate game state.
- **No Raw Color Literals in UI**: Always use `InterfaceSkin` / `Ui.Colour("token.name")` (`Scripts/UiTheme.cs`).
- **Localization**: Every player-visible string must exist in both `game/localization/english/` and `game/localization/russian/`.

## 2. Standard Verification Suite
Before claiming any feature or fix is complete:
1. **Build C# project**:
   `dotnet build DealOrForce.csproj`
2. **Run Formable & Integration Tests**:
   `dotnet run --project Tools/FormableTests/FormableTests.csproj`
   `dotnet run --project Tools/ArmyInteractionTests/ArmyInteractionTests.csproj`
3. **Run Headless Simulation Benchmark (`SimBench`)**:
   `dotnet run -c Release --project Tools/SimBench/SimBench.csproj -- 365`
4. **Check Campaign Fingerprint & Save Integrity**:
   Ensure new simulation state participates in `CampaignFingerprint` and `SaveSystem` serialization.
