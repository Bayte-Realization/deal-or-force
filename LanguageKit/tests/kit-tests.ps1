param([string]$KitRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. (Join-Path $KitRoot 'kit-common.ps1')
$script:Checks = 0
function Check([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "FAIL $Label" }
    $script:Checks++; Write-Host "PASS $Label"
}
function Reject([scriptblock]$Action, [string]$Label) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected $Label
}
$lock = Read-KitLock $KitRoot
Check ($lock.adapters.Count -eq 11) 'six reviewed languages, two labelled optional adapters, three new adapters'
Check (@($lock.adapters | Where-Object { -not $_.PSObject.Properties['verified'] }).Count -eq 0) 'every adapter states its historical verification flag'
Check (@($lock.adapters | Where-Object { $_.verified -eq $false }).Count -eq 3) 'legacy flags are not silently upgraded by changing the engine pin'
foreach ($id in @('rust','zig','go')) {
    Check ($lock.verification.$id.stage -eq 'runtime-smoke' -and $lock.verification.$id.engine -eq $lock.engine.version) "$id smoke evidence names the exact target engine"
}
Check ($lock.verification.python.stage -eq 'failed-teardown') 'Python teardown failure is not presented as success'
Check (@($lock.adapters | Where-Object { $_.verified -eq $false -and -not $_.PSObject.Properties['requires'] }).Count -eq 0) 'an unverified adapter names the toolchain it needs'
Check (@($lock.adapters | Where-Object { $_.verified -eq $false -and -not $_.PSObject.Properties['status'] }).Count -eq 0) 'an unverified adapter says why it is unverified'
Check (($lock.adapters | Where-Object { $_.id -eq 'fsharp' }).integration -eq 'managed-library-csharp-bridge') 'F# is not misrepresented as a new ScriptLanguage'
Check (($lock.sources | Where-Object { $_.id -eq 'luau' }).url -ne ($lock.sources | Where-Object { $_.id -eq 'lua' }).url) 'Lua is not silently substituted for Luau'
Check (@($lock.sources | Where-Object { $_.commit -notmatch '^[a-f0-9]{40}$' }).Count -eq 0) 'all upstream sources are pinned to complete commits'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('dof-kit-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$probeRoot = Join-Path $temp 'source'
New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $lockFixture = Join-Path $temp 'lock-fixture'
    New-Item -ItemType Directory -Path $lockFixture | Out-Null
    $invalidLock = $lock | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $invalidLock.adapters += $invalidLock.adapters[0]
    [IO.File]::WriteAllText((Join-Path $lockFixture 'sources.lock.json'), ($invalidLock | ConvertTo-Json -Depth 20))
    Reject { Read-KitLock $lockFixture } 'duplicate adapter id rejected'
    $invalidLock = $lock | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $invalidLock.sources[0].commit = 'main'
    [IO.File]::WriteAllText((Join-Path $lockFixture 'sources.lock.json'), ($invalidLock | ConvertTo-Json -Depth 20))
    Reject { Read-KitLock $lockFixture } 'mutable source reference rejected'
    $invalidLock = $lock | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $invalidLock.adapters[0].source = 'missing-upstream'
    [IO.File]::WriteAllText((Join-Path $lockFixture 'sources.lock.json'), ($invalidLock | ConvertTo-Json -Depth 20))
    Reject { Read-KitLock $lockFixture } 'unknown upstream reference rejected'
    $child = Get-KitPath $probeRoot 'nested/file.txt'
    Check ($child.StartsWith($probeRoot, [StringComparison]::OrdinalIgnoreCase)) 'normal child path remains bounded'
    Reject { Get-KitPath $probeRoot '../outside.txt' } 'parent traversal rejected'
    Reject { Get-KitPath $probeRoot 'nested/../../outside.txt' } 'nested traversal rejected'
    Reject { Get-KitPath $probeRoot 'C:/outside.txt' } 'absolute target rejected'
    Reject { Get-KitPath $probeRoot 'file.txt:stream' } 'alternate stream rejected'
    Reject { Assert-KitInstallPath ([IO.Path]::GetPathRoot($temp)) } 'drive root rejected for install'
    Reject { Assert-KitInstallPath ([Environment]::GetFolderPath('UserProfile')) } 'user profile root rejected for install'
    # Test fixture files, not product/config edits.
    [IO.File]::WriteAllText((Join-Path $probeRoot 'keep.txt'), 'source-v1')
    New-Item -ItemType Directory -Path (Join-Path $probeRoot '.cache') | Out-Null
    [IO.File]::WriteAllText((Join-Path $probeRoot '.cache/not-for-shipping.dll'), 'not-a-binary')
    Check (@(Get-KitSourceFiles $probeRoot).Count -eq 1) 'cache is not redistributed as source'
    $destination = Join-Path $temp 'installed'
    $plan = @(Install-KitSources $probeRoot $destination)
    Check ($plan.Count -eq 1 -and (Get-Content -Raw (Join-Path $destination 'keep.txt')) -eq 'source-v1') 'source copy preserves contents'
    Check (@(Install-KitSources $probeRoot $destination).Count -eq 1) 'identical source install is idempotent'
    [IO.File]::WriteAllText((Join-Path $destination 'keep.txt'), 'user-edit')
    Reject { Install-KitSources $probeRoot $destination } 'different existing file is preserved, not overwritten'
    Check ((Get-Content -Raw (Join-Path $destination 'keep.txt')) -eq 'user-edit') 'existing user edit survived failed preflight'
    Reject { Install-KitSources $probeRoot (Join-Path $probeRoot 'recursive-copy') } 'recursive self-copy rejected'
    [IO.File]::WriteAllText((Join-Path $probeRoot 'unreviewed.dll'), 'not-a-binary')
    Reject { Get-KitSourceFiles $probeRoot } 'binary artifact cannot enter source-only engine kit'
} finally {
    $resolvedTemp = [IO.Path]::GetFullPath($temp)
    if ($resolvedTemp.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemp) -like 'dof-kit-contract-*') {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
Write-Host "KIT_TESTS checks=$script:Checks failures=0"
