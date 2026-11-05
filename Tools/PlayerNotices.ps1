# One redistribution register beside the executable, not a licenses/docs directory.
function Write-PlayerNotices([string]$ProjectRoot, [string]$OutputDirectory) {
    $register = Join-Path $ProjectRoot 'THIRD_PARTY_NOTICES.md'
    if (-not (Test-Path -LiteralPath $register -PathType Leaf)) {
        throw 'Player redistribution notices are missing.'
    }
    $parts = [Collections.Generic.List[string]]::new()
    # Ship the reviewed player document, not internal art briefs. Original-art provenance
    # stays in docs/art_sources; actual third-party attribution stays in this register/CREDITS.
    $parts.Add([IO.File]::ReadAllText($register))
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'libluau-script.windows.editor.x86_64.dll')) {
        foreach ($notice in @('LICENSE.txt', 'COPYRIGHT.txt')) {
            $source = Join-Path $ProjectRoot "game/extensions/luau/$notice"
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "Native adapter is missing its redistribution notice: $notice"
            }
            $parts.Add("`n## godot-luau-script - $notice`n`n" + [IO.File]::ReadAllText($source))
        }
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'THIRD_PARTY_NOTICES.md'),
        ($parts -join "`n---`n`n"), [Text.UTF8Encoding]::new($false))
    # These are build copies only. Source README links continue to point at docs/.
    foreach ($notice in @('README.md', 'README.ru.md', 'LICENSE-EXCEPTIONS.md')) {
        $copy = Join-Path $OutputDirectory $notice
        if (Test-Path -LiteralPath $copy -PathType Leaf) {
            $text = [IO.File]::ReadAllText($copy).Replace('docs/THIRD_PARTY_MEDIA.md', 'THIRD_PARTY_NOTICES.md')
            [IO.File]::WriteAllText($copy, $text, [Text.UTF8Encoding]::new($false))
        }
    }
}
