$enDir = "D:\Godot Projects\deal-or-force-godot-4-8\game\localization\english"
$ruDir = "D:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian"
$enFiles = Get-ChildItem $enDir -Filter *.json | Select-Object -ExpandProperty Name
$missingFiles = @()
foreach ($f in $enFiles) {
    if ($f -eq '.kilo') { continue }
    $ruName = $f -replace '^00_english', '00_russian'
    $ruPath = Join-Path $ruDir $ruName
    if (-not (Test-Path $ruPath)) {
        if (-not (Test-Path (Join-Path $ruDir $f))) {
            $missingFiles += $f
        }
    }
}
Write-Host "=== Missing Russian localization files ==="
$missingFiles | Sort-Object | ForEach-Object { Write-Host "  $_" }
if ($missingFiles.Count -eq 0) { Write-Host "  (none)" }

Write-Host ""
Write-Host "=== Key coverage: English keys missing from Russian (top files) ==="
foreach ($f in $enFiles) {
    if ($f -eq '.kilo') { continue }
    $ruName = $f -replace '^00_english', '00_russian'
    $ruPath = Join-Path $ruDir $ruName
    if (-not (Test-Path $ruPath)) {
        $ruPath = Join-Path $ruDir $f
        if (-not (Test-Path $ruPath)) { continue }
    }
    $enJson = Get-Content (Join-Path $enDir $f) -Raw | ConvertFrom-Json
    $ruJson = Get-Content $ruPath -Raw | ConvertFrom-Json
    $enKeys = @($enJson.PSObject.Properties.Name)
    $ruKeys = @($ruJson.PSObject.Properties.Name)
    $missingKeys = $enKeys | Where-Object { $_ -notin $ruKeys -and $_ -ne 'language.name' -and $_ -ne 'language.culture' }
    if ($missingKeys.Count -gt 0) {
        Write-Host "  $f : $($missingKeys.Count) missing keys"
        $missingKeys | Select-Object -First 5 | ForEach-Object { Write-Host "    - $_" }
    }
}
Write-Host "Done."
