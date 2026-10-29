$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExportDiagnostics.ps1')
$passed = 0
foreach ($case in @(
    @{ Out = 'EXPORT_VERIFY failures=0'; Err = ''; Count = 0 },
    @{ Out = 'EXPORT_VERIFY failures=0'; Err = 'WARNING: ObjectDB instances leaked at exit'; Count = 0 },
    @{ Out = 'EXPORT_VERIFY failures=0'; Err = 'SHADER ERROR: sampler hint mismatch'; Count = 1 },
    @{ Out = "EXPORT_VERIFY failures=0`nSCRIPT ERROR: invalid call"; Err = ''; Count = 1 },
    @{ Out = ''; Err = "  SHADER ERROR: invalid sampler`n  SCRIPT ERROR: parse error"; Count = 2 },
    @{ Out = ''; Err = "ERROR: known ObjectDB exit diagnostics`n   at: cleanup"; Count = 0 }
)) {
    $actual = @(Get-FatalExportDiagnostic $case.Out $case.Err)
    if ($actual.Count -ne $case.Count) { throw "Expected $($case.Count) engine failures; found $($actual.Count)." }
    $passed++
}
Write-Host "EXPORT_DIAGNOSTICS_TESTS passed=$passed failures=0"
