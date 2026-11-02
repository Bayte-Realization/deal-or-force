# Flags as data, and a country edited as a whole

Two things this template shows, both written in exactly the format the in-game editors save:

* **`common/flag_designs/`** - flags described as a background and emblems instead of painted
  as PNGs. `50_examples.json` explains every field in its header comment;
  `60_yaml_example.yaml` is the same format in YAML.
* **A country's files** - `common/countries`, `history/countries` and `localization/<language>`
  files named `90_editor_lux`, as the country editor writes them for Luxembourg.

To try it, copy this folder to `mods/my_flags`, change `id` in `manifest.json` and set
`enabled` to `true`. The template itself is disabled and never changes a normal campaign.

## How a design becomes a flag

A design's id is a sprite name, so it joins the flag search the way a picture of the same
name would (`common/flags/00_flags.json`): `flag_lux` is Luxembourg's flag, `flag_lux_socialism`
the flag it flies while socialist, `flag_lux_absolute_monarchy` while an absolute monarchy.
Where a design and a PNG share a name, the design wins. Nothing else has to be declared.

## The editors

* **Flag editor** - *Scenarios & workshop -> Flag editor*, the **Flag…** button on your own
  country's card, or **Edit flag…** in the map editor's country inspector. Pick a tag and the
  politics the flag is for, a pattern, up to five exact `#rrggbb` colours (the picker's presets
  are the base game's flag palette), and emblems: geometric shapes or any icon in `gfx/`. It
  previews at dossier, list and map-marker size and saves `common/flag_designs/<id>.json` into
  the *Editor workspace* mod, plus a PNG export in `exports/flags/` for anyone who wants to
  finish it by hand. The saved flag is flown at once.
* **Country editor** - *Scenarios & workshop -> Country editor*. Name and adjective in every
  language the game has, map colour, capital (name and coordinates), government, ideology,
  leaders, primary cultures, starting treasury, flag, and which regions it holds on the
  bookmark. It writes the files above (and `history/provinces/90_editor_<tag>.csv` when regions
  change hands) into the *Editor workspace* mod; the base game is never written.
