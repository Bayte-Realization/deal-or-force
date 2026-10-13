param([string]$Recipe = 'Tools/IconCompiler/Source/Icons/minimal-icons.json',
    [string]$Directory = 'game/gfx/interface/icons',
    [string]$Colour = '#e7bd69',
    [string]$GoodsColour = '#b48732')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$count = 0
foreach ($item in (Get-Content -LiteralPath $Recipe -Raw | ConvertFrom-Json)) {
    $reference = [Drawing.ColorTranslator]::FromHtml($(if ($item.name.StartsWith('good_')) { $GoodsColour } else { $Colour }))
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
                if ($pixel.A -ge 250 -and ([Math]::Abs($pixel.R / [double]$reference.R - $pixel.G / [double]$reference.G) -gt 0.025 -or
                    [Math]::Abs($pixel.R / [double]$reference.R - $pixel.B / [double]$reference.B) -gt 0.025 -or $pixel.R -lt 100)) {
                    throw "Pixel outside the gold palette: $path at $x,$y"
                }
                if ($item.name.StartsWith('good_') -and $pixel.A -ge 250 -and
                    ([Math]::Abs($pixel.R - $reference.R) -gt 2 -or [Math]::Abs($pixel.G - $reference.G) -gt 2 -or [Math]::Abs($pixel.B - $reference.B) -gt 2)) {
                    throw "Commodity icon must use one flat bronze colour: $path at $x,$y"
                }
                if ($x -lt 2 -or $y -lt 2 -or $x -ge 94 -or $y -ge 94) { throw "Clipped silhouette: $path at $x,$y" }
            }
        }
        if ($filled -lt 200 -or $filled -gt 7000) { throw "Unexpected empty/solid icon: $path ($filled pixels)" }
        $count++
    } finally { $image.Dispose() }
}
Write-Output "PASS $count PNGs: decoded, 96x96, gold palette, transparent margins, non-empty silhouettes."
