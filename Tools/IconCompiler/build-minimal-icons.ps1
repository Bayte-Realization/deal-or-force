param(
    [string]$Recipe = 'Tools/IconCompiler/Source/Icons/minimal-icons.json',
    [string]$OutputDirectory = 'game/gfx/interface/icons',
    [string]$Colour = '#e7bd69',
    [string]$GoodsColour = '#b48732'
)
# Editable geometry and restrained facets; PNGs are authored in the game's gold palette.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class MinimalIconRenderer {
    public static Bitmap Canvas() { return new Bitmap(384, 384, PixelFormat.Format32bppArgb); }
    public static Graphics Context(Bitmap canvas) {
        var g = Graphics.FromImage(canvas);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.ScaleTransform(16, 16);
        return g;
    }
    public static void Shape(Graphics g, string kind, float[] values, bool cut, float width, float shade) {
        if (shade < 0.5f || shade > 1f) throw new ArgumentException("Shade must be from 0.5 to 1");
        g.CompositingMode = cut ? CompositingMode.SourceCopy : CompositingMode.SourceOver;
        int level = (int)Math.Round(255 * shade);
        var colour = cut ? Color.Transparent : Color.FromArgb(level, level, level);
        using (var brush = new SolidBrush(colour))
        using (var pen = new Pen(colour, width)) {
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            if (kind == "rect") g.FillRectangle(brush, values[0], values[1], values[2], values[3]);
            else if (kind == "ellipse") g.FillEllipse(brush, values[0], values[1], values[2], values[3]);
            else if (kind == "ring") g.DrawEllipse(pen, values[0], values[1], values[2], values[3]);
            else if (kind == "curve") {
                if (values.Length < 8 || (values.Length - 2) % 6 != 0) throw new ArgumentException("Invalid cubic curve");
                var points = new PointF[values.Length / 2];
                for (int i = 0; i < points.Length; i++) points[i] = new PointF(values[i * 2], values[i * 2 + 1]);
                g.DrawBeziers(pen, points);
            }
            else {
                if (values.Length < 4 || values.Length % 2 != 0) throw new ArgumentException("Invalid points");
                var points = new PointF[values.Length / 2];
                for (int i = 0; i < points.Length; i++) points[i] = new PointF(values[i * 2], values[i * 2 + 1]);
                if (kind == "polygon") g.FillPolygon(brush, points);
                else if (kind == "outline") g.DrawPolygon(pen, points);
                else if (kind == "line") g.DrawLines(pen, points);
                else throw new ArgumentException("Unknown shape " + kind);
            }
        }
    }
    public static void Save(Bitmap canvas, string path, Color colour) {
        using (var result = new Bitmap(96, 96, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(result)) {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, 96, 96));
            for (int y = 0; y < 96; y++) for (int x = 0; x < 96; x++) {
                var pixel = result.GetPixel(x, y);
                result.SetPixel(x, y, Color.FromArgb(pixel.A,
                    (int)Math.Round(pixel.R * colour.R / 255.0),
                    (int)Math.Round(pixel.G * colour.G / 255.0),
                    (int)Math.Round(pixel.B * colour.B / 255.0)));
            }
            result.Save(path, ImageFormat.Png);
        }
    }
}
'@
$recipes = Get-Content -LiteralPath $Recipe -Raw | ConvertFrom-Json
$seen = @{}
foreach ($value in @($Colour, $GoodsColour)) {
    if ($value -notmatch '^#[0-9a-fA-F]{6}$') { throw 'Colour must be #RRGGBB.' }
    $reference = [Drawing.ColorTranslator]::FromHtml($value)
    if ($reference.R -eq 0 -or $reference.G -eq 0 -or $reference.B -eq 0) {
        throw 'Colour must have nonzero channels for runtime theme compensation.'
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$sprites = [ordered]@{}
foreach ($item in $recipes) {
    if ($item.name -notmatch '^[a-z][a-z0-9_]+$' -or $seen.ContainsKey($item.name)) {
        throw "Invalid or duplicate icon name: $($item.name)"
    }
    $seen[$item.name] = $true
    $itemColour = if ($item.name.StartsWith('good_')) { $GoodsColour } else { $Colour }
    $palette = [Drawing.ColorTranslator]::FromHtml($itemColour)
    $sprites["icon_$($item.name)"] = @{ source_colour = $itemColour }
    if ($item.shapes.Count -eq 0) { throw "Empty icon: $($item.name)" }
    $canvas = [MinimalIconRenderer]::Canvas()
    $graphics = [MinimalIconRenderer]::Context($canvas)
    try {
        foreach ($shape in $item.shapes) {
            $width = if ($null -ne $shape.width) { $shape.width } elseif ($item.name.StartsWith('good_')) { 1.25 } else { 2.4 }
            $shade = if ($null -eq $shape.shade) { 1.0 } else { $shape.shade }
            [MinimalIconRenderer]::Shape($graphics, $shape.kind, [single[]]$shape.values, [bool]$shape.cut, $width, $shade)
        }
        [MinimalIconRenderer]::Save($canvas, (Join-Path $outputRoot "icon_$($item.name).png"), $palette)
    } finally { $graphics.Dispose(); $canvas.Dispose() }
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'minimal_palette.json'),
    (@{ sprites = $sprites } | ConvertTo-Json -Depth 4) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
Write-Output "Rendered $($seen.Count) gold 96x96 PNGs from editable geometry."
