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
public sealed partial class DomesticCrisisState
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
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same daily review, yielded between countries.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null) yield break;
        var awarenessEnabled = c.FeatureEnabled("resident_awareness");
        var crisesEnabled = c.FeatureEnabled("domestic_crises") && c.FeatureEnabled("politics");
        // Every region's alienation for the day, read side by side before the countries are
        // taken one at a time (PoliticalCohorts.AlienationTable).
        var alienation = PoliticalCohorts.ReadAlienation(c);

        // Pre-compute resident awareness across very large worlds in parallel (chunked range pass),
        // then apply deterministically per nation so 1-thread and N-thread runs agree bit-for-bit.
        // On normal worlds (< 8192 states), updating inside the per-nation pass below avoids pool
        // dispatch, ArrayPool churn, and second scans over every region object.
        var awarenessPrecomputed = false;
        if (awarenessEnabled && c.States.Count >= 8192)
        {
            // Warm up thread-safe dense index snapshot before fanning out.
            _ = c.NationsByMapIndex();
            var states = c.States;
            var stateCount = states.Count;
            var precomputedAwareness = System.Buffers.ArrayPool<float>.Shared.Rent(stateCount);
            try
            {
                var changePerDay = MathF.Max(0f, c.Defines.ResidentAwarenessChangePerDay);
                var elapsed = c.ElapsedDays;
                DeterministicParallel.ForRange(c, 0, stateCount, (from, to) =>
                {
                    for (var i = from; i < to; i++)
                    {
                        var region = states[i];
                        var owner = region.NationMapIndex > 0 ? c.NationByMapIndex(region.NationMapIndex) : null;
                        if (owner is null) { precomputedAwareness[i] = region.ResidentAwareness; continue; }
                        var lastReview = owner.DomesticCrisis is { } mem ? mem.LastReviewDay : -1;
                        if (lastReview == elapsed) { precomputedAwareness[i] = region.ResidentAwareness; continue; }
                        var days = lastReview < 0 ? 1 : Math.Clamp(elapsed - lastReview, 0, 60);
                        var target = ResidentAwareness.Target(c, region);
                        precomputedAwareness[i] = region.ResidentAwareness < 0f
                            ? target
                            : Move(region.ResidentAwareness, target, changePerDay * days);
                    }
                }, minItems: 64);
                // Apply in ascending state order on the calling thread.
                for (var i = 0; i < stateCount; i++)
                    states[i].ResidentAwareness = precomputedAwareness[i];
                awarenessPrecomputed = true;
            }
            finally
            {
                System.Buffers.ArrayPool<float>.Shared.Return(precomputedAwareness);
            }
        }
        if (awarenessEnabled && !awarenessPrecomputed)
            _ = c.NationsByMapIndex();

        var tick = 0;
        var awarenessChangePerDay = MathF.Max(0f, c.Defines.ResidentAwarenessChangePerDay);
        try
        {
            var activeNations = c.ActiveNations;
            for (var ni = 0; ni < activeNations.Count; ni++)
            {
                var nation = activeNations[ni];
                if (++tick % 32 == 0 && Campaign.StageOverdue()) yield return 0;
                if (c.IsRetired(nation)) continue;
                var memory = nation.DomesticCrisis ??= new();
                if (memory.LastReviewDay == c.ElapsedDays) continue;
                // Start at the current conditions; a legacy save never accrues years of backdated crises.
                var days = memory.LastReviewDay < 0 ? 1 : Math.Clamp(c.ElapsedDays - memory.LastReviewDay, 0, 60);
                memory.LastReviewDay = c.ElapsedDays;
                var regions = c.StatesOf(nation);
                if (regions.Length == 0 && (nation.Movement is not null || nation.Population <= 0) &&
                    memory.ResponseUntilDay < 0 && memory.ConciliationUntilDay < 0 && memory.CoupPressure <= 0f)
                {
                    memory.DaysAboveThreshold = 0;
                    alienation?.ClearSummary();
                    continue;
                }
                if (awarenessEnabled && (!awarenessPrecomputed || nation.MapIndex <= 0) &&
                    regions.Length > 0 &&
                    (nation.MapIndex <= 0 || ReferenceEquals(c.NationByMapIndex(nation.MapIndex), nation)))
                {
                    var awarenessStep = awarenessChangePerDay * days;
                    for (var ri = 0; ri < regions.Length; ri++)
                    {
                        var region = regions[ri];
                        var target = ResidentAwareness.Target(c, region);
                        region.ResidentAwareness = region.ResidentAwareness < 0f
                            ? target
                            : Move(region.ResidentAwareness, target, awarenessStep);
                    }
                }
                var cohortPlace = (uint)nation.Id < (uint)c.Nations.Count && ReferenceEquals(c.Nations[nation.Id], nation)
                    ? nation.Id
                    : c.Nations.IndexOf(nation);
                PoliticalCohorts.Review(c, nation, days, alienation, cohortPlace);
                if (crisesEnabled)
                {
                    ReviewResponse(c, nation, days);
                    var couldConciliate = memory.ConciliationRegion >= 0 && memory.ConciliationUntilDay >= c.ElapsedDays - days;
                    ReviewConciliation(c, nation, days);
                    if (couldConciliate) alienation?.InvalidateMood();
                    // A coup is the one thing in this review that reaches beyond its own country.
                    if (regions.Length > 0 && ReviewPressure(c, nation, days)) alienation?.Spoil();
                    alienation?.ClearSummary();
                    if (!nation.IsHuman && nation.Movement is null && regions.Length > 0)
                    {
                        ConsiderConciliation(c, nation);
                        // The longer lever beside the season's payment: a region that runs itself.
                        RegionalAutonomy.ConsiderGrant(c, nation);
                    }
                    // The street is read weekly: a wave takes days to build, and Read walks every region.
                    if (c.Defines.ProtestsEnabled && nation.Movement is null && nation.Population > 0 && regions.Length > 0 &&
                        (c.ElapsedDays + nation.Id) % 7 == 0)
                    {
                        AwarenessAndRadicalism(c, nation, out var awareness, out var radicalism);
                        MassProtests.Review(c, nation, awareness, radicalism, 7);
                    }
                }
                else
                {
                    alienation?.ClearSummary();
                }
            }
        }
        finally
        {
            alienation?.ClearSummary();
        }
    }

    private static void AwarenessAndRadicalism(Campaign c, Nation nation, out float awareness, out float radicalism)
    {
        var regions = c.StatesOf(nation);
        double people = 0, awarenessSum = 0, radicalismSum = 0;
        for (var i = 0; i < regions.Length; i++)
        {
            var region = regions[i];
            if (region.Population <= 0) continue;
            people += region.Population;
            awarenessSum += region.Population * (double)ResidentAwareness.Of(c, region);
            radicalismSum += region.Population * (double)M.Clamp01(region.Radicalism);
        }
        awareness = people > 0 ? (float)(awarenessSum / people) : 0f;
        radicalism = people > 0 ? (float)(radicalismSum / people) : 0f;
    }

    private static float Threshold(Campaign c) => M.Clamp(c.Defines.CoupPressureThreshold, .05f, 1f);
    private static float Move(float value, float target, float amount) => value < target
        ? MathF.Min(target, value + amount) : MathF.Max(target, value - amount);
}
