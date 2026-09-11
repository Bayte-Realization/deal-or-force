# Map data override template

1. Copy this directory out of `_templates` and give the folder and manifest a unique ID.
2. Create `map_data/` and copy only the layers you want to edit from `game/map_data/`.
3. A province edit must include both `provinces.png` and `definition.csv`.
4. `rivers.png` and `lakes.png` may be overridden independently.
5. Keep images at 5760×2880, RGB, lossless, non-interlaced and non-indexed.
6. Use the exact colours documented in `game/map_data/RASTER_PALETTE.md`.

The base game remains untouched. A later `load_order` wins, and disabling the mod restores
the base rasters on the next campaign.
