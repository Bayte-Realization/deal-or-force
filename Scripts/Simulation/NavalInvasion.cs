using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Putting an army ashore.
///
/// This was the hole the whole naval side was shaped around: `CanDeclareWar` refused every
/// war across water with the words "naval invasions are not ported yet", which meant every
/// island in the world was permanently safe and a navy could never do the one thing navies
/// are for.
///
/// The model is the shortest one that is still a model. Divisions embark on a fleet in a
/// port, the fleet sails, and what it carries goes ashore where it is sent. What the fleet
/// can carry is set by its hulls; what a landing costs is cohesion, because troops coming
/// off a ship onto a hostile beach are not in order. There is no invasion planning
/// percentage, no naval supremacy calculation and no separate transport fleet: those are
/// Hearts of Iron's, and they are the parts of it people bounce off.
///
/// A landing that finds nobody home takes the province. A landing that finds a defender
/// starts an ordinary battle with the ordinary combat model, which is the point - an
/// amphibious assault is a battle a player already knows how to read, fought at a
/// disadvantage they can see in the panel.
/// </summary>
public static class NavalInvasion
{
    /// <summary>How many divisions one hull can carry.</summary>
    public static float Capacity(Campaign campaign, Fleet fleet) =>
        fleet.Hulls * campaign.Defines.FleetDivisionsPerHull;

    public static float CarriedDivisions(Campaign campaign, Fleet fleet)
    {
        var carried = 0f;
        foreach (var id in fleet.Carried)
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is not null) carried += army.Divisions;
        }
        return carried;
    }

    /// <summary>
    /// Whether a formation is close enough to a fleet to walk up its gangway. Loading
    /// happens in a port, so both have to be at the same place - which for a fleet means
    /// its own coast rather than the open sea.
    /// </summary>
    public static bool CanEmbark(Campaign campaign, Fleet fleet, Army army, out string reason)
    {
        reason = "";
        if (fleet is null || army is null) { reason = "Nothing to load."; return false; }
        if (army.NationId != fleet.NationId) { reason = "That formation is not yours to load."; return false; }
        if (army.EmbarkedFleetId >= 0) { reason = $"{army.Name} is already aboard."; return false; }
        if (army.InBattle) { reason = $"{army.Name} is in action."; return false; }
        if (fleet.HasOrder) { reason = "The fleet is under way. Bring it to port first."; return false; }
        if (army.ProvinceId <= 0) { reason = $"{army.Name} is nowhere the fleet can reach."; return false; }

        var topology = campaign.ProvinceMap;
        if (topology is null) { reason = "No map."; return false; }
        var centre = topology.CenterOf(army.ProvinceId);
        if (NavalOperations.Distance(fleet.Latitude, fleet.Longitude, centre.Latitude, centre.Longitude) >
            campaign.Defines.FleetEmbarkRangeDegrees)
        {
            reason = $"{army.Name} is too far from the fleet to embark.";
            return false;
        }
        if (CarriedDivisions(campaign, fleet) + army.Divisions > Capacity(campaign, fleet) + 0.001f)
        {
            reason = $"{fleet.Name} has no room for {army.Name}.";
            return false;
        }
        return true;
    }

    public static void Embark(Campaign campaign, Fleet fleet, Army army)
    {
        // An embarked formation is off the land map entirely: it holds no province, is not
        // on anybody's front, and cannot be walked into. Province zero is what every land
        // system already treats as "nowhere", so nothing else has to learn about ships.
        army.EmbarkedFleetId = fleet.Id;
        army.ProvinceId = 0;
        army.Route.Clear();
        army.PlannedRoute.Clear();
        army.MoveProgress = 0f;
        army.OrderKind = ArmyOrder.Manual;
        army.OrderProvinceId = -1;
        army.OrderStateMapIndex = -1;
        army.AssignedWarId = -1;
        army.Entrenchment = 0f;
        BattlePlanning.Forget(campaign, army);
        if (!fleet.Carried.Contains(army.Id)) fleet.Carried.Add(army.Id);
    }

    /// <summary>Put everyone back on the shore the fleet is standing off.</summary>
    public static int Disembark(Campaign campaign, Fleet fleet, int provinceId)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null || provinceId <= 0 || provinceId > topology.ProvinceCount) return 0;
        var centre = topology.CenterOf(provinceId);
        var landed = 0;
        foreach (var id in fleet.Carried)
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null) continue;
            army.EmbarkedFleetId = -1;
            army.ProvinceId = provinceId;
            army.StateMapIndex = topology.ProvinceToState[provinceId];
            army.Latitude = centre.Latitude;
            army.Longitude = centre.Longitude;
            army.MoveProgress = 0f;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            landed++;
        }
        fleet.Carried.Clear();
        return landed;
    }

    /// <summary>
    /// The landing itself. Everyone aboard comes ashore in the target province, out of
    /// order and out of breath, and then one of two ordinary things happens: an undefended
    /// coast changes hands, or a battle starts.
    /// </summary>
    public static string Land(Campaign campaign, Fleet fleet, int provinceId)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return "no map";
        if (provinceId <= 0 || provinceId > topology.ProvinceCount) return "nowhere to land";
        if (fleet.Carried.Count == 0) return "nothing aboard to land";

        var invader = campaign.NationById(fleet.NationId);
        if (invader is null) return "the fleet has no country";
        var holderIndex = controllers[provinceId];
        var holder = campaign.Nations.Find(nation => nation.MapIndex == holderIndex);

        var war = holder is null || holder.Id == invader.Id
            ? null
            : campaign.Wars.Find(candidate => !candidate.Ended && candidate.Involves(invader.Id) &&
                                              candidate.Involves(holder.Id));
        if (holder is not null && holder.Id != invader.Id && war is null)
            return $"not at war with {holder.Name}";

        var landed = new List<Army>();
        foreach (var id in fleet.Carried)
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is not null) landed.Add(army);
        }

        Disembark(campaign, fleet, provinceId);
        foreach (var army in landed)
        {
            // Coming off a ship costs order, and a beachhead is not dug in - but troops
            // trained for it pay most of that price in training instead.
            var trained = UnitTypeCatalog.Get(army.TemplateId).Amphibious;
            army.Organisation = M.Clamp01(army.Organisation -
                campaign.Defines.InvasionLandingOrganisationCost * (1f - trained));
            army.Entrenchment = 0f;
            army.Planning = 0f;
            army.ReadyDay = campaign.ElapsedDays +
                (int)MathF.Round(campaign.Defines.InvasionRestAfterLandingDays * (1f - trained));
            if (war is not null) army.AssignedWarId = war.Id;
        }

        fleet.Mission = FleetMission.Patrol;
        fleet.TargetProvinceId = -1;
        return war is null
            ? $"{landed.Count:N0} formation(s) put ashore"
            : Warfare.LandInvasion(campaign, war, provinceId, landed);
    }
}

/// <summary>Load formations onto a fleet lying in port.</summary>
public sealed class EmbarkArmiesCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FleetId { get; init; }
    public IReadOnlyList<int> ArmyIds { get; init; } = Array.Empty<int>();

    public void Apply(Campaign campaign)
    {
        var fleet = campaign.Fleets.Find(candidate => candidate.Id == FleetId);
        if (fleet is null || fleet.NationId != IssuerId) return;
        foreach (var id in ArmyIds)
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null) continue;
            if (!NavalInvasion.CanEmbark(campaign, fleet, army, out _)) continue;
            NavalInvasion.Embark(campaign, fleet, army);
        }
    }
}

/// <summary>Send a loaded fleet at a coast and put what it carries ashore when it arrives.</summary>
public sealed class LaunchInvasionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FleetId { get; init; }
    public int ProvinceId { get; init; }

    public void Apply(Campaign campaign)
    {
        var fleet = campaign.Fleets.Find(candidate => candidate.Id == FleetId);
        var topology = campaign.ProvinceMap;
        if (fleet is null || fleet.NationId != IssuerId || topology is null) return;
        if (ProvinceId <= 0 || ProvinceId > topology.ProvinceCount) return;
        if (fleet.Carried.Count == 0) return;

        var centre = topology.CenterOf(ProvinceId);
        fleet.Mission = FleetMission.Invasion;
        fleet.TargetProvinceId = ProvinceId;
        fleet.HasOrder = true;
        fleet.OrderLatitude = centre.Latitude;
        fleet.OrderLongitude = centre.Longitude;
    }
}
