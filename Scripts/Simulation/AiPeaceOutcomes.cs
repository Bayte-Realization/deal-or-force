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
        // A state facing its own breakaway, or a civil-war contender claiming the state, writes
        // the whole country back into one once the war is decided.
        var reclaiming = Reintegration.ClaimsStateInCivilWar(campaign, war, winner, loser);
        // A war between a government and its rebels, or between two factions of one civil
        // war, may end with either side taking the state. The seat is the strongest claim,
        // not the only one: a side that has overrun the country has won it.
        var civilWar = war.CivilConflict || AnnexationAmbition.IsCivilWarBetween(winner, loser);
        // A war declared to absorb a small neighbour is not priced like a border correction
        // once the winner plainly holds the country. These are the words Warmaking writes.
        var annexationWar = war.Justification is "hegemony" or "annexation" or "conquest" or "imperialism";
        var rebelContender = !reclaiming && WarAims.IsRebel(winner);
        var seatTaken = false;
        if (rebelContender)
        {
            var incumbent = campaign.NationById(war.EnemyLeaderOf(winner.Id));
            seatTaken = incumbent is not null && incumbent.CapitalProvinceId > 0 &&
                        campaign.ProvinceController[incumbent.CapitalProvinceId] == winner.MapIndex;
        }
        // A war of reunification — shared people, kindred homeland across the border, or an
        // authored national unification — ends, when won decisively, with the union itself.
        var reunification = Reintegration.IsReunificationTarget(campaign, war, winner, loser) ||
            AnnexationAmbition.IsReunification(campaign, winner, loser) ||
            (war.Justification == "reconquest" && TerritorialClaims.HoldsOurHomeland(campaign, winner, loser));
        var score = war.IsAttacking(winner.Id) ? war.WarScore : -war.WarScore;
        var territory = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => map.ProvinceToNation[p] == loser.MapIndex && !map.IsWater(p)).ToArray();
        if (territory.Length == 0) return false;
        var held = territory.Count(p => campaign.ProvinceController[p] == winner.MapIndex);
        var heldShare = held / (float)territory.Length;
        var secured = territory.Count(p => campaign.ProvinceController[p] == winner.MapIndex &&
            campaign.ProvinceOccupation[p] >= campaign.Defines.OccupationSecureThreshold);
        var securedShare = secured / (float)territory.Length;
        // Either side of a civil war may take the state once it has plainly won it. The
        // capital is the cleanest proof, but a government that has lost most of its ground
        // and its army has lost the country whether or not the last office still stands.
        // Ground is its own proof: a side holding two-thirds of the country has settled the
        // question whatever the war-score ledger says. The old AND let a government that
        // had reconquered all but one region sign a peace that left that region the war's
        // whole point - a rump state neither side could use and nobody wanted.
        var civilDecisive = civilWar && !reclaiming &&
            (SideOverrun(campaign, war, war.IsAttacking(loser.Id)) ||
             heldShare >= .65f ||
             (score >= campaign.Defines.AiCivilAnnexationScore &&
              securedShare >= campaign.Defines.AiCivilAnnexationOccupationShare));
        var annexationDecisive = annexationWar &&
            score >= campaign.Defines.AiAnnexationDecisiveScore &&
            securedShare >= campaign.Defines.AiAnnexationOccupationShare;
        var absorbing = reclaiming || seatTaken || reunification || civilDecisive;
        if (!absorbing && !PeaceShape.Near(campaign, winner, loser)) return false;
        if (absorbing)
        {
            var scoreFloor = reunification
                ? campaign.Defines.AiReunificationDecisiveScore
                : Math.Min(campaign.Defines.ReclaimDecisiveScore, campaign.Defines.AiCivilAnnexationScore);
            var occupationFloor = reunification
                ? campaign.Defines.AiReunificationOccupationShare
                : Math.Min(campaign.Defines.ReclaimSecuredShare, campaign.Defines.AiCivilAnnexationOccupationShare);
            var rebelBroken = reclaiming && (campaign.Armies.Count(a => a.NationId == loser.Id && a.Strength > 0.05f) == 0 ||
                                             heldShare >= 0.75f || (heldShare >= 0.50f && score >= 40f));
            if (!rebelBroken && score < scoreFloor &&
                securedShare < occupationFloor &&
                heldShare < 0.65f &&
                !SideOverrun(campaign, war, war.IsAttacking(loser.Id))) return false;
            foreach (var province in territory) demand.Provinces.Add(province);
            // The war is decided: what is left of the government capitulates with the rest.
            // A civil war or a reunification is not a border bargain, and a war score that
            // could not buy every province must not leave the loser standing as a rump.
            demand.Unconditional = true;
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
        var fullConquest = Reintegration.WantsFullConquestAnnexation(campaign, war, winner, loser, score, securedShare, heldShare) ||
            annexationDecisive;
        if (!fullConquest && (score < campaign.Defines.AiPeaceDecisiveScore || securedShare < campaign.Defines.AiPeaceDecisiveOccupationShare))
            return false;

        // The full treaty may spend the full earned score; the ordinary safety margin
        // must not make an affordable victory impossible. No free territory is created:
        // the ground is already held or the enemy is overrun.
        foreach (var province in territory) demand.Provinces.Add(province);
        if (fullConquest)
        {
            demand.Unconditional = true;
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
        if (ledger.Cost(demand) <= ledger.Budget)
        {
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
        demand.Provinces.Clear();

        // The whole country does not fit at list price. Ground the army is already standing
        // on is not given back so a cheaper client can be written instead: that peace is
        // the one WriteDemand draws from the occupation. A client remains the alternative
        // only when the victory is not standing on their land.
        if (territory.Any(p => campaign.ProvinceController[p] == (ushort)winner.MapIndex))
            return false;

        // A war begun to restore a union ends by restoring one. The personal-union subject
        // kind is dormant for ordinary peaces (imposable: false in data) and wakes only for
        // the war whose stated reason was the dynastic claim.
        if (war.Justification == "dynastic_claim" && SubjectCatalog.Knows("personal_union") &&
            PeaceConference.SubjugationCost(campaign, winner, loser, "personal_union") <= ledger.Budget)
        {
            demand.Subjugate = "personal_union";
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }

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
