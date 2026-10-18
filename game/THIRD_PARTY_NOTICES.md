# Third-party notices

Everything in Deal or Force that its makers made - the code, the data, the artwork, the
music and the sounds - is under the GNU General Public License v3.0 or later (`LICENSE`,
with the additional permissions in `LICENSE-EXCEPTIONS.md`): take it, change it, use it,
pass it on. What came from elsewhere keeps the terms it came with, and is listed here.

## YamlDotNet 18.1.0

YAML data parsing uses YamlDotNet. Upstream:
https://github.com/aaubry/YamlDotNet/tree/v18.1.0

MIT license, reproduced from upstream LICENSE.txt:

Copyright (c) 2008, 2009, 2010, 2011, 2012, 2013, 2014 Antoine Aubry and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## FSharp.Core 10.1.402

The production pure-rules library uses FSharp.Core in the game's existing .NET host.
Package: https://www.nuget.org/packages/FSharp.Core/10.1.402
Upstream: https://github.com/dotnet/fsharp
The package contains code from the F# Software Foundation.

MIT license, reproduced from upstream License.txt:

Copyright (c) Microsoft Corporation.
All rights reserved.
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Flags

`game/gfx/flags/**` comes from Wikimedia Commons. Every file is listed in
[`gfx/CREDITS.md`](gfx/CREDITS.md) with its source, its licence and its author.
As of writing, 632 flags:

| Licence | Files | What it means here |
|---|---:|---|
| Public domain | 457 | no conditions |
| CC0 | 41 | no conditions |
| CC BY 3.0 | 3 | attribution required |
| CC BY 4.0 | 3 | attribution required |
| CC BY-SA 2.5 | 16 | attribution **and** share-alike |
| CC BY-SA 3.0 | 58 | attribution **and** share-alike |
| CC BY-SA 4.0 | 54 | attribution **and** share-alike |

The 128 share-alike files keep their own licence: share-alike is a copyleft condition, an
adapted file stays under the same licence, and resampling one to the game's flag height is
an adaptation. `CREDITS.md` is what keeps that honest.

All of these permit commercial use, so none of them stands in the way of a paid release.
What they require is that the credits file ships with the game and stays accurate.

Not every flag is in that table yet, and it is better written down than discovered:

- the 263 national flags, `flag_<iso>.png`, were rendered from the public-domain SVGs
  Wikipedia serves for each country's flag when the flags were first added. They are
  public domain, but `CREDITS.md` does not list them file by file yet;
- the 89 people's flags in the second table of `CREDITS.md` are copies of a state flag and
  carry that flag's terms;
- 21 variants (`*_protectorate`, `*_at_war`, a few ideology and formable flags) have no
  source recorded. Until one is, treat them as unverified: replace or credit them before a
  commercial release.

## Map data

The rasters in `game/map_data/` are built from open data, and the builders are in `Tools/`:

| File | Source | Terms |
|---|---|---|
| `terrain.png`, `terrain_provinces.csv` | land cover from ESA GlobCover 2009 ([doi:10.1594/PANGAEA.787668](https://doi.org/10.1594/PANGAEA.787668)), relief from the row below, classified by `Tools/TerrainCompiler` | © ESA 2010 and UCLouvain; credit required |
| `heightmap.png`, `bathymetry.png`, `relief_color.png` | NASA Earth Observatory, [Blue Marble topography and bathymetry](https://science.nasa.gov/earth/earth-observatory/blue-marble-next-generation/topography-bathymetry-maps/): imagery by Jesse Allen, NASA Earth Observatory, using data from the General Bathymetric Chart of the Oceans (GEBCO) produced by the British Oceanographic Data Centre | NASA imagery may be reused, commercially included, with credit |
| `rivers.png`, `lakes.png`, `hydrography/` | [Natural Earth](https://www.naturalearthdata.com/) rivers and lakes | public domain |
| `cities/00_cities.json` and the `city.*` names in `localization/*/24_cities.json` | [Natural Earth](https://www.naturalearthdata.com/) populated places, read by `Tools/CityCompiler` | public domain |

The terrain once took its elevation from WorldClim 2.1, which is licensed CC BY-NC-SA 4.0 -
non-commercial - and could not have shipped in a game that is sold. It was replaced by the
NASA and GEBCO relief above in September 2026; `terrain_provenance.json` records which source
and which files (by SHA-256) the current rasters were built from.

`game/history/states/2024/20_population.json` is derived from
[Natural Earth](https://www.naturalearthdata.com/)'s populated places, which are public
domain: no conditions, credited here because it is right to say where the numbers came from.
The places file itself is not in the repository; `Tools/RegionPopulation` reads it to produce
the figures.

## Editor plugins

`addons/` is excluded from the repository. Each plugin there carries its own licence. Godot
itself is MIT, which the GPL accepts.
