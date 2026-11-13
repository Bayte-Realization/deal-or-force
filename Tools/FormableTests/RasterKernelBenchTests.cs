using System;
using System.Collections.Generic;
using System.Diagnostics;
using DealOrForce.Simulation;

/// <summary>
/// The Odin raster-overlay kernel, measured end to end against the managed packer it can
/// replace. The native backend is chosen once per process (the loader probes its env var on
/// the first pack), so the A/B is two processes run interleaved M N M N by the caller; this
/// bench makes each arm print the same fixture's buffer hashes first, so a timing comparison
/// is only ever read between runs that produced identical bytes.
///
/// Timing covers the whole PackProvinceOverlays call - palette flattening, the three output
/// clears, the seven pins - because that is what the game pays, not just the native body.
/// Run: FormableTests --raster-kernel-only [kernelDir]
/// </summary>
static class RasterKernelBenchTests
{
    const int Provinces = 35295;
    const int Samples = 31;
    const int CallsPerSample = 200;

    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("raster kernel bench: " + label); Console.WriteLine("PASS raster kernel bench: " + label); }

    public static void Run(string[] args)
    {
        var slot = Array.IndexOf(args, "--raster-kernel-only");
        var dir = slot >= 0 && slot + 1 < args.Length ? args[slot + 1] : null;
        if (dir is not null)
        {
            Check(RasterOverlayKernel.TryEnableNative(dir), $"the Odin kernel loads from {dir}");
            Check(RasterOverlayKernel.BackendStatus == "odin-c-abi-v1", $"backend is odin, got {RasterOverlayKernel.BackendStatus}");
        }
        else
        {
            Check(RasterOverlayKernel.BackendStatus == "managed-flat-soa", $"backend is managed, got {RasterOverlayKernel.BackendStatus}");
        }

        var random = new Random(20261112);
        var owners = new ushort[Provinces + 1];
        var painted = new ushort[Provinces + 1];
        var controllers = new ushort[Provinces + 1];
        for (var p = 1; p <= Provinces; p++)
        {
            owners[p] = (ushort)random.Next(0, 500);
            painted[p] = random.Next(0, 10) == 0 ? (ushort)random.Next(0, 500) : owners[p];
            controllers[p] = random.Next(0, 20) == 0 ? (ushort)random.Next(0, 500) : owners[p];
        }
        var palette = new Dictionary<int, uint>();
        for (var i = 0; i < 500; i++) palette[i] = (uint)random.Next(int.MinValue, int.MaxValue);
        var control = new byte[256 * 256 * 4];
        var owner = new byte[256 * 256 * 4];
        var ids = new byte[256 * 256 * 4];

        var moved = RasterOverlayKernel.PackProvinceOverlays(Provinces, owners, painted, controllers,
            palette, 200, 7, 0xFF0000FFu, control, owner, ids);
        Console.WriteLine($"RASTER_KERNEL_HASH backend={RasterOverlayKernel.BackendStatus} moved={moved} " +
                          $"control={Hash(control):X16} owner={Hash(owner):X16} ids={Hash(ids):X16}");

        for (var warm = 0; warm < 12; warm++)
            RasterOverlayKernel.PackProvinceOverlays(Provinces, owners, painted, controllers,
                palette, 200, 7, 0xFF0000FFu, control, owner, ids);

        var samples = new double[Samples];
        for (var s = 0; s < Samples; s++)
        {
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < CallsPerSample; i++)
                RasterOverlayKernel.PackProvinceOverlays(Provinces, owners, painted, controllers,
                    palette, 200, 7, 0xFF0000FFu, control, owner, ids);
            var elapsed = Stopwatch.GetTimestamp() - started;
            samples[s] = elapsed * 1000d / Stopwatch.Frequency / CallsPerSample;
        }
        Array.Sort(samples);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < CallsPerSample; i++)
            RasterOverlayKernel.PackProvinceOverlays(Provinces, owners, painted, controllers,
                palette, 200, 7, 0xFF0000FFu, control, owner, ids);
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)CallsPerSample;
        Console.WriteLine($"RASTER_KERNEL backend={RasterOverlayKernel.BackendStatus} provinces={Provinces} " +
                          $"samples={Samples} calls_per_sample={CallsPerSample} median_ms={samples[Samples / 2]:F4} " +
                          $"p95_ms={samples[(int)(Samples * 0.95)]:F4} bytes_per_call={bytes:F1}");
        Console.WriteLine("PASS raster kernel bench: every arm measured and reported");
    }

    static ulong Hash(byte[] buffer)
    {
        ulong hash = 14695981039346656037UL;
        for (var i = 0; i < buffer.Length; i++)
        {
            hash ^= buffer[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
