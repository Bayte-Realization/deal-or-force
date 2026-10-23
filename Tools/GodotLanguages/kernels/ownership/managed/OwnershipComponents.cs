using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>Tools-only integer CSR experiment; not compiled into the game.</summary>
internal sealed class OwnershipComponentGraph
{
    internal const int MaximumVertices = 1_000_000;
    internal const int MaximumEdges = 16_000_000;
    internal readonly int[] Offsets;
    internal readonly int[] Neighbors;
    internal int VertexCount => Offsets.Length - 1;
    internal int EdgeCount => Neighbors.Length;

    private OwnershipComponentGraph(int[] offsets, int[] neighbors)
    { Offsets = offsets; Neighbors = neighbors; }

    internal static OwnershipComponentGraph FromNeighbors(int[][] adjacency)
    {
        if (adjacency is null || adjacency.Length < 1 || adjacency.Length > MaximumVertices)
            throw new ArgumentException("Graph must include sentinel vertex zero and stay within the kernel budget.", nameof(adjacency));
        var offsets = new int[adjacency.Length + 1];
        var count = 0;
        for (var vertex = 0; vertex < adjacency.Length; vertex++)
        {
            if (adjacency[vertex] is null) throw new ArgumentException("Null adjacency row.", nameof(adjacency));
            foreach (var next in adjacency[vertex])
                if (next > 0 && next < adjacency.Length)
                {
                    if (count == MaximumEdges) throw new ArgumentException("Graph exceeds the edge budget.", nameof(adjacency));
                    count++;
                }
            offsets[vertex + 1] = count;
        }
        var neighbors = new int[count];
        var write = 0;
        foreach (var row in adjacency)
            foreach (var next in row)
                if (next > 0 && next < adjacency.Length) neighbors[write++] = next;
        return new OwnershipComponentGraph(offsets, neighbors);
    }

    internal void ValidateInputs(ushort[] owners, byte[] water)
    {
        if (owners is null || owners.Length != VertexCount)
            throw new ArgumentException("Ownership length must match the graph.", nameof(owners));
        if (water is null || water.Length != VertexCount)
            throw new ArgumentException("Water length must match the graph.", nameof(water));
        foreach (var flag in water)
            if (flag > 1) throw new ArgumentException("Water flags must be zero or one.", nameof(water));
    }

    internal OwnershipComponentBatch ComputeManaged(ushort[] owners, byte[] water)
    {
        ValidateInputs(owners, water);
        var result = new OwnershipComponentBatch(VertexCount);
        FillManaged(owners, water, result);
        return result;
    }

    internal OwnershipComponentBatch Compute(ushort[] owners, byte[] water, NativeOwnershipKernel native)
    {
        ValidateInputs(owners, water);
        var result = new OwnershipComponentBatch(VertexCount);
        if (native is not null && native.IsEnabled)
        {
            if (native.TryCompute(this, owners, water, result)) return result;
            // A failed native call may have written scratch. Discard the entire partial batch.
            Array.Clear(result.Labels);
            Array.Clear(result.MemberOrder);
            Array.Clear(result.ComponentOffsets);
        }
        FillManaged(owners, water, result);
        return result;
    }

    private void FillManaged(ushort[] owners, byte[] water, OwnershipComponentBatch result)
    {
        var labels = result.Labels;
        var members = result.MemberOrder;
        var components = 0;
        var tail = 0;
        for (var start = 1; start < VertexCount; start++)
        {
            if (labels[start] != 0 || owners[start] == 0 || water[start] != 0) continue;
            var owner = owners[start];
            var head = tail;
            labels[start] = ++components;
            members[tail++] = start;
            while (head < tail)
            {
                var vertex = members[head++];
                for (var edge = Offsets[vertex]; edge < Offsets[vertex + 1]; edge++)
                {
                    var next = Neighbors[edge];
                    if (labels[next] != 0 || owners[next] != owner || water[next] != 0) continue;
                    labels[next] = components;
                    members[tail++] = next;
                }
            }
            result.ComponentOffsets[components] = tail;
        }
        result.ComponentCount = components;
        result.MemberCount = tail;
    }
}

/// <summary>Caller-owned scratch, never serialized or included in the world fingerprint.</summary>
internal sealed class OwnershipComponentBatch
{
    internal int[] Labels { get; }
    internal int[] MemberOrder { get; }
    internal int[] ComponentOffsets { get; }
    internal int ComponentCount { get; set; }
    internal int MemberCount { get; set; }

    internal OwnershipComponentBatch(int vertices)
    {
        Labels = new int[vertices];
        MemberOrder = new int[vertices];
        ComponentOffsets = new int[vertices + 1];
    }

    // Structural validation of a trusted native implementation, not a second BFS or
    // an adversarial-code sandbox. Exact traversal semantics have differential tests.
    internal bool IsStructurallyValid(ushort[] owners, byte[] water)
    {
        var vertices = owners.Length;
        if (ComponentCount < 0 || MemberCount < ComponentCount || MemberCount >= vertices ||
            ComponentCount >= vertices || ComponentOffsets[0] != 0 || Labels[0] != 0 ||
            ComponentOffsets[ComponentCount] != MemberCount) return false;
        var seen = new bool[vertices];
        var lastRoot = 0;
        for (var component = 1; component <= ComponentCount; component++)
        {
            var begin = ComponentOffsets[component - 1];
            var end = ComponentOffsets[component];
            if (begin < 0 || end <= begin || end > MemberCount) return false;
            var root = MemberOrder[begin];
            if (root <= lastRoot || root >= vertices) return false;
            lastRoot = root;
            var owner = owners[root];
            for (var index = begin; index < end; index++)
            {
                var vertex = MemberOrder[index];
                if (vertex < root || vertex >= vertices || seen[vertex] || owner == 0 ||
                    water[vertex] != 0 || owners[vertex] != owner || Labels[vertex] != component) return false;
                seen[vertex] = true;
            }
        }
        for (var vertex = 1; vertex < vertices; vertex++)
        {
            var included = owners[vertex] != 0 && water[vertex] == 0;
            if (included != seen[vertex] || (!included && Labels[vertex] != 0)) return false;
        }
        return true;
    }
}

/// <summary>Per-world ownership revision cache; occupation, RNG and commands are untouched.</summary>
internal static class OwnershipComponents
{
    private sealed class Entry
    {
        internal readonly object Gate = new();
        internal ProvinceTopology Map;
        internal int Revision;
        internal OwnershipComponentBatch Batch;
    }

    private static readonly ConditionalWeakTable<ProvinceTopology, OwnershipComponentGraph> Graphs = new();
    private static readonly ConditionalWeakTable<Campaign, Entry> Worlds = new();
    private static readonly Lazy<NativeOwnershipKernel> Native = new(SelectNative);
    internal static string BackendDiagnostic => Native.Value?.Diagnostic ?? "managed (native kernel not enabled)";

    private static NativeOwnershipKernel SelectNative()
    {
        var mode = Environment.GetEnvironmentVariable("DOF_EXPERIMENTAL_KERNEL")?.ToLowerInvariant();
        if (mode is not ("rust" or "odin" or "nim")) return null;
        if (NativeOwnershipKernel.TryOpen(Environment.GetEnvironmentVariable("DOF_EXPERIMENTAL_KERNEL_DIRECTORY"), out var kernel, out var reason)) return kernel;
        Console.Error.WriteLine("EXPERIMENTAL_KERNEL managed fallback: " + reason);
        return null;
    }

    internal static OwnershipComponentGraph ForMap(ProvinceTopology map) =>
        Graphs.GetValue(map ?? throw new ArgumentNullException(nameof(map)), value => OwnershipComponentGraph.FromNeighbors(value.Neighbors));

    internal static byte[] PackWater(ProvinceTopology map)
    {
        var water = new byte[map.ProvinceCount + 1];
        for (var vertex = 1; vertex < water.Length; vertex++) water[vertex] = map.IsWater(vertex) ? (byte)1 : (byte)0;
        return water;
    }

    internal static OwnershipComponentBatch Read(Campaign campaign)
    {
        if (campaign?.ProvinceMap is not { } map) throw new ArgumentException("Campaign has no province topology.", nameof(campaign));
        var entry = Worlds.GetValue(campaign, _ => new Entry());
        lock (entry.Gate)
        {
            if (ReferenceEquals(entry.Map, map) && entry.Revision == campaign.MapRevision && entry.Batch is not null) return entry.Batch;
            var batch = ForMap(map).Compute(map.ProvinceToNation, PackWater(map), Native.Value);
            // Publish a complete result only. No cache is shared between two campaigns.
            entry.Map = map;
            entry.Revision = campaign.MapRevision;
            entry.Batch = batch;
            return batch;
        }
    }

    // This method is a tools-only probe. The measured prototype was withdrawn
    // from game callsites; adding a backend is not a reason to slow the C# core.
    internal static bool TryReadNative(Campaign campaign, out OwnershipComponentBatch batch)
    {
        batch = null;
        if (Native.Value is not { IsEnabled: true } kernel) return false;
        try
        {
            var candidate = Read(campaign);
            if (!kernel.IsEnabled) return false; // Its failed scratch result was not accepted as native.
            batch = candidate;
            return true;
        }
        catch (ArgumentException error)
        {
            kernel.RejectHostInput("unsupported graph/input: " + error.ParamName);
            return false;
        }
    }

    internal static List<HashSet<int>> Pieces(Campaign campaign, int owner)
    {
        return Pieces(campaign.ProvinceMap, owner, Read(campaign));
    }

    internal static List<HashSet<int>> Pieces(ProvinceTopology map, int owner, OwnershipComponentBatch batch)
    {
        var pieces = new List<HashSet<int>>();
        for (var component = 1; component <= batch.ComponentCount; component++)
        {
            var begin = batch.ComponentOffsets[component - 1];
            var end = batch.ComponentOffsets[component];
            if (map.ProvinceToNation[batch.MemberOrder[begin]] != owner) continue;
            var piece = new HashSet<int>(end - begin);
            for (var at = begin; at < end; at++) piece.Add(batch.MemberOrder[at]);
            pieces.Add(piece);
        }
        return pieces;
    }
}
