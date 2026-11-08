using System;
using System.Collections.Generic;
using System.Reflection;
using DealOrForce.Simulation;

static class StageBudgetTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("Stage budget: " + label);
        Console.WriteLine("PASS stage budget: " + label);
    }

    public static void Allocations(bool enforce)
    {
        // Compare against the *same* production iterator, not a guessed object size.
        // Reflection/delegate creation and JIT warm-up are outside both measurements.
        var c = new Campaign(61045) { IsPaused = true };
        var factory = (Func<IEnumerable<int>>)typeof(Campaign)
            .GetMethod("TickStages", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<IEnumerable<int>>), c);
        const int count = 20000;
        void Bare()
        {
            for (var i = 0; i < count; i++)
            {
                using var stages = factory().GetEnumerator();
                stages.MoveNext();
            }
        }
        void Stepped(double budget)
        {
            for (var i = 0; i < count; i++) c.StepFor(budget);
        }
        Bare();
        foreach (var budget in new[] { 0.0, 1.0, double.PositiveInfinity }) Stepped(budget);
        var start = GC.GetAllocatedBytesForCurrentThread();
        Bare();
        var iteratorBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        foreach (var budget in new[] { 0.0, 1.0, double.PositiveInfinity })
        {
            start = GC.GetAllocatedBytesForCurrentThread();
            Stepped(budget);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - start;
            Console.WriteLine($"STAGE_BUDGET budget={budget} iterations={count} iterator_bytes={iteratorBytes} total_bytes={bytes} overhead_bytes={bytes - iteratorBytes}");
            // Tiered JIT can eliminate a one-time allocation in the measured path
            // after Bare was sampled. Fewer bytes is valid; additional heap work
            // (including a single timing object) must still fail this guard.
            if (enforce) Check(bytes <= iteratorBytes, "budget timing adds no heap object, budget=" + budget);
        }
        Check(c.ElapsedHours == 0 && !c.MidStep, "allocation probe never advances a paused campaign");
    }

    public static void Run()
    {
        Allocations(true);
        const int seed = 61045;
        var whole = Campaign.CreateBootstrap(seed);
        whole.Defines.HoursPerTick = 24;
        whole.IsPaused = false;
        var expected = new ulong[3];
        for (var i = 0; i < expected.Length; i++)
        {
            whole.StepFor(double.PositiveInfinity);
            expected[i] = CampaignFingerprint.Of(whole);
        }
        var sliced = Campaign.CreateBootstrap(seed);
        sliced.Defines.HoursPerTick = 24;
        sliced.IsPaused = false;
        var completed = 0;
        sliced.StepCompleted += _ => completed++;
        for (var i = 0; i < expected.Length; i++)
        {
            var frames = 0;
            while (!sliced.StepFor(0))
                if (++frames > 1000) throw new Exception("Staged day did not terminate.");
            Check(CampaignFingerprint.Of(sliced) == expected[i], "zero-budget slices equal whole-day state on day " + (i + 1));
            Check(completed == i + 1, "completion is emitted once on day " + (i + 1));
        }
        StagedStepBoundaryTests.Run();
        HourlySlices();
    }

    private static void HourlySlices()
    {
        var whole = Campaign.CreateBootstrap(61047);
        whole.Defines.HoursPerTick = 1; whole.IsPaused = false;
        var expected = new ulong[48];
        var random = new ulong[48];
        for (var i = 0; i < expected.Length; i++)
        {
            whole.Tick(); expected[i] = CampaignFingerprint.Of(whole); random[i] = whole.Random.State;
        }
        var sliced = Campaign.CreateBootstrap(61047);
        sliced.Defines.HoursPerTick = 1; sliced.IsPaused = false;
        var completed = 0; sliced.StepCompleted += _ => completed++;
        for (var i = 0; i < expected.Length; i++)
        {
            var frames = 0;
            while (!sliced.StepFor(0))
                if (++frames > 1000) throw new Exception("Sliced hour did not terminate.");
            Check(CampaignFingerprint.Of(sliced) == expected[i] && sliced.Random.State == random[i],
                "hourly extra yield points preserve exact state/RNG at hour " + (i + 1));
        }
        Check(completed == expected.Length && !sliced.MidStep, "hourly slices publish exactly one completion per hour");
    }
}
