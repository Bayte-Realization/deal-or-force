# By Deal or Force - the user's whole plan (kept so nothing is dropped)

Status marks: [x] done+committed, [~] done, not committed, [ ] to do.

## GUIDING PRINCIPLE (user, 2026-10-08 night)
Focus on LOOK, TIDINESS and CLARITY of the game. One interesting mechanic is not game design: build a
connected, consistent design - every mechanic visible, explained, linked to the others (deals <-> claims
<-> unification <-> civil wars <-> organizations <-> AI).

## Now (2026-10-08)
- [~] AI batch (mobilisation, holes, ThreatPosture, no war without cause, ultimatums, rebels armed
      quietly, civil wars end at the line). Bench snapshot s12; commit msg tools/b9_commit.txt;
      `git add Scripts/Simulation/ThreatPosture.cs`.
- [~] Ladders: WrapGuard.cs (real tree + w48) - wrapping labels in rows take the row's width.
- [~] No "AI/computer" in player text (tools/no_ai_words.py), credit line removed. Graphics: ask
      the user which images look machine-made.
- [~] Check boxes visible everywhere (ControlSkin.cs writes icons into the default theme).
- [~] DIPLOMACY DOSSIER (IN THE WORLD block done) shows every state of a country at a glance: wars (with whom, flags), conflicts/
      tensions, civil wars, backers/proxies, organizations, alliances, pacts, embargoes, subjects/
      overlord, claims, movements inside it. "Everything visual and immediately clear."
- [~] DEALS (meter, balance/ask-more buttons, kin/homeland land value, capital unsellable done; AI-to-AI deals TODO): verdict meter, "balance with cash", value of kin/homeland/border land, capital
      unsellable, AI-to-AI deals (enclave swaps, kin-region purchase, cash for pact), AI offers to
      the player (pending offer UI), deals in the news.
- [~] FORMABLES (10 reunifications + 2 kin unions + Arab formables open to Arab sides incl. Hamas; Kurdistan TODO; dynamic names TODO): movements/factions can form (Hamas -> Arab formables, PMR -> Moldova ...), more
      formables, flags, DYNAMIC NAMES (government/ideology/size-based names).
- [ ] ORGANIZATIONS: real 2024 bodies (EU, AU, ASEAN, Arab League, SCO, BRICS, OPEC, GCC,
      Mercosur, CIS, EAEU...), types and effects, AI joins/leaves/splits, organizations screen
      right half filled (members' flags, budget, votes), map mode.
- [ ] UNIFICATION: kin states (Serbia-like) unite peacefully / by pressure / by war / formables.
- [ ] AI keeps borders beautiful (ChatGPT started: TerritorialCoherence) - verify, extend to peace
      demands and deals.

- [ ] AI CAPITAL REWORK (user 2026-10-08: "still very dumb, doesn't use its options, not interested in
      survival or keeping its borders"): survival instinct (defend the core, retake lost land, call
      allies/guarantors, seek protectors/organizations when threatened, buy peace by deal when losing),
      use every tool (deals, ultimatums, proxies, alliances, organizations, mobilisation, decisions),
      measure with ledgers.
- [~] GLOBE CAMERA reaches the poles: latitude limit 69 -> 86 degrees (Greenland, Antarctica).
- [~] MOVEMENT MARKERS: every movement on the map shown by its FLAG (some show a different icon) - one style.
- [~] MENU NAVIGATION (ESC chain: settings/save/network, back to pause): ESC closes the top menu (save/load menu, every in-game window), Back returns
      to the previous menu from any menu; one consistent stack.
- [~] CRITICAL LAG: formables menu (all formables shown) and some other menus freeze the game - cache
      evaluations, build rows lazily, refresh daily not per frame.
- [~] CIVIL WARS that actually happen (CivilWarOutbreak.cs, SecessionEvents.Uprising; measure!) (user has never seen one): new civil wars from instability/coups/
      contested elections, with partial national breakup - minorities secede while the state is weak.
- [ ] MOVEMENT METHODS ARE DYNAMIC: peaceful movements can radicalise into violent/terrorist ones and
      radicals can moderate, with stages between; the existing labels (peaceful/armed/terrorist) become
      a moving state driven by repression, success, leadership, backers, radicalism.
- [~] FLICKERING opinion driver "They are stronger than us" appears/disappears every second - fix.
- [~] AI ARMY DISPOSITION (ArmyDisposition.cs: attrition relief + peacetime garrisons; measure): knows attrition/supply exhaustion - never keeps a stack wasting in one
      province, spreads over the region/country when attrition shows; at peace armies stand in logical
      places (borders with threats, capital, key cities, coasts), never one idle heap (ThreatPosture is
      the start - extend: peacetime garrison layout + attrition-aware spreading).
- [~] DELEGATE ANYTHING TO THE AI (command/diplomacy/cabinet portfolios + master switch added): the player can hand over the army (exists: Delegation.Army -
      surface it clearly), navy, budget, trade, research, diplomacy, construction, laws, decisions - one
      clear "Hand to advisers" switch per area and a master switch. (User: "this is what other games lack".)
- [ ] REPO CLEANUP (asked long ago): junk, stubs/placeholders, files players must not see, obvious traces
      of machine work (agent notes, CONTEXT.md, *.old, picker html, logs, .kilo worktrees...). AUDIT FIRST,
      show the user the list, delete only with their OK (other agents use some of these files).
- [ ] MORE CONTENT, unique: technologies, new countries (de facto/2024-real and formable), movements,
      events, decisions - widen and colour the game, make it more varied.

## Standing list (from earlier messages)
- [ ] Desktop list "Список изменений и улучшений для игры.txt" (117 items): #79 movement circle vs
      emblem, #98 AI inciting rebellions, #99 more movements/flags, #100 alliances unique/dynamic,
      #101-102 unification/formables - and the rest.
- [ ] De facto borders; many civil wars (more 2024 factions, new ones arising).
- [ ] Movements fixes from the earlier session period; Hamas/Malta influence realism.
- [ ] Alliances/organizations deepening: organic, dynamic, can split.
- [ ] Every mechanic visible on screen: map modes/overlays (de facto control, proxy arrows,
      alliances, organizations, claims), tooltips.
- [ ] Frontend priority: map declutter, summary panel, laws window in diplomacy style, economy
      screen consolidation; bold rework, no rose glasses.
- [ ] Backend ~20%: optimisation, debug, small UX.
- [ ] AI: close/exploit holes (done), desperate mobilisation (done), posture vs threats (done),
      multi-move geopolitics (ultimatum first - more), realistic dynamic conflicts.
- [ ] PRK-KOR endless war vs human player (AI exhaustion).
- [ ] Two coup systems (Coups.cs vs DomesticCrises) - reconcile.
- [ ] Translate remaining strings; flag credits.
- [ ] Awareness factor: done by the other agent (resident_awareness) - surface it in UI.

## ChatGPT's area right now (don't duplicate, may edit)
NaN integrity, save errors, Antarctica economy, fragmented movement territories, border beauty
(TerritorialCoherence), Deals/Organizations/TerritorialClaims first versions (staged in real tree).

## Save size (done 2026-10-08)
Compressed default + SaveDefaults omission: 247 MB -> 17.8 MB, fingerprint identical.

## Front shift to investigate
front24 on real tree = 128.9 vs 110.8 at ffa3d4d; ArmyDisposition/CivilWarOutbreak have zero effect;
cause is in ChatGPT's changes (ProductionCapacity/economy?). Bisect.

## Night of 2026-10-08 (done, uncommitted unless noted)
- ChatGPT committed b2459e9 + merge 53a61b4 (swept my earlier edits in).
- Since: Homelands/Fallen saved (multiplayer desync fixed), PMR cores, BVC name, Estrange leaves orgs,
  NATO test via organization, deal capital rule (transfers only) + RegionWorth receiving flag,
  BuyPeace gating at the caller, conflict tab names, diplomacy row names, autopilot A+B chains.
- Real 2024 organizations (EU, AU, ASEAN, Arab League, SCO, BRICS, OPEC, GCC, Mercosur, CIS, EAEU,
  ECOWAS) + organizations tab with member flags; diplomatic map shows defence blocs and shared orgs.
- Dynamic names (name.form.* by changed government/ideology), Kurdistan (required_regions) + flag.
- Civil war outbreaks capped at 3 a year worldwide; border war cooldown 8 years per pair.
- LIVING STANDARD ANCHOR (EconomicLiving + LivingOffset, LivingReversionPerWeek 0.01): the century-long
  collapse of living standards/stability (2138 save: median stability 0.25) was an unanchored random walk.
- Movement methods: militancy 0..1 with stages Civic/Militant/Armed/Terror, drifting with radicalism,
  crackdowns, designations, backers' arms vs legitimacy and land; uprisings need Armed; news + gauge.
- TODO next: commit all after s17 checks; governance term stuck at -limit; AI-to-AI kin purchases.

## 2026-10-13 evening
- REQUEST: clouds on the map (strategic view), fading out as the camera closes in; more map
  elements so it is "SUPER beautiful and full"; fog of war that looks like real fog, appearing
  when the camera is close (tactical). Strategic vs tactical camera are distinct notions.
  NOTE ChatGPT has crossover-only clouds in untracked GlobeSky.cs (MovingClouds node) - mine are
  base-game, in my own GlobeWeather.cs, and hide when MovingClouds is visible.
- Casualties: per-capita rate cap 2.5 + absolute exchange cap 3 per battle-day (CombatCore).
- UI: alert severity stripe + flash, BalanceBar under war/battle rows, unified tooltip frame.
- REQUEST (22:50): rethink the interface and ITS INTERACTION WITH THE MAP; many basic buttons /
  mechanics / possibilities / charts that change the course of history.
  My queue for it (connected, base-level):
  1. Reconnaissance: click an enemy/foreign province -> sweep (cost, cooldown) reveals a radius for
     ~10 days, the mist parts there; at peace it is an airspace incident (tension); AI uses it on
     fronts it attacks. Ties fog+mist, map, diplomacy, war planning.
  2. Province context actions on the map (right-click: reconnoitre, invest, garrison, crack down...).
  3. Address to the nation: stability / war support now, diminishing, remembered if it fails.
  4. History chart: chronicle timeline of GDP/stability/wars with event markers.
  5. Regional investment chosen on the map (development vs separatism).
- Ambience: MapAmbience.cs (wind_high / land_calm / front_far, Creative Claw, 34 credits) +
  "map_ambience" setting; moddable paths still TODO (constants in MapAmbience).
- REQUEST (23:20): optimisation "capitally, to an excellent result"; Paradox-like tick SMOOTHING,
  switchable in settings; fix many bugs; many QOL improvements for atmosphere and comfort.
- REQUEST: fix the 3 failing suites (backlog69 = tests outdated by sovereign debt -> fixed to
  position; movement82 = my WillRise let a for_hire company rise -> fixed; save-seed-mature ->
  diagnosing with SimBench --save-roundtrip).
- DECIDED: intelligence = slow intel NETWORK per target country + wartime air RECONNAISSANCE as a
  mission of the existing long-range air/drone formations (common/support, SupportCore).
- Visual/audio must IMPRESS; may install plugins/MCP that help.
- REMINDER (user): movements must stay organic/dynamic - change positions and ways of pursuing
  their goals (MovementMethods); make PMCs livelier too (change employers / fields).
- REQUEST (user): deepen civil wars, coups and uprisings, WITH GAUGES (шкалы), organic and dynamic.
- REQUEST (user, civil wars): the world REACTS to civil wars; they run plausibly, pieces break
  off countries (especially the periphery); peace is very hard to conclude; mostly protracted;
  a side confident of victory and fanatical fights to the end.
- [x] 3 failing suites fixed + pushed (093a91d): load books only never-opened accounts; PMC rise;
  cash tests to position; size threshold 0.8; finance fixture legacy vs mid-week. 47/47 green.
- REQUEST (user, 2026-10-13 night): MUCH more content - flags, pictures, formable nations,
  decisions, names, events. GAUGES of approach to coup / uprising / civil war, for the whole
  country AND for single regions/oblasts. Deepen the game but keep it MAXIMALLY clear; the game
  partly GUIDES the player itself; hand (almost) every element to the AI and FINISH the existing
  delegations. Gripping, dynamic, organically simulated, super moddable, reacting visibly and
  sometimes unpredictably to any event in the world.
- REQUEST: more FUNDAMENTAL game elements adjustable in defines; and it must be possible to switch
  OFF the 2D map or the globe map (defines: which projections exist).
- REQUEST (user): REWORK THE ECONOMY strongly and DEEPLY - especially construction, buildings,
  troop recruitment, resources and goods, and their effect on the economy and the population.
- REQUEST (user): integrate OTHER (programming) LANGUAGES more where they fit (mods/_templates
  already has rust/go/luau/fsharp workers); may modify or fork the Godot engine for it.
  (Asked to confirm it is programming languages, not translations.)
  CLARIFIED: languages for THE GAME ITSELF too, by fit - heavy maths, repetitive bulk work,
  graphics, networking. My stance: fix algorithms first (measured hotspots are algorithmic), then
  move what is genuinely language-bound: map graph work (paths, vision flood) to Rust/C++ via
  GDExtension; bulk per-texel map work to GPU compute shaders. Write a proposal for the user.
- TODO perf: JIT warm-up costs ~40 ms on the first market week -> ReadyToRun/tiered PGO for exports.

## 2026-10-14 night (user asleep; "thousands of lines, all of it")
- CLOUDS: less torn, softer/blurred, no hard seams/straight edges (screens: diagonal cut near Spain,
  horizontal cut near Japan). FOG OF WAR MIST: much less dense - an effect, must not hide the map.
  Toggles for clouds and for fog, ALSO on the country-select screen.
- CONTENT: many more cultures, religions, possible new countries, dynamic names and flags, movements.
- FORMATION LOGIC by CULTURE and RELIGION opens options; RESTORATION of countries that existed at
  game start and vanished; Belarus must be able to form the Soviet Union etc.
- HEGEMONS breakable: USA (critically too stable) and China can lose greatness - states secede,
  civil wars, hard economic crises; long-run organic unpredictability; young nations rise, old
  powers decline; external plots, intrigue and aggression from interested states.
- MAP OBJECTS, even 3D-ish, many; static and event-driven. TRADE ROUTES shown with moving transports
  (trucks, ships).
- Keep in mind: rework interface, audio, music, visuals; COMFORTABLE and CLEAR.
- REQUEST: separate MUSIC button and ARTWORK button somewhere in the interface; rework the
  interface capitally - its forms and layout; the interface is messy.
- REQUEST: FILL the game with content; more EFFECTS and MODIFIERS, more CONDITIONS and SYNTAX for
  modders; many more TECHNOLOGIES.
- REQUEST: optimisation to an excellent state - remove ALL freezes; FREEZE when opening the
  "Conflicts" menu; the Conflicts menu mixes too much in one (split it).
- REQUEST: localisation work; remove elements and proper names that betray AI work.
- REQUEST: POPULATION SPLIT POLITICALLY - by ideology, by laws they hate/want, by form of
  government, by expansion vs staying put, by quality of life. Many new problems, our beloved
  CHARTS, new mechanics, and especially INSTABILITY, harder separatism, civil wars, coups,
  diplomatic and economic scandals, INTERNATIONAL SCANDALS (information age), CIVIL WARS.
- REQUEST: rare WORLD WARS ("War of the Worlds") - or wars between big organizations - always with
  causes, felt by the whole world, with matching atmosphere (music, news, map).
- REMINDER (user): kin consolidation - Serbia striving by any means to consolidate former
  Yugoslavia; same for other culturally-historically close countries.
- REQUEST: movements a FORMIDABLE force once they gain momentum, recognition and resources -
  able to do much; add mechanics for it.
- REQUEST: keep improving the MODDING structure - flexibility and simplicity; maybe an in-game EDITOR.
- REQUEST: alliances and organizations barely break up or change radically - make them organic,
  dynamic (split, collapse, realign).
- REQUEST: countries with exclaves (screen: Turkey holds Ismayilli near Baku in 2072) TRY TO CONNECT
  their borders - by dialogue (deals: corridor purchase / swap with the country in between) or by
  aggression (claims, pressure, war goals aimed at the gap).
- REQUEST: the world is TOO CALM - events must be VISIBLE on the map: realpolitik, wars, collapses,
  unifications; much more happening; REBALANCE key values, add variables/mechanics. Measure with
  20-year ledgers (wars, civil wars, map changes per decade) before/after.
- REQUEST: SAVE SIZE - huge after decades; target <= ~80 MB after 100 years. Measure what grows
  (history samples, news, war ledgers, battles rounds, social memory...) with a 50-100 year bench save.
- REQUEST (user + tester): get rid of the LEFT SIDEBAR with its scroll bar (screen: COUNTRY/ECONOMY/
  FORCES/WORLD column needs scrolling). Full interface rework: a compact grouped dock without
  scrolling (icons + hover labels), categories kept.

## 2026-10-14 status (package 8, see tools/pkg8_commit.txt)
- DONE in pkg8: save size (history thinning + text layout), consolidation (CONS-01), corridors
  (CONS-03), world war + tension (WAR-01), intel networks + air recon (DIP-02/03), living
  organizations (ORG-01), contractors (MOV-03), address to the nation (UI-10), livelier escalation
  defines (world too calm). Benches --unions --world --orgs --pulse.
- IN PROGRESS: trade traffic on the map (VIS-03, GlobeTradeTraffic.cs, setting map_trade_traffic;
  hooks tools/trade_traffic_edit.py) - needs a vis screenshot check.
- NEXT (mine): province context actions (UI-09), movements formidable (MOV-02), 3D map objects
  (VIS-04), split the Conflicts screen, content streams, localisation/AI-trace report,
  programming-language proposal, restoration layer completion with ChatGPT.
- NOTE for ChatGPT's map bar (MapControlsUI): a "TRADE" toggle for map_trade_traffic would sit
  beside CLOUDS/MIST.

## 2026-10-14 user feedback (playing the real tree with trade traffic)
- REQUEST: trade lane LINES stand out too much - thinner, stylised (dashed, gently animated),
  SMOOTH curves instead of jagged polylines.
- REQUEST: ships and lorries - make them in BLENDER, decent and REALISTIC (colours, shapes,
  detail - now "cardboard"), and much SMALLER.
- BUG: lorries drive over water (USA-Mexico straight capital line over the Gulf); ships may cross
  land. Land lanes must follow land, sea lanes must stay on water - validate every lane.
- REQUEST: Long-range and Laws icons on the left outliner look DIFFERENT from the others - match
  their style, at least the colour.
- REQUEST: remove "Repeated clicks cannot buy instant trust." from the organizations page (sounds
  AI-written); keep auditing such phrases (localisation / AI-trace report).
- REMINDER: keep implementing the whole plan; write everything down.
- 2026-10-14 late: pkg9 pushed 403c652 (rebased on ChatGPT 839678e, + AssistanceCooldowns order fix).
  pkg10 testing: MovementPower (MOV-02), ProvinceContextMenuUI card (UI-09), GlobeEventPulses + map_event_pulses.
  AI-trace: 48 fixes applied to the real tree via tools/ai_trace_apply.py; commit as pkg11 (apply to snapshot).
  POL-01 is being written by another agent (Scripts/Simulation/PoliticalCohorts.cs, untracked) - do not duplicate;
  build charts/map mode on it once committed. SaveSystem.cs in the real tree lost my SaveTextLayout hook (someone
  wrote an older copy at 18:16) - committed version is fine; tell the user/other agents before anyone commits it.
  NEXT: visual check of the province card and pulses; dynamic content; interface rework (user's priority: RMB menu).

## 2026-10-14 late: packages 10 and 11 pushed (314f3f8, a27d313)
- pkg10: movement power, province card (RMB), event pulses. pkg11: 48 plain-word fixes (Bavaria Party text lengthened to pass the >=60 char test).
- Covert operations (pkg12, in progress): CovertOperations.cs, CovertOperationsUI.cs, common/covert_operations, 20_covert news, 51/67 intelligence data, covert_edit.py hooks (SimulationCore, MechanicFeatures, NewsFeed, ModLoader, CampaignFingerprint, ProvinceContextMenuUI, ClaudeStateFingerprintTests), GlobeEventPulses styles.
- NEW USER REQUEST (2026-10-14): OPTIMISATION and LOADING SPEED - campaign creation (~5 s in the bench), game start, save load, scene/editor load, per-day sim cost. Profile first (--profile, timers in the loader), then cut. Do it right after pkg12.
- NEW USER REQUEST (2026-10-14): ABOVE ALL rework the INTERFACE - as clear, detailed, comfortable and convenient as possible, and above all BEAUTIFUL, pleasant, with STYLE. Keep doing it alongside optimisation: the RMB card first (covert section, facts row), then screens one by one (diplomacy, region, the world tab), with screenshots via vis.sh.

## 2026-10-15: package 12 pushed (29deb46 map repair speedups, 4a8293a covert operations + modding guide section)
- Loading, next (pkg13): terrain tiles/relief prepared on worker threads in parallel with BuildPoliticalMap (stage 5 was 3.3 s main thread);
  SeaLanes graph prewarmed on the map worker (AttachProvinceTopology's AssignHomePorts built it on the main thread);
  BuildPoliticalMap sub-steps in parallel (topology / hydrology raster / label anchors / surface). Then Factions.Found 0.27 s, TryBuildProvinceTopology 0.75 s.
- UI rework (user's top priority): screenshot audit of every screen, then a plan; RMB card polish.

## 2026-10-15 morning
- pkg13 pushed (21564ec): terrain pictures on worker threads, parallel map worker, sea graph prewarm.
- pkg14 committed locally in snaps/pkg14 (1cb991e, tests green): topology colour-run cache + parallel terrain.png decode (hash-identical), city catalogue prewarm. NOT PUSHED: GCM "Select an account" window blocks fetch/push - ask the user.
- UI audit: tools/UI_AUDIT.md (90 findings, 17 high; the map/country/RMB group and the synthesis did not run - session limit). Next UI fixes: Economy desk buttons clip numbers ($18 for $184.8B), Decisions show no effects + brass-bar wall, Production panel overlaps the nav rail, Diplomacy opens on own country, chart axes/one-sample charts, Support panel empty middle, conflict bar colours.
- RMB card: another agent is editing ProvinceContextMenuUI.cs (scroll, facts); its facts row collapses to 1 px - leave to them / mention to the user.
- Not mine, leave: MovementPower perf (ChatGPT 19fa327), AiStatecraft ~14 ms/day (owner unknown).

## 2026-10-20: THE NEW BIG ASSIGNMENT (user, verbatim intent)
Main values of the game: clarity, pleasantness, FUN, beauty, style, vibe, dynamism, unpredictability, plausibility and organic simulation.
1. Analyse how the game plays/looks now and how the world simulation runs; correct backend or frontend as needed.
2. Finish everything left unfinished by me or other agents (docs/MASTER_DEVELOPMENT_PLAN_RU.md: many "очередь"/"частично"; 8 red suites on the live tree 2026-10-20).
3. Version mentions in the game -> 0.8.1 (GameBuildInfo.VersionLabel, project.godot config/version, export_presets file/product_version, SaveSystem.GameVersion, docs).
4. Push everything fresh to GitHub. BLOCKER: repo is now PRIVATE; GCM has only the SteppeImperialist account -> "Repository not found". Ask the user to sign in as Bayte-Realization or grant SteppeImperialist access. Publish path: snaps/pub = origin/ReimAlpha (83e3dbcb) + live tree overlay (robocopy, excluding ignored dirs; project.godot local plugin/autoload hunk must NOT be published).
5. LanguageKit (C:\GodotEngineNet 4.8-Beta1\LanguageKit) at the most visible place of the project folder and the repo: already at the live tree root; Codex branch codex/language-kit-20261020 adds it at the repo root - merge into ReimAlpha + README link.
6. TOTAL optimisation to top performance.
7. Refactoring and cleanup of the code.
8. Integrate other languages where it clearly helps performance/clarity (NOT Rust for now): F# pure rules already in; Nim tools, Odin raster, Luau mod formulas per docs/LANGUAGE_BACKENDS_RU.md.
9. Creative Claw: lots of assets - music, sounds, ambience, textures, icons, UI elements, effects, map elements; top indie grand-strategy look. Balance 1721 credits (music ~16, sfx ~12, image ~16).
10. (added mid-turn) Work on the game LAUNCHER (Tools/Launcher), long untouched.
### 2026-10-20 evening state (usage limit hit)
- Done: version 0.8.1 in the LIVE tree (GameBuildInfo, SaveSystem.GameVersion, project.godot config/version, export_presets, docs); README/README.ru top pointer to LanguageKit/.
- snaps/pub = origin/ReimAlpha + live overlay (built 0 errors). 58 suites: 50 pass, 8 fail. Red already on origin: peace-coalition (BlockReason "demand is empty"), save-seed, save-seed-mature (.bak file counted by the test). New on live: ai-diplomacy, capital-point, conflicts (Sudan/Port Sudan), map-peace, mobilisation.
- Workflow w1p93dkt8 (diagnose-red-suites, read-only) was running: results in its journal (subagents/workflows/wf_80a6679c-ccb/journal.jsonl).
- Reports running/ran in snaps/pub_reports (pulse_1825, profile_365, world, stability).
- Creative Claw: tools/assets_2026_10_20.json = ledger (13 music jobs, 7 ambience loops, 16 sfx, 28 image jobs). Remaining music jobs to check: eurasia_tense, south_america_calm, oceania_calm, world_war, after_ceasefire, menu_theme_atlas; all image jobs. Then download, integrate (music catalog with regions, MapAmbience terrain layers, news->cue table, loading screens, event pictures).
- GitHub push still blocked by credentials (private repo, GCM account SteppeImperialist).

### 2026-10-21 (gh route works; red suites fixed; assets integrated)
- GitHub: tools/gitgh.sh borrows the gh CLI login (Bayte-Realization) per command. coop-anything MCP registered (user scope; tools next session). universal-modder: useful only for the BTA crossover/showcase, plugin install is the user's own slash command.
- Red suites: all 8 green on pub (tools/red_fix_edit.py, also applied to the live tree): capitals rename regression, AI rival overture as a mission, outdated tests (save .bak, war emergency, treaty shape gate, map-peace limited war).
- Music: 17 new tracks (game/music/15_world_regions.json, -19 LUFS Ogg), sub-region + country-tag matching (most specific wins; Iran by tag), western_wind/statecraft_papers trimmed -7 dB, listening library 16_world_regions_library.json. south_asia_calm v2 job dd0de2d5-92c0-4319-9995-9e2460473003 pending.
- Map ambience: terrain beds under the tactical camera (MapAmbience.Survey), 7 seamless loops at -43 LUFS.
- News stings: common/news/40_stings.json + sound/05_news.json, StrategicAudioUI.RefreshNewsAudio; interface sounds deal_sent/deal_concluded/covert_launched/map_focus, diplomacy_page now has a file.
- Loading screen: rotating gfx/loading/loading_*.jpg behind a dark card (LoadingScreen.cs). Event pictures: gfx/events/event_*.jpg, common/event_pictures table, "picture" field, EventUI band (modal.event 630 px tall).
- project.godot config/version now set through the editor (godot-ai settings_set); direct edits get overwritten by the running editor.
- Publish: snaps/pub reset to c1259641 (Codex language kit, PR #7) + tools/overlay_pub.sh; project.godot = origin + version line. Suites running (b21jjung3) then commit + push to ReimAlpha.
- NEXT: launcher rework (Tools/Launcher), UI audit fixes (tools/UI_AUDIT.md), optimisation (profile), sim analysis (pulse/world benches), other-language integration.

### 2026-10-21 (2): pushed 08552db9 to ReimAlpha (0.8.1 + assets + launcher + LanguageKit merge, 60/60 suites)
- After the push, in the live tree (not yet published):
  - Diplomacy list fix (DiplomacyScreenUI: wrapped labels reserved lines at 0 px width -> first row ~400 px; flag slot 40 px).
  - Dependency plausibility (SubjectCore/SubjectInteractions/10_associations, test FormableTests --dependencies-only): leases not agitable,
    dependent territories and associated states leave peacefully, agitation needs Defines.SubjectAgitationMinPopulation (100k), liberty decays in grace.
    5-year pulse: no more USA-Guantanamo / RUS-Baikonur / GBR-Gibraltar / FRA-New Caledonia wars ("USA Civil War with 10", "UK Civil War with 18" gone).
  - Intel vision: CampaignIntel.ProvincesHeldBy controller index; IntelNetworks.RevealInto walks only targets' provinces. intel.visibility 1,989 -> 627 ms / 120 d, fingerprint 18FE5A7945F9D2F5 unchanged.
  - 8 more event pictures (battlefield, naval, air_raid, occupation, ceasefire, election, coup, laboratory) + remap; south_asia_calm track.
- Running: workflow w7rcwhm6e (bit-identical speedups for war:advances, ai:diplomacy/buy_peace, movements, weekly systems, hourly military) -> apply, measure fingerprint.
- Findings to follow up:
  - Greece -> Cyprus "civil" war ~day 833: culture_union then breakaway then reconquest (check PeacefulFormation/culture union between EU members).
  - World tension saturates at 100 for years (world report); world war 15 years in (China vs Myanmar, 21 belligerents).
  - Load: "Preparing the world" 9.2 s (countries+regions 3.7 s vs warm 1.1 s in --world-build -> JIT); try PublishReadyToRun in Release export (verify with Tools/build.ps1 + smoke test).
  - Per-day sim 127-157 ms vs 41-45 on 2026-09-30.

### 2026-10-21 (3): pushed fb6357db (dependencies leave by vote, vision index, harbour flood, diplomacy list, trade price, 8 event pictures, south_asia_calm; goldens re-recorded for FormableProclamationAi; 61/61 suites)
- Next commit (testing): Corridors pores keyed on ownership (753->141 ms, 426->50 MB/yr), NationalModifiers concrete dictionary walk (261->215 MB). Fingerprint 3664E4526B219EB2 unchanged.
- Day cost: 157 ms (morning) -> ~113 ms. Remaining: warfare 7 s/yr (war:advances 3.6, war:staff 1.8), movements 6.5 s (mov:people cache resets on every MapRevision - needs proof that no group move rides a MapRevision bump alone, WarDamage refugees suspected), statecraft 4.5, ai:diplomacy 3.6, intel.visibility 2.5 (armies loop twice per rebuild).
- Plausibility left: USA-Cuba war -> "Third World War" with 52 belligerents (~day 2266); Greece-Cyprus culture_union -> breakaway -> reconquest; world tension pinned at 100.
- Launcher rework shipped (key art, version, counts, Play, states, --snapshot). Live root exe replaced (old one in claude-wt/backup).
- Language integration: other agents measured Odin/Rust kernels slower than C# (docs/LANGUAGE_KERNEL_EVALUATION_2026_10_16_RU.md); speed comes from C#.

## 2026-10-21 evening: USER (verbatim intent) - continue all of it
"Главное: оптимизация и высокая моддинговость; понятность, приятность, стиль, вайб, удобство, красота и подробность интерфейса;
игра максимально красивая, приятная, динамичная, органичная, правдоподобная, логичная, нелинейная, весёлая, интересная, удобная -
самая ГРАНД из гранд-стратегических инди, вершина и в игре, и со стороны."
Order: (1) moddability of today's hooks (map ambience table to data, MODDING_GUIDE sections: music regions, stings, event pictures,
loading art, subject flags); (2) plausibility: USA-Cuba -> "Third World War" 52 belligerents, tension gauge pinned at 100; (3) warfare/movement speed;
(4) UI polish from fresh screenshots (Production empty half, Ledger one-sample charts, event window space, Research idle hint).
- USER addition (same evening): not only the Cuba escalation - "in different runs the world must be completely different, like history;
  the game must be NONLINEAR and UNPREDICTABLE". -> check how a new campaign picks its seed; measure divergence across seeds (pulse x N seeds:
  wars, regime changes, secessions, formables, world wars); add sources of divergence where runs converge.
- USER (same evening): console commands `snowgrave` and `weirdroute` (Deltarune references), both permanent in the save:
  snowgrave - the world under a thick crust of 3D ice, every action stops, time runs but nothing changes, music stops for good;
  weirdroute - music stops, every army (existing and new) marches through any country to drown in the nearest water;
  a world with no water at all (a mod) -> impenetrable white noise / static over the screen with its sound, the game is over.
- USER: a setting to turn off the AI loading screens, with a joking name ("Отключить ИИ-слоповые загрузочные экраны").
- Seeds: 3-seed 900-day ledgers showed the 2024 wars resolve identically on every seed (RUS takes the same 126 provinces by day 250,
  Yemen day 214...). Combat had no dice -> Defines.CombatFortune (daily per-side triangular factor, seeded).
- USER: when the loading illustrations are switched off, the loading screen must be "that old first beautiful one" -> plain mode
  (no picture, no shade, no dark card): grid, gold frame, seal, dossier. Setting `loading_without_ai_art`.
- USER: typing snowgrave/weirdroute prints ONE line only: "PROCEED" (Deltarune).
- Fortune results (3 seeds, 900 d): new wars now diverge (NGA->NER 3 vs 148 provinces; IRN targets differ), but RUS->UKR still
  ends day 250 with the same 126 provinces (Chernihiv, Sumy, Volyn, Rivne) on every seed: the outcome is the war goal + total
  Ukrainian collapse (UKR 0 formations, reserve 700k -> 0), not battle luck. TODO: plausibility of the opening war (Ukraine collapses
  completely in every run).
- USER (with screenshots, 2040 observer run): fix ugly, illogical, implausible breakaways: "Military Command of Dagestan" capital Rostov
  holding a band Kharkiv-Voronezh-Rostov-Kalmykia-Dagestan; "Military Command of Belarusian Democratic Forces" a snake Lviv->Moscow;
  "Republic of Siberia" R15 ragged; Kazakhstan holding Orenburg/Samara. Breakaways need compact contiguous homelands, names and
  capitals that match their land. Workflow wf_efe52012-351 (secession-shapes-diagnosis) investigating.
- USER (2026-10-21 evening, "главное"): everything in the world must change over time - new powers rise, new GREAT powers appear,
  former great powers break apart and lose influence, agendas change: real CYCLICITY of history, as in ours. Workflow
  (history-cycles) measuring great-power turnover over 25 years on several seeds and designing rise/decline mechanisms.
- DONE 2026-10-22, pushed 94922bf8: snowgrave/weirdroute (PROCEED, 3D ice, frost, static, silence, sounds, WorldCurseTests),
  plain loading screen setting, CombatFortune, fresh world seed, map ambience data, breakaway pass 1 (accretion only from the
  core and only on the people's ground, homeland = core, revival needs real overlap, armed zones connected, capital in core,
  monthly name refit + form follows regime, movement label no longer a name base). 63/63 suites, formable 5234 PASS.
  Left OUT of the publish (other agent's unfinished work, still in the live tree): CapitalCore evacuation morale shock and
  Tools/FormableTests/GeopoliticalDynamicsTests.cs (it contradicts CapitalLossTests "evacuation preserves stability").
- NEXT breakaways pass 2 (probe cycles_after_111/222, 13 y): "Belarus" from RUS with 28 regions/62.8M (literal base from the
  movement state name is not measured by NameHold - measure literal by the movement's peoples); "Military Command of Russia"
  seceded from BRA; "Republic of Tajik Republic"; duplicate names (3x Eritrea, 4x Ukraine in the baseline); "Bhutan" with 160M.
- NEXT cyclicity (user's "главное"): baseline 25 y shows churn but implausible (France absorbs DEU/ITA/CHE to 12.5% world GDP,
  India -> Military Command of India 820M, USA absolute monarchy 11 y) and a frozen economy (OutputScale fixed from 2024,
  productivity pulled to a target, no compounding, ~1.5 buildings/country-year). Another agent started GeopoliticalCycle.cs
  (global tension offset) - coordinate, do not duplicate. Plan: TFP stock with convergence + overstretch/debt decay,
  regime-change plausibility for large stable democracies, then measure with the probe over 25-50 y on 4+ seeds.
- DONE 2026-10-22, pushed 6b1a7038 + b9b5e988 (cyclicity, stage 1): SecularCycle (OutputScale drifts weekly: catch-up,
  leader drag, per-country long wave 35-60 y, seeded luck, war/overstretch drags; "Rise and decline" row in Economy);
  conquest blends OutputScale; culture unions = same main people + small/game-born joiner; shards = game-born only;
  restoration needs 40 % of people on old ground; strongman crown only from junta; kin-state peoples do not found
  duplicates; crushed regions quiet (crushed_pressure, any road back); exhaustion lets regions go; union breakup spares
  capital/crushed/kin ground; election pendulum (ideology changed 105-109 of ~170 vs 14-26); notoriety is only the
  defender's casus belli (India vs Bhutan); region names need 20 % of people.
- NEXT cyclicity: verify v4 probes (Russia stability trap, India collapse); remaining: ~95-105 countries gone in 25 y
  (check which: microstates absorbed? Austria absorbed as DEU subject in 2 seeds), USA/China stay #1/#2 in every seed
  (great-power succession needs stronger forces: debt supercycle, demographic ageing -> output, technology eras);
  public_debt 2024 data; HumanCapital from GDP per head; regime churn in juntas (RUS flips 4-5 times).
- DONE 2026-10-22 pushed a38790a0: TroublesDays (radicalism fades faster the longer the troubles), submission reach
  (no protectors across the world), Rise-and-decline icon. 63/63, formable 5235.
- NEXT (root of the Russian trap, found in v5): the opening RUS->UKR war ends in TOTAL Ukrainian collapse in every seed
  (UKR 54 formations -> 0, reserve 700k -> 0, losses 1.39M vs RUS 0.49M by day 250). Ukraine is then gone and every
  Ukrainian oblast rises alone (Galicia, Podolia, Sloboda, Donbas, Novorossiya, Tavrida) -> Russia fights 25-38 wars,
  stays at 0.10 for 25 years. Fix the war balance first (attacker/defender loss ratio, Ukrainian manpower and
  mobilisation, foreign aid), then re-measure the trap. India also collapses in most seeds (aggressive wars early).
- USER (2026-10-22): keep the world dangerous from the early game (crises, collapses even of GREAT powers, alliance
  breakups, rising powers, major wars, key treaties, agenda changes, diplomatic realignments) - never boring, but FAIR.
- USER: restoration ground share 30 % not 40 %; peoples may rise and found NEW unique nations away from their homeland
  under conditions (diaspora far from its kin state, majority in its land) - migrations and ethnogenesis as in history.
- USER: then demography needs work: MIGRATION mechanics, ASSIMILATION, and displacement of native cultures in some cases.
- Opening war: --front 365 fails (Ukraine 126 -> 0 provinces, casualties 0.49M RUS / 1.38M UKR; without fortune
  0.28M / 1.90M) - the bench expects a stalemate; not a fortune regression.
- USER (2026-10-22): STANDARDISED structure - no narrow data files for one or two countries "just because" in game
  folders. Done: common/regional_demography (Belarus 2019 census only) -> Belarus regions now use the standard
  "cultures" shares in history/states/2024/BLR.json; the census mechanism stays a mod feature, shipped as
  mods/_templates/regional_census (RegionalDemographyTests read the template). common/lands merged into
  common/naming (10_macro_regions.json); LandAtlas still reads "lands" for old mods. Keep auditing for others.
- Front bisect: 45cb1d5e and earlier hold the stalemate (ea4510cf: 98k/96k casualties); b47a0a37 (AI decision
  currencies) flips it to Ukraine +38; 08552db9 onwards Russia wins. The front is chaotic -> judge on 8+ seeds.
  Fixes in progress: replacement reserve (ReplacementReserveShare), operational pause (AiOffensiveReady/Exhausted
  Organisation), defenders advance only on own ground until winning (AiDefenderInvadesFromScore).
- FOUND 2026-10-22 (the 0.8.1 front regression): two systems, neither in the combat files the bisect had copied
  (SimulationCore ticks them, so the "good" bisect build never ran them):
  1. StrategicStockpiles: ammunition came only from ArmsIndustry buildings, which the history files give to RUS,
     UKR, KOR and PRK alone - the USA refilled at 0.3 a day, Russia at 6.6; backers' aid never reached a depot;
     an empty depot meant 0.60 fire AND -3.5 % organisation a day in battle. Ukraine ran dry about day 120 and broke.
     Fixed: every economy has a defence industry (StockpileIndustryPerRootGdp 0.06 plants per sqrt GDP), backers ship
     from their own depots (ProxyShipmentShare 0.25 per level above the first, StrategicStockpiles.Ship), an empty
     depot is a ration (StockpileCoverage, saved + fingerprinted; factor 0.6 + 0.4 x coverage), penalties are
     defines and the ammunition organisation drain defaults to 0 (double count). SimBench --front prints depots.
  2. WarEmergency treated any unheld post as a war for existence -> Russia (attacker, long front) raised a division
     a day on credit and rushed every empty province (135-165 divisions vs 99-125). Now open posts count only for
     the defender; lost capital / 5 % occupied for anyone.
  Also the replacement reserve is ReplacementReserveDays = 60 days of the country's own replacement capacity (the
  share of the mobilisation ceiling left Ukraine unable to raise anything). Result over 8 seeds (11..88, 240 days):
  Ukraine 126 -> 126,88,91,93,97,129,115,64 (mean 100), was 33-76; stockpiles-off baseline mean 103. Still a mild
  Russian drift; the front stays chaotic per seed. NEXT: re-run the cycle probe (Russia trap, India), oil imports
  for depots (Ukraine's oil intake 0.63/day vs 1-2.5 burn: market imports never reach the depot).
- DONE 2026-10-22 pushed 9fc257a9 (English LanguageKit GUIDE.md + October 13 checkpoint, Ziva note removed - USER
  asked: no Ziva mention on the front pages) and 19714973 (fair opening war, see FOUND above; 63/63, formable 5241).
- 2026-10-22 DEMOGRAPHY 1 (uncommitted, measuring): StockpileFuelFromMarket (army fuel bought on the market reaches
  the depot; front 8 seeds mean 98, Ukraine's oil depot full). Assimilation BUG: DemographyAssimilationAnnual (0.6 %)
  was applied every weekly review = ~27 %/yr, every minority gone in a decade. Rewritten (DemographicCycle.Assimilate):
  scaled to the review; target = the country's own culture where its people are >= 10 % of the region, else the
  local majority; same family x2, other faith x0.4, large share slower, literacy faster, living kin state x0.6,
  modifier key "assimilation" (ideologies carry it: fascism +0.8 ... agrarianism -0.3); people keep trade/class/faith.
  InternalMigration.cs: people move between their own country's regions by Pull (living, work, order, peace,
  capital), nearer/larger destinations, war refugees to the safe rear, carrying culture. SimBench --peoples N.
  NEXT: settlers into conquered/annexed land (displacement), deportations as a decision, refugee camps abroad.
- 2026-10-22 DEMOGRAPHY 1 continued: found a SECOND assimilation (EconomyCore.MinorityPressure, AssimilationPerWeek
  0.0009 ~ 5 %/yr into the first group of the state's people; removed with its define, only its compaction stays) and
  migrants relabelled as the region's largest minority (Ugandan refugees became Ganda) - BrainDrain now moves the people
  themselves (InternalMigration.Move). Nationalities policy is a LAW (common/laws/30_nationalities.json: integration
  default / minority rights / assimilation / settlement; history/countries/2024/50_laws.json; AI: fascism settles,
  liberal families drop settlement/assimilation). 5 years: world minority share 25.2 % flat; Xinjiang Han 42->45,
  Tibet 12->16, Latgale/Estonia flat, Uganda Ganda 18->20.
- DATA (found 2026-10-22): many regions carry their COUNTRY's culture split instead of their own - Harghita
  (ro_hr) shows Romanian 90 % / Hungarian 6 % (real: ~85 % Hungarian); every Ugandan and Tanzanian region
  is identical. Regional culture shares need a standard pass (census by region) - same rule as BLR.json.
- FOUND 2026-10-22 (why Ukraine is GONE by day ~250 in every long run even after the stockpile fix): the 240-day
  bench hid it. WarCoalition.SpreadRetreatProvince (0.8.1) let a beaten formation retreat only into a neighbour with
  NO battle and no enemy; on a fighting front there is none, so it surrendered whole (CombatTerritory: "no line of
  retreat"). Seed 111: 1.6M of Ukraine's 2.0M losses were PRISONERS by day 240, with 1-2 divisions ever cut off.
  Fix: friendly ground under attack is allowed (score -40), only enemy-held neighbours are refused. Judge the front
  at 365+ days from now on (the band was measured over two years). Trace now prints org/exp/dug/cut off/killed/taken.
- DONE 2026-10-22 pushed 0d1897f0: demography (one assimilation, migrants keep culture, internal migration, nationalities
  law, --peoples) + war logic fixes (beaten formations fall back onto contested own ground - voluntary withdrawals stay
  strict; PocketBreakout; defender presses only while losses < 2.5x; army fuel from market). 63/63, formable 5241.
- OPEN: the opening war over 365 days. Ukraine gone on 5/8 seeds (was 8/8). Pre-0.8.1 (45cb1d5e, c1259641) and
  bisA (c1259641 + all new combat files) hold it for a year (108-186), so the cascade comes from systems ticked by
  the new SimulationCore or non-combat 0.8.1 files, not from the combat files or the combat defines. Probing:
  v1 = no CultureUnion/HistoricalTimeline/Rulers/NationNaming/OccupationResistance/Defections ticks; v2 = no
  Defections + stockpiles/emergency off (snaps/probe_v1, probe_v2).
- FOUND 2026-10-22: the 365-day cascade was WarEmergency.EmergencyMilitia - any open post was filled with a local
  militia (nearest, anywhere), the post counted as held, no division was sent, the line broke there and became a
  pocket. Militia now only as the last resort (reserve below one division) and only on its own region's posts.
  8 seeds x 365 days: Ukraine 136,109,129,71,57,133,236,200 (was 0 on 8/8, then 5/8). Losses balanced again.
- USER (2026-10-22): variety of outcomes is wanted - in some runs Ukraine should vanish or lose a great deal, in
  others hold or win; plausible and different each game. So judge the front by the DISTRIBUTION over seeds (some
  collapses, some stalemates, some reversals), not by every seed landing in the stalemate band. Check 730 days next.
- DONE 2026-10-22 pushed cc91b537 (militia last resort). 730 days x 8 seeds: Ukraine 173,173,155,0,173,134,78,0 -
  two collapses, two long wars near the line, one swing 235->78, three "gone then back at 173" (war ended, score
  +80 for Russia yet Ukraine +47: CHECK whether the 0 low is a bench artefact after the peace or a real rebirth).

## 2026-10-27: USER's new assignment (approved plan, "полное благословение")
Order: (1) finish what is begun, (2) UI that feels SERIOUS and STYLISH (volume, depth, standard geometry, every tab
reachable, consistent buttons; competitor screenshot: left icon rail, KPI top bar with flag+leader, side panel
with sub-nav + chart cards, right "Agenda" column, photoreal globe with night lights), (3) MAP visuals at
Paradox level (effects, objects, quality), (4) languages: give each language what it is good at - F# pure rules
(assimilation/migration formulas with differential tests), Kotlin content validator (catches uniform regional
mixes, filler cultures, broken references; NOT the BTA mod), Odin/Nim dense raster/overlay prep, C# day
parallelism (the only honest 4-6x); NO Rust for now. (5) TOTAL BUGFIX. User encourages own initiative/imagination.
- Regional peoples: India + batches 1-3 (ROU SVK SRB LVA EST KAZ GEO TUR DZA MAR RUS) are already in origin via
  another agent's checkpoint commit. NEXT batch: IRN PAK IDN PHL THA MMR USA ESP(rest) IRQ(rest) CHN-check.
- vis.sh refresh (full mirror + build + headless import) froze the user's machine on 2026-10-27: run it only
  with the editor closed and nothing else heavy; prefer a lighter shot path.
- SCREENSHOTS NOW WORK without a mirror: Scripts/Boot/boot_guard.gd has a dev hook - env DOF_AUTOPILOT=<path to .gd>
  loads it as a node (w48/vis_autopilot.gd; SHOT_DIR, SHOT_COUNTRY=DEU, SHOT_VIEWS, SHOT_NAVS...). Run
  Godot_v4.8-beta1_mono_win64_console.exe --path <live tree> --resolution 1920x1080 --windowed, minimized; shots in
  claude-wt/out/ui_1027b. (Fennara game-capture.ps1 never reached readiness; desktop CopyFromScreen grabs the
  user's screen - never use it.)
- UI/MAP DIAGNOSIS 2026-10-27 (v_europe.png, v_world.png):
  MAP: political fill one flat colour; rivers drawn as stair-stepped raster blocks; city footprints = grey squares;
  thin borders, no relief/depth at close zoom; at mid zoom fog/clouds wash everything to grey mush.
  HUD: top bar = a row of 12 equal text KPIs, no grouping/hierarchy, placeholder leader silhouette, truncated head
  of state; top-centre alert icons glossy cartoon roundels (off the strict art direction); right "Situation desk"
  huge and empty ("Nothing on this desk this week"); map-mode bar wraps to two rows, every button the same brass
  outline weight; panels flat - no elevation, shadow, highlight edge or material.
  PLAN "UI 2.0": layered surfaces (3 elevations: soft drop shadow, 1px top highlight, faint vertical gradient,
  inner hairline), 8px grid, one header-bar component for every window, icon rail with active indicator + count
  badges, KPI groups (economy | society | military | politics) with trend arrows and sparklines in tooltips,
  agenda column like the competitor's (monthly report / getting worse / deadlines / under watch) that sizes to
  content and collapses, map modes as one segmented control, flat-styled alert chips. MAP: smooth vector rivers,
  stronger borders (country 2px + glow, region hairline), terrain relief shading, city lights at night side,
  less fog at mid zoom.
- DONE (live tree, UNCOMMITTED, untested 2026-10-27): SituationDeskUI sizes to content (empty week = header + one
  line); hud.map_controls widened to 840 px in BOTH 10_campaign_shell.json and 35_resource_atlas.json (the later
  file won) - not yet re-shot (game runs hung at D3D12 init while the user's editor restarted at 23:04);
  boot_guard.gd DOF_AUTOPILOT hook.
- DONE: Tools/ContentLint (KOTLIN, first language task): JSON-with-comments reader, checks unknown cultures,
  shares != 1, national mix copied to every region, the same mix pasted across countries, primary peoples < 50 %,
  unknown modifier keys. Run: JAVA_HOME=jdk-21, gradle 9.1 (~/.gradle/wrapper/dists/gradle-9.1.0-bin/.../bin/
  gradle.bat) -q run --args="<game folder>". First run: 0 errors, 150 warnings (114 copied mixes, 48 states whose
  own peoples < 50 %). Applied tools/primary_peoples_edit.py to 26 multi-ethnic states (5 % floor, kin-state
  peoples stay minorities, Gulf/microstates skipped). Lint also exposed foreign filler peoples inside national
  mixes (Chagga/Kamba in Uganda, Acholi in Ethiopia, Tiv/Ijaw in Cameroon) - next data pass.
- NEXT: run FormableTests + suites on these, publish; re-shoot UI; city footprints (grey boxes) -> real look;
  panel depth (UI 2.0); top bar grouping; alert chips restyle; F# rules port; C# day parallelism; bugfix sweep.
- COORDINATION 2026-10-27: other agents got the SAME prompts and are mid-flight (uncommitted) in UI/visual:
  cities (volumetric quarters), desk embedded in the outliner header, atmosphere, ShellCoherenceTests (HUD regions
  must not overlap), language allocation doc. I REVERTED my SituationDeskUI fit and map_controls widening to stay
  out of their files. My lane: data quality (ContentLint-driven), languages (Kotlin lint done; F#/C# parallel day
  only after reading docs/LANGUAGE_ALLOCATION_2026_10_27_RU.md), simulation, bugfix via benches.
- Publishing now: boot_guard DOF_AUTOPILOT hook + 26 primary-peoples + Tools/ContentLint.
- 2026-10-27 data: PAK Punjab filler removed (punjabi 29->48 %); THA Isan Lao/Khmer/Malay/Karen/Shan placed (lao 3->21 %);
  PHL by mother tongue (+hiligaynon, waray, bikol cultures; filler malay 13->0.6 %; javanese/melanesian filler
  still ~1.5 % each). Pending publish with boot hook + 26 primaries + ContentLint after the 63 suites.

## 2026-10-28 progress
- Published: cdef3b08 (26 primaries, PAK, ContentLint, autopilot hook), 3883267d (THA/PHL), 061911d7 (two stale tests fixed; suites 72/72).
- Pending publish (pub_bin16 checks running): SubmissionNearerProtectors (Jersey->Estonia fix) + test; peoples batches 6 (UKR BIH LTU FIN BGR IRQ SYR LKA KGZ TJK ESP-Valencia, new culture sri_lankan_moor), 7 (MEX GTM BOL PER ECU), 8 (USA by state) - applied in live tree, not yet in pub.
- BUG hunting: front 730d seeds 11/22/55: Ukraine 0 low then 173 end (=126 start + 47 Russian-held) - suspect Restoration.Return/TakeZone bringing back the whole homeland incl. Crimea/Donbas held by Russia. Traced run: pub_reports/fr730_22_trace.txt (DOF_TRACE_FRONT now prints UKR chronicle entries and holding jumps).
- User 2026-10-28: own initiative always welcome; aim past Paradox; never stagnate (memory bdof-initiative).
- Next data: IDN regional, CHN check, NGA/COD/ETH regional, CAN, AUS Aboriginal NT, NZL Maori; lint check for far-away filler peoples.
- 2026-10-28 ECONOMY (pending publish, pub_bin19): (1) GdpPriceShare 0.35 - GDP valued at base price + 35% of price deviation (was nominal: +7.9% world jump in year 1). (2) ConstructionCost.cs - building cost x country price level (GDP per real output vs world), project limits x economy size (GrossOutput/12, cap 12); AI starts round(size/2) projects a month; UI shows real cost (EconomyWorkbenchUI, RegionBuildListUI, StateAffairsUI). (3) poor countries: 1 in 3 projects to university/agriculture, the rest via AiBuilder (was universities forever under literacy .72). (4) Movements.Place after LocateCapitals (save-seed-mature failed: landless HQ stale vs reload). SimBench --growth <days> [every], DOF_GROWTH_TAGS. Results 10y seed 7: before world +2.5%/yr with Italy x1.7 real, China +5% real; after (1)+(2) world +3.0%, China real +26%, Italy conquest-driven, India/Brazil/Nigeria still flat -> (3).
- Ukraine 730d: survives (seed 22) or absorbed whole by decisive settlement (seeds 11, 55). "0 then 173" no longer occurs.
- 2026-10-28 PUBLISHED: a494a526 (real GDP GdpPriceShare 0.35, ConstructionCost price level + size limits, UI true cost, poor-country 1-in-3 needs, Movements.Place after LocateCapitals), 6db8c131 (State Affairs back on the rail + NavigationCaptionFit; growth bench tallies), 8fe2eedb (construction sites work in parallel: WorkingSites = Size x k; State Affairs shows open vs worked), b07046e1 (CapitalDepreciation 2%/yr on goods-making buildings; k 0.7; AiBuildTreasuryFloor 0.01). Seed 7 10y: world +3.4%/yr, India x1.59 real, China x1.55, USA +31%.
- TODO economy: labour constraint never binds (850k jobs per civilian industry vs workforce) - revisit; Indonesia flat; ConstructionCost.cs.uid / NavigationCaptionFit.cs.uid / CapitalDepreciation.cs.uid to commit once the editor generates them.

## 2026-10-28 USER VISION: living, cyclical history (see memory bdof-living-history)
Countries break up, organisations collapse, deep crises, stabs in the back, second fronts, new powers rise, great powers fade, COMEBACKS (Rojava, Syrian opposition, Transnistria-USSR, Taiwan to mainland, Serbia, Ireland, Mexico, Colombia, Francoist Spain, German hegemony, Afghan jihad, Russia's old borders, Megali Idea, Palestine), unification manoeuvres (China-Taiwan, Koreas). Plan: (1) baseline 25y cycle probe on current code, 2 seeds; (2) survey existing mechanics (formables, Reintegration, AnnexationAmbition, exile restoration, SecularCycle, alliances/organisations collapse); (3) fill gaps with conditional mechanics + data ambitions; (4) re-measure.
- 2026-10-28 (user, night): REMINDER - optimisation of the game and moving code to better-suited languages are VERY important (no Rust for now; Kotlin for tools OK; F#/C# per docs/LANGUAGE_ALLOCATION_2026_10_27_RU.md). Also: fronts static -> AI + combat numbers; game looks "soapy" -> PerformanceGovernor fix (GPU-time aware, floor, fast 18 ms); divided-region highlight -> live membership texture; Israel/Palestine-style divergence between playthroughs; big flags+names pack.
- 2026-10-28 night, pending (pub working tree): 96_irredenta.json; live-region highlight (selected_region_members); PerformanceGovernor GPU-aware + floor + fast 18ms; CombatBattleFortune/Decisive; AiOffensivePulseStrength; SimBench --mobility; regime flags pack 1168 (Tools/regime_flags.py) + 296_regime_names (tools/regime_names.py). Findings: cycle probes 111/222 - USA runs away to 42-43% of world GDP after year ~12 (investigating growth_111_long.txt); fronts: planning ~0, 70% of divisions marching, battles ~a month long, attacker wins ~35%; combat variant A (counterattack 0.30/breakthrough 0.60) did not help; testing BaseOrganisationDamage 0.065.
- 2026-10-28 USER: add DRONES and TANKS as new goods (and more military goods/resources on my taste); icons via Creative Claw; work on the economy: buildings, interacting with the economy, regional economies, upgrades ("прокачка"), economic dynamism.
- 2026-10-28 USER: DIPLOMATIC OVERHAUL too - many more diplomatic actions, a "bouquet" from all Paradox games (EU4/HOI4/Vic3/Stellaris/CK3: guarantees, warnings, embargo, subsidies, claims fabrication, royal marriage analogue, support rebels, transfer occupation, demand concessions, joint wars, non-aggression, defensive pacts, influence/sphere, trade deals, military access, coups...), usable in different ways with satisfying results. Check what exists first (DiplomaticActs, MovementDealings, Deals, Ultimatum, alliance programs, organisations).

## 2026-10-28 user request: successful guerrilla war
- Insurgencies must be able to survive and beat hegemons (Hussites, Taliban): asymmetric tactics,
  terrain/population support, outside backers, resurgence after apparent suppression.
- But variance: some revolts achieve nothing, some states on paper weak surprise everyone,
  some states collapse. Measure distribution across seeds (cycle probe / pulse).

## 2026-10-28 user request: large bug-fix pass
- Analyse the game myself for bugs (benches, integrity, soak, UI).
- Reported: conquering the whole rebel country gives all land to the conqueror but the war goes on,
  and peace is finally signed with a country with no territory. A side with no ground must end the war
  (victory), the rebel then disappears or goes underground (movement that can rise again).

## 2026-10-28 user rules
- A successful movement takes (mostly) its whole area of interest, not one-region Silesias/Scotlands
  (sometimes a single region is fine).
- FULL PARITY: AI has no advantages over the player and the player none over the AI.
  Known player advantages to remove/review: Defines.CivilWarStrikesPlayers=false,
  MovementExilePlayer bonus, anything IsHuman-gated in simulation (audit `IsHuman` in Scripts/Simulation).

## 2026-10-28 movements vs states (user, screenshot: Silesia holds 1 region since day 3040 but is still a movement)
- A movement that holds land must become a state (graduate); prefer whole area of interest at once.
- Countries should themselves have movement-like capabilities (sponsor/organise abroad, strikes,
  parallel administration, irredentist campaigns) -> real manoeuvres; parity with movements.
## Bugs found 2026-10-28 (war ledger seed 7, 10y)
- Wars flagged CivilConflict between distinct countries: USA->CHN (allies USG,UMI,PRI..), GRC->CYP, QAT->BHR,
  GHA->LBR/TGO, TUR->JOR, SGP->TLS, ETH->DJI, RUS->UKR... Investigate (tag reuse? joins into civil war?).
- Nigerian rebels HAU/HUS/H01/H02 all named "Republic of Niger" (name collision with real Niger).

## Economy findings 2026-10-28 (22-year growth run, sold-output GDP, seed 7)
- US runaway gone: USA 25T -> 33.7T in 22y (price level 3.9 though: nominal inflation in US only?).
- India stagnates 4.3T -> 4.0T in 22 years (should be the fastest grower); China flat 17-18T, CHN stability 0.13-0.33.
- Most goods in big economies sit at the price floor (x0.55): chronic oversupply -> fix production/demand balance.
- BEL 93k/head, AUS 96k/head look too high.

## Design sketch: countries with movement-like manoeuvres (user 2026-10-28)
Existing country->movement acts: recognise, fund, list, crack down, expel, host (MovementDealings).
Missing, to add as country actions (player and AI alike, parity):
- Kin agitation: raise support for union with us among our people in a foreign region (cost, tension, grievance).
- Proxy movement: found a movement out of our kin in a neighbour's region (DPR/LPR 2014), funded and armed by us.
- Deniable volunteers: militia formations sent to back a rising without declaring war (escalation risk).
- Referendum in held ground: legitimise annexation of occupied/claimed land (Crimea 2014); recognition fights.
- Parallel administration / passports in claimed regions (Abkhazia, Transnistria).
Done 2026-10-28: graduation drops movement identity; civic campaigns confined to homeland; Silesian/Hawaiian peoples data.

## 2026-10-28 user: desertion and recruiting enemy soldiers
- Desertion (low morale/war support/unpaid army/losing war) bleeds formations; deserters go home or to the enemy.
- Recruiting enemy soldiers: defectors, POW recruitment, ethnic kin in enemy army, amnesty offers (modern wars).
- Existing: Scripts/Simulation/Defections.cs (formations change sides when war support collapses) - extend.
## 2026-10-28 user: combat is THE key: unpredictable wars, interesting to fight and to WATCH, battles included
- Generals: ArmyGroup has Commander/StaffSkill unused in combat -> named generals with traits, fame, death/capture.
- Named battles in the chronicle ("Battle of X"), decisive moments visible.

## 2026-10-28 user requests (momentum)
- Many more full-scale civil wars (not rare); also more coups, splits, breakups, referendums.
- Moddability: as much as possible simply moddable.
- Defines that switch off mechanics AND graphics: trade routes, 3D models (cities, trucks, ships)...
- 3D models and animations easy to mod, in visible places (like Paradox gfx/models) - for medieval/
  early modern/antiquity/victorian scenario modders.
- Version label everywhere: EntertAlpha 0.3.0.3.

## UI/graphics audit via MCP (2026-10-28)
- BUG: flat map shows a large soft white glow over some seas (Sea of Japan at the Korea view;
  mid-Pacific with pale vertical bands when panned east). World-space, animated slowly. NOT clouds
  (CloudDeck invisible), not selection, not bathymetry data (Sea of Japan grey there). Next suspects:
  ocean_shelf_color/slope mapping in GlobePresentation water shader, AirGlow/AtmosphereRim in flat mode,
  space background bleeding. Ask the user if they see it.
- Scenario screen: done picture; could add "countries to watch" quick picks like Paradox bookmarks.
- Main menu: done illustrations.

## Diplomacy bouquet - design (2026-10-28)
Existing: trade agreement, NAP, alliance, guarantee, military access, corridors, ultimatums, deals,
embargo, subjects, unions, movement dealings, covert ops, foreign assistance, organisations, proxy wars,
promises, outreach. Add (player and AI alike, each with a visible, satisfying result + chronicle line):
1. Insult / expel diplomats: opinion and tension down, home war support up (nationalists cheer).
2. State visit: big opinion gain, costs political credit; ruler meets ruler, news.
3. Joint military exercise with a partner: deterrence against a named third country, its tension rises.
4. Mediate a peace between two warring countries: if both accept -> ceasefire, mediator prestige/opinion.
5. Development loan: money now for opinion, influence and repayment with interest; default -> grievance.
6. Customs union (later): shared tariffs, market access.

## 2026-10-28 evening: user reports (status)
- DONE (pending publish): map modes on current owners; flat provisional flags; Baikonur -> KAZ;
  "Republic of X Republic" names; Niger-State naming; lost referendums; crash after Quebec vote.
- DONE live-only: flat map white glow = other agent's WIP water specular (GlobeAtmosphere.cs,
  SPECULAR 0.48) -> SPECULAR off under orthographic camera in the live shader. Tell the user.
- TODO: verify in the running game (map modes after annexation, flags, glow).
- TODO: economy - India stagnation (growth53_india.txt), chronic oversupply at price floor.

## Economy diagnosis 2026-10-28 (growth53_india.txt, seed 7, 6 years)
- India: real gross output +0.9%/yr; price level 0.25; buildings: Agriculture 428->389,
  CivilianIndustry 47->41, Infrastructure 32->29 (2%/yr CapitalDepreciation outpaces building),
  ExtractionIndustry 24->72 (the AI builds mines). Factories unprofitable: consumer goods at the
  price floor worldwide -> household demand must grow with income and population (catch-up
  growth), and AI construction must weigh development (infrastructure, industry) not only margin.
- Next: read HouseholdEconomy demand per head vs income; AiEconomicPolicy build scoring.

## Economy batch 2026-10-28 night (pub, bins 55-58; commit message tools/c_economy_commit.txt)
- DONE: income factor, two-pass demand recalibration (day 60, then 5-year), AiBuildWorldGlut,
  ShortOfArms, capital deepening relative to own start (CapitalIntensityReference), spin-up 182 d.
- 5-year balance seed 7 (balance59_5y.txt): all civil goods 0.87-1.5x.
- TODO: weapons 3.7-4.6x at year 5 (war demand ends, plants stay): AI production programme should
  switch arms lines to civilian in peace / arsenals absorb; or peacetime upkeep demand.
- TODO: sulfur 0.65x (fertiliser "needs" eat it; refinery sulfur output 0.30 maybe low);
  phosphates 2.41x.
- TODO: India's real output: fiscally starved construction (treasury 287 -> ~50), check budget.
- TODO (growth58.txt, 10y seed 7): CAN 1.74 -> 3.50T (88k/head) on exported electronics/materials
  although labour binds (employed 87 %); USA real output x2 from RareEarths 0 -> 50.9 and Oil
  13.8 -> 48.8 unsold at the floor price: extraction_efficiency rises with Infrastructure
  (0.11 each) so building roads opens mines. World +2.2 %/yr, no first-year jump (spin-up).

## 2026-10-29 USER: leased land and occupation without war (Crimea 2014) - "half-states"
Design (Arrangements.cs, campaign.Arrangements, saved + fingerprinted; same for player and AI):
- Lease: owner keeps sovereignty (ProvinceToNation), holder gets control (ProvinceController) of a
  region's provinces for a term at a rent; Occupations.Tick must spare arranged ground. Rent weekly
  holder -> owner; unpaid -> lapses. Expiry: owner renews or refuses (player: inbox paper); a holder
  refused may withdraw or STAY -> presence ("stayed past its lease", the Sevastopol road).
- Presence (troops in without war): conditions - borders holder or holder lease there, or holder's
  people >= ~40 % of the region, or owner collapsing (stability < .3 / civil war), or invited by a
  movement there. Effects: control flips, owner's armies there leave, owner loses part of the
  region's output; owner grievance + casus belli; notoriety; owner AI weighs war (strength incl.
  allies) or protests; friends of owner cool. Later steps for the holder: referendum -> annex
  (Crimea), found a people's republic as its subject (DPR), or withdraw.
- UI: region panel actions (request lease / end lease / send troops / withdraw / referendum /
  republic), inbox rows for lease offers & renewals, region tooltip lines, chronicle, EN+RU.
- Data common/arrangements: Baikonur KAZ->RUS lease to 2050 (0.115 bn/yr), Guantanamo CUB->USA.
- AI: holders with appetite (AnnexationAmbition) and kin abroad, restrained by the owner's allies.

## 2026-10-29 USER: engine-core performance (towards Flax/Unity level) - may create plugins
Measure first (SimProfiler, frame timings, GC counts), then in order of expected gain:
1. Simulation on a worker thread; UI reads a snapshot (double buffer / immutable view); commands
   queued to the sim thread. Biggest win: no frame stalls while time runs.
2. Deterministic parallel stages (per-nation market balance, economy, demography): Parallel.For
   with fixed reduction order and per-nation RNG streams; fingerprint must stay bit-identical.
3. GC hygiene: no LINQ/closures/temp lists in hot loops, pooled buffers, SoA province data,
   GCSettings.LatencyMode = SustainedLowLatency in play; count GCs per sim day in the bench.
4. Rendering: MultiMesh instancing (armies, cities, ships, trucks), LOD/culling, multi-threaded
   render thread model, no full UI rebuilds (cache panels, dirty flags per widget).
5. Threaded resource loading (ResourceLoader.LoadThreadedRequest), parallel map preparation.
6. Native kernels as GDExtension plugins (Odin/Nim/C++, not Rust) only for dense numeric work
   (province raster repaint, batched pathfinding) and only after A/B incl. marshaling.

## 2026-10-29 USER: treasury dynamics - no mindless hoarding; crises, inflation, domino effects
Treasuries pile up (USA 3.4T cash by year 8, growth58). Real capital was cyclical; mechanisms to model:
- Credit cycle (Minsky): calm years build leverage -> bubble -> financial crisis; bailouts and
  stimulus drain treasuries (1873, 1929, 2008). World business cycle with a phase.
- Contagion / domino: recessions and crises spread along trade shares and creditor ties
  (1997 Asia, 1980s Latin debt); sovereign defaults hit lenders (development loans exist).
- Political pressure to spend: reserves above a comfortable level raise demands (tax cuts,
  welfare, pork) -> stability cost if hoarded, or spending; populist governments spend faster.
- Inflation: money stock vs output; monetised deficits and big reserves erode real value;
  hyperinflation spirals in collapsing states; price level already exists (ConstructionCost).
- Commodity booms/busts now that markets clear: exporters' windfalls and crashes (resource curse).
- Unexpected expenses: disasters, epidemics, ageing/pension bills, corruption leakage, capital flight.
Measure: treasury/GDP distribution over 10-25 years, crisis counts, contagion chains (bench).
