# Third-party media and data notices

This is a provenance register, not a new restriction on Deal or Force's original assets.
The author permits unrestricted reuse of the project's own artwork and audio as stated in
[README.md](../README.md#licence). Third-party works retain their own terms.

## Flags

[game/gfx/CREDITS.md](../game/gfx/CREDITS.md) identifies sources, authors and licences
for the credited Wikimedia drawings. Public-domain and CC0 drawings may be reused freely;
CC BY requires attribution; CC BY-SA also requires the applicable share-alike terms for
adaptations. Keep the original credits and licence links with redistributed files.

The existing register contains 632 credited variants. The earlier inventory recorded
263 national drawings not individually catalogued, 89 cultural copies of national flags,
and 21 variants with incomplete provenance. These historical inventory figures are not a
fresh legal clearance of every current file. Check a file's source before reusing an
unverified third-party drawing. Original flags generated for this project have separate
source notes under [art_sources](art_sources/).

## Map and population data

| Project data | Source and notices |
| --- | --- |
| `game/map_data/terrain.png`, `terrain_provinces.csv` | ESA GlobCover 2009, [PANGAEA dataset](https://doi.org/10.1594/PANGAEA.787668). © ESA 2010 and UCLouvain; retain credit. Classified by `Tools/TerrainCompiler`. |
| `heightmap.png`, `bathymetry.png`, `relief_color.png` | NASA Earth Observatory [Blue Marble topography and bathymetry](https://science.nasa.gov/earth/earth-observatory/blue-marble-next-generation/topography-bathymetry-maps/). Imagery by Jesse Allen using GEBCO data produced by the British Oceanographic Data Centre. Retain these source notices. |
| Rivers, lakes, hydrography, city catalogue and associated names | [Natural Earth](https://www.naturalearthdata.com/), public domain. |
| `game/history/states/2024/20_population.json` | Derived from Natural Earth's populated places by `Tools/RegionPopulation`. Public domain; figures are a game model, not census records. |

Current raster provenance and hashes are recorded in `game/map_data/terrain_provenance.json`.
The old WorldClim elevation input (non-commercial terms) was replaced; do not reintroduce
that input into redistributed builds without checking its terms.

## Fonts, libraries, editor plugins

Preserve each bundled font/library's copyright and licence notices. Excluding `addons/`
from Git does not change the licences of locally installed plugins. Godot is MIT-licensed.
The optional native Luau adapter has its own notices in `game/extensions/luau/LICENSE.txt`;
release builds omit the experimental native adapter unless explicitly requested.

## Packaging

Ship this register, `game/gfx/CREDITS.md`, `LICENSE` and `LICENSE-EXCEPTIONS.md` with the
game. An absent asset source is a documentation gap, not permission to claim ownership.
