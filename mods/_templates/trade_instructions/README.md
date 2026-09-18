# Trade instructions template

What a country may decide about its own trade is seven controls in
`game/common/trade/00_instructions.json`, and the screen is drawn out of that file: the
controls, their bounds, their step, their names, and — new — the sentence each one reads as
at whatever it is currently set to. This template replaces one of the seven and adds an
eighth.

Copy the directory out of `_templates`, give the folder and the `id` in `manifest.json` a
unique name, set `"enabled": true`, and start the game. The trade section of the state
affairs screen rebuilds itself from your file.

## A control

```json
"import_ceiling": {
  "scope": "good",          // "good" - set per commodity; "partner" - set about a country
  "shape": "share",         // "share" (0..1), "multiplier" (×), "flag" (a switch)
  "default": 1.0,           // what every country is taken to have chosen
  "minimum": 0.34,
  "maximum": 1.0,
  "step": 0.02,             // 0 for a free number
  "name": "Import ceiling",
  "description": "…",
  "reads": { … }
}
```

`shape` decides the control as well as the reading: a `flag` is drawn as a switch and not as
a slider, because "FORBIDDEN 0 %" is not a thing anybody should have to read.

## `reads`, and why it exists

A number and its name are not an instruction. "Price floor 0.4×" is a true fact about a
control; "we will not sell this below two fifths of what it is worth" is the decision the
player actually made. The screen shows the second one under the slider, and dims the whole
row when the control is resting at its default — so "four instructions in force" is four lit
rows rather than the number four at the bottom of the screen.

| key | shown when |
| --- | --- |
| `resting` | the value equals `default` — this control is not doing anything |
| `off` | the value is at `minimum`, where that means *none* rather than *a little* |
| `on` | the value is at `maximum`, or a flag is on |
| `set` | anywhere else. `{0}` is the value as the screen prints it |

All four are ordinary localisation keys, like `name` and `description`: put the English in
the JSON and the translations in your mod's `localization/<language>/`. Leave them all out
and the row falls back to the control's own `description`, which is what the screen showed
before any of this existed.

Order matters: `resting` is checked first, because "this is doing nothing" is the most useful
thing a screen of seven controls can say about six of them.

## What is in this template

**`common/trade/00_instructions.json`**

- **`import_ceiling`** — the id the base game uses, so this **replaces** that control and
  leaves the other six alone. Floored at a third, stepped finer, and with an `off` sentence
  that now means "the least we can get away with" rather than "nothing at all". This is the
  shape of every trade mod: name what you disagree with, inherit the rest.
- **`strategic_partner`** — an id nothing else uses, so it is a **new** control, appearing in
  the country card under Preference and Forbidden.

**`localization/`** — the eight strings both of those introduce, in English and Russian.

## Things that will bite

- **Seven ids do something. Yours does not.** The weekly routing reads `import_priority`,
  `import_ceiling`, `export_reserve`, `price_ceiling`, `price_floor`, `preference` and
  `forbid` by name. A control you invent is stored on the country, saved, shown and
  translated — and ignored by the market until a script or a patch reads it.
- **A price limit is a multiple of the good's own base price**, from `common/goods`. A
  ceiling of 1.4 on oil means "not above forty per cent over what oil is normally worth", and
  it keeps meaning that after a mod reprices oil.
- **Nought is the off position for the price limits**, and the screen prints it as "no limit"
  rather than "0.0×", which would read as the opposite.
- **Changing a `default` changes every country.** All two hundred and sixty-one are taken to
  have chosen the default until they choose otherwise, so a new default is a world-wide
  policy change on day one — including for the AI.
- **Bounds are enforced where the value is stored**, not only on the slider, so narrowing a
  range cannot be got round by a script or an old save.
