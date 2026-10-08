# Native map regressions

These tests exercise the actual Godot scene and C# renderer, not a substitute
geometry model. Build the game first and run from the project directory with
the same **Godot 4.8 .NET** executable used for the project:

```powershell
dotnet build DealOrForce.csproj
& 'C:\GodotEngineNet 4.8-dev\Godot_v4.8-dev7_mono_win64_console.exe' --headless --path . --script Tools/GraphicsTests/capital_surface.gd
```

For rendered evidence, omit `--headless` and add a unique proof label:

```powershell
& 'C:\GodotEngineNet 4.8-dev\Godot_v4.8-dev7_mono_win64_console.exe' --path . --rendering-driver d3d12 --script Tools/GraphicsTests/capital_surface.gd -- capital-review
```

The test starts its own scene, selects Germany and checks near/wide globe,
flat map, then the return to the original globe location. It verifies stable
capital node identity, map-tangent orientation, absence of billboarding and
bounded projected size. Images are saved below
`.fennara/capital-surface-proof/<proof-label>/`; the test exits on completion.
Do not run several native graphical probes concurrently or against a user's
manually running game.

Success requires both process exit code `0` and
`CAPITAL_SURFACE_COMPLETE failures=0`. `CAPITAL_PIXELS` records measured
projected quad dimensions, not an FPS benchmark. Review the rendered captures
as well as the numerical checks when changing appearance.

Editor/network addons may emit diagnostics during SceneTree bootstrap because
the temporary scene is installed after autoloads. Those messages are not a
substitute for the test's explicit failure counter and should be investigated
separately; this test does not claim that every addon or shutdown is error-free.
