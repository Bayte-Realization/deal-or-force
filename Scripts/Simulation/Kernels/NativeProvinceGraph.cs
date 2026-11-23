// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Persistent native province topology arena (<c>DofProvinceGraph*</c>) bound once per
/// <see cref="ProvinceTopology"/> via <see cref="ConditionalWeakTable{TKey, TValue}"/>, paired
/// with zero-contention thread-local generation-stamped scratch (<c>DofGraphScratch*</c>).
/// </summary>
public sealed partial class NativeProvinceGraph
{
    private sealed class SafeGraphHandle : SafeHandle
    {
        public SafeGraphHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        public void Init(IntPtr ptr) => SetHandle(ptr);
        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero) dof_graph_destroy_v1(handle);
            handle = IntPtr.Zero;
            return true;
        }
    }

    private sealed class SafeScratchHandle : SafeHandle
    {
        public SafeScratchHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        public void Init(IntPtr ptr) => SetHandle(ptr);
        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero) dof_graph_scratch_destroy_v1(handle);
            handle = IntPtr.Zero;
            return true;
        }
    }

    private sealed class ThreadWorkspace
    {
        internal SafeScratchHandle? Scratch;
        internal int[] OutProvinces;
        internal float[] OutDistances;
        internal int[] SrcBuffer;

        internal ThreadWorkspace(int provinceCount)
        {
            OutProvinces = new int[provinceCount];
            OutDistances = new float[provinceCount];
            SrcBuffer = new int[Math.Min(provinceCount, 1024)];
        }
    }

    private static readonly ConditionalWeakTable<ProvinceTopology, NativeProvinceGraph> Table = new();

    private readonly SafeGraphHandle _graph;
    private readonly int _provinceCount;
    private readonly ThreadLocal<ThreadWorkspace> _workspace;

    public int ProvinceCount => _provinceCount;

    private NativeProvinceGraph(SafeGraphHandle graph, int provinceCount)
    {
        _graph = graph;
        _provinceCount = provinceCount;
        _workspace = new ThreadLocal<ThreadWorkspace>(() => CreateWorkspace(provinceCount), trackAllValues: false);
    }

    ~NativeProvinceGraph()
    {
        _workspace.Dispose();
        _graph.Dispose();
    }

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial IntPtr dof_graph_create_v1(
        uint provinceCount,
        uint* csrOffsets,
        uint* csrEdges,
        float* edgeKm,
        uint edgeCount,
        uint* landOffsets,
        uint* landEdges,
        uint landEdgeCount,
        byte* water,
        byte* coastal,
        int* harbourBasin,
        float* provLat,
        float* provLon);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void dof_graph_destroy_v1(IntPtr graph);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial IntPtr dof_graph_scratch_create_v1(uint provinceCount);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void dof_graph_scratch_destroy_v1(IntPtr scratch);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_graph_distance_field_v1(
        IntPtr graph,
        IntPtr scratch,
        int* sources,
        uint sourceCount,
        float boundKm,
        int* outProvinces,
        float* outDistances,
        uint outCapacity,
        uint* outCount);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_graph_solve_supply_v1(
        IntPtr graph,
        IntPtr scratch,
        ushort* controllers,
        uint controllerCount,
        ushort* provinceToNation,
        ulong* passableBits,
        uint passableWordCount,
        ushort ownMapIndex,
        ushort* provinceToState,
        int* roots,
        uint rootCount,
        int* territory,
        uint territoryCount,
        ulong* barracksStateBits,
        uint barracksWordCount,
        ushort* landStateIds,
        int* landStateCounts,
        uint landStateLen,
        ushort* heldStateIds,
        int* heldStateCounts,
        uint heldStateLen,
        byte insurgent,
        float forwardDepotHeldShare,
        int* armyProvinces,
        uint armyCount,
        byte* outArmyFlags,
        int* outArmyDistance,
        int* outArmySourceState);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_graph_owner_components_v1(
        IntPtr graph,
        IntPtr scratch,
        ushort* owners,
        uint ownerCount,
        int* outLabels,
        int* outMembers,
        int* outComponentOffsets,
        int* outComponentCount,
        int* outMemberCount);

    private static ThreadWorkspace CreateWorkspace(int provinceCount)
    {
        var ws = new ThreadWorkspace(provinceCount);
        var ptr = dof_graph_scratch_create_v1((uint)provinceCount);
        if (ptr != IntPtr.Zero)
        {
            var sh = new SafeScratchHandle();
            sh.Init(ptr);
            ws.Scratch = sh;
        }
        return ws;
    }

    public static NativeProvinceGraph? ForMap(ProvinceTopology? map)
    {
        if (map is null || !NativeKernelsLoader.IsAvailable) return null;
        if (Table.TryGetValue(map, out var existing)) return existing;

        var created = TryBuildFromTopology(map);
        if (created is null) return null;

        lock (Table)
        {
            if (Table.TryGetValue(map, out existing)) return existing;
            Table.Add(map, created);
            return created;
        }
    }

    private static unsafe NativeProvinceGraph? TryBuildFromTopology(ProvinceTopology map)
    {
        var neighbors = map.Neighbors;
        if (neighbors is null || neighbors.Length < 1) return null;

        var n = neighbors.Length;
        var csrOffsets = new uint[n + 1];
        var edgeCount = 0;
        for (var u = 0; u < n; u++)
        {
            var row = neighbors[u];
            if (row is not null)
            {
                for (var k = 0; k < row.Length; k++)
                {
                    var v = row[k];
                    if (v >= 0 && v < n) edgeCount++;
                }
            }
            csrOffsets[u + 1] = (uint)edgeCount;
        }

        var csrEdges = new uint[edgeCount];
        var edgeKm = new float[edgeCount];
        var provLat = new float[n];
        var provLon = new float[n];
        for (var p = 1; p < n; p++)
        {
            var c = map.CenterOf(p);
            provLat[p] = c.Latitude;
            provLon[p] = c.Longitude;
        }

        const float radians = MathF.PI / 180f;
        var ew = 0;
        for (var u = 0; u < n; u++)
        {
            var row = neighbors[u];
            if (row is null) continue;
            var fromLat = provLat[u];
            var fromLon = provLon[u];
            var cosFrom = MathF.Cos(fromLat * radians);
            for (var k = 0; k < row.Length; k++)
            {
                var v = row[k];
                if (v < 0 || v >= n) continue;
                csrEdges[ew] = (uint)v;
                var toLat = provLat[v];
                var toLon = provLon[v];
                var dLat = (toLat - fromLat) * radians;
                var dLon = (toLon - fromLon) * radians;
                var sinLat = MathF.Sin(dLat * 0.5f);
                var sinLon = MathF.Sin(dLon * 0.5f);
                var haversine = sinLat * sinLat + cosFrom * MathF.Cos(toLat * radians) * sinLon * sinLon;
                edgeKm[ew] = 6371f * 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(haversine)));
                ew++;
            }
        }

        var landNeighbors = map.LandNeighbors;
        var landOffsets = new uint[n + 1];
        var landCount = 0;
        if (landNeighbors is not null && landNeighbors.Length == n)
        {
            for (var u = 0; u < n; u++)
            {
                var row = landNeighbors[u];
                if (row is not null)
                {
                    for (var k = 0; k < row.Length; k++)
                    {
                        var v = row[k];
                        if (v >= 0 && v < n) landCount++;
                    }
                }
                landOffsets[u + 1] = (uint)landCount;
            }
        }

        var landEdges = new uint[landCount];
        if (landCount > 0 && landNeighbors is not null)
        {
            var lw = 0;
            for (var u = 0; u < n; u++)
            {
                var row = landNeighbors[u];
                if (row is null) continue;
                for (var k = 0; k < row.Length; k++)
                {
                    var v = row[k];
                    if (v >= 0 && v < n) landEdges[lw++] = (uint)v;
                }
            }
        }

        var water = new byte[n];
        var coastal = new byte[n];
        var basins = new int[n];
        Array.Fill(basins, -1);
        for (var p = 1; p < n; p++)
            water[p] = map.IsWater(p) ? (byte)1 : (byte)0;

        var coastalArr = SeaLanes.CoastalProvinceArray(map);
        for (var i = 0; i < coastalArr.Length; i++)
        {
            var cp = coastalArr[i];
            if (cp > 0 && cp < n)
            {
                coastal[cp] = 1;
                basins[cp] = SeaLanes.HarbourBasin(map, cp);
            }
        }

        fixed (uint* pOff = csrOffsets)
        fixed (uint* pEdges = csrEdges)
        fixed (float* pKm = edgeKm)
        fixed (uint* pLOff = landOffsets)
        fixed (uint* pLEdges = landEdges)
        fixed (byte* pWater = water)
        fixed (byte* pCoast = coastal)
        fixed (int* pBasin = basins)
        fixed (float* pLat = provLat)
        fixed (float* pLon = provLon)
        {
            var raw = dof_graph_create_v1(
                (uint)n, pOff, pEdges, pKm, (uint)edgeCount,
                pLOff, pLEdges, (uint)landCount,
                pWater, pCoast, pBasin, pLat, pLon);
            if (raw == IntPtr.Zero) return null;
            var handle = new SafeGraphHandle();
            handle.Init(raw);
            return new NativeProvinceGraph(handle, n);
        }
    }

    /// <summary>
    /// Computes bit-identical road Dijkstra distances up to <paramref name="boundKm"/> matching
    /// <c>LongRangeMovement.DistanceField</c>.
    /// </summary>
    public unsafe bool TryDistanceField(
        List<int> sources,
        float boundKm,
        out Dictionary<int, float>? distances,
        out List<int>? within)
    {
        distances = null;
        within = null;
        if (!NativeKernelsLoader.IsAvailable || _graph.IsInvalid || sources is null) return false;

        var ws = _workspace.Value;
        if (ws?.Scratch is null || ws.Scratch.IsInvalid) return false;

        var sc = sources.Count;
        if (sc > ws.SrcBuffer.Length)
            ws.SrcBuffer = new int[Math.Max(sc, ws.SrcBuffer.Length * 2)];
        for (var i = 0; i < sc; i++) ws.SrcBuffer[i] = sources[i];

        uint count = 0;
        fixed (int* pSrc = ws.SrcBuffer)
        fixed (int* pOutProv = ws.OutProvinces)
        fixed (float* pOutDist = ws.OutDistances)
        {
            var status = dof_graph_distance_field_v1(
                _graph.DangerousGetHandle(),
                ws.Scratch.DangerousGetHandle(),
                pSrc, (uint)sc, boundKm,
                pOutProv, pOutDist, (uint)_provinceCount, &count);
            if (status != 0) return false;
        }

        var cInt = (int)count;
        var dict = new Dictionary<int, float>(cInt);
        var list = new List<int>(cInt);
        var op = ws.OutProvinces;
        var od = ws.OutDistances;
        for (var i = 0; i < cInt; i++)
        {
            var p = op[i];
            list.Add(p);
            dict[p] = od[i];
        }

        distances = dict;
        within = list;
        return true;
    }

    /// <summary>
    /// Computes bit-identical road Dijkstra distances directly into caller-owned
    /// <paramref name="distances"/> (previously cleared over prior <paramref name="within"/> entries)
    /// with zero managed heap allocations.
    /// </summary>
    internal unsafe bool TryFillDistanceField(
        List<int> sources,
        float boundKm,
        float[] distances,
        List<int> within)
    {
        if (!NativeKernelsLoader.IsAvailable || _graph.IsInvalid || sources is null ||
            distances is null || distances.Length <= _provinceCount) return false;

        var ws = _workspace.Value;
        if (ws?.Scratch is null || ws.Scratch.IsInvalid) return false;

        var sc = sources.Count;
        if (sc > ws.SrcBuffer.Length)
            ws.SrcBuffer = new int[Math.Max(sc, ws.SrcBuffer.Length * 2)];
        for (var i = 0; i < sc; i++) ws.SrcBuffer[i] = sources[i];

        uint count = 0;
        fixed (int* pSrc = ws.SrcBuffer)
        fixed (int* pOutProv = ws.OutProvinces)
        fixed (float* pOutDist = ws.OutDistances)
        {
            var status = dof_graph_distance_field_v1(
                _graph.DangerousGetHandle(),
                ws.Scratch.DangerousGetHandle(),
                pSrc, (uint)sc, boundKm,
                pOutProv, pOutDist, (uint)_provinceCount, &count);
            if (status != 0) return false;
        }

        for (var i = 0; i < within.Count; i++) distances[within[i]] = float.PositiveInfinity;
        within.Clear();
        var cInt = (int)count;
        var op = ws.OutProvinces;
        var od = ws.OutDistances;
        for (var i = 0; i < cInt; i++)
        {
            var p = op[i];
            if ((uint)p < (uint)distances.Length)
            {
                distances[p] = od[i];
                within.Add(p);
            }
        }
        within.Sort();
        return true;
    }

    public unsafe bool TrySolveSupply(
        ushort[] controllers,
        ushort[] provinceToNation,
        ulong[] passableBits,
        ushort ownMapIndex,
        ushort[] provinceToState,
        ReadOnlySpan<int> roots,
        ReadOnlySpan<int> territory,
        ulong[] barracksStateBits,
        ReadOnlySpan<ushort> landStateIds,
        ReadOnlySpan<int> landStateCounts,
        ReadOnlySpan<ushort> heldStateIds,
        ReadOnlySpan<int> heldStateCounts,
        bool insurgent,
        float forwardDepotHeldShare,
        ReadOnlySpan<int> armyProvinces,
        Span<byte> outArmyFlags,
        Span<int> outArmyDistance,
        Span<int> outArmySourceState)
    {
        if (!NativeKernelsLoader.IsAvailable || _graph.IsInvalid) return false;
        var ws = _workspace.Value;
        if (ws?.Scratch is null || ws.Scratch.IsInvalid) return false;
        if (controllers.Length < _provinceCount || provinceToNation.Length < _provinceCount || provinceToState.Length < _provinceCount)
            return false;
        if (outArmyFlags.Length < armyProvinces.Length || outArmyDistance.Length < armyProvinces.Length || outArmySourceState.Length < armyProvinces.Length)
            return false;

        fixed (ushort* pCtrl = controllers)
        fixed (ushort* pPtn = provinceToNation)
        fixed (ulong* pPass = passableBits)
        fixed (ushort* pPts = provinceToState)
        fixed (int* pRoots = roots)
        fixed (int* pTerr = territory)
        fixed (ulong* pBar = barracksStateBits)
        fixed (ushort* pLsi = landStateIds)
        fixed (int* pLsc = landStateCounts)
        fixed (ushort* pHsi = heldStateIds)
        fixed (int* pHsc = heldStateCounts)
        fixed (int* pArm = armyProvinces)
        fixed (byte* pFlags = outArmyFlags)
        fixed (int* pDist = outArmyDistance)
        fixed (int* pSrc = outArmySourceState)
        {
            return dof_graph_solve_supply_v1(
                _graph.DangerousGetHandle(),
                ws.Scratch.DangerousGetHandle(),
                pCtrl, (uint)controllers.Length, pPtn,
                pPass, (uint)passableBits.Length, ownMapIndex, pPts,
                pRoots, (uint)roots.Length, pTerr, (uint)territory.Length,
                pBar, (uint)barracksStateBits.Length,
                pLsi, pLsc, (uint)landStateIds.Length,
                pHsi, pHsc, (uint)heldStateIds.Length,
                insurgent ? (byte)1 : (byte)0, forwardDepotHeldShare,
                pArm, (uint)armyProvinces.Length,
                pFlags, pDist, pSrc) == 0;
        }
    }

    public unsafe bool TryComputeOwnerComponents(
        ushort[] owners,
        int[] outLabels,
        int[] outMembers,
        int[] outComponentOffsets,
        out int componentCount,
        out int memberCount)
    {
        componentCount = 0;
        memberCount = 0;
        if (!NativeKernelsLoader.IsAvailable || _graph.IsInvalid) return false;
        var ws = _workspace.Value;
        if (ws?.Scratch is null || ws.Scratch.IsInvalid) return false;
        if (owners.Length != _provinceCount || outLabels.Length < _provinceCount ||
            outMembers.Length < _provinceCount || outComponentOffsets.Length < _provinceCount + 1)
            return false;

        int cc = 0, mc = 0;
        fixed (ushort* pOw = owners)
        fixed (int* pLab = outLabels)
        fixed (int* pMem = outMembers)
        fixed (int* pOff = outComponentOffsets)
        {
            var status = dof_graph_owner_components_v1(
                _graph.DangerousGetHandle(),
                ws.Scratch.DangerousGetHandle(),
                pOw, (uint)owners.Length,
                pLab, pMem, pOff, &cc, &mc);
            if (status != 0) return false;
        }

        componentCount = cc;
        memberCount = mc;
        return true;
    }
}
