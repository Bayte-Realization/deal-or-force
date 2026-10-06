# The game's technical architecture

*Русская версия: [ARCHITECTURE_RU.md](ARCHITECTURE_RU.md)*

## The main border

The project is split into two layers:

```text
Godot presentation                         Engine-neutral simulation
Scenes/Main.tscn                           Scripts/Simulation/*.cs
DealOrForceGame + UI partial classes   ->    Campaign + systems + commands
GlobePresentation / shaders                no Godot dependency
input, sound, drawing                <-    state snapshots + Changed event
```

The simulation does not depend on `Godot.Node`, on the frame rate or on the scene. Which is
why the same `Campaign` can be tested without graphics, used in a future server-side
simulation, and reproduced from the same seed. Godot handles input, display, sound and the
lifecycle.

## Lifecycle and the tick

1. `DealOrForceGame._Ready()` builds the interface and discovers mods.
2. `Campaign.CreateBootstrap(seed)` creates the base scenario.
3. Data mods override defines and countries; then the C#/GDScript callbacks run.
4. The base layer `game/common/defines/military.json` is loaded into a typed `Defines`
   before the scenario is created; mod defines are applied on top of it.
5. The campaign stays paused until the player decides otherwise.
6. `_Process()` converts real time into game days, at most four days per frame so the UI
   does not freeze.
7. After a day, the mods' `on_day_advanced` runs and the presentation updates through
   `Changed`.

The order inside `Campaign.Tick()` is part of the rules: commands → calendar → logistics →
army → navy → war and battles → construction → **modifiers** → opinions → economy →
**diplomacy** → statecraft (AI) → events → the weekly history → UI. Logistics comes before
combat on principle: today's encirclement has to affect today's battle. Modifiers come
before the economy for the same reason — a country's condition must already apply when this
week's output is computed. Diplomacy comes **after** the economy and **before** the AI,
because a decision about war reads the trade shares the market computed on that same day.

Every pass is wrapped in `SimProfiler.Measure`, so both the console (`sim`) and the headless
bench can name the system that spent the time instead of diagnosing "the simulation is slow".

## The economy: output from the bottom up

GDP is not an independent number growing at an annual rate beside the simulation. It is
derived from what a country actually produced:

```
buildings × staffing × productivity
        ↓
   goods produced, by region             (MarketCore.CalculateNationalBalance)
        ↓
   world clearing of supply and demand → prices
        ↓
   GrossOutput = Σ production × price
        ↓
   GDP ← converges to GrossOutput × 52 × OutputScale   (EconomySystem.Tick)
```

Three things follow that were not true under a "GDP grows by itself" model:

- **population is an economic input.** `MarketCore.JobsDemanded` counts how many workers a
  region's buildings ask for, and `WorkersAvailable` counts how many there really are, given
  the working-age share and the employment of pop groups. A country whose industry has
  outgrown its workforce runs below capacity rather than creating output from nothing;
- **policy acts through production.** Taxes, stability, war, shortage and the cabinet's
  course no longer move GDP directly — they move `Productivity`, which enters production.
  The causal chain runs one way;
- **shortage has a price.** A lack of raw material or energy lowers output rather than
  "penalising GDP".

**Calibration.** `EconomySystem.CalibrateOutput` runs once when a campaign is created, after
the regions, buildings, population and armies exist. It runs one settled market pass and
remembers an `OutputScale` per country — the multiplier that turns a week's output into the
GDP the scenario was written with. Which is why day zero still reads exactly as 2024
(verified: 0.00 % deviation across every large economy), and everything after it is
emergent. The report is printed to the log at startup and is available with the console
command `economy`.

`OutputScale` is stored in the save. A save written before the move to emergent output
arrives with a zero — such a save is not rejected but recalibrated on load, against the GDP
recorded in it.

## A map of the code

- `SimulationCore.cs` — the calendar, the RNG, the country, the Campaign and the command
  queue.
- `ScenarioCatalog.cs` and the scenario data — creating the world.
- `ProvinceTopology.cs` — province adjacency and controllers.
- `LogisticsCore.cs` — supply paths, sources, throughput.
- `MilitaryCore.cs` — armies, orders, movement, manpower reserves.
- `CombatCore.cs` — battles, width, reinforcements, losses.
- `WarCore.cs`, `PeaceCore.cs` — goals, score, capitulation, treaties and truces.
- `EconomyCore.cs`, `PopulationCore.cs`, `ConstructionCore.cs` — the economy, population
  groups and construction.
- `MarketCore.cs` — production, demand, prices, tariffs and trade stances; twenty goods, ten
  of which are links in production chains.
- `TradeCore.cs` — who sells to whom. A bilateral trade network: shortfalls are matched
  against the real surpluses of real countries, ranked by reachability, relations and
  prohibitions. This is where `Nation.TradePartners` comes from — the share of a country's
  economy that runs through each partner, which is the price of invading your own customer.
- `Statecraft.cs` — the reason for war, reputation, coalitions and calling in allies. An
  alliance answers with its own declaration of war through the normal path, so nothing
  downstream has to know about wars with more than two sides.
- `AiJudgement.cs` — the balance of a war: what it is worth and what it will cost, plus
  patience (a war plan) and actions instead of war (sanctions, guarantees).
- `PeaceOffers.cs` — a peace offered to the player rather than imposed on them.
- `CampaignFingerprint.cs` — one number for the whole world, for comparing network nodes.
- `TerrainCatalog.cs` — terrain as a catalogue rather than an enumeration: a mod adds a type
  without touching code.
- `HistoryCore.cs` — bounded time series per country for the Ledger, diagnostics and saves.
- `SaveSystem.cs` — a versioned campaign snapshot, checksum, compression and atomic slot
  replacement.
- `DealOrForceGame.cs` — UI composition and the game loop.
- `GlobePresentation.cs` — the 3D/2D map, borders, labels, armies, fronts, the peace brush
  and the free camera.
- `ArmyPanelUI.cs` — the army panel: the division list, bars, counters and battle plans.
- `PeaceConferenceUI.cs` — the negotiating table: the map brush, the schedule and the border
  preview.
- `ArmyOperationsUI.cs`, `WarPeaceUI.cs`, `EconomyUI.cs` — the working panels.
- `LedgerUI.cs`, `StrategicCharts.cs` — interactive analytics with no game truth of their
  own.
- `InterfaceSkin.cs`, `UiTheme.cs` — named interface colour tokens and their binding to
  Godot.
- `Format.cs` — how numbers read to a player: compact sums, populations and signed changes.
- `StatePartition.cs` — annexing provinces and splitting regions along a new border.
- `PeaceConference.cs` — a written peace: the demand, the price, a refusal with a reason, and
  signing.
- `AiStatecraft.cs` — weekly diplomacy, a monthly budget and an eleven-day decision about
  war; judging the war itself lives in `AiJudgement.cs`.
- `ProvinceOwnership.cs` — the political map as data: the owner and the controller of every
  province.
- `Scripts/Modding` — the public contracts and the mod loader.

`DealOrForceGame` is split into `partial` classes: one C# type spread across files by
subject, so that the army does not get mixed up with diplomacy and sound.

## The interface layer

The interface is assembled in code but does not look like code. Between them stands
`InterfaceSkin` — a table of named tokens (`text.caption`, `map.at_war`,
`button.primary_fill`), engine-neutral like the rest of the simulation: it holds strings such
as `#e7c966` and knows nothing about Godot. Only `Ui` (`Scripts/UiTheme.cs`) turns them into
a `Color`, with a cache that is dropped when mods reload. That separation lets the headless
bench check a mod's skin without rendering anything.

Before it, 21 interface files held 155 colour literals, among them about twenty nearly
indistinguishable greys and a dozen and a half shades of gold — each introduced separately,
for one particular caption. There are now none: the palette is reduced to five steps of
text, one accent, five meaningful colours and the families of the map, the globe, the
counters and the charts. That is modding and ordinary coherence at once — you can only
rename a role once the game has agreed with itself about what "a section caption" is.

`Fmt` (`Scripts/Simulation/Format.cs`) decides how numbers read. The US economy is 21,433.2
billion; written that way it is nine digits the eye has to recount before they mean
anything. The HUD writes `$21.43T` and gives the exact figure in a tooltip. The compact forms
use the invariant culture, so digits do not change their separator along with the OS locale;
the expanded forms in tooltips do the opposite and use the user's culture.

The top bar keeps a rolling week of daily snapshots of the treasury, GDP and population
(`Trend`), so under every value there is a real movement rather than just its current level.
The game speed and the open screen are highlighted by the same `StyleButton` function that
draws ordinary buttons — the accent stopped being a property of a button and became a state.

## Commands and responsiveness

Input is handled immediately, but a change to the rules is expressed as an
`ICampaignCommand` and goes into `Campaign.Commands`. On the next game day the commands are
applied in a stable order. Long computations do not belong in a mouse handler.

This gives an instant visual reaction, a reproducible result, and the basis for replay and
for the lockstep multiplayer. A command holds IDs and values, never Godot nodes.

## The military model

An army has a province, a route, an order, numbers, organisation and supply parameters.
Front orders use the adjacency graph, not screen coordinates.

The logistics BFS starts in a country's interior and passes only through friendly control.
Distance, infrastructure, barracks and troop concentration produce a supply level. No path
means the army is cut off.

A reserve sits in depth, recovers faster and can join a neighbouring battle. A spearhead
prefers the province it was given, then the war goal. Reinforcement spends
`Nation.ManpowerReserve` and is bounded by `ReinforcementCapacity`.

`UnitTypeCatalog` reads `game/common/units/unit_types.json`. The same type drives the
counter's symbol, speed, combat width, attack, defence and supply consumption, so the UI and
the simulation cannot disagree. `ArmyStance` and accumulated `Planning` belong to
engine-neutral state; the UI changes a posture only through `SetArmyStanceCommand`.

The same contract is used by `BuildingCatalog`, `CommodityCatalog`, `DecisionCatalog`,
`EventCatalog` and `TerritorialTopology`. The base JSON is loaded from `game`, then
`ModLoader` applies the active mods in the computed load order. Mutable catalogues are reset
before a new campaign, so enabling and disabling mods is reproducible without restarting the
process.

`RegionInteractionUI` is only a projection of state. Construction, recruitment and splitting
formations go through `QueueConstructionCommand`, `RaiseArmyCommand` and `SplitArmyCommand`;
the UI keeps no second copy of the economy or the troops. A right click issues
`MoveArmyCommand` with `updateSelection: false`, so the operation does not spoil the
analytical context of the selected province.

Front orders survive a moving border. After a `MapRevision` and a front rebuild,
`FrontPlanner.RefreshAssignments()` checks the current and target sectors of every assigned
army and lays a new friendly route only for those whose sector has genuinely gone stale.
That avoids daily pathfinding for the entire army.

`SetArmyPlanExecutionCommand` separates laying out an offensive plan from launching it. A
player's automatic deployment creates a prepared plan, while the AI executes the plans
assigned to it at once. The state stays in the engine-neutral model and is therefore
compatible with the deterministic command queue, replays and saves.

After a victory, `ProvinceController` changes immediately and raises `MapRevision`, so the
map's colour changes in the same presentation cycle. `ProvinceOccupation` is responsible for
consolidating control for the treaty.

## Performance

- The simulation does not create a Godot node per economic object.
- Heavy economic subsystems run less often than the daily tick.
- The map uses a revision/fingerprint and rebuilds a layer only when the data changes.
- A catch-up day limit prevents a long stall.
- `SimProfiler.Measure()` marks the critical stretches.
- `CampaignHistory` records one compact structure per country per week and deletes points
  older than 780 weeks.
- Collections in the hot path should be reused; LINQ belongs in bootstrap and UI, not in a
  deep loop over every province every day.
- **A cache that lives between calls is tagged with the campaign it describes.** That is not
  a stylistic rule but a fixed bug. Several systems cached expensive answers — who borders
  whom, what each country's army is worth, how far apart the capitals are — tagging the cache
  with a day, a map revision or a country count. Each tag is correct for exactly one world,
  and a network client rebuilding from a snapshot holds two: the new one and the old one the
  collector has not taken yet. Whichever ticks first sets the tag; the second reads somebody
  else's answers and quietly diverges. The check `two worlds in one process do not read each
  other's notes` failed before the fix and passes after it.
- **A market week must not make rubbish.** The national balance asked four questions of four
  global lists once per country, each through LINQ with a capturing closure; the trade pass
  walked all 1,826 regions once per country and allocated an object for each of 2,200
  routes. One pass per list and a pool for the routes: 220 kB of garbage a week → 10 kB, and
  the whole day 288 → 258 kB. What is separately instructive is what did **not** work: a
  cache of willingness to trade, keyed by country pair, should have been free and instead
  made the pass twice as slow — a dictionary of twenty thousand long keys, grown from
  nothing every week, costs more than two multiplications.
- **Regions are indexed by owner.** `Campaign.StatesOf(nation)` returns a ready array instead
  of filtering the whole list of 1,720 regions. Every system that works per country used to
  do that — the economy alone twice per country per weekly tick, which is on the order of
  900,000 comparisons and an array per country for an answer that does not change during a
  campaign (conquest moves provincial control, not regional ownership). The index is built
  once and dropped only when the region list is replaced — on scenario setup and on loading a
  save. Measured on the bench: the economic pass 0.62 → 0.30 ms, that is 2×, and the whole
  day 0.99 → 0.85 ms.

## The headless bench and the checks

`Tools/SimBench` compiles `Scripts/Simulation` into a separate
console project **with not one reference to Godot** — the fifth item of the performance
contract ("a full campaign tick must run outside a Godot node") made executable: if anybody
brings Godot into the simulation layer, the project simply stops building.
WorldMapSource/WorldMapTables read external PNG and JSON/YAML data; the original
codec is retained only in the offline SourceMapMigration tool, not the game assembly.

```bash
dotnet run --project Tools/SimBench/SimBench.csproj -- 730
```

It creates a campaign, runs the given number of days, prints a breakdown by system and
checks invariants you cannot see by eye: that day zero reproduces the historical GDPs, that
every country has a calibration, that the ownership index agrees with a linear scan, that
scripted effects really apply, that modifiers are granted, refreshed and removed, and that
no GDP has gone NaN or negative. The exit code is non-zero on failure, so it is a ready-made
CI step.

The very first run found a real import bug: in the country table, `-99` means "no data", and
the import took it literally — the Vatican, Antarctica and a couple of dozen dependent
territories were given a **negative economy**. Such an entry now gets a plausible GDP derived
from its population.

## Land combat

Combat is two-sided, in the shape Hearts of Iron uses. Each side both shoots and defends at
the same time, but with different characteristics: the attacker is covered by
**breakthrough**, the defender by **defense**. From which it follows that a division built
for a breakthrough survives a counter-attack it would not have survived on the defensive,
and an entrenched division holds an assault it could never have carried out itself.

Damage is computed like this:

1. **Combat width.** A province offers 80 units of front and a division takes 20–30. Four
   fit; a fifth waits its turn. Attacking from several provinces widens the front
   (`combat_width_per_direction`), and over-stuffing is punished (`over_width_penalty`). That
   is exactly why a smaller but better army can hold a line.
2. **Hardness and armour.** The target's `hardness` decides which of the two attacks the
   enemy uses — `soft_attack` or `hard_attack`. `piercing` against `armour` decides how much
   of it arrives at all: fail to pierce and it is halved.
3. **Pressure.** The attacker's fire is divided by the defender's defence and vice versa.
   Both sides lose organisation according to the pressure on them.
4. **Losses.** Organisation goes first and men go slowly. A formation at zero organisation
   that keeps standing starts coming apart (`broken_casualty_multiplier`).

An empty province is not an open road. Divisions in neighbouring provinces cover the gap with
fire: it can only be taken if the attacker is worth more than the cover
(`gap_exploit_advantage`), and even then slowly (`zone_of_control_resistance`). Without that,
a front turns into a race rather than a front.

Every number lives in `common/defines` and `common/units/unit_types.json` — that is, in data.

### How this is verified

The bench attaches the same province map the game uses, so logistics, fronts and combat
really do execute (they used to show 0 ms because they were not running at all). The scenario
opens in the third year of the Russo-Ukrainian war, and that is the hardest test of the
combat model: over the first year, with no player at all, the line must not move more than 8
provinces out of 151, the war must not end by itself and must not freeze — it has to cost
both sides very dearly.

A 365-day measurement: the line at 143…158 from a start of 151, losses of 424 thousand on the
Russian side and 274 thousand on the Ukrainian, and 104 battles. That is close to real 2024
estimates — and it came out of the model rather than out of tuning that one war.

## Splitting regions along a new border

A peace treaty used to move only provincial **control**. Regional ownership never changed: a
country could hold half of somebody else's region for a decade while the enemy's economy went
on counting every person in it. Annexation is the other half of that mechanic.

`StatePartition.Annex` moves provinces into permanent ownership and then rebuilds every
region the new border ran through. A region is cut along two lines: **by owner** and **by
connectivity**. Two pockets of one country that cannot reach each other are two places, not
one.

A piece is not a label but a full region: its own population, its own buildings, its own
social groups and its own deposits, divided in proportion to the share of provinces. Half an
industrial region is half of its industry, and from that day the halves develop separately.

The name stays with the part that remained with the former owner — the way a seceding half
takes a new name and the remainder keeps the old one. New pieces are named by direction
relative to the original region's centre (`Northern`, `Eastern`…), and collisions are
numbered: a player cannot act on two regions with the same name. Dynamic regions get a
`map_index` from 4000 up, so they never collide with authored or modded ones.

A border the world redrew is campaign state, not map data. The raster on disk still shows the
world of 2024, so a save carries its own political layer (the owner and the region of every
province). Without it, loading would silently give every conquest back and leave the region
list describing a map that no longer exists.

Verified by the bench: a region is cut in half, and it checks that the population and
buildings add up to the last unit, that the names are unique, that every province belongs to
a region that exists, and that after a save and a load every province came back where the
world left it.

## What a country does when nobody is playing it

Previously, nothing. The operational AI moved divisions if a war was already on, but a war
never started: `ai_war_chance` was declared in defines and **read from nowhere**. Two hundred
and sixty countries stood in perpetual peace, raised no taxes, built nothing, and neither
quarrelled nor made up with anybody. The only event in the world was the one war written into
the scenario.

`AiStatecraft` is the other half of the AI: the half that decides.

**Diplomacy, weekly.** An opinion of a neighbour drifts towards where the relationship
actually is: a shared border is friction, a trade agreement is warmth, a neighbour twice as
strong is fear. A country takes one action a week towards whichever neighbour it feels most
strongly about in either direction: it courts, signs trade, then a non-aggression pact, then
an alliance — or declares a rivalry. The courting matters: without it, neighbours dislike
each other by default and nobody in the world would ever sign anything.

**Economy, monthly.** An emptying treasury raises a tax; a full treasury with an unhappy
population lowers one. The army costs what the situation costs: 16 % at war, 9 % under
tension, 4.5 % in peace. An unhappy country buys quiet with social spending if it can afford
it. And one project a month, in whichever region needs it most: an army without supply builds
roads, a country at war builds factories, a country at peace builds whatever makes it richer
in a year.

**War.** A country judges every neighbour: the ratio of forces (below 1.45 there is no
appetite at all), hostility, rivalry, and whether the neighbour is already busy with somebody
else's war. Wanting is not starting: the roll is tied to the country and the date rather than
to the general stream of random numbers, because a shared stream meant that a change in how
countries build roads shifted the Russo-Ukrainian front by twenty provinces.

**Peace.** A winning country writes its demand through **the same conference the player
uses**: it takes what it occupies, cheapest first, until it runs into 85 % of its war score.
A country winning on points but holding none of the enemy's land **does not stop** — stopping
would turn a won war into nothing. And the AI does not sit down to discuss the war the
scenario opens with for three years: a war in its third year is a war both governments have
already refused to end.

A measurement over a year with no player at all: **68 wars declared, 14 peaces signed, 104
provinces annexed, 61 trade agreements, 701 construction projects**, taxes from 20 % to 40 %,
90 countries on a war footing — and not one country ate more than a quarter of the world.

## History and charts

`CampaignHistory` belongs to the simulation, not to Godot. Every weekly point holds primary
numeric values: GDP, the budget, population, social indicators, the market, price indices,
military power and war support. The UI only picks series and formats them.

That separation guarantees a chart cannot accidentally fall out of step with the mechanics.
The same data can be used in the AI, in balance telemetry, in replays and in a server-side
simulation. The `StrategicLineChart` and `StrategicBarChart` controls only draw, hit-test
hovers and show tooltips; they do not change the campaign.

Tax and trade policy are expressed as an `ICampaignCommand`. That matters for the current
host-authoritative lockstep, for replays and for mods: the interface does not change a
`Nation` directly.

## How to add a mechanic

1. State the state and the invariants in engine-neutral types.
2. Add the tunable numbers to `Defines`.
3. Create a command for the player's or the AI's action.
4. Put the system into one clear point of `Campaign.Tick()`.
5. Add a compact UI representation and a tooltip with the reason for the result.
6. Add mod data and stable IDs.
7. Check the build, a live scenario and the Fennara validation.

An economic formula must not be written inside a button handler, and game truth must not be
derived from the position of a UI node.

## Saves, mods and debts

`SaveSystem` serialises only engine-neutral campaign state and stable identifiers — never
Godot nodes or visual resources. The format holds its own version, the game version, the seed
and RNG state, and a fingerprint of the active mods. New slots use one `.bdfsave`
file: editable JSON by default or optional GZip, detected by content. The write goes
to `.tmp`, and on success the old slot is replaced atomically. Embedded metadata at
the front lets the menu list slots without reading whole campaigns; old sidecars and
their SHA-256 checksums remain supported on load.

Loading restores countries, regions and pops, the market, construction, armies and fleets,
routes and orders, wars, battles and peaces, diplomatic relations, provincial control and
occupation, events and the weekly history. If the asynchronous map topology is not ready yet,
the controller and occupation arrays are held as pending state and attached once the same
deterministic graph is built. A mod mismatch produces an explicit warning; a bad checksum or
an unsupported version blocks the load before a live campaign is replaced.

Open debts:

- a complete replacement of `provinces.png + definition.csv`, and independent replacement of
  rivers and lakes, are standardised, but a map editor with visual diagnostics is still to be
  built into the game;
- regional membership (which regions a country is made of) is still geometric: a province is
  grown inside one region, so its region is a fact about shape rather than about politics.
  Province ownership has been moved out to `history/provinces`; regional composition has not;
- panel contents are still code: the layout file moves, stretches and hides the screens the
  base game builds, but cannot invent a new one or add an element to an existing one. Colour
  and geometry are data; composition is not;
- the ENet/UDP network mode already uses an authoritative host, unique human seats, validated
  commands and deterministic ticks; the next production step is checksums, automatic resync
  and recovery from losing the host;
- a C# DLL cannot be promised on AOT, Web or consoles, where data and GDScript mods will
  remain;
- the GDScript API is still minimal and should be extended with commands;
- future compatibility across several major save versions will need a chain of DTO
  migrations, though the current format 2 already rejects an incompatible format and map
  contract before replacing a live campaign; the metadata records `MapDataVersion` and the
  province count.

## The military map and deployment

`MapRasterCatalog` reads `default.map`, `provinces.png` and `definition.csv`, checks every
colour against the political layers and hands the IDs to `ProvinceTopology`. The base PNG
holds 38,013 irregular anisotropic provinces and forms a graph of 99,207 land edges. A wide
distribution of area, orientation and elongation removes the even "scale pattern" while
keeping the connectivity fronts need. IDs stay 16-bit, so shader selection, the controller
texture, pathfinding, fronts and saves all use one contract. `Tools/MapDataCompiler` builds
the source set reproducibly and performs an exact PNG round-trip. There is deliberately no
runtime fallback: a corrupted or incomplete base map raises an explicit error and never
creates new unstable IDs during startup.

`MapRasterCatalog` reads the authoritative `rivers.png` and `lakes.png` into the same
`HydrologyRaster` that the presentation and the simulation receive. The GeoJSON in
`map_data/hydrography` is the compiler's source and its fallback.
`ProvinceTopology.AttachHydrology()` derives river crossings only for land edges that already
exist in the graph: a river changes the cost of marching and fighting, but never becomes an
impassable wall. After province relaxation the base map holds 8,759 crossings, 461 river
geometries and 412 lakes.

Once the graph is attached, `StrategicDeploymentPlanner` collects territories and foreign
borders in one linear pass. The armies of active belligerents get an echelon on the real
front; peaceful powers get border cover and a capital reserve. The planner depends on the
live control graph rather than on Godot coordinates, so it works after a mod has changed the
political map.

The visual layer uses the single micro-layers `SurfaceLineRadius`, `SurfaceMarkerRadius` and
`SurfaceTextRadius`. Routes, capitals, fleets, armies, battles and names no longer have
independent inflated radii that made them appear to hover. The flat projection follows the
same contract through `FlatLineZ`, `FlatMarkerZ` and `FlatTextZ`.

Army markers are synchronised by the campaign's structural fingerprint. Raising, splitting or
destroying a formation rebuilds only the counter layer; armies standing together get a
deterministic tangential offset while staying in the same tactical province.
