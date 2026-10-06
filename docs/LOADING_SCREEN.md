# The loading screen

*Русская версия: [LOADING_SCREEN_RU.md](LOADING_SCREEN_RU.md)*

A strategic-dossier screen built from the existing logo
(`game/gfx/interface/brand_logo_banner.png`) and a light texture
(`game/gfx/interface/panels/panel_dossier.png`). The grid, the frame and the cartographic
mark are drawn as vector lines; there is no separate 3D scene, no shader effect and no new
sound.

## When it appears

- Starting the game: before the catalogues load, the campaign is created and the map is
  prepared.
- A new campaign begun after a game is already started or loaded.
- Loading a slot, a quick load, and "Load latest campaign".

Opening ordinary menus and autosaving are not covered by the full-screen panel. Receiving a
world snapshot over the network still uses the existing connection interface.

The indicator shows stages — not the percentage of bytes read and not an estimate of the
time remaining. There is no minimum-duration timer: the screen disappears once the world is
ready and a frame with a finished map has been presented. While it is up, campaign time does
not advance, the camera, orders and hotkeys are locked, and pressing load again does not
start a second operation.

## Errors and diagnostics

A save that fails to read leaves the previous world alone and offers to go back. If the
initial load failed, or the binding of an already-replaced world did, the game offers to
close rather than continue in a damaged state. A failure to build or load the map layers is
also reported on screen.

The existing `performance.log` receives `LOADING_STAGE` with each stage's duration and
`LOADING_COMPLETE` with the total in milliseconds. `LOADING_FAILED`, with the exception and
its stack, goes to `errors.log`. Where the logs live is described in `LOGS.md`.

## Code and localisation

- `Scripts/LoadingScreen.cs` — the look, the responsive layout and the error screen.
- `Scripts/LoadingScreenUI.cs` — the shared operation coordinator, repeat blocking, frame
  waiting and logging.
- `Scripts/gfx/GlobePresentation.cs` — the map's real readiness and the stages of handing layers
  to the GPU.
- `game/localization/{english,russian}/10_loading.json` — the translatable strings; mods
  override them the usual way.

Heavy operations on Godot resources stay on the main thread with a frame presented between
stages. The background map raster builder that already existed carries on separately. This
screen does not promise to remove every main-thread stall.

Checks: `.fennara/scripts/runtime/loading_screen_check.gd` creates its own unique test slot,
verifies restoration, a repeated call, a new campaign and the return path after a missing
slot; the test slot is then deleted through the normal save system.
