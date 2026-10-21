using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Where a campaign day goes.
///
/// The simulation already measures itself - every staged pass wraps itself in a SimProfiler
/// scope - so the question is not how to instrument it but which scope is eating the day.
/// This runs a real world for a few hundred days and prints the worst offenders, which is the
/// only honest way to choose what to optimise rather than guessing.
/// </summary>
static class TickBench
{
    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20261019);
        MapRasterCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError))
        { Console.WriteLine("BENCH map failed: " + mapError); return; }
        campaign.AttachProvinceTopology(map);
        // A campaign opens paused, the way a player finds it. Unpause it, and forget whatever
        // the bootstrap already measured, so the numbers below are the days and nothing else.
        campaign.IsPaused = false;
        SimProfiler.Reset();

        const int days = 240;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var day = 0; day < days; day++) campaign.TickDay();
        clock.Stop();

        Console.WriteLine($"BENCH {days} days in {clock.ElapsedMilliseconds} ms " +
            $"({clock.ElapsedMilliseconds / (double)days:0.00} ms/day)");
        var worst = SimProfiler.All
            .OrderByDescending(sample => sample.TotalMilliseconds)
            .Take(25);
        Console.WriteLine($"{"scope",-28} {"calls",-9} {"total ms",-10} {"avg ms",-9} {"alloc MB",-9}");
        foreach (var sample in worst)
            Console.WriteLine($"BENCH {sample.Name,-28} {sample.Runs,-9} {sample.TotalMilliseconds,-10:0.0} " +
                $"{sample.MeanMilliseconds,-9:0.000} {sample.AllocatedBytes / 1048576.0,-9:0.0}");
    }
}
