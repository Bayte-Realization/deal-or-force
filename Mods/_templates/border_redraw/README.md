# Border redraw template

The shape of the world is data (`provinces.png` + `definition.csv`) and so is who owns it
(`history/provinces/*.csv`). This template changes the second without touching the first.

1. Copy this directory out of `_templates`, give the folder and manifest a unique id and
   set `"enabled": true`.
2. Open `game/history/provinces/ownership.csv` and find the provinces you want.
3. Put them in `history/provinces/claims.csv` with the owner you want, one per line.
4. Start the game. Everything downstream — the economy, fronts, supply, the AI, the peace
   conference — reads ownership from the same place, so the border you moved is moved for
   all of them.

`owner` is who holds it in law; `controller` is who is standing on it. Leaving the
controller out means the owner holds it, which is the ordinary case.

## What this cannot do

Region membership. A province is grown inside one region and never crosses its border, so
which region it belongs to is a fact about its shape rather than its politics. To change
that, edit `map_data/region_overrides.json` and recompile the raster — see
`Mods/_templates/map_data_override`.
