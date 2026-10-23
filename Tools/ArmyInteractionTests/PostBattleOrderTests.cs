using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class PostBattleOrderTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL post-battle orders: " + label);
        Console.WriteLine("PASS post-battle orders: " + label);
    }

    static MoveArmyCommand Order(Campaign c, int target, bool append = false, bool support = false)
    {
        var order = new MoveArmyCommand { IssuerId = 1, ArmyId = 9001,
            TargetProvinceId = target, AppendWaypoint = append, SupportAttack = support };
        c.Commands.Enqueue(order); c.Commands.ApplyAll(c); return order;
    }

    public static void Run()
    {
        var c = MoveBattlePreflightTests.Fixture();
        var a = c.Armies[1];
        var before = CampaignFingerprint.Of(c);
        Check(Order(c, 3).Result == ArmyMoveResult.QueuedAfterBattle, "map order accepted on pause");
        Check(CampaignFingerprint.Of(c) != before, "future destinations participate in authoritative fingerprint");
        var first = CampaignFingerprint.Of(c);
        Order(c, 4, true, true);
        Check(a.PostBattleWaypoints.SequenceEqual(new[] { 3, 4 }) && a.PostBattleSupportAttack,
            "Shift appends ordered waypoints and support mode is preserved");
        Check(CampaignFingerprint.Of(c) != first, "waypoint order and support mode affect fingerprint");
        Check(a.InBattle && a.ProvinceId == 2 && a.OrderProvinceId == 2 && !c.Battles[0].Ended,
            "queue leaves combat, active objective and position unchanged");
        var halt = new HaltArmyCommand { IssuerId = 1, ArmyId = a.Id }; halt.Apply(c);
        Check(halt.Halted && a.PostBattleWaypoints.Count == 0 && a.InBattle && a.AssignedWarId == 883 && a.PlanActive,
            "Halt cancels only future orders, not the live battle");
        Order(c, 4); Order(c, 2);
        Check(a.PostBattleWaypoints.Count == 0 && a.InBattle, "clicking own position cancels queue without retreat");

        Order(c, 3);
        for (var i = 1; i < BattleMovementOrders.MaxWaypoints; i++) Order(c, i % 2 == 0 ? 3 : 4, true);
        var full = CampaignFingerprint.Of(c);
        Check(Order(c, 4, true).Result == ArmyMoveResult.QueueFull && CampaignFingerprint.Of(c) == full,
            "bounded queue refuses overflow without changing authoritative state");
        Order(c, 4);
        Check(a.PostBattleWaypoints.SequenceEqual(new[] { 4 }), "normal click replaces rather than grows queue");
        c.Battles[0].Ended = true; a.BattleId = -1;
        a.ProvinceId = 3; a.StateMapIndex = 3003; a.ReadyDay = c.ElapsedDays + 2;
        MilitaryOperations.Tick(c);
        Check(a.PostBattleWaypoints.Count == 1 && a.Route.Count == 0 && a.ProvinceId == 3,
            "regrouping does not consume pending order or move early");
        a.ReadyDay = c.ElapsedDays;
        MilitaryOperations.Tick(c);
        Check(a.PostBattleWaypoints.Count == 0 && a.OrderProvinceId == 4 && a.DirectOrder &&
            a.PlannedRoute.SequenceEqual(new[] { 4 }) && a.ProvinceId == 3,
            "resume recomputes route from actual post-battle position and marches normally");

        c = MoveBattlePreflightTests.Fixture(); a = c.Armies[1];
        Order(c, 4); c.Battles[0].Ended = true; a.BattleId = -1;
        c.SetProvinceController(4, 502);
        MilitaryOperations.Tick(c);
        Check(a.PostBattleWaypoints.Count == 0 && a.LastMoveResult == ArmyMoveResult.RouteInterrupted &&
            a.Route.Count == 0 && a.ProvinceId == 2 && c.ProvinceController[4] == 502,
            "changed neutral border refuses stale future order without capture or endless retry");

        c = MoveBattlePreflightTests.Fixture(); a = c.Armies[1]; Order(c, 4, support: true);
        var folder = Path.Combine(Path.GetTempPath(), "dof-post-battle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            c.ConfigureSinglePlayer(1);
            SaveSystem.Save(folder, "queued", c, "post-battle");
            var saved = SaveSystem.Load(folder, "queued", "post-battle");
            Check(saved.Success && saved.Campaign.Armies[1].PostBattleWaypoints.SequenceEqual(new[] { 4 }) &&
                saved.Campaign.Armies[1].PostBattleSupportAttack && saved.Campaign.Armies[1].InBattle,
                "compressed save/load retains pending order, mode and live assignment");
        }
        finally { Directory.Delete(folder, true); }
        var legacy = JsonSerializer.Deserialize<Army>("{\"Id\":5}", new JsonSerializerOptions { IncludeFields = true });
        Check(legacy.PostBattleWaypoints.Count == 0 && !legacy.PostBattleSupportAttack,
            "old Army records default to no pending order");

        c = MoveBattlePreflightTests.Fixture(); a = c.Armies[1];
        a.ProvinceId = 3;
        var organisation = a.Organisation; var ready = a.ReadyDay;
        Warfare.ReconcileBattleAssignments(c);
        Check(!a.InBattle && !c.Battles[0].Defenders.Contains(a.Id) && a.ProvinceId == 3 &&
            a.Organisation == organisation && a.ReadyDay == ready,
            "old remote defender assignment is released without teleportation, healing or recovery penalty");

        FreshReservesPreventPrematureDefeat();
        NeighbourMustActuallyMarch();
    }

    static void FreshReservesPreventPrematureDefeat()
    {
        var c = MoveBattlePreflightTests.Fixture();
        c.Defines.FrontStaffCallsUpIdle = false;
        c.Defines.BaseOrganisationDamage = c.Defines.MinOrganisationDamage = c.Defines.MaxOrganisationDamage = 1f;
        c.Defines.CombatWidth = UnitTypeCatalog.Get("line_infantry").CombatWidth;
        foreach (var army in c.Armies) { army.Divisions = 1; army.Organisation = .9f; }
        for (var id = 0; id < 2; id++)
        {
            var army = new Army { Id = 9100 + id, NationId = id, ProvinceId = id + 1,
                StateMapIndex = 3001 + id, BattleId = 777, AssignedWarId = 883,
                OrderKind = id == 0 ? ArmyOrder.AdvanceFront : ArmyOrder.DefendFront,
                OrderProvinceId = 2, Divisions = 1, Organisation = .89f, Strength = .8f,
                Equipment = .6f, Personnel = 15000 };
            c.Armies.Add(army);
            (id == 0 ? c.Battles[0].Attackers : c.Battles[0].Defenders).Add(army.Id);
        }
        Warfare.Tick(c);
        var b = c.Battles[0];
        Check(!b.Ended && b.LastAttackerLine.Count == 1 && b.LastDefenderLine.Count == 1 &&
            c.Armies[2].Organisation > c.Defines.BattleBreakOrganisation &&
            c.Armies[3].Organisation > c.Defines.BattleBreakOrganisation,
            "spent first lines do not lose the battle while fresh reserves remain");
        c.Defines.BaseOrganisationDamage = c.Defines.MinOrganisationDamage = c.Defines.MaxOrganisationDamage = .001f;
        Warfare.Tick(c);
        Check(b.LastAttackerLine.Contains(9100) && b.LastDefenderLine.Contains(9101),
            "fresh reserves replace spent formations on the next combat round");
    }

    static void NeighbourMustActuallyMarch()
    {
        var c = MoveBattlePreflightTests.Fixture();
        c.Defines.FrontStaffCallsUpIdle = false;
        c.Defines.BaseOrganisationDamage = c.Defines.MinOrganisationDamage = c.Defines.MaxOrganisationDamage = .001f;
        var reserve = new Army { Id = 9300, NationId = 1, ProvinceId = 3, StateMapIndex = 3003,
            Divisions = 1, Organisation = .9f, OrderKind = ArmyOrder.ReserveFront, AssignedWarId = 883 };
        c.Armies.Add(reserve);
        var move = new MoveArmyCommand { IssuerId = 1, ArmyId = reserve.Id, TargetProvinceId = 2 };
        move.Apply(c);
        Warfare.Tick(c);
        Check(move.Result == ArmyMoveResult.Issued && !reserve.InBattle && reserve.ProvinceId == 3 &&
            reserve.Route.SequenceEqual(new[] { 2 }),
            "a real reinforcement order starts a march, not a remote battle assignment");
        for (var i = 0; i < 64 && reserve.ProvinceId != 2; i++) MilitaryOperations.Tick(c);
        Check(reserve.ProvinceId == 2 && !reserve.InBattle,
            "ordinary movement reaches the attacked province before the reserve can fight");
        Warfare.Tick(c);
        Check(reserve.InBattle && c.Battles[0].Defenders.Contains(reserve.Id),
            "the physically arrived reserve joins the local defence");
    }
}
