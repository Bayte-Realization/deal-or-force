# YAML data mod

Copy this folder to `mods/my_yaml_mod`, change the JSON manifest ID, and set
`enabled` to `true`. The template itself is disabled and never changes a normal campaign.

The mod disables aviation and drones, adds a steel description, sets 12 allocation units
per factory and 50% conversion efficiency, and supplies a Russian translation. It does not
change steel's production recipe. JSON and YAML use the same schemas and layer by filename.

The governance examples now also overlay fiscal-law cost/cooldown, add a targeted
regional community-services law and a one-year government pledge, and replace a
national course's description while inheriting its pressures/effects. These do not
instantly select a national course. Unknown modifier names are rejected.
All three files are parsed and checked by the regional-content regression tests.

See [the YAML reference](../../../docs/YAML_MODDING_RU.md) for supported folders and limits.
