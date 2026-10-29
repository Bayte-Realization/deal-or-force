# Engine diagnostics can be printed even when the application exits with code zero.
function Get-FatalExportDiagnostic([string]$StandardOutput, [string]$StandardError) {
    # Limit the gate to explicit script/shader failures; existing ObjectDB teardown
    # warnings are reported separately, not silently promoted to shader failures.
    @([regex]::Matches(($StandardOutput + "`n" + $StandardError),
        '(?m)^\s*(?:SHADER ERROR|SCRIPT ERROR):[^\r\n]*') | ForEach-Object { $_.Value.Trim() })
}
