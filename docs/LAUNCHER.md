# The Deal or Force launcher

Updated 28 October 2026: **ReimAlpha 2 0.9.0**. The launcher embeds the game's
current `brand_logo_banner.png` and DoF icon. Rebuild the launcher to update
those embedded resources; replacing a source PNG does not update an existing EXE.

*Русская версия: [LAUNCHER_RU.md](LAUNCHER_RU.md)*

Run **Deal or Force Launcher.exe** beside `project.godot` (development) or
`Deal or Force.exe` (a finished Windows build). The launcher is self-contained: a player's
machine needs no separately installed .NET, Godot, browser or account.

In project mode it needs an installed **Godot .NET** and a built game project. On the first
run, the "Choose Godot .NET" button remembers the path to the engine. The launcher does not
rebuild sources: after changing C#, build the game in the editor.

## What the game is made of

- **Mods**: folders with `mods/<name>/manifest.json`.
- **Add-ons**: folders with `dlc/<name>/dlc.json`.
- Published data folders `data_DealOrForce*/mods` and `dlc` are also supported.
- User `user://mods` and `user://dlc` take priority over the installation when the IDs
  match.
- Folders starting with `_`, `_templates` included, are not installed packages. To use an
  example, copy its individual folder out of `_templates` into `mods`.

The checkbox decides what the next run contains. "Save" saves without launching; "Play"
saves first and then opens the game. Closing the window with unsaved changes offers to save
them, discard them or go back. "Refresh" saves the changes and re-reads the folders. Search
works on name and ID. "Open folder" opens the selected package, or the category directory if
the list is empty.

**Earlier / Later** change mod priority: later ones override earlier ones. Required
dependencies always load before the package that depends on them. The real position is
stated in the description. Mods always come after DLC. Missing dependencies, disabled ones
and cycles are shown as errors; launching with a problematic package enabled is blocked, so
the game never starts with an unexpectedly incomplete set.

A package with executable C# or GDScript is marked with a warning. Only enable code you
trust. The compatibility an author declares with a version is shown as information, not as a
promise of automatically verified compatibility. Switching mods does not guarantee that old
saves and networked games still work.

## DLC and entitlement

Turning DLC off and owning it are different things. A free package (with no `entitlement`)
can be toggled immediately. The rest use the existing `dlc/entitlements.json` and
`user://entitlements.json` system. The launcher **does not grant rights**, does not change
`unlock_everything` and does not rewrite manifests. An installed but unavailable package
stays visible with an explanation.

So a gated pack whose entitlement the shipped `dlc/entitlements.json` does not grant shows
no active checkbox, and that is not a broken toggle. Setting up entitlements as a developer
remains separate from the launcher.

## Settings and logs

The file shared with the game is `user://content_selection.json`. On Windows, by default:

`Documents/Bayte Realization/Deal or Force/content_selection.json`

The path is resolved by the same `GameFolders` code as the game, including Windows
Documents redirection. Language preferences use `settings/settings.json`, with
read compatibility for the older root-level `settings.json`. The profile button
opens this player folder and shows its full path as a tooltip. `--profile` remains
an explicit test override. With no package selected, "Open folder" opens the
player's `mods` or `dlc` directory; a selected package keeps its actual location.

Settings are keyed by ID, not by absolute package paths. With no saved settings, the
manifests' own values are used. Saving is atomic and the previous version becomes `.bak`. A
corrupted file is not silently overwritten; restore the `.bak` or fix the JSON. The game
also reads these settings when started directly without the launcher.

The launcher's log is `Documents/Bayte Realization/Deal or Force/logs/launcher.log`, rotated past 1 MB. The "Logs and settings"
button opens the profile folder. The launcher records discovery errors, saves, the PID of
the launch, the exit and its code. The game's own logs are described in `LOGS.md`. No log is
sent anywhere over the internet.

## Building and distributing

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' build DealOrForce.csproj
powershell -ExecutionPolicy Bypass -File Tools/build-launcher.ps1
```

A standalone x64 Windows EXE appears in the root. For somewhere else:

```powershell
powershell -ExecutionPolicy Bypass -File Tools/build-launcher.ps1 -OutputDirectory 'D:/My Build'
```

The main `Tools/build.ps1` now builds the launcher too and puts `dlc` beside `mods` and both
EXEs before archiving. The launcher also reads the older nested DLC structure. Old game EXEs
need rebuilding: on their own they do not yet read the new switches. Move the whole build
folder, not the game EXE alone.

## Checks

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' run --project Tools/LauncherTests/LauncherTests.csproj -c Release
```

The tests use a temporary isolated installation: dependencies, cycles, file priority, DLC
entitlement, saving and backup, corruption, paths with spaces, and launch parameters.

Development parameters: `--root <game folder>`, `--godot <Godot .NET.exe>`,
`--profile <test settings folder>`. The last one isolates the launcher's settings and passes
`DOF_CONTENT_SELECTION` to the game, but does not override the whole of Godot's `user://`
(saves and the player's own user mods stay in the standard profile).
