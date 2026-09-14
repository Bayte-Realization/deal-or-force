[English](README.md) · [Русский](README.ru.md)

# Deal or Force

A grand strategy game about the whole world, in Godot 4.7 and .NET 8.

It opens on 1 January 2024 with 261 countries, 1 826 regions and 37 909 provinces, and
after that nothing is scripted. Prices come out of what is produced and shipped. A war moves
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

Regions secede when nobody is governing them. Governments fall and are replaced. Armies grow
when a country has reason to arm and are sent home when it does not, instead of staying the
size they started.

And everyone flies the right flag: 807 of them, chosen by what a country has become — its
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

## Building and running

You need **Godot 4.7+ (.NET)** and the **.NET SDK 8**.

```bash
dotnet build DealOrForce.csproj
```

Then open the folder in Godot and press Play — the main scene is `Scenes/Main.tscn`. To
build a copy somebody else can run:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\build.ps1
```

`Tools/SimBench` plays a campaign without the engine and checks it against about a hundred
invariants: that the economy adds up, that two identical worlds stay identical, that a front
has not quietly stopped being contested. It is the thing to run before trusting a change.

```bash
dotnet run --project Tools/SimBench/SimBench.csproj -- 730
```

### After a fresh clone

`addons/` is not in the repository — those are third-party editor plugins, about 1.6 GB, and
the game does not reference them anywhere. But `project.godot` still registers two autoloads
belonging to development tools (`fennara`, `godot_ai`), so Godot will report them missing.
Remove them in *Project → Project Settings → Autoload*; the game does not care.

## Documentation

| | |
|---|---|
| [PLAYER_GUIDE](docs/PLAYER_GUIDE.md) | how to play |
| [MODDING_GUIDE](docs/MODDING_GUIDE.md) | how to change it |
| [ARCHITECTURE](docs/ARCHITECTURE.md) | how it is built |
| [CSHARP_GUIDE](docs/CSHARP_GUIDE.md) | for a first change in C# |
| [NETWORK_MULTIPLAYER](docs/NETWORK_MULTIPLAYER.md) | LAN and direct IP |

## Known debts

- The map rasters ship twice: Godot imports all 319 PNGs into the `.pck`, and they are also
  laid down as ordinary files because part of the code reads them directly. Tens of
  megabytes nobody needs.
- The interface is built in code; there is no `gui/` folder a mod could restyle.
- No host migration in multiplayer: losing the host ends the session.

## Licence

**GNU General Public License v3.0 or later** — full text in [`LICENSE`](LICENSE).

    Copyright (C) 2026 Bayte Realization

In practice: take it, read it, change it, pass it on — and if you pass on a changed version,
pass on its source under the same licence.

`addons/` is excluded from the repository and each plugin there carries its own licence.
Godot itself is MIT, which GPL accepts.

The flags in `game/gfx/flags` come from Wikimedia Commons, and every one of them is credited
with its source, licence and author in [`game/gfx/CREDITS.md`](game/gfx/CREDITS.md). A flag
as a symbol is usually public domain; a particular drawing of it need not be, which is why
the file exists.

If this ever reaches Steam: GPLv3 and the Steamworks SDK sit badly together, and the usual
answer is an additional permission under §7 for linking against Steamworks. Nothing turns on
it today — `godotsteam` is local only and no line of the game talks to Steam.
