using System;
using System.Linq;
using DealOrForce;
using DealOrForce.Simulation;

/// <summary>
/// Display width of the three authored river classes. The source plate is not rewritten,
/// so a crossing still reads the same byte.
/// </summary>
static class RiverWidthTests
{
    const float Reach = 0.90f;

    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("river width: " + label);
        Console.WriteLine("PASS river width: " + label);
    }

    public static void Run()
    {
        ReadingDoesNotChangeTheFingerprint();
        CreekStaysOneCell();
        ClassesSeparateByWidth();
        NamedStrokeIsNotFattened();
        DatelineWidensLocally();
        OversizedReachIsClamped();
        ShippedReachLoads();
    }

    static void ReadingDoesNotChangeTheFingerprint()
    {
        var campaign = new Campaign(29);
        campaign.Nations.Add(new Nation { Id = 0, Tag = "RW0", Name = "Reader", Population = 1_000_000 });
        Check(campaign.Nations[0].Id == 0 && campaign.NationById(0) == campaign.Nations[0],
            "nation id 0 stands at index 0");
        var before = CampaignFingerprint.Of(campaign);
        var pixels = Line(8, 225);
        var original = (byte[])pixels.Clone();
        var surface = HydrologySurfaceData.Build(new HydrologyRaster(32, 16, pixels, 1, 0), widthReach: Reach);
        Check(CampaignFingerprint.Of(campaign) == before, "building the river plate does not change the fingerprint");
        Check(pixels.SequenceEqual(original), "the plate build does not rewrite crossing bytes");
        Check(surface[(8 * 32 + 8) * 3 + 1] < 120, "the same read draws a trunk deeper than the one-cell shore");
        Check(SaveSystem.FormatVersion == 2, "save format stays 2");
    }

    static void CreekStaysOneCell()
    {
        var pixels = Line(8, 96);
        var plain = HydrologySurfaceData.Build(new HydrologyRaster(32, 16, pixels, 1, 0));
        var wide = HydrologySurfaceData.Build(new HydrologyRaster(32, 16, pixels, 1, 0), widthReach: Reach);
        Check(wide.SequenceEqual(plain), "class 96 stays the authored one-cell shore");
        Check(plain[(8 * 32 + 8) * 3 + 1] == 120 && plain[(7 * 32 + 8) * 3 + 1] == 136,
            "the unscaled creek centre is half a texel and the next cell is dry");
    }

    static void ClassesSeparateByWidth()
    {
        var plain = Plate(8, 225, 0f);
        var ordinary = Plate(8, 160, Reach);
        var trunk = Plate(8, 225, Reach);
        Check(trunk[(8 * 32 + 8) * 3 + 1] == 106 && trunk[(7 * 32 + 8) * 3 + 1] == 122,
            "a trunk centre stores 1.375 texels (byte 106) and the next cell centre is water (byte 122)");
        Check(ordinary[(8 * 32 + 8) * 3 + 1] == 113 && ordinary[(7 * 32 + 8) * 3 + 1] == 129,
            "an ordinary centre stores 0.9375 texels (byte 113) and the next cell centre stays dry (byte 129)");
        Check(trunk[(8 * 32 + 8) * 3 + 1] < ordinary[(8 * 32 + 8) * 3 + 1] &&
              ordinary[(8 * 32 + 8) * 3 + 1] < plain[(8 * 32 + 8) * 3 + 1],
            "trunk, ordinary and hairline are three different shores");
        Check(trunk[(8 * 32 + 8) * 3 + 2] == 225 && ordinary[(8 * 32 + 8) * 3 + 2] == 160,
            "class bytes stay on the plate for the shader");
        Check(Enumerable.Range(0, 32 * 16).All(cell => trunk[cell * 3] == plain[cell * 3]),
            "widening a river does not move a lake");
        Check(trunk[(0 * 32 + 8) * 3 + 1] == plain[(0 * 32 + 8) * 3 + 1],
            "eight cells off a trunk the shore is untouched");
    }

    static void NamedStrokeIsNotFattened()
    {
        var pixels = Line(8, 225);
        var profile = new RiverVisualProfile(new[] { "Example" }, 2.8f, 2.8f, 90f, -90f);
        var river = new RiverFeature("Example", 1, new[] { new[] { At(4, 8), At(12, 8) } });
        var plain = HydrologySurfaceData.Build(new HydrologyRaster(32, 16, pixels, 1, 0), new[] { profile }, new[] { river });
        var wide = HydrologySurfaceData.Build(new HydrologyRaster(32, 16, pixels, 1, 0), new[] { profile }, new[] { river }, widthReach: Reach);
        Check(plain.SequenceEqual(wide), "a named stroke keeps the width it already authored");
        Check(plain[(8 * 32 + 8) * 3 + 2] == 255, "the stroke still marks its own cells");
    }

    static void DatelineWidensLocally()
    {
        var pixels = new byte[32 * 16 * 2];
        pixels[(6 * 32 + 31) * 2 + 1] = 225;
        var plain = HydrologySurfaceData.Build(new HydrologyRaster(32, 16, pixels, 1, 0));
        var wide = HydrologySurfaceData.Build(new HydrologyRaster(32, 16, pixels, 1, 0), widthReach: Reach);
        Check(wide[(6 * 32) * 3 + 1] < plain[(6 * 32) * 3 + 1] && wide[(6 * 32) * 3 + 1] < 128,
            "a trunk crossing the date line widens onto the other edge");
        Check(wide[(6 * 32 + 16) * 3 + 1] == plain[(6 * 32 + 16) * 3 + 1],
            "the same trunk does not draw a stripe around the earth");
    }

    static void OversizedReachIsClamped()
    {
        var clamped = Plate(8, 225, 2.5f);
        var huge = Plate(8, 225, 9f);
        Check(huge.SequenceEqual(clamped), "a reach past 2.5 texels is drawn as 2.5");
        Check(huge[(0 * 32 + 8) * 3 + 1] == Plate(8, 225, 0f)[(0 * 32 + 8) * 3 + 1],
            "the clamp still leaves the far shore alone");
    }

    static void ShippedReachLoads()
    {
        TerrainCatalog.ResetCatalog();
        Check(MathF.Abs(TerrainCatalog.Water.RiverWidthReach - Reach) < 0.001f,
            "water settings load a trunk reach of 0.90 texels");
    }

    static byte[] Line(int y, byte importance)
    {
        var pixels = new byte[32 * 16 * 2];
        for (var x = 4; x <= 12; x++) pixels[(y * 32 + x) * 2 + 1] = importance;
        pixels[(2 * 32 + 4) * 2] = 255;
        return pixels;
    }

    static byte[] Plate(int y, byte importance, float reach)
    {
        var pixels = Line(y, importance);
        return HydrologySurfaceData.Build(new HydrologyRaster(32, 16, pixels, 1, 0), widthReach: reach);
    }

    static HydroPoint At(float x, float y) => new(-180f + (x + .5f) * 360f / 32f, 90f - (y + .5f) * 180f / 16f);
}
