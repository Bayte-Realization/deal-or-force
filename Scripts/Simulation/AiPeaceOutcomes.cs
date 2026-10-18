using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float AiPeaceDecisiveScore { get; set; } = 75f;
    public float AiPeaceDecisiveOccupationShare { get; set; } = .65f;
}

public static partial class AiStatecraft
{
    /// <summary>Buy a decade of quiet only when it serves the settlement, not out of habit.</summary>
    internal static bool WantsLongTruce(Campaign campaign, Nation nation, StrategicWar war, PeaceDemand demand)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || nation is null || war is null || demand is null) return true;
        // Exhaustion can outweigh an unfinished claim. The ordinary truce still applies
        // in every other case; declining the paid extension never permits an immediate war.
        if (nation.Stability < campaign.Defines.AiWarStabilityFloor ||
            nation.WarSupport < campaign.Defines.AiWarSupportFloor) return true;
        var enemies = war.EnemiesOf(nation.Id).Select(campaign.NationById).Where(n => n is not null)
            .Select(n => n.MapIndex).ToHashSet();
        var subject = demand.Subjugate.Length > 0 ? campaign.NationById(war.EnemyLeaderOf(nation.Id))?.MapIndex ?? 0 : 0;
        var unresolved = campaign.States.Where(s => enemies.Contains(s.NationMapIndex) && s.NationMapIndex != subject &&
            StrategicWarAim(campaign, nation, war, s))
            .Select(s => s.MapIndex).ToHashSet();
        if (unresolved.Count == 0) return true;
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (!map.IsWater(p) && enemies.Contains(map.ProvinceToNation[p]) && map.ProvinceToNation[p] != subject &&
                unresolved.Contains(map.ProvinceToState[p]) && !demand.Provinces.Contains(p)) return false;
        return true;
    }

    /// <summary>Compare a complete settlement before spending its budget on a partial border and extras.</summary>
    internal static bool WriteDecisiveSettlement(Campaign campaign, Nation winner, StrategicWar war,
        PeaceLedger ledger, PeaceDemand demand)
    {
        var map = campaign.ProvinceMap;
        var loser = campaign.NationById(war.EnemyLeaderOf(winner.Id));
        if (map is null || campaign.ProvinceController is null || campaign.ProvinceOccupation is null ||
            loser is null || loser.IsSubject) return false;
        // A state facing its own breakaway is taking back its own ground: it writes the whole of
        // it back once the war is decided, and does not settle for a dependency instead.
        var reclaiming = Reintegration.IsBreakawayOf(loser, winner);
        if (!reclaiming && !PeaceShape.Near(campaign, winner, loser)) return false;
        var score = war.IsAttacking(winner.Id) ? war.WarScore : -war.WarScore;
        var territory = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => map.ProvinceToNation[p] == loser.MapIndex && !map.IsWater(p)).ToArray();
        if (territory.Length == 0) return false;
        var secured = territory.Count(p => campaign.ProvinceController[p] == winner.MapIndex &&
            campaign.ProvinceOccupation[p] >= campaign.Defines.OccupationSecureThreshold);
        var securedShare = secured / (float)territory.Length;
        if (reclaiming)
        {
            if (score < campaign.Defines.ReclaimDecisiveScore && securedShare < campaign.Defines.ReclaimSecuredShare) return false;
            foreach (var province in territory) demand.Provinces.Add(province);
            if (ledger.Cost(demand) <= ledger.Budget)
            {
                WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
                return true;
            }
            // Not all of it yet: what it holds, and the war goes on for the rest another day.
            demand.Provinces.Clear();
            return false;
        }
        if (score < campaign.Defines.AiPeaceDecisiveScore) return false;
        if (securedShare < campaign.Defines.AiPeaceDecisiveOccupationShare) return false;

        // The full treaty may spend the full earned score; the ordinary safety margin
        // must not make an affordable victory impossible. No free territory is created.
        foreach (var province in territory) demand.Provinces.Add(province);
        if (ledger.Cost(demand) <= ledger.Budget)
        {
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
        demand.Provinces.Clear();

        // Annexation can be too expensive. Leaving a dependent state intact is a genuine
        // alternative, not a last-minute extra after reparations consumed its price.
        var kind = SubjectCatalog.Imposable
            .Where(k => PeaceConference.SubjugationCost(campaign, winner, loser, k.Id) <= ledger.Budget)
            .OrderByDescending(k => k.DemandCost).ThenBy(k => k.Id, StringComparer.Ordinal).FirstOrDefault();
        if (kind is null) return false;
        demand.Subjugate = kind.Id;
        WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
        return true;
    }
}
