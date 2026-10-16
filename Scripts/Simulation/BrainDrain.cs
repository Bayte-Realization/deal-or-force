using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool BrainDrainEnabled { get; set; } = true;
    public float BrainDrainMaxWeeklyShare { get; set; } = 0.00032f;
    public float BrainDrainStabilityThreshold { get; set; } = 0.48f;
    public float BrainDrainLivingThreshold { get; set; } = 0.52f;
    public float BrainDrainLiteracyWeight { get; set; } = 0.25f;
}

public sealed partial class ScenarioState
{
    /// <summary>Net people gained (+) or lost (−) through migration in the latest weekly review.</summary>
    public long ReviewNetMigration;

    /// <summary>Current push pressure on educated specialists and households to leave this region (0..1).</summary>
    public float BrainDrainPressure;
}

public sealed partial class Nation
{
    /// <summary>Net population change from cross-border migration in the latest weekly review.</summary>
    public long WeeklyNetMigration;

    /// <summary>Cumulative net migration since the campaign began.</summary>
    public long CumulativeNetMigration;

    /// <summary>Signed human-capital flow (−1..+1): negative means brain drain, positive means talent inflow.</summary>
    public float BrainDrainRate;

    /// <summary>Primary country receiving emigrants from this nation, or -1 when none.</summary>
    public int BrainDrainDestinationId = -1;

    /// <summary>Primary country sending skilled migrants to this nation, or -1 when none.</summary>
    public int BrainGainSourceId = -1;
}

/// <summary>
/// Demographic migration and brain drain: when war, instability, shortages or lack of opportunity
/// strike an educated region, skilled households and specialists emigrate to peaceful, prosperous
/// partner countries. Every person who leaves one region arrives in another — global population is
/// conserved to the exact person, and human capital (literacy and productivity) shifts with them.
/// </summary>
public static class BrainDrain
{
    public static float CalculatePressure(Campaign c, Nation n, ScenarioState s, bool atWar)
    {
        if (c is null || n is null || s is null || s.Unclaimed || s.Population <= 0) return 0f;
        var d = c.Defines;
        var damage = M.Clamp01(s.WarDamage);
        var stress = M.Clamp01(s.LivelihoodStress);
        var lowStability = MathF.Max(0f, d.BrainDrainStabilityThreshold - s.Stability);
        var radicalism = MathF.Max(0f, s.Radicalism - 0.30f);
        var unemployment = MathF.Max(0f, 0.88f - s.EmploymentTarget);

        var push = (atWar ? 0.18f : 0f)
                   + damage * 0.45f
                   + stress * 0.30f
                   + lowStability * 0.65f
                   + radicalism * 0.32f
                   + unemployment * 0.35f;

        var universities = s.Buildings.GetValueOrDefault(BuildingKind.University);
        var labs = s.Buildings.GetValueOrDefault(BuildingKind.ResearchLab);
        var highLiving = MathF.Max(0f, s.StandardOfLiving - d.BrainDrainLivingThreshold);
        var retentionSpirit = ModifierSystem.Has(n, "talent_retention") ? 0.22f : 0f;
        var educationFocus = n.GovernmentFocus == GovernmentFocus.SocialCohesion ? 0.05f : 0f;

        var anchor = highLiving * 0.55f
                     + MathF.Min(0.24f, universities * 0.04f + labs * 0.06f)
                     + retentionSpirit
                     + educationFocus;

        var net = MathF.Max(0f, push - anchor);
        // Educated specialists have the means and qualifications to relocate fastest.
        var mobility = 0.40f + 0.60f * M.Clamp01(s.Literacy);
        return M.Clamp01(net * mobility);
    }

    public static Nation BestDestination(Campaign c, Nation source, HashSet<int> atWarNations)
    {
        if (c is null || source is null) return null;
        Nation best = null;
        var bestScore = 0.12f; // Minimum pull threshold required to attract cross-border migrants.

        for (var i = 0; i < c.Nations.Count; i++)
        {
            var candidate = c.Nations[i];
            if (candidate.Id == source.Id || candidate.Movement is not null || candidate.Population <= 0) continue;
            if (atWarNations.Contains(candidate.Id)) continue;
            if (candidate.Stability < 0.52f || candidate.StandardOfLiving <= source.StandardOfLiving + 0.05f) continue;

            var states = c.StatesOf(candidate);
            if (states.Length == 0) continue;

            var rel = CampaignDiplomacy.Peek(c, source.Id, candidate.Id);
            if (rel is not null && (rel.EmbargoByA || rel.EmbargoByB || rel.Stance == DiplomaticStance.Rival)) continue;

            var livingGap = candidate.StandardOfLiving - source.StandardOfLiving;
            var stabilityBonus = (candidate.Stability - 0.50f) * 0.55f;
            var tradeLink = source.TradePartners.GetValueOrDefault(candidate.Id) * 0.85f;
            var sameContinent = string.Equals(candidate.Continent, source.Continent, StringComparison.OrdinalIgnoreCase) ? 0.18f : 0f;
            var friendly = rel is not null && rel.Opinion > 15f ? 0.12f : 0f;
            var productivityBonus = M.Clamp(candidate.Productivity - 1f, -0.2f, 0.5f) * 0.25f;

            var score = livingGap * 1.35f + stabilityBonus + tradeLink + sameContinent + friendly + productivityBonus;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    public static ScenarioState ReceptionState(Campaign c, Nation destination)
    {
        var states = c.StatesOf(destination);
        if (states.Length == 0) return null;
        ScenarioState best = null;
        var bestScore = float.MinValue;
        for (var i = 0; i < states.Length; i++)
        {
            var s = states[i];
            if (s.Unclaimed || s.Population <= 0) continue;
            var isCap = s.MapIndex == destination.CapitalStateMapIndex ? 0.35f : 0f;
            var research = s.Buildings.GetValueOrDefault(BuildingKind.University) * 0.05f +
                           s.Buildings.GetValueOrDefault(BuildingKind.ResearchLab) * 0.08f;
            var score = s.StandardOfLiving + isCap + research - s.WarDamage;
            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }
        return best ?? states[0];
    }

    public static void Tick(Campaign c)
    {
        if (c is null || !c.Defines.BrainDrainEnabled || !c.Defines.HouseholdDynamicsEnabled) return;
        if (c.ElapsedDays <= 0 || c.ElapsedDays % Math.Max(1, c.Defines.EconomyTickDays) != 0) return;
        if (c.Nations.Count < 2 || c.States.Count < 2) return;

        var atWarNations = new HashSet<int>();
        for (var i = 0; i < c.Wars.Count; i++)
        {
            var w = c.Wars[i];
            if (w.Ended) continue;
            atWarNations.Add(w.AttackerId);
            atWarNations.Add(w.DefenderId);
        }

        for (var i = 0; i < c.States.Count; i++)
        {
            c.States[i].ReviewNetMigration = 0;
            c.States[i].BrainDrainPressure = 0f;
        }

        var topSourceForDest = new Dictionary<int, (int SourceId, long Moved)>();
        for (var i = 0; i < c.Nations.Count; i++)
        {
            var n = c.Nations[i];
            n.WeeklyNetMigration = 0;
            n.BrainDrainRate = 0f;
            n.BrainDrainDestinationId = -1;
            n.BrainGainSourceId = -1;
        }

        var maxWeeklyShare = M.Clamp(c.Defines.BrainDrainMaxWeeklyShare, 0f, 0.005f);
        var literacyWeight = M.Clamp(c.Defines.BrainDrainLiteracyWeight, 0f, 1f);

        for (var i = 0; i < c.Nations.Count; i++)
        {
            var sourceNation = c.Nations[i];
            if (sourceNation.Movement is not null || sourceNation.Population <= 10_000) continue;
            var states = c.StatesOf(sourceNation);
            if (states.Length == 0) continue;

            var atWar = atWarNations.Contains(sourceNation.Id);
            var maxPressure = 0f;
            for (var sIdx = 0; sIdx < states.Length; sIdx++)
            {
                var st = states[sIdx];
                st.BrainDrainPressure = CalculatePressure(c, sourceNation, st, atWar);
                if (st.BrainDrainPressure > maxPressure) maxPressure = st.BrainDrainPressure;
            }

            if (maxPressure < 0.08f) continue;

            var destNation = BestDestination(c, sourceNation, atWarNations);
            if (destNation is null) continue;
            var destState = ReceptionState(c, destNation);
            if (destState is null) continue;

            long totalOutflow = 0;
            double weightedPressure = 0;
            for (var sIdx = 0; sIdx < states.Length; sIdx++)
            {
                var st = states[sIdx];
                weightedPressure += st.BrainDrainPressure * st.Population;
                if (st.BrainDrainPressure < 0.08f || st.Population <= 5_000) continue;

                var moved = (long)Math.Round(st.Population * (double)st.BrainDrainPressure * maxWeeklyShare);
                moved = Math.Clamp(moved, 0L, st.Population / 50L);
                if (moved <= 0) continue;

                EconomySystem.AdjustStatePopulation(st, -moved);
                EconomySystem.AdjustStatePopulation(destState, moved);
                st.ReviewNetMigration -= moved;
                destState.ReviewNetMigration += moved;
                totalOutflow += moved;

                var stateShare = (float)(moved / (double)Math.Max(1L, st.Population));
                st.Literacy = M.Clamp(st.Literacy - stateShare * literacyWeight * st.Literacy, 0.05f, 0.999f);
                destState.Literacy = M.Clamp(destState.Literacy + stateShare * literacyWeight * 0.5f * st.Literacy, 0.05f, 0.999f);
            }

            if (totalOutflow <= 0) continue;

            sourceNation.Population = Math.Max(1L, states.Sum(s => s.Population));
            destNation.Population = Math.Max(1L, c.StatesOf(destNation).Sum(s => s.Population));

            sourceNation.WeeklyNetMigration -= totalOutflow;
            sourceNation.CumulativeNetMigration -= totalOutflow;
            destNation.WeeklyNetMigration += totalOutflow;
            destNation.CumulativeNetMigration += totalOutflow;

            sourceNation.BrainDrainDestinationId = destNation.Id;
            if (!topSourceForDest.TryGetValue(destNation.Id, out var prev) || totalOutflow > prev.Moved)
                topSourceForDest[destNation.Id] = (sourceNation.Id, totalOutflow);

            var avgPressure = (float)(weightedPressure / Math.Max(1.0, sourceNation.Population));
            sourceNation.BrainDrainRate = -M.Clamp01(avgPressure);

            var nationOutflowShare = (float)(totalOutflow / (double)Math.Max(1L, sourceNation.Population));
            sourceNation.Literacy = M.Clamp(sourceNation.Literacy - nationOutflowShare * literacyWeight * sourceNation.Literacy, 0.05f, 0.999f);
            sourceNation.Productivity = M.Clamp(sourceNation.Productivity * (1f - nationOutflowShare * literacyWeight * 0.4f), 0.15f, 4f);

            var destInflowShare = (float)(totalOutflow / (double)Math.Max(1L, destNation.Population));
            destNation.Literacy = M.Clamp(destNation.Literacy + destInflowShare * literacyWeight * 0.6f * sourceNation.Literacy, 0.05f, 0.999f);
            destNation.Productivity = M.Clamp(destNation.Productivity * (1f + destInflowShare * literacyWeight * 0.3f), 0.15f, 4f);
        }

        foreach (var (destId, (sourceId, moved)) in topSourceForDest)
        {
            var dest = c.NationById(destId);
            if (dest is null) continue;
            dest.BrainGainSourceId = sourceId;
            if (dest.WeeklyNetMigration > 0)
            {
                var gainRatio = (float)(dest.WeeklyNetMigration / (double)Math.Max(1L, dest.Population)) / MathF.Max(0.00001f, maxWeeklyShare);
                dest.BrainDrainRate = M.Clamp(gainRatio * 0.35f, 0.01f, 1f);
            }
        }
    }
}
