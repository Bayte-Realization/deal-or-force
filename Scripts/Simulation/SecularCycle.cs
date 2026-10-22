using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// Whether economies rise and fall over decades (SecularCycle). Off, every country keeps the
    /// productivity gap it had in 2024 for ever, which is the world as it was before.
    /// </summary>
    public bool SecularCycleEnabled { get; set; } = true;

    /// <summary>
    /// Catch-up: a year's growth of a country's productivity per unit of log distance below the
    /// frontier, at full readiness. 0.012 lets a stable, schooled country at a tenth of the
    /// frontier's output per head gain about 2.8 % a year on it - China's pace, not a miracle's.
    /// </summary>
    public float SecularConvergence { get; set; } = 0.012f;

    /// <summary>The pull back towards the frontier on a country above it, per unit of log distance.</summary>
    public float SecularLeaderDrag { get; set; } = 0.010f;

    /// <summary>
    /// The height of a country's own long wave of fortune, a year's growth at the crest: the
    /// decades of rise and of stagnation every economy goes through, with a period and a phase
    /// of its own drawn from the campaign seed.
    /// </summary>
    public float SecularWaveAmplitude { get; set; } = 0.008f;

    /// <summary>Shortest and longest long wave, in years.</summary>
    public float SecularWaveShortestYears { get; set; } = 35f;
    public float SecularWaveLongestYears { get; set; } = 60f;

    /// <summary>A year's luck either way, drawn from the seed: the good harvest, the bad bank.</summary>
    public float SecularFortune { get; set; } = 0.006f;

    /// <summary>What a year of war takes from the growth of an economy, beyond the year itself.</summary>
    public float SecularWarDrag { get; set; } = 0.006f;

    /// <summary>What full imperial overstretch takes from a year's growth.</summary>
    public float SecularOverstretchDrag { get; set; } = 0.012f;

    /// <summary>Below this stability a country is in a time of troubles (Nation.TroublesDays).</summary>
    public float TroublesStability { get; set; } = 0.20f;
    /// <summary>At or above this the troubles are over and the count starts again.</summary>
    public float TroublesOverStability { get; set; } = 0.40f;
    /// <summary>Days of troubles that double how fast radicalism fades; capped at TroublesExhaustionCap more.</summary>
    public float TroublesExhaustionDays { get; set; } = 730f;
    public float TroublesExhaustionCap { get; set; } = 3f;

    /// <summary>A year's drift is held within these bounds.</summary>
    public float SecularFloor { get; set; } = -0.04f;
    public float SecularCeiling { get; set; } = 0.06f;
}

/// <summary>
/// The rise and fall of economies.
///
/// A country's output per head was fixed in 2024 for ever: the gap between Niger and Norway sat in
/// one calibrated multiplier (Nation.OutputScale) that nothing ever touched, every lever of growth
/// moved a level that was pulled back, and so the great powers of 2024 were the great powers of
/// any year. Now that multiplier is a stock that drifts each week, and the drift has the four
/// things that actually reorder the world over decades:
///
/// - Catch-up. A country below the frontier gains on it in proportion to how far behind it is -
///   but only if it is ready: schooled, stable and at peace. Conditional convergence is why
///   Korea and China rose and why most of the poor world did not.
/// - The pull on leaders. Above the frontier, the lead is given back slowly: the cost of being
///   first, of an old capital stock and of everybody copying you.
/// - A long wave of its own for every country, thirty-five to sixty years, its phase drawn from
///   the campaign seed: the decades of boom and of sclerosis no formula of conditions explains.
///   Two worlds from two seeds rise and stagnate in different orders.
/// - Drags that accumulate: a war is not only a bad year but a lost one, and an empire past its
///   capacity pays for it in growth.
/// - And a year's luck, from the seed.
///
/// The frontier is the output per head of the rich large economies (the 90th percentile of
/// countries over ten million people), worked out once a week. Cheap: one multiply per country.
/// </summary>
public static class SecularCycle
{
    private static readonly List<double> PerHead = new();
    private static int _frontierDay = -1;
    private static Campaign _frontierWorld;
    private static double _frontier;

    /// <summary>The frontier output per head this week, in dollars.</summary>
    public static double Frontier(Campaign campaign)
    {
        if (ReferenceEquals(_frontierWorld, campaign) && _frontierDay == campaign.ElapsedDays) return _frontier;
        PerHead.Clear();
        foreach (var nation in campaign.Nations)
            if (nation.Population >= 10_000_000 && nation.Gdp > 0f)
                PerHead.Add(nation.Gdp * 1e9 / nation.Population);
        PerHead.Sort();
        _frontier = PerHead.Count == 0 ? 0 : PerHead[Math.Min(PerHead.Count - 1, (int)(PerHead.Count * 0.9))];
        _frontierWorld = campaign;
        _frontierDay = campaign.ElapsedDays;
        return _frontier;
    }

    /// <summary>This year's drift of a country's productivity, as an annual rate.</summary>
    public static float AnnualDrift(Campaign campaign, Nation nation, bool atWar) =>
        Terms(campaign, nation, atWar, null);

    /// <summary>The drift and what it is made of, for the economy screen.</summary>
    public static Breakdown Explain(Campaign campaign, Nation nation)
    {
        var breakdown = new Breakdown();
        breakdown.Begin("Rise and decline, a year", "share");
        var atWar = false;
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
        Terms(campaign, nation, atWar, breakdown);
        return breakdown;
    }

    private static float Terms(Campaign campaign, Nation nation, bool atWar, Breakdown explain)
    {
        var defines = campaign.Defines;
        if (!defines.SecularCycleEnabled || nation.Population <= 0 || nation.Gdp <= 0f) return 0f;
        var frontier = Frontier(campaign);
        if (frontier <= 0) return 0f;
        var perHead = nation.Gdp * 1e9 / nation.Population;
        var gap = (float)Math.Log(frontier / Math.Max(1.0, perHead));

        // Ready to catch up: schooled, governed, at peace.
        var schooling = M.Clamp(nation.HumanCapital, 0.25f, 1.1f);
        var order = M.Clamp01((nation.Stability - 0.30f) / 0.45f);
        var readiness = schooling * order * (atWar ? 0.4f : 1f);
        var catchUp = gap > 0f
            ? defines.SecularConvergence * gap * readiness
            : defines.SecularLeaderDrag * gap;

        // Its own long wave.
        var shortest = MathF.Max(5f, defines.SecularWaveShortestYears);
        var longest = MathF.Max(shortest, defines.SecularWaveLongestYears);
        var period = shortest + (longest - shortest) * Escalation.Roll(campaign.Seed, nation.Id, 0x5EC, 0, 0x91);
        var phase = Escalation.Roll(campaign.Seed, nation.Id, 0x5EC, 0, 0x92);
        var years = campaign.ElapsedDays / 365.25f;
        var wave = defines.SecularWaveAmplitude * MathF.Sin(MathF.Tau * (years / period + phase));

        // A year's luck, the same all year.
        var year = campaign.ElapsedDays / 365;
        var luck = defines.SecularFortune * (Escalation.Roll(campaign.Seed, nation.Id, 0x5EC, year, 0x93) +
                                             Escalation.Roll(campaign.Seed, nation.Id, 0x5EC, year, 0x94) - 1f);

        var war = atWar ? -defines.SecularWarDrag : 0f;
        var empire = -defines.SecularOverstretchDrag * StabilityCore.Overstretch(campaign, nation);
        var drift = M.Clamp(catchUp + wave + luck + war + empire, defines.SecularFloor, defines.SecularCeiling);
        if (explain is not null)
        {
            explain.Add(gap > 0f ? "Catching up with the leading economies" : "The cost of leading", catchUp);
            explain.Add("The country's long wave", wave);
            explain.Add("This year's fortune", luck);
            if (war != 0f) explain.Add("War", war);
            if (empire != 0f) explain.Add("Imperial overstretch", empire);
            explain.Settle(drift);
        }
        return drift;
    }

    /// <summary>A week of it, into the stock that turns what a country makes into what it is worth.</summary>
    public static void Week(Campaign campaign, Nation nation, bool atWar)
    {
        // The count of a time of troubles: weeks at the bottom add up, a recovery ends it.
        var defines = campaign.Defines;
        if (nation.Stability < defines.TroublesStability) nation.TroublesDays += Math.Max(1, defines.EconomyTickDays);
        else if (nation.Stability >= defines.TroublesOverStability) nation.TroublesDays = 0;
        var drift = AnnualDrift(campaign, nation, atWar);
        nation.SecularDrift = drift;
        if (drift == 0f || nation.OutputScale <= 0f) return;
        nation.OutputScale *= 1f + drift * MathF.Max(1, campaign.Defines.EconomyTickDays) / 365f;
    }
}

public sealed partial class Nation
{
    /// <summary>
    /// Days the country has spent in a time of troubles - stability below TroublesStability -
    /// since it last recovered. Saved. The longer it lasts, the faster radicalism fades.
    /// </summary>
    public int TroublesDays;

    /// <summary>This year's rate of rise or decline of the country's productivity (SecularCycle); derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float SecularDrift;
}
