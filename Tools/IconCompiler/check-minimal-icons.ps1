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
                # GDI+ round-trips premultiplied antialias edges with a small RGB rounding error.
                if ($pixel.R -ne $pixel.G -or $pixel.G -ne $pixel.B -or ($pixel.A -ge 250 -and $pixel.R -lt 250)) {
                    throw "Non-monochrome pixel: $path at $x,$y"
                }
                if ($x -lt 2 -or $y -lt 2 -or $x -ge 94 -or $y -ge 94) { throw "Clipped silhouette: $path at $x,$y" }
            }
        }
        if ($filled -lt 200 -or $filled -gt 7000) { throw "Unexpected empty/solid icon: $path ($filled pixels)" }
        $count++
    } finally { $image.Dispose() }
}
Write-Output "PASS $count minimal PNGs: decoded, 96x96, monochrome, transparent margins, non-empty silhouettes."
