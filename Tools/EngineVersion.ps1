# The Godot build that DealOrForce.csproj was written against, as `godot --version` prints it.
#
#   Godot.NET.Sdk/4.8.0-beta.5  ->  4.8.beta5.
#   Godot.NET.Sdk/4.8.0-rc.1    ->  4.8.rc1.
#   Godot.NET.Sdk/4.8.0         ->  4.8.stable.
#   Godot.NET.Sdk/4.8.1         ->  4.8.1.stable.
#
# Comparing only `4.8.` let an older beta editor export a project whose C# was built against
# beta5's GodotSharp. The export templates folder carries the same name
# (`export_templates\4.8.beta5.mono`), so this is also the template version the engine wants.
# Windows PowerShell 5.1 dot-sources this too: keep it free of 7-only syntax.

function Get-ExpectedEngineVersionPrefix([string]$CsprojPath) {
    $sdk = ([xml](Get-Content -LiteralPath $CsprojPath -Raw)).Project.Sdk
    if ($sdk -notmatch '/(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<status>[A-Za-z]+)\.?(?<number>\d+))?') {
        throw "Cannot read a Godot version from the SDK '$sdk' in $CsprojPath."
    }
    $prefix = "$($Matches.major).$($Matches.minor)."
    if ($Matches.patch -ne '0') { $prefix += "$($Matches.patch)." }
    if ($Matches.status) { $prefix += "$($Matches.status.ToLowerInvariant())$($Matches.number)." }
    else { $prefix += 'stable.' }
    return $prefix
}

function Resolve-MatchingGodot(
    [string]$ProjectRoot,
    [string]$ExplicitGodot = '',
    [string]$DefaultFallback = 'C:\GodotEngineNet 4.8-Beta5\Godot_v4.8-beta5_mono_win64_console.exe'
) {
    $csproj = Join-Path $ProjectRoot 'DealOrForce.csproj'
    $wanted = if (Test-Path -LiteralPath $csproj) { Get-ExpectedEngineVersionPrefix $csproj } else { '' }
    $candidates = New-Object System.Collections.ArrayList
    if ($ExplicitGodot) { $null = $candidates.Add($ExplicitGodot) }
    if ($env:GODOT_EXECUTABLE) { $null = $candidates.Add($env:GODOT_EXECUTABLE) }
    $config = Join-Path $ProjectRoot '.mcp.json'
    if (Test-Path -LiteralPath $config) {
        try {
            $mcpPath = (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).mcpServers.'godot-coding-solo'.env.GODOT_PATH
            if ($mcpPath) { $null = $candidates.Add($mcpPath) }
        } catch { }
    }
    if ($DefaultFallback) { $null = $candidates.Add($DefaultFallback) }

    foreach ($cand in $candidates) {
        if (-not $cand -or -not (Test-Path -LiteralPath $cand -PathType Leaf)) { continue }
        if (-not $wanted) { return $cand }
        $ver = (& $cand --version 2>$null | Select-Object -Last 1)
        $verText = if ($ver) { $ver.ToString().Trim() } else { '' }
        if ($verText.StartsWith($wanted, [StringComparison]::Ordinal)) {
            return $cand
        }
        if ($ExplicitGodot -and $cand -eq $ExplicitGodot) {
            throw "Godot $verText ($cand) does not match the project's $wanted SDK."
        }
    }
    throw "No Godot executable matching $wanted found (checked explicit, GODOT_EXECUTABLE, .mcp.json, and $DefaultFallback)."
}
