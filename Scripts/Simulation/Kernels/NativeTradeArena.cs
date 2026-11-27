// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Persistent native trade clearing / distance / partner-affinity arena (<c>DofTradeArena*</c>)
/// bound per <see cref="Campaign"/>.
/// </summary>
public sealed partial class NativeTradeArena
{
    private sealed class SafeTradeHandle : SafeHandle
    {
        public SafeTradeHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        public void Init(IntPtr ptr) => SetHandle(ptr);
        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero) dof_trade_destroy_v1(handle);
            handle = IntPtr.Zero;
            return true;
        }
    }

    private static readonly ConditionalWeakTable<Campaign, NativeTradeArena> Cache = new();
    private readonly SafeTradeHandle _handle;
    private readonly object _gate = new();

    private NativeTradeArena(SafeTradeHandle handle) => _handle = handle;

    ~NativeTradeArena() => _handle.Dispose();

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial IntPtr dof_trade_create_v1(uint maxSlots);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void dof_trade_destroy_v1(IntPtr arena);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_trade_update_distances_v1(
        IntPtr arena,
        int* nationIds,
        float* lats,
        float* lons,
        uint nationCount,
        int* dirtyIndices,
        uint dirtyCount,
        uint fullRebuild,
        uint stride,
        float* outDistTable,
        uint outDistLen);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_trade_compute_affinity_v1(
        IntPtr arena,
        int* buyerIds,
        ushort* buyerMapIdx,
        float* buyerReach,
        uint buyerCount,
        int* sellerIds,
        ushort* sellerMapIdx,
        float* sellerReach,
        uint sellerCount,
        float* distTable,
        uint distStride,
        uint distNationCount,
        float* pairOpinion,
        byte* pairStance,
        byte* pairFlags,
        uint pairStride,
        float halfReachKm,
        float customsInside,
        float customsOutside,
        float* outAffinity,
        uint outStride);

    public static NativeTradeArena? ForCampaign(Campaign? campaign, int maxSlots = 4096)
    {
        if (campaign is null || !NativeKernelsLoader.IsAvailable) return null;
        if (Cache.TryGetValue(campaign, out var existing)) return existing;

        var ptr = dof_trade_create_v1((uint)Math.Clamp(maxSlots, 64, 65536));
        if (ptr == IntPtr.Zero) return null;

        var sh = new SafeTradeHandle();
        sh.Init(ptr);
        var created = new NativeTradeArena(sh);
        lock (Cache)
        {
            if (Cache.TryGetValue(campaign, out existing))
            {
                sh.Dispose();
                return existing;
            }
            Cache.Add(campaign, created);
            return created;
        }
    }

    public unsafe bool TryUpdateDistances(
        ReadOnlySpan<int> nationIds,
        ReadOnlySpan<float> lats,
        ReadOnlySpan<float> lons,
        ReadOnlySpan<int> dirtyIndices,
        bool fullRebuild,
        int stride,
        Span<float> outDistTable)
    {
        if (!NativeKernelsLoader.IsAvailable || _handle.IsInvalid) return false;
        if (nationIds.Length != lats.Length || nationIds.Length != lons.Length) return false;

        lock (_gate)
        {
            fixed (int* pIds = nationIds)
            fixed (float* pLat = lats)
            fixed (float* pLon = lons)
            fixed (int* pDirty = dirtyIndices)
            fixed (float* pOut = outDistTable)
            {
                return dof_trade_update_distances_v1(
                    _handle.DangerousGetHandle(),
                    pIds, pLat, pLon, (uint)nationIds.Length,
                    pDirty, (uint)dirtyIndices.Length,
                    fullRebuild ? 1u : 0u, (uint)stride,
                    pOut, (uint)outDistTable.Length) == 0;
            }
        }
    }

    /// <summary>
    /// Computes buyer x seller affinity scores in C# IEEE-754 parity mode.
    /// <paramref name="pairFlags"/>: bit 0 = has_relation, bit 1 = blocked (writes -1),
    /// bit 2 = shared_customs_union, bit 3 = buyer_has_customs_union, bit 4 = shares_land_border,
    /// bit 5 = self pair (writes 0).
    /// Missing distance entries default to 12000 km.
    /// </summary>
    public unsafe bool TryComputeAffinity(
        ReadOnlySpan<int> buyerIds,
        ReadOnlySpan<ushort> buyerMapIdx,
        ReadOnlySpan<float> buyerReach,
        ReadOnlySpan<int> sellerIds,
        ReadOnlySpan<ushort> sellerMapIdx,
        ReadOnlySpan<float> sellerReach,
        ReadOnlySpan<float> distTable,
        int distStride,
        int distNationCount,
        ReadOnlySpan<float> pairOpinion,
        ReadOnlySpan<byte> pairStance,
        ReadOnlySpan<byte> pairFlags,
        int pairStride,
        float halfReachKm,
        float customsInside,
        float customsOutside,
        Span<float> outAffinity,
        int outStride)
    {
        if (!NativeKernelsLoader.IsAvailable || _handle.IsInvalid) return false;

        lock (_gate)
        {
            fixed (int* pBi = buyerIds)
            fixed (ushort* pBm = buyerMapIdx)
            fixed (float* pBr = buyerReach)
            fixed (int* pSi = sellerIds)
            fixed (ushort* pSm = sellerMapIdx)
            fixed (float* pSr = sellerReach)
            fixed (float* pDist = distTable)
            fixed (float* pOp = pairOpinion)
            fixed (byte* pSt = pairStance)
            fixed (byte* pFl = pairFlags)
            fixed (float* pOut = outAffinity)
            {
                return dof_trade_compute_affinity_v1(
                    _handle.DangerousGetHandle(),
                    pBi, pBm, pBr, (uint)buyerIds.Length,
                    pSi, pSm, pSr, (uint)sellerIds.Length,
                    pDist, (uint)distStride, (uint)distNationCount,
                    pOp, pSt, pFl, (uint)pairStride,
                    halfReachKm, customsInside, customsOutside,
                    pOut, (uint)outStride) == 0;
            }
        }
    }
}
