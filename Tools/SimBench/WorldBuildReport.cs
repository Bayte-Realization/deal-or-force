using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Where the seconds of world-building go.
///
/// A campaign takes several seconds to assemble - the bench has printed "Campaign created in 6421
/// ms" for a while - and every one of those seconds is a loading screen. Optimising that is
/// guesswork until somebody says which phase is slow, and a profiler attached to a headless run
/// answers a different question (where the CPU went) than the one that matters here (which step of
/// building the world is expensive, and is it that step or the file it reads).
///
/// So the builder records a phase timing when it is asked to, and this prints them. The cost of
/// asking is one empty list; the cost of not asking is one null check per phase.
/// </summary>
internal static class WorldBuildReport
{
    public static int Run(int seed, int repeats)
    {
        if (repeats < 1) repeats = 1;
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        var order = new List<string>();
        var wholeRun = 0.0;
        var accounted = 0.0;

        for (var run = 0; run < repeats; run++)
        {
            // A fresh list per run: the builder appends to whatever it finds, so a list carried
            // over from the last run would count every phase twice.
            ScenarioCatalog.BuildTimings = new List<(string Phase, double Milliseconds)>();
            var clock = Stopwatch.StartNew();
            var campaign = ScenarioCatalog.CreateBaseCampaign(seed);
            clock.Stop();
            wholeRun += clock.Elapsed.TotalMilliseconds;

            foreach (var (phase, milliseconds) in ScenarioCatalog.BuildTimings)
            {
                if (!totals.ContainsKey(phase)) order.Add(phase);
                totals[phase] = totals.TryGetValue(phase, out var held) ? held + milliseconds : milliseconds;
            }
            ScenarioCatalog.BuildTimings = null;
            if (run == 0)
                Console.WriteLine($"  the world: {campaign.Nations.Count} nations, {campaign.States.Count} regions, " +
                                  $"{campaign.States.Sum(region => region.Population):N0} people");
        }

        accounted = totals.Values.Sum();
        Console.WriteLine($"  {repeats} build(s), {wholeRun / repeats:0} ms each on average; " +
                          $"phases account for {accounted / repeats:0} ms, {(wholeRun - accounted) / repeats:0} ms unrecorded");
        Console.WriteLine();
        Console.WriteLine("    phase                            ms     share");
        foreach (var phase in order.OrderByDescending(name => totals[name]))
        {
            var mean = totals[phase] / repeats;
            Console.WriteLine($"    {phase.PadRight(30)} {mean,7:0.0}  {mean / Math.Max(1.0, accounted / repeats) * 100,6:0.0}%");
        }
        return 0;
    }
}
