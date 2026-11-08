using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class LargeProductionCatalogTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS large production catalogue: " + message); }
    public static void Run()
    {
        var n = new Nation();
        var s = new ScenarioState { Population = 1000000, Urbanization = .4f, StandardOfLiving = .5f };
        s.Buildings[BuildingKind.Agriculture] = 3; s.Buildings[BuildingKind.ExtractionIndustry] = 2;
        s.ResourceDeposits[Commodity.Coal] = 7;
        foreach (var good in CommodityCatalog.All) n.Market[good] = new MarketQuote { Commodity = good, Price = 1 };
        ProductionChains.RunRegion(n, s, 1);
        var baseline = n.Market.ToDictionary(pair => pair.Key, pair => (pair.Value.Production, pair.Value.Demand));
        try
        {
            for (var i = 0; i < 200; i++)
            {
                Check(BuildingCatalog.Declare("era_building_" + i, "Era building " + i, 1, 50, 0, "Mod building"), "declare building " + i);
                Check(CommodityCatalog.Override("era_good_" + i, "Era good " + i, "", 1, 0, "Mod good"), "declare good " + i);
            }
            var errors = new List<string>();
            ProductionChains.Merge("""{"large_catalog_test":{"good":"era_good_199","output":{"era_building_199":2,"raw:era_building_199":3,"deposit:era_good_199":4},"demand":{"population":1}}} """, errors);
            Check(errors.Count == 0, "recipes accept both buildings and goods beyond the former 64 spare slots");
            BuildingCatalog.TryParse("era_building_199", out var building); CommodityCatalog.TryParse("era_good_199", out var good199);
            s.Buildings[building] = 2; s.ResourceDeposits[good199] = 3;
            foreach (var quote in n.Market.Values) { quote.Production = 0; quote.Demand = 0; }
            n.Market[good199] = new MarketQuote { Commodity = good199, Price = 1 };
            ProductionChains.RunRegion(n, s, 1);
            Check(n.Market[good199].Production == 22 && n.Market[good199].Demand == 1, "staffed, raw and deposit slots stay distinct after growth");
            Check(baseline.All(p => n.Market[p.Key].Production == p.Value.Production && n.Market[p.Key].Demand == p.Value.Demand),
                "late catalogue growth preserves every already-compiled base recipe exactly");
            var makes = new List<(Commodity Good, float Amount)>(); var uses = new List<(Commodity Good, float Amount)>(); var needs = new List<Commodity>();
            ProductionChains.Marginal(n, s, building, 1, makes, uses, needs);
            Check(makes.Any(p => p.Good == good199 && p.Amount == 5), "build preview measures the same large-mod recipe as the running market");
        }
        finally { BuildingCatalog.ResetCatalog(); CommodityCatalog.ResetCatalog(); ProductionChains.ResetCatalog(); }
    }
}
