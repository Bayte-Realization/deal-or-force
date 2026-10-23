# Viv Ansanm's banners. The coalition has no official cloth, so the game's banner is the
# one its streets would make: the Haitian revolutionary palette turned over - black above
# the coalition's dark red - with a white star for the neighbourhoods that answer to it.
# The political variants recolour the same cloth for the day the coalition holds the state.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-VanFlag([string]$path, [string]$top, [string]$bottom, [string]$star,
    [int]$band = 0, [string]$bandColour = "#000000")
{
    $width = 768; $height = 512
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $half = [int]($height / 2)
    $graphics.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($top))), 0, 0, $width, $half)
    $graphics.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($bottom))), 0, $half, $width, $height - $half)
    if ($band -gt 0)
    {
        $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($bandColour))
        $graphics.FillRectangle($brush, 0, 0, $width, $band)
        $graphics.FillRectangle($brush, 0, $height - $band, $width, $band)
        $brush.Dispose()
    }
    $starBrush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($star))
    $centreX = [int]($width / 2); $centreY = [int]($height / 2); $radius = [int]($height * 0.21)
    $points = New-Object 'System.Drawing.PointF[]' 10
    for ($p = 0; $p -lt 10; $p++)
    {
        $rad = (-90 + $p * 36) * [Math]::PI / 180
        $r = if ($p % 2 -eq 0) { $radius } else { $radius * 0.42 }
        $points[$p] = New-Object System.Drawing.PointF(($centreX + $r * [Math]::Cos($rad)), ($centreY + $r * [Math]::Sin($rad)))
    }
    $graphics.FillPolygon($starBrush, $points)
    $starBrush.Dispose()
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output "wrote $path"
}

$dark = '#7B1F1F'
New-VanFlag 'game/gfx/flags/flag_van.png' '#141414' $dark '#F5F1E6'
New-VanFlag 'game/gfx/flags/flag_van_communism.png' '#141414' $dark '#F2C14E' 26 '#C99A2E'
New-VanFlag 'game/gfx/flags/flag_van_fascism.png' '#0B0B0B' '#5E1414' '#F5F1E6' 18 '#F5F1E6'
New-VanFlag 'game/gfx/flags/flag_van_theocracy.png' '#141414' $dark '#FFFFFF' 14 '#F5F1E6'
New-VanFlag 'game/gfx/flags/flag_van_absolute_monarchy.png' $dark '#141414' '#E3B23C' 22 '#0B0B0B'
