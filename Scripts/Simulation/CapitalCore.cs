using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>The day this government last moved of its own choosing; far in the past if never.</summary>
    public int CapitalMovedDay = -100_000;

    /// <summary>
    /// The province the capital stands in, or 0 before the map has placed it. The region is
    /// not enough on its own: a peace can cut a region in two, give the city to the winner and
    /// leave the country the rest under the same name.
    /// </summary>
    public int CapitalProvinceId;

    /// <summary>
    /// The temporary wartime government seat; 0 uses the legal capital, -1 means none is
    /// usable. Occupation must not move the legal capital a war or civil-war victory is about.
    /// </summary>
    public int GovernmentSeatProvinceId;
}

public sealed partial class Campaign
{
    /// <summary>
    /// Bumped whenever a capital moves, so the globe knows to put its markers back where the
    /// capitals are. Not saved: a loaded campaign draws its markers from scratch anyway.
    /// </summary>
    public int CapitalRevision { get; internal set; }
}

/// <summary>
/// Where a country is governed from, and how that changes.
///
/// A capital was a name and a point on the globe, fixed for the whole campaign. Two things
/// followed from that. A player could not move theirs - item 64 on the list, and the oldest
/// decision a government under threat makes: Kuibyshev in 1941, Bordeaux in 1940, Brasília
/// and Astana and Nusantara in peacetime. And a country that lost the region its capital
/// stood in went on being governed, for the rest of the game, from a city it no longer
/// had - with its marker standing in somebody else's country, which is half of item 40.
///
/// Both are one operation here. A player moves the capital to a region of their own that
/// they hold, for money, for a little of the country's settledness, and not again for a
/// while. Permanent cession moves its legal capital; occupation evacuates its government
/// temporarily, preserving the legal capital for war score and civil-war victory rules.
/// Only a country with no owned land left loses its capital and its field army.
///
/// What a capital is FOR is unchanged: StabilityCore weighs its region far above its size,
/// and that is exactly what makes moving it a decision rather than a formality.
/// </summary>
public static class CapitalCore
{
    /// <summary>A landless government returning home needs a new seat, not its old overseas office.</summary>
    internal static void Establish(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (campaign is null || nation is null || state is null || state.NationMapIndex != nation.MapIndex) return;
        var seat = Seat(campaign, nation, state);
        if (campaign.ProvinceMap is not null && seat <= 0) return;
        Place(campaign, nation, state, seat);
    }

    /// <summary>What moving the government costs this country, in the treasury's money.</summary>
    public static float Cost(Campaign campaign, Nation nation) =>
        MathF.Max(campaign.Defines.CapitalMoveCostFloor, nation.Gdp * campaign.Defines.CapitalMoveCostShare);

    /// <summary>Why the capital cannot move here now, or empty when it can.</summary>
    public static string Refusal(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (campaign is null || nation is null || state is null) return "Choose a region.";
        if (state.NationMapIndex != nation.MapIndex) return "Only a region we own can hold the capital.";
        if (state.MapIndex == nation.CapitalStateMapIndex) return "The capital is already here.";
        if (campaign.ProvinceMap is not null && Seat(campaign, nation, state) <= 0)
            return "None of this region's ground is ours to govern from.";
        if (StabilityCore.OccupiedShare(campaign, state, nation) > 0.5f)
            return "Most of this region is occupied; a government cannot sit there.";
        var wait = nation.CapitalMovedDay + campaign.Defines.CapitalMoveIntervalDays - campaign.ElapsedDays;
        if (wait > 0) return Loc.Format("The government moved recently; it can move again in {0} day(s).", wait);
        var cost = Cost(campaign, nation);
        if (nation.Treasury < cost)
            return Loc.Format("Moving the government costs {0:0.00}; the treasury holds {1:0.00}.", cost, nation.Treasury);
        return "";
    }

    /// <summary>Move the government by choice: paid for, and felt.</summary>
    public static bool Move(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (Refusal(campaign, nation, state).Length > 0) return false;
        nation.Treasury -= Cost(campaign, nation);
        var limit = StabilityCore.Weights.GovernanceLimit;
        nation.StabilityGovernance = M.Clamp(
            nation.StabilityGovernance - campaign.Defines.CapitalMoveStabilityCost, -limit, limit);
        nation.CapitalMovedDay = campaign.ElapsedDays;
        Place(campaign, nation, state, Seat(campaign, nation, state));
        DiagnosticLogs.Write("simulation",
            $"CAPITAL_MOVED nation={nation.Tag} to={state.Id} day={campaign.ElapsedDays} by=choice");
        return true;
    }

    /// <summary>
    /// Follow permanent ownership and temporary control separately. A usable emergency
    /// seat stays put until the legal capital is safe or that seat is lost too. The actual
    /// province owner wins over stale or split region metadata.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(CapitalCore))) return;
        var topology = campaign?.ProvinceMap;
        if (topology is null) return;
        var regions = new Dictionary<int, ScenarioState>();
        foreach (var state in campaign.States) regions[state.MapIndex] = state;
        Dictionary<int, HashSet<int>> availableByOwner = null;
        HashSet<int> landOwners = null;
        void SurveyGround()
        {
            if (availableByOwner is not null) return;
            availableByOwner = new Dictionary<int, HashSet<int>>();
            landOwners = new HashSet<int>();
            var controllers = campaign.ProvinceController;
            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                int owner = topology.ProvinceToNation[province];
                if (owner <= 0 || topology.IsWater(province) || campaign.DestroyedProvinces.Contains(province)) continue;
                landOwners.Add(owner);
                if (controllers is not null && (province >= controllers.Length || controllers[province] != owner)) continue;
                if (!availableByOwner.TryGetValue(owner, out var held)) availableByOwner[owner] = held = new HashSet<int>();
                held.Add(topology.ProvinceToState[province]);
            }
        }
        foreach (var nation in campaign.Nations)
        {
            // A movement or a country never represented on this raster has no placed seat
            // to lose. Its field units are governed by its own lifecycle.
            if (nation.CapitalStateMapIndex <= 0 && nation.CapitalProvinceId <= 0 &&
                nation.GovernmentSeatProvinceId <= 0) continue;
            if (Standing(campaign, nation))
            {
                if (Usable(campaign, nation, nation.CapitalProvinceId, regions))
                {
                    if (nation.GovernmentSeatProvinceId > 0)
                    {
                        nation.WarSupport = MathF.Min(1.0f, nation.WarSupport + 0.12f);
                        nation.StabilityGovernance = MathF.Min(StabilityCore.Weights.GovernanceLimit, nation.StabilityGovernance + 0.10f);
                        DiagnosticLogs.Write("simulation",
                            $"CAPITAL_LIBERATED nation={nation.Tag} capital={nation.CapitalName} day={campaign.ElapsedDays}");
                    }
                    SetGovernmentSeat(campaign, nation, 0);
                    continue;
                }
                if (Usable(campaign, nation, nation.GovernmentSeatProvinceId, regions)) continue;
                SurveyGround();
                BestSeat(campaign, nation, availableByOwner.GetValueOrDefault(nation.MapIndex), out _, out var temporarySeat);
                var prevSeat = nation.GovernmentSeatProvinceId;
                SetGovernmentSeat(campaign, nation, temporarySeat > 0 ? temporarySeat : -1);
                if (prevSeat == 0 && temporarySeat > 0)
                {
                    nation.WarSupport = MathF.Max(0.05f, nation.WarSupport - 0.15f);
                    nation.StabilityGovernance = MathF.Max(-StabilityCore.Weights.GovernanceLimit, nation.StabilityGovernance - 0.12f);
                    DiagnosticLogs.Write("simulation",
                        $"CAPITAL_EVACUATED nation={nation.Tag} from={nation.CapitalName} to={EffectiveName(campaign, nation)} day={campaign.ElapsedDays}");
                }
                continue;
            }

            SurveyGround();
            BestSeat(campaign, nation, availableByOwner.GetValueOrDefault(nation.MapIndex), out var best, out var seat);
            if (best is null)
            {
                // Total occupation is not extinction. Armies abroad and liberation remain
                // possible while any owned land survives, even a fragment of a foreign region.
                if (landOwners.Contains(nation.MapIndex))
                {
                    SetGovernmentSeat(campaign, nation, -1);
                    continue;
                }
                SetGovernmentSeat(campaign, nation, 0);
                nation.CapitalStateMapIndex = 0;
                nation.CapitalProvinceId = 0;
                campaign.CapitalRevision++;
                DiagnosticLogs.Write("simulation",
                    $"CAPITAL_LOST nation={nation.Tag} was={nation.CapitalName} day={campaign.ElapsedDays}");
                Fall(campaign, nation);
                continue;
            }
            var from = nation.CapitalName;
            Place(campaign, nation, best, seat);
            DiagnosticLogs.Write("simulation",
                $"CAPITAL_MOVED nation={nation.Tag} from={from} to={best.Id} day={campaign.ElapsedDays} by=necessity");
        }
    }

    /// <summary>
    /// A country with no ground left has no army either. Its formations used to go on standing
    /// in the middle of the country that had annexed it - an army of a state that was no longer
    /// on the map - so what is still in the field surrenders with it, counted as prisoners of
    /// whichever war it was fighting (see WarLosses). A formation aboard a ship goes the same
    /// way, and is taken off the ship's list.
    /// </summary>
    private static void Fall(Campaign campaign, Nation nation)
    {
        var fallen = 0;
        for (var index = campaign.Armies.Count - 1; index >= 0; index--)
        {
            var army = campaign.Armies[index];
            if (army.NationId != nation.Id) continue;
            WarLosses.Surrender(campaign, army);
            foreach (var fleet in campaign.Fleets) fleet.Carried.Remove(army.Id);
            Attrition.Destroy(campaign, army, "destroyed: its country ceased to exist");
            fallen++;
        }
        if (fallen == 0) return;
        DiagnosticLogs.Write("simulation", $"COUNTRY_FELL nation={nation.Tag} formations={fallen} day={campaign.ElapsedDays}");
        campaign.MarkStrategicChange();
    }

    /// <summary>Whether a country has a capital worth drawing: one it still holds.</summary>
    public static bool HasCapital(Campaign campaign, Nation nation) =>
        nation is not null && nation.CapitalStateMapIndex > 0 && Standing(campaign, nation);

    /// <summary>Whether there is an owned, controlled, usable place the government can sit.</summary>
    public static bool HasSeat(Campaign campaign, Nation nation) => EffectiveProvinceId(campaign, nation) > 0;

    /// <summary>Read-only effective seat. A captured or ceded province is never reported as a government seat.</summary>
    public static int EffectiveProvinceId(Campaign campaign, Nation nation)
    {
        if (nation is null || campaign?.ProvinceMap is null || nation.GovernmentSeatProvinceId < 0) return 0;
        var province = nation.GovernmentSeatProvinceId > 0 ? nation.GovernmentSeatProvinceId : nation.CapitalProvinceId;
        return Usable(campaign, nation, province) ? province : 0;
    }

    /// <summary>
    /// One supply origin, at the government's usable seat. Logistics runs before the daily
    /// evacuation pass: when no recorded seat is usable yet, predict that pass's choice
    /// without moving the government or applying its political consequences here.
    /// </summary>
    public static int SupplyOriginProvinceId(Campaign campaign, Nation nation)
    {
        if (nation is null || campaign?.ProvinceMap is not { } topology) return 0;
        var effective = EffectiveProvinceId(campaign, nation);
        if (effective > 0) return effective;

        // Legacy maps may know a capital region but not its precise province. This is not
        // a licence to use a remnant of the region after a placed capital has been captured.
        if (nation.CapitalProvinceId <= 0 && nation.CapitalStateMapIndex > 0)
        {
            var region = campaign.States.Find(s => s.MapIndex == nation.CapitalStateMapIndex);
            if (region is not null)
            {
                var legacy = Seat(campaign, nation, region);
                if (legacy > 0) return legacy;
            }
        }

        // Only countries awaiting evacuation need this scan; healthy capitals return above.
        // Use exactly the capital system's ownership, destroyed-ground and safety rules.
        var available = new HashSet<int>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (GroundAvailable(campaign, nation, province))
                available.Add(topology.ProvinceToState[province]);
        BestSeat(campaign, nation, available, out _, out var seat);
        return seat;
    }

    /// <summary>The effective seat's localized city or region name, or empty when there is no usable seat.</summary>
    public static string EffectiveName(Campaign campaign, Nation nation)
    {
        var province = EffectiveProvinceId(campaign, nation);
        if (province <= 0) return "";
        if (province == nation.CapitalProvinceId && !SeatNameLost(campaign, nation)) return CountryNames.Capital(nation);
        return campaign.States.Find(state => state.MapIndex == campaign.ProvinceMap.ProvinceToState[province])?.DisplayName ?? "";
    }

    /// <summary>
    /// Whether the city the capital is named after stands on somebody else's ground: a faction
    /// that took a fallen state's name inherits "Damascus" with it, but its seat is wherever it
    /// actually rules, and it must not be told that it sits in a city it does not hold.
    /// </summary>
    private const float SeatNameLostKm = 120f;

    public static bool SeatNameLost(Campaign campaign, Nation nation)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || nation is null || nation.CapitalProvinceId <= 0) return false;
        var named = CityCatalog.AnchorOnLand(map, nation.CapitalLatitude, nation.CapitalLongitude).ProvinceId;
        // Far away, not merely across a line: Rome's centre falls in the Vatican's province and
        // Nicosia's on the Green Line, and both are still the capitals they are named after.
        return named > 0 && named != nation.CapitalProvinceId && map.ProvinceToNation[named] != nation.MapIndex &&
               SupportCore.DistanceKm(campaign, named, nation.CapitalProvinceId) > SeatNameLostKm;
    }

    public static float EffectiveLatitude(Campaign campaign, Nation nation)
    {
        var province = EffectiveProvinceId(campaign, nation);
        return province <= 0 ? 0f : province == nation.CapitalProvinceId ? nation.CapitalLatitude :
            campaign.ProvinceMap.CenterOf(province).Latitude;
    }

    public static float EffectiveLongitude(Campaign campaign, Nation nation)
    {
        var province = EffectiveProvinceId(campaign, nation);
        return province <= 0 ? 0f : province == nation.CapitalProvinceId ? nation.CapitalLongitude :
            campaign.ProvinceMap.CenterOf(province).Longitude;
    }

    private static void SetGovernmentSeat(Campaign campaign, Nation nation, int province)
    {
        if (nation.GovernmentSeatProvinceId == province) return;
        nation.GovernmentSeatProvinceId = province;
        campaign.CapitalRevision++;
        campaign.MarkStrategicChange();
    }

    private static bool GroundAvailable(Campaign campaign, Nation nation, int province) =>
        OwnedLand(campaign, nation, province) && (campaign.ProvinceController is null ||
            (province < campaign.ProvinceController.Length && campaign.ProvinceController[province] == nation.MapIndex));

    private static bool OwnedLand(Campaign campaign, Nation nation, int province) =>
        campaign?.ProvinceMap is { } map && nation is not null && nation.MapIndex > 0 &&
        province > 0 && province <= map.ProvinceCount && map.ProvinceToNation[province] == nation.MapIndex &&
        !map.IsWater(province) && !campaign.DestroyedProvinces.Contains(province);

    private static bool UsableRegion(ScenarioState state) =>
        state is { Population: > 0, Unclaimed: false } && !LakeRegions.IsWaterState(state);

    private static bool Usable(Campaign campaign, Nation nation, int province,
        Dictionary<int, ScenarioState> regions = null)
    {
        if (!GroundAvailable(campaign, nation, province)) return false;
        var region = campaign.ProvinceMap.ProvinceToState[province];
        return UsableRegion(regions is not null ? regions.GetValueOrDefault(region) :
            campaign.States.Find(state => state.MapIndex == region));
    }

    private static void BestSeat(Campaign campaign, Nation nation, HashSet<int> available,
        out ScenarioState best, out int seat)
    {
        best = null;
        seat = 0;
        if (available is null || available.Count == 0) return;
        var bestScore = -1f;
        var hasSafeAlternative = campaign.States.Any(s => available.Contains(s.MapIndex) && UsableRegion(s) &&
            StabilityCore.OccupiedShare(campaign, s, nation) < 0.5f);

        foreach (var state in campaign.States)
        {
            if (!available.Contains(state.MapIndex) || !UsableRegion(state)) continue;
            var occupied = StabilityCore.OccupiedShare(campaign, state, nation);
            if (occupied >= 0.85f && hasSafeAlternative) continue;

            var capitalRegion = state.MapIndex == nation.CapitalStateMapIndex;
            var isHomeland = TerritorialClaims.TitleOf(campaign, nation, state) == TerritorialTitle.Homeland;
            var effectivePop = state.Population * (1f - occupied);
            var score = effectivePop * (isHomeland ? 1.5f : 1.0f);
            if (capitalRegion) score *= 0.1f;

            if (best is not null && score <= bestScore) continue;

            best = state;
            bestScore = score;
        }
        if (best is not null) seat = Seat(campaign, nation, best);
    }

    /// <summary>Whether the capital is still the country's own ground.</summary>
    private static bool Standing(Campaign campaign, Nation nation)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is not null && nation.CapitalProvinceId > 0 && nation.CapitalProvinceId <= topology.ProvinceCount)
            return OwnedLand(campaign, nation, nation.CapitalProvinceId);
        if (campaign is null) return false;
        foreach (var state in campaign.StatesOf(nation))
            if (state.MapIndex == nation.CapitalStateMapIndex) return true;
        return false;
    }

    /// <summary>
    /// Where in a region a government would sit: the country's own province nearest the
    /// region's middle, so the point is inside the region whatever its shape. 0 when none of
    /// the region is the country's.
    /// </summary>
    private static int Seat(Campaign campaign, Nation nation, ScenarioState state)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null || !UsableRegion(state)) return 0;
        // The middle, averaged on the circle so a region across the date line does not average
        // to the far side of the world.
        double latitude = 0, x = 0, y = 0;
        var count = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != state.MapIndex) continue;
            if (!GroundAvailable(campaign, nation, province)) continue;
            var centre = topology.CenterOf(province);
            latitude += centre.Latitude;
            var radians = centre.Longitude * Math.PI / 180.0;
            x += Math.Cos(radians);
            y += Math.Sin(radians);
            count++;
        }
        if (count == 0) return 0;

        var midLatitude = (float)(latitude / count);
        var midLongitude = (float)(Math.Atan2(y, x) * 180.0 / Math.PI);
        var scale = MathF.Max(0.12f, MathF.Cos(midLatitude * M.Pi / 180f));
        var best = float.MaxValue;
        var seat = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != state.MapIndex) continue;
            if (!GroundAvailable(campaign, nation, province)) continue;
            var centre = topology.CenterOf(province);
            var dx = MathF.Abs(centre.Longitude - midLongitude);
            dx = MathF.Min(dx, 360f - dx) * scale;
            var dy = centre.Latitude - midLatitude;
            var distance = dx * dx + dy * dy;
            if (distance >= best) continue;
            best = distance;
            seat = province;
        }
        return seat;
    }

    /// <summary>Put the capital there, named after its region - the name the tables know it by.</summary>
    private static void Place(Campaign campaign, Nation nation, ScenarioState state, int seat)
    {
        nation.CapitalStateMapIndex = state.MapIndex;
        nation.CapitalName = state.Name;
        nation.GovernmentSeatProvinceId = 0;
        var topology = campaign.ProvinceMap;
        if (topology is not null && seat > 0)
        {
            var centre = topology.CenterOf(seat);
            nation.CapitalLatitude = centre.Latitude;
            nation.CapitalLongitude = centre.Longitude;
            nation.CapitalProvinceId = seat;
        }
        campaign.CapitalRevision++;
        campaign.MarkStrategicChange();
    }
}
/// <summary>Move the government to another region of one's own. See CapitalCore.</summary>
public sealed class MoveCapitalCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }
    public bool Moved { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var state = campaign.States.Find(candidate => candidate.MapIndex == StateMapIndex);
        Moved = CapitalCore.Move(campaign, nation, state);
    }
}
