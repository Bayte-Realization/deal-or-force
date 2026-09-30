# Cities and militia template

Two things that belong together: the cities on the map, and the militia that forms up in them.

1. Copy this directory out of `_templates`, give the folder and the manifest a unique id and
   set `"enabled": true`.
2. `map_data/cities/50_cities.json` adds a town and changes another. A city is geography, like
   a river: its region and its owner come from the map under it, so it changes hands with its
   ground. `"tier": -1` takes a base-game city off the map.
3. Give a new city its name in every language you ship: `city.<key>.name` in
   `localization/<language>/`.
4. `common/militia/50_militia.json` changes how militia rises. Nobody recruits it: a region
   raises it when the enemy is on or next to its ground, and forms it up in the largest town of
   the region its country still holds - so the towns you add are where it will appear, and a
   formation carries the town's name.

The whole formula - who rises and how fast - is written at the top of the base game's
`common/militia/00_militia.json`.
