# One redistribution register beside the executable, not a licenses/docs directory.
function Write-PlayerNotices([string]$ProjectRoot, [string]$OutputDirectory) {
    $register = Join-Path $ProjectRoot 'docs/THIRD_PARTY_MEDIA.md'
    $artSources = Join-Path $ProjectRoot 'docs/art_sources'
    if (-not (Test-Path -LiteralPath $register -PathType Leaf) -or
        -not (Test-Path -LiteralPath $artSources -PathType Container)) {
        throw 'Third-party media register or artwork provenance is missing.'
    }
    $parts = [Collections.Generic.List[string]]::new()
    $parts.Add("# Deal or Force - third-party notices`n`nRedistribution notices consolidated from the source registers. The game's own licence is in LICENSE and LICENSE-EXCEPTIONS.md.`n")
    $media = [IO.File]::ReadAllText($register).Replace('../README.md', 'README.md').Replace('../game/', 'game/')
    $media = $media.Replace('[art_sources](art_sources/)', '[original artwork](#original-artwork)')
    $parts.Add($media)
    $parts.Add("`n## Original artwork`n")
    # Source drawings and Godot import sidecars are not licence notices. Keep all textual
    # provenance, in deterministic order, without shipping a second asset source tree.
    foreach ($source in Get-ChildItem -LiteralPath $artSources -Recurse -File |
            Where-Object { $_.Extension -in @('.md', '.txt') } | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($artSources, $source.FullName).Replace('\', '/')
        $parts.Add("`n### Source: art_sources/$relative`n`n" + [IO.File]::ReadAllText($source.FullName))
    }
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
