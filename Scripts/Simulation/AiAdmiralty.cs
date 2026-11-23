using System;
using System.Collections.Generic;
using System.Linq;

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
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same daily admiralty pass, yielded between batches of fleets.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(AiAdmiralty))) yield break;
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) yield break;
        // Each fleet thinks on its own day (below), so this pass has nothing to do on a step
        // that is not the start of one.
        if (!campaign.StepOpensNewDay) yield break;
        ForgetGround();
        if (campaign.ElapsedDays % 7 == 0) Concentrate(campaign);
        yield return 0;

        var ticked = 0;
        foreach (var fleet in campaign.Fleets)
        {
            if (++ticked % 16 == 0 || Campaign.StageOverdue()) yield return 0;
            if (fleet.Hulls <= 0) continue;
            var nation = campaign.NationById(fleet.NationId);
            // A player's navy is the player's - unless they have handed it to the admiralty,
            // which then runs it exactly as it runs everybody else's.
            if (nation is null || (nation.IsHuman && !Delegation.Holds(nation, Delegation.Navy))) continue;
            // A fleet carrying out a sea lift has its orders already.
            if (SeaLift.Holds(campaign, fleet)) continue;

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
                LaunchIfPossible(campaign, fleet, nation, enemy, war);
                continue;
            }

            // A war that cannot be walked to has to be sailed to, and an invasion is the
            // only thing that can decide such a war - so it takes priority over blockading
            // and raiding rather than competing with them. It has to be the whole decision
            // for this fleet: sending it home to load and then, in the same breath, ordering
            // it out to blockade simply cancels the order it was just given.
            //
            // Co-belligerent navies fighting on our side share the sea-control burden, and a
            // narrow strait crossing (e.g. Taiwan Strait) weighs local active enemy fleets at
            // sea rather than anchored squadrons a thousand kilometres away in another theatre.
            if (!WalkedTo(campaign, nation, enemy, war) && CanAttemptInvasion(campaign, fleet, nation, enemy, war))
            {
                if (fleet.Mission != FleetMission.Anchored) { SendHome(campaign, fleet); continue; }
                if (TryLoad(campaign, fleet, nation)) LaunchIfPossible(campaign, fleet, nation, enemy, war);
                continue;
            }

            Deploy(campaign, fleet, nation, enemy, war);
        }
    }

    /// <summary>
    /// At war, squadrons lying in the same port sail as one. Fifteen small squadrons each
    /// sent out on its own were fifteen chances to be beaten in detail; an admiralty
    /// concentrates before it gives battle (FleetGroups). Once a week, ports only, never a
    /// convoy or a fleet under orders.
    /// </summary>
    private static void Concentrate(Campaign campaign)
    {
        var groups = new Dictionary<(int Nation, int Berth), List<Fleet>>();
        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.Hulls <= 0 || !fleet.InPort || fleet.IsLoaded || fleet.HasOrder || fleet.HullsOrdered > 0 ||
                SeaLift.Holds(campaign, fleet)) continue;
            var key = (fleet.NationId, fleet.BerthProvinceId);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Fleet>();
            list.Add(fleet);
        }
        foreach (var ((nationId, _), fleets) in groups)
        {
            if (fleets.Count < 2) continue;
            var nation = campaign.NationById(nationId);
            if (nation is null || (nation.IsHuman && !Delegation.Holds(nation, Delegation.Navy))) continue;
            if (FindWar(campaign, nation) is null) continue;
            var into = fleets.OrderByDescending(fleet => fleet.Hulls).ThenBy(fleet => fleet.Id).First();
            FleetGroups.Merge(campaign, into, fleets);
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
    /// Prioritize wars where this country is a primary leader (attacker or defender) over
    /// distant junior coalition wars (so an island nation fighting its neighbour never sends
    /// its fleet across the ocean to Cuba), and prioritize overseas wars that cannot be walked
    /// to over landward wars, breaking ties by closest enemy capital.
    /// </summary>
    private static StrategicWar FindWar(Campaign campaign, Nation nation)
    {
        StrategicWar best = null;
        var bestTier = -1;
        var bestDistance = float.MaxValue;
        foreach (var war in Warfare.OpenWars(campaign))
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            var enemyId = war.EnemyLeaderOf(nation.Id);
            var enemy = campaign.NationById(enemyId);
            if (enemy is null) continue;

            var isLeader = war.AttackerId == nation.Id || war.DefenderId == nation.Id;
            var overseas = !WalkedTo(campaign, nation, enemy, war);
            var tier = (isLeader ? 2 : 0) + (overseas ? 1 : 0);
            var distance = NavalOperations.Distance(nation.CapitalLatitude, nation.CapitalLongitude,
                enemy.CapitalLatitude, enemy.CapitalLongitude);
            if (tier > bestTier || (tier == bestTier && distance < bestDistance))
            {
                bestTier = tier;
                bestDistance = distance;
                best = war;
            }
        }
        return best;
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

    /// <summary>Our navy plus co-belligerent allied fleets fighting on our side of this war.</summary>
    private static float AlliedNavy(Campaign campaign, Nation nation, StrategicWar war)
    {
        var friendly = 0f;
        foreach (var other in campaign.Fleets)
        {
            if (other.Hulls <= 0) continue;
            if (other.NationId == nation.Id ||
                (war is not null && WarCoalition.SameSide(war, other.NationId, nation.Id)))
                friendly += other.Presence;
        }
        return friendly;
    }

    /// <summary>
    /// Whether this fleet's side has enough sea control (globally with co-belligerents, or locally
    /// across a narrow strait) to embark and sail an amphibious assault or bridgehead reinforcement.
    /// </summary>
    private static bool CanAttemptInvasion(Campaign campaign, Fleet fleet, Nation nation, Nation enemy, StrategicWar war)
    {
        var allied = AlliedNavy(campaign, nation, war);
        var enemyTotal = KnownNavy(campaign, enemy, nation);
        if (enemyTotal <= allied * campaign.Defines.AiInvasionNavalOdds) return true;

        var narrowRange = MathF.Max(1f, campaign.Defines.AiNarrowStraitInvasionDegrees);
        CollectBeaches(campaign, fleet, enemy);
        // Measure narrow-strait crossing distance from the fleet's HOME port (where troops embark),
        // never from a distant patrol position off an enemy coast across an ocean.
        var nearestEnemy = float.MaxValue;
        for (var i = 0; i < Beaches.Count; i++)
        {
            if (SeaLanes.TryHarbourPoint(campaign.ProvinceMap, Beaches[i].Province, out var eLat, out var eLon))
            {
                var d = NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude, eLat, eLon);
                if (d < nearestEnemy) nearestEnemy = d;
            }
        }
        var bridgehead = FriendlyBridgeheadPort(campaign, fleet, nation, war);
        var nearestFriendly = float.MaxValue;
        if (bridgehead > 0 && SeaLanes.TryHarbourPoint(campaign.ProvinceMap, bridgehead, out var bLat, out var bLon))
            nearestFriendly = NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude, bLat, bLon);
        if (MathF.Min(nearestEnemy, nearestFriendly) > narrowRange) return false;

        // Across a narrow strait, only enemy fleets actually at sea near the crossing block fast runs.
        var localSeaThreat = 0f;
        foreach (var other in campaign.Fleets)
        {
            if (other.Hulls <= 0 || other.InPort) continue;
            if (other.NationId != enemy.Id && !CampaignDiplomacy.AtWar(campaign, other.NationId, nation.Id)) continue;
            if (NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude, other.Latitude, other.Longitude) <= narrowRange + 4f)
                localSeaThreat += other.Presence;
        }
        return localSeaThreat <= allied * MathF.Max(campaign.Defines.AiInvasionNavalOdds, campaign.Defines.AiNarrowStraitInvasionOdds);
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
    private static void Deploy(Campaign campaign, Fleet fleet, Nation nation, Nation enemy, StrategicWar war = null)
    {
        var mine = AlliedNavy(campaign, nation, war);
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

    /// <summary>Take aboard whatever is standing at the fleet's own port, marines first.</summary>
    private static bool TryLoad(Campaign campaign, Fleet fleet, Nation nation)
    {
        if (fleet.Mission != FleetMission.Anchored) { SendHome(campaign, fleet); return false; }

        Loadable.Clear();
        var capacity = NavalInvasion.Capacity(campaign, fleet);
        var limit = Math.Clamp(Math.Max(campaign.Defines.AiInvasionMaxFormations, (int)MathF.Floor(capacity)), 1, 6);
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || army.EmbarkedFleetId >= 0) continue;
            if (army.TemplateId == MilitiaCore.TemplateId) continue;
            if (!NavalInvasion.CanEmbark(campaign, fleet, army, out _)) continue;
            Loadable.Add(army);
        }
        if (Loadable.Count == 0)
        {
            // Nobody at the quayside. An invasion the AI wants and never mounts because its
            // divisions are all standing on a front six hundred miles inland is not an AI
            // with an invasion; so it sends for formations to embark.
            SummonToPort(campaign, fleet, nation);
            return false;
        }

        // Embark amphibious-trained marines and fresh offensive divisions first.
        Loadable.Sort((left, right) =>
        {
            var leftAmphib = UnitTypeCatalog.Get(left.TemplateId).Amphibious;
            var rightAmphib = UnitTypeCatalog.Get(right.TemplateId).Amphibious;
            var cmp = rightAmphib.CompareTo(leftAmphib);
            if (cmp != 0) return cmp;
            cmp = (right.Organisation * right.Strength).CompareTo(left.Organisation * left.Strength);
            return cmp != 0 ? cmp : left.Id.CompareTo(right.Id);
        });

        var embarked = 0;
        var loadedDivisions = NavalInvasion.CarriedDivisions(campaign, fleet);
        foreach (var army in Loadable)
        {
            if (embarked >= limit) break;
            if (loadedDivisions + army.Divisions > capacity + 0.001f) continue;
            NavalInvasion.Embark(campaign, fleet, army);
            loadedDivisions += army.Divisions;
            embarked++;
        }
        if (embarked == 0)
        {
            SummonToPort(campaign, fleet, nation);
            return false;
        }
        return true;
    }

    /// <summary>
    /// March up to a wave's worth of idle / non-front formations (preferring marines) to the
    /// fleet's port, while always keeping a home garrison behind if few divisions remain.
    /// </summary>
    private static void SummonToPort(Campaign campaign, Fleet fleet, Nation nation)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null || fleet.HomeProvinceId <= 0) return;

        var capacity = Math.Clamp((int)MathF.Floor(NavalInvasion.Capacity(campaign, fleet)), 1, 4);
        var candidates = new List<(Army Army, float Amphibious, float Distance)>();
        var alreadyHeading = 0;
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || army.EmbarkedFleetId >= 0) continue;
            if (army.InBattle || army.ProvinceId <= 0) continue;
            if (army.TemplateId == MilitiaCore.TemplateId || army.FrontLineId >= 0) continue;
            if (army.Route.Count > 0)
            {
                if (army.OrderProvinceId == fleet.HomeProvinceId) alreadyHeading++;
                continue;
            }
            if (army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead or ArmyOrder.DefendFront)
            {
                if (WalksToFront(campaign, nation, army.AssignedWarId).Contains(army.ProvinceId)) continue;
            }
            if (army.ProvinceId != fleet.HomeProvinceId && topology.FindRoute(army.ProvinceId,
                    fleet.HomeProvinceId, (ushort)nation.MapIndex, campaign.ProvinceController).Length == 0) continue;
            var centre = topology.CenterOf(army.ProvinceId);
            var distance = NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude,
                centre.Latitude, centre.Longitude);
            candidates.Add((army, UnitTypeCatalog.Get(army.TemplateId).Amphibious, distance));
        }
        if (candidates.Count == 0 || alreadyHeading >= capacity) return;

        // Leave at least 2 divisions on the home landmass when summoning an expeditionary wave.
        var maxSummon = Math.Min(capacity - alreadyHeading, Math.Max(1, candidates.Count - 2));
        if (candidates.Count <= 2) maxSummon = alreadyHeading > 0 ? 0 : 1;
        if (maxSummon <= 0) return;

        candidates.Sort((left, right) =>
        {
            var cmp = right.Amphibious.CompareTo(left.Amphibious);
            if (cmp != 0) return cmp;
            cmp = left.Distance.CompareTo(right.Distance);
            return cmp != 0 ? cmp : left.Army.Id.CompareTo(right.Army.Id);
        });

        for (var i = 0; i < maxSummon && i < candidates.Count; i++)
        {
            new MoveArmyCommand
            {
                IssuerId = nation.Id, ArmyId = candidates[i].Army.Id, TargetProvinceId = fleet.HomeProvinceId
            }.Apply(campaign);
        }
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
        foreach (var candidate in Warfare.OpenWars(campaign))
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

    private static void LaunchIfPossible(Campaign campaign, Fleet fleet, Nation nation, Nation enemy, StrategicWar war = null)
    {
        // If we already hold an occupied enemy coastal bridgehead that walks to the front AND
        // has room to expand (not overcrowded in a tiny 1-2 province pocket), shuttle follow-up
        // waves straight into that friendly beachhead port. If our pocket is cramped and already
        // packed with divisions, open a flanking second landing on another weakly defended beach!
        var bridgehead = war is not null ? FriendlyBridgeheadPort(campaign, fleet, nation, war) : -1;
        var beach = bridgehead > 0 ? bridgehead : BestBeach(campaign, fleet, enemy);
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
    /// A coastal province already captured from the enemy across water that can walk to our
    /// overseas front in <paramref name="war"/>, unless that pocket is tiny and already crowded
    /// (in which case returning -1 lets the fleet open a flanking second landing!).
    /// </summary>
    private static int FriendlyBridgeheadPort(Campaign campaign, Fleet fleet, Nation nation, StrategicWar war)
    {
        var topology = campaign.ProvinceMap;
        var control = campaign.ProvinceController;
        if (topology is null || control is null || war is null) return -1;
        var reachableFromFront = WalksToFront(campaign, nation, war.Id);
        if (reachableFromFront.Count == 0) return -1;
        var basin = SeaLanes.BasinAt(topology, fleet.Latitude, fleet.Longitude);
        var search = campaign.Defines.AiInvasionSearchDegrees;
        var own = (ushort)nation.MapIndex;

        // Count how many occupied enemy provinces we hold in this front component and how many
        // friendly divisions are already crammed inside it.
        var occupiedProvinces = 0;
        foreach (var p in reachableFromFront)
            if (p > 0 && p < control.Length && control[p] == own && topology.ProvinceToNation[p] != own)
                occupiedProvinces++;
        if (occupiedProvinces == 0) return -1;
        if (occupiedProvinces <= 2)
        {
            var crowded = 0;
            foreach (var army in campaign.Armies)
                if (army.NationId == nation.Id && army.EmbarkedFleetId < 0 && army.ProvinceId > 0 &&
                    reachableFromFront.Contains(army.ProvinceId))
                    crowded++;
            // Already 8+ divisions in a 1-2 province beachhead: open a second coastal landing!
            if (crowded >= 8) return -1;
        }

        var best = -1;
        var bestDistance = float.MaxValue;
        foreach (var province in SeaLanes.CoastalProvinces(topology))
        {
            if (province <= 0 || province >= control.Length || control[province] != own) continue;
            // Must be an occupied enemy-owned coastal bridgehead on the rival's landmass, never
            // one of our own tiny offshore islands (like Kinmen) where troops would just stack.
            if (topology.ProvinceToNation[province] == own) continue;
            if (!reachableFromFront.Contains(province)) continue;
            if (basin >= 0 && SeaLanes.HarbourBasin(topology, province) != basin) continue;
            if (!SeaLanes.TryHarbourPoint(topology, province, out var latitude, out var longitude)) continue;
            var distance = NavalOperations.Distance(fleet.Latitude, fleet.Longitude, latitude, longitude);
            if (distance >= search || distance >= bestDistance) continue;
            bestDistance = distance;
            best = province;
        }
        return best;
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
        Beaches.Sort((left, right) => left.Distance != right.Distance ? left.Distance.CompareTo(right.Distance) : left.Province.CompareTo(right.Province));
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
