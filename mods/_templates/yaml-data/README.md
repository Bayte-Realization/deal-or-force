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

The content editor now browses, validates and saves `.yaml` / `.yml` as well as JSON,
including the `events/` folder. Ordinary Source saves retain comments; property-tree
and layout edits normalise formatting and remove comments (undo restores the text).
The editor refuses stale writes instead of overwriting another editor's changes.
Package/scenario manifests and `world.json` still deliberately use JSON.

`events/70_workshop.yml` is a complete two-choice event with English and Russian
captions. Its investment choice spends 1% of treasury and adds construction capacity;
the alternative costs no money and slightly lowers stability. The regression stand
loads it through EventCatalog and applies its actual effect, not a mock schema.

`common/technologies/70_research_routes.yml` adds two research projects with real
`research_speed` and `supply` modifiers, a shared cross-category route and RU/EN
captions. Costs are research points; `requires` names technology IDs, not translated
titles. The research workspace includes them in effect search and route estimates.
The template is still disabled: normal campaigns do not receive these discoveries.
The regression stand parses the YAML, validates every link, and checks that learning
the first project really increases weekly research. No extra runtime or DLL is needed.
