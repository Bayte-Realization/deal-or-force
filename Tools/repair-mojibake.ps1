$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$cp1251 = [System.Text.Encoding]::GetEncoding(1251)

# Characters that UTF-8-as-cp1251 mojibake produces and that clean Russian prose does not
# use: the tail of one encoded letter seen as another. A string with two or more of them is
# mojibake; a normal Russian sentence never has them.
$rareCodes = @(
    0x0402, 0x0403, 0x0409, 0x040A, 0x040B, 0x040C, 0x040F, 0x040E,
    0x0453, 0x0459, 0x045A, 0x045B, 0x045C, 0x045F,
    0x00B5, 0x201A, 0x201E, 0x2026, 0x2020, 0x2021, 0x20AC, 0x2030,
    0x2039, 0x203A, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2122
)

function Rare-Count([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return 0 }
    $count = 0
    foreach ($code in $rareCodes) {
        $count += ([regex]::Matches($text, [regex]::Escape([string][char]$code))).Count
    }
    return $count
}

function Repair-Text([string]$text) {
    $script:repaired = 0
    $result = [regex]::Replace($text, '"([^"\\]|\\.)*"', {
        param($m)
        $token = $m.Value
        $before = Rare-Count $token
        if ($before -lt 2) { return $token }
        try {
            $bytes = $cp1251.GetBytes($token)
            $decoded = [System.Text.Encoding]::UTF8.GetString($bytes)
        } catch { return $token }
        if ($decoded.Contains([char]0xFFFD)) { return $token }
        if ((Rare-Count $decoded) -ne 0) { return $token }
        $script:repaired++
        return $decoded
    })
    return @{ Text = $result; Count = $script:repaired }
}

foreach ($file in Get-ChildItem 'game\localization\russian\*.json') {
    $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    if ((Rare-Count $text) -lt 2) { continue }
    $repair = Repair-Text $text
    if ($repair.Count -eq 0) { continue }
    try { $null = $repair.Text | ConvertFrom-Json } catch {
        Write-Host ("SKIP " + $file.Name + " : json would break: " + $_.Exception.Message)
        continue
    }
    [System.IO.File]::WriteAllText($file.FullName, $repair.Text, $utf8)
    Write-Host ("FIXED " + $file.Name + " : " + $repair.Count + " strings")
}
