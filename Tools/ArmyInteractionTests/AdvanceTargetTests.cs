using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class AdvanceTargetTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL advance rotation: " + label);
        Console.WriteLine("PASS advance rotation: " + label);
    }

    public static void Run()
    {
        CoveredPinGivesWayToAnOpenSector();
        DirectOrderKeepsItsCoveredPin();
        DefendedPinIsStillAnAssault();
    }

    // A 6×6 cell grid (the raster wraps horizontally, so everything is placed away from the
    // seam and the covering distance is counted on the grid). The attacker holds one cell
    // (col 1, row 2); every other cell is enemy. The pinned objective is east of it
    // (col 2, row 2) and a strong enemy corps stands one hop further east, covering the pin.
    // The sectors north, west and south of the attacker are three hops from the corps -
    // outside the coverage radius - so the refused pin must give way to one of them.
    static Campaign Fixture()
    {
        const int width = 96, height = 96;
        var states = new ushort[width * height];
        var controllers = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = (y / 16) * 6 + x / 16 + 1;
            var index = y * width + x;
            states[index] = (ushort)(3000 + p);
            controllers[index] = (ushort)(p == 14 ? 500 : 501);
            rgb[index * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, controllers, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 36).ToDictionary(p => p, p => p));
        var c = new Campaign(20261014) { ScenarioId = "advance_rotation_fixture" };
        c.Defines.HoursPerTick = 24;
        c.Defines.FrontStaffCallsUpIdle = false;
        for (var id = 0; id < 2; id++)
            c.Nations.Add(new Nation { Id = id, MapIndex = 500 + id, Tag = "RT" + id,
                Name = "Country " + id, Population = 10_000_000, Gdp = 100, Treasury = 20 });
        for (var p = 1; p <= 36; p++)
            c.States.Add(new ScenarioState { Id = "rotation_region_" + p, Name = "Region " + p,
                MapIndex = 3000 + p, NationMapIndex = p == 14 ? 500 : 501,
                NationId = p == 14 ? "RT0" : "RT1", Population = 1_000_000 });
        c.AttachProvinceTopology(map);
        c.Armies.Clear();
        c.Wars.Add(new StrategicWar { Id = 884, AttackerId = 0, DefenderId = 1,
            LastFrontDay = c.ElapsedDays, FrontDirty = false });
        return c;
    }

    static Army Unit(Campaign c, int id, int nation, int province, ArmyOrder order, float divisions = 1f)
    {
        var centre = c.ProvinceMap.CenterOf(province);
        var army = new Army { Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], OrderKind = order,
            OrderProvinceId = -1, AssignedWarId = c.Wars[0].Id,
            Latitude = centre.Latitude, Longitude = centre.Longitude,
            Divisions = divisions, Organisation = 1, SupplyLevel = 1, Strength = 1,
            Planning = 1, PlanActive = true, OrderIssuedDay = -100 };
        c.Armies.Add(army);
        return army;
    }

    static void CoveredPinGivesWayToAnOpenSector()
    {
        var c = Fixture();
        var war = c.Wars[0];
        var army = Unit(c, 1, 0, 14, ArmyOrder.AdvanceFront, divisions: 1f);
        army.OrderProvinceId = 15;
        // The covering wall: far stronger than anything pushing on the pin, one hop east of it.
        Unit(c, 2, 1, 16, ArmyOrder.DefendFront, divisions: 4f);
        Warfare.Tick(c);
        Check(!army.InBattle && c.Battles.Count == 0, "the refused pin starts no battle");
        Check(war.UnopposedProgress.GetValueOrDefault(15) == 0f,
            "the covered pin is not walked into");
        Check(war.UnopposedProgress.Values.Any(value => value > 0f),
            "the army turns to an open sector beside the refused pin");
        var captured = -1;
        for (var day = 0; day < 90 && captured < 0; day++)
        {
            Warfare.Tick(c);
            for (var p = 1; p <= 36 && captured < 0; p++)
                if (p != 14 && p != 15 && c.ProvinceController[p] == 500) captured = p;
        }
        Check(captured > 0 && captured != 15,
            "an open sector falls while the covered one is still refused");
        Check(c.ProvinceController[15] == 501, "the covered pin was not taken by walking through it");
    }

    static void DirectOrderKeepsItsCoveredPin()
    {
        var c = Fixture();
        var war = c.Wars[0];
        var army = Unit(c, 1, 0, 14, ArmyOrder.AdvanceFront);
        army.OrderProvinceId = 15;
        army.DirectOrder = true;
        Unit(c, 2, 1, 16, ArmyOrder.DefendFront, divisions: 4f);
        Warfare.Tick(c);
        Check(!army.InBattle && war.UnopposedProgress.GetValueOrDefault(15) > 0f,
            "a direct order advances into the covered pin at the ordered pace, as before");
        Check(!war.UnopposedProgress.Where(entry => entry.Key != 15).Any(entry => entry.Value > 0f),
            "a direct order is not rotated off its province");
    }

    static void DefendedPinIsStillAnAssault()
    {
        var c = Fixture();
        var army = Unit(c, 1, 0, 14, ArmyOrder.AdvanceFront);
        army.OrderProvinceId = 15;
        Unit(c, 2, 1, 15, ArmyOrder.DefendFront, divisions: 4f);
        Warfare.Tick(c);
        Check(c.Battles.Count == 1 && c.Battles[0].ProvinceId == 15 && army.InBattle,
            "a defended pin is assaulted exactly as before the rotation existed");
    }
}
