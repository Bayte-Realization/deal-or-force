using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.LanguageBench;

/// <summary>
/// The ownership kernel's full-materialisation stage, copied from Tools/OwnershipKernelBench so
/// the earlier measurement stays as it is: the original all-owner BFS from the Corridors pore
/// cache (the C# as the game wrote it), and the HashSet/rim/pore materialisation every CSR
/// contender's batch is turned into, with the same digest.
/// </summary>
internal static class OwnershipReference
{
    public sealed class Materialized
    {
        public readonly Dictionary<int, List<HashSet<int>>> Owners = new();
        public readonly Dictionary<int, int> FirstProvince = new();
        public readonly Dictionary<int, HashSet<int>> Pores = new();
    }

    public static int PoreLimit(Campaign campaign) =>
        typeof(Defines).GetProperty("PeaceCompactHoleProvinces")?.GetValue(campaign.Defines) is int value ? Math.Max(1, value) : 3;

    public static Materialized OriginalAllOwnerBfs(ProvinceTopology map, int limit)
    {
        var result = new Materialized();
        var seen = new bool[map.ProvinceCount + 1]; var queue = new Queue<int>();
        for (var start = 1; start <= map.ProvinceCount; start++)
        {
            if (seen[start] || map.IsWater(start)) continue;
            var owner = map.ProvinceToNation[start];
            if (owner == 0) { seen[start] = true; continue; }
            var cluster = new HashSet<int>(); var rim = new HashSet<int>(); var open = false;
            queue.Clear(); queue.Enqueue(start); seen[start] = true;
            while (queue.Count > 0)
            {
                var province = queue.Dequeue(); cluster.Add(province);
                var state = map.ProvinceToState[province];
                if (state != 0 && !result.FirstProvince.ContainsKey(state)) result.FirstProvince[state] = province;
                foreach (var next in map.Neighbors[province])
                {
                    if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                    var who = map.ProvinceToNation[next];
                    if (who == owner)
                    {
                        if (!seen[next]) { seen[next] = true; queue.Enqueue(next); }
                        continue;
                    }
                    if (who == 0) { open = true; continue; }
                    rim.Add(who);
                }
            }
            Add(result, owner, cluster, rim, open, limit);
        }
        return result;
    }

    public static Materialized Materialize(ProvinceTopology map, OwnershipComponentBatch batch, int limit)
    {
        var result = new Materialized();
        for (var component = 0; component < batch.ComponentCount; component++)
        {
            var begin = batch.ComponentOffsets[component]; var end = batch.ComponentOffsets[component + 1];
            var owner = map.ProvinceToNation[batch.MemberOrder[begin]];
            var cluster = new HashSet<int>(); var rim = new HashSet<int>(); var open = false;
            for (var index = begin; index < end; index++)
            {
                var province = batch.MemberOrder[index]; cluster.Add(province);
                var state = map.ProvinceToState[province];
                if (state != 0 && !result.FirstProvince.ContainsKey(state)) result.FirstProvince[state] = province;
                foreach (var next in map.Neighbors[province])
                {
                    if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                    var who = map.ProvinceToNation[next];
                    if (who == owner) continue;
                    if (who == 0) { open = true; continue; }
                    rim.Add(who);
                }
            }
            Add(result, owner, cluster, rim, open, limit);
        }
        return result;
    }

    private static void Add(Materialized result, int owner, HashSet<int> cluster, HashSet<int> rim, bool open, int limit)
    {
        if (!result.Owners.TryGetValue(owner, out var list)) result.Owners[owner] = list = new();
        list.Add(cluster);
        if (open || cluster.Count == 0 || cluster.Count > limit || rim.Count != 1) return;
        var surrounder = rim.First();
        if (!result.Pores.TryGetValue(surrounder, out var best) || cluster.Count < best.Count) result.Pores[surrounder] = cluster;
    }

    public static ulong Digest(Materialized result)
    {
        var hash = 14695981039346656037UL;
        void Mix(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211UL; } }
        foreach (var (owner, pieces) in result.Owners)
        {
            Mix(-1); Mix(owner); Mix(pieces.Count);
            foreach (var piece in pieces) { Mix(-2); Mix(piece.Count); foreach (var province in piece) Mix(province); }
        }
        foreach (var (state, province) in result.FirstProvince) { Mix(-3); Mix(state); Mix(province); }
        foreach (var (surrounder, pore) in result.Pores)
        { Mix(-4); Mix(surrounder); Mix(pore.Count); foreach (var province in pore) Mix(province); }
        return hash;
    }

    public static ulong BatchDigest(OwnershipComponentBatch batch)
    {
        var hash = 14695981039346656037UL;
        void Mix(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211UL; } }
        Mix(batch.ComponentCount); Mix(batch.MemberCount);
        foreach (var label in batch.Labels) Mix(label);
        for (var at = 0; at < batch.MemberCount; at++) Mix(batch.MemberOrder[at]);
        for (var at = 0; at <= batch.ComponentCount; at++) Mix(batch.ComponentOffsets[at]);
        return hash;
    }
}
