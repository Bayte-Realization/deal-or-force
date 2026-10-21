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
    internal static bool StrategicWarAim(Campaign c, Nation nation, StrategicWar war, ScenarioState region)
    {
        // A war of absorption values every region of the enemy, not just the one the
        // declaration named. Otherwise the front and the peace writer disagree about what
        // the war is for.
        if (!war.Limited &&
            war.Justification is ("hegemony" or "annexation" or "reunification" or "conquest" or "imperialism") &&
            war.EnemiesOf(nation.Id).Select(c.NationById)
                .Any(enemy => enemy is not null && enemy.MapIndex == region.NationMapIndex)) return true;
        if (region.MapIndex == war.ClaimedStateMapIndex ||
            TerritorialClaims.TitleOf(c, nation, region) == TerritorialTitle.Homeland) return true;
        // A rising's national ground: its own people next to the ground it already holds.
        // This is a claim to press, not a free transfer; the peace still prices it.
        if (WarAims.RebelClaim(c, war, nation, region)) return true;
        // A small, authored consolidation project remains this participant's interest
        // inside a coalition war. Shared culture alone is not a world-conquest mandate.
        if (!war.Limited && ConsolidationAim(c,nation,region)) return true;
        if (!war.IsAttacking(nation.Id) || war.Justification != "irredentism" || region.Population <= 0) return false;
        var primary = region.PopulationGroups.Where(g => DemographyCatalog.IsPrimary(nation.Tag, g.Culture))
            .Sum(g => Math.Max(0L, g.Size));
        return primary > region.Population / 2;
    }

    internal static bool ConsolidationAim(Campaign c, Nation nation, ScenarioState region)
    {
        if(region.Population<=0) return false;
        var owner=c.Nations.Find(n=>n.MapIndex==region.NationMapIndex);
        if(owner is null || owner.Id==nation.Id) return false;
        var primary=region.PopulationGroups.Where(g=>DemographyCatalog.IsPrimary(nation.Tag,g.Culture))
            .Sum(g=>Math.Max(0L,g.Size));
        if(primary<=region.Population/2) return false;
        return FormableNations.All.Any(f=>f.AiFoundation && f.RequiredTags.Length is >=2 and <=3 &&
            f.RequiredTags.Contains(nation.Tag,StringComparer.OrdinalIgnoreCase) &&
            f.RequiredTags.Contains(owner.Tag,StringComparer.OrdinalIgnoreCase) &&
            FormableNations.Eligible(c,nation,f) && FormableNations.CompatibleProclamation(nation,f));
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
        var enemies = war.EnemiesOf(nation.Id).Select(c.NationById).Where(n => n is not null)
            .Select(n => n.MapIndex).ToHashSet();
        var aims = c.States.Where(s => enemies.Contains(s.NationMapIndex) && StrategicWarAim(c, nation, war, s))
            .Select(s => s.MapIndex).ToHashSet();
        if (aims.Count == 0) return false;
        // Read only the local front and reconnaissance, not hidden enemy strength.
        var approaches = new HashSet<int>();
        foreach (var post in war.FriendlyFront(nation.Id))
        {
            if (post <= 0 || post > map.ProvinceCount ||
                control[post] != nation.MapIndex) continue;
            foreach (var next in map.Neighbors[post])
                if (aims.Contains(map.ProvinceToState[next]) && enemies.Contains(map.ProvinceToNation[next]) &&
                    (draft is null || !draft.Provinces.Contains(next))) approaches.Add(post);
        }
        if (approaches.Count == 0) return false;
        var local = new HashSet<int>(approaches);
        foreach (var post in approaches) foreach (var next in map.Neighbors[post]) local.Add(next);
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

