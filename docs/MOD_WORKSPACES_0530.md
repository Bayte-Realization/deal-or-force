# Mod workspaces and behavior replacement — 0.5.3.0

The workshop header now exposes **Create / select mod** and the current destination.
Content, country, flag, layout and scenario editors share it. New packages are created
under `Documents/Bayte Realization/Deal or Force/mods/<id>` with a real manifest and
English/Russian localization folders. Existing folders are never overwritten.
Changing packages reopens the editor against that package's file baselines;
unsaved drafts require confirmation. New packages are disabled until explicitly
enabled in the launcher for a new campaign. Selecting a package does not execute its code.

Data overlays replace existing catalogue IDs; JSON and YAML use the same loaders.
Use `common/scripted_triggers`, `common/scripted_effects` and `use` references to
compose richer content without duplicating base files. `gui/`, `interface/` and
`gfx/` override supported resource IDs.

Compiled C#/F# mods use `corecode_assembly` and `IGlobalityMod`. They are trusted
.NET code, not a sandbox. A loose `Scripts/Simulation/EconomyCore.cs`, or a class
with the same namespace/name in another assembly, **does not replace the host's
compiled class**. Arbitrary runtime patching of every game method is not implemented
by this update. For supported subsystem replacement, first check that the system
honors `Features` / `FeatureSystems` gates; implement the replacement through the
existing lifecycle. Do not run a second copy of the vanilla system beside it.

The loader reports misplaced source-replacement attempts and assemblies with no
concrete entry point. Entry points have deterministic type-name ordering. `StopCode`
detaches plugins between campaigns; implement `IDisposable` to release your own
subscriptions/resources. Determinism and multiplayer compatibility require separate tests.

`minimum_component_share` applies to **each** required founding tag (default `0.5`,
explicit `0` retains the old aggregate-only rule). Greater Italy and Reunified China
require `1.0`. Occupation alone is not ownership, and peaceful accession still needs
consent. Dynamic restorations retain their partial-restoration rule.

`common/flags` accepts `prefer_country_base` (default `true`): national artwork remains
stable across cabinet changes. Custom flags, formable identities and restored identities
still take precedence. Explicit `false` enables the older qualifier-first political
variants. Movement banners retain their separate policy. Reunified China selects
CHN/TWN artwork by founder rather than borrowing a generic ideology flag.

See the [modding guide](MODDING_GUIDE.md) for the remaining API contracts.

The old loose `mods/luau_events` was not an installed mod: it had no manifest or
campaign caller. Its pure module reference is now with the native Luau template,
`mods/_templates/luau_engine/corecode/modules/war_damage.mod.lua`. Duplicate function
definitions were removed without changing the effective three event proposals.
This organization change does not register those proposals as campaign events;
the template README describes the supported JSON/YAML event alternative.
