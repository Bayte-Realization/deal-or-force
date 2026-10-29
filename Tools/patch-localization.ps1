$csv = Import-Csv "tmp_logs/localization-audit.csv"
foreach ($row in $csv) {
    $path = Join-Path "game/localization/russian/" $row.Second
    if (Test-Path $path) {
        $txt = [IO.File]::ReadAllText($path)
        $escapedKey = [regex]::Escape($row.Key)
        $escapedOld = [regex]::Escape($row.SecondValue)
        $newVal = $row.FirstValue -replace '\\', '\\\\' -replace '"', '\"'
        $pattern = '"' + $escapedKey + '"\s*:\s*"' + $escapedOld + '"'
        $new = '"' + $row.Key + '": "' + $newVal + '"'
        $txt = $txt -replace $pattern, $new
        [IO.File]::WriteAllText($path, $txt)
        Write-Host "Patched $path"
    }
}
