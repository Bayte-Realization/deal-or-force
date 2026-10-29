param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Archive
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../workbench/probe-common.ps1')
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
# GitHub's release-asset SHA256, independently checked on expanded_assets/4.7-alpha21.
$hash = '7FA28DB6E5614523A4A9E092CA2E75FB76DFA1F4C385AAF6DD624EB33BF3EB4C'
if ((Get-FileHash -LiteralPath $archivePath).Hash -ne $hash) { throw 'py4godot 4.7-alpha21 archive checksum mismatch.' }
$output = New-KitProbeWorkspace $OutputDirectory (Split-Path -Parent $archivePath) 'python-release'
$project = Join-Path $output 'project'
New-Item -ItemType Directory -Path $project -Force | Out-Null
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $plan = @()
    foreach ($entry in $zip.Entries) {
        if (-not $entry.FullName.StartsWith('install_dir/addons/py4godot/')) { continue }
        if ($entry.FullName.EndsWith('/')) { continue }
        if ($entry.FullName -match '/cpython-[^/]+/' -and $entry.FullName -notmatch '/cpython-3\.14\.4-windows64/') { continue }
        if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Symlink entry is not accepted.' }
        $relative = $entry.FullName.Substring('install_dir/'.Length)
        $target = Get-KitPath $project $relative
        Assert-KitInstallPath $target
        $plan += @{entry=$entry;target=$target}
    }
    # Validate the complete bounded extraction plan before extracting any file.
    foreach ($item in $plan) {
        if (Test-Path -LiteralPath $item.target) { continue }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $item.target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($item.entry, $item.target, $false)
    }
} finally { $zip.Dispose() }
Copy-KitTemplate (Join-Path $PSScriptRoot 'dof_python_kernel.py') (Join-Path $project 'dof_python_kernel.py')
Copy-KitTemplate (Join-Path $PSScriptRoot 'probe-runtime.gd') (Join-Path $project 'probe.gd')
New-KitGodotProject $GodotExecutable $project 'Python release ABI probe'
Write-Output "PROBE_PROJECT=$project"
Write-Output "ARCHIVE_SHA256=$hash"
