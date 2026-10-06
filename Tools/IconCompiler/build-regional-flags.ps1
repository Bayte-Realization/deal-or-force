param([string]$OutputDirectory = 'game/gfx/flags')
# New fictional sandbox families only. Never edits an existing historical flag PNG.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class RegionalFlagWorkshop {
    static void Poly(Graphics g, Brush b, params float[] xy) {
        var p = new PointF[xy.Length/2];
        for(int i=0;i<p.Length;i++) p[i] = new PointF(xy[i*2],xy[i*2+1]);
        g.FillPolygon(b,p);
    }
    static void Motif(Graphics g, Brush b, int kind) {
        using(var pen=new Pen(b,2.5f)) {
            pen.StartCap=pen.EndCap=LineCap.Round;
            switch(kind%6) {
                case 0:
                    for(int i=0;i<12;i++) { double a=i*Math.PI/6; g.DrawLine(pen,(float)(96+17*Math.Cos(a)),(float)(60+17*Math.Sin(a)),(float)(96+23*Math.Cos(a)),(float)(60+23*Math.Sin(a))); }
                    g.FillEllipse(b,84,48,24,24); break;
                case 1: Poly(g,b,66,78,89,43,102,64,113,48,128,78); break;
                case 2:
                    g.DrawEllipse(pen,74,38,44,44); Poly(g,b,96,34,103,55,122,60,103,65,96,86,89,65,70,60,89,55); break;
                case 3:
                    for(int i=0;i<3;i++) g.DrawBezier(pen,69,49+i*11,82,37+i*11,105,66+i*11,123,49+i*11); break;
                case 4:
                    g.DrawLine(pen,96,39,96,84);
                    for(int i=0;i<4;i++) { g.FillEllipse(b,82,43+i*8,14,7); g.FillEllipse(b,96,47+i*8,14,7); } break;
                default:
                    g.DrawArc(pen,73,34,46,51,25,130); g.DrawArc(pen,73,34,46,51,205,130);
                    for(int i=0;i<5;i++) { g.FillEllipse(b,70+i*3,42+i*7,8,5); g.FillEllipse(b,114-i*3,42+i*7,8,5); }
                    Poly(g,b,96,44,100,55,111,55,102,62,106,73,96,66,86,73,90,62,81,55,92,55); break;
            }
        }
    }
    public static void Save(string path, string primary, int layout, int motif, int variant) {
        using(var canvas=new Bitmap(768,480,PixelFormat.Format32bppArgb))
        using(var g=Graphics.FromImage(canvas))
        using(var main=new SolidBrush(ColorTranslator.FromHtml(primary)))
        using(var cream=new SolidBrush(Color.FromArgb(232,222,184)))
        using(var ink=new SolidBrush(Color.FromArgb(28,46,58)))
        using(var gold=new SolidBrush(Color.FromArgb(199,159,73))) {
            g.ScaleTransform(4,4); g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(main.Color);
            switch(layout%5) {
                case 0: g.FillRectangle(cream,0,0,192,25); g.FillRectangle(ink,0,95,192,25); break;
                case 1: g.FillRectangle(ink,0,0,48,120); g.FillRectangle(cream,48,0,8,120); break;
                case 2: Poly(g,ink,0,0,192,120,0,120); Poly(g,cream,0,0,12,0,192,108,192,120); break;
                case 3: g.FillRectangle(ink,0,0,192,17); g.FillRectangle(cream,0,21,192,5); g.FillRectangle(ink,0,103,192,17); break;
                default: Poly(g,ink,0,0,61,60,0,120); Poly(g,cream,0,0,10,0,71,60,10,120,0,120,61,60); break;
            }
            g.FillEllipse(ink,64,28,64,64); Motif(g,gold,motif);
            if(variant==1) {
                using(var red=new SolidBrush(Color.FromArgb(149,42,44))) g.FillRectangle(red,0,0,192,16);
                Poly(g,gold,16,2,18,6,23,7,19,10,20,14,16,11,12,14,13,10,9,7,14,6);
            }
            if(variant==2) {
                g.FillRectangle(gold,0,0,192,4); g.FillRectangle(gold,0,116,192,4);
                Poly(g,gold,79,20,81,10,88,17,96,8,104,17,111,10,113,20);
            }
            using(var result=new Bitmap(192,120)) using(var scaled=Graphics.FromImage(result)) {
                scaled.InterpolationMode=InterpolationMode.HighQualityBicubic;
                scaled.DrawImage(canvas,0,0,192,120); result.Save(path,ImageFormat.Png);
            }
        }
    }
}
'@
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$families = @()
function Read-WorkshopJson([string]$Path) {
    # These three authored sources use only whole-line // comments, not a general JSON5 parser.
    (Get-Content -LiteralPath $Path | Where-Object { $_ -notmatch '^\s*//' }) -join "`n" | ConvertFrom-Json
}
$movementData = Read-WorkshopJson 'game/common/movements/30_regional_voices.json'
$cultureData = Read-WorkshopJson 'game/common/cultures/96_regional_identities.json'
$formableData = Read-WorkshopJson 'game/common/formable_nations/30_regional_projects.json'
foreach ($entry in $movementData.PSObject.Properties) {
    if ($entry.Name.StartsWith('//')) { continue }
    $families += @{ name = 'flag_' + $entry.Value.tag.ToLowerInvariant(); colour = $entry.Value.colour }
}
foreach ($entry in $cultureData.PSObject.Properties) {
    if ($entry.Name.StartsWith('//')) { continue }
    $families += @{ name = 'flag_culture_' + $entry.Name; colour = $entry.Value.colour }
}
foreach ($entry in $formableData.PSObject.Properties) {
    if ($entry.Name.StartsWith('//')) { continue }
    $families += @{ name = 'flag_formable_' + $entry.Name; colour = $entry.Value.colour }
}
$suffixes = @('', '_communism', '_absolute_monarchy')
$count = 0
for ($index = 0; $index -lt $families.Count; $index++) {
    for ($variant = 0; $variant -lt 3; $variant++) {
        $flagPath = Join-Path $OutputDirectory ($families[$index].name + $suffixes[$variant] + '.png')
        if (Test-Path -LiteralPath $flagPath) { throw "Refusing to overwrite existing flag $flagPath" }
        [RegionalFlagWorkshop]::Save($flagPath, $families[$index].colour, $index % 5, [int]($index / 5) % 6, $variant)
        $count++
    }
}
Write-Output "Created $count new PNGs in $($families.Count) fictional regional/formable flag families."
