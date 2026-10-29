using System;
using DealOrForce.Simulation;

/// <summary>
/// A staff attack waits when the depot's recorded ammunition or oil cover is under the floor.
/// A direct order does not wait. Reading the sentence does not spend the depot.
/// </summary>
static class DepotAttackTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("depot attack: " + label);
        Console.WriteLine("PASS depot attack: " + label);
    }

    public static void Run()
    {
        var loaded = new Defines();
        GameDataLoader.ApplyDefines(loaded);
        Check(MathF.Abs(loaded.SupplyAttackCoverageFloor - 0.5f) < 0.0001f,
            "the shipped floor is half of yesterday's draw");
        Check(!loaded.FrontStaffMovesAdvances, "advances are not spread along the front");

        ShortAmmunitionDoesNotStart();
        OilWaitsWhenAmmunitionIsUnrecorded();
        AFullCoverAndAQuietDepotStillStart();
        TheFloorCanBeTurnedOff();
        ADirectOrderStillStarts();
        ARoadHoldStillWinsTheSentence();
        TheFloorIsFingerprintedOnlyWhenChanged();
        AnIdThatIsNotTheListIndexStillWaits();
    }

    static void ShortAmmunitionDoesNotStart()
    {
        var c = War();
        Check(c.Nations[0].Id == 0 && c.NationById(0) == c.Nations[0], "nation id 0 stands at index 0");
        var first = Advance(c, 1);
        var second = Advance(c, 2);
        c.Nations[0].StockpileCoverage["Ammunition"] = 0.2f;
        c.Nations[0].Treasury = 40f;
        var province = first.ProvinceId;
        FrontPlanner.UpdateAiExecution(c, c.Wars[0]);
        Check(!first.PlanActive && !second.PlanActive,
            "a recorded cover of 20 percent does not start either formation");
        new SetArmyPlanExecutionCommand { IssuerId = 0, ArmyId = first.Id, Active = true }.Apply(c);
        Check(!first.PlanActive, "telling the staff to execute does not start the attack");
        Check(c.Nations[0].Treasury == 40f && first.ProvinceId == province,
            "the refusal spends no treasury and moves no formation");
        var sentence = StaffReasons.Explain(c, first);
        Check(sentence == Loc.Format(StaffReasons.DepotShortSentence, 20, 50),
            "the reason names the cover and the floor: " + sentence);
        Check(StaffReasons.Label(c, first) == Loc.Get("Depot short"), "the clipped label is the short depot mark");
        var before = CampaignFingerprint.Of(c);
        var coverage = c.Nations[0].StockpileCoverage["Ammunition"];
        _ = StaffReasons.Explain(c, first);
        _ = StaffReasons.Label(c, first);
        _ = LogisticsSystem.DescribeSupply(c, first);
        _ = LogisticsSystem.DepotShortOfAttack(c, first);
        Check(CampaignFingerprint.Of(c) == before && c.Nations[0].StockpileCoverage["Ammunition"] == coverage &&
              !first.PlanActive,
            "reading the reason does not change the fingerprint, the depot, or the order");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var russian = StaffReasons.Explain(c, first);
            Check(russian.Contains("20") && russian.Contains("50") && russian.Contains("атака не начинается") &&
                  CampaignFingerprint.Of(c) == before,
                "the Russian sentence is the same refusal: " + russian);
        }
        finally { Loc.Language = language; }

        c.Nations[0].StockpileCoverage["Ammunition"] = 0.5f;
        FrontPlanner.UpdateAiExecution(c, c.Wars[0]);
        Check(first.PlanActive && second.PlanActive,
            "cover exactly at the floor starts the attack");
        Check(StaffReasons.Explain(c, first) == Loc.Get("The staff ordered an advance along the front."),
            "a cover at the floor is an ordinary staff advance");
        Check(SaveSystem.FormatVersion == 2, "save format stays 2");
    }

    static void OilWaitsWhenAmmunitionIsUnrecorded()
    {
        var c = War();
        var army = Advance(c, 1);
        c.Nations[0].StockpileCoverage["Oil"] = 0.49f;
        FrontPlanner.UpdateAiExecution(c, c.Wars[0]);
        Check(!army.PlanActive, "oil under the floor waits even when ammunition is not recorded");
        Check(StaffReasons.Explain(c, army) == Loc.Format(StaffReasons.DepotShortSentence, 49, 50),
            "the sentence uses the oil percent: " + StaffReasons.Explain(c, army));
    }

    static void AFullCoverAndAQuietDepotStillStart()
    {
        var quiet = War();
        var quietArmy = Advance(quiet, 1);
        FrontPlanner.UpdateAiExecution(quiet, quiet.Wars[0]);
        Check(quietArmy.PlanActive, "a depot with no recorded shortfall still starts");

        var full = War();
        var fullArmy = Advance(full, 1);
        full.Nations[0].StockpileCoverage["Ammunition"] = 1f;
        full.Nations[0].StockpileCoverage["Oil"] = 0.8f;
        FrontPlanner.UpdateAiExecution(full, full.Wars[0]);
        Check(fullArmy.PlanActive, "cover at or above the floor still starts");
    }

    static void TheFloorCanBeTurnedOff()
    {
        var c = War();
        var army = Advance(c, 1);
        c.Defines.SupplyAttackCoverageFloor = 0f;
        c.Nations[0].StockpileCoverage["Ammunition"] = 0f;
        c.Nations[0].StockpileCoverage["Oil"] = 0f;
        FrontPlanner.UpdateAiExecution(c, c.Wars[0]);
        Check(army.PlanActive, "a floor of zero does not wait");

        var off = War();
        var dry = Advance(off, 1);
        off.Defines.StrategicStockpilesEnabled = false;
        off.Nations[0].StockpileCoverage["Ammunition"] = 0f;
        FrontPlanner.UpdateAiExecution(off, off.Wars[0]);
        Check(dry.PlanActive, "stockpiles switched off do not wait");
    }

    static void ADirectOrderStillStarts()
    {
        var c = War();
        c.Nations[0].IsHuman = true;
        var army = Advance(c, 1);
        army.DirectOrder = true;
        c.Nations[0].StockpileCoverage["Ammunition"] = 0f;
        c.Nations[0].Treasury = 12f;
        FrontPlanner.UpdateAiExecution(c, c.Wars[0]);
        Check(!army.PlanActive, "the staff does not take a human formation");
        new SetArmyPlanExecutionCommand { IssuerId = 0, ArmyId = army.Id, Active = true }.Apply(c);
        Check(army.PlanActive && army.DirectOrder, "a direct order still executes on an empty recorded cover");
        Check(StaffReasons.Explain(c, army) == Loc.Get("The direct order was kept."),
            "the reason says the direct order was kept");
        Check(c.Nations[0].Treasury == 12f, "the direct order does not spend the treasury");
        army.DirectOrder = false;
        army.PlanActive = false;
        new SetArmyPlanExecutionCommand { IssuerId = 0, ArmyId = army.Id, Active = true }.Apply(c);
        Check(!army.PlanActive, "the same click without a direct order does not start");
    }

    static void ARoadHoldStillWinsTheSentence()
    {
        var c = War();
        var army = Advance(c, 1);
        army.CutOff = true;
        army.SupplyDistance = -1;
        c.Nations[0].StockpileCoverage["Ammunition"] = 0f;
        Check(StaffReasons.SupplyHoldsAttack(c, army), "a cut-off formation is still held");
        Check(StaffReasons.Explain(c, army) == Loc.Get("No path to a supply hub, so the attack does not start."),
            "no path is still the sentence when the depot is short as well");
        Check(!c.Defines.FrontStaffMovesAdvances, "the road hold did not spread advances");
    }

    static void TheFloorIsFingerprintedOnlyWhenChanged()
    {
        var c = new Campaign(29);
        var before = CampaignFingerprint.Of(c);
        c.Defines.SupplyAttackCoverageFloor = 0.5f;
        Check(CampaignFingerprint.Of(c) == before, "the default floor is not in the fingerprint");
        c.Defines.SupplyAttackCoverageFloor = 0.25f;
        Check(CampaignFingerprint.Of(c) != before, "a changed floor is in the fingerprint");
        c.Defines.SupplyAttackCoverageFloor = 0.5f;
        Check(CampaignFingerprint.Of(c) == before, "restoring the floor restores the fingerprint");
        Check(SaveSystem.FormatVersion == 2, "the fingerprint check leaves the save format at 2");
    }

    static void AnIdThatIsNotTheListIndexStillWaits()
    {
        var c = new Campaign(29);
        c.Nations.Add(new Nation { Id = 0, MapIndex = 1, Tag = "AT0", Name = "Attacker", Population = 2_000_000 });
        c.Nations.Add(new Nation { Id = 4, MapIndex = 9, Tag = "N4", Name = "Fourth", Population = 2_000_000, IsHuman = false });
        Check(c.Nations[0].Id == 0 && c.NationById(0) == c.Nations[0] && c.NationById(4) is null,
            "nation id 0 stands at index 0, and id 4 is not that index");
        var war = new StrategicWar { Id = 7, AttackerId = 4, DefenderId = 0 };
        c.Wars.Add(war);
        var army = new Army
        {
            Id = 1, NationId = 4, ProvinceId = 3, OrderKind = ArmyOrder.AdvanceFront, Divisions = 1f,
            Organisation = 1f, SupplyLevel = 1f, Strength = 1f, Planning = 1f, PlanActive = false,
            AssignedWarId = war.Id, SupplyDistance = 2, DirectOrder = false, FrontLineId = -1
        };
        c.Armies.Add(army);
        c.Nations[1].StockpileCoverage["Ammunition"] = 0.1f;
        new SetArmyPlanExecutionCommand { IssuerId = 4, ArmyId = army.Id, Active = true }.Apply(c);
        Check(!army.PlanActive && army.ProvinceId == 3,
            "the wait follows the nation's own depot, not the list index");
    }

    static Campaign War()
    {
        var c = new Campaign(29);
        c.Nations.Add(new Nation { Id = 0, MapIndex = 1, Tag = "AT0", Name = "Attacker", Population = 2_000_000, IsHuman = false });
        c.Nations.Add(new Nation { Id = 1, MapIndex = 2, Tag = "DF1", Name = "Defender", Population = 2_000_000 });
        c.Wars.Add(new StrategicWar { Id = 7, AttackerId = 0, DefenderId = 1 });
        return c;
    }

    static Army Advance(Campaign c, int id)
    {
        var army = new Army
        {
            Id = id, NationId = 0, ProvinceId = 1, OrderKind = ArmyOrder.AdvanceFront, Divisions = 1f,
            Organisation = 1f, SupplyLevel = 1f, Strength = 1f, Planning = 1f, PlanActive = false,
            AssignedWarId = c.Wars[0].Id, SupplyDistance = 2, CutOff = false, DirectOrder = false, FrontLineId = -1
        };
        c.Armies.Add(army);
        return army;
    }
}
