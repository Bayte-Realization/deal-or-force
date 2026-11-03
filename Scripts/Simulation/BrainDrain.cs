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
    /// <summary>
    /// The most a country takes in across its borders in a week, as a share of its own people.
    /// 0.0001 is about one in two hundred a year - a busy immigration country; the Gulf states at
    /// their busiest took twice that. Without it every Chinese region under pressure sent its
    /// emigrants to the one most attractive region of one country, and an independent Texas was
    /// 38% Han by 2046.
    /// </summary>
    public float BrainDrainMaxIntakeWeeklyShare { get; set; } = 0.0001f;
    /// <summary>How many of a destination's best regions share its arrivals.</summary>
    public int BrainDrainReceptionRegions { get; set; } = 4;
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
    public static float CalculatePressure(Campaign c, Nation n, ScenarioState s, bool atWar, Breakdown explain = null)
    {
        if (c is null || n is null || s is null || s.Unclaimed || s.Population <= 0) return 0f;
        var d = c.Defines;
        var damage = M.Clamp01(s.WarDamage);
        var stress = M.Clamp01(s.LivelihoodStress);
        var lowStability = MathF.Max(0f, d.BrainDrainStabilityThreshold - s.Stability);
        var radicalism = MathF.Max(0f, s.Radicalism - 0.30f);
        var unemployment = MathF.Max(0f, 0.88f - s.EmploymentTarget);
        var politicalAlienation = s.PoliticalDivergence >= 0.45f && s.GovernmentApproval >= 0f && s.GovernmentApproval < 0.40f
            ? (s.PoliticalDivergence - 0.40f) * 0.22f
            : 0f;
        var identityAlienation = StateIdentity.RegionAlienation(c, n, s) * 0.18f;
        var inflationPush = n.Inflation > 0.12f ? M.Clamp(n.Inflation - 0.12f, 0f, 0.40f) * 0.25f : 0f;
        var youthBulgePush = s.StandardOfLiving < 0.42f && s.Literacy < 0.72f && stress > 0.15f ? 0.12f : 0f;

        var foodSatisfaction = n.Market.TryGetValue(Commodity.Food, out var foodQ) ? Math.Clamp(foodQ.Satisfaction, 0f, 1f) : 1f;
        var refugeeSurge = damage > 0.10f ? (damage - 0.10f) * 0.55f : 0f;
        var faminePush = foodSatisfaction < 0.45f ? (0.45f - foodSatisfaction) * 0.65f : 0f;

        var warPush = atWar ? 0.18f : 0f;
        var damagePush = damage * 0.45f;
        var stressPush = stress * 0.30f;
        var stabilityPush = lowStability * 0.65f;
        var radicalismPush = radicalism * 0.32f;
        var jobPush = unemployment * 0.35f;

        var push = warPush + damagePush + stressPush + stabilityPush + radicalismPush + jobPush +
                   politicalAlienation + identityAlienation + inflationPush + youthBulgePush +
                   refugeeSurge + faminePush;

        var universities = s.Buildings.GetValueOrDefault(BuildingKind.University);
        var labs = s.Buildings.GetValueOrDefault(BuildingKind.ResearchLab);
        var highLiving = MathF.Max(0f, s.StandardOfLiving - d.BrainDrainLivingThreshold);
        var retentionSpirit = ModifierSystem.Has(n, "talent_retention") ? 0.22f : 0f;
        var borderControls = ModifierSystem.Has(n, "strict_border_controls") ? 0.18f : 0f;
        var educationFocus = n.GovernmentFocus == GovernmentFocus.SocialCohesion ? 0.05f : 0f;

        var livingAnchor = highLiving * 0.55f;
        var campusAnchor = MathF.Min(0.24f, universities * 0.04f + labs * 0.06f);
        var anchor = livingAnchor + campusAnchor + retentionSpirit + borderControls + educationFocus;

        var net = MathF.Max(0f, push - anchor);
        // Educated specialists relocate fastest, but war devastation displaces refugees regardless of education.
        var baseMobility = 0.40f + 0.60f * M.Clamp01(s.Literacy) + (damage > 0.10f ? 0.35f : 0f);
        var mobility = baseMobility * (ModifierSystem.Has(n, "strict_border_controls") ? 0.55f : 1f);
        var result = M.Clamp01(net * mobility);

        if (explain is not null)
        {
            explain.Begin(Loc.Get("Emigration & brain-drain pressure"), "share");
            if (warPush > 0f) explain.Add(Loc.Get("Active war"), warPush * mobility);
            if (damagePush > 0.0005f) explain.Add(Loc.Get("Local war damage"), damagePush * mobility);
            if (refugeeSurge > 0.0005f) explain.Add(Loc.Get("War refugee displacement"), refugeeSurge * mobility);
            if (faminePush > 0.0005f) explain.Add(Loc.Get("Famine & starvation flight"), faminePush * mobility);
            if (stressPush > 0.0005f) explain.Add(Loc.Get("Household livelihood stress"), stressPush * mobility);
            if (stabilityPush > 0.0005f) explain.Add(Loc.Get("Low regional stability"), stabilityPush * mobility);
            if (radicalismPush > 0.0005f) explain.Add(Loc.Get("Social radicalism"), radicalismPush * mobility);
            if (jobPush > 0.0005f) explain.Add(Loc.Get("Unemployment"), jobPush * mobility);
            if (politicalAlienation > 0.0005f) explain.Add(Loc.Get("Political cohort rejection"), politicalAlienation * mobility);
            if (identityAlienation > 0.0005f) explain.Add(Loc.Get("State identity alienation"), identityAlienation * mobility);
            if (inflationPush > 0.0005f) explain.Add(Loc.Get("Runaway inflation"), inflationPush * mobility);
            if (youthBulgePush > 0.0005f) explain.Add(Loc.Get("Demographic overcrowding pressure"), youthBulgePush * mobility);
            if (livingAnchor > 0.0005f) explain.Add(Loc.Get("High standard of living (retention)"), -livingAnchor * mobility);
            if (campusAnchor > 0.0005f) explain.Add(Loc.Get("Universities & research laboratories"), -campusAnchor * mobility);
            if (retentionSpirit > 0.0005f) explain.Add(Loc.Get("Talent retention programme"), -retentionSpirit * mobility);
            if (borderControls > 0.0005f) explain.Add(Loc.Get("Strict border controls"), -borderControls * mobility);
            if (educationFocus > 0.0005f) explain.Add(Loc.Get("Social cohesion focus"), -educationFocus * mobility);
            explain.Settle(result, Loc.Get("Mobility & bounds"));
        }

        return result;
    }

    public static Nation BestDestination(Campaign c, Nation source, HashSet<int> atWarNations)
    {
        if (c is null || source is null) return null;
        Nation best = null;
        var bestScore = 0.12f; // Minimum pull threshold required to attract cross-border migrants.
        var sourcePrimary = DemographyCatalog.PrimaryCulture(source.Tag);

        for (var i = 0; i < c.Nations.Count; i++)
        {
            var candidate = c.Nations[i];
            if (candidate.Id == source.Id || candidate.Movement is not null || candidate.Population <= 0) continue;
            if (atWarNations.Contains(candidate.Id)) continue;
            if (ModifierSystem.Has(candidate, "strict_border_controls")) continue;
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
            var kinship = StateIdentity.ShareDeclaredFaith(source, candidate) ||
                          (!string.IsNullOrEmpty(sourcePrimary) && DemographyCatalog.IsPrimary(candidate.Tag, sourcePrimary))
                ? 0.16f : 0f;
            var openDoors = ModifierSystem.Has(candidate, "open_migration_doors") ? 0.35f : 0f;
            // An aging destination experiencing workforce contraction exerts higher demand and pull for workers.
            var laborShortagePull = MathF.Max(0f, 0.62f - candidate.WorkingShare) * 1.5f;

            var score = livingGap * 1.35f + stabilityBonus + tradeLink + sameContinent + friendly + productivityBonus + kinship + openDoors + laborShortagePull;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// The regions arrivals settle in, the most attractive first: cities, universities and the
    /// capital pull, but people go to many places, not one.
    /// </summary>
    public static List<ScenarioState> ReceptionStates(Campaign c, Nation destination, int count)
    {
        var states = c.StatesOf(destination);
        return states.Where(s => !s.Unclaimed && s.Population > 0)
            .OrderByDescending(s => Attraction(destination, s)).ThenBy(s => s.MapIndex)
            .Take(Math.Max(1, count)).ToList();
    }

    private static float Attraction(Nation destination, ScenarioState s)
    {
        var isCap = s.MapIndex == destination.CapitalStateMapIndex ? 0.35f : 0f;
        var research = s.Buildings.GetValueOrDefault(BuildingKind.University) * 0.05f +
                       s.Buildings.GetValueOrDefault(BuildingKind.ResearchLab) * 0.08f;
        return s.StandardOfLiving + isCap + research - s.WarDamage;
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
        foreach (var _ in Stages(c)) { }
    }

    /// <summary>How many source countries one stage works through before the frame gets its turn back.</summary>
    private const int StageCountries = 48;

    /// <summary>
    /// The week's emigration in stages: the same countries in the same order, a pause every
    /// StageCountries - one indivisible piece of twenty-five milliseconds before.
    /// </summary>
    public static IEnumerable<int> Stages(Campaign c)
    {
        if (c is null || !c.Defines.BrainDrainEnabled || !c.Defines.HouseholdDynamicsEnabled) yield break;
        if (c.ElapsedDays <= 0 || c.ElapsedDays % Math.Max(1, c.Defines.EconomyTickDays) != 0) yield break;
        if (c.Nations.Count < 2 || c.States.Count < 2) yield break;

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
        var intakeRoom = new Dictionary<int, long>();
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
            if (i > 0 && i % StageCountries == 0) yield return 0;
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
            var receptions = ReceptionStates(c, destNation, c.Defines.BrainDrainReceptionRegions);
            if (receptions.Count == 0) continue;
            // A country takes in only so many a week, whoever they come from.
            if (!intakeRoom.TryGetValue(destNation.Id, out var room))
                room = (long)(destNation.Population * (double)M.Clamp(c.Defines.BrainDrainMaxIntakeWeeklyShare, 0f, 0.002f));
            var arrivals = 0;

            long totalOutflow = 0;
            double weightedPressure = 0;
            for (var sIdx = 0; sIdx < states.Length; sIdx++)
            {
                var st = states[sIdx];
                weightedPressure += st.BrainDrainPressure * st.Population;
                if (st.BrainDrainPressure < 0.08f || st.Population <= 5_000) continue;

                var doorMultiplier = ModifierSystem.Has(destNation, "open_migration_doors") ? 1.65d : 1.0d;
                var moved = (long)Math.Round(st.Population * (double)st.BrainDrainPressure * maxWeeklyShare * doorMultiplier);
                moved = Math.Clamp(moved, 0L, Math.Min(st.Population / 50L, room));
                if (moved <= 0) continue;
                var destState = receptions[arrivals++ % receptions.Count];

                // The people themselves cross: who they are arrives with them.
                moved = InternalMigration.Move(st, destState, moved, withinCountry: false);
                if (moved <= 0) continue;
                room -= moved;
                st.ReviewNetMigration -= moved;
                destState.ReviewNetMigration += moved;
                totalOutflow += moved;

                var stateShare = (float)(moved / (double)Math.Max(1L, st.Population));
                st.Literacy = M.Clamp(st.Literacy - stateShare * literacyWeight * st.Literacy, 0.05f, 0.999f);
                destState.Literacy = M.Clamp(destState.Literacy + stateShare * literacyWeight * 0.5f * st.Literacy, 0.05f, 0.999f);
            }

            intakeRoom[destNation.Id] = room;
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
