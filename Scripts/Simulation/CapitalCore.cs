using System;

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
/// while. A country whose capital region has been ceded moves at once and for nothing, to
/// the most populous region it has left, because it has no choice - and a country with no
/// regions left has no capital at all.
///
/// What a capital is FOR is unchanged: StabilityCore weighs its region far above its size,
/// and that is exactly what makes moving it a decision rather than a formality.
/// </summary>
public static class CapitalCore
{
    /// <summary>What moving the government costs this country, in the treasury's money.</summary>
    public static float Cost(Campaign campaign, Nation nation) =>
        MathF.Max(campaign.Defines.CapitalMoveCostFloor, nation.Gdp * campaign.Defines.CapitalMoveCostShare);

    /// <summary>Why the capital cannot move here now, or empty when it can.</summary>
    public static string Refusal(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (campaign is null || nation is null || state is null) return "Choose a region.";
        if (state.NationMapIndex != nation.MapIndex) return "Only a region we own can hold the capital.";
        if (state.MapIndex == nation.CapitalStateMapIndex) return "The capital is already here.";
        if (campaign.ProvinceMap is not null && Seat(campaign.ProvinceMap, nation, state) <= 0)
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
        Place(campaign, nation, state, Seat(campaign.ProvinceMap, nation, state));
        DiagnosticLogs.Write("simulation",
            $"CAPITAL_MOVED nation={nation.Tag} to={state.Id} day={campaign.ElapsedDays} by=choice");
        return true;
    }

    /// <summary>
    /// Capitals follow the ground. Once a day, and one lookup a country: whether the province
    /// the capital stands in is still the country's. That is the question a peace changes - a
    /// cession moves the owner of each province it takes - and it is asked of the province
    /// rather than the region because the two can disagree: a few places on the map (the
    /// buffer zone across Nicosia, Bir Tawil) are a region on one list and somebody else's
    /// ground on the other, and asking the region would move their capital on the first
    /// morning for no reason at all.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(CapitalCore))) return;
        var topology = campaign?.ProvinceMap;
        if (topology is null) return;
        foreach (var nation in campaign.Nations)
        {
            // Zero is a country the map never placed a capital for - one with no ground on it
            // at all, or one whose capital is already gone. Neither has anything to follow.
            if (nation.CapitalStateMapIndex <= 0) continue;
            if (Standing(campaign, nation)) continue;

            // The most populous region it has left that it can actually sit in.
            ScenarioState best = null;
            var seat = 0;
            foreach (var state in campaign.StatesOf(nation))
            {
                if (best is not null && state.Population <= best.Population) continue;
                var candidate = Seat(topology, nation, state);
                if (candidate <= 0) continue;
                best = state;
                seat = candidate;
            }
            if (best is null)
            {
                // Nothing left to govern from.
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

    /// <summary>Whether the capital is still the country's own ground.</summary>
    private static bool Standing(Campaign campaign, Nation nation)
    {
        var topology = campaign.ProvinceMap;
        if (topology is not null && nation.CapitalProvinceId > 0 && nation.CapitalProvinceId <= topology.ProvinceCount)
            return topology.ProvinceToNation[nation.CapitalProvinceId] == nation.MapIndex;
        foreach (var state in campaign.StatesOf(nation))
            if (state.MapIndex == nation.CapitalStateMapIndex) return true;
        return false;
    }

    /// <summary>
    /// Where in a region a government would sit: the country's own province nearest the
    /// region's middle, so the point is inside the region whatever its shape. 0 when none of
    /// the region is the country's.
    /// </summary>
    private static int Seat(ProvinceTopology topology, Nation nation, ScenarioState state)
    {
        if (topology is null) return 0;
        // The middle, averaged on the circle so a region across the date line does not average
        // to the far side of the world.
        double latitude = 0, x = 0, y = 0;
        var count = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != state.MapIndex) continue;
            if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
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
            if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
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
