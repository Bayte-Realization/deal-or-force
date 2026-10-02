using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A front is anchored to a country pair and geographic endpoints, not old province IDs.</summary>
public static class BorderFronts
{
    public static HashSet<int> Candidates(Campaign campaign, int nationId, int foreignId)
    {
        var result = new HashSet<int>();
        var map = campaign.ProvinceMap;
        var own = campaign.NationById(nationId);
        var other = campaign.NationById(foreignId);
        if (map is null || own is null || other is null || own == other) return result;
        var control = campaign.ProvinceController ?? map.ProvinceToNation;
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (!map.IsWater(p) && control[p] == own.MapIndex &&
                map.Neighbors[p].Any(n => !map.IsWater(n) && control[n] == other.MapIndex)) result.Add(p);
        return result;
    }

    public static int ForeignAt(Campaign campaign, int nationId, int province)
    {
        var map = campaign.ProvinceMap;
        var nation = campaign.NationById(nationId);
        if (map is null || nation is null || province <= 0 || province > map.ProvinceCount) return -1;
        var control = campaign.ProvinceController ?? map.ProvinceToNation;
        var owner = control[province];
        if (owner != nation.MapIndex && !map.Neighbors[province].Any(n => control[n] == nation.MapIndex)) return -1;
        var foreign = owner != nation.MapIndex && map.Neighbors[province].Any(n => control[n] == nation.MapIndex)
            ? owner : map.Neighbors[province].Where(n => !map.IsWater(n) && control[n] != 0 && control[n] != nation.MapIndex)
                .Select(n => control[n]).OrderBy(n => n).FirstOrDefault();
        return campaign.Nations.FirstOrDefault(n => n.MapIndex == foreign)?.Id ?? -1;
    }

    public static HashSet<int> Resolve(Campaign campaign, DrawnFrontLine line)
    {
        var available = Candidates(campaign, line.NationId, line.BorderNationId);
        if (available.Count == 0) return available;
        var map = campaign.ProvinceMap;
        var start = Nearest(map, available, line.StartLatitude, line.StartLongitude);
        var end = Nearest(map, available, line.EndLatitude, line.EndLongitude);
        var queue = new Queue<int>();
        var previous = new Dictionary<int, int> { [start] = -1 };
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var next in map.Neighbors[p].OrderBy(n => n))
                if (available.Contains(next) && previous.TryAdd(next, p)) queue.Enqueue(next);
        }
        // Never bridge separate borders across sea or neutral land.
        if (line.WholeBorder) return previous.Keys.ToHashSet();
        if (!previous.ContainsKey(end)) end = Nearest(map, previous.Keys, line.EndLatitude, line.EndLongitude);
        var result = new HashSet<int>();
        for (var p = end; p > 0; p = previous[p]) result.Add(p);
        return result;
    }

    private static int Nearest(ProvinceTopology map, IEnumerable<int> candidates, float lat, float lon) =>
        candidates.OrderBy(p => Distance(map.CenterOf(p), lat, lon)).ThenBy(p => p).First();
    private static double Distance((float Latitude, float Longitude) point, float lat, float lon)
    {
        var dx = Math.Abs(point.Longitude - lon);
        dx = MathF.Min(dx, 360 - dx) * MathF.Cos(lat * MathF.PI / 180);
        var dy = point.Latitude - lat;
        return dx * dx + dy * dy;
    }

    public static void Refresh(Campaign campaign, DrawnFrontLine line)
    {
        if (line.Kind == FrontLineKind.Offensive && line.ObjectiveSequence.Count > 0 && line.BorderNationId >= 0)
        {
            if (line.BorderRevision == campaign.MapRevision) return;
            line.SourceProvinces.Clear();
            line.SourceProvinces.UnionWith(Resolve(campaign, line));
            line.PostsDay = int.MinValue;
            line.BorderRevision = campaign.MapRevision;
            return;
        }
        if (line.Kind != FrontLineKind.Defence || line.BorderNationId < 0 ||
            line.BorderRevision == campaign.MapRevision) return;
        line.Provinces.Clear();
        line.Provinces.UnionWith(Resolve(campaign, line));
        line.PostsDay = int.MinValue;
        line.BorderRevision = campaign.MapRevision;
    }
}
