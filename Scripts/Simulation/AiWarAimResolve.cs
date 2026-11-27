using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool AiPursueUnfinishedWarAims { get; set; } = true;
    public float AiWarAimResolvePenalty { get; set; } = 70f;
    public float AiWarAimLocalAdvantage { get; set; } = 1.2f;
}

/// <summary>Do not exchange an achievable war aim for incidental gains. This is an
/// intention check, not a bonus to combat, a free annexation, or a truce bypass.</summary>
public static partial class AiStatecraft
{
    [ThreadStatic] private static HashSet<int> _warAimEnemies;
    [ThreadStatic] private static HashSet<int> _warAimStates;
    [ThreadStatic] private static HashSet<int> _warAimApproaches;
    [ThreadStatic] private static HashSet<int> _warAimLocal;

    internal static bool StrategicWarAim(Campaign c, Nation nation, StrategicWar war, ScenarioState region)
    {
        // A war of absorption values every region of the enemy, not just the one the
        // declaration named. Otherwise the front and the peace writer disagree about what
        // the war is for.
        if (!war.Limited &&
            war.Justification is ("hegemony" or "annexation" or "reunification" or "conquest" or "imperialism"))
        {
            foreach (var enemyId in war.EnemiesOf(nation.Id))
            {
                if (c.NationById(enemyId) is { } enemy && enemy.MapIndex == region.NationMapIndex)
                    return true;
            }
        }
        if (region.MapIndex == war.ClaimedStateMapIndex ||
            TerritorialClaims.TitleOf(c, nation, region) == TerritorialTitle.Homeland) return true;
        // A rising's national ground: its own people next to the ground it already holds.
        // This is a claim to press, not a free transfer; the peace still prices it.
        if (WarAims.RebelClaim(c, war, nation, region)) return true;
        // A small, authored consolidation project remains this participant's interest
        // inside a coalition war. Shared culture alone is not a world-conquest mandate.
        if (!war.Limited && ConsolidationAim(c, nation, region)) return true;
        if (!war.IsAttacking(nation.Id) || war.Justification != "irredentism" || region.Population <= 0) return false;
        return PrimaryPopulation(nation.Tag, region) > region.Population / 2;
    }

    private static long PrimaryPopulation(string tag, ScenarioState region)
    {
        var groups = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(region.PopulationGroups);
        long primary = 0;
        string lastCulture = null;
        var lastPrimary = false;
        for (var i = 0; i < groups.Length; i++)
        {
            var g = groups[i];
            if (g.Size <= 0 || g.Culture is null) continue;
            if (!ReferenceEquals(g.Culture, lastCulture))
            {
                lastCulture = g.Culture;
                lastPrimary = DemographyCatalog.IsPrimary(tag, lastCulture);
            }
            if (lastPrimary) primary += g.Size;
        }
        return primary;
    }

    private static bool ContainsTagIgnoreCase(string[] tags, string tag)
    {
        for (var i = 0; i < tags.Length; i++)
            if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool ConsolidationAim(Campaign c, Nation nation, ScenarioState region)
    {
        if (region.Population <= 0) return false;
        var owner = c.NationByMapIndex(region.NationMapIndex);
        if (owner is null || owner.Id == nation.Id) return false;
        if (PrimaryPopulation(nation.Tag, region) <= region.Population / 2) return false;
        foreach (var f in FormableNations.All)
        {
            if (f.AiFoundation && f.RequiredTags.Length is >= 2 and <= 3 &&
                ContainsTagIgnoreCase(f.RequiredTags, nation.Tag) &&
                ContainsTagIgnoreCase(f.RequiredTags, owner.Tag) &&
                FormableNations.Eligible(c, nation, f) &&
                FormableNations.CompatibleProclamation(nation, f))
                return true;
        }
        return false;
    }

    internal static bool ShouldPressWarAim(Campaign c, Nation nation, StrategicWar war, PeaceDemand draft = null)
    {
        var map = c?.ProvinceMap; var control = c?.ProvinceController;
        if (map is null || control is null || nation is null || war is null || war.Ended ||
            !c.Defines.AiPursueUnfinishedWarAims || war.Stalled || !war.Involves(nation.Id)) return false;
        var score = war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore;
        if (!float.IsFinite(score) || score < c.Defines.AiPeaceMinimumBudget ||
            !float.IsFinite(nation.Stability) || !float.IsFinite(nation.WarSupport) ||
            nation.Stability < c.Defines.AiWarStabilityFloor || nation.WarSupport < c.Defines.AiWarSupportFloor ||
            WarWeariness.TimeToStop(c, war, winning: true)) return false;
        if (draft is { Subjugate.Length: > 0 }) return false;
        if (draft is not null && draft.Provinces.Count > 0)
        {
            var duration = c.ElapsedDays - war.StartedDay;
            if (duration >= c.Defines.AiStalemateDays || war.Stalled || nation.WarSupport < 0.65f)
                return false;
        }
        var enemies = _warAimEnemies ??= new HashSet<int>();
        enemies.Clear();
        var aims = _warAimStates ??= new HashSet<int>();
        aims.Clear();
        foreach (var eid in war.EnemiesOf(nation.Id))
        {
            var enemy = c.NationById(eid);
            if (enemy is null) continue;
            enemies.Add(enemy.MapIndex);
            var enemyStates = c.StatesOf(enemy);
            for (var si = 0; si < enemyStates.Length; si++)
            {
                var st = enemyStates[si];
                if (StrategicWarAim(c, nation, war, st)) aims.Add(st.MapIndex);
            }
        }
        if (aims.Count == 0) return false;
        // Read only the local front and reconnaissance, not hidden enemy strength.
        var approaches = _warAimApproaches ??= new HashSet<int>();
        approaches.Clear();
        foreach (var post in war.FriendlyFront(nation.Id))
        {
            if (post <= 0 || post > map.ProvinceCount ||
                control[post] != nation.MapIndex) continue;
            foreach (var next in map.Neighbors[post])
                if (aims.Contains(map.ProvinceToState[next]) && enemies.Contains(map.ProvinceToNation[next]) &&
                    (draft is null || !draft.Provinces.Contains(next))) approaches.Add(post);
        }
        if (approaches.Count == 0) return false;
        var local = _warAimLocal ??= new HashSet<int>();
        local.Clear();
        foreach (var post in approaches)
        {
            local.Add(post);
            foreach (var next in map.Neighbors[post]) local.Add(next);
        }
        var ready = 0f; var resistance = 0f;
        foreach (var army in c.Armies)
        {
            if (army.ProvinceId <= 0 || !local.Contains(army.ProvinceId) || army.EmbarkedFleetId >= 0) continue;
            if (WarCoalition.SameSide(war, army.NationId, nation.Id))
            {
                if (army.AssignedWarId == war.Id && army.ReadyDay <= c.ElapsedDays &&
                    army.SupplyLevel >= .35f && army.Organisation >= .25f && army.Strength > 0 &&
                    army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead)
                    ready += army.Power;
            }
            else if (war.Opposes(nation.Id, army.NationId) && CampaignIntel.CanSeeArmy(c, nation.Id, army))
                resistance += army.Power;
        }
        var advantage = float.IsFinite(c.Defines.AiWarAimLocalAdvantage)
            ? Math.Clamp(c.Defines.AiWarAimLocalAdvantage, 1f, 5f) : 1.2f;
        return ready > 0 && ready >= resistance * advantage;
    }
}
