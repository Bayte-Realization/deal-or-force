using System;
using DealOrForce.Simulation;

/// <summary>Which headlines come up, and how often (HeadlinePopups, HeadlineQueue).</summary>
internal static class HeadlinePopupTests
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("FAIL headline popups: " + message);
        Console.WriteLine("PASS headline popups: " + message);
    }

    static NewsFeed.Line Line(long sequence, string kind) => new(sequence, 1, kind, kind, 1, 2);

    public static void Run()
    {
        Check(HeadlinePopups.Worth("important", 1, HeadlineTie.Own, false), "anything of the player's own comes up");
        Check(HeadlinePopups.Worth("important", 3, HeadlineTie.Neighbour, false) &&
              !HeadlinePopups.Worth("important", 2, HeadlineTie.Neighbour, false), "a neighbour's news comes up from importance 3");
        Check(HeadlinePopups.Worth("important", 2, HeadlineTie.Ally, false), "an ally's from importance 2");
        Check(!HeadlinePopups.Worth("important", 3, HeadlineTie.World, false) &&
              HeadlinePopups.Worth("important", 3, HeadlineTie.World, true), "the world's news only when a great power is in it");
        Check(!HeadlinePopups.Worth("off", 3, HeadlineTie.Own, true), "off is off");
        Check(HeadlinePopups.Worth("mine", 1, HeadlineTie.Own, false) &&
              !HeadlinePopups.Worth("mine", 3, HeadlineTie.Ally, true), "mine is the player's own news only");

        var queue = new HeadlineQueue();
        queue.Offer(Line(1, "coup"), HeadlineTie.Neighbour, 2);
        queue.Offer(Line(2, "coup"), HeadlineTie.Neighbour, 2);
        queue.Offer(Line(3, "coup"), HeadlineTie.Neighbour, 2);
        Check(queue.Waiting == 1, "three coups next door wait as one card");
        var first = queue.Next(0, 0);
        Check(first is { Count: 3 } && first.Line.Sequence == 3, "and it says three, with the newest sentence");
        queue.Offer(Line(4, "war_started"), HeadlineTie.Own, 3);
        Check(queue.Next(1, 1) is null, "a second card waits out the minimum gap");
        Check(queue.Next(HeadlineQueue.MinimumGap + 0.1, 1) is { Tie: HeadlineTie.Own }, "then comes up");
        queue.Offer(Line(5, "a"), HeadlineTie.World, 3);
        queue.Offer(Line(6, "b"), HeadlineTie.World, 3);
        var world = queue.Next(100, 0);
        Check(world is { Tie: HeadlineTie.World }, "a world headline comes up");
        Check(queue.Next(100 + HeadlineQueue.MinimumGap + 1, 0) is null, "the next world headline waits the longer world gap");
        Check(queue.Next(100 + HeadlineQueue.WorldGap + 1, 0) is { Tie: HeadlineTie.World }, "and then comes up");
        Check(queue.Next(1000, 3) is null || queue.Waiting == 0, "never more than three on screen");
        for (var i = 0; i < 10; i++) queue.Offer(Line(10 + i, "k" + i), i == 9 ? HeadlineTie.Own : HeadlineTie.World, 1);
        Check(queue.Waiting == HeadlineQueue.MaxWaiting, "the waiting list is bounded");
        Check(queue.Next(5000, 0) is { Tie: HeadlineTie.Own }, "the least important is dropped first, the player's own kept and first");
    }
}
