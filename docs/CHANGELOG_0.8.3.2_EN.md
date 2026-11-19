# Deal or Force — EntertAlpha 0.8.3.2

19 November 2026 · Windows 64-bit · Godot 4.8 Beta 3 (.NET)

Major visual craft, structural UI redesign, campaign peace logic, and deep Speed-5/GC performance release built on top of 0.7.2.3.

## Highlights in EntertAlpha 0.8.3.2

### Uniform Multi-Surface Texture Pack, Structural UI Redesign & Full Moddability
- **5 seamlessly tiled, uniform matte UI materials (`game/gfx/interface/panels/`)**: command leather (`shell_surface_leather`), brushed slate twill (`shell_surface_slate`), archival linen (`shell_dossier_grain`), diplomatic laid paper (`shell_surface_laid_paper`), and cartographic blueprint grid (`shell_surface_blueprint`) applied edge-to-edge across menus, HUD plates, dossiers, and full-page workspaces via `ChromeSurfaceOverlay` (`Node2D` layer unaffected by container content margins), alongside symmetric uniform button bevels (`shell_button_bevel`), chamfered card wrappers (`shell_card_wrapper`), and centered diamond header filigree (`shell_header_filigree`).
- **30+ moddable icons & panel sprites (`90_shell_sprites.json` & `90_shell.json`, documented in `game/interface/README.md`)**: 12 monochrome alabaster terrain badges (`icon_terrain_*`), 11 normalized menu action icons (`icon_menu_*`), ornate ruler cameo bezel (`shell_portrait_bezel`), geodesic modal backdrop (`shell_modal_backdrop`), recessed tab strip (`shell_tab_strip`), and skin metrics (`shell.surface_texture_alpha`, `shell.portrait_bezel_alpha`, `shell.card_wrapper_enabled`, etc.).
- **Redesigned Command Desks & Archive**:
  - **Main Menu (`MainMenuScene`)**: Hero Continue Campaign card with nation flag, deduplicated campaign subtitle, diamond filigree, leather folio texture, and rescaled action icons.
  - **Pause Desk (`PauseMenuUI`)**: Live Campaign Card featuring player flag, ornate ruler cameo bezel, composite world rank, active war badge, geodesic backdrop, and leather surface.
  - **Campaign Archive (`SaveLoadUI`)**: Instant search/filter field (`slot`, `name`, `tag`), live save counter, and chamfered dossier cards with nation flags and save-format pills.
  - **Country & Region Dossiers & Left Rail (`ShellCountryDossierView`, `ShellRegionVisualsView`, `ShellWorkPanelView`, `ShellNavRailView`, `SupportScreenUI`)**: Standing 2×2 KPI grid, redesigned `Society` tab with engraved profile meters and 2×2 government/faith cards, evenly fitted footer buttons (`ClipText` + `TrimEllipsis`), locked-width Region Dossier with per-terrain/building/good icons, `Wars of the World` full-width front cards, and direct **Long-Range Forces** (`Support`) access from the left navigation rail (`ShellRail_Support`, blueprint grid surface, live idle-formation wartime badge, clean fit in both 1920×1080 and 1280×720).

### Decisive Peace on Total Occupation or 100% War Score & Bug Fixes
- **Forced peace (`ForcePeaceOnTotalOccupationOrFullScore` in `02_peace.json`)**: 100%-occupied countries (including junior coalition partners via `SeparatePeace`) and inter-state wars reaching `±99.5%` war score settle peace decisively instead of stalling indefinitely to state collapse; moving civil wars keep reconquest protection (`!war.CivilConflict || war.Stalled`), and human victors receive immediate enemy capitulation offers without losing agency.
- **Live UI & localisation fixes**: fixed `WarLedgerUI` body collapse (`OffsetBottom` + full-workspace span in `ShellOrchestrator`), unified Outliner `"War"` -> `"Wars"` routing (`ShellAlerts.cs`, `DealOrForceGame.cs`), cleared stale `_sideBody` text across panel switches, fixed top-bar war readout & bottom caption spacing (`ShellCornerView`), localized newspaper dispatch dates via `GameDate.Display()` with 3-line wrap (`ShellAlertColumnView`), eliminated duplicated Hero Continue subtitles, and removed 82 `p_name.is_empty()` button warnings.

### Deep Optimization (Stage A + Stage C) & Benchmarks (`SimBench` + `FormableTests`)
- **Render & Speed-5 GC fixes**: pooled `ArmyStackMarker` subtrees (`GlobePresentation.cs`), warm army counter texture cache (`CompactCounterTextures.cs`), throttled `RebuildBattleMarkers` (`MilitaryPresentation.cs`), rate-limited Speed-5 province controller GPU uploads, zero-alloc `ShellStripView.cs`, and cooperative `Campaign.StageOverdue()` slice yields inside indivisible simulation bursts.
- **Stage C simulation cadence & folding**: enabled `FoldSliverGroups = true` and `StaggerWeeklyEconomy = true` in `95_long_campaign.json`, plus smart 7-day cadence for calm peacetime AI nations (`AiCalmCadenceDays = 7`; active belligerents, threatened nations, and domestic crises evaluate daily).
- **Verified `SimBench` BEFORE vs AFTER**:
  - **2024 Start, Speed 5 (`SimBench 30 42 --frames 5,3 --frame-days 14`, 16 ms slice)**: worst smooth frame `661.9 ms -> 27.3 ms`, `p99`: `343.89 ms -> 25.39 ms`, frames `> 33 ms`: `14 -> 0`, frames `> 50 ms`: `8 -> 0`, throughput: `2.61 -> 6.53 days/s` (Speed 3: median `1.02 -> 0.67 ms`, `0` frames `> 33 ms`).
  - **Soak & Determinism**: `SimBench 365 1 --soak` -> `Soak PASS: 365/365 days, 0 alert(s), 0 invariant failure(s)`; `SimBench 60 42 --fingerprint 60` bit-for-bit identical across repeated parallel runs and `BDOF_NO_PARALLEL=1`.
  - **Late Save 2131 (`bozhe.bdfsave`, `--diet`, 30d smooth 9 ms)**: folded `69,228` sliver groups (`268,313 -> 201,890`), smooth frames `p99 21.53 ms`, `worst 32.5 ms`, `0` frames `> 33 ms`.

## Verification Snapshot
- `DealOrForce.sln` (`Debug` & `Release`), `Tools/FormableTests`, and `Tools/SimBench`: `0` compile errors; `godotInSim = 0`, `deadCandidates = 115`, all `Scripts/ui/Shell/*.cs` `< 800` lines.
- `Tools/Test-ReleaseVersion.ps1` (`PASS`) & `Tools/audit-localization.ps1 -Fail` (`0` conflicts / placeholder mismatches / empty values across `27,639` entries in `422` files).
- Live Godot MCP Pro screenshot suite refreshed across all screens in `docs/screenshots/0.8.3.2/` (`menu-planet.jpg`, `country-picker.jpg`, `map-globe.jpg`, `map-flat.jpg`, `campaign.jpg`, `region-dossier.jpg`, `region-population.jpg`, `war-ledger.jpg`, `pause-desk.jpg`, `long-range-forces.jpg`).
