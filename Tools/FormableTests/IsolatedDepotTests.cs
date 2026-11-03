using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>Local stores buy time; an isolated barracks is not an infinite national supply route.</summary>
public static class IsolatedDepotTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL isolated depot: " + text);
        Console.WriteLine("PASS isolated depot: " + text);
    }

    private static Campaign World(bool open)
    {
        const int width = 32, height = 192;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = y * width + x;
                var province = y / 32 + 1;
                states[at] = (ushort)(1000 + province);
                owners[at] = 100;
                rgb[at * 3 + 2] = (byte)province;
            }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 6).ToDictionary(p => p, p => p));
        var c = new Campaign(2401) { ScenarioId = "isolated_depot" };
        c.Nations.Add(new Nation { Id = 0, Tag = "DP0", Name = "Depot country", MapIndex = 100,
            Population = 4000000, Gdp = 400, Treasury = 50, MilitarySpending = .06f,
            StockpilesSeeded = true, CapitalProvinceId = 1, CapitalStateMapIndex = 1001 });
        c.Nations.Add(new Nation { Id = 1, Tag = "DP1", Name = "Enemy", MapIndex = 101,
            Population = 2000000, Gdp = 200, Treasury = 50 });
        for (var p = 1; p <= 6; p++)
            c.States.Add(new ScenarioState { Id = "dp_" + p, Name = "Depot region " + p,
                MapIndex = 1000 + p, NationMapIndex = 100, NationId = "DP0", Population = 100000 });
        c.States[4].Buildings[BuildingKind.Barracks] = 2;
        c.AttachProvinceTopology(map);
        // The depot rule on the bare hunger clock; pocket stores are tested in StrandedGarrisonTests.
        c.Defines.StrandedGarrisonsEnabled = false;
        c.ProvinceController[3] = (ushort)(open ? 100 : 101);
        c.Armies.Clear();
        c.Armies.Add(new Army { Id = 1, NationId = 0, ProvinceId = 6, StateMapIndex = 1006,
            TemplateId = "line_infantry", Divisions = 1, Personnel = 10000, MaxPersonnel = 10000,
            Strength = 1f, Equipment = .2f, SupplyLevel = 1f, Organisation = .8f });
        foreach (var good in StrategicStockpiles.TrackedGoods) c.Nations[0].Stockpiles[good] = 100f;
        return c;
    }

    private static void Morning(Campaign c, int day)
    {
        // Exercise the logistics clock at real day numbers, without unrelated AI/economic ticks.
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours), BindingFlags.Public | BindingFlags.Instance)
            .SetValue(c, day * 24L);
        LogisticsSystem.Tick(c);
    }

    private static Campaign SeaWorld(bool isolatedPort)
    {
        const int width = 720, height = 360;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                // A harbour grid cell must contain both land and sea on each shore.
                var p = y < 40 ? 1 : y < 80 ? 2 : y <= 120 ? 3 : y <= 240 ? 0 : y < 280 ? 4 : 5;
                if (p == 0) continue;
                var at = y * width + x;
                states[at] = (ushort)(1000 + p);
                owners[at] = (ushort)(isolatedPort && p >= 4 ? 101 : 100);
                rgb[at * 3 + 2] = (byte)p;
            }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 5).ToDictionary(p => p, p => p));
        var c = new Campaign(2402) { ScenarioId = "isolated_sea_depot" };
        c.Nations.Add(new Nation { Id = 0, Tag = "DS0", Name = "Sea depot country", MapIndex = 100,
            Population = 1000000, Gdp = 100, Treasury = 20, MilitarySpending = .06f,
            StockpilesSeeded = true, CapitalProvinceId = 1, CapitalStateMapIndex = 1001 });
        c.Nations.Add(new Nation { Id = 1, Tag = "DS1", Name = "Other shore", MapIndex = 101,
            Population = 1000000, Gdp = 100, Treasury = 20 });
        for (var p = 1; p <= 5; p++)
        {
            var other = isolatedPort && p >= 4;
            c.States.Add(new ScenarioState { Id = "ds_" + p, Name = "Sea depot region " + p,
                MapIndex = 1000 + p, NationMapIndex = other ? 101 : 100,
                NationId = other ? "DS1" : "DS0", Population = 100000 });
        }
        c.States[isolatedPort ? 2 : 4].Buildings[BuildingKind.Barracks] = 1;
        c.AttachProvinceTopology(map);
        // The depot rule on the bare hunger clock; pocket stores are tested in StrandedGarrisonTests.
        c.Defines.StrandedGarrisonsEnabled = false;
        for (var p = 1; p <= 5; p++) c.ProvinceController[p] = 100;
        if (isolatedPort) c.ProvinceController[2] = 101;
        c.Armies.Clear();
        var atProvince = isolatedPort ? 4 : 5;
        c.Armies.Add(new Army { Id = 1, NationId = 0, ProvinceId = atProvince,
            StateMapIndex = 1000 + atProvince, TemplateId = "line_infantry", Divisions = 1,
            Personnel = 10000, MaxPersonnel = 10000, Strength = 1f, Equipment = .2f,
            SupplyLevel = 1f, Organisation = .8f });
        return c;
    }

    public static void Run()
    {
        var c = World(false);
        var a = c.Armies[0];
        var random = c.Random.State;
        Morning(c, 0);
        Check(a.CutOff, "barracks behind a closed corridor do not cancel encirclement");
        Check(a.SupplySourceStateMapIndex == 1005, "the emergency depot remains identifiable");
        Check(a.DaysCutOff == 1 && a.SupplyLevel > 0f && a.Organisation > c.Defines.BattleBreakOrganisation,
            "local reserves buy time instead of vanishing on the first day");
        Check(EquipmentLogistics.DailyLimit(c, a) == 0f,
            "a full national warehouse cannot deliver equipment through the closed pocket");
        var stock = c.Nations[0].Stockpiles["Arms"];
        EquipmentLogistics.ReplenishDay(c);
        Check(a.Equipment == .2f && c.Nations[0].Stockpiles["Arms"] == stock,
            "closed-pocket delivery neither repairs kit nor debits remote stores");
        for (var day = 1; day < 7; day++) Morning(c, day);
        Check(a.DaysCutOff == 7, "three-day route refreshes do not reset the daily reserve clock");

        var hash = CampaignFingerprint.Of(c);
        var text = LogisticsSystem.DescribeSupply(c, a);
        Check(text.Contains("No path to a supply hub") && text.Contains("7"),
            "the existing supply explanation reports a lost route and elapsed days");
        Check(CampaignFingerprint.Of(c) == hash && c.Random.State == random,
            "observing and tracing a pocket draw no random number and presentation writes no state");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var restored = JsonSerializer.Deserialize<Army>(JsonSerializer.Serialize(a, options), options);
        Check(restored.CutOff && restored.DaysCutOff == 7 && restored.SupplyLevel == a.SupplyLevel,
            "existing army JSON retains the finite reserve state");
        Check(JsonSerializer.Deserialize<Army>("{\"Id\":2}", options).DaysCutOff == 0,
            "legacy armies without a reserve clock keep the zero default");

        for (var day = 7; day < c.Defines.SupplyCutoffDays; day++) Morning(c, day);
        Check(a.DaysCutOff == 14 && a.SupplyLevel == 0f && a.Organisation <= c.Defines.BattleBreakOrganisation,
            "an isolated depot runs dry at the same defined deadline as any other pocket");
        Morning(c, 15);
        Check(a.CutOff && a.SupplyLevel == 0f && a.DaysCutOff == 15,
            "another network rebuild cannot refill an exhausted emergency depot");
        Check(a.Personnel == 10000 && a.Strength == 1f && a.Equipment == .2f,
            "reserve exhaustion does not erase personnel or durable equipment");
        a.DirectOrder = true;
        a.PlanActive = true;
        a.OrderKind = ArmyOrder.AdvanceFront;
        Check(!AssaultReadiness.Assess(c, a).Ready, "even a direct attack cannot bypass exhausted reserves");

        c.ProvinceController[3] = 100;
        Morning(c, 18);
        Check(!a.CutOff && a.DaysCutOff == 0, "restoring the real corridor clears encirclement");
        Check(a.SupplyLevel > 0f && a.SupplyLevel < 1f, "deblocking restores supply gradually, not instantly");
        Check(EquipmentLogistics.DailyLimit(c, a) > 0f, "the restored route allows paid equipment delivery again");
        c.ProvinceController[3] = 101;
        Morning(c, 21);
        Check(a.CutOff && a.DaysCutOff == 1, "a genuinely resupplied grouping starts a new clock if surrounded again");

        c = World(true);
        for (var day = 0; day < 20; day++) Morning(c, day);
        Check(!c.Armies[0].CutOff && c.Armies[0].DaysCutOff == 0 && c.Armies[0].SupplyLevel > .7f,
            "a connected depot remains usable indefinitely while its route exists");

        c = World(false);
        c.Defines.SupplyCutoffDays = 7;
        c.Nations[0].IsHuman = true;
        for (var day = 0; day < 7; day++) Morning(c, day);
        Check(c.Armies[0].CutOff && c.Armies[0].DaysCutOff == 7 && c.Armies[0].SupplyLevel == 0f,
            "the configured deadline also applies to human-owned emergency depots");

        c = SeaWorld(false);
        Morning(c, 0);
        Check(!c.Armies[0].CutOff && c.Armies[0].SupplySourceStateMapIndex == 1005,
            "a real sea route connects the island hinterland and its inland depot " +
            $"(cutoff={c.Armies[0].CutOff} source={c.Armies[0].SupplySourceStateMapIndex})");
        Check(c.Armies[0].DaysCutOff == 0 && c.Armies[0].SupplyLevel > .7f,
            "finite isolated stores do not starve an island with an open supply route");
        c = SeaWorld(true);
        Morning(c, 0);
        Check(c.Armies[0].CutOff && c.Armies[0].SupplySourceStateMapIndex == -1,
            "a disconnected coastal depot cannot become a national sea-supply origin");
        Check(c.Armies[0].DaysCutOff == 1 && c.Armies[0].SupplyLevel > 0f,
            "a beachhead without a connected source keeps only its finite carried reserves");

        c = World(false);
        // One administrative region can contain both a connected province and a pocket.
        c.ProvinceMap.ProvinceToState[6] = 1002;
        c.States[1].Buildings[BuildingKind.Barracks] = 1;
        c.Armies.Add(new Army { Id = 2, NationId = 0, ProvinceId = 2, StateMapIndex = 1002,
            TemplateId = "line_infantry", Divisions = 1, SupplyLevel = 1f, Organisation = .8f });
        Morning(c, 0);
        Check(!c.Armies[1].CutOff && c.Armies[1].DaysCutOff == 0,
            "an isolated fragment does not mark the connected half of its region as cut off");
        Check(c.Armies[0].CutOff, "the disconnected fragment still cannot borrow the region's connected depot");
    }
}
