using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

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
    // Region adjacency and who owns which region. Both are functions of ProvinceToState and
    // NationMapIndex. MapRevision also fires when a battle takes a province, and a long war
    // does that every day, so keying this walk on MapRevision rebuilt every neighbour edge
    // in the world for a border that had not moved. OwnershipRevision is the one that moves
    // when those arrays move. Each campaign keeps its own tables: the old static pair was
    // cleared by whichever world asked next.
    private sealed class Board
    {
        public ProvinceTopology Map;
        public int Revision = int.MinValue;
        public readonly Dictionary<int, Dictionary<int, int>> Neighbours = new();
        public readonly Dictionary<int, HashSet<int>> OwnedRegions = new();
        public readonly List<int> Stale = new();
    }

    private static readonly ConditionalWeakTable<Campaign, Board> Boards = new();

    /// <summary>How many times the province walk has run. The rule test reads it.</summary>
    internal static int AdjacencyBuilds { get; private set; }

    /// <summary>This campaign's region-adjacency table, or null before the first ask.</summary>
    internal static Dictionary<int, Dictionary<int, int>> NeighbourTables(Campaign campaign) =>
        campaign is not null && Boards.TryGetValue(campaign, out var board) ? board.Neighbours : null;

    private static Board Ensure(Campaign c)
    {
        var board = Boards.GetOrCreateValue(c);
        var map = c.ProvinceMap;
        if (map is null) return board;
        if (ReferenceEquals(board.Map, map) && board.Revision == c.OwnershipRevision) return board;
        board.Map = map;
        board.Revision = c.OwnershipRevision;
        AdjacencyBuilds++;

        foreach (var next in board.Neighbours.Values) next.Clear();
        foreach (var owned in board.OwnedRegions.Values) owned.Clear();
        foreach (var state in c.States)
        {
            if (state.NationMapIndex <= 0) continue;
            if (!board.OwnedRegions.TryGetValue(state.NationMapIndex, out var owned))
                board.OwnedRegions[state.NationMapIndex] = owned = new HashSet<int>();
            owned.Add(state.MapIndex);
        }
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            int a = map.ProvinceToState[p];
            if (a == 0) continue;
            if (!board.Neighbours.TryGetValue(a, out var next)) board.Neighbours[a] = next = new();
            foreach (var q in map.Neighbors[p])
            {
                int b = map.ProvinceToState[q];
                if (a != b && b != 0) next[b] = next.GetValueOrDefault(b) + 1;
            }
        }
        // A region that no longer touches anything must leave the table. An empty dictionary
        // left behind would still answer ContainsKey, and a package's component count would move.
        board.Stale.Clear();
        foreach (var (region, next) in board.Neighbours)
            if (next.Count == 0) board.Stale.Add(region);
        foreach (var region in board.Stale) board.Neighbours.Remove(region);
        return board;
    }

    public static bool TouchesOwnedGround(Campaign c, Nation nation, int region)
    {
        if (c?.ProvinceMap is null || nation is null) return false;
        var board = Ensure(c);
        if (!board.Neighbours.TryGetValue(region, out var next)) return false;
        if (!board.OwnedRegions.TryGetValue(nation.MapIndex, out var owned)) return false;
        foreach (var neighbour in next.Keys)
            if (owned.Contains(neighbour)) return true;
        return false;
    }

    private static readonly Dictionary<int, int> NoBorders = new();

    /// <summary>A region's neighbours with the length of the border to each, in province edges.</summary>
    public static IReadOnlyDictionary<int, int> RegionBorders(Campaign c, int region)
    {
        if (c?.ProvinceMap is null) return NoBorders;
        var board = Ensure(c);
        return board.Neighbours.TryGetValue(region, out var next) ? next : NoBorders;
    }

    /// <summary>Read-only region adjacency, shared by administrative packages and treaties.</summary>
    public static IEnumerable<int> AdjacentRegions(Campaign c, int region)
    {
        if (c?.ProvinceMap is null) return Array.Empty<int>();
        var board = Ensure(c);
        return board.Neighbours.TryGetValue(region, out var next)
            ? next.Keys.OrderBy(id => id) : Enumerable.Empty<int>();
    }

    /// <summary>Loss of utility from the complete territorial package, not a region-at-a-time preview.</summary>
    public static float Penalty(Campaign c, Nation nation, IEnumerable<int> gain, IEnumerable<int> yield)
    {
        var gains = (gain ?? Array.Empty<int>()).ToHashSet();
        var yields = (yield ?? Array.Empty<int>()).ToHashSet();
        if (c?.ProvinceMap is null || nation is null || gains.Count + yields.Count == 0) return 0;
        var neighbours = Ensure(c).Neighbours;
        var before = c.States.Where(s => s.NationMapIndex == nation.MapIndex && neighbours.ContainsKey(s.MapIndex))
            .Select(s => s.MapIndex).ToHashSet();
        var after = before.Except(yields).Concat(gains.Where(neighbours.ContainsKey)).ToHashSet();
        var oldParts = Components(neighbours, before);
        var newParts = Components(neighbours, after);
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
            if (titled.Contains(id) || !neighbours.TryGetValue(id, out var next)) continue;
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

    private static List<HashSet<int>> Components(Dictionary<int, Dictionary<int, int>> neighbours, HashSet<int> regions)
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
                if (!neighbours.TryGetValue(queue.Dequeue(), out var next)) continue;
                foreach (var neighbour in next.Keys)
                    if (unseen.Remove(neighbour)) { part.Add(neighbour); queue.Enqueue(neighbour); }
            }
            result.Add(part);
        }
        return result;
    }
}
