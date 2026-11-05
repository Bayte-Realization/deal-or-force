$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PlayerNotices.ps1')
$project = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $project ('build/.notice-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
function Assert-Notice([bool]$Condition, [string]$Caption) {
    if (-not $Condition) { throw $Caption }
    Write-Host "PASS $Caption"
}
$sourceHash = (Get-FileHash (Join-Path $project 'README.md')).Hash
foreach ($notice in @('README.md', 'README.ru.md', 'LICENSE-EXCEPTIONS.md')) {
    Copy-Item -LiteralPath (Join-Path $project $notice) -Destination $testRoot
}
Write-PlayerNotices $project $testRoot
$path = Join-Path $testRoot 'THIRD_PARTY_NOTICES.md'
$actual = [IO.File]::ReadAllText($path)
Assert-Notice ($actual.Contains('© ESA 2010 and UCLouvain')) 'map attribution retained'
Assert-Notice ($actual.Contains('Keep the original credits and licence links')) 'flag attribution instructions retained'
Assert-Notice ($actual.Contains('[game/gfx/CREDITS.md](game/gfx/CREDITS.md)')) 'register links resolve from build root'
Assert-Notice ($actual.Contains([IO.File]::ReadAllText((Join-Path $project 'THIRD_PARTY_NOTICES.md')))) 'reviewed player register retained in full'
Assert-Notice (-not $actual.Contains('Roman restoration flags') -and -not $actual.Contains('#original-artwork')) 'internal art briefs and broken anchors are not appended'
Assert-Notice ($actual.Contains('Blue Marble') -and $actual.Contains('Natural Earth') -and $actual.Contains('Godot is MIT-licensed')) 'remaining third-party notices retained'
Assert-Notice (-not (Test-Path (Join-Path $testRoot 'licenses'))) 'no licenses directory generated'
Assert-Notice (-not (Test-Path (Join-Path $testRoot 'docs'))) 'no docs directory generated'
Assert-Notice (-not $actual.Contains('## godot-luau-script')) 'native adapter notices omitted when adapter absent'
foreach ($notice in @('README.md', 'README.ru.md', 'LICENSE-EXCEPTIONS.md')) {
    $copy = [IO.File]::ReadAllText((Join-Path $testRoot $notice))
    Assert-Notice ($copy.Contains('THIRD_PARTY_NOTICES.md') -and -not $copy.Contains('docs/THIRD_PARTY_MEDIA.md')) ("build notice link: " + $notice)
}
$firstHash = (Get-FileHash $path).Hash
Write-PlayerNotices $project $testRoot
Assert-Notice ((Get-FileHash $path).Hash -eq $firstHash) 'notices generated deterministically'
New-Item -ItemType File -Path (Join-Path $testRoot 'libluau-script.windows.editor.x86_64.dll') | Out-Null
Write-PlayerNotices $project $testRoot
$actual = [IO.File]::ReadAllText($path)
foreach ($notice in @('LICENSE.txt', 'COPYRIGHT.txt')) {
    Assert-Notice ($actual.Contains([IO.File]::ReadAllText((Join-Path $project "game/extensions/luau/$notice")))) ("opt-in native notice retained: " + $notice)
}
Assert-Notice ((Get-FileHash (Join-Path $project 'README.md')).Hash -eq $sourceHash) 'source README unchanged'
Write-Host "Notice packaging checks complete; isolated fixtures: $testRoot"
