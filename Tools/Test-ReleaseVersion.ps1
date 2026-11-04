param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$version = ([xml](Get-Content -LiteralPath (Join-Path $ProjectRoot 'GameVersion.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Expected a numeric game release version.' }
$expected = [regex]::Escape($version)
$checks = @{
    'Scripts/Simulation/GameBuildInfo.cs' = 'public const string Version = "' + $expected + '";'
    'Scripts/Simulation/SaveSystem.cs' = 'public const string GameVersion = GameBuildInfo\.Version;'
    'project.godot' = 'config/version="' + $expected + '"'
    'export_presets.cfg' = 'application/file_version="' + $expected + '"'
    'README.md' = 'Version \*\*' + $expected + '\*\*'
    'README.ru.md' = 'Версия \*\*' + $expected + '\*\*'
    'docs/BUILDING.md' = 'Current game release: \*\*EntertAlpha ' + $expected + '\*\*'
    'docs/BUILDING_RU.md' = 'Актуальный выпуск игры: \*\*EntertAlpha ' + $expected + '\*\*'
}
foreach ($file in $checks.Keys | Sort-Object) {
    if ([IO.File]::ReadAllText((Join-Path $ProjectRoot $file)) -notmatch $checks[$file]) {
        throw "Release version differs from $version in $file"
    }
    Write-Host "PASS release version $version in $file"
}
if ([IO.File]::ReadAllText((Join-Path $ProjectRoot 'export_presets.cfg')) -notmatch ('application/product_version="' + $expected + '"')) {
    throw 'Windows product version differs from the release version.'
}
Write-Host 'PASS Windows product version'
# Task Manager uses the native executable description, not the window title.
# Guard the export inputs too: a correct config/name alone cannot fix that label.
$exportPreset = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'export_presets.cfg'))
foreach ($key in @('product_name', 'file_description')) {
    $values = [regex]::Matches($exportPreset, '(?m)^application/' + $key + '="([^"]*)"\s*$')
    if ($values.Count -eq 0 -or @($values | Where-Object { $_.Groups[1].Value -cne 'Deal or Force' }).Count -gt 0) {
        throw "Windows export $key must be exactly Deal or Force."
    }
    Write-Host "PASS Windows export $key is Deal or Force"
}
foreach ($file in @('DealOrForce.csproj', 'Scripts/Rules/DealOrForce.Rules.fsproj', 'Tools/Launcher/DealOrForce.Launcher.csproj')) {
    if ([IO.File]::ReadAllText((Join-Path $ProjectRoot $file)) -notmatch '<Import Project="(\.\./)*GameVersion\.props"\s*/>') {
        throw "Shared assembly version is not imported by $file"
    }
    Write-Host "PASS shared assembly version in $file"
}
