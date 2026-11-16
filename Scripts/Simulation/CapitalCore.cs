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
    /// The city the capital is: a key of the cities on the map (CityCatalog). Saved.
    ///
    /// A capital is a city, not a region and not a point: the province it stands in is the
    /// province under that city, its name is the city's, and when the government moves it moves
    /// into another city and this changes with it. Empty when the capital stands where the map
    /// has no city of the country's own (then it goes by its region's name). Null - which is what
    /// a save written before capitals were cities reads back as, and what a country made in play
    /// starts with - means nobody has looked yet: the first time the map places the capital it
    /// finds the city (Campaign.LocateCapitals).
    /// </summary>
    public string CapitalCityId;

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
///
/// And a capital is a city. It was a point and a name, and after any move the name was the
/// region's - "the capital of Ukraine: Lviv Oblast". Now the country carries the city's key
/// (Nation.CapitalCityId): it names one in its data, every move goes into a city of the region
/// chosen - the first written of those standing on ground the government can use - and the
/// province, the point and the name all follow the city. Only where the map has no city on the
/// country's own ground does the capital fall back to its region's name.
/// </summary>
public static class CapitalCore
{
    /// <summary>
    /// How far a capital's point may stand from the city it is said to be and still be that
    /// city. The point is the older truth and code that knows nothing of cities still writes it
    /// (a faction taking a fallen state's capital, a mod moving one by its coordinates); when
    /// the two disagree by more than this, the city is found again from the point.
    /// </summary>
    private const float CityAgreesKm = 60f;

    /// <summary>
    /// Whether the map is one the cities stand on. They are the world's: real places by latitude
    /// and longitude. A raster too small to be a map of the world - the eight-province boards
    /// the simulation is tried on - has no cities for a government to sit in, though every one
    /// of the world's would fall somewhere on it. The same line ProvinceTopology draws between
    /// a world and a board.
    /// </summary>
    private static bool HasCities(ProvinceTopology map) => map is not null && map.Width >= ProvinceTopology.WorldRasterWidth;

    /// <summary>The city a country's capital is, or null when it has none on the map.</summary>
    public static CityDefinition City(Nation nation) => CityCatalog.Find(nation?.CapitalCityId);

    /// <summary>
    /// The city the government sits in today: the capital's while it sits there, the city under
    /// its temporary seat while it does not, null when it has no seat or sits where there is no
    /// city. This is the city the map marks as a capital.
    /// </summary>
    public static CityDefinition SeatCity(Campaign campaign, Nation nation)
    {
        var province = EffectiveProvinceId(campaign, nation);
        if (province <= 0) return null;
        if (province == nation.CapitalProvinceId) return City(nation);
        return HasCities(campaign.ProvinceMap) ? CityCatalog.CityIn(campaign.ProvinceMap, province) : null;
    }

    /// <summary>
    /// The name a country's data gives its capital over the city's own, or null when it gives
    /// none. A file that restates "capital_name" for a country without naming a city - a mod
    /// that calls London "Londinium" - renames the capital, not the city: the capital is still
    /// that city, governed from it and marked on it, and reads by the name it was given for as
    /// long as it stands there. A move into another city takes that city's name (Bind).
    /// </summary>
    public static string OwnName(Nation nation) =>
        City(nation) is { } city && !string.IsNullOrWhiteSpace(nation.CapitalName) &&
        !string.Equals(nation.CapitalName, city.Name, StringComparison.OrdinalIgnoreCase) ? nation.CapitalName : null;

    /// <summary>
    /// The capital's name as a key, for a name that is made once and read in any language - an
    /// organisation founded in play is "the pact of this capital and that one". The city's own
    /// row (city.&lt;id&gt;.name) when the capital is a city that has one, so the organisation says
    /// what the map says; otherwise the name the capital goes by, which the capitals' table and
    /// the plain key translate as they always did. The raw city name is not a key in either
    /// language: the Russian reader was shown "København".
    /// </summary>
    public static string NameKey(Nation nation)
    {
        if (nation is null) return "";
        if (City(nation) is { } city && OwnName(nation) is null && Loc.Has($"city.{city.Id}.name"))
            return $"city.{city.Id}.name";
        return nation.CapitalName ?? "";
    }

    /// <summary>
    /// A country's data names its capital city (common/countries, "capital_city"; a mod's
    /// country file the same). The name and the point follow at once, so that everything that
    /// still reads those two - and the check that the point agrees with the city - sees the city.
    /// A key no city has is kept as written and simply never binds.
    /// </summary>
    public static void Author(Nation nation, string cityId)
    {
        if (nation is null) return;
        nation.CapitalCityId = cityId?.Trim() ?? "";
        if (CityCatalog.Find(nation.CapitalCityId) is not { } city) return;
        nation.CapitalName = city.Name;
        nation.CapitalLatitude = city.Latitude;
        nation.CapitalLongitude = city.Longitude;
    }

    /// <summary>
    /// How near its own ground a city must stand to be a country's capital when the city's very
    /// point is somebody else's, in cells of the map. Rome's centre falls in the Vatican's
    /// province, N'Djamena's on the Cameroonian bank of the Chari, Nicosia's on the Green Line -
    /// and all three are the capitals they are named after. The government then sits on its own
    /// ground at the city's edge.
    /// </summary>
    private const int CityEdgeCells = 3;

    /// <summary>
    /// Bind a capital that has no province yet to its city, at the start of a campaign or when
    /// a country comes to hold ground (see <see cref="CityAt"/> for which city). False when the
    /// country has no city there at all: the caller then places the capital on the nearest
    /// province and it goes by a name, as it always did.
    /// </summary>
    /// <param name="nearest">The country's own province nearest the capital's point.</param>
    internal static bool BindAtPoint(Campaign campaign, Nation nation, int nearest)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || nation is null) return false;
        var region = nearest > 0 && nearest <= map.ProvinceCount ? map.ProvinceToState[nearest] : 0;
        if (!CityAt(campaign, nation, region, heldOnly: false, out var city, out var anchor)) return false;
        Bind(campaign, nation, city, anchor);
        return true;
    }

    /// <summary>
    /// A capital placed before capitals were cities - a save from an older build - is given its
    /// city once, when the save is loaded: the same search, in the region the capital already
    /// stands in, and on ground the country holds if it holds the capital. Loading a save must
    /// not put a government under occupation that was not.
    /// </summary>
    internal static void BindLoaded(Campaign campaign, Nation nation)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || nation is null || nation.CapitalCityId is not null) return;
        // A capital the map never placed has nothing to bind and stays unlooked-at: it is found
        // when the country comes to hold ground (LocateCapitals), as it would be in a new world.
        var province = nation.CapitalProvinceId;
        if (province <= 0 || province > map.ProvinceCount) return;
        nation.CapitalCityId = "";
        if (!OwnedLand(campaign, nation, province)) return;
        var held = GroundAvailable(campaign, nation, province);
        if (!CityAt(campaign, nation, map.ProvinceToState[province], held, out var city, out var anchor)) return;
        Bind(campaign, nation, city, anchor);
        campaign.CapitalRevision++;
    }

    /// <summary>
    /// Which city a capital is, from what the country says about it. In order:
    ///
    /// the city it names by key, if the country's own ground is at it and the capital's name
    /// and point are that city's (or no point was ever given);
    ///
    /// the city at its point: one of the capital's own name first, then - for a capital with a
    /// name of its own - the nearest with the country's own ground at it (or, when no point was
    /// ever given, the first written city of that name). The city named by key is one of these
    /// like any other, so a file that only renames the capital keeps it (it is the nearest: the
    /// point is its own), and a file that restates the name and the point without a city - the
    /// older way to move a capital, and what the country editor writes - moves it even to a
    /// city next door: The Hague is 52 km from Amsterdam and Tel Aviv 54 from Jerusalem, and
    /// the key the base game wrote used to win both;
    ///
    /// and otherwise a city of the region: the one nearest the point, or - for a capital that was
    /// only ever named after its region, which is what a move or a country's birth in play used
    /// to leave - the region's first.
    /// </summary>
    private static bool CityAt(Campaign campaign, Nation nation, int region, bool heldOnly,
        out int found, out CityCatalog.MapAnchor anchor)
    {
        var map = campaign.ProvinceMap;
        found = -1;
        anchor = default;
        if (!HasCities(map)) return false;
        var cities = CityCatalog.All;
        var pointed = nation.CapitalLatitude != 0f || nation.CapitalLongitude != 0f;
        float Apart(int index) => Kilometres(cities[index].Latitude, cities[index].Longitude,
            nation.CapitalLatitude, nation.CapitalLongitude);

        var regional = NamedAfter(nation, region > 0 ? campaign.StateByMapIndex(region) : null);
        bool Bears(int index) => string.Equals(cities[index].Name, nation.CapitalName, StringComparison.OrdinalIgnoreCase);
        // The key decides at once where nothing says otherwise: the capital bears that city's
        // name, or no name at all, and stands where the city does - or was never given a point,
        // and the key is all there is to go by. Of two cities of one name within reach of the
        // point it is the one meant, however near the other.
        var keyed = CityCatalog.IndexOf(nation.CapitalCityId);
        found = keyed;
        if (keyed >= 0 && (!pointed || (string.IsNullOrWhiteSpace(nation.CapitalName) || Bears(keyed)) &&
                Apart(keyed) <= CityAgreesKm) &&
            OwnAnchor(campaign, nation, cities[keyed], heldOnly, out anchor)) return true;

        if (pointed)
        {
            // The few cities within reach of the point: one that bears the capital's name before
            // any other - a capital's point and its city's are seldom the same point, and a
            // suburb may be the nearer - then the nearest; of two equally near, the earlier
            // written. A capital that goes by its region's name takes only a city of that very
            // name here (Kharkiv in the Kharkiv region); a town that merely happens to be near
            // the middle of the region is not what it was named for.
            // A degree of latitude is 111 km, so most cities are dismissed at a glance.
            var near = new List<(bool Other, float Distance, int Index)>();
            var band = CityAgreesKm / 110f;
            for (var index = 0; index < cities.Count; index++)
            {
                if (MathF.Abs(cities[index].Latitude - nation.CapitalLatitude) > band) continue;
                var distance = Apart(index);
                if (distance > CityAgreesKm) continue;
                near.Add((!Bears(index), distance, index));
            }
            near.Sort();
            foreach (var (other, _, index) in near)
            {
                // (The city the country names by key is no such town: it was written down.)
                if (other && regional && index != keyed) continue;
                if (OwnAnchor(campaign, nation, cities[index], heldOnly, out anchor)) { found = index; return true; }
            }
        }
        else if (!regional)
        {
            for (var index = 0; index < cities.Count; index++)
                if (string.Equals(cities[index].Name, nation.CapitalName, StringComparison.OrdinalIgnoreCase) &&
                    OwnAnchor(campaign, nation, cities[index], heldOnly, out anchor)) { found = index; return true; }
        }

        anchor = default;
        found = region <= 0 ? -1 : regional
            ? FirstCity(campaign, nation, region, heldOnly)
            : NearestCity(campaign, nation, region, nation.CapitalLatitude, nation.CapitalLongitude, heldOnly);
        if (found < 0) return false;
        anchor = CityCatalog.AnchorOnLand(map, cities[found].Latitude, cities[found].Longitude);
        return true;
    }

    /// <summary>
    /// Where a country governs from when this city is its capital: the ground under the city if
    /// that is the country's own, else its own ground at the city's edge. False when it has none
    /// within <see cref="CityEdgeCells"/>.
    /// </summary>
    private static bool OwnAnchor(Campaign campaign, Nation nation, CityDefinition city, bool heldOnly,
        out CityCatalog.MapAnchor anchor)
    {
        var map = campaign.ProvinceMap;
        bool Ours(int province) => heldOnly ? GroundAvailable(campaign, nation, province) : OwnedLand(campaign, nation, province);
        anchor = CityCatalog.AnchorOnLand(map, city.Latitude, city.Longitude);
        if (Ours(anchor.ProvinceId)) return true;
        anchor = CityCatalog.AnchorNear(map, city.Latitude, city.Longitude, CityEdgeCells, Ours);
        return anchor.ProvinceId > 0;
    }

    /// <summary>
    /// Whether a capital has no name but its region's: one the game placed there, by a move or
    /// at a country's birth, rather than a town somebody wrote down.
    /// </summary>
    private static bool NamedAfter(Nation nation, ScenarioState region) =>
        string.IsNullOrWhiteSpace(nation.CapitalName) ||
        region is not null && (string.Equals(nation.CapitalName, region.Name, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(nation.CapitalName, region.DisplayName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The capital is this city: its key and its name, governed from that ground. One name is
    /// kept: the one the country's data gave this very city (<see cref="OwnName"/>) - the
    /// country already named the city by key and calls it something else. A capital that
    /// becomes a different city, by a move or because the old way of writing it found one,
    /// takes the city's name.
    /// </summary>
    private static void Bind(Campaign campaign, Nation nation, int cityIndex, CityCatalog.MapAnchor anchor)
    {
        var city = CityCatalog.All[cityIndex];
        if (string.IsNullOrWhiteSpace(nation.CapitalName) ||
            !string.Equals(nation.CapitalCityId, city.Id, StringComparison.OrdinalIgnoreCase))
            nation.CapitalName = city.Name;
        nation.CapitalCityId = city.Id;
        nation.CapitalLatitude = anchor.Latitude;
        nation.CapitalLongitude = anchor.Longitude;
        nation.CapitalProvinceId = anchor.ProvinceId;
        nation.CapitalStateMapIndex = campaign.ProvinceMap.ProvinceToState[anchor.ProvinceId];
    }

    /// <summary>
    /// The first city written that stands in a region on the country's own ground (and ground
    /// it holds, when that is asked), as an index into the cities, or -1.
    /// </summary>
    private static int FirstCity(Campaign campaign, Nation nation, int region, bool controlledOnly)
    {
        var map = campaign.ProvinceMap;
        if (!HasCities(map)) return -1;
        var under = CityCatalog.ProvincesOf(map);
        for (var index = 0; index < under.Length; index++)
        {
            var province = under[index];
            if (province <= 0 || map.ProvinceToState[province] != region) continue;
            if (controlledOnly ? GroundAvailable(campaign, nation, province) : OwnedLand(campaign, nation, province))
                return index;
        }
        return -1;
    }

    /// <summary>The same, but the city nearest a point; of two equally near, the earlier written.</summary>
    private static int NearestCity(Campaign campaign, Nation nation, int region, float latitude, float longitude,
        bool controlledOnly)
    {
        var map = campaign.ProvinceMap;
        if (!HasCities(map)) return -1;
        var cities = CityCatalog.All;
        var under = CityCatalog.ProvincesOf(map);
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var index = 0; index < under.Length; index++)
        {
            var province = under[index];
            if (province <= 0 || map.ProvinceToState[province] != region) continue;
            if (!(controlledOnly ? GroundAvailable(campaign, nation, province) : OwnedLand(campaign, nation, province)))
                continue;
            var distance = Kilometres(cities[index].Latitude, cities[index].Longitude, latitude, longitude);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        return best;
    }

    private static float Kilometres(float latitudeA, float longitudeA, float latitudeB, float longitudeB)
    {
        const double radians = Math.PI / 180.0;
        var dLat = (latitudeB - latitudeA) * radians;
        var dLon = (longitudeB - longitudeA) * radians;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(latitudeA * radians) * Math.Cos(latitudeB * radians) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return (float)(6371.0 * 2.0 * Math.Asin(Math.Min(1.0, Math.Sqrt(h))));
    }

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

    /// <summary>
    /// The cities of a region the government could move into today: those standing on ground the
    /// country owns and holds, in the order the map writes them, without the city it sits in now.
    /// A capital is moved into a city, and this is the list the player chooses from.
    /// </summary>
    public static List<CityDefinition> Candidates(Campaign campaign, Nation nation, ScenarioState state)
    {
        var found = new List<CityDefinition>();
        var map = campaign?.ProvinceMap;
        if (nation is null || state is null || state.NationMapIndex != nation.MapIndex || !HasCities(map)) return found;
        var cities = CityCatalog.All;
        var under = CityCatalog.ProvincesOf(map);
        for (var index = 0; index < under.Length && index < cities.Count; index++)
        {
            var province = under[index];
            if (province <= 0 || map.ProvinceToState[province] != state.MapIndex) continue;
            if (string.Equals(cities[index].Id, nation.CapitalCityId, StringComparison.OrdinalIgnoreCase)) continue;
            if (GroundAvailable(campaign, nation, province)) found.Add(cities[index]);
        }
        return found;
    }

    private static int IndexOf(Campaign campaign, Nation nation, ScenarioState state, string cityId)
    {
        var map = campaign?.ProvinceMap;
        if (string.IsNullOrWhiteSpace(cityId) || nation is null || state is null || !HasCities(map)) return -1;
        var cities = CityCatalog.All;
        var under = CityCatalog.ProvincesOf(map);
        for (var index = 0; index < under.Length && index < cities.Count; index++)
        {
            if (!string.Equals(cities[index].Id, cityId, StringComparison.OrdinalIgnoreCase)) continue;
            var province = under[index];
            return province > 0 && map.ProvinceToState[province] == state.MapIndex && GroundAvailable(campaign, nation, province)
                ? index : -1;
        }
        return -1;
    }

    /// <summary>
    /// Why the capital cannot move here now, or empty when it can. With a city named, the move is
    /// into that city - which may stand in the capital's own region: Saint Petersburg to
    /// Gatchina is a move like any other. With none, it is the region's first city, and the
    /// capital's own region is refused, as it always was (which is also what keeps a computer
    /// from paying to move next door).
    /// </summary>
    public static string Refusal(Campaign campaign, Nation nation, ScenarioState state, string cityId = null)
    {
        if (campaign is null || nation is null || state is null) return "Choose a region.";
        if (state.NationMapIndex != nation.MapIndex) return "Only a region we own can hold the capital.";
        if (!string.IsNullOrWhiteSpace(cityId))
        {
            if (string.Equals(cityId, nation.CapitalCityId, StringComparison.OrdinalIgnoreCase)) return "The capital is already here.";
            if (IndexOf(campaign, nation, state, cityId) < 0) return "That city does not stand on ground we govern in this region.";
        }
        else if (state.MapIndex == nation.CapitalStateMapIndex) return "The capital is already here.";
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
    public static bool Move(Campaign campaign, Nation nation, ScenarioState state, string cityId = null)
    {
        if (Refusal(campaign, nation, state, cityId).Length > 0) return false;
        { var ledgerBefore = nation.Treasury; nation.Treasury -= Cost(campaign, nation); MoneyLedger.Book(nation, MoneyFlow.Construction, nation.Treasury - ledgerBefore); }
        var limit = StabilityCore.Weights.GovernanceLimit;
        nation.StabilityGovernance = M.Clamp(
            nation.StabilityGovernance - campaign.Defines.CapitalMoveStabilityCost, -limit, limit);
        nation.CapitalMovedDay = campaign.ElapsedDays;
        var chosen = IndexOf(campaign, nation, state, cityId);
        if (chosen >= 0)
        {
            // Into the city that was named: its ground, its point and its name.
            nation.GovernmentSeatProvinceId = 0;
            var at = CityCatalog.All[chosen];
            Bind(campaign, nation, chosen, CityCatalog.AnchorOnLand(campaign.ProvinceMap, at.Latitude, at.Longitude));
            campaign.CapitalRevision++;
            campaign.MarkStrategicChange();
        }
        else Place(campaign, nation, state, Seat(campaign, nation, state));
        DiagnosticLogs.Write("simulation",
            $"CAPITAL_MOVED nation={nation.Tag} to={state.Id} city={nation.CapitalCityId} day={campaign.ElapsedDays} by=choice");
        return true;
    }

    /// <summary>
    /// Follow permanent ownership and temporary control separately. A usable emergency
    /// seat stays put until the legal capital is safe or that seat is lost too. The actual
    /// province owner wins over stale or split region metadata.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    private sealed class SeatBoard
    {
        public readonly Dictionary<int, ScenarioState> Regions = new();
        public readonly Dictionary<int, HashSet<int>> AvailableByOwner = new();
        public readonly HashSet<int> LandOwners = new();
        public readonly Stack<HashSet<int>> HeldPool = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, SeatBoard> SeatBoards = new();

    /// <summary>The same seat watch, yielded between batches of nations.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(CapitalCore))) yield break;
        var topology = campaign?.ProvinceMap;
        if (topology is null) yield break;
        var board = SeatBoards.GetOrCreateValue(campaign);
        var regions = board.Regions;
        regions.Clear();
        for (var i = 0; i < campaign.States.Count; i++)
        {
            var state = campaign.States[i];
            regions[state.MapIndex] = state;
        }
        var availableByOwner = board.AvailableByOwner;
        var landOwners = board.LandOwners;
        var heldPool = board.HeldPool;
        var surveyed = false;
        void SurveyGround()
        {
            if (surveyed) return;
            surveyed = true;
            foreach (var existing in availableByOwner.Values) { existing.Clear(); heldPool.Push(existing); }
            availableByOwner.Clear();
            landOwners.Clear();
            var controllers = campaign.ProvinceController;
            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                int owner = topology.ProvinceToNation[province];
                if (owner <= 0 || topology.IsWater(province) || campaign.DestroyedProvinces.Contains(province)) continue;
                landOwners.Add(owner);
                if (controllers is not null && (province >= controllers.Length || controllers[province] != owner)) continue;
                if (!availableByOwner.TryGetValue(owner, out var held))
                    availableByOwner[owner] = held = heldPool.Count > 0 ? heldPool.Pop() : new HashSet<int>();
                held.Add(topology.ProvinceToState[province]);
            }
        }
        var scanned = 0;
        foreach (var nation in campaign.Nations)
        {
            if (++scanned % 8 == 0) yield return 0;
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
            var region = campaign.StateByMapIndex(nation.CapitalStateMapIndex);
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

    /// <summary>
    /// The effective seat's name in the reader's language, or empty when there is no usable seat:
    /// the city the government sits in, and only where it sits in no city the name the capital
    /// goes by, or its region's.
    /// </summary>
    public static string EffectiveName(Campaign campaign, Nation nation)
    {
        var province = EffectiveProvinceId(campaign, nation);
        if (province <= 0) return "";
        if (SeatCity(campaign, nation) is { } city)
            return province == nation.CapitalProvinceId ? CountryNames.CapitalCityName(nation, city) : city.DisplayName;
        if (province == nation.CapitalProvinceId && !SeatNameLost(campaign, nation)) return CountryNames.Capital(nation);
        return campaign.StateByMapIndex(campaign.ProvinceMap.ProvinceToState[province])?.DisplayName ?? "";
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
        // A capital bound to a city is named after the city it stands in, by construction.
        if (City(nation) is not null) return false;
        var named = CityCatalog.AnchorOnLand(map, nation.CapitalLatitude, nation.CapitalLongitude).ProvinceId;
        // Far away, not merely across a line: Rome's centre falls in the Vatican's province and
        // Nicosia's on the Green Line, and both are still the capitals they are named after.
        return named > 0 && named != nation.CapitalProvinceId && map.ProvinceToNation[named] != nation.MapIndex &&
               SupportCore.DistanceKm(campaign, named, nation.CapitalProvinceId) > SeatNameLostKm;
    }

    public static float EffectiveLatitude(Campaign campaign, Nation nation) => EffectivePoint(campaign, nation).Latitude;

    public static float EffectiveLongitude(Campaign campaign, Nation nation) => EffectivePoint(campaign, nation).Longitude;

    /// <summary>
    /// Where the government's mark is drawn: the capital's own point, or for a temporary seat the
    /// city it sits in - on the city's dot, not in the middle of the province around it.
    /// </summary>
    private static (float Latitude, float Longitude) EffectivePoint(Campaign campaign, Nation nation)
    {
        var province = EffectiveProvinceId(campaign, nation);
        if (province <= 0) return (0f, 0f);
        if (province == nation.CapitalProvinceId) return (nation.CapitalLatitude, nation.CapitalLongitude);
        var map = campaign.ProvinceMap;
        if (HasCities(map) && CityCatalog.CityIn(map, province) is { } city)
        {
            var anchor = CityCatalog.AnchorOnLand(map, city.Latitude, city.Longitude);
            return (anchor.Latitude, anchor.Longitude);
        }
        return map.CenterOf(province);
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
            campaign.StateByMapIndex(region));
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
    /// Where in a region a government would sit: in a city - the first written of the region's
    /// cities standing on ground the country owns and holds. Only a region with no such city
    /// seats it on the country's own province nearest the region's middle, so the point is
    /// inside the region whatever its shape. 0 when none of the region is the country's.
    /// </summary>
    private static int Seat(Campaign campaign, Nation nation, ScenarioState state)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null || !UsableRegion(state)) return 0;
        var city = FirstCity(campaign, nation, state.MapIndex, controlledOnly: true);
        if (city >= 0) return CityCatalog.ProvincesOf(topology)[city];
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

    /// <summary>
    /// Put the capital there: into the city <see cref="Seat"/> chose, which it then is - its key,
    /// its name, its point. A region with no city the country can use takes the capital on the
    /// seat province under the region's own name, the one case a capital is not a city.
    /// </summary>
    private static void Place(Campaign campaign, Nation nation, ScenarioState state, int seat)
    {
        nation.GovernmentSeatProvinceId = 0;
        var topology = campaign.ProvinceMap;
        var city = topology is null ? -1 : FirstCity(campaign, nation, state.MapIndex, controlledOnly: true);
        if (city >= 0 && CityCatalog.ProvincesOf(topology)[city] == seat)
        {
            var at = CityCatalog.All[city];
            Bind(campaign, nation, city, CityCatalog.AnchorOnLand(topology, at.Latitude, at.Longitude));
        }
        else
        {
            nation.CapitalStateMapIndex = state.MapIndex;
            nation.CapitalName = state.Name;
            nation.CapitalCityId = "";
            if (topology is not null && seat > 0)
            {
                var centre = topology.CenterOf(seat);
                nation.CapitalLatitude = centre.Latitude;
                nation.CapitalLongitude = centre.Longitude;
                nation.CapitalProvinceId = seat;
            }
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
    /// <summary>The city of that region to move into (its key), or empty for the region's first.</summary>
    public string CityId { get; init; } = "";
    public bool Moved { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var state = campaign.States.Find(candidate => candidate.MapIndex == StateMapIndex);
        Moved = CapitalCore.Move(campaign, nation, state, string.IsNullOrWhiteSpace(CityId) ? null : CityId);
    }
}
