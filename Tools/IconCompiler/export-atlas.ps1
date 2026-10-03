param(
    [Parameter(Mandatory = $true)][string]$Atlas,
    [Parameter(Mandatory = $true)][string]$Mapping,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
# Mechanical export of an authored/generated 4x4 sheet. No artwork is invented here.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class IconAtlasExport {
    public static void Export(string path, string[] names, string directory) {
        if (names.Length != 16) throw new ArgumentException("Expected sixteen row-major names");
        using (var atlas = new Bitmap(path)) {
            for (int index = 0; index < names.Length; index++) {
                int column = index % 4, row = index / 4;
                int ox = column * atlas.Width / 4, oy = row * atlas.Height / 4;
                int width = (column + 1) * atlas.Width / 4 - ox;
                int height = (row + 1) * atlas.Height / 4 - oy;
                int left = width, top = height, right = -1, bottom = -1;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
                    if (atlas.GetPixel(ox + x, oy + y).A < 16) continue;
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
                if (right < left || bottom < top) throw new ArgumentException("Empty cell: " + names[index]);
                var source = new Rectangle(ox + left, oy + top, right - left + 1, bottom - top + 1);
                using (var image = new Bitmap(96, 96, PixelFormat.Format32bppArgb))
                using (var graphics = Graphics.FromImage(image)) {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float scale = Math.Min(84f / source.Width, 84f / source.Height);
                    var target = new RectangleF((96 - source.Width * scale) / 2,
                        (96 - source.Height * scale) / 2, source.Width * scale, source.Height * scale);
                    graphics.DrawImage(atlas, target, source, GraphicsUnit.Pixel);
                    image.Save(System.IO.Path.Combine(directory, "icon_" + names[index] + ".png"), ImageFormat.Png);
                }
            }
        }
    }
}
'@
$names = Get-Content -LiteralPath $Mapping -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
[IconAtlasExport]::Export((Resolve-Path -LiteralPath $Atlas).Path, [string[]]$names, (Resolve-Path -LiteralPath $OutputDirectory).Path)
Write-Output "Exported $($names.Count) transparent 96x96 icons."
