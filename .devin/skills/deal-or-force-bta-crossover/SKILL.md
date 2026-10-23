---
name: deal-or-force-bta-crossover
description: >-
  The Deal or Force x Better Than Adventure live spherical-world bridge: what each side owns,
  how digging in BTA costs population and GDP in DoF, and how commissioned buildings become
  regional sites. Use when touching Tools/BtaBridge, mods/bta_bridge, CrossoverSurface,
  CrossoverWorks, TerrainDestruction or the GlobeCrossover* presentation files.
---

# Deal or Force x BTA crossover bridge

Ported into the project on 16 October 2026 from `~/.config/opencode/skills`, where it was the
only copy. Claims below were checked against `Tools/BtaBridge/README.md` and the source files;
anything not verified is marked.

## What the bridge is

A real-time link between **Deal or Force** (Godot 4.8, C#) and **Better Than Adventure 8.0.1**
(Fabric / Java 21). The two keep separate authority: DoF owns the strategic world, BTA owns
the block world the player is standing in.

## Where each side lives

| Side | Path |
| --- | --- |
| DoF simulation | `Scripts/Simulation/CrossoverSurface.cs`, `CrossoverWorks.cs`, `TerrainDestruction.cs` |
| DoF globe presentation | `Scripts/gfx/GlobeCrossoverSurface.cs`, `GlobeCrossoverVoxels.cs`, `GlobeCrossoverSites.cs`, `GlobeSky.cs` |
| Mod | `mods/bta_bridge` (`manifest.json`, `corecode`, `localization`, `bridge.local.json`) |
| Bridge tooling and the Java side | `Tools/BtaBridge` (`bta`, `flat`, `build-bta.ps1`, `docs`, `DEMO.md`) |

`Tools/BtaBridge` and `tmp_playtest/` must not be deleted — the project rules say so explicitly.

## The two directions of consequence

**BTA → DoF.** Ordinary digging removes one real block; its neighbours and the province
survive, and a hole appears in BTA's smooth shell. A new dry surface cell also causes local
damage in DoF to population, GDP, resources and regional condition (`CrossoverSurface.Stamp`).
Old pits act as the initial state on first connection, and re-digging the same cell does not
charge people twice. Digging a deep cave, removing water or plants, or placing a wall causes
no such loss.

`/dof destroy` is the separate, total path: aim at the land you want and the whole province is
destroyed, with population, GDP and divisions lost.

**DoF → BTA.** Building 24+ player blocks and commissioning a **Workshop**, **Hospital** or
**Supply hub** registers a regional site in DoF and provides field services in BTA. To use
them, stand within 12 blocks of the marker, keep the DoF link up and the building operating,
then open **Site services** in the compass dossier or run `/dof services`. The panel shows
cost and how much material is in the inventory.

The site markers are rendered by `GlobeCrossoverSites.cs` under a `Node3D` named
`BtaCommissionedSites`, hidden in flat-map mode.

## The world shape

Spherical Earth, 2048 blocks across, surface radius 1024, radial gravity. The Cheese Moon is a
smooth sphere 560 blocks across with an eight-block crust of two native materials — `Lunar
Cheese` for the first four levels, `Aged Lunar Cheese` below — and a cheese core. The blocks are
a building material with their own porous textures, soft sound and differing hardness, **not**
an edible item. Large pores are visible from orbit, fine texture up close. Camera range covers
the whole sphere, including looking straight up or down.

Trade reference: `Lunar Trader` exchanges 8 Lunar Cheese for 1 Controller.
