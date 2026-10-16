$path = "D:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian\99_world_polish.json"
$content = Get-Content $path -Raw -Encoding UTF8
try {
    $json = $content | ConvertFrom-Json
    Write-Host "JSON VALID"
    $props = $json.PSObject.Properties
    Write-Host "Total keys: $($props.Count)"
} catch {
    Write-Host "INVALID: $($_.Exception.Message)"
}