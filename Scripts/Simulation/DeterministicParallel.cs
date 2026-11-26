using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DealOrForce.Simulation;

/// <summary>
/// The one place that says how a pass may use the thread pool, and the one rule every such
/// pass obeys:
///
/// > compute in your own slot by index, apply in ascending index order on this thread,
/// > keep scratch on your own stack.
///
/// There were four copies of that rule before this file - the regional economy, the
/// demographic cycle, the hostile-flag tally of the whole map, and the market week - and each
/// copy carried its own <see cref="ParallelOptions"/>, its own threshold, and (in two of them)
/// its own "somebody is already inside" flag. Four copies of a rule is four chances to write
/// it slightly differently, and the difference only shows up as a world that two runs of the
/// same save do not agree on. A pass that wants workers calls in here instead.
///
/// The rule is not a style preference. The campaign is deterministic and lockstep: the host
/// and the client must be handed the same world, and the fingerprint in the save is how that
/// is checked. Two things make that true under threads, and this file is both of them:
///
/// 1. <b>Nothing writes to the shared world from a worker.</b> A worker fills its own slot, or
///    its own thread-local scratch; the merge back into the world happens afterwards, on the
///    calling thread, in ascending index order. Set union, sums and lists folded in that order
///    reproduce the serial pass byte for byte, including the insertion order of dictionaries
///    that a later pass walks.
/// 2. <b>Only one loop runs at a time.</b> A lease is taken for the duration of a parallel
///    loop; a second loop entered while one is running - a nested call, or a second campaign
///    on another thread - runs serially on its own thread instead of fighting for cores and
///    invalidating the order the first one promised. The world is the same either way; only
///    the wall time differs, and it differs in the direction of not oversubscribing the box.
///
/// Both paths - parallel and serial - go through the same code, so a loop that was measured
/// serially is also a correctness check of the parallel one: run the same save twice, once
/// with <see cref="ForceSerial"/> (or <c>BDOF_NO_PARALLEL=1</c>), and the fingerprints must
/// match. `Tools/FormableTests --parallel-determinism-only` does exactly that on the helper
/// itself.
/// </summary>
public static partial class DeterministicParallel
{
    /// <summary>
    /// How many regions, wars or goods have to be waiting before handing them out is worth
    /// the hand-off. Below this a pass runs on the calling thread: the work is a few
    /// microseconds and the thread pool's wake-up is longer than it.
    /// </summary>
    public const int MinItems = 16;

    /// <summary>
    /// Whether this process caps parallel workers to a mobile-friendly core slice (<c>2..4</c>) so slow
    /// efficiency cores never stall thread-pool barriers. Defaults to <c>true</c> on Android/BlueStacks.
    /// </summary>
    public static bool TouchCapEnabled { get; set; } = OperatingSystem.IsAndroid();

    /// <summary>
    /// The big.LITTLE rule for phones (Galaxy S22 and its kind: one prime core, three big ones and four
    /// slow Cortex-A510 efficiency cores). A barrier waits for its slowest worker, so extra workers
    /// landing on efficiency cores make every loop as slow as those cores. When this is on, a pass
    /// gets at most <c>Math.Clamp(cores / 2, 2, 4)</c> workers (fitting the prime + performance cluster)
    /// and a loop's minimum item threshold is raised modestly via <see cref="ResolveMinItems"/>
    /// (<c>Math.Max(minItems, Math.Min(minItems * 2, 24))</c>) so real AI/nation passes still run in
    /// parallel while tiny 16-item loops stay serial under MonoVM. Pure .NET, no engine type: defaults
    /// to on for Android. Only the wall time changes - every pass computes by index and merges in
    /// index order, so the world is the same with it on or off.
    /// </summary>
    public static bool MobileBigLittleCap { get; set; } = OperatingSystem.IsAndroid();

    /// <summary>The ceiling used by <see cref="ResolveMinItems"/> when <see cref="MobileBigLittleCap"/> is on.</summary>
    public const int MobileMinItems = 24;

    /// <summary>The same ceiling, aliased for readability.</summary>
    public const int MobileMinItemsCap = MobileMinItems;

    /// <summary>
    /// The workers one pass may use: under <see cref="MobileBigLittleCap"/> the big/prime cores
    /// (<c>Math.Clamp(cores / 2, 2, 4)</c>); on touch/Android capped to <c>Math.Clamp(cores / 2, 2, 4)</c>
    /// to keep off slow E-cores; on desktop one fewer than logical processors up to eight, because passes
    /// are memory-bound long before they are core-bound and the ninth worker buys a cache fight rather
    /// than throughput.
    /// </summary>
    public static int Workers => MobileBigLittleCap
        ? Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
        : TouchCapEnabled
            ? Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
            : Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));

    /// <summary>The item threshold a loop actually uses: the caller's, scaled modestly on a phone.</summary>
    internal static int EffectiveMinItems(int minItems) => ResolveMinItems(minItems, MobileBigLittleCap);

    internal static int ResolveMinItems(int minItems, bool mobileCap) =>
        mobileCap ? Math.Max(minItems, Math.Min(minItems * 2, MobileMinItemsCap)) : minItems;

    /// <summary>
    /// How many workers a loop of <paramref name="itemCount"/> items would be spread over, as a pure
    /// function of its inputs - 1 means it runs on the calling thread. For tests and probes; the loops
    /// themselves also honour <see cref="ForceSerial"/>, the environment switch and the lease.
    /// </summary>
    internal static int ResolveWorkerCount(int itemCount, int minItems, bool mobileCap, int processorCount)
    {
        if (itemCount <= 0 || itemCount < ResolveMinItems(minItems, mobileCap)) return 1;
        var workers = mobileCap
            ? Math.Clamp(processorCount / 2, 2, 4)
            : Math.Max(1, Math.Min(processorCount - 1, 8));
        return Math.Max(1, Math.Min(workers, Math.Max(1, processorCount)));
    }

    /// <summary>
    /// <c>BDOF_NO_PARALLEL=1</c> in the environment keeps every loop on the calling thread.
    /// A support case that smells of a race is answered by starting the game with it and
    /// asking whether the world still diverges; nothing in the shipped game sets it.
    /// </summary>
    private static readonly bool EnvironmentSerial =
        Environment.GetEnvironmentVariable("BDOF_NO_PARALLEL") == "1";

    /// <summary>
    /// The same answer as the environment variable, for a probe or a test that has to switch
    /// mid-process. Not saved, not part of the world.
    /// </summary>
    public static bool ForceSerial { get; set; }

    /// <summary>
    /// Whether this campaign wants workers at all, before the size question is asked: the
    /// player can turn it off in the defines, the environment can override it, and a machine
    /// with one usable core has nothing to hand out.
    /// </summary>
    public static bool Enabled(Campaign campaign) =>
        EffectiveWorkers > 1 && campaign?.Defines is not { ParallelRegions: false };

    /// <summary>1 while a parallel loop is running, 0 otherwise. See the lease in the summary.</summary>
    private static int _inside;

    private static int _parallelRuns;
    private static int _serialRuns;
    private static long _parallelItems;

    /// <summary>How many loops have actually been spread over workers this process.</summary>
    public static int ParallelRuns => Volatile.Read(ref _parallelRuns);

    /// <summary>How many have run on the calling thread: too small, switched off, or leased out.</summary>
    public static int SerialRuns => Volatile.Read(ref _serialRuns);

    /// <summary>How many items the parallel loops have carried, for a probe that wants scale.</summary>
    public static long ParallelItems => Volatile.Read(ref _parallelItems);

    /// <summary>Forget the three counters. The world is unaffected; this is a probe's reset.</summary>
    public static void ResetDiagnostics()
    {
        Volatile.Write(ref _parallelRuns, 0);
        Volatile.Write(ref _serialRuns, 0);
        Volatile.Write(ref _parallelItems, 0);
    }

    /// <summary>
    /// <paramref name="from"/> (inclusive) to <paramref name="toExclusive"/>, worked side by
    /// side when there is enough of it and nobody else is inside.
    ///
    /// Returns true when the body ran on workers and false when it ran on the calling thread.
    /// The return value is not "did the work happen" - it always happens - and a caller that
    /// merges results must not branch on it; it exists so a probe can see which path was taken.
    /// </summary>
    public static bool For(int from, int toExclusive, Action<int> body, int minItems = MinItems) =>
        ForCore(allowed: true, from, toExclusive, body, minItems);

    /// <summary>
    /// The same for a pass on the tick path: the campaign has the last word, through its
    /// defines and through <see cref="Enabled"/>. A pass that calls this cannot forget the
    /// switch, because the switch is not in the pass.
    /// </summary>
    public static bool For(Campaign campaign, int from, int toExclusive, Action<int> body, int minItems = MinItems) =>
        ForCore(Enabled(campaign), from, toExclusive, body, minItems);

    private static bool ForCore(bool allowed, int from, int toExclusive, Action<int> body, int minItems)
    {
        if (body is null) return false;
        var count = toExclusive - from;
        if (count <= 0) return false;

        if (!allowed || count < EffectiveMinItems(minItems) || !TryLease())
        {
            Interlocked.Increment(ref _serialRuns);
            for (var index = from; index < toExclusive; index++) body(index);
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
                for (var index = start; index < end; index++)
                    body(index);
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
    /// The shape a pass should use when it changes the world rather than only reading it:
    /// every index is computed into its own slot - possibly on a worker - and then the slots
    /// are applied to the world on this thread, in ascending index order.
    ///
    /// This is the two-phase pattern the design notes call for. Computing in the world is
    /// what makes a pass order-dependent and therefore unsafe to split; computing into a slot
    /// and merging afterwards is the same answer as the serial pass, because the merge sees
    /// exactly the sequence the serial pass produced.
    /// </summary>
    /// <param name="slots">Caller-owned storage, reused across days so the merge allocates nothing.</param>
    /// <param name="compute">Runs on a worker: reads the world, writes only its own return value.</param>
    /// <param name="apply">Runs on this thread in ascending index order: the only writer of the world.</param>
    public static bool Slots<T>(T[] slots, Func<int, T> compute, Action<int, T> apply, int minItems = MinItems)
    {
        if (slots is null) return false;
        return SlotsCore(allowed: true, slots.Length, slots, compute, apply, minItems);
    }

    /// <summary>The same, with the campaign's own switch deciding, for a pass on the tick path.</summary>
    public static bool Slots<T>(Campaign campaign, T[] slots, Func<int, T> compute, Action<int, T> apply, int minItems = MinItems)
    {
        if (slots is null) return false;
        return SlotsCore(Enabled(campaign), slots.Length, slots, compute, apply, minItems);
    }

    /// <summary>The same, with caller-owned reusable storage whose capacity may exceed <paramref name="count"/>.</summary>
    public static bool Slots<T>(Campaign campaign, int count, T[] slots, Func<int, T> compute, Action<int, T> apply, int minItems = MinItems)
    {
        if (slots is null || count <= 0) return false;
        return SlotsCore(Enabled(campaign), count, slots, compute, apply, minItems);
    }

    /// <summary>
    /// The same, for a pass that has no reason to keep the slots between days. Allocates one
    /// array of the size; a pass on the tick path should prefer the overload above.
    /// </summary>
    public static bool Slots<T>(int count, Func<int, T> compute, Action<int, T> apply, int minItems = MinItems)
    {
        if (count <= 0) return false;
        return SlotsCore(allowed: true, count, new T[count], compute, apply, minItems);
    }

    /// <summary>The same, with the campaign's own switch deciding.</summary>
    public static bool Slots<T>(Campaign campaign, int count, Func<int, T> compute, Action<int, T> apply, int minItems = MinItems)
    {
        if (count <= 0) return false;
        return SlotsCore(Enabled(campaign), count, new T[count], compute, apply, minItems);
    }

    private static bool SlotsCore<T>(bool allowed, int count, T[] slots, Func<int, T> compute, Action<int, T> apply, int minItems)
    {
        if (count <= 0 || compute is null || apply is null) return false;
        if (count > slots.Length)
            throw new ArgumentException("the slot array is shorter than the count", nameof(slots));

        if (!allowed || count < EffectiveMinItems(minItems) || !TryLease())
        {
            Interlocked.Increment(ref _serialRuns);
            for (var index = 0; index < count; index++) apply(index, compute(index));
            return false;
        }

        try
        {
            Parallel.For(0, count, ActiveOptions(), index => slots[index] = compute(index));
            // Ascending, on this thread, every time: this line is what makes the parallel pass
            // and the serial pass the same world.
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

    /// <summary>
    /// Takes the lease, or answers false and means "run it yourself". Both the switched-off
    /// cases and the already-busy case come back here as false, because in all three the
    /// answer is the same: this thread does the work.
    /// </summary>
    private static bool TryLease()
    {
        if (EffectiveWorkers <= 1) return false;
        return Interlocked.CompareExchange(ref _inside, 1, 0) == 0;
    }

    private static void Release() => Volatile.Write(ref _inside, 0);
}
