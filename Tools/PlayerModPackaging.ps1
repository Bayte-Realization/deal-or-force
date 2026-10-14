# Optional crossovers declare ship_with_base=false and ship through their own installer.
# This function only selects folders; copying/exporting remains the caller's responsibility.
function Get-BaseBuildModFolders([string]$Source) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { return }
    foreach ($taskFolder in Get-ChildItem -LiteralPath $Source -Force -Directory | Sort-Object Name) {
        if ($taskFolder.Name.StartsWith('.') -or ($taskFolder.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        $taskManifest = Join-Path $taskFolder.FullName 'manifest.json'
        if (Test-Path -LiteralPath $taskManifest -PathType Leaf) {
            if ((Get-Item -LiteralPath $taskManifest).Length -gt 262144) { throw "Oversized mod manifest: $taskManifest" }
            $taskDescriptor = Get-Content -LiteralPath $taskManifest -Raw | ConvertFrom-Json
            if ($taskDescriptor.PSObject.Properties.Name -contains 'ship_with_base' -and
                $taskDescriptor.ship_with_base -isnot [bool]) { throw "ship_with_base must be boolean: $taskManifest" }
            if ($taskDescriptor.PSObject.Properties.Name -contains 'ship_with_base' -and
                -not $taskDescriptor.ship_with_base) { continue }
        }
        $taskFolder
    }
}
