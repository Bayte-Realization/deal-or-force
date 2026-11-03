using System;
using DealOrForce.Simulation;

/// <summary>A rising holds ground it does not own; its army is fed from that ground, not left to starve.</summary>
static class InsurgentSupplyTests
{
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL insurgent supply: " + name);
        Console.WriteLine("PASS insurgent supply: " + name);
    }

    public static void Run()
    {
        var c = new Campaign(219) { ScenarioId = "insurgent_supply_fixture" };
        c.Defines.HoursPerTick = 24;
        for (var id = 0; id < 2; id++) c.Nations.Add(new Nation { Id = id, MapIndex = 600 + id,
            Tag = "IS" + id, Name = "Insurgent country " + id, Population = 1_000_000,
            Gdp = 100, Treasury = 20, OutputScale = 1f, DebtInitialized = true });
        var map = FriendlyRouteSearchTests.Map();
        for (var p = 1; p <= 8; p++)
        {
            map.ProvinceToNation[p] = 600;
            c.States.Add(new ScenarioState { Id = "insurgent_" + p, MapIndex = 3000 + p,
                NationMapIndex = 600, NationId = "IS0", Name = "Insurgent region " + p, Population = 100_000 });
        }
        c.AttachProvinceTopology(map); c.Armies.Clear(); c.Wars.Clear(); c.Battles.Clear();
        c.ConfigureSinglePlayer(0); c.IsPaused = true;
        // The rising holds three provinces of the country and owns none.
        foreach (var p in new[] { 2, 3, 4 }) c.SetProvinceController(p, 601);
        c.Wars.Add(new StrategicWar { Id = 77001, AttackerId = 1, DefenderId = 0, StartedDay = 0 });
        c.MarkWarsChanged();
        var army = new Army { Id = 9101, NationId = 1, ProvinceId = 3, StateMapIndex = 3003,
            Divisions = 1, Strength = .8f, Organisation = .7f, Personnel = 12000, Equipment = .6f,
            CutOff = true, SupplyLevel = .1f };
        c.Armies.Add(army);
        Check(!c.HasOwnedLand(c.Nations[1]), "the rising owns no land");
        LogisticsSystem.Tick(c);
        Check(!army.CutOff, "its army is supplied from the ground it holds");
    }
}
