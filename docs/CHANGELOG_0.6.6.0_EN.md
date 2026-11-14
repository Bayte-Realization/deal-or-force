# Deal or Force — EntertAlpha 0.6.6.0

14 November 2026 · late-game speed, a truthful map, a new main menu, the first session

A large release on top of 0.6.3.1. Below: what changed for the player, how it was checked,
and what is still not done.

## Release number 0.6.6.0

- **0.6.6.0** is the same in the main menu, new save metadata, the game, the rules library,
  the launcher and the Windows EXE, the Godot and export settings, the README, the build
  instructions and this changelog.
- The save format, the map version and the network protocol are unchanged. Loading saves
  from 0.6.3.1 in this release was not checked separately.

## Late-game speed

- A day of the late game is computed about twice as fast: on a real save of 2050 (495
  nations in memory, 26 open wars, 2,070 divisions) it is **533 → about 240 ms a day** on
  a quiet machine; under load the figure is higher.
- It is a speed-up with no change in behaviour: the world after the run matches the old
  one bit for bit, with threads and without. AI diplomacy, movements and advances, society
  and opinion, the war machinery, stockpiles and the market were made faster.
- Every country is simulated the same way: there is no shortcut for small countries or for
  ones far from the player.

## The map

- **No made-up countries.** Siachen, disputed reefs, bases, buffer zones and the other
  "non-countries" of the source map belong to whoever actually administers them;
  Antarctica, the Spratly Islands and Bir Tawil are nobody's. They are gone from the
  country picker and from diplomacy.
- **Strait crossings.** 34 crossings - the Gibraltar ferry, the Crimean Bridge and others.
  An army marches over one more slowly, an assault across one is weaker, and an enemy
  squadron in the strait closes the ferry to the country it is at war with.
- **One line for every border.** A border that appeared during play is drawn like a border
  from the scenario.
- **The country border leads.** The province line is a hairline; the country border is
  still the heaviest line on the map and is thinner than it was: about three pixels of ink
  instead of six.
- **One colour per country.** Inside its borders a country no longer breaks into two tones
  laid out in squares: the kind of ground only tints the colour, and the relief is carried
  by a smooth hillshade.

## Main menu and loading

- Behind the menu is a **live planet**: a globe wearing the game's own map (first-day
  borders and country colours, relief), with no icons and no labels. It turns slowly, the
  dusk line crosses it with the lights of the cities, and it can be spun with the mouse.
- One leading button instead of eight equal ones; Continue comes first when there is a
  save.
- The loading screen shows the same planet.

## The first session

- New campaign goes straight to the country picker. The picker keeps the map, the search
  and one button; playing a movement without land and the world seed are folded away.
- **The "Next" line.** Under the hint strip one line names the first thing not yet done
  ("Next: Shortage: Tanks →") and opens the screen for it.
- Game speed on keys 1–5 and ±, Handbook [F1] in the pause menu, leaving a campaign asks
  first.

## Interface and Russian

- The "ink and paper" palette: light headings and icons, one blue leading action instead
  of gold on every button. State colours and the map's own colours are unchanged.
- 113 strings that were shown in English are now in Russian (the whole Situation room, the
  deal inbox, tooltips); clipped captions in the top bar and the map layer switches are
  fixed.
- The picture settings are called "Turn off the AI-slop loading screens" and "Turn off the
  AI-slop main menu pictures" again.

## Fixes

- **A campaign stopping with "Treasury = NaN".** A campaign could be stopped by the
  integrity check: a movement campaigning in a region with a corrupted head count paid a
  not-a-number out of its treasury. The price of an action can no longer be not-a-number.
- The credits carry an honest line about how the game was made.

## For mod authors

- The menu's planet is replaced by two pictures by name (`menu_globe`,
  `menu_globe_lights`) and three shaders (`game/gfx/interface/73_menu_globe.json`);
  `Tools/bake-menu-globe.py` bakes the pictures from the map data.
- Icons are drawn in one ink and coloured by the skin; `Tools/reink-icons.py` re-inks a
  set. The weight and colour of the map's lines are data (`game/gui`).

## Known limits

- At the middle zoom a border still runs in small steps along the raster's cells: a thinner
  line shows less of them, and they will only go with a rework of how borders are rebuilt.
- What corrupted one region's head count in one of the worlds examined has not been found:
  the consequence that stopped the campaign is fixed. The trap `SimBench --nan-trap` stays
  in the tools.
- Army and leader names stay in Latin script in the Russian version: they come from the
  world's data.
- The menu backdrop and the map were checked on one machine (Windows, Forward+); 4K screens
  and other video drivers were not checked separately.

## Checks

- The full `FormableTests` suite: 16,228 checks, 0 failures.
- The world fingerprint after 14 late days matches the old one, with threads and without;
  `SimBench --determinism-only 60` agrees to the bit.
- Localisation table audit: 0 keys translated two ways, 0 lost placeholders, 0 empty
  values.
- The menu, the loading screen, the country picker and the map at the middle zoom were
  looked at in the running game.
