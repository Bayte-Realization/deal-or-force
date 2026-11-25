# Deal or Force — EntertAlpha 0.8.5.3

24 November 2026 · Windows x64 & Android · Godot 4.8 Beta 3 (.NET 10)

This cumulative changelog covers the 0.8 EntertAlpha series, from the 19 November release of 0.8.3.2 through 0.8.3.4, 0.8.3.5, and 0.8.5.3: background simulation, zero-stutter frame pacing across map and HUD, multi-decade scaling, Samsung Galaxy S21 / Mali-G78 Android stability, and the upgrade to .NET 10.

---

## 0.8.5.3 · Zero-stutter frame pacing, map & shaders

- **Zero-degradation smooth panning.** Moving the camera, orbiting the globe, or panning the flat map keeps full shader resolution and topographic relief without dropping detail mid-motion.
- **No per-frame `ImmediateMesh` rebuilds.** Converted dynamic trade lanes, battle arrows and clashes, fleet routes, offensive plans, order ribbons, sea lifts, supply lines, strait crossings, and support range rings to dirty-versioned and content-hash-gated indexed `ArrayMesh` surfaces.
- **Spatial R-tree culling & GPU texture reuse.** Integrated `RBush` 4.0.0 spatial indexes (with anti-meridian split and per-frustum-bucket caching) for over 7,000 cities, rivers, capitals, and region labels. Map `ImageTexture` buffers update in place via `.Update(...)` and stagger heavy plate/frontier uploads away from controller, fog, and palette updates at day boundaries.
- **Lossless shader optimization & GLES3 compatibility.** Hoisted `uv_dx`/`uv_dy` screen derivatives into uniform control flow, guarded zero-weight and same-slot `terrain_tiles` fetches, preserved the 12-unit packed shader and full Forward+ relief, cut `GlobeWeather` `CloudShader` from 16 to 9 3D `gnoise` octaves per pixel (~44% fragment ALU reduction across deck + shadow pass), and added `relief_height_ready` and `compat_renderer` paths for Mali-G78 GLES3 alongside a 120-frame GPU `FRAME_PACING` verification probe.
- **Sculptural relief, coastal water & cleaner readability.** Realistic rendering refines biome blending, ocean depth gradients, coastal surf, and mountain elevation; armies, cities, fronts, battles, and labels sit at terrain height. Dashed trade lanes draw only in Economy mode or for the selected country, excessive solar ocean glare was capped, battle/capital/selection glows became crisp ink strokes, and **Spin** (`F6`) returns to the NewShell map bar.

## 0.8.5.3 · HUD pacing, Release Server GC & Country Picker

- **Desktop Release Server GC, off-thread saves & `BackgroundSimRunner`.** `DealOrForce.csproj` and `runtimeconfig.template.json` now enable `System.GC.Server`, `System.GC.Concurrent`, `Optimize`, and `TieredPGO` in `ExportRelease` builds as well as Debug (eliminating blocking Workstation GC pauses). `ModLoader.HasRuntimeCode` keeps `BackgroundSimRunner` active when pure-data mods or empty workspace stubs are loaded, `SaveLoadUI.SaveToSlot` runs `SaveSystem.Prepare` + `.Commit` off the main thread (`RequestYield`) to remove the main-thread save hitch, and `DiagnosticsUI` excludes intentional background FPS throttling (`_isBackgroundFramePaced`) from `FRAME_STALL`.
- **Lock-free HUD snapshot & budgeted NewShell drain.** `BackgroundSimRunner` publishes an immutable `CampaignHudSnapshot` (`Volatile.Write`/`Read`) so main-thread UI frames no longer stall on `CampaignAccessGate`. `ShellOrchestrator` drains heavy views within a `1.50 ms` desktop (`1.25 ms` mobile) frame budget, `NewShell` telemetry uses `stackalloc`, `[ThreadStatic]` builders, and `HashCode` dirty checks, `DealInboxUI` eliminates double refreshes, and `LayoutAudit.Watch` suspends continuous geometry walks during active camera motion and Speed 4–5.
- **Country Picker & Display/Performance Settings.** Added a 16/24-row scrollable country list (`PickerResultsScroll`), Prestige header readout, expanded dossier height and scrollbar clearance so all six stat chips fit cleanly without clipping at `1920×1080` and `1280×720`, and clear EN/RU active GPU, renderer, frame-pacing, and Android log diagnostics cards inside `Settings → Display` and `Interface`.
- **NewShell panels & Android touch ergonomics.** Refined top-bar and workspace framing, fixed **Open Region Card** behaviour, hid territorial income and money-ledger rows on landless movements, scaled close (`×`) buttons and touch hit targets (`RecommendedCloseButtonPx`), added safe-area margins, moved the pause developer console clear of the touch keyboard, and restored the anonymous silhouette for rulers without a photo.

## 0.8.5.3 · 30-year campaign scaling & .NET 10 libraries

- **Active wars index, cohort coalescing & late-game scaling.** Added an invalidated `Campaign.ActiveWars` index and monotonic ended-war watermarks (`WarRevision`) so daily and long-campaign stages skip finished historical wars; rewrote `MoveCultures` to O(N) pooling while deterministically coalescing duplicate cohorts and folding tiny (`<= 250`) slivers; added `OpinionSystem.PruneInertReasons`, `SocialMemory.ForgetTheGone`, and `HistoryCore.Thin` compaction; and added single-pass war/leader aggregation in `OrganizationDynamics`, cached `FormableNations.CountryIdentity`, and yielded/memoized `Corridors`, `LongRangeMovement`, and `AiStatecraft.NegotiateStages`.
- **Measured SimBench scaling (Release).** In the 10-year `SimBench 3650 42 --aging` run (seed 42), Year-10 step time fell from **97.3 to 78.8 ms/day** (-19.0%), worst Year-10 spike dropped from **331 to 156 ms** (-52.9%), Year-10 Gen0 collections dropped **61.5%** (13 → 5), and Day-365 `--soak` allocation fell from **4145 to 2690 kB/day** (-35.1%, reaching **44.8 ms/day** and **2219 kB/day** at Day 3650 with `Soak PASS`). On a real Day 13,358 campaign save, `organization life` dropped from **5.2% to 1.7–1.8%** (`185 → 59 ms` pre-throttle, `0.0 MB` allocs), `consolidation` allocations dropped from **9.3 to 4.0 MB**, and profiled daily allocations fell from **5,810 to 5,375 kB/day** with exact `CampaignFingerprint` parity. Headless SimBench measurements; actual speed varies by save and hardware.
- **.NET 10 & pure-managed MIT / Apache-2.0 libraries.** Upgraded the game and standalone tools to `.NET 10` and integrated pure-managed packages with zero GPL/LGPL transitive dependencies: `CommunityToolkit.HighPerformance`, `System.Numerics.Tensors`, `K4os.Compression.LZ4`, `ZLinq`, `System.IO.Hashing` (`XxHash3`), `BitFaster.Caching` (`ConcurrentLru`), `Microsoft.IO.RecyclableMemoryStream`, `RBush`, `ZeroAlloc.Collections`, `DotNext`, `SimdLinq`, `Equativ.RoaringBitmaps`, and `Ben.Demystifier`.

## 0.8.5.3 · Android stability (Galaxy S21 / Mali-G78) & diagnostics

- **GLES3 (`gl_compatibility`) default on Android.** Switched Android rendering from mobile Vulkan to `gl_compatibility` with `msaa_3d=0` to avoid ARM Mali-G78 (`libGLES_mali.so`) Vulkan driver crashes while keeping all map styles and the 12-unit packed map shader working; added a deferred 3-frame `16×16` shader prewarm in `MainMenuScene` and mobile safeguards in `MenuGlobe`.
- **Android boot memory, texture budget & SGen LOS hardening.** Lowered Android `System.GC.HeapHardLimitPercent` from `70` to `40`, mapped `mali-g78` and `<= 8500 MB` phones to compact texture budgets in `PlatformProfile`, added `StartupIntro.ReleaseWarmResources()` to drop menu/loading textures on exit, and enforced CPU-safe mobile `process/size_limit` caps in `sync-core.ps1` (`256` flags, `1024–2048` menu/loading assets, largest `.ctex` capped at `2.55 MB` with zero CPU `GetImage()` regressions). Capped parallel boot extraction streams to `2` on `<= 8.5 GB` devices, lowered `FileStream` buffers to `4 KB` (under Mono SGen's 8 KB LOS threshold), reused pooled `ArrayPool<byte>` scratch buffers with explicit inter-stage GC cleanup, hardened `NativeKernelsLoader` on ARM64, aligned writable paths with Scoped Storage, and preserved the zero-PNG 3-tier LZ4 fast boot cache.
- **Synchronous boot trace & demystified crash logs.** `BootCrashDiagnostics` writes synchronously flushed `boot_trace.txt` (`boot_trace.prev.txt`) and `Ben.Demystifier`-formatted `last_crash.txt` (with GPU adapter, active renderer, memory stats, and trace tail) to `user://logs/` and user-accessible `Android/data/com.bayterealization.dealorforce/files/logs/` on Android (readable in Samsung My Files without ADB or root), plus `Documents\Bayte Realization\Deal or Force\logs\` on PC.

## 0.8.5.3 · Organic gameplay & balance

- **Prestige mechanic in rankings and diplomacy.** Nations earn international prestige (0–1000) from sovereignty, GDP, living standards, literacy, technologies, megaprojects, alliances, deterrence, and conduct. Prestige weights 15% into overall world rankings (with raw army share reduced to 12%) and grants a diplomatic willingness bonus.
- **GDP and income-scaled event costs.** Event monetary options scale against national GDP and income (`gdp_share` / `income_share`) instead of flat treasury percentages.
- **Systemic divided-nation reunification.** Divided-nation mainland reclamation runs through general campaign rules rather than one-tag scripting.
- **New mechanics.** **Megaprojects** (16 kinds, co-fundable by allies), **world situations**, 12 **national ambitions** and 6 **cabinet directives**, switchable **production methods** for buildings, **diseases and natural disasters**, expanded **strategic and tactical doctrines**, 36 distinct splinter-state colours, and the **Mod Content Studio** with live form validation.
- **Country dossier & economy.** Added a clickable **Capital** row and expandable **Domestic stability outlook** in the country dossier, plus a **Tax efficiency** warning in the economy workspace.

---

## 0.8.3.5 · AI staff delegation & Diplomatic Inbox

- **AI staff delegation in one place.** Your own country dossier (`Q` or clicking your flag) adds a dedicated **AI Staff** tab plus an Overview quick-link (`Configure →`) with a master toggle and individual switches for all 11 branches. **State Affairs** (`E`) also exposes the full 11-branch **THE STAFF** 2-column grid and header counter at the top of the screen.
- **Diplomatic Inbox restored to the main HUD.** A permanent **Diplomatic Inbox** bar sits at the top of the right-hand HUD column below the Date/DEFCON plate showing incoming letters across all 9 diplomatic channels, synchronized with the Diplomacy left-rail counter and dossier button.

## 0.8.3.4 · Map, menus and launcher

- **Immediate treasury and manpower feedback.** Recruitment, construction, and reconstruction deductions appear right after placing orders on the map.
- **Campaign top bar alignment.** The national figures row and outer frame resize together when the last war ends.
- Realistic map look named **Realism** in Display settings; modal frames and portrait trims keep proper draw order; closing a dossier clears province/region map highlights.
- **Stable army counters.** Counters keep steady size at a fixed camera, recycled counters appear at the right location, and distant globe readouts stay hidden behind the planet.
- **Bilingual release notes in the launcher.** English and Russian changelogs ship with the game and open under What's new.

## 0.8.3.2 · Campaign interface, map, wars and Speed 5

- **New campaign shell:** top-bar figures, right-hand clock and speed controls, left-rail navigation, live political minimap, paced newspaper dispatches, main-menu Continue card, and updated country/region dossiers.
- **Map looks & audio:** live switching between Realism, Paper, Atlas and Classic with independent political fill opacity; distinct 3D city, capital and port miniatures; Bosphorus and Dardanelles water passages; four interface sounds and **Architects of Détente**.
- **War & campaign rules:** decisive war score and full occupation settle prolonged wars, fully occupied junior coalition members can accept separate peace, and returning states recover their seat and records.
- **Speed 5 & long campaigns:** staggered weekly economy updates, pooled buffers and campaign-scoped caches, and safe folding of tiny compatible population groups. In the 0.8.3.2 SimBench benchmark at the 2024 start, Speed 5 rose from **2.61 to 6.53 days per second** and the longest simulation frame fell from **661.9 to 27.3 ms**.
- **Modding:** layered loading across more than 90 data categories, skin and icon overrides, shipped F# rules assembly, and template mods.
