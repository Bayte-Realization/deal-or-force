# Zig / gdzig

Pinned source: c1b0cc3c7befc4c2483624d078e1e650b07b2dfa (MIT).
The pinned build.zig.zon requires **Zig 0.17.0**, despite an older README
mentioning 0.16.0. Build output and upstream source must be on the same
drive for the relative local package dependency.

Use workbench/build-local.ps1 -Languages zig from the kit root, or pass
the explicit compiler/source/engine/output parameters to build.ps1.
The recipe supplies the exact Godot path and a temporary compiler PATH
because upstream bindgen invokes zig fmt by name. No persistent PATH changes.

The kit uses its own minimal RefCounted class, not upstream's visual demo.
Its probe calls weighted_sum with signed/zero, negative and 4294967297
inputs, then requires clean import and shutdown. Actual Beta2 execution
passed on Windows x64. Templates preserve user edits; generated output
has an owner marker and .gdignore.

The upstream API snapshot remains 4.7.2. This small Beta2 smoke test does
not certify every API, export, hot reload, other OSes or performance.
See [current evidence](../../reports/beta2-runtime-2026-10-29.md).

