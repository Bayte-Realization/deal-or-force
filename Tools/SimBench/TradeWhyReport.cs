using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// `--trade-why [Good,Good...] [TAG,TAG...] [days]`: why a country goes short of a good while the
/// world has it to spare. The world is played for the given days (28 by default); then one more
/// market week is traced: for every good, the exporters' whole surplus against what entered the
/// trading pool and what was left unsold, and for every buyer the chain that decides what it
/// gets - its shortfall, the trade policy, the transport ceiling, its own import cap, what it went
/// looking for, the exporters it could reach, what it bought, what a price limit refused and what
/// the grey market added. Written because "a shortage of everything" with a world surplus of
/// almost everything is a question about that chain, not about production.
/// </summary>
internal static class TradeWhyReport
{
    public static int Run(Campaign campaign, string[] args, int at)
    {
        string Arg(int offset) => at + offset < args.Length && !args[at + offset].StartsWith("--", StringComparison.Ordinal) ? args[at + offset] : null;
        var goodsArg = Arg(1);
        var tagsArg = Arg(2);
        var days = int.TryParse(Arg(3), out var d) ? d : int.TryParse(tagsArg, out var d2) ? d2 : 28;
        if (int.TryParse(tagsArg, out _)) tagsArg = null;
        var goods = goodsArg is null || goodsArg == "all"
            ? CommodityCatalog.All.ToList()
            : goodsArg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => CommodityCatalog.TryParse(id, out var g) ? g : (Commodity?)null)
                .Where(g => g is not null).Select(g => g.Value).ToList();
        var tags = (tagsArg ?? "JPN,DEU,KOR,FRA,GBR,KAZ,NGA,BRA").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        campaign.IsPaused = false;
        for (var day = 0; day < days; day++) campaign.TickDay();

        var buyers = new Dictionary<(int, Commodity), TradeNetwork.BuyerTrace>();
        var pools = new Dictionary<Commodity, TradeNetwork.PoolTrace>();
        var gate = new object();
        TradeNetwork.BuyerTraceSink = trace => { lock (gate) buyers[(trace.NationId, trace.Good)] = trace; };
        TradeNetwork.PoolTraceSink = pool => { lock (gate) pools[pool.Good] = pool; };
        try
        {
            var routedBefore = campaign.TradeRoutesDay;
            for (var day = 0; day < 14 && (campaign.TradeRoutesDay == routedBefore || pools.Count == 0); day++) campaign.TickDay();
        }
        finally
        {
            TradeNetwork.BuyerTraceSink = null;
            TradeNetwork.PoolTraceSink = null;
        }

        Console.WriteLine($"TRADE-WHY day {campaign.ElapsedDays}: {pools.Count} goods traced, {buyers.Count} buyer-goods");
        double allShort = 0, allSought = 0, allBought = 0, allGrey = 0, allRefused = 0;
        foreach (var trace in buyers.Values)
        {
            allShort += trace.Shortfall; allSought += trace.Sought; allBought += trace.Bought;
            allGrey += trace.GreyImported; allRefused += trace.PriceRefused;
        }
        Console.WriteLine($"  every buyer, every good: shortfall {allShort:0.0}; went looking for {allSought:0.0} ({allSought / Math.Max(1e-6, allShort):P0}); " +
                          $"bought {allBought:0.0} ({allBought / Math.Max(1e-6, allShort):P0}); grey market {allGrey:0.0} ({allGrey / Math.Max(1e-6, allShort):P0}); refused on price {allRefused:0.0}");
        var factorRows = buyers.Values.Where(t => t.Shortfall > 0.01f).ToList();
        if (factorRows.Count > 0)
            Console.WriteLine($"  average factors: policy x{factorRows.Average(t => t.ImportAccess):0.00}, transport ceiling x{factorRows.Average(t => t.Ceiling):0.00}, " +
                              $"own import cap x{factorRows.Average(t => t.DeskCeiling):0.00}, blocked x{factorRows.Average(t => t.BlockedFactor):0.00}, defcon x{factorRows.Average(t => t.DefconFactor):0.00}; " +
                              $"ceiling below 0.7 for {factorRows.Count(t => t.Ceiling < 0.7f)} of {factorRows.Count}");

        foreach (var good in goods)
        {
            if (!pools.TryGetValue(good, out var pool)) { Console.WriteLine($"GOOD {CommodityCatalog.IdOf(good)}: no exporter this week"); continue; }
            var forGood = buyers.Values.Where(t => t.Good.Equals(good)).ToList();
            var shortfall = forGood.Sum(t => (double)t.Shortfall);
            Console.WriteLine($"GOOD {CommodityCatalog.IdOf(good)}: surplus of all exporters {pool.SurplusOfAllSellers:0.0}, in the pool {pool.PoolCapacity:0.0} ({pool.Sellers} sellers), " +
                              $"left unsold {pool.Unsold:0.0}; {forGood.Count} buyers short {shortfall:0.0}, looked for {forGood.Sum(t => (double)t.Sought):0.0}, " +
                              $"bought {forGood.Sum(t => (double)t.Bought):0.0}, grey {forGood.Sum(t => (double)t.GreyImported):0.0}");
            foreach (var trace in forGood.OrderByDescending(t => t.Shortfall))
            {
                var nation = campaign.NationById(trace.NationId);
                if (nation is null || !tags.Contains("ALL") && !tags.Contains(nation.Tag)) continue;
                var met = nation.Market.TryGetValue(good, out var quote) ? quote.Satisfaction : float.NaN;
                Console.WriteLine($"   {nation.Tag}: short {trace.Shortfall:0.00} x policy {trace.ImportAccess:0.00} x ceiling {trace.Ceiling:0.00} x own cap {trace.DeskCeiling:0.00} " +
                                  $"= looked for {trace.Sought:0.00} ({trace.Sought / Math.Max(1e-6f, trace.Shortfall):P0}); reachable sellers {trace.SellersReachable}/{trace.SellersInPool}; " +
                                  $"blocked x{trace.BlockedFactor:0.00} defcon x{trace.DefconFactor:0.00}; bought {trace.Bought:0.00}, refused {trace.PriceRefused:0.00}, grey {trace.GreyImported:0.00}; demand met {met:P0}");
            }
        }
        return 0;
    }
}
