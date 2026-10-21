Add-Type -AssemblyName System.Drawing

$targetDir = "d:/Godot Projects/deal-or-force-godot-4-8/game/gfx/flags"
$w = 768
$h = 512

function Draw-Star($gfx, $brush, [float]$cx, [float]$cy, [float]$outerR, [float]$innerR, [float]$startAngle = -0.5) {
    $points = New-Object System.Drawing.PointF[] 10
    for ($i = 0; $i -lt 10; $i++) {
        $angle = ($startAngle * [Math]::PI) + ($i * [Math]::PI / 5.0)
        $r = if ($i % 2 -eq 0) { $outerR } else { $innerR }
        $px = $cx + $r * [Math]::Cos($angle)
        $py = $cy + $r * [Math]::Sin($angle)
        $points[$i] = New-Object System.Drawing.PointF([float]$px, [float]$py)
    }
    $gfx.FillPolygon($brush, $points)
}

# 1. CMA Regional (Flag of Azawad: Green, Red, Black horizontal with Yellow hoist triangle)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sh = $h / 3.0
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 140, 50))), 0, 0, $w, [int][Math]::Ceiling($sh))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(215, 25, 32))), 0, [int][Math]::Floor($sh), $w, [int][Math]::Ceiling($sh))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(20, 20, 20))), 0, [int][Math]::Floor($sh * 2), $w, [int][Math]::Ceiling($sh))
$triPts = New-Object System.Drawing.PointF[] 3
$triPts[0] = New-Object System.Drawing.PointF(0.0, 0.0)
$triPts[1] = New-Object System.Drawing.PointF([float]($w * 0.38), [float]($h / 2.0))
$triPts[2] = New-Object System.Drawing.PointF(0.0, [float]$h)
$g.FillPolygon((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 215, 0))), $triPts)
$bmp.Save("$targetDir/flag_cma_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 2. SDF Regional (Flag of Rojava / TEV-DEM: Yellow, Red, Green horizontal tricolor)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sh = $h / 3.0
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 215, 0))), 0, 0, $w, [int][Math]::Ceiling($sh))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(215, 25, 32))), 0, [int][Math]::Floor($sh), $w, [int][Math]::Ceiling($sh))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 140, 50))), 0, [int][Math]::Floor($sh * 2), $w, [int][Math]::Ceiling($sh))
$bmp.Save("$targetDir/flag_sdf_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 3. HOU Regional (Flag of Yemen: Red, White, Black horizontal tricolor)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sh = $h / 3.0
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(206, 17, 38))), 0, 0, $w, [int][Math]::Ceiling($sh))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))), 0, [int][Math]::Floor($sh), $w, [int][Math]::Ceiling($sh))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 0, 0))), 0, [int][Math]::Floor($sh * 2), $w, [int][Math]::Ceiling($sh))
$bmp.Save("$targetDir/flag_hou_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 4. HZB Regional (Flag of Lebanon: Red, White with Cedar Tree, Red)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sh1 = $h * 0.25
$sh2 = $h * 0.50
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(237, 28, 36))), 0, 0, $w, [int][Math]::Ceiling($sh1))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))), 0, [int][Math]::Floor($sh1), $w, [int][Math]::Ceiling($sh2))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(237, 28, 36))), 0, [int][Math]::Floor($sh1 + $sh2), $w, [int][Math]::Ceiling($sh1))
$green = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 130, 60))
$brown = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(110, 60, 30))
[float]$cx = $w / 2.0; [float]$cy = $h / 2.0
$g.FillRectangle($brown, [float]($cx - 15), [float]($cy + 55), [float]30, [float]45)
$tier1 = New-Object System.Drawing.PointF[] 3
$tier1[0] = New-Object System.Drawing.PointF($cx, [float]($cy - 90))
$tier1[1] = New-Object System.Drawing.PointF([float]($cx + 80), [float]($cy - 10))
$tier1[2] = New-Object System.Drawing.PointF([float]($cx - 80), [float]($cy - 10))
$tier2 = New-Object System.Drawing.PointF[] 3
$tier2[0] = New-Object System.Drawing.PointF($cx, [float]($cy - 40))
$tier2[1] = New-Object System.Drawing.PointF([float]($cx + 115), [float]($cy + 30))
$tier2[2] = New-Object System.Drawing.PointF([float]($cx - 115), [float]($cy + 30))
$tier3 = New-Object System.Drawing.PointF[] 3
$tier3[0] = New-Object System.Drawing.PointF($cx, [float]$cy)
$tier3[1] = New-Object System.Drawing.PointF([float]($cx + 140), [float]($cy + 70))
$tier3[2] = New-Object System.Drawing.PointF([float]($cx - 140), [float]($cy + 70))
$g.FillPolygon($green, $tier3)
$g.FillPolygon($green, $tier2)
$g.FillPolygon($green, $tier1)
$bmp.Save("$targetDir/flag_hzb_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 5. AIP Regional (Flag of Alaska: Deep Blue with Big Dipper and Polaris)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(15, 32, 66))), 0, 0, $w, $h)
$gold = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 182, 18))
Draw-Star $g $gold ($w * 0.85) ($h * 0.22) 28 11
Draw-Star $g $gold ($w * 0.16) ($h * 0.76) 16 6.5
Draw-Star $g $gold ($w * 0.25) ($h * 0.69) 16 6.5
Draw-Star $g $gold ($w * 0.33) ($h * 0.65) 16 6.5
Draw-Star $g $gold ($w * 0.44) ($h * 0.66) 16 6.5
Draw-Star $g $gold ($w * 0.49) ($h * 0.80) 16 6.5
Draw-Star $g $gold ($w * 0.62) ($h * 0.78) 16 6.5
Draw-Star $g $gold ($w * 0.58) ($h * 0.62) 16 6.5
$bmp.Save("$targetDir/flag_aip_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 6. TNM Regional (Texas Lone Star Flag: Blue vertical band with star, White over Red)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$vw = $w / 3.0
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 40, 104))), 0, 0, [int][Math]::Ceiling($vw), $h)
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))), [int][Math]::Floor($vw), 0, $w - [int][Math]::Floor($vw), [int]($h / 2.0))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(191, 10, 48))), [int][Math]::Floor($vw), [int]($h / 2.0), $w - [int][Math]::Floor($vw), [int]($h / 2.0))
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))
Draw-Star $g $white ($vw / 2.0) ($h / 2.0) 65 25
$bmp.Save("$targetDir/flag_tnm_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 7. YCA Regional (California Bear Flag: Red star, Grizzly Bear, Red stripe, Republic text)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))), 0, 0, $w, $h)
$red = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(183, 18, 52))
$stripeH = $h / 6.0
$g.FillRectangle($red, 0, [int]($h - $stripeH), $w, [int][Math]::Ceiling($stripeH))
Draw-Star $g $red ($w * 0.16) ($h * 0.22) 42 16
$greenBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 120, 50))
$g.FillEllipse($greenBrush, [float]($w * 0.28), [float]($h * 0.60), [float]($w * 0.50), [float]35)
$brownBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(100, 60, 30))
$g.FillEllipse($brownBrush, [float]($w * 0.40), [float]($h * 0.40), [float]180, [float]100)
$g.FillEllipse($brownBrush, [float]($w * 0.35), [float]($h * 0.43), [float]70, [float]55)
$g.FillRectangle($brownBrush, [float]($w * 0.38), [float]($h * 0.52), [float]24, [float]55)
$g.FillRectangle($brownBrush, [float]($w * 0.46), [float]($h * 0.52), [float]24, [float]55)
$g.FillRectangle($brownBrush, [float]($w * 0.54), [float]($h * 0.52), [float]24, [float]55)
$g.FillRectangle($brownBrush, [float]($w * 0.60), [float]($h * 0.52), [float]24, [float]55)
$font = New-Object System.Drawing.Font("Arial", 22, [System.Drawing.FontStyle]::Bold)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$blackBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(20, 20, 20))
$g.DrawString("CALIFORNIA REPUBLIC", $font, $blackBrush, [float]($w / 2.0), [float]($h - $stripeH - 38), $sf)
$bmp.Save("$targetDir/flag_yca_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 8. KAU Regional (Flag of Hawaii: Union Jack in canton, 8 stripes of white, red, blue)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sh = $h / 8.0
$colors = @(
    [System.Drawing.Color]::FromArgb(255, 255, 255),
    [System.Drawing.Color]::FromArgb(206, 17, 38),
    [System.Drawing.Color]::FromArgb(0, 38, 100),
    [System.Drawing.Color]::FromArgb(255, 255, 255),
    [System.Drawing.Color]::FromArgb(206, 17, 38),
    [System.Drawing.Color]::FromArgb(0, 38, 100),
    [System.Drawing.Color]::FromArgb(255, 255, 255),
    [System.Drawing.Color]::FromArgb(206, 17, 38)
)
for ($i = 0; $i -lt 8; $i++) {
    $g.FillRectangle((New-Object System.Drawing.SolidBrush($colors[$i])), 0, [int]($i * $sh), $w, [int][Math]::Ceiling($sh))
}
[float]$cw = $w / 2.0; [float]$ch = $h / 2.0
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 38, 100))), 0, 0, [int]$cw, [int]$ch)
$wPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 255), 36)
$rPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(206, 17, 38), 20)
$g.DrawLine($wPen, 0, 0, [int]$cw, [int]$ch)
$g.DrawLine($wPen, 0, [int]$ch, [int]$cw, 0)
$g.DrawLine($rPen, 0, 0, [int]$cw, [int]$ch)
$g.DrawLine($rPen, 0, [int]$ch, [int]$cw, 0)
$wCross = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 255), 52)
$rCross = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(206, 17, 38), 32)
$g.DrawLine($wCross, [float]($cw / 2.0), 0, [float]($cw / 2.0), [float]$ch)
$g.DrawLine($wCross, 0, [float]($ch / 2.0), [float]$cw, [float]($ch / 2.0))
$g.DrawLine($rCross, [float]($cw / 2.0), 0, [float]($cw / 2.0), [float]$ch)
$g.DrawLine($rCross, 0, [float]($ch / 2.0), [float]$cw, [float]($ch / 2.0))
$bmp.Save("$targetDir/flag_kau_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 9. RAS Regional (Flag of Upper Silesia: Gold over Blue with Golden Eagle)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sh = $h / 2.0
$goldBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 215, 0))
$blueBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 80, 165))
$g.FillRectangle($goldBrush, 0, 0, $w, [int][Math]::Ceiling($sh))
$g.FillRectangle($blueBrush, 0, [int][Math]::Floor($sh), $w, [int][Math]::Ceiling($sh))
[float]$cx = $w / 2.0; [float]$cy = $h / 2.0
$g.FillEllipse($goldBrush, [float]($cx - 60), [float]($cy - 70), [float]120, [float]140)
$g.FillEllipse($goldBrush, [float]($cx - 130), [float]($cy - 50), [float]90, [float]110)
$g.FillEllipse($goldBrush, [float]($cx + 40), [float]($cy - 50), [float]90, [float]110)
$penWhite = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 255), 10)
$g.DrawArc($penWhite, [float]($cx - 45), [float]($cy - 25), [float]90, [float]50, 10, 160)
$bmp.Save("$targetDir/flag_ras_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 10. TPN Regional (Morning Star / West Papua Flag: Red hoist band with Star, 13 blue/white stripes)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sh = $h / 13.0
$blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 50, 140))
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))
for ($i = 0; $i -lt 13; $i++) {
    $br = if ($i % 2 -eq 0) { $blue } else { $white }
    $g.FillRectangle($br, 0, [int]($i * $sh), $w, [int][Math]::Ceiling($sh))
}
$rw = $w * 0.32
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(206, 17, 38))), 0, 0, [int][Math]::Ceiling($rw), $h)
Draw-Star $g $white ($rw / 2.0) ($h / 2.0) 65 25
$bmp.Save("$targetDir/flag_tpn_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 11. ELN Regional (Flag of Colombia: Yellow 50%, Blue 25%, Red 25%)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 205, 0))), 0, 0, $w, [int]($h / 2.0))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 48, 130))), 0, [int]($h / 2.0), $w, [int]($h / 4.0))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(206, 17, 38))), 0, [int]($h * 0.75), $w, [int]($h / 4.0))
$bmp.Save("$targetDir/flag_eln_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 12. ARS Regional (Flag of Rohingya: Dark Green with White Crescent and 5 Stars)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 102, 34))), 0, 0, $w, $h)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))
$green = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 102, 34))
[float]$cx = $w / 2.0; [float]$cy = $h / 2.0
$g.FillEllipse($white, [float]($cx - 80), [float]($cy - 80), [float]160, [float]160)
$g.FillEllipse($green, [float]($cx - 45), [float]($cy - 70), [float]140, [float]140)
Draw-Star $g $white ($cx + 40) ($cy - 10) 30 12
$bmp.Save("$targetDir/flag_ars_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 13. ARA Regional (Flag of Rakhine State: White over Red with Blue Srivatsa Disc)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sh = $h / 2.0
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))), 0, 0, $w, [int][Math]::Ceiling($sh))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(206, 17, 38))), 0, [int][Math]::Floor($sh), $w, [int][Math]::Ceiling($sh))
[float]$cx = $w / 2.0; [float]$cy = $h / 2.0
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 50, 140))), [float]($cx - 90), [float]($cy - 90), [float]180, [float]180)
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 255), 10)
$g.DrawEllipse($pen, [float]($cx - 50), [float]($cy - 50), [float]100, [float]100)
$g.DrawLine($pen, [float]($cx - 30), [float]$cy, [float]($cx + 30), [float]$cy)
$bmp.Save("$targetDir/flag_ara_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 14. VAN Regional (Flag of Haiti: Blue over Red with White Center Panel)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sh = $h / 2.0
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 32, 128))), 0, 0, $w, [int][Math]::Ceiling($sh))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 16, 52))), 0, [int][Math]::Floor($sh), $w, [int][Math]::Ceiling($sh))
$pw = 140; $ph = 110
[float]$cx = $w / 2.0; [float]$cy = $h / 2.0
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))), [float]($cx - $pw / 2.0), [float]($cy - $ph / 2.0), [float]$pw, [float]$ph)
$green = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 130, 40))
$palm = New-Object System.Drawing.PointF[] 3
$palm[0] = New-Object System.Drawing.PointF($cx, [float]($cy - 40))
$palm[1] = New-Object System.Drawing.PointF([float]($cx + 35), [float]($cy + 25))
$palm[2] = New-Object System.Drawing.PointF([float]($cx - 35), [float]($cy + 25))
$g.FillPolygon($green, $palm)
$bmp.Save("$targetDir/flag_van_regional.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Host "Successfully generated all 14 authentic regional flags for movements!"
