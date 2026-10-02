using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Offline border repair: keep province IDs and reserve a surviving seed for every province.</summary>
public static class StableProvinceBorders
{
    public static ProvinceTopology Reconcile(ProvinceTopology original, ushort[] states, ushort[] owners)
    {
        if (states.Length != original.CellToProvince.Length || owners.Length != states.Length)
            throw new InvalidDataException("Political rasters must match the province map.");
        var cells = original.CellToProvince.Select(p => (int)p).ToArray();
        var counts = new Dictionary<(int Province, ushort State, ushort Owner), int>();
        var regions = (ushort[])original.ProvinceToState.Clone();
        var nations = (ushort[])original.ProvinceToNation.Clone();
        for (var i = 0; i < cells.Length; i++)
        {
            var p = cells[i];
            if (p == 0)
            {
                if (states[i] != 0) throw new InvalidDataException("Stable repair cannot create new land or lake provinces.");
                continue;
            }
            if (states[i] == 0) throw new InvalidDataException("Stable repair cannot erase a province into ocean.");
            var key = (p, states[i], owners[i]); counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        foreach (var group in counts.GroupBy(e => e.Key.Province))
        {
            var best = group.OrderByDescending(e => e.Value).ThenBy(e => e.Key.State).ThenBy(e => e.Key.Owner).First().Key;
            regions[best.Province] = best.State; nations[best.Province] = best.Owner;
        }
        // Every desired political region needs at least one existing ID. Reserve one
        // without stealing the last representative of any other region.
        foreach (var key in counts.Keys.Select(k => (k.State, k.Owner)).Distinct().OrderBy(k => k.State).ThenBy(k => k.Owner))
        {
            if (Enumerable.Range(1, original.ProvinceCount).Any(p => regions[p] == key.State && nations[p] == key.Owner)) continue;
            var candidate = counts.Where(e => e.Key.State == key.State && e.Key.Owner == key.Owner)
                .OrderByDescending(e => e.Value).ThenBy(e => e.Key.Province)
                .FirstOrDefault(e => Enumerable.Range(1, original.ProvinceCount).Count(p => regions[p] == regions[e.Key.Province] && nations[p] == nations[e.Key.Province]) > 1);
            if (candidate.Value == 0) throw new InvalidDataException("Border repair would need a new province ID.");
            regions[candidate.Key.Province] = key.State; nations[candidate.Key.Province] = key.Owner;
        }
        var seeds = Enumerable.Repeat(-1, original.ProvinceCount + 1).ToArray();
        var distances = Enumerable.Repeat(long.MaxValue, seeds.Length).ToArray();
        for (var i = 0; i < cells.Length; i++)
        {
            var p = cells[i]; if (p == 0 || regions[p] != states[i] || nations[p] != owners[i]) continue;
            var centre = original.ProvinceCenterCell[p];
            var dx = i % original.Width - centre % original.Width; var dy = i / original.Width - centre / original.Width;
            var distance = (long)dx * dx + (long)dy * dy;
            if (distance < distances[p]) { distances[p] = distance; seeds[p] = i; }
        }
        if (seeds.Skip(1).Any(s => s < 0)) throw new InvalidDataException("A province would disappear during border repair.");
        var candidates = Enumerable.Range(1, original.ProvinceCount).GroupBy(p => (regions[p], nations[p]))
            .ToDictionary(g => g.Key, g => g.ToArray());
        for (var i = 0; i < cells.Length; i++)
        {
            var p = cells[i]; if (p == 0 || regions[p] == states[i] && nations[p] == owners[i]) continue;
            var x = i % original.Width; var y = i / original.Width;
            cells[i] = candidates[(states[i], owners[i])].MinBy(candidate =>
            {
                var seed = seeds[candidate]; var dx = x - seed % original.Width; var dy = y - seed / original.Width;
                return (long)dx * dx + (long)dy * dy;
            });
        }
        return original.WithGeometry(cells, regions, nations);
    }
}
