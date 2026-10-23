# Syria's flags at the 2024 bookmark and beyond. The game opens on 1 January 2024, when
# the Baath republic still stood: red-white-black with two green stars. The post-2024
# independence flag (green-white-black with three red stars) is drawn beside it, so a
# change of government in play can fly the cloth its politics call for.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-SyriaFlag([string]$path, [string]$top, [string]$middle, [string]$bottom,
    [string]$star, [int]$stars, [int]$width = 768, [int]$height = 480)
{
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $band = [int]($height / 3)
    $graphics.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($top))), 0, 0, $width, $band)
    $graphics.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($middle))), 0, $band, $width, $band)
    $graphics.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($bottom))), 0, $band * 2, $width, $height - $band * 2)
    $starBrush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($star))
    for ($i = 0; $i -lt $stars; $i++)
    {
        $centreX = if ($stars -eq 2) { [int]($width * (0.33 + 0.34 * $i)) } else { [int]($width * 0.5) }
        $centreY = [int]($band + $band / 2)
        $radius = [int]($band * 0.30)
        $points = New-Object 'System.Drawing.PointF[]' 10
        for ($p = 0; $p -lt 10; $p++)
        {
            $angle = -90 + $p * 36
            $rad = $angle * [Math]::PI / 180
            $r = if ($p % 2 -eq 0) { $radius } else { $radius * 0.42 }
            $points[$p] = New-Object System.Drawing.PointF(($centreX + $r * [Math]::Cos($rad)), ($centreY + $r * [Math]::Sin($rad)))
        }
        $graphics.FillPolygon($starBrush, $points)
    }
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output "wrote $path"
}

New-SyriaFlag 'game/gfx/flags/flag_syr.png' '#CE1126' '#FFFFFF' '#000000' '#007A3D' 2
New-SyriaFlag 'game/gfx/flags/flag_syr_one_party_state.png' '#CE1126' '#FFFFFF' '#000000' '#007A3D' 2
New-SyriaFlag 'game/gfx/flags/flag_syr_socialism.png' '#CE1126' '#FFFFFF' '#000000' '#007A3D' 2
New-SyriaFlag 'game/gfx/flags/flag_syr_independence.png' '#007A3D' '#FFFFFF' '#000000' '#CE1126' 3
