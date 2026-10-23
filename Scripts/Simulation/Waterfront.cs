using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>How much water a place has, for the things that need some.</summary>
public enum WaterAccess
{
    /// <summary>Dry. Nothing floats out of here.</summary>
    None,

    /// <summary>A lake or a navigable river. Hulls can be built and cargo moved; the sea cannot be reached.</summary>
    Inland,

    /// <summary>Open water. The whole world is downstream.</summary>
    Sea
}

/// <summary>
/// Where the water is.
///
/// The game knew about lakes and it knew about rivers and it did not know about the sea,
/// because the sea is not on the map: a province is a patch of land, and the ocean is
/// simply where the provinces stop. Nothing needed to ask until now - fleets sail by
/// latitude and longitude, and invasions land on whatever province is nearest - so a
/// shipyard could be ordered in the middle of Mongolia and the economy would accept it,
/// build it over two hundred and ninety days, and add its tonnage to the national total.
///
/// So the coast is worked out once from the cell raster and kept. A province is on the sea
/// when one of its cells touches a cell that belongs to no province at all, which on this
/// map means open water; it is inland-watered when it touches a lake region or carries a
/// river. That is the whole of the classification, and it is geography, so it is computed
/// per map rather than per day and never invalidated by anything a campaign does.
///
/// The only place the rule is inexact is a coastline drawn against unclaimed land, of which
/// this map has none that borders an inhabited region - the one large unowned area is
/// Antarctica, and nothing borders it but ocean.
/// </summary>
public static class Waterfront
{
    private static ProvinceTopology _map;
    private static WaterAccess[] _byProvince = Array.Empty<WaterAccess>();

    // Regions are cut by wars - a partition gives half a coastal region to somebody else -
    // so the region answer is stamped with how many regions there are. Geography does not
    // move, but which province belongs to which region does.
    private static int _regionStamp = -1;
    private static readonly Dictionary<int, WaterAccess> ByRegion = new();

    /// <summary>
    /// Whether this building is useless away from water. Declared by the building, in
    /// `common/buildings`, so a mod's fishing harbour needs a coast and a mod's river
    /// terminal is a river terminal without either of them touching this file.
    /// </summary>
    public static bool NeedsWater(BuildingKind building) => BuildingCatalog.Get(building).NeedsWater;

    /// <summary>What a province can reach by water.</summary>
    public static WaterAccess OfProvince(ProvinceTopology map, int province)
    {
        Refresh(map);
        return province > 0 && province < _byProvince.Length ? _byProvince[province] : WaterAccess.None;
    }

    /// <summary>
    /// What a region can reach by water: the best any of its provinces can. One province
    /// on the coast makes the region coastal, which is how a port works - Bavaria is not
    /// a seaport because Germany has Hamburg, but Hamburg's own region is.
    /// </summary>
    public static WaterAccess OfRegion(Campaign campaign, int stateMapIndex)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || stateMapIndex <= 0) return WaterAccess.None;
        Refresh(map);

        var stamp = campaign.States?.Count ?? 0;
        if (stamp != _regionStamp)
        {
            _regionStamp = stamp;
            ByRegion.Clear();
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                var region = map.ProvinceToState[province];
                if (region == 0) continue;
                var here = _byProvince[province];
                if (here == WaterAccess.None) { ByRegion.TryAdd(region, WaterAccess.None); continue; }
                if (!ByRegion.TryGetValue(region, out var best) || here > best) ByRegion[region] = here;
            }
        }
        return ByRegion.GetValueOrDefault(stateMapIndex);
    }

    public static WaterAccess OfRegion(Campaign campaign, ScenarioState state) =>
        state is null ? WaterAccess.None : OfRegion(campaign, state.MapIndex);

    /// <summary>
    /// What an inland yard or wharf is worth against a sea one.
    ///
    /// The compromise, and the reason this is a number rather than a refusal: Duisburg is
    /// the largest inland port on earth, Paraguay runs a merchant fleet a thousand miles
    /// from the sea, and the yards on the Great Lakes built warships for two world wars.
    /// A country with a river should be able to build on it. What it should not get is the
    /// same reach as a country with a deep-water coast, because a barge terminal is not
    /// Rotterdam - so it builds, at the same price, and delivers a fraction.
    /// </summary>
    public static float Yield(Campaign campaign, WaterAccess access) => access switch
    {
        WaterAccess.Sea => 1f,
        WaterAccess.Inland => campaign?.Defines?.InlandWaterworksYield ?? 0.45f,
        _ => 0f
    };

    public static float Yield(Campaign campaign, int stateMapIndex, BuildingKind building) =>
        NeedsWater(building) ? Yield(campaign, OfRegion(campaign, stateMapIndex)) : 1f;

    /// <summary>
    /// Why this cannot be built here, or an empty string when it can.
    ///
    /// Spoken rather than silent: the command below refuses quietly, which is right for an
    /// AI giving the order and wrong for a button, and "the port button does nothing" was
    /// the whole of the complaint that produced this file.
    /// </summary>
    public static string Refusal(Campaign campaign, int stateMapIndex, BuildingKind building)
    {
        if (!NeedsWater(building)) return "";
        if (OfRegion(campaign, stateMapIndex) != WaterAccess.None) return "";
        var name = Loc.Get(BuildingCatalog.Get(building).Name);
        return Loc.Format("{0}: this region has no coast, no lake and no navigable river.", name);
    }

    // One answer per map, kept for as long as the map is: two campaigns alive at once - two
    // peers stepped side by side, a client and the snapshot it is rebuilding from - each ask
    // about their own map, and remembering only the last one re-read sixteen million cells on
    // every switch.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ProvinceTopology, WaterAccess[]> Known = new();

    private static void Refresh(ProvinceTopology map)
    {
        if (map is null || ReferenceEquals(_map, map)) return;
        _map = map;
        _regionStamp = -1;
        if (Known.TryGetValue(map, out var known))
        {
            _byProvince = known;
            return;
        }

        var access = new WaterAccess[map.ProvinceCount + 1];
        var cells = map.CellToProvince;
        var width = map.Width;
        var height = map.Height;

        // The sea, from the one place it is visible: a cell with no province on it. Only
        // the four edge-sharing neighbours count - a diagonal touch is a corner, and on a
        // raster this coarse every second inland cell has one.
        // Walk rows rather than recovering x/y for each cell. Geography does not need
        // division, two modulo operations and four bounds-checked Open calls per land
        // pixel; on the shipped raster those made the first mapped market a long stage.
        // Horizontal neighbours still wrap at the date line, and vertical edges do not.
        var water = map.WaterByProvince;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var end = row + width;
            for (var cell = row; cell < end; cell++)
            {
                var province = cells[cell];
                if (province <= 0 || province >= access.Length ||
                    access[province] == WaterAccess.Sea || water[province]) continue;

                if (cells[cell + 1 == end ? row : cell + 1] == 0 ||
                    cells[cell == row ? end - 1 : cell - 1] == 0 ||
                    (y > 0 && cells[cell - width] == 0) ||
                    (y + 1 < height && cells[cell + width] == 0))
                    access[province] = WaterAccess.Sea;
            }
        }

        // And the rest of the water, which the map does record.
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (access[province] == WaterAccess.Sea) continue;
            if (map.HasLakeAccess(province) || map.HasRiver(province))
                access[province] = WaterAccess.Inland;
        }

        _byProvince = access;
        Known.AddOrUpdate(map, access);
    }
}
