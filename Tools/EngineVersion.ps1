# The Godot build that DealOrForce.csproj was written against, as `godot --version` prints it.
#
#   Godot.NET.Sdk/4.8.0-beta.3  ->  4.8.beta3.
#   Godot.NET.Sdk/4.8.0-rc.1    ->  4.8.rc1.
#   Godot.NET.Sdk/4.8.0         ->  4.8.stable.
#   Godot.NET.Sdk/4.8.1         ->  4.8.1.stable.
#
# Comparing only `4.8.` let a beta2 editor export a project whose C# was built against
# beta3's GodotSharp. The export templates folder carries the same name
# (`export_templates\4.8.beta3.mono`), so this is also the template version the engine wants.
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
