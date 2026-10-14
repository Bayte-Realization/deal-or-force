using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Repeated routing of one frozen scenario: isolates market preparation from simulation
/// decisions, and reports the first mapped pass separately from warm repeated passes.
/// Every routing result is checked bit for bit before a performance number is accepted.
/// No quote, policy, diplomacy or ownership input is changed between repetitions.
/// </summary>
public static class TradeLatencyReport
{
    public static int Run(Campaign campaign, int repeats)
    {
        repeats = Math.Clamp(repeats, 3, 200);
        var commodities = CommodityCatalog.All.ToArray();
        var runs = new List<(double Total, double Preparation, double Worst, long Bytes)>();
        ulong expected = 0;
        for (var run = 0; run <= repeats; run++)
        {
            var total = Stopwatch.StartNew();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var stages = new List<double>();
            using (var iterator = TradeNetwork.RouteStages(campaign).GetEnumerator())
                while (true)
                {
                    var watch = Stopwatch.StartNew();
                    var more = iterator.MoveNext();
                    watch.Stop();
                    if (!more) break;
                    stages.Add(watch.Elapsed.TotalMilliseconds);
                }
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            total.Stop();
            var signature = ResultSignature(campaign);
            if (run == 0) expected = signature;
            else if (signature != expected)
            {
                Console.WriteLine($"TRADE FAIL repeat {run}: signature changed {expected:X16} -> {signature:X16}");
                return 1;
            }
            if (stages.Count != commodities.Length + 1)
            {
                Console.WriteLine($"TRADE FAIL expected {commodities.Length + 1} stages, received {stages.Count}");
                return 1;
            }
            var sample = (total.Elapsed.TotalMilliseconds, stages[0], stages.Max(), bytes);
            if (run == 0)
            {
                Console.WriteLine(FormattableString.Invariant($"TRADE cold total={sample.Item1:0.000} ms preparation={sample.Item2:0.000} ms worst={sample.Item3:0.000} ms allocated={bytes} routes={campaign.TradeRoutes.Count} signature={signature:X16}"));
                for (var stage = 1; stage < stages.Count; stage++)
                    Console.WriteLine(FormattableString.Invariant($"  commodity {CommodityCatalog.IdOf(commodities[stage - 1])}: {stages[stage]:0.000} ms"));
            }
            else runs.Add(sample);
        }
        double Median(IEnumerable<double> values)
        {
            var ordered = values.OrderBy(x => x).ToArray();
            return ordered[ordered.Length / 2];
        }
        Console.WriteLine(FormattableString.Invariant($"TRADE warm repeats={repeats} total_median={Median(runs.Select(r => r.Total)):0.000} ms preparation_median={Median(runs.Select(r => r.Preparation)):0.000} ms worst_stage_median={Median(runs.Select(r => r.Worst)):0.000} ms total_min={runs.Min(r => r.Total):0.000} ms total_max={runs.Max(r => r.Total):0.000} ms allocated_median={runs.Select(r => r.Bytes).OrderBy(x => x).ElementAt(repeats / 2)} signature={expected:X16}"));
        return 0;
    }

    // Includes all routing outputs, not just the world checksum (routes are intentionally
    // derived and not authoritative save data). Floating point bit patterns catch a
    // changed ordering or arithmetic evaluation even when displayed amounts look alike.
    private static ulong ResultSignature(Campaign campaign)
    {
        const ulong basis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = basis;
        void Int(int value) { hash ^= unchecked((uint)value); hash *= prime; }
        void Float(float value) => Int(BitConverter.SingleToInt32Bits(value));
        Int(campaign.TradeRoutesDay);
        foreach (var route in campaign.TradeRoutes)
        {
            Int(route.SellerId); Int(route.BuyerId); Int((int)route.Commodity);
            Float(route.Volume); Float(route.Value);
        }
        foreach (var nation in campaign.Nations)
        {
            Int(nation.Id); Float(nation.ImportValue); Float(nation.ExportValue);
            foreach (var quote in nation.Market.OrderBy(x => (int)x.Key))
            { Int((int)quote.Key); Float(quote.Value.Imports); Float(quote.Value.Exports); }
            foreach (var partner in nation.TradePartners.OrderBy(x => x.Key))
            { Int(partner.Key); Float(partner.Value); }
            Float(TradeNetwork.ReachOf(campaign, nation));
        }
        return hash;
    }
}
