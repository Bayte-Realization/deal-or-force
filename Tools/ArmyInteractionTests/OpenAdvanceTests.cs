using System;
using DealOrForce.Simulation;

static class OpenAdvanceTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("open advance: " + text); Console.WriteLine("PASS open advance: " + text); }

    static (Campaign c, Army a) World()
    {
        var c = AdvanceTargetTests.Fixture();
        var a = AdvanceTargetTests.Unit(c, 1, 0, 14, ArmyOrder.AdvanceFront);
        a.PlanActive = false; a.Planning = 0; a.OrderProvinceId = 15; a.OrderIssuedDay = c.ElapsedDays;
        return (c,a);
    }

    public static void Run()
    {
        var (c,a) = World(); var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        Check(Warfare.CanStartOpenAdvance(c,c.Wars[0],a), "a ready formation sees the empty neighbouring opening without an artillery preparation");
        Check(before == CampaignFingerprint.Of(c) && random == c.Random.State, "reading a local opening changes neither world state nor RNG");
        FrontPlanner.UpdateAiExecution(c,c.Wars[0]);
        Check(a.PlanActive && a.Planning == 0, "the existing execution command starts the plan without manufacturing planning points");
        Warfare.Tick(c);
        Check(a.MoveProgress > 0 && !a.InBattle && c.Battles.Count == 0, "the formation actually advances into empty ground, not merely an enabled checkbox");

        (c,a) = World(); AdvanceTargetTests.Unit(c,2,1,15,ArmyOrder.DefendFront,4);
        FrontPlanner.UpdateAiExecution(c,c.Wars[0]);
        Check(!a.PlanActive, "a defended objective still waits for normal planning");
        (c,a) = World(); AdvanceTargetTests.Unit(c,2,1,16,ArmyOrder.DefendFront,4);
        FrontPlanner.UpdateAiExecution(c,c.Wars[0]);
        Check(!a.PlanActive, "an empty but covered trap does not bypass the existing local superiority rule");

        foreach (var condition in new (string, Action<Campaign,Army>)[] {
            ("low organisation", (w,u)=>u.Organisation=0), ("low supply", (w,u)=>u.SupplyLevel=0),
            ("exact organisation floor", (w,u)=>u.Organisation=w.Defines.AutonomousAssaultMinimumOrganisation),
            ("exact supply floor", (w,u)=>u.SupplyLevel=w.Defines.AutonomousAssaultMinimumSupply),
            ("nonfinite organisation", (w,u)=>u.Organisation=float.NaN),
            ("nonfinite supply", (w,u)=>u.SupplyLevel=float.PositiveInfinity),
            ("recovery interval", (w,u)=>u.ReadyDay=w.ElapsedDays+1), ("ongoing march", (w,u)=>u.Route.Add(13)),
            ("battle", (w,u)=>u.BattleId=1), ("embarked", (w,u)=>u.EmbarkedFleetId=1),
            ("direct order", (w,u)=>u.DirectOrder=true), ("support fire only", (w,u)=>u.SupportOnly=true),
            ("disabled mod rule", (w,u)=>w.Defines.AiStartsUnopposedAdvance=false) })
        {
            (c,a) = World(); condition.Item2(c,a); FrontPlanner.UpdateAiExecution(c,c.Wars[0]);
            Check(!a.PlanActive, condition.Item1 + " cannot use the opportunistic start");
        }
        (c,a) = World(); c.ConfigureSinglePlayer(0); FrontPlanner.UpdateAiExecution(c,c.Wars[0]);
        Check(!a.PlanActive, "a human's undelegated paused plan remains paused");
        (c,a) = World(); a.OrderKind=ArmyOrder.DefendFront; FrontPlanner.UpdateAiExecution(c,c.Wars[0]);
        Check(!a.PlanActive, "local defence is not silently converted into an offensive");
        TwoWarsRefreshTheirIndexBeforeEnumeration();
        VisionFollowsAnHourlyMarch();
    }

    static void TwoWarsRefreshTheirIndexBeforeEnumeration()
    {
        var (c, first) = World();
        c.AddNation(new Nation { Tag="OA2", MapIndex=502, Name="Third", Population=1000000 });
        c.AddNation(new Nation { Tag="OA3", MapIndex=503, Name="Fourth", Population=1000000 });
        c.SetProvinceController(32,502); c.SetProvinceController(33,503);
        var secondWar = new StrategicWar { Id=885, AttackerId=2, DefenderId=3, LastFrontDay=c.ElapsedDays };
        c.Wars.Add(secondWar);
        var second = AdvanceTargetTests.Unit(c,2,2,32,ArmyOrder.AdvanceFront);
        second.AssignedWarId=885; second.PlanActive=false; second.Planning=0; second.OrderProvinceId=33;
        c.Wars[0].FrontDirty=false; c.Wars[0].LastFrontDay=c.ElapsedDays;
        c.Defines.OpenCountryCapturePerDay=2; c.Defines.UnopposedCapturePerDay=2;
        Warfare.Tick(c);
        Check(c.ProvinceController[15] == 500 && second.PlanActive,
            "a capture in the first war refreshes the shared index before iterating the next war's formations");
    }

    static void VisionFollowsAnHourlyMarch()
    {
        var (c,a) = World();
        c.Defines.BorderVisionDepth=0; c.Defines.ArmyVisionDepth=0; c.Defines.HoursPerTick=1;
        a.OrderKind=ArmyOrder.Manual; a.AssignedWarId=-1; a.PlanActive=false; a.ProvinceId=20;
        Check(CampaignIntel.Vision(c,0).Sees(20) && !CampaignIntel.Vision(c,0).Sees(28),
            "the warm visibility cache initially reveals only the army's actual position");
        a.ProvinceId=28; c.IsPaused=false; c.Tick();
        Check(c.ElapsedDays == 0 && CampaignIntel.Vision(c,0).Sees(28) && !CampaignIntel.Vision(c,0).Sees(20),
            "one hourly step refreshes army vision without waiting for a new day or a province capture");
    }
}
