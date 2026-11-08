using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

internal static class ExclaveSupplyTests
{
    internal static Campaign Rack(bool barracks = false)
    {
        const int width = 720, height = 360;
        var states = new ushort[width * height]; var owners = new ushort[states.Length];
        var pixels = new byte[states.Length * 3];
        void Paint(int left, int top, int right, int bottom, int province, ushort owner, ushort state)
        {
            for (var y = top; y < bottom; y++) for (var x = left; x < right; x++)
            { var at = y * width + x; states[at] = state; owners[at] = owner; pixels[at * 3 + 2] = (byte)province; }
        }
        Paint(70, 80, 160, 135, 1, 100, 1001); // mainland capital with its own sea exit
        Paint(406, 160, 520, 241, 4, 101, 1003); // foreign land around the exclave
        Paint(400, 160, 404, 166, 2, 100, 1002); // detached spit: the region's first depot
        Paint(400, 170, 406, 241, 5, 100, 1002); // another coast in that region, deliberately empty
        Paint(406, 176, 456, 235, 3, 100, 1002); // inland, bordered only by land provinces
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, pixels), new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 3, [4] = 4, [5] = 5 });
        var c = new Campaign(1206) { ScenarioId = "exclave_supply" };
        c.Nations.Add(new Nation { Id = 0, Tag = "EX0", Name = "Exclave holder", MapIndex = 100,
            Population = 1_000_000, Gdp = 100, Treasury = 20, MilitarySpending = .06f, StockpilesSeeded = true,
            CapitalProvinceId = 1, CapitalStateMapIndex = 1001 });
        c.Nations.Add(new Nation { Id = 1, Tag = "EX1", Name = "Neighbour", MapIndex = 101,
            Population = 1_000_000, Gdp = 100, Treasury = 20, MilitarySpending = .06f, StockpilesSeeded = true,
            CapitalProvinceId = 4, CapitalStateMapIndex = 1003 });
        for (var i = 1; i <= 3; i++) c.States.Add(new ScenarioState { Id = "exclave_" + i, Name = "Region " + i,
            MapIndex = 1000 + i, NationMapIndex = i == 3 ? 101 : 100, NationId = i == 3 ? "EX1" : "EX0",
            Population = i == 2 ? 100_000 : 300_000 });
        if (barracks) c.States[1].Buildings[BuildingKind.Barracks] = 1;
        c.AttachProvinceTopology(map); c.Armies.Clear();
        var capital = map.CenterOf(1);
        c.Nations[0].CapitalLatitude = capital.Latitude; c.Nations[0].CapitalLongitude = capital.Longitude;
        c.Armies.Add(new Army { Id = 1, NationId = 0, ProvinceId = 3, StateMapIndex = 1002,
            TemplateId = "line_infantry", Divisions = 1, Personnel = 10_000, MaxPersonnel = 10_000,
            Strength = 1, SupplyLevel = 1, Organisation = .8f });
        return c;
    }

    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("FAIL exclave: " + message); Console.WriteLine("PASS exclave: " + message); }

    internal static void Run()
    {
        foreach (var barracks in new[] { false, true })
        {
            var c = Rack(barracks); var army = c.Armies[0]; var rng = c.Random.State;
            Check(SeaLanes.TryHarbourPoint(c.ProvinceMap, 5, out _, out _) &&
                !SeaLanes.TryHarbourPoint(c.ProvinceMap, 3, out _, out _), "fixture has an empty coast and a truly inland garrison");
            LogisticsSystem.Tick(c);
            Check(!army.CutOff && army.DaysCutOff == 0 && army.SupplySourceStateMapIndex > 0 &&
                army.SupplyDistance >= 0,
                "sea entry supplies the inland exclave, including local barracks=" + barracks +
                $" cutoff={army.CutOff} source={army.SupplySourceStateMapIndex} distance={army.SupplyDistance} origin={CapitalCore.SupplyOriginProvinceId(c, c.Nations[0])}");
            Check(c.Random.State == rng, "maritime propagation is deterministic and draws no random numbers");
            c.SetProvinceController(5, 101); SetDay(c, 3); LogisticsSystem.Tick(c);
            Check(army.CutOff, "losing the sole coastal entry really cuts the inland pocket off");
            c.SetProvinceController(5, 100); SetDay(c, 6); LogisticsSystem.Tick(c);
            Check(!army.CutOff && army.DaysCutOff == 0, "recapturing the coastal entry restores the same inland route");
        }
    }

    internal static void RealKaliningrad()
    {
        var c = Campaign.CreateBootstrap(1206);
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var why)) throw new Exception(why);
        c.AttachProvinceTopology(map);
        var nation = c.NationByTag("RUS");
        var region = c.States.Find(s => s.NationMapIndex == nation.MapIndex &&
            s.Name.Contains("Kaliningrad", StringComparison.OrdinalIgnoreCase));
        Check(region is not null, "authored Kaliningrad region exists on the production map");
        Check(c.Armies.Exists(a => a.NationId == nation.Id && a.StateMapIndex == region.MapIndex),
            "real opening deployment reserves a Russian formation for Kaliningrad");
        c.Armies.Clear();
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (map.ProvinceToState[p] == region.MapIndex && !map.IsWater(p))
                c.Armies.Add(new Army { Id = p, NationId = nation.Id, ProvinceId = p, StateMapIndex = region.MapIndex,
                    TemplateId = "line_infantry", Divisions = 1, Personnel = 10_000, MaxPersonnel = 10_000,
                    Strength = 1, SupplyLevel = 1, Organisation = .8f });
        Check(c.Armies.Count > 0, "authored exclave has land provinces");
        LogisticsSystem.Tick(c);
        foreach (var army in c.Armies)
        {
            var coastal = SeaLanes.TryHarbourPoint(map, army.ProvinceId, out _, out _);
            Console.WriteLine($"KALININGRAD province={army.ProvinceId} coastal={coastal} cutoff={army.CutOff} " +
                $"source={army.SupplySourceStateMapIndex} distance={army.SupplyDistance} congestion={army.SupplyCongestion:F3} supply={army.SupplyLevel:F3}");
            Check(!army.CutOff && army.SupplySourceStateMapIndex > 0, "real Kaliningrad province retains a supply route " + army.ProvinceId);
        }
        Check(StrategicExclaves.EntryPosts(c, nation).Contains(13011) ||
            StrategicExclaves.EntryPosts(c, nation).Contains(13142) ||
            StrategicExclaves.EntryPosts(c, nation).Contains(13317), "AI identifies a substantial coastal entry in real Kaliningrad");
        foreach (var army in c.Armies) army.Divisions = 100;
        SetDay(c, 3); LogisticsSystem.Tick(c);
        Check(c.Armies.TrueForAll(a => a.SupplyCongestion < .5f), "regional fragments share capacity rather than creating unlimited depots");
        foreach (var army in c.Armies)
            if (SeaLanes.TryHarbourPoint(map, army.ProvinceId, out _, out _)) c.SetProvinceController(army.ProvinceId, 77);
        SetDay(c, 6); LogisticsSystem.Tick(c);
        Check(c.Armies.Find(a => a.ProvinceId == 13113).CutOff, "loss of all real Kaliningrad sea entrances cuts off its inland garrison");
        foreach (var army in c.Armies) { c.SetProvinceController(army.ProvinceId, (ushort)nation.MapIndex); army.Divisions = 1; }
        SetDay(c, 9); LogisticsSystem.Tick(c);
        Check(c.Armies.TrueForAll(a => !a.CutOff && a.DaysCutOff == 0), "recapture restores all real Kaliningrad fragments");
    }

    private static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, day * 24L);
}
