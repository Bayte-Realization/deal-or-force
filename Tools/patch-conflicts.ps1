$conflicts = @{
    "Сделка предложена" = @("136_deal_composer.json", "160_deal_negotiations.json")
    "Военный заговор" = @("143_deal_and_crisis_workspace.json", "161_situation_room.json")
    "Непрерывное критическое давление" = @("143_deal_and_crisis_workspace.json", "161_situation_room.json")
}

foreach ($key in $conflicts.Keys) {
    foreach ($file in $conflicts[$key]) {
        $path = "d:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian\$file"
        if (Test-Path $path) {
            $json = Get-Content $path -Raw | ConvertFrom-Json
            # Применяем эталонное значение
            $json.$key = $key
            $json | ConvertTo-Json -Depth 10 | Set-Content $path
            Write-Host "Patching $file to value: $key"
        }
    }
}
