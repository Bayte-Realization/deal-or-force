# The modding guide

*Русская версия: [MODDING_GUIDE_RU.md](MODDING_GUIDE_RU.md)*

## Philosophy and folders

Modding is the game's public layer. The basic path resembles Paradox games: a mod is an
ordinary folder with a manifest and subject catalogues. Changing the balance needs no IDE, no
compiler and no engine sources.

The game looks for mods in two places:

- `res://mods` — a developer's mods beside the project;
- `user://mods` — mods a user installed, which survive a game update.

If the same `id` appears in both places, the `user://mods` version takes priority. Folders
whose name begins with `_` are considered templates and are not loaded.

## Worked examples

`mods/_templates` holds working examples. Folders starting with `_` are deliberately not
loaded by the game: copy the one you want out, give it a new `id` and set `enabled: true`.

| Template | What it shows |
|---|---|
| `balance_and_script` | A first mod: defines, a country, a commodity and a GDScript entry point. |
| `production_chains` | The economy: commodities, buildings, **production recipes** and the trade ceiling. |
| `statecraft` | Diplomacy and the AI: the price of war, reputation, coalitions, reasons for opinions. |
| `border_redraw` | Who owns what, without rebuilding the raster. |
| `map_data_override` | The shape of the world itself, and rebuilding the map. |
| `interface_reskin` | Colours, panel layout and localisation. |
| `central_bank` | The monetary model: inflation, the key rate and the price of cheap money. |
| `trade_instructions` | Trade controls and the sentences with which they explain themselves. |
| `csharp_mod` | A new mechanic in C#. |

Every key of every template is covered by the standing bench check `every mod template names
something the game actually has`, so an example that has fallen behind the code breaks the
build as loudly as the code itself would.

## A quick start with no programming

1. Copy `mods/_templates/balance_and_script` to `mods/my_first_mod`.
2. Change `id` and `name`, and set `enabled: true` in `manifest.json`.
3. Clear `gdscript_entry` if you do not need a script.
4. Change the JSON in `common` and start the game.
5. The main menu will show the number of loaded mods; the detail is written to Godot's Output
   with the `[mods]` prefix.

```text
my_first_mod/
├── manifest.json
├── common/
│   ├── defines/balance.json
│   ├── modifiers/spirits.json
│   └── countries/korea.json
├── events/my_events.json
├── localization/russian.json
└── scripts/main.gd
```

## manifest.json

```json
{
  "id": "my_first_mod",
  "name": "My First Mod",
  "version": "1.0.0",
  "supported_game_version": "0.1.*",
  "enabled": true,
  "load_order": 100,
  "dependencies": ["shared_library"],
  "gdscript_entry": "scripts/main.gd",
  "csharp_assembly": ""
}
```

- `id` — a permanent unique identifier; do not translate it and do not change it after
  publication.
- `load_order` — lower numbers load earlier. A later mod overrides a value that matches.
- `dependencies` — required `id`s; a dependency always loads first.
- `gdscript_entry` and `csharp_assembly` are optional.
- paths must be relative and cannot leave the mod's folder.

If a dependency is missing, the mod is not activated. Circular dependencies should be treated
as a composition error and removed.

## Defines: changing the rules

Every JSON in `common/defines` is an object. Names are insensitive to case, `_` and `-`, so
`SupplyTraceRange`, `supply_trace_range` and `supply-trace-range` are the same thing.

```json
{
  "seconds_per_day": 0.45,
  "supply_trace_range": 28,
  "occupation_daily_progress": 0.01,
  "defender_advantage": 1.45,
  "speed_factors": [1.0, 0.55, 0.30, 0.15, 0.06]
}
```

The full current list is in `Scripts/Simulation/Defines.cs`. An unknown name does not break
startup: the loader raises a warning and keeps the base value.

The game's own base military values are in `game/common/defines/military.json`. The file
really is loaded before a campaign is created and is copied into the desktop build. It
exposes combat width, the space one division takes on a front, the organisation-break
threshold, the speed of an unopposed capture, the density of the starting echelon, supply,
occupation and the military AI's parameters. A mod's `common/defines/*.json` is applied on
top of that base layer.

The base map's geometry uses 16-bit IDs and is read from `game/map_data/provinces.png`. Each
of the 38,013 polygons has a unique RGB colour mapped to an ID in `definition.csv`. A modded
map must not exceed 65,535 land provinces; otherwise startup stops with an explicit error
rather than corrupting controllers and the save. The bundled `Tools/MapDataCompiler` is a
separate offline tool for map authors: its output is ordinary PNG/CSV that can be edited
further by hand. A running game draws nothing procedurally and renumbers nothing; without a
complete valid pair of rasters, a campaign raises a load error.

Small islands lost in the source political raster can be declared in `region_overrides.json`
through `seed_points`. Each entry has the form `[longitude, latitude, radius_pixels]`; the
compiler adds that patch of land to the administrative and tactical rasters without painting
over somebody else's existing territory. That is how the Lakshadweep, Andaman and Nicobar,
and Maldive chains are declared in the base map.

The historical active strength of armies at the start date is in
`game/history/units/2024/active_personnel.json`. The key is a stable three-letter country
tag, the value the number of serving personnel. The simulation turns that into divisions and
field armies, after which `StrategicDeploymentPlanner` lays them out along the live graph of
fronts, foreign borders and the capital reserve. That separates historical data from the
algorithm: an alternative scenario changes JSON and the political raster, not C# coordinates.

## Unit types

`game/common/units/unit_types.json` is the catalogue of formation types actually in use. The
stable IDs of the starting version are `line_infantry`, `motorized_corps` and
`armored_corps`.

```json
{
  "mountain_corps": {
    "name": "Mountain corps",
    "counter_symbol": "infantry",
    "attack": 1.02,
    "defense": 1.18,
    "speed": 0.90,
    "combat_width": 1.65,
    "supply_use": 0.88
  }
}
```

`attack` and `defense` change the effective strength of the corresponding side of a battle;
`speed` is the pace of crossing graph edges; `combat_width` is the space a division takes in
a battle; `supply_use` is the load on a logistics node; `counter_symbol` chooses the
conventional mark on the map. Do not change an ID after publishing a scenario: an army stores
exactly that.

## Countries: changing countries

Files in `common/countries` contain an object keyed by country tags:

```json
{
  "KOR": {
    "name": "Republic of Korea",
    "color": "#4d8fd1",
    "capital_name": "Seoul",
    "population": 52000000,
    "treasury": 190000000000.0,
    "gdp": 1800000000000.0,
    "military_spending": 0.065,
    "stability": 0.78,
    "war_support": 0.62,
    "manpower": 600000.0
  }
}
```

`common/countries` changes the parameters of registered countries, while a full scenario
layer can add a country through the base country/history registry and a territorial rule with
`source_nation`. That is how Transnistria (`PMR`) was added: a stable tag, a history, the
capital Tiraspol, a colour, armed forces and an authoritative territory carved out of Moldova
before the provinces are generated. Arbitrarily replacing the source 16-bit rasters still
requires a separate scenario package and respecting the ID limit.

## States: renaming regions

`common/states` is the same thing as `common/countries`, one level down: the 1,826 provinces,
oblasts, departments and prefectures of the world. Until recently their names were compiled
into the game and a mod could not touch a single one.

```json
{
  "it_to": { "name": "Augusta Taurinorum", "kind": "Province" },
  "fr_57": { "name": "Mediomatrici", "kind": "Civitas" }
}
```

The key is the **region's identifier**, not its code. Codes are not unique: nine of them are
carried by more than one region (five Madagascan provinces hide under `MG-F`), so a file
keyed by code would rename the wrong region. The identifiers are listed in
`game/common/states/00_states.json` — open it and find the name you want.

`name` is what the region is called; `kind` is the word the interface uses for the type of
unit (Province, Oblast, Department, Canton — whatever suits your world).

The rules are the same as for countries: every file in the folder is read in filename order,
later ones win by key, and a mod names only what it changes.
`game/common/states/00_states.json` is the base game's file; call yours `50_...json` and the
other 1,822 regions are inherited.

`name` is also a localisation key. A mod that has invented its own region names puts a
`localization/<language>/50_....json` beside it with the translation — otherwise the name
shows exactly as written.

A worked example is `mods/_templates/rename_the_world`: six countries, four regions and a
Russian localisation for them.

## GDScript mods

GDScript is convenient for events, periodic effects and prototyping. The entry point inherits
from `RefCounted` and may implement three optional callbacks:

```gdscript
extends RefCounted

func on_load(api) -> void:
	api.log("Mod loaded")

func on_campaign_created(api) -> void:
	var country := api.get_nation("KOR")
	api.log(country.get("name", "unknown"))

func on_day_advanced(api) -> void:
	if api.get_day() % 30 == 0:
		api.set_nation_number("KOR", "stability", 0.8)
```

The available methods are `get_day()`, `get_date()`, `get_nation(tag)`,
`set_nation_number(tag, key, value)`, `get_define(name, fallback)`, `set_define(name, value)`
and `log(text)`.

The GDScript API is deliberately narrower than the internal C# one: that reduces the chance
of breaking a save or determinism. New safe actions should be added to `ModApi` rather than
exposing the whole game scene.

## Goods: modding the market

JSON in `common/goods` overrides the public commodity definitions without a script:

```json
{
  "Food": {
    "name": "Staple food",
    "icon": "●",
    "base_price": 0.012,
    "essential_weight": 1.0,
    "description": "Basic household food supply."
  },
  "IndustrialMaterials": {
    "base_price": 0.040,
    "essential_weight": 0.65
  }
}
```

The stable IDs of the twenty commodities:

**The first tier** — `Food`, `ConsumerGoods`, `IndustrialMaterials`, `Energy`, `Arms`,
`Coal`, `Oil`, `IronOre`, `RareEarths`, `Machinery`.

**The second tier**, added for the sake of the chains — `NaturalGas`, `Timber`, `Bauxite`,
`Uranium`, `Aluminium`, `Chemicals`, `Fertiliser`, `Electronics`, `Textiles`,
`Pharmaceuticals`.

`base_price` is in billions of dollars per market unit; `essential_weight` decides how much a
complete shortage contributes to a country's overall shortage index, and through that to GDP,
the standard of living and radicalism.

The second tier is **links, not objects**. Bauxite is useless without a smelter and cheap
electricity; electronics want rare earths, which eight countries have; a modern harvest is
made of natural gas, and a country cut off from fertiliser loses about a quarter of it. The
chain reads **last** week's calculation, so a broken supply slows production rather than
stopping it, and does so with a delay — exactly as an embargo does in life.

### A twenty-first commodity

This used to say that a mod could not add a new commodity. Now it can.

Commodities and buildings are enumerations in code, and that sounded like a hard limit, but
it was not: an enumeration in C# is a number, some of whose values have names, and
`(Commodity)20` is a perfectly legal value — a dictionary key, a field, a number in a save.
What got in the way was not the enumeration but the fact that every road into the catalogue
went through `Enum.TryParse`, which only knows the compiled names. The catalogues now keep
their own name tables and hand out the next free number for a name they have not seen.

Declaring one is no different from overriding one — the same file, the same place. The
difference is that the name does not exist yet:

```json
// my_mod/common/goods/50_alloys.json
{
  "titanium": {
    "name": "Titanium",
    "icon": "◈",
    "base_price": 0.132,
    "essential_weight": 0.28,
    "description": "Airframes, hulls and things that must not be heavy."
  }
}
```

```json
// my_mod/common/buildings/50_works.json
{
  "titanium_mill": {
    "name": "Titanium mill",
    "cost": 3.4,
    "days": 320,
    "gdp_gain": 1.8,
    "capacity_gain": 0.07,
    "description": "Sponge and ingot out of ore, electricity and a great deal of patience."
  }
}
```

A new commodity needs **a name and a base price**, a new building **a name, a cost and a
duration**. Without those it is a typo, and rejecting typos is half a catalogue's job. The
rest is optional and takes sensible values.

After that a new commodity is no different from the twenty old ones: it gets a quote on every
country's market, it is traded, it appears in the ledger and it takes part in
`common/production` recipes — which is where you say what it is made of:

```json
// my_mod/common/production/50_titanium.json
{
  "titanium_milling": {
    "good": "titanium",
    "output": { "titanium_mill": 2.4, "deposit:IronOre*extraction_efficiency": 0.2 },
    "needs": { "Energy": 0.55 },
    "demand": { "CivilianIndustry": 0.3, "ArmsIndustry": 0.5 }
  }
}
```

A building with no recipe raises GDP and provides jobs — and nothing else. That is a
legitimate thing to want, and it is also the first thing to check when a mod's new factories
"do nothing".

**About saves.** A modded commodity's number is handed out in the order the mods are read, so
the same game opened with the same mods in a different order would have found aluminium where
fertiliser used to be — silently, because a number is a number. So a save carries a
"name → number" table with it, and on load the numbers are recomputed from the names. Saves
made before that table existed read as before: the numbers of the twenty and the seventeen
built-in entries never changed. A name the catalogue no longer has (the mod was removed) stays
on its number and breaks nothing else.

All of which is verified on the bench: `a mod can add a good and a building the game never
had` declares a commodity and a building that exist nowhere in the game, gives the building a
recipe, builds it, confirms production started, saves the game, **shifts the commodity's
number** — exactly as a different mod order would — and requires that after loading the
commodity is found by name with the same figures.

## Production: what things are made of

`common/production` is the file that decides who needs whom, and therefore how the game
plays. It used to be a hundred and thirty lines of arithmetic inside `MarketCore.cs`, and a
mod could not reach it: you could rename aluminium and reprice it, but what aluminium **is** —
bauxite, a smelter and cheap electricity — was compiled in.

A recipe names a commodity, a weighted sum of the terms that make it, a weighted sum of the
terms that consume it, and the commodities whose last-week supply scales the output:

```json
"aluminium_smelting": {
  "good":   "Aluminium",
  "output": { "Smelter": 1.90, "deposit:Bauxite*extraction_efficiency": 0.30 },
  "needs":  { "Energy": 0.70 },
  "demand": { "ElectronicsPlant": 0.55, "ArmsIndustry": 0.40 }
}
```

A term is a product of named factors joined with `*`, so `population*standard_of_living` means
exactly what it looks like. The available factors:

| Factor | What it is |
|---|---|
| `population` | millions of people in the region |
| `urbanization`, `standard_of_living` | 0..1 |
| `output_factor` | the share of the labour asked for that is covered, times its productivity |
| `extraction_efficiency` | how well a region can get things out of its own ground |
| `timber_yield` | the same for forest — it answers to farms rather than mines |
| `<Building>` | the staffed number: buildings times `output_factor` |
| `raw:<Building>` | just the building count: roads do not stop working because a factory is short of hands |
| `deposit:<Commodity>` | what lies in the ground, before it was dug up |

For a recipe with `"scope": "nation"` — computed once per country rather than per region —
`projects`, `divisions`, `divisions_at_war` (the same, but zero in peacetime, so a wartime
rate is written as the peacetime weight plus the difference), `hulls`, `gdp` and
`arms_productivity` are also available.

Two more fields:

- `needs` multiplies the output by how each input's supply arrived last week, with a floor: a
  starved chain slows down rather than stopping.
- `shortfall` multiplies by (supply − 1), that is, by zero or a negative number: it is the
  penalty one chain's shortage imposes on another. It is the only way for a recipe to produce
  a negative number, and it is how a country cut off from fertiliser loses part of a harvest
  it had already grown.

Several recipes can name the same commodity: their outputs add up. Merging is **by recipe
name**, so a mod of three recipes changes three chains and inherits the other thirty-six,
including their rebalancing in the next patch.

The full format reference is the first comment in `game/common/production/00_chains.json` —
which is also the whole base game's economy in one file. A worked example is
`mods/_templates/production_chains`.

A typo in a factor or a commodity name used to be a compile error and is now a line in a file
that silently produces nothing. So the loader complains, and the check `every good is made by
something and eaten by something` in `Tools/SimBench` fails with an exact address:

```
Production recipe 'consumer_goods_manufacture' asks for 'standard_of_life', which is not a factor.
```

What a recipe cannot do: ask for a factor the simulation does not compute. The factor list is
deliberately closed — otherwise a mod would be asking a question the simulation cannot answer.
What is open is how they combine, and the whole design of the game is right there.

## Buildings

JSON in `common/buildings` overrides buildings. Seventeen stable IDs: `Agriculture`,
`CivilianIndustry`, `Infrastructure`, `University`, `Hospital`, `Barracks`,
`ExtractionIndustry`, `PowerPlant`, `ArmsIndustry`, `Refinery`, `ChemicalPlant`, `Smelter`,
`ElectronicsPlant`, `Shipyard`, `Port`, `RailNetwork`, `ResearchLab`.

```json
{
  "Port": { "cost": 1.10, "days": 150 },
  "ElectronicsPlant": { "cost": 4.20, "days": 380 }
}
```

Fields you do not name keep their base values. `gdp_gain` is output, `capacity_gain` is
construction capacity, that is, the speed of the next project.

Two buildings are not industry at all. A **port** and a **rail network** set *reach*: how much
of its own shortfall a country is physically able to bring in, regardless of how many friends
it has. Cheap docks mean a world that trades freely; expensive ones mean geography starts to
dictate foreign policy.

**What a country nobody plays builds.** At war it builds arms, with congested supply lines
infrastructure, with an unschooled people universities and with a poor one farms. Otherwise it
builds for its market's worst shortage: the buildings whose recipes make that good, the
specialist first (the one that makes the fewest different goods), a mine where that good's
deposit is richest and anything else in its biggest region. So a chain a mod adds is built by
the AI as soon as some country is short of what it makes. The thresholds are in
`common/defines/15_ai_building.json`.

## Money: inflation and the central bank

The whole monetary model is `common/inflation`, about two dozen numbers, and not one of them
is hard-coded. A mod names only what it disagrees with; anything unnamed keeps its base value,
so such a mod does not go stale when something else in that file is retuned.

A country's inflation is not stored as a fact about it but derived once an economic week from
what the government did: the budget deficit, shortage on the market, whether there is a war,
how large the debt is and where the key rate stands. Each cause is a weight in this file, and
each reaches the `Breakdown` — that is, the "why is inflation nine per cent" tooltip — as its
own line.

### The key rate

The rate is not stored twice. It is the `debt_interest` line in `common/budget` — an ordinary
player-settable multiplier from a fifth of neutral to triple it — and the `central_bank` block
decides **what that multiplier means** and who moves it:

    key rate = central_bank.neutral_rate × (the line's rate ÷ the line's neutral)

```json
{
  "base": 0.018,
  "central_bank": {
    "neutral_rate": 0.04,
    "target": 0.02,
    "response": 1.6,
    "smoothing": 0.25,
    "war_tolerance": 0.015,
    "credibility": 0.35
  }
}
```

| line | what it is |
|---|---|
| `neutral_rate` | what the line's neutral point means, per annum |
| `target` | what the bank holds prices to |
| `response` | points of rate per point of excess — above one, or the bank eases while pretending to tighten |
| `smoothing` | what share of the distance it covers in an economic week |
| `war_tolerance` | how much excess it forgives while the country is at war |
| `credibility` | what a government pays for every point it holds the rate **below** what the bank wanted |

The bank's rule is one line:

    wanted = neutral_rate + response × (inflation − target − (war ? war_tolerance : 0))

clamped by the line's limits and reached at `smoothing` of the distance per week. It works for
**every country in the world**, not just the player's — so changing these numbers changes two
hundred and sixty-one monetary policies, and the AI will live under them too.

### Emission

The second monetary lever, unlocked by the first: only a government that has already taken the
bank can print. All in the same `central_bank` block.

```json
{
  "central_bank": {
    "emission_ceiling": 0.15,
    "emission_inflation": 0.90,
    "emission_credibility": 1.60,
    "emission_memory_decay": 0.12
  }
}
```

| Key | What it means |
|---|---|
| `emission_ceiling` | how much of a year's output can be printed in a year. `0` disables the press entirely |
| `emission_inflation` | points of inflation per unit printed — the direct part |
| `emission_credibility` | and for what people stopped believing. Larger than the first, and that is the mechanic |
| `emission_memory_decay` | what share of that memory goes each economic week |

From scripts: `nation.MoneyPrinting` is the share of output per year, `nation.EmissionMemory`
is what the country is remembered for. The command is
`SetEmissionCommand { IssuerId, ShareOfOutput }`; it refuses until `GovernmentSetsKeyRate` is
set, exactly as the rate does.

Measured on the base numbers: printing 8 % of output over a year gives 89.5 to the treasury
and raises prices from 1.9 % to 24.8 %; a month after stopping it is 21.1 %, and eighteen
months later 2.7 %.

### The decision this exists for

A government can take the rate away from its own bank. Taking it is free; what is paid for is
holding the rate **below** what the bank wanted: money a government has ordered to be cheap is
money nobody believes will stay cheap. That is `credibility`, for every point below, every
week, as its own line in the breakdown.

Set `credibility` to zero and the lever becomes free, cheap money becomes strictly good, and
the decision disappears. Set it high and seizing the rate becomes a wartime measure.

### From scripts

`inflation`, `key_rate` and `government_sets_key_rate` are ordinary scriptable fields, which is
to say conditions read them and effects move them, just like `stability` or `treasury`:

```json
"trigger": { "min_inflation": 0.06, "max_government_sets_key_rate": 0.5 },
"effects": { "government_sets_key_rate_set": 1.0, "key_rate_set": 0.012 }
```

A whole decision built on them, for `common/decisions`:

```json
"cheap_money_decree": {
  "name": "Cheap money by decree",
  "description": "Take the key rate off the central bank and put it on the floor.",
  "cooldown_days": 1095,
  "trigger": { "min_inflation": 0.06, "max_government_sets_key_rate": 0.5 },
  "effects": { "government_sets_key_rate_set": 1.0, "key_rate_set": 0.012, "stability": 0.02 }
}
```

## Trade: who sells to whom

The market is not an anonymous pot. Every economic week each country's shortfall is matched
against the real surpluses of real countries, and the result is a list of routes — about 2,200
of them on day one.

Who gets chosen is decided by diplomacy: war and an embargo zero a pair out, a trade agreement
multiplies the pull by 1.75, an alliance by 1.3, a rivalry divides it, opinion shifts it within
±65 %, and the distance between capitals damps the link the further apart they are and the
worse both sides' ports and railways.

How much can actually be brought in is decided by docks and rails: the ceiling is
`world_market_access` times the country's reach. Below all of that there remains a thin grey
market — a country with no friends should get poorer, not die the same day.

Hence the mod's levers: `world_market_access` and `import_markup` in `common/defines`, the cost
of a port and a rail network in `common/buildings`, and the weight of opinions in
`common/opinion_modifiers`.

The Ledger picks up changed names, base prices and commodity weights automatically. A price
index always takes the modded `base_price` as 100, so the chart stays meaningful after a
complete rebalance of the market.

### Trade controls and how they explain themselves

The seven controls a country runs its own trade with are declared in `common/trade`, and the
screen is drawn from that file: the controls themselves, their limits, their step, their names
— and the sentence each uses to describe what it does **at its current value**.

```json
"price_floor": {
  "scope": "good",          // "good" — about a commodity; "partner" — about another country
  "shape": "multiplier",    // "share" (0..1), "multiplier" (×), "flag" (a toggle)
  "default": 0.0,
  "minimum": 0.0,
  "maximum": 5.0,
  "step": 0.1,
  "name": "Price floor",
  "description": "…",
  "reads": {
    "resting": "We will take whatever it fetches.",
    "set": "We will not sell below {0} of what it is normally worth."
  }
}
```

A number and a name are not an instruction. "Price floor 0.4×" is a truth about a control,
while "we will not sell this below two fifths of what it is worth" is a decision somebody took.
The screen shows the second one under the slider and dims the whole row when a control is at
its default — which turns "4 instructions in force" into four lit rows you can see rather than
a figure.

| key | when it is shown |
|---|---|
| `resting` | the value equals `default` — the control is doing nothing |
| `off` | the value is at `minimum`, where that means *none at all* rather than *a little* |
| `on` | the value is at `maximum`, or a toggle is on |
| `set` | in every other case. `{0}` is the value as the screen prints it |

All four are ordinary localisation keys, like `name` and `description`. `shape` also decides
the control's appearance: `flag` is drawn as a toggle rather than a slider.

**Seven identifiers do something; yours does not.** The weekly calculation reads, by name,
`import_priority`, `import_ceiling`, `export_reserve`, `price_ceiling`, `price_floor`,
`preference` and `forbid`. A control you invent is stored on the country, saved, shown and
translated — and not read by the market until a script or a patch reads it.

The `trade_instructions` template replaces one of the seven and adds an eighth.

## Diplomacy, reputation and coalitions

`common/opinion_modifiers` holds the reasons an opinion is made of. An opinion is not a number
somebody sets but the sum of named reasons, so each can be renamed, reweighted and given a
different memory: `days: 0` holds while it is true; `decays: true` fades over its term;
`max_stacks` is how many times the same thing counts.

The reasons that arrived with diplomacy: `embargoed_us`, `guaranteed_us`, `notoriety`,
`coalition_partner`, `stood_by_us`, `abandoned_us`.

The prices of diplomacy itself are in `common/defines`:

| Define | What it does |
|---|---|
| `notoriety_per_million_taken` | Reputation per million people annexed. By people rather than provinces: tundra frightens no neighbours. |
| `notoriety_per_unjustified_war` | Reputation for a war whose reason nobody accepted. |
| `unjustified_war_stability_cost` | What such a war costs at home. |
| `unjustified_war_support_cost` | And with the population. |
| `broken_promise_stability_cost` | What refusing your own alliance obligation costs. |

Reputation fades only with time. Past a threshold, the neighbours gather into a coalition, and
a coalition answers when its target attacks somebody.

**A reason for war** is not a flag but an inference from the state of things: land taken from
us and not forgotten, our people living across the border, a friend already being hit. A war
with no reason is expensive.

### How the AI decides to fight

Every eleven days a country looks at everybody it can reach and counts what a war is **worth**
and what it will **cost**. One is subtracted from the other. Above `ai_war_desire_threshold` it
may declare; above zero but below the threshold, the country keeps the target as a plan, arms
itself and starts prising its friends away.

On the benefit side: `ai_conquest_appetite`, `ai_irredenta_appetite`, `ai_resource_appetite`,
`ai_hostility_appetite`, `ai_rivalry_appetite`, `ai_opportunism_appetite`,
`ai_war_minimum_advantage`.

On the cost side: `ai_trade_loss_weight`, `ai_ally_deterrence`, `ai_coalition_deterrence`,
`ai_notoriety_caution`, `ai_no_casus_belli_caution`, `ai_war_weariness_caution`,
`ai_instability_caution`, `ai_poverty_caution`, `ai_overseas_caution`.

And patience: `ai_war_plan_patience_days` is how long a government prepares before dropping the
idea; `ai_embargo_leverage` is how much more dependent a rival has to be before sanctions look
better than an invasion.

A worked example is `mods/_templates/statecraft`. The result is checked by the bench: the line
`the world after 730 day(s)` counts the wars, annexations, trade agreements, pacts, alliances
and rivalries over two years of a world nobody plays.

### Dependency without a war

A subject used to be something only a peace treaty could make. There are now three more ways,
each a button beside the alliance in a foreign country's panel, and each ending in a row of
`common/subjects`:

- **Offer protection** - a protectorate. Costs nothing; weighed by how much the smaller country
  fears somebody else (a war, or a relation as bad as a rivalry, with a country stronger than it).
- **Buy influence** - a client state. Pays `SubmissionEconomicPayment` of the smaller country's
  GDP, and only if it accepts; a trade agreement and a treasury in debt make it readier.
- **Back a government** - a satellite. Costs `SubmissionPoliticalCost` of its GDP up front and
  `SubmissionPoliticalNotoriety` of notoriety whether it works or not; shared ideology and a
  shaky government make it readier.

The smaller country decides, the way an alliance is decided, and the tooltip shows the terms
before anything is spent: how it regards the asker, how much stronger the asker is (nothing
below `SubmissionMinimumStrengthRatio`), and what the kind of dependency costs it - its
`liberty_desire` times `SubmissionReluctanceScale` - plus the route's own reason. Above
`SubmissionWillingness` is yes. A refusal is remembered (`common/opinion_modifiers/20_submission.json`)
and the same country is not asked again for `SubmissionRefusalDays`. A player is never made a
subject this way, a country with subjects of its own answers to nobody, and a subject cannot take
subjects. Countries nobody plays do it too, rarely (`SubmissionAiChancePerMonth`), and only when
the odds clear the bar by `SubmissionAiMargin`. Which row each route makes is a define as well
(`SubmissionProtectionKind`, `SubmissionEconomicKind`, `SubmissionPoliticalKind`), so a mod can
point a route at a kind of its own. Everything is in `common/defines/09_submission.json`; from code,
`SubmissionSystem.Offer` or `OfferSubmissionCommand`.

## Terrain

`common/terrain` is a catalogue, not an enumeration: a mod can **add a new terrain type without
touching code**.

```json
{
  "salt_flats": {
    "name": "Salt flats",
    "color": "#D8D2C0",
    "texture": "gfx/terrain/salt_flats.png",
    "movement_speed": 1.1,
    "attack": 1.05,
    "defense": 0.9,
    "combat_width": 1.1,
    "supply": 0.85
  }
}
```

Fields you do not name are inherited, so partially overriding an existing type is three lines.
What lies where is decided by the raster `map_data/terrain.png`, built by
`Tools/TerrainCompiler`; individual provinces can be overridden by history, and history beats
the raster.

## History and your own charts

The weekly points are in `Campaign.History.ForNation(nationId)`. A C# mod can read them for
AI, reports and a UI panel of its own. Do not modify the returned collection and do not keep a
reference to an individual point as game state: the history is derived telemetry, not a source
of rules.

To change an outcome, use commands (`SetTaxRateCommand`, `SetTradePolicyCommand` or your own
`ICampaignCommand`), not the Ledger's visual controls. Safe read-only access to the history
from the GDScript API will be added once the save format has stabilised.

## C# mods

C# is for new systems, complex AI or large libraries. Copy `mods/_templates/csharp_mod`,
rename the folder and build the nested project:

```powershell
dotnet build .\mods\my_csharp_mod\csharp\ExampleCSharpMod.csproj
```

The mod implements `DealOrForce.Modding.IGlobalityMod`:

```csharp
public sealed class MyMod : IGlobalityMod
{
    public void OnCampaignCreated(ModContext context)
    {
        var country = context.Nation("KOR");
        context.Log($"Loaded for {country?.Name}");
    }

    public void OnDayAdvanced(ModContext context)
    {
        // For game actions, prefer putting an ICampaignCommand in the Queue.
    }
}
```

A C# DLL is loaded only in a desktop/.NET build with JIT. Such a mod is executable code with
the privileges of the game process: install a DLL only from a source you trust. For a Workshop,
data and GDScript mods should be the default recommendation, with the presence of a DLL flagged
explicitly.

## Determinism and compatibility

- Do not use the system clock or `System.Random` for a game outcome. In C#, use
  `Campaign.Random`.
- Do not modify collections while enumerating them; defer actions through `Campaign.Commands`.
- One effect should happen in one callback, so that speed does not apply it twice.
- Keep the old `id` and raise the `version`.
- Split a mod into small JSON files by subject.
- On a conflict, the later mod wins. A dependency is needed when a mod does not work without
  somebody else's content.

## Testing a mod

1. Check the mod line in the main menu.
2. Look for `[mods]` and `[mod-api]` in the Output.
3. Start a new campaign: data overrides are applied when one is created.
4. Run at least a month at speeds 1 and 5.
5. Save, and confirm the mod set is the same after a restart.
6. For C#, build the mod and the game separately with zero warnings.

## A familiar content structure

The base game and a mod have the same tree. A later mod overlays entries by stable ID, so there
is no need to copy a whole file for one change:

The notation below: **live** — the catalogue is already loaded by the game; **next** — a fixed
public contract for the next stage. Planned folders do not count as implemented until the loader
and the validator accept their contents.

```text
game/ or my_mod/
├── descriptor.json     # the package/mod manifest and dependencies            [live for mods]
├── common/
│   ├── buildings/     # building cost, duration and effects
│   ├── decisions/     # national decisions and cooldowns
│   ├── defines/       # numeric simulation rules
│   ├── goods/         # commodities, prices and the importance of a shortage
│   ├── production/    # what things are made of and who consumes them
│   ├── units/         # formation types, combat width and supply
│   ├── ai_strategy/   # strategic AI weights and profiles
│   ├── modifiers/     # named country conditions and their effects
│   └── diplomacy/     # actions, cost, attitude and acceptance             [next]
├── events/            # events, triggers, options and composite effects
├── history/
│   ├── provinces/     # who owns each province and who holds it
│   ├── countries/2024/# the country and its government at the scenario date
│   ├── states/2024/   # regional population, buildings and control
│   └── units/2024/    # the starting strength of the armed forces
├── map_data/          # administrative regions, topology and hydrography
│   ├── default.map    # the names of the authoritative data maps and horizontal wrap
│   ├── provinces.png # a unique RGB per tactical province
│   ├── definition.csv# RGB → stable 16-bit ID
│   ├── rivers.png    # fixed colours for river classes
│   ├── lakes.png     # the lake mask
│   └── hydrography/  # GeoJSON sources for recompiling the base map
├── localization/      # strings by language, key = the English source text
│   └── english/       # the full index: what there is to translate at all
├── interface/         # named colour tokens for the interface and the map
├── gfx/               # materials, textures and map symbol definitions        [next]
├── sound/             # sound effects, music and event-to-cue mappings        [next]
└── scripts/           # optional GDScript/C# extensions for the mod           [live for mods]
```

Instead of a closed syntax of its own, this uses ordinary JSON with comments, stable string
IDs, validation and safe C# fallbacks. On a new campaign the catalogues are first reset to base
and only then given the mod layers — a disabled mod does not "leak" into the next run.

The list of what is "actually wired up" used to live here — and went stale silently, because
nobody checks an assertion in a document. The build checks it now: the loader records every
category it was asked for, and SimBench compares that list against what is on disk. A data
folder nobody reads fails the check — otherwise a modder would edit it all day with no error
and no effect.

As of today, everything under `common/` is read, and that is 34 categories:

`common/ai_strategy`, `common/budget`, `common/buildings`, `common/countries`,
`common/cultures`, `common/decisions`, `common/defines`, `common/diagnostics`, `common/flags`,
`common/formable_nations`, `common/goods`, `common/governance`, `common/government_focus`,
`common/governments`, `common/ideologies`, `common/militia`, `common/modifiers`,
`common/opinion_modifiers`, `common/peace_terms`, `common/production`, `common/professions`,
`common/religions`, `common/scripted_effects`, `common/scripted_modifiers`,
`common/scripted_triggers`, `common/secession`, `common/settings`, `common/stability`,
`common/states`, `common/subjects`, `common/terrain`, `common/trade`, `common/trade_policies`,
`common/units`.

Plus `events`, `localization`, `interface`, `gui`, `gfx`, `fonts`, `sound`, `music`,
`scenarios`, `history/countries`, `history/states`, `history/pops`, `history/units`,
`history/provinces` and `map_data/*`.

Recently moved out into data: `common/peace_terms` (the eight standard peace terms — their
price in war score, the opponent's readiness, the minimum war duration), `common/trade` (the
rules for buying and selling), `common/flags` → `identities` (a proclaimed nation's flag beats
the flag of the country that proclaimed it), `needs_water` on a building in `common/buildings`
(a port and a shipyard are useless without water), and the new defines
`MaxConstructionProjects`, `EventIntervalDays`, `EventMemoryDays`, `EventExpiryDays`,
`InlandWaterworksYield`, `DemolitionRefund`, `PeaceRoundUpShare` and `PeaceRoundDownShare`.

### Compatibility rules

- Content is addressed by a stable string ID, not by position in an array or a file name. For
  public mods, the namespace `author_mod:item_id` is recommended.
- Loading goes `base game → dependencies → load_order → id`, so the result is deterministic and
  identical in single-player and in a local network game.
- A later layer changes only the fields it explicitly passes. Full removal and replacement will
  get a declaration of their own, so that an empty value does not mean three different
  operations at once.
- Scenario history is separate from common definitions: a building type lives in `common`, and
  its level on 1 January 2024 lives in `history`.
- C# and GDScript extend the public API, but ordinary balance, the map, events, countries,
  troops and localisation must all be moddable without compiling anything.
- A save stores the map version and a fingerprint of the active mods; an incompatible set is
  rejected before the current campaign is damaged.

This is deliberately a familiar structure but not a byte-for-byte copy of any other game's
formats: the useful separation of responsibilities is taken, while JSON, strict diagnostics and
atomic saves remove the usual ambiguities of older scripting formats.

### Geographic overrides

`map_data/region_overrides.json` can add and override administrative regions by `map_index` and
correct narrow mistaken borders. The `source_nation` field allows safely carving new territory
out of an existing controller before the regions and provinces are built. This changes the
authoritative raster: selection, provinces, the AI, fronts and construction all see one
geography rather than a cosmetic mask.

The optional `polygon` field takes an array of `[longitude, latitude]` pairs. The rule's
bounding box is applied first, then a point-in-polygon test and the `source_nation` check, so a
complex border does not repaint the sea or a third country. PMR serves as a worked example: its
56-point outline is simplified from OpenStreetMap relation 65335 down to roughly the resolution
of the source raster; `boundary_source` keeps the provenance and the licence note beside the
data.

### The political map as data

The shape of the world has been data for a long time: `provinces.png` and `definition.csv`
together say where each of the 38,013 provinces is. **Who owns them** was not: ownership came
from a control raster compiled into the executable. A mod could redraw the shape of the world
and not move one border on it.

There is now a second half, in a form familiar from Paradox: a `history/provinces` folder with
files of the form

```
province;owner;controller
12345;FRA
12346;FRA;DEU
```

`owner` is whose land it is by right, `controller` who stands on it. The controller can be left
out: then the owner holds it. Provinces the file does not mention keep the base game's owner —
a mod that moves one border does not have to rewrite the other thirty-seven thousand.

The base game ships the full map (`game/history/provinces/ownership.csv`, 38,013 lines), and it
is **exported by the same compiler** that builds the raster — so what a modder edits is exactly
what the game runs on, not a hand-written approximation of it. The bench checks that the file
and the map have not drifted apart, and that a one-line edit moves exactly one province.

Everything downstream — the economy, fronts, supply, the AI, the peace conference — reads
ownership from one place, so a moved line moves them all.

**What this layer does not do:** membership of a region. A province is grown inside one region
and never crosses its border, so its region is a fact about its shape rather than about
politics. To change that, edit `map_data/region_overrides.json` and rebuild the raster.

The template: `mods/_templates/border_redraw`.

### Land that belongs to nobody

A region can belong to no country at all. In a `history/states` file:

```json
{ "aq_x01": { "owner": "none", "population": 0 } }
```

Its provinces are then unowned whatever the map painted there, it is drawn in the grey of land no
flag flies over, and it opens with nobody's name on it. `"none"` is the only owner this key takes -
who owns a province is otherwise `history/provinces/ownership.csv`, and two answers to one
question would disagree sooner or later. The base game uses it for Antarctica
(`history/states/2024/unclaimed.json`), which no state holds under the 1959 treaty; a
colonial-era scenario would list its unclaimed regions the same way.

Any country that can reach such a region may settle it - across a land border from a province of
its own, or by sea when both the region and the country have a coast. Clicking the region shows
what it costs and how long it takes; the country pays once, the settlers work for the given
number of days, and on the last one the region is its own, with the settlers (taken from its most
populous region) living there. One country settles a region at a time, and a country founds one
colony at a time. Countries nobody plays do the same, rarely, when they are at peace and have
several times the price to spare.

**Claims.** A region's history can name the countries that claim it without holding it:
`"claims": ["NOR"]`. A claimant settles its own claim for `ColonisationClaimantCostShare` of the
price and `ColonisationClaimantDaysShare` of the time. Anybody else who settles it is remembered
by every claimant - `settling_our_claim` when the settlers sail and `took_our_claim` when the flag
goes up (`common/opinion_modifiers/31_colonies.json`) - and a country nobody plays reckons land
somebody else claims `AiColonisationForeignClaimWeight` times dearer than it is. The base game
gives Antarctica's lands the claims the 1959 Treaty froze rather than ended: Norway's Queen Maud
Land, Australia's quarter of the continent, France's Adelie Land, New Zealand's Ross sector, and
the peninsula that Britain, Chile and Argentina all claim. Marie Byrd Land is the one land nobody
claims.

**Antarctica's lands.** Antarctica is seventeen regions - the lands its coasts are named for, the
Polar Plateau at the Pole, Dome Argus and Vostok - with the stations as their buildings and what
the geology says is under the ice as their resources: the iron and coal of the Prince Charles
Mountains, the Beacon coal of the Transantarctic Mountains, oil and gas on the Ross and Weddell
shelves, the krill of the Scotia Sea off Graham Land. They are cut by a partition in
`map_data/state_regions/00_region_overrides.json`, which names the region it divides
(`carve_from_iso`), any other region whose ground goes with it (`also_carve_from_iso`) and a seed
point for each share; the shares grow from their seeds until they meet. `"polar": true` measures
that growth on the ground rather than on the raster - near a pole a cell is a sliver, and grown a
cell a step the borders come out as the spokes and rings of a dartboard - bends the result with
smooth noise so that no border runs down a meridian or along a parallel for long, and gives each
island to the share nearest across the water. A partition changes the region layer, so the map is
recompiled with `Tools/MapDataCompiler`, which renumbers the provinces; `terrain_provinces.csv`
is then recounted from the unchanged `terrain.png` with
`python Tools/TerrainCompiler/compile_terrain.py --recount-only`, which needs only numpy and Pillow.

The numbers are in `common/defines/08_colonisation.json`: `ColonisationCostBase` and
`ColonisationCostPerProvinceRoot` (the price grows with the square root of the region's
provinces), `ColonisationDaysBase` and `ColonisationDaysPerProvinceRoot` (the same for time),
`ColonistsOnFounding`, `MaxColoniesAtOnce`, `AiColonisationChancePerMonth` and
`AiColonisationTreasuryMultiple`. From code, `ColonisationCore.Start` / `Abandon` or the
`ColoniseCommand` / `AbandonColonyCommand` commands.

### Microstates and the raster

Three countries had a place in the country table and not one cell on the map: the Vatican,
Northern Cyprus and Gibraltar. A row in a table is not a state: such a country cannot be
picked, taken, included in a peace treaty or declared war on.

The Vatican and Gibraltar were added as `seed_points` of radius 0 — one cell, cut out of Rome
and out of Cádiz. A raster cell is about 0.0625°, which is roughly 36 km² against the Vatican's
real 0.49 km². That is crude, but a state that exists, borders Italy and takes part in
diplomacy is closer to the game's truth than an exact one that does not. Northern Cyprus is
given as a polygon along the UN line of separation; its southern side follows the line and the
other three are deliberately pushed out to sea — only cells already belonging to the source
country are painted.

Nakhchivan is the same case from the other side: an exclave of Azerbaijan cut off by Armenia,
while the source table gives Azerbaijan only four districts, so the exclave was swept into
whichever of them the raster could reach. A region you cannot walk to from the rest of the same
region is not one region.

The population donor is now determined from the map if a rule does not name one: the region
currently holding the land the rule is about to take. Naming it by hand is easy to get wrong,
and the answer is on the map anyway.

The Coral Sea Islands and the Spratlys cannot be added: their land is smaller than a raster cell
and there is nothing to paint. The `Tools/SimBench` check requires every populated country to
have land, and lists those two exceptions explicitly with the reason.

Note that changing regions changes provinces too. A province never crosses a regional border, so
after editing `region_overrides.json` the raster has to be rebuilt:

```bash
dotnet run --project Tools/MapDataCompiler/MapDataCompiler.csproj -- <path to the project>
```

Otherwise the game refuses to load the map with an explicit "province crosses an administrative
or national border" — deliberately, rather than quietly renumbering IDs.

### The province raster map

All data maps are ordinary lossless RGB PNGs of 5760×2880 with no indexed palette and no
interlacing. `default.map` gives the relative file names and `wrap_x = yes`. Black in
`provinces.png` means sea, any patch of land must have a colour from `definition.csv`, and one
province cannot cross an administrative or national border of the starting scenario. The loader
checks the dimensions, unknown and duplicated colours, missing IDs, coloured sea and border
crossings before a campaign is created.

A mod that changes provinces must ship both `provinces.png` and `definition.csv`; replacing only
one of the pair is refused. `rivers.png` and `lakes.png` can be replaced independently. The full
palette with RGB codes is in `game/map_data/RASTER_PALETTE.md`.

### Hydrography

During play, `rivers.png` and `lakes.png` are authoritative. `rivers.png` has four permitted
colours: black, a minor river, an ordinary river and a major one; `lakes.png` uses black and
`#3366FF`. Those very pixels are both drawn and turned into game properties of the graph.

`map_data/hydrography/*.json` are kept as editable GeoJSON sources for the compiler: rivers
accept `LineString`/`MultiLineString`, lakes `Polygon`/`MultiPolygon`, with coordinates in
`[longitude, latitude]` order. They are convenient for bulk-importing real data, but they do not
override the drawn PNGs at startup.

One PNG creates both the visible layer and the provinces' game properties, so a river you draw
automatically takes part in crossing searches, in combat and in movement. The numbers are in
`common/defines/military.json`: `river_movement_multiplier`, `river_attack_multiplier`,
`major_river_attack_multiplier` and `lake_supply_bonus`. There is deliberately no impassability
value here — rivers never break the land graph.

### Events and the effects language

A file in `events/*.json` contains an object of events. Every event has a `title`, a
`description` and an array of `options`; an option has a `title`, a `consequence` and an
`effects` object.

`effect` used to be a name from a closed C# enumeration: a mod could write a new event but only
choose one of six pre-written outcomes. A consequence is now a freely composed set of key-value
pairs, and that is how events, decisions and modifiers are all written:

```json
{
  "energy_shock": {
    "title": "Energy shock",
    "description": "Fuel costs have doubled in a season.",
    "trigger": { "min_state.urbanization": 0.30, "at_war": false },
    "weight": 1.5,
    "options": [
      {
        "title": "Subsidise the bills",
        "effects": { "treasury_percent": -0.08, "state.radicalism": -0.012 },
        "modifiers": [ { "id": "austerity", "days": 180 } ]
      },
      {
        "title": "Let prices find their level",
        "effects": { "productivity": -0.004, "state.radicalism": 0.02 }
      }
    ]
  }
}
```

The rules of the effects language:

- a bare number is **added**: `"stability": 0.02`;
- the `_percent` suffix multiplies by `(1 + value)`: `"treasury_percent": -0.08` is minus 8 %;
- the `_mult` suffix multiplies directly, `_set` assigns;
- the `state.` prefix applies the entry **to every region of the country** rather than to the
  country; `army.` to every one of its divisions (`army.organisation`, `army.entrenchment`,
  `army.experience`, `army.equipment`) and `fleet.` to every fleet (`fleet.readiness`,
  `fleet.supply`); `research` puts points on the technology being researched;
- `"add_modifier": { "<spirit>": days }` hangs a national spirit on the country, and
  `"remove_modifier"` takes one or a list off (see *National modifiers*);
- an unknown key does not break the file: it is written to the log with the list of permitted
  keys, and the rest of the mod goes on loading.

The full list of keys is always available in the game itself — the console command `effects`. It
is also used in triggers: `min_<key>`, `max_<key>`, plus `at_war` and `tags`. An empty trigger
means "any country will do", which is what an ordinary flavour event wants. `weight` sets the
relative weight when choosing among the events that fit.

The old form `"effect": "InvestIndustry"` still works as a preset, so existing content does not
break — but new content is better written through `effects`.

### Modifiers (national spirits)

`common/modifiers/*.json` holds named country conditions that events and decisions grant for a
term. It is what Hearts of Iron calls a national spirit and Victoria calls a modifier, and it is
there for the same reason: an event that moved a number once is forgotten at once, while an
event that hung a named condition on a country for a year stays part of the game.

```json
{
  "war_economy": {
    "name": "War economy",
    "description": "Industry is on a war footing.",
    "days": 365,
    "positive": true,
    "effects": { "productivity": 0.0022, "state.radicalism": 0.0010 }
  }
}
```

`days` = 0 makes a modifier permanent. The effects are applied **once per economic tick** while
the modifier is active, so the numbers here are rates per tick, not one-off amounts. Granting it
again refreshes the timer rather than stacking a second copy. A spirit may also carry a
`modifiers` block - shares of what the simulation works out, such as `"construction_speed": 0.15` -
for as long as it lasts; see *National modifiers*. The country card lists the spirits a country
carries with the days each has left, and the National Ledger shows them on the "In effect" line.

An event refers to them with a `modifiers` array: the short form `["war_economy"]` or the full
`[{ "id": "war_economy", "days": 180 }]`.

### The navy and the sea lanes

**Fleets** are data: `history/units/2024/*.json` lists each country's fleets under
`"fleets": { "TAG": [ { "name", "hulls", "latitude", "longitude" } ] }`. Listing a country
replaces its fleets whole; every other country is left alone. The coordinates are where the
fleet lies at home; its home port is worked out from them - the nearest coastal province the
owner holds on the same sea - so a mod never writes a province id.

**The sea** is worked out from the province raster: every cell that belongs to no province is
water. The world is cut into one-degree squares and the water in each square into the pieces
of it that touch; those pieces are the nodes a course is plotted through, so an island or an
isthmus narrower than a square still stands in the way. A course is the shortest way through
them, pulled straight wherever a line stays on water.

**Straits and canals** the raster is too narrow to draw are in `common/sea_lanes/*.json`:

```json
{
  "suez_canal": {
    "name": "Suez Canal",
    "closes_to_enemies": true,
    "points": [[31.35, 32.32], [30.60, 32.29], [29.93, 32.55], [28.30, 33.30]]
  }
}
```

The points are a line of `[latitude, longitude]` cut into the water `SeaLanePassageWidth`
raster cells either side; begin and end it in open water. `closes_to_enemies` shuts it to
every country at war with whoever holds the ground at its middle (or at `"holder"`), which is
how the Bosporus, the Kerch Strait and the canals behave. `"open": false` shuts a passage
outright - a mod closes Suez by naming it again with that one field, or cuts a new canal by
adding a key.

**Ports and harbours.** A province has a harbour when its coast touches the open sea; that is
where a fleet lies in its port, blockades it or lands on it. A fleet repairs and refills only
lying in a friendly port - its own, an ally's or a co-belligerent's - and when its home port is
taken it moves to the nearest one its country still holds on the same sea.

**What a navy does.** A blockade hurts the country whose coast it sits on; a raider takes most
near its victim's ports and little in another ocean (`FleetRaidRangeDegrees`,
`FleetRaidFloor`); a country with no coast has no shipping to lose. A fleet that breaks sinks
`FleetHullsSunkOnBreak` of the damage it carries. Actions take a share of a day's losses each
step, so a fight lasts as many days at any clock.

**Shipyards.** A country has `SlipwaysPerEstablishedHull` slipways for every hull of the navy
it began with and `SlipwaysPerShipyard` for every shipyard (a yard on a lake or a river adds
its share). A hull costs `HullCost` (billions, like every sum), paid over `HullBuildDays`; an
empty treasury idles the yards. A country nobody plays builds toward what it began with scaled
by its military budget (`NavalResponse`, `NavalTargetFloor`, `NavalTargetCeiling`); a player
lays hulls down from the fleet panel. All of these are in `common/defines/12_navy_logistics.json`,
with the AI's thresholds for sending a fleet home and out again.

`SimBench --sea sea.png` draws the water, the straits, every harbour and a dozen voyages, and
prints how long each is: the quickest way to see what a mod's map or passages did to the sea.

### Fronts and the pace of an army

**The front staff.** Once a day, and at once for a front that has just moved, every country's
sector of every front is looked at as a whole: every post held before any post is held twice,
the posts facing the enemy first, and a pile given up to the holes nearest it. A post's share of
the divisions is its share of the pressure on the line - one for being on the front, and one more
for every division's worth of the enemy standing next to it. The same staff keeps the posts of a
player's divisions ordered to defend a front and the lines a player draws. Spearheads, divisions
in a battle and divisions on a direct order are never moved, and by default neither are
divisions ordered to advance (`FrontStaffMovesAdvances`). A division still marching to its post
is diverted only to a post well nearer than the one it is walking to, so a column is not turned
round halfway. A division of a country at war that serves none of its wars - raised during it,
or released when another war ended - is told to hold the nearest front at once
(`FrontStaffCallsUpIdle`). The numbers are in `common/defines/13_front_staff.json`.

**More than one war.** A country fighting several wars splits its army between them in
proportion to what each needs: its own stretch of that front, and half a division for every
enemy division put into it. A war where it holds no ground of its own - an ally's - asks for
little. A division stays with the war it serves while that war is not over its share, and the
war that is short takes the divisions nearest its front. An order given without naming a war
goes to the war whose front is nearest the division, or the one it already served when that is
about as near.

**Roads and open country.** A march runs `1 + RoadSpeedPerInfrastructure` faster for every level
of infrastructure in the region it enters, up to `RoadSpeedCap`. Enemy ground that nobody is near
enough to cover - the country behind a broken line - is taken at `OpenCountryCapturePerDay`, with
supply counted at no less than `OpenCountrySupplyFloor`; a gap in a line the enemy still holds
stays at the slower `UnopposedCapturePerDay`. Both are in `common/defines/07_movement.json`.

`SimBench --fronts 180` plays half a year and prints every front: its posts, the ones nobody
holds, and the largest pile. `--define Key=Value` changes one define for one run.

### National modifiers

A country's **modifiers** are shares added to what the simulation works out anyway:
`"construction_speed": 0.15` builds a sixth faster, `"output": -0.05` makes five per cent less.
Five kinds of content may carry a `modifiers` block, and the country's total for each key is their
sum:

| Where | For how long |
|---|---|
| `common/modifiers` (national spirits) | while the spirit lasts |
| `common/governments` | while the country has that form of government |
| `common/ideologies` | while it holds that ideology |
| `common/government_focus` | while its cabinet has that priority |
| `common/technologies` (`effects`) | once researched, for ever |

```jsonc
"general_mobilisation": {
  "name": "General mobilisation",
  "description": "Every reservist has been called up...",
  "days": 180,
  "positive": true,
  "modifiers": { "recruitment_speed": 0.30, "reinforcement_rate": 0.25, "output": -0.05 }
}
```

| Key | What it changes |
|---|---|
| `output` | what farms, mines and factories produce |
| `research_speed` | research points a week |
| `construction_speed` | build time, divided by one plus this |
| `construction_cost` | what a building costs (negative is cheaper) |
| `tax_efficiency` | how much of what the taxes ask for arrives |
| `trade_reach` | how far the country's goods travel |
| `army_attack`, `army_defense` | a division's fire attacking and holding ground |
| `army_speed` | how fast divisions march |
| `supply` | supply throughput along the country's lines |
| `organisation_recovery` | how fast a division out of battle recovers |
| `entrenchment_speed` | how fast a division standing still digs in |
| `reinforcement_rate` | replacements a day |
| `recruitment_speed` | training time, divided by one plus this |
| `recruitment_cost` | what a division costs to raise (negative is cheaper) |
| `naval_speed` | how fast fleets sail |
| `shipbuilding_speed` | how fast the yards finish hulls |
| `stability_weekly`, `war_support_weekly` | points added every week (0.001 is a tenth of a point) |
| `radicalism_weekly`, `living_weekly` | the same, in every region of the country |
| `reconstruction_speed` | how fast regions the war has wrecked are rebuilt - see *War damage* |

A key the list does not have is reported with the list beside it, so a typo is an error rather
than a bonus that never applies. The keys are fixed because each is a question the simulation
has to be written to ask; who carries them, and how much, is entirely data. Construction and
training speeds apply to what is started while they last. The modifiers of a spirit are separate
from its `effects`, which stay per-tick drifts in the effect keys events use.

**Giving a country a spirit.** Any effect block - an event option, a decision, a scripted effect -
may say `"add_modifier": { "tax_reform": 540 }` (0 for the length the spirit declares) and
`"remove_modifier": "martial_law"` (or a list). An event option may also list
`"modifiers": [ { "id": "war_weariness", "days": 270 } ]`, as before. Tooltips show a granted spirit
with what it does indented under it; the country card lists the spirits a country carries, the days
each has left, and under **All modifiers** the country's totals and where each part comes from.

### Technologies

`common/technologies/*.json` is the research tree. Each key is a technology:

```json
{
  "drone_warfare": {
    "name": "Drone warfare",
    "description": "Cheap eyes and cheap strikes over the whole front, day and night.",
    "category": "military",
    "cost": 220,
    "requires": ["network_centric_warfare"],
    "icon": "icon_drone",
    "effects": { "army_attack": 0.04 }
  }
}
```

`name` and `description` are localisation keys like everything's. `category` is `industry`,
`society`, `military` or `logistics`. `effects` are shares added to something the simulation
already works out, summed over what a country has: `output` (what the economy makes),
`research_speed`, `army_attack`, `army_defense`, `army_speed` (marching and advancing) and
`supply`. A mod adds a technology by adding a key and changes one by naming it again - the layers
merge a key at a time, as everywhere.

A country researches `ResearchBasePerWeek + ResearchGdpWeight * sqrt(GDP in billions) * human capital`
points a week, bent by its education spending and multiplied by `1 + research_speed`; a technology
costs its `cost` less `ResearchDiffusion` times the share of the world's GDP that already has it.
The numbers are in `common/defines/11_research.json`. A country nobody plays chooses the cheapest
thing it can start - from the military and logistics shelves while it is at war - and so does a
player's ministry until the player chooses: a week of research is never thrown away, and
switching keeps what was put into the last choice.

The bench checks the tree: every prerequisite is a technology, every effect is one of the six,
every category is one of the four, and nothing needs itself through its prerequisites.

Long-range formations live in `common/support/*.json` (see
`game/common/support/00_support_types.json`). Their `icon` field omits the `icon_` prefix:
`"icon": "air"` resolves to `icon_air` in the `gfx` catalogue. The same symbol appears
in the formation list and on the map. Supply a transparent PNG for a custom icon; the map
also cuts away the opaque black backing of legacy icons. `range_km` drives the actual
reach and the `RANGE` map view, so a mod's changed distance is visible to the player.

### Localisation

`localization/<language>.json` is a flat map of "string → translation". The folder is named the
way Paradox names it, but the format is ordinary JSON rather than their YAML dialect.

The key is the **English source text**, as in Godot's own `tr()`:

```json
{
  "language.name": "Deutsch",
  "Singleplayer": "Einzelspieler",
  "Quit to desktop": "Zum Desktop beenden"
}
```

Two convenient properties follow: an untranslated string simply shows in English rather than as
nothing, and a translation mod does not have to cover the whole interface to be useful.
`language.name` decides what the language is called in the switcher — in that language.

**Where to start a translation.** `game/localization/english/00_english.json` is the complete
index of what there is to translate: every string mapped to itself. Copy the file to
`localization/<language>/00_<language>.json`, translate the right half of each line and leave the
left half alone.

It looks redundant and is not. A lookup that fails to find a key falls back to the current
language, then to English, then to the key itself — so the interface would work with an empty
English file, and it was empty: three lines. The price was that `Loc.MissingKeys`, whose job is
to be a translator's to-do list, and the console command `loc <language>` that prints it, were
comparing every language against those three lines and always answering that nothing was
untranslated.

The index is kept current by the bench check `the English table indexes every string the game
shows`: it fails if a caption from the sources did not reach it, and prints each language's
coverage. Captions assembled on the fly — a country name, a number, a date — are not keys and are
not expected in the index.

The language is switched with the button in the main menu; the list is collected from every
loaded table, modded ones included. The console command `loc <language>` switches the language
and shows how many keys are translated and which are left.

**Plurals.** Russian counts in threes — 1 division, 2 divisions, 5 divisions — and English in
twos, and no translation can fix that if the form was chosen in the source. So the number chooses
a **key** and the table chooses the words: a string with a count in it exists in three variants —
the key itself, the key with `|few` and the key with `|many`.

```json
{
  "{0:N0} division selected.": "Выбрана {0:N0} дивизия.",
  "{0:N0} division selected.|few": "Выбрано {0:N0} дивизии.",
  "{0:N0} division selected.|many": "Выбрано {0:N0} дивизий."
}
```

Which rule a language uses is declared by the language itself, in its own table, beside
`language.name`:

```json
{ "language.name": "Polski", "language.plural": "slavic" }
```

Available are `one-other` (English and most languages, the default), `slavic` (Russian,
Ukrainian, Polish, Czech) and `single` (Japanese, Chinese, Korean, Turkish — the word does not
change). A mod adding a new language writes one line and touches no code.

**Numbers.** How a number is written — a decimal comma, a space between thousands — belongs to
the language, not to the Windows the game runs on. The language names its culture beside its name:

```json
{ "language.name": "Deutsch", "language.plural": "one-other", "language.culture": "de-DE" }
```

The game switches to it as the language is chosen (a table that names none is written in the
invariant culture). The compact headline figures — `$1.43T`, `1.4B`, `73.5%` — are written one way
in every language on purpose; the culture is for everything else: counts, exact figures,
percentages in tooltips and every `{0:N0}` in a translated line.

**Starting a translation in one command.** In the console (`` ` ``):

```
loc german template
```

writes `user://mods/translation_german/` — a `manifest.json` and
`localization/german/00_german.json` holding every line the game has: what the language already
translates is kept, the rest arrives in English, and `language.name`, `language.plural` and
`language.culture` are at the top to be filled in. Translate the right half of each line, restart,
and the language is on the selector. An existing file is never overwritten.

**Letters the shipped face lacks.** Chinese, Japanese, Arabic and Hindi need a face that has them.
The translation mod declares one as the `ui.default` role in its own `fonts/` folder (the format is
in `game/fonts/00_fonts.json`) and ships the font file beside it.

**What the bench checks.** Every loaded language, on every run — `SimBench --loc` runs the check
alone in seconds: the language names itself (`language.name`); its plural rule and its culture
exist; and no translated line asks `string.Format` for an argument its English line does not pass,
or has a brace that does not close. None of those is a crash — `Loc.Format` catches them — which
is exactly why they are checked: the player would read `{2}` or the raw pattern, and nobody would
find out.

### Interface: colours as data

Every interface colour used to be a literal in code — 155 of them, scattered across 21 files,
among them a couple of dozen nearly identical shades of grey. They are all named tokens now, in
`game/interface/skin.json`, and a mod overlays its own layer exactly as it would over any other
content:

```json
// my_mod/interface/skin.json
{
  "colors": {
    "text.accent":        "#7a4b1e",
    "surface.background": "#e8dcc3",
    "map.at_war":         "#a63b32"
  }
}
```

Tokens are named by role rather than by appearance: `text.caption`, not `text.grey`. From which
the main property follows — overriding one `text.caption` changes **every** section heading in
the game at once, because the base game has already agreed with itself about what a section
heading is.

The token families:

| Family | What it sets |
|---|---|
| `text.*` | the five-step text scale: `bright`, `body`, `body_soft`, `muted`, `hint`, `faint` plus `accent`, `accent_dim`, `caption` |
| `state.*` | meaningful colours: `positive`, `negative`, `warning`, `info`, `critical` — for meaning only, never decoration |
| `surface.*` | the ground, panels, insets and rules |
| `button.*` | every state of a button, ordinary and accented |
| `map.*` | map modes: politics, diplomacy, war, value gradients |
| `globe.*` | the sky and labels of the 3D globe |
| `military.*` | formation counters and battle markers |
| `series.*`, `chart.*` | the chart palette |
| `goods.*`, `building.*` | content colours for commodities and buildings |

The rules are the same as for the rest of the content: list only what you change; an unknown or
broken token costs one line in the mod's error list rather than the whole layer; an unknown token
in code is drawn bright pink, so a typo is visible on screen rather than crashing the game. The
full list with base values is in `game/interface/skin.json` — that is the file to copy. A
ready-made template: `mods/_templates/interface_reskin`.

The console command `skin` shows which tokens mods have moved relative to the base; `skin text`
prints one family with a colour swatch on every line.

### Panel layout

Colour became data earlier; geometry remained sixty-four assignments of anchors and offsets
smeared across a dozen files — a mod that wanted a wider Ledger or the army list on the right was
forced to fork the interface.

A panel is now **a name and a rectangle**, and a rectangle is data. `interface/skin.json` has
gained a `layout` section:

```json
"layout": {
  "modal.ledger":   { "anchors": [0.03, 0.08, 0.97, 0.94] },
  "hud.army_panel": { "anchors": [1, 0, 1, 1], "offsets": [-500, 140, -14, -190] }
}
```

Four anchors are fractions of the screen, four offsets are pixels; the order is Godot's: left,
top, right, bottom. Anchors and offsets are independent: a panel that only needs to be wider can
list the offsets and inherit the anchors.

Twenty names cover the whole interface: `hud.top`, `hud.navigation`, `hud.side_panel`,
`hud.army_panel`, `hud.peace_panel`, `hud.outliner`, `hud.alerts`, `hud.map_controls`,
`hud.hotseat`, `hud.network`, `menu.main`, `picker.*`, `modal.*`. The console command `skin hud.`
prints them with their current rectangles.

**What the layer does not do:** a panel's contents. The layout file can move, stretch or remove a
screen the base game builds, but it cannot invent a new one or add an element to an existing one.
That needs a C# mod.

### A region's first day

`history/states/2024/` holds every region of the 2024 world as the bookmark opens it: one file per
country (`RUS.json`, `USA.json`, ...) and `unclaimed.json` for land nobody owns. A region is one
entry, keyed by its id - the one `common/states` declares; its ISO code works too where the code
names exactly one region:

```jsonc
// Tatarstan
"ru_ta": {
  "population": 4004809,
  "urbanization": 0.77,
  "cultures": { "tatar": 0.53, "russian": 0.40, "chuvash": 0.03, "udmurt": 0.01 },
  "buildings": { "Agriculture": 1, "CivilianIndustry": 2, "Infrastructure": 2, "ArmsIndustry": 1 },
  "resources": { "Oil": 3.1, "NaturalGas": 0.4, "Timber": 1.2 }
}
```

| Field | What it is |
|---|---|
| `population` | People on 1 January 2024. A country is the sum of its regions: raise one and the country rises. |
| `urbanization` | The share living in towns, 0 to 1. It decides which trades the region's people follow. |
| `cultures` | Peoples by id from `common/cultures`, as shares (normalised). Each people's faith comes from its entry there. A later file's list replaces the whole mix. |
| `buildings` | Counts by id from `common/buildings`. Merged key by key; `0` removes a building. |
| `resources` | Deposits by good id from `common/goods`. Merged key by key; `0` removes a deposit. |
| `literacy`, `standard_of_living`, `radicalism` | Optional, 0 to 1. Left out, the region takes its country's figure with a spread of its own. |
| `war_damage` | Optional, 0 to 1: how much of the region a war has already wrecked on the first day - see *War damage*. |
| `owner` | Only `"none"` - see *Land that belongs to nobody*. |
| `controller` | Optional: the tag of a country that holds the region on the first day without owning it - Israel in the Gaza Strip. Every province of the region opens occupied by it, striped on the map, and from then on who holds it is the war's business. Written by region because province ids change whenever the map is recut. |

**What is written is what the region has.** A building or a deposit left out of a region's record
is not there. A field left out altogether is worked out from the country: the peoples as its
primary culture, the farms and factories from its people (`SeedPeoplePerFarm`,
`SeedUrbanPeoplePerIndustry` in `common/defines`), the deposits from its geology - which is what
happens to a region a mod adds to the map. The regions are read when the world is first
populated, not applied afterwards, so a region's peoples, trades, farms and factories are dealt
from its own record.

**A later file wins, key by key.** Files are read in name order, and a mod's `history/states`
files after the base game's. To change one number, write that one number:

```json
{ "ru_mow": { "population": 21500000 } }
```

**The file is where to find a region, not who owns it.** Who owns a province is
`history/provinces/ownership.csv`. When the map is attached, a region a border runs through is cut
into pieces - occupied Donetsk, northern Cyprus - and each piece takes its share of the region's
people, buildings and deposits; the pieces need no record of their own.

**Whose state it is** is `history/countries/2024/30_peoples.json`:
`"RUS": { "primary_cultures": ["russian"] }`. Everyone else living in a country is a minority of
it, which is what assimilation works on and what radicalises a region governed by somebody its
people are not.

**A whole country at once.** A mod may still describe a country's peoples in `history/pops/` - a
composition per country, with `"where"` naming the regions a people lives in - for the regions
whose own record lists no cultures.

The head-counts are estimates worked out from the map by `Tools/RegionPopulation` (Natural Earth
populated places, public domain, spread over the land around each town; the countryside weighted
by ground and by how settled the country around it is). Most regions on this map merge several
real provinces, so no official table fits them; against regions whose population is known, the
typical figure is within a quarter of the truth. The tool now fills only regions no file gives a
figure for, into `zz_population.json`. `SimBench --write-regions <folder>` writes the regions of
the world as it stands in this shape, and `--dump-regions <file>` writes the dealt world as a
table, to compare two versions of the data line by line.

Farms and factories are counted for the whole country (`SeedPeoplePerFarm`,
`SeedUrbanPeoplePerIndustry` in `common/defines`) and dealt to its regions - farms by where its
countryside is, factories by where its towns are - so the total does not depend on how finely the
map cuts the country.

The starting `active_personnel` from `history/units/2024` is expanded into independent divisions
of a standard size. At runtime each division holds `personnel`, `max_personnel`, `experience`,
`equipment` and `cohesion`, plus reconnaissance, engineer and artillery support. A visual stack
is not a saved entity: it groups a country's divisions in one tactical province afresh each time.
`ArmyGroup` is a separate headquarters level for a name, a commander and the assignment of a
shared plan.

### The war score

A war's score runs from -100 to 100, attacker-positive, and is seven parts, each worked out for
both sides and netted (`Scripts/Simulation/WarScoring.cs`, numbers in
`common/defines/16_war_score.json`):

| Part | Up to | What it is |
|---|---|---|
| occupation | 50 | the share of the other side's worth held: a region counts by its people and its industry, not its province count, and in a coalition each member counts by its GDP |
| capitals | 12 | how much of each leader's capital region the other side holds |
| armies | 15 | what is left of each side's field army against what it went to war with |
| battles | 12 | the casualty ratio |
| war goal | -10 to 20 | how much of the region the war was declared for the attackers hold |
| momentum | 15 | a point a month to whoever holds the war goal, and to the defender once the attacker has held nothing of it for half a year |
| home front | 10 | the difference in war support |

A side has **collapsed** - and the score is 100 against it - when 90 % of its worth is occupied,
or 60 % of it with no more than 15 % of its army left. Taking all of a country is a won war,
not a lead of seventy points. The war ledger shows every part.

### Literacy and human capital

`history/countries/2024/10_statistics.json` gives a country its `literacy`, the share of its people
who can read, and that is the literacy the game starts with and shows: 99 % for Russia, 37 % for
Niger. The `standard_of_living` and `stability` written in `history/countries` stand as well. A
country that states none of them has them worked out from its GDP.

What productivity, research and a region's stability read is the country's **human capital**
(`Nation.HumanCapital`). On the first day it is the literacy the economy's development implies,
`0.42 + 0.10 x log10(GDP in billions)` held between 0.32 and 0.98. From then on it moves by exactly
as much as literacy moves: schools, a university, an education reform or an event that adds
literacy still pay, and a country that is literate and poor does not produce like a rich one. The
gap between the two is `LiteracyOffset`, saved with the country.

The reason is the balance. A literacy rate is near its ceiling in most of the world, and how fast
countries research, how productive their plants are and how settled their regions are was tuned
against the development figure when it was the only literacy the game had. Over 24 seeds of the
Russo-Ukrainian war, reading the literacy rate straight cost Ukraine 18 of its 161 provinces; read
as human capital, nothing (75.4 held on average before, 74.8 after).

Literacy itself is what triggers read (`min_literacy`, the `poorly_schooled` trigger), and so do the
AI's choice of a university and the governments that ask for a literate country.

### War damage

Every region has a `war_damage` from 0 (untouched) to 1 (ruins). Each day of battle in one of its
provinces adds to it: more for a bloody day, shared over the region's provinces, and less the closer
the region already is to ruins. Every economy week a damaged region:

- makes less: its output is multiplied by `1 - WarDamageOutputLoss x damage`;
- lives worse: its standard of living falls a little;
- empties: a share of its people leave, most of them for the regions of their own country the war
  has not reached (by how many already live there) and the rest for the neighbours at peace,
  keeping their people and faith. Those who stayed in the country are counted, by people, and come
  home a share a week once the region is rebuilt and safe;
- is rebuilt: faster with infrastructure and with the `reconstruction_speed` modifier.

A country can also **pay to rebuild** one region: the region card's *Rebuild*, or the AI for its
worst ruin once it can spare four times the price. For half a year the region is then rebuilt
three times as fast. That holds only while no battle is fought in it (nobody rebuilds under
fire) and only as far as the country's market has the industrial materials it wants. The price is
a quarter of the region's share of GDP (by its people) times the damage.

**Occupation** costs a region too. The part of it the enemy holds makes nothing for its owner, so
its output is multiplied by `1 - OccupationOutputLoss x the occupied share`. A factory behind the
enemy's line does not deliver to its owner's market. Until then occupation had moved only
stability and war support, and Ukraine went on producing in the provinces Russia held.

The numbers are in `common/defines/14_war_damage.json`. The region card shows the damage, and its
tooltip says what the damage costs and how fast it comes back.

| Where | How |
|---|---|
| an effect (event, decision) | `"state.war_damage": 0.2` adds a fifth in every region of the country; `"state.war_damage_set": 0` clears it |
| a trigger | `"min_worst_war_damage": 0.15` asks about the worst-hit region, `"min_state.war_damage"` about the average |
| a modifier | `"reconstruction_speed": 1.0` rebuilds twice as fast |
| a region's history | `"war_damage": 0.4` opens the scenario with the region already in ruins |

The base game uses them in the **Reconstruction programme**. It is a decision for a country with a
region at least 15 % in ruins (the `war_torn` trigger). It gives a spirit that rebuilds twice as
fast for two years, and the AI takes it mostly at peace.

### Movements

A movement is a country without land: a tag, a treasury, relations, spirits and decisions, and a
player who can be it - but no ground. It has a headquarters instead, a medallion on the map inside
somebody else's country; a following among its peoples, region by region; cadres; standing; and a
host whose patience it spends. They live in `common/movements/*.json` - the header of
`00_movements.json` lists every field and trait - and later files win key by key, so a mod adds a
movement with a key of its own and changes one by naming it again with only the fields it changes.

- **What it does.** Moves its headquarters, campaigns, fundraises, recruits, seeks recognition,
  sends fighters into a region ahead of a rising (days on the road by distance), and rises. A
  rising that succeeds makes it a country with a capital and militia - out of its cadres, its
  hidden fighters and its street, one formation per `MovementMilitiaPerSupporters` supporters -
  and a war with the country it rose against. Losing all of it makes it landless again.
- **What the world does about it.** Any country - the AI and the player - can recognise it, fund
  it, list it as a terrorist organisation, crack down on its following, expel its headquarters or
  offer it asylum. Opinion reasons are in `common/opinion_modifiers/30_movements.json`.
- **Traits** change its rules: `armed_wing`, `tunnel_network`, `charity_network`,
  `diaspora_lobby`, `street_patrols`, `hospitallers`, `recognised_sovereign`,
  `government_in_exile`, `humanitarian` (its AI never takes up arms), `nonviolence`,
  `refugee_camps`, `for_hire`.
- **Exile.** A country that loses the last of its ground may go on as a government in exile,
  made from the entry marked `"template": true` (`government_in_exile`): its peoples, homeland and
  name are filled in from the country itself. Its host is the friendliest country that did not
  take its land, and its friends recognise it. The chance is `MovementExileBase` plus stability,
  friends, size and `MovementExilePlayer` for a player's country.
- **The end of one.** A movement with nowhere to stand for `MovementDissolveHomelessWeeks`, or
  with no cadres, money or following left, is dissolved; its armies are disbanded.
- **Events** carry `"for": "movements"`, `"countries"` or `"all"`; the effects language reads and
  writes `movement.cadres`, `movement.legitimacy`, `movement.heat` (the host's temper) and
  `movement.support`.
- **Flags.** A movement's real flag, when it is the flag of an armed group, is
  `gfx/flags/flag_<tag>_insignia.png`, shown only with the `movement_flags` setting on.
- The numbers are defines named `Movement*` (see `Scripts/Simulation/MovementRules.cs`), set from
  any `common/defines` file.

### Peace, the status quo and war weariness

The AI writes the land of a treaty region by region from its own border outwards
(`PeaceShape`): every region it asks for touches ground it holds or has just asked for, islands
excepted, and any part of the loser the treaty would cut off is taken too when it is small or
given back otherwise. What it wants is what countries say when they take land - our people live
there, it was ours, it gives us the sea, it keeps them from our capital, it is where the plants
are - and each war carries one of those arguments as its own.

`StatusQuo` is the peace of a deadlock: each side keeps what it holds securely, each drawn into a
border of its own, and a five-year truce. A white peace is for a war where nobody holds anything.
A war whose score has moved less than `WarStalledScore` in half a year is standing still; after
`PeaceWearinessAfterDays` every year adds `PeaceWearinessPerYear` (up to `PeaceWearinessCap`) to
what both sides will accept, the
countries fighting it take the `long_war` and `exhausted_by_war` modifiers
(`common/modifiers/30_war_weariness.json`), and a war with nobody's player in it that reaches
`PeaceForcedEndDays` ends in the status quo, or in a white peace when neither side leads by
`PeaceForcedEndLead`.

### The console and mod diagnostics

The console on `~` exists for checking content without special UI buttons. `tag`, `observe`,
`focus`, `teleport`, `mapmode`, `province`, `armies`, `fronts` and `sim` let you get to the
scenario you want quickly and check the result. A new command should stay local, change the
campaign deterministically through its public API, and return a readable error string rather than
letting an exception out.

The commands are declared in a table in `Scripts/ui/DeveloperConsoleCommands.cs`, and `help` is
printed **from it**: a command that exists is documented, and a documented one exists. Adding a
command is one `Add(...)` call.

The console itself is deliberately English — it prints type names and syntax, and the bench skips
its files when checking localisation. The exception is the `FUN` category in
`DeveloperConsoleExtras.cs`: a joke in a language you cannot read is not a joke, so those strings
go through the table like any other content.
