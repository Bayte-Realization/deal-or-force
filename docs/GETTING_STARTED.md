[English](GETTING_STARTED.md) · [Русский](GETTING_STARTED_RU.md)

# Running and building Deal or Force

Everything needed to get from a fresh clone to a running game, and from a running game to an
executable somebody else can double-click. Nothing here assumes you have worked on this
project before.

If something in here turns out to be wrong, it is a bug in this file — say so.

---

## 1. What you need

| | version | why |
|---|---|---|
| **Godot** | **4.7.2 · .NET / Mono build** | the engine. The plain build will not do: this project is C#, and the two downloads are different programs. |
| **.NET SDK** | **8.0** | the compiler. `dotnet --version` should print `8.` something. |
| **Git** | any | to clone it. |

Godot: <https://godotengine.org/download> — take the one labelled **.NET**.
.NET 8 SDK: <https://dotnet.microsoft.com/download/dotnet/8.0> — the **SDK**, not the runtime.

The version has to match exactly, including the word `mono`. `Godot.NET.Sdk/4.7.2` is written
into `DealOrForce.csproj`, so a 4.6 or a 4.8 editor will complain, and a non-.NET editor will
open the project and then find no scripts at all.

Windows is the only platform with a build script. The game itself has nothing
Windows-specific in it and the editor runs the project on Linux and macOS as well.

---

## 2. Clone and compile

```bash
git clone https://github.com/Bayte-Realization/deal-or-force.git
cd deal-or-force
dotnet build DealOrForce.csproj
```

That last line should end with `Build succeeded` and no warnings you did not put there.
It compiles the game to `.godot/mono/temp/bin/Debug/` — Godot loads it from there.

**If `dotnet` is not found**, the SDK is installed but not on your `PATH`. On Windows the
usual place is `C:\Program Files\dotnet\dotnet.exe`, and `Tools\fix-dotnet-path.cmd` in this
repository puts it on the path for you.

---

## 3. Run it

Open the folder in Godot (**Import** → pick `project.godot`) and press **F5**, or:

```bash
"C:/GodotEngineNet/Godot_v4.7.2-stable_mono_win64.exe" --path .
```

The game opens on `Scenes/MainMenu.tscn` — the menu is a scene of its own and comes up
immediately. The world is built when you press **Singleplayer** or **Load latest campaign**,
which takes ten to thirty seconds depending on the machine: it is reading 37 909 provinces
out of a raster and working out who borders whom.

### After a fresh clone, two things will complain

**`addons/` is missing.** Those are third-party editor plugins — about 1.6 GB, not in the
repository, and the game does not reference them anywhere. But `project.godot` still
registers two autoloads that belong to them (`fennara`, `godot_ai`), so Godot reports them
missing on first open. Remove both in **Project → Project Settings → Autoload**. The game
does not care.

**`export_presets.cfg` is missing.** It is gitignored, by Godot's own convention, because
export presets often carry machine paths and signing keys. You need it only to build an
executable — see §5, which tells you how to make one.

---

## 4. Check it still works

The simulation compiles and runs **without the engine**, which is what makes it testable.
`Tools/SimBench` plays a campaign headless and checks it against about a hundred invariants:
that the economy adds up, that two identical worlds stay identical, that a front has not
quietly stopped being contested, that every string the interface shows is in the
localisation table.

```bash
dotnet run -c Release --project Tools/SimBench/SimBench.csproj -- 730
```

The argument is how many steps to run. It prints a line per check and a count of failures at
the end; a run of 730 takes a few minutes. **Run it before trusting a change** — it is the
only thing that catches "the front stopped moving" three months into a campaign.

Three smaller harnesses check narrower things and run in seconds:

```bash
dotnet run --project Tools/FormableTests          # scenarios, flags, the editor's writes
dotnet run --project Tools/ArmyInteractionTests   # orders, fronts, offensive plans
dotnet run --project Tools/TerrainTests           # the terrain catalogue and its rasters
```

> **Release, not Debug.** The tools are built in Release and the editor runs Debug. They are
> separate binaries in separate folders; building one does not rebuild the other, and a tool
> that seems not to have picked up your change has usually just not been rebuilt.

---

## 5. Build something other people can run

Godot can run this project all day without any preparation, but turning it into a standalone
`.exe` needs **export templates** — a separate download of about a gigabyte, and they are not
installed by default.

In the editor: **Editor → Manage Export Templates… → Download and Install**. The version has
to match exactly, `mono` included: for this engine that is `4.7.2.stable.mono`. Templates
from the plain build will not work with the .NET build, and the error when they do not is
not helpful.

Then:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\build.ps1
```

It exports to `build\windows\`, verifies what it produced, and leaves
`build\Deal or Force <date>.zip` beside it. Flags: `-Debug` for a build with a console and
readable stack traces, `-NoZip` to skip the archive, `-Godot <path>` if the engine moved.

`build/` is gitignored on purpose. Builds do not belong in the repository.

The full account, including what to do when the preset is missing, is in
[BUILDING.md](BUILDING.md).

---

## 6. Where things are

```
Scripts/Simulation/   the game. No `using Godot` anywhere in here - that is enforced by
                      Tools/SimBench, which compiles this folder on its own.
Scripts/ui/           the interface, built in code, one screen per file.
Scripts/              the map, the globe, the shell that holds the screens together.
game/                 everything a mod can change: common/, events/, localization/,
                      interface/ (the palette), gui/ (where things sit), gfx/, map_data/.
Scenes/               two scenes - MainMenu.tscn and Main.tscn.
Tools/                the headless harnesses and the build script.
mods/_templates/      a starting point per kind of mod. Copy one out of _templates.
dlc/                  content packs, including the test pack.
docs/                 this.
```

The rule the project is built on: **anything a designer would want to change is data.**
Colours, layouts, goods, buildings, budget lines, trade rules, the monetary model, the length
of a simulation step, every string in the game. If you find yourself editing C# to change a
number, that is usually a bug in the architecture rather than in your approach.

---

## 7. When something is wrong

| what you see | what it is |
|---|---|
| Godot opens and there are no scripts | the plain build, not the .NET one |
| `Godot.NET.Sdk/4.7.2` not found | the .NET 8 SDK is missing, or `dotnet` is not on the path |
| the map is missing, or every province is black | the editor is running Debug and the tools built Release, or the other way round — rebuild the one you are using |
| autoloads `fennara` / `godot_ai` missing | see §3; remove them |
| an export fails on templates | see §5; the version has to match exactly, `mono` included |
| a change to `game/**.json` does nothing | the file is read in name order and a later file wins — check nothing after it declares the same key |

Logs live beside the saves, in
`Documents\Deal or Force\`, and what is written there is chosen in **Settings → Diagnostics**.
[DIAGNOSTICS_RU.md](DIAGNOSTICS_RU.md) says what each channel records.
