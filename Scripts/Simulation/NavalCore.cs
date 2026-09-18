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

    public bool IsMoving => HasOrder;

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
    /// the province it stands in.
    /// </summary>
    public float Presence => Hulls * (0.80f + Experience * 0.30f) * (0.60f + Readiness * 0.40f);
}

public static class NavalOperations
{
    private static readonly List<Fleet> Engaged = new();

    public static void Tick(Campaign campaign)
    {
        var defines = campaign.Defines;
        foreach (var fleet in campaign.Fleets) fleet.InAction = false;

        foreach (var fleet in campaign.Fleets)
        {
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null || fleet.Hulls <= 0) continue;

            // Readiness is what the treasury reaches the fleet with, and it moves slowly:
            // a navy is not re-crewed by a budget line in a fortnight.
            var funded = M.Clamp(nation.MilitarySpending /
                MathF.Max(0.01f, defines.BaselineMilitarySpending), 0.35f, 1f);
            fleet.Readiness = M.Lerp(fleet.Readiness, funded, defines.FleetReadinessDrift);

            var inPort = fleet.Mission == FleetMission.Anchored && !fleet.HasOrder;
            if (fleet.Hulls > 0 && fleet.Carried.Count > 0 && fleet.Mission != FleetMission.Invasion &&
                !fleet.HasOrder)
            {
                // A fleet ordered home while loaded lands its passengers where it stops.
                var port = fleet.HomeProvinceId;
                if (fleet.Mission == FleetMission.Anchored && port > 0) NavalInvasion.Disembark(campaign, fleet, port);
            }
            // Everything below is a rate per day and this runs per step, so each takes the
            // step's share. Without it a fleet at an hourly step resupplied, repaired and
            // recovered its order twenty-four times faster than the number beside it says.
            var share = campaign.TickScale;
            if (inPort)
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
                // a player can act on by taking a port.
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

        ResolveActions(campaign);
    }

    /// <summary>
    /// Move toward the order, at a speed the fleet's condition sets. A fleet sails a
    /// straight line over the globe rather than through a graph of sea provinces: this map
    /// has no sea provinces, and inventing thirty thousand of them to move fifteen fleets
    /// would be the tail wagging the dog.
    /// </summary>
    private static void Sail(Campaign campaign, Fleet fleet)
    {
        if (!fleet.HasOrder) return;
        var defines = campaign.Defines;
        var remaining = Distance(fleet.Latitude, fleet.Longitude, fleet.OrderLatitude, fleet.OrderLongitude);
        // Degrees a DAY, and this is a step - so a crossing that is written as sixty days
        // takes sixty days whatever the clock is set to.
        var step = defines.FleetDegreesPerDay * campaign.TickScale *
                   M.Clamp(0.35f + fleet.Supply * 0.65f, 0.35f, 1f) *
                   M.Clamp(0.50f + fleet.Readiness * 0.50f, 0.5f, 1f);
        if (remaining <= step)
        {
            fleet.Latitude = fleet.OrderLatitude;
            fleet.Longitude = fleet.OrderLongitude;
            fleet.HasOrder = false;
            return;
        }

        var deltaLatitude = fleet.OrderLatitude - fleet.Latitude;
        var deltaLongitude = ShortestLongitude(fleet.OrderLongitude - fleet.Longitude);
        var scale = step / MathF.Max(0.0001f, remaining);
        fleet.Latitude = M.Clamp(fleet.Latitude + deltaLatitude * scale, -85f, 85f);
        fleet.Longitude = Wrap(fleet.Longitude + deltaLongitude * scale);
    }

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
                if (!AtWar(campaign, mine.NationId, theirs.NationId)) continue;
                if (Distance(mine.Latitude, mine.Longitude, theirs.Latitude, theirs.Longitude) >
                    defines.FleetContactRangeDegrees) continue;

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

        // A fleet with an army aboard is a convoy: its decks are full, it is not free to
        // manoeuvre, and it is trying to get somewhere rather than to win. Catching one is
        // the whole reason a country keeps ships in its own waters, so it fights at a
        // penalty and what it is carrying is what it stands to lose.
        var leftPower = MathF.Max(0.01f, left.Power * (left.IsLoaded ? defines.FleetConvoyCombatPenalty : 1f));
        var rightPower = MathF.Max(0.01f, right.Power * (right.IsLoaded ? defines.FleetConvoyCombatPenalty : 1f));
        Damage(campaign, left, rightPower / leftPower);
        Damage(campaign, right, leftPower / rightPower);
        DrownPassengers(campaign, left, rightPower / leftPower);
        DrownPassengers(campaign, right, leftPower / rightPower);

        // Whoever's cohesion gives way breaks off and runs for home. Nothing is sunk
        // outright: a beaten fleet is a fleet that has to be repaired, which is a cost the
        // player feels for months rather than a number that vanishes.
        Withdraw(campaign, left, right);
        Withdraw(campaign, right, left);
    }

    private static void Damage(Campaign campaign, Fleet fleet, float pressure)
    {
        var defines = campaign.Defines;
        var organisationLoss = defines.FleetOrganisationLossPerAction * M.Clamp(pressure, 0.25f, 4f);
        fleet.Organisation = M.Clamp01(fleet.Organisation - organisationLoss);
        fleet.Strength = M.Clamp01(fleet.Strength -
            organisationLoss * defines.FleetLossesPerOrganisationPoint);
        fleet.Experience = M.Clamp01(fleet.Experience + defines.FleetExperiencePerAction);
    }

    /// <summary>
    /// Men go into the sea with the transports. An invasion caught on the water arrives
    /// weaker than it left, or does not arrive at all - which is what makes escorting a
    /// convoy worth a player's attention rather than a chore.
    /// </summary>
    private static void DrownPassengers(Campaign campaign, Fleet fleet, float pressure)
    {
        if (!fleet.IsLoaded) return;
        var lost = campaign.Defines.InvasionLossesPerActionAtSea * M.Clamp(pressure, 0.25f, 4f);
        if (lost <= 0f) return;
        foreach (var id in fleet.Carried)
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null) continue;
            army.Strength = M.Clamp01(army.Strength - lost);
            army.Organisation = M.Clamp01(army.Organisation - lost);
        }
    }

    private static void Withdraw(Campaign campaign, Fleet fleet, Fleet enemy)
    {
        if (fleet.Organisation > campaign.Defines.FleetBreakOrganisation) return;
        // An invasion that is broken at sea turns back with what is left of it.
        fleet.TargetProvinceId = -1;
        fleet.Mission = FleetMission.Anchored;
        fleet.HasOrder = true;
        fleet.OrderLatitude = fleet.HomeLatitude;
        fleet.OrderLongitude = fleet.HomeLongitude;
        fleet.ReadyDay = campaign.ElapsedDays + campaign.Defines.FleetRestAfterActionDays;
    }

    /// <summary>
    /// What a navy is worth to a country's trade this week. Raiders take from an enemy's
    /// merchant traffic, escorts protect one's own, and a blockade does both to whatever it
    /// is sitting on. This is where a navy stops being scenery: it is money.
    /// </summary>
    public static void ApplyCommerceEffects(Campaign campaign, Nation nation, Breakdown trade)
    {
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
            if (fleet.Mission == FleetMission.Raid) raided += fleet.Presence * defines.FleetRaidValue;
            else blockaded += fleet.Presence * defines.FleetBlockadeValue;
        }

        var protection = M.Clamp01(guarded / MathF.Max(0.01f, raided + blockaded + guarded));
        var lost = (raided + blockaded) * (1f - protection) * nation.Gdp * defines.FleetCommerceShare;
        if (lost <= 0f) return;
        nation.WeeklyTradeBalance -= lost;
        trade?.Add("Shipping lost to enemy navies", -lost);
    }

    /// <summary>Whether a province's coast is being sat on, for the supply that reaches it.</summary>
    public static float BlockadePressure(Campaign campaign, Nation owner, float latitude, float longitude)
    {
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

    public void Apply(Campaign campaign)
    {
        var fleet = campaign.Fleets.Find(candidate => candidate.Id == FleetId);
        if (fleet is null || fleet.NationId != IssuerId) return;
        fleet.Mission = Mission;
        if (HasDestination)
        {
            fleet.HasOrder = true;
            fleet.OrderLatitude = M.Clamp(Latitude, -85f, 85f);
            fleet.OrderLongitude = Longitude;
        }
        else if (Mission == FleetMission.Anchored)
        {
            // Ordering a fleet to anchor without saying where means "come home".
            fleet.HasOrder = true;
            fleet.OrderLatitude = fleet.HomeLatitude;
            fleet.OrderLongitude = fleet.HomeLongitude;
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
