# The campaign interface and formable nations

*Русская версия: [MODDABLE_CAMPAIGN_UI_RU.md](MODDABLE_CAMPAIGN_UI_RU.md)*

## What changed

The repeating painting of a desk with a map has been taken out of the standard dossier. It
showed nothing about the country you had selected, took up 86 px of height and competed with
the map. The star texture behind the globe is off by default: the calm `shell.space` colour
is used instead. The image files are kept, so the change is reversible. Flags, function
icons, the map itself and the main menu illustrations were not removed.

Decisions lost the "What it would cost" link, and diplomacy lost the jump to your own policy
settings. Governing your own country lives on its government tab; a foreign dossier does not
offer to govern a foreign government. The budget no longer duplicates the regional
demography. Genuine actions that are blocked (a decision on cooldown, for instance) stay,
with the reason given — those are not empty buttons.

## Where to edit

| File/folder under `game` | What it controls |
| --- | --- |
| `gui/10_campaign_shell.json` | Panel positions and sizes, anchors and margins |
| `gui/20_campaign_structure.json` | Which top figures there are and in what order, the navigation, tabs, links and their visibility conditions, and the order and hiding of report groups |
| `gui/21_dossier_contents.json` | The order, hiding and captions of the dossier's information rows |
| `interface/` and `gui/`, the `colors` and `metrics` sections | Colours, button and tab sizes, inner padding, the calm ground |
| `common/formable_nations/` | New nations, founders, territories, cost and bonuses |
| `localization/english/32_formable_nations.json` | The mechanic's captions |

JSON files are loaded by name; a later value overrides an earlier one. Mods use the same
paths **relative to the mod's root**, with no extra `game` folder. Restart the game after a
change. Recompiling C# is not needed. This is not a live editor: a window tree that has
already been built is not rebuilt automatically on every file edit.

Arrays are replaced whole. A missing property is inherited. `[]` explicitly removes every
element of an array. `detail_labels` is merged by key. Do not copy a whole source file in
order to change one property.

### An example of a compact HUD

```json
{
  "campaign": {
    "top_stats": ["TREASURY", "BALANCE", "STABILITY", "MANPOWER"],
    "outliner_order": ["CONSTRUCTION", "MARKET", "WARS", "BATTLES", "NAVY", "WATCHLIST"],
    "hidden_outliner": ["BATTLES"],
    "screens": {
      "Nation": {
        "hidden_details": ["GDP per head"],
        "detail_labels": {"States held": "Administrative regions"}
      },
      "Diplomacy": {"links": []}
    }
  }
}
```

`detail_order` lists row keys in display order. A heading is prefixed with `#`: `#STATE`,
`#ECONOMY`. Rows you do not mention are kept after the ones you do. To remove one, use
`hidden_details`. The key is the original English caption before localisation. A row's value
is computed by the simulation and is not replaced by text from JSON. When you reorder, move
the headings along with the rows of their section.

### Tabs and links

The navigation screens are `Nation`, `Economy`, `Military`, `Diplomacy`, `Wars`,
`Decisions`, `State Affairs` and `Ledger`. They can be reordered, removed, renamed through
`label`, and given a different `icon` (the `icon_…` name suffix).

The available tab handlers:

- `Nation`: `Overview`, `Government`, `Society`; `groups` sets which groups are visible
  (`STATE`, `ECONOMY`, `GOVERNMENT`, `SOCIETY`). An empty `groups` shows them all.
- `Economy`: `Budget`, `Market`, `Construction`.
- `Region`: `Overview`, `Population`, `Province`.
- `Decisions`: `Decisions`, `Formables`.

A link has `label`, `action`, and optional `tab`, `context` and `help`. The handlers are
`government`, `ledger`, `wars` and `diplomacy`. The contexts are `always`, `own` and
`foreign`. An unknown action creates no button; a mod's errors are reported to the loader.
Access to a foreign government is blocked separately in code, even if a mod sets the context
wrongly.

## Formable nations

To open: **Decisions → Formables**. By default only your own country's options are shown; a
toggle opens a reference list of the rest, without inapplicable proclamation buttons.

`00_unions.json` has six: a United Korea, a Central Asian Union, a Benelux Union, a Nordic
Union, an Iberian Union and a Baltic Union. Further catalogue files extend the list,
historical projects included. These are alternate-history game projects, not a description
of existing states.

The conditions are common to all: a suitable founder, peace, sufficient stability and
treasury, and full ownership **and** control of the required land provinces. A temporary
occupation does not count. No other country may already have proclaimed the same project.
Each country can form one nation per campaign. Confirmation is required before the sum is
deducted, and the command re-checks the conditions.

```json
{
  "my_union": {
    "name": "My union",
    "description": "A description of the new nation and the idea behind it.",
    "eligible_tags": ["BEL", "NLD"],
    "required_tags": ["BEL", "NLD", "LUX"],
    "minimum_stability": 0.70,
    "gdp_cost_share": 0.02,
    "governance_gain": 0.04,
    "productivity_gain": 0.015
  }
}
```

`required_tags` means the territory of the **original active scenario** from
`history/provinces`, not whatever land a country has left after your conquests. A mod's
changes to the original map are respected. For an exact set of your own you can use
`required_provinces: [123, 456]`; the lists are combined. An unknown country or a
non-existent province blocks the formation rather than relaxing the requirement. Sea and
lake provinces are not required. The numbers for conditions and bonuses live in the range
0…1. A later file can change individual properties of a project;
`{"my_union":{"disabled":true}}` removes it from the catalogue.

Proclaiming changes the name, records the project's ID and the day, deducts the cost and
gives a one-off rise to accumulated governance and productivity. The governance contribution
is bounded by `common/stability` and then follows the usual decay; it is **not a permanent
multiplier**. The form of government, the country ID, the troops and the diplomatic ties are
kept. The flag is chosen by the common `FlagCatalog`, the `formable` trait included: with a
suitable asset present it will change. The action adds no territory and no free annexations.
The identity goes into saves and network snapshots; the command supports the common network
codec. The AI choosing such projects on its own has not been added yet.

## Icons and flags for political options

All 12 base ideologies have original vector symbols in
`game/gfx/interface/ideologies/`. The file name is `icon_ideology_<id>.svg`. A mod can drop
a file with the same sprite name into its own `gfx/`, as SVG **or PNG**: no recompilation
and no JSON needed. A new ideology with no image of its own uses `icon_ideology_generic`. To
change the drawing's size or proportions, edit the SVG in Inkscape or replace the PNG with a
transparent one. These are conventional game symbols, not universal official emblems of the
ideologies.

Policy shows a separate flag for each ideology and each form of government. The preview
calls the ordinary `FlagCatalog.Resolve` on a copy of the political fields: country, war,
culture and a formed identity all keep their context. The campaign is not changed. If the
result matches the current flag, it says "The flag will not change". The rules for aliases,
combinations and priority stay in `game/common/flags` and the images in `game/gfx/flags`;
mod overrides apply to both the preview and the accepted option. After replacing assets in a
running editor, wait for the import and restart the game scene.

## The network lobby

Two blocks from `game/gui/22_multiplayer.json`: `multiplayer.connection` and
`multiplayer.countries`. A mod can replace the coordinates, sizes and anchors; the
background uses the shared `shell.*` colours. The middle of the screen does not cover the
globe.

A country is chosen by clicking its territory or by searching its localised name or tag. The
right button rotates the globe and the wheel zooms. Armies and battles do not intercept a
click on a country while choosing, and no military orders are sent from the lobby. The
server confirms the reservation: a taken country is no longer silently swapped for another
one. Player names, countries and readiness are sent to every client. Changing country or
composition resets readiness; late confirmations of a previous composition are rejected.
Starting needs at least two registered and ready players with different countries. During a
campaign, changing an already assigned country is forbidden; the old path for a new player
joining late with a campaign snapshot is kept.

The lobby protocol has changed: everybody needs the same game version and the same mods.
This is a direct ENet/IP connection, not an internet server browser: firewalls, routing, VPN
and UDP forwarding are still needed depending on the network. The port is validated
explicitly (1024–65535).

## What stays in code

The main campaign shell, the information rows and the choice of ready-made actions are now
driven by data. This is **not a port of the entire interface engine**: computing the values,
the handlers, the complex internals of the military and peace windows, the budget setup and
the generic components all stay in C#. A fundamentally new kind of action, or a new
interactive component, cannot yet be added with JSON alone. That limit is stated openly so
that mods do not end up with buttons wired to invented commands that do nothing.

Checks: `dotnet run --project Tools/FormableTests/FormableTests.csproj` — UI loading and
overriding, contexts, territory resolution, refusal on conditions, the command's re-check,
the cost, the absence of annexation, a second press, the network codec and save/load.
