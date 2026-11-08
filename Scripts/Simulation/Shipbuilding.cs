using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// The navy a country's economy can carry, in hulls per square root of its GDP (billions):
    /// 2.5 gives the United States and China about the fleets they have, Turkey about fifty and
    /// a country of twenty billion about ten. An AI country with a coast and no fleet founds one
    /// towards a quarter of it; a navy below it grows by a hull a quarter. 0 turns both off.
    /// </summary>
    public float AiNavalAmbition { get; set; } = 2.5f;
    /// <summary>The smallest economy (billions) that founds a navy of its own.</summary>
    public float AiNavyFoundingGdp { get; set; } = 15f;
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
    /// <summary>Slipways a port adds, against Defines.SlipwaysPerShipyard for a yard: a quay can fit out a hull, not lay one down.</summary>
    public const float SlipwaysPerPort = 0.35f;

    /// <summary>Days a hull takes for this country: the define, over its shipbuilding speed.</summary>
    public static float BuildDays(Campaign campaign, Nation nation) =>
        MathF.Max(1f, campaign.Defines.HullBuildDays) / NationalModifiers.Factor(nation, "shipbuilding_speed");

    private static readonly Dictionary<int, float> SlipwaysByNation = new();
    private static int _slipwaysDay = -1;
    private static Campaign _slipwaysCampaign;

    /// <summary>Once a day.</summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(Shipbuilding))) return;
        Review(campaign);
        Ambitions(campaign);

        var anyOrdered = false;
        foreach (var fleet in campaign.Fleets)
            if (fleet.HullsOrdered > 0) { anyOrdered = true; break; }
        if (!anyOrdered) return;

        var defines = campaign.Defines;
        foreach (var nation in campaign.Nations)
        {
            var baseBuildDays = BuildDays(campaign, nation);
            var ordered = 0;
            var highestMode = MobilisationMode.Standard;
            foreach (var fleet in campaign.Fleets)
                if (fleet.NationId == nation.Id && fleet.Hulls > 0 && fleet.HullsOrdered > 0)
                {
                    ordered += fleet.HullsOrdered;
                    if (fleet.HullOrderMode > highestMode) highestMode = fleet.HullOrderMode;
                }
            if (ordered <= 0) continue;

            var slipMultiplier = highestMode switch
            {
                MobilisationMode.RapidCallUp => 2.2f,
                MobilisationMode.TotalMobilisation => 4.5f,
                _ => 1f
            };
            var timeFactor = highestMode switch
            {
                MobilisationMode.RapidCallUp => 0.40f,
                MobilisationMode.TotalMobilisation => 0.16f,
                _ => 1f
            };
            var costFactor = highestMode switch
            {
                MobilisationMode.RapidCallUp => 1.35f,
                MobilisationMode.TotalMobilisation => 1.75f,
                _ => 1f
            };
            var buildDays = MathF.Max(14f, baseBuildDays * timeFactor);
            var working = MathF.Min(Slipways(campaign, nation, highestMode) * slipMultiplier, ordered);
            if (working <= 0f) continue;
            var cost = working * (defines.HullCost * costFactor) / buildDays;
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
                if (fleet.HullsOrdered == 0)
                {
                    fleet.HullProgress = 0f;
                    fleet.HullOrderMode = MobilisationMode.Standard;
                }
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
    public static float Slipways(Campaign campaign, Nation nation, MobilisationMode mode = MobilisationMode.Standard)
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
                var ports = state.Buildings.GetValueOrDefault(BuildingKind.Port);
                if (yards <= 0 && ports <= 0) continue;
                var owner = NavalOperations.NationAt(campaign, state.NationMapIndex);
                if (owner is null) continue;
                var yield = Waterfront.Yield(campaign, state.MapIndex, BuildingKind.Shipyard);
                SlipwaysByNation[owner.Id] = SlipwaysByNation.GetValueOrDefault(owner.Id) +
                                             yards * yield * defines.SlipwaysPerShipyard +
                                             ports * yield * SlipwaysPerPort;
            }
        }
        var baseSlips = SlipwaysByNation.GetValueOrDefault(nation.Id);
        if (mode != MobilisationMode.Standard)
        {
            if (baseSlips <= 0f && campaign?.ProvinceMap is not null)
            {
                // Emergency civilian port requisition if the country controls any sea coast
                foreach (var coast in SeaLanes.CoastalProvinces(campaign.ProvinceMap))
                    if (campaign.ProvinceController[coast] == nation.MapIndex)
                        return mode == MobilisationMode.TotalMobilisation ? 4f : 2f;
            }
            else if (baseSlips > 0f)
            {
                var factor = mode == MobilisationMode.TotalMobilisation ? 2.2f : 1.4f;
                return baseSlips * factor;
            }
        }
        return baseSlips;
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

    /// <summary>The hulls this country's economy can carry (AiNavalAmbition).</summary>
    public static int Ambition(Campaign campaign, Nation nation) =>
        (int)MathF.Round(MathF.Max(0f, campaign.Defines.AiNavalAmbition) * MathF.Sqrt(MathF.Max(0f, nation.Gdp)));

    /// <summary>
    /// Once a quarter for each AI country: a country with a coast and an economy but no navy
    /// founds one in its own harbour - a country born after the campaign began starts with none,
    /// and the old rule only ever kept the fleets of 2024 - and a navy smaller than its economy
    /// can carry grows by a hull. The same for everyone; the budget still scales the result.
    /// </summary>
    private static void Ambitions(Campaign campaign)
    {
        var defines = campaign.Defines;
        if (!campaign.StepOpensNewDay || defines.AiNavalAmbition <= 0f || campaign.ProvinceMap is null) return;
        var quarter = Math.Max(1, defines.AiNavalReviewDays) * 3;
        foreach (var nation in campaign.Nations)
        {
            if ((campaign.ElapsedDays + nation.Id * 11) % quarter != 0) continue;
            if (nation.Movement is not null || nation.Population <= 0 || nation.IsHuman || nation.Gdp < defines.AiNavyFoundingGdp) continue;
            var ambition = Ambition(campaign, nation);
            var fleets = campaign.Fleets.Where(f => f.NationId == nation.Id).ToList();
            if (fleets.Count == 0)
            {
                var hulls = Math.Clamp(ambition / 4, 2, 8);
                if (nation.Treasury < defines.HullCost * hulls * 2f) continue;
                var order = new CommissionNewFleetCommand { IssuerId = nation.Id, ProvinceId = -1, Hulls = hulls, Mode = MobilisationMode.Standard };
                order.Apply(campaign);
                if (order.Refusal.Length == 0)
                    DiagnosticLogs.Write("simulation", $"NAVY_FOUNDED {nation.Tag} hulls={hulls} ambition={ambition}");
                continue;
            }
            var establishment = fleets.Sum(f => f.Establishment);
            if (establishment >= ambition) continue;
            fleets.OrderByDescending(f => f.Establishment).ThenBy(f => f.Id).First().Establishment++;
        }
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
            // A fleet sunk to the last hull is rebuilt, not given up: it keeps its establishment.
            if (fleet.Establishment <= 0) continue;
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
    public MobilisationMode Mode { get; init; } = MobilisationMode.Standard;

    /// <summary>Why nothing was ordered, or empty when it was.</summary>
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var fleet = campaign.Fleets.Find(candidate => candidate.Id == FleetId);
        var nation = campaign.NationById(IssuerId);
        if (fleet is null || nation is null || fleet.NationId != IssuerId) { Refusal = Loc.Get("That fleet is not yours to order."); return; }
        if (Count == 0) { Refusal = Loc.Get("Nothing to order."); return; }
        if (Count > 0 && Shipbuilding.Slipways(campaign, nation, Mode) <= 0f)
        {
            Refusal = Loc.Get("Your country has no naval yards. Build a shipyard on the coast first.");
            return;
        }
        if (Count > 0)
        {
            var bill = StrategicStockpiles.HullMaterialBill(Count, Mode);
            StrategicStockpiles.ConsumeBill(campaign, nation, bill);
            if (Mode == MobilisationMode.RapidCallUp)
                nation.Stability = M.Clamp(nation.Stability - 0.0015f * Count, 0.05f, 1f);
            else if (Mode == MobilisationMode.TotalMobilisation)
                nation.Stability = M.Clamp(nation.Stability - 0.003f * Count, 0.05f, 1f);
        }
        if (Count > 0 && Mode > fleet.HullOrderMode) fleet.HullOrderMode = Mode;
        fleet.HullsOrdered = Math.Clamp(fleet.HullsOrdered + Count, 0, 2000);
        if (fleet.HullsOrdered == 0)
        {
            fleet.HullProgress = 0f;
            fleet.HullOrderMode = MobilisationMode.Standard;
        }
        campaign.MarkStrategicChange();
    }
}

/// <summary>Commission a brand-new squadron in any controlled coastal harbour.</summary>
public sealed class CommissionNewFleetCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ProvinceId { get; init; }
    public int Hulls { get; init; } = 5;
    public MobilisationMode Mode { get; init; } = MobilisationMode.RapidCallUp;
    public string CustomName { get; init; } = "";
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign?.NationById(IssuerId);
        var map = campaign?.ProvinceMap;
        if (nation is null || map is null || Hulls < 1) { Refusal = Loc.Get("Invalid naval order."); return; }
        var portProvince = ProvinceId;
        if (portProvince <= 0 || SeaLanes.HarbourOf(map, portProvince) < 0 || campaign.ProvinceController[portProvince] != nation.MapIndex)
        {
            portProvince = -1;
            foreach (var coast in SeaLanes.CoastalProvinces(map))
                if (campaign.ProvinceController[coast] == nation.MapIndex && SeaLanes.HarbourOf(map, coast) >= 0)
                { portProvince = coast; break; }
        }
        if (portProvince <= 0)
        {
            Refusal = Loc.Get("No controlled coastal harbour available to commission a new fleet.");
            return;
        }
        var upFrontCost = campaign.Defines.HullCost * Hulls * (Mode == MobilisationMode.TotalMobilisation ? 1.5f : 1.15f);
        if (nation.Treasury < upFrontCost)
        {
            Refusal = Loc.Format("Requires {0} in treasury to commission a new squadron.", Fmt.Money(upFrontCost));
            return;
        }
        nation.Treasury -= upFrontCost;
        var bill = StrategicStockpiles.HullMaterialBill(Hulls, Mode);
        StrategicStockpiles.ConsumeBill(campaign, nation, bill);
        if (Mode == MobilisationMode.TotalMobilisation)
            nation.Stability = M.Clamp(nation.Stability - 0.003f * Hulls, 0.05f, 1f);

        SeaLanes.TryHarbourPoint(map, portProvince, out var lat, out var lon);
        var nextId = campaign.Fleets.Count == 0 ? 1 : campaign.Fleets.Max(f => f.Id) + 1;
        var countOwned = campaign.Fleets.Count(f => f.NationId == nation.Id) + 1;
        var fleet = new Fleet
        {
            Id = nextId,
            NationId = nation.Id,
            Name = string.IsNullOrWhiteSpace(CustomName) ? $"{countOwned}. {nation.Tag} Squadron" : CustomName.Trim(),
            Hulls = Mode == MobilisationMode.TotalMobilisation ? Math.Max(1, Hulls / 2) : 1,
            HullsOrdered = Mode == MobilisationMode.TotalMobilisation ? Hulls - Math.Max(1, Hulls / 2) : Hulls - 1,
            HullOrderMode = Mode,
            Establishment = Hulls,
            HomeProvinceId = portProvince,
            BerthProvinceId = portProvince,
            HomeLatitude = lat,
            HomeLongitude = lon,
            Latitude = lat,
            Longitude = lon,
            Strength = 0.85f,
            Organisation = 0.70f,
            Supply = 0.90f,
            Readiness = 0.85f,
            Mission = FleetMission.Anchored
        };
        campaign.Fleets.Add(fleet);
        campaign.MarkStrategicChange();
    }
}
