# Package an already verified build. Windows can briefly retain an executable's
# image lock after process shutdown; retry packaging, not the entire export.
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Archive
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-PosixZipArchive {
    param(
        [Parameter(Mandatory = $true)][string]$SourceFolder,
        [Parameter(Mandatory = $true)][string]$DestinationZip
    )

    $folderFull = [IO.Path]::GetFullPath($SourceFolder).TrimEnd('\', '/')
    $rootPrefix = [IO.Path]::GetFileName($folderFull)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    $stream = [IO.File]::Open($DestinationZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false, $utf8NoBom)
        try {
            # Windows PowerShell 5.1 Compress-Archive emits backslash paths and bare 0-byte directory entries
            # without trailing '/', which itch.io butler rejects as file/directory name collisions.
            [string[]]$files = [IO.Directory]::GetFiles($folderFull, '*', [IO.SearchOption]::AllDirectories)
            [Array]::Sort($files, [StringComparer]::OrdinalIgnoreCase)
            foreach ($file in $files) {
                $relativePath = $file.Substring($folderFull.Length).TrimStart('\', '/').Replace('\', '/')
                if ([string]::IsNullOrWhiteSpace($relativePath)) {
                    throw "Failed to compute relative archive path for file: $file"
                }
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip,
                    $file,
                    "$rootPrefix/$relativePath",
                    [IO.Compression.CompressionLevel]::Optimal
                ) | Out-Null
            }

            [string[]]$dirs = [IO.Directory]::GetDirectories($folderFull, '*', [IO.SearchOption]::AllDirectories)
            [Array]::Sort($dirs, [StringComparer]::OrdinalIgnoreCase)
            foreach ($dir in $dirs) {
                $enumerator = [IO.Directory]::EnumerateFileSystemEntries($dir).GetEnumerator()
                try {
                    $isEmpty = -not $enumerator.MoveNext()
                }
                finally {
                    $enumerator.Dispose()
                }
                if ($isEmpty) {
                    $relativeDir = $dir.Substring($folderFull.Length).TrimStart('\', '/').Replace('\', '/')
                    if ([string]::IsNullOrWhiteSpace($relativeDir)) { continue }
                    $dirEntry = $zip.CreateEntry("$rootPrefix/$relativeDir/")
                    $dirEntry.ExternalAttributes = 0x10
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Test-PackageZipContainer {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$ExpectedRootPrefix
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $allNormalized = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $fileEntries = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $dirEntries = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $expectedExe = "$ExpectedRootPrefix/Deal or Force.exe"
        $exeEntryFound = $false

        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ($name.Contains('\')) {
                throw "ZIP container check failed: entry contains backslash separator: $name"
            }
            if ($name.StartsWith('/') -or $name.Contains('//')) {
                throw "ZIP container check failed: malformed slashes in entry name: $name"
            }
            $trimmed = $name.TrimEnd('/')
            if ([string]::IsNullOrWhiteSpace($trimmed)) {
                throw "ZIP container check failed: empty entry name '$name'"
            }
            if (-not $allNormalized.Add($trimmed)) {
                throw "ZIP container check failed: duplicate or colliding entry path: $name"
            }

            if ($name.EndsWith('/')) {
                if ($entry.Length -ne 0) {
                    throw "ZIP container check failed: directory entry has non-zero length: $name"
                }
                [void]$dirEntries.Add($trimmed)
            }
            else {
                [void]$fileEntries.Add($trimmed)
                if ($trimmed.Equals($expectedExe, [StringComparison]::OrdinalIgnoreCase) -and $entry.Length -gt 0) {
                    $exeEntryFound = $true
                }
            }

            $slash = $trimmed.LastIndexOf('/')
            while ($slash -gt 0) {
                [void]$dirEntries.Add($trimmed.Substring(0, $slash))
                $slash = $trimmed.LastIndexOf('/', $slash - 1)
            }
        }

        foreach ($filePath in $fileEntries) {
            if ($dirEntries.Contains($filePath)) {
                throw "ZIP container check failed: file entry collides with directory path: $filePath"
            }
        }
        if (-not $exeEntryFound) {
            throw "ZIP container check failed: missing non-empty executable entry '$expectedExe'"
        }
    }
    finally {
        $archive.Dispose()
    }
}

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
$packageRootPrefix = [IO.Path]::GetFileName($packageFolder.TrimEnd('\', '/'))
$packageTemporary = Join-Path $packageRoot ('.package-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            Write-PosixZipArchive -SourceFolder $packageFolder -DestinationZip $packageTemporary
            Test-PackageZipContainer -ArchivePath $packageTemporary -ExpectedRootPrefix $packageRootPrefix
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
