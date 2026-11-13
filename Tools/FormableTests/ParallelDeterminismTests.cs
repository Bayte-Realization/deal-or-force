using System;
using System.Collections.Generic;
using System.Threading;
using DealOrForce.Simulation;

/// <summary>
/// <see cref="DeterministicParallel"/> decides whether a pass may use the thread pool, and the
/// promise it makes is that the answer does not change the world - only the wall time. That is
/// the property the whole lockstep and the save fingerprint rest on, so it is checked here on
/// the helper itself rather than discovered as two runs of one save that disagree.
///
/// Four things are asserted: the loop covers every index once on both paths; a two-phase pass
/// applies its slots in ascending order on both paths and therefore ends with the same world;
/// a loop entered from inside a running loop runs on its own thread instead of nesting; and the
/// campaign's own switch is what turns the workers off.
/// </summary>
static class ParallelDeterminismTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("parallel determinism: " + note); Console.WriteLine("PASS parallel determinism: " + note); }

    public static void Run()
    {
        const int items = 400;

        // Every index once, on your own slot: the shape a regional pass uses.
        var parallel = Spread(forceSerial: false, items, out var ranParallel);
        var serial = Spread(forceSerial: true, items, out var ranSerial);
        Check(ranParallel && !ranSerial && DeterministicParallel.Workers > 1,
            "a large loop is handed out and a forced one is not");
        Check(Covered(parallel, items) && Covered(serial, items),
            "both paths cover every index exactly once");
        Check(Same(parallel, serial), "both paths write the same slot values");

        // The two-phase shape: compute into a slot, apply in ascending order, on this thread.
        var appliedParallel = Fold(forceSerial: false, items);
        var appliedSerial = Fold(forceSerial: true, items);
        Check(Ascending(appliedParallel) && Ascending(appliedSerial),
            "slots are applied in ascending index order on both paths");
        Check(Same(appliedParallel, appliedSerial),
            "a parallel pass and a serial pass leave the same world");

        // A pass too small to hand out stays where it is.
        Check(!DeterministicParallel.For(0, 5, _ => { }), "a short loop stays on the calling thread");

        // A loop entered while one is running must not nest: it takes this thread instead.
        if (DeterministicParallel.Workers > 1)
        {
            var outer = 0;
            var innerItems = 0;
            var innerParallel = 0;
            DeterministicParallel.For(0, 64, _ =>
            {
                Interlocked.Increment(ref outer);
                if (DeterministicParallel.For(0, 64, _ => Interlocked.Increment(ref innerItems)))
                    Interlocked.Increment(ref innerParallel);
            });
            Check(outer == 64 && innerItems == 64 * 64,
                "the nested loop runs to completion without deadlocking");
            Check(innerParallel == 0, "the nested loop runs on its own thread instead of nesting");
        }

        // The switch that turns workers off is the campaign's, and a probe may override it.
        DeterministicParallel.ResetDiagnostics();
        DeterministicParallel.Slots(64, index => index, (_, _) => { });
        Check(DeterministicParallel.ParallelRuns >= 1 && DeterministicParallel.ParallelItems >= 64,
            "the counters report what ran and how much it carried");
        var off = new Campaign(1, new Defines { ParallelRegions = false });
        Check(!DeterministicParallel.Enabled(off), "a campaign that switched regions off gets no workers");
        Check(DeterministicParallel.Enabled(new Campaign(2)), "an ordinary campaign gets workers");
        Check(DeterministicParallel.Enabled(null), "a probe with no world is not blocked by a switch");
        Check(DeterministicParallel.For(off, 0, 64, _ => { }) == false,
            "a pass that names its campaign obeys the campaign's switch");
    }

    /// <summary>Each index writes only its own slot, and the slots are read back afterwards.</summary>
    static int[] Spread(bool forceSerial, int count, out bool ranParallel)
    {
        var values = new int[count];
        var was = DeterministicParallel.ForceSerial;
        DeterministicParallel.ForceSerial = forceSerial;
        try
        {
            ranParallel = DeterministicParallel.For(0, count, index => values[index] = Value(index));
            return (int[])values.Clone();
        }
        finally { DeterministicParallel.ForceSerial = was; }
    }

    /// <summary>Computed into slots, then folded into a list on the calling thread.</summary>
    static List<int> Fold(bool forceSerial, int count)
    {
        var applied = new List<int>(count);
        var slots = new int[count];
        var was = DeterministicParallel.ForceSerial;
        DeterministicParallel.ForceSerial = forceSerial;
        try
        {
            DeterministicParallel.Slots(slots, Value, (_, value) => applied.Add(value));
            return applied;
        }
        finally { DeterministicParallel.ForceSerial = was; }
    }

    /// <summary>Something cheap, index-dependent and not the identity, so a swap would show.</summary>
    static int Value(int index) => (index * 37 + 11) % 97;

    static bool Covered(int[] values, int count)
    {
        if (values.Length != count) return false;
        for (var index = 0; index < count; index++)
            if (values[index] != Value(index)) return false;
        return true;
    }

    static bool Ascending(List<int> applied)
    {
        for (var index = 0; index < applied.Count; index++)
            if (applied[index] != Value(index)) return false;
        return applied.Count > 0;
    }

    static bool Same(int[] left, int[] right) => Covered(left, left.Length) && Covered(right, right.Length);

    static bool Same(List<int> left, List<int> right) => Ascending(left) && Ascending(right) && left.Count == right.Count;
}
