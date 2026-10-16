using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class MobilisationTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL mobilisation: " + label);
        Console.WriteLine("PASS mobilisation: " + label);
    }

    public static void Run()
    {
        ShortFrontRaisesABatch();
        PeaceStandsItsArmyDownSooner();
        QueueCapDoublesInWar();
    }

    // A bare campaign with a hand-built ribbon: four strips, the middle two owned by nation 0,
    // the outer two by nation 1 - so nation 0 holds two front posts, which is what a wartime
    // shortage is counted against, and a training region with a controlled province.
    static (Campaign c, Nation owner, Nation other, StrategicWar war, int home) Ribbon()
    {
        const int width = 128, height = 64;
        var states = new ushort[width * height];
        var controllers = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = x / 32 + 1;
            var index = y * width + x;
            states[index] = (ushort)(3000 + p);
            controllers[index] = p == 2 || p == 3 ? (ushort)500 : (ushort)501;
            rgb[index * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, controllers, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 4).ToDictionary(p => p, p => p));
        var c = new Campaign(20261014) { ScenarioId = "mobilisation_fixture" };
        c.Defines.HoursPerTick = 24;
        c.Defines.FrontStaffCallsUpIdle = false;
        c.SetFeature("militia", false);
        for (var id = 0; id < 2; id++)
            c.Nations.Add(new Nation { Id = id, MapIndex = 500 + id, Tag = "MB" + id,
                Name = "Country " + id, Population = 100_000_000, Gdp = 100, Treasury = 20 });
        for (var p = 1; p <= 4; p++)
            c.States.Add(new ScenarioState { Id = "mobilisation_region_" + p, Name = $"Ribbon {p}",
                MapIndex = 3000 + p, NationMapIndex = p == 2 || p == 3 ? 500 : 501,
                NationId = p == 2 || p == 3 ? "MB0" : "MB1", Population = 10_000_000 });
        c.AttachProvinceTopology(map);
        c.Wars.Add(new StrategicWar { Id = 885, AttackerId = 0, DefenderId = 1,
            LastFrontDay = c.ElapsedDays, FrontDirty = false });
        return (c, c.Nations[0], c.Nations[1], c.Wars[0], 2);
    }

    static Army Unit(Campaign c, Nation nation, int province, float divisions)
    {
        var centre = c.ProvinceMap.CenterOf(province);
        var army = new Army { Id = c.Armies.Count + 500_000, NationId = nation.Id, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], OrderKind = ArmyOrder.Manual,
            AssignedWarId = -1, Latitude = centre.Latitude, Longitude = centre.Longitude,
            Divisions = divisions, Organisation = 1, SupplyLevel = 1, Strength = 1, PlanActive = false };
        c.Armies.Add(army);
        return army;
    }

    static void ShortFrontRaisesABatch()
    {
        var (c, owner, other, war, home) = Ribbon();
        c.IsPaused = false;
        foreach (var province in new[] { 2, 3 }) war.AttackerFront.Add(province);
        Unit(c, owner, home, 1f);
        owner.FoundingDivisions = 1f;
        owner.EstablishedDivisions = 1f;
        owner.Treasury = 1_000f;
        owner.ManpowerReserve = 1_000_000_000f;
        var fired = false;
        for (var day = 0; day < 30 && !fired; day++)
        {
            var before = c.Recruitment.Count(order => order.NationId == owner.Id);
            c.TickDay();
            var raised = c.Recruitment.Count(order => order.NationId == owner.Id) - before;
            if (raised > 0)
            {
                Check(raised >= 2,
                    "a country short of its front raises several formations at once, not one a month");
                fired = true;
            }
        }
        Check(fired, "the wartime queue actually received the batch");
    }

    static void PeaceStandsItsArmyDownSooner()
    {
        var (c, owner, _, war, home) = Ribbon();
        c.IsPaused = false;
        c.Wars.Clear();
        // An army one tenth over the establishment it began with: inside the old 12% wartime
        // margin, outside the peace margin a war's end is meant to impose.
        owner.FoundingDivisions = 10f;
        owner.EstablishedDivisions = 10f;
        for (var id = 0; id < 11; id++) Unit(c, owner, home, 1f);
        var before = c.Armies.Count(army => army.NationId == owner.Id);
        var stoodDown = false;
        for (var day = 0; day < 40 && !stoodDown; day++)
        {
            c.TickDay();
            if (c.Armies.Count(army => army.NationId == owner.Id) < before) stoodDown = true;
        }
        Check(stoodDown,
            "a peace one tenth over its establishment sends a formation home (the old margin waited for a fifth)");
    }

    static void QueueCapDoublesInWar()
    {
        var (c, owner, other, war, home) = Ribbon();
        c.Wars.Clear();
        owner.Treasury = 1_000_000f;
        owner.ManpowerReserve = 1_000_000_000f;
        void Raise()
        {
            new RaiseArmyCommand { IssuerId = owner.Id, StateMapIndex = c.ProvinceMap.ProvinceToState[home], Divisions = 1f }.Apply(c);
        }
        for (var i = 0; i < 12; i++) Raise();
        var before = owner.Treasury;
        Raise();
        Check(c.Recruitment.Count(order => order.NationId == owner.Id) == 12 &&
              MathF.Abs(owner.Treasury - before) < 0.001f,
            "the thirteenth batch waits while the country is at peace");
        c.Wars.Add(new StrategicWar { Id = 9002, AttackerId = owner.Id, DefenderId = other.Id });
        Raise();
        Check(c.Recruitment.Count(order => order.NationId == owner.Id) == 13,
            "the same thirteenth batch is accepted the day war comes");
        for (var i = 0; i < 11; i++) Raise();
        before = owner.Treasury;
        Raise();
        Check(c.Recruitment.Count(order => order.NationId == owner.Id) == 24 &&
              MathF.Abs(owner.Treasury - before) < 0.001f,
            "the wartime book holds twenty-four and no more");
    }
}
