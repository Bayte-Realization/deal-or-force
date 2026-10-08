using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float MovementTerritorialResidentShare { get; set; } = .5f;
    /// <summary>Diplomatic utility lost per new disconnected component, not a ban on historical islands.</summary>
    public float AiBorderFragmentPenalty { get; set; } = 45f;
    public float AiBorderDetachedRegionPenalty { get; set; } = 18f;
    public float AiBorderSalientPenalty { get; set; } = 12f;
}

/// <summary>Administrative geography in AI judgement. Reads ownership, never temporary military control.</summary>
public static class TerritorialCoherence
{
    private static Campaign _campaign;
    private static int _revision = -1;
    private static readonly Dictionary<int, Dictionary<int, int>> Neighbours = new();

    private static void Ensure(Campaign c)
    {
        if (ReferenceEquals(_campaign, c) && _revision == c.MapRevision) return;
        Neighbours.Clear(); _campaign = c; _revision = c.MapRevision;
        var map = c.ProvinceMap;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            int a = map.ProvinceToState[p];
            if (a == 0) continue;
            if (!Neighbours.TryGetValue(a, out var next)) Neighbours[a] = next = new();
            foreach (var q in map.Neighbors[p])
            {
                int b = map.ProvinceToState[q];
                if (a != b && b != 0) next[b] = next.GetValueOrDefault(b) + 1;
            }
        }
    }

    public static bool TouchesOwnedGround(Campaign c, Nation nation, int region)
    {
        if (c?.ProvinceMap is null || nation is null) return false;
        Ensure(c);
        if (!Neighbours.TryGetValue(region, out var next)) return false;
        return c.States.Any(s => s.NationMapIndex == nation.MapIndex && next.ContainsKey(s.MapIndex));
    }

    /// <summary>Loss of utility from the complete territorial package, not a region-at-a-time preview.</summary>
    public static float Penalty(Campaign c, Nation nation, IEnumerable<int> gain, IEnumerable<int> yield)
    {
        var gains = (gain ?? Array.Empty<int>()).ToHashSet();
        var yields = (yield ?? Array.Empty<int>()).ToHashSet();
        if (c?.ProvinceMap is null || nation is null || gains.Count + yields.Count == 0) return 0;
        Ensure(c);
        var before = c.States.Where(s => s.NationMapIndex == nation.MapIndex && Neighbours.ContainsKey(s.MapIndex))
            .Select(s => s.MapIndex).ToHashSet();
        var after = before.Except(yields).Concat(gains.Where(Neighbours.ContainsKey)).ToHashSet();
        var oldParts = Components(before);
        var newParts = Components(after);
        var newFragments = Math.Max(0, newParts.Count - oldParts.Count);
        // A package restoring a genuine historical home may reconnect it in a later war.
        // A remote office or diplomatic invitation is not a historical title.
        var titled = gains.Where(id => c.States.Find(s => s.MapIndex == id) is { } s &&
            TerritorialClaims.TitleOf(c, nation, s) == TerritorialTitle.Homeland).ToHashSet();
        var legitimateRemoteParts = newParts.Count(part => !part.Overlaps(before) && part.Overlaps(titled));
        newFragments = Math.Max(0, newFragments - legitimateRemoteParts);
        var detached = newParts.Where(part => !part.Overlaps(before) && !part.Overlaps(titled)).Sum(part => part.Count);
        var salient = 0f;
        foreach (var id in gains)
        {
            if (titled.Contains(id) || !Neighbours.TryGetValue(id, out var next)) continue;
            var total = next.Values.Sum();
            var contact = next.Where(e => after.Contains(e.Key)).Sum(e => e.Value);
            // Filling a corner or a hole costs nothing; a thin spur has a real carrying cost.
            if (total > 0) salient += Math.Max(0, 1f - 2f * contact / total);
        }
        return newFragments * Weight(c.Defines.AiBorderFragmentPenalty) +
            detached * Weight(c.Defines.AiBorderDetachedRegionPenalty) +
            salient * Weight(c.Defines.AiBorderSalientPenalty);
    }

    private static float Weight(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1000) : 0;

    private static List<HashSet<int>> Components(HashSet<int> regions)
    {
        var unseen = new HashSet<int>(regions);
        var result = new List<HashSet<int>>();
        while (unseen.Count > 0)
        {
            var start = unseen.First(); unseen.Remove(start);
            var part = new HashSet<int> { start };
            var queue = new Queue<int>(); queue.Enqueue(start);
            while (queue.Count > 0)
            {
                if (!Neighbours.TryGetValue(queue.Dequeue(), out var next)) continue;
                foreach (var neighbour in next.Keys)
                    if (unseen.Remove(neighbour)) { part.Add(neighbour); queue.Enqueue(neighbour); }
            }
            result.Add(part);
        }
        return result;
    }
}
