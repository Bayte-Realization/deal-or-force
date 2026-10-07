using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Land that belongs to nobody, and settling it (item 60): Antarctica opens unclaimed, the
/// map agrees, and a country that can reach it may make a colony of it.
/// </summary>
static class ColonisationTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20260923);
        c.AttachProvinceTopology(map);
        var aus = c.NationByTag("AUS");
        var arg = c.NationByTag("ARG");
        var mng = c.NationByTag("MNG");
        var ata = c.NationByTag("ATA");
        c.ConfigureSinglePlayer(aus.Id);
        Check(c.Nations.All(n => n.MapIndex > 0), "every country has a place on the map, so 0 can mean nobody");

        // --- A: nobody's, on the record and on the ground ---------------------------------
        var ice = c.States.First(s => s.Id == "aq_x01");
        var small = c.States.First(s => s.Id == "aq_x02");
        foreach (var region in new[] { ice, small })
        {
            var provinces = PeaceConference.ProvincesOfState(map, region.MapIndex);
            Check(ColonisationCore.IsUnclaimed(region) && region.NationMapIndex == 0 && region.Population == 0,
                $"{region.Id} opens belonging to nobody, with nobody living there");
            Check(provinces.Count > 0 && provinces.All(p => map.ProvinceToNation[p] == 0 && c.ProvinceController[p] == 0),
                $"every one of the {provinces.Count:N0} provinces of {region.Id} is unowned on the ground as well");
        }
        Check(ata is null || (c.StatesOf(ata).Length == 0 && ata.Population == 0),
            "the country the map called Antarctica has no land and nobody left");

        // --- B: who can get there ---------------------------------------------------------
        Check(ColonisationCore.Reaches(c, aus, small) && ColonisationCore.Reaches(c, arg, small),
            "a country with a coast can sail to coastal land nobody owns");
        Check(!ColonisationCore.Reaches(c, mng, small) && ColonisationCore.Refusal(c, mng, small).Length > 0,
            "a landlocked country with no border there cannot");
        var owned = c.StatesOf(aus).First();
        Check(ColonisationCore.Refusal(c, aus, owned).Length > 0, "a region somebody owns cannot be settled");

        // --- C: what it costs ----------------------------------------------------------------
        var count = PeaceConference.ProvincesOfState(map, small.MapIndex).Count;
        var cost = ColonisationCore.Cost(c, small);
        var days = ColonisationCore.Days(c, small);
        Console.WriteLine($"  {small.Id}: {count} provinces, {cost:0.00} and {days} days; {ice.Id}: " +
                          $"{ColonisationCore.Cost(c, ice):0.00} and {ColonisationCore.Days(c, ice)} days");
        Check(ColonisationCore.Cost(c, ice) > cost && ColonisationCore.Days(c, ice) > days,
            "a bigger colony costs more and takes longer");
        foreach (var region in new[] { small, ice })
        {
            var able = c.Nations.Where(n => !n.IsHuman && n.Population > 0 &&
                n.Treasury >= ColonisationCore.Cost(c, region) * c.Defines.AiColonisationTreasuryMultiple &&
                ColonisationCore.Reaches(c, n, region)).Select(n => n.Tag).ToList();
            Console.WriteLine($"  {able.Count} countries nobody plays could afford {region.Id} on the first day: " +
                              string.Join(" ", able.Take(24)));
        }
        aus.Treasury = cost * 0.5f;
        Check(ColonisationCore.Refusal(c, aus, small).Length > 0, "a treasury that cannot pay is refused");
        aus.Treasury = 1_000f;

        // --- F: the lands, and the claims on them ------------------------------------------
        // Antarctica was one region, so whoever settled first had a continent. It is its lands
        // now, and the seven claims the Treaty froze are worth something again.
        var lands = c.States.Where(s => s.Id.StartsWith("aq_", StringComparison.Ordinal)).ToList();
        Check(lands.Count >= 15 && lands.All(ColonisationCore.IsUnclaimed) &&
              lands.All(s => PeaceConference.ProvincesOfState(map, s.MapIndex).Count > 0),
            $"Antarctica is {lands.Count} lands, each nobody's and each with ground of its own");
        // And the borders between them are the ground's, not ruled: no stretch of one runs down a
        // meridian or along a parallel for long - the spokes and rings a cell-a-step growth drew
        // around the Pole before the partition was measured on the ground (TerritorialTopology).
        var isLand = new System.Collections.Generic.HashSet<int>(lands.Select(s => s.MapIndex));
        int RegionAt(int x, int y)
        {
            var province = map.CellToProvince[y * map.Width + x];
            return province == 0 ? 0 : map.ProvinceToState[province];
        }
        var downMeridian = 0;
        var alongParallel = 0f;
        var runs = new int[map.Width];
        for (var y = 0; y < map.Height; y++)
        {
            var latitude = 90f - (y + 0.5f) * 180f / map.Height;
            if (latitude > -60f) continue;
            var across = MathF.Cos(latitude * MathF.PI / 180f);
            var along = 0;
            for (var x = 0; x < map.Width; x++)
            {
                var here = RegionAt(x, y);
                var east = RegionAt((x + 1) % map.Width, y);
                runs[x] = isLand.Contains(here) && isLand.Contains(east) && here != east ? runs[x] + 1 : 0;
                downMeridian = Math.Max(downMeridian, runs[x]);
                var south = y + 1 < map.Height ? RegionAt(x, y + 1) : 0;
                along = isLand.Contains(here) && isLand.Contains(south) && here != south ? along + 1 : 0;
                alongParallel = MathF.Max(alongParallel, along * across);
            }
        }
        Check(downMeridian <= 40 && alongParallel <= 48f,
            $"no border between them runs down a meridian or along a parallel for long (at most {downMeridian} cells " +
            $"down one and {alongParallel:0} along one)");
        var nor = c.NationByTag("NOR");
        var rus = c.NationByTag("RUS");
        var maud = lands.First(s => s.Id == "aq_qml");
        var byrd = lands.First(s => s.Id == "aq_mbl");
        Check(ColonisationCore.Claims(maud, nor) && !ColonisationCore.Claims(maud, rus) && byrd.Claims.Count == 0,
            "Norway claims Queen Maud Land, Russia does not, and Marie Byrd Land is nobody's claim");
        Check(ColonisationCore.CostFor(c, nor, maud) < ColonisationCore.CostFor(c, rus, maud) &&
              ColonisationCore.DaysFor(c, nor, maud) < ColonisationCore.DaysFor(c, rus, maud),
            "a claimant settles its own claim for less, and sooner");
        var norway = CampaignDiplomacy.Get(c, nor.Id, rus.Id);
        var fondness = norway.Opinion;
        var chance = c.Defines.AiColonisationChancePerMonth;
        c.Defines.AiColonisationChancePerMonth = 0f;
        rus.Treasury = Math.Max(rus.Treasury, 1_000f);
        Check(ColonisationCore.Start(c, rus, maud) && norway.Reasons.Any(r => r.Id == "settling_our_claim"),
            "Norway remembers who is settling its claim");
        var voyage = ColonisationCore.DaysFor(c, rus, maud);
        for (var day = 0; day <= voyage; day++) ColonisationCore.Tick(c);
        Check(maud.NationMapIndex == rus.MapIndex && norway.Reasons.Any(r => r.Id == "took_our_claim") &&
              norway.Opinion < fondness,
            $"Russia's flag goes up over Queen Maud Land after {voyage} days, and Norway does not forget it " +
            $"({fondness:0} -> {norway.Opinion:0})");
        c.Defines.AiColonisationChancePerMonth = chance;

        // --- D: settling ---------------------------------------------------------------------
        var order = new ColoniseCommand { IssuerId = aus.Id, StateMapIndex = small.MapIndex };
        order.Apply(c);
        Check(order.Started && small.ColonistMapIndex == aus.MapIndex && MathF.Abs(aus.Treasury - (1_000f - cost)) < 0.001f,
            "the settlers set out, and the treasury pays for them");
        Check(ColonisationCore.Refusal(c, arg, small).Length > 0, "one settler at a time holds the field");
        Check(ColonisationCore.Refusal(c, aus, ice).Length > 0, "and one colony at a time per country");
        new AbandonColonyCommand { IssuerId = arg.Id, StateMapIndex = small.MapIndex }.Apply(c);
        Check(small.ColonistMapIndex == aus.MapIndex, "nobody else can call our settlers home");
        new AbandonColonyCommand { IssuerId = aus.Id, StateMapIndex = small.MapIndex }.Apply(c);
        Check(small.ColonistMapIndex == 0 && small.ColonisationProgress == 0f, "we can");
        order = new ColoniseCommand { IssuerId = aus.Id, StateMapIndex = small.MapIndex };
        order.Apply(c);
        Check(order.Started, "and send them again");
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "colony-tests"), "colony-tests");
        var restoredSmall = restored.Campaign?.States.First(s => s.MapIndex == small.MapIndex);
        var restoredIce = restored.Campaign?.States.First(s => s.MapIndex == ice.MapIndex);
        Check(restored.Success && restoredSmall.ColonistMapIndex == aus.MapIndex && restoredSmall.Unclaimed &&
              restoredIce.Unclaimed && restoredIce.NationMapIndex == 0,
            "a save keeps what nobody owns and who is on the way there");

        // Nobody else sets out while the test counts the days.
        c.Defines.AiColonisationChancePerMonth = 0f;
        var source = c.StatesOf(aus).OrderByDescending(s => s.Population).First();
        var sourceBefore = source.Population;
        var randomBefore = c.Random.State;
        for (var day = 0; day < days - 1; day++) ColonisationCore.Tick(c);
        Check(ColonisationCore.IsUnclaimed(small) && small.ColonisationProgress is > 0.95f and < 1f,
            "a day short, it is still nobody's");
        ColonisationCore.Tick(c);
        var colony = PeaceConference.ProvincesOfState(map, small.MapIndex);
        Check(!ColonisationCore.IsUnclaimed(small) && small.NationMapIndex == aus.MapIndex && small.NationId == aus.Tag &&
              small.ColonistMapIndex == 0, "on the day it is due the colony is founded");
        Check(colony.All(p => map.ProvinceToNation[p] == aus.MapIndex && c.ProvinceController[p] == aus.MapIndex),
            "and the ground under it is ours");
        Check(c.StatesOf(aus).Contains(small), "the country's region list knows it");
        var settlers = small.Population;
        Check(settlers > 0 && settlers <= c.Defines.ColonistsOnFounding && source.Population == sourceBefore - settlers,
            $"{settlers:N0} settlers live there, and {source.Name} has that many fewer");
        Check(small.PopulationGroups.Sum(g => g.Size) == settlers, "the settlers are a people of the country, counted once");

        // --- E: the countries nobody plays -------------------------------------------------
        c.Defines.AiColonisationChancePerMonth = 1f;
        foreach (var nation in c.Nations) nation.Treasury = Math.Max(nation.Treasury, 10_000f);
        // Under the Antarctic Treaty they do not go at all, however rich and however able.
        for (var day = 0; day < 30; day++) ColonisationCore.Tick(c);
        Check(lands.All(s => ColonisationCore.Colonist(c, s) is null or { IsHuman: true }),
            "under the Antarctic Treaty nobody the computer plays sets out for the ice");
        // With the treaty a mod has torn up, they do.
        c.Defines.AiColonisesTreatyLand = true;
        for (var day = 0; day < 30; day++) ColonisationCore.Tick(c);
        // Antarctica is its lands, and a country goes for the cheapest it can reach - its own
        // claim cheapest of all - so which land is settled first is the AI's to choose.
        var bound = lands.Where(s => ColonisationCore.Colonist(c, s) is { IsHuman: false }).ToList();
        var goers = bound.Select(s => ColonisationCore.Colonist(c, s)).ToList();
        Console.WriteLine("  set out: " + string.Join(", ", bound.Select(s =>
            $"{ColonisationCore.Colonist(c, s).Tag}->{s.Name}{(ColonisationCore.Claims(s, ColonisationCore.Colonist(c, s)) ? " (its claim)" : "")}")));
        Check(bound.Count > 0 && bound.All(s => ColonisationCore.Reaches(c, ColonisationCore.Colonist(c, s), s)),
            $"countries nobody plays set out for the ice when they can ({bound.Count} of {lands.Count} lands)");
        Check(goers.All(settler => c.Wars.Where(w => !w.Ended).All(w => !w.Involves(settler.Id))), "and only ones at peace");

        // The campaign's random stream is not touched: whether a country settles comes from a
        // hash, so a world does not change because somebody considered Antarctica.
        Check(c.Random.State == randomBefore, "settling draws nothing from the campaign's random stream");
    }
}
