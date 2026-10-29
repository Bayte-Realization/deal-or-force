using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float AiPeaceDecisiveScore { get; set; } = 75f;
    public float AiPeaceDecisiveOccupationShare { get; set; } = .65f;

    /// <summary>
    /// How long a war score may sit at a decisive level over ground the winner holds before
    /// the score itself is the verdict. Measured wars (WAR_CALIBRATION_2026_10_26) ran for
    /// years at a score of a hundred without the occupation map ever converting to "secure",
    /// and a finished war that never ends is not a war any more: it is a bug with casualties.
    /// </summary>
    public int AiPeaceDecisivePatienceDays { get; set; } = 540;

    /// <summary>
    /// The score at which a war the aggressor is plainly losing becomes the defender's
    /// victory: liberation of its own ground and reparations, never annexation. The
    /// attacker's own decisive bar stays where it is; this is the defender's, and it is
    /// lower because a repelled invasion is a verdict the whole world has already watched.
    /// </summary>
    public float AiPeaceRepelledScore { get; set; } = 35f;
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

        // The Cuba rule: one holder of the deterrent does not conquer another. The wall's
        // door has no handle on this side. A war between holders still ends - on limited
        // terms, and sooner the higher the shadow, because the world (and each side's own
        // cities) is doing the pushing: the decisive bar comes down by the brink, and the
        // settlement is liberation and money, never the aggressor's homeland on the map.
        var brink = global::DealOrForce.Simulation.Deterrence.Brinkmanship(campaign, war);
        if (!absorbing && brink > 0f)
        {
            var bar = MathF.Max(campaign.Defines.AiPeaceRepelledScore,
                campaign.Defines.AiPeaceDecisiveScore - brink * 10f);
            if (score < bar) return false;
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (map.ProvinceToNation[province] == winner.MapIndex && !map.IsWater(province) &&
                    campaign.ProvinceController[province] == loser.MapIndex)
                    demand.Provinces.Add(province);
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }

        // A repelled aggression is its own victory, and it is not a conquest. Measured
        // (WAR_CALIBRATION_2026_10_26): defenders led wars by as much as 47 points and
        // still took nothing, because the only decisive verdict was the attacker's
        // annexation. When the side that was attacked has plainly won on points, its
        // settlement is liberation and money: every province of its own still under the
        // aggressor's boot (ground already held, priced as nothing to retake), reparations
        // from the verdict's budget, and no claim on the aggressor's homeland at all.
        // The absorbing cases above keep their own, harsher roads.
        if (!absorbing && !civilWar && !war.IsAttacking(winner.Id) &&
            score >= campaign.Defines.AiPeaceRepelledScore &&
            campaign.ElapsedDays - war.StartedDay >= campaign.Defines.AiPeaceDecisivePatienceDays / 2)
        {
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (map.ProvinceToNation[province] == winner.MapIndex && !map.IsWater(province) &&
                    campaign.ProvinceController[province] == loser.MapIndex)
                    demand.Provinces.Add(province);
            // The verdict's money half is written by the terms from the ledger's budget;
            // the demand here carries only the ground.
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
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
        // A verdict that has stood for a season of the war is its own proof of conquest. The
        // civil branch already says ground is its own proof; an interstate war that has held
        // two-thirds of a country at a score of ninety for the patience of the world does not
        // need the occupation paperwork to finish converting before the loser capitulates.
        // Measured before this gate: wars of 618 and 730 days at a score of 100, unresolved.
        var longDecisive = score >= MathF.Min(90f, campaign.Defines.AiPeaceDecisiveScore + 15f) &&
                           campaign.ElapsedDays - war.StartedDay >= campaign.Defines.AiPeaceDecisivePatienceDays &&
                           heldShare >= .65f;
        var fullConquest = Reintegration.WantsFullConquestAnnexation(campaign, war, winner, loser, score, securedShare, heldShare) ||
            annexationDecisive || longDecisive;
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
