# Capture retention: the playtest camera keeps every session forever, and after a busy
# month that is ten thousand pictures and hundreds of megabytes of pictures nobody is
# ever going to open again. This keeps the newest sessions (the ones a report might
# still point at) and offers the rest for deletion.
#
#   Tools/capture-retention.ps1            # report only - what would go
#   Tools/capture-retention.ps1 -Apply     # actually delete it
#
# Rules: always keep the newest 20 sessions, whatever their age; beyond those, delete
# sessions whose directory is older than 14 days (by name: folders are timestamps).

param(
    [string]$Captures = "tmp_playtest\captures",
    [int]$KeepNewest = 20,
    [int]$Days = 14,
    [switch]$Apply
)

$root = Join-Path (Join-Path $PSScriptRoot "..") $Captures
if (-not (Test-Path -LiteralPath $root)) { Write-Host "no captures folder at $root"; exit 0 }

$sessions = Get-ChildItem -LiteralPath $root -Directory |
    Where-Object { $_.Name -match '^\d{8}-\d{6}$' } |
    Sort-Object Name -Descending
Write-Host "sessions: $($sessions.Count) in $Captures"

$cutoff = (Get-Date).AddDays(-$Days).ToString('yyyyMMdd-HHmmss')
$victims = @()
for ($i = 0; $i -lt $sessions.Count; $i++) {
    if ($i -lt $KeepNewest) { continue }
    if ($sessions[$i].Name -lt $cutoff) { $victims += $sessions[$i] }
}

if ($victims.Count -eq 0) {
    Write-Host "nothing to do: the newest $KeepNewest are kept and the rest are younger than $Days days"
    exit 0
}

$freed = ($victims | Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue |
    Measure-Object Length -Sum).Sum / 1MB
Write-Host ("would delete {0} sessions, freeing {1:N0} MB:" -f $victims.Count, $freed)
$victims | Select-Object -First 10 | ForEach-Object { Write-Host "  $($_.Name)" }
if ($victims.Count -gt 10) { Write-Host "  ... and $($victims.Count - 10) more" }

if (-not $Apply) { Write-Host "dry run - pass -Apply to delete"; exit 0 }
foreach ($session in $victims) {
    # Only timestamp-named directories inside the captures root are ever touched, and
    # only whole sessions - never the live one a running game is writing into.
    if ($session.Name -notmatch '^\d{8}-\d{6}$') { continue }
    Remove-Item -LiteralPath $session.FullName -Recurse -Force
}
Write-Host "deleted $($victims.Count) sessions"
