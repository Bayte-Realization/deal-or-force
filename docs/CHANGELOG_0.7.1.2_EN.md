# Deal or Force — EntertAlpha 0.7.1.2

15 November 2026 · where the money goes, a market without a false shortage, construction and factories, watching the world, deals by rule, a new top bar

A release on top of 0.6.6.0. Below: what changed for the player, how it was checked, and what
is still not done. The 0.6.2.0–0.6.3.1 line from GitHub was merged into this branch as well; it
was released before and is not listed here.

## Release number 0.7.1.2

- **0.7.1.2** is the same in the main menu, new save metadata, the game, the rules library, the
  launcher and the Windows EXE, the Godot and export settings, the README, the build
  instructions and these notes.
- The save format, the map version and the network protocol are unchanged. This was checked in
  the history: no commit of this release touched the save format version
  (`SaveSystem.FormatVersion` = 2), the map data version (`MapDataVersion` = `2024.11.09-a`) or
  the network protocol version (`NetworkLockstep.Version` = 2). The only change to the protocol
  version in the range (d49cdfcb, 8 November) came with the older line and was already in
  0.6.6.0.
- Loading saves from 0.6.6.0 was not checked as a whole. It was checked in parts: old saves and
  mods naming the faith "protestant" load as before (the religion tests), and surplus
  governments in exile in old saves are cleared at their next review.

## Money: you can see where it goes

- **A money ledger.** Every income and expense of a country, a movement and an international
  organisation's common fund is written out for the week. Whatever has no line of its own is
  reconciled into "Other", so the rows always add up to the change in the treasury. The ledger
  is on the economy, movement and organisation screens.
- **Income penalties instead of lump sums.** Financial crises, pandemics, disasters and world,
  dated and historical events no longer take a sum out of the treasury at once: they take a
  share of gross revenue for a number of days, 50 % in all at most. Decisions, promises and
  modifiers that carry a penalty are named in the breakdown. Check: 65 income-penalty tests
  pass.
- **The big drains closed.** A year counted over three worlds (seeds 1, 7, 42, from 2024) showed
  that construction is not what empties treasuries. Raised alert upkeep took 22–38 % of the
  whole world's income (USA 32–56 %, China 41–71 %), diplomacy 30 % (the median country 85 %,
  Poland 80 %, the USA 2.6 %), covert work 7 %. Now:
  - the country that ordered an alert posture pays for it, not every country by the world's
    level; the others pay only while they are at war themselves; the surcharge falls on the
    army's weekly pay instead of 0.2 % of the whole economy per step;
  - diplomacy is priced to income: a country earning half of what the USA earned in 2024 pays
    half the price; the screens show the scaled price;
  - a computer-run secret service spends at most 5 % of weekly income; players have no cap.
  - After: alert 1.7–6.1 % of world income, diplomacy 2.1–2.3 % (median country 1.3–1.6 %,
    0–1 countries above 10 %), covert work 3.3–3.9 %. A five-year world-health run (seeds 1 and
    7) passed with no broken invariant; in seed 1 world GDP ends 5.7 % higher and defaults go
    10 → 2.
- **An extra building no longer shrinks GDP.** Returns to capital were counted over every job a
  country's buildings offered, even the ones nobody could staff, so past full employment each
  new building made the country poorer: Switzerland at peace built 11 → 29 buildings for the
  same 5.3 million workers, its output halved and its debt reached 98 % of GDP. Now only the
  jobs a country can fill count. Ten years, three seeds: countries ending lower than they
  started 105 → 69, 62 → 59, 84 → 82; Switzerland better in all three. A new test fails before
  the fix and passes after it.

## The market, factories and construction

- **"A shortage of everything" was not real.** The world made more than it asked for of almost
  every good, yet 100–230 countries were short of each. Three causes, all fixed:
  - a country looked for only 36–58 % of what it lacked, because the import ceiling was
    multiplied by transport reach; now a well-connected country buys its whole shortfall and one
    with roads only about three quarters, so ports and railways still matter;
  - grain was generated 15–19 % short of demand; fixed in the source data - on day 60 grain
    meets demand and its price is ×1.11 instead of ×2.27;
  - buyers were served largest first, and island states got a quarter of their grain from the
    grey market; now a short pool is shared in proportion to each buyer's shortfall and import
    priority.
  - Before → after: share of the shortfall sought 49 % → 96 %, bought 49 % → 85–91 %; countries
    under 65 % of their grain 145 → 3. Over a year (seeds 1–3): world GDP 89.1/90.1/88.9 →
    94.0/93.9/94.1 trillion, countries above 25 % inflation 5/5/3 → 4/2/2.
- **A factory that is built now produces.** Four reasons production stayed at zero: a wrong
  threshold in the staff spending check, an empty production line holding its slot, every
  construction site running at once so none ever finished, and idle sites still buying
  materials. Sites now queue, the treasury pays them in order, and a site says what it is
  waiting for. When every civilian unit is converted to military, the screen says so. Check:
  31 industry tests pass.
- **Construction: more, cheaper, faster, under a ceiling.**
  - Sites worked at once: 2 plus one per 4 civilian factories, 30 at most (Italy 1 → 2,
    Germany 1 → 3, China 8 → 21) - one rule for every country.
  - Price ×0.6, time ×0.75: a civilian factory 240 → 180 days.
  - A weekly ceiling of 35 % of budget income plus 1/26 of the treasury; sites past it wait for
    money and say so; construction alone can never empty the treasury.
  - Building sites have their own ledger line ("Building sites"); the old line is now
    "Rebuilding and resettlement". The build list, map placement, the queue and the state
    dossier show what a site and the whole queue cost a week, against income.
  - Three years, seeds 1/7/42: buildings built a year 142 → 393, countries with every site
    frozen for money 26.8 → 12.2, debt to GDP 14.1 → 14.2 %, median inflation 2.5 → 2.0 %,
    world growth 2.0 → 4.6 % a year. A five-year world-health run (seeds 1 and 7) shows no
    broken invariant.

## Deals: standing answers

- Every kind of paper (trade agreements, gifts, payments, land, guarantees and the rest) can be
  given a standing answer: "Ask me", "Sign if beneficial" (with a price range where money
  changes hands) or decline. Papers a rule decided are listed in the inbox under "Decided by
  rule" with the rule that decided. "Sign if beneficial" takes two presses, being the only
  answer that signs. No rule ever gives land away, and ultimatums and peace offers are never
  answered by a rule. The rules are saved with the campaign.
- A refusal by rule no longer makes a donor repeat its offer every week.
- This is the first of eight steps of the deals rework. Check: 85 deal tests pass; the late-save
  world fingerprint is unchanged.

## Watching the world

- The country picker has a **"Watch the world"** button beside "Random nation": every nation is
  handed to the computer, the clock runs, the controls step aside.
- The top bar shows the world, not the last country the picker showed: GDP, wars under way,
  countries holding ground, population, population-weighted stability, divisions. The situation
  report lists the world's headlines and the ten largest wars, each opening on its front; news
  is no longer marked "Your country" when nobody plays that country.
- Checked in the running game (4–15 January 2024); 16 headline popup tests pass.

## War and armies

- **A defender's allies advance into the enemy's empty provinces.** The "take back your own
  first" rule held everyone on the defending side, so Poland and the Baltic states in Ukraine's
  war stood on the Belarusian border beside empty provinces (0 of 7 entered in 180 days). Now
  it holds only a country whose own ground the enemy stands on. Five seeds × 730 days: province
  control changes 7,286 → 10,191, wars ended 125 → 144, wars open longer than a year 30 → 17.
- **A right-click on the province an army is defending holds it** instead of withdrawing.
  Only an attack is broken off that way; giving ground up is still H or Leave battle, behind
  the warning. 14 tests pass, and the test fails with the old rule.
- **A click on an army card at the bottom of the map selects that army again** - after a new
  campaign or a load the cards were not rebuilt and a click did nothing. Checked in the running
  game.

## States, movements, faith

- **Integrating a subject binds it instead of pushing it out.** A player integrated Northern
  Cyprus to 100 % and the chronicle said it "breaks free". The resentment from integration now
  fades with progress and what was built holds the subject: under an overlord in crisis liberty
  desire is 1.10 at the start, 0.81 halfway and 0.51 at 100 % - below the breaking point of
  about 1.0. At 100 % integration completes by itself for every overlord; a subject that would
  answer with a war waits for the player's click. The subject tests pass.
- **Only a country that had a government goes into exile.** By 2034 the map held forty
  "governments in exile", among them armed groups, fragments, two Republics of Circassia and
  raw ids. Now there is one exile per state, names go through the normal naming, and a faded
  exile ends after three years. Three seeds × 10 years: headquarters drawn on the map 75 → 34,
  74 → 41, 60 → 43; governments in exile founded from armed groups 21/15/10 → 0; duplicates and
  raw names → 0. A 2072 save: landless movements 298 → 164. 52 tests pass.
- **Religions: families for mechanics, denominations for the map.** Mechanics compare the
  family of faith (the 46 existing ones, so saves and mods keep working); the map and the
  population screen show the denomination: Lutheran, Anglican, Baptist, Methodist, Pentecostal,
  Latter-day Saints (50 % of Utah), Jehovah's Witnesses, Ahmadiyya, the Orthodox Church of
  Ukraine and the UOC-MP, Twelver, Zaydi, Ismaili, Alawite, the branches of Judaism, Parsis,
  Buddhist schools and more - every share with its source and year. A new Religion map layer.
  358 tests pass; the world fingerprint and the speed of a day are unchanged.
- **Cities:** Astana, not Nur-Sultan, and four more names out of date (Prayagraj,
  Kropyvnytskyi, Gurugram, Shimla); Astana's and Gurugram's populations are current.

## The map

- **Smooth borders from afar.** From the middle zoom out a border no longer runs in the
  raster's steps: the line is drawn off a smoothed field of distance to the frontier. Close in
  nothing changed. Checked in the game over Turkey, Syria and Iraq at three zooms; 15 + 104
  tests pass. The Compatibility renderer keeps the old drawing.
- **Movements without land show from the regional view in**, at most three to a city with a
  "+N" for the rest; every one of them stays in the lists and panels.

## Interface

- **Step 2 of the new interface.** The flag sits on its own plaque with the country's rank. The
  top bar is one resource plate: each of the twelve figures has its caption, its number, the
  week's change and a half-year thread, and turns amber or red by thresholds given as data, so
  a problem is seen without a hover. A healthy 2024 country starts with nothing lit. A screen
  narrower than 1,100 points keeps the five main figures and puts the rest behind "…", which
  takes the colour of the worst one and brings a figure in trouble onto the plate by itself.
  The brief/full toggle by the clock has its caption again. 69 resource-plate tests pass.
- **Steps 0 and 1** are groundwork with no visible change: every layout key is pinned (a
  rename cannot silently drop a mod's override), icons have categories, panel materials and era
  overlays are described as data. **Both are off by default**; the 2024 scenario names no era
  overlay. 32 + 16 + 10 + 6 tests pass.
- **Left panels open beside the navigation rail, not under it.** In Russian the rail is about
  60 points wider than in English, and the agenda, the division, fleet and other panels lost
  their first letters. Every left panel and every workspace now starts where the rail ends.
  Checked in the running game in Russian.
- **The rank badge under the flag** no longer cuts "#156" to "#1".
- **Leaving asks first.** "Return to main menu" and "Quit to desktop" - in the pause menu and in
  both main menus - ask "Are you sure?"; in a campaign they remind that anything not saved is
  lost. Enter picks the safe answer. Checked in the running game in Russian.
- **"Scenarios / editor" is back in the main menu**, under "Load campaign": the scenario library
  with every editor opens directly. "New campaign" still goes straight to the country picker.

## Russian

- 864 strings that were shown in English are translated: the leaders and ministers of 2024 under
  their established Russian spellings, the situation room, the deal inbox, battle notes, dossier
  and economy tooltips, map names. 62 existing translations were rewritten to read as Russian
  rather than as calques of the English.
- Audit of 26,612 Russian entries: 0 keys translated two ways, 0 lost placeholders, 0 empty
  values.

## Launcher

- The launcher wears "ink and paper" over the main menu's planet.
- It does what the game cannot: shows What's new (these notes) in its own language, checks the
  build's files by SHA-256, remembers start options (renderer, safe map, window mode), writes a
  problem report, and warns when two enabled mods ship the same data file. The game still
  starts without the launcher. 44 launcher checks pass.
- The GitHub page: fresh screenshots, how to play, a bug report template.

## For mod authors

- New data: income penalties (`game/common/defines/93_income_penalties.json`, the
  `income_penalty` effect), the construction queue and capacity (`93_construction_queue.json`),
  money drains (`106_money_drains.json`), kinds of paper for standing answers
  (`game/common/deals/00_answer_kinds.json`), denominations
  (`game/common/religions/13_denominations.json`, shares in `game/history/faiths`), the life
  cycle of exiles (`53_movement_lifecycle.json`), the integration numbers in `common/subjects`
  with comments.
- Interface: materials (`game/interface/10_materials.json`, the relief shader
  `game/gfx/interface/74_ui_relief.json`), era overlays (`skins/<id>/`, named by a scenario's
  manifest field `skin`), icon categories (`game/gui/30_icon_roles.json`), the resource plate's
  colour thresholds (`game/gui/79_hud_resources.json`). The list of layout keys is pinned by a
  test.

## Planned next (plans, not features of this release)

Written down but not built: the new interface plan in eleven verifiable steps, in the warm
direction based on Age of History (`docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md`; steps 0–2
shipped here), the overhaul programme (`docs/OVERHAUL_PROGRAMME_2026_11_14_RU.md`) and three
gameplay designs: revanche and national moods, stability levers, manpower and migration
(`docs/design/gameplay-2026-11-15/`).

## Known limits

- The world behaves differently on purpose (economy, market, construction, money drains,
  subjects): an old save carried on will not develop as it would have in 0.6.6.0.
- Why Russia's stability falls to 0.11–0.30 at war, switching its catch-up growth off, has not
  been found; there is no error in its GDP arithmetic.
- On 14 November it was recorded that the computer keeps building for its shortages after it
  runs out of workers and borrows into default; this was not measured again after the
  construction rework.
- After the exile fix undergrounds grow in two seeds of three; an ending for an underground with
  no following is the next step.
- In the market check, defaults over a year went 8/7/11 → 9/5/9 - better in one seed, worse in
  another.
- Religions and holding a province with a right-click were not seen in the running game; an
  army on a drawn front plan may be posted back to its line by the planner after a halt.
- Smooth borders were not checked at the globe's poles, on a 4K screen or on a weak GPU (22 MB
  more texture and about 0.5 s of background work per border change). The movement density
  numbers were tightened after the screenshot and not photographed again.
- City populations other than Astana's and Gurugram's were not checked against 2024 figures.
- Panel materials and era overlays are off: steps 0 and 1 do not change the screen yet. The
  decision model scored step 2 below the earlier screen (the comparison is unclean); the
  owner's eye decides.
- Closing the window with its X or Alt+F4 still does not ask.
- The larger construction rework (region investment, a construction slider in the budget) is
  only described (`docs/CONSTRUCTION_ECONOMY_REWORK_RU.md`).

## Checks

- The full `FormableTests` suite on the working tree at interface step 2: 17,094 checks pass; on
  an isolated copy of the construction commit 17,022 pass, 0 fail. For the last commit (money
  drains) its message gives no full-suite figure: it was checked by its own
  `--money-drains-only` tests and the five-year runs.
- Five-year `SimBench --soak` on seeds 1 and 7 after construction and after the money drains:
  PASS, 0 broken invariants.
- Localisation audit after the 864 translations: 0 keys translated two ways, 0 lost
  placeholders, 0 empty values; a duplicate key the audit found after the leave confirmation was
  removed.
- Looked at in the running game: watching the world, the left panels and the rail in Russian,
  the army card click, the leave question, "Scenarios / editor" in the main menu, smooth
  borders.
