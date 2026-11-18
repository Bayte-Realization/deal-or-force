// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Persistent native raster arena (<c>DofRasterBake*</c>) keyed by the immutable authored map buffers.
/// Eliminates the 182 MB per-call managed LOH allocations of <see cref="FrontierField.Build"/> and
/// accelerates the 16.6M-cell 2-pass Chamfer + separable Gaussian via halo-exact threads + SIMD.
/// </summary>
public static partial class NativeRasterBake
{
    private sealed class SafeRasterHandle : SafeHandle
    {
        public SafeRasterHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        public void Init(IntPtr ptr) => SetHandle(ptr);
        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero) dof_raster_destroy_v1(handle);
            handle = IntPtr.Zero;
            return true;
        }
    }

    private sealed class CachedBake : IDisposable
    {
        internal readonly object Gate = new();
        internal readonly int Width;
        internal readonly int Height;
        internal readonly ushort[]? CellToProvince;
        internal readonly SafeRasterHandle Handle;

        internal CachedBake(int width, int height, ushort[]? cellToProvince, SafeRasterHandle handle)
        {
            Width = width;
            Height = height;
            CellToProvince = cellToProvince;
            Handle = handle;
        }

        public void Dispose() => Handle.Dispose();
    }

    private static readonly ConditionalWeakTable<ushort[], CachedBake> Cache = new();

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial IntPtr dof_raster_create_v1(
        uint width,
        uint height,
        ushort* authoredHolders,
        ushort* cellToProvince);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void dof_raster_destroy_v1(IntPtr bake);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_raster_bake_frontier_v1(
        IntPtr bake,
        ushort* owners,
        uint ownersLen,
        ushort* authoredOwners,
        uint authoredOwnersLen,
        float cap,
        float smoothing,
        uint threads,
        byte* outBytes,
        uint outLen);

    /// <summary>
    /// Attempts a persistent native frontier field bake. Returns <c>false</c> cleanly when the
    /// native library is absent or rejects inputs so callers silently fall back to C#.
    /// </summary>
    public static unsafe bool TryBuildFrontier(
        ushort[] authoredHolders,
        ushort[]? cellToProvince,
        int width,
        int height,
        ushort[]? owners,
        ushort[]? authoredOwners,
        float cap,
        float smoothing,
        out byte[]? bytes,
        uint threads = 0)
    {
        bytes = null;
        if (!NativeKernelsLoader.IsAvailable) return false;
        if (authoredHolders is null || width <= 0 || height <= 0) return false;
        var cellsLong = (long)width * height;
        if (cellsLong <= 0 || cellsLong > int.MaxValue || authoredHolders.Length < (int)cellsLong) return false;
        if (cellToProvince is not null && cellToProvince.Length < (int)cellsLong) return false;

        var entry = GetOrCreateEntry(authoredHolders, cellToProvince, width, height);
        if (entry is null) return false;

        var output = new byte[(int)cellsLong];
        lock (entry.Gate)
        {
            if (entry.Handle.IsInvalid) return false;

            fixed (ushort* ownersPtr = owners)
            fixed (ushort* authoredOwnersPtr = authoredOwners)
            fixed (byte* outPtr = output)
            {
                var status = dof_raster_bake_frontier_v1(
                    entry.Handle.DangerousGetHandle(),
                    ownersPtr,
                    owners is null ? 0u : (uint)owners.Length,
                    authoredOwnersPtr,
                    authoredOwners is null ? 0u : (uint)authoredOwners.Length,
                    cap,
                    smoothing,
                    threads,
                    outPtr,
                    (uint)output.Length);
                if (status != 0) return false;
            }
        }

        bytes = output;
        return true;
    }

    /// <summary>
    /// Non-caching single-use or pre-warmed handle path for tests and benchmarks.
    /// Returns false without modifying <paramref name="output"/> on fallback.
    /// </summary>
    internal static unsafe bool TryBakeInto(
        ushort[] authoredHolders,
        ushort[]? cellToProvince,
        int width,
        int height,
        ushort[]? owners,
        ushort[]? authoredOwners,
        float cap,
        float smoothing,
        byte[] output,
        uint threads = 0)
    {
        if (!NativeKernelsLoader.IsAvailable) return false;
        if (authoredHolders is null || output is null || width <= 0 || height <= 0) return false;
        var cells = (long)width * height;
        if (cells <= 0 || cells > int.MaxValue || authoredHolders.Length < (int)cells || output.Length < (int)cells) return false;
        if (cellToProvince is not null && cellToProvince.Length < (int)cells) return false;

        var entry = GetOrCreateEntry(authoredHolders, cellToProvince, width, height);
        if (entry is null) return false;

        lock (entry.Gate)
        {
            if (entry.Handle.IsInvalid) return false;
            fixed (ushort* ownersPtr = owners)
            fixed (ushort* authoredOwnersPtr = authoredOwners)
            fixed (byte* outPtr = output)
            {
                return dof_raster_bake_frontier_v1(
                    entry.Handle.DangerousGetHandle(),
                    ownersPtr,
                    owners is null ? 0u : (uint)owners.Length,
                    authoredOwnersPtr,
                    authoredOwners is null ? 0u : (uint)authoredOwners.Length,
                    cap,
                    smoothing,
                    threads,
                    outPtr,
                    (uint)output.Length) == 0;
            }
        }
    }

    private static unsafe CachedBake? GetOrCreateEntry(ushort[] authoredHolders, ushort[]? cellToProvince, int width, int height)
    {
        if (Cache.TryGetValue(authoredHolders, out var existing))
        {
            if (existing.Width == width && existing.Height == height && ReferenceEquals(existing.CellToProvince, cellToProvince))
                return existing;
            return null;
        }

        SafeRasterHandle? safe = null;
        try
        {
            fixed (ushort* holdersPtr = authoredHolders)
            fixed (ushort* provPtr = cellToProvince)
            {
                var raw = dof_raster_create_v1((uint)width, (uint)height, holdersPtr, provPtr);
                if (raw == IntPtr.Zero) return null;
                safe = new SafeRasterHandle();
                safe.Init(raw);
            }

            var created = new CachedBake(width, height, cellToProvince, safe);
            lock (Cache)
            {
                if (Cache.TryGetValue(authoredHolders, out existing))
                {
                    created.Dispose();
                    return existing.Width == width && existing.Height == height && ReferenceEquals(existing.CellToProvince, cellToProvince)
                        ? existing
                        : null;
                }
                Cache.Add(authoredHolders, created);
                return created;
            }
        }
        catch
        {
            safe?.Dispose();
            return null;
        }
    }
}
