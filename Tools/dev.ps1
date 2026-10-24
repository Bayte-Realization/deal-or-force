<#
.SYNOPSIS
    The daily development loop for Deal or Force, in one command.

.DESCRIPTION
    Everything this project is actually checked with - a build, the headless test stand, the
    Godot asset import and the in-game verification probe - behind four switches, so the loop
    is one line instead of four remembered invocations. It reports the numbers that matter
    (test count, failures, EXPORT_VERIFY result) and never hides a failure behind "done".

    The Godot executable is found in this order: -Godot, $env:GODOT_EXECUTABLE, the GODOT_PATH
    inside .mcp.json, then the engine path this branch is developed against.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\dev.ps1 -All
    Build, run the whole test stand, and run the game's own verification probe.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\dev.ps1 -Test -TestArgs '--identity-only'
    Run one named suite.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\dev.ps1 -Game -CaptureDirectory C:\shots
    Play the scripted campaign in a real window and write screenshots to C:\shots.

.EXAMPLE
    ./Tools/dev.ps1 -Game -GameArgs '--verify-isolated-depot-only' -CaptureDirectory build/depot-probe
    Run a narrow development probe through the same engine/version and result checks.
#>
param(
    [switch]$Build,
    [switch]$Test,
    [string]$TestArgs = '',
    [switch]$Import,
    [switch]$Game,
    [string[]]$GameArgs = @(),
    [switch]$Headless,
    [string]$CaptureDirectory,
    [string]$Godot,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
Set-Location $project

function Find-Godot {
    if ($Godot) { return $Godot }
    if ($env:GODOT_EXECUTABLE) { return $env:GODOT_EXECUTABLE }
    $config = Join-Path $project '.mcp.json'
    if (Test-Path -LiteralPath $config) {
        try {
            $path = (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).mcpServers.'godot-coding-solo'.env.GODOT_PATH
            if ($path -and (Test-Path -LiteralPath $path)) { return $path }
        } catch { }
    }
    return 'C:\GodotEngineNet 4.8-Beta1\Godot_v4.8-beta1_mono_win64_console.exe'
}

function Invoke-Build {
    Write-Host '== building ==' -ForegroundColor Cyan
    dotnet build (Join-Path $project 'DealOrForce.sln') -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'the game does not compile' }
    dotnet build (Join-Path $project 'Tools\FormableTests\FormableTests.csproj') -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'the test stand does not compile' }
    Write-Host '  game and test stand compile' -ForegroundColor Green
}

function Find-MatchingGodot {
    $candidate = Find-Godot
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "No Godot executable at $candidate"
    }
    # A stale .mcp.json can point at another 4.x build. The game's own export check
    # already refuses a mismatched engine; play and import must do the same, or a wrong
    # editor is started and its MCP bridge hangs before the game ever reports readiness.
    $version = (& $candidate --version | Select-Object -Last 1).Trim()
    $sdk = ([xml](Get-Content (Join-Path $project 'DealOrForce.csproj') -Raw)).Project.Sdk
    if ($sdk -match '/(?<major>\d+)\.(?<minor>\d+)') {
        $wanted = "$($Matches.major).$($Matches.minor)."
        if (-not $version.StartsWith($wanted, [StringComparison]::Ordinal)) {
            $fallback = 'C:\GodotEngineNet 4.8-Beta1\Godot_v4.8-beta1_mono_win64_console.exe'
            if (-not (Test-Path -LiteralPath $fallback)) {
                throw "Godot $version does not match the project's $wanted SDK, and no matching fallback exists."
            }
            Write-Host "  engine $version does not match $wanted; using $fallback" -ForegroundColor Yellow
            $candidate = $fallback
        }
    }
    return $candidate
}

function Invoke-LoggedNative([string]$Executable, [string[]]$Arguments, [string]$LogPath) {
    # Windows PowerShell turns native stderr into ErrorRecords. Keep it in the log
    # and inspect the exit code below, instead of losing the report to Stop first.
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments *> $LogPath
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousErrorAction }
}

function Invoke-Tests {
    Write-Host '== test stand ==' -ForegroundColor Cyan
    $log = Join-Path ([IO.Path]::GetTempPath()) ('dof-tests-' + [Guid]::NewGuid().ToString('N') + '.log')
    # Incremental compilation is required here: otherwise a new --suite-only flag
    # can silently run an older assembly's entire stand and report a false pass.
    $arguments = @('run', '--project', (Join-Path $project 'Tools\FormableTests'), '--')
    if ($TestArgs) { $arguments += $TestArgs.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) }
    $exit = Invoke-LoggedNative 'dotnet' $arguments $log
    $pass = (Select-String -Path $log -Pattern '^PASS ' -ErrorAction SilentlyContinue).Count
    $failure = Select-String -Path $log -Pattern '^FAIL |Exception|Unhandled' -ErrorAction SilentlyContinue |
        Where-Object { $_.Line -notmatch 'PASS ' } | Select-Object -First 5
    Write-Host "  PASS $pass" -ForegroundColor Green
    if ($exit -ne 0 -or $failure) {
        $failure | ForEach-Object { Write-Host ('  ' + $_.Line) -ForegroundColor Red }
        Write-Host "  full log: $log" -ForegroundColor Red
        throw "the test stand failed (exit $exit)"
    }
    Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
}

function Invoke-Import {
    Write-Host '== importing assets ==' -ForegroundColor Cyan
    $log = Join-Path ([IO.Path]::GetTempPath()) ('dof-import-' + [Guid]::NewGuid().ToString('N') + '.log')
    $exit = Invoke-LoggedNative (Find-MatchingGodot) @('--headless', '--path', $project, '--import') $log
    if ($exit -ne 0) { throw "the Godot asset import failed (exit $exit); log: $log" }
    $count = (Get-ChildItem (Join-Path $project '.godot\imported') -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "  $count imported resources" -ForegroundColor Green
}

function Invoke-Game {
    Write-Host '== playing the scripted campaign ==' -ForegroundColor Cyan
    $captures = if ($CaptureDirectory) { $CaptureDirectory } else { Join-Path ([IO.Path]::GetTempPath()) 'dof-shots' }
    New-Item -ItemType Directory -Force -Path $captures | Out-Null
    $arguments = @('--path', $project)
    if ($Headless) { $arguments = @('--headless') + $arguments }
    $arguments += @('--', '--verify-build', '--verify-development', '--verify-build-output', $captures)
    $arguments += $GameArgs
    $log = Join-Path $captures 'run.log'
    $exit = Invoke-LoggedNative (Find-MatchingGodot) $arguments $log
    $result = Select-String -Path $log -Pattern 'EXPORT_VERIFY failures=(\d+)' | Select-Object -Last 1
    $shots = (Get-ChildItem $captures -Filter *.png -ErrorAction SilentlyContinue | Measure-Object).Count
    if (-not $result) {
        Get-Content $log -Tail 20
        throw "the game produced no verification result; log: $log"
    }
    $failures = [int]$result.Matches[0].Groups[1].Value
    Write-Host "  $result" -ForegroundColor ($(if ($failures -eq 0) { 'Green' } else { 'Red' }))
    Write-Host "  screenshots: $shots in $captures" -ForegroundColor Green
    if ($failures -ne 0 -or $exit -ne 0) { throw "the game failed its own verification (exit $exit); log: $log" }
}

if ($All) { $Build = $true; $Test = $true; $Game = $true }
if (-not ($Build -or $Test -or $Import -or $Game)) {
    Write-Host 'Nothing asked for. Use -Build, -Test, -Import, -Game or -All.' -ForegroundColor Yellow
    exit 0
}
if ($Build) { Invoke-Build }
if ($Import) { Invoke-Import }
if ($Test) { Invoke-Tests }
if ($Game) { Invoke-Game }
Write-Host 'Done.' -ForegroundColor Cyan
