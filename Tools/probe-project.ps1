# A copy of the project that runs without the tooling autoloads.
#
# Why: every attempt to run the game from the project stalls before the first C# frame on this
# machine, and the log always stops right after the MCP runtime server registers its port. The
# editor's helpers - Fennara's capture hook, the MCP runtime server, the godot_ai game helper -
# are autoloads, so they run before any screen exists. A game does not need any of them.
#
# So this builds `tmp\probe_run\`: the same project, the same data, the same assembly, reached
# through directory junctions to the real folders, with one difference - `project.godot` names
# only BootGuard as an autoload. Nothing is copied but the project file and the icon, so the run
# sees exactly the code and content being worked on, and the copy is cheap enough to rebuild
# anytime.
#
# It lives under `tmp\`, which carries a `.gdignore`: the editor walks `res://` and follows
# directory junctions, so a probe project next to the real folders made every editor start scan
# the whole project a second time (and import the output of every earlier run). Inside an ignored
# folder the editor never sees it, and the probe's own `--path` is unaffected because `.gdignore`
# only hides a folder from the editor's filesystem scan.
#
#   powershell -ExecutionPolicy Bypass -File Tools\probe-project.ps1
#   <godot> --verbose --path <project>\tmp\probe_run -- --verify-build --verify-development \
#           --verify-build-output <project>\tmp_playtest\shots
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$target = Join-Path $project 'tmp\probe_run'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null

if (Test-Path -LiteralPath $target) {
    # Junctions only: never recurse into them, or the delete walks the real folders.
    Get-ChildItem -LiteralPath $target -Force | ForEach-Object {
        if ($_.LinkType) { cmd /c rmdir "`"$($_.FullName)`"" } else { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    }
} else {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
}

foreach ($folder in 'game', 'Scripts', 'Scenes', 'addons', 'mods', 'dlc', 'Assets', 'Tools', '.godot') {
    $source = Join-Path $project $folder
    if (-not (Test-Path -LiteralPath $source)) { continue }
    cmd /c mklink /J "`"$(Join-Path $target $folder)`"" "`"$source`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "junction failed for $folder" }
}

foreach ($file in 'icon.svg', 'icon.png', 'icon.ico', 'icon_small.png') {
    $source = Join-Path $project $file
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $target -Force }
}

# The one real difference: every autoload except the start-up guard is gone.
#
# Not just the three tooling ones. `Ezcha`, `LitManager`, `GDSync` and `DialogueManager` come
# back by themselves: a Godot plugin registers its singleton with the editor, and the editor
# writes the line into project.godot again on the next save. That is why removing those four by
# hand is not durable - it holds until the editor is opened. In this copy nothing re-registers
# them, because the editor is not opening this project.
$lines = Get-Content -LiteralPath (Join-Path $project 'project.godot')
$keep = $lines | Where-Object {
    $_ -notmatch '^_fennara_game_capture=' -and
    $_ -notmatch '^MCPRuntimeServer=' -and
    $_ -notmatch '^_mcp_game_helper=' -and
    $_ -notmatch '^GDSync=' -and
    $_ -notmatch '^DialogueManager=' -and
    $_ -notmatch '^Ezcha=' -and
    $_ -notmatch '^LitManager='
}
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllLines((Join-Path $target 'project.godot'), $keep, $utf8)

Write-Host ("probe project ready: {0}" -f $target) -ForegroundColor Green
($keep | Where-Object { $_ -match '^[A-Za-z_][A-Za-z0-9_]*="\*res://' }) | ForEach-Object { Write-Host ("  autoload " + $_) -ForegroundColor DarkGray }
