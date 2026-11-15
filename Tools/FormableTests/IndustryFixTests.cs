using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// A friend testing 0.6.6.0 as a small country from 1 January 2024: "rifles demand 40, my production is
/// 10; I built one factory of each, like in Hearts of Iron, and production is 0". Reproduced headlessly
/// (tmp_logs/industry): four separate causes, each pinned here.
///
///   1. A production line opened while its pool was empty (0 units, which is what "Open a line" sends
///      then) switched the first factory built for its good off: Kazakhstan's new arms plant made no
///      arms. A line now keeps the pool's unclaimed units as well as its own.
///   2. Twelve open sites, one worked at a time, bought construction materials for twelve: Albania asked
///      for 65 industrial materials a week instead of 6.5. Only the sites worked today buy.
///   3. The army staff checked a flat 0.18 before raising a 0.60 division, and kept nothing for the
///      builders: Albania's only factory site stopped on day 47 for the rest of the year. It now checks
///      the division's real price plus weeks of the queued construction bill, and says when it raises
///      one on a delegating player's money.
///   4. A site that waits now records why, so the lists and an alert can say so.
/// </summary>
static class IndustryFixTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("industry fixes: " + label); Console.WriteLine("PASS industry fixes: " + label); }

    public static void Run()
    {
        TheTopicsDataFilesParse();
        ALineNeverTakesFromItsOwnGood();
        AFullyConvertedPoolIsNotAPoolWithoutFactories();
        ASiteThatWaitsSaysWhy();
        TheWorldOfAlbania();
    }

    static void TheTopicsDataFilesParse()
    {
        // A comment line that lost its "//" made the loader reject the whole defines file, so the
        // reserve could not be tuned or modded and every start logged a data error.
        foreach (var file in new[] { Path.Combine("common", "defines", "93_construction_queue.json"),
                     Path.Combine("localization", "english", "zz_330_construction_queue.json"),
                     Path.Combine("localization", "russian", "zz_330_construction_queue.json") })
        {
            JsonDocument document = null;
            try { document = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), file)), GameDataLoader.ForgivingJson); }
            catch (JsonException) { }
            using (document)
                Check(document is not null && document.RootElement.ValueKind == JsonValueKind.Object, "the data file parses: " + file);
        }
        using (var defines = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), "common", "defines",
                   "93_construction_queue.json")), GameDataLoader.ForgivingJson))
            Check(defines.RootElement.TryGetProperty("ArmyStaffConstructionReserveWeeks", out var weeks) && weeks.GetSingle() == 4f,
                "the defines file carries the army staff's reserve");
    }

    static void ALineNeverTakesFromItsOwnGood()
    {
        ProductionProgramme.ResetCatalog(); ProductionChains.ResetCatalog();
        var c = new Campaign(1115);
        var n = c.AddNation(new Nation { Id = 1, MapIndex = 1, Tag = "KAZ", Name = "Test", Gdp = 100, OutputScale = 1 });
        var s = new ScenarioState { Id = "industry", MapIndex = 1, NationMapIndex = 1, Population = 0 };
        c.States.Add(s); c.InvalidateStateIndex();
        c.ConfigureSinglePlayer(n.Id);
        foreach (var good in CommodityCatalog.All)
            n.Market[good] = new MarketQuote { Commodity = good, Price = CommodityCatalog.Get(good).BasePrice, Satisfaction = 1 };
        void Lines(params ProductionLine[] lines) =>
            new SetProductionProgrammeCommand { IssuerId = n.Id, ConvertedCivilianUnits = 0, Lines = lines.ToList() }.Apply(c);
        float Made(Commodity good)
        {
            foreach (var quote in n.Market.Values) { quote.Production = 0; quote.Demand = 0; }
            ProductionChains.RunRegion(n, s, 1, ProductionProgramme.Allocate(c, n));
            return n.Market[good].Production;
        }

        // The player's order of events: open an Arms line on the first morning, while there is no arms
        // plant (0 units - all the empty pool has), then build the plant.
        Lines(new ProductionLine { Good = Commodity.Arms, Units = 0 });
        Check(n.ProductionLines.Count == 1 && n.ProductionLines[0].Units == 0, "an Arms line opened in an empty pool holds 0 units");
        s.Buildings[BuildingKind.ArmsIndustry] = 1;
        var withLine = Made(Commodity.Arms);
        Lines();
        var without = Made(Commodity.Arms);
        Check(without > 4f && MathF.Abs(withLine - without) < 0.001f,
            $"the first arms plant makes its arms with the empty line open ({withLine:0.00}) as without one ({without:0.00})");

        // A line short of the whole pool never makes its own good less than no line did, and what it
        // claims is taken from the other goods of the pool.
        Check(CommodityCatalog.TryParse("Tanks", out var tanks), "tanks are a good");
        var tanksAlone = Made(tanks);
        Lines(new ProductionLine { Good = Commodity.Arms, Units = 4 });
        Check(Made(Commodity.Arms) >= without - 0.001f, "four of ten units leave Arms at the plant's full output");
        Check(Made(tanks) < tanksAlone - 0.001f, "the four units Arms claimed are taken from Tanks");
        Lines(new ProductionLine { Good = Commodity.Arms, Units = 10 });
        Check(Made(tanks) == 0f && Made(Commodity.Arms) >= without - 0.001f, "the whole pool on Arms leaves nothing for Tanks");
    }

    static void AFullyConvertedPoolIsNotAPoolWithoutFactories()
    {
        // The Production screen calls an empty civilian pool "no factories yet, build some". A country
        // that converted every civilian unit to military has an empty pool and its factories: the
        // screen tells the two apart by RawCivilian, and each case has its own sentence.
        ProductionProgramme.ResetCatalog();
        var c = new Campaign(1117);
        var n = c.AddNation(new Nation { Id = 1, MapIndex = 1, Tag = "ALB", Name = "Test", Gdp = 15 });
        var s = new ScenarioState { Id = "converted", MapIndex = 1, NationMapIndex = 1, Population = 0 };
        c.States.Add(s); c.InvalidateStateIndex();
        c.ConfigureSinglePlayer(n.Id);
        var none = ProductionProgramme.Allocate(c, n);
        Check(none.Capacity(ProductionPool.Civilian) == 0 && none.RawCivilian == 0, "no civilian factories: an empty pool with nothing to convert back");
        s.Buildings[BuildingKind.CivilianIndustry] = 3;
        new SetProductionProgrammeCommand { IssuerId = n.Id, ConvertedCivilianUnits = 1000, Lines = new List<ProductionLine>() }.Apply(c);
        var all = ProductionProgramme.Allocate(c, n);
        Check(all.Capacity(ProductionPool.Civilian) == 0 && all.RawCivilian == 3 * ProductionProgramme.UnitsPerFactory && all.CivilianFactories == 3,
            $"every civilian unit converted: the pool is empty and the factories are there ({all.RawCivilian} units, {all.CivilianFactories} factories)");
        foreach (var key in new[] { "No factories yet. Build {0} on the Economy screen: about {1} days each here, longer behind other sites.",
                     "All {0} civilian units are converted to military. Convert some back to open a civilian line." })
        {
            var russian = Loc.TextIn("russian", key);
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", key)) && !string.IsNullOrWhiteSpace(russian) && russian != key &&
                  Loc.Placeholders(key).SetEquals(Loc.Placeholders(russian)), "an empty pool's sentence in english and russian: " + key);
        }
    }

    static void ASiteThatWaitsSaysWhy()
    {
        var c = new Campaign(1116);
        var n = c.AddNation(new Nation { Id = 1, MapIndex = 1, Tag = "ALB", Name = "Test", Gdp = 15, Treasury = 10f, ConstructionCapacity = 1f });
        c.States.Add(new ScenarioState { Id = "queue", MapIndex = 1, NationMapIndex = 1, Population = 1_000_000 });
        c.InvalidateStateIndex();
        c.ConfigureSinglePlayer(n.Id);
        for (var id = 1; id <= 3; id++)
            c.Construction.Add(new ConstructionProject { Id = id, NationId = n.Id, StateMapIndex = 1,
                Building = BuildingKind.Agriculture, Cost = 1f, DurationDays = 100 });
        Check(ConstructionCost.WorkingSites(c, n) == 1, "a small economy works one site at a time");
        ConstructionSystem.Tick(c);
        var sites = ConstructionQueue.Sites(c, n);
        Check(sites[0].Waiting == ConstructionQueue.Working && sites[1].Waiting == ConstructionQueue.Queued &&
              sites[2].Waiting == ConstructionQueue.Queued && sites[1].WaitingBehind == 1 && sites[2].WaitingBehind == 1,
            "the oldest site works and the others record that they wait behind it");
        var plan = ConstructionQueue.Schedule(c, n);
        Check(plan[0].StartDays == 0 && plan[0].FinishDays == 99 && plan[1].StartDays == 99 && plan[2].StartDays == 199 && plan[2].FinishDays == 299,
            $"the estimate plays the queue forward ({string.Join(" | ", plan.Select(e => e.StartDays + "-" + e.FinishDays))})");
        Check(!ConstructionQueue.FrozenForMoney(c, n), "a long queue is not a frozen one");

        n.Treasury = 0f;
        ConstructionSystem.Tick(c);
        Check(sites[0].Waiting == ConstructionQueue.Treasury && sites[0].StalledSinceDay == c.ElapsedDays &&
              sites[1].Waiting == ConstructionQueue.Queued && sites[1].WaitingBehind == 1,
            "an empty treasury stops the oldest site and says so; the rest wait behind it");
        Check(ConstructionQueue.FrozenForMoney(c, n) && ConstructionQueue.Schedule(c, n).All(e => !e.Funded && e.StartDays < 0),
            "every site frozen for money, and no date is promised");
        Check(CampaignAlerts.Read(c, n, Array.Empty<SituationItem>()).Any(a => a.Id == "construction_frozen"),
            "the player is alerted that all construction has stopped");
        Check(ConstructionQueue.Describe(c, sites[0]).Length > 0 && ConstructionQueue.Describe(c, sites[1]).Length > 0,
            "both states read as a sentence");

        // One more site queued while frozen has not had a construction day yet. It will wait behind the
        // unpaid site like the rest, so the freeze, the alert and the missing dates must hold until then.
        var late = new ConstructionProject { Id = 4, NationId = n.Id, StateMapIndex = 1, Building = BuildingKind.Agriculture,
            Cost = 1f, DurationDays = 100 };
        c.Construction.Add(late);
        Check(late.Waiting == "" && ConstructionQueue.FrozenForMoney(c, n) &&
              ConstructionQueue.Schedule(c, n).All(e => !e.Funded && e.StartDays < 0) &&
              CampaignAlerts.Read(c, n, Array.Empty<SituationItem>()).Any(a => a.Id == "construction_frozen"),
            "a site queued into a frozen queue does not lift the freeze before its first construction day");
        Check(ConstructionQueue.Describe(c, late) == Loc.Format("Waiting: the treasury cannot pay {0} ahead of it",
                  Loc.Get(BuildingCatalog.Get(sites[0].Building).Name)),
            "and it says it waits behind the unpaid site, not that it starts tomorrow");
        Check(ConstructionQueue.Describe(new Campaign(1118), new ConstructionProject { Id = 1 }) == Loc.Get("Starts on the next construction day"),
            "a site not yet tried with nothing unpaid ahead starts on the next construction day");
        c.Construction.Remove(late);

        n.Treasury = 10f;
        ConstructionSystem.Tick(c);
        Check(sites[0].Waiting == ConstructionQueue.Working && sites[0].StalledSinceDay == -1 && !ConstructionQueue.FrozenForMoney(c, n),
            "money back, the site works again and the alert goes");
    }

    static void TheWorldOfAlbania()
    {
        // The map is cut against the region layer the world was dealt from, so the world comes first.
        ProvinceTopology map = null;
        (Campaign C, Nation N, ScenarioState Main) Albania()
        {
            var c = Campaign.CreateBootstrap(20240101);
            if (map is null)
            {
                MapRasterCatalog.ResetCatalog();
                Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                    WorldMapSource.Width, WorldMapSource.Height, out map, out var mapError), "the world map loads: " + mapError);
            }
            c.AttachProvinceTopology(map.CloneForOverrides());
            var n = c.Nations.First(x => x.Tag == "ALB");
            c.ConfigureSinglePlayer(n.Id);
            c.Defines.HoursPerTick = 24;
            c.IsPaused = false;
            return (c, n, c.StatesOf(n).OrderByDescending(s => s.Population).First());
        }
        void Queue(Campaign c, Nation n, ScenarioState where, BuildingKind kind)
        {
            c.Commands.Enqueue(new QueueConstructionCommand { IssuerId = n.Id, StateMapIndex = where.MapIndex, Building = kind });
            c.Commands.ApplyAll(c);
        }

        // Twelve open sites, one worked: materials for one. 64.97 a week before, 6.55 with a single site.
        var (world, albania, shkoder) = Albania();
        for (var i = 0; i < 12; i++) Queue(world, albania, shkoder, BuildingKind.Agriculture);
        Check(world.Construction.Count(p => p.NationId == albania.Id) == 12, "twelve sites open");
        for (var day = 0; day < 14; day++) world.Tick();
        var wanted = albania.Market[Commodity.IndustrialMaterials].Demand;
        Check(wanted < 12f, $"twelve open sites, one worked, buy materials for one ({wanted:0.0} a week)");
        // The staff raised a division on the player's money in these two weeks (day 11): it says so.
        var raised = world.Chronicle.Entries.Where(e => e.Kind == "army_raised_by_staff" && e.ActorId == albania.Id).ToArray();
        Check(raised.Length > 0 && raised.All(e => e.Subject.Length > 0),
            $"a division the staff raises on the player's money is in the chronicle, with its price ({raised.Length})");

        // A treasury the staff would have emptied: 0.75 with a 0.60 division due on day 11 and an arms
        // plant being built. The staff now keeps four weeks of the site's bill and leaves the division.
        var (lean, leanAlbania, leanMain) = Albania();
        leanAlbania.Treasury = 0.75f;
        Queue(lean, leanAlbania, leanMain, BuildingKind.ArmsIndustry);
        var seen = 0;
        for (var day = 0; day < 30; day++)
        {
            var reserve = ConstructionQueue.Bill(lean, leanAlbania, lean.Defines.ArmyStaffConstructionReserveWeeks);
            lean.Tick();
            var now = lean.Chronicle.Entries.Count(e => e.Kind == "army_raised_by_staff" && e.ActorId == leanAlbania.Id);
            if (now > seen)
                Check(leanAlbania.Treasury >= reserve * 0.9f,
                    $"day {lean.ElapsedDays}: the staff leaves the builders {reserve:0.000} ({leanAlbania.Treasury:0.000} left)");
            seen = now;
        }
        var site = lean.Construction.FirstOrDefault(p => p.NationId == leanAlbania.Id);
        Check(site is not null && site.Waiting == ConstructionQueue.Working && site.ProgressDays >= 29f,
            $"the arms plant is still being built on day 30 ({site?.Waiting} {site?.ProgressDays:0}/{site?.DurationDays}, treasury {leanAlbania.Treasury:0.000})");
    }
}
