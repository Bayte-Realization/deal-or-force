using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DealOrForce.Simulation;

/// <summary>
/// Thread-count controls and cache-friendly range-partitioned deterministic parallel helpers.
///
/// Extends <see cref="DeterministicParallel"/> with:
/// - <see cref="MaxThreadsOverride"/>: a runtime/test knob allowing exact 1-thread vs N-thread
///   determinism verification without rebuilding or mutating campaign defines.
/// - <see cref="ForRange(Campaign, int, int, Action{int, int}, int)"/>: contiguous chunked
///   execution that eliminates per-item delegate overhead and reduces false sharing.
/// - <see cref="SlotsRange{T}(Campaign, int, T[], Action{int, int, T[]}, Action{int, T}, int)"/>:
///   chunked parallel compute into pre-allocated per-index slots followed by deterministic
///   ascending sequential commit on the calling thread.
/// </summary>
public static partial class DeterministicParallel
{
    private const int MaxConfigurableWorkers = 32;

    // Pre-built ParallelOptions per worker count so toggling MaxThreadsOverride never allocates.
    private static readonly ParallelOptions[] OptionsByWorkers = BuildOptionsTable();

    private static ParallelOptions[] BuildOptionsTable()
    {
        var table = new ParallelOptions[MaxConfigurableWorkers + 1];
        for (var i = 1; i <= MaxConfigurableWorkers; i++)
            table[i] = new ParallelOptions { MaxDegreeOfParallelism = i };
        return table;
    }

    private static int _maxThreadsOverride;

    /// <summary>
    /// Optional explicit worker-thread cap for profiling and determinism tests:
    /// <c>0</c> (default) uses <see cref="Workers"/>; <c>1</c> forces single-threaded execution;
    /// <c>N &gt; 1</c> caps parallel loops to <c>min(N, ProcessorCount, 32)</c> workers.
    /// Not saved and never part of campaign state.
    /// </summary>
    public static int MaxThreadsOverride
    {
        get => Volatile.Read(ref _maxThreadsOverride);
        set => Volatile.Write(ref _maxThreadsOverride, Math.Clamp(value, 0, MaxConfigurableWorkers));
    }

    /// <summary>Effective worker count taking <see cref="MaxThreadsOverride"/> and serial switches into account.</summary>
    public static int EffectiveWorkers
    {
        get
        {
            if (ForceSerial || EnvironmentSerial) return 1;
            var over = Volatile.Read(ref _maxThreadsOverride);
            if (over > 0) return Math.Min(over, Math.Max(1, Environment.ProcessorCount));
            return Workers;
        }
    }

    private static ParallelOptions ActiveOptions()
    {
        var w = Math.Clamp(EffectiveWorkers, 1, MaxConfigurableWorkers);
        return OptionsByWorkers[w];
    }

    /// <summary>
    /// Execute contiguous index ranges <c>[rangeStart, rangeEnd)</c> across workers when allowed,
    /// avoiding per-item delegate dispatch overhead on dense loops.
    /// </summary>
    public static bool ForRange(Campaign campaign, int from, int toExclusive,
        Action<int, int> rangeBody, int minItems = MinItems) =>
        ForRangeCore(Enabled(campaign), from, toExclusive, rangeBody, minItems);

    public static bool ForRange(int from, int toExclusive,
        Action<int, int> rangeBody, int minItems = MinItems) =>
        ForRangeCore(allowed: true, from, toExclusive, rangeBody, minItems);

    private static bool ForRangeCore(bool allowed, int from, int toExclusive,
        Action<int, int> rangeBody, int minItems)
    {
        if (rangeBody is null) return false;
        var count = toExclusive - from;
        if (count <= 0) return false;

        // EffectiveMinItems: the caller's threshold, raised on a big.LITTLE phone
        // (MobileBigLittleCap) so a small loop never wakes the pool under MonoVM.
        if (!allowed || count < EffectiveMinItems(minItems) || !TryLease())
        {
            Interlocked.Increment(ref _serialRuns);
            rangeBody(from, toExclusive);
            return false;
        }

        try
        {
            var options = ActiveOptions();
            var workers = options.MaxDegreeOfParallelism;
            var chunkSize = Math.Max(1, (count + workers * 2 - 1) / (workers * 2));
            var chunkCount = (count + chunkSize - 1) / chunkSize;
            Parallel.For(0, chunkCount, options, chunk =>
            {
                var start = (int)Math.Min(toExclusive, from + (long)chunk * chunkSize);
                var end = (int)Math.Min(toExclusive, (long)start + chunkSize);
                rangeBody(start, end);
            });
            Interlocked.Increment(ref _parallelRuns);
            Interlocked.Add(ref _parallelItems, count);
            return true;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// Chunked two-phase deterministic parallel pass:
    /// workers fill disjoint slices of <paramref name="slots"/> in parallel, then the calling
    /// thread commits <c>0..count-1</c> in strict ascending order.
    /// </summary>
    public static bool SlotsRange<T>(Campaign campaign, int count, T[] slots,
        Action<int, int, T[]> computeRange, Action<int, T> apply, int minItems = MinItems) =>
        SlotsRangeCore(Enabled(campaign), count, slots, computeRange, apply, minItems);

    public static bool SlotsRange<T>(int count, T[] slots,
        Action<int, int, T[]> computeRange, Action<int, T> apply, int minItems = MinItems) =>
        SlotsRangeCore(allowed: true, count, slots, computeRange, apply, minItems);

    private static bool SlotsRangeCore<T>(bool allowed, int count, T[] slots,
        Action<int, int, T[]> computeRange, Action<int, T> apply, int minItems)
    {
        if (count <= 0 || slots is null || computeRange is null || apply is null) return false;
        if (count > slots.Length)
            throw new ArgumentException("the slot array is shorter than the count", nameof(slots));

        if (!allowed || count < EffectiveMinItems(minItems) || !TryLease())
        {
            Interlocked.Increment(ref _serialRuns);
            computeRange(0, count, slots);
            for (var index = 0; index < count; index++) apply(index, slots[index]);
            return false;
        }

        try
        {
            var options = ActiveOptions();
            var workers = options.MaxDegreeOfParallelism;
            var chunkSize = Math.Max(1, (count + workers * 2 - 1) / (workers * 2));
            var chunkCount = (count + chunkSize - 1) / chunkSize;
            Parallel.For(0, chunkCount, options, chunk =>
            {
                var start = Math.Min(count, chunk * chunkSize);
                var end = Math.Min(count, start + chunkSize);
                computeRange(start, end, slots);
            });
            for (var index = 0; index < count; index++) apply(index, slots[index]);
            Interlocked.Increment(ref _parallelRuns);
            Interlocked.Add(ref _parallelItems, count);
            return true;
        }
        finally
        {
            Release();
        }
    }
}
