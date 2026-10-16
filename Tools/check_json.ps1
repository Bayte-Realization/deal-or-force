$path = "D:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian\99_world_polish.json"
$content = Get-Content $path -Raw -Encoding UTF8
try {
    $json = $content | ConvertFrom-Json
    Write-Host "JSON is VALID"
    $count = $json.PSObject.Properties.Count
    Write-Host "Keys: $count"
} catch {
    Write-Host "ERROR: $($_.Exception.Message)"
}