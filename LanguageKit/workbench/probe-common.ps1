. (Join-Path $PSScriptRoot '../kit-common.ps1')

function New-KitProbeWorkspace {
    param([string]$OutputDirectory, [string]$SourceDirectory, [string]$Adapter)
    $output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
    $source = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd('\', '/')
    Assert-KitInstallPath $output
    if ($output -eq $source -or $output.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Workspace must be outside upstream sources.' }
    $marker = Join-Path $output '.languagekit-owned.json'
    if (Test-Path -LiteralPath $output) {
        if (-not (Test-Path -LiteralPath $marker)) { throw "Choose a fresh output directory: $output" }
        if ((Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).adapter -ne $Adapter) { throw 'Output belongs to a different adapter.' }
    } else { New-Item -ItemType Directory -Path $output -Force | Out-Null }
    if (-not (Test-Path -LiteralPath $marker)) {
        [IO.File]::WriteAllText($marker, (@{adapter=$Adapter;source=$source;schema=1} | ConvertTo-Json))
        [IO.File]::WriteAllText((Join-Path $output '.gdignore'), '')
    }
    return $output
}

function Copy-KitTemplate {
    param([string]$Source, [string]$Target, [string]$Content)
    if (-not $PSBoundParameters.ContainsKey('Content')) { $Content = [IO.File]::ReadAllText($Source) }
    if (Test-Path -LiteralPath $Target) {
        if ([IO.File]::ReadAllText($Target) -cne $Content) { throw "Edited generated source preserved: $Target" }
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target) | Out-Null
        [IO.File]::WriteAllText($Target, $Content, [Text.UTF8Encoding]::new($false))
    }
}

function New-KitGodotProject {
    param([string]$GodotExecutable, [string]$Project, [string]$Title)
    New-Item -ItemType Directory -Path $Project -Force | Out-Null
    $config = Join-Path $Project 'project.godot'
    if (Test-Path -LiteralPath $config) { return }
    $worker = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'create-probe-project.gd')).Path
    $null = Invoke-KitGodot $GodotExecutable @('--headless', '--path', $Project, '--script', $worker, '--', $config, $Title)
}

function Test-KitScalarAdapter {
    param([string]$GodotExecutable, [string]$OutputDirectory, [string]$Marker)
    $project = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'project'
    Invoke-KitEditorBootstrap $GodotExecutable $project
    $result = Invoke-KitGodot $GodotExecutable @('--headless', '--path', $project, '--script', 'res://probe.gd', '--quit-after', '240')
    if ($result.Output -match '(?i)leaked|still in use|CrashHandler|access violation|segmentation fault') { throw "$Marker teardown/resource failure." }
    if ($result.Output -notmatch [regex]::Escape($Marker + '_PROBE PASS')) { throw "Missing $Marker call/return success marker." }
    Write-Host "$($Marker)_VERIFIED import=0 runtime=0 errors=0"
}
