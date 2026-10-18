using System;
using System.Linq;
using DealOrForce.Simulation;

static class WarStalemateTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("war regression: " + label); Console.WriteLine("PASS war regression: " + label); }

    static Campaign Fixture(string attacker = "RUS", string defender = "UKR")
    {
        var c = new Campaign(20261018) { ScenarioId = "war_regression_fixture" };
        c.AddNation(new Nation { Tag = attacker, MapIndex = 501, Population = 3_000_000, Gdp = 100,
            Treasury = 100, OutputScale = 1, Stability = .8f, WarSupport = 1 });
        c.AddNation(new Nation { Tag = defender, MapIndex = 502, Population = 2_000_000, Gdp = 60,
            Treasury = 60, OutputScale = 1, Stability = .8f, WarSupport = 1 });
        for (var i = 0; i < 5; i++)
            c.States.Add(new ScenarioState { Id = "war_fixture_" + i, MapIndex = 1001 + i,
                Name = "War fixture " + i, NationId = i < 3 ? attacker : defender,
                NationMapIndex = i < 3 ? 501 : 502, Population = 1_000_000, Stability = .8f });
        var cells = Enumerable.Range(0, 80).Select(i => (ushort)(1001 + i % 20 / 4)).ToArray();
        var owners = Enumerable.Range(0, 80).Select(i => (ushort)(i % 20 < 12 ? 501 : 502)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 20, 4, _ => false));
        Check(c.ProvinceMap.ProvinceCount == 5, "the fixture has five distinct provinces");
        for (var p = 1; p <= 5; p++)
            c.ProvinceMap.Neighbors[p] = p == 1 ? new[] { 2 } : p == 5 ? new[] { 4 } : new[] { p - 1, p + 1 };
        c.Wars.Add(new StrategicWar { Id = 0, AttackerId = 0, DefenderId = 1, StartedDay = 0,
            AiNegotiationUnlockDay = 99999, ClaimedStateMapIndex = 1004, FrontDirty = false, LastFrontDay = 0 });
        CampaignDiplomacy.Get(c, 0, 1).Stance = DiplomaticStance.AtWar;
        c.Nations[0].CapitalProvinceId = 1; c.Nations[1].CapitalProvinceId = 5;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 12L * 24);
        SovereignFinance.NormalizeAccounts(c);
        return c;
    }

    static Army Unit(Campaign c, int id, int province, string template = "line_infantry")
    {
        var a = new Army { Id = id, NationId = 0, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], TemplateId = template,
            Divisions = 1, Organisation = 1, Strength = 1, SupplyLevel = 1, Planning = 1,
            Personnel = 15000, MaxPersonnel = 15000, Equipment = 1, Cohesion = 1 };
        var point = c.ProvinceMap.CenterOf(province); a.Latitude = point.Latitude; a.Longitude = point.Longitude;
        c.Armies.Add(a); return a;
    }

    public static void Run()
    {
        EmergencyReserves();
        PeriodicReview();
        DelegatedCommand();
        Occupation("RUS", "UKR");
        Occupation("PRK", "KOR");
    }

    static void EmergencyReserves()
    {
        var c = Fixture();
        for (var p = 1; p <= 3; p++) for (var i = 0; i < 3; i++) Unit(c, c.Armies.Count, p, "militia");
        var before = CampaignFingerprint.Of(c);
        var field = FrontPlanner.FieldArmy(c, 0);
        Check(field.Count == 3 && field.Select(a => a.StateMapIndex).Distinct().Count() == 3,
            "a militia-only country can detach one existing unit per donor region");
        Check(c.Armies.Count == 9 && CampaignFingerprint.Of(c) == before,
            "selection creates no troops, equipment or simulation changes");
        Check(c.Armies.GroupBy(a => a.StateMapIndex).All(g => g.Count(a => !field.Contains(a)) >= 1),
            "each donor retains local guards");
        foreach (var a in field) { a.AssignedWarId = 0; a.OrderKind = ArmyOrder.DefendFront; a.StateMapIndex = 1003; }
        Check(FrontPlanner.FieldArmy(c, 0).Select(a => a.Id).SequenceEqual(field.Select(a => a.Id)),
            "moving detached units does not continually conscript additional guards");
        var regular = Unit(c, 99, 2);
        var mixed = FrontPlanner.FieldArmy(c, 0);
        Check(mixed.Contains(regular) && field.All(mixed.Contains) && mixed.Count == field.Count + 1,
            "a rebuilt regular army retains its already-detached units without taking more guards");
        var ordinary = Fixture(); Unit(ordinary, 0, 1); Unit(ordinary, 1, 1, "militia"); Unit(ordinary, 2, 1, "militia");
        Check(FrontPlanner.FieldArmy(ordinary, 0).Count == 1,
            "ordinary regular armies do not absorb territorial militia");
        var small = Fixture(); Unit(small, 0, 1, "militia"); Unit(small, 1, 2, "militia");
        Check(FrontPlanner.FieldArmy(small, 0).Count == 0, "the last local guard is never drafted away");
        var ordered = Fixture(); Unit(ordered, 0, 1, "militia").DirectOrder = true;
        Unit(ordered, 1, 1, "militia").FrontLineId = 10; Unit(ordered, 2, 1, "militia").EmbarkedFleetId = 20;
        Check(FrontPlanner.FieldArmy(ordered, 0).Count == 0,
            "direct orders, authored lines and embarked formations are protected");
    }

    static void PeriodicReview()
    {
        var c = Fixture(); var a = Unit(c, 0, 1);
        a.OrderKind = ArmyOrder.DefendFront; a.OrderProvinceId = 1; a.AssignedWarId = 0;
        c.Nations[0].IsHuman = true; c.Nations[1].IsHuman = true;
        Warfare.Tick(c);
        Check(a.OrderProvinceId != 1 && c.Wars[0].FriendlyFront(0).Contains(a.OrderProvinceId) && a.Route.Count > 0,
            "a static front reviews a stale rear-area post without waiting for a capture");
        var direct = Fixture(); var d = Unit(direct, 0, 1);
        d.OrderKind = ArmyOrder.DefendFront; d.OrderProvinceId = 2; d.AssignedWarId = 0;
        d.DirectOrder = true; d.Route.Add(2); d.PlannedRoute.Add(2);
        direct.Nations[0].IsHuman = true; direct.Nations[1].IsHuman = true;
        Warfare.Tick(direct);
        Check(d.DirectOrder && d.OrderProvinceId == 2 && d.Route.SequenceEqual(new[] { 2 }),
            "the periodic review preserves an explicit player march");
    }

    static void DelegatedCommand()
    {
        var c = Fixture();
        c.Nations[0].IsHuman = true;
        var a = Unit(c, 0, 1);
        var b = Unit(c, 1, 2);
        FrontPlanner.ReassignAi(c, c.Wars[0]);
        Check(a.OrderKind == ArmyOrder.Manual && b.OrderKind == ArmyOrder.Manual,
            "a player's formations keep their own orders while command is not delegated");

        Delegation.Set(c.Nations[0], Delegation.Command, true);
        FrontPlanner.ReassignAi(c, c.Wars[0]);
        Check(a.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead && a.AssignedWarId == c.Wars[0].Id,
            "delegating the field command gives the player's army a role in the war");
        Check(b.OrderKind != ArmyOrder.Manual && b.AssignedWarId == c.Wars[0].Id,
            $"the whole delegated field army is assigned, not one formation (a={a.OrderKind}/{a.AssignedWarId}, b={b.OrderKind}/{b.AssignedWarId})");

        a.PlanActive = false;
        a.Planning = 1f;
        a.OrderKind = ArmyOrder.AdvanceFront;
        a.AssignedWarId = c.Wars[0].Id;
        FrontPlanner.UpdateAiExecution(c, c.Wars[0], new System.Collections.Generic.List<Army> { a });
        Check(a.PlanActive, "delegated command also activates the offensive plan");
    }

    static void Occupation(string attacker, string defender)
    {
        var c = Fixture(attacker, defender); var war = c.Wars[0];
        c.ConfigureSinglePlayer(0);
        c.Nations[0].IsHuman = true; c.Nations[1].IsHuman = true;
        var a = Unit(c, 0, 3);
        new MoveArmyCommand { IssuerId = 0, ArmyId = a.Id, TargetProvinceId = 4 }.Apply(c);
        c.IsPaused = false;
        for (var day = 0; day < 24 && c.ProvinceController[4] != 501; day++) c.TickDay();
        Check(c.ProvinceController[4] == 501 && !war.Ended, attacker + " physically captures the enemy province before peace");
        Check(c.ProvinceMap.ProvinceToNation[4] == 502 && c.States.First(s => s.MapIndex == 1004).NationMapIndex == 502,
            attacker + " occupation does not transfer " + defender + " ownership or regional title");
        var before = CampaignFingerprint.Of(c);
        var demand = new PeaceDemand { WarId = 0, ProposerId = 0 }; demand.Provinces.Add(4);
        _ = PeaceConference.Round(c, war, demand);
        Check(before == CampaignFingerprint.Of(c), attacker + " draft treaty calculation never annexes land");
        var imported = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "war-regression"), "war-regression");
        var loaded = imported.Campaign;
        Check(loaded is not null, "an occupied campaign uses the real snapshot codec: " + imported.Warning);
        loaded.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(loaded.ProvinceMap.ProvinceToNation[4] == 502 && loaded.ProvinceController[4] == 501,
            attacker + " save/load preserves distinct owner and occupier");
        war.WarScore = 100; war.StartedDay = c.ElapsedDays - 400; war.AiNegotiationUnlockDay = 0;
        Check(PeaceConference.Resolve(c, war, demand), attacker + " can sign the secured territorial settlement: " + war.LastNegotiation);
        Check(war.Ended && c.ProvinceMap.ProvinceToNation[4] == 501,
            attacker + " obtains permanent title only at signature");
    }
}
