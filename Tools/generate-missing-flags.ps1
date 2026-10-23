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

function Draw-SunRays([System.Drawing.Graphics]$g, [System.Drawing.Brush]$brush, [float]$cx, [float]$cy, [float]$rInner, [float]$rOuter, [int]$count = 21) {
    $g.FillEllipse($brush, ($cx - $rInner), ($cy - $rInner), ($rInner * 2), ($rInner * 2))
    $step = (2 * [Math]::PI) / $count
    for ($i = 0; $i -lt $count; $i++) {
        $angle = $i * $step - ([Math]::PI / 2)
        $tipX = $cx + $rOuter * [Math]::Cos($angle)
        $tipY = $cy + $rOuter * [Math]::Sin($angle)
        $baseAngle1 = $angle - ($step * 0.22)
        $baseAngle2 = $angle + ($step * 0.22)
        $b1X = $cx + ($rInner * 0.95) * [Math]::Cos($baseAngle1)
        $b1Y = $cy + ($rInner * 0.95) * [Math]::Sin($baseAngle1)
        $b2X = $cx + ($rInner * 0.95) * [Math]::Cos($baseAngle2)
        $b2Y = $cy + ($rInner * 0.95) * [Math]::Sin($baseAngle2)
        $tri = @(
            (New-Object System.Drawing.PointF $tipX, $tipY),
            (New-Object System.Drawing.PointF $b1X, $b1Y),
            (New-Object System.Drawing.PointF $b2X, $b2Y)
        )
        $g.FillPolygon($brush, $tri)
    }
}

function Draw-Crown([System.Drawing.Graphics]$g, [System.Drawing.Brush]$brush, [float]$cx, [float]$cy, [float]$w, [float]$h) {
    $x = $cx - $w / 2
    $y = $cy - $h / 2
    $pts = @(
        (New-Object System.Drawing.PointF $x, ($y + $h)),
        (New-Object System.Drawing.PointF ($x + $w), ($y + $h)),
        (New-Object System.Drawing.PointF ($x + $w), ($y + $h * 0.2)),
        (New-Object System.Drawing.PointF ($x + $w * 0.75), ($y + $h * 0.55)),
        (New-Object System.Drawing.PointF ($x + $w * 0.5), $y),
        (New-Object System.Drawing.PointF ($x + $w * 0.25), ($y + $h * 0.55)),
        (New-Object System.Drawing.PointF $x, ($y + $h * 0.2))
    )
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
    Write-Host "Generated $name"
}

# -------------------------------------------------------------
# 1. KRG: Kurdistan Region (Red, White, Green + 21-ray golden sun)
# -------------------------------------------------------------
Save-Flag "flag_krg.png" { param($g)
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 35, 45))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 250, 250))
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 140, 60))
    $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 195, 15))
    $g.FillRectangle($red, 0, 0, 768, 160)
    $g.FillRectangle($white, 0, 160, 768, 160)
    $g.FillRectangle($green, 0, 320, 768, 160)
    Draw-SunRays $g $gold 384 240 50 85 21
}

# -------------------------------------------------------------
# 2. PNT: Puntland State of Somalia (Light Blue + white star, White, Green)
# -------------------------------------------------------------
Save-Flag "flag_pnt.png" { param($g)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(65, 140, 215))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 250, 250))
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(35, 155, 60))
    $g.FillRectangle($blue, 0, 0, 768, 160)
    $g.FillRectangle($white, 0, 160, 768, 160)
    $g.FillRectangle($green, 0, 320, 768, 160)
    Draw-Star $g $white 384 80 45 18 5 -90
}

# -------------------------------------------------------------
# 3. UWS: Wa State / United Wa State Army (Red, Blue + yellow star and emblem)
# -------------------------------------------------------------
Save-Flag "flag_uws.png" { param($g)
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 30, 35))
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(25, 70, 160))
    $yellow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 210, 30))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 250, 250))
    $g.FillRectangle($red, 0, 0, 768, 240)
    $g.FillRectangle($blue, 0, 240, 768, 240)
    # Golden star on canton or center
    Draw-Star $g $yellow 384 240 85 36 5 -90
    $pen = New-Object System.Drawing.Pen $yellow, 6
    $g.DrawEllipse($pen, 384 - 110, 240 - 110, 220, 220)
}

# -------------------------------------------------------------
# 4. MND: MNDAA / Kokang (Red, Blue, Green + white star emblem)
# -------------------------------------------------------------
Save-Flag "flag_mnd.png" { param($g)
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(215, 30, 35))
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 75, 170))
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 140, 55))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 250, 250))
    $g.FillRectangle($red, 0, 0, 768, 160)
    $g.FillRectangle($blue, 0, 160, 768, 160)
    $g.FillRectangle($green, 0, 320, 768, 160)
    $pen = New-Object System.Drawing.Pen $white, 7
    $g.DrawEllipse($pen, 384 - 65, 240 - 65, 130, 130)
    Draw-Star $g $white 384 240 50 20 5 -90
}

# -------------------------------------------------------------
# 5. TNL: Ta'ang National Liberation Army / Palaung (Green, Yellow, Red)
# -------------------------------------------------------------
Save-Flag "flag_tnl.png" { param($g)
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 135, 55))
    $yellow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 205, 25))
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(215, 35, 40))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 250, 250))
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(25, 65, 150))
    $g.FillRectangle($green, 0, 0, 768, 160)
    $g.FillRectangle($blue, 0, 160, 768, 160)
    $g.FillRectangle($red, 0, 320, 768, 160)
    $g.FillEllipse($yellow, 384 - 60, 240 - 60, 120, 120)
    Draw-Star $g $white 384 240 45 18 5 -90
}

# -------------------------------------------------------------
# 6. SPN: SPLM-North (Black, White fimbriation, Red, Green + Blue triangle with gold star)
# -------------------------------------------------------------
Save-Flag "flag_spn.png" { param($g)
    $black = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 20, 20))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 250, 250))
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(210, 30, 35))
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 130, 50))
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 80, 180))
    $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 210, 30))
    $g.FillRectangle($black, 0, 0, 768, 140)
    $g.FillRectangle($white, 0, 140, 768, 20)
    $g.FillRectangle($red, 0, 160, 768, 160)
    $g.FillRectangle($white, 0, 320, 768, 20)
    $g.FillRectangle($green, 0, 340, 768, 140)
    $tri = @(
        (New-Object System.Drawing.PointF 0, 0),
        (New-Object System.Drawing.PointF 300, 240),
        (New-Object System.Drawing.PointF 0, 480)
    )
    $g.FillPolygon($blue, $tri)
    Draw-Star $g $gold 100 240 50 20 5 -90
}

# -------------------------------------------------------------
# Ideology Sets Generator for tags
# -------------------------------------------------------------
function Generate-Ideology-Set([string]$tag, [string]$primaryColor, [string]$secondaryColor) {
    $c1 = [System.Drawing.ColorTranslator]::FromHtml($primaryColor)
    $c2 = [System.Drawing.ColorTranslator]::FromHtml($secondaryColor)

    # 1. Communism: Red banner + gold star / emblem
    Save-Flag "flag_$($tag)_communism.png" { param($g)
        $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(195, 25, 30))
        $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245, 205, 35))
        $g.Clear($red.Color)
        # Canton with national strip
        $b1 = New-Object System.Drawing.SolidBrush $c1
        $g.FillRectangle($b1, 0, 0, 768, 40)
        Draw-Star $g $gold 140 140 60 25 5 -90
        $pen = New-Object System.Drawing.Pen $gold, 5
        $g.DrawEllipse($pen, 80, 80, 120, 120)
    }

    # 2. Fascism: Dark authoritative field + national colors in dynamic chevron / emblem
    Save-Flag "flag_$($tag)_fascism.png" { param($g)
        $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 35, 40))
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245, 245, 245))
        $b1 = New-Object System.Drawing.SolidBrush $c1
        $b2 = New-Object System.Drawing.SolidBrush $c2
        $g.Clear($dark.Color)
        # Bold diagonal or chevron
        $pts1 = @(
            (New-Object System.Drawing.PointF 0, 0),
            (New-Object System.Drawing.PointF 240, 240),
            (New-Object System.Drawing.PointF 0, 480)
        )
        $g.FillPolygon($b1, $pts1)
        $pts2 = @(
            (New-Object System.Drawing.PointF 20, 0),
            (New-Object System.Drawing.PointF 260, 240),
            (New-Object System.Drawing.PointF 20, 480)
        )
        $pen = New-Object System.Drawing.Pen $white, 8
        $g.DrawLines($pen, $pts2)
        Draw-Star $g $white 480 240 65 26 4 0
    }

    # 3. Theocracy: Holy field with central crescent / rayed emblem
    Save-Flag "flag_$($tag)_theocracy.png" { param($g)
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 250, 250))
        $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 190, 40))
        $b1 = New-Object System.Drawing.SolidBrush $c1
        $g.Clear($white.Color)
        # Top and bottom decorative borders
        $g.FillRectangle($b1, 0, 0, 768, 60)
        $g.FillRectangle($b1, 0, 420, 768, 60)
        $g.FillRectangle($gold, 0, 60, 768, 15)
        $g.FillRectangle($gold, 0, 405, 768, 15)
        # Central sacred sun/disc
        Draw-SunRays $g $gold 384 240 45 80 16
        $pen = New-Object System.Drawing.Pen $b1, 6
        $g.DrawEllipse($pen, 384 - 95, 240 - 95, 190, 190)
    }

    # 4. Absolute Monarchy: Royal traditional field + royal crown
    Save-Flag "flag_$($tag)_absolute_monarchy.png" { param($g)
        $b1 = New-Object System.Drawing.SolidBrush $c1
        $b2 = New-Object System.Drawing.SolidBrush $c2
        $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 195, 45))
        $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(25, 30, 45))
        $g.Clear($b1.Color)
        # Center royal mantle
        $g.FillRectangle($dark, 234, 0, 300, 480)
        $penGold = New-Object System.Drawing.Pen $gold, 6
        $g.DrawRectangle($penGold, 234, 0, 300, 480)
        Draw-Crown $g $gold 384 240 160 110
    }
}

# Generate ideology sets for the 4 movements
Generate-Ideology-Set "cma" "#8a7a3b" "#2b4560"
Generate-Ideology-Set "sdf" "#f0c020" "#2e7d32"
Generate-Ideology-Set "hou" "#2c6330" "#c82525"
Generate-Ideology-Set "hzb" "#f7d417" "#007a3d"

# Generate ideology sets for the 6 newly flagged entities
Generate-Ideology-Set "krg" "#eb232d" "#148c3c"
Generate-Ideology-Set "pnt" "#418cd7" "#239b3c"
Generate-Ideology-Set "uws" "#dc1e23" "#1946a0"
Generate-Ideology-Set "mnd" "#d71e23" "#1e8c37"
Generate-Ideology-Set "tnl" "#148737" "#194196"
Generate-Ideology-Set "spn" "#141414" "#148232"

Write-Host "All missing flags and ideology sets generated successfully."
