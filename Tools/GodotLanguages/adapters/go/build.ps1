param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$GoExecutable = 'go',
    # The pinned graphics.gd release requires a Go newer than the machine-wide install;
    # the toolchain is fetched on demand into the user's Go cache, no admin rights.
    [string]$GoToolchain = 'go1.27.3'
)
$ErrorActionPreference = 'Stop'
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
$GodotExecutable = (Resolve-Path -LiteralPath $GodotExecutable).Path
if ((& git -C $SourceDirectory rev-parse HEAD) -ne 'af2e5393cdf04d36e1cba40cb4d6437624916d40') { throw 'graphics.gd source is not the reviewed pin.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$app = Join-Path $OutputDirectory 'goapp'
New-Item -ItemType Directory -Force -Path $app | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src') -Destination $app -Recurse -Force
$manifest = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'go.mod.in') -Raw).Replace('@GD@', './gdsrc')
[IO.File]::WriteAllText((Join-Path $app 'go.mod'), $manifest)
# The go tool rejects both "D:/..." and "D:\..." as module paths (the colon), so the
# upstream is reached through a junction the go tool accepts as a relative directory.
$junction = Join-Path $app 'gdsrc'
if (-not (Test-Path -LiteralPath $junction)) { New-Item -ItemType Junction -Path $junction -Target $SourceDirectory | Out-Null }
$env:GOTOOLCHAIN = $GoToolchain
$env:GDGodotBin = $GodotExecutable
# graphics.gd ships an ordinary Go program first and a c-shared runtime second: without a
# C compiler on the machine (cgo), the probe builds as the executable the `gd` tool runs.
# The module lives at the app root (go.mod with the junction replace) and the program in
# src/, so the build runs from the root with the package path.
Push-Location $app
try
{
    & $GoExecutable mod tidy
    if ($LASTEXITCODE -ne 0) { throw "Go module tidy failed: $LASTEXITCODE" }
    & $GoExecutable build -o (Join-Path $OutputDirectory 'dof_languagekit_go.exe') ./src
}
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "Go compilation failed: $LASTEXITCODE" }
Write-Output "EXECUTABLE=$(Join-Path $OutputDirectory 'dof_languagekit_go.exe')"
Write-Output "APP_SOURCE=$app"
