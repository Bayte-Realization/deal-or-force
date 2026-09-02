# Deal or Force — migration from s&box to Godot .NET

## Why the move is justified

The strategy simulation is C# and deterministic by design; it should not depend on the renderer. Godot owns presentation, input and assets, while the campaign model runs as plain .NET code. That gives the project predictable frame budgeting, standard C# tooling and a clean path to headless simulation tests.

## Current foundation

- Scripts/Simulation/SimulationCore.cs is deliberately free of Godot/s&box APIs. It provides GameDate, stable xorshift RNG, command queue, campaign clock and lightweight profiler.
- Scripts/DealOrForceGame.cs is the Godot adapter. It builds the temporary migration UI and turns UI actions into campaign commands.
- Scenes/Main.tscn hosts the adapter. Run this scene while the main-scene setting is not yet written through the restricted editor bridge.
- DealOrForce.csproj uses Godot.NET.Sdk/4.7.2; its assembly name intentionally matches the project setting: Deal or Force.

## Source mapping

| Old s&box layer | Godot target | Rule |
|---|---|---|
| Code/Sim | Scripts/Simulation | Keep deterministic rules/data; replace Sandbox timing/logging/vector adapters only. |
| Code/Data | Scripts/Data + Data/ | Keep mod definitions and scenario content; load from res:// and user://. |
| Code/Game/GameSession | Scripts/App/CampaignController.cs | Split clock/command dispatch from presentation. |
| Code/Game/MapTexture.cs, GlobeView.cs | Scripts/Map + Godot shaders/MultiMesh | Reimplement; do not emulate the s&box texture upload path. |
| Code/UI/*.razor | Scenes/UI + Scripts/UI | Rebuild as Godot Controls and themes, retaining layout/information hierarchy. |

## Non-negotiable performance contract

1. A click updates local command/UI state in the same frame.
2. Simulation has a fixed daily command boundary and a wall-clock budget per frame.
3. Map redraw, front rebuild and path/supply work are incremental or invalidation-driven.
4. UI never scans every province, army or war to answer a hover/click.
5. Full campaign tick and pure systems must be runnable outside a Godot Node.

## Next migration tranche

1. Port data definitions and localisation, using System.Text.Json.
2. Port World, province/state/army/war models and SelfTest.
3. Load the existing scenario into the new model and compare deterministic snapshots.
4. Add Godot map rendering with a texture atlas + shader and pooled markers.
5. Recreate Paradox-style panels, selection, army orders, war and peace interactions.

## Validation completed

- Godot 4.7.2 MCP session connected.
- .NET 8 SDK installed and C# project compiles with zero warnings/errors.
- The migration scene runs. Its Begin control advances the campaign; a slider change becomes a queued command and is applied on the subsequent simulation tick.

## Milestone 1 — verified base-world import (2026-08-30)

The Godot shell now directly carries the source project’s 2024 catalogue:

- 260 country rows and 1,710 administrative-region rows, copied from the source tables;
- ISO/map IDs, names, population, GDP, continent/region, political colour and capital
  coordinates;
- the original (s&box) `Defines` tuning object;
- KOR selected deterministically, with Seoul and the source-scale initial treasury
  (`$184.80B`) visible in the Godot UI.

The importer actively fails if the row counts change, KOR/its capital is absent, or
Korean GDP ceases to be 1,646.739 billion USD after converting the source's
million-USD input. These are migration guards, not UI assertions.

### Runtime check

Godot C# build: **0 warnings, 0 errors**. The running scene accepted real pointer
input for Begin, speed and spending controls. After nine simulation days, KOR's
treasury remained `$184.80B`; the former synthetic daily-income formula was removed
because it mixed million-USD table values with billion-USD UI values. The portable
clock now advances state without fabricating an economy until the source Economy pass
is ported.

### Next implementation slice

1. Port the source's actual economy initialization/tick and validate initial figures
   against the s&box baseline.
2. Move province topology, controller/occupation and adjacency data before rendering
   the map.
3. Port armies, fronts, orders and supply on top of that topology; UI acts only through
   deterministic commands.

## Milestone 2 — globe visual vertical slice (2026-08-30)

Godot now renders the source project's globe instead of a placeholder panel:

- exact source control/state rasters are copied unchanged and decoded into the political texture; coastlines, country borders and administrative seams are data, not a screenshot;
- the original space background and capital-star asset are used;
- all capitals in the source catalogue are placed as globe markers;
- right mouse dragging orbits the globe; wheel input changes distance; WASD also controls the orbit;
- political texture creation runs on a background task, then hands one finished ImageTexture to Godot on the main thread. The decoded 16.6M-cell source grids are released after that visual build and can be re-decoded later for precise picking.

### Ocean presentation

The source Defines.OceanReliefEnabled is false and remains false in Godot. The copied ocean_vibrant.png is deliberately retained as an asset but **not sampled by the globe shader**. Ocean uses the source scenario colour (#0f4a6b) with Godot-light compensation: dark, saturated, flat water rather than noisy repeated texture.

### Fidelity correction — political-map resolution

The first Godot globe build used a 1536 by 768 convenience texture. That was visibly
softer than the s&box map and was removed. The globe now builds its political texture
at the exact baked-source resolution of 5760 by 2880 (one texture pixel per Earth cell)
and generates mipmaps for stable distant viewing. A focused live comparison around
China, Korea, coastlines and provincial borders confirmed the previous upscale blur is
gone. The runtime keeps its UI responsive because raster construction remains a
background task; no reduced-resolution fallback is used for the final map.


## Milestone 2.1 — exact political picking

The globe now keeps the source control and state rasters resident after the 5760×2880 political texture has been generated. A left click casts a camera ray to the globe, converts the hit to latitude/longitude, then resolves both the nation and state from the original baked cells. This avoids using approximate screen colours or a secondary hit-test image.

The selected administrative region and its tactical province are rendered directly
from 16-bit ID textures. The parent region receives a restrained fill and border; the
province receives the stronger close-zoom outline. Selection therefore matches the
authoritative cells exactly without rebuilding or uploading the political texture.
Right-drag, mouse wheel and WASD navigation remain independent from this input path.

Validation on 30 August 2026: a click at screen point (250, 350) on the Korea-focused view resolved to China (CHN), Shandong (Province). The project compiled with 0 warnings and 0 errors after this change.

Trade-off: retaining the decoded country and state grids increases resident memory, but makes every political selection O(1), deterministic and directly moddable through the source map data. A future streaming pass may replace this with a memory-mapped or tiled representation without changing gameplay-facing picking.


## Milestone 2.2 — topology and close-range fidelity

The renderer no longer applies political corrections as a painted overlay. A new
engine-neutral `TerritorialTopology` pass mutates the authoritative control grid
before the texture and picking tables consume it. The first declared rule repairs the
ABK–RUS coastal connection through the bounded Georgian corridor. It uses a
multi-source shortest-path search plus a configurable corridor width. The repair now
updates both the country-control raster and the administrative-state raster; rendering,
picking, province generation and future adjacency/AI systems therefore cannot disagree.
The wider rule removes the remaining one-cell Georgian halo rather than merely painting
a cosmetic connection.

Close zoom now uses the exact 5760×2880 political texture through a nearest-filtered
detail sampler, blended with its anisotropic mipmapped sampler by camera distance.
This keeps the distant globe stable while preventing bilinear softness at province
scale. Water remains flat, dark and saturated as requested.

Capital and formation billboards now use true hemisphere culling. Markers whose
surface normal faces away from the camera (including a guarded limb band) are hidden,
rather than bleeding through or forming a halo around the globe. Capitals and armies
also have separate distance LODs; the strategic overview stays clean and formations
appear only at operational zoom.

## Milestone 3 — military data and playable HUD shell

The Godot campaign now contains an engine-neutral military vertical slice:

- the source 2024 active-personnel table is copied exactly;
- source constants are retained for division size (15,000), divisions per formation
  (12), formation cap (14), organisation, reinforcement, marching and entrenchment;
- deterministic bootstrap formations are positioned around real capital coordinates;
- each army stores order, organisation, strength, entrenchment, supply, battle and
  geographic state;
- the initial order vocabulary is Manual, Defend front and Advance front.

The deployment around capitals is explicitly temporary. It keeps the imported force
totals playable while province adjacency, fronts and supply graphs are connected; it
must not become the final strategic placement algorithm.

The one-panel migration placeholder has been replaced with the source UI information
architecture: main menu, country identity and resources, campaign date, pause/speed
controls, Nation/Economy/Military/Diplomacy/Wars navigation and a reusable right-side
panel. Panels and buttons use the shared source-derived chrome textures instead of
flat default Godot widgets. Escape closes the side panel, then opens the campaign pause menu. UI
commands mutate local state in the same frame, including while the campaign is paused.

### Live validation

At 1920×1080, real Windows pointer input successfully opened Singleplayer and the
Military screen. KOR reported 500,000 active personnel, 33.3 divisions and three field
armies. Speed 1 changed pause state immediately and advanced the deterministic clock
from 1 to 29 January during the observation interval. The bottom navigation and side
panel remain fully inside the viewport after accounting for the texture's minimum
chrome margins.

The .NET build completes with **0 warnings and 0 errors**. Performance numbers gathered
while the game is background-throttled are not accepted as gameplay measurements;
the next profiling pass must keep the game window foregrounded and capture frame-time
distribution, draw calls and marker LOD counts together.

## Milestone 3.1 — country selection and tactical province LOD

Singleplayer now opens a map-first country picker instead of silently starting as the
bootstrap nation. Search accepts country names and tags, the empty query shows a small
GDP-ranked featured set, and clicking the political globe selects the country directly.
The dossier reports rank/tier, capital, population, GDP, treasury, region count and
ported division total. Random nation follows the source rule of requiring at least three
administrative regions. Small countries receive a closer automatic camera framing.

Country labels are anchored to the centroid-nearest land cell of each nation's largest
connected territorial component. This prevents an island or exclave from stealing the
label. Distance and hemisphere LODs reduce clutter; early limb culling prevents fixed-size
labels from protruding through or being sliced by the globe edge, while long country names
use an adaptive font size.

The map now has three political reading levels:

1. country colours and country borders at strategic distance;
2. the imported 1,710 administrative regions at medium distance;
3. deterministic tactical provinces at close operational distance.

The first tactical generator faithfully reproduced the s&box prototype's capped
four-neighbour flood fill, but its raster-order seeds produced a visible diamond lattice.
After direct inspection of Victoria 3's installed `game/map_data/provinces.png`, the Godot
generator was replaced with an original implementation inspired by the same visual
principles, without copying Paradox data: a full-cycle deterministic seed walk, compact
eight-neighbour growth, Euclidean priority and stable boundary noise. Provinces cannot
cross either a country or administrative-region boundary. The current 2024 raster yields
22,348 land provinces, with smaller target areas in disputed regions.

Runtime construction stays off the rendering thread. Political RGBA, region IDs, province
IDs, province topology and country-label anchors are handed to Godot only when the complete
map build is ready. Live selection was verified on South Korea: one click resolved the
country, North Gyeongsang administrative region and tactical province 15,992.

## Milestone 3.2 — selectable formations and operational orders

Army markers are no longer display-only counters. Once province topology is ready, every
bootstrap formation resolves its geographic post back to an authoritative administrative
region and tactical province. Left-click prioritises the nearest visible formation marker
before the underlying political cell and opens an operational dossier showing divisions,
organisation, strength, supply, entrenchment, region, province and current order.

The player can assign Manual, Defend front or Advance front through a new
`SetArmyOrderCommand`; direct UI mutation of simulation state is not allowed. Manual also
clears assigned war/target/movement state. The selected counter receives a distinct gold
plate tint, while enemy formations remain inspectable but do not expose order controls.

This is intentionally the command-and-selection vertical slice, not a fabricated complete
front system. The next military milestone must construct province adjacency, war fronts and
routes, then make Defend/Advance dispatch formations to those real targets using the same
order vocabulary and command queue.

## Milestone 3.3 — province graph, marching and first real front

The tactical raster now produces a reusable engine-neutral land graph alongside its visual
ID maps. Each of the 22,348 provinces has a land-cell centre, original nation/state identity
and a sorted neighbor list. The current generated world contains 54,426 undirected land
edges. Shortest-path searches are constrained by political ownership in peacetime, so a
right-click cannot silently route an army through foreign land or across water.

Right mouse input distinguishes a short command click from a camera drag. With a friendly
formation selected, a click queues `MoveArmyCommand`; a drag retains globe rotation. Armies
march one graph edge at a time, update province/state/geographic position, clear
entrenchment while moving and expose leg/route progress in the operational dossier. Marker
transforms update only when simulation coordinates change. A gold line strip previews the
selected formation's remaining route without rebuilding the political texture.

The bootstrap campaign now includes the Russo-Ukrainian War. Its front is derived from
attacker/defender province adjacencies, not authored screen coordinates: validation resolves
25 Russian-side and 24 Ukrainian-side front provinces. A close-zoom red line layer renders
the active contact edges, and the Wars panel reports belligerents, front size and war
support. Defend/Advance routes a belligerent's army to the nearest friendly front province.

Non-player belligerents use the same province graph and `SetArmyOrderCommand` as the player.
The deterministic first planner sorts formations by combat power, assigns offensive and
defensive shares, then dispatches them through the ordinary routing contract. Battle,
dynamic controller/occupation changes and peace remain the next milestone; the planner does
not receive teleportation or a separate AI-only pathing API.
### Milestone 3.4 — live land combat and occupation

- Advance-front orders now create deterministic province battles when formations reach contact.
- Battles spend organisation separately from manpower; entrenched defenders gain the calibrated source-game advantage.
- A broken defender retreats, the attacker occupies the province, and the live front is rebuilt from the new controller layer.
- Unopposed advances, occupation security, war losses, support and war score are represented in engine-neutral campaign state.
- A 256×256 province-controller lookup updates captured land on the globe without rebuilding the 5760×2880 political texture.
- Country picking reads live province control, so presentation, input and simulation agree after an occupation.

### Milestone 3.5 — responsive command layer, diplomacy and hotseat

- Box selection uses real projected formation positions and can select several friendly armies in one drag.
- Group orders and right-click movement pass through the same deterministic command queue as single-army orders.
- `Auto-deploy front` distributes the whole force over sorted front targets rather than stacking every formation on the nearest province.
- Improve relations, denounce and declare war operate on persistent bilateral relations; declaration remains a two-step UI action.
- Shared-screen multiplayer supports several human countries, private handover screens and AI exclusion for every human-controlled nation.
- Escape opens an explicit pause screen with Resume, Return to main menu and Quit to desktop; returning to the menu does not destroy the in-memory campaign.

### Milestone 3.6 — exact border fronts, navies and peace

The previous debug front mesh connected province centres and produced the visible floating
"spider web". It has been removed from presentation. A compact province membership atlas now
stores war and side identity; the globe shader compares exact neighbouring political pixels
and draws contact only where opposing members of the same war meet. The line therefore hugs
the imported border at globe height. The same controller atlas is applied before final land
colour calculation, so occupation recolours a captured province in the frame in which control
changes.

Initial naval task forces are now engine-neutral campaign formations with hull count,
readiness and named real-world operating areas. Procedural ship plates keep their graphic
independent from Godot import sidecars. Naval readiness follows military funding weekly and
the Military dossier totals fleets and commissioned hulls.

Peace negotiation supports three outcomes: white peace restores every recorded pre-war
controller; enforced terms preserve the occupation map but require decisive score/support;
capitulation accepts the current occupation. Every outcome closes battles, fronts and assigned
orders and creates a five-year truce which blocks an immediate repeat declaration.

### Milestone 3.7 — strategic command HUD and legible war accounting

- A collapsible strategic outliner now keeps player wars, active battles, priority armies and
  construction visible without opening separate screens.
- The alert strip raises actionable event, battle, supply, idle-construction and decision
  warnings; every alert opens or focuses the relevant object instead of serving as decoration.
- Battle markers are directly selectable. Their dossier shows location, combat day, committed
  formations, combat width, cohesion bars, casualties and the six latest deterministic rounds.
- Combat width limits how much of an oversized formation can contribute in one round. Damage,
  organisation and casualty records now preserve the underlying round values for inspection.
- Every war has a moddable administrative-state objective. War score is exposed as occupation,
  battle, objective and national-support components, while support scales with population,
  casualties and occupied homeland instead of a fixed global loss constant.
- Peace offers expose computed opponent acceptance and resolve into a proper truce. War, peace,
  battle outcome and event cues reuse the original project's sounds after lossless WAV import.

### Milestone 4 — live economy, society, construction, decisions and events

The top-bar numbers are no longer immutable scenario decoration. A weekly deterministic pass
updates taxable output, public expenditure, balance, GDP, population, productivity, stability
and standard of living. Tax revenue is calibrated against a taxable share of GDP rather than
treating the full economy as public income. Construction is paid daily from the treasury,
progresses one head project per country, and changes buildings, GDP, literacy, living standards
and capacity when completed.

All 1,710 administrative states hold population, literacy, urbanisation, radicalism, standard
of living, building levels and four explicit social groups (farmers, workers, middle class and
owners) with nationality, employment, wealth and group size. The Economy screen exposes the
selected owned state and offers industry, infrastructure, university and hospital projects
with costs, durations and explanatory tooltips.

Five national decisions share the same live state: stimulus, education reform, public health,
anti-corruption and emergency mobilisation. Conditions, treasury cost, cooldown and effects
are visible before use. Paced campaign events pause play, present two consequences with
tooltips, time out after 182 days and modify the same economy/society data rather than a second
UI-only model.

### Live validation — 31 August 2026

- Foreground Windows input selected Germany and began a campaign without editor-side shortcuts.
- The Russo-Ukrainian front rendered as one thin line on the exact national border; no centre-to-centre debug mesh remained.
- A drag over Germany selected both field armies and opened the aggregate operational panel with group orders and auto-deploy.
- Naval plates were visible off Britain, France, Turkey and other seeded maritime powers.
- Hovering Georgia resolved Samegrelo-Zemo Svaneti and tactical province 10,670 through the authoritative ID textures.
- A 75-day KOR speed-five run advanced to 17 March, changed GDP/population/treasury, funded civilian industry to 31%, then paused on a housing event.
- The .NET Debug build and Fennara three-second scene validation complete with zero warnings and zero errors. Fennara's optional standalone runtime launcher remains unable to locate the host .NET SDK; editor/MCP and direct `dotnet.exe` validation are unaffected.

### Milestone 4.2 — interactive regions and layered content catalogues

- Map clicks expose one state/province inspector for demographics, class employment/wealth, nationality shares, buildings, construction, occupation, adjacency and stationed forces.
- Owned regions can queue infrastructure/industry or raise a real one-division formation. Armies can be split through deterministic commands rather than UI-side list edits.
- Buildings, goods, decisions and events moved to live `game/` JSON catalogues; mods layer the same folders and catalogues reset before each campaign.
- `map_data/region_overrides.json` owns the Abkhazia/Russia adjacency repair and separate Abkhazia, South Ossetia, Andaman–Nicobar and Lakshadweep regions.
- Right-click movement preserves the left-click inspection selection and renders a textured surface ribbon instead of selecting the destination.

### Milestone 4.3 — scenario extensibility, tactical cells and campaign archive

- The 2024 scenario now registers 261 nations and 1,715 administrative states. Transnistria (`PMR`) is a real de-facto scenario participant with Tiraspol, history, personnel and a territory carved from the Moldovan source layer before region/province generation; it is not a cosmetic colour overlay. The first rectangular prototype was replaced by a 56-point polygon simplified from OpenStreetMap relation 65335 near the source raster's native resolution, preserving the long Dniester corridor and the Bender/Tighina west-bank pocket.
- Country names are single tangent-space words placed on the largest connected land component. Horizontal transform stretches normal letter spacing across the available axis without rotating individual glyphs; the label shares the map transform, obeys depth testing and reuses the same geographic anchor in flat mode. Close tactical LOD hides it rather than showing cropped letter fragments over provinces and units.
- Tactical topology now yields 37,833 provinces (approximately the forty-thousand design target). Its current baked raster builds 98,550 undirected land edges after territorial overrides, connectivity repair and restrained anisotropic relaxation are applied. Flat and globe shaders use their own zoom metric, so province borders appear only at operational scale in both projections.
- Save format 2 binds every campaign to an explicit map-data contract and province count. Incompatible pre-topology saves are rejected before the active campaign is replaced; a live F5/F9 round trip restored 1 January after advancing to 6 January with the complete province-control state intact.
- `SaveSystem` adds GZip-compressed versioned campaign snapshots, SHA-256 verification, atomic temporary-file replacement, metadata sidecars, mod fingerprints, autosave, quicksave and five manual slots. Every engine-neutral subsystem — including province control, occupations, pending events, diplomacy, weekly history and RNG state — participates in the round trip.
- Live validation on 1 September 2026 saved South Korea to a manual slot at 1 January, advanced at speed five to 5 January, and restored the complete campaign to 1 January through the archive UI. The compressed snapshot was 368 KiB. The current .NET build completes with zero warnings and zero errors.

### Milestone 4.4 — authoritative hydrology and formation presentation

- Natural Earth GeoJSON rivers and lakes feed the map compiler; authoritative `rivers.png` and `lakes.png` draw the same features and annotate the tactical graph, producing 8,759 real crossing edges without cutting land connectivity.
- Rivers remain freely crossable but slow movement and penalise attacks; major rivers apply a stronger penalty. Lake access adds a small local supply benefit, tooltips explain the terrain, and front AI values dry or weakly defended sectors while spearheads tolerate more terrain risk.
- Polygon regions run a bounded four-connected repair after rasterisation. This joins narrow valid corridors such as Transnistria without filling arbitrary rectangles or repainting foreign territory. Andaman–Nicobar and Lakshadweep remain separate authoritative Indian regions rather than inheriting Rajasthan.
- Army marker topology now follows the campaign structure fingerprint. Splitting a formation creates the second counter immediately, selects both halves, and separates co-located counters in a compact map-bound stack in globe and flat modes.

### Milestone 4.5 — Paradox-familiar content contract

The project is converging on one public content tree shared by the base game and mods: `common/`, `history/`, `events/`, `map_data/`, `localization/`, `gfx/`, `sound/` and optional `scripts/`. Existing live catalogues keep their stable string IDs and deterministic layered load order; planned directories are documented as planned rather than silently accepted and ignored.

The next extraction order is diplomacy and AI acceptance, localization, presentation definitions, then audio cue mappings. Each extraction must remove the corresponding hardcoded content from C#, add validation and fallback behavior, participate in the mod fingerprint, and pass a base-game plus override-mod smoke test before its directory is marked live.

### Milestone 4.6 — authoritative raster data maps

- `provinces.png` plus `definition.csv` now own tactical province cells and stable 16-bit IDs. `default.map` declares the filenames and horizontal wrapping; a mod replaces the pair as one validated layer.
- `rivers.png` and `lakes.png` are independently replaceable colour maps. Their documented fixed palette carries river class and lake presence into both the shader and the simulation graph.
- The offline map-authoring compiler relaxes seed-growth cells into varied anisotropic polygons, then performs an exact PNG round trip. Runtime requires the authored province PNG/CSV pair and never generates replacement topology or unstable province IDs during campaign startup.

### Milestone 4.7 — division entities, governance, console and staged startup

- Source active-personnel totals now instantiate independent 15,000-person divisions. Co-located divisions are grouped only by the presentation layer; the counter reports division count above and total personnel below. `ArmyGroup` is a separate organisational headquarters, while selection, orders, casualties and movement continue to address individual divisions.
- Combat power now includes equipment, experience, cohesion, reconnaissance, engineers and artillery support. Reinforcement priority is weighted per division and consumes the same finite national manpower pool, allowing prepared high-quality formations to outperform a larger but exhausted force.
- Russia and Ukraine commit 94% of available divisions to evenly sampled provinces along their active historical front, with the remainder retained as a reserve. Other nations continue to use peacetime border and capital deployment rules.
- The national ledger gained a live Government workspace: cabinet focus plus welfare and education shares flow through the deterministic command queue into expenses, growth, living standards, radicalism, literacy, productivity, stability and war support. State populations remain authoritative; nation population is recalculated from currently owned states.
- Peace packages now include ceasefire, white peace, reparations, trade concessions, demilitarisation, war-goal enforcement, occupied-territory demands and capitulation. Each exposes acceptance and demand cost, produces distinct territorial/economic/military effects, writes a settlement summary and establishes a truce.
- A `~` developer console provides Paradox-familiar `tag` and `observe` commands plus time, map, free-camera, teleport, economy and profiler diagnostics. `Esc` and `~` close it without leaking Space or other typed keys into campaign controls.
- Startup uploads the full-resolution political texture, distant mipmapped texture, ID maps, hydrology and topology over separate frames. This removed the previous main-thread stall caused by allocating and uploading every 5760×2880 resource and mip chain in one frame.
- Army presentation no longer mirrors every foreign division with three separate 3D nodes. Player divisions retain province-level stacks, foreign divisions aggregate by operational headquarters, and the visual layer updates at 8 Hz with a strategic zoom cutoff. In the live regression test this removed D3D12 descriptor-heap exhaustion and its render-error flood; the game advanced 89 frames during a 1.2-second capture interval.
