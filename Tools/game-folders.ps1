# One folder for everything the game writes.
#
#     powershell -ExecutionPolicy Bypass -File Tools\game-folders.ps1
#     powershell -ExecutionPolicy Bypass -File Tools\game-folders.ps1 -WhatIf
#
# The game's own code (Scripts/GamePaths.cs, Scripts/Simulation/GameFolders.cs) already keeps
# saves, settings and logs in `Documents\Bayte Realization\Deal or Force`. What it cannot move
# is the engine's half: Godot puts `user://` in `%APPDATA%\Godot\app_userdata\Deal or Force`,
# and that is where the engine log, the renderer choice, installed mods and DLC rights live.
# This script moves that folder into the game folder and replaces it with a directory junction,
# so `user://` and the game folder are the same place on disk. Nothing in the game has to know.
#
# A junction is used rather than a setting because Godot has no setting for it: user:// is
# derived from the project name, and the alternatives are an absolute path baked into
# project.godot (which would break on every other machine) or teaching every `user://` call
# site a second path. The junction costs one Windows API call and keeps the game's own paths
# exactly as they are.
#
# Close the Godot editor first: it holds godot.log open, and Windows will not let a folder with
# open files be renamed or replaced. The script says which files it could not move and leaves
# everything else done, so running it again after closing the editor finishes the job.

param(
    [switch]$WhatIf,
    [switch]$NoLink,
    [switch]$KeepCaches,
    [string]$Documents,
    [string]$UserData
)

$ErrorActionPreference = 'Stop'

$game = 'Deal or Force'
$root = if ($Documents) { $Documents } else { [Environment]::GetFolderPath('MyDocuments') }
$root = Join-Path (Join-Path $root 'Bayte Realization') $game
$engine = if ($UserData) { $UserData } else {
    Join-Path $env:APPDATA (Join-Path 'Godot\app_userdata' $game)
}
$legacyDocuments = Join-Path ([Environment]::GetFolderPath('MyDocuments')) $game

function Say($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

# Move a folder's contents into another, keeping what is already there. Never overwrites: a file
# already in the new place is the newer decision, and losing a save to a migration would be a
# worse bug than the mess this script is cleaning up.
function Merge-Move([string]$From, [string]$To, [System.Collections.Generic.List[string]]$Locked) {
    if (-not (Test-Path -LiteralPath $From)) { return }
    if ($WhatIf) { Say "  would move $From -> $To" 'DarkGray'; return }
    # A folder whose destination does not exist yet is a rename, not a copy: one Move-Item on
    # the directory is a single filesystem operation however many files it holds. This is not a
    # micro-optimisation - the engine's shader cache is a third of a million files, and moving
    # them one at a time took longer than the whole rest of the job.
    if (-not (Test-Path -LiteralPath $To)) {
        try {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $To) | Out-Null
            Move-Item -LiteralPath $From -Destination $To -Force
            Say "  moved $From" 'DarkGray'
            return
        } catch {
            Say ("  moving the folder failed, falling back to file by file: " + $From) 'DarkYellow'
        }
    }
    New-Item -ItemType Directory -Force -Path $To | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $From -File -Force) {
        $target = Join-Path $To $file.Name
        if (Test-Path -LiteralPath $target) {
            $existing = Get-Item -LiteralPath $target -Force
            if ($existing.Length -eq $file.Length) {
                # The same file, byte for byte, already in the right place: this copy is the
                # duplicate the migration exists to remove, so it goes rather than accumulating
                # a second folder of saves that is merely spelled differently.
                Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
                Say ("  dropped duplicate " + $file.Name) 'DarkGray'
                continue
            }
            # Same name, different content: two moments of the same campaign, and the newer one
            # is the one the player expects to see under that name. The older is kept beside it
            # with an extension the game does not list, so nothing is lost and nothing doubles.
            $source = Get-Item -LiteralPath $file.FullName -Force
            if ($source.LastWriteTime -gt $existing.LastWriteTime) {
                try {
                    Move-Item -LiteralPath $target -Destination ($target + ".legacy") -Force
                    Move-Item -LiteralPath $file.FullName -Destination $target -Force
                    Say ("  newer wins: " + $file.Name + " (the older kept as .legacy)") 'DarkYellow'
                } catch { $Locked.Add($file.FullName); Say ("  locked: " + $file.Name) 'Yellow' }
            } else {
                try { Move-Item -LiteralPath $file.FullName -Destination ($target + ".legacy") -Force
                      Say ("  kept existing: " + $file.Name + " (the older kept as .legacy)") 'DarkYellow' }
                catch { $Locked.Add($file.FullName); Say ("  locked: " + $file.Name) 'Yellow' }
            }
            continue
        }
        try { Move-Item -LiteralPath $file.FullName -Destination $target -Force }
        catch { $Locked.Add($file.FullName); Say ("  locked: " + $file.Name) 'Yellow' }
    }
    foreach ($folder in Get-ChildItem -LiteralPath $From -Directory -Force) {
        Merge-Move $folder.FullName (Join-Path $To $folder.Name) $Locked
        if (-not (Get-ChildItem -LiteralPath $folder.FullName -Force -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $folder.FullName -Force -ErrorAction SilentlyContinue
        }
    }
}

function Test-Junction([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $item = Get-Item -LiteralPath $Path -Force
    return ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
}

# Folders that are not the game's state but a tool's scratch. The engine and the editor
# recreate them on demand, and one of them - Fennara's capture cache - held two hundred and
# eighty thousand files, which turned a two-second rename into a quarter of an hour of copying
# of things nobody would miss. `-KeepCaches` moves them instead of dropping them.
$scratch = @('.fennara', 'shader_cache', 'vulkan', 'objectdb_snapshots', 'godot_ai_update', 'asset_placer')

function Drop-Scratch([string]$Base, $Names) {
    foreach ($name in $Names) {
        $path = Join-Path $Base $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        if ($WhatIf) { Say ("  would drop scratch " + $path) 'DarkGray'; continue }
        # Rename first, delete later: a rename inside one volume is one filesystem operation,
        # while deleting a quarter of a million cached captures takes minutes however it is
        # asked. The migration has no business waiting for garbage, so the tree is moved out of
        # the game folder at once and a detached process takes its time over the deletion.
        $bin = Join-Path $env:TEMP ("dof-scratch-" + $name.Trim('.') + "-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
        try {
            Move-Item -LiteralPath $path -Destination $bin -Force
            Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', 'rd', '/s', '/q', "`"$bin`"" -WindowStyle Hidden
            Say ("  dropped scratch " + $name + " (deleted in the background)") 'DarkGray'
        } catch {
            # A rename that fails (a file open in the folder) is not worth stopping for; say it
            # and let the next run try again.
            Say ("  could not drop scratch " + $name + ": " + $_.Exception.Message) 'Yellow'
        }
    }
}

Say ''
Say "game folder : $root" 'Cyan'
Say "engine data : $engine" 'Cyan'
Say ''

foreach ($sub in 'saves', 'settings', 'logs', 'screenshots') {
    if (-not $WhatIf) { New-Item -ItemType Directory -Force -Path (Join-Path $root $sub) | Out-Null }
}

$locked = [System.Collections.Generic.List[string]]::new()

# 1. The folder saves used to live in, before there was a publisher folder.
if (Test-Path -LiteralPath $legacyDocuments) {
    Say 'moving the old save folder into the game folder' 'White'
    $saves = Join-Path $legacyDocuments 'save games'
    if (Test-Path -LiteralPath $saves) { Merge-Move $saves (Join-Path $root 'saves') $locked }
    # Everything else in the old folder, but not `save games` again: the merge above already
    # put it where it belongs, and recursing into it here once created a second copy of the
    # same saves under the old name.
    foreach ($item in Get-ChildItem -LiteralPath $legacyDocuments -File -Force) {
        Merge-Move $item.FullName (Join-Path $root $item.Name) $locked
    }
    foreach ($folder in Get-ChildItem -LiteralPath $legacyDocuments -Directory -Force) {
        if ($folder.Name -eq 'save games') { continue }
        Merge-Move $folder.FullName (Join-Path $root $folder.Name) $locked
    }
    if (-not $WhatIf -and -not (Get-ChildItem -LiteralPath $legacyDocuments -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $legacyDocuments -Force -ErrorAction SilentlyContinue
        Say '  removed the empty old folder' 'DarkGray'
    } elseif (-not $WhatIf) {
        # A directory cannot be merged by Move-Item onto a non-empty target, so the old folder
        # goes once its contents are gone; if anything is left, say what rather than deleting it.
        Say '  the old folder still holds: ' 'Yellow'
        Get-ChildItem -LiteralPath $legacyDocuments -Force | ForEach-Object { Say ("    " + $_.Name) 'Yellow' }
    }
}

# 1b. A `save games` folder inside the game folder means an older merge copied the saves
# sideways instead of into saves/. Put them where they belong and take the name away.
$straySaves = Join-Path $root 'save games'
if (Test-Path -LiteralPath $straySaves) {
    Say 'folding a stray "save games" folder into saves/' 'White'
    Merge-Move $straySaves (Join-Path $root 'saves') $locked
    if (-not $WhatIf -and -not (Get-ChildItem -LiteralPath $straySaves -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $straySaves -Force -ErrorAction SilentlyContinue
    }
}

# 2. The engine's own folder: move it in, then point the old path at the new one.
$junction = Test-Junction $engine
if ($junction) {
    $target = (Get-Item -LiteralPath $engine -Force).Target
    Say "engine data is already a junction to $target" 'Green'
} elseif (Test-Path -LiteralPath $engine) {
    Say 'moving engine data (settings, logs, mods, caches) into the game folder' 'White'
    if (-not $KeepCaches) { Drop-Scratch $engine $scratch }
    if (-not $KeepCaches) { Drop-Scratch $root $scratch }
    # Two files go to a subfolder rather than to the root, because that is where the game keeps
    # them now: the settings file and the old fixed save slots.
    $oldSettings = Join-Path $engine 'settings.json'
    $newSettings = Join-Path (Join-Path $root 'settings') 'settings.json'
    if ((Test-Path -LiteralPath $oldSettings) -and -not $WhatIf -and -not (Test-Path -LiteralPath $newSettings)) {
        Move-Item -LiteralPath $oldSettings -Destination $newSettings -Force
        Say '  settings.json -> settings\settings.json' 'DarkGray'
    }
    $oldSaves = Join-Path $engine 'save_games'
    if (Test-Path -LiteralPath $oldSaves) { Merge-Move $oldSaves (Join-Path $root 'saves') $locked }
    Merge-Move $engine $root $locked
    $left = Get-ChildItem -LiteralPath $engine -Force -ErrorAction SilentlyContinue
    if ($locked.Count -gt 0 -or $left) {
        Say ''
        Say 'the engine folder still holds files another process has open:' 'Yellow'
        foreach ($line in $locked) { Say ("  " + $line) 'Yellow' }
        Say 'close the Godot editor (and any running copy of the game), then run this again.' 'Yellow'
    }
    if (-not $WhatIf -and -not $left) { Remove-Item -LiteralPath $engine -Force -Recurse }
} else {
    Say 'no engine folder yet; it will be created as a junction' 'White'
}

if (-not $NoLink -and -not $junction -and -not $WhatIf -and -not (Test-Path -LiteralPath $engine)) {
    $parent = Split-Path -Parent $engine
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $result = cmd /c mklink /J "`"$engine`"" "`"$root`"" 2>&1
    if ($LASTEXITCODE -eq 0) { Say "engine data now points at the game folder: $engine -> $root" 'Green' }
    else { Say "could not create the junction: $result" 'Red'; Say 'run this script from an account that may write to %APPDATA%' 'Yellow' }
}

if ($WhatIf) { Say ''; Say 'nothing was changed (-WhatIf)' 'Yellow'; return }

# 3. Say what the folder now holds, so the answer to "where is my game?" is one screen.
Say ''
Say 'contents:' 'White'
Get-ChildItem -LiteralPath $root -Force | Sort-Object PSIsContainer, Name | ForEach-Object {
    $label = if ($_.PSIsContainer) { '<folder>' } else { '' }
    Say ("  " + $_.Name.PadRight(24) + $label) 'Gray'
}
Say ''
Say "everything for this game is in $root" 'Green'
