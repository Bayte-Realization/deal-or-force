# What is covered by which licence

This repository holds two different kinds of thing, and they are **not** under the same
terms.

    Copyright (C) 2026 Bayte Realization

---

## Code — GPLv3 or later

Everything that is not listed as an asset below: `Scripts/`, `Tools/`, `Scenes/`, the
project files, and all of `game/` that is data rather than media — `common/`, `history/`,
`gui/`, `interface/`, `localization/`, `scenarios/`, `map_data/*.csv`, `map_data/*.json`.

Terms: **GNU General Public License v3.0 or later** ([`LICENSE`](LICENSE)), plus the
additional permissions in [`LICENSE-EXCEPTIONS.md`](LICENSE-EXCEPTIONS.md).

Take it, read it, change it, pass it on — and if you pass on a changed version, pass on its
source under the same licence.

## Assets — three different situations

### a) Ours, all rights reserved

Original artwork and audio made for this game:

- `game/gfx/globe/**`, `game/gfx/interface/**`, `game/gfx/terrain/**`
- `game/sound/**`, `game/music/**` - the soundtrack and the long-range forces' interface
  sounds were generated for this game with Creative Claw; each audio folder's README lists them
- the Deal or Force name, logo and brand marks

**These are not licensed under the GPL and are not free to redistribute.** All rights
reserved. They may be used to build and run this game, and for the ordinary purposes of
writing about it — screenshots, reviews, videos, press coverage — but not extracted and
reused in another project.

If you fork the code, the fork is yours to publish. The art is not.

### b) Third-party, under their own terms

`game/gfx/flags/**` comes from Wikimedia Commons. Every file is listed in
[`game/gfx/CREDITS.md`](game/gfx/CREDITS.md) with its source, its licence and its author.
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

The 128 share-alike files **cannot** be placed under section (a), now or ever. Share-alike
is a copyleft condition: an adapted file stays under the same licence, and resampling one to
the game's flag height is an adaptation. They stay as they are, and `CREDITS.md` is what
keeps that honest.

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

### Map data

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

### c) Third-party editor plugins

`addons/` is excluded from the repository. Each plugin there carries its own licence. Godot
itself is MIT, which the GPL accepts.

---

## Why the split exists

The GPL does not forbid selling anything — charge whatever you like. What it does is give
whoever receives the program the right to pass it on, including for nothing.

For code that is the point, and it is not a problem: this project is open because being
open is worth more to it than the code is.

Art is a different thing. Splitting the two is the ordinary arrangement for a commercial
game with open source, and it keeps both halves honest: the word *open-source* stays true,
and a complete playable copy still cannot be assembled and given away by somebody else.

---

## If you want to use something from here

- **Code** — go ahead, under the GPL. No permission needed.
- **A flag** — go to Wikimedia Commons and take it from the source named in `CREDITS.md`;
  it will be cleaner than taking the resampled copy.
- **Our art, the font, or the name** — ask. The answer may well be yes; it just is not
  automatic.

---

*This file states an ordinary and widely used arrangement. It is not legal advice; before a
paid release it is worth having a lawyer read it together with
[`LICENSE-EXCEPTIONS.md`](LICENSE-EXCEPTIONS.md).*
