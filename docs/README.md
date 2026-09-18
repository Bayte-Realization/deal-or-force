# Deal or Force documentation

*Русская версия: [README_RU.md](README_RU.md)*

The documentation matches the Godot.NET version of the project as of 18 September 2026.

If this is your first time here, start with the tutorials. They are written as a walk
through, not as a reference: you sit down with them and do things rather than read them.

| Tutorial | For whom | How long |
|---|---|---|
| [TUTORIAL.md](TUTORIAL.md) — **your first campaign, hand held** | a player | a couple of hours, with the game running |
| [MODDING_QUICKSTART.md](MODDING_QUICKSTART.md) — **your first mod** | a modder | fifteen minutes |
| [CSHARP_GUIDE.md](CSHARP_GUIDE.md) — **C# through this game's code** | a programmer from scratch | an evening to read, a weekend for the exercises |

After that come the references, which you visit for a detail:

| Document | For whom | What is in it |
|---|---|---|
| [PLAYER_GUIDE.md](PLAYER_GUIDE.md) | a player | controls, economy, the army, war, peace, emission, network multiplayer |
| [NETWORK_MULTIPLAYER.md](NETWORK_MULTIPLAYER.md) | player/developer | LAN, direct IP, Radmin, UDP 7270 and the synchronisation model |
| [MODDING_GUIDE.md](MODDING_GUIDE.md) | a modder | the Paradox-like mod structure, JSON, GDScript, C#, load order |
| [ARCHITECTURE.md](ARCHITECTURE.md) | a developer | how the simulation is built, a map of the code, ticks, commands, performance |
| [BUILDING.md](BUILDING.md) | a developer | building, the Debug/Release configurations, release |
| [SCENARIOS_AND_WORKSHOP.md](SCENARIOS_AND_WORKSHOP.md) | a scenario author | the world format, the data editor, map labels |
| [MODDABLE_CAMPAIGN_UI.md](MODDABLE_CAMPAIGN_UI.md) | an interface modder | panel layout, tabs, formable nations, the lobby |
| [LAUNCHER.md](LAUNCHER.md) | a player | choosing mods and DLC, load order, logs |
| [DIAGNOSTICS.md](DIAGNOSTICS.md) | a tester | the five logs, how to read them and what to send after a crash |
| [FOG_OF_WAR.md](FOG_OF_WAR.md), [OFFENSIVE_PLANS.md](OFFENSIVE_PLANS.md) | player/modder | visibility and drawing offensives |
| [LOADING_SCREEN.md](LOADING_SCREEN.md), [INTERFACE_AUDIO.md](INTERFACE_AUDIO.md), [UI_REDESIGN.md](UI_REDESIGN.md) | a developer | the loading screen, the sounds, the first stage of the interface rework |
| [MIGRATION.md](MIGRATION.md) | the team | the state of the port from s&box and the technical decisions behind it |

## Which languages things are written in

Checked against the text of the files rather than their names — a name lies:
`PLAYER_GUIDE.md`, `BUILDING.md` and `ARCHITECTURE.md` were Russian under English names for a
long time.

The convention: **a bare name is English, `_RU` is Russian.**

Every document now exists in both languages, with two exceptions:

| Document | 🇬🇧 | 🇷🇺 |
|---|:--:|:--:|
| PLAYER_GUIDE, TUTORIAL, MODDING_GUIDE, MODDING_QUICKSTART,<br>CSHARP_GUIDE, ARCHITECTURE, BUILDING, GETTING_STARTED,<br>NETWORK_MULTIPLAYER, SCENARIOS_AND_WORKSHOP,<br>MODDABLE_CAMPAIGN_UI, LAUNCHER, DIAGNOSTICS, FOG_OF_WAR,<br>OFFENSIVE_PLANS, LOADING_SCREEN, INTERFACE_AUDIO, UI_REDESIGN | ✅ | ✅ |
| MIGRATION — the log of the port from s&box | ✅ | — |
| `*_2026-09-*.md` — a work journal rather than documentation | — | ✅ |

To check what language a file is really in — count letters rather than trusting the name:

```bash
cd docs && for f in *.md; do
  ru=$(head -c 4000 "$f" | grep -o '[а-яё]' | wc -l)
  en=$(head -c 4000 "$f" | grep -o '[a-z]' | wc -l)
  [ "$ru" -gt "$en" ] && echo "RU  $f" || echo "EN  $f"
done | sort
```

## Tools

- `Tools/MapDataCompiler` — builds the raster map offline.
- `Tools/TerrainCompiler` — builds the terrain raster (Python).
- `Tools/SimBench` — runs a campaign without Godot and checks the simulation's invariants:
  `dotnet run --project Tools/SimBench/SimBench.csproj -c Release`.
- `Tools/TerrainTests` — terrain and front regressions.

The bench is the main way to find out what changed: it prints the world's commodity balance,
what became of the world over two years with no player, and it checks determinism, the
isolation of campaigns from each other, and that the mod examples still name things that
exist.

There are two more tools inside the game itself: the developer console (`~`, the `help`
command) and the layout audit — the `layout` command, which names every caption on screen
that was not given room for its own text. In a debug build the same audit runs by itself once
a second and writes its findings to the log.

## Mod templates

They are in `mods/_templates`; folders starting with `_` are deliberately not loaded by the
game.

| Template | What it shows |
|---|---|
| `balance_and_script` | A first mod: defines, a country, a commodity, GDScript. |
| `production_chains` | The economy: commodities, buildings, chains, the trade ceiling. |
| `statecraft` | Diplomacy and the AI: the price of war, reputation, coalitions, opinions. |
| `central_bank` | The monetary model: inflation, the key rate, the price of cheap money. |
| `trade_instructions` | Trade controls and how they explain themselves to a player. |
| `border_redraw` | Who owns what. |
| `map_data_override` | The shape of the world, and rebuilding the map. |
| `interface_reskin` | Colours, layout, localisation. |
| `csharp_mod` | A new mechanic in C#. |
