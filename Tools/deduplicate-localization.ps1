# Tools/deduplicate-localization.ps1
# Removes duplicate keys from secondary localization files if they already exist in 00_russian.json

$baseFile = "d:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian\00_russian.json"
$locDir = "d:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian"

Write-Host "Reading base keys from $baseFile..."
$baseContent = Get-Content -Path $baseFile -Raw -Encoding UTF8 | ConvertFrom-Json
$baseKeys = $baseContent.PSObject.Properties.Name

Write-Host "Found $($baseKeys.Count) base keys."

$otherFiles = Get-ChildItem -Path $locDir -Filter "*.json" | Where-Object { $_.FullName -ne $baseFile }

$totalRemoved = 0

foreach ($file in $otherFiles) {
    $content = Get-Content -Path $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $modified = $false
    $currentKeys = @($content.PSObject.Properties.Name)

    foreach ($key in $currentKeys) {
        if ($baseKeys -contains $key) {
            $content.PSObject.Properties.Remove($key)
            $modified = $true
            $totalRemoved++
        }
    }

    if ($modified) {
        Write-Host "Cleaning duplicates in $($file.Name)..."
        $newJson = $content | ConvertTo-Json -Depth 10
        # Re-save with UTF8 without BOM
        [System.IO.File]::WriteAllText($file.FullName, $newJson, [System.Text.Encoding]::UTF8)
    }
}

Write-Host "Done! Removed $totalRemoved duplicate keys across all files."
