param(
    # Copy the built addon into game/extensions/luau and write the active descriptor.
    # Without it the addon is only built into build/luau-addon-<stamp> for inspection.
    [switch]$Activate,
    [ValidateRange(1, 32)][int]$Jobs = 8,
    [string]$GodotExecutable = 'C:/GodotEngineNet 4.8-Beta3/Godot_v4.8-beta3_mono_win64_console.exe',
    [string]$PythonExecutable = 'python'
)
# Builds the game's Luau ScriptLanguageExtension with the LanguageKit recipe
# (LanguageKit/adapters/luau/build.ps1): bindings from the API of the engine given here,
# both .lua and .luau (the game ships no other Lua), an editor and a template_release DLL.
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$kitBuild = Join-Path $repository 'LanguageKit/adapters/luau/build.ps1'
$legacySource = Join-Path $repository 'build/godot-luau-script'
$sourceRoot = Join-Path $repository 'build/godot-luau-script-beta2'
$installRoot = Join-Path $repository 'game/extensions/luau'
$sourceCommit = '7942ce6def8023596ca0cb8d819b032eb40a4bce'
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Required tool missing: git' }
if (-not (Test-Path -LiteralPath $sourceRoot)) {
    # A separate checkout: the 4.4-era build/godot-luau-script stays as it was.
    & git -c core.longpaths=true clone --no-checkout https://github.com/Fumohouse/godot-luau-script.git $sourceRoot
    if ($LASTEXITCODE) { throw 'Source clone failed' }
    & git -C $sourceRoot checkout --detach $sourceCommit
    if ($LASTEXITCODE) { throw 'Pinned source checkout failed' }
}
$output = Join-Path $repository ('build/luau-addon-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
& $kitBuild -GodotExecutable $GodotExecutable -SourceDirectory $sourceRoot -OutputDirectory $output `
    -Suffixes lua-and-luau -Targets editor, template_release -PythonExecutable $PythonExecutable -Jobs ([Math]::Min($Jobs, 16))
if (-not $Activate) {
    Write-Host "Built into $output. Nothing in game/ changed; rerun with -Activate (Godot closed) to install."
    return
}
# A running Godot holds the library (and its ~lib copy); never replace it underneath.
$existing = Get-ChildItem -LiteralPath $installRoot -Filter 'libluau-script*.dll' -ErrorAction SilentlyContinue
foreach ($dll in $existing) {
    try { $handle = [IO.File]::Open($dll.FullName, 'Open', 'ReadWrite', 'None'); $handle.Dispose() }
    catch { throw "$($dll.Name) is in use. Close every Godot that has this project open, then rerun." }
}
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
foreach ($name in @('libluau-script.windows.editor.x86_64.dll', 'libluau-script.windows.template_release.x86_64.dll')) {
    Copy-Item -LiteralPath (Join-Path $output "bin/$name") -Destination (Join-Path $installRoot $name) -Force
}
foreach ($name in @('LICENSE.txt', 'COPYRIGHT.txt', 'build-info.json')) {
    Copy-Item -LiteralPath (Join-Path $output $name) -Destination (Join-Path $installRoot $name) -Force
}
$descriptor = @'
[configuration]
entry_symbol = "luau_script_init"
compatibility_minimum = "4.8"
reloadable = false

[libraries]
windows.debug.x86_64 = "res://game/extensions/luau/libluau-script.windows.editor.x86_64.dll"
windows.release.x86_64 = "res://game/extensions/luau/libluau-script.windows.template_release.x86_64.dll"
'@
[IO.File]::WriteAllText((Join-Path $installRoot 'luau_script.gdextension'), $descriptor, [Text.UTF8Encoding]::new($false))
Write-Host 'Luau adapter installed (.lua and .luau, editor + template_release). Restart Godot to load it.'
Get-ChildItem -LiteralPath $installRoot -Filter 'libluau-script*.dll' | Get-FileHash -Algorithm SHA256
