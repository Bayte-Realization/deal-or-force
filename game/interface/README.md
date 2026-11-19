# Interface Skin, Sprites, Layout & Modding Reference

Everything the Deal or Force interface draws—colour palettes, sculpted plate ornaments, button 9-slices, divider medallions, news-pacer timings, shader paths, panel rectangles, icon roles, and hotkeys—loads from JSON data so mods and era overlays can reskin and re-pace the UI without modifying C#.

---

## 1. Directory Roles & Parser Rules

| Directory | Contents | Parser / Consumer |
|---|---|---|
| `game/interface/*.json` | Base colours, materials, chrome/ornament/pacer metrics, hotkeys | `InterfaceSkin`, `InterfaceMaterials`, `HotkeyCatalog` |
| `game/gui/*.json` | HUD & modal layout rectangles, panel metrics, screen catalogues, font/spacing scales | `InterfaceSkin`, `CampaignUiCatalog` |
| `game/gfx/interface/*.json` | Explicit sprite ID mappings (`"sprites"`) and shader path overrides (`"shaders"`) | `SpriteCatalog` |
| `game/skins/<skin_id>/` | Era or scenario visual overlays (`skin.json` + optional `interface/`, `gui/`, `gfx/`) | `SkinOverlays` |

### JSON Conventions (`InterfaceSkin.cs`)
- Only `*.json` files are loaded (non-JSON documentation files such as this `README.md` are ignored).
- **Comments**: Top-level and inline `//` line comments (JSONC) are supported; object keys starting with `//` or `#` are also ignored as comments for strict-JSON editors.
- **Spelling**: Top-level `"colors"` and `"colours"` are treated identically. All token keys are case-insensitive.
- **Valid colour formats**: `#RGB`, `#RGBA`, `#RRGGBB`, or `#RRGGBBAA`. Unknown tokens resolve to `#ff00ff` rather than throwing, so typos appear immediately on screen.
- **Comments in `materials` / `layout`**: Individual keys preserve earlier settings unless explicitly overwritten (for example setting `"material": ""` on a layout entry clears its material role opt-in).

---

## 2. Load Order & Mod Override Layering (`mods/<mod_id>/`)

At startup and on catalogue reload (`SkinOverlays.Rebuild` / `InterfaceSkin.ResetCatalog`), layers apply in strict order where **later layers win per key**:

1. **Compiled C# defaults** (`InterfaceSkin`, `InterfaceMaterials`, `CampaignUiCatalog`, `HotkeyCatalog`).
2. **Base game skin & layout**:
   - `game/interface/*.json` sorted alphabetically by filename (`00_skin.json` -> `10_materials.json` -> `90_shell.json` -> `91_controls_hotkeys.json`).
   - `game/gui/*.json` sorted alphabetically (`00_panels.json` -> `90_shell.json`). Because both folders use the same merger, either folder may override colours, metrics, materials, or layouts.
3. **Base game artwork & shaders**: `game/gfx/**` (`SpriteCatalog.LoadTree`)—every image under `game/gfx/` is registered by basename first, then `game/gfx/**/*.json` `"sprites"` and `"shaders"` tables override explicit paths.
4. **Active mods (`mods/<mod_id>/`)** in mod load order:
   - `mods/<mod_id>/interface/*.json`
   - `mods/<mod_id>/gui/*.json`
   - `mods/<mod_id>/gfx/**` (both loose files under `gfx/` and JSON sprite/shader declarations)
5. **Active era / scenario skin overlay (`SkinOverlays.Apply`)**:
   - `game/skins/<id>/{interface,gui,gfx}`
   - `mods/<mod_id>/skins/<id>/{interface,gui,gfx}` in mod order.
   - If the overlay's `skin.json` declares `"inherits": "<parent_id>"`, parent overlays load recursively before the child (up to depth 8).
   - Which `<id>` applies is chosen by `SkinOverlays.Choose(scenarioSkin, setting)`: player setting `"skin_overlay"` = `"none"` always keeps the base look; `""` or `"scenario"` uses the scenario's `"skin"` field; any other value activates that overlay ID directly. If the named overlay is absent, `SKIN_UNKNOWN` is logged and the base interface remains intact.

> **Mod filename tip**: Because files inside each folder load in alphabetical order, prefix mod override files with `50_` or `95_` (e.g. `mods/<mod_id>/interface/95_custom_chrome.json`) if your mod overrides another file inside the same folder. Always match shipped C# defaults cleanly—avoid leaving `game/interface/` in an inconsistent state when authoring core changes.

---

## 3. Sculpted Chrome, Ornaments & News-Pacer Metrics

### Tactile Chrome & Ornament Toggles (`00_skin.json`)
| Metric Key | Default | Consumer | Effect |
|---|---|---|---|
| `shell.ornament_alpha` | `0.62` | `ChromePlateArt.AttachCornerOrnaments` | Alpha multiplier (`0..1`) for the four sculpted pewter-brass corner L-brackets (`shell_corner_ornament`). Set `0` to hide corner ornaments cleanly. |
| `shell.divider_alpha` | `0.78` | `ChromePlateArt.CreateEngravedMedallionDivider` | Alpha multiplier (`0..1`) for engraved horizontal section dividers (`shell_divider_medallion`). Set `0` to fall back cleanly to hairline separators (`MenuChromeTheme.CreateHairlineRule`). |
| `shell.button_texture_enabled` | `1.0` | `ChromePlateArt.BuildSculptedButtonStyle` | Enables sculpted 9-slice `StyleBoxTexture` buttons (`>= 0.5`). Set `0` (`< 0.5`) to fall back to flat `StyleBoxFlat` fills. |
| `shell.plate_bevel_px` | `2.0` | `ChromePlateArt`, `MenuChromeBrassFrame` | Edge bevel thickness in px (`clamped 1..6` in button 9-slice computation: `slice = round(6 + bevel)` -> `8px` default). |
| `shell.space_texture` | `1` | Menu backdrop renderer | Enables starry space backdrop texture (`1`) vs solid fill (`0`). |
| `panel.texture_margin` | `16` | `InterfaceMaterials` | 9-slice margin (`clamped 8..64` px) for procedurally baked `ui.materials` plates. |
| `button.corner_radius` | `3` | `UiTheme`, `InterfaceMaterials` | Corner radius for flat/material buttons (`clamped 0..24`). |
| `button.border_width` | `1` | `UiTheme`, `InterfaceMaterials` | Border width for flat/material buttons (`clamped 0..6`). |
| `button.focus_width` | `2` | `UiTheme`, `MenuChromeTheme` | Keyboard focus ring border thickness (`px`). |
| `menu.backdrop_brightness` | `0.42` | Main menu background | Brightness multiplier on menu backdrop. |
| `menu.globe_brightness` | `1.0` | Main menu globe | Brightness multiplier on menu globe. |
| `news.feed_rows` | `3` | News ticker / feed UI | Compact news feed row count. |

### Shell Chrome, Brass Trim & News-Pacer Metrics (`90_shell.json`)
| Metric Key | Default | Consumer | Effect |
|---|---|---|---|
| `shell.news_min_dwell_sec` | `2.8` | `ShellNewsPacer` | Minimum display duration (seconds) before a surfaced newspaper headline may rotate out. |
| `shell.news_cooldown_sec` | `1.2` | `ShellNewsPacer` | Minimum quiet gap (seconds) between dismissing/expiring one news card and surfacing the next. |
| `shell.news_audio_cooldown_sec` | `3.5` | `ShellNewsPacer` / audio cue | Minimum interval (seconds) between consecutive news stinger sounds. |
| `shell.news_max_queue` | `6` | `ShellNewsPacer` | Maximum queued headlines held waiting before overflowing. |
| `shell.news_lifetime_sec` | `9.0` | `ShellNewsPacer` | Maximum lifetime (seconds) before an unpinned headline card auto-dismisses. |
| `shell.news_max_visible` | `2.0` | Campaign news stack | Maximum concurrent visible headline cards. |
| `shell.border_width` | `1.0` | `MenuChromeTheme` | Default shell plate border thickness. |
| `shell.outliner_border_width` | `1.6` | `ShellContext.AttachPlateFrame` | Crisp perimeter frame stroke width (`0..4` px) drawn on the right outliner (`ShellAlertColumn`), `ShellDatePlate`, `ShellMapBar`, and `ShellMinimap`. Set `0` to hide. |
| `shell.corner_radius` | `3.0` | `MenuChromeTheme` | Default shell plate corner radius. |
| `shell.brass_corner_len` | `14.0` | `MenuChromeBrassFrame` | Procedural L-bracket arm length when sprite ornaments are disabled or hidden. |
| `shell.brass_corner_thick` | `2.5` | `MenuChromeBrassFrame` | Procedural L-bracket stroke thickness. |
| `map.city_font_size` | `44` | `GlobeCities` | Rasterized font size (`12..64`) for map city labels (`map.cities` role in `game/fonts/00_fonts.json`). |
| `map.city_capital_text_pixels` | `13.8` | `GlobeCities` | On-screen pixel height for capital city labels (`capitals > ordinary towns`). |
| `map.city_outline_ratio` | `0.14` | `GlobeCities` | Halo outline ratio (`0.06..0.30` of `map.city_font_size`) for crisp city label contours. |
| `shell.alerts_max_folded` | `4.0` | `ShellAlertColumnView` | Alert chips collapsed behind overflow badge. |
| `shell.alerts_max_outliner` | `3.0` | `ShellAlertColumnView` | Max alert rows shown alongside outliner. |
| `shell.max_map_signs` | `20.0` | Map signposts | Max active map badges. |
| `shell.max_rest_buttons` | `40.0` | Shell action cap | Maximum resting shell buttons. |
| `shell.max_rest_panels` | `6.0` | Shell panel cap | Maximum resting shell panels. |
| `shell.next_hint_days` | `60.0` | `ShellContext` | Interval (campaign days) between unobtrusive advisor hints. |

---

## 4. Sprites, Shaders & 9-Slice Margin Conventions

### Sprite Declarations (`game/gfx/interface/90_shell_sprites.json` & `56_governance_palette.json`)
`SpriteCatalog` discovers every image under `gfx/` by filename stem and lets any JSON map explicit IDs under `"sprites"`:

**Sculpted Chrome & Shell Sprites (`90_shell_sprites.json`)**:
- `shell_corner_ornament` -> `res://game/gfx/interface/shell/corner_ornament_brass.png` *(64x64 RGBA top-left brass/pewter L-bracket; mirrored horizontally/vertically across all 4 plate corners by `ChromePlateArt.AttachCornerOrnaments`)*
- `shell_divider_medallion` -> `res://game/gfx/interface/shell/divider_engraved_medallion.png` *(256x24 RGBA tapered bar with central engraved diamond medallion; `ExpandMode.IgnoreSize`, `StretchMode.Scale`, rendered inside a centered wrapper at `shell.divider_alpha`)*
- `shell_button_bevel` -> `res://game/gfx/interface/shell/button_bevel_sheen.png` *(128x48 warm matte dark-slate beveled plate used for sculpted button 9-slices)*
- `shell_dossier_header` -> `res://game/gfx/interface/shell/dossier_paper_header.png` *(256x64 warm slate/parchment header tint behind `ShellWorkPanelView` titles and ruler cards)*
- `shell_meter_track` -> `res://game/gfx/interface/shell/meter_track_engraved.png` *(128x16 recessed engraved gauge channel for telemetry meters inside `ShellRegionVisualsView`)*
- `icon_atlas_terrain` -> `res://game/gfx/interface/shell/icon_atlas_terrain.png` *(64x64 topographic terrain badge used on region terrain & ground cards)*
- `shell_plate_brass_9s` / `_2x`, `shell_plate_umber_9s` / `_2x`, `shell_header_brass_9s` / `_2x`
- `shell_button_brass_cta_9s` / `_2x`, `shell_button_umber_9s` / `_2x`, `shell_tab_folder_9s` / `_2x`
- `shell_well_recessed_9s` / `_2x`, `shell_flag_pocket_9s` / `_2x`, `shell_paper_tag_9s` / `_2x`
- `shell_newspaper_card_9s` / `_2x`, `shell_corner_joint` / `_2x`, `shell_round_bezel` / `_2x`, `shell_round_bezel_active` / `_2x`
- `shell_header_frieze`, `shell_plate_weave`, `shell_paper`, `leader_anonymous`

**Governance Icons (`56_governance_palette.json`)**:
`icon_agenda`, `icon_alliance_commitment`, `icon_credit_confidence`, `icon_debt_default`, `icon_lake_logistics`, `icon_laws`, `icon_public_debt`, `icon_river_crossing`

**Shaders (`72_flag_cloth.json`, `73_menu_globe.json`, `74_ui_relief.json`)**:
`"shaders"` maps logical shader keys (`flag_cloth`, `menu_globe`, `menu_globe_air`, `menu_globe_sky`, `ui_relief`) to `.gdshader` resource paths.

### 9-Slice Margin Conventions
There are three distinct 9-slice paths in Deal or Force; mod textures must respect each path's pixel margins so corners do not warp or stretch:

1. **Shell `NineSlice` (`ShellContext.NineSlice` in `Scripts/ui/shell/ShellContext.cs`)**
   - Asset naming: `<frameName>.png` (1x baseline) paired with `<frameName>@2x.png` (HiDPI, twice the pixel resolution).
   - Automatic HiDPI selection: when viewport width `>= 1600px`, `NineSlice` loads `<frameName>@2x.png` (falling back to `<frameName>.png`) and **doubles `sliceMargin`**: `TextureMarginLeft/Top/Right/Bottom = hiDpi ? sliceMargin * 2 : sliceMargin` (default `sliceMargin = 16` -> `16px` at `<1600px`, `32px` at `>=1600px`). `ContentMarginLeft/Top/Right/Bottom` stays in logical unscaled pixels.
   - Authoring rule: keep corner caps, rivets, and outer bevels inside the outer `sliceMargin` px (1x) / `2 * sliceMargin` px (`@2x`), and ensure the inner cross/center slice tiles or stretches uniformly.

2. **Sculpted Button 9-Slice (`ChromePlateArt.BuildSculptedButtonStyle`)**
   - Uses `shell_button_bevel` (`button_bevel_sheen.png`, `128x48` nominal dimensions).
   - `slice = MathF.Round(6f + Math.Clamp(shell.plate_bevel_px, 1f, 6f))` -> `8px` default across all 4 `TextureMargin*` edges.
   - State feedback (`normal`, `hover`, `pressed`, `disabled`, `primary` CTA variant) applies neutral `ModulateColor` multipliers (`0.90` -> `1.14` -> `0.68`, or `1.12` -> `1.26` -> `0.76` for primary, `(0.56, 0.55, 0.53, 0.72)` for disabled).
   - Authoring rule: author `shell_button_bevel` in desaturated neutral charcoal/pewter tones with an 8px beveled border ring so `ModulateColor` produces clean warm dark stone states without cyan or sepia tinting.

3. **Procedural Baked Materials (`10_materials.json` / `InterfaceMaterials.cs`)**
   - Active when `ui.materials` = `1` (base shipping sets `"ui.materials": 0` for the crisp vector/sprite chrome architecture).
   - `"lighting"`: `{ "direction": [-0.7, -0.7], "highlight": 0.1, "shade": 0.18, "grain": 0.018, "inner_shadow_alpha": 0.35, "drop_shadow_alpha": 0.45 }`
   - `"materials"` roles: `plate`, `plate.header`, `plate.strip`, `plate.modal`, `well`, `button`, `button.primary`, `chip`, `round`, `flag.mount`, `tooltip`, `token`.
   - Role properties: `inherits`, `top`, `bottom`, `border`, `engrave`, `engrave_inset`, `bevel`, `corner_radius`, `border_width`, `drop_shadow`, `inner_shadow`, `inset`, `grain`, `notches`, `shape` (`""` | `"circle"` | `"shield"`), `texture`, `content_margin` (`[left, top, right, bottom]`).
   - Procedural texture baking uses `TextureMargin* = clamp(panel.texture_margin, 8, 64)` (`16px` default).

---

## 5. Complete Colour Token Catalogue

### Base Skin Palette (`00_skin.json` & `02_branding.json` & `03_urban_districts.json` & `65_leader_portraits.json`)
- **Surfaces & Lines (Matte Warm Charcoal / Obsidian)**: `surface.background`, `surface.base`, `surface.panel`, `surface.panel_tint`, `surface.raised`, `surface.inset`, `surface.overlay`, `surface.line`, `surface.line_faint`
- **Buttons**: `button.fill`, `button.fill_hover`, `button.fill_pressed`, `button.fill_disabled`, `button.border`, `button.border_hover`, `button.border_pressed`, `button.border_disabled`, `button.text`, `button.text_hover`, `button.text_pressed`, `button.text_disabled`, `button.primary_fill`, `button.primary_fill_hover`, `button.primary_fill_pressed`, `button.primary_border`
- **Text Hierarchy (WCAG AA ramp)**: `text.bright`, `text.body`, `text.body_soft`, `text.muted`, `text.caption`, `text.hint`, `text.faint`, `text.accent`, `text.accent_dim`
- **States**: `state.positive`, `state.warning`, `state.negative`, `state.critical`, `state.info`
- **Charts**: `chart.tick`, `chart.hover`, `chart.row_hover`
- **Globe, Terrain & Map**: `globe.ambient`, `globe.atmosphere`, `globe.space`, `globe.selection`, `globe.city`, `globe.city_capital`, `globe.city_outline`, `globe.label`, `globe.label_outline`, `globe.label_minor`, `globe.label_minor_outline`, `globe.label_province`, `globe.label_province_outline`, `map.city_building`, `map.city_roof`, `map.player`, `map.allied`, `map.friendly`, `map.neutral`, `map.rival`, `map.at_war`, `map.war_self`, `map.war_attacker`, `map.war_defender`, `map.war_bystander`, `map.counter_rim`, `map.terrain_low`, `map.terrain_mid`, `map.terrain_high`, `map.value_low`, `map.value_mid`, `map.value_high`
- **Military Map Counters**: `military.ready`, `military.planning`, `military.shaken`, `military.routed`, `military.battle`, `military.battle_outline`, `military.clash`, `military.clash_outline`
- **Goods & Buildings**: `icon.commodity`, `goods.industrial_materials`, `goods.coal`, `goods.oil`, `goods.iron_ore`, `goods.rare_earths`, `building.infrastructure`, `building.extraction`, `building.power_plant`, `building.arms_industry`
- **Brand & Portraits**: `brand.paper`, `brand.intro_background`, `portrait.anonymous_background`, `portrait.anonymous_foreground`
- **Materials & Categories (`10_materials.json`)**: `material.plate.top`, `material.plate.bottom`, `material.plate.border`, `material.plate.engrave`, `material.well.fill`, `accent.primary`, `icon.category.economy`, `icon.category.military`, `icon.category.diplomacy`, `icon.category.society`, `icon.category.state`

### Shell Chrome Colours (`90_shell.json` & `20_campaign_structure.json`)
> **Protected `MenuChromeTests` architectural anchors in `90_shell.json`**: `#222120` (`shell.plate_top`), `#171615` (`shell.plate_bottom`), `#0C0C0B` (`shell.well_top`), `#9B958C` (`shell.brass_hi` / `shell.brass_cta`), `#F2F0EC` (`shell.paper`), `#302E2C` (`shell.rule`).
- **Ink & Paper**: `shell.paper`, `shell.paper_muted`, `shell.paper_faint`, `shell.paper_ghost`, `shell.icon`, `shell.highlight`, `shell.ink`, `shell.ink_muted`, `shell.ink_faint`, `shell.paper_tag_top`, `shell.paper_tag_bottom`, `shell.paper_tag_edge`, `shell.news_paper`, `shell.news_tear`, `shell.journal_rule`, `shell.pause_amber`
- **Charcoal Plates, Wells & Pewter-Brass**: `shell.plate_top`, `shell.plate_bottom`, `shell.plate_hover`, `shell.body_top`, `shell.body_bottom`, `shell.well`, `shell.well_top`, `shell.well_bottom`, `shell.brass_hi`, `shell.brass`, `shell.brass_dim`, `shell.brass_cta`, `shell.brass_shadow`, `shell.border`, `shell.outliner_border`, `shell.engrave`, `shell.rule`, `shell.sep_dark`, `shell.sep_light`, `shell.ruled_stroke`, `shell.ruled_ink`, `shell.tick_ink`
- **Shell States**: `shell.state.negative`, `shell.state.negative_text`, `shell.state.warning`, `shell.state.warning_text`, `shell.state.positive`, `shell.state.info`, `shell.state.crisis`
- **Outliner / Navigation Rows (`20_campaign_structure.json`)**: `shell.background`, `shell.border`, `shell.row`, `shell.row_alternate`, `shell.space`

### Feature-Specific Colours (`game/gui/` & `91_controls_hotkeys.json`)
- **Production (`28_production_units.json`)**: `production.card`, `production.border`, `production.inset`, `production.hover`, `production.focus`, `production.empty_border`, `production.empty_icon`, `production.military`, `production.military_fill`, `production.civilian`, `production.civilian_fill`, `production.blocked`
- **Army Bar (`75_army_bar.json`)**: `army_bar.card`, `army_bar.card_selected`, `army_bar.border`, `army_bar.border_selected`, `army_bar.title`, `army_bar.body`, `army_bar.count`, `army_bar.status.holding`, `army_bar.status.moving`, `army_bar.status.attacking`, `army_bar.status.battle`, `army_bar.status.sea`, `army_bar.status.idle`
- **Planning Bar (`76_planning_bar.json`)**: `planning_bar.background`, `planning_bar.border`
- **Combat Readout (`39_combat_readout.json`)**: `combat.badge_background`, `combat.bar_background`, `combat.strength`, `combat.organisation`, `combat.supply`, `combat.equipment`, `combat.cohesion`
- **War Ledger (`36_war_ledger.json`)**: `war.card`, `war.card_hover`, `war.card_border`, `war.card_border_hover`, `war.card_mine`, `war.track`, `war.front_line`, `war.inset`, `war.inset_border`, `war.killed`, `war.wounded`, `war.captured`
- **Support Map (`34_support_map.json`)**: `support.air`, `support.air_defence`, `support.artillery`, `support.critical`, `support.depleted`, `support.displaced`, `support.drone`, `support.ready`, `support.standby`
- **Resource Atlas (`35_resource_atlas.json`)**: `resources.high`, `resources.mid`, `resources.low`, `resources.none`
- **Map Borders (`77_map_borders.json`)**: `map.border_country`, `map.border_region`, `map.border_province`, `map.border_province_halo`, `map.border_occupation`, `map.border_occupation_key`
- **Map Look (`78_map_look.json`)**: `map.look_capital_fill`, `map.look_coast_ink`, `map.look_foam`, `map.look_label_fill`, `map.look_label_outline`, `map.look_lamp`, `map.look_paper`, `map.look_region_fill`, `map.look_river_fill`, `map.look_river_outline`, `map.look_sea_chart`, `map.look_sea_coast`, `map.look_sea_deep`, `map.look_sea_mid`, `map.look_sea_shelf`, `map.look_shade`, `map.look_unowned`
- **Controls & Hotkey Badges (`91_controls_hotkeys.json`)**: `controls.everywhere_border`, `controls.everywhere_fill`, `controls.focus_border`, `controls.keycap_border`, `controls.keycap_ink`, `controls.keycap_paper`, `controls.keycap_shadow`, `controls.undo_border`, `controls.undo_fill`

---

## 6. Layout Rectangles (`"layout"`)

Each layout entry supports partial or full overrides:
```json
"layout": {
  "hud.top": {
    "anchors": [0.0, 0.0, 1.0, 0.0],
    "offsets": [96, 6, -370, 80],
    "material": "plate"
  }
}
```
- `anchors`: `[left, top, right, bottom]` (`0..1` screen fraction).
- `offsets`: `[left, top, right, bottom]` (pixels from anchor edges). Omitting `anchors` preserves shipped anchors; omitting `offsets` preserves shipped offsets.
- `material`: optional `10_materials.json` role name (or `""` to opt out).

**All Layout Keys**:
- **Shell HUD (`game/gui/90_shell.json`)**: `shell.top_plate`, `shell.rail`, `shell.date_plate`, `shell.alert_column`, `shell.map_bar`, `shell.strip`, `shell.news_card`, `shell.left_panel`, `shell.full_workspace` — plus responsive `<1600x900` counterparts `shell.compact.*` (`top_plate`, `rail`, `date_plate`, `alert_column`, `map_bar`, `strip`, `news_card`, `left_panel`, `full_workspace`).
- **HUD Panels (`00_panels.json`, `10_campaign_shell.json`, `67_situation_desk.json`, `75_army_bar.json`, `76_planning_bar.json`, `79_hud_resources.json`, `35_resource_atlas.json`)**: `hud.top`, `hud.date_plate`, `hud.flag_mount`, `hud.navigation`, `hud.side_panel`, `hud.alerts`, `hud.outliner`, `hud.army_panel`, `hud.fleet_panel`, `hud.country_card`, `hud.region_card`, `hud.peace_panel`, `hud.support_panel`, `hud.map_controls`, `hud.resource_atlas`, `hud.army_bar`, `hud.planning_bar`, `hud.hotseat`, `hud.network`, `hud.desk_left`, `hud.desk_right`, `hud.desk_tab_left`, `hud.desk_tab_right`.
- **Menus, Picker, Modals & Workspaces (`00_panels.json`, `22_multiplayer.json`, `24_workshop.json`, `27_research_tree.json`, `28_production_units.json`, `40_ultimatums.json`, `57_policy_workspace.json`, `58_deal_workspace.json`, `58_handbook.json`, `59_conflicts.json`, `60_situation_room.json`, `61_media_library.json`, `62_doctrine_screen.json`, `64_workspace_inset.json`, `71_country_rankings.json`)**: `menu.main`, `picker.heading`, `picker.country`, `picker.actions`, `multiplayer.connection`, `multiplayer.countries`, `workshop.panel`, `workspace.situation`, `workspace.media`, `modal.diplomacy`, `modal.event`, `modal.handover`, `modal.ledger`, `modal.multiplayer`, `modal.pause`, `modal.peace_offer`, `modal.production`, `modal.research`, `modal.save_load`, `modal.settings`, `modal.support`, `modal.trade`, `modal.war_ledger`, `modal.ultimatum`, `modal.policies`, `modal.deal`, `modal.handbook`, `modal.conflicts`, `modal.doctrines`, `modal.rankings`.

---

## 7. Other Moddable `game/gui/` & `game/interface/` Metrics & Keys

All remaining tunables live under `"metrics"`, `"campaign"`, or `"hotkeys"` and can be overridden from `mods/<mod_id>/interface/*.json` or `mods/<mod_id>/gui/*.json`:
- **Typography & Spacing (`28_ui_font.json`, `63_spacing_grid.json`, `29_hud_readability.json`)**: `ui.font.*` (26 type roles from `micro` through `display_large` and `handbook.*`), `hud.density` (`0.8..1.25` clamp), `spacing.xs/sm/md/lg/xl`, `control.height` / `.small`, `panel.gutter`, `panel.pad`, `tab.gap`, `tab.height`, `hud.alert_row_gap`, `hud.stat_labels`, `hud.tabular_numbers`.
- **HUD Stats & Shell (`10_campaign_shell.json`, `20_campaign_structure.json`, `79_hud_resources.json`, `60_situation_room.json`)**: `hud.alerts.max_rows`, `hud.alerts.row_height`, `hud.speed_pips`, `shell.navigation_*`, `shell.outliner_*`, `shell.tab_height`, `shell.row_padding`, `hud.flag_mount_size`, `hud.portrait_slot`, `hud.resource_cell_width*`, `hud.resource_slots*`, `hud.stat_state.*`, `hud.top_stats.primary.*`, `hud.brief_row_height`, `hud.two_row_below`, `hud.promote_bad_stats`, `hud.second_row_height`, `hud.spark_height`.
- **Screens & Widgets**: `production.*` (`28_production_units.json`), `army_bar.*` (`75_army_bar.json`), `planning_bar.*` (`76_planning_bar.json`), `combat.*` (`06_battle_dossier.json`, `39_combat_readout.json`), `war_ledger.*` (`36_war_ledger.json`), `research.*` (`27_research_tree.json`), `support.*` (`34_support_map.json`, `38_flat_map.json`), `resources.*` (`35_resource_atlas.json`), `formation.*` (`33_formable_nations.json`), `diplomacy.*` (`08_diplomacy_comfort.json`, `25_diplomatic_actions.json`), `recruitment.*` (`09_recruitment_workbench.json`), `doctrine.*` (`10_doctrine_readability.json`), `decision.*` (`74_decision_readability.json`), `rankings.weight.*` (`05_country_rankings.json`), `crises.regions_shown` (`59_governance_situation.json`), `agenda.*` (`64_agenda_workspace.json`), `situation.*` (`60_situation_room.json`), `construction.queue_height` (`31_construction_queue.json`), `exposure.*` (`22_strategic_reports.json`, `26_supply_stress.json`), `atlas.*` (`21_opportunity_atlas.json`, `59_atlas_surfaces.json`, `61_media_library.json`), `chronicle.page_size` (`22_strategic_reports.json`).
- **Map, Globe, Flags & Cursors**: `map.*` (`04_river_contours.json`, `24_workshop.json`, `38_flat_map.json`, `59_map_capital_marks.json`, `62_responsive_map_controls.json`, `77_map_borders.json`, `78_map_look.json`), `map_controls.*` (`30_icon_roles.json`, `62_responsive_map_controls.json`), `map_editor.*` (`23_map_editor.json`, `37_scenario_editor.json`), `picker.actions_bottom_inset`, `movement_marker.art_radius` (`32_movement_markers.json`), `flag.*` (`07_flag_cloth.json`), `cursor.*` (`36_cursors.json`).
- **Hotkeys & Catalogue (`91_controls_hotkeys.json`, `20_campaign_structure.json`, `30_icon_roles.json`, `31_action_icons.json`, `56_governance_icons.json`, `58_deal_workspace.json`)**: `"hotkeys"` bindings (`78` actions), `"controls.*"`, and `"campaign"` (`top_stats`, `navigation`, `screens`, `outliner_order`, `hidden_outliner`, `icons`, `icon_categories`).

---

## 8. Minimal Mod Example (`mods/<mod_id>/`)

See working examples in `mods/_templates/interface_reskin/` and `mods/_templates/era_skin/`.

```text
mods/my_slate_theme/
├── manifest.json
├── interface/
│   └── 95_slate_override.json
├── gui/
│   └── 95_pacer_and_layout.json
└── gfx/
    └── interface/
        ├── 95_custom_sprites.json
        └── shell/
            ├── corner_ornament_custom.png
            ├── divider_custom.png
            └── button_bevel_custom.png
```

`mods/my_slate_theme/interface/95_slate_override.json`:
```json
{
  "colors": {
    "surface.background": "#141413",
    "surface.panel": "#1D1C1A",
    "text.accent": "#E6CFA0"
  },
  "metrics": {
    "shell.ornament_alpha": 0.45,
    "shell.divider_alpha": 0.85,
    "shell.button_texture_enabled": 1.0,
    "shell.plate_bevel_px": 3.0,
    "shell.news_min_dwell_sec": 3.5,
    "shell.news_cooldown_sec": 1.5,
    "shell.news_audio_cooldown_sec": 4.0,
    "shell.news_max_queue": 4
  },
  "layout": {
    "shell.news_card": {
      "offsets": [660, 82, 1280, 198]
    }
  }
}
```

`mods/my_slate_theme/gfx/interface/95_custom_sprites.json`:
```json
{
  "sprites": {
    "shell_corner_ornament": "res://mods/my_slate_theme/gfx/interface/shell/corner_ornament_custom.png",
    "shell_divider_medallion": "res://mods/my_slate_theme/gfx/interface/shell/divider_custom.png",
    "shell_button_bevel": "res://mods/my_slate_theme/gfx/interface/shell/button_bevel_custom.png"
  }
}
```
