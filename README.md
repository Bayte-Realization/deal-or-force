[English](README.md) · [Русский](README.ru.md)

# Deal or Force

A grand strategy game about the whole world, on Godot 4.8 (.NET build) and .NET 10. Version **0.6.3.0** — **EntertAlpha 0.6.3.0**.

[Changelog](https://github.com/Bayte-Realization/deal-or-force/blob/EntertAlpha/docs/CHANGELOG_0.6.3.0_EN.md)

> **[LanguageKit](LanguageKit/README.md)** — the Godot 4.8 Beta1 language kit: source integrations
> for Luau, F#, Nim, Odin, Rust and Kotlin, with build recipes, probes and a workbench
> ([guide](LanguageKit/GUIDE.md)).

World rendering scripts now live in
[`Scripts/gfx`](Scripts/gfx/README.md), separately from UI and simulation rules.

It opens on 1 January 2024 with hundreds of countries and movements, thousands of regions
and 35 295 provinces. Authored history sets the starting conditions; simulation rules and
events then shape the campaign. Prices come out of what is produced and shipped. A war moves
a front province by province and has to be supplied along it. A country that governs badly
loses regions to people who would rather leave. You can play any country on the map.

## What is in it

The world is one simulation rather than a set of screens. Twenty goods are made by 39
recipes and carried along 2 148 trade routes, so an embargo takes away goods rather than a
number. Peace is written at a conference: every province you mean to keep is priced in war
score, and the other side can refuse and tell you why.

Countries answer to each other in more than one way. Alliances are proposed and can be
turned down. A frightened country goes and asks somebody larger for a guarantee. Five kinds
of dependency — protectorate, puppet, satellite, dominion, client — each take their own
tribute, bring their own obligations in war, and drift towards wanting out at their own
pace. NATO, the CSTO and a dozen standing guarantees are on the map from the first morning,
because they were.

The wars of 2024 that were not between states are on the map from the first day: the Rapid
Support Forces in Darfur and Khartoum, the Houthis in Sanaa, Idlib, north-eastern Syria, the
east of Libya, Myanmar's resistance, al-Shabaab, the M23 - each a country with its own ground
and fighters, in the war, truce or peace it had that morning. A civil war is fought by its
two sides, and their friends arm them rather than join them; a faction that takes the capital
takes the state. Between the wars every pair of countries carries a tension that rises
through crisis to armed clashes, and a border that keeps shooting can become a few weeks'
border war ending in a ceasefire. Unstable governments face military coups.

Regions secede when nobody is governing them. Governments fall and are replaced. Armies grow
when a country has reason to arm and are sent home when it does not, instead of staying the
size they started. A threatened region raises its own militia, in its largest town and under
that town's name.

A war is fought from the air as well as on the ground. Artillery, rocket batteries, aircraft,
drones and air defence are formations of their own, based in regions and sent on missions from
a panel beside the map; they reach into the battles within their range and pay for every day
they fly over a defended front. Seventy-one technologies spread from the countries that know
them to their neighbours and partners.

The map reads like an atlas: five and a half thousand cities that never print over each other,
relief lit from the north-west, a sea with shelves and a floor, and coasts and borders drawn as
the lines they were digitised from rather than as the texel staircase they are stored as.

Saves are text a person can open and edit, and a campaign started from a chosen seed rebuilds
the same world.

And everyone flies the right flag: about a thousand of them, chosen by what a country has become — its
people, its ideology, its form of government, whether it answers to somebody else — and
declared by nothing but the file name.

## Modding

Nearly everything is data. `game/common` holds goods, buildings, recipes, ideologies,
governments, decisions, modifiers and the numbers that balance them; `game/history` holds
what the world was on day one; `game/localization` holds every string the game shows, in
English and in Russian, complete.

A mod puts its own files beside the base game's and wins by load order, so changing three
numbers never means forking a file:

```
mods/my_mod/
  mod.json
  common/defines/economy.json     only the keys it names
  events/my_events.json
  localization/russian.json
```

Effects are data too, not code:

```json
"effects": { "treasury_percent": -0.06, "stability": 0.018, "state.radicalism": -0.01 }
```

A bare number adds, `_percent` scales, `_mult` multiplies, `_set` assigns, and the `state.`
prefix applies the effect to every region of the country. `mods/_templates` has worked
examples at all three levels: JSON, GDScript, compiled C#. The rest is in
[MODDING_GUIDE](docs/MODDING_GUIDE.md).

## Language development kit

The project retains source integrations for **Luau, F#, Nim, Odin, Rust and Kotlin**.
See the [kit and build recipes](LanguageKit/README.md) and
[support levels / migration policy](docs/LANGUAGE_BACKENDS_RU.md). All six ran together
in the Godot 4.8 Beta1 .NET workbench; this does not certify every export or API.
The campaign remains C#. Rust is reserved for rare, measured hard cases; players and
ordinary data-mod authors do not need six toolchains.

## Building and running

**[docs/GETTING_STARTED.md](docs/GETTING_STARTED.md) is the full account**, written for
somebody who has never opened this project. The short version:

You need **Godot 4.8 in its .NET build** (4.8 dev7 or later) — the plain download will open
the project and find no scripts — and the **.NET SDK 10**.

```bash
git clone https://github.com/Bayte-Realization/deal-or-force.git
cd deal-or-force
dotnet build DealOrForce.csproj
```

Then open the folder in Godot and press **F5**. The game opens on `Scenes/MainMenu.tscn`,
which is a scene of its own and comes up immediately; the world is built when you ask for a
campaign, which takes ten to thirty seconds.


**Before trusting a change**, play a campaign without the engine and check it against about a
hundred invariants: that the economy adds up, that two identical worlds stay identical, that
a front has not quietly stopped being contested.

```bash
dotnet run -c Release --project Tools/SimBench/SimBench.csproj -- 730
```

To build a copy somebody else can double-click — this needs Godot's export templates
installed, see [docs/BUILDING.md](docs/BUILDING.md):

```powershell
powershell -ExecutionPolicy Bypass -File Tools\build.ps1
```

## If it will not start

The game watches its own start (`Scripts/Boot/boot_guard.gd`, which runs even when the C#
half does not). If a start never reaches the menu, or the menu comes up black, it moves to a
simpler renderer by itself and starts again: Direct3D 12, then Vulkan, then OpenGL, then OpenGL
through Direct3D 11 (ANGLE), which draws on nearly any graphics card of the last fifteen years.
If the game's code does not load - a build copied without its `data_DealOrForce_windows_x86_64`
folder, or one an antivirus has quarantined part of - it says so on screen rather than staying
black.

- The choice is kept in `%APPDATA%\Godot\app_userdata\Deal or Force\graphics_override.cfg`.
  Delete it to go back to automatic, or pick a renderer in *Settings → Display → Graphics
  renderer*.
- To start on one directly: `"Deal or Force.exe" --rendering-method gl_compatibility`.
- The log is `%APPDATA%\Godot\app_userdata\Deal or Force\logs\godot.log`.
- `"Deal or Force.exe" -- --boot-guard-pretend-black` walks the whole chain on a machine whose
  drivers all work, for testing.

## Documentation

Everything below exists in English and in Russian; a `_RU` suffix is the Russian one, and
[docs/README.md](docs/README.md) is the full index.

| | |
|---|---|
| [TUTORIAL](docs/TUTORIAL.md) | **your first campaign, hand held** |
| [MODDING_QUICKSTART](docs/MODDING_QUICKSTART.md) | **your first mod in fifteen minutes** |
| [GETTING_STARTED](docs/GETTING_STARTED.md) | how to run and build it |
| [PLAYER_GUIDE](docs/PLAYER_GUIDE.md) | how to play |
| [MODDING_GUIDE](docs/MODDING_GUIDE.md) | how to change it |
| [ARCHITECTURE](docs/ARCHITECTURE.md) | how it is built |
| [CSHARP_GUIDE](docs/CSHARP_GUIDE.md) | **C# from scratch, taught on this game's own code** |
| [NETWORK_MULTIPLAYER](docs/NETWORK_MULTIPLAYER.md) | LAN and direct IP |
| [CONFLICTS_AND_FACTIONS](docs/CONFLICTS_AND_FACTIONS.md) | tension, border wars, proxy wars, de facto powers, coups |
| [October 8 changes](docs/RELEASE_2026_10_08_RU.md) | verified changes and limitations (Russian) |
| [Development roadmap](docs/DEVELOPMENT_ROADMAP_2026_10_08_RU.md) | coherent redesign, remaining work and acceptance criteria (Russian) |
| [Campaign integrity](docs/CAMPAIGN_INTEGRITY_RU.md) | overflow cause, autosave protection, compression and compatibility (Russian) |
| [Sovereign finance](docs/SOVEREIGN_FINANCE_RU.md) | financial workspace, legacy overdraft conversion, arrears and bounded issuance (Russian) |
| [Institutions](docs/INTERNATIONAL_ORGANIZATIONS_RU.md) | organizational charters, fund, votes and defence (Russian) |
| [Territory and independence](docs/TERRITORY_AND_RELEASE_RU.md) | homeland titles, treaty releases and Bir Tawil (Russian) |
| [Households](docs/HOUSEHOLD_ECONOMY_RU.md) | employment, basic needs and demographic dynamics (Russian) |

## Known debts

- The map rasters ship twice: Godot imports all 319 PNGs into the `.pck`, and they are also
  laid down as ordinary files because part of the code reads them directly. Tens of
  megabytes nobody needs.
- The interface is built in code, though where every panel sits is declared in
  `game/gui/` and the palette in `game/interface/`, so a reskin needs no compiler.
- No host migration in multiplayer: losing the host ends the session.

## Licence

**GNU General Public License v3.0 or later** — full text in [`LICENSE`](LICENSE).

    Copyright (C) 2026 Bayte Realization

In practice: take it, read it, change it, pass it on — and if you pass on a changed version,
pass on its source under the same licence.

`addons/` is excluded from the repository and each plugin there carries its own licence.
Godot itself is MIT, which GPL accepts.

The flags in `game/gfx/flags` come from Wikimedia Commons. [`game/gfx/CREDITS.md`](game/gfx/CREDITS.md)
lists 632 of them file by file with their source, licence and author; the national flags, which
were rendered from the public-domain drawings Wikipedia uses, are not listed one by one yet, and
21 variants have no source recorded - [`game/THIRD_PARTY_NOTICES.md`](game/THIRD_PARTY_NOTICES.md) says which. A flag
as a symbol is usually public domain; a particular drawing of it need not be, which is why
the file exists.

The map is built from open data: land cover from ESA GlobCover, relief and the sea floor from
NASA Earth Observatory's topography and bathymetry (GEBCO data), rivers, lakes and cities from
Natural Earth. [`game/THIRD_PARTY_NOTICES.md`](game/THIRD_PARTY_NOTICES.md) gives each with its terms.

**Original artwork and audio are freely reusable.** Bayte Realization grants everyone
permission to use, copy, modify, distribute and sell the project's original artwork and
audio, including outside this game, without asking permission or paying royalties.
Attribution is appreciated, not required. This permission includes original interface
icons, textures, generated music/sounds and original flag illustrations. There is no
separate restrictive asset licence. The code's GPL terms are unchanged.

This grant covers only rights held by Bayte Realization. Third-party flags, fonts and map
inputs retain their own notices and licence conditions. Sources and unresolved provenance
are documented in [third-party media notices](docs/THIRD_PARTY_MEDIA.md) and
[flag credits](game/gfx/CREDITS.md). Reusing a logo does not imply endorsement by its author.

**Steam.** GPLv3 and the proprietary Steamworks SDK cannot both be satisfied at once, so an
additional permission under GPL §7 is granted for linking against it — see
[`LICENSE-EXCEPTIONS.md`](LICENSE-EXCEPTIONS.md). Nothing depends on it today: `godotsteam`
is local only and no line of the game talks to Steam. It is granted in advance because such
a permission needs every copyright holder's agreement, and that is easiest while there is
one.
