# Banners for the five civic regionalist movements, drawn as the real cloths:
#   AIP - the Alaska flag (dark blue, the Dipper and Polaris in gold);
#   TNM - the Lone Star flag (blue hoist band, white over red, white star);
#   YCA - the California Republic bear flag (white, red star, brown bear, red base stripe);
#   KAU - the Hawaiian Kingdom flag (Union Jack canton, eight white-red-blue stripes);
#   RAS - the Silesian arms as a flag (the black eagle on gold).
# Political variants keep each base and add a regime accent, for the day one governs.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Flag([string]$path, [scriptblock]$paint)
{
    $width = 768; $height = 512
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    & $paint $graphics $width $height
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output "wrote $path"
}

function Fill([System.Drawing.Graphics]$g, [string]$colour, [single]$x, [single]$y, [single]$w, [single]$h)
{
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($colour))
    $g.FillRectangle($brush, $x, $y, $w, $h)
    $brush.Dispose()
}

function Star([System.Drawing.Graphics]$g, [single]$cx, [single]$cy, [single]$radius, [string]$colour)
{
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($colour))
    $points = New-Object 'System.Drawing.PointF[]' 10
    for ($p = 0; $p -lt 10; $p++)
    {
        $rad = (-90 + $p * 36) * [Math]::PI / 180
        $r = if ($p % 2 -eq 0) { $radius } else { $radius * 0.42 }
        $points[$p] = New-Object System.Drawing.PointF(($cx + $r * [Math]::Cos($rad)), ($cy + $r * [Math]::Sin($rad)))
    }
    $g.FillPolygon($brush, $points)
    $brush.Dispose()
}

# --- Alaska -----------------------------------------------------------------
$alaska = {
    param($g, $w, $h)
    Fill $g '#002776' 0 0 $w $h
    Star $g ($w * 0.70) ($h * 0.22) 30 '#ffce00'
    $dipper = @(@(0.28,0.44),@(0.36,0.48),@(0.44,0.52),@(0.52,0.54),@(0.58,0.62),@(0.50,0.72),@(0.40,0.68))
    foreach ($p in $dipper) { Star $g ($w * $p[0]) ($h * $p[1]) 13 '#ffce00' }
}

# --- Texas ------------------------------------------------------------------
$texas = {
    param($g, $w, $h)
    Fill $g '#ffffff' 0 0 $w $h
    Fill $g '#bf0a30' 0 ($h * 0.5) $w ($h * 0.5)
    Fill $g '#002868' 0 0 ($w / 3) $h
    Star $g ($w / 6) ($h * 0.5) ($h * 0.27) '#ffffff'
}

# --- California -------------------------------------------------------------
$california = {
    param($g, $w, $h)
    Fill $g '#f5f1e6' 0 0 $w $h
    Fill $g '#b22234' 0 ($h * 0.83) $w ($h * 0.17)
    Star $g ($w * 0.15) ($h * 0.28) 28 '#b22234'
    Fill $g '#6e4a2f' ($w * 0.34) ($h * 0.38) ($w * 0.34) ($h * 0.26)
    Fill $g '#6e4a2f' ($w * 0.62) ($h * 0.26) ($w * 0.13) ($h * 0.16)
    Fill $g '#6e4a2f' ($w * 0.37) ($h * 0.58) ($w * 0.05) ($h * 0.16)
    Fill $g '#6e4a2f' ($w * 0.50) ($h * 0.58) ($w * 0.05) ($h * 0.16)
    Fill $g '#6e4a2f' ($w * 0.60) ($h * 0.58) ($w * 0.05) ($h * 0.16)
}

# --- Hawaii -----------------------------------------------------------------
$hawaii = {
    param($g, $w, $h)
    $stripes = @('#ffffff', '#bf0a30', '#00247d', '#ffffff', '#bf0a30', '#00247d', '#ffffff', '#bf0a30')
    $band = $h / 8
    for ($i = 0; $i -lt 8; $i++) { Fill $g $stripes[$i] 0 ($i * $band) $w ($band + 1) }
    # the Union Jack canton over the first four stripes, half the flag's length
    $cw = $w * 0.5; $ch = $band * 4
    Fill $g '#00247d' 0 0 $cw $ch
    # white saltire and cross, red crosses over them, as the canton reads at flag distance
    $white = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#ffffff'), ($ch * 0.20))
    $red = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#cf142b'), ($ch * 0.10))
    $g.DrawLine($white, 0, 0, $cw, $ch); $g.DrawLine($white, $cw, 0, 0, $ch)
    $g.DrawLine($white, $cw * 0.5, 0, $cw * 0.5, $ch); $g.DrawLine($white, 0, $ch * 0.5, $cw, $ch * 0.5)
    $g.DrawLine($red, $cw * 0.5, 0, $cw * 0.5, $ch); $g.DrawLine($red, 0, $ch * 0.5, $cw, $ch * 0.5)
    $white.Dispose(); $red.Dispose()
}

# --- Silesia ----------------------------------------------------------------
$silesia = {
    param($g, $w, $h)
    Fill $g '#e3b23c' 0 0 $w $h
    # the black Silesian eagle, heraldic and spread: wings bar, body, head, leg markers
    Fill $g '#141414' ($w * 0.28) ($h * 0.36) ($w * 0.44) ($h * 0.18)
    Fill $g '#141414' ($w * 0.44) ($h * 0.26) ($w * 0.12) ($h * 0.36)
    Fill $g '#141414' ($w * 0.34) ($h * 0.50) ($w * 0.06) ($h * 0.18)
    Fill $g '#141414' ($w * 0.60) ($h * 0.50) ($w * 0.06) ($h * 0.18)
    Star $g ($w * 0.50) ($h * 0.20) 22 '#141414'
}

$dark = '#141414'
foreach ($set in @(
    @{ id = 'aip'; paint = $alaska },
    @{ id = 'tnm'; paint = $texas },
    @{ id = 'yca'; paint = $california },
    @{ id = 'kau'; paint = $hawaii },
    @{ id = 'ras'; paint = $silesia }
))
{
    New-Flag "game/gfx/flags/flag_$($set.id).png" $set.paint
    New-Flag "game/gfx/flags/flag_$($set.id)_communism.png" { param($g, $w, $h) & $set.paint $g $w $h; Fill $g '#8a1f1f' 0 0 $w 34 }
    New-Flag "game/gfx/flags/flag_$($set.id)_fascism.png" { param($g, $w, $h) & $set.paint $g $w $h; Fill $g $dark 0 0 $w 22; Fill $g $dark 0 ($h - 22) $w 22 }
    New-Flag "game/gfx/flags/flag_$($set.id)_theocracy.png" { param($g, $w, $h) & $set.paint $g $w $h; Fill $g '#f5f1e6' 0 ($h * 0.47) $w ($h * 0.06) }
    New-Flag "game/gfx/flags/flag_$($set.id)_absolute_monarchy.png" { param($g, $w, $h) & $set.paint $g $w $h; Fill $g '#e3b23c' 0 0 $w 26 }
}
