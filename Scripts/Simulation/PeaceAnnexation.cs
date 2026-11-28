using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Full-annexation rules and post-treaty force cleanup for <see cref="PeaceConference"/>.
/// </summary>
internal static class PeaceAnnexation
{
    /// <summary>
    /// Checks whether an AI-vs-AI peace demand should be blocked because an enemy insurgent
    /// still fights on without ground. Human proposers and collapsed/totally-occupied wars
    /// are never blocked by this refusal.
    /// </summary>
    public static string LastGroundFieldBlock(Campaign campaign, StrategicWar war, PeaceDemand rounded,
        bool forcePeace, Nation proposer)
    {
        if (proposer is { IsHuman: true }) return "";
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        foreach (var memberId in war.EnemiesOf(rounded.ProposerId))
        {
            if (campaign.NationById(memberId) is { IsHuman: false } member &&
                Factions.FightsOnWithoutGround(campaign, member) &&
                rounded.Provinces.Any(province => topology.ProvinceToNation[province] == member.MapIndex) &&
                !Enumerable.Range(1, topology.ProvinceCount).Any(province =>
                    topology.ProvinceToNation[province] == member.MapIndex && !rounded.Provinces.Contains(province)))
            {
                if (forcePeace || war.Collapsed != 0 || PeaceNegotiation.IsTotallyOccupiedInWar(campaign, war, member))
                    continue;
                return Loc.Get("They will not sign away the last of their ground while they still have an army in the field");
            }
        }
        return "";
    }

    /// <summary>
    /// Disbands stranded armies, fleets, sea lifts, support formations, orders, and army groups
    /// of any non-movement nation that lost its last owned province in a signed annexation.
    /// </summary>
    public static void DisbandFullyAnnexedForces(Campaign campaign, PeaceDemand demand, HashSet<ushort> cededOwners)
    {
        if (campaign is null || demand is null || demand.ReleaseRegions || cededOwners is null || cededOwners.Count == 0) return;
        var cleaned = 0;
        for (var ni = 0; ni < campaign.Nations.Count; ni++)
        {
            var nation = campaign.Nations[ni];
            if (!cededOwners.Contains((ushort)nation.MapIndex)) continue;
            if (nation.Landless || nation.Movement is not null) continue;
            if (campaign.HasOwnedLand(nation)) continue;

            for (var ai = campaign.Armies.Count - 1; ai >= 0; ai--)
            {
                var army = campaign.Armies[ai];
                if (army.NationId != nation.Id) continue;
                WarLosses.Surrender(campaign, army);
                for (var fi = 0; fi < campaign.Fleets.Count; fi++)
                    campaign.Fleets[fi].Carried.Remove(army.Id);
                Attrition.Destroy(campaign, army, "destroyed: annexed in peace treaty");
                cleaned++;
            }
            cleaned += campaign.SeaLifts.RemoveAll(lift => lift.NationId == nation.Id);
            cleaned += campaign.Fleets.RemoveAll(fleet => fleet.NationId == nation.Id);
            cleaned += campaign.SupportFormations.RemoveAll(formation => formation.NationId == nation.Id);
            cleaned += campaign.SupportOrders.RemoveAll(order => order.NationId == nation.Id);
            cleaned += campaign.ArmyGroups.RemoveAll(group => group.NationId == nation.Id);
            cleaned += campaign.Recruitment.RemoveAll(order => order.NationId == nation.Id);
            if (nation.CapitalStateMapIndex != 0 || nation.CapitalProvinceId != 0 || nation.GovernmentSeatProvinceId != 0)
            {
                nation.CapitalStateMapIndex = 0;
                nation.CapitalProvinceId = 0;
                nation.GovernmentSeatProvinceId = 0;
                campaign.CapitalRevision++;
            }
        }
        if (cleaned > 0) campaign.MarkStrategicChange();
    }

    /// <summary>
    /// Offers separate peace from a totally occupied enemy ally to a human war leader while
    /// enforcing the separate-peace cooldown so declining does not re-spawn the modal daily.
    /// </summary>
    public static void OfferTotallyOccupiedSeparatePeace(Campaign c, StrategicWar war, Nation enemyLeader,
        Nation member, string cooldownKey, int cooldownDays)
    {
        if (member.DecisionAvailableDay.GetValueOrDefault(cooldownKey) > c.ElapsedDays) return;
        member.DecisionAvailableDay[cooldownKey] = c.ElapsedDays + System.Math.Max(30, cooldownDays);
        SeparatePeace.Offer(c, war, enemyLeader, member, SeparatePeaceTerms.Concede, fromMember: true);
    }
}
