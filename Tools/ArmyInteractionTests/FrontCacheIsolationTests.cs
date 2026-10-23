using System;
using System.Linq;
using DealOrForce.Simulation;

static class FrontCacheIsolationTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("front cache isolation: " + text); Console.WriteLine("PASS front cache isolation: " + text); }

    public static void Run()
    {
        var (first, _) = MarchContinuityTests.World();
        var (second, _) = MarchContinuityTests.World();
        var one = first.Wars.Single(); var two = second.Wars.Single();
        two.AttackerFront.Clear(); two.AttackerFront.Add(4);
        var remembered = FrontPlanner.ReserveLine(first, one, 0);
        Check(remembered.SetEquals(new[] { 6 }), "the first world's reserve stands behind its actual front");
        var other = FrontPlanner.ReserveLine(second, two, 0);
        Check(other.SetEquals(new[] { 3, 5 }), "an equal-sized front in another campaign cannot borrow the first world's posts");
        Check(!ReferenceEquals(remembered, other) && remembered.SetEquals(new[] { 6 }),
            "cached reserve sets have campaign-local ownership");
        Check(ReferenceEquals(remembered, FrontPlanner.ReserveLine(first, one, 0)),
            "the valid cache is reused within one campaign");
        second.SetProvinceController(5, 502);
        Check(FrontPlanner.ReserveLine(second, two, 0).SetEquals(new[] { 3 }),
            "a controller revision invalidates the affected world's reserve line");
        Check(FrontPlanner.ReserveLine(first, one, 0).SetEquals(new[] { 6 }),
            "changing the other world leaves the original reserve line intact");
        HourlySplitSurvivesAColdSnapshot();
        PostureSurvivesAnHourlySnapshotBoundary();
    }

    static void HourlySplitSurvivesAColdSnapshot()
    {
        var c = SplitWorld(); var peer = SplitWorld();
        FrontPlanner.ReassignAi(c,c.Wars[0]); // warm only one derived allocation
        foreach (var world in new[] {c,peer})
        {
            foreach (var u in world.Armies)
            {
                u.ProvinceId=u.Id == 1 ? 6 : 3; u.StateMapIndex=3000+u.ProvinceId;
                (u.Latitude,u.Longitude)=world.ProvinceMap.CenterOf(u.ProvinceId);
                u.OrderKind=ArmyOrder.Manual; u.AssignedWarId=-1; u.OrderProvinceId=u.OrderStateMapIndex=-1;
                u.ObjectiveStateMapIndex=-1; u.OrderIssuedDay=0; u.Stance=ArmyStance.Balanced;
                u.Route.Clear(); u.PlannedRoute.Clear(); u.MoveProgress=0; u.PlanActive=false;
                u.OrderRouteSteps=u.OrderStepsCompleted=0;
            }
            world.IsPaused=false; world.Tick();
        }
        Check(c.ElapsedHours == 1 && CampaignFingerprint.Of(c) == CampaignFingerprint.Of(peer),
            "warm and cold hourly allocation fixtures begin in exactly the same authoritative state");
        FrontPlanner.ReassignAi(c,c.Wars[0]); FrontPlanner.ReassignAi(peer,peer.Wars[0]);
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(peer),
            "a warm allocation from the preceding hour cannot differ from a cold client's allocation");
    }

    static Campaign SplitWorld()
    {
        var (c,a) = MarchContinuityTests.World();
        c.Defines.HoursPerTick=1; c.ConfigureSinglePlayer(2);
        var b = new Army { Id=2, NationId=0, TemplateId="line_infantry", ProvinceId=6, StateMapIndex=3006,
            Divisions=1, Organisation=1, SupplyLevel=1, Strength=1, OrderKind=ArmyOrder.Manual };
        c.Armies.Add(b);
        var second = new StrategicWar { Id=884, AttackerId=0, DefenderId=2, LastFrontDay=0 };
        second.AttackerFront.Add(2); second.DefenderFront.Add(1); c.Wars.Add(second);
        c.Wars[0].LastFrontDay=0; c.Wars[0].FrontDirty=false;
        return c;
    }

    static void PostureSurvivesAnHourlySnapshotBoundary()
    {
        var (first,a)=MarchContinuityTests.World(); var (peer,b)=MarchContinuityTests.World();
        AiStrategyCatalog.Resolve(first,first.Wars[0],first.NationById(0));
        foreach (var pair in new[] {(first,a),(peer,b)})
        {
            var (w,u)=pair; w.Defines.HoursPerTick=1; w.Wars[0].LastFrontDay=0; w.Wars[0].FrontDirty=false;
            u.OrderKind=ArmyOrder.Manual; u.AssignedWarId=-1; u.PlanActive=false; u.Route.Clear(); u.PlannedRoute.Clear();
            // Control the clock boundary without running unrelated systems which
            // would themselves refresh this cache before the assertion.
            u.Organisation=.2f;
            typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(w,1L);
            w.Date.Advance(1);
        }
        Check(CampaignFingerprint.Of(first) == CampaignFingerprint.Of(peer),
            "warm and cold posture fixtures have the same live organisation, supply and hour");
        var warm=AiStrategyCatalog.Resolve(first,first.Wars[0],first.NationById(0));
        var cold=AiStrategyCatalog.Resolve(peer,peer.Wars[0],peer.NationById(0));
        Check(warm.Id == cold.Id && warm.Id == "defensive",
            "hourly posture cannot retain yesterday's offensive readiness across a cold snapshot boundary");
    }
}
