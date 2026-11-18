# Deal or Force — EntertAlpha 0.7.2.3-dev

16 November 2026 · Windows 64-bit · Godot 4.8 Beta 3 (.NET)

This release is built from the current project, including the uncommitted changes captured
for the build. The notes distinguish the new changes since 0.7.1.2 from the improvements
already included in that release over 0.6.6.0. The source snapshot and build information
record exactly what was packaged.

## New in the 16 November build

### Cities and capitals

- A capital is now a particular city. Its name, position and province come from that city,
  rather than a separate regional label. The country screen, country picker and map use the
  same city name in the selected language.
- Moving the capital lets You choose a city in the region. Moving between cities of the same
  region is possible too. Only cities where the government can actually sit are offered;
  where there is no usable city, the regional fallback remains.
- A temporary seat after the loss of a capital is attached to a city as well. Existing saves
  without a city identifier resolve their capital when loaded.
- Cities have a name and coordinates, without a separate population or importance tier.
  They use the same map marker and label size; the capital marker follows the current capital.
  City order controls which labels appear first at a distance. The existing city identifiers
  remain, with additional capitals for small territories and de facto administrations.
- Starting populations are still divided using province-level urban weights. Removing a
  population field from a city does not mean dividing every region equally among its cities.
- Mod-defined cities and province-level urban weights are loaded through the mod system.
  Older city fields are tolerated, and older capital names and coordinates remain supported.

### Campaign interface

- The clock is on a date plate at the top right: slower, pause, faster, speed marks and Menu.
  DEFCON and world tension sit with the date instead of occupying the resource row.
- The alert list is under the date plate, with a Next action and severity marks. The resource
  figures have their own row again.
- Observer mode keeps the campaign interface available while showing world-level information.
  War losses are broken down by country.

### Long campaigns, saves and simulation work

- Countries that are genuinely gone can be archived as short identity records. Obsolete
  market, research, doctrine and history data, unused relations and abandoned assets no longer
  need to remain full live objects forever. The identity needed for a country's return is kept.
- Finished wars shed detailed province bookkeeping after the retention period. History series
  are trimmed under the configured rules while preserving their ends.
- Text saves write repeated relations and routes more compactly and omit unused references
  that simply mean "nobody". A missing save-format setting now falls back to compressed saves.
- Very small population groups can be folded into related groups to limit fragmentation over
  long campaigns. The rules preserve the total population, combine economic values by weight,
  and protect small peoples, homelands and groups tied to living movements. This is an
  approximation in demographic bookkeeping and can change a continued campaign's development.
- Additional caching reduces repeated scans in war, loss and naval bookkeeping.
- Fixed stale active-war data when the latest war is reopened.
- These changes do not promise a particular day speed, constant memory forever, or verified
  thousand-year campaigns. Previous late-save measurements are development evidence for
  particular saves and machines, rather than a benchmark of every player's game.

### Economy and engine

- Capital returns diminish earlier, and household demand follows additional output less
  strongly. This moderates the excessive growth seen after the construction and budget fixes;
  the cheaper parallel construction system remains.
- The engine and the game's Godot .NET SDK are now based on **Godot 4.8 Beta 3**. The launcher,
  game, production rules library and Windows metadata use the same game version.

## Also included from 0.7.1.2

### Money, trade, construction and industry

- A weekly money ledger lists income and expenses for countries, movements and organisation
  funds, with reconciliation for the remaining items.
- Financial crises, disasters and other timed penalties reduce income over a period instead
  of taking an unexplained lump sum from the treasury. Relevant penalties appear in the
  breakdown.
- Alert upkeep is paid by the country that ordered the posture, rather than every country
  being charged for the world's alert level. Diplomatic costs scale with income, and the
  computer's covert spending has an income-based ceiling.
- New buildings no longer reduce GDP simply because their jobs cannot all be staffed.
- Import ceilings account for an actual shortfall more consistently; an artificial grain
  shortage was corrected, and scarce goods are shared between buyers by shortfall and priority.
- Completed factories can start producing. Empty production lines, funding thresholds and
  construction sites that never finished were corrected; sites explain what they await.
- More construction sites can work together. Costs and durations are lower, with a weekly
  funding ceiling, an ordered queue and visible site expenses in the ledger and relevant panels.

### Diplomacy, states and war

- Standing answers for different kinds of diplomatic offers: ask, decline, or sign if
  beneficial. Rules show which decision they made and are saved with the campaign. They do not
  hand land away or automatically answer ultimatums and peace offers.
- Rejected offers decided by rule no longer repeat every week.
- Integrating a subject strengthens its attachment as progress grows, instead of making
  completed integration provoke separation.
- Governments in exile are restricted to actual former states, avoid duplicate identities
  and raw names, and can expire when their claim fades.
- Allies of a defender can advance into empty enemy provinces when their own ground does
  not first need recovering.
- Right-clicking the province an army is defending keeps it there instead of withdrawing.
  Clicking its army card selects it again after starting or loading a campaign.

### Map, language and usability

- Watch the world from the country picker without choosing a country, with world summaries
  and a situation report suited to observer mode.
- Faith families remain the basis of mechanics while denominations can be displayed on the
  map and population screens. A religion map layer is available.
- Smoother distant borders in the supported renderer; the Compatibility path retains its own
  border rendering. Landless movement markers are less crowded at regional zoom levels.
- Resource figures have captions, weekly changes, short history threads and warning colours.
  Smaller layouts prioritise the main figures and expose the rest through the overflow control.
- Left panels clear the navigation rail in both languages. Rank labels are no longer clipped,
  and Scenarios / editor is accessible directly from the main menu.
- Menu commands for returning to the main menu or quitting ask for confirmation.
- Expanded Russian localisation, including leaders, ministries, situations, diplomacy and map
  names; several outdated city names were corrected.
- The launcher can show these notes, check installation hashes, retain launch options, make a
  problem report and warn about conflicting mod data. Direct game launch remains available.
- Interface material and era-overlay definitions are present for modders. They are still off
  by default; plans for further interface and gameplay changes are not completed features.

## Compatibility and verification

The save and network format numbers are not a promise that every old campaign behaves exactly
as before: economics, capitals, population grouping and archive maintenance have changed.
Keep a separate copy of an important save before continuing it in a new build.

The release report beside this changelog records the checks actually run against this source
snapshot and exported executable. It separates this build's results from earlier development
measurements. Multiplayer across two real machines, Android and thousand-year campaigns are
not covered by the Windows export check.

Development tools, private assistant configuration and caches are not player content. Optional
experimental native Luau is not enabled by the standard release export; managed and worker mod
examples remain separate from that editor adapter.
