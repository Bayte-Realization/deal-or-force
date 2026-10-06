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
        Check(TerrainCatalog.Water.RiverProfiles.Single().FeatureNames.Contains("Dnipro"), "shipped Dnipro profile loads through moddable water settings");
        Check(HydrologyCatalog.RiverFeatures.Count(r => TerrainCatalog.Water.RiverProfiles.Single().FeatureNames.Contains(r.Name)) == 3,
            "profile matches canonical English names of the real river and reservoir centreline");
    }
}
