# Deal or Force — third-party notices

Redistribution notices for the game's third-party media and data. The game's own
licence is in `LICENSE` and `LICENSE-EXCEPTIONS.md`.

This register is not a new restriction on Deal or Force's original assets.
The author permits unrestricted reuse of the project's own artwork and audio as
stated in [README.md](README.md#licence). Third-party works retain their own terms.

## Flags

[game/gfx/CREDITS.md](game/gfx/CREDITS.md) identifies sources, authors and licences
for the credited Wikimedia drawings. Public-domain and CC0 drawings may be reused freely;
CC BY requires attribution; CC BY-SA also requires the applicable share-alike terms for
adaptations. Keep the original credits and licence links with redistributed files.

The existing register contains 632 credited variants. The earlier inventory recorded
263 national drawings not individually catalogued, 89 cultural copies of national flags,
and 21 variants with incomplete provenance. These historical inventory figures are not a
fresh legal clearance of every current file. Check a file's source before reusing an
unverified third-party drawing.

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

## Fonts, libraries and editor plugins

Preserve each bundled font/library's copyright and licence notices. Excluding `addons/`
from Git does not change the licences of locally installed plugins. Godot is MIT-licensed.
The optional native Luau adapter has its own notices in `game/extensions/luau/LICENSE.txt`;
release builds omit the experimental native adapter unless explicitly requested.

## Development tools (not shipped with the game)

`Tools/SaveFormatBench` (save-format measurement, 17 November 2026) restores these NuGet
packages for the tool only. The game's own project does not reference them, so no build
of the game redistributes them. Each licence was read from the repository's `LICENSE`
file and the package's NuGet licence expression before the package was added.

| Package | Version | Licence | Copyright | Source |
| --- | --- | --- | --- | --- |
| MessagePack (MessagePack-CSharp) | 3.1.12 | MIT; its `LICENSE` also carries the BSD-2-Clause notice of lz4net and an Apache-2.0 notice for `BufferWriter.cs` | © 2017 Yoshifumi Kawai and contributors | [LICENSE](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/master/LICENSE), [nuget.org](https://www.nuget.org/packages/MessagePack/3.1.12) |
| MemoryPack | 1.22.0 | MIT | © 2022 Cysharp, Inc. | [LICENSE](https://github.com/Cysharp/MemoryPack/blob/main/LICENSE), [nuget.org](https://www.nuget.org/packages/MemoryPack/1.22.0) |
| K4os.Compression.LZ4 | 1.3.8 | MIT | © 2017 Milosz Krajewski | [LICENSE](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE), [nuget.org](https://www.nuget.org/packages/K4os.Compression.LZ4/1.3.8) |
| ZstdSharp.Port | 0.8.8 | MIT for the port. It is a C# port of Zstandard, whose upstream code is BSD-3-Clause OR GPL-2.0; if it is ever shipped, take it under the BSD-3-Clause option and ship that notice (© Meta Platforms, Inc.) too | © 2021 Oleg Stepanischev | [LICENSE](https://github.com/oleg-st/ZstdSharp/blob/master/LICENSE), [nuget.org](https://www.nuget.org/packages/ZstdSharp.Port/0.8.8) |

If any of them is later referenced by `DealOrForce.csproj`, move its row to the section
above and ship its licence text with the game.

## Packaging

Ship this register, `game/gfx/CREDITS.md`, `LICENSE` and `LICENSE-EXCEPTIONS.md` with the
game. An absent asset source is a documentation gap, not permission to claim ownership.
