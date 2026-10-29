# Close the Godot things that are left running.
#
# Three kinds of process share the name:
#
#   Godot_v4.8-beta2_mono_win64.exe  with a window title - an editor somebody opened. It holds
#       the project and every GDExtension DLL, so a second editor or a headless run cannot copy
#       `~lib*.dll` while it lives. Closing it loses unsaved scenes, so this script never
#       touches it unless you ask with -Editors.
#   Godot_v4.8-beta2_mono_win64_console.exe  a command-line run: a probe, a test suite, a
#       capture. Nothing to save; safe to close.
#   godot-ai.exe  Fennara's helper daemon, started by the MCP server (`attach --port 8016`).
#       It exists so the assistant can drive a running game. It is recreated on demand, so
#       closing it costs nothing but a moment on the next tool call.
#
# Usage:
#   Tools\close-godot.ps1              list, change nothing
#   Tools\close-godot.ps1 -Daemons     close godot-ai helpers
#   Tools\close-godot.ps1 -Runs        close command-line Godot runs
#   Tools\close-godot.ps1 -Editors     close editors too (unsaved work is lost)
#   Tools\close-godot.ps1 -All -Yes    everything, without asking
param(
    [switch]$Daemons,
    [switch]$Runs,
    [switch]$Editors,
    [switch]$All,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

function Show-State {
    $rows = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -match '^(Godot|godot-ai)' } |
        Sort-Object ProcessName, StartTime |
        ForEach-Object {
            $kind = if ($_.ProcessName -eq 'godot-ai') { 'daemon' }
                    elseif ($_.MainWindowTitle) { 'editor' }
                    else { 'run' }
            $started = try { $_.StartTime.ToString('HH:mm') } catch { '?' }
            [pscustomobject]@{
                Kind    = $kind
                Id      = $_.Id
                Name    = $_.ProcessName
                Started = $started
                RamMB   = [int]($_.WorkingSet64 / 1MB)
                Window  = if ($_.MainWindowTitle) { $_.MainWindowTitle } else { '' }
            }
        }
    if (-not $rows) {
        Write-Host 'No Godot processes are running.'
        return @()
    }
    $rows | Format-Table -AutoSize | Out-String -Width 120 | Write-Host
    return @($rows)
}

if ($All) { $Daemons = $true; $Runs = $true; $Editors = $true }

$state = Show-State
if (-not $Daemons -and -not $Runs -and -not $Editors) {
    Write-Host 'Nothing closed. Add -Daemons, -Runs, -Editors or -All.'
    exit 0
}

$wanted = @()
if ($Daemons) { $wanted += 'daemon' }
if ($Runs) { $wanted += 'run' }
if ($Editors) { $wanted += 'editor' }
$targets = @($state | Where-Object { $wanted -contains $_.Kind })
if (-not $targets) {
    Write-Host 'Nothing to close of the kinds asked for.'
    exit 0
}

if ($Editors -and -not $Yes) {
    Write-Host ''
    Write-Host 'Editors are about to be closed. Unsaved scenes and scripts are lost.'
    $answer = Read-Host 'Type yes to continue'
    if ($answer -ne 'yes') { Write-Host 'Cancelled.'; exit 1 }
}

foreach ($p in $targets) {
    Write-Host ("closing {0} {1} ({2})" -f $p.Kind, $p.Id, $p.Name)
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}

Start-Sleep -Milliseconds 700
Write-Host ''
Show-State | Out-Null
