# Raster data-map palette

All maps are lossless, non-interlaced 8-bit RGB PNG files at 5760×2880. Never save
them as JPEG or indexed/paletted PNG. The left and right edges wrap horizontally.

## provinces.png

- `#000000` is ocean/no province.
- Every land province uses one unique RGB colour.
- `definition.csv` maps that colour to a stable numeric province ID. The state and
  nation columns are exported diagnostics; political ownership still comes from the
  scenario layers, so conquest never rewrites this source image.

## rivers.png

- `#000000`: no river.
- `#1E5AA8`: minor river, movement multiplier and ordinary crossing penalty.
- `#1E8CD2`: river, stronger crossing penalty.
- `#2DD4FF`: major river, strongest crossing penalty.

Rivers do not remove land adjacency: armies cross them normally and receive the
terrain modifiers defined in `common/defines/military.json`.

## lakes.png

- `#000000`: no lake.
- `#3366FF`: freshwater lake. Nearby land provinces receive lake access and its
  supply effect; the political land raster remains authoritative for passability.
