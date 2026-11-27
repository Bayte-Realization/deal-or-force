# Example Custom Map Pack

This template shows how to ship a playable custom map pack in a Deal or Force mod.

## Structure

- `manifest.json` — standard mod descriptor (keep `"enabled": false` for templates; set `true` or toggle in Launcher when testing).
- `maps/example_custom_map/map.json` — map pack manifest read by `MapCatalog` and validated by `ModValidator` / `ContentWorkbench`.
- `localization/english/00_custom_map_pack.json` & `localization/russian/00_custom_map_pack.json` — balanced EN/RU display localization stubs.

## Supported `map.json` fields

- `id` (string, `^[a-z0-9_]{2,48}$`)
- `name`, `description`, `author` (strings)
- `cells_per_province`, `disputed_cells_per_province` (integers, `4..512`)
- `siberia_multiplier`, `polar_multiplier` (floats, `0.25..8.0`)
- `inherit_base_hydrology` / `inherits_base_hydrology` (boolean; keep `true` unless shipping full custom river/sea hydrology data)
- Optional custom file overrides inside `maps/<id>/` (`provinces`, `definition`, `ownership`) when replacing map geometry or ownership.

## Validation

```powershell
powershell -ExecutionPolicy Bypass -File Tools\validate-mod.ps1 -ModPath mods\_templates\custom_map_pack -Strict
```
