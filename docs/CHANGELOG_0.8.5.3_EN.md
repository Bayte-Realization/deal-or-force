# Deal or Force — EntertAlpha 0.8.5.3

23 November 2026 · Windows x64 & Android · Godot 4.8 Beta 3 (.NET)

This cumulative changelog covers the 0.8 EntertAlpha series, from the 19 November release of 0.8.3.2 through 0.8.3.4, 0.8.3.5, and the 0.8.5.3 architecture, Android, map relief, UI clarity, and gameplay expansion update.

## 0.8.5.3 · Background simulation, 3D terrain relief, Android & gameplay expansion

- **Dedicated background simulation thread (`DoF-Sim`) and zero-freeze pacing.** Daily simulation ticks run asynchronously on a dedicated background thread with stage-yielding `StepFor` slicing, non-blocking HUD/map reads (`TryEnterRead(0)`), long-campaign O(1) demographic folding, and pre-baked binary map boot caches — removing end-of-day micro-freezes on both Windows and Android.
- **Playable Android release pipeline.** Mobile-tuned low-memory asset path, `STORED(0)` APK resource packaging for fast audio/texture access, touch camera & command controls, safe-area layout, and a pause-menu Developer Console with dynamic soft-keyboard avoidance.
- **Physical 3D terrain relief with elevation-aligned overlays (PC).** Armies, cities, capitals, front lines, battles, trade routes, and map labels sample local terrain elevation so every marker sits cleanly on mountains and lowlands alike, paired with height-synchronized fog of war, clouds, and atmospheric shells.
- **Country Dossier & Economy workspace clarity.** Added a clickable **Capital** row (`FocusLatLon`), expandable **Domestic Stability Outlook** driver breakdown in the Country Dossier, and a live **Tax Efficiency** collection warning banner in the Economy workspace whenever stacked taxes exceed economic tolerance.
- **Distinct splinter & civil-war colours.** Expanded the secession palette to 36 high-contrast cartographic hues with 2-hop region neighbor checks, perceptual hue/neutral separation, and survivor/faction colour conflict guards.
- **Major gameplay & modding wave.** Added **Grand Projects**, **World Situations**, **National Ambitions** & **Cabinet Directives**, multi-variant **Production Methods**, **Diseases & Disasters**, expanded **Strategic & Tactical Doctrines**, **Mainland Reclamation**, and **Mod Content Studio**.

## 0.8.3.5 · AI staff delegation & Diplomatic Inbox

- **AI staff delegation in one place.** Your own country dossier (`Q` or clicking your flag) adds a dedicated **AI Staff** (`Штаб (ИИ)`) tab plus an Overview quick-link (`Configure →`) with a master toggle and individual switches for all 11 branches (construction, research, cabinet personnel, subjects, treasury, trade, army size, army field command, foreign relations, decisions, and navy). **State Affairs** (`E`) also exposes the full 11-branch **THE STAFF** 2-column grid and header counter right at the top of the screen alongside the treasury switch.
- **Diplomatic Inbox restored to the main HUD.** A permanent **Diplomatic Inbox** bar sits at the top of the right-hand HUD column directly below the Date/DEFCON plate showing incoming letters across all 9 diplomatic channels (deals, ultimatums, foreign assistance, arms shipments, military access, leases, alliance/rival shifts, pacts, and diplomatic acts), synchronized with the Diplomacy left-rail counter and a direct button inside the Country Dossier `Diplomacy` tab. The Diplomatic Inbox modal uses the diplomatic laid-paper surface and frames cleanly in the new campaign shell.

## 0.8.3.4 · Map, menus and launcher

- **The treasury and manpower update while placing orders on the map.** Recruitment shows the deduction after each accepted click, construction payments appear as the work is funded, and reconstruction charges show as soon as they are paid.
- **The campaign top bar shrinks when the last war ends.** The row of national figures and its outer frame now resize together.
- The realistic map look is called **Realism** in Display settings.
- **Pause menu ornaments stay with their panel.** The pause frame and portrait trim follow the menu draw order when Settings or Sound and artwork covers them. The same rule applies to shared frames and ornaments in other menus.
- **Region selection ends when the dossier closes.** The close button and Escape both remove the highlight from the region and the picked province. Going from the region dossier to the country dossier also clears the ground selection.
- **Army counters stay steady at a fixed camera.** On the globe their size follows their actual camera depth. On the flat map the same zoom compensation applies during creation, refresh and layout.
- **Reused counters appear in the correct place.** An existing formation still glides when it crosses a province border. A recycled counter for another stack starts at that stack's location.
- **Army readouts stay behind the globe.** A refresh no longer makes the equipment and organisation bars of hidden armies flash across an ocean or another continent.
- **Both languages have release notes in the launcher.** The English and Russian changelogs travel with the game and open under What's new. The menus, launcher, newly written saves and Windows executable metadata carry the current release number.

## 0.8.3.2 · Campaign interface and controls

- The new campaign interface arranges the national figures along the top, a date and speed panel at the right, compact navigation at the left, and the minimap and map modes at the bottom. The clock can show hours. Choose a speed with the numbered controls or keys 1–5 while paused, then resume when ready.
- The live political minimap follows changes in ownership and the camera. The bottom strip places army and front cards together and gives the map more room when a full workspace is open.
- The country picker shows a country dossier alongside the map. The campaign dossiers show overall world rank, including the badge at the upper left, and the left rail brings available Decisions and Long-Range Forces within one click.
- Dispatches appear in a paced newspaper feed. Related messages gather in the queue, and pinning a dispatch or hovering over it gives time to read. Dates use the active language. Escape closes the uppermost menu or dialog while a dispatch can be dismissed with its own close button.
- The main menu has a Continue campaign card with the country's flag. The Pause Desk shows the ruler, date, world rank and war status. In the save archive, search by slot, campaign name or country tag and see the format on each result.
- The country dossier gives rulers and heads of government room for a photo or anonymous silhouette, their title and political profile. The Society tab presents government and faith summaries and population indicators.
- The region dossier keeps a steady width as the campaign advances. Its overview shows terrain and movement, combat and supply modifiers, followed by stability, living standards, urbanisation, literacy and radicalism. Separate tabs show population, construction and recruitment, with demographics, buildings, deposits and garrisons where available.
- The war ledger gives front summaries a full-width layout and opens them directly from the war alert. The treasury's debt screen gains an amount slider and a shortcut for the largest repayment available.

## 0.8.3.2 · Map, artwork and audio

- Switch the map look live between Realism, Paper, Atlas and Classic. Political fill has opaque, balanced and translucent settings so the terrain and borders can be read at the preferred contrast.
- The realistic view adds graduated ocean depth, more natural highlights, finer waves, elevation-dependent snow and river shading. A neutral palette reduces yellow haze, and clearer lettering makes city names easier to read.
- Cities, capitals and ports have distinct 3D miniatures. City silhouettes and coastal placements were corrected. The Bosphorus and Dardanelles are represented as water with their crossings and sea passages defined in map data.
- The desks and workspaces use five matte finishes drawn from leather, slate twill, archive linen, laid paper and blueprint grid. Corner ornaments, ruler bezels, tab strips and an expanded set of terrain, menu and building icons share the same visual style.
- Four short interface sounds mark a press, an opening drawer, a map mode change and a dispatch. The music library adds **Architects of Détente**, a chamber piece for quieter moments.

## 0.8.3.2 · War and campaign rules

- Full occupation or a decisive war score can settle an interstate war without waiting indefinitely for the defeated government. A fully occupied subordinate coalition member can leave by separate peace. A human victor receives the enemy's capitulation as an offer.
- Active civil wars keep their existing opportunity for a counteroffensive. The decisive-peace rule applies to stalled civil wars under its configured conditions.
- A standing front on a single border is shown among the player's planning orders so it can be found again from the map controls.
- Returning states recover a seat and their records, and leave the fallen-country list. The militia replenishment and homeland bonus use the correct map identifiers when checking the region.
- Army groups with nobody serving in them are retired. Recruits keep their headquarters, and retired group IDs and names are kept out of reuse.

## 0.8.3.2 · Speed 5 and long campaigns

- The time loop yields between smaller pieces of work so heavy simulation stages leave room for rendering and input at high speeds. Weekly economic work is spread across the calendar.
- Calm peacetime AI countries can use a seven-day evaluation interval. Belligerents, threatened countries and countries in domestic crisis still need daily attention.
- Trade uses compact lists of participating economies and reuses distance and deal-scoring data. War systems that only need active conflicts can skip the finished-war archive. Political and long-range-force calculations reuse temporary data and search results.
- Very small compatible population groups can be folded under the long-campaign rules. Total population is preserved, with safeguards for homelands and relevant movements. Folding can change how demographic detail develops over time.
- The map reuses army counter nodes and textures, updates battle icons and province controller colours less often under load, and avoids rebuilding the hidden classic interface when the new one is in use.
- In the 0.8.3.2 development benchmark, the fixed Speed 5 run at the 2024 start rose from **2.61 to 6.53 days per second** and the longest simulation frame fell from **661.9 to 27.3 ms**. A separate 30-day test of the 2131 save measured **21.53 ms at p99** in its 9 ms slicing configuration. These are measurements of those workloads in SimBench. In-game frame times and campaign speed depend on the map, save, settings and hardware.

## 0.8.3.2 · Mods and engine

- Layered mod loading covers more than 90 data categories. More catalogues support explicit removals, and content checks can report malformed definitions and invalid paths earlier.
- Mods can customise the chrome, icons, textures and layout metrics, or reload supported presentation assets from the editor. Documentation and sample mods show how to override data, change presentation and check content structure.
- Campaign caches belong to their campaign, reducing repeated scans and leakage of cached state between sessions. Selected rules execute in the shipped F# library.
- The source includes optional Rust kernels for selected map and range calculations. The standard Windows package uses the built-in C# fallback for those kernels.
