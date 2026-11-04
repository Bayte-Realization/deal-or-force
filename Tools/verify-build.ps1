param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$LogDirectory,
    [ValidateRange(10, 900)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExportDiagnostics.ps1')
$verificationExecutable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $verificationExecutable -PathType Leaf)) { throw "Missing game executable: $verificationExecutable" }
& (Join-Path $PSScriptRoot 'Test-WindowsExecutableIdentity.ps1') -Executable $verificationExecutable
if (-not $LogDirectory) {
    $LogDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dof-export-verification-' + [Guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$stdoutPath = Join-Path $LogDirectory 'verification.stdout.log'
$stderrPath = Join-Path $LogDirectory 'verification.stderr.log'
$verificationProcess = Start-Process -FilePath $verificationExecutable -ArgumentList '--headless', '--', '--verify-build' `
    -WorkingDirectory (Split-Path -Parent $verificationExecutable) -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
# Touch the handle now: a process started with -PassThru and waited on with WaitForExit(ms)
# reports no exit code at all unless its handle was taken while it was running, and a
# clean run then fails as "exit ".
$null = $verificationProcess.Handle
$verificationTimer = [Diagnostics.Stopwatch]::StartNew()
while (-not $verificationProcess.WaitForExit(1000)) {
    if ($verificationTimer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        # Only the verification process just created above belongs to this operation.
        $verificationProcess.Kill()
        $null = $verificationProcess.WaitForExit(5000)
        throw "Export verification timed out. Logs: $LogDirectory"
    }
}
$verificationOutput = Get-Content -LiteralPath $stdoutPath -Raw
$verificationError = Get-Content -LiteralPath $stderrPath -Raw
$engineFailures = @(Get-FatalExportDiagnostic $verificationOutput $verificationError)
Get-Content -LiteralPath $stdoutPath -Tail 12 | Write-Host
if ($verificationProcess.ExitCode -ne 0 -or $verificationOutput -notmatch '(?m)^EXPORT_VERIFY failures=0\s*$' -or $engineFailures.Count -gt 0) {
    $engineFailures | Write-Host
    Get-Content -LiteralPath $stderrPath -Tail 20 | Write-Host
    throw "Export verification failed, including possible shutdown failure (exit $($verificationProcess.ExitCode)). Logs: $LogDirectory"
}
Write-Host "Export verification passed, including clean process shutdown and no shader/script errors. Logs: $LogDirectory" -ForegroundColor Green
