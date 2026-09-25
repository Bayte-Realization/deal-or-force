using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// What a hull costs from keel to commissioning, in billions like every sum in the game,
    /// paid day by day while it is built. A frigate or a destroyer, averaged with everything
    /// smaller: about half again a division of line infantry.
    /// </summary>
    public float HullCost { get; set; } = 0.9f;

    /// <summary>Days a hull spends on the slipway.</summary>
    public float HullBuildDays { get; set; } = 540f;

    /// <summary>
    /// Slipways a country's naval yards have for every hull of the navy it began with. The
    /// fleet of 2024 was built somewhere, and those yards are still there.
    /// </summary>
    public float SlipwaysPerEstablishedHull { get; set; } = 0.06f;

    /// <summary>Slipways each shipyard adds - on the sea; a yard on a lake or a river adds its share.</summary>
    public float SlipwaysPerShipyard { get; set; } = 2f;

    /// <summary>The smallest navy an AI keeps, against the one it began with.</summary>
    public float NavalTargetFloor { get; set; } = 0.6f;

    /// <summary>The largest navy an AI builds toward, against the one it began with.</summary>
    public float NavalTargetCeiling { get; set; } = 1.4f;

    /// <summary>Days between one AI country's looks at the size of its navy.</summary>
    public int AiNavalReviewDays { get; set; } = 30;
}

/// <summary>
/// Building ships.
///
/// There was no way to. The fleets of 2024 were all the navy the world would ever have, so
/// a hull sunk in March was a hull gone for the rest of the century, a country's shipyards
/// added tonnage to a national total nothing read, and `NavalResponse` - "doubling the
/// budget roughly doubles the navy, eventually, at shipyard pace" - was a line in the
/// defines with no code behind it.
///
/// Now a country's yards are slipways: some for every hull of the navy it began with,
/// because that navy was built somewhere, and more for every shipyard it has put up since.
/// A hull on a slipway takes a year and a half and costs the treasury a share of its price
/// every day it is there; a treasury that cannot pay leaves the yards idle rather than
/// running up debt. Hulls are laid down for a fleet and join it when they are finished,
/// new and green - whole, but with nothing learned.
///
/// A country nobody plays builds toward the navy its budget says it keeps: the one it began
/// with, scaled by what it spends on its forces against what it spent in 2024, within a
/// floor and a ceiling. It never scraps what it has. A player orders hulls from the fleet
/// panel, five at a time, and can cancel what has not been finished.
/// </summary>
public static class Shipbuilding
{
    /// <summary>Days a hull takes for this country: the define, over its shipbuilding speed.</summary>
    public static float BuildDays(Campaign campaign, Nation nation) =>
        MathF.Max(1f, campaign.Defines.HullBuildDays) / NationalModifiers.Factor(nation, "shipbuilding_speed");

    private static readonly Dictionary<int, float> SlipwaysByNation = new();
    private static int _slipwaysDay = -1;
    private static Campaign _slipwaysCampaign;

    /// <summary>Once a day.</summary>
    public static void Tick(Campaign campaign)
    {
        Review(campaign);

        var anyOrdered = false;
        foreach (var fleet in campaign.Fleets)
            if (fleet.HullsOrdered > 0) { anyOrdered = true; break; }
        if (!anyOrdered) return;

        var defines = campaign.Defines;
        foreach (var nation in campaign.Nations)
        {
            var buildDays = BuildDays(campaign, nation);
            var ordered = 0;
            foreach (var fleet in campaign.Fleets)
                if (fleet.NationId == nation.Id && fleet.Hulls > 0) ordered += fleet.HullsOrdered;
            if (ordered <= 0) continue;

            var working = MathF.Min(Slipways(campaign, nation), ordered);
            if (working <= 0f) continue;
            var cost = working * defines.HullCost / buildDays;
            // A yard is paid as it works. One the treasury cannot pay stops for the day rather
            // than building on credit the country never voted.
            if (nation.Treasury < cost) continue;
            nation.Treasury -= cost;

            // The slipways go to the fleets in the order they were raised, as a navy's
            // yards go to whatever squadron was first to ask.
            foreach (var fleet in campaign.Fleets)
            {
                if (working <= 0f) break;
                if (fleet.NationId != nation.Id || fleet.HullsOrdered <= 0 || fleet.Hulls <= 0) continue;
                var slips = MathF.Min(working, fleet.HullsOrdered);
                working -= slips;
                fleet.HullProgress += slips / buildDays;
                while (fleet.HullProgress >= 1f && fleet.HullsOrdered > 0)
                {
                    fleet.HullProgress -= 1f;
                    Commission(fleet);
                }
                if (fleet.HullsOrdered == 0) fleet.HullProgress = 0f;
            }
        }
    }

    /// <summary>A new hull joins: whole, stored, and green.</summary>
    private static void Commission(Fleet fleet)
    {
        var hulls = MathF.Max(0, fleet.Hulls);
        fleet.Strength = (fleet.Strength * hulls + 1f) / (hulls + 1f);
        fleet.Supply = (fleet.Supply * hulls + 1f) / (hulls + 1f);
        fleet.Organisation = (fleet.Organisation * hulls + 1f) / (hulls + 1f);
        fleet.Experience = fleet.Experience * hulls / (hulls + 1f);
        fleet.Hulls++;
        fleet.HullsOrdered--;
    }

    /// <summary>
    /// How many hulls a country can have on the slipways at once: its old yards and its new
    /// ones. Worked out for every country once a day, from one pass over the regions.
    /// </summary>
    public static float Slipways(Campaign campaign, Nation nation)
    {
        if (!ReferenceEquals(_slipwaysCampaign, campaign) || _slipwaysDay != campaign.ElapsedDays)
        {
            _slipwaysCampaign = campaign;
            _slipwaysDay = campaign.ElapsedDays;
            SlipwaysByNation.Clear();
            var defines = campaign.Defines;
            foreach (var fleet in campaign.Fleets)
            {
                SlipwaysByNation[fleet.NationId] = SlipwaysByNation.GetValueOrDefault(fleet.NationId) +
                                                   fleet.Establishment * defines.SlipwaysPerEstablishedHull;
            }
            foreach (var state in campaign.States)
            {
                var yards = state.Buildings.GetValueOrDefault(BuildingKind.Shipyard);
                if (yards <= 0) continue;
                var owner = NavalOperations.NationAt(campaign, state.NationMapIndex);
                if (owner is null) continue;
                var yield = Waterfront.Yield(campaign, state.MapIndex, BuildingKind.Shipyard);
                SlipwaysByNation[owner.Id] = SlipwaysByNation.GetValueOrDefault(owner.Id) +
                                             yards * yield * defines.SlipwaysPerShipyard;
            }
        }
        return SlipwaysByNation.GetValueOrDefault(nation.Id);
    }

    /// <summary>What the yards are costing a country a day, at the hulls it has on them now.</summary>
    public static float DailyCost(Campaign campaign, Nation nation)
    {
        var ordered = 0;
        foreach (var fleet in campaign.Fleets)
            if (fleet.NationId == nation.Id && fleet.Hulls > 0) ordered += fleet.HullsOrdered;
        var working = MathF.Min(Slipways(campaign, nation), ordered);
        return working <= 0f ? 0f : working * campaign.Defines.HullCost / BuildDays(campaign, nation);
    }

    /// <summary>
    /// Days until the next hull joins this fleet at the pace its country's yards are working,
    /// or -1 when none is coming.
    /// </summary>
    public static int DaysToNextHull(Campaign campaign, Fleet fleet)
    {
        if (fleet.HullsOrdered <= 0) return -1;
        var nation = campaign.NationById(fleet.NationId);
        if (nation is null) return -1;
        var working = Slipways(campaign, nation);
        foreach (var other in campaign.Fleets)
        {
            if (other == fleet) break;
            if (other.NationId == nation.Id && other.Hulls > 0) working -= other.HullsOrdered;
        }
        var slips = MathF.Min(MathF.Max(0f, working), fleet.HullsOrdered);
        if (slips <= 0f) return -1;
        return (int)MathF.Ceiling((1f - fleet.HullProgress) * BuildDays(campaign, campaign.NationById(fleet.NationId)) / slips);
    }

    /// <summary>The navy an AI keeps for this fleet: what it began with, scaled by the budget.</summary>
    public static int TargetHulls(Campaign campaign, Nation nation, Fleet fleet)
    {
        var defines = campaign.Defines;
        var voted = nation.MilitarySpending / MathF.Max(0.0001f, defines.BaselineMilitarySpending);
        var scale = M.Clamp(MathF.Pow(MathF.Max(0.05f, voted), defines.NavalResponse),
            defines.NavalTargetFloor, defines.NavalTargetCeiling);
        return (int)MathF.Round(fleet.Establishment * scale);
    }

    /// <summary>
    /// An AI country looks at each of its fleets once a month and orders what it is short,
    /// or cancels what it no longer wants. Nothing already afloat is ever given up.
    /// </summary>
    private static void Review(Campaign campaign)
    {
        var interval = Math.Max(1, campaign.Defines.AiNavalReviewDays);
        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.Hulls <= 0 || fleet.Establishment <= 0) continue;
            if ((campaign.ElapsedDays + fleet.Id * 7) % interval != 0) continue;
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null || nation.Population <= 0) continue;
            if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Navy)) continue;

            var target = TargetHulls(campaign, nation, fleet);
            var planned = fleet.Hulls + fleet.HullsOrdered;
            if (planned < target) fleet.HullsOrdered += target - planned;
            else if (fleet.HullsOrdered > 0 && planned > target)
                fleet.HullsOrdered = Math.Max(0, fleet.HullsOrdered - (planned - target));
            if (fleet.HullsOrdered == 0) fleet.HullProgress = 0f;
        }
    }
}

/// <summary>Lay hulls down for a fleet, or with a negative count, cancel ones not yet finished.</summary>
public sealed class OrderHullsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FleetId { get; init; }
    public int Count { get; init; }

    /// <summary>Why nothing was ordered, or empty when it was.</summary>
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        var fleet = campaign.Fleets.Find(candidate => candidate.Id == FleetId);
        var nation = campaign.NationById(IssuerId);
        if (fleet is null || nation is null || fleet.NationId != IssuerId) { Refusal = Loc.Get("That fleet is not yours to order."); return; }
        if (Count == 0) { Refusal = Loc.Get("Nothing to order."); return; }
        if (Count > 0 && Shipbuilding.Slipways(campaign, nation) <= 0f)
        {
            Refusal = Loc.Get("Your country has no naval yards. Build a shipyard on the coast first.");
            return;
        }
        fleet.HullsOrdered = Math.Clamp(fleet.HullsOrdered + Count, 0, 500);
        if (fleet.HullsOrdered == 0) fleet.HullProgress = 0f;
    }
}
