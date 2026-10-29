param(
    [string]$KitRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$EngineDirectory = 'C:/GodotEngineNet 4.8-Beta2',
    [string]$BackupDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $KitRoot 'kit-common.ps1')
$source = (Resolve-Path -LiteralPath $KitRoot).Path.TrimEnd('\','/')
$engine = Get-KitEngine $EngineDirectory
$lock = Read-KitLock $KitRoot
if ($engine.Version -ne $lock.engine.version) { throw 'Engine differs from the kit target.' }
$destination = Join-Path $engine.Directory 'LanguageKit'
if ($destination -eq $source) { throw 'Run this updater from the source kit, not from the installed copy.' }
if (-not $BackupDirectory) { $BackupDirectory = Join-Path $engine.Directory ('LanguageKit-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')) }
$backup = [IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\','/')
Assert-KitInstallPath $destination
Assert-KitInstallPath $backup
if (Test-Path -LiteralPath $backup) { throw 'Backup directory must be fresh.' }
foreach ($root in @($source,$destination)) {
    if ($backup -eq $root -or $backup.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must be outside source and destination.' }
}
$plan = @(Get-KitSourceFiles $source | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($source,$_.FullName)
    $target = Get-KitPath $destination $relative
    Assert-KitInstallPath $target
    $old = if (Test-Path -LiteralPath $target) { (Get-FileHash -LiteralPath $target).Hash } else { $null }
    [pscustomobject]@{relative=$relative;source=$_.FullName;target=$target;old=$old;new=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
# Freeze and back up the complete preimage before replacing any differing source.
New-Item -ItemType Directory -Force -Path $backup | Out-Null
foreach ($item in $plan) {
    if ($item.old -and $item.old -ne $item.new) {
        $saved = Get-KitPath $backup $item.relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $saved) | Out-Null
        Copy-Item -LiteralPath $item.target -Destination $saved
        if ((Get-FileHash -LiteralPath $saved).Hash -ne $item.old) { throw 'Destination changed during backup; no source replaced.' }
    }
}
foreach ($item in $plan) {
    $now = if (Test-Path -LiteralPath $item.target) { (Get-FileHash -LiteralPath $item.target).Hash } else { $null }
    if ($now -ne $item.old -or (Get-FileHash -LiteralPath $item.source).Hash -ne $item.new) { throw 'Concurrent source/destination edit detected; retry after review.' }
}
foreach ($item in $plan) {
    if ($item.old -eq $item.new) { continue }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $item.target) | Out-Null
    Copy-Item -LiteralPath $item.source -Destination $item.target -Force
    if ((Get-FileHash -LiteralPath $item.target).Hash -ne $item.new) { throw 'Installed file checksum mismatch; restore from recorded backup.' }
}
[IO.File]::WriteAllText((Join-Path $backup 'receipt.json'), ($plan | Select-Object relative,old,new | ConvertTo-Json -Depth 5))
Write-Host "KIT_UPDATED files=$($plan.Count) backup=$backup"
Write-Host 'Only source kit files updated. Existing projects, cache, compiler binaries, engine and game addons preserved.'
