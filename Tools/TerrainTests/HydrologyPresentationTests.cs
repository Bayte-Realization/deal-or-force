using System;
using System.Linq;
using DealOrForce;
using DealOrForce.Simulation;

static class HydrologyPresentationTests
{
    public static void Run()
    {
        void Check(bool pass, string label)
        { if (!pass) throw new Exception(label); Console.WriteLine("PASS hydrology: " + label); }
        HydroPoint At(float x, float y) => new(-180 + (x + .5f) * 360 / 32, 90 - (y + .5f) * 180 / 16);
        var pixels = new byte[32 * 16 * 2];
        pixels[(5 * 32 + 5) * 2 + 1] = 160;
        pixels[(10 * 32 + 25) * 2 + 1] = 96;
        pixels[(9 * 32 + 12) * 2] = 255;
        var source = new HydrologyRaster(32, 16, pixels, 1, 1);
        var original = (byte[])pixels.Clone();
        var river = new RiverFeature("Example", 4, new[] { new[] { At(5, 5), At(10, 10), At(16, 10) } });
        var profile = new RiverVisualProfile(new[] { "example" }, 2.8f, 2.8f, 90, -90);
        var normal = HydrologySurfaceData.Build(source);
        var drawn = HydrologySurfaceData.Build(source, new[] { profile }, new[] { river });
        Check(Enumerable.Range(5, 6).All(x => drawn[(x * 32 + x) * 3 + 1] < 128), "diagonal channel and joins remain continuous");
        Check(drawn[(10 * 32 + 13) * 3 + 2] == 255, "named feature gets visual prominence independent of rank");
        Check(Enumerable.Range(0, 32 * 16).All(c => normal[c * 3] == drawn[c * 3]), "lake/reservoir shoreline is unchanged");
        Check(Enumerable.Range(0, 3).All(c => normal[(10 * 32 + 25) * 3 + c] == drawn[(10 * 32 + 25) * 3 + c]), "unrelated river stays byte-identical");
        Check(pixels.SequenceEqual(original), "river display cannot mutate crossing strength or geography");
        Check(HydrologySurfaceData.Build(source, new[] { profile }, Array.Empty<RiverFeature>()).SequenceEqual(normal), "missing optional named feature leaves raster unchanged");
        var seam = new RiverFeature("Example", 4, new[] { new[] { At(31, 6), At(0, 6) } });
        var wrapped = HydrologySurfaceData.Build(source, new[] { profile }, new[] { seam });
        Check(wrapped[(6 * 32 + 31) * 3 + 1] < 128 && wrapped[(6 * 32) * 3 + 1] < 128 &&
            wrapped[(6 * 32 + 16) * 3 + 2] == 0, "dateline joins take the short path, not a stripe around Earth");
        TerrainCatalog.ResetCatalog();
        // Match the shader's linear SDF sampling: adjacent diagonal wet texels share
        // a saddle at zero. A positive inset turns that into a dry gap at every step.
        var diagonalPixels = new byte[32 * 16 * 2];
        for (var i = 3; i <= 12; i++) diagonalPixels[(i * 32 + i) * 2 + 1] = 160;
        var diagonalSource = new HydrologyRaster(32,16,diagonalPixels,1,0);
        var originalDiagonal = (byte[])diagonalPixels.Clone();
        var diagonalSurface = HydrologySurfaceData.Build(diagonalSource, connectRiverDiagonals: false);
        var roundedSurface = HydrologySurfaceData.Build(diagonalSource);
        float BilinearRiver(float x,float y,byte[] field = null)
        {
            field ??= diagonalSurface;
            var ix=(int)MathF.Floor(x);var iy=(int)MathF.Floor(y);var fx=x-ix;var fy=y-iy;
            float D(int px,int py)=>(field[(py*32+px)*3+1]-128f)/16f;
            return (D(ix,iy)*(1-fx)+D(ix+1,iy)*fx)*(1-fy)+
                   (D(ix,iy+1)*(1-fx)+D(ix+1,iy+1)*fx)*fy;
        }
        Check(BilinearRiver(5.5f,5.5f)+.18f>0,"old positive inset reproduces diagonal river gaps");
        Check(Enumerable.Range(0,901).All(i=>BilinearRiver(3+i*.01f,3+i*.01f)+TerrainCatalog.Water.RiverInset<0),
            "shipped river inset preserves a continuous bilinear diagonal centreline");
        Check(BilinearRiver(6.5f,4.5f)+TerrainCatalog.Water.RiverInset>0,
            "continuity fix does not flood unrelated dry land");
        Check(diagonalSource.Pixels.SequenceEqual(originalDiagonal),"river continuity remains a presentation-only change");
        Check(BilinearRiver(5.5f,5.5f,roundedSurface) < -.25f,
            "rounded diagonal contacts retain water depth instead of a zero-width saddle");
        Check(Enumerable.Range(0,901).All(i => BilinearRiver(3+i*.01f,3+i*.01f,roundedSurface) < -.25f),
            "entire thin diagonal centreline retains a bounded visible neck");
        float CubicRiver(float x,float y,byte[] field)
        {
            // Reference 16-tap positive B-spline, equal to the shader's four bilinear taps.
            float[] Weights(float f) => new[]{MathF.Pow(1-f,3)/6,
                (3*f*f*f-6*f*f+4)/6,(-3*f*f*f+3*f*f+3*f+1)/6,f*f*f/6};
            var ix=(int)MathF.Floor(x);var iy=(int)MathF.Floor(y);
            var wx=Weights(x-ix);var wy=Weights(y-iy);var result=0f;
            for(var row=0;row<4;row++)for(var column=0;column<4;column++)
            {
                var px=((ix+column-1)%32+32)%32;var py=Math.Clamp(iy+row-1,0,15);
                result+=(field[(py*32+px)*3+1]-128f)/16f*wx[column]*wy[row];
            }
            return MathF.Min(result,BilinearRiver(x,y,field)+.25f);
        }
        Check(Enumerable.Range(0,701).All(i => CubicRiver(4+i*.01f,4+i*.01f,roundedSurface)+TerrainCatalog.Water.RiverInset<-.2f),
            "positive cubic filtering leaves the complete diagonal visibly continuous");
        Check(CubicRiver(10.5f,4.5f,roundedSurface)+TerrainCatalog.Water.RiverInset>0,
            "cubic river contour does not introduce a nearby phantom channel");
        Check(Enumerable.Range(0,32*16).All(c => roundedSurface[c*3] == diagonalSurface[c*3]),
            "rounding a river cannot change a lake shore");
        Check(Enumerable.Range(0,32*16).All(c => roundedSurface[c*3+2] <= 160),
            "minor diagonals never acquire a named-major-river prominence");
        Check(roundedSurface[(4*32+10)*3+1] == diagonalSurface[(4*32+10)*3+1],
            "unrelated dry land stays byte-identical, not globally dilated");
        var straightPixels = new byte[32*16*2];
        for(var x=4;x<=12;x++) straightPixels[(5*32+x)*2+1]=96;
        var straight = new HydrologyRaster(32,16,straightPixels,1,0);
        Check(HydrologySurfaceData.Build(straight).SequenceEqual(HydrologySurfaceData.Build(straight,connectRiverDiagonals:false)),
            "orthogonal river widths and authored mod routes stay byte-identical");
        var separatePixels = new byte[32*16*2];
        separatePixels[(4*32+4)*2+1]=160;separatePixels[(6*32+6)*2+1]=160;
        var separate = new HydrologyRaster(32,16,separatePixels,1,0);
        Check(HydrologySurfaceData.Build(separate).SequenceEqual(HydrologySurfaceData.Build(separate,connectRiverDiagonals:false)),
            "genuine gaps are never bridged across missing authored cells");
        var isolatedSurface = HydrologySurfaceData.Build(separate);
        Check(CubicRiver(4,4,isolatedSurface)+TerrainCatalog.Water.RiverInset<0,
            "cubic averaging cannot erase an isolated authored headwater");
        var seamPixels = new byte[32*16*2];
        seamPixels[(5*32+31)*2+1]=96;seamPixels[6*32*2+1]=96;
        var seamSource = new HydrologyRaster(32,16,seamPixels,1,0);
        var seamDrawn = HydrologySurfaceData.Build(seamSource);
        var seamOriginal = HydrologySurfaceData.Build(seamSource,connectRiverDiagonals:false);
        Check(seamDrawn[(5*32)*3+1] < seamOriginal[(5*32)*3+1] &&
            seamDrawn[(5*32+16)*3+1] == seamOriginal[(5*32+16)*3+1],
            "diagonal contacts wrap longitude locally without a global stripe");
        var polarPixels = new byte[32*16*2];
        polarPixels[8*2+1]=96;polarPixels[(15*32+9)*2+1]=96;
        var polar = new HydrologyRaster(32,16,polarPixels,1,0);
        Check(HydrologySurfaceData.Build(polar).SequenceEqual(HydrologySurfaceData.Build(polar,connectRiverDiagonals:false)),
            "opposite poles cannot form a river connection");
        foreach(var dimensions in new[]{(1,1),(1,4),(4,1),(2,2)})
        {
            var tiny = new HydrologyRaster(dimensions.Item1,dimensions.Item2,
                Enumerable.Repeat((byte)96,dimensions.Item1*dimensions.Item2*2).ToArray(),1,1);
            Check(HydrologySurfaceData.Build(tiny).Length==dimensions.Item1*dimensions.Item2*3,
                "bounded small-map contour " + dimensions);
        }
        Check(TerrainCatalog.Water.RiverProfiles.Single().FeatureNames.Contains("Dnipro"), "shipped Dnipro profile loads through moddable water settings");
        Check(HydrologyCatalog.RiverFeatures.Count(r => TerrainCatalog.Water.RiverProfiles.Single().FeatureNames.Contains(r.Name)) == 3,
            "profile matches canonical English names of the real river and reservoir centreline");
        VerifyRiverCentreAtlas();
    }

    private static void VerifyRiverCentreAtlas()
    {
        const int width = 2049;
        const int height = 1025;
        const int tileSide = HydrologySurfaceData.RiverTileSide;
        const int columns = (width + tileSide - 1) / tileSide;
        var dense = new byte[width * height * 4];
        void SetSample(int x, int y)
        {
            var at = (y * width + x) * 4;
            dense[at] = (byte)x;
            dense[at + 1] = (byte)y;
            dense[at + 2] = 196;
            dense[at + 3] = 255;
        }
        for (var tile = 0; tile < 256; tile++)
            SetSample(tile % columns * tileSide, tile / columns * tileSide);
        SetSample(31, 31);
        SetSample(width - 1, height - 1);

        var atlas = HydrologySurfaceData.PackRiverCentreline(dense, width, height)
            ?? throw new Exception("expected a sparse centreline atlas");
        if (atlas.Pixels.Length >= dense.Length || atlas.HeaderRows != 2)
            throw new Exception("expected a smaller atlas with a multi-row directory");
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y / tileSide * columns + x / tileSide) * 4;
            var ordinal = atlas.Pixels[index] | atlas.Pixels[index + 1] << 8;
            var source = (y * width + x) * 4;
            if (ordinal == 0)
            {
                if (dense[source + 2] != 0) throw new Exception($"wet sample omitted at ({x}, {y})");
                continue;
            }

            var tile = ordinal - 1;
            var tileColumns = atlas.Width / tileSide;
            var atlasX = tile % tileColumns * tileSide + x % tileSide;
            var atlasY = atlas.HeaderRows + tile / tileColumns * tileSide + y % tileSide;
            var target = (atlasY * atlas.Width + atlasX) * 4;
            if (!dense.AsSpan(source, 4).SequenceEqual(atlas.Pixels.AsSpan(target, 4)))
                throw new Exception($"centreline sample changed at ({x}, {y})");
        }
        Array.Fill(dense, (byte)255);
        if (HydrologySurfaceData.PackRiverCentreline(dense, width, height) is not null)
            throw new Exception("dense centreline should keep the original texture");
        Console.WriteLine("PASS hydrology: centreline atlas preserves all samples at tile boundaries, including partial tiles and 16-bit tile indices");
    }
}
