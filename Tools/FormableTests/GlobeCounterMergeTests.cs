using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using C = DealOrForce.Simulation.GlobeCounterMerge.Counter;

/// <summary>The globe's region merge of army counters: pure, no engine (23 November 2026).</summary>
static class GlobeCounterMergeTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("globe counter merge: " + message);
        Console.WriteLine("PASS globe counter merge: " + message);
    }

    [TestSuite("--globe-counter-merge-only", Description = "Army counters on the globe merge by country and region at the strategic distance.")]
    public static void Run()
    {
        const int kor = 1, prk = 2, jpn = 3;
        const int gyeonggi = 10, gangwon = 11, busan = 12;
        var noFront = new HashSet<int>();
        static bool Peace(int a, int b) => false;
        static bool KoreanWar(int a, int b) => (a == 1 && b == 2) || (a == 2 && b == 1);
        var planner = new GlobeCounterMerge.Planner();
        var hosts = new int[16];

        // Distance and its dead band.
        Check(GlobeCounterMerge.Strategic(false, 8.4f), "the campaign's opening camera (8.4) merges");
        Check(!GlobeCounterMerge.Strategic(false, 6.0f), "a tactical close-up (6.0) never merges");
        Check(!GlobeCounterMerge.Strategic(false, 7.1f) && GlobeCounterMerge.Strategic(true, 7.1f),
            "inside the dead band the state the camera came from holds");
        Check(GlobeCounterMerge.Strategic(false, 7.31f) && !GlobeCounterMerge.Strategic(true, 6.69f),
            "past either edge of the band the state changes");

        // Two stacks of one country in one region become one; the largest hosts it.
        var two = new List<C> { new(kor, gyeonggi, 4f, false, false, false), new(kor, gyeonggi, 12f, false, false, false) };
        planner.Plan(two, true, noFront, Peace, hosts);
        Check(hosts[0] == 1 && hosts[1] == 1, "two counters of one country in one region show as one, hosted by the larger");
        planner.Plan(two, false, noFront, Peace, hosts);
        Check(hosts[0] == 0 && hosts[1] == 1, "close in (not strategic) both stand on their own");

        // Different regions, different countries.
        var spread = new List<C>
        {
            new(kor, gyeonggi, 3f, false, false, false), new(kor, gangwon, 3f, false, false, false),
            new(jpn, gyeonggi, 2f, false, false, false), new(kor, gyeonggi, 3f, false, false, false)
        };
        planner.Plan(spread, true, noFront, Peace, hosts);
        Check(hosts[0] == 0 && hosts[3] == 0, "a tie keeps the first counter as host");
        Check(hosts[1] == 1, "a counter in a neighbouring region is never combined");
        Check(hosts[2] == 2, "another country's counter in the same region stays its own");

        // In battle, moving and selected counters never merge.
        var busy = new List<C>
        {
            new(kor, busan, 5f, false, false, false), new(kor, busan, 9f, true, false, false),
            new(kor, busan, 2f, false, true, false), new(kor, busan, 1f, false, false, true),
            new(kor, busan, 3f, false, false, false)
        };
        planner.Plan(busy, true, noFront, Peace, hosts);
        Check(hosts[1] == 1, "a counter in battle never merges, however large");
        Check(hosts[2] == 2, "a counter with a move order never merges");
        Check(hosts[3] == 3, "a counter the player picked never merges");
        Check(hosts[0] == 0 && hosts[4] == 0, "the idle counters of the region still merge among themselves");

        // A front through the region, or an enemy at war in it, holds the line apart.
        var line = new List<C>
        {
            new(kor, gangwon, 4f, false, false, false), new(kor, gangwon, 4f, false, false, false),
            new(kor, busan, 2f, false, false, false), new(kor, busan, 2f, false, false, false)
        };
        planner.Plan(line, true, new HashSet<int> { gangwon }, KoreanWar, hosts);
        Check(hosts[0] == 0 && hosts[1] == 1, "a region on an active front keeps every counter apart");
        Check(hosts[2] == 2 && hosts[3] == 2, "a region behind the front still merges");
        var contact = new List<C>
        {
            new(kor, gyeonggi, 4f, false, false, false), new(kor, gyeonggi, 4f, false, false, false),
            new(prk, gyeonggi, 6f, false, false, false), new(prk, gyeonggi, 1f, false, false, false)
        };
        planner.Plan(contact, true, noFront, KoreanWar, hosts);
        Check(hosts[0] == 0 && hosts[1] == 1 && hosts[2] == 2 && hosts[3] == 3,
            "an enemy at war in the region keeps both sides apart");
        planner.Plan(contact, true, noFront, Peace, hosts);
        Check(hosts[1] == 0 && hosts[3] == 2, "the same two countries at peace each merge their own");

        // Counters with no region, and the input is never changed.
        var unplaced = new List<C> { new(kor, -1, 1f, false, false, false), new(kor, -1, 1f, false, false, false) };
        planner.Plan(unplaced, true, noFront, Peace, hosts);
        Check(hosts[0] == 0 && hosts[1] == 1, "a counter without a region stands alone");
        var before = new List<C>(busy);
        planner.Plan(busy, true, noFront, Peace, hosts);
        var same = true;
        for (var i = 0; i < busy.Count; i++) same &= busy[i] == before[i];
        Check(same, "planning reads the counters and changes none of them");
        var again = new int[16];
        planner.Plan(busy, true, noFront, Peace, again);
        var stable = true;
        for (var i = 0; i < busy.Count; i++) stable &= again[i] == hosts[i];
        Check(stable, "the same counters give the same plan every time");
    }
}
