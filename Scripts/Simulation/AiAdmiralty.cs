using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Stores below which a fleet the AI commands turns for home to refill.</summary>
    public float AiFleetReturnSupply { get; set; } = 0.3f;

    /// <summary>Strength below which it turns for home to be repaired.</summary>
    public float AiFleetReturnStrength { get; set; } = 0.45f;

    /// <summary>Cohesion below which it turns for home to recover.</summary>
    public float AiFleetReturnOrganisation { get; set; } = 0.3f;

    /// <summary>What a fleet lying in port has to be back to before the AI sends it out again.</summary>
    public float AiFleetSailSupply { get; set; } = 0.85f;

    public float AiFleetSailOrganisation { get; set; } = 0.8f;

    public float AiFleetSailStrength { get; set; } = 0.6f;

    /// <summary>
    /// How much farther than the nearest enemy beach the AI will sail for a more thinly held
    /// one, as a multiple of the nearest one's distance.
    /// </summary>
    public float AiInvasionBeachSpread { get; set; } = 1.3f;

    /// <summary>
    /// The enemy's navy, against one's own, above which the AI will not put an army to sea.
    /// A convoy sent through a stronger fleet is a convoy sent to the bottom.
    /// </summary>
    public float AiInvasionNavalOdds { get; set; } = 1.5f;
}

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
/// enemy's coast if it can reach one. A fleet that has spent its stores or taken a beating
/// comes home before it is empty rather than after, and goes out again once it is fit. And
/// a country at war with somebody it cannot walk to loads an army and goes - to the beach
/// that is held most thinly among the near ones, and only when its navy is not the weaker
/// by a distance - because otherwise an island is a country that cannot be fought.
///
/// It is not a naval doctrine. It is enough that the sea is contested, that a blockade is
/// answered, and that an overseas war is a war rather than a stalemate by geography.
/// </summary>
public static class AiAdmiralty
{
    private static readonly List<Army> Loadable = new();
    private static readonly Dictionary<int, float> Defenders = new();
    private static readonly List<(int Province, float Distance)> Beaches = new();

    public static void Tick(Campaign campaign)
    {
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return;
        // Each fleet thinks on its own day (below), so this pass has nothing to do on a step
        // that is not the start of one.
        if (!campaign.StepOpensNewDay) return;
        ForgetGround();

        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.Hulls <= 0) continue;
            var nation = campaign.NationById(fleet.NationId);
            // A player's navy is the player's - unless they have handed it to the admiralty,
            // which then runs it exactly as it runs everybody else's.
            if (nation is null || (nation.IsHuman && !Delegation.Holds(nation, Delegation.Navy))) continue;

            // Each fleet thinks on its own day, so two hundred navies do not all think at
            // once.
            if ((campaign.ElapsedDays + fleet.Id) % campaign.Defines.AiFleetDecisionInterval != 0) continue;

            // Home to refill before the stores run out, not after - asked even of a fleet on
            // its way somewhere, because the way somewhere is where they run out.
            if (!fleet.IsLoaded && fleet.Mission != FleetMission.Anchored && Spent(campaign, fleet))
            {
                NavalOperations.SendHome(campaign, fleet);
                continue;
            }

            // A fleet already sailing somewhere is left to get there, and one being repaired
            // is left to be repaired.
            if (fleet.HasOrder) continue;
            if (fleet.InPort && !fleet.IsLoaded && !Fit(campaign, fleet)) continue;

            var war = FindWar(campaign, nation);
            if (war is null)
            {
                SendHome(campaign, fleet);
                continue;
            }

            var enemyId = war.EnemyLeaderOf(nation.Id);
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
            //
            // Unless the enemy's navy is so much the stronger that a convoy would never
            // arrive, in which case the fleet fights for the sea first.
            if (!WalkedTo(campaign, nation, enemy, war) &&
                KnownNavy(campaign, enemy, nation) <= OwnNavy(campaign, nation) * campaign.Defines.AiInvasionNavalOdds)
            {
                if (fleet.Mission != FleetMission.Anchored) { SendHome(campaign, fleet); continue; }
                if (TryLoad(campaign, fleet, nation)) LaunchIfPossible(campaign, fleet, nation, enemy);
                continue;
            }

            Deploy(campaign, fleet, nation, enemy);
        }
    }

    private static bool Spent(Campaign campaign, Fleet fleet)
    {
        var defines = campaign.Defines;
        return fleet.Supply < defines.AiFleetReturnSupply ||
               fleet.Strength < defines.AiFleetReturnStrength ||
               fleet.Organisation < defines.AiFleetReturnOrganisation;
    }

    private static bool Fit(Campaign campaign, Fleet fleet)
    {
        var defines = campaign.Defines;
        return fleet.Supply >= defines.AiFleetSailSupply &&
               fleet.Organisation >= defines.AiFleetSailOrganisation &&
               fleet.Strength >= defines.AiFleetSailStrength;
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
            var enemyId = war.EnemyLeaderOf(nation.Id);
            var enemy = campaign.NationById(enemyId);
            if (enemy is null) continue;
            if (!WalkedTo(campaign, nation, enemy, war)) return war;
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

    private static float OwnNavy(Campaign campaign, Nation nation)
    {
        var mine = 0f;
        foreach (var other in campaign.Fleets)
            if (other.NationId == nation.Id) mine += other.Presence;
        return mine;
    }

    /// <summary>
    /// The navy on the other side: the enemy's and that of everyone fighting beside it.
    ///
    /// Counted from the fleets themselves. It used to be guessed from how many regions the
    /// enemy held, which made a landlocked giant a naval power and an island nation with a
    /// fleet of two hundred a nuisance; navies are the one arm of a country everyone can
    /// count from a reference book.
    /// </summary>
    private static float KnownNavy(Campaign campaign, Nation enemy, Nation nation)
    {
        var theirs = 0f;
        foreach (var other in campaign.Fleets)
        {
            if (other.Hulls <= 0) continue;
            if (other.NationId == enemy.Id ||
                (other.NationId != nation.Id && CampaignDiplomacy.AtWar(campaign, other.NationId, nation.Id)))
                theirs += other.Presence;
        }
        return theirs;
    }

    /// <summary>
    /// Put to sea, and at what. The weaker navy guards what it has; the stronger one goes
    /// after what the enemy has. Both are the obvious move, which is the point - an AI that
    /// does the obvious thing is one a player can plan against.
    /// </summary>
    private static void Deploy(Campaign campaign, Fleet fleet, Nation nation, Nation enemy)
    {
        var mine = OwnNavy(campaign, nation);
        var theirs = KnownNavy(campaign, enemy, nation);

        var mission = mine >= theirs * campaign.Defines.AiFleetOffensiveRatio
            ? FleetMission.Blockade
            : mine >= theirs
                ? FleetMission.Raid
                : FleetMission.Escort;

        // Both offensive missions are fought off the enemy's coast: a blockade sits on it,
        // and a raider hunts the shipping that runs to it. A fleet that cannot reach any of
        // it guards its own instead.
        if (mission is FleetMission.Blockade or FleetMission.Raid)
        {
            foreach (var coast in EnemyCoasts(campaign, fleet, enemy, 3))
            {
                if (!SeaLanes.TryHarbourPoint(campaign.ProvinceMap, coast, out var latitude, out var longitude)) continue;
                if (!NavalOperations.SendTo(campaign, fleet, latitude, longitude)) continue;
                fleet.Mission = mission;
                fleet.TargetProvinceId = -1;
                return;
            }
            mission = FleetMission.Escort;
        }

        // Guarding one's own shipping means being where one's own shipping is, which is
        // also where the enemy's blockade will be. An escort that stays wherever it
        // happened to be guards nothing and never meets anybody.
        if (NavalOperations.Distance(fleet.Latitude, fleet.Longitude, fleet.HomeLatitude, fleet.HomeLongitude) >
            campaign.Defines.FleetBerthRangeDegrees &&
            NavalOperations.SendTo(campaign, fleet, fleet.HomeLatitude, fleet.HomeLongitude))
        {
            fleet.Mission = FleetMission.Escort;
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
            if (army.TemplateId == MilitiaCore.TemplateId || army.FrontLineId >= 0) continue;
            // Only an existing march to THIS port fulfils the request. An unrelated
            // column must not prevent every other reserve from boarding.
            if (army.Route.Count > 0)
            {
                if (army.OrderProvinceId == fleet.HomeProvinceId) return;
                continue;
            }
            if (army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead or ArmyOrder.DefendFront)
            {
                // A nominal land role in an overseas war with no own sector is not
                // a reason to leave the only landing troops inland forever.
                //
                // Nor is a sector the division cannot walk to. Holding one post of the front
                // anywhere used to count for the whole army, so a country whose front is
                // across the sea from most of it summoned nobody.
                if (WalksToFront(campaign, nation, army.AssignedWarId).Contains(army.ProvinceId)) continue;
            }
            var centre = topology.CenterOf(army.ProvinceId);
            var distance = NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude,
                centre.Latitude, centre.Longitude);
            if (distance >= bestDistance) continue;
            if (army.ProvinceId != fleet.HomeProvinceId && topology.FindRoute(army.ProvinceId,
                    fleet.HomeProvinceId, (ushort)nation.MapIndex, campaign.ProvinceController).Length == 0) continue;
            bestDistance = distance;
            chosen = army;
        }
        if (chosen is null) return;

        new MoveArmyCommand
        {
            IssuerId = nation.Id, ArmyId = chosen.Id, TargetProvinceId = fleet.HomeProvinceId
        }.Apply(campaign);
    }

    /// <summary>
    /// Whether this war is fought on foot: whether the country's capital can walk to its own
    /// posts on the front. Sharing a border with the enemy somewhere is not the same thing -
    /// America borders Cuba at Guantanamo, and the rest of its army cannot walk there.
    /// A capital that is not in its own hands says nothing, and nor does a war whose fronts have
    /// not been drawn yet - its first day - so then the border is asked.
    /// </summary>
    private static bool WalkedTo(Campaign campaign, Nation nation, Nation enemy, StrategicWar war)
    {
        var capital = nation.CapitalProvinceId;
        if (capital <= 0 || capital >= campaign.ProvinceController.Length ||
            campaign.ProvinceController[capital] != nation.MapIndex ||
            (war.AttackerFront.Count == 0 && war.DefenderFront.Count == 0))
            return CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, enemy.MapIndex);
        return WalksToFront(campaign, nation, war.Id).Contains(capital);
    }

    // Kept for one pass of the admiralty (Tick clears it): the ground does not change while it
    // thinks, and it does between one pass and the next. The sets are handed back rather than
    // dropped - a country's ground is thousands of provinces, every day.
    private static readonly Dictionary<(int Nation, int War), HashSet<int>> Walkable = new();
    private static readonly Stack<HashSet<int>> SpareGround = new();
    private static readonly Queue<int> Flood = new();

    private static void ForgetGround()
    {
        foreach (var reached in Walkable.Values)
        {
            reached.Clear();
            SpareGround.Push(reached);
        }
        Walkable.Clear();
    }

    /// <summary>
    /// The ground from which a country's divisions can walk to its own posts on one war's front:
    /// flooded out from the posts over what it holds, which is the ground its routes may cross.
    /// Once per country and war per pass, rather than a route search for every division asked.
    /// </summary>
    private static HashSet<int> WalksToFront(Campaign campaign, Nation nation, int warId)
    {
        if (Walkable.TryGetValue((nation.Id, warId), out var reached)) return reached;
        Walkable[(nation.Id, warId)] = reached = SpareGround.Count > 0 ? SpareGround.Pop() : new HashSet<int>();
        var topology = campaign.ProvinceMap;
        var control = campaign.ProvinceController;
        StrategicWar war = null;
        foreach (var candidate in campaign.Wars)
            if (!candidate.Ended && candidate.Id == warId) { war = candidate; break; }
        if (war is null || topology is null || control is null) return reached;
        var own = (ushort)nation.MapIndex;
        Flood.Clear();
        foreach (var post in war.FriendlyFront(nation.Id))
            if (control[post] == own && reached.Add(post)) Flood.Enqueue(post);
        while (Flood.Count > 0)
        {
            var province = Flood.Dequeue();
            foreach (var next in topology.Neighbors[province])
                if (control[next] == own && reached.Add(next)) Flood.Enqueue(next);
        }
        return reached;
    }

    private static void LaunchIfPossible(Campaign campaign, Fleet fleet, Nation nation, Nation enemy)
    {
        var beach = BestBeach(campaign, fleet, enemy);
        if (beach > 0)
        {
            var launch = new LaunchInvasionCommand { IssuerId = nation.Id, FleetId = fleet.Id, ProvinceId = beach };
            launch.Apply(campaign);
            if (launch.Refusal.Length == 0) return;
        }

        // Nowhere to put them. Better ashore at home than at sea forever - and ashore where
        // the fleet is, which is home only when the fleet is.
        if (fleet.InPort) NavalInvasion.Disembark(campaign, fleet, fleet.BerthProvinceId);
        else NavalOperations.SendHome(campaign, fleet);
    }

    /// <summary>
    /// The enemy's coasts the fleet can reach, nearest first. Nearest rather than best on
    /// purpose: an AI that picks the weakest beach in the world is an AI that sails past
    /// three continents to reach it, and this map is the whole planet.
    /// </summary>
    private static IEnumerable<int> EnemyCoasts(Campaign campaign, Fleet fleet, Nation enemy, int count)
    {
        CollectBeaches(campaign, fleet, enemy);
        for (var index = 0; index < Beaches.Count && index < count; index++) yield return Beaches[index].Province;
    }

    private static void CollectBeaches(Campaign campaign, Fleet fleet, Nation enemy)
    {
        Beaches.Clear();
        var topology = campaign.ProvinceMap;
        if (topology is null) return;
        var basin = SeaLanes.BasinAt(topology, fleet.Latitude, fleet.Longitude);
        var search = campaign.Defines.AiInvasionSearchDegrees;
        foreach (var province in SeaLanes.CoastalProvinces(topology))
        {
            if (campaign.ProvinceController[province] != (ushort)enemy.MapIndex) continue;
            if (basin >= 0 && SeaLanes.HarbourBasin(topology, province) != basin) continue;
            if (!SeaLanes.TryHarbourPoint(topology, province, out var latitude, out var longitude)) continue;
            var distance = NavalOperations.Distance(fleet.Latitude, fleet.Longitude, latitude, longitude);
            if (distance >= search) continue;
            Beaches.Add((province, distance));
        }
        Beaches.Sort((left, right) => left.Distance.CompareTo(right.Distance));
    }

    /// <summary>
    /// Where to land: among the beaches not much farther than the nearest, the one the enemy
    /// holds most thinly - counting what stands on it and half of what stands next to it,
    /// because a beachhead is attacked from inland the day after it is taken.
    /// </summary>
    private static int BestBeach(Campaign campaign, Fleet fleet, Nation enemy)
    {
        CollectBeaches(campaign, fleet, enemy);
        if (Beaches.Count == 0) return -1;

        Defenders.Clear();
        foreach (var army in campaign.Armies)
        {
            if (army.ProvinceId <= 0 || army.EmbarkedFleetId >= 0) continue;
            if (army.NationId != enemy.Id && !CampaignDiplomacy.AtWar(campaign, army.NationId, fleet.NationId)) continue;
            Defenders[army.ProvinceId] = Defenders.GetValueOrDefault(army.ProvinceId) + army.Divisions * army.Strength;
        }

        var topology = campaign.ProvinceMap;
        var reach = Beaches[0].Distance * campaign.Defines.AiInvasionBeachSpread + 3f;
        var best = -1;
        var bestDefence = float.MaxValue;
        var bestDistance = float.MaxValue;
        foreach (var (province, distance) in Beaches)
        {
            if (distance > reach) break;
            var defence = Defenders.GetValueOrDefault(province);
            foreach (var next in topology.Neighbors[province]) defence += Defenders.GetValueOrDefault(next) * 0.5f;
            if (defence > bestDefence + 0.001f) continue;
            if (MathF.Abs(defence - bestDefence) <= 0.001f && distance >= bestDistance) continue;
            bestDefence = defence;
            bestDistance = distance;
            best = province;
        }
        return best;
    }
}
