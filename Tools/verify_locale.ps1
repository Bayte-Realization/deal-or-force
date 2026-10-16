$path1 = "D:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian\99d_policy_index.json"
$path2 = "D:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian\99_world_polish.json"
foreach ($p in @($path1, $path2)) {
    try {
        $content = Get-Content $p -Raw -Encoding UTF8
        $json = $content | ConvertFrom-Json
        $count = $json.PSObject.Properties.Count
        Write-Host "${p}: VALID, keys: $count"
    } catch {
        Write-Host "${p}: INVALID - $($_.Exception.Message)"
    }
}