using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Fleets that can be put together and taken apart.
///
/// A navy was a list of squadrons fixed at the size the yards or the history file made them:
/// fifteen counters that could each be sent somewhere and nothing else. A real admiralty
/// concentrates - three squadrons become one battle line for the decisive action - and
/// detaches - half the line peels off to escort a convoy. Both are here, for the player and
/// the computer alike, on the same rules: the same country, the same water.
/// </summary>
public static class FleetGroups
{
    /// <summary>How close, in degrees of arc, fleets must lie to be joined at sea.</summary>
    public const float JoinReachDegrees = 1.5f;

    /// <summary>Whether two fleets lie together: the same berth, or within reach at sea.</summary>
    public static bool Together(Fleet left, Fleet right) =>
        left is not null && right is not null &&
        ((left.BerthProvinceId > 0 && left.BerthProvinceId == right.BerthProvinceId) ||
         NavalOperations.Distance(left.Latitude, left.Longitude, right.Latitude, right.Longitude) <= JoinReachDegrees);

    /// <summary>
    /// Fold the others into the first. Condition is averaged over the hulls, so a fresh
    /// squadron joined to a battered one makes a half-battered fleet rather than a fresh one.
    /// </summary>
    public static int Merge(Campaign campaign, Fleet into, IEnumerable<Fleet> others)
    {
        if (campaign is null || into is null || others is null) return 0;
        var merged = 0;
        foreach (var other in others.ToList())
        {
            if (other is null || ReferenceEquals(other, into) || other.NationId != into.NationId || !Together(into, other)) continue;
            var total = Math.Max(1, into.Hulls + other.Hulls);
            float Mix(float mine, float theirs) => (mine * into.Hulls + theirs * other.Hulls) / total;
            into.Strength = Mix(into.Strength, other.Strength);
            into.Organisation = Mix(into.Organisation, other.Organisation);
            into.Supply = Mix(into.Supply, other.Supply);
            into.Experience = Mix(into.Experience, other.Experience);
            into.Readiness = Mix(into.Readiness, other.Readiness);
            into.Hulls += other.Hulls;
            into.Establishment += other.Establishment;
            into.HullsOrdered += other.HullsOrdered;
            into.HullsLost += other.HullsLost;
            into.ReadyDay = Math.Max(into.ReadyDay, other.ReadyDay);
            foreach (var carried in other.Carried)
                if (!into.Carried.Contains(carried)) into.Carried.Add(carried);
            campaign.Fleets.Remove(other);
            merged++;
        }
        if (merged > 0) campaign.MarkStrategicChange();
        return merged;
    }

    /// <summary>
    /// Detach half the hulls as a new fleet in the same place, on the same mission. Troops
    /// aboard and hulls on the slipways stay with the original.
    /// </summary>
    public static Fleet Split(Campaign campaign, Fleet fleet)
    {
        if (campaign is null || fleet is null || fleet.Hulls < 2 || fleet.InAction) return null;
        var detached = fleet.Hulls / 2;
        fleet.Hulls -= detached;
        var establishment = Math.Min(detached, Math.Max(0, fleet.Establishment - fleet.Hulls));
        fleet.Establishment = Math.Max(fleet.Hulls, fleet.Establishment - establishment);
        var nation = campaign.NationById(fleet.NationId);
        var owned = campaign.Fleets.Count(candidate => candidate.NationId == fleet.NationId) + 1;
        var copy = new Fleet
        {
            Id = campaign.Fleets.Count == 0 ? 1 : campaign.Fleets.Max(candidate => candidate.Id) + 1,
            NationId = fleet.NationId,
            Name = Loc.Format("{0}. {1} Squadron", owned, nation?.Tag ?? "?"),
            Hulls = detached,
            Establishment = Math.Max(detached, establishment),
            Strength = fleet.Strength,
            Organisation = fleet.Organisation,
            Supply = fleet.Supply,
            Experience = fleet.Experience,
            Readiness = fleet.Readiness,
            Latitude = fleet.Latitude,
            Longitude = fleet.Longitude,
            HomeProvinceId = fleet.HomeProvinceId,
            HomeLatitude = fleet.HomeLatitude,
            HomeLongitude = fleet.HomeLongitude,
            BerthProvinceId = fleet.BerthProvinceId,
            ReadyDay = fleet.ReadyDay,
            // A loaded fleet's escort detaches to patrol; the convoy keeps its own orders.
            Mission = fleet.Mission == FleetMission.Invasion ? FleetMission.Patrol : fleet.Mission
        };
        if (fleet.HasOrder && fleet.Carried.Count == 0)
        {
            copy.HasOrder = true;
            copy.OrderLatitude = fleet.OrderLatitude;
            copy.OrderLongitude = fleet.OrderLongitude;
        }
        campaign.Fleets.Add(copy);
        campaign.MarkStrategicChange();
        return copy;
    }
}

/// <summary>Join several fleets of one country lying together into the first of them.</summary>
public sealed class MergeFleetsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int[] FleetIds { get; init; } = Array.Empty<int>();

    public int Merged { get; private set; }
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var fleets = FleetIds.Select(id => campaign.Fleets.Find(fleet => fleet.Id == id))
            .Where(fleet => fleet is not null && fleet.NationId == IssuerId).ToList();
        if (fleets.Count < 2) { Refusal = Loc.Get("Select at least two of your fleets to join."); return; }
        // Into the largest: it keeps its name, its orders and its place in the line.
        var into = fleets.OrderByDescending(fleet => fleet.Hulls).ThenBy(fleet => fleet.Id).First();
        Merged = FleetGroups.Merge(campaign, into, fleets);
        if (Merged == 0) Refusal = Loc.Get("Fleets join only in the same port or within a day's sail of each other.");
    }
}

/// <summary>Detach half of one fleet as a new squadron.</summary>
public sealed class SplitFleetCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FleetId { get; init; }

    public int NewFleetId { get; private set; } = -1;
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var fleet = campaign.Fleets.Find(candidate => candidate.Id == FleetId);
        if (fleet is null || fleet.NationId != IssuerId) { Refusal = Loc.Get("That fleet is not yours to order."); return; }
        if (fleet.Hulls < 2) { Refusal = Loc.Get("A single hull cannot be split."); return; }
        if (fleet.InAction) { Refusal = Loc.Get("A fleet in action cannot divide."); return; }
        NewFleetId = FleetGroups.Split(campaign, fleet)?.Id ?? -1;
    }
}
