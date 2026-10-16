using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float RiverSeasonalAmplitude { get; set; } = .20f;
    public float LakeWinterLogisticsLoss { get; set; } = .65f;
    /// <summary>Share of road capacity a deep winter takes at high latitude. Zero leaves roads fair-weather all year.</summary>
    public float RoadSeasonAmplitude { get; set; } = .22f;
}

/// <summary>Calendar-driven, deliberately modest seasonal proxy, not a weather forecast.
/// Derived from saved date and latitude, without new random state. Winter never turns
/// a lake into passable ground.</summary>
public static class WaterConditions
{
    public static float CrossingDifficulty(Campaign campaign, float latitude)
    {
        if (campaign is null || !SeasonOn(campaign, "seasonal_water")) return 1f;
        var temperate = M.Clamp01((MathF.Abs(latitude) - 15f) / 35f);
        var phase = MonthPhase(campaign.Date, latitude < 0f ? 10f : 4f);
        return 1f + Math.Clamp(campaign.Defines.RiverSeasonalAmplitude, 0f, .5f) * temperate * MathF.Cos(phase);
    }

    public static float LakeAvailability(Campaign campaign, float latitude)
    {
        if (campaign is null || !SeasonOn(campaign, "seasonal_water")) return 1f;
        var cold = M.Clamp01((MathF.Abs(latitude) - 35f) / 25f);
        var winter = MathF.Max(0f, MathF.Cos(MonthPhase(campaign.Date, latitude < 0f ? 7f : 1f)));
        return 1f - Math.Clamp(campaign.Defines.LakeWinterLogisticsLoss, 0f, .9f) * cold * winter;
    }

    /// <summary>Both the master <c>seasons</c> switch and the named part must be on.</summary>
    private static bool SeasonOn(Campaign campaign, string part) =>
        campaign.FeatureEnabled("seasons") && campaign.FeatureEnabled(part);

    private static float MonthPhase(GameDate date, float peak) =>
        (date.Month + (date.Day - 1) / 31f - peak) * MathF.PI / 6f;

    /// <summary>
    /// What to call the time of year at this latitude. Empty when seasons are off.
    /// The words follow the same calendar as the roads: January is deep winter in Warsaw
    /// and high summer in Buenos Aires, and the equator gets a wet season rather than a frost.
    /// </summary>
    public static string SeasonCaption(Campaign campaign, float latitude)
    {
        if (campaign is null || !campaign.FeatureEnabled("seasons") || !float.IsFinite(latitude)) return "";
        var phase = MonthPhase(campaign.Date, latitude < 0f ? 7f : 1f);
        var winter = MathF.Max(0f, MathF.Cos(phase));
        if (MathF.Abs(latitude) < 16f) return winter >= 0.35f ? "The wet season" : "The dry season";
        if (winter >= 0.60f) return MathF.Abs(latitude) >= 48f ? "Deep winter" : "Winter";
        if (winter >= 0.20f) return MathF.Sin(phase) > 0f ? "The thaw" : "The harvest";
        return MathF.Cos(phase) <= -0.75f ? "High summer" : "The bright months";
    }

    public static float MovementMultiplier(Campaign campaign, int from, int to)
    {
        var map = campaign?.ProvinceMap;
        if (map is null) return 1f;
        var strength = map.RiverCrossingStrength(from, to);
        if (strength <= 0f) return 1f;
        var difficulty = CrossingDifficulty(campaign, map.CenterOf(to).Latitude);
        return Math.Clamp(1f - (1f - campaign.Defines.RiverMovementMultiplier) * strength * difficulty, .25f, 1f);
    }

    public static float AttackMultiplier(Campaign campaign, float strength, int province)
    {
        if (strength <= 0f) return 1f;
        var normal = M.Lerp(campaign.Defines.RiverAttackMultiplier, campaign.Defines.MajorRiverAttackMultiplier,
            M.Clamp01((strength - .45f) / .45f));
        var latitude = campaign.ProvinceMap?.CenterOf(province).Latitude ?? 0f;
        return Math.Clamp(1f - (1f - normal) * CrossingDifficulty(campaign, latitude), .25f, 1f);
    }

    /// <summary>
    /// How much of a fair-weather road the season leaves. Tropics stay open.
    /// January slows the north; July slows the south. Derived from the saved date, so it needs no extra state.
    /// </summary>
    public static float RoadThroughput(Campaign campaign, float latitude)
    {
        if (campaign is null || !SeasonOn(campaign, "seasonal_roads")) return 1f;
        var weight = M.Clamp01((MathF.Abs(latitude) - 23f) / 42f);
        var winter = MathF.Max(0f, MathF.Cos(MonthPhase(campaign.Date, latitude < 0f ? 7f : 1f)));
        var loss = Math.Clamp(campaign.Defines.RoadSeasonAmplitude, 0f, .40f) * weight * winter;
        return Math.Clamp(1f - loss, .60f, 1f);
    }

    public static float LakeSupplyMultiplier(Campaign campaign, int province)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || !map.HasLakeAccess(province)) return 1f;
        return 1f + campaign.Defines.LakeSupplyBonus * LakeAvailability(campaign, map.CenterOf(province).Latitude);
    }
}
