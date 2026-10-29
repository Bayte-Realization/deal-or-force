using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// Staff sentences and the supply line a player reads. Tiny maps are attached by reflection.
/// </summary>
static class StaffReasonTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("staff reasons: " + label);
        Console.WriteLine("PASS staff reasons: " + label);
    }

    public static void Run()
    {
        CutOffDoesNotStartAnAttack();
        ReserveFillsAnEmptyPost();
        DirectOrderIsKept();
        ReadingDoesNotChangeTheFingerprint();
        AFedLineNamesTheHub();
    }

    static void CutOffDoesNotStartAnAttack()
    {
        var c = Bare();
        var war = new StrategicWar { Id = 7, AttackerId = 0, DefenderId = 1 };
        c.Wars.Add(war);
        var held = Army(1, ArmyOrder.AdvanceFront);
        held.AssignedWarId = war.Id;
        held.PlanActive = false;
        held.Planning = 1f;
        held.CutOff = true;
        held.SupplyDistance = -1;
        held.DaysCutOff = 2;
        var free = Army(2, ArmyOrder.AdvanceFront);
        free.AssignedWarId = war.Id;
        free.PlanActive = false;
        free.Planning = 1f;
        free.CutOff = false;
        free.SupplyDistance = 2;
        free.SupplyLevel = 1f;
        c.Armies.Add(held);
        c.Armies.Add(free);
        FrontPlanner.UpdateAiExecution(c, war);
        Check(!held.PlanActive, "a cut-off formation does not start an attack");
        Check(free.PlanActive, "a supplied formation on the same side still starts");
        new SetArmyPlanExecutionCommand { IssuerId = 0, ArmyId = held.Id, Active = true }.Apply(c);
        Check(!held.PlanActive && held.OrderKind == ArmyOrder.AdvanceFront,
            "telling a cut-off formation to execute does not start the attack");
        Check(StaffReasons.Explain(c, held) == Loc.Get("No path to a supply hub, so the attack does not start."),
            "the reason is the cutoff sentence: " + StaffReasons.Explain(c, held));
        Check(StaffReasons.Label(c, held) == Loc.Get("No route"), "the clipped label is the short supply mark");
        Check(LogisticsSystem.DescribeSupply(c, held).Contains("No path to a supply hub") &&
              LogisticsSystem.DescribeSupply(c, held).Contains("2"),
            "the supply line still names the missing road and the days already counted");
    }

    static void ReserveFillsAnEmptyPost()
    {
        var c = Front();
        Check(!c.Defines.FrontStaffMovesAdvances, "advances are not spread along the front");
        var war = c.Wars[0];
        war.AttackerFront.Add(4);
        war.DefenderFront.Add(5);
        var reserve = Unit(c, 0, 12, ArmyOrder.ReserveFront);
        Unit(c, 1, 5, ArmyOrder.Manual, nation: 1, divisions: 4f);
        FrontManager.Balance(c, war);
        Check(reserve.OrderKind == ArmyOrder.DefendFront && reserve.OrderProvinceId == 4 && reserve.Route.Count > 0,
            "a free reserve fills the empty post");
        Check(reserve.StaffNote == StaffNote.EmptyPostFromReserve, "the fill is recorded on the formation");
        Check(StaffReasons.Explain(c, reserve) == Loc.Get("A free reserve filled the empty post."),
            "the reason says the reserve filled the post: " + StaffReasons.Explain(c, reserve));
        Check(StaffReasons.Label(c, reserve) == Loc.Get("Reserve fill"), "the clipped label is the reserve mark");
    }

    static void DirectOrderIsKept()
    {
        var c = Front();
        var war = c.Wars[0];
        war.AttackerFront.Add(4);
        war.DefenderFront.Add(5);
        var reserve = Unit(c, 0, 12, ArmyOrder.ReserveFront);
        reserve.DirectOrder = true;
        reserve.OrderProvinceId = 12;
        Unit(c, 1, 5, ArmyOrder.Manual, nation: 1, divisions: 4f);
        var kind = reserve.OrderKind;
        var post = reserve.OrderProvinceId;
        var direct = reserve.DirectOrder;
        FrontManager.Balance(c, war);
        Check(reserve.OrderKind == kind && reserve.OrderProvinceId == post && reserve.DirectOrder == direct &&
              reserve.Route.Count == 0,
            "a direct player order is not overwritten");
        Check(StaffReasons.Explain(c, reserve) == Loc.Get("The direct order was kept."),
            "the reason says the direct order was kept: " + StaffReasons.Explain(c, reserve));
        var spear = Army(9, ArmyOrder.Spearhead);
        spear.SupplyDistance = 1;
        Check(StaffReasons.Explain(c, spear) == Loc.Get("The spearhead stays concentrated."),
            "a supplied spearhead says it stays concentrated");
    }

    static void ReadingDoesNotChangeTheFingerprint()
    {
        var c = Bare();
        var army = Army(1, ArmyOrder.AdvanceFront);
        army.CutOff = true;
        army.SupplyDistance = -1;
        army.DaysCutOff = 1;
        c.Armies.Add(army);
        var kind = army.OrderKind;
        var active = army.PlanActive;
        var post = army.OrderProvinceId;
        var before = CampaignFingerprint.Of(c);
        _ = StaffReasons.Explain(c, army);
        _ = StaffReasons.Label(c, army);
        _ = LogisticsSystem.DescribeSupply(c, army);
        Check(CampaignFingerprint.Of(c) == before && army.OrderKind == kind && army.PlanActive == active &&
              army.OrderProvinceId == post,
            "reading the reason and the supply line does not change the fingerprint or the order");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var russian = StaffReasons.Explain(c, army);
            Check(russian.Contains("атака не начинается") && CampaignFingerprint.Of(c) == before,
                "the Russian sentence is the same fact and does not change the fingerprint: " + russian);
        }
        finally { Loc.Language = language; }
        Check(SaveSystem.FormatVersion == 2, "save format stays 2");
    }

    static void AFedLineNamesTheHub()
    {
        var c = Bare();
        c.States.Add(new ScenarioState { MapIndex = 100, Name = "Depot" });
        var army = Army(1, ArmyOrder.DefendFront);
        army.SupplyDistance = 4;
        army.SupplySourceStateMapIndex = 100;
        army.SupplyCongestion = 1f;
        army.CutOff = false;
        c.Armies.Add(army);
        var line = LogisticsSystem.DescribeSupply(c, army);
        Check(line.Contains("reaches a hub") && line.Contains("Depot") && line.Contains("4"),
            "a fed formation names the hub and the provinces along the road: " + line);
        Check(line.Contains("ammunition") && line.Contains("oil"),
            "depot ammunition and oil days are on the same line");
    }

    static Campaign Bare()
    {
        var c = new Campaign(29);
        c.Nations.Add(new Nation { Id = 0, MapIndex = 1, Tag = "AT0", Name = "Attacker", Population = 2_000_000, IsHuman = false });
        c.Nations.Add(new Nation { Id = 1, MapIndex = 2, Tag = "DF1", Name = "Defender", Population = 2_000_000 });
        Check(c.Nations[0].Id == 0 && c.NationById(0) == c.Nations[0], "nation id 0 stands at index 0");
        return c;
    }

    static Army Army(int id, ArmyOrder order) => new()
    {
        Id = id, NationId = 0, ProvinceId = 1, OrderKind = order, Divisions = 1f,
        Organisation = 1f, SupplyLevel = 1f, Strength = 1f, DirectOrder = false, FrontLineId = -1
    };

    static Campaign Front()
    {
        var owner = new ushort[] { 0, 501, 500, 502, 500, 501, 501, 500, 502,
            502, 502, 502, 500, 500, 500, 500, 502 };
        const int width = 128, height = 64;
        var states = new ushort[width * height];
        var controllers = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var province = (y < 32 ? 8 : 0) + x / 16 + 1;
            var index = y * width + x;
            states[index] = (ushort)(3000 + province);
            controllers[index] = owner[province];
            rgb[index * 3 + 2] = (byte)province;
        }
        var map = ProvinceTopology.FromColorRaster(states, controllers, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 16).ToDictionary(p => p, p => p));
        var c = new Campaign(20261029) { ScenarioId = "staff_reason_fixture" };
        for (var nation = 0; nation < 3; nation++)
            c.Nations.Add(new Nation
            {
                Id = nation, MapIndex = 500 + nation, Tag = "SR" + nation, Name = "Country " + nation,
                Population = 10_000_000, Gdp = 100, Treasury = 20, IsHuman = false
            });
        Check(c.Nations[0].Id == 0 && c.NationById(0) == c.Nations[0], "nation id 0 stands at index 0");
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(c, (ushort[])map.ProvinceToNation.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(c, new float[map.ProvinceCount + 1]);
        c.Wars.Add(new StrategicWar { Id = 882, AttackerId = 0, DefenderId = 1 });
        return c;
    }

    static Army Unit(Campaign c, int id, int province, ArmyOrder duty, int nation = 0, float divisions = 1f)
    {
        var centre = c.ProvinceMap.CenterOf(province);
        var army = new Army
        {
            Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], OrderProvinceId = province,
            AssignedWarId = c.Wars[0].Id, OrderKind = duty, Divisions = divisions,
            Latitude = centre.Latitude, Longitude = centre.Longitude, OrderIssuedDay = -100,
            Strength = 1, Organisation = 1, SupplyLevel = 1, DirectOrder = false, FrontLineId = -1
        };
        c.Armies.Add(army);
        return army;
    }
}
