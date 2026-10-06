# F# corecode example

Disabled by default. This Godot 4.8 branch targets .NET 10, matching the host game. Copy this directory to `mods/example_fsharp_mod` (not inside `_templates`), build the project, then enable its manifest.

From this repository:

```powershell
dotnet build mods/_templates/fsharp_mod/corecode/ExampleFSharpMod.fsproj -c Debug
dotnet run --project Tools/ManagedModTests/ManagedModTests.csproj -c Release
```

After copying, adjust the project's game reference to your actual `DealOrForce.csproj` or reference the matching exported `DealOrForce.dll`. The manifest must point to the same build configuration you compiled. Do not copy the game's own DLL into a replacement engine folder.

F# uses the existing language-neutral `IGlobalityMod` interface, not a second simulation or a Godot engine fork. The example only logs lifecycle hooks; it never changes campaign balance. Compiled mods run trusted .NET code with the game's permissions; this is **not a sandbox**.

`CopyLocalLockFileAssemblies` places F# dependencies beside the compiled mod. The loader uses a private ManagedModLoadContext with AssemblyDependencyResolver and adjacent DLL lookup. Test dependency deployment against the matching exported host; arbitrary native dependencies are not automatically portable.

The shared context also provides `EnactPolicy(tag, id, targetTag, region, promise)`, `ManageDebt(tag, action, amount)` and `AddCourseShock(tag, strength, reason)`. True means queued, not guaranteed execution; prices, cooldowns and territory are checked again when commands apply. Never retry a borrowing request blindly.
