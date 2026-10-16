# F# through Godot's .NET runtime

This is a managed library integration, **not** an additional `ScriptLanguage`.
F# files do not appear as attachable `.fs` Node scripts in Godot. C# owns Godot
source generation, exported properties and signals; pure F# domain code is called
through ordinary strongly typed .NET references. No native engine fork is needed.

Tested: official Godot `4.8.beta1.mono.official.4d10419ca`, Windows x86_64,
.NET SDK 10.0.402, `net10.0`, Godot.NET.Sdk `4.8.0-beta.1`.

```powershell
./build.ps1 -GodotExecutable 'C:/GodotEngineNet 4.8-Beta1/Godot_v4.8-beta1_mono_win64_console.exe' -OutputDirectory C:/Temp/fsharp-probe
./probe.ps1 -GodotExecutable 'C:/GodotEngineNet 4.8-Beta1/Godot_v4.8-beta1_mono_win64_console.exe' -ProjectDirectory C:/Temp/fsharp-probe
```

The template is reusable by copying `FSharpLogic` to your project and adding its
`ProjectReference` to your Godot C# project. Keep generated outputs out of version
control. For different Godot/.NET versions, adjust the SDK and target frameworks
together, then repeat the runtime check. The installer deliberately refuses an
unverified engine version instead of silently claiming compatibility.

The probe calls an integer weighted kernel from GDScript through a Godot C# bridge,
checks two separate F# counter instances and validates rejected input. Its process
must finish normally, not merely print a success line.

For editor-import checks with native addons, use `editor-probe.ps1` on the isolated
project. Godot 4.8 beta1 still exhibits the upstream
[cold GDExtension documentation shutdown race](https://github.com/godotengine/godot/issues/111048).
An immediate cold `--import` can crash in engine documentation cleanup even when
runtime and normal editor operation work. The script first allows deferred editor
callbacks to finish, then checks ordinary warm import. This is a bounded automation
workaround, not an engine fix, and it never retries a failed import as if it passed.

Use F# for typed economic formulas, rules, validation and offline data conversion.
Do not rewrite hot loops merely for the syntax: C# and F# share the .NET runtime,
and allocation patterns/batching matter more than language labels. Keep
authoritative state, save schema and deterministic arithmetic in one shared model.

Official references: [Godot C# basics](https://docs.godotengine.org/en/latest/tutorials/scripting/c_sharp/c_sharp_basics.html),
[F# .NET library tutorial](https://learn.microsoft.com/en-us/dotnet/fsharp/get-started/get-started-command-line).
