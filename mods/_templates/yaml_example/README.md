# YAML in one file

The smallest possible YAML mod: one file that changes one number. Use it to see the
format; use [`../yaml-data`](../yaml-data/README.md) for a larger example that touches
defines, goods, laws and a translation.

To try it, copy this folder to `mods/my_yaml_mod`, change `id` in `manifest.json` and set
`enabled` to `true`. The template itself is disabled and never changes a normal campaign.

## What it does

`common/opinion_modifiers/50_longer_grudges.yaml` makes a public denunciation linger for
1825 days instead of the base game's 1100. Nothing else about the reason changes: its
name, its size, whether it fades and how often it stacks all come from the base game.

## The format

Every data folder that takes `.json` also takes `.yaml` and `.yml`, with the same schema.
The game converts YAML to JSON when it reads the file, so a key that works in one works in
the other, and an error message names the same field either way. These two say the same:

```yaml
denounced:
  opinion: -18
  days: 1825
```

```json
{ "denounced": { "opinion": -18, "days": 1825 } }
```

What to know when writing it:

- **Load order is the file name**, across formats. `00_diplomacy.json` loads before
  `50_longer_grudges.yaml` because `00` sorts before `50`, not because of the extension.
  If two files share a name apart from the extension, `.json` loads before `.yaml`/`.yml`
  and the YAML one wins - but give them different names rather than rely on that.
- **Only `true` and `false` are booleans.** `yes`, `no`, `on` and `off` stay text, so a
  country tag like `NO` (Norway) is never read as "false".
- **Numbers must look like JSON numbers.** `0.5`, `-18` and `1e3` are numbers; `001`,
  `.5` and `2024-01-01` stay text. Put quotes round anything you want kept as text.
- **Colours need quotes:** `"#e7bd69"`. An unquoted `#` starts a comment.
- **One document per file.** No `---` separators, no anchors (`&`), aliases (`*`) or
  merge keys (`<<`), and no `!tags` beyond the plain `!!str`, `!!int`, `!!float`,
  `!!bool` and `!!null`. Use another numbered file instead of an anchor.
- **Duplicate keys are an error**, not "the last one wins", so a copy-paste mistake cannot
  silently change balance.
- **A broken file costs only itself.** The mod log names the file and the line and column
  of the mistake; every other file in the folder still loads.

`manifest.json` stays JSON: package manifests, save files and settings are not read
through the YAML path.
