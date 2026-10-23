param([string]$OutputDirectory = 'game/gfx/flags')
# Flags for formables that have no artwork yet, drawn from the definition id itself.
#
# The rule this file obeys, the same one build-regional-flags.ps1 states: never touch an existing
# historical flag PNG. These are new formable states of the catalogue - a united Congo, a restored
# Mali Federation, the Polish-Lithuanian Commonwealth, a reunited Panama, a united Samoa - whose
# flag does not exist to copy, so it is drawn: a banded field coloured from the id, a pale band
# through the middle, and three marks whose arrangement is also the id's. Same id in, same flag
# out, and every one of them is reproducible by running this script.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class ClaimFlagWorkshop {
    static int Hash(string id) {
        uint hash = 2166136261u;
        foreach (char c in id) hash = (hash ^ c) * 16777619u;
        return (int)(hash % 2147483647u);
    }
    public static void Save(string path, string id) {
        int seed = Hash(id);
        var palette = new[] {
            Color.FromArgb(178, 58, 62), Color.FromArgb(38, 84, 124), Color.FromArgb(42, 108, 74),
            Color.FromArgb(206, 158, 70), Color.FromArgb(96, 62, 122), Color.FromArgb(184, 96, 44),
            Color.FromArgb(58, 122, 130)
        };
        var main = palette[seed % palette.Length];
        var second = palette[(seed / 7 + 3) % palette.Length];
        if (second == main) second = palette[(seed / 11 + 1) % palette.Length];
        using (var canvas = new Bitmap(768, 480, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(canvas))
        using (var a = new SolidBrush(main))
        using (var b = new SolidBrush(second))
        using (var pale = new SolidBrush(Color.FromArgb(238, 232, 214)))
        {
            g.Clear(a.Color);
            int layout = seed % 4;
            switch (layout) {
                case 0: g.FillRectangle(b, 0, 160, 768, 160); break;
                case 1: g.FillRectangle(b, 0, 0, 256, 480); break;
                case 2: g.FillRectangle(b, 0, 0, 768, 120); g.FillRectangle(b, 0, 360, 768, 120); break;
                default: g.FillRectangle(b, 0, 200, 768, 80); break;
            }
            g.FillRectangle(pale, 0, 226, 768, 28);
            // Three marks, arranged by the same seed: a canton, a fess or a diagonal.
            using (var mark = new SolidBrush(pale.Color))
            {
                int marks = seed % 3;
                if (marks == 0) { g.FillEllipse(mark, 300, 156, 168, 168); }
                else if (marks == 1) {
                    for (int i = 0; i < 3; i++) g.FillRectangle(mark, 264 + i * 84, 156, 56, 168);
                } else {
                    var points = new[] { new PointF(300, 324), new PointF(384, 150), new PointF(468, 324) };
                    g.FillPolygon(mark, points);
                }
            }
            using (var result = new Bitmap(192, 120))
            using (var scaled = Graphics.FromImage(result))
            {
                scaled.InterpolationMode = InterpolationMode.HighQualityBicubic;
                scaled.DrawImage(canvas, 0, 0, 192, 120);
                result.Save(path, ImageFormat.Png);
            }
        }
    }
}
'@
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($id in @('congo_reunited', 'mali_federation', 'panama_reunited', 'polish_lithuanian', 'samoa_reunited')) {
    $path = Join-Path $OutputDirectory ("flag_formable_{0}.png" -f $id)
    if (Test-Path $path) { Write-Host "kept (already drawn): $path"; continue }
    [ClaimFlagWorkshop]::Save($path, $id)
    Write-Host "drew: $path"
}
