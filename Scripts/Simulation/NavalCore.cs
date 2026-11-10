using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// What a fleet has been told to do.
///
/// Five, where Hearts of Iron has a dozen and a half. The ones that survived are the ones a
/// player can hold in their head and that the rest of the simulation can actually feel:
/// every one of these changes a number somewhere else. The rest of what a navy does in that
/// game - patrol regions, repair priorities, task force composition rules - is detail this
/// game has nowhere to put.
///
/// The vocabulary is Paradox's rather than Hearts of Iron's specifically: a fleet sits in
/// port, watches a sea, strangles a coast, preys on trade, or guards it. Victoria and
/// Europa Universalis have all worked in those terms, and they are the terms a player who
/// has never opened a naval screen already understands.
/// </summary>
public enum FleetMission
{
    /// <summary>In port. Repairs and reorganises fastest, and does nothing else.</summary>
    Anchored,

    /// <summary>At sea, watching. Ready to fight what comes; costs upkeep and readiness.</summary>
    Patrol,

    /// <summary>Sitting on an enemy coast, cutting what reaches it by sea.</summary>
    Blockade,

    /// <summary>Hunting an enemy's merchant shipping wherever it can be found.</summary>
    Raid,

    /// <summary>Guarding one's own shipping against exactly that.</summary>
    Escort,

    /// <summary>Carrying an army at a coast, to put it ashore when it arrives.</summary>
    Invasion
}

/// <summary>
/// A fleet, built on the same bones as a division.
///
/// A land formation is a count of divisions with an organisation, a strength, a supply
/// level and an experience, which together make a Power the combat model compares against
/// somebody else's. A fleet is hulls with the same four, and it works the same way - which
/// is the point. There is one set of ideas in this game about what a formation is and how
/// it wears out, and the navy uses it rather than inventing a second.
///
/// What is deliberately absent is everything that makes naval war its own game: ship
/// classes, screening ratios, carrier wings, positioning rolls. A fleet is a fleet.
/// </summary>
public sealed class Fleet
{
    public int Id;
    public int NationId;
    public string Name = "";

    /// <summary>Ships. The naval count, as divisions are the land one.</summary>
    public int Hulls;

    /// <summary>How much of the fleet is fit to sail. Lost in battle, restored in port.</summary>
    public float Strength = 1f;

    /// <summary>Cohesion under fire. Spent by fighting, recovered by resting.</summary>
    public float Organisation = 1f;

    /// <summary>Fuel, munitions and stores. Falls at sea, fills in port.</summary>
    public float Supply = 1f;

    /// <summary>What the crews have learned. Grows only by fighting.</summary>
    public float Experience;

    /// <summary>How much of the navy's budget has reached it. Set by military spending.</summary>
    public float Readiness = 0.85f;

    public float Latitude;
    public float Longitude;

    /// <summary>The coastal province it calls home, and returns to when it is beaten.</summary>
    public int HomeProvinceId = -1;
    public float HomeLatitude;
    public float HomeLongitude;

    public FleetMission Mission = FleetMission.Anchored;

    /// <summary>Where it has been sent. Negative latitude is valid, so an order is a flag.</summary>
    public bool HasOrder;
    public float OrderLatitude;
    public float OrderLongitude;

    /// <summary>Days before it can fight again after an action broke off.</summary>
    public int ReadyDay;

    /// <summary>Set for the day a fleet was in action, so the interface can say so.</summary>
    public bool InAction;

    /// <summary>Formations aboard, and the coast they are being taken to.</summary>
    public readonly List<int> Carried = new();
    public int TargetProvinceId = -1;

    /// <summary>
    /// The course still to sail, as latitude and longitude pairs; the last pair is where it
    /// stops. Plotted through the sea lanes, so a voyage round a continent goes round it.
    /// </summary>
    public readonly List<float> Waypoints = new();

    /// <summary>
    /// The order the course was plotted for. Anything that writes an order - a command, a
    /// beaten fleet turning for home - only has to write the order: a course that no longer
    /// matches it is plotted again before the fleet moves.
    /// </summary>
    public bool Routed;
    public float RoutedLatitude;
    public float RoutedLongitude;

    /// <summary>The friendly port it is lying in, or -1 when it is at sea.</summary>
    public int BerthProvinceId = -1;

    /// <summary>The hulls it was commissioned with, which the country's yards are sized to keep.</summary>
    public int Establishment;

    /// <summary>Hulls laid down for it and not yet delivered, and how far along the next one is.</summary>
    public int HullsOrdered;
    public float HullProgress;
    public MobilisationMode HullOrderMode;

    /// <summary>Hulls sunk under it, over the whole campaign.</summary>
    public int HullsLost;

    /// <summary>The last day it was in action, or -1.</summary>
    public int LastActionDay = -1;

    public bool IsMoving => HasOrder;

    /// <summary>Lying in a friendly port: repairing, refilling, and out of the fighting.</summary>
    public bool InPort => Mission == FleetMission.Anchored && !HasOrder && BerthProvinceId > 0;

    /// <summary>
    /// What the fleet is worth in a fight, on the same shape as a division's Power: the
    /// count, scaled by everything that can be worn away.
    /// </summary>
    public float Power => Hulls * Strength * Organisation *
                          (0.55f + Supply * 0.45f) * (0.80f + Experience * 0.30f) *
                          (0.60f + Readiness * 0.40f);

    /// <summary>Whether this fleet is a convoy rather than a battle line at the moment.</summary>
    public bool IsLoaded => Carried.Count > 0;

    /// <summary>
    /// What it is worth for simply being somewhere - blockading, escorting, being a threat.
    /// A battered fleet still closes a sea lane, the way a battered division still holds
    /// the province it stands in - but a wrecked one closes less of it than a whole one.
    /// </summary>
    public float Presence => Hulls * (0.40f + Strength * 0.60f) *
                             (0.80f + Experience * 0.30f) * (0.60f + Readiness * 0.40f);
}

public sealed partial class Defines
{
    /// <summary>How close a fleet has to lie to a friendly harbour to be in its port.</summary>
    public float FleetBerthRangeDegrees { get; set; } = 1.6f;

    /// <summary>
    /// How much of the damage a fleet carries when it breaks goes to the bottom. The rest is
    /// ships that limp home to be repaired; this share is ships that do not come back.
    /// </summary>
    public float FleetHullsSunkOnBreak { get; set; } = 0.3f;

    /// <summary>How far from a country's coast its merchant shipping can be found in full.</summary>
    public float FleetRaidRangeDegrees { get; set; } = 18f;

    /// <summary>What a raider far out at sea still takes, against one on the enemy's sea lanes.</summary>
    public float FleetRaidFloor { get; set; } = 0.2f;

    /// <summary>
    /// How many provinces of a supply trace a sea crossing counts for: an island or a
    /// beachhead no road reaches is supplied through the nearest port that one does, this
    /// much further from the depot.
    /// </summary>
    public int SeaSupplyHops { get; set; } = 3;
}

public static class NavalOperations
{
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same fleet upkeep and sailing pass, yielded between batches of fleets.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(NavalOperations))) yield break;
        var defines = campaign.Defines;
        foreach (var fleet in campaign.Fleets) fleet.InAction = false;

        // Everything below is a rate per day and this runs per step, so each takes the
        // step's share. Without it a fleet at an hourly step resupplied, repaired and
        // recovered its order twenty-four times faster than the number beside it says.
        var share = campaign.TickScale;
        yield return 0;
        var ticked = 0;
        foreach (var fleet in campaign.Fleets)
        {
            if (++ticked % 32 == 0) yield return 0;
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null) continue;
            // A fleet sunk to its last hull is rebuilt in its home port, so it still needs one.
            if (fleet.Hulls <= 0) { if (campaign.StepOpensNewDay) KeepPort(campaign, fleet, nation); continue; }

            // Readiness is what the treasury reaches the fleet with, and it moves slowly:
            // a navy is not re-crewed by a budget line in a fortnight. The drift is a day's, and
            // this is a step, so it takes the step's share of it - the whole of it every hour
            // closed the gap to the budget twenty-four times as fast as the define says.
            var funded = M.Clamp(nation.MilitarySpending /
                MathF.Max(0.01f, defines.BaselineMilitarySpending), 0.35f, 1f);
            fleet.Readiness = M.Lerp(fleet.Readiness, funded,
                1f - MathF.Pow(1f - M.Clamp01(defines.FleetReadinessDrift), share));

            // A port is a place somebody holds, and the holder changes. Asked once a day: a
            // port does not fall between two hours of the same afternoon.
            if (campaign.StepOpensNewDay) KeepPort(campaign, fleet, nation);

            if (fleet.InPort)
            {
                fleet.Supply = M.Clamp01(fleet.Supply + defines.FleetSupplyInPortPerDay * share);
                fleet.Organisation = M.Clamp01(fleet.Organisation + defines.FleetOrganisationInPortPerDay * share);
                fleet.Strength = M.Clamp01(fleet.Strength +
                    defines.FleetRepairInPortPerDay * share * fleet.Readiness);
            }
            else
            {
                // At sea a fleet burns stores, and how fast depends on how far from home it
                // is - the one bit of naval logistics this game keeps, because it is the one
                // a player can act on by taking a port. At anchor off a coast that is not a
                // friendly port it burns them just the same: there is nobody there to refill it.
                var reach = MathF.Max(1f, defines.FleetSupplyRangeDegrees);
                var fromHome = M.Clamp01(Distance(fleet.Latitude, fleet.Longitude,
                    fleet.HomeLatitude, fleet.HomeLongitude) / reach);
                fleet.Supply = M.Clamp01(fleet.Supply -
                    defines.FleetSupplyAtSeaPerDay * share * (0.45f + fromHome * 0.55f));
                fleet.Organisation = M.Clamp01(fleet.Organisation +
                    defines.FleetOrganisationAtSeaPerDay * share * fleet.Supply);
            }

            Sail(campaign, fleet);

            // A loaded fleet that has reached the coast it was sent to puts its army
            // ashore. Everything that then happens - a province changing hands, or a
            // battle - is the ordinary land model's business.
            if (fleet.Mission == FleetMission.Invasion && !fleet.HasOrder && fleet.TargetProvinceId > 0)
                NavalInvasion.Land(campaign, fleet, fleet.TargetProvinceId);
        }

        yield return 0;
        using (SimProfiler.Measure("    sea:actions")) ResolveActions(campaign);

        // A fleet with nothing left afloat is not a fleet. Whoever was aboard went down
        // with it.
        for (var index = campaign.Fleets.Count - 1; index >= 0; index--)
        {
            var fleet = campaign.Fleets[index];
            if (fleet.Hulls > 0) continue;
            foreach (var id in fleet.Carried)
            {
                var army = campaign.Armies.Find(candidate => candidate.Id == id);
                if (army is not null) Attrition.Destroy(campaign, army, "lost at sea");
            }
            fleet.Carried.Clear();
            campaign.Fleets.RemoveAt(index);
        }
    }

    // --- ports ------------------------------------------------------------------------

    /// <summary>
    /// Whether a country's ships may use a port held by another: its own, an ally's, a
    /// co-belligerent's, and those of the country it answers to or that answers to it.
    /// </summary>
    public static bool Friendly(Campaign campaign, int nationId, int holderId)
    {
        if (nationId == holderId) return true;
        var relation = CampaignDiplomacy.Peek(campaign, nationId, holderId);
        if (relation is not null && relation.Stance == DiplomaticStance.Allied) return true;
        var nation = campaign.NationById(nationId);
        var holder = campaign.NationById(holderId);
        if (nation is not null && nation.IsSubject && nation.OverlordId == holderId) return true;
        if (holder is not null && holder.IsSubject && holder.OverlordId == nationId) return true;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(nationId) || !war.Involves(holderId)) continue;
            if (!war.Opposes(nationId, holderId)) return true;
        }
        return false;
    }

    public static bool FriendlyPort(Campaign campaign, int nationId, int province)
    {
        var holder = HolderOf(campaign, province);
        return holder is not null && Friendly(campaign, nationId, holder.Id);
    }

    /// <summary>Who holds a province's ground now, rather than who it belonged to in 2024.</summary>
    public static Nation HolderOf(Campaign campaign, int province)
    {
        var map = campaign.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return null;
        var controllers = campaign.ProvinceController;
        var mapIndex = controllers is not null && province < controllers.Length
            ? controllers[province]
            : map.ProvinceToNation[province];
        return NationAt(campaign, mapIndex);
    }

    private static Campaign _indexed;
    private static Nation[] _byMapIndex = Array.Empty<Nation>();

    /// <summary>The country painted with a map index, or null for nobody.</summary>
    public static Nation NationAt(Campaign campaign, int mapIndex)
    {
        if (campaign is null || mapIndex <= 0) return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 1 || !ReferenceEquals(_indexed, campaign))
            {
                _indexed = campaign;
                var top = 0;
                foreach (var nation in campaign.Nations) top = Math.Max(top, nation.MapIndex);
                var index = new Nation[top + 1];
                foreach (var nation in campaign.Nations)
                    if (nation.MapIndex > 0) index[nation.MapIndex] = nation;
                _byMapIndex = index;
            }
            var found = mapIndex < _byMapIndex.Length ? _byMapIndex[mapIndex] : null;
            if (found is not null && found.MapIndex == mapIndex) return found;
        }
        return null;
    }

    /// <summary>
    /// The friendly port nearest a fleet, within the distance that counts as lying in it;
    /// -1 when there is none.
    /// </summary>
    public static int FindBerth(Campaign campaign, Fleet fleet)
    {
        var map = campaign.ProvinceMap;
        if (map is null)
            return Distance(fleet.Latitude, fleet.Longitude, fleet.HomeLatitude, fleet.HomeLongitude) <=
                   campaign.Defines.FleetBerthRangeDegrees ? Math.Max(1, fleet.HomeProvinceId) : -1;

        var range = campaign.Defines.FleetBerthRangeDegrees;
        var best = -1;
        var bestDistance = range;
        foreach (var province in SeaLanes.CoastalProvinces(map))
        {
            if (!SeaLanes.TryHarbourPoint(map, province, out var latitude, out var longitude)) continue;
            if (MathF.Abs(latitude - fleet.Latitude) > range) continue;
            var distance = Distance(fleet.Latitude, fleet.Longitude, latitude, longitude);
            if (distance > bestDistance) continue;
            if (!FriendlyPort(campaign, fleet.NationId, province)) continue;
            // Home first when home is within reach: a fleet lying in its own port is not
            // lying in the neighbour's.
            if (province == fleet.HomeProvinceId) return province;
            bestDistance = distance;
            best = province;
        }
        return best;
    }

    /// <summary>
    /// A fleet's home port was taken, or never existed: move it to the nearest port its own
    /// country still holds on the same sea, and send it there if it was lying at the old one.
    /// </summary>
    private static void KeepPort(Campaign campaign, Fleet fleet, Nation nation)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return;

        var home = HolderOf(campaign, fleet.HomeProvinceId);
        if (home is null || home.Id != nation.Id) Rehome(campaign, fleet, nation);

        if (fleet.BerthProvinceId > 0 && !FriendlyPort(campaign, fleet.NationId, fleet.BerthProvinceId))
        {
            // A port that falls is a port a fleet leaves.
            fleet.BerthProvinceId = -1;
            if (fleet.Mission == FleetMission.Anchored && !fleet.HasOrder) SendHome(campaign, fleet);
        }
        if (fleet.Mission == FleetMission.Anchored && !fleet.HasOrder && fleet.BerthProvinceId <= 0)
            fleet.BerthProvinceId = FindBerth(campaign, fleet);
    }

    public static bool Rehome(Campaign campaign, Fleet fleet, Nation nation)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return false;
        var basin = SeaLanes.BasinAt(map, fleet.Latitude, fleet.Longitude);
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var anySea = 0; anySea < 2 && best <= 0; anySea++)
        foreach (var province in SeaLanes.CoastalProvinces(map))
        {
            if (anySea == 0 && basin >= 0 && SeaLanes.HarbourBasin(map, province) != basin) continue;
            var holder = HolderOf(campaign, province);
            if (holder is null || holder.Id != nation.Id) continue;
            if (!SeaLanes.TryHarbourPoint(map, province, out var latitude, out var longitude)) continue;
            var distance = Distance(fleet.Latitude, fleet.Longitude, latitude, longitude);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = province;
        }
        if (best <= 0) return false;

        var wasHome = fleet.InPort && fleet.BerthProvinceId == fleet.HomeProvinceId;
        fleet.HomeProvinceId = best;
        SeaLanes.TryHarbourPoint(map, best, out fleet.HomeLatitude, out fleet.HomeLongitude);
        if (wasHome || (fleet.Mission == FleetMission.Anchored && fleet.HasOrder)) SendHome(campaign, fleet);
        return true;
    }

    /// <summary>Anchor, and sail home to do it. Plotted on the next step, like any order.</summary>
    public static void SendHome(Campaign campaign, Fleet fleet)
    {
        fleet.Mission = FleetMission.Anchored;
        fleet.TargetProvinceId = -1;
        fleet.HasOrder = true;
        fleet.OrderLatitude = fleet.HomeLatitude;
        fleet.OrderLongitude = fleet.HomeLongitude;
        fleet.BerthProvinceId = -1;
    }

    // --- sailing --------------------------------------------------------------------------

    /// <summary>
    /// Send a fleet somewhere by sea. False, and the fleet left exactly as it was, when the
    /// place cannot be reached: another ocean's lake, or behind a canal its holder has shut.
    /// </summary>
    public static bool SendTo(Campaign campaign, Fleet fleet, float latitude, float longitude)
    {
        var course = new List<float>();
        if (!SeaLanes.Plan(campaign, fleet.NationId, fleet.Latitude, fleet.Longitude, latitude, longitude,
                course, out _)) return false;
        fleet.HasOrder = true;
        fleet.OrderLatitude = latitude;
        fleet.OrderLongitude = longitude;
        fleet.Waypoints.Clear();
        fleet.Waypoints.AddRange(course);
        fleet.Routed = true;
        fleet.RoutedLatitude = latitude;
        fleet.RoutedLongitude = longitude;
        fleet.BerthProvinceId = -1;
        return true;
    }

    /// <summary>Degrees of sea still to sail, along the course rather than as the crow flies.</summary>
    public static float CourseLength(Fleet fleet)
    {
        if (!fleet.HasOrder) return 0f;
        if (!fleet.Routed || fleet.Waypoints.Count < 2)
            return Distance(fleet.Latitude, fleet.Longitude, fleet.OrderLatitude, fleet.OrderLongitude);
        var length = 0f;
        var latitude = fleet.Latitude;
        var longitude = fleet.Longitude;
        for (var index = 0; index + 1 < fleet.Waypoints.Count; index += 2)
        {
            length += Distance(latitude, longitude, fleet.Waypoints[index], fleet.Waypoints[index + 1]);
            latitude = fleet.Waypoints[index];
            longitude = fleet.Waypoints[index + 1];
        }
        return length;
    }

    /// <summary>Degrees a day at the fleet's present condition.</summary>
    public static float Speed(Campaign campaign, Fleet fleet) =>
        campaign.Defines.FleetDegreesPerDay *
        M.Clamp(0.35f + fleet.Supply * 0.65f, 0.35f, 1f) *
        M.Clamp(0.50f + fleet.Readiness * 0.50f, 0.5f, 1f) *
        NationalModifiers.Factor(campaign.NationById(fleet.NationId), "naval_speed");

    /// <summary>Whole days until it gets where it is going, or zero when it is not going anywhere.</summary>
    public static int DaysToGo(Campaign campaign, Fleet fleet) =>
        fleet.HasOrder ? (int)MathF.Ceiling(CourseLength(fleet) / MathF.Max(0.01f, Speed(campaign, fleet))) : 0;

    /// <summary>
    /// Follow the course, at a speed the fleet's condition sets. A course is legs of open
    /// water between waypoints, so a fleet goes round a continent rather than across it.
    /// </summary>
    private static void Sail(Campaign campaign, Fleet fleet)
    {
        if (!fleet.HasOrder) return;
        if (!fleet.Routed || fleet.RoutedLatitude != fleet.OrderLatitude ||
            fleet.RoutedLongitude != fleet.OrderLongitude || fleet.Waypoints.Count < 2)
        {
            var course = new List<float>();
            if (!SeaLanes.Plan(campaign, fleet.NationId, fleet.Latitude, fleet.Longitude,
                    fleet.OrderLatitude, fleet.OrderLongitude, course, out _))
            {
                AbandonOrder(fleet);
                return;
            }
            fleet.Waypoints.Clear();
            fleet.Waypoints.AddRange(course);
            fleet.Routed = true;
            fleet.RoutedLatitude = fleet.OrderLatitude;
            fleet.RoutedLongitude = fleet.OrderLongitude;
        }
        fleet.BerthProvinceId = -1;

        // Degrees a DAY, and this is a step - so a crossing that is written as sixty days
        // takes sixty days whatever the clock is set to.
        var step = Speed(campaign, fleet) * campaign.TickScale;
        while (step > 0f && fleet.Waypoints.Count >= 2)
        {
            var latitude = fleet.Waypoints[0];
            var longitude = fleet.Waypoints[1];
            var remaining = Distance(fleet.Latitude, fleet.Longitude, latitude, longitude);
            if (remaining <= step)
            {
                fleet.Latitude = latitude;
                fleet.Longitude = longitude;
                fleet.Waypoints.RemoveRange(0, 2);
                step -= remaining;
                continue;
            }
            var deltaLatitude = latitude - fleet.Latitude;
            var deltaLongitude = ShortestLongitude(longitude - fleet.Longitude);
            var scale = step / MathF.Max(0.0001f, remaining);
            fleet.Latitude = M.Clamp(fleet.Latitude + deltaLatitude * scale, -85f, 85f);
            fleet.Longitude = Wrap(fleet.Longitude + deltaLongitude * scale);
            step = 0f;
        }
        if (fleet.Waypoints.Count >= 2) return;

        fleet.HasOrder = false;
        fleet.Routed = false;
        fleet.Waypoints.Clear();
        Arrive(campaign, fleet);
    }

    private static void Arrive(Campaign campaign, Fleet fleet)
    {
        if (fleet.Mission != FleetMission.Anchored) return;
        fleet.BerthProvinceId = FindBerth(campaign, fleet);
        // Lying off its own home harbour it is in port, whatever the search found: the same rule
        // a loaded game applies (FleetHistory), or a save and its reload disagreed about it.
        if (fleet.BerthProvinceId <= 0 && fleet.HomeProvinceId > 0 &&
            Distance(fleet.Latitude, fleet.Longitude, fleet.HomeLatitude, fleet.HomeLongitude) <=
            campaign.Defines.FleetBerthRangeDegrees + 1.5f)
            fleet.BerthProvinceId = fleet.HomeProvinceId;
        // A fleet ordered into port while loaded lands its passengers there - in the port it
        // is lying in, not in whichever one it happens to call home.
        if (fleet.IsLoaded && fleet.BerthProvinceId > 0)
            NavalInvasion.Disembark(campaign, fleet, fleet.BerthProvinceId);
    }

    private static void AbandonOrder(Fleet fleet)
    {
        fleet.HasOrder = false;
        fleet.Routed = false;
        fleet.Waypoints.Clear();
        if (fleet.Mission != FleetMission.Invasion) return;
        // A beach that cannot be reached is not a beach; the troops stay aboard.
        fleet.Mission = FleetMission.Patrol;
        fleet.TargetProvinceId = -1;
    }

    // --- fighting -------------------------------------------------------------------------

    /// <summary>
    /// Fleets of countries at war that are close enough to see each other fight, on the
    /// same terms a land battle is fought on: organisation is what breaks, strength is what
    /// is lost, and the side that breaks goes home.
    /// </summary>
    private static void ResolveActions(Campaign campaign)
    {
        var defines = campaign.Defines;
        var fleets = campaign.Fleets;
        for (var index = 0; index < fleets.Count; index++)
        {
            var mine = fleets[index];
            if (mine.Hulls <= 0 || mine.ReadyDay > campaign.ElapsedDays || mine.InAction) continue;
            if (mine.Mission == FleetMission.Anchored) continue;

            for (var other = index + 1; other < fleets.Count; other++)
            {
                var theirs = fleets[other];
                if (theirs.Hulls <= 0 || theirs.InAction || theirs.NationId == mine.NationId) continue;
                if (theirs.ReadyDay > campaign.ElapsedDays) continue;
                if (theirs.Mission == FleetMission.Anchored) continue;
                if (Distance(mine.Latitude, mine.Longitude, theirs.Latitude, theirs.Longitude) >
                    defines.FleetContactRangeDegrees) continue;
                if (!AtWar(campaign, mine.NationId, theirs.NationId)) continue;

                Engage(campaign, mine, theirs);
                break;
            }
        }
    }

    private static void Engage(Campaign campaign, Fleet left, Fleet right)
    {
        var defines = campaign.Defines;
        left.InAction = true;
        right.InAction = true;
        left.LastActionDay = right.LastActionDay = campaign.ElapsedDays;

        // An action is fought over days, and this is one step of it: each step takes its
        // share of a day's losses. It used to take a whole day's, so at an hourly step two
        // fleets that met at breakfast had broken each other by lunch.
        var share = campaign.TickScale;

        // A fleet with an army aboard is a convoy: its decks are full, it is not free to
        // manoeuvre, and it is trying to get somewhere rather than to win. Catching one is
        // the whole reason a country keeps ships in its own waters, so it fights at a
        // penalty and what it is carrying is what it stands to lose.
        var leftPower = MathF.Max(0.01f, left.Power * (left.IsLoaded ? defines.FleetConvoyCombatPenalty : 1f));
        var rightPower = MathF.Max(0.01f, right.Power * (right.IsLoaded ? defines.FleetConvoyCombatPenalty : 1f));
        Damage(campaign, left, rightPower / leftPower, share);
        Damage(campaign, right, leftPower / rightPower, share);
        DrownPassengers(campaign, left, rightPower / leftPower, share);
        DrownPassengers(campaign, right, leftPower / rightPower, share);

        // Whoever's cohesion gives way breaks off and runs for home, and what was most
        // badly hit does not make it back.
        Withdraw(campaign, left);
        Withdraw(campaign, right);
    }

    private static void Damage(Campaign campaign, Fleet fleet, float pressure, float share)
    {
        var defines = campaign.Defines;
        var organisationLoss = defines.FleetOrganisationLossPerAction * M.Clamp(pressure, 0.25f, 4f) * share;
        fleet.Organisation = M.Clamp01(fleet.Organisation - organisationLoss);
        fleet.Strength = M.Clamp01(fleet.Strength -
            organisationLoss * defines.FleetLossesPerOrganisationPoint);
        fleet.Experience = M.Clamp01(fleet.Experience + defines.FleetExperiencePerAction * share);
    }

    /// <summary>
    /// Men go into the sea with the transports. An invasion caught on the water arrives
    /// weaker than it left, or does not arrive at all - which is what makes escorting a
    /// convoy worth a player's attention rather than a chore.
    /// </summary>
    private static void DrownPassengers(Campaign campaign, Fleet fleet, float pressure, float share)
    {
        if (!fleet.IsLoaded) return;
        var lost = campaign.Defines.InvasionLossesPerActionAtSea * M.Clamp(pressure, 0.25f, 4f) * share;
        if (lost <= 0f) return;
        foreach (var id in fleet.Carried)
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null) continue;
            army.Strength = M.Clamp01(army.Strength - lost);
            army.Organisation = M.Clamp01(army.Organisation - lost);
        }
    }

    private static void Withdraw(Campaign campaign, Fleet fleet)
    {
        if (fleet.Organisation > campaign.Defines.FleetBreakOrganisation) return;

        // The worst of the damage goes down. A fleet at ninety per cent strength is ninety
        // per cent of every ship, which is not how a battle ends: some are untouched and
        // some are on the bottom. Sinking the wrecks leaves the rest in better order.
        var wrecked = fleet.Hulls * (1f - fleet.Strength) * campaign.Defines.FleetHullsSunkOnBreak;
        var sunk = Math.Min(fleet.Hulls, (int)MathF.Round(wrecked));
        if (sunk > 0)
        {
            var afloat = fleet.Hulls * fleet.Strength;
            // Those aboard the hulls that went down went down with them.
            NavalInvasion.DrownWithHulls(campaign, fleet, sunk / (float)Math.Max(1, fleet.Hulls));
            fleet.Hulls -= sunk;
            fleet.HullsLost += sunk;
            fleet.Strength = fleet.Hulls > 0 ? M.Clamp01(afloat / fleet.Hulls) : 0f;
        }

        // An invasion that is broken at sea turns back with what is left of it.
        SendHome(campaign, fleet);
        fleet.ReadyDay = campaign.ElapsedDays + campaign.Defines.FleetRestAfterActionDays;
    }

    // --- what a navy is worth ----------------------------------------------------------------

    /// <summary>
    /// What a navy is worth to a country's trade this week. Raiders take from an enemy's
    /// merchant traffic, escorts protect one's own, and a blockade does both to whatever it
    /// is sitting on. This is where a navy stops being scenery: it is money.
    ///
    /// Where the enemy's ships are matters. A blockade is a blockade of the coast it is
    /// sitting on, not of every coast its enemy has; a raider hunts where merchant shipping
    /// runs, which is toward the victim's ports, and one in another ocean takes little. And a
    /// country with no coast has no merchant fleet to sink.
    /// </summary>
    public static void ApplyCommerceEffects(Campaign campaign, Nation nation, Breakdown trade)
    {
        if (!campaign.FeatureEnabled("navy")) return;
        var defines = campaign.Defines;
        var raided = 0f;
        var guarded = 0f;
        var blockaded = 0f;

        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.Hulls <= 0) continue;
            if (fleet.NationId == nation.Id)
            {
                if (fleet.Mission == FleetMission.Escort)
                    guarded += fleet.Presence * defines.FleetEscortValue;
                continue;
            }
            if (fleet.Mission != FleetMission.Raid && fleet.Mission != FleetMission.Blockade) continue;
            if (!AtWar(campaign, nation.Id, fleet.NationId)) continue;

            var reach = CoastReach(campaign, fleet, nation);
            if (fleet.Mission == FleetMission.Raid)
            {
                var range = MathF.Max(1f, defines.FleetRaidRangeDegrees);
                var found = reach <= range
                    ? 1f
                    : MathF.Max(defines.FleetRaidFloor, 1f - (reach - range) / (range * 2f));
                if (float.IsPositiveInfinity(reach)) found = 0f;
                raided += fleet.Presence * defines.FleetRaidValue * found;
            }
            else if (reach <= defines.FleetBlockadeRangeDegrees)
                blockaded += fleet.Presence * defines.FleetBlockadeValue;
        }

        var protection = M.Clamp01(guarded / MathF.Max(0.01f, raided + blockaded + guarded));
        var lost = (raided + blockaded) * (1f - protection) * nation.Gdp * defines.FleetCommerceShare;
        if (lost <= 0f) return;
        nation.WeeklyTradeBalance -= lost;
        trade?.Add("Shipping lost to enemy navies", -lost);
    }

    private static Campaign _reachesCampaign;
    private static int _reachesRevision = -1;
    private static readonly Dictionary<long, (float Latitude, float Longitude, int Week, float Reach)> Reaches = new();

    /// <summary>
    /// Degrees from a fleet to the nearest harbour a country holds; infinity for a country
    /// with no coast; zero on a campaign with no map, where every fleet is everywhere.
    /// </summary>
    public static float CoastReach(Campaign campaign, Fleet fleet, Nation nation)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return 0f;
        if (!ReferenceEquals(_reachesCampaign, campaign) || _reachesRevision != campaign.MapRevision)
        {
            Reaches.Clear();
            _reachesCampaign = campaign;
            _reachesRevision = campaign.MapRevision;
        }
        var key = ((long)fleet.Id << 32) | (uint)nation.MapIndex;
        var week = campaign.ElapsedDays / 7;
        if (Reaches.TryGetValue(key, out var known) && known.Week == week &&
            MathF.Abs(known.Latitude - fleet.Latitude) < 0.05f && MathF.Abs(known.Longitude - fleet.Longitude) < 0.05f)
            return known.Reach;

        var reach = float.PositiveInfinity;
        foreach (var province in SeaLanes.CoastalProvinces(map))
        {
            var holder = HolderOf(campaign, province);
            if (holder is null || holder.Id != nation.Id) continue;
            if (!SeaLanes.TryHarbourPoint(map, province, out var latitude, out var longitude)) continue;
            var distance = Distance(fleet.Latitude, fleet.Longitude, latitude, longitude);
            if (distance < reach) reach = distance;
        }
        if (Reaches.Count > 4096) Reaches.Clear();
        Reaches[key] = (fleet.Latitude, fleet.Longitude, week, reach);
        return reach;
    }

    /// <summary>Whether a province's coast is being sat on, for the supply that reaches it.</summary>
    public static float BlockadePressure(Campaign campaign, Nation owner, float latitude, float longitude)
    {
        if (!campaign.FeatureEnabled("navy")) return 0f;
        var defines = campaign.Defines;
        var pressure = 0f;
        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.Hulls <= 0 || fleet.Mission != FleetMission.Blockade) continue;
            if (fleet.NationId == owner.Id || !AtWar(campaign, owner.Id, fleet.NationId)) continue;
            if (Distance(fleet.Latitude, fleet.Longitude, latitude, longitude) > defines.FleetBlockadeRangeDegrees)
                continue;
            pressure += fleet.Presence * defines.FleetBlockadeValue;
        }
        return M.Clamp01(pressure);
    }

    private static bool AtWar(Campaign campaign, int left, int right)
    {
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            if (war.Opposes(left, right)) return true;
        }
        return false;
    }

    /// <summary>Degrees between two points, close enough at this scale and cheap.</summary>
    public static float Distance(float latitudeA, float longitudeA, float latitudeB, float longitudeB)
    {
        var deltaLatitude = latitudeA - latitudeB;
        var deltaLongitude = ShortestLongitude(longitudeA - longitudeB) *
                             MathF.Cos((latitudeA + latitudeB) * 0.5f * MathF.PI / 180f);
        return MathF.Sqrt(deltaLatitude * deltaLatitude + deltaLongitude * deltaLongitude);
    }

    private static float ShortestLongitude(float delta)
    {
        while (delta > 180f) delta -= 360f;
        while (delta < -180f) delta += 360f;
        return delta;
    }

    private static float Wrap(float longitude)
    {
        while (longitude > 180f) longitude -= 360f;
        while (longitude < -180f) longitude += 360f;
        return longitude;
    }
}

/// <summary>Send a fleet somewhere, and tell it what to do when it arrives.</summary>
public sealed class SetFleetOrderCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FleetId { get; init; }
    public FleetMission Mission { get; init; }

    /// <summary>Leave the destination unset to change the mission where the fleet already is.</summary>
    public bool HasDestination { get; init; }
    public float Latitude { get; init; }
    public float Longitude { get; init; }

    /// <summary>Why the order was not given, or empty when it was.</summary>
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var fleet = campaign.Fleets.Find(candidate => candidate.Id == FleetId);
        if (fleet is null || fleet.NationId != IssuerId) { Refusal = Loc.Get("That fleet is not yours to order."); return; }
        if (HasDestination)
        {
            if (!NavalOperations.SendTo(campaign, fleet, M.Clamp(Latitude, -85f, 85f), Longitude))
            {
                Refusal = Loc.Get("The fleet cannot reach that by sea.");
                return;
            }
            fleet.Mission = Mission;
            if (Mission != FleetMission.Invasion) fleet.TargetProvinceId = -1;
        }
        else if (Mission == FleetMission.Anchored)
        {
            // Ordering a fleet to anchor without saying where means "come home" - unless it
            // is already lying in a friendly port, where anchoring means staying put.
            if (fleet.BerthProvinceId > 0 && !fleet.HasOrder) fleet.Mission = FleetMission.Anchored;
            else NavalOperations.SendHome(campaign, fleet);
        }
        else
        {
            fleet.Mission = Mission;
            fleet.BerthProvinceId = -1;
        }
    }
}

public static class NavalBootstrap
{
    private readonly record struct Port(string Tag, string Name, int Hulls, float Latitude, float Longitude);

    /// <summary>
    /// The navies of 2024, as a compiled fallback. The shipped scenario reads them from
    /// history/units, the way every other catalogue in the game is read; this keeps a
    /// mod workspace with no data files bootable.
    /// </summary>
    private static readonly Port[] Ports =
    {
        new("USA", "Atlantic Fleet", 243, 36.9f, -75.4f), new("CHN", "Northern Theatre Fleet", 230, 35.6f, 121.5f),
        new("RUS", "Pacific Fleet", 115, 42.7f, 133.6f), new("JPN", "Maritime Defence Fleet", 102, 34.9f, 140.1f),
        new("KOR", "Republic of Korea Fleet", 82, 34.6f, 129.7f), new("PRK", "Korean East Sea Fleet", 54, 39.0f, 129.6f),
        new("GBR", "Home Fleet", 75, 50.2f, -3.1f), new("FRA", "Atlantic Naval Force", 72, 47.8f, -5.2f),
        new("IND", "Western Naval Command", 85, 18.5f, 71.8f), new("TUR", "Turkish Naval Forces", 48, 40.9f, 28.6f),
        new("ITA", "Marina Militare", 45, 39.8f, 17.3f), new("ESP", "Spanish Fleet", 32, 36.2f, -7.3f),
        new("AUS", "Royal Australian Fleet", 42, -33.8f, 152.1f), new("BRA", "Brazilian Fleet", 38, -23.2f, -42.7f),
        new("IDN", "Eastern Fleet Command", 55, -7.0f, 113.3f)
    };

    public static void Seed(Campaign campaign)
    {
        campaign.Fleets.Clear();
        var id = 0;
        foreach (var port in Ports)
        {
            var nation = campaign.NationByTag(port.Tag);
            if (nation is null) continue;
            campaign.Fleets.Add(new Fleet
            {
                Id = id++,
                NationId = nation.Id,
                Name = port.Name,
                Hulls = port.Hulls,
                Establishment = port.Hulls,
                Latitude = port.Latitude,
                Longitude = port.Longitude,
                HomeLatitude = port.Latitude,
                HomeLongitude = port.Longitude,
                Mission = FleetMission.Anchored
            });
        }
        FleetHistory.Apply(campaign);
    }
}
