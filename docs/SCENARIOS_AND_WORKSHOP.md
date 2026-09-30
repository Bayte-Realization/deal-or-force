# Scenarios, the workshop and map labels

*Русская версия: [SCENARIOS_AND_WORKSHOP_RU.md](SCENARIOS_AND_WORKSHOP_RU.md)*

## How to open it

Main menu → "Singleplayer" → "Scenarios & workshop". Pick a scenario, then a country
directly on the map. From the country picker you can go back with the "Scenarios / editor"
button.

"Create scenario from this world" saves an independent copy of the current campaign. It is a
copy and nothing more: changing the date does not turn modern populations, economies and
borders into a historically plausible world. Those have to be edited separately.

## The scenario format

`game/scenarios/<id>/scenario.json`, or the same structure inside a mod:

```json
{
  "id": "my_world",
  "name": "My world",
  "description": "A description of the authored scenario",
  "world": "world.json"
}
```

`world.json` is a complete readable snapshot: Date, Nations, States, Armies, Fleets, Wars,
relations, the political layers ProvinceOwner / ProvinceController / ProvinceState /
ProvinceOccupation, ScenarioRules and ScenarioTerritories. It is far easier to make one with
the button than to write tens of thousands of entries by hand. References between entities
use stable numeric IDs. Province arrays include a zeroth service element. The map geometry
and its version have to match.

ScenarioTerritories holds the original territories of tags for the requirements of formable
nations. They are not replaced with modern borders in authored scenarios. A scenario's date
is independent; ElapsedDays preserves the origin for the internal deadlines of orders and
events. Do not zero it without recomputing the deadlines that depend on it.

Only the built-in `2024` scenario, which has no world file, uses the old modern generator. A
full scenario does not call it and does not receive modern armies, wars, country histories or
border corrections on load. The modern generator is still in C# for now: not all of its
original geography has been moved into a single world.json. Saves and network snapshots keep
the scenario identifier, and authored ones keep its rules as well. Old saves with no
identifier are treated as the 2024 scenario.

## The data workshop

The editor shows the base JSON documents, then the overlays of enabled mods, then your own
working copy. Search filters the paths. Hover a file to see its full path and its source.
Supported: common, history, scenarios, gui, interface, localization, fonts, sound, music,
gfx and map_data.

The "Properties" tab: select a value, type a number, true/false or a quoted string, and
press "Apply property". The tree is limited to six levels, the first 100 elements of each
array and 4,000 nodes, so that it stays responsive. The whole document, adding and removing
elements, and containers are available on the JSON tab. Ctrl+Z / Ctrl+Y undo and redo. "New
document" creates an editable draft at a path you give; nothing is written until you press
"Save".

Saving goes to `user://mods/editor_workspace`, not into the game itself. Overwriting is
atomic; the previous version stays beside it as `.bak`. Invalid paths and redirected
files or directories are refused. "Validate" checks the JSON, and for scenario snapshots
also the map version and the main references. It is not a full semantic check of every
catalogue: the loaders of individual subsystems check their own fields again at startup.

Enable **Editor workspace** in the mod selection and restart for rule and UI changes.
Scenarios in the working folder appear in the library at once. The editor does not execute
code from the text and does not automatically reload arbitrary scripts.

This is a first working data editor, not a finished visual editor of everything: there is no
brush for changing province geometry yet, no visual widget designer, no raster editor, no
CSV tables and no wizards for every kind of entity.

## Country names and fonts

For the country RUS: `country.RUS.name` is the full name, `country.RUS.map` is the map
label. For a formed nation: `formable.<id>.name` and `.map`. They are looked up from the
most specific to the most general:

1. `.government.<government>.ideology.<ideology>.name` / `.map`;
2. `.government.<government>.name` / `.map`;
3. `.ideology.<ideology>.name` / `.map`;
4. `.name` / `.map`.

With no special key, the translation of the country's original name is used. The existing
Russian table `20_countries.json` is kept. Short labels for the USA, the DPRK and so on are
added in `34_workshop.json`. Do not change tags in order to rename something.

The typographic profile "Globality Atlas": a calm sans-serif interface and widely tracked
serif cartographic labels. It is not a new original TTF. `game/fonts/00_fonts.json` declares
the roles `ui.default` and `map.countries`. `system_families` asks for an installed font;
for the same look on every machine, put a licensed TTF/OTF in a mod and set `file` for the
role.

## UI and border legibility

`game/gui` already exposes the panel layout, the navigation, the tabs, the dossier fields,
the contextual links, the statistics, the situation report and the lobby. The workshop edits
those same documents. The engine handlers stay in C# — the data chooses among supported
actions, it does not turn into arbitrary method calls.

`24_workshop.json` adds the workshop's position and some metrics:

- `map.country_font_size`, `map.country_tracking` — the labels;
- `map.province_border_width` — the thickness of close-up borders, in screen pixels;
- `map.province_border_opacity` — contrast, 0–1;
- `map.province_border_halo` — a light outline for dark terrain.

Provincial lines fade in smoothly as you zoom in and are suppressed at the strategic view.
Country and front borders keep their own styling.

## Offensives

Select divisions → "Front": LMB for the whole border or RMB for a sector → "Attack": drag an
RMB line for the objective inside the enemy's country → "Execute". "Prepare" stops the
execution; Alt+RMB redraws the objective. An empty province under heavy covering fire from
its neighbours can delay an order: that is a zone of control doing its job, not the loss of
the route you drew.

To check offensives on their own:
`dotnet run --project Tools/ArmyInteractionTests -- --offensive-only`.
To check scenarios, flags and the editor's writing:
`dotnet run --project Tools/FormableTests`.

## Limits of the current stage

### Visual interface layout

Choose **UI layout editor** in Scenarios & workshop. It opens your mod's
`gui/99_editor_layout.json`, or an unsaved draft. The Layout tab also works with any
`gui` document. Select an existing panel in the list or schematic, drag to move it,
or drag its lower-right handle to resize. The grid uses 8 reference-screen pixels;
Shift bypasses it and Escape cancels the current gesture without closing the workshop.

The inspector edits four anchors (0–1) and pixel offsets. Anchor presets keep the panel
in place at the chosen reference resolution. Test both 1280×720 and 1920×1080. Undo/Redo
and Ctrl+Z/Ctrl+Y share the JSON editor's history; one gesture is one undo operation.
Gold list entries belong to this file; grey entries are inherited from the loaded skin.
The panel search matches localized names and stable IDs case-insensitively. Filtering
does not change the document or active panel; tooltips include the full name and ID.
Remove file override removes the entry, not the game's panel. Its preview falls back to
the **loaded** skin; if that override was already enabled at startup, restart to see its
removal fully take effect.

Save to my mod writes authored entries only, preserving unrelated JSON fields (not JSON
comments). Enable Editor workspace and restart to use the result. The preview is a scaled
schematic, not a rendered copy of the live UI: content minimum sizes and adaptive placement,
especially outliner height, can affect the final result. Out-of-screen geometry is warned
about. This tool does not create new widgets, handlers or scripts and does not change the
active campaign. Raster/province-geometry painting remains future work.

Runtime-verified on 2026-09-23: mouse move/resize, undo/redo, Escape cancellation,
mod save/reopen, name/ID search and unchanged document during filtering. English,
Russian and a 1280×720 window were visually checked. The reproduced D3D12 exhaustion
during combined language/window/projection changes is addressed by glyph/font reuse
and additional descriptor capacity; see [rendering regression evidence](RENDERING_REGRESSION.md)
for the bounded stress checks and remaining hardware/mod coverage limitations.

With the schematic focused, arrow keys move the selected panel by one reference-screen
pixel; Shift + arrows moves it by eight. Each press is undoable (key repeat is ignored).
Arrows in search fields and the panel list retain their normal behavior. **Fit inside
screen** recovers an off-screen or inverted panel at the selected preview resolution,
preserving anchors and shrinking only when necessary. It is disabled when no change is
needed and supports undo/redo. This modifies the draft, not the running interface or
other resolutions automatically. These controls were runtime-checked with real key
events; geometry edge cases are covered by `LayoutWorkbenchTests`.

The interface scripts are in `Scripts/ui`; the editable definitions are in `game`. The
workshop can already change JSON through the property tree and the text mode, create a
separate mod with a backup, and save a self-contained scenario. It is not yet a visual
designer for every window's contents, and not a brush for redrawing provinces. Placement of
existing panels can now be edited visually.

`FormableTests` and the full `ArmyInteractionTests` suite were checked on 23 September 2026.
The military subsystem fixtures explicitly use a daily step; covered advances are also
checked at 1-hour and 24-hour steps. A direct order advances more slowly under cover,
while an autonomous gap exploit against overwhelming cover is blocked. A trapped defeated
division is removed; a retreating survivor may march through friendly territory but cannot
counterattack until recovery ends. These are simulation checks, not automated verification
of mouse gestures in the interface.
