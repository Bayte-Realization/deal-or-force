param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$LogDirectory,
    [ValidateRange(10, 900)][int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExportDiagnostics.ps1')
$doctrineExecutable = [IO.Path]::GetFullPath($Executable)
& (Join-Path $PSScriptRoot 'Test-WindowsExecutableIdentity.ps1') -Executable $doctrineExecutable
$doctrineEvidence = [IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Force -Path $doctrineEvidence | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $doctrineEvidence '.gdignore') | Out-Null
$doctrineStdout = Join-Path $doctrineEvidence 'doctrines.stdout.log'
$doctrineStderr = Join-Path $doctrineEvidence 'doctrines.stderr.log'
$doctrineDocuments = Join-Path $doctrineEvidence 'profile/documents'
New-Item -ItemType Directory -Force -Path $doctrineDocuments | Out-Null
$previousDocuments = $env:DOF_VERIFICATION_DOCUMENTS
try {
    $env:DOF_VERIFICATION_DOCUMENTS = $doctrineDocuments
    $doctrineProcess = Start-Process -FilePath $doctrineExecutable -WindowStyle Hidden -PassThru `
        -WorkingDirectory (Split-Path -Parent $doctrineExecutable) `
        -ArgumentList @('--rendering-method', 'gl_compatibility', '--', '--verify-build',
            '--verify-paused-doctrines-only', '--verify-build-output', ('"' + $doctrineEvidence + '"')) `
        -RedirectStandardOutput $doctrineStdout -RedirectStandardError $doctrineStderr
}
finally { $env:DOF_VERIFICATION_DOCUMENTS = $previousDocuments }
$null = $doctrineProcess.Handle
$doctrineTimer = [Diagnostics.Stopwatch]::StartNew()
while (-not $doctrineProcess.WaitForExit(1000)) {
    if ($doctrineTimer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        $doctrineProcess.Kill()
        $null = $doctrineProcess.WaitForExit(5000)
        throw "Doctrine verification timed out. Evidence: $doctrineEvidence"
    }
}
$doctrineOutput = Get-Content -LiteralPath $doctrineStdout -Raw
$doctrineErrors = Get-Content -LiteralPath $doctrineStderr -Raw
$doctrineFatal = @(Get-FatalExportDiagnostic $doctrineOutput $doctrineErrors)
Get-Content -LiteralPath $doctrineStdout -Tail 12 | Write-Host
if ($doctrineProcess.ExitCode -ne 0 -or $doctrineOutput -notmatch '(?m)^EXPORT_VERIFY failures=0\s*$' -or
    $doctrineOutput -notmatch '(?m)^DOCTRINE_CONTENT books=18 articles=288 advanced=144\s*$' -or
    $doctrineFatal.Count -gt 0) {
    $doctrineFatal | Write-Host
    Get-Content -LiteralPath $doctrineStderr -Tail 20 | Write-Host
    throw "Exported doctrine verification failed (exit $($doctrineProcess.ExitCode)). Evidence: $doctrineEvidence"
}
foreach ($language in @('english', 'russian')) {
    foreach ($width in @(1280, 1920)) {
        foreach ($service in @('ground', 'air', 'sea', 'general')) {
            $capture = Join-Path $doctrineEvidence "export-doctrine-service-$service-$language-$width.png"
            if (-not (Test-Path -LiteralPath $capture -PathType Leaf)) {
                throw "Missing real service screenshot: $capture"
            }
        }
    }
}
Write-Host "PASS exported doctrines: full catalogue, real study clicks, EN/RU 1080p/720p. Evidence: $doctrineEvidence" -ForegroundColor Green
