param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$LuaAddonDirectory,
    [Parameter(Mandatory)][string]$LuauAddonDirectory,
    [Parameter(Mandatory)][string]$ProjectDirectory
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath (Join-Path $ProjectDirectory 'project.godot')) { throw 'Choose a fresh isolated project directory.' }
& (Join-Path $PSScriptRoot 'build.ps1') -GodotExecutable $GodotExecutable -OutputDirectory $ProjectDirectory
$addons = Join-Path $ProjectDirectory 'addons'
New-Item -ItemType Directory -Path $addons -Force | Out-Null
Copy-Item -LiteralPath $LuaAddonDirectory -Destination (Join-Path $addons 'dof_lua') -Recurse
Copy-Item -LiteralPath $LuauAddonDirectory -Destination (Join-Path $addons 'dof_luau') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../lua/probe/probe.lua') -Destination (Join-Path $ProjectDirectory 'lua_probe.lua')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../luau/probe/probe.luau') -Destination (Join-Path $ProjectDirectory 'luau_probe.luau')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../luau/probe/values.mod.luau') -Destination $ProjectDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'coexistence.gd') -Destination $ProjectDirectory
$output = @(& $GodotExecutable --headless --path $ProjectDirectory --script coexistence.gd --quit-after 120 2>&1)
$code = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($code -ne 0 -or ($output -join "`n") -notmatch 'LANGUAGE_KIT_PASS combined=lua,luau,fsharp' -or ($output -join "`n") -match '(?m)^(SCRIPT ERROR:|ERROR:)') {
    throw "Combined Lua / Luau / F# runtime probe failed (exit $code)."
}
