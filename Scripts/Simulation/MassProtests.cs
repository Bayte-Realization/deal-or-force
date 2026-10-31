using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Off until calibrated: the first bench filled the streets of 181 countries in a year.</summary>
    public bool ProtestsEnabled { get; set; } = false;
    /// <summary>Grievance from a cut in welfare and education against last year's level: a 10% cut is 0.1 times this.</summary>
    public float ProtestCutWeight { get; set; } = 3f;
    /// <summary>Military spending rising while the cut is made: the budget read as guns for butter.</summary>
    public float ProtestGunsWeight { get; set; } = 0.5f;
    public float ProtestInflationFloor { get; set; } = 0.04f;
    public float ProtestInflationWeight { get; set; } = 4f;
    public float ProtestStabilityFloor { get; set; } = 0.45f;
    public float ProtestStabilityWeight { get; set; } = 0.8f;
    public float ProtestRadicalismWeight { get; set; } = 0.4f;
    /// <summary>The strongest neighbouring wave, carried over the border at this share.</summary>
    public float ProtestContagion { get; set; } = 0.35f;
    /// <summary>Grievance below which nobody goes out.</summary>
    public float ProtestThreshold { get; set; } = 0.30f;
    public float ProtestGrievancePerDay { get; set; } = 0.01f;
    public float ProtestRisePerDay { get; set; } = 0.03f;
    public float ProtestFallPerDay { get; set; } = 0.015f;
    /// <summary>A government that does not answer to voters keeps its crowds this much smaller - and angrier.</summary>
    public float ProtestRepressedTurnout { get; set; } = 0.6f;
    public float ProtestModifierAt { get; set; } = 0.25f;
    public float ProtestUnrestAt { get; set; } = 0.60f;
    /// <summary>A government the computer runs gives back this share of the cut, once a month, while the streets are full.</summary>
    public float ProtestConcession { get; set; } = 0.5f;
    public float ProtestConcessionAt { get; set; } = 0.45f;
}

public sealed partial class DomesticCrisisState
{
    public float ProtestGrievance;
    public float ProtestIntensity;
    /// <summary>Welfare and education spending a year back, smoothed; -1 until first read.</summary>
    public float SpendingMemory = -1f;
    public float MilitaryMemory = -1f;
    public int LastProtestNewsDay = -1;
    public int LastConcessionDay = -1;
}

/// <summary>
/// The street, in any country.
///
/// A rich democracy was the one place nothing happened: its stability had no lever but war and
/// coups, so France could cut forty billion from its social budget, double its army and keep a
/// quiet capital. Autumn 2026 said otherwise - school strikes in France, Germany and Austria,
/// housing protests in Spain ending in a snap election, a wave from Lisbon to Belgrade.
/// Nothing here names a country. A cut in welfare and schools against last year's budget,
/// arms rising while it is made, prices, a shaky state, radical regions and a wave next door
/// make the grievance; an educated, urban people turns it into crowds; a government that does
/// not answer to voters keeps the crowds smaller. Crowds cost stability and output through two
/// modifiers in the data (mass_protests, mass_unrest). A government the computer runs gives back
/// part of the cut; a player has the same budget sliders.
/// </summary>
public static class MassProtests
{
    public static void Review(Campaign c, Nation nation, DomesticCrisisSnapshot snapshot, int days)
    {
        var d = c.Defines;
        if (!d.ProtestsEnabled || nation.Movement is not null || nation.Population <= 0 || days <= 0) return;
        var memory = nation.DomesticCrisis;
        var spending = nation.WelfareSpending + nation.EducationSpending;
        if (memory.SpendingMemory < 0f) memory.SpendingMemory = spending;
        if (memory.MilitaryMemory < 0f) memory.MilitaryMemory = nation.MilitarySpending;

        var cut = MathF.Max(0f, memory.SpendingMemory - spending) / MathF.Max(0.01f, memory.SpendingMemory);
        var guns = cut > 0f
            ? MathF.Min(0.2f, MathF.Max(0f, nation.MilitarySpending - memory.MilitaryMemory) /
                              MathF.Max(0.005f, memory.MilitaryMemory) * d.ProtestGunsWeight)
            : 0f;
        var neighbours = 0f;
        foreach (var mapIndex in CampaignDiplomacy.Neighbours(c, nation.MapIndex))
            if (c.NationByMapIndex(mapIndex) is { } next && next.DomesticCrisis is { } theirs)
                neighbours = MathF.Max(neighbours, theirs.ProtestIntensity);
        var own = cut * d.ProtestCutWeight + guns +
                  MathF.Max(0f, nation.Inflation - d.ProtestInflationFloor) * d.ProtestInflationWeight +
                  MathF.Max(0f, d.ProtestStabilityFloor - nation.Stability) * d.ProtestStabilityWeight +
                  snapshot.Radicalism * d.ProtestRadicalismWeight;
        // A wave next door carries over only what people here are already angry about: added
        // without that, two neighbours kept each other's streets full and 162 countries marched
        // in the first year.
        var target = M.Clamp01(own + neighbours * d.ProtestContagion *
                               M.Clamp01(own / MathF.Max(0.05f, d.ProtestThreshold)));
        memory.ProtestGrievance = Move(memory.ProtestGrievance, target, d.ProtestGrievancePerDay * days);

        var openness = ParliamentaryPolitics.Eligible(nation) ? 1f : d.ProtestRepressedTurnout;
        var turnout = (0.35f + 0.65f * snapshot.Awareness) * openness;
        var threshold = M.Clamp(d.ProtestThreshold, 0f, 0.95f);
        var wanted = M.Clamp01((memory.ProtestGrievance - threshold) / MathF.Max(0.05f, 0.8f - threshold)) * turnout;
        var before = memory.ProtestIntensity;
        memory.ProtestIntensity = before < wanted
            ? MathF.Min(wanted, before + d.ProtestRisePerDay * days)
            : MathF.Max(wanted, before - d.ProtestFallPerDay * days);

        // Last year's budget is what people measure a cut against; it follows the budget slowly.
        var year = MathF.Min(1f, days / 365f);
        memory.SpendingMemory += (spending - memory.SpendingMemory) * year;
        memory.MilitaryMemory += (nation.MilitarySpending - memory.MilitaryMemory) * year;

        var intensity = memory.ProtestIntensity;
        if (intensity >= d.ProtestUnrestAt) ModifierSystem.Grant(c, nation, "mass_unrest", 14);
        else if (intensity >= d.ProtestModifierAt) ModifierSystem.Grant(c, nation, "mass_protests", 14);
        if (before < d.ProtestModifierAt && intensity >= d.ProtestModifierAt &&
            c.ElapsedDays - memory.LastProtestNewsDay >= 180)
        {
            memory.LastProtestNewsDay = c.ElapsedDays;
            c.Chronicle.Record(c, "mass_protests", nation.Id);
            DiagnosticLogs.Write("simulation", $"PROTEST {nation.Tag} grievance={memory.ProtestGrievance:0.00} " +
                $"cut={cut:0.00} inflation={nation.Inflation:0.00} stability={nation.Stability:0.00} day={c.ElapsedDays}");
        }

        // The answer a cabinet gives most often: part of the money back.
        if (intensity >= d.ProtestConcessionAt && cut > 0f && c.ElapsedDays - memory.LastConcessionDay >= 30 &&
            (!(nation.IsHuman || nation.IsPlayer) || Delegation.Holds(nation, Delegation.Budget)))
        {
            memory.LastConcessionDay = c.ElapsedDays;
            var back = (memory.SpendingMemory - spending) * d.ProtestConcession;
            var share = spending > 0f ? nation.WelfareSpending / spending : 1f;
            nation.WelfareSpending += back * share;
            nation.EducationSpending += back * (1f - share);
            Count("protest_concession");
        }
    }

    public static readonly System.Collections.Generic.Dictionary<string, int> Tally = new();
    private static void Count(string key) => Tally[key] = Tally.TryGetValue(key, out var n) ? n + 1 : 1;

    private static float Move(float value, float target, float amount) => value < target
        ? MathF.Min(target, value + amount) : MathF.Max(target, value - amount);
}
