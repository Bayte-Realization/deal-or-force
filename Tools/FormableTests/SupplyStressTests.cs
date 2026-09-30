using System;
using System.Linq;
using DealOrForce.Simulation;

static class SupplyStressTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS supply stress " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(223);
        var n = c.NationByTag("KOR"); var supplier = c.NationByTag("JPN");
        var third = c.NationByTag("CHN");
        n.Market.Clear(); c.TradeRoutes.Clear(); c.TradeRoutesDay = 0;
        var q = new MarketQuote { Commodity = Commodity.Food, Demand = 100, Production = 20, Imports = 70, Satisfaction = .9f };
        n.Market[Commodity.Food] = q;
        c.TradeRoutes.Add(new() { BuyerId = n.Id, SellerId = supplier.Id, Commodity = Commodity.Food, Volume = 30 });
        c.TradeRoutes.Add(new() { BuyerId = n.Id, SellerId = supplier.Id, Commodity = Commodity.Food, Volume = 20 });
        c.TradeRoutes.Add(new() { BuyerId = n.Id, SellerId = third.Id, Commodity = Commodity.Food, Volume = 10 });
        c.TradeRoutes.Add(new() { BuyerId = supplier.Id, SellerId = n.Id, Commodity = Commodity.Food, Volume = 100 });
        var before = CampaignFingerprint.Of(c);
        var row = SupplyStressTest.Read(c, n, supplier.Id).Rows.Single();
        Check(row.RemovedImports == 50 && row.CurrentSupply == .9 && row.RemainingSupply == .4 && row.AddedShortfall == 50,
            "aggregates named imports, preserves other and unattributed supply, ignores exports");
        Check(CampaignFingerprint.Of(c) == before && c.TradeRoutes[0].Volume == 30 && q.Imports == 70,
            "inspection does not mutate market, diplomacy or campaign");
        q.Production = 150;
        row = SupplyStressTest.Read(c, n, supplier.Id).Rows.Single();
        Check(row.CurrentSupply == 1 && row.RemainingSupply == 1 && row.AddedShortfall == 0, "surplus production absorbs lost imports");
        q.Production = 60;
        row = SupplyStressTest.Read(c, n, supplier.Id).Rows.Single();
        Check(row.CurrentSupply == 1 && row.RemainingSupply == .8 && Math.Abs(row.AddedShortfall - 20) < .0001,
            "partial production buffer is not lost by subtracting from capped satisfaction");
        c.TradeRoutes[0].Volume = 1;
        Check(row.RemovedImports == 50, "result detached from pooled routes");
        c.TradeRoutesDay = -1;
        Check(!SupplyStressTest.Read(c, n, supplier.Id).Available, "load without route attribution cannot invent stress estimates");
        c.TradeRoutesDay = 1;
        Check(!SupplyStressTest.Read(c, n, supplier.Id).Available, "future route snapshot rejected");
        c.TradeRoutesDay = 0;
        Check(!SupplyStressTest.Read(c, n, n.Id).Available && !SupplyStressTest.Read(c, n, int.MaxValue).Available,
            "self and missing supplier rejected");
        c.TradeRoutes[0].Volume = 100;
        Check(SupplyStressTest.Read(c, n, supplier.Id) is { ExcludedGoods: 1, Rows.Length: 0 }, "over-attributed imports omitted with warning count");
        c.TradeRoutes[0].Volume = float.NaN;
        Check(SupplyStressTest.Read(c, n, supplier.Id).ExcludedGoods == 1, "non-finite route data rejected");
        c.TradeRoutes[0].Volume = 30;
        q.Demand = 0;
        Check(SupplyStressTest.Read(c, n, supplier.Id).ExcludedGoods == 1, "zero demand cannot divide by zero");
        q.Demand = 100; q.Production = float.PositiveInfinity;
        Check(SupplyStressTest.Read(c, n, supplier.Id).ExcludedGoods == 1, "non-finite production rejected");
        q.Production = 20;
        var noRoutes = SupplyStressTest.Read(c, supplier, third.Id);
        Check(noRoutes.Available && noRoutes.Rows.Length == 0, "empty partner link does not fabricate imports");
    }
}
