using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool PrestigeEnabled { get; set; } = true;
    public float PrestigeWeeklyDriftRate { get; set; } = 0.015f;
}

public sealed partial class Nation
{
    /// <summary>International standing on a 0..1000 scale, drifting toward equilibrium and shifted by diplomacy.</summary>
    public float Prestige;
}

/// <summary>
/// Organic international prestige derived from sovereignty, economy, society, knowledge,
/// alliances, deterrence and conduct, drifting weekly toward equilibrium and weighing
/// into country rankings and diplomatic willingness.
/// </summary>
public static class PrestigeSystem
{
    public const float MinPrestige = 0f;
    public const float MaxPrestige = 1000f;

    /// <summary>Allocation-free equilibrium prestige on a 0..1000 scale.</summary>
    public static float ComputeEquilibrium(Campaign c, Nation n) => ComputeEquilibrium(c, n, null, null);

    private static float ComputeEquilibrium(Campaign c, Nation n, AllianceSystem.OfferScan allianceScan,
        Dictionary<int, int> subjectsByOverlord)
    {
        if (c is null || n is null) return 0f;
        if (n.Landless)
        {
            var legitimacy = n.Movement?.Legitimacy ?? 0f;
            if (!float.IsFinite(legitimacy)) legitimacy = 0f;
            return M.Clamp(legitimacy * 40f, MinPrestige, MaxPrestige);
        }

        if (n.Population <= 0f && c.States.Count > 0 && c.StatesOf(n).Length == 0)
            return 0f;

        var subjectFactor = n.IsSubject ? 0.40f : 1.0f;
        var sovereignty = 15f * subjectFactor;

        var gdp = float.IsFinite(n.Gdp) && n.Gdp > 0f ? n.Gdp : 0f;
        var sol = float.IsFinite(n.StandardOfLiving) ? M.Clamp01(n.StandardOfLiving) : 0f;
        var lit = float.IsFinite(n.Literacy) ? M.Clamp01(n.Literacy) : 0f;
        var gdpLivingLit = MathF.Min(260f, MathF.Sqrt(gdp) * 2.4f) + sol * 45f + lit * 25f;

        var techs = n.Technologies?.Count ?? 0;
        var completedGp = GrandProjects.CountCompleted(c, n.Id);
        var techAndProjects = MathF.Min(180f, techs * 2.5f) + completedGp * 30f;

        var allies = 0;
        var subjects = 0;
        if (allianceScan is null)
        {
            var nations = c.ActiveNations;
            for (var i = 0; i < nations.Count; i++)
            {
                var other = nations[i];
                if (other is null || other.Id == n.Id || c.IsRetired(other)) continue;
                if (other.AnswersTo(n.Id)) subjects++;
                if (CampaignDiplomacy.Peek(c, n.Id, other.Id)?.Stance == DiplomaticStance.Allied) allies++;
            }
        }
        else
        {
            subjects = subjectsByOverlord.GetValueOrDefault(n.Id);
            foreach (var ally in allianceScan.Allies(c, n))
                if (!c.IsRetired(ally)) allies++;
        }
        var alliancesAndSubjects = MathF.Min(140f, allies * 7f + subjects * 14f);

        var deterrence = Deterrence.TierOf(c, n) * 20f;
        var notoriety = float.IsFinite(n.Notoriety) && n.Notoriety > 0f ? n.Notoriety : 0f;
        var stability = float.IsFinite(n.Stability) ? M.Clamp01(n.Stability) : 0f;
        var defaultPenalty = n.DebtCondition >= DebtCondition.Default ? 55f : 0f;

        var raw = sovereignty + gdpLivingLit + techAndProjects + alliancesAndSubjects + deterrence
            - notoriety * 0.90f - (1f - stability) * 30f - defaultPenalty;

        return float.IsFinite(raw) ? M.Clamp(raw, MinPrestige, MaxPrestige) : 0f;
    }

    /// <summary>Diplomatic willingness bonus granted by high prestige (0..+0.08).</summary>
    public static float DiplomaticBonus(Nation n)
    {
        if (n is null || !float.IsFinite(n.Prestige) || n.Prestige <= 0f) return 0f;
        return M.Clamp(n.Prestige / 400f, 0f, 1f) * 0.08f;
    }

    public static void Adjust(Nation n, float delta)
    {
        if (n is null || !float.IsFinite(delta)) return;
        var current = float.IsFinite(n.Prestige) ? n.Prestige : 0f;
        n.Prestige = M.Clamp(current + delta, MinPrestige, MaxPrestige);
    }

    public static void SeedInitial(Campaign c)
    {
        if (c is null || !c.Defines.PrestigeEnabled) return;
        var nations = c.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var nation = nations[i];
            if (nation is null) continue;
            if (nation.StatedFigures is not null && nation.StatedFigures.Contains("prestige"))
                nation.Prestige = M.Clamp(nation.Prestige, MinPrestige, MaxPrestige);
            else
                nation.Prestige = ComputeEquilibrium(c, nation);
        }
    }

    public static void NormalizeAfterLoad(Campaign c, bool seedLegacy = false)
    {
        if (c is null) return;
        var nations = c.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var nation = nations[i];
            if (nation is null) continue;
            var activeSeed = seedLegacy && c.Defines.PrestigeEnabled && nation.RetiredDay < 0;
            if (!float.IsFinite(nation.Prestige))
                nation.Prestige = activeSeed ? ComputeEquilibrium(c, nation) : 0f;
            else if (activeSeed && nation.Prestige == 0f)
                nation.Prestige = ComputeEquilibrium(c, nation);
            else
                nation.Prestige = M.Clamp(nation.Prestige, MinPrestige, MaxPrestige);
        }
    }

    public static void Tick(Campaign c)
    {
        if (c is null || !c.Defines.PrestigeEnabled) return;
        foreach (var _ in DriftStages(c)) { }
    }

    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !c.Defines.PrestigeEnabled || c.ElapsedDays <= 0 || c.ElapsedDays % 7 != 0)
            yield break;
        foreach (var stage in DriftStages(c)) yield return stage;
    }

    private static IEnumerable<int> DriftStages(Campaign c)
    {
        var rate = float.IsFinite(c.Defines.PrestigeWeeklyDriftRate)
            ? M.Clamp(c.Defines.PrestigeWeeklyDriftRate, 0f, 1f)
            : 0.015f;
        var nations = c.ActiveNations;
        var subjectsByOverlord = new Dictionary<int, int>();
        for (var i = 0; i < nations.Count; i++)
        {
            var subject = nations[i];
            if (subject.IsSubject && subject.OverlordId != subject.Id && !c.IsRetired(subject))
                subjectsByOverlord[subject.OverlordId] = subjectsByOverlord.GetValueOrDefault(subject.OverlordId) + 1;
        }
        var allianceScan = AiStatecraft.AllianceScanFor(c);
        for (var i = 0; i < nations.Count; i++)
        {
            var nation = nations[i];
            if (c.IsRetired(nation)) continue;
            DriftNation(nation, ComputeEquilibrium(c, nation, allianceScan, subjectsByOverlord), rate);
            if (Campaign.StageOverdue()) yield return 0;
        }
    }

    private static void DriftNation(Nation n, float equilibrium, float rate)
    {
        var cur = float.IsFinite(n.Prestige) ? M.Clamp(n.Prestige, MinPrestige, MaxPrestige) : equilibrium;
        n.Prestige = M.Clamp(cur + (equilibrium - cur) * rate, MinPrestige, MaxPrestige);
    }
}
