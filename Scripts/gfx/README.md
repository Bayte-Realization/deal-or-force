# World graphics (not UI)

This folder owns map/globe rendering, terrain and fog, city and movement labels,
surface army cards, battle/fleet/support counters, route ribbons and map order tools.
`GraphicsRenderer` connects the presentation to the boot renderer selector.

Boundaries:

- `Scripts/gfx`: Godot world presentation and its picking/drawing interaction.
- `Scripts/ui`: screen panels, buttons, tooltips and the scenario editor interface.
- `Scripts/Simulation`: engine-neutral campaign rules, map topology and orders.
- `game/gfx`: moddable pictures, flags, map materials and presentation data.

The 22 relocated C# files retain their original `.cs.uid` companions and namespace.
Their classes are referenced by type; no authored scene/settings path needed rewriting.
After moving files outside Godot, run the editor filesystem scan so its UID cache resolves
the new paths. Do not delete/regenerate UID files or move `DealOrForceGame.cs`, the main
scene entry point, as incidental cleanup.
