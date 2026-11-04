param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$binary = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    throw "Missing Windows game executable: $binary"
}

# Read the PE version resource that Windows exposes in Explorer and Task Manager.
# Do not accept the filename or the game's window title as proof of its identity.
$identity = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary)
foreach ($entry in ([ordered]@{
    FileDescription = 'Deal or Force'
    ProductName = 'Deal or Force'
    CompanyName = 'Bayte Realization'
}).GetEnumerator()) {
    $actual = $identity.($entry.Key)
    if ($actual -cne $entry.Value) {
        throw "Windows executable $($entry.Key) must be '$($entry.Value)', got '$actual': $binary"
    }
    Write-Host "PASS Windows executable $($entry.Key): $actual"
}
