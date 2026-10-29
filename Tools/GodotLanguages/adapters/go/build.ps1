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
$OutputDirectory = [IO.File]::GetFullPath($OutputDirectory)
$app = Join-Path $OutputDirectory 'goapp'
New-Item -ItemType Directory -Force -Path $app | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src') -Destination $app -Recurse -Force
$manifest = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'go.mod.in') -Raw).Replace('@GD@', $SourceDirectory.Replace('\','/'))
[IO.File]::WriteAllText((Join-Path $app 'go.mod'), $manifest)
$env:GOTOOLCHAIN = $GoToolchain
$env:GDGodotBin = $GodotExecutable
& $GoExecutable build -buildmode=c-shared -o (Join-Path $OutputDirectory 'dof_languagekit_go.dll') ./...
if ($LASTEXITCODE -ne 0) { throw "Go compilation failed: $LASTEXITCODE" }
Write-Output "SHARED_LIBRARY=$(Join-Path $OutputDirectory 'dof_languagekit_go.dll')"
Write-Output "APP_SOURCE=$app"
