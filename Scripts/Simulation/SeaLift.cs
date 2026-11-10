using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// One sea lift: formations taken across water by one fleet, from a port of their country's
/// own to the shore nearest where they were sent, and on from there on foot. Saved.
/// </summary>
public sealed class SeaLiftOrder
{
    public int Id;
    public int NationId;
    public readonly List<int> ArmyIds = new();
    public int FleetId = -1;
    /// <summary>Where they board: a coastal province of their own country.</summary>
    public int PortProvinceId = -1;
    /// <summary>Where they come ashore: the coast nearest the destination.</summary>
    public int LandingProvinceId = -1;
    /// <summary>Where they were sent - the landing, or a province inland of it.</summary>
    public int TargetProvinceId = -1;
    /// <summary>0 gathering at the port, 1 at sea, 2 ashore (the order is then removed).</summary>
    public int Stage;
    public int OrderedDay;
}

public sealed partial class Campaign
{
    /// <summary>The sea lifts under way (SeaLift).</summary>
    public readonly List<SeaLiftOrder> SeaLifts = new();
}

public sealed partial class Defines
{
    /// <summary>
    /// Days a sea lift waits at its port for formations still marching to it before it sails
    /// with those that have arrived; 0 sails as soon as the fleet is in.
    /// </summary>
    public int SeaLiftGatherDays { get; set; } = 45;
    /// <summary>How far (degrees of arc) from the destination a landing beach is looked for.</summary>
    public float SeaLiftLandingSearchDegrees { get; set; } = 14f;
}

/// <summary>
/// Moving formations across the sea with one order.
///
/// Taking a division over water was four separate jobs: march it to a port, bring a fleet to the
/// same port, load it, and send the fleet at a beach - each a different panel, each failing
/// quietly if the one before had not finished. A player sends formations somewhere across the
/// sea the same way as anywhere else - a right-click - and when no march gets there, this
/// does the rest: it picks the country's own port the formations can walk to on the right sea,
/// a fleet of the country's with room for them all, and the coast nearest the destination; the
/// formations march to the port while the fleet sails there, they board when everyone has come
/// (or after Defines.SeaLiftGatherDays with whoever has), the fleet carries them over and puts
/// them ashore - a friendly coast as an arrival, an enemy's as a landing with the ordinary
/// battle - and from the beach they march on to where they were sent.
///
/// The same rules for every country: nothing here asks who is playing.
/// </summary>
public static class SeaLift
{
    /// <summary>Whether a fleet is carrying out a sea lift (the admiralty leaves it alone).</summary>
    public static bool Holds(Campaign campaign, Fleet fleet)
    {
        if (campaign is null || fleet is null) return false;
        foreach (var order in campaign.SeaLifts)
            if (order.FleetId == fleet.Id) return true;
        return false;
    }

    /// <summary>The sea lift a formation is part of, or null.</summary>
    public static SeaLiftOrder Of(Campaign campaign, Army army)
    {
        if (campaign is null || army is null) return null;
        foreach (var order in campaign.SeaLifts)
            if (order.ArmyIds.Contains(army.Id)) return order;
        return null;
    }

    /// <summary>Every controller a country's formations may stand on: its own, those it may enter, and its enemies'.</summary>
    internal static HashSet<ushort> Walkable(Campaign campaign, int nationId)
    {
        var allowed = MilitaryAccess.Controllers(campaign, nationId);
        foreach (var war in Warfare.LiveWars(campaign))
        {
            if (war.Ended || !war.Involves(nationId)) continue;
            WarCoalition.FillControllers(campaign, war, true, allowed);
            WarCoalition.FillControllers(campaign, war, false, allowed);
        }
        return allowed;
    }

    /// <summary>
    /// The coast to land on for a destination: the destination itself when it has a coast, or the
    /// coastal province nearest it from which the formations can walk there. Never a neutral
    /// country's shore. -1 when there is none within Defines.SeaLiftLandingSearchDegrees.
    /// </summary>
    internal static int LandingFor(Campaign campaign, int nationId, int target, HashSet<ushort> walkable)
    {
        var map = campaign.ProvinceMap;
        var control = campaign.ProvinceController;
        if (map is null || control is null || target <= 0 || target > map.ProvinceCount || map.IsWater(target)) return -1;
        bool Standable(int province) => province > 0 && province < control.Length && walkable.Contains(control[province]);
        if (SeaLanes.HarbourOf(map, target) >= 0 && Standable(target)) return target;
        var centre = map.CenterOf(target);
        var search = MathF.Max(1f, campaign.Defines.SeaLiftLandingSearchDegrees);
        var candidates = new List<(int Province, float Distance)>();
        foreach (var coast in SeaLanes.CoastalProvinces(map))
        {
            if (!Standable(coast) || SeaLanes.HarbourOf(map, coast) < 0) continue;
            var at = map.CenterOf(coast);
            var distance = NavalOperations.Distance(centre.Latitude, centre.Longitude, at.Latitude, at.Longitude);
            if (distance <= search) candidates.Add((coast, distance));
        }
        foreach (var (coast, _) in candidates.OrderBy(c => c.Distance).ThenBy(c => c.Province).Take(12))
            if (map.FindOperationalRoute(coast, target, walkable, control).Length > 0) return coast;
        return -1;
    }

    /// <summary>
    /// The port to board at: a coastal province the country holds and owns, on the landing's sea,
    /// that the lead formation can walk to - the nearest to it, with the sea passage counted at
    /// half the weight of the march. -1 when there is none.
    /// </summary>
    internal static int PortFor(Campaign campaign, Nation nation, Army lead, int landing)
    {
        var map = campaign.ProvinceMap;
        var control = campaign.ProvinceController;
        if (map is null || control is null || lead is null || lead.ProvinceId <= 0) return -1;
        var sea = SeaLanes.HarbourBasin(map, landing);
        var from = map.CenterOf(lead.ProvinceId);
        var to = map.CenterOf(landing);
        var own = (ushort)nation.MapIndex;
        var candidates = new List<(int Province, float Score)>();
        foreach (var coast in SeaLanes.CoastalProvinces(map))
        {
            if (coast >= control.Length || control[coast] != own || map.ProvinceToNation[coast] != own) continue;
            if (SeaLanes.HarbourOf(map, coast) < 0 || SeaLanes.HarbourBasin(map, coast) != sea) continue;
            var at = map.CenterOf(coast);
            var score = NavalOperations.Distance(from.Latitude, from.Longitude, at.Latitude, at.Longitude) +
                        0.5f * NavalOperations.Distance(at.Latitude, at.Longitude, to.Latitude, to.Longitude);
            candidates.Add((coast, score));
        }
        // The nearest dozen by that first guess, scored again by the passage the fleet will really
        // sail: a straight line across a peninsula is no measure of a voyage round it.
        var beachKnown = SeaLanes.TryHarbourPoint(map, landing, out var toLatitude, out var toLongitude);
        var course = new List<float>();
        var best = -1;
        var bestScore = float.MaxValue;
        foreach (var (coast, _) in candidates.OrderBy(c => c.Score).ThenBy(c => c.Province).Take(12))
        {
            if (coast != lead.ProvinceId && MilitaryAccess.Route(campaign, nation.Id, lead.ProvinceId, coast, control).Length == 0)
                continue;
            var at = map.CenterOf(coast);
            var march = NavalOperations.Distance(from.Latitude, from.Longitude, at.Latitude, at.Longitude);
            var passage = NavalOperations.Distance(at.Latitude, at.Longitude, to.Latitude, to.Longitude);
            if (beachKnown && SeaLanes.TryHarbourPoint(map, coast, out var quayLatitude, out var quayLongitude))
            {
                if (!SeaLanes.Plan(campaign, nation.Id, quayLatitude, quayLongitude, toLatitude, toLongitude, course, out var length))
                    continue;
                passage = length;
            }
            var score = march + passage / 5f;
            if (score >= bestScore) continue;
            bestScore = score;
            best = coast;
        }
        return best;
    }

    /// <summary>
    /// The fleet to carry them: one of the country's own, not already carrying anybody or on
    /// another lift, on the port's sea, with room for every division - the nearest to the port.
    /// </summary>
    internal static Fleet FleetFor(Campaign campaign, Nation nation, int port, float divisions, out float largest)
    {
        largest = 0f;
        var map = campaign.ProvinceMap;
        if (map is null || !SeaLanes.TryHarbourPoint(map, port, out var latitude, out var longitude)) return null;
        var sea = SeaLanes.HarbourBasin(map, port);
        Fleet best = null;
        var bestDistance = float.MaxValue;
        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.NationId != nation.Id || fleet.Hulls <= 0 || fleet.Carried.Count > 0 || Holds(campaign, fleet)) continue;
            if (SeaLanes.BasinAt(map, fleet.Latitude, fleet.Longitude) != sea) continue;
            var capacity = NavalInvasion.Capacity(campaign, fleet);
            largest = MathF.Max(largest, capacity);
            if (capacity + 0.001f < divisions) continue;
            var distance = NavalOperations.Distance(fleet.Latitude, fleet.Longitude, latitude, longitude);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = fleet;
        }
        return best;
    }

    public static void Tick(Campaign campaign)
    {
        if (campaign is null || campaign.SeaLifts.Count == 0) return;
        for (var index = campaign.SeaLifts.Count - 1; index >= 0; index--)
        {
            var order = campaign.SeaLifts[index];
            if (Advance(campaign, order)) continue;
            campaign.SeaLifts.RemoveAt(index);
        }
    }

    /// <summary>One lift's step. False when it is over and leaves the list.</summary>
    private static bool Advance(Campaign campaign, SeaLiftOrder order)
    {
        var fleet = campaign.Fleets.Find(candidate => candidate.Id == order.FleetId);
        var nation = campaign.NationById(order.NationId);
        var map = campaign.ProvinceMap;
        if (nation is null || map is null) return false;
        // Formations that are gone - or, still ashore, were given another order since - leave the lift.
        order.ArmyIds.RemoveAll(id =>
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null || army.NationId != order.NationId) return true;
            return order.Stage == 0 && army.EmbarkedFleetId < 0 && army.ProvinceId != order.PortProvinceId &&
                   army.OrderProvinceId != order.PortProvinceId && !army.InBattle;
        });
        if (fleet is null || fleet.Hulls <= 0)
        {
            // Sunk, or scrapped, with formations aboard: they went down with it (NavalCore). Ashore,
            // they simply stay where they are.
            return false;
        }
        if (order.ArmyIds.Count == 0 && fleet.Carried.Count == 0) return false;

        if (order.Stage == 0)
        {
            if (!SeaLanes.TryHarbourPoint(map, order.PortProvinceId, out var latitude, out var longitude)) return false;
            var near = NavalOperations.Distance(fleet.Latitude, fleet.Longitude, latitude, longitude) <=
                       campaign.Defines.FleetEmbarkRangeDegrees;
            if (!near)
            {
                // Under way to the port; sent again if something else stopped it on the way.
                if (!fleet.HasOrder && !NavalOperations.SendTo(campaign, fleet, latitude, longitude)) return false;
                return true;
            }
            if (fleet.HasOrder) return true;
            var waiting = 0;
            var arrived = new List<Army>();
            foreach (var id in order.ArmyIds)
            {
                var army = campaign.Armies.Find(candidate => candidate.Id == id);
                if (army is null || army.EmbarkedFleetId >= 0) continue;
                if (army.ProvinceId == order.PortProvinceId && !army.IsMoving && !army.InBattle) arrived.Add(army);
                else waiting++;
            }
            var patience = Math.Max(0, campaign.Defines.SeaLiftGatherDays);
            if (waiting > 0 && campaign.ElapsedDays - order.OrderedDay < patience) return true;
            foreach (var army in arrived)
                if (NavalInvasion.CanEmbark(campaign, fleet, army, out _)) NavalInvasion.Embark(campaign, fleet, army);
            if (fleet.Carried.Count == 0) return waiting > 0;
            // Whoever had not come by now is not part of it any more.
            order.ArmyIds.RemoveAll(id => !fleet.Carried.Contains(id));
            var launch = new LaunchInvasionCommand { IssuerId = nation.Id, FleetId = fleet.Id, ProvinceId = order.LandingProvinceId };
            launch.Apply(campaign);
            if (launch.Refusal.Length > 0)
            {
                // The coast cannot be reached after all: back on the quay rather than at sea for ever.
                NavalInvasion.Disembark(campaign, fleet, order.PortProvinceId);
                return false;
            }
            order.Stage = 1;
            return true;
        }

        if (order.Stage == 1)
        {
            if (fleet.Carried.Count > 0)
            {
                // Still at sea, or the landing was refused (a shore that has turned neutral):
                // then ashore at the nearest friendly coast, and the lift ends there.
                if (fleet.Mission == FleetMission.Invasion || fleet.HasOrder) return true;
                NavalInvasion.PutAshore(campaign, fleet, out _);
                return false;
            }
            // Ashore. On to where they were sent, on foot.
            if (order.TargetProvinceId > 0 && order.TargetProvinceId != order.LandingProvinceId)
                foreach (var id in order.ArmyIds)
                {
                    var army = campaign.Armies.Find(candidate => candidate.Id == id);
                    if (army is null || army.ProvinceId <= 0) continue;
                    new MoveArmyCommand { IssuerId = nation.Id, ArmyId = army.Id, TargetProvinceId = order.TargetProvinceId }
                        .Apply(campaign);
                }
            return false;
        }
        return false;
    }
}

/// <summary>
/// Send formations across the sea: they march to a port, a fleet carries them over and puts them
/// ashore, and they march on to the destination (SeaLift). The answer to a right-click that no
/// march reaches.
/// </summary>
public sealed class SeaLiftCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public IReadOnlyList<int> ArmyIds { get; init; } = Array.Empty<int>();
    public int TargetProvinceId { get; init; }

    /// <summary>Why nothing was ordered, or empty when the lift is under way.</summary>
    public string Refusal { get; private set; } = "";
    /// <summary>The lift ordered, for the order's report.</summary>
    public SeaLiftOrder Order { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var map = campaign.ProvinceMap;
        if (nation is null || map is null) { Refusal = Loc.Get("No map."); return; }
        var armies = new List<Army>();
        foreach (var id in ArmyIds.Distinct())
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null || army.NationId != IssuerId || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.InBattle) continue;
            if (SeaLift.Of(campaign, army) is not null) continue;
            armies.Add(army);
        }
        if (armies.Count == 0) { Refusal = Loc.Get("No formation free to go by sea."); return; }

        var walkable = SeaLift.Walkable(campaign, nation.Id);
        var landing = SeaLift.LandingFor(campaign, nation.Id, TargetProvinceId, walkable);
        if (landing < 0) { Refusal = Loc.Get("No coast near there that our formations may land on."); return; }
        var lead = armies.OrderByDescending(a => a.Divisions).ThenBy(a => a.Id).First();
        var port = SeaLift.PortFor(campaign, nation, lead, landing);
        if (port < 0) { Refusal = Loc.Get("No port of ours on that sea that these formations can march to."); return; }
        var divisions = armies.Sum(a => a.Divisions);
        var fleet = SeaLift.FleetFor(campaign, nation, port, divisions, out var largest);
        if (fleet is null)
        {
            Refusal = largest <= 0f
                ? Loc.Get("No free fleet of ours on that sea.")
                : Loc.Format("No free fleet can carry {0:0.#} divisions; the largest carries {1:0.#}. Send fewer, or merge fleets.", divisions, largest);
            return;
        }
        if (!SeaLanes.TryHarbourPoint(map, port, out var latitude, out var longitude) ||
            (NavalOperations.Distance(fleet.Latitude, fleet.Longitude, latitude, longitude) > campaign.Defines.FleetEmbarkRangeDegrees &&
             !NavalOperations.SendTo(campaign, fleet, latitude, longitude)))
        {
            Refusal = Loc.Get("The fleet cannot reach that port by sea.");
            return;
        }

        var order = new SeaLiftOrder
        {
            Id = campaign.SeaLifts.Count == 0 ? 1 : campaign.SeaLifts.Max(o => o.Id) + 1,
            NationId = nation.Id, FleetId = fleet.Id, PortProvinceId = port, LandingProvinceId = landing,
            TargetProvinceId = TargetProvinceId, OrderedDay = campaign.ElapsedDays
        };
        foreach (var army in armies)
        {
            if (army.ProvinceId != port)
            {
                var march = new MoveArmyCommand { IssuerId = nation.Id, ArmyId = army.Id, TargetProvinceId = port };
                march.Apply(campaign);
                if (army.OrderProvinceId != port && army.ProvinceId != port) continue;
            }
            order.ArmyIds.Add(army.Id);
        }
        if (order.ArmyIds.Count == 0) { Refusal = Loc.Get("No port of ours on that sea that these formations can march to."); return; }
        campaign.SeaLifts.Add(order);
        Order = order;
    }
}
