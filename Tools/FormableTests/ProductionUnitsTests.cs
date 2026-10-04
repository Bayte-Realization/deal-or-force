using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class ProductionUnitsTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS production units: " + label); }
    private static bool Near(float a, float b) => Math.Abs(a - b) < .001f;
    public static void Run()
    {
        ProductionProgramme.ResetCatalog(); ProductionChains.ResetCatalog();
        var c = new Campaign(93);
        var n = c.AddNation(new Nation { Id = 1, MapIndex = 1, Tag = "KOR", Name = "Test", Gdp = 100, OutputScale = 1 });
        var s = new ScenarioState { Id = "test", MapIndex = 1, NationMapIndex = 1, Population = 1000 };
        var second = new ScenarioState { Id = "test2", MapIndex = 2, NationMapIndex = 1, Population = 3000 };
        c.States.Add(s); c.States.Add(second); c.InvalidateStateIndex();
        c.ConfigureSinglePlayer(n.Id);
        s.Buildings[BuildingKind.CivilianIndustry] = 2;
        s.Buildings[BuildingKind.ArmsIndustry] = 1;
        ProductionAllocation A() => ProductionProgramme.Allocate(c, n);
        void Set(params ProductionLine[] lines) => new SetProductionProgrammeCommand
        { IssuerId = n.Id, ConvertedCivilianUnits = 0, Lines = lines.ToList() }.Apply(c);
        Check(A().Civilian == 20 && A().Military == 10, "two independent pools, ten units per factory");
        Set(new() { Good = Commodity.ConsumerGoods, Units = 5 }, new() { Good = Commodity.Machinery, Units = 10 },
            new() { Good = Commodity.Arms, Units = 4 });
        Check(A().CivilianUsed == 15 && A().MilitaryUsed == 4 && A().Free(ProductionPool.Civilian) == 5,
            "civilian and military allocations consume only their own pool");
        foreach (var good in CommodityCatalog.All)
            n.Market[good] = new MarketQuote { Commodity = good, Price = CommodityCatalog.Get(good).BasePrice, Satisfaction = 1 };
        void Produce()
        {
            foreach (var quote in n.Market.Values) { quote.Production = 0; quote.Demand = 0; }
            ProductionChains.RunRegion(n, s, 1, A());
        }
        // Suppress the population component to measure only assigned factory output.
        s.Population = 0;
        Produce();
        Check(Near(n.Market[Commodity.ConsumerGoods].Production, 3), "five units make exactly half a civilian factory's output");
        Check(Near(n.Market[Commodity.Arms].Production, 2.04f), "military line scales arms factories separately from civilian workshops");
        Check(Near(n.Market[Commodity.Coal].Demand, 1.04f), "coal input demand is not incorrectly scaled as a coal production line");
        s.Buildings[BuildingKind.CivilianIndustry] = 3; Produce();
        Check(A().Assigned(Commodity.ConsumerGoods) == 5 && Near(n.Market[Commodity.ConsumerGoods].Production, 3),
            "new factories add free capacity, not hidden assignments or extra output to existing lines");
        s.Buildings[BuildingKind.CivilianIndustry] = 1;
        Check(A().Assigned(Commodity.ConsumerGoods) == 5 && A().Assigned(Commodity.Machinery) == 5 && n.ProductionLines[1].Units == 10,
            "factory loss suspends lower-priority capacity without destroying saved requests");
        s.Buildings[BuildingKind.CivilianIndustry] = 2;
        Check(A().Assigned(Commodity.Machinery) == 10, "rebuilding restores suspended units");
        Set(new ProductionLine { Good = Commodity.ConsumerGoods, Units = 0 }); Produce();
        Check(n.ProductionLines.Count == 1 && n.Market[Commodity.ConsumerGoods].Production == 0 && A().CivilianUsed == 0,
            "zero-unit lines persist and suppress their factory output");
        Set(); Produce();
        Check(Near(n.Market[Commodity.ConsumerGoods].Production, 12), "closing a line returns factories to automatic economy");
        Set(new() { Good = Commodity.ConsumerGoods, Units = int.MaxValue }, new() { Good = Commodity.ConsumerGoods, Units = 2 },
            new() { Good = Commodity.Machinery, Units = -20 }, new() { Good = (Commodity)999999, Units = 100 },
            new() { Good = Commodity.Arms, Units = int.MaxValue });
        Check(n.ProductionLines.Count == 3 && A().CivilianUsed == 20 && A().MilitaryUsed == 10 && n.ProductionLines[1].Units == 0,
            "commands clamp overflow and negatives, reject unknown goods and deduplicate lines");
        new SetProductionProgrammeCommand { IssuerId = n.Id, ConvertedCivilianUnits = 10, Lines = new() }.Apply(c);
        Check(A().Civilian == 10 && A().Military == 13 && A().ConvertedMilitary == 3,
            "conversion consumes civilian units and rounds military yield down");
        s.Buildings[BuildingKind.CivilianIndustry] = 100; n.ConvertedCivilianUnits = 1000;
        Check(A().ConvertedMilitary == 350, "decimal conversion does not lose units to floating-point rounding");
        s.Buildings[BuildingKind.CivilianIndustry] = 2;
        n.ConvertedCivilianUnits = -1; n.ArmsFocus = .5f;
        n.ProductionLines = new() { new() { Good = Commodity.ConsumerGoods, Share = .5f } };
        ProductionProgramme.MigrateLegacy(n, A());
        Check(n.ConvertedCivilianUnits == 10 && n.ProductionLines[0].Units == 5, "legacy shares migrate into whole units once");
        s.Buildings[BuildingKind.CivilianIndustry] = 3;
        Check(A().Converted == 10 && A().Assigned(Commodity.ConsumerGoods) == 5, "legacy migration does not reapply percentages after expansion");
        n.ArmsFocus = float.NaN; n.ConvertedCivilianUnits = -1;
        n.ProductionLines[0].Units = -1; n.ProductionLines[0].Share = float.PositiveInfinity;
        Check(A().Converted == 0 && A().CivilianUsed == 0, "non-finite legacy fields cannot create capacity");
        var order = new SetProductionProgrammeCommand { IssuerId = n.Id, ConvertedCivilianUnits = 3,
            Lines = new() { new() { Good = Commodity.Arms, Units = 7 }, new() { Good = Commodity.Machinery, Units = 8 } } };
        Check(NetworkCommandCodec.TryEncode(order, out var type, out var json) &&
            NetworkCommandCodec.TryDecode(type, json, out var decoded) && decoded is SetProductionProgrammeCommand,
            "whole-unit order has a whitelisted network representation");
        NetworkCommandCodec.TryDecode(type, json, out var roundtrip); roundtrip.Apply(c);
        Check(n.ConvertedCivilianUnits == 3 && A().MilitaryUsed == 7 && A().CivilianUsed == 8, "network replay applies the real programme");
        s.Population = 1000; s.RegionalOutputValue = second.RegionalOutputValue = -1;
        Check(Near(RegionalStatistics.EstimatedGdp(c, s), 25), "regional GDP falls back to public population weights for old saves");
        s.RegionalOutputValue = 60; second.RegionalOutputValue = 40;
        Check(Near(RegionalStatistics.EstimatedGdp(c, s), 60) && Near(c.States.Sum(x => RegionalStatistics.EstimatedGdp(c, x)), n.Gdp),
            "regional production weights partition national GDP without inventing extra GDP");
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)1, 128).ToArray(),
            Enumerable.Repeat((ushort)1, 128).ToArray(), 16, 8, _ => false));
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "production"), "production");
        Check(saved.Success && saved.Campaign.Player.ConvertedCivilianUnits == 3 &&
            saved.Campaign.Player.ProductionLines.Select(l => l.Units).SequenceEqual(new[] { 7, 8 }) &&
            Near(saved.Campaign.States[0].RegionalOutputValue, 60), "campaign save roundtrip preserves allocations, order and regional statistics");
        var errors = new List<string>();
        using (var malformed = JsonDocument.Parse("""{"units_per_factory":"bad","conversion":{"efficiency":{}},"lines":{"Arms":{"pool":"magic"}}}"""))
            ProductionProgramme.Validate(malformed.RootElement, errors);
        Check(errors.Count == 3 && ProductionProgramme.UnitsPerFactory == 10 && ProductionProgramme.PoolOf(Commodity.Arms) == ProductionPool.Military,
            "malformed mod data is diagnosed without changing the live catalogue");
        ProductionProgramme.Merge("""{"units_per_factory":12,"lines":{"Machinery":{"pool":"military"}}}""");
        Check(ProductionProgramme.UnitsPerFactory == 12 && ProductionProgramme.PoolOf(Commodity.Machinery) == ProductionPool.Military,
            "mods can redefine units per factory and line pools");
        ProductionProgramme.Merge("""{"conversion":{"efficiency":0.5},"lines":{"Steel":{"pool":"military"}}}""");
        CommodityCatalog.TryParse("Steel", out var steel);
        Check(ProductionProgramme.UnitsPerFactory == 12 && Near(ProductionProgramme.ConversionEfficiency, .5f) &&
            ProductionProgramme.PoolOf(Commodity.Machinery) == ProductionPool.Military &&
            ProductionProgramme.PoolOf(steel) == ProductionPool.Military,
            "later partial programmes inherit units and unrelated line pools");
        CommodityCatalog.Override("TestProgrammeGood", "Test programme good", "TP", .01f, .01f, "test");
        CommodityCatalog.TryParse("TestProgrammeGood", out var customGood);
        ProductionProgramme.Merge("""{"lines":{"TestProgrammeGood":{"pool":"civilian"}}}""");
        ProductionProgramme.Merge("""{"lines":{"TestProgrammeGood":{}}}""");
        Check(ProductionProgramme.IsLineable(customGood) && ProductionProgramme.Lineable.Count(x => x == customGood) == 1,
            "mod-added good gains a stable line without duplicate entries on later layers");
        errors.Clear();
        ProductionProgramme.Merge("""{"units_per_factory":0,"conversion":{"efficiency":2},"lines":{"Steel":{"pool":"unknown"},"MissingProgrammeGood":{}}}""", errors);
        Check(errors.Count == 4 && ProductionProgramme.UnitsPerFactory == 12 && Near(ProductionProgramme.ConversionEfficiency, .5f) &&
            ProductionProgramme.PoolOf(steel) == ProductionPool.Military,
            "invalid overlay fields preserve effective settings and report every problem");
        CommodityCatalog.ResetCatalog();
        ProductionProgramme.ResetCatalog();
        Check(ProductionProgramme.UnitsPerFactory == 10 && Near(ProductionProgramme.ConversionEfficiency, .35f) &&
            ProductionProgramme.PoolOf(Commodity.Machinery) == ProductionPool.Civilian && !ProductionProgramme.IsLineable(customGood),
            "next campaign clears programme settings and custom lines from disabled mods");
    }
}
