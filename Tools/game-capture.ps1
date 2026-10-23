# Capture the running game, from the project rather than from an export.
#
# `Tools/dev.ps1 -Game` runs the EXPORTED executable and collects the pictures its own
# verification probe writes. That is the release gate, and it cannot show a screen a player
# reaches by clicking - it has no hands.
#
# This runs the game out of the editor project with Fennara's runtime helper switched on, so
# the game reports when it is up, photographs itself, and then takes orders: a small GDScript
# file can press buttons, wait, and capture. That is how a screen reachable only by clicking
# gets looked at.
#
#   powershell -ExecutionPolicy Bypass -File Tools\game-capture.ps1
#       launches, waits for readiness, and keeps the captures it took at startup.
#
#   powershell -ExecutionPolicy Bypass -File Tools\game-capture.ps1 -Script res://tools/probe.gd
#       also runs that script inside the running game and waits for it to finish.
#
# The script is a plain .gd file that extends RefCounted and defines `func run(ctx)`. What
# `ctx` can do is listed in addons/fennara/runtime/runtime_script_context.gd - capture,
# click_button, button_path_by_text, find_button_by_text, until_exists, wait, snapshot.
# `click_button` and `capture` are async: they need `await`, or GDScript stops the script.
#
# The game is left running when this returns, so more scripts can be sent to it. Close its
# window, or `Stop-Process -Id <pid>`, when you are done with it.
#
# Why the environment variable matters: addons/fennara/runtime/game_capture_helper.gd frees
# itself unless FENNARA_RT_SPEC names a spec file, and Fennara's own runtime_session does not
# set it on this machine - which is why every runtime session timed out at startup. Setting
# it here is the whole of the fix.

[CmdletBinding()]
param(
    [string]$Godot,
    [string]$Project,
    [string]$Scene = 'res://Scenes/MainMenu.tscn',
    [string]$Out,
    [string]$Script,
    [string]$Resolution,
    [int]$ReadyTimeoutSeconds = 240,
    [int]$ScriptTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

# Worked out in the body rather than as a parameter default: $PSScriptRoot is not yet set
# while parameters are being bound, which is why Tools/dev.ps1 does the same thing here.
if (-not $Project) { $Project = Split-Path -Parent $PSScriptRoot }

function Find-Godot {
    if ($Godot) { return $Godot }
    if ($env:GODOT_EXECUTABLE) { return $env:GODOT_EXECUTABLE }
    $config = Join-Path $Project '.mcp.json'
    if (Test-Path -LiteralPath $config) {
        try {
            $path = (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).mcpServers.'godot-coding-solo'.env.GODOT_PATH
            if ($path -and (Test-Path -LiteralPath $path)) { return $path }
        } catch { }
    }
    return 'C:\GodotEngineNet 4.8-Beta1\Godot_v4.8-beta1_mono_win64_console.exe'
}

$engine = Find-Godot
if (-not (Test-Path -LiteralPath $engine)) { throw "no Godot executable at $engine" }

$session = if ($Out) { $Out } else { Join-Path $Project ('tmp_playtest\captures\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$commandDir = Join-Path $session 'commands'
$artifactDir = Join-Path $session 'artifacts'
$captureDir = Join-Path $session 'captures'
$statusDir = Join-Path $session 'status'
foreach ($d in @($session, $commandDir, $artifactDir, $captureDir, $statusDir)) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

$sessionId = 'capture-' + (Get-Date -Format 'HHmmss')
$specPath = Join-Path $session 'spec.json'
$log = Join-Path $session 'game.log'

# Fennara reads these with forward slashes and accepts absolute paths.
function As-Slash([string]$path) { return $path.Replace('\', '/') }

$spec = [ordered]@{
    mode                           = 'runtime_session'
    session_id                     = $sessionId
    command_dir                    = (As-Slash $commandDir)
    artifact_dir                   = (As-Slash $artifactDir)
    captures_dir                   = (As-Slash $captureDir)
    scene_path                     = $Scene
    startup_capture_max_resolution = 1920
    startup_capture_status_path    = (As-Slash (Join-Path $session 'startup_status.json'))
}
$spec | ConvertTo-Json | Set-Content -LiteralPath $specPath -Encoding UTF8

Write-Host "  session   $sessionId"
Write-Host "  engine    $engine"
Write-Host "  log       $log"

# -ArgumentList joins an array with spaces and does not quote it, so a project path with a
# space in it arrives as two arguments and Godot aborts with "Invalid project path".
$arguments = @('--path', ('"' + $Project + '"'), $Scene)
# A layout is only proved at the size it is meant to run at: `-Resolution 1280x720` answers the
# question "does this fit on the small screen" without changing the project for everybody.
if ($Resolution) { $arguments += @('--windowed', '--resolution', $Resolution) }
$previous = $env:FENNARA_RT_SPEC
$env:FENNARA_RT_SPEC = $specPath
try {
    $game = Start-Process -FilePath $engine -ArgumentList $arguments -WorkingDirectory $Project `
        -RedirectStandardOutput $log -RedirectStandardError (Join-Path $session 'game.err') -PassThru
} finally {
    $env:FENNARA_RT_SPEC = $previous
}
Write-Host "  game pid  $($game.Id)"

# Readiness is a line the game prints once its first frame is readable.
$deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    if ($game.HasExited) { throw "the game exited before it reported readiness; log: $log" }
    if ((Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -Pattern 'FENNARA_RUNTIME_SESSION_READY' -Quiet)) {
        $ready = $true
        break
    }
}
if (-not $ready) { throw "the game did not report readiness within $ReadyTimeoutSeconds s; log: $log" }
Write-Host '  ready' -ForegroundColor Green

if ($Script) {
    $runId = 'run-' + (Get-Date -Format 'HHmmss')
    $statusPath = Join-Path $statusDir "$runId.json"
    $command = [ordered]@{
        action        = 'run_runtime_script'
        script_run_id = $runId
        session_id    = $sessionId
        script_path   = $Script
        status_path   = (As-Slash $statusPath)
    }
    $command | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $commandDir "$runId.json") -Encoding UTF8
    Write-Host "  script    $Script ($runId)"

    $scriptDeadline = (Get-Date).AddSeconds($ScriptTimeoutSeconds)
    $state = ''
    while ((Get-Date) -lt $scriptDeadline) {
        Start-Sleep -Milliseconds 700
        if (-not (Test-Path -LiteralPath $statusPath)) { continue }
        try { $state = (Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json).status } catch { continue }
        if ($state -eq 'completed' -or $state -eq 'failed') { break }
    }
    Write-Host "  script    $state"
}

$shots = Get-ChildItem -Path $session -Recurse -Filter *.png -ErrorAction SilentlyContinue
foreach ($shot in $shots) { Write-Host "  shot      $($shot.FullName)" }
Write-Host "  $($shots.Count) image(s) under $session"
Write-Host "  the game is still running as pid $($game.Id); close its window when done"
