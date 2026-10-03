param(
    [string]$Recipe = 'Assets/ArtSource/Icons/minimal-icons.json',
    [string]$OutputDirectory = 'game/gfx/interface/icons'
)
# Author flat silhouettes from editable geometry; PNGs remain the runtime/mod format.
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
        g.CompositingMode = CompositingMode.SourceCopy;
        g.ScaleTransform(16, 16);
        return g;
    }
    public static void Shape(Graphics g, string kind, float[] values, bool cut, float width) {
        var colour = cut ? Color.Transparent : Color.White;
        using (var brush = new SolidBrush(colour))
        using (var pen = new Pen(colour, width)) {
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            if (kind == "rect") g.FillRectangle(brush, values[0], values[1], values[2], values[3]);
            else if (kind == "ellipse") g.FillEllipse(brush, values[0], values[1], values[2], values[3]);
            else {
                if (values.Length < 4 || values.Length % 2 != 0) throw new ArgumentException("Invalid points");
                var points = new PointF[values.Length / 2];
                for (int i = 0; i < points.Length; i++) points[i] = new PointF(values[i * 2], values[i * 2 + 1]);
                if (kind == "polygon") g.FillPolygon(brush, points);
                else if (kind == "line") g.DrawLines(pen, points);
                else throw new ArgumentException("Unknown shape " + kind);
            }
        }
    }
    public static void Save(Bitmap canvas, string path) {
        using (var result = new Bitmap(96, 96, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(result)) {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, 96, 96));
            result.Save(path, ImageFormat.Png);
        }
    }
}
'@
$recipes = Get-Content -LiteralPath $Recipe -Raw | ConvertFrom-Json
$seen = @{}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
foreach ($item in $recipes) {
    if ($item.name -notmatch '^[a-z][a-z0-9_]+$' -or $seen.ContainsKey($item.name)) {
        throw "Invalid or duplicate icon name: $($item.name)"
    }
    $seen[$item.name] = $true
    if ($item.shapes.Count -eq 0) { throw "Empty icon: $($item.name)" }
    $canvas = [MinimalIconRenderer]::Canvas()
    $graphics = [MinimalIconRenderer]::Context($canvas)
    try {
        foreach ($shape in $item.shapes) {
            $width = if ($null -eq $shape.width) { 2.4 } else { $shape.width }
            [MinimalIconRenderer]::Shape($graphics, $shape.kind, [single[]]$shape.values, [bool]$shape.cut, $width)
        }
        [MinimalIconRenderer]::Save($canvas, (Join-Path $outputRoot "icon_$($item.name).png"))
    } finally { $graphics.Dispose(); $canvas.Dispose() }
}
Write-Output "Rendered $($seen.Count) flat monochrome 96x96 PNGs from editable geometry."
