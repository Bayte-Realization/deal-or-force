Add-Type -AssemblyName System.Drawing
$width = 768
$height = 512
$bmp = New-Object System.Drawing.Bitmap($width, $height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

# Stripes
$stripeHeight = $height / 3.0
$redBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(206, 17, 38))
$whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))
$blackBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 0, 0))
$greenBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 122, 61))

$g.FillRectangle($redBrush, 0, 0, $width, [int][Math]::Ceiling($stripeHeight))
$g.FillRectangle($whiteBrush, 0, [int][Math]::Floor($stripeHeight), $width, [int][Math]::Ceiling($stripeHeight))
$g.FillRectangle($blackBrush, 0, [int][Math]::Floor($stripeHeight * 2), $width, [int][Math]::Ceiling($stripeHeight))

function Draw-Star($cx, $cy, $outerR, $innerR) {
    $points = New-Object System.Drawing.PointF[] 10
    for ($i = 0; $i -lt 10; $i++) {
        $angle = [Math]::PI / 2.0 + ($i * [Math]::PI / 5.0)
        $r = if ($i % 2 -eq 0) { $outerR } else { $innerR }
        $x = $cx + $r * [Math]::Cos($angle)
        $y = $cy - $r * [Math]::Sin($angle)
        $points[$i] = New-Object System.Drawing.PointF($x, $y)
    }
    $g.FillPolygon($greenBrush, $points)
}

$centerY = $height / 2.0
$outerRadius = $stripeHeight * 0.36
$innerRadius = $outerRadius * 0.382

# Two green stars placed at 1/3 and 2/3 of flag width
Draw-Star ($width * (1.0 / 3.0)) $centerY $outerRadius $innerRadius
Draw-Star ($width * (2.0 / 3.0)) $centerY $outerRadius $innerRadius

$targetDir = "d:/Godot Projects/deal-or-force-godot-4-8/game/gfx/flags"
$bmp.Save("$targetDir/flag_syr.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Save("$targetDir/flag_syr_one_party_state.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Save("$targetDir/flag_syr_socialism.png", [System.Drawing.Imaging.ImageFormat]::Png)

$g.Dispose()
$bmp.Dispose()
Write-Host "Successfully generated 2-star Syrian Baathist flag!"
