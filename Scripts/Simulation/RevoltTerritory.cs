using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A declaration does not defeat an occupying army. An uprising may replace
/// its parent's administration only on ground still held by that administration.
/// Third-party occupation retains both its title and control until an actual settlement.
/// This is not a military-access grant or automatic war against the new government.</summary>
internal static class RevoltTerritory
{
    internal static bool HasForeignOccupation(Campaign c, Nation parent, Nation claimant, ScenarioState region)
    {
        if (c?.ProvinceMap is not { } map || parent is null || claimant is null || region is null) return false;
        var control = c.ProvinceController ?? map.ProvinceToNation;
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (map.ProvinceToState[p] == region.MapIndex && map.ProvinceToNation[p] == parent.MapIndex &&
                control[p] != parent.MapIndex && control[p] != claimant.MapIndex) return true;
        return false;
    }

    internal static List<int> Select(Campaign c, Nation parent, ref List<SecessionPiece> leaving)
    {
        var map = c.ProvinceMap;
        var control = c.ProvinceController ?? map.ProvinceToNation;
        var selected = leaving.Select(p => p.MapIndex).ToHashSet();
        var total = new Dictionary<int, int>();
        var free = new Dictionary<int, int>();
        var ground = new List<int>();
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            var region = (int)map.ProvinceToState[p];
            if (!selected.Contains(region) || map.ProvinceToNation[p] != parent.MapIndex) continue;
            total[region] = total.GetValueOrDefault(region) + 1;
            if (control[p] != parent.MapIndex) continue;
            free[region] = free.GetValueOrDefault(region) + 1;
            ground.Add(p);
        }
        leaving = leaving.Where(p => free.GetValueOrDefault(p.MapIndex) > 0).Select(p => new SecessionPiece
        {
            MapIndex = p.MapIndex, Name = p.Name, Cause = p.Cause,
            Population = (long)MathF.Round(p.Population * (free[p.MapIndex] / (float)total[p.MapIndex]))
        }).ToList();
        return ground;
    }
}
