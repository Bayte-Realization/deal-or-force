# F# corecode example

Disabled by default. Copy this directory to `mods/example_fsharp_mod` (not inside `_templates`), build the project, then enable its manifest.

From this repository:

```powershell
dotnet build mods/_templates/fsharp_mod/corecode/ExampleFSharpMod.fsproj -c Debug
dotnet run --project Tools/ManagedModTests/ManagedModTests.csproj -c Release
```

After copying, adjust the project's game reference to your actual `DealOrForce.csproj` or reference the matching exported `DealOrForce.dll`. The manifest must point to the same build configuration you compiled. Do not copy the game's own DLL into a replacement engine folder.

F# uses the existing language-neutral `IGlobalityMod` interface, not a second simulation or a Godot engine fork. The example only logs lifecycle hooks; it never changes campaign balance. Compiled mods run trusted .NET code with the game's permissions; this is **not a sandbox**.

`CopyLocalLockFileAssemblies` places F# dependencies beside the compiled mod. The current loader does not provide a general per-mod dependency resolver: advanced plugins using FSharp.Core/custom dependencies require deployment/resolution work. The minimal example and shared contract are tested by `ManagedModTests`; a full F# plugin with arbitrary dependencies is not promised to load automatically.
