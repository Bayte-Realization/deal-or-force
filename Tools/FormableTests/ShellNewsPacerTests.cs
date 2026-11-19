using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

static class ShellNewsPacerTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL news-pacer: " + label);
        Console.WriteLine("PASS news-pacer: " + label);
    }

    private static NewsFeed.Line Line(long seq, string kind, string text = null, int day = 10, int actor = 1, int target = 2) =>
        new(seq, day, kind, text ?? $"Dispatch #{seq} ({kind})", actor, target);

    [TestSuite("--news-pacer-only", Description = "ShellNewsPacer dwell, bounded priority queue, lifetime, hover/pin freeze, and audio cooldown.")]
    public static void Run()
    {
        InterfaceSkin.ResetCatalog();
        try
        {
            TestMinimumDwellAndSwap();
            TestBoundedPriorityQueue();
            TestHoverAndPinFreeze();
            TestLifetimeExpiry();
            TestUrgentPreemptionAndPausedSafety();
            TestAudioCueThrottle();
            TestSkinModMetrics();
            TestUidAndZeroGodotInPacer();
        }
        finally
        {
            InterfaceSkin.ResetCatalog();
        }
    }

    private static void TestMinimumDwellAndSwap()
    {
        var pacer = new ShellNewsPacer();
        pacer.Offer(Line(1, "construction"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        var s0 = pacer.Tick(0.0, hovered: false);
        Check(s0.Changed && s0.Current?.Sequence == 1, "first routine card displays immediately");

        // Second routine dispatch arrives during min dwell (2.8s): must queue, not swap immediately
        pacer.Offer(Line(2, "trade"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        var s1 = pacer.Tick(1.2, hovered: false);
        Check(!s1.Changed && s1.Current?.Sequence == 1 && s1.QueuedCount == 1,
            "routine card holds during 1.2s (< 2.8s min dwell) while second queues");

        var s2 = pacer.Tick(1.4, hovered: false); // Age = 2.6s < 2.8s
        Check(!s2.Changed && s2.Current?.Sequence == 1 && s2.QueuedCount == 1,
            "routine card still holds at 2.6s (< 2.8s min dwell)");

        var s3 = pacer.Tick(0.3, hovered: false); // Age = 2.9s >= 2.8s
        Check(s3.Changed && s3.Current?.Sequence == 2 && s3.QueuedCount == 0 && Math.Abs(s3.Current.Age) < 1e-6,
            "queued routine card swaps in cleanly once min dwell (2.8s) elapses");
    }

    private static void TestBoundedPriorityQueue()
    {
        var pacer = new ShellNewsPacer();
        pacer.Offer(Line(1, "construction"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        pacer.Tick(0.1, hovered: false);

        // Fill queue with 6 routine items (seq 2..7)
        for (long i = 2; i <= 7; i++)
            pacer.Offer(Line(i, "trade"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        Check(pacer.QueuedCount == ShellNewsPacer.DefaultMaxQueue, $"pending queue caps at {ShellNewsPacer.DefaultMaxQueue}");

        // Offering War & Crisis dispatches must evict routine fluff, NOT other War dispatches
        pacer.Offer(Line(8, "war_declared"), "Urgent dispatch", ShellNewsPriority.War, urgent: false);
        pacer.Offer(Line(9, "crisis"), "Dispatch", ShellNewsPriority.Crisis, urgent: false);
        Check(pacer.QueuedCount == ShellNewsPacer.DefaultMaxQueue, "queue remains bounded after high-priority arrivals");
        Check(pacer.PendingQueue.Any(x => x.Sequence == 8) && pacer.PendingQueue.Any(x => x.Sequence == 9),
            "War and Crisis dispatches retained in queue over routine fluff");

        // Now fill queue with 6 War dispatches and ensure incoming routine fluff cannot evict any War item
        var warPacer = new ShellNewsPacer();
        warPacer.Offer(Line(100, "war_declared"), "Urgent dispatch", ShellNewsPriority.War, urgent: true);
        warPacer.Tick(0.1, hovered: false);
        for (long i = 101; i <= 106; i++)
            warPacer.Offer(Line(i, "war_declared"), "Urgent dispatch", ShellNewsPriority.War, urgent: true);
        for (long i = 200; i <= 210; i++)
            warPacer.Offer(Line(i, "construction"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        Check(warPacer.QueuedCount == 6 && warPacer.PendingQueue.All(x => x.Priority == ShellNewsPriority.War),
            "routine fluff cannot evict War dispatches when queue is full");

        // Dequeue order prioritizes War (#8) then Crisis (#9) ahead of routine (#4..#7)
        var step = pacer.Tick(3.0, hovered: false);
        Check(step.Changed && step.Current?.Sequence == 8, "War dispatch dequeues ahead of queued routine fluff");
        step = pacer.Tick(3.0, hovered: false);
        Check(step.Changed && step.Current?.Sequence == 9, "Crisis dispatch dequeues before remaining routine items");
    }

    private static void TestHoverAndPinFreeze()
    {
        var pacer = new ShellNewsPacer();
        pacer.Offer(Line(10, "trade"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        pacer.Tick(2.0, hovered: false);
        Check(Math.Abs(pacer.Active.Age - 2.0) < 1e-6, "age advances normally to 2.0s unhovered");

        // Hovering freezes age completely and prevents queued swap & lifetime expiry
        pacer.Offer(Line(11, "war_declared"), "Urgent dispatch", ShellNewsPriority.Urgent, urgent: true);
        var hoverStep = pacer.Tick(25.0, hovered: true);
        Check(hoverStep.Frozen && hoverStep.Current?.Sequence == 10 && Math.Abs(hoverStep.Current.Age - 2.0) < 1e-6 && hoverStep.QueuedCount == 1,
            "hover completely freezes card age (2.0s after 25s tick) and blocks swap/expiry under cursor");

        // Pinning also freezes age even when cursor leaves
        pacer.SetPinned(true);
        var pinStep = pacer.Tick(30.0, hovered: false);
        Check(pinStep.Frozen && pinStep.Current?.Sequence == 10 && Math.Abs(pinStep.Current.Age - 2.0) < 1e-6,
            "pinning keeps card frozen even when mouse is no longer hovering");

        // Unpinning resumes aging and immediately swaps to queued urgent dispatch
        pacer.SetPinned(false);
        var resumeStep = pacer.Tick(0.1, hovered: false);
        Check(!resumeStep.Frozen && resumeStep.Changed && resumeStep.Current?.Sequence == 11,
            "unpinning resumes and promotes queued urgent dispatch");
    }

    private static void TestLifetimeExpiry()
    {
        var pacer = new ShellNewsPacer();
        pacer.Offer(Line(30, "trade"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        pacer.Tick(0.0, hovered: false);

        var sMid = pacer.Tick(8.5, hovered: false);
        Check(sMid.Current?.Sequence == 30 && Math.Abs(sMid.Current.Age - 8.5) < 1e-6,
            "card remains active at 8.5s (< 9.0s lifetime)");

        var sEnd = pacer.Tick(0.6, hovered: false); // 9.1s >= 9.0s
        Check(sEnd.Changed && sEnd.Current is null,
            "card expires cleanly after 9.0s lifetime when queue is empty");
    }

    private static void TestUrgentPreemptionAndPausedSafety()
    {
        var pacer = new ShellNewsPacer();
        pacer.Offer(Line(40, "construction"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        pacer.Tick(0.4, hovered: false);

        // Urgent dispatch preempts unhovered routine card and preserves routine card in queue
        pacer.Offer(Line(41, "nuclear_strike"), "Urgent dispatch", ShellNewsPriority.Urgent, urgent: true);
        var s = pacer.Tick(0.0, hovered: false);
        Check(s.Changed && s.Current?.Sequence == 41 && s.QueuedCount == 1,
            "urgent dispatch preempts unhovered routine card and queues displaced item");

        // Urgent dispatch does NOT get bumped at minDwell by routine item, nor expire while clock paused
        var pausedStep = pacer.Tick(20.0, hovered: false, paused: true);
        Check(pausedStep.Frozen && pausedStep.Current?.Sequence == 41 && Math.Abs(pausedStep.Current.Age) < 1e-6,
            "clock pause freezes urgent card age so player can read it");

        var afterMinDwell = pacer.Tick(3.5, hovered: false, paused: false);
        Check(!afterMinDwell.Changed && afterMinDwell.Current?.Sequence == 41,
            "urgent dispatch keeps screen past minDwell when only routine item waits in queue");

        var afterLife = pacer.Tick(6.0, hovered: false, paused: false); // 9.5s >= 9.0s
        Check(afterLife.Changed && afterLife.Current?.Sequence == 40,
            "displaced routine card appears cleanly once urgent dispatch completes full lifetime");
    }

    private static void TestAudioCueThrottle()
    {
        var pacer = new ShellNewsPacer();
        Check(pacer.ShouldPlayNewsCue(0.0, urgent: false), "first routine audio cue plays immediately at t=0");
        Check(!pacer.ShouldPlayNewsCue(0.5, urgent: false), "routine cue throttled at 0.5s (< 3.5s)");
        Check(!pacer.ShouldPlayNewsCue(3.4, urgent: false), "routine cue still throttled at 3.4s (< 3.5s)");
        Check(pacer.ShouldPlayNewsCue(3.5, urgent: false), "routine cue allowed once 3.5s cooldown elapses");

        // Urgent cue breaks routine cooldown, but rapid urgent cascade (< 1s) is still guarded
        Check(pacer.ShouldPlayNewsCue(4.0, urgent: true), "urgent cue overrides routine cooldown");
        Check(!pacer.ShouldPlayNewsCue(4.2, urgent: true), "same-second urgent burst does not machine-gun audio");
        Check(!pacer.ShouldPlayNewsCue(5.5, urgent: false), "routine cue waits 3.5s after urgent cue");
        Check(pacer.ShouldPlayNewsCue(7.6, urgent: false), "routine cue resumes after 3.5s post-urgent gap");
    }

    private static void TestSkinModMetrics()
    {
        InterfaceSkin.Merge(@"{
          ""metrics"": {
            ""shell.news_min_dwell_sec"": 1.5,
            ""shell.news_max_queue"": 2,
            ""shell.news_lifetime_sec"": 4.0,
            ""shell.news_audio_cooldown_sec"": 1.0
          }
        }", "test-news-pacer-mod", null);

        Check(Math.Abs(ShellNewsPacer.MinDwellSec - 1.5f) < 0.01f &&
              ShellNewsPacer.MaxQueue == 2 &&
              Math.Abs(ShellNewsPacer.LifetimeSec - 4.0f) < 0.01f &&
              Math.Abs(ShellNewsPacer.AudioCooldownSec - 1.0f) < 0.01f,
            "InterfaceSkin.Metric overrides configure dwell, queue cap, lifetime, and audio cooldown");

        var pacer = new ShellNewsPacer();
        pacer.Offer(Line(1, "trade"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        pacer.Tick(0.0, hovered: false);
        pacer.Offer(Line(2, "trade"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        pacer.Offer(Line(3, "trade"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        pacer.Offer(Line(4, "trade"), "Dispatch", ShellNewsPriority.Routine, urgent: false);
        Check(pacer.QueuedCount == 2, "modded max_queue=2 caps pending items at 2");
        Check(pacer.Tick(1.6, hovered: false).Current?.Sequence == 3, "modded min_dwell=1.5s swaps at 1.6s");
        Check(pacer.ShouldPlayNewsCue(0.0, false) && !pacer.ShouldPlayNewsCue(0.8, false) && pacer.ShouldPlayNewsCue(1.1, false),
            "modded audio_cooldown=1.0s gates at 1.0s");
    }

    private static void TestUidAndZeroGodotInPacer()
    {
        var repo = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        var pacerPath = Path.Combine(repo, "Scripts", "Simulation", "Shell", "ShellNewsPacer.cs");
        var uidPath = pacerPath + ".uid";
        Check(File.Exists(pacerPath) && File.Exists(uidPath), "ShellNewsPacer.cs and .cs.uid sidecar exist");
        var src = File.ReadAllText(pacerPath);
        Check(!src.Contains("Godot", StringComparison.Ordinal), "ShellNewsPacer.cs has zero Godot dependency");
        var testUidPath = Path.Combine(repo, "Tools", "FormableTests", "ShellNewsPacerTests.cs.uid");
        Check(File.Exists(testUidPath) && File.ReadAllText(testUidPath).Trim().StartsWith("uid://", StringComparison.Ordinal),
            "ShellNewsPacerTests.cs.uid exists and starts with uid://");
    }
}
