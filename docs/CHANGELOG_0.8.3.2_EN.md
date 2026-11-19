# Deal or Force — EntertAlpha 0.8.3.2

19 November 2026 · Windows 64-bit · Godot 4.8 Beta 3 (.NET)

Major visual cohesion, HUD workflow, simulation responsiveness, and performance release built on top of 0.7.2.3.

## Highlights in EntertAlpha 0.8.3.2

### Unified Opaque Command Desk Chrome & Custom Assets
- **100 % opaque slate-graphite & muted pewter chrome** across Main Menu, Country Picker, Campaign HUD, State Affairs, Production, Trade, Diplomacy, and all modal workspaces (eliminating translucent mud and residual navy-blue surfaces).
- **Sculpted UI details (`Creative Claw` assets in `game/gfx/interface/shell/`, all moddable via `90_shell_sprites.json` & `game/interface/*.json`)**: beveled charcoal button surfaces (`shell_button_bevel`), chamfered corner brackets (`shell_corner_ornament`), engraved compass dividers (`shell_divider_medallion`), cartographic dossier header plates (`shell_dossier_header`), recessed graduated meter tracks (`shell_meter_track`), and terrain contour emblems (`icon_atlas_terrain`).
- **Symmetrical white architectural perimeter frames** on the right outliner (`ShellAlertColumn`), `ShellDatePlate`, `ShellMapBar`, and `ShellMinimap` matching the top-left corner plate (`shell.outliner_border` / `shell.outliner_border_width`).

### Country & Region Dossiers, Rail & World Power Rank
- **Ruler Portrait Cameo in every Country Dossier**: inspecting any nation (domestic or foreign) now shows its leader card with cameo/mod portrait, title, age, trait, ideology, and head of government.
- **Locked-width, visual-first Region Dossier (`ShellRegionVisualsView`)**: fixed horizontal container eliminates width jitter across ticking days; rich health meters, terrain/ground multiplier card, stacked culture/faith/age composition bars, buildings/deposits, and visible garrisons appear across all 4 tabs for both own and foreign regions.
- **Decisions in Left Navigation Rail**: Decisions (`ShellRail_Decisions`) with live actionable badge count now sits directly inside the governance group on the left rail alongside State Affairs.
- **Overall World Power Rank**: top-left HUD rank badge (`№ X overall`) and Country Dossier now display overall composite superpower ranking matching Country Rankings weights.

### Dispatch Pacing, Window Stacking, Globe & Typography
- **Calm, flicker-free dispatches (`ShellNewsPacer`)**: minimum real-time dwell (`2.8s`), bounded priority queue keeping War/Crisis dispatches ahead of routine alerts, audio cooldown (`3.5s`), hover-to-freeze timer, and a dedicated close (`×`) button; pressing `Esc` no longer swallows closes onto the news card.
- **Strict modal & Save/Load stacking**: `Esc`, `Space`, and speed hotkeys (`1–5`) honor the top open system modal (`Save/Load`, `Settings`, `PauseMenu`), preventing duplicate Pause Menu overlays or unintended unpausing.
- **De-hazed Orbit & Grounded Cities**: clearer Menu/Campaign atmosphere and cloud pass, grounded matte 3D city blocks, supersampled modern sans-serif city typography (`map.cities`), and updated `Gqeberha` naming.

### Performance, Memory & Android/Mobile Readiness
- Eliminated redundant updates on hidden Classic HUD widgets when New Shell is active, eliminated daily `34,000`-province scans inside Region Dossiers via map-revision caching, coalesced Speed-5 HUD refreshes (`0.22s` desktop / `0.35s` mobile), added `O(1)` chronicle checks, preallocated minimap raster buffers, and added automatic mobile GPU quality + smooth-tick adaptation (`PerformanceGovernor`, `DealOrForceGame`).
