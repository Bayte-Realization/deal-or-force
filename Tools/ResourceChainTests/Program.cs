using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class Program
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("FAIL " + label); Console.WriteLine("PASS " + label); }
    static bool Near(float a, float b) => Math.Abs(a - b) < .001f;
    static Commodity Good(string id)
    { if (!CommodityCatalog.TryParse(id, out var good)) throw new Exception("Missing good " + id); return good; }
    static void Clear(Nation n)
    { foreach (var q in n.Market.Values) { q.Production = q.Demand = 0; q.Satisfaction = 1; } }
    static void Main()
    {
        var ids = new[] { "Copper", "Lithium", "Nickel", "Cobalt", "Phosphates", "Silica", "Rubber", "Sulfur",
            "Steel", "Batteries", "Semiconductors", "MotorVehicles", "Ammunition", "PrecisionComponents" };
        var errors = new List<string>();
        ProductionChains.ResetCatalog(errors); ProductionProgramme.ResetCatalog(errors);
        Check(errors.Count == 0, "shipped recipes and production pools validate: " + string.Join("; ", errors));
        Check(ids.All(id => CommodityCatalog.TryParse(id, out _)), "14 strategic goods registered with stable data IDs");
        Check(ids.Take(8).All(id => ProductionChains.ResourceGoods.Contains(Good(id))) &&
            ids.Skip(8).All(id => !ProductionChains.ResourceGoods.Contains(Good(id))),
            "resource selector discovers all raw materials from recipes, excluding finished goods");
        SurveyTests();
        var copperPrice = CommodityCatalog.Get(Good("Copper")).BasePrice;
        Check(!CommodityCatalog.Override("Copper", "corrupted", null, float.PositiveInfinity, null, null) &&
            Near(CommodityCatalog.Get(Good("Copper")).BasePrice, copperPrice) && CommodityCatalog.Get(Good("Copper")).Name == "Copper",
            "non-finite goods patch cannot poison prices or partially rename a good");
        var produced = ProductionChains.GoodsProduced().ToHashSet();
        var consumed = ProductionChains.GoodsConsumed().ToHashSet();
        Check(ids.All(id => produced.Contains(Good(id)) && consumed.Contains(Good(id))), "every new good has a source and a use");
        Check(ProductionProgramme.PoolOf(Good("Ammunition")) == ProductionPool.Military &&
            ids.Skip(8).All(id => ProductionProgramme.IsLineable(Good(id))) &&
            ids.Skip(8).Where(id => id != "Ammunition").All(id => ProductionProgramme.PoolOf(Good(id)) == ProductionPool.Civilian),
            "six finished goods can be assigned to the correct factory pool");
        var c = new Campaign(96);
        var n = c.AddNation(new Nation { MapIndex = 1, Tag = "KOR", Gdp = 100, OutputScale = 1 });
        c.ConfigureSinglePlayer(n.Id);
        var s = new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "cl_an", Iso = "CL-AN", Population = 100000 };
        c.States.Add(s); c.InvalidateStateIndex();
        s.Buildings[BuildingKind.CivilianIndustry] = 2;
        s.Buildings[BuildingKind.ArmsIndustry] = 1;
        s.Buildings[BuildingKind.ElectronicsPlant] = 1;
        s.Buildings[BuildingKind.ExtractionIndustry] = 1;
        s.ResourceDeposits[Good("Copper")] = 10;
        foreach (var good in CommodityCatalog.All)
            n.Market[good] = new MarketQuote { Commodity = good, Price = CommodityCatalog.Get(good).BasePrice, Satisfaction = 1 };
        ProductionChains.RunRegion(n, s, 1);
        Check(n.Market[Good("Copper")].Production > 10 && n.Market[Good("Batteries")].Production > 0,
            "mines and existing factories actually produce the new goods");
        var full = n.Market[Good("Batteries")].Production;
        Clear(n); n.Market[Good("Lithium")].Satisfaction = 0;
        ProductionChains.RunRegion(n, s, 1);
        Check(n.Market[Good("Batteries")].Production > 0 && n.Market[Good("Batteries")].Production < full,
            "lithium shortage reduces battery output without poisoning the economy");
        Clear(n);
        new SetProductionProgrammeCommand { IssuerId = n.Id, ConvertedCivilianUnits = 0,
            Lines = new() { new() { Good = Good("MotorVehicles"), Units = 10 }, new() { Good = Good("Ammunition"), Units = 5 } } }.Apply(c);
        ProductionChains.RunRegion(n, s, 1, ProductionProgramme.Allocate(c, n));
        Check(Near(n.Market[Good("MotorVehicles")].Production, .65f) && Near(n.Market[Good("Ammunition")].Production, .75f),
            "ten civilian units and five military units make the proportional line output");
        Clear(n); ProductionChains.RunNation(n, 2, 10, 0, false);
        var peace = n.Market[Good("Ammunition")].Demand;
        Check(Near(n.Market[Good("Steel")].Demand, .90f), "construction projects demand steel");
        Clear(n); ProductionChains.RunNation(n, 2, 10, 0, true);
        Check(n.Market[Good("Ammunition")].Demand > peace * 3, "war increases army ammunition demand");
        var makes = new List<Commodity>(); var uses = new List<Commodity>();
        ProductionChains.RolesOf(BuildingKind.ExtractionIndustry, makes, uses);
        Check(makes.Contains(Good("Copper")) && makes.Contains(Good("Lithium")), "AI builder and building tooltips discover the new mining chains");
        ProductionChains.RolesOf(BuildingKind.Agriculture, makes, uses);
        Check(makes.Contains(Good("Rubber")), "rubber plantations are discoverable as agriculture output");

        var before = ProductionChains.RecipeCount;
        foreach (var bad in new[] {
            """{"steel_chain":{"good":"Steel","output":{"Smelter":"oops"}}}""",
            """{"steel_chain":{"good":"Steel","output":{"Smelter":1e99}}}""",
            """{"steel_chain":{"good":"Steel","output":{"Smelter":1},"needs":{"Coal":{}}}}""",
            """{"steel_chain":{"good":"Steel","output":{"Smelter":1},"needs":{"Coal":2}}}""",
            """{"steel_chain":{"good":"Steel","output":{"**":1}}}""",
            """{"steel_chain":{"good":"Steel","output":{"missing_factor":1}}}""",
            """{"steel_chain":{"good":"Steel","scope":"planet","output":{"Smelter":1}}}""" })
        {
            errors.Clear(); ProductionChains.Merge(bad, errors);
            Check(errors.Count > 0 && ProductionChains.RecipeCount == before, "invalid override diagnosed and previous recipe preserved");
        }
        Clear(n); n.ProductionLines.Clear(); ProductionChains.RunRegion(n, s, 1);
        Check(Near(n.Market[Good("Steel")].Production, 1), "rejected mods did not partially rewrite steel output");
        var rejected = false;
        try { ContentWorkbench.Validate("common/production/50_test.json", """{"bad":{"good":"Steel","needs":[]}}"""); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && ProductionChains.RecipeCount == before, "in-game editor rejects invalid recipes without changing live catalogues");
        ContentWorkbench.Validate("common/production/50_test.json", """{"test":{"good":"Steel","output":{"Smelter":5}}}""");
        Check(ProductionChains.RecipeCount == before, "valid editor preview is also non-mutating");
        foreach (var bad in new[] { "[]", "{\"conversion\":[]}", "{\"lines\":[]}" })
        {
            rejected = false;
            try { ContentWorkbench.Validate("common/programme/50_test.json", bad); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "in-game editor rejects malformed programme containers");
        }
        ProductionChains.Merge("""{"steel_chain":{"good":"Steel","scope":"nation","output":{"gdp":1}}}""");
        Check(ProductionChains.RecipeCount == before, "scope change replaces rather than duplicates a recipe");
        ProductionChains.ResetCatalog();

        // Same region ID as the 2024 overlay, but independent authored world data.
        var map = ProvinceTopology.Build(Enumerable.Repeat((ushort)1, 128).ToArray(),
            Enumerable.Repeat((ushort)1, 128).ToArray(), 16, 8, _ => false);
        c.AttachProvinceTopology(map);
        s.ResourceDeposits[Good("Copper")] = 2;
        new SetProductionProgrammeCommand { IssuerId = n.Id, ConvertedCivilianUnits = 0,
            Lines = new() { new() { Good = Good("Ammunition"), Units = 4 } } }.Apply(c);
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "resources"), "resources");
        Check(saved.Success && Near(saved.Campaign.States[0].ResourceDeposits[Good("Copper")], 2) &&
            saved.Campaign.Player.ProductionLines.Any(l => l.Good == Good("Ammunition") && l.Units == 4),
            "actual save roundtrip preserves dynamic resource IDs and production allocations");
        var other = SaveSystem.ImportScenarioJson(SaveSystem.ExportScenarioJson(c, "independent"), "independent", map.ProvinceCount);
        Check(Near(other.States[0].ResourceDeposits[Good("Copper")], 2) && !other.States[0].ResourceDeposits.ContainsKey(Good("Lithium")),
            "2024 deposits do not bleed into independent scenarios with the same region ID");
        foreach (var good in ids.Select(Good)) n.Market.Remove(good);
        MarketSystem.Prime(c);
        Check(ids.All(id => n.Market.ContainsKey(Good(id))), "old campaign markets acquire new goods on recalculation");

        var world = Campaign.CreateBootstrap(96);
        var path = Path.Combine(GameDataLoader.DataFolder(), "history", "states", "2024", "zz_strategic_deposits.json");
        using var deposits = JsonDocument.Parse(File.ReadAllText(path), GameDataLoader.ForgivingJson);
        var count = 0;
        foreach (var entry in deposits.RootElement.EnumerateObject())
        {
            var region = world.States.FirstOrDefault(r => string.Equals(r.Id, entry.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Iso.Replace('-', '_'), entry.Name, StringComparison.OrdinalIgnoreCase));
            Check(region != null, "deposit region resolves: " + entry.Name);
            foreach (var good in entry.Value.GetProperty("resources").EnumerateObject())
                Check(Near(region.ResourceDeposits.GetValueOrDefault(Good(good.Name)), good.Value.GetSingle()), "authored deposit reaches live region: " + good.Name);
            count++;
        }
        Check(count == 45, "45 resource overlays loaded without overwriting older region fields");
        var chile = world.States.First(r => r.Id == "cl_an");
        Check(chile.Population == 940443 && chile.ResourceDeposits[Commodity.Oil] > 0, "new deposit overlay preserves authored population and old oil deposit");
        Check(ids.All(id => world.Nations.Sum(country => country.Market[Good(id)].Production) > 0), "all 14 goods have positive global production at campaign start");
        for (var day = 0; day < 14; day++) { MarketSystem.Tick(world); }
        Check(world.Nations.All(country => country.Market.Values.All(q => float.IsFinite(q.Price) && float.IsFinite(q.Production) && float.IsFinite(q.Demand))),
            "repeated market clearing stays finite across all countries and goods");
    }

    static void SurveyTests()
    {
        var good = Good("Copper");
        var c = new Campaign(97);
        var own = c.AddNation(new Nation { MapIndex = 1, Name = "Northland", Tag = "AAA" });
        var foreign = c.AddNation(new Nation { MapIndex = 2, Name = "Southland", Tag = "BBB" });
        c.ConfigureSinglePlayer(own.Id);
        var a = new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "copper_a", Name = "Amber coast" };
        var b = new ScenarioState { MapIndex = 2, NationMapIndex = 2, Id = "copper_b", Name = "Blue mountains" };
        c.States.Add(a); c.States.Add(b);
        a.ResourceDeposits[good] = 2; b.ResourceDeposits[good] = 10;
        var survey = ResourceAtlas.Read(c, good);
        Check(Near(survey.Total, 12) && Near(survey.Maximum, 10) && survey.Regions[0].MapIndex == 2,
            "foreign public deposits are ranked with our own on a shared global scale");
        Check(ResourceAtlas.Filter(survey, "  BLUE ").Single().MapIndex == 2 &&
            ResourceAtlas.Filter(survey, "copper_a").Single().MapIndex == 1 &&
            ResourceAtlas.Filter(survey, "", own.Id).Single().MapIndex == 1 &&
            ResourceAtlas.Filter(survey, "", foreign.Id).Single().MapIndex == 2,
            "atlas supports case-insensitive names, IDs and ownership filters");
        Check(ResourceAtlas.Filter(survey, "", own.Id).Single().Intensity == survey.Regions[1].Intensity,
            "filtering does not rescale colour intensities");
        Check(Near(ResourceAtlas.Intensity(10, 10), 1) && ResourceAtlas.Intensity(2, 10) > .2f &&
            ResourceAtlas.Intensity(float.NaN, 10) == 0 && ResourceAtlas.Intensity(2, float.PositiveInfinity) == 0 &&
            ResourceAtlas.Intensity(0, 10) == 0 && Near(ResourceAtlas.Intensity(20, 10), 1),
            "logarithmic scale preserves small deposits and rejects non-finite inputs");
        a.ResourceDeposits[good] = float.NaN;
        Check(ResourceAtlas.Read(c, good).Regions.Length == 1 && survey.Regions[1].Potential == 2,
            "atlas omits corrupt potentials and previously returned snapshots stay detached");
        Check(ResourceAtlas.Read(c, Commodity.Food).Regions.Length == 0 && ResourceAtlas.FocusProvince(c, 1) == 0,
            "absent deposits and a map not yet loaded are safe");
        a.ResourceDeposits[good] = .01f;
        Check(ResourceAtlas.Read(c, good).Regions.Length == 1 && ResourceAtlas.Read(c, good, 0).Regions.Length == 2,
            "moddable minimum potential applies consistently");
        var map = ProvinceTopology.Build(Enumerable.Repeat((ushort)1, 128).ToArray(),
            Enumerable.Repeat((ushort)1, 128).ToArray(), 16, 8, _ => false);
        c.AttachProvinceTopology(map);
        var province = ResourceAtlas.FocusProvince(c, 1);
        Check(province > 0 && map.ProvinceToState[province] == 1, "region locator chooses real land");
        map.WaterByProvince[province] = true;
        Check(ResourceAtlas.FocusProvince(c, 1) != province, "region locator never targets lake provinces");
        map.WaterByProvince[province] = false;
        map.ProvinceToState[province] = 2;
        Check(ResourceAtlas.FocusProvince(c, 2) == province && ResourceAtlas.FocusProvince(c, 1) != province,
            "region locator follows live edited or split borders, not the authored raster");
    }
}
