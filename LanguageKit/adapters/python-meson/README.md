# Godot Python: Meson candidate

This is the actual `touilleMan/godot-python` **godot4-meson** branch, pinned at
`4f585652a76d3161a5beca4b41559eeb940fcf1d`, not the older Godot 3 addon.
The branch's development manifest requires Python 3.14+.

`review.ps1` applies the retained MIT-source compatibility patch, dumps the API
from the selected local engine and runs eight regression tests. It never runs
pip, builds CPython, downloads another engine or modifies the user's game.
Pass `-SourceDirectory`, `-PythonExecutable`, `-GodotExecutable` and a fresh
`-OutputDirectory`. Fetch the source at `source.lock.json` first using
`Get-KitSource` from `../../kit-common.ps1`.

The patched parser accepted the exact Godot 4.8 Beta2 dump in float_32,
double_32, float_64 and double_64 layouts. This is **not** a native build,
working export or Python-runtime compatibility certificate. Exposing the new
global constants in generated user-facing bindings is still separate work.

See [the runtime report](../../reports/beta2-runtime-2026-10-29.md).
