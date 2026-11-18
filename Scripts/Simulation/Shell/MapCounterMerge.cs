using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation.Shell;

public enum MapZoomTier
{
    StrategicFar,
    OperationalMid,
    TacticalClose
}

public sealed record MapCounterInput(
    int Id,
    int NationId,
    string NationTag,
    int RegionId,
    int FrontId,
    double Latitude,
    double Longitude,
    float Strength,
    bool IsPlayer,
    bool IsAlly,
    bool IsAtWarEnemy,
    bool IsSelected,
    bool IsInspectedNation);

public sealed record MapCounterCluster(
    string ClusterId,
    int NationId,
    string NationTag,
    int MemberCount,
    float TotalStrength,
    double Latitude,
    double Longitude,
    int RepresentativeId,
    int FrontId,
    int RegionId,
    bool ContainsSelection);

/// <summary>
/// Zoom-aware deterministic merging and <= 20 sign budget enforcement for map counters.
/// Keeps CampaignFingerprint untouched and hides non-essential foreign peacetime stacks.
/// </summary>
public static class MapCounterMerge
{
    public static MapZoomTier ZoomTier(float distance, bool flatMap, float flatZoom)
    {
        if (flatMap)
        {
            if (flatZoom >= 4.2f) return MapZoomTier.StrategicFar;
            if (flatZoom >= 1.5f) return MapZoomTier.OperationalMid;
            return MapZoomTier.TacticalClose;
        }
        if (distance >= 18f) return MapZoomTier.StrategicFar;
        if (distance >= 8f) return MapZoomTier.OperationalMid;
        return MapZoomTier.TacticalClose;
    }

    public static IReadOnlyList<MapCounterCluster> Merge(
        IReadOnlyList<MapCounterInput> inputs,
        MapZoomTier tier,
        bool warMapMode = false,
        int alertPinsCount = 0,
        int maxSigns = 20)
    {
        if (inputs is null || inputs.Count == 0) return Array.Empty<MapCounterCluster>();

        var signCap = Math.Max(1, maxSigns - Math.Max(0, alertPinsCount));
        var visible = new List<MapCounterInput>(inputs.Count);
        foreach (var item in inputs)
        {
            if (warMapMode || item.IsPlayer || item.IsAlly || item.IsAtWarEnemy || item.IsSelected || item.IsInspectedNation)
                visible.Add(item);
        }
        if (visible.Count == 0) return Array.Empty<MapCounterCluster>();

        var buckets = new Dictionary<string, List<MapCounterInput>>(StringComparer.Ordinal);
        foreach (var item in visible)
        {
            var key = tier switch
            {
                MapZoomTier.StrategicFar => item.IsSelected
                    ? $"sel:{item.Id}"
                    : item.FrontId >= 0
                        ? $"far:{item.NationId}:f{item.FrontId}"
                        : $"far:{item.NationId}:home",
                MapZoomTier.OperationalMid => item.IsSelected
                    ? $"sel:{item.Id}"
                    : item.RegionId >= 0
                        ? $"mid:{item.NationId}:r{item.RegionId}"
                        : $"mid:{item.NationId}:home",
                _ => $"close:{item.NationId}:r{item.RegionId}:{(int)Math.Round(item.Latitude * 2)}:{(int)Math.Round(item.Longitude * 2)}:{(item.IsSelected ? item.Id : 0)}"
            };
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<MapCounterInput>(4);
                buckets[key] = list;
            }
            list.Add(item);
        }

        var clusters = buckets
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => BuildCluster(kv.Key, kv.Value))
            .ToList();

        // Hard map budget cap <= 20 signs total (including alert pins)
        while (clusters.Count > signCap)
        {
            var merged = TryMergeWeakestPair(clusters);
            if (!merged) break;
        }

        return clusters;
    }

    private static MapCounterCluster BuildCluster(string key, List<MapCounterInput> list)
    {
        var totalStr = 0f;
        var sumLat = 0d;
        var sumLon = 0d;
        var selected = false;
        var rep = list[0];
        foreach (var m in list)
        {
            totalStr += Math.Max(0f, m.Strength);
            sumLat += m.Latitude;
            sumLon += m.Longitude;
            if (m.IsSelected) selected = true;
            if (m.Strength > rep.Strength || (m.IsSelected && !rep.IsSelected))
                rep = m;
        }
        return new MapCounterCluster(
            key,
            rep.NationId,
            rep.NationTag ?? "",
            list.Count,
            totalStr,
            sumLat / list.Count,
            sumLon / list.Count,
            rep.Id,
            rep.FrontId,
            rep.RegionId,
            selected);
    }

    private static bool TryMergeWeakestPair(List<MapCounterCluster> clusters)
    {
        var bestI = -1;
        var bestJ = -1;
        var bestScore = double.MaxValue;

        for (var i = 0; i < clusters.Count; i++)
        {
            for (var j = i + 1; j < clusters.Count; j++)
            {
                var a = clusters[i];
                var b = clusters[j];
                if (a.NationId != b.NationId) continue;
                var dLat = a.Latitude - b.Latitude;
                var dLon = a.Longitude - b.Longitude;
                var score = dLat * dLat + dLon * dLon + (a.ContainsSelection || b.ContainsSelection ? 100000d : 0d);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestI = i;
                    bestJ = j;
                }
            }
        }

        if (bestI < 0)
        {
            // Fold weakest non-selected pair of lowest strength if every nation only has 1 cluster
            var lowest = clusters
                .Select((c, idx) => (c, idx))
                .Where(t => !t.c.ContainsSelection)
                .OrderBy(t => t.c.TotalStrength)
                .Select(t => t.idx)
                .FirstOrDefault(-1);
            if (lowest < 0) return false;
            clusters.RemoveAt(lowest);
            return true;
        }

        var ca = clusters[bestI];
        var cb = clusters[bestJ];
        var count = ca.MemberCount + cb.MemberCount;
        var merged = new MapCounterCluster(
            ca.ClusterId + "+" + cb.RepresentativeId,
            ca.NationId,
            ca.NationTag,
            count,
            ca.TotalStrength + cb.TotalStrength,
            (ca.Latitude * ca.MemberCount + cb.Latitude * cb.MemberCount) / count,
            (ca.Longitude * ca.MemberCount + cb.Longitude * cb.MemberCount) / count,
            ca.TotalStrength >= cb.TotalStrength ? ca.RepresentativeId : cb.RepresentativeId,
            ca.FrontId >= 0 ? ca.FrontId : cb.FrontId,
            ca.RegionId >= 0 ? ca.RegionId : cb.RegionId,
            ca.ContainsSelection || cb.ContainsSelection);

        clusters[bestI] = merged;
        clusters.RemoveAt(bestJ);
        return true;
    }
}
