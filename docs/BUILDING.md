# How to make a build

*Русская версия: [BUILDING_RU.md](BUILDING_RU.md)*

For handing the game to somebody who has neither Godot nor the sources.

## Once: the export templates

Godot will run the project in the editor with no preparation at all, but it can only build a
standalone `.exe` from **export templates** — a separate download of about a gigabyte, and
they are not installed by default.

In the editor: **Editor → Manage Export Templates… → Download and Install**.

The version has to match **exactly**, the word `mono` included: the engine here is
`C:\GodotEngineNet\Godot_v4.7.2-stable_mono_win64.exe`, so the templates must be
`4.7.2.stable.mono`. Templates from a plain build will not do for a .NET build, and the
error when they do not will be unhelpful.

To check they arrived: the folder
`C:\Users\<name>\AppData\Roaming\Godot\export_templates\4.7.2.stable.mono\` should exist and
not be empty.

## Every time: build

```powershell
powershell -ExecutionPolicy Bypass -File Tools\build.ps1
```

The script exports to `build\windows\`, verifies the build (see below) and leaves
`build\Deal or Force <date>.zip` beside it — that is the thing to send.

Switches: `-Debug` builds the debug version (it has a console and detailed errors, which
helps when a tester catches a crash), `-NoZip` skips the archive, `-Godot <path>` if the
engine moves.

The same thing by hand, if the script does not suit for some reason:

```powershell
& "C:\GodotEngineNet\Godot_v4.7.2-stable_mono_win64.exe" --headless `
    --path "D:\Godot Projects\by-deal-or-force" `
    --export-release "Windows Desktop" "D:\Godot Projects\by-deal-or-force\build\windows\Deal or Force.exe"
```

### One wrinkle about `export_presets.cfg`

The `Windows Desktop` preset is already in the project root and Godot accepts it — verified:
the export gets as far as checking the templates and fails only on those. But the file is in
`.gitignore` (that is Godot's convention: presets often end up holding machine paths and
signing keys), so anybody who clones the repository will not have it, and `Tools\build.ps1`
will not run for them.

There is nothing machine-specific in this preset — the export path is relative and there is
no signing. If somebody other than you will be making builds, remove the `export_presets.cfg`
line from `.gitignore` and commit the file. While you are the only one building, it can stay
as it is.

## Three things the first build tripped over

All three are fixed; they are written down so it is clear what not to touch.

**1. There was no solution file.** When exporting a .NET project, Godot requires a `.sln`
named after `dotnet/project/assembly_name` from `project.godot`. There never was one — the
editor does not need it. Without it the export **reports no error**: it packs the `.pck`,
writes "completed with warnings" and hands back a folder with not a single managed assembly
in it. A C# game in such a folder does not start at all. There is now a `DealOrForce.sln`,
and the script refuses to build without it.

**2. Three names did not match.** `assembly_name` was `"Deal or Force"`, so Godot looked for
`Deal or Force.csproj`, while the file had always been `DealOrForce.csproj` — hence
`MSBUILD : error MSB1009: project file does not exist`. They are now one name with no
spaces: `assembly_name`, `<AssemblyName>` and the `.csproj`/`.sln` names are all
`DealOrForce`. Change one and you change all four.

**3. The order ribbons opened `res://` as a file.** `GlobeOrderRibbons` loaded its png
through `ProjectSettings.GlobalizePath("res://game/gfx/globe/...")`. In the editor that is a
real path; in a built game it is a path inside the `.pck`, which does not exist on disk:
`Image.LoadFromFile` returned null, and the very first exported build crashed with a
`NullReferenceException` on the second screen. Now it looks for a loose file first (so a
modder can substitute the png), then for the imported copy, and if there is neither, the
ribbon is drawn without its grain and the game carries on.

While we are here: on this machine's PATH, `C:\Program Files (x86)\dotnet` comes **before**
the real one, and that is a 32-bit host with no SDK at all. So the script puts
`C:\Program Files\dotnet` at the front of PATH — which is not belt-and-braces but a cure:
the same PATH breaks the editor too, see the next section.

And one more: **Godot's exit code cannot be trusted**. A successful headless export leaves
`$LASTEXITCODE` empty, while a failed C# build announces itself with a warning. So the
script only prints the exit code and decides by what is actually in the folder.

## If the editor has stopped running the project

The symptom: F5 in the editor does not start the game, and the build panel shows one of
these:

    .NET Sdk not found. The required version is '10.0.12'.
    Could not load file or assembly 'Microsoft.Build, Version=15.1.0.0, ...'
    ERROR: An EditorPlugin build callback failed.

That is the same PATH. The editor looks for `dotnet` along PATH and finds
`C:\Program Files (x86)\dotnet` first — a 32-bit host with no `sdk` folder in it at all.
MSBuildLocator reports that there is no SDK on the machine, `Microsoft.Build` then fails to
load, the C# build from the editor collapses entirely, and with no build there is nothing for
Play to start.

What makes it treacherous is that **`dotnet build` from a console works fine** meanwhile: the
shell resolves PATH its own way and the editor resolves it its own way. So the project
compiles perfectly by hand while the editor goes on insisting it cannot. And as long as the
manual build landed on the same file name the editor expected, the breakage was invisible —
it only surfaced when the assembly was renamed to `DealOrForce` and the old DLL stopped
fitting.

**The cure is `Tools\fix-dotnet-path.cmd`.** Double-click it, let Windows ask for
administrator rights once, and the script raises `C:\Program Files\dotnet` to the front of
the system PATH and saves the old PATH to a file in your home folder. Only the order
changes: nothing is installed and nothing is removed, and the x86 entry stays on PATH for
whoever needs it — it simply stops being asked first. **After that Godot has to be closed
completely and opened again:** a program reads environment variables once, at start.

And there is a trap here worth a paragraph, because it looks like "the fix did not work".
Writing to the registry is not enough: running programs do not hear about it.
`explorer.exe` reads the environment when it starts and hands a copy to every program you
launch from it — and if Explorer has been running since last week, that copy is a week old.
Closing Godot and opening it again from Explorer means getting the **old** PATH. So the
script broadcasts `WM_SETTINGCHANGE`: the message that asks Explorer to re-read the
environment. If the editor still will not build after that, Explorer did not take the hint,
and what is left is restarting it (`Stop-Process -Name explorer -Force`; the taskbar comes
back by itself) or rebooting. `Tools\godot.cmd` works either way: it depends on none of this.

Administrator rights are not belt-and-braces here. The system PATH is a machine setting and
its order cannot be worked around with user variables: `DOTNET_HOST_PATH` and `DOTNET_ROOT`
were both tried, neither changes anything — MSBuildLocator inside the editor still walks PATH
and still finds the empty one first.

**The workaround from before there was a fix.** Start the editor through `Tools\godot.cmd` —
it fixes PATH for its own process only and touches nothing else.

## What the script checks, and will almost certainly save you from

The script makes three checks: that the `.exe` appeared, that there is a managed
`DealOrForce.dll` in the folder, and that the data arrived.

Nearly everything the game reads while running — the whole economy, the whole map, every
scenario, the localisation, the mods — lives in `game/` and is **read by file path through
`System.IO`**, not through `res://`.

In the editor that works because the working directory is the project itself. In a built
game, `res://` lives inside the `.pck`, where `System.IO` cannot look, so the data has to
arrive as **real files**. And it does: `DealOrForce.csproj` puts it in the published output
with `<Content Include="game\**" … CopyToPublishDirectory="PreserveNewest" />`, Godot moves
that into the `data_DealOrForce_windows_x86_64` folder beside the `.exe`, and the loader
finds it by walking up from its own assembly.

But if that ever stops being true, the symptom will be this: the game starts, complains
about nothing, and shows an empty world — **on somebody else's computer**. So the script
refuses to hand over a folder with data missing, and names what is missing.

## What to tell the people you give it to

- The archive unpacks into **one folder, `Deal or Force`**; the game is the `.exe` inside it.
  Run it from there: the game reads its world from the folders beside it, and an `.exe`
  pulled out on its own will have nothing.
- Saves, settings and logs are written to
  `%APPDATA%\Godot\app_userdata\Deal or Force\` — not into the game folder.
- If something crashes, `logs\errors.log` is in that same place. An empty file means the game
  saw no errors; if there is something in it, that is the most useful thing a tester can
  send.
- `~` opens the in-game console, `F3` toggles map smoothing, `F2` switches between the globe
  and the flat map.
- Mods go in the **`mods` folder beside the `.exe`** — one folder per mod with a
  `manifest.json` inside. `mods\_templates` is there too: six worked examples, from
  rebalancing to redrawing borders. The `user://mods` folder (that is,
  `%APPDATA%\Godot\app_userdata\Deal or Force\mods`) works as well and takes priority.

### How to give testers a content pack

`Iron and Grain` ships in the build but is inactive: the game considers it unowned and writes
`Content pack Iron and Grain present but not active (not owned)` to the log. There is no shop
yet, and its role is played by the file
`data_DealOrForce_windows_x86_64\dlc\entitlements.json`. To switch the pack on, one line in
it is enough:

    "unlock_everything": true

After that the log says `Content pack Iron and Grain 1.0.0 active`. In a real build this
should stay `false` — that is what the switch is for.

## What this build does not have yet

- **An icon.** `application/icon` in `export_presets.cfg` is empty, so the `.exe` will carry
  Godot's icon. Fine for a test, not fine for a shop window.
- **A signature.** Windows SmartScreen will warn about an unsigned `.exe` from the internet,
  and the tester will have to press "More info → Run anyway". Warn them in advance or half of
  them simply will not start it.
- **Size.** The map rasters currently land in the build twice: Godot imports all 319 pngs into
  the `.pck`, and the same files are placed beside it as ordinary files, because some of the
  code reads them directly. For a test that is a few dozen wasted megabytes and nothing more;
  they can be separated with export filters, but that is worth doing with a working build in
  hand rather than before one.
- **Windows only.** There is one preset. Linux is a second preset and the same templates.
- **Editor add-ons ship in the build.** The startup log shows
  `Error loading GDExtension … ziva_agent.gdextension` — an editor extension with no business
  being in the game. Not fatal, but it is extra weight and extra red lines in somebody else's
  log; an exclusion filter in the preset clears it.

## Size

The folder is 359 MB and the archive 232 MB. Half of that is the double packing of the
rasters: Godot imports 319 pngs into the `.pck`, and the same files sit beside it as ordinary
files because some of the code reads them directly. Among friends, for a test, that is
tolerable; for wider distribution it is worth separating them — but carefully, and with a
working build in hand, because some of the images are read through `res://` and some through
the filesystem, and confusing the two produces exactly the breakage that stopped the first
build from starting.
