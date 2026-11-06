#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Executable,
    [string[]]$Methods = @('gl_compatibility', 'gl_angle', 'forward_plus'),
    [ValidateRange(30,900)][int]$TimeoutSeconds = 300,
    [switch]$MapStyles,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $executablePath) ('verification-renderers-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new renderer-evidence directory; existing evidence is never overwritten.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
foreach ($method in $Methods) {
    if ($method -notin @('gl_compatibility', 'gl_angle', 'forward_plus', 'mobile')) { throw "Unknown renderer: $method" }
    $folder = Join-Path $OutputDirectory $method
    New-Item -ItemType Directory -Path $folder | Out-Null
    # Fresh writable Godot cache and game documents for each renderer. Do not touch
    # the player's archive or fail a shader test because their cache is read-only.
    $profile = Join-Path $folder 'profile'
    $roaming = Join-Path $profile 'roaming'
    $documents = Join-Path $profile 'documents'
    New-Item -ItemType Directory -Force -Path $roaming, $documents | Out-Null
    $renderer = if ($method -eq 'gl_angle') { 'gl_compatibility' } else { $method }
    $arguments = @('--rendering-method', $renderer)
    if ($method -eq 'gl_compatibility') { $arguments += @('--rendering-driver', 'opengl3') }
    if ($method -eq 'gl_angle') { $arguments += @('--rendering-driver', 'opengl3_angle') }
    # Start-Process joins ArgumentList on Windows. Quote the evidence path explicitly.
    $arguments += @('--', '--verify-build', '--verify-map-render-only', '--verify-build-output', ('"' + $folder + '"'))
    if ($MapStyles) { $arguments += '--verify-map-styles' }
    $process = Start-Process -FilePath $executablePath -WorkingDirectory (Split-Path -Parent $executablePath) `
        -WindowStyle Hidden -ArgumentList $arguments -PassThru `
        -Environment @{ APPDATA = $roaming; DOF_VERIFICATION_DOCUMENTS = $documents } `
        -RedirectStandardOutput (Join-Path $folder 'stdout.log') -RedirectStandardError (Join-Path $folder 'stderr.log')
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        # Only the exact process this script launched, never a user editor/game session.
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw "GPU verification timed out on $method; evidence: $folder"
    }
    $process.WaitForExit()
    $stdout = [IO.File]::ReadAllText((Join-Path $folder 'stdout.log'))
    $stderr = [IO.File]::ReadAllText((Join-Path $folder 'stderr.log'))
    # A driver request may silently fall back. That is not evidence of an ANGLE pass.
    if ($method -eq 'gl_angle' -and $stdout -notmatch 'driver=opengl3_angle') {
        throw "ANGLE was requested but not actually used; evidence: $folder"
    }
    if ($process.ExitCode -ne 0 -or $stdout -notmatch 'EXPORT_VERIFY failures=0' -or
        $stdout -notmatch 'MAP_RENDER_HEALTH' -or $stderr -match 'SHADER ERROR|SCRIPT ERROR|ERROR:') {
        throw "GPU verification failed on $method (exit $($process.ExitCode)); evidence: $folder"
    }
    if (@(Get-ChildItem -LiteralPath $folder -Filter 'export-map-render-health-*.png').Count -ne 1) {
        throw "GPU verification did not retain the actual map image on $method."
    }
    Write-Host "PASS GPU renderer $method; logs and image: $folder"
}
