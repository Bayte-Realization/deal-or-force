# 3D models

Everything the map draws in three dimensions lives under this folder, one folder per kind,
the way a Paradox game keeps `gfx/models`. A mod replaces or adds a model by shipping a file
with the same path or a new one; nothing in the code names a model by file.

| Folder | What it draws | Read by |
|---|---|---|
| `trade/` | ships on the sea lanes and lorries on the roads | `Scripts/gfx/GlobeTradeTraffic.cs` |
| `source/` | the Blender files the shipped models were made from (`.gdignore`: not loaded) | nobody - for modellers |

## A trade model

One JSON file per model. Either it carries the mesh itself (what the export script in
`source/trade_vehicles.blend` writes):

```json
{ "name": "Articulated lorry", "role": "lorry", "weight": 1, "length_m": 16.05,
  "positions": [...], "normals": [...], "colors": [...], "indices": [...] }
```

or it names a glTF file beside it - any `.glb` or `.gltf` exported from Blender, Maya or
anything else, with its own materials and textures:

```json
{ "name": "Galley", "role": "ship", "weight": 2, "length_m": 40, "mesh": "galley.glb" }
```

- `role`: `ship` sails the sea lanes, `lorry` drives the roads between neighbours.
- `weight`: how often it is picked among the models of its role (1 to 100).
- `length_m`: its real length in metres; the map draws it a few pixels long at any distance
  and keeps the proportions between models.
- Metres, Y up, the bow or the cab towards +X. Apply all transforms before exporting: the
  first mesh in the file is used as it is, without its node's rotation or scale.

A medieval mod replaces the container ships with cogs and the lorries with ox carts by
shipping `trade/container_ship.json` and the rest under the same names (or removes them by
shipping files with an empty `positions`), and switches off what it has no use for in
`common/world_layers` - `trade_traffic`, `trade_lanes`, `supply_lines` and the others.
