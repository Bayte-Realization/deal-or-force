<#
.SYNOPSIS
Two owned exported Godot processes: paused recovery, host-side burst budget, cleanup.
.DESCRIPTION
Copies an existing export to a fresh private depot, overlays the supplied managed build,
and isolates each peer's documents. Does not export, publish, or stop any existing game.
The old pack is a runtime fixture, not proof that a new release pack has been verified.
#>
param(
    [Parameter(Mandatory)][string]$ExportDirectory,
    [Parameter(Mandatory)][string]$ManagedDirectory,
    [string]$OutputDirectory,
    [int]$Port = 18279,
    [ValidateSet('recovery', 'catchup')][string]$Probe = 'recovery'
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$template = (Resolve-Path -LiteralPath $ExportDirectory).Path
$managed = (Resolve-Path -LiteralPath $ManagedDirectory).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $project ('build/network-recovery-native-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory: old signals must not satisfy a new run.' }
if (Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue) { throw "UDP $Port is already in use." }
if (-not (Test-Path -LiteralPath (Join-Path $managed 'DealOrForce.dll'))) { throw 'Build the managed game first.' }
New-Item -ItemType Directory -Path $output | Out-Null
$depot = Join-Path $output 'depot'
New-Item -ItemType Directory -Path $depot | Out-Null
Get-ChildItem -LiteralPath $template | Copy-Item -Destination $depot -Recurse
$data = Join-Path $depot 'data_DealOrForce_windows_x86_64'
foreach ($name in @('DealOrForce.dll', 'DealOrForce.pdb', 'DealOrForce.Rules.dll', 'DealOrForce.Rules.pdb')) {
    $source = Join-Path $managed $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $data $name) }
}
$expectedHash = (Get-FileHash -LiteralPath (Join-Path $managed 'DealOrForce.dll')).Hash
if ((Get-FileHash -LiteralPath (Join-Path $data 'DealOrForce.dll')).Hash -ne $expectedHash) { throw 'Managed overlay hash mismatch.' }
Write-Host "Managed SHA256 $expectedHash"
$exe = Join-Path $depot 'Deal or Force.exe'
$signal = Join-Path $output 'client.result'
$ownedPeers = [Collections.Generic.List[Diagnostics.Process]]::new()
$originalDocuments = $env:DOF_VERIFICATION_DOCUMENTS
function Start-Peer([string]$Role) {
    $profile = Join-Path $output ($Role + '-documents')
    New-Item -ItemType Directory -Path $profile | Out-Null
    $env:DOF_VERIFICATION_DOCUMENTS = $profile
    $arguments = '--headless -- --verify-build --verify-development --verify-scenario-peer ' + $Role + ' ' + $Port +
        ' --verify-network-' + $Probe + ' --verify-recovery-signal "' + $signal + '" --verify-build-output "' + (Join-Path $output $Role) + '"'
    $peer = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $depot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $output ($Role + '.stdout.log')) -RedirectStandardError (Join-Path $output ($Role + '.stderr.log'))
    $ownedPeers.Add($peer)
    Write-Host "$Role PID $($peer.Id)"
    return $peer
}
try {
    $hostPeer = Start-Peer 'host'
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    $hostLog = Join-Path $output 'host.stdout.log'
    while (-not $hostPeer.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if (Select-String -LiteralPath $hostLog -Pattern 'LOBBY_VERIFY host-ready' -Quiet) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($hostPeer.HasExited -or -not (Select-String -LiteralPath $hostLog -Pattern 'LOBBY_VERIFY host-ready' -Quiet)) {
        throw "Host did not reach its real ENet lobby; inspect $hostLog"
    }
    $clientPeer = Start-Peer 'client'
    $deadline = [DateTime]::UtcNow.AddMinutes(8)
    while ([DateTime]::UtcNow -lt $deadline -and (@($ownedPeers | Where-Object { -not $_.HasExited }).Count -gt 0)) {
        Start-Sleep -Milliseconds 500
    }
    foreach ($peer in $ownedPeers) {
        if (-not $peer.HasExited) { throw "Owned verification PID $($peer.Id) exceeded its deadline." }
        $peer.WaitForExit()
        if ($peer.ExitCode -ne 0) { throw "Owned verification PID $($peer.Id) exited $($peer.ExitCode)." }
    }
    foreach ($role in @('host', 'client')) {
        $log = Join-Path $output ($role + '.stdout.log')
        if (-not (Select-String -LiteralPath $log -Pattern '^EXPORT_VERIFY failures=0$' -Quiet)) { throw "$role did not finish cleanly." }
        $passed = @(Select-String -LiteralPath $log -Pattern '^PASS EXPORT_VERIFY ').Count
        Write-Host "$role completed, $passed native assertions"
    }
    $recoveries = @(Select-String -LiteralPath $hostLog -Pattern '^\[net\] resynchronising peer ')
    if ($Probe -eq 'recovery' -and ($recoveries.Count -ne 2 -or $recoveries[0].Line -notmatch 'verify-recovery-first$' -or $recoveries[1].Line -notmatch 'verify-recovery-second$')) {
        throw "Host prepared $($recoveries.Count) recovery snapshots, expected exactly the two admitted requests."
    }
    if ($Probe -eq 'recovery') { Write-Host 'PASS: two real snapshots; forty bypass requests caused no extra preparation.' }
    elseif ($recoveries.Count -ne 1 -or $recoveries[0].Line -notmatch 'checksum mismatch on day ') { throw 'Catch-up expected exactly one checksum-triggered recovery.' }
    else { Write-Host 'PASS: staged ENet catch-up, mixed cadence, deferred checksum and one repairing snapshot.' }
    Write-Host "Evidence: $output"
}
finally {
    $env:DOF_VERIFICATION_DOCUMENTS = $originalDocuments
    # Only these two processes belong to this script. Never touch an editor or another run.
    foreach ($peer in $ownedPeers) {
        if (-not $peer.HasExited) { Stop-Process -Id $peer.Id -ErrorAction SilentlyContinue }
        $peer.Dispose()
    }
}
