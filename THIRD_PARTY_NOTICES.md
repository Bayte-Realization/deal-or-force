# Deal or Force — third-party notices

Everything in Deal or Force that its makers made — the code, the data, the artwork, the
music and the sounds — is under the GNU General Public License v3.0 or later (`LICENSE`,
with the additional permissions in `LICENSE-EXCEPTIONS.md`): take it, change it, use it,
pass it on. What came from elsewhere keeps the terms it came with, and is listed here
with gratitude to every author, maintainer, and contributor.

This register is not a new restriction on Deal or Force's original assets. The author
permits unrestricted reuse of the project's own artwork and audio as stated in
[README.md](README.md#licence). Third-party works retain their own terms.

---

## Engine, Physics & .NET Runtimes

### Godot Engine 4.8 (`Godot.NET.Sdk/4.8.0-beta.5`)
- **Authors**: Juan Linietsky, Ariel Manzur, and the Godot Engine contributors
- **Website**: https://godotengine.org · https://github.com/godotengine/godot
- **Licence**: MIT License (`Copyright (c) 2014-present Godot Engine contributors`, `Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur`)

### Jolt Physics / Godot Jolt (3D Physics Engine)
Configured in `project.godot` (`3d/physics_engine="Jolt Physics"` on PC).
- **Authors**: Jorrit Rouwe (Jolt Physics), Mikael Hermansson (`mihe`, Godot Jolt integration), and contributors
- **Upstream**: https://github.com/jrouwe/JoltPhysics · https://github.com/godot-jolt/godot-jolt
- **Licence**: MIT License (`Copyright (c) 2021 Jorrit Rouwe`, `Copyright (c) 2022-present Mikael Hermansson`)

### .NET 10, WPF (`Tools/Launcher`), `System.IO.Hashing` 11.0.0, `System.Numerics.Tensors` 11.0.0, `Microsoft.IO.RecyclableMemoryStream` 4.0.1
- **Authors**: Microsoft Corporation and .NET Foundation contributors
- **Websites**: https://dot.net · https://github.com/dotnet/dotnet · https://github.com/Microsoft/Microsoft.IO.RecyclableMemoryStream
- **Licence**: MIT License (`Copyright (c) .NET Foundation and Contributors / Microsoft Corporation`)

### F# & `FSharp.Core` 10.1.402
The production pure-rules library (`Scripts/Rules/DealOrForce.Rules.fsproj`) uses F# and `FSharp.Core` inside the game's .NET host.
- **Authors**: Don Syme, Microsoft Corporation, and the F# Software Foundation contributors
- **Package**: https://www.nuget.org/packages/FSharp.Core/10.1.402
- **Upstream**: https://github.com/dotnet/fsharp
- **Licence**: MIT License

```text
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
```

---

## Performance, Spatial, Data & Compression Libraries (Shipping Game & Launcher)

Every managed package compiled into `DealOrForce.csproj` (`Scripts/Simulation/StructuredData.Dependencies.props`) and `DealOrForce.Launcher.csproj` uses permissive MIT or Apache-2.0 terms with zero GPL/LGPL transitive dependencies.

| Package | Version | Authors / Creators | Upstream | Licence |
|---|---:|---|---|---|
| `ZLinq` | 1.6.1 | Yoshifumi Kawai (`neuecc`) / Cysharp, Inc. | https://github.com/Cysharp/ZLinq | MIT |
| `SimdLinq` | 1.3.2 | Yoshifumi Kawai (`neuecc`) / Cysharp, Inc. | https://github.com/Cysharp/SimdLinq | MIT |
| `CommunityToolkit.HighPerformance` | 8.4.2 | Sergio Pedri, Microsoft & .NET Foundation Contributors | https://github.com/CommunityToolkit/dotnet | MIT |
| `DotNext` | 6.13.0 | Roman Sakno (`sakno`), .NET Foundation & Contributors | https://dotnet.github.io/dotNext/ · https://github.com/dotnet/dotNext | MIT |
| `BitFaster.Caching` | 2.6.1 | Alex Peck | https://github.com/bitfaster/BitFaster.Caching | MIT |
| `ZeroAlloc.Collections` | 2.0.1 | Marcel Roozekrans | https://collections.zeroalloc.net/ · https://github.com/ZeroAlloc-Net/ZeroAlloc.Collections | MIT |
| `Equativ.RoaringBitmaps` | 0.1.6 | Equativ.RoaringBitmaps Contributors (Roaring Bitmap format by Daniel Lemire et al.) | https://github.com/equativ/Equativ.RoaringBitmaps | Apache-2.0 |
| `RBush` | 4.0.0 | Daniel Newman / Turning Code, LLC (`viceroypenguin`; based on JS RBush by Vladimir Agafonkin) | https://github.com/viceroypenguin/RBush | MIT |
| `K4os.Compression.LZ4` & `K4os.Compression.LZ4.Streams` | 1.3.8 | Milosz Krajewski (port of Yann Collet's LZ4) | https://github.com/MiloszKrajewski/K4os.Compression.LZ4 | MIT (LZ4: BSD-2-Clause) |
| `YamlDotNet` | 18.1.0 | Antoine Aubry and contributors | https://github.com/aaubry/YamlDotNet | MIT |
| `Ben.Demystifier` | 0.4.1 | Ben Adams and contributors | https://github.com/benaadams/Ben.Demystifier | Apache-2.0 |

### YamlDotNet 18.1.0
YAML data parsing uses YamlDotNet (https://github.com/aaubry/YamlDotNet/tree/v18.1.0).

MIT license, reproduced from upstream `LICENSE.txt`:

```text
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
```

### BitFaster.Caching 2.6.1
```text
Copyright (c) 2020-2026 Alex Peck

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
```

### Other MIT / Apache-2.0 NuGet Packages Listed Above
- `ZLinq` 1.6.1 & `SimdLinq` 1.3.2 — `Copyright © Cysharp, Inc.` (MIT)
- `CommunityToolkit.HighPerformance` 8.4.2 — `Copyright (c) .NET Foundation and Contributors` (MIT)
- `DotNext` 6.13.0 — `Copyright © .NET Foundation and Contributors` (MIT)
- `ZeroAlloc.Collections` 2.0.1 — `Copyright (c) Marcel Roozekrans` (MIT)
- `RBush` 4.0.0 — `Copyright © 2017-2024 Turning Code, LLC (and others)` (MIT)
- `K4os.Compression.LZ4` / `K4os.Compression.LZ4.Streams` 1.3.8 — `Copyright Milosz Krajewski` (MIT)
- `System.IO.Hashing` 11.0.0, `System.Numerics.Tensors` 11.0.0, `Microsoft.IO.RecyclableMemoryStream` 4.0.1 — `Copyright © Microsoft Corporation` (MIT)
- `Equativ.RoaringBitmaps` 0.1.6 — `Equativ.RoaringBitmaps Contributors` (Apache-2.0: https://github.com/equativ/Equativ.RoaringBitmaps/blob/main/LICENSE)
- `Ben.Demystifier` 0.4.1 — `Ben Adams` (Apache-2.0: https://licenses.nuget.org/Apache-2.0)

---

## Profiling Tools (Development Builds Only)

None of these are in a release build. The Tracy binding is compiled in only when a build is
made with `-p:DofTracy=true` (see `Scripts/Simulation/StructuredData.Dependencies.props` and
`docs/PROFILING_2026_11_17_RU.md`); a build made that way and handed to anyone must carry the
two notices below.

| Component | Version | Licence | Source | Where it is used |
| --- | --- | --- | --- | --- |
| `Tracy-CSharp` | 0.13.1 | MIT, © 2022 clibequilibrium | [nuget.org](https://www.nuget.org/packages/Tracy-CSharp/0.13.1), [GitHub](https://github.com/clibequilibrium/Tracy-CSharp) | `Tracy.dll` in profiling builds only |
| Tracy Profiler (`TracyClient.dll` & viewer) | 0.13.1 | BSD 3-Clause, © 2017-2026 Bartosz Taudul | [GitHub release v0.13.1](https://github.com/wolfpld/tracy/releases/tag/v0.13.1) | Native client in `-p:DofTracy=true` builds; viewer outside repo |
| `dotnet-trace`, `dotnet-counters` | 10.0.750501 | MIT, © .NET Foundation and Contributors | [nuget.org](https://www.nuget.org/packages/dotnet-trace), [GitHub](https://github.com/dotnet/diagnostics) | Global .NET diagnostic tools on developer machine; not shipped |

### Tracy-CSharp 0.13.1 (MIT)
```text
Copyright (c) 2022 clibequilibrium (https://github.com/clibequilibrium)

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
```

### Tracy Profiler 0.13.1 (BSD 3-Clause)
```text
Copyright (c) 2017-2026, Bartosz Taudul <wolf@nereid.pl>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
    * Redistributions of source code must retain the above copyright
      notice, this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above copyright
      notice, this list of conditions and the following disclaimer in the
      documentation and/or other materials provided with the distribution.
    * Neither the name of the <organization> nor the
      names of its contributors may be used to endorse or promote products
      derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO THE IMPLIED
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

---

## Development Tools & Benchmarks (Not Shipped with the Game)

### Static Code Audit (`Tools/CodeAudit`)
- `Microsoft.CodeAnalysis` & `Microsoft.CodeAnalysis.CSharp` (Roslyn, SDK-bundled, Microsoft & .NET Foundation, MIT).

### Data-Layout & Native Kernel Benchmark (`Tools/KernelBench`)
`Tools/KernelBench` restores these packages for benchmarking only; none are referenced by `DealOrForce.csproj` or shipped with the game:

| Package | Version | Authors | Licence | Source |
|---|---:|---|---|---|
| `Friflo.Engine.ECS` | 3.6.0 | Ullrich Praetz | MIT | https://github.com/friflo/Friflo.Engine.ECS/blob/main/LICENSE |
| `Friflo.Json.Fliox`, `Friflo.Json.Burst`, `Friflo.Json.Fliox.Annotation` (pulled by `Friflo.Engine.ECS`) | 1.0.4 | Ullrich Praetz | **LGPL-3.0-only** | https://github.com/friflo/Friflo.Json.Fliox/blob/main/LICENSE |
| `Arch` | 2.1.0 | `genaray` | Apache-2.0 | https://github.com/genaray/Arch/blob/master/LICENSE.MD |
| `Arch.LowLevel` | 1.1.5 | `genaray` | Apache-2.0 | https://github.com/genaray/Arch.Extended |
| `ZeroAllocJobScheduler` | 1.1.2 | `genaray` | Apache-2.0 | https://github.com/genaray/ZeroAllocJobScheduler |
| `Collections.Pooled` | 2.0.0-preview.27 | Joel Mueller, Dzmitry Lahoda | MIT | https://github.com/jtmueller/Collections.Pooled/blob/master/LICENSE.txt |
| `CommunityToolkit.HighPerformance` | 8.2.2 | Microsoft & .NET Foundation | MIT | https://github.com/CommunityToolkit/dotnet |
| `Microsoft.Extensions.ObjectPool` | 7.0.0 | Microsoft Corporation | MIT | https://github.com/dotnet/aspnetcore |
| `System.Runtime.CompilerServices.Unsafe` | 6.0.0 / 6.1.0 | Microsoft & .NET Foundation | MIT | https://github.com/dotnet/runtime |

`Friflo.Engine.ECS` itself is MIT, but its three `Friflo.Json` dependencies are LGPL-3.0-only — acceptable in a dev-only benchmark, **not cleared for the shipping game** (especially AOT builds). The optional Rust kernel in `Tools/KernelBench/native` uses Rust 1.100.0 / rustup 1.29.3 (`MIT OR Apache-2.0`, https://github.com/rust-lang/rust/blob/master/COPYRIGHT) and is not shipped.

### Save-Format Measurement (`Tools/SaveFormatBench`)
`Tools/SaveFormatBench` restores these NuGet packages for the tool only (never shipped by `DealOrForce.csproj`):

| Package | Version | Licence | Copyright | Source |
|---|---:|---|---|---|
| `MessagePack` (`MessagePack-CSharp`) | 3.1.12 | MIT (includes BSD-2-Clause `lz4net` and Apache-2.0 `BufferWriter.cs`) | © 2017 Yoshifumi Kawai (`neuecc`), Andrew Arnott (`aarnott`) & contributors | https://github.com/MessagePack-CSharp/MessagePack-CSharp |
| `MemoryPack` | 1.22.0 | MIT | © 2022 Cysharp, Inc. | https://github.com/Cysharp/MemoryPack |
| `K4os.Compression.LZ4` | 1.3.8 | MIT | © 2017 Milosz Krajewski | https://github.com/MiloszKrajewski/K4os.Compression.LZ4 |
| `ZstdSharp.Port` | 0.8.8 | MIT (C# port of Meta Zstandard: BSD-3-Clause OR GPL-2.0; if ever shipped, use BSD-3-Clause) | © 2021 Oleg Stepanischev (`oleg-st`) | https://github.com/oleg-st/ZstdSharp |

### Optional `LanguageKit/` Bindings & Legacy Luau Adapter
`LanguageKit/` is excluded from `DealOrForce.csproj` (`LanguageKit/README.md`, `LanguageKit/sources.lock.json`) and provides source recipes for optional Godot bindings:
- **Rust**: `godot-rust/gdext` (`MPL-2.0`)
- **Luau**: `Fumohouse/godot-luau-script` (`MIT`, `Copyright © 2022-present voided_etc & godot-luau-script contributors`; legacy notices in `game/extensions/luau/LICENSE.txt` and `COPYRIGHT.txt`)
- **Nim**: `godot-nim/gdext-nim` (`MIT`)
- **Odin**: `dresswithpockets/odin-godot` (`Apache-2.0`)
- **Kotlin**: `utopia-rise/godot-jvm` (`MIT`)
- **Lua**: `gilzoide/lua-gdextension` (`MIT`)
- **Godot Sandbox**: `libriscv/godot-sandbox` (`BSD-3-Clause`)
- **Zig**: `gdzig/gdzig` (`MIT`)
- **Go**: `quaadgras/graphics.gd` (`MIT`)
- **Python**: `niklas2902/py4godot` (`MIT`)

---

## Fonts

### Source Serif 4 (Regular, SemiBold, Bold)
Bundled unmodified in `game/fonts/` (`game/fonts/NOTICE.txt`).
- **Author**: Frank Grießhammer / Adobe Systems Incorporated
- **Copyright**: `Copyright 2014 - 2021 Adobe Systems Incorporated (http://www.adobe.com/), with Reserved Font Name "Source".`
- **Licence**: SIL Open Font License, Version 1.1 (`http://scripts.sil.org/OFL`)

```text
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting — in part or in whole — any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical writer
or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any Modified
Version, except to acknowledge the contribution(s) of the Copyright
Holder(s) and the Author(s) or with their explicit written permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created using
the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM OTHER
DEALINGS IN THE FONT SOFTWARE.
```

---

## Flags

`game/gfx/flags/**` comes from Wikimedia Commons. Every file is listed in
[game/gfx/CREDITS.md](game/gfx/CREDITS.md) with its source, its licence and its author.
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
an adaptation. Keep the original credits and licence links with redistributed files.

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

---

## Map and population data

The rasters in `game/map_data/` are built from open data, and the builders are in `Tools/`:

| File | Source | Terms |
|---|---|---|
| `terrain.png`, `terrain_provinces.csv` | land cover from ESA GlobCover 2009 ([doi:10.1594/PANGAEA.787668](https://doi.org/10.1594/PANGAEA.787668)), relief from the row below, classified by `Tools/TerrainCompiler` | © ESA 2010 and UCLouvain; credit required |
| `heightmap.png`, `bathymetry.png`, `relief_color.png` | NASA Earth Observatory, [Blue Marble topography and bathymetry](https://science.nasa.gov/earth/earth-observatory/blue-marble-next-generation/topography-bathymetry-maps/): imagery by Jesse Allen, NASA Earth Observatory, using data from the General Bathymetric Chart of the Oceans (GEBCO) produced by the British Oceanographic Data Centre | NASA imagery may be reused, commercially included, with credit |
| `rivers.png`, `lakes.png`, `hydrography/` | [Natural Earth](https://www.naturalearthdata.com/) rivers and lakes | public domain |
| `cities/00_cities.json` and the `city.*` names in `localization/*/24_cities.json` | [Natural Earth](https://www.naturalearthdata.com/) populated places, read by `Tools/CityCompiler` | public domain |

The terrain once took its elevation from WorldClim 2.1, which is licensed CC BY-NC-SA 4.0 —
non-commercial — and could not have shipped in a game that is sold. It was replaced by the
NASA and GEBCO relief above in September 2026; `terrain_provenance.json` records which source
and which files (by SHA-256) the current rasters were built from.

`game/history/states/2024/20_population.json` is derived from
[Natural Earth](https://www.naturalearthdata.com/)'s populated places, which are public
domain: no conditions, credited here because it is right to say where the numbers came from.
The places file itself is not in the repository; `Tools/RegionPopulation` reads it to produce
the figures.

---

## Editor plugins (`addons/`)

Preserve each bundled font/library's copyright and licence notices. Excluding `addons/`
from Git does not change the licences of locally installed plugins. Godot is MIT-licensed.
Local workspace editor plugins under `addons/` include `Fennara` (MIT), `LimboAI` (MIT, Serhii Snitsaruk & contributors), `GodotSteam` (MIT, GP Garcia, Chris Ridenour & contributors), `Dialogue Manager` (MIT, Nathan Hoad & contributors), `Ezcha Network` (MIT, Ezcha LLC), `Lit` (MIT, Shawn Deprey / Fading Lantern Games), `Scalable Vector Shapes 2D` (`curved_lines_2d`, MIT), `Script-IDE` (MIT, Marius Hanl), `Godot AI` (MIT), `GDQuest GDScript Formatter` (MIT), `Little Camera Preview` (`anthonyec.camera_preview`), `Godot Asset Placer`, `Kanban Tasks`, `Orca`, `@icons`, `Godot MCP Pro`, `Godot MCP Toolkit`, `GD-Sync`, and `Ziva Agent` (proprietary local editor plugin). Release builds omit editor plugins and the experimental native Luau adapter unless explicitly requested.

---

## Packaging

Ship this register, `game/gfx/CREDITS.md`, `LICENSE` and `LICENSE-EXCEPTIONS.md` with the
game. An absent asset source is a documentation gap, not permission to claim ownership.
