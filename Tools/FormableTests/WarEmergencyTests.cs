using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A war for existence must not be a scheduling problem. These checks pin the emergency
/// path to ordinary commands: an idle formation is re-posted into an empty front post, an
/// undefended enemy province can be taken by a reserve, and an emergency formation is
/// queued through the same recruitment rules - including the wartime deficit rule.
/// </summary>
static class WarEmergencyTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("war emergency: " + label);
        Console.WriteLine("PASS war emergency: " + label);
    }

    public static void Run()
    {
        var c = Fixture(out var attacker, out var defender);
        var war = c.Wars[0];
        var idle = Unit(c, 0, 1);
        var treasury = attacker.Treasury;
        var manpower = attacker.ManpowerReserve;

        WarEmergency.Tick(c);
        Check(idle.AssignedWarId == war.Id && idle.OrderProvinceId > 0,
            "an idle division is given a real post on the threatened front");
        Check(c.Recruitment.Count > 0 || attacker.Treasury != treasury,
            "a short line queues an emergency formation through the ordinary recruitment path");
        Check(attacker.ManpowerReserve < manpower,
            "emergency recruitment spends real manpower rather than creating a free unit");

        var poor = Fixture(out var poorAttacker, out _);
        poorAttacker.Treasury = 0f;
        poorAttacker.ManpowerReserve = 200_000;
        var before = poor.Recruitment.Count;
        WarEmergency.Tick(poor);
        Check(poor.Recruitment.Count > before,
            "a country defending its own ground may borrow for an emergency formation");
        var paid = poor.Recruitment.Where(order => order.NationId == poorAttacker.Id).Sum(order => order.PaidCost);
        Check(poorAttacker.Treasury == 0f && paid > 0 && MathF.Abs(poorAttacker.PublicDebt - paid) < .00001f,
            "empty cash is normalised into exactly the paid recruitment debt, not free units");

        var denied = new RaiseArmyCommand
        {
            IssuerId = poorAttacker.Id, StateMapIndex = 1001, Divisions = 1f, AllowDeficit = false
        };
        denied.Apply(poor);
        Check(denied.FailureReason.Contains("treasury", StringComparison.OrdinalIgnoreCase),
            "an ordinary raise still refuses a treasury it cannot pay");

        var gap = Fixture(out var gapAttacker, out _);
        Unit(gap, 0, 1);
        var gapArmy = Unit(gap, 0, 2);
        gapArmy.OrderKind = ArmyOrder.ReserveFront;
        gapArmy.AssignedWarId = -1;
        gap.Wars[0].DefenderOccupiedValue = 0.1f;
        // The empty province is the defender's own land under occupation: a defender that is
        // not yet winning takes back its own before it invades (AiDefenderInvadesFromScore).
        gap.States[2].CoreTags.Add("AAA");
        WarEmergency.Tick(gap);
        Check(gap.Armies.Any(army => army.DirectOrder || army.OrderProvinceId == 3 || army.Route.Contains(3)),
            "a reserve can occupy an undefended enemy province beside the front");

        var longLine = Fixture(out _, out _, 3, 6);
        var onlyReserve = Unit(longLine, 0, 1);
        WarEmergency.Tick(longLine);
        Check(onlyReserve.OrderProvinceId == 2 && onlyReserve.Route.Count > 0 && onlyReserve.Route[^1] == 2,
            "one reserve is not redirected a second time to pretend it can fill two gaps");

        foreach (var role in new[] { ArmyOrder.DefendFront, ArmyOrder.AdvanceFront, ArmyOrder.Spearhead })
        {
            var posted = Fixture(out _, out _);
            var serving = Unit(posted, 0, 1);
            serving.AssignedWarId = 0; serving.OrderKind = role;
            serving.OrderProvinceId = 1;
            WarEmergency.Tick(posted);
            Check(serving.OrderKind == role && serving.OrderProvinceId == 1 && !serving.DirectOrder,
                "emergency does not strip an existing " + role + " duty to mend another hole");
        }
        var busy = Fixture(out _, out _);
        var otherWarReserve = Unit(busy, 0, 1);
        otherWarReserve.AssignedWarId = 99; otherWarReserve.OrderKind = ArmyOrder.ReserveFront;
        WarEmergency.Tick(busy);
        Check(otherWarReserve.AssignedWarId == 99, "a reserve serving another war is not silently stolen");

        var twoWars = Fixture(out _, out _, 3, 6);
        twoWars.SetFeature("militia", false);
        twoWars.Defines.WarEmergencyReinforcementsPerDay = 1;
        Unit(twoWars, 0, 1);
        var spare = Unit(twoWars, 0, 1);
        var second = new StrategicWar { Id = 2, AttackerId = 1, DefenderId = 0 };
        second.DefenderFront.Add(3); second.AttackerFront.Add(4);
        twoWars.Wars.Add(second);
        WarEmergency.Tick(twoWars);
        Check(spare.AssignedWarId < 0, "the daily country reposting budget is shared across its wars");

        var engaged = Fixture(out _, out _);
        engaged.Wars[0].DefenderFront.Clear(); engaged.Wars[0].DefenderFront.Add(2);
        var fighting = Unit(engaged, 0, 2); fighting.BattleId = 99;
        WarEmergency.Tick(engaged);
        Check(engaged.Recruitment.All(order => order.NationId != 0),
            "a formation already fighting for a front province is not counted as an empty post");

        var zero = Fixture(out _, out _);
        zero.Defines.WarEmergencyRaisesPerDay = 0; zero.Defines.WarEmergencyReinforcementsPerDay = 0;
        var waiting = Unit(zero, 0, 1);
        WarEmergency.Tick(zero);
        Check(zero.Recruitment.All(order => order.NationId != 0) && waiting.AssignedWarId < 0,
            "zero data budgets really disable emergency raising and reposting");

        var exhausted = Fixture(out var indebted, out _);
        indebted.Treasury = 0; indebted.PublicDebt = indebted.Gdp * exhausted.Defines.WarDebtLimitShare;
        var men = indebted.ManpowerReserve;
        WarEmergency.Tick(exhausted);
        Check(exhausted.Recruitment.All(order => order.NationId != indebted.Id) && indebted.ManpowerReserve == men,
            "normalised debt cannot bypass the wartime credit ceiling on the next day");
        var direct = new RaiseArmyCommand { IssuerId = indebted.Id, StateMapIndex = 1001, AllowDeficit = true };
        direct.Apply(exhausted);
        Check(direct.FailureReason == "Wartime recruitment credit limit reached.",
            "the authoritative recruitment command enforces the same credit ceiling");

        // The side that started the war has open posts along any long front; that is not a
        // war for existence, and it raises and reposts nothing until it loses ground.
        var aggressor = Fixture(out var invader, out _);
        aggressor.Wars[0].AttackerId = 0; aggressor.Wars[0].DefenderId = 1;
        var own = aggressor.Wars[0].DefenderFront.ToArray();
        var theirs = aggressor.Wars[0].AttackerFront.ToArray();
        aggressor.Wars[0].AttackerFront.Clear(); aggressor.Wars[0].AttackerFront.UnionWith(own);
        aggressor.Wars[0].DefenderFront.Clear(); aggressor.Wars[0].DefenderFront.UnionWith(theirs);
        var invaderIdle = Unit(aggressor, 0, 1);
        var invaderMen = invader.ManpowerReserve;
        WarEmergency.Tick(aggressor);
        Check(invaderIdle.AssignedWarId < 0 && aggressor.Recruitment.All(order => order.NationId != invader.Id) &&
              invader.ManpowerReserve == invaderMen,
            "an attacker's open posts are not a war for existence");

        var human = Fixture(out var humanNation, out _);
        human.ConfigureSinglePlayer(humanNation.Id);
        var humanUnit = Unit(human, humanNation.Id, 1);
        WarEmergency.Tick(human);
        Check(humanUnit.AssignedWarId < 0 && human.Recruitment.All(order => order.NationId != humanNation.Id),
            "an undelegated human retains command of formations and recruitment");
    }

    static Army Unit(Campaign c, int nationId, int province)
    {
        var army = new Army
        {
            Id = c.Armies.Count, NationId = nationId, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], TemplateId = "line_infantry",
            Divisions = 1f, Organisation = 1f, Strength = 1f, SupplyLevel = 1f,
            Planning = 1f, Personnel = 15_000, MaxPersonnel = 15_000,
            Equipment = 1f, Cohesion = 1f, OrderKind = ArmyOrder.Manual
        };
        var point = c.ProvinceMap.CenterOf(province);
        army.Latitude = point.Latitude; army.Longitude = point.Longitude;
        c.Armies.Add(army);
        return army;
    }

    static Campaign Fixture(out Nation attacker, out Nation defender, int owned = 2, int total = 4)
    {
        var c = new Campaign(20261018) { ScenarioId = "war_emergency_fixture" };
        attacker = c.AddNation(new Nation
        {
            Tag = "AAA", MapIndex = 501, Population = 3_000_000, Gdp = 100,
            Treasury = 100, OutputScale = 1, Stability = .8f, WarSupport = 1,
            ManpowerReserve = 500_000
        });
        defender = c.AddNation(new Nation
        {
            Tag = "BBB", MapIndex = 502, Population = 2_000_000, Gdp = 60,
            Treasury = 60, OutputScale = 1, Stability = .8f, WarSupport = 1,
            ManpowerReserve = 500_000
        });
        for (var i = 0; i < total; i++)
            c.States.Add(new ScenarioState
            {
                Id = "emergency_" + i, MapIndex = 1001 + i, Name = "Emergency " + i,
                NationId = i < owned ? "AAA" : "BBB", NationMapIndex = i < owned ? 501 : 502,
                Population = 1_000_000, Stability = .8f
            });
        c.InvalidateStateIndex();
        var width = total;
        var height = 2;
        var cells = new ushort[width * height];
        var owners = new ushort[width * height];
        for (var x = 0; x < width; x++)
        {
            cells[x] = (ushort)(1001 + x);
            cells[width + x] = (ushort)(1001 + x);
            owners[x] = x < owned ? (ushort)501 : (ushort)502;
            owners[width + x] = x < owned ? (ushort)501 : (ushort)502;
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, width, height, _ => false));
        // The country under test (AAA, "attacker" in the names above for the side whose line
        // is short) is the one being attacked: an open post is an emergency for a defender.
        var war = new StrategicWar
        {
            Id = 0, AttackerId = 1, DefenderId = 0, StartedDay = 0,
            ClaimedStateMapIndex = 1002, FrontDirty = false, LastFrontDay = 0
        };
        war.DefenderFront.UnionWith(Enumerable.Range(1, owned));
        war.AttackerFront.UnionWith(Enumerable.Range(owned + 1, total - owned));
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, 0, 1).Stance = DiplomaticStance.AtWar;
        c.Nations[0].CapitalProvinceId = 1;
        c.Nations[0].CapitalStateMapIndex = 1001;
        c.Nations[1].CapitalProvinceId = total;
        c.Nations[1].CapitalStateMapIndex = 1000 + total;
        return c;
    }
}

