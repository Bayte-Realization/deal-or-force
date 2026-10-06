using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float RiverSeasonalAmplitude { get; set; } = .20f;
    public float LakeWinterLogisticsLoss { get; set; } = .65f;
}

/// <summary>Calendar-driven, deliberately modest seasonal proxy, not a weather forecast.
/// Derived from saved date and latitude, without new random state. Winter never turns
/// a lake into passable ground.</summary>
public static class WaterConditions
{
    public static float CrossingDifficulty(Campaign campaign, float latitude)
    {
        if (campaign is null || !campaign.FeatureEnabled("seasonal_water")) return 1f;
        var temperate = M.Clamp01((MathF.Abs(latitude) - 15f) / 35f);
        var phase = MonthPhase(campaign.Date, latitude < 0f ? 10f : 4f);
        return 1f + Math.Clamp(campaign.Defines.RiverSeasonalAmplitude, 0f, .5f) * temperate * MathF.Cos(phase);
    }

    public static float LakeAvailability(Campaign campaign, float latitude)
    {
        if (campaign is null || !campaign.FeatureEnabled("seasonal_water")) return 1f;
        var cold = M.Clamp01((MathF.Abs(latitude) - 35f) / 25f);
        var winter = MathF.Max(0f, MathF.Cos(MonthPhase(campaign.Date, latitude < 0f ? 7f : 1f)));
        return 1f - Math.Clamp(campaign.Defines.LakeWinterLogisticsLoss, 0f, .9f) * cold * winter;
    }

    private static float MonthPhase(GameDate date, float peak) =>
        (date.Month + (date.Day - 1) / 31f - peak) * MathF.PI / 6f;

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

    public static float LakeSupplyMultiplier(Campaign campaign, int province)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || !map.HasLakeAccess(province)) return 1f;
        return 1f + campaign.Defines.LakeSupplyBonus * LakeAvailability(campaign, map.CenterOf(province).Latitude);
    }
}
