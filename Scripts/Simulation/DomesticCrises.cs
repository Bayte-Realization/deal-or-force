using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float ResidentAwarenessLiteracyWeight { get; set; } = .60f;
    public float ResidentAwarenessUrbanWeight { get; set; } = .20f;
    public float ResidentAwarenessChangePerDay { get; set; } = .0015f;
    public float CoupPressureThreshold { get; set; } = .78f;
    public float CoupPressureRisePerDay { get; set; } = .006f;
    public float CoupPressureRecoveryPerDay { get; set; } = .008f;
    public int CoupRequiredPressureDays { get; set; } = 35;
    public int CoupAftermathCooldownDays { get; set; } = 365;
    public int CrisisResponseDurationDays { get; set; } = 90;
    public int CrisisResponseCooldownDays { get; set; } = 180;
    public float CrisisResponseCostShare { get; set; } = .0006f;
    public float CrisisResponseCostFloor { get; set; } = .015f;
    /// <summary>How long a paid settlement of one region cools it.</summary>
    public int CrisisConciliationDays { get; set; } = 120;
    /// <summary>Cost is this share of GDP times the region's share of the country's people, and never below the floor.</summary>
    public float CrisisConciliationCostShare { get; set; } = .004f;
    public float CrisisConciliationCostFloor { get; set; } = .02f;
    public float CrisisConciliationPressurePerDay { get; set; } = .003f;
    public float CrisisConciliationRadicalismPerDay { get; set; } = .0016f;
    public float CrisisConciliationDivergencePerDay { get; set; } = .0012f;
    public float CrisisConciliationApprovalPerDay { get; set; } = .001f;
    /// <summary>Below this heat the region is not asking for the money.</summary>
    public float CrisisConciliationHeat { get; set; } = .22f;
    /// <summary>A country nobody plays pays only for a louder region, and only with this much treasury in reserve.</summary>
    public float CrisisConciliationAiHeat { get; set; } = .55f;
    public float CrisisConciliationAiReserve { get; set; } = 4f;
}

public sealed partial class ScenarioState
{
    // -1 marks an old save or a new unauthored region, not an uneducated population.
    public float ResidentAwareness = -1f;
}

public sealed partial class Nation
{
    public DomesticCrisisState DomesticCrisis = new();
}

/// <summary>Saved history of institutional strain, not a random event probability.</summary>
public sealed class DomesticCrisisState
{
    public float CoupPressure;
    public int DaysAboveThreshold;
    public int LastReviewDay = -1;
    public int CooldownUntilDay = -1;
    public string LastOutcomeId = "";
    public int LastOutcomeDay = -1;
    public string ResponseId = "";
    public int ResponseUntilDay = -1;
    public int ResponseCooldownUntilDay = -1;
    /// <summary>The region being paid to settle, or -1. One at a time.</summary>
    public int ConciliationRegion = -1;
    /// <summary>The last day that payment still cools the region. -1 when none is running.</summary>
    public int ConciliationUntilDay = -1;
}

public sealed class DomesticCrisisRegion
{
    public ScenarioState Region { get; init; }
    public float Awareness { get; init; }
    public float Radicalism { get; init; }
    public float SecessionPressure { get; init; }
    public float SecessionThreshold { get; init; } = 1f;
    public bool IsCapital { get; init; }
}

public sealed class DomesticCrisisSnapshot
{
    public float Awareness { get; init; }
    public float Radicalism { get; init; }
    public float CoupPressure { get; init; }
    public float CoupTarget { get; init; }
    public float CoupThreshold { get; init; }
    public int DaysAboveThreshold { get; init; }
    public int RequiredDays { get; init; }
    public int CooldownDays { get; init; }
    public bool CoupEligible { get; init; }
    public string ResponseId { get; init; } = "";
    public int ResponseDays { get; init; }
    public int ResponseCooldownDays { get; init; }
    public string LastOutcomeId { get; init; } = "";
    public int LastOutcomeDay { get; init; } = -1;
    public string[] Drivers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DomesticCrisisRegion> Regions { get; init; } = Array.Empty<DomesticCrisisRegion>();
}

public static class ResidentAwareness
{
    /// <summary>Education and civic networks, never anger, wealth poverty or an ethnicity label.</summary>
    public static float Target(Campaign c, ScenarioState region) => M.Clamp01(.10f +
        M.Clamp01(region.Literacy) * c.Defines.ResidentAwarenessLiteracyWeight +
        M.Clamp01(region.Urbanization) * c.Defines.ResidentAwarenessUrbanWeight);

    public static float Of(Campaign c, ScenarioState region) => region.ResidentAwareness < 0f
        ? Target(c, region) : M.Clamp01(region.ResidentAwareness);
}

public static partial class DomesticCrises
{
    public static DomesticCrisisSnapshot Read(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return new();
        var regions = c.StatesOf(nation);
        double people = 0, awareness = 0, radicalism = 0;
        var rows = new List<DomesticCrisisRegion>(regions.Length);
        foreach (var region in regions)
        {
            var conscious = ResidentAwareness.Of(c, region);
            if (region.Population > 0)
            {
                people += region.Population;
                awareness += region.Population * (double)conscious;
                radicalism += region.Population * (double)M.Clamp01(region.Radicalism);
            }
            rows.Add(new DomesticCrisisRegion { Region = region, Awareness = conscious,
                Radicalism = M.Clamp01(region.Radicalism), SecessionPressure = MathF.Max(0f, region.SecessionPressure),
                IsCapital = region.MapIndex == nation.CapitalStateMapIndex });
        }
        var memory = nation.DomesticCrisis ?? new();
        var anger = people > 0 ? (float)(radicalism / people) : 0f;
        var capital = regions.FirstOrDefault(r => r.MapIndex == nation.CapitalStateMapIndex);
        var drivers = new List<string>();
        var target = Target(c, nation, anger, M.Clamp01(capital?.Radicalism ?? 0f), drivers);
        return new DomesticCrisisSnapshot { Awareness = people > 0 ? (float)(awareness / people) : 0f,
            Radicalism = anger, CoupTarget = target, CoupEligible = Eligible(c, nation), Drivers = drivers.ToArray(),
            CoupPressure = M.Clamp01(memory.CoupPressure), CoupThreshold = Threshold(c),
            DaysAboveThreshold = memory.DaysAboveThreshold,
            RequiredDays = Math.Max(1, c.Defines.CoupRequiredPressureDays),
            CooldownDays = Math.Max(0, memory.CooldownUntilDay - c.ElapsedDays),
            ResponseId = memory.ResponseId, ResponseDays = Math.Max(0, memory.ResponseUntilDay - c.ElapsedDays),
            ResponseCooldownDays = Math.Max(0, memory.ResponseCooldownUntilDay - c.ElapsedDays),
            LastOutcomeId = memory.LastOutcomeId, LastOutcomeDay = memory.LastOutcomeDay, Regions = rows };
    }

    public static void Tick(Campaign c)
    {
        if (c is null) return;
        var awarenessEnabled = c.FeatureEnabled("resident_awareness");
        var crisesEnabled = c.FeatureEnabled("domestic_crises") && c.FeatureEnabled("politics");
        foreach (var nation in c.Nations)
        {
            var memory = nation.DomesticCrisis ??= new();
            if (memory.LastReviewDay == c.ElapsedDays) continue;
            // Start at the current conditions; a legacy save never accrues years of backdated crises.
            var days = memory.LastReviewDay < 0 ? 1 : Math.Clamp(c.ElapsedDays - memory.LastReviewDay, 0, 60);
            memory.LastReviewDay = c.ElapsedDays;
            if (awarenessEnabled)
                foreach (var region in c.StatesOf(nation))
                {
                    if (region.ResidentAwareness < 0f) region.ResidentAwareness = ResidentAwareness.Target(c, region);
                    else region.ResidentAwareness = Move(region.ResidentAwareness, ResidentAwareness.Target(c, region),
                        MathF.Max(0f, c.Defines.ResidentAwarenessChangePerDay) * days);
                }
            PoliticalCohorts.Review(c, nation, days);
            if (crisesEnabled)
            {
                ReviewResponse(c, nation, days);
                ReviewConciliation(c, nation, days);
                ReviewPressure(c, nation, days);
                ConsiderConciliation(c, nation);
            }
        }
    }

    private static float Threshold(Campaign c) => M.Clamp(c.Defines.CoupPressureThreshold, .05f, 1f);
    private static float Move(float value, float target, float amount) => value < target
        ? MathF.Min(target, value + amount) : MathF.Max(target, value - amount);
}
