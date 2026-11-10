# Deal or Force — EntertAlpha 0.6.3.0

10 November 2026 · the start of the army and combat rework

## Release number 0.6.3.0

- The number **0.6.3.0** is the same in the main menu, new save metadata, the game, the
  rules library, the launcher and the Windows EXE, the Godot and export settings, the
  README files, the build instructions and this changelog.
- The save format, map version and network protocol are unchanged.
- The middle digit moved because the way armies are commanded is being rebuilt. This
  release is the first step of that work, not the whole of it.

## Sending forces home

- Divisions can be disbanded: the **Disband** button on the army panel's Organise page,
  or the **Delete** key. The first press asks, a second press within four seconds acts.
  The men go back to the manpower reserve and nothing is counted as a loss.
- A fleet can be decommissioned from its panel (**Decommission**, also asked twice).
  12 % of its hull price comes back to the treasury (`FleetDecommissionRefund` in the
  defines) and hulls still on its slipways are cancelled.
- Neither works in battle, aboard ship, or with troops aboard.
- The computer's unpaid surplus goes home through the same rule, so the player and the
  AI share one way of standing forces down.

## The army bar

- Every army the player has is now a card along the bottom of the map: its name, how
  many divisions it has, its commander, its organisation and strength, and what it is
  doing (holding a front, attacking, in battle, on the march, at sea, idle). The colour
  on the card's left edge shows that state, so the bar reads at a glance.
- Click a card to command that army, double-click to fly the camera to it, Ctrl+click
  to move the selected divisions into it. Divisions in no army form the **Reserve**
  card. The last card, **+ New army**, raises a new army from the selection.
- The bar's position, colours and sizes are data in `game/gui/75_army_bar.json`, so a
  mod can restyle or move it without code.

## Faster marches

- Armies move much faster. They used to crawl compared with how fast in-game time runs.
  A march through friendly ground crosses a district in about a day and a third instead of
  four and a half (`MarchProgressPerDay` 0.22 → 0.75). Taking open country behind a broken
  line and ordered advances into empty ground are almost twice as fast
  (`OpenCountryCapturePerDay` 0.35 → 0.6, `OrderedCapturePerDay` 0.30 → 0.5). Fighting
  through a held line is unchanged.
- What this does to the wars: over eight seeds of the 2024 Russo-Ukrainian front, Ukraine
  ends two years with 128 provinces on average instead of 102. The front moves more, and on
  one seed of the eight Ukraine now collapses. The numbers are in
  `game/common/defines/07_movement.json` for anyone who wants them back.
