using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// "A shortage of everything" from the first week, while the world had almost everything to spare.
///
/// Two rules made it. The import ceiling multiplied a country's shortfall by WorldMarketAccess x
/// transport reach, and 2024 reach runs 0.45-0.72, so every country went looking for 36-58 % of
/// what it lacked and the exporters sat on the rest (SimBench --trade-why: 49 % sought, 530 of 900
/// units of oil unsold). And buyers were served largest first, so when a good was genuinely scarce
/// the giants emptied the pool and the islands got a quarter of their grain from the grey market.
/// ImportReachReference calibrates the ceiling (1.0 is the old rule); a short pool is now shared in
/// proportion to each buyer's weighted shortfall. Grain itself was made 15 % short of what the
/// world asks for; its calibration (Tools/WorldResources/calib.json) now covers it.
/// </summary>
static class MarketShortageTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("market shortage: " + label);
        Console.WriteLine("PASS market shortage: " + label);
    }

    /// <summary>One market week of a fresh 2024 world, every buyer traced: what it lacked and what it went looking for.</summary>
    static (double Short, double Sought) FirstWeek(float reference)
    {
        var c = Campaign.CreateBootstrap(20240101);
        c.Defines.ImportReachReference = reference;
        var traces = new List<TradeNetwork.BuyerTrace>();
        var gate = new object();
        c.IsPaused = false;
        TradeNetwork.BuyerTraceSink = trace => { lock (gate) traces.Add(trace); };
        try { for (var day = 0; day < 8; day++) c.TickDay(); }
        finally { TradeNetwork.BuyerTraceSink = null; }
        return (traces.Sum(t => (double)t.Shortfall), traces.Sum(t => (double)t.Sought));
    }

    public static void Run()
    {
        var (shortNow, soughtNow) = FirstWeek(0.50f);
        var (shortOld, soughtOld) = FirstWeek(1.0f);
        Check(shortNow > 0 && shortOld > 0, "the 2024 world has importers in its first week");
        Check(soughtNow / shortNow >= 0.85, $"countries go looking for what they lack ({soughtNow / shortNow:P0} of their shortfall)");
        Check(soughtOld / shortOld <= 0.65, $"the old ceiling is what starved them ({soughtOld / shortOld:P0}) - the define is the lever, so this test can fail");

        // A month on, grain is the good the world is closest to short of: the pool is shared, so
        // the small importers are not served last out of it.
        var world = Campaign.CreateBootstrap(20240101);
        world.IsPaused = false;
        for (var day = 0; day < 28; day++) world.TickDay();
        Check(CommodityCatalog.TryParse("Grain", out var grainGood), "grain is a good of the world");
        var grain = world.Nations.Where(n => n.Population > 0 && n.Market.TryGetValue(grainGood, out var q) && q.Demand > 0.01f)
            .Select(n => (n.Tag, n.Market[grainGood].Satisfaction)).ToList();
        var starving = grain.Count(g => g.Satisfaction < 0.65f);
        Check(starving <= grain.Count / 20, $"few countries get under two thirds of their grain ({starving} of {grain.Count})");
        foreach (var island in new[] { "BHS", "BRB", "ISL" })
        {
            var met = grain.FirstOrDefault(g => g.Tag == island);
            if (met.Tag is null) continue;
            Check(met.Satisfaction >= 0.85f, $"{island}, a small importer, is not served last out of a short pool ({met.Satisfaction:P0})");
        }
        var japanOil = world.Nations.First(n => n.Tag == "JPN").Market[Commodity.Oil].Satisfaction;
        Check(japanOil >= 0.95f, $"Japan, which imports its oil, gets it a month in ({japanOil:P0})");
    }
}
