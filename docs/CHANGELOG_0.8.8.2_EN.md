# Deal or Force — PortAlpha 0.8.8.2

26 November 2026 · Windows x64 & Android · Godot 4.8 Beta 3 (.NET 10)

PortAlpha 0.8.8.2 advances the **PortAlpha** publication branch (Android `versionCode` 14) with native Rust SIMD kernels (`dof_kernels` across diplomacy, market, distance, render-prep, and economy/demography), configurable 4-mode `ai_diplomacy_lod`, weekly heavy-stage staggering, a 6-pillar desktop real-time map/VRAM/GC smoothness overhaul on top of Speed-5 render-sync locking, GLES3 GPU shader & mobile VRAM/RAM reductions, plus fixes for ship scaling, `rising:...` country names, Observer Landless Movements UI, `!is_inside_tree()` guards, and `zipalign` Android packaging. For earlier notes see [CHANGELOG 0.8.7.3](CHANGELOG_0.8.7.3_EN.md) and [CHANGELOG 0.8.5.3](CHANGELOG_0.8.5.3_EN.md).

---

## Native Rust SIMD kernels (`dof_kernels`)

- **Cross-platform Rust kernel library (`native/dof_kernels/`).** Compiled with `cargo build --release` for Win64 (`dof_kernels.dll`, AVX2/SSE2 runtime dispatch, `Caps=0xDF`), Android `arm64-v8a` (`libdof_kernels.so` with NEON and 16 KB ELF page alignment), and Android `x86_64` (`libdof_kernels.so`). `DealOrForceGame` logs `[dof-kernels]` availability, ABI, SIMD level, and capability flags on campaign boot.
- **Diplomacy, Market, Distance & Render-Prep SIMD (`CapDiplomacy` / `CapMarket` / `CapDistance` / `CapRenderPrep`).** `KernelBench` (`11/11 BIT-EXACT` vs pure C#): `dip_pair_score` `2.63×`, `dip_reason_decay` `1.62×`, `market_settle` `3.10×`, `market_clear` `2.45×`, `distance_within_range` `1.80×`, `raster_pack_overlays` `1.38×`, and `political_plate (1920×960)` `6.57×` (`18.997 → 2.892 ms`). `PolyglotKernels` measures `raster_frontier_5760x2880` at `9.64×` (`685.59 → 71.09 ms`, bit-exact across 16.59M cells).
- **Economy & Demography SIMD (`CapEconomyDemo`).** `demo_step_cohorts` `5.05×`, `econ_household_groups` `5.75×`, `econ_vital_rates` `8.92×`, and `demo_aggregate_nations` `4.00×` (kept opt-in via `DOF_NATIVE_DEMO_AGGREGATE=1` after end-to-end `SimBench` showed unbatched `StatesOf` packing outweighed a simple sum). On `SimBench 120d`, native kernels trim `economy` by `9.9%`, `eco:states` by `13.1%`, and `demography` by `9.7%` with identical `CampaignFingerprint` against `DOF_KERNELS_DISABLE=1`.

## Configurable AI Diplomacy LOD, Simulation Staggering & RAM Pass

- **4-mode `ai_diplomacy_lod` campaign setting (`00_settings.json`, EN/RU `zz_447_ai_diplomacy_lod.json`).** Choose live between `full` (all countries evaluated), `world` (player-independent significance for Great Powers, wars, allies, neighbors, and flashpoints), `balanced` (default balanced focus), and `player_focus` (prioritizes the player's sphere and falls back automatically to `world` in Observer Mode). Preserved in campaign `Defines` and hashed into `CampaignFingerprint` when set away from `balanced`.
- **Weekly heavy-stage staggering & `FrontManager` caching.** Spreads weekly `MarketCore`, `BrainDrain`, `TechnologyCore`, `TickStages`, `MovementRules`, `Statecraft`, and cohort `InternalMigration` across `ElapsedDays % 7`, with `QuietFronts` caching and `FrontDisengagement` 8-slot LRU (`0` smooth frames `>33 ms` in `SimBench 120d --profile`; worst frame `25.7 ms`, median `1.37 ms`).
- **RAM & GC optimization sweep (`HistoryCore`, `LongCampaign`, `SaveRows`, mobile boot GC).** Releases the `33.2 MB` `_frontierAuthored` buffer on touch/mobile, runs a one-shot Gen-2 GC after mobile map load (`gc_ms=35`, managed heap `153 MB` on Galaxy S22 World War), tightens collection capacities + `TrimExcess()`, and interns repeated tags (`bozhe.bdfsave` Year 2131 managed heap trimmed `534 → 500 MB` at load, `450 → 408 MB` post-Diet, `537 → 508 MB` post-153d).

## Desktop Speed-5 Render Sync, Smoothness Overhaul & Android GLES3

- **5-part Desktop Speed-5 map/front/counter render-sync fix.** Fixed map borders, occupation stripes, battle markers, and division counters falling behind simulation time at Speed 5:
  1. `DealOrForceGame._Process` shares its step-boundary read lock directly via `GlobePresentation.SyncWorldVisualsUnderReadLock` so HUD refreshes never starve the world renderer.
  2. `Campaign.TickStages.RunStages` breaks at stage boundaries on `AccessGate.IsReaderStarving`, paired with a `50 ms` starvation-recovery margin in `CampaignAccessGate.ExitWrite`.
  3. Desktop `BackgroundSimRunner` Speed-5 worker slicing tightened (`sliceMs=24 ms`, `batchElapsedMs >= 8 ms`, `readerWorkAheadMs = 0`) while keeping all mobile pacing constants untouched.
  4. `SyncFencedWorldVisuals` paints province controllers and live war fronts (now tracking `WarRevision` in addition to `MapRevision`, zero-alloc `PaintLiveWarFronts`) *before* syncing armies/battles/routes at unified `33 ms` desktop cadence.
  5. `GlobePlateRepaint` keeps fast Rust `PoliticalPlate` repaints (`0.75 s` normal / `2.5 s` Speed 4–5) while throttling heavy `5760×2880` `FrontierField` rebuilds (`2.5 s` normal / `10.0 s` Speed 4–5).
- **6-pillar real-time smoothness, VRAM/LOH GC & scene-tree overhaul.** Eliminated `300–709 ms` frame stalls, unmanaged image leaks, and camera-vs-sync jitter:
  1. Reuses persistent LOH pixel scratch arrays via `PoliticalPlate.PaintInto` and `FrontierField.BuildInto`, staggers plate/frontier GPU uploads across separate frames, and calls `.Dispose()` on temporary `Image` handles.
  2. Keys `CityMiniatureRenderer` on structural changes (`DestroyedProvinces.Count`, `CapitalRevision`, year) instead of raw `MapRevision`, adds differential `_countryLabelCache` in `GlobePresentation.Labels`, and debounces fast-sim placement.
  3. Widens `SurfaceBattleBadge` redraw hysteresis (`0.01f` share / `0.15f` momentum), expands the battle badge `SubViewport` pool (`24 → 192`), and reuses `ArmyCounterTexture` scratch buffers with `Bilinear` filtering.
  4. Replaces nested `liveWars × provinces` work with a single `O(P)` `PaintLiveWarFronts` pass and caches marker geo/moving/arrival-glide state (`HasPendingArrivalGlide`).
  5. Advances camera pose (`AdvanceCameraForFrame`) before fenced sync, uses monotonic `>` revision gating without mid-step lock hammering, adapts counter damping (`_simStepIntervalEma`), and defers flat counter density rebuilds via `InvalidateFlatCounterDensity()`.
  6. Adds `IsInsideTree()` / `IsInstanceValid()` camera/root guards across `GlobeCities` and `GlobeOverlaySync` (`!is_inside_tree()` fixes).
- **5 GLES3 GPU shader optimizations (`globe_map.gdshader`).** Preserves the exact 12-sampler `PackedMapShader` budget while trimming per-pixel sample work; paired with `MobileRenderScaleFloor = 0.50f`, `MobileMediumDefaultHighDpiScale = 0.85f`, `2880×1440` mobile map buffers, and selective ETC2/ASTC texture compression (`sync-core.ps1`).
- **Verified Galaxy S22 (`SM-S901B`) memory & package savings.** APK shrunk `408.9 → 383.9 MB` (ARM64, 16 KB ELF aligned, `VERIFY_STORED failures=0`); live S22 World War `dumpsys meminfo`: `GL mtrack` **`654.6 → 537.6 MB`** (`-17.9%`), `SWAP PSS` **`393.6 → 64.2 MB`** (`-83.7%`), `TOTAL PSS` `1.78 → 1.66 GB`, `4.13 s` cold menu start, `0` crashes.

## Visual, Naming, Observer UI & Packaging Fixes

- **Oversized ship & infinite battle-arrow guards (`GlobeTradeTraffic`, `DateLineSeam.GlobeLineSanity`).** Clamped rogue trade ship scale transforms, filtered invalid `centerCells[provinceId] <= 0` origins, and restricted battle assault arrows to province neighbors of `battle.ProvinceId`.
- **Nested `rising:...` country name repair & Observer UI fixes.** Unwraps and repairs stacked `rising:...` insurgent names (`CountryNames`, `NationNaming`), fixes empty Landless Movements lists (`MovementUI`, `ObserverUiRules`) and `player == null` HUD guards, and adds PowerShell stderr protection for `zipalign` probe in Android `build-apk.ps1`.
