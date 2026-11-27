using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Minimum preparation for a voluntary AI war; not scenario wars, revolts or calls to arms.</summary>
    public int AiWarPreparationDays { get; set; } = 45;
}

public static partial class AiStatecraft
{
    internal static bool PreparationComplete(Campaign c, Nation nation, Nation target)
    {
        if (nation.WarPlanTargetId != target.Id || nation.WarPlanSinceDay < 0)
        {
            nation.WarPlanTargetId = target.Id;
            nation.WarPlanSinceDay = c.ElapsedDays;
            Count("planned");
        }
        return c.ElapsedDays - nation.WarPlanSinceDay >= Math.Max(0, c.Defines.AiWarPreparationDays);
    }

    /// <summary>Uses controlled land and actual shipping capacity, not just GDP or capital distance.</summary>
    [ThreadStatic] private static int[] _reachStamp;
    [ThreadStatic] private static int _reachGeneration;
    [ThreadStatic] private static Queue<int> _reachQueue;

    /// <summary>
    /// Marks the land from which a division can march to the harbour - the harbour's own stretch
    /// of the country's ground, by the steps FindRoute takes - and returns the mark it left in
    /// <see cref="_reachStamp"/>, or -1 when FindRoute would refuse the harbour as a destination.
    /// </summary>
    private static int FloodHarbour(ProvinceTopology map, int harbour, ushort mapIndex, ushort[] controllers)
    {
        if (harbour <= 0 || harbour >= map.Neighbors.Length || controllers is null ||
            controllers.Length < map.Neighbors.Length || controllers[harbour] != mapIndex || map.IsWater(harbour))
            return -1;
        var reachStamp = _reachStamp;
        if (reachStamp is null || reachStamp.Length < map.Neighbors.Length)
            _reachStamp = reachStamp = new int[map.Neighbors.Length];
        if (_reachGeneration == int.MaxValue) { Array.Clear(reachStamp); _reachGeneration = 0; }
        var mark = ++_reachGeneration;
        reachStamp[harbour] = mark;
        var queue = _reachQueue ??= new Queue<int>(128);
        queue.Clear();
        queue.Enqueue(harbour);
        while (queue.Count > 0)
            foreach (var next in map.Neighbors[queue.Dequeue()])
            {
                if (reachStamp[next] == mark || controllers[next] != mapIndex || map.IsWater(next)) continue;
                reachStamp[next] = mark;
                queue.Enqueue(next);
            }
        return mark;
    }

    [ThreadStatic] private static List<Army> _stageFieldScratch;
    [ThreadStatic] private static List<float> _stageCourseScratch;

    public static bool CanStageWar(Campaign c, Nation nation, Nation target)
    {
        var map = c.ProvinceMap;
        if (map is null || c.ProvinceController is null || nation is null || target is null ||
            nation.Landless || target.Landless) return false;
        var field = _stageFieldScratch ??= new List<Army>(32);
        field.Clear();
        var armies = c.Armies;
        var nationId = nation.Id;
        for (var i = 0; i < armies.Count; i++)
        {
            var a = armies[i];
            if (a.NationId == nationId && a.TemplateId != MilitiaCore.TemplateId &&
                a.ProvinceId > 0 && !a.InBattle && a.EmbarkedFleetId < 0 && a.Strength > .25f &&
                a.Organisation >= .35f && a.SupplyLevel >= .35f)
                field.Add(a);
        }
        if (field.Count == 0) return false;
        var controllers = c.ProvinceController;
        var nationMap = (ushort)nation.MapIndex;
        var targetMap = (ushort)target.MapIndex;
        var border = BorderFronts.Candidates(c, nationId, target.Id);
        if (border.Count > 0)
        {
            for (var i = 0; i < field.Count; i++)
            {
                var prov = field[i].ProvinceId;
                if (border.Contains(prov) || map.FindRouteToAny(prov, border, nationMap, controllers).Length > 0)
                    return true;
            }
        }

        // Match the admiralty's search radius/basin. Having a frigate is not a
        // route to a landlocked capital, nor to a coast held by a third country.
        var course = _stageCourseScratch ??= new List<float>(32);
        var fleets = c.Fleets;
        var coastal = SeaLanes.CoastalProvinces(map);
        var minHulls = c.Defines.AiInvasionMinimumHulls;
        var searchDegrees = c.Defines.AiInvasionSearchDegrees;
        for (var fi = 0; fi < fleets.Count; fi++)
        {
            var fleet = fleets[fi];
            if (fleet.NationId != nationId || fleet.Hulls < minHulls ||
                fleet.HomeProvinceId <= 0 || controllers[fleet.HomeProvinceId] != nationMap) continue;
            var capacity = NavalInvasion.Capacity(c, fleet);
            // One flood from the harbour answers for every division: FindRoute walks the land the
            // country holds, and the graph runs both ways, so a division can march to the harbour
            // exactly when it stands in the harbour's stretch of that ground. Asking FindRoute for
            // each division in turn was four fifths of what this check cost.
            var reach = FloodHarbour(map, fleet.HomeProvinceId, nationMap, controllers);
            var loadable = false;
            for (var i = 0; i < field.Count; i++)
            {
                var a = field[i];
                if (a.Divisions <= capacity &&
                    (a.ProvinceId == fleet.HomeProvinceId ||
                     reach > 0 && a.ProvinceId > 0 && a.ProvinceId < _reachStamp.Length && _reachStamp[a.ProvinceId] == reach))
                {
                    loadable = true;
                    break;
                }
            }
            if (!loadable) continue;
            var basin = SeaLanes.HarbourBasin(map, fleet.HomeProvinceId);
            for (var ci = 0; ci < coastal.Count; ci++)
            {
                var coast = coastal[ci];
                if (controllers[coast] != targetMap || SeaLanes.HarbourBasin(map, coast) != basin ||
                    !SeaLanes.TryHarbourPoint(map, coast, out var lat, out var lon)) continue;
                if (NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude, lat, lon) > searchDegrees) continue;
                course.Clear();
                if (SeaLanes.Plan(c, nationId, fleet.HomeLatitude, fleet.HomeLongitude, lat, lon, course, out _)) return true;
            }
        }
        return false;
    }
}
