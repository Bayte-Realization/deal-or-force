param([string]$GameRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
$project = Join-Path $GameRoot 'DealOrForce.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw 'Pass the game repository root, not an installed standalone kit.' }
$output = @(& dotnet msbuild $project -nologo '-getItem:Compile,Content,None') -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate the real game project.' }
$items = ($output | ConvertFrom-Json).Items
foreach ($kind in @('Compile','Content','None')) {
    $leaked = @($items.$kind | Where-Object { $_.Identity -match '(^|[\\/])LanguageKit[\\/]' })
    if ($leaked.Count) { throw "LanguageKit leaked into $kind`: $($leaked[0].Identity)" }
}
if (-not @($items.Compile | Where-Object { $_.Identity -match 'Scripts[\\/]DealOrForceGame.cs$' }).Count) {
    throw 'The game itself was excluded along with the examples.'
}
Write-Host 'GAME_KIT_ISOLATION_PASS compile=true content=true none=true real_game_sources=true'
