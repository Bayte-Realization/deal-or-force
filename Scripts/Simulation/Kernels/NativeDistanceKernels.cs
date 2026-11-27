// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Batched native SIMD geographic distance &amp; latitude-band range prefilter wrappers
/// backed by <c>dof_kernels</c> (<c>dof_distance_*</c>) with pure C# fallbacks.
/// </summary>
public static partial class NativeDistanceKernels
{
    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_distance_haversine_batch_v1(
        float originLat,
        float originLon,
        float* targetLats,
        float* targetLons,
        uint targetCount,
        float* outKm);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_distance_within_range_batch_v1(
        float originLat,
        float originLon,
        float* targetLats,
        float* targetLons,
        uint targetCount,
        float reachKm,
        int* outIndices,
        float* outKm,
        uint outCapacity,
        uint* outCount);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_distance_pairwise_matrix_v1(
        float* lats,
        float* lons,
        uint count,
        uint stride,
        float* outKm);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float GreatCircleKm(float latA, float lonA, float latB, float lonB)
    {
        const float Radians = MathF.PI / 180.0f;
        float phiA = latA * Radians;
        float phiB = latB * Radians;
        float deltaPhi = (latB - latA) * Radians;
        float deltaLambda = (lonB - lonA) * Radians;
        float sinDp = MathF.Sin(deltaPhi * 0.5f);
        float sinDl = MathF.Sin(deltaLambda * 0.5f);
        float haversine = sinDp * sinDp + MathF.Cos(phiA) * MathF.Cos(phiB) * sinDl * sinDl;
        float clamped = MathF.Sqrt(haversine > 0f ? haversine : 0f);
        if (clamped > 1.0f) clamped = 1.0f;
        return 6371.0f * 2.0f * MathF.Asin(clamped);
    }

    public static bool HaversineBatch(
        float originLat,
        float originLon,
        ReadOnlySpan<float> targetLats,
        ReadOnlySpan<float> targetLons,
        Span<float> outKm)
    {
        if (NativeKernelsLoader.EnableDistanceDefault &&
            TryNativeHaversineBatch(originLat, originLon, targetLats, targetLons, outKm))
        {
            return true;
        }

        ManagedHaversineBatch(originLat, originLon, targetLats, targetLons, outKm);
        return false;
    }

    public static unsafe bool TryNativeHaversineBatch(
        float originLat,
        float originLon,
        ReadOnlySpan<float> targetLats,
        ReadOnlySpan<float> targetLons,
        Span<float> outKm)
    {
        int n = targetLats.Length;
        if (targetLons.Length < n || outKm.Length < n) return false;
        if (n == 0) return true;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapDistance)) return false;

        fixed (float* pLat = targetLats)
        fixed (float* pLon = targetLons)
        fixed (float* pOut = outKm)
        {
            return dof_distance_haversine_batch_v1(originLat, originLon, pLat, pLon, (uint)n, pOut) == 0;
        }
    }

    public static void ManagedHaversineBatch(
        float originLat,
        float originLon,
        ReadOnlySpan<float> targetLats,
        ReadOnlySpan<float> targetLons,
        Span<float> outKm)
    {
        int n = targetLats.Length;
        if (targetLons.Length < n || outKm.Length < n)
            throw new ArgumentException("Mismatched span lengths for ManagedHaversineBatch.");

        for (int i = 0; i < n; i++)
            outKm[i] = GreatCircleKm(originLat, originLon, targetLats[i], targetLons[i]);
    }

    public static bool WithinRangeBatch(
        float originLat,
        float originLon,
        ReadOnlySpan<float> targetLats,
        ReadOnlySpan<float> targetLons,
        float reachKm,
        Span<int> outIndices,
        Span<float> outKm,
        out int count)
    {
        if (NativeKernelsLoader.EnableDistanceDefault &&
            TryNativeWithinRangeBatch(originLat, originLon, targetLats, targetLons, reachKm, outIndices, outKm, out count))
        {
            return true;
        }

        count = ManagedWithinRangeBatch(originLat, originLon, targetLats, targetLons, reachKm, outIndices, outKm);
        return false;
    }

    public static unsafe bool TryNativeWithinRangeBatch(
        float originLat,
        float originLon,
        ReadOnlySpan<float> targetLats,
        ReadOnlySpan<float> targetLons,
        float reachKm,
        Span<int> outIndices,
        Span<float> outKm,
        out int count)
    {
        count = 0;
        int n = targetLats.Length;
        int cap = outIndices.Length;
        if (targetLons.Length < n || outKm.Length != cap || !float.IsFinite(reachKm) || reachKm < 0f)
            return false;
        if (n == 0) return true;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapDistance)) return false;

        uint nativeCount = 0;
        fixed (float* pLat = targetLats)
        fixed (float* pLon = targetLons)
        fixed (int* pIdx = outIndices)
        fixed (float* pKm = outKm)
        {
            int rc = dof_distance_within_range_batch_v1(
                originLat, originLon, pLat, pLon, (uint)n, reachKm,
                cap > 0 ? pIdx : null, cap > 0 ? pKm : null, (uint)cap, &nativeCount);
            if (rc != 0) return false;
        }
        count = (int)nativeCount;
        return true;
    }

    public static int ManagedWithinRangeBatch(
        float originLat,
        float originLon,
        ReadOnlySpan<float> targetLats,
        ReadOnlySpan<float> targetLons,
        float reachKm,
        Span<int> outIndices,
        Span<float> outKm)
    {
        int n = targetLats.Length;
        int cap = outIndices.Length;
        if (targetLons.Length < n || outKm.Length != cap)
            throw new ArgumentException("Mismatched span lengths for ManagedWithinRangeBatch.");
        if (!float.IsFinite(reachKm) || reachKm < 0f)
            throw new ArgumentOutOfRangeException(nameof(reachKm));

        int written = 0;
        for (int i = 0; i < n; i++)
        {
            float tlat = targetLats[i];
            if (MathF.Abs(originLat - tlat) * 111.0f > reachKm) continue;
            float km = GreatCircleKm(originLat, originLon, tlat, targetLons[i]);
            if (km <= reachKm)
            {
                if (written >= cap)
                    throw new ArgumentException("Output capacity exceeded in ManagedWithinRangeBatch.");
                outIndices[written] = i;
                outKm[written] = km;
                written++;
            }
        }
        return written;
    }

    public static bool PairwiseMatrix(
        ReadOnlySpan<float> lats,
        ReadOnlySpan<float> lons,
        int stride,
        Span<float> outKm)
    {
        if (NativeKernelsLoader.EnableDistanceDefault &&
            TryNativePairwiseMatrix(lats, lons, stride, outKm))
        {
            return true;
        }

        ManagedPairwiseMatrix(lats, lons, stride, outKm);
        return false;
    }

    public static unsafe bool TryNativePairwiseMatrix(
        ReadOnlySpan<float> lats,
        ReadOnlySpan<float> lons,
        int stride,
        Span<float> outKm)
    {
        int n = lats.Length;
        if (stride < n || lons.Length < n) return false;
        if (n == 0) return true;
        long needed = (long)n * stride;
        if (needed > int.MaxValue || outKm.Length < needed) return false;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapDistance)) return false;

        fixed (float* pLat = lats)
        fixed (float* pLon = lons)
        fixed (float* pOut = outKm)
        {
            return dof_distance_pairwise_matrix_v1(pLat, pLon, (uint)n, (uint)stride, pOut) == 0;
        }
    }

    public static void ManagedPairwiseMatrix(
        ReadOnlySpan<float> lats,
        ReadOnlySpan<float> lons,
        int stride,
        Span<float> outKm)
    {
        int n = lats.Length;
        if (stride < n || lons.Length < n)
            throw new ArgumentOutOfRangeException(nameof(stride));
        if (n == 0) return;
        long needed = (long)n * stride;
        if (needed > int.MaxValue || outKm.Length < needed)
            throw new ArgumentException("Output buffer too small for ManagedPairwiseMatrix.");

        for (int i = 0; i < n; i++)
        {
            float la = lats[i];
            float lo = lons[i];
            outKm[i * stride + i] = 0f;
            for (int j = i + 1; j < n; j++)
            {
                float km = GreatCircleKm(la, lo, lats[j], lons[j]);
                outKm[i * stride + j] = km;
                outKm[j * stride + i] = km;
            }
        }
    }
}
