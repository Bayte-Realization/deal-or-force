param([string]$OutputDirectory = "game/gfx/flags")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Draw-Star([System.Drawing.Graphics]$g, [System.Drawing.Brush]$brush, [float]$cx, [float]$cy, [float]$rOuter, [float]$rInner, [int]$points = 5, [float]$startAngle = -90.0) {
    $pts = New-Object System.Drawing.PointF[] ($points * 2)
    $step = [Math]::PI / $points
    $angle = $startAngle * [Math]::PI / 180.0
    for ($i = 0; $i -lt ($points * 2); $i++) {
        $r = if ($i % 2 -eq 0) { $rOuter } else { $rInner }
        $pts[$i] = New-Object System.Drawing.PointF ($cx + $r * [Math]::Cos($angle)), ($cy + $r * [Math]::Sin($angle))
        $angle += $step
    }
    $g.FillPolygon($brush, $pts)
}

function Save-Flag([string]$name, [scriptblock]$drawAction) {
    $path = Join-Path $OutputDirectory $name
    $bmp = New-Object System.Drawing.Bitmap 768, 480, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    & $drawAction $g
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Generated: $path"
}

# 1. ARA Communism (Red field, golden star & emblem, green base stripe)
Save-Flag "flag_ara_communism.png" {
    param($g)
    $bRed = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(215, 25, 32))
    $bGreen = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 110, 50))
    $bGold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 215, 0))
    $g.FillRectangle($bRed, 0, 0, 768, 380)
    $g.FillRectangle($bGreen, 0, 380, 768, 100)
    Draw-Star $g $bGold 140 130 55 24 5
}

# 2. ARA Fascism (Red and dark green diagonal slash with white diamond and sword/star emblem)
Save-Flag "flag_ara_fascism.png" {
    param($g)
    $bRed = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(180, 20, 25))
    $bGreen = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 70, 35))
    $bWhite = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $bDark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 30, 30))
    $g.FillRectangle($bRed, 0, 0, 768, 480)
    $pts = @(
        (New-Object System.Drawing.PointF 0, 480),
        (New-Object System.Drawing.PointF 768, 180),
        (New-Object System.Drawing.PointF 768, 480)
    )
    $g.FillPolygon($bGreen, $pts)
    # Diamond in center
    $diamond = @(
        (New-Object System.Drawing.PointF 384, 150),
        (New-Object System.Drawing.PointF 474, 240),
        (New-Object System.Drawing.PointF 384, 330),
        (New-Object System.Drawing.PointF 294, 240)
    )
    $g.FillPolygon($bWhite, $diamond)
    Draw-Star $g $bDark 384 240 45 20 5
}

# 3. ARA Theocracy (Buddhist red, saffron/gold, and green sacred wheel/emblem)
Save-Flag "flag_ara_theocracy.png" {
    param($g)
    $bSaffron = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 160, 20))
    $bRed = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(190, 25, 30))
    $bGreen = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 115, 55))
    $bWhite = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $bGold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 220, 40))
    $g.FillRectangle($bRed, 0, 0, 768, 160)
    $g.FillRectangle($bSaffron, 0, 160, 768, 160)
    $g.FillRectangle($bGreen, 0, 320, 768, 160)
    # Sacred solar/dharmic disc in center
    $g.FillEllipse($bWhite, 314, 170, 140, 140)
    $g.FillEllipse($bGold, 324, 180, 120, 120)
    Draw-Star $g $bRed 384 240 38 18 8
}

# 4. ARA Absolute Monarchy (Royal Mrauk-U Kingdom red & green with golden crown / peacock royal crest)
Save-Flag "flag_ara_absolute_monarchy.png" {
    param($g)
    $bRed = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 25, 30))
    $bGreen = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 120, 50))
    $bGold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 215, 0))
    $g.FillRectangle($bRed, 0, 0, 768, 240)
    $g.FillRectangle($bGreen, 0, 240, 768, 240)
    # Royal crown in center
    $x = 384 - 70
    $y = 240 - 55
    $pts = @(
        (New-Object System.Drawing.PointF $x, ($y + 110)),
        (New-Object System.Drawing.PointF ($x + 140), ($y + 110)),
        (New-Object System.Drawing.PointF ($x + 140), ($y + 25)),
        (New-Object System.Drawing.PointF ($x + 105), ($y + 60)),
        (New-Object System.Drawing.PointF ($x + 70), $y),
        (New-Object System.Drawing.PointF ($x + 35), ($y + 60)),
        (New-Object System.Drawing.PointF $x, ($y + 25))
    )
    $g.FillPolygon($bGold, $pts)
    Draw-Star $g $bRed 384 210 18 8 5
}
