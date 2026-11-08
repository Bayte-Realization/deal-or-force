using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class AtlasSurfaceTests
{
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS atlas surfaces: " + label); }
    public static void Run()
    {
        Check(AtlasSurfaceLighting.Light(0, 0) > AtlasSurfaceLighting.Light(1, 1), "raised lighting has one upper-left source");
        Check(AtlasSurfaceLighting.Light(0, 0, true) < AtlasSurfaceLighting.Light(0, 1, true), "pressed face has an inverted recessed bevel");
        Check(AtlasSurfaceLighting.Light(-100, -100) == AtlasSurfaceLighting.Light(0, 0)
            && AtlasSurfaceLighting.Light(100, 100) == AtlasSurfaceLighting.Light(1, 1), "sampling outside the face clamps to its edges");
        Check(AtlasSurfaceLighting.Relief(-10) == 0 && AtlasSurfaceLighting.Relief(10) == 1, "relief is bounded and can be switched off by data");
        Check(AtlasSurfaceLighting.Relief(float.NaN) == .65f && AtlasSurfaceLighting.Relief(float.PositiveInfinity) == .65f, "invalid mod relief has a finite fallback");
        Check(AtlasSurfaceLighting.GrainAmount(-1) == 0 && AtlasSurfaceLighting.GrainAmount(10) == .05f, "grain has a readability cap and an off value");
        Check(AtlasSurfaceLighting.GrainAmount(float.NaN) == .018f, "invalid grain has a safe fallback");
        Check(AtlasSurfaceLighting.MaterialShade(0, 0, false, .65f) >
            AtlasSurfaceLighting.MaterialShade(1, 1, false, .65f), "instrument face is raised toward the upper light");
        Check(AtlasSurfaceLighting.MaterialShade(.5f, 0, true, .65f) <
            AtlasSurfaceLighting.MaterialShade(.5f, 1, true, .65f), "gauge track has the opposite recessed lighting");
        Check(AtlasSurfaceLighting.MaterialShade(0, 0, false, 0) == 0 &&
            AtlasSurfaceLighting.MaterialShade(1, 1, true, 0) == 0, "zero relief disables material face lighting");
        Check(float.IsFinite(AtlasSurfaceLighting.MaterialShade(float.NaN, float.PositiveInfinity, false, float.NaN)),
            "malformed material inputs cannot create invalid pixels");
        var samples = Enumerable.Range(0, 4096).Select(i => AtlasSurfaceLighting.Grain(i % 64, i / 64)).ToArray();
        Check(samples.All(value => float.IsFinite(value) && value >= -1 && value <= 1), "grain samples stay in a finite signed range");
        Check(Math.Abs(samples.Average()) < .05, "texture has no large brightness bias");
        Check(samples.SequenceEqual(Enumerable.Range(0, 4096).Select(i => AtlasSurfaceLighting.Grain(i % 64, i / 64))), "material is deterministic without a random generator");
        Check(samples.Distinct().Count() > 3900, "grain is not a visible checkerboard");
        var campaign = Campaign.CreateBootstrap(73);
        var before = CampaignFingerprint.Of(campaign);
        foreach (var sample in samples) _ = AtlasSurfaceLighting.Light(sample, sample);
        Check(before == CampaignFingerprint.Of(campaign), "material sampling never changes the world fingerprint");
        Check(InterfaceSkin.Metric("atlas.relief", -1) == .65f && InterfaceSkin.Metric("atlas.grain", -1) == .018f, "shipped material controls load from the moddable skin");
    }
}
