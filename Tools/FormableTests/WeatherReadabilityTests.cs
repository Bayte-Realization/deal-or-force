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
        Check(WeatherReadability.LayersAllowed(null) && WeatherReadability.LayersAllowed(c), "clouds and mist draw until a campaign turns them off");
        c.SetFeature("weather", false);
        Check(!WeatherReadability.LayersAllowed(c), "defines can switch the cloud and mist shell off");
        c.Date = new GameDate(2024, 1, 15);
        Check(WaterConditions.RoadThroughput(c, 60) < 0.9f && WaterConditions.RoadThroughput(c, 0) == 1f,
            "January slows northern roads and leaves the equator open");
        Check(WaterConditions.SeasonCaption(c, 52) == "Deep winter" && WaterConditions.SeasonCaption(c, 0) == "The wet season",
            "January is deep winter in the north and a wet season on the equator");
        c.Date = new GameDate(2024, 7, 15);
        Check(WaterConditions.SeasonCaption(c, 52) == "High summer" && WaterConditions.SeasonCaption(c, -52) == "Deep winter",
            "July is high summer in the north and deep winter in the south");
        c.Date = new GameDate(2024, 1, 15);
        c.SetFeature("seasons", false);
        Check(WaterConditions.RoadThroughput(c, 60) == 1f && WaterConditions.LakeAvailability(c, 60) == 1f
              && WaterConditions.CrossingDifficulty(c, 55) == 1f,
            "seasons master switch returns water and roads to fair weather");
        Check(WaterConditions.SeasonCaption(c, 52) == "", "a switched-off calendar does not invent a season");
        c.SetFeature("seasons", true);
        c.SetFeature("seasonal_roads", false);
        Check(WaterConditions.RoadThroughput(c, 60) == 1f && WaterConditions.LakeAvailability(c, 60) < 1f,
            "road season switches off without silencing winter lakes");
    }
}
