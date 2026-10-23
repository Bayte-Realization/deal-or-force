# ReimAlpha — 13 October 2026

[English](REIMALPHA_2026_10_13.md) · [Русский](REIMALPHA_2026_10_13_RU.md)

Development continues on the **ReimAlpha** branch. This pass closes one specific
diplomatic loop and some editor upkeep; it does not declare the large rework of the
economy, organisations, movements and map finished.

## Changes

- Incoming offers to players with an explicit signature, refusal, withdrawal,
  counter-terms, a deadline, a history, and a fresh check against the current world.
- A new-deals button on the HUD and a full mailbox in diplomacy; a map of the terms,
  the resulting treasury and explanations of the limits. Russian and English labels.
- Letters are saved, and the network answer is authoritative. Signing twice does not
  charge twice.
- Fixed duplicates piling up in the speed table when a save is loaded.
- Icon sources moved from `Assets/ArtSource` to `Tools/IconCompiler/Source`.
  Recipes, atlases and mapping files are kept; runtime icons were not moved.

## Checks and what comes next

Checks are to be run on the snapshot being published, without unfinished local changes
from other tasks. Probe campaigns and temporary diagnostic methods are not part of the
release game code. Exported builds are not added to Git.

Next: more meaningful outgoing offers from states; negotiations inside organisations;
the causes and consequences of a territorial agreement in the shared diplomatic
history; the long-run balance of the economy and checks of late campaigns. They are not
to be replaced by random gifts of territory to show off the new mail.

In this pass a successful runtime launch confirmed the new incoming-offers indicator on
the HUD at 1920×1080. Two later launches through Fennara did not reach the runtime
helper's readiness in time, and an ordinary launch from the editor did not reach the
helper's readiness either. After that a clean copy of the prepared Git snapshot,
without local editor addons, went through the whole scenario with the mouse: letter →
review → confirmation → a single signature → closing with ESC. The Russian layout was
checked at 1920×1080 and 1280×720; the actions stay pinned outside the scrolling terms.
The hang when launching the working editor environment remains a separate limitation,
not evidence that this clean snapshot is broken.

Details: [negotiations](DEAL_NEGOTIATIONS_RU.md), [icons](ICONS_2026_10_04_RU.md)
(in Russian).

The result on the clean copy: the C# build had 0 errors and 0 warnings; the full
FormableTests set gave 2 046 PASS; separately, 51 PNG files passed decoding and the size,
palette and transparent-margin checks. The GUI contract had 0 scenario failures. On exit
from one GUI run Godot reported unreleased ObjectDB objects; this is not hidden as "zero
runtime warnings" and needs its own audit of application shutdown.
