# Package an already verified build. Windows can briefly retain an executable's
# image lock after process shutdown; retry packaging, not the entire export.
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Archive
)
$ErrorActionPreference = 'Stop'
$packageProject = Split-Path -Parent $PSScriptRoot
$packageRoot = [IO.Path]::GetFullPath((Join-Path $packageProject 'build')) + [IO.Path]::DirectorySeparatorChar
$packageFolder = [IO.Path]::GetFullPath($OutputDirectory)
$packageArchive = [IO.Path]::GetFullPath($Archive)
foreach ($target in @($packageFolder, $packageArchive)) {
    if (-not $target.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Packaging target must be a child of this project's build folder: $target"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $packageFolder 'Deal or Force.exe') -PathType Leaf)) {
    throw 'The package has no game executable.'
}
if ([IO.Path]::GetExtension($packageArchive) -ne '.zip' -or (Test-Path -LiteralPath $packageArchive)) {
    throw "Choose a new .zip archive path; existing archives are preserved: $packageArchive"
}
$packageTemporary = Join-Path $packageRoot ('.package-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            Compress-Archive -LiteralPath $packageFolder -DestinationPath $packageTemporary -CompressionLevel Optimal
            break
        }
        catch {
            # Only the temporary file created for this invocation can be removed.
            if (Test-Path -LiteralPath $packageTemporary -PathType Leaf) {
                Remove-Item -LiteralPath $packageTemporary -Force
            }
            if ($attempt -eq 4) { throw }
            Write-Warning "Packaging attempt $attempt failed: $($_.Exception.Message). Retrying in two seconds."
            Start-Sleep -Seconds 2
        }
    }
    # File.Move refuses to overwrite an archive created concurrently by another build.
    [IO.File]::Move($packageTemporary, $packageArchive)
    $packageSize = [math]::Round((Get-Item -LiteralPath $packageArchive).Length / 1MB, 1)
    Write-Host "  $packageArchive ($packageSize MB)" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $packageTemporary -PathType Leaf) {
        Remove-Item -LiteralPath $packageTemporary -Force
    }
}
