param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$GoExecutable = 'go',
    [string]$CCompiler = 'gcc',
    [switch]$BuildTool
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../workbench/probe-common.ps1')
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $source rev-parse HEAD) -ne 'af2e5393cdf04d36e1cba40cb4d6437624916d40') { throw 'Unexpected graphics.gd source revision.' }
$go = (Get-Command $GoExecutable -ErrorAction Stop).Source
$version = (& $go version | Out-String).Trim()
if ($version -notmatch 'go1\.(\d+)' -or [int]$Matches[1] -lt 27) { throw "Go 1.27+ required, found $version." }
$output = New-KitProbeWorkspace $OutputDirectory $source 'go'
$crate = Join-Path $output 'crate'
$project = Join-Path $output 'project'
$addon = Join-Path $project 'addons/dof_go'
New-Item -ItemType Directory -Force -Path $crate,$addon | Out-Null
Copy-KitTemplate (Join-Path $PSScriptRoot 'main.go') (Join-Path $crate 'main.go')
$mod = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'go.mod.in')).Replace('@GRAPHICS_GD@', '"' + $source.Replace('\','/') + '"')
Copy-KitTemplate -Target (Join-Path $crate 'go.mod') -Content $mod
Copy-KitTemplate (Join-Path $PSScriptRoot 'go.sum') (Join-Path $crate 'go.sum')
$saved = @{}
foreach ($key in @('CC','CGO_ENABLED','GOTOOLCHAIN','GOCACHE','GOMODCACHE','GOMAXPROCS')) { $saved[$key] = [Environment]::GetEnvironmentVariable($key,'Process') }
try {
    $env:CC = $CCompiler; $env:CGO_ENABLED = '1'; $env:GOTOOLCHAIN = 'local'; $env:GOMAXPROCS = '2'
    $env:GOCACHE = Join-Path $output 'go-cache'; $env:GOMODCACHE = Join-Path $output 'go-mod-cache'
    Push-Location -LiteralPath $crate
    try {
        & $go mod tidy
        if ($LASTEXITCODE -ne 0) { throw 'Go dependency resolution failed.' }
        & $go build -p 2 -buildmode=c-shared -o (Join-Path $addon 'dof_go.dll') .
        if ($LASTEXITCODE -ne 0) { throw 'Go extension build failed.' }
    } finally { Pop-Location }
    if ($BuildTool) {
        Push-Location -LiteralPath $source
        try { & $go build -p 2 -o (Join-Path $output 'gd.exe') ./cmd/gd } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw 'Go gd tool build failed.' }
    }
} finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key],'Process') } }
Copy-KitTemplate (Join-Path $PSScriptRoot 'dof_go.gdextension') (Join-Path $addon 'dof_go.gdextension')
Copy-KitTemplate (Join-Path $PSScriptRoot 'probe.gd') (Join-Path $project 'probe.gd')
Copy-Item -LiteralPath (Join-Path $source 'License') -Destination $addon -Force
New-KitGodotProject $GodotExecutable $project 'Go ABI probe'
Write-Output "PROBE_PROJECT=$project"
