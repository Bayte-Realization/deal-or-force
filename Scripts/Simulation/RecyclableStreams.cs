using System;
using System.Buffers;
using System.IO;
using System.IO.Hashing;
using Microsoft.IO;

namespace DealOrForce.Simulation;

/// <summary>
/// Shared pooled memory streams and hardware-accelerated XxHash3 helpers for saves,
/// support reports, and binary boot caches, avoiding Large Object Heap allocations.
/// </summary>
public static class RecyclableStreams
{
    private const int BlockBytes = 64 * 1024;
    private const int LargeBufferStepBytes = 1024 * 1024;
    private const int MaximumBufferBytes = 32 * 1024 * 1024;
    private const int MaxSmallPoolFreeBytes = 16 * 1024 * 1024;
    private const int MaxLargePoolFreeBytes = 32 * 1024 * 1024;
    private const int StreamHashScratchBytes = 64 * 1024;

    /// <summary>Process-wide pooled stream manager shared across pure-C# subsystems.</summary>
    public static readonly RecyclableMemoryStreamManager Manager = CreateManager();

    private static RecyclableMemoryStreamManager CreateManager()
    {
        var options = new RecyclableMemoryStreamManager.Options
        {
            BlockSize = BlockBytes,
            LargeBufferMultiple = LargeBufferStepBytes,
            MaximumBufferSize = MaximumBufferBytes,
            MaximumSmallPoolFreeBytes = MaxSmallPoolFreeBytes,
            MaximumLargePoolFreeBytes = MaxLargePoolFreeBytes,
            ThrowExceptionOnToArray = false
        };
        return new RecyclableMemoryStreamManager(options);
    }

    /// <summary>Rent a pooled stream tagged for diagnostics.</summary>
    public static RecyclableMemoryStream Get(string tag) => Manager.GetStream(tag);

    /// <summary>Rent a pooled stream with an initial capacity hint.</summary>
    public static RecyclableMemoryStream Get(string tag, long requiredSize) =>
        Manager.GetStream(tag, Math.Max(0L, requiredSize));

    /// <summary>Rent a pooled read/write stream pre-populated with <paramref name="bytes"/>.</summary>
    public static RecyclableMemoryStream FromBytes(string tag, ReadOnlySpan<byte> bytes) =>
        Manager.GetStream(tag, bytes);

    /// <summary>Compute a 64-bit hardware-accelerated XxHash3 digest over a byte span.</summary>
    public static ulong ComputeXxHash3(ReadOnlySpan<byte> bytes, long seed = 0) =>
        XxHash3.HashToUInt64(bytes, seed);

    /// <summary>Compute a 64-bit XxHash3 digest over the remaining bytes of <paramref name="stream"/>.</summary>
    public static ulong ComputeXxHash3(Stream stream, long seed = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var hasher = new XxHash3(seed);
        var scratch = ArrayPool<byte>.Shared.Rent(StreamHashScratchBytes);
        try
        {
            while (true)
            {
                var read = stream.Read(scratch, 0, scratch.Length);
                if (read <= 0) break;
                hasher.Append(scratch.AsSpan(0, read));
            }
            return hasher.GetCurrentHashAsUInt64();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}
