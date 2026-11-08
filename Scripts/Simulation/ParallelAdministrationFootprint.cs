using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static class ParallelAdministrationFootprint
{
    private static int[] Ground(Campaign c, ScenarioState region) => region.AdministrationProvinces.Count > 0
        ? region.AdministrationProvinces.OrderBy(p => p).ToArray()
        : Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => c.ProvinceMap.ProvinceToState[p] == region.MapIndex).ToArray();

    /// <summary>Before rebinding province IDs. No office acquires ground in an unserved sibling.</summary>
    internal static void Merge(Campaign c, ScenarioState into, ScenarioState from,
        IReadOnlyList<int> intoGround, IReadOnlyList<int> fromGround, long intoPeople, long fromPeople)
    {
        var left = into.AdministeredById >= 0;
        var right = from.AdministeredById >= 0;
        if (!left && !right) return;
        var footprint = new SortedSet<int>();
        if (left) footprint.UnionWith(into.AdministrationProvinces.Count > 0 ? into.AdministrationProvinces : intoGround ?? Array.Empty<int>());
        if (right) footprint.UnionWith(from.AdministrationProvinces.Count > 0 ? from.AdministrationProvinces : fromGround ?? Array.Empty<int>());
        var covered = (left ? intoPeople * (double)ParallelAdministration.Coverage(into) : 0) +
            (right ? fromPeople * (double)ParallelAdministration.Coverage(from) : 0);
        if (!left)
        {
            into.AdministeredById = from.AdministeredById;
            into.AdministeredSinceDay = from.AdministeredSinceDay;
            into.AdministrationExposed = from.AdministrationExposed;
        }
        else if (right)
        {
            into.AdministeredSinceDay = Math.Min(into.AdministeredSinceDay, from.AdministeredSinceDay);
            into.AdministrationExposed |= from.AdministrationExposed;
        }
        into.AdministrationProvinces = footprint.ToList();
        into.AdministrationPopulationShare = intoPeople + fromPeople > 0
            ? M.Clamp01((float)(covered / (intoPeople + fromPeople))) : 1f;
    }

    /// <summary>Split only the existing office footprint when the political border really changes.</summary>
    internal static void Carve(Campaign c, ScenarioState parent, ScenarioState child, IReadOnlyList<int> piece)
    {
        if (parent.AdministeredById < 0) return;
        var original = Ground(c, parent);
        var moved = original.Intersect(piece).OrderBy(p => p).ToList();
        var remaining = original.Except(piece).OrderBy(p => p).ToList();
        var coveredPeople = (parent.Population + child.Population) * (double)ParallelAdministration.Coverage(parent);
        var movedShare = original.Length > 0 ? moved.Count / (double)original.Length : 0;
        var movedTaken = parent.AdministrationTaken * (float)movedShare;
        if (moved.Count > 0 && c.NationByMapIndex(child.NationMapIndex)?.Id != parent.AdministeredById)
        {
            child.AdministeredById = parent.AdministeredById;
            child.AdministeredSinceDay = parent.AdministeredSinceDay;
            child.AdministrationExposed = parent.AdministrationExposed;
            child.AdministrationProvinces = moved;
            child.AdministrationPopulationShare = child.Population > 0
                ? M.Clamp01((float)(coveredPeople * movedShare / child.Population)) : 0;
            child.AdministrationTaken = movedTaken;
        }
        parent.AdministrationTaken -= movedTaken;
        if (remaining.Count == 0)
        {
            parent.AdministeredById = -1; parent.AdministeredSinceDay = -1; parent.AdministrationExposed = false;
            parent.AdministrationProvinces.Clear(); parent.AdministrationPopulationShare = 1f;
        }
        else
        {
            parent.AdministrationProvinces = remaining;
            parent.AdministrationPopulationShare = parent.Population > 0
                ? M.Clamp01((float)(coveredPeople * (1 - movedShare) / parent.Population)) : 0;
        }
    }
}
