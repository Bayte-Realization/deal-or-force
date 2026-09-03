using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// What a country that is not being played does with its navy.
///
/// Until now: nothing. Every fleet in the world but the player's sat in its home port for
/// the whole campaign, which made the sea a decoration and made the player's own navy
/// unopposed - blockading, raiding and landing wherever it liked against an opponent who
/// would not put to sea to stop it.
///
/// The reasoning is deliberately shallow, and staggered so the cost is spread. A country at
/// peace keeps its ships at home. A country at war sends them out: to guard its own trade
/// if it is the weaker navy, to take the enemy's if it is the stronger, and to sit on the
/// enemy's coast if it can reach one. And a country at war with somebody it cannot walk to
/// loads an army and goes, because otherwise an island is a country that cannot be fought.
///
/// It is not a naval doctrine. It is enough that the sea is contested, that a blockade is
/// answered, and that an overseas war is a war rather than a stalemate by geography.
/// </summary>
public static class AiAdmiralty
{
    private static readonly List<Army> Loadable = new();

    public static void Tick(Campaign campaign)
    {
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return;

        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.Hulls <= 0) continue;
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null || nation.IsHuman) continue;

            // Each fleet thinks on its own day, so two hundred navies do not all think at
            // once. A fleet already sailing somewhere is left to get there.
            if ((campaign.ElapsedDays + fleet.Id) % campaign.Defines.AiFleetDecisionInterval != 0) continue;
            if (fleet.HasOrder) continue;

            var war = FindWar(campaign, nation);
            if (war is null)
            {
                SendHome(campaign, fleet);
                continue;
            }

            var enemyId = war.AttackerId == nation.Id ? war.DefenderId : war.AttackerId;
            var enemy = campaign.NationById(enemyId);
            if (enemy is null) { SendHome(campaign, fleet); continue; }

            if (fleet.IsLoaded)
            {
                LaunchIfPossible(campaign, fleet, nation, enemy);
                continue;
            }

            // A war that cannot be walked to has to be sailed to, and an invasion is the
            // only thing that can decide such a war - so it takes priority over blockading
            // and raiding rather than competing with them. It has to be the whole decision
            // for this fleet: sending it home to load and then, in the same breath, ordering
            // it out to blockade simply cancels the order it was just given.
            if (!CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, enemy.MapIndex))
            {
                if (fleet.Mission != FleetMission.Anchored) { SendHome(campaign, fleet); continue; }
                if (TryLoad(campaign, fleet, nation)) LaunchIfPossible(campaign, fleet, nation, enemy);
                continue;
            }

            Deploy(campaign, fleet, nation, enemy);
        }
    }

    /// <summary>
    /// The war this navy is for.
    ///
    /// A country can be in several at once, and taking whichever came first is how a fleet
    /// ends up blockading a neighbour it shares a land border with while an island it
    /// cannot otherwise reach goes unfought. A war across water is the one a navy is the
    /// only answer to, so it comes first.
    /// </summary>
    private static StrategicWar FindWar(Campaign campaign, Nation nation)
    {
        StrategicWar landward = null;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            var enemyId = war.AttackerId == nation.Id ? war.DefenderId : war.AttackerId;
            var enemy = campaign.NationById(enemyId);
            if (enemy is null) continue;
            if (!CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, enemy.MapIndex)) return war;
            landward ??= war;
        }
        return landward;
    }

    private static void SendHome(Campaign campaign, Fleet fleet)
    {
        if (fleet.Mission == FleetMission.Anchored) return;
        new SetFleetOrderCommand
        {
            IssuerId = fleet.NationId, FleetId = fleet.Id, Mission = FleetMission.Anchored
        }.Apply(campaign);
    }

    /// <summary>
    /// Put to sea, and at what. The weaker navy guards what it has; the stronger one goes
    /// after what the enemy has. Both are the obvious move, which is the point - an AI that
    /// does the obvious thing is one a player can plan against.
    /// </summary>
    private static void Deploy(Campaign campaign, Fleet fleet, Nation nation, Nation enemy)
    {
        var mine = 0f;
        var theirs = 0f;
        foreach (var other in campaign.Fleets)
        {
            if (other.NationId == nation.Id) mine += other.Presence;
            else if (other.NationId == enemy.Id) theirs += other.Presence;
        }

        var mission = mine >= theirs * campaign.Defines.AiFleetOffensiveRatio
            ? FleetMission.Blockade
            : mine >= theirs
                ? FleetMission.Raid
                : FleetMission.Escort;

        if (mission == FleetMission.Blockade)
        {
            var coast = NearestEnemyCoast(campaign, fleet, enemy);
            if (coast > 0)
            {
                var centre = campaign.ProvinceMap.CenterOf(coast);
                new SetFleetOrderCommand
                {
                    IssuerId = nation.Id, FleetId = fleet.Id, Mission = FleetMission.Blockade,
                    HasDestination = true, Latitude = centre.Latitude, Longitude = centre.Longitude
                }.Apply(campaign);
                return;
            }
            mission = FleetMission.Raid;
        }

        if (mission == FleetMission.Escort)
        {
            // Guarding one's own shipping means being where one's own shipping is, which is
            // also where the enemy's blockade will be. An escort that stays wherever it
            // happened to be guards nothing and never meets anybody.
            new SetFleetOrderCommand
            {
                IssuerId = nation.Id, FleetId = fleet.Id, Mission = FleetMission.Escort,
                HasDestination = true, Latitude = fleet.HomeLatitude, Longitude = fleet.HomeLongitude
            }.Apply(campaign);
            return;
        }

        if (fleet.Mission == mission) return;
        new SetFleetOrderCommand { IssuerId = nation.Id, FleetId = fleet.Id, Mission = mission }.Apply(campaign);
    }

    /// <summary>Take aboard whatever is standing at the fleet's own port.</summary>
    private static bool TryLoad(Campaign campaign, Fleet fleet, Nation nation)
    {
        if (fleet.Mission != FleetMission.Anchored) { SendHome(campaign, fleet); return false; }

        Loadable.Clear();
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || army.EmbarkedFleetId >= 0) continue;
            if (!NavalInvasion.CanEmbark(campaign, fleet, army, out _)) continue;
            Loadable.Add(army);
            if (Loadable.Count >= campaign.Defines.AiInvasionMaxFormations) break;
        }
        if (Loadable.Count == 0)
        {
            // Nobody at the quayside. An invasion the AI wants and never mounts because its
            // divisions are all standing on a front six hundred miles inland is not an AI
            // with an invasion; so it sends for one.
            SummonToPort(campaign, fleet, nation);
            return false;
        }

        foreach (var army in Loadable) NavalInvasion.Embark(campaign, fleet, army);
        return true;
    }

    /// <summary>
    /// March the nearest formation that is doing nothing important to the fleet's port.
    /// One at a time, and only one: an AI that empties its whole front into a harbour to
    /// mount a landing has lost the war it was already fighting.
    /// </summary>
    private static void SummonToPort(Campaign campaign, Fleet fleet, Nation nation)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null || fleet.HomeProvinceId <= 0) return;

        Army chosen = null;
        var bestDistance = float.MaxValue;
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || army.EmbarkedFleetId >= 0) continue;
            if (army.InBattle || army.ProvinceId <= 0) continue;
            // Already marching somewhere, and quite possibly here.
            if (army.Route.Count > 0) return;
            if (army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead or ArmyOrder.DefendFront) continue;
            var centre = topology.CenterOf(army.ProvinceId);
            var distance = NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude,
                centre.Latitude, centre.Longitude);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            chosen = army;
        }
        if (chosen is null) return;

        new MoveArmyCommand
        {
            IssuerId = nation.Id, ArmyId = chosen.Id, TargetProvinceId = fleet.HomeProvinceId
        }.Apply(campaign);
    }

    private static void LaunchIfPossible(Campaign campaign, Fleet fleet, Nation nation, Nation enemy)
    {
        var coast = NearestEnemyCoast(campaign, fleet, enemy);
        if (coast <= 0)
        {
            // Nowhere to put them. Better ashore at home than at sea forever.
            if (fleet.HomeProvinceId > 0) NavalInvasion.Disembark(campaign, fleet, fleet.HomeProvinceId);
            return;
        }
        new LaunchInvasionCommand { IssuerId = nation.Id, FleetId = fleet.Id, ProvinceId = coast }.Apply(campaign);
    }

    /// <summary>
    /// The nearest province the enemy holds. Nearest rather than best on purpose: an AI
    /// that picks the weakest beach in the world is an AI that sails past three continents
    /// to reach it, and this map is the whole planet.
    /// </summary>
    private static int NearestEnemyCoast(Campaign campaign, Fleet fleet, Nation enemy)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return -1;

        var best = -1;
        var bestDistance = campaign.Defines.AiInvasionSearchDegrees;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (controllers[province] != (ushort)enemy.MapIndex) continue;
            var centre = topology.CenterOf(province);
            var distance = NavalOperations.Distance(fleet.Latitude, fleet.Longitude,
                centre.Latitude, centre.Longitude);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = province;
        }
        return best;
    }
}
