using System;
using System.Linq;
using DealOrForce.Simulation;

public static class ExposureBatchTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL exposure batch: " + label);
        Console.WriteLine("PASS exposure batch: " + label);
    }

    public static Campaign World(int seed = 1)
    {
        var c = new Campaign(seed);
        for (var id = 0; id < 12; id++) c.Nations.Add(new Nation { Id = id, MapIndex = id + 1,
            Tag = "EX" + id, Name = "Exposure " + id, Population = 100000, Gdp = 10 });
        foreach (var good in Enum.GetValues<Commodity>()) c.Nations[0].Market[good] = new MarketQuote {
            Commodity = good, Demand = 100, Imports = 70, Production = 25 };
        c.TradeRoutesDay = 0;
        return c;
    }

    public static void Run()
    {
        Check(EconomicExposure.Read(null, null).Length == 0, "null input remains an empty view");
        var random = new Random(241024);
        for (var fixture = 0; fixture < 300; fixture++)
        {
            var c = World(fixture + 1); var n = c.Nations[0];
            foreach (var quote in n.Market.Values)
            {
                quote.Demand = (float)(random.NextDouble() * 100);
                quote.Imports = (float)(random.NextDouble() * 100);
            }
            if (fixture % 7 == 0) n.Market[Commodity.Food].Demand = float.NaN;
            if (fixture % 11 == 0) n.Market[Commodity.Arms].Imports = float.PositiveInfinity;
            if (fixture % 13 == 0) n.Market[Commodity.Oil].Demand = 0;
            // Duplicate quote goods and out-of-catalogue mod goods must keep stable row order.
            if (fixture % 5 == 0) n.Market[Commodity.ConsumerGoods].Commodity = Commodity.Food;
            if (fixture % 17 == 0) n.Market[(Commodity)700] = new MarketQuote {
                Commodity = (Commodity)700, Demand = 14, Imports = 10 };
            for (var r = 0; r < 400; r++) c.TradeRoutes.Add(new TradeRoute {
                BuyerId = r % 7 == 0 ? 1 : 0, SellerId = random.Next(-1, 15),
                Commodity = (Commodity)random.Next(-2, 22), Volume = r % 29 == 0 ? float.NaN :
                    r % 31 == 0 ? float.PositiveInfinity : r % 23 == 0 ? -1 : (float)random.NextDouble() * 10 });
            if (fixture % 19 == 0) c.TradeRoutesDay = -1;
            var before = CampaignFingerprint.Of(c); var state = c.Random.State;
            var expected = ExposureReadOracle.Read(c, n); var actual = EconomicExposure.Read(c, n);
            Check(ExposureReadOracle.Equal(expected, actual), "bitwise rows, suppliers and ordering: fixture " + fixture);
            Check(CampaignFingerprint.Of(c) == before && c.Random.State == state,
                "reading attribution mutates neither world nor RNG: fixture " + fixture);
        }
        var precise = World();
        foreach (var volume in new[] { 16777216f, 1f, 1f }) precise.TradeRoutes.Add(new TradeRoute {
            BuyerId = 0, SellerId = 1, Commodity = Commodity.Food, Volume = volume });
        var first = EconomicExposure.Read(precise, precise.Nations[0]);
        Check(first.First(r => r.Good == Commodity.Food).Suppliers[0].Volume == 16777218f,
            "double accumulation preserves a supplier total that float += would round incorrectly");
        precise.TradeRoutes[0].Volume = 5;
        var second = EconomicExposure.Read(precise, precise.Nations[0]);
        Check(first.First(r => r.Good == Commodity.Food).Suppliers[0].Volume == 16777218f &&
            second.First(r => r.Good == Commodity.Food).Suppliers[0].Volume == 7,
            "returned snapshots are detached and same-day route edits are read freshly");

        var large = World();
        foreach (var good in Enum.GetValues<Commodity>())
        for (var seller = 1; seller < 12; seller++)
        for (var part = 0; part < 5; part++) large.TradeRoutes.Add(new TradeRoute {
            BuyerId = 0, SellerId = seller, Commodity = good, Volume = part + 1 });
        long Alloc(Func<EconomicExposure.Row[]> read)
        {
            for (var pass = 0; pass < 4; pass++) _ = read();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var pass = 0; pass < 20; pass++) _ = read();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        var baseline = Alloc(() => ExposureReadOracle.Read(large, large.Nations[0]));
        var candidate = Alloc(() => EconomicExposure.Read(large, large.Nations[0]));
        Check(ExposureReadOracle.Equal(ExposureReadOracle.Read(large, large.Nations[0]),
            EconomicExposure.Read(large, large.Nations[0])), "large batch preserves complete attribution");
        Check(candidate * 4 <= baseline * 3,
            $"large-batch managed allocations fall by at least 25% (before={baseline} after={candidate})");
    }
}
