# Deal or Force — EntertAlpha 0.6.2.1

November 10, 2026 · consolidated local update with additions to the November 9 build

## Release number 0.6.2.1

- Aligned **0.6.2.1** across the main menu, new-save metadata, game, rules library,
  launcher and Windows executable, Godot/export settings, current README files,
  build instructions and this changelog.
- The pre-export version check now also validates both current changelog headings
  and the release expected by the regression test.
- The save format, map-data version and network protocol are unchanged.
  Historical reports and previous builds retain their actual original numbers.
- The full accumulated update follows; this version correction does not claim
  additional gameplay changes.

This changelog brings accumulated interface, map, simulation, content and tooling
changes together in one release snapshot. It does not mean the development plan
is complete. A local build, publishing source to GitHub and uploading a new game
version to Itch.io are separate actions.

## November 10 additions: armies, fleets and graphics settings

- A normal movement order can arrange a sea crossing when there is no land route.
  It selects an accessible home port, a free national fleet with sufficient capacity
  and a suitable landing shore. Formations march to port, board, cross and continue
  towards their destination. An enemy shore uses the existing landing/battle rules;
  this is not an instant transfer of territory.
- The admiralty leaves a fleet assigned to a crossing alone. Sea-lift orders are
  saved and included in the simulation fingerprint. Defines expose the gathering
  timeout and the landing-shore search radius.
- The map displays the land legs and sea passage, with progress captions on
  formations and in the army panel.
- Embarked formations follow their ships instead of leaving counters on land.
  Fleets show the troops aboard. Sinking part of a fleet inflicts proportional
  losses on its passengers; a formation with no survivors is removed.
- Manual loading takes nearby troops at the fleet rather than collecting them
  from approximately 550 km of coastline. Stronger formations board first within
  capacity, and refusals explain where the fleet is.
- The army-panel header now has a “1/2” button for the same half-selection action
  available through Ctrl+X.
- Automatic quality reduction no longer hides clouds, visual mist, city labels or
  trade traffic against the player's settings. Render resolution, smoothing and
  shadows and the frame cap remain the visual cost controls.
- Regression coverage was updated for sea crossings, quality profiles and
  Lebanon's opening population. This is not a new national-border overhaul.

The complete accumulated changelog, including the expanded doctrines, follows.

## Doctrines and military development

- The Book of War is clearly divided into ground, air, naval and general doctrines.
  Each section remembers its last opened book.
- The catalogue contains **18 books with 16 articles each — 288 articles in total**.
  New books include elastic defence, dispersed warfare, the air shield, convoy
  service, mission command and joint logistics.
- The latest expansion adds **144 advanced articles**: two new independent pairs
  of specialisations in each book, with a continuation for every choice. Choices
  have working benefits and drawbacks; mutually exclusive paths cannot both be
  studied. AI preferences reflect its character.
- Two books can share a shelf. Matching effects on that shelf use the larger
  contribution plus half the smaller one; effects on different shelves add fully.
  Both the stacking rule and the number of books are data-configurable.
- Trees have more readable titles, prices and study/rush buttons, wrapped long
  text and independent catalogue scrolling. Changing books resets zoom and scroll
  to the beginning of the new tree.

## Interface, controls and comfort

- Rank badges by the player's flag, country card and diplomacy open one world
  ranking of territorial countries, rather than just the country intelligence can
  inspect. Search, pages and six sorts cover the index, GDP, population, ground
  power, technology and living standards. The index is a comparison, not a victory forecast.
- Top-bar statistics accept clicks across the whole cell: caption, number,
  sparkline and empty edges.
- Closing rankings, music and alliance proposals with the mouse no longer leaves
  a gold outline that looks like a stuck button. Visible keyboard focus is preserved.
- Improved diplomacy layout, translated navigation clearance, keyboard search
  and headings above country and organisation lists.
- Escape closes more local windows and modes without unnecessarily opening the
  pause menu, including country release, fleets, armies and map cards.
- Map-layer buttons stay in place when hovered. Country relationships appear as
  navigable flags, showing different kinds of ties.
- Shared buttons, bars and sliders have matte texture, relief and clearer hover,
  pressed and disabled states. These use shared resources, not one animation
  handler for every button.
- Battle-dossier actions remain outside the scrolling area, formation lists can
  collapse, and blocked withdrawal explains its reason before an order is sent.

## Recruitment and construction on the map

- Recruit by choosing a training region on the map. Hover previews show price,
  time, personnel, materials, capacity and rejection reasons. Clicking submits
  an ordinary recruitment order, not an instant division at the pointed location.
- The recruitment window has five sections, a catalogue beside the order,
  persistent actions, name/ID search and retained template/mode selection.
- Army stockpiles show resource names, quantities, capacity and daily change.
  Rows are reused instead of rebuilding the grid on every refresh.
- Construction previews show price, duration and effects before purchase;
  repeated placement checks available funds again. Ctrl-box selection supports
  placing projects across several regions.
- Escape and right-click finish placement without cancelling paid orders.
  Construction and recruitment do not compete for the same gesture.
- Fixed cases where simply opening a window or reading a preview initialised
  stockpiles and silently changed the campaign.

## Map, cities and regions

- Fixed army-marker interpolation across the horizontal 2D-map seam: changing
  repeated map sheets no longer looks like travelling across the world.
- Country selection before starting highlights the entire country, not one region.
  Map and diplomacy names use a shared naming path.
- Improved map sharpness, province-line width, distant labels and fog stability.
  Refined polar geography.
- Region badges show specialities, resources, projects and regional conditions,
  with tooltips explaining their meaning. Badge placement is steadier while
  moving the map, and building symbols remain proportionate to their region.
- Expanded the city catalogue and corrected regional ownership, population and
  behaviour when territories split. Refined opening population, homelands and
  individual geographic records in the scenario.
- Compatible pieces of a region can reunite without annexing another country's
  land. A parallel administration's local footprint survives separately from the
  region's border. Removing treaty or administrative restrictions wakes another
  reunification check.

## Flags, country appearance and audio

- Interface flags have cloth lighting and soft movement without GIF animation.
  Fine texture fades below pixel size to reduce uneven shimmer. Fabric and
  animation have independent settings.
- Movements and governments in exile retain their national or authored flag
  identity instead of incorrectly falling back to a parent country's flag.
- Current-country appearance is separate from scenario authoring. Names, palettes,
  HEX colours and flag designs apply to the running campaign and save with it;
  leaving returns to that campaign, not the scenario menu. Discarding a draft
  requires confirmation.
- A changed colour refreshes the map's actual loaded political palette; a new
  flag refreshes the HUD and open country cards.
- Added the soft Felt map selection cue and the calm Atlas by lamplight track.
  Important alerts have not been replaced with quiet selection feedback.
- Mod-declared MP3/Ogg/WAV files can load directly without a prior Godot Editor
  import; packaged audio resources remain supported.

## Military AI, pockets and homecoming

- An unreachable order to a front post no longer counts as real coverage.
  An available reserve can receive an actual route to the empty position.
- Peace clears finished operations that blocked peacetime deployment. Free AI
  troops can march home from accessible allied territory, without teleportation
  or recalling the player's non-delegated troops.
- Marching formations are not redirected halfway through solely because the
  next threat review prefers a different border position.
- Long-range formations evacuate lost bases for human and AI countries alike.
  Without a suitable base, a formation becomes an inactive off-map reserve
  instead of operating from foreign territory.
- An encircled garrison can continue a real battle while strength and stores
  remain. Attrition and normal losses still apply; the defence is not immortal.
- Withdrawal and breakout use adjacent permitted steps, not free jumps to a
  distant rear. Relief depends on live formations and real routes/attacks, not
  merely a nearby ally. A reserve can reconnect a pocket through an empty post.
- Subjects and overlords share supply corridors. Improved squadron and shipyard
  development and fixed repeated payment of a fleet's one-off founding cost.

## Diplomacy and a more changeable world

- Military alliance proposals show all positive and negative reasons, the total
  and acceptance threshold. Confirmation rechecks the situation; a human response
  is not replaced with an automatic AI decision.
- AI considers both sides' benefit and avoids unnecessary duplication of existing
  collective defence. Fixed preparation/evaluation bypasses in early ultimatums
  that caused instant territory transfers.
- All 17 Cooperation actions are available in the quick diplomacy card, using
  shared prices and rejection rules with an explicit selected recipient.
- An alliance or guarantee can turn into rivalry over wars, broken obligations,
  retained land and worsening relations. Players receive a decision rather than
  having their alliance silently broken.
- Coalition members can negotiate separate peace: leave without concessions,
  concede occupied land, pay or accept a buy-out. Retention subsidies and changing
  sides have political consequences.
- Crises involving real clashes need not disappear automatically on a timer.
  Great-power intervention follows its interests. Revised individual financial
  and preparation barriers to independent AI initiative.
- Peace documents explain border shape, occupation transfer and truce duration
  more clearly. Reading a document does not sign it.
- Refined the lifecycle of exiled governments, placement of risings and the
  relationship between newly created countries, their land and their people.

## Performance and saves

- Weekly demography runs in bounded worker batches; large countries' regional
  economy also uses independent regional jobs. These are specific parallel
  stages, not the entire mutable world moved to a worker.
- Front, army, battle, war-score and logistics indexes/scratch buffers are isolated
  by campaign. Reduced repeated searches and temporary collections.
- Hot passes use active-war lists instead of repeatedly walking the full archive.
  Ended wars remain available to history and documents.
- Reduced repeated traversal in flag polling, alliances, formable-country checks,
  economic exposure and kin-population calculations. Corrected population-cache lifetimes.
- Faded public images and obsolete references are cleaned up; existing strings,
  cards and buffers are reused where safe.
- One-time JSON-contract preparation for the first autosave runs on a worker
  during loading. Capturing a consistent live save remains separate work.
- Fixed reading the saved FPS limit. A paused, unfocused window uses background
  frame limiting without independently changing the active campaign's clock.
- Improved tick, memory, frame-stall and long-run reporting. Benchmarks count
  genuinely elapsed days and stop when the clock stalls or simulation faults.

## Multiplayer

- Hourly lockstep, a pre-start version handshake, host-selected scenarios and
  coordinated human seats.
- Recovery can retry while paused using real time. The host independently limits
  requests before expensive snapshot preparation.
- Client catch-up is spread across frames with one shared budget; mixed step
  lengths remain ordered. Orders, seat changes and checksums wait for a completed
  simulation boundary.
- Command queues avoid shifting their entire tail on each removal and release
  references on recovery/shutdown. Fixed individual post-snapshot divergences.

## Tools for modders

- Small doctrine additions through `append_nodes`, dependency/exclusion validation,
  AI preferences and appearance tuning without rewriting C#.
- A six-parameter recruitment-layout example, doctrine-tree, palette, flag-fabric
  and audio settings, with documentation and inactive templates.
- Campaign cosmetics remain separate from exporting a scenario flag into a mod.
- Improved BCE scenario settings and a calendar example. Changing the calendar
  alone does not replace modern content with a medieval or ancient world.
- Expanded worker-mod examples, including Kotlin/Native alongside Go/Rust/Luau,
  and instructions for the existing protocol. A template does not mean its compiler
  is installed or that these worker mods are enabled in multiplayer.
- Retained JSON/YAML overlays, era-based building rules, event-driven officeholder
  and portrait editing, and loose audio loading from packages.

## Verification and known limits

The accompanying release verification report records exact checks of the final
candidate: compilation, exported content, rule tests and the executable itself.
Historical results from individual development passes are not a combined count
of new tests and do not replace checking this build.

- A comfortable **1,000+ year campaign has not been accepted**. A one- or three-year
  run cannot establish that the game will not slow down after a millennium.
- Remaining autosave preparation, heavy-event and tick stalls are still present.
  This is not a claim that every microfreeze is gone or that total RAM/CPU usage
  has fallen by a particular percentage.
- There is no general parallel tick or ECS integration. Snapshot and Rust
  experiments are not presented as production-wide acceleration.
- Two local ENet processes do not establish WAN/two-machine reliability, packet
  latency/loss handling, every mod combination or host migration.
- Saves from another map version may be refused. Universal automatic migration
  is not promised, and past annexations are not rolled back.
- The full visual matrix across zoom levels/renderers and all 2D layers, military
  AI balance, automatic naval evacuation and all long-term peace deployments remain open.
- The existing catalogue has 46 religions/traditions/worldviews. This is earlier
  content, not a completed new religion expansion with unique benefits and
  drawbacks for every faith; that work remains planned.
- One new cue and one track are not a complete audio overhaul. Overall style,
  accessibility, economy, editors and the remaining backlog continue to develop.

Neither this changelog nor successful compilation declares the whole project finished.
