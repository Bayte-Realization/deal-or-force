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
    public static bool CanStageWar(Campaign c, Nation nation, Nation target)
    {
        var map = c.ProvinceMap;
        if (map is null || c.ProvinceController is null || nation is null || target is null ||
            nation.Landless || target.Landless) return false;
        var field = c.Armies.Where(a => a.NationId == nation.Id && a.TemplateId != MilitiaCore.TemplateId &&
            a.ProvinceId > 0 && !a.InBattle && a.EmbarkedFleetId < 0 && a.Strength > .25f &&
            a.Organisation >= .35f && a.SupplyLevel >= .35f).ToArray();
        if (field.Length == 0) return false;
        var border = BorderFronts.Candidates(c, nation.Id, target.Id);
        if (border.Count > 0)
            foreach (var army in field)
                if (border.Contains(army.ProvinceId) || map.FindRouteToAny(army.ProvinceId, border,
                    (ushort)nation.MapIndex, c.ProvinceController).Length > 0) return true;

        // Match the admiralty's search radius/basin. Having a frigate is not a
        // route to a landlocked capital, nor to a coast held by a third country.
        var course = new List<float>();
        foreach (var fleet in c.Fleets)
        {
            if (fleet.NationId != nation.Id || fleet.Hulls < c.Defines.AiInvasionMinimumHulls ||
                fleet.HomeProvinceId <= 0 || c.ProvinceController[fleet.HomeProvinceId] != nation.MapIndex) continue;
            var capacity = NavalInvasion.Capacity(c, fleet);
            var loadable = field.Any(a => a.Divisions <= capacity &&
                (a.ProvinceId == fleet.HomeProvinceId || map.FindRoute(a.ProvinceId, fleet.HomeProvinceId,
                    (ushort)nation.MapIndex, c.ProvinceController).Length > 0));
            if (!loadable) continue;
            var basin = SeaLanes.HarbourBasin(map, fleet.HomeProvinceId);
            foreach (var coast in SeaLanes.CoastalProvinces(map))
            {
                if (c.ProvinceController[coast] != target.MapIndex || SeaLanes.HarbourBasin(map, coast) != basin ||
                    !SeaLanes.TryHarbourPoint(map, coast, out var lat, out var lon)) continue;
                if (NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude, lat, lon) > c.Defines.AiInvasionSearchDegrees) continue;
                if (SeaLanes.Plan(c, nation.Id, fleet.HomeLatitude, fleet.HomeLongitude, lat, lon, course, out _)) return true;
            }
        }
        return false;
    }
}
