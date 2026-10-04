param([string]$Recipe = 'Assets/ArtSource/Icons/minimal-icons.json',
    [string]$Directory = 'game/gfx/interface/icons')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$count = 0
foreach ($item in (Get-Content -LiteralPath $Recipe -Raw | ConvertFrom-Json)) {
    $path = Join-Path $Directory "icon_$($item.name).png"
    $image = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $path).Path)
    try {
        if ($image.Width -ne 96 -or $image.Height -ne 96) { throw "Wrong size: $path" }
        $filled = 0
        for ($y = 0; $y -lt 96; $y++) {
            for ($x = 0; $x -lt 96; $x++) {
                $pixel = $image.GetPixel($x, $y)
                if ($pixel.A -lt 16) { continue }
                $filled++
                # Alpha edges may round-trip with small RGB errors; check fully covered faces.
                if ($pixel.A -ge 250 -and ([Math]::Abs($pixel.R / 231.0 - $pixel.G / 189.0) -gt 0.02 -or
                    [Math]::Abs($pixel.R / 231.0 - $pixel.B / 105.0) -gt 0.02 -or $pixel.R -lt 110)) {
                    throw "Pixel outside the gold palette: $path at $x,$y"
                }
                if ($x -lt 2 -or $y -lt 2 -or $x -ge 94 -or $y -ge 94) { throw "Clipped silhouette: $path at $x,$y" }
            }
        }
        if ($filled -lt 200 -or $filled -gt 7000) { throw "Unexpected empty/solid icon: $path ($filled pixels)" }
        $count++
    } finally { $image.Dispose() }
}
Write-Output "PASS $count PNGs: decoded, 96x96, gold palette, transparent margins, non-empty silhouettes."
