using System;
using DealOrForce.Simulation;

static class WeatherReadabilityTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("weather: " + message); Console.WriteLine("PASS weather: " + message); }
    public static void Run()
    {
        var defaults = WeatherReadability.Read(null);
        Check(defaults.MistOpacity <= .25f, "default mist leaves most of the map visible");
        Check(defaults.DetailStrength < .1f && defaults.EdgeSoftness >= .15f, "broad soft cloud envelope dominates small details");
        var d = new Defines { CloudOpacity = -4, MistOpacity = 12, CloudEdgeSoftness = 0, CloudDetailStrength = 2 };
        var bounded = WeatherReadability.Read(d);
        Check(bounded.CloudOpacity == 0 && bounded.MistOpacity == 1, "mod opacity is bounded rather than overflowing");
        Check(bounded.EdgeSoftness > 0 && bounded.DetailStrength == .3f, "shader smoothstep keeps a nonzero width and bounded detail");
        d.CloudOpacity = float.NaN; d.MistOpacity = float.PositiveInfinity;
        d.CloudEdgeSoftness = float.NegativeInfinity; d.CloudDetailStrength = float.NaN;
        Check(WeatherReadability.Read(d) == defaults, "nonfinite mod values recover to safe presentation defaults");
        var c = new Campaign(1010); var fingerprint = CampaignFingerprint.Of(c);
        _ = WeatherReadability.Read(c.Defines);
        Check(CampaignFingerprint.Of(c) == fingerprint, "weather readout does not mutate a campaign");
    }
}
