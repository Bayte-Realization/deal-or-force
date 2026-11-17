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

`Tools/KernelBench` (the data-layout and native-code benchmark of 17 November 2026) restores
these packages from nuget.org for the benchmark only. None of them is referenced by
`DealOrForce.csproj` or exported with the game. Licences were read from each package's
licence expression on nuget.org and the repository's `LICENSE` file on 17 November 2026.

| Package | Version | Licence | Source |
| --- | --- | --- | --- |
| Friflo.Engine.ECS | 3.6.0 | MIT | <https://github.com/friflo/Friflo.Engine.ECS/blob/main/LICENSE> |
| Friflo.Json.Fliox, Friflo.Json.Burst, Friflo.Json.Fliox.Annotation (pulled in by Friflo.Engine.ECS) | 1.0.4 | **LGPL-3.0-only** | <https://github.com/friflo/Friflo.Json.Fliox/blob/main/LICENSE> |
| Arch | 2.1.0 | Apache-2.0 | <https://github.com/genaray/Arch/blob/master/LICENSE.MD> |
| Arch.LowLevel | 1.1.5 | Apache-2.0 | nuget.org licence expression |
| ZeroAllocJobScheduler | 1.1.2 | Apache-2.0 | nuget.org licence expression |
| Collections.Pooled | 2.0.0-preview.27 | MIT | <https://github.com/jtmueller/Collections.Pooled/blob/master/LICENSE.txt> |
| CommunityToolkit.HighPerformance | 8.2.2 | MIT | nuget.org licence expression |
| Microsoft.Extensions.ObjectPool | 7.0.0 | MIT | nuget.org licence expression |
| System.Runtime.CompilerServices.Unsafe | 6.0.0 / 6.1.0 | MIT | nuget.org licence expression |

Friflo.Engine.ECS itself is MIT, but its three Friflo.Json dependencies are LGPL-3.0-only.
That is acceptable in a benchmark nobody receives; it is **not cleared for the game**, and in
particular not for an ahead-of-time compiled build (Android), where the library would be linked
into the binary. Do not add Friflo.Engine.ECS to the game without resolving that first.

The Rust kernel in `Tools/KernelBench/native` uses no crates. It was built with the official
Rust toolchain 1.100.0 (stable, minimal profile) installed by rustup 1.29.3 (`rustup-init.exe` from
<https://static.rust-lang.org/rustup/dist/x86_64-pc-windows-msvc/rustup-init.exe>,
SHA-256 38027d51d69a5d05493627a0634dbea52d6cd793c8f072d2373d3f4163eec7db, checked against the
published `.sha256`), into a git-ignored folder without changing PATH. Rust, rustup and the
standard library linked into the DLL are MIT OR Apache-2.0
(<https://github.com/rust-lang/rust/blob/master/COPYRIGHT>). The DLL is not shipped.

Благодарности: Ullrich Praetz (Friflo.Engine.ECS), genaray и участникам Arch, проекту Rust
и команде rustup, Joel Mueller (Collections.Pooled), команде .NET Community Toolkit.

## Packaging

Ship this register, `game/gfx/CREDITS.md`, `LICENSE` and `LICENSE-EXCEPTIONS.md` with the
game. An absent asset source is a documentation gap, not permission to claim ownership.
