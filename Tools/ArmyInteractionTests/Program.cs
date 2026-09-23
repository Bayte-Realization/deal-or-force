using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class Program
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    static string Snapshot(Army army) => JsonSerializer.Serialize(army, new JsonSerializerOptions { IncludeFields = true });

    static void Main(string[] args)
    {
        var c = Campaign.CreateBootstrap(20260906);
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR");
        c.ConfigureSinglePlayer(rus.Id);
        var own = c.Armies.First(a => a.NationId == rus.Id);
        var foreign = c.Armies.First(a => a.NationId == ukr.Id);
        Check(ArmyInteraction.CanSelect(c, own), "own division selectable");
        Check(!ArmyInteraction.CanSelect(c, foreign), "foreign division not selectable");
        Check(!ArmyInteraction.CanSelect(c, null) && !ArmyInteraction.CanSelect(null, own), "empty context rejected");
        c.AddHumanSeat(ukr.Id);
        Check(!ArmyInteraction.CanSelect(c, foreign), "other human player's division not selectable");
        c.TakeSeat(1);
        Check(!ArmyInteraction.CanSelect(c, own) && ArmyInteraction.CanSelect(c, foreign), "selection authority follows local seat");
        c.TakeSeat(0);
        rus.IsHuman = false;
        Check(!ArmyInteraction.CanSelect(c, own), "observer cannot select former country divisions");
        rus.IsHuman = true;

        var before = Snapshot(foreign);
        ICampaignCommand[] rejected = {
            new MoveArmyCommand { IssuerId = rus.Id, ArmyId = foreign.Id, TargetProvinceId = 1 },
            new SetArmyOrderCommand { IssuerId = rus.Id, ArmyId = foreign.Id, Order = ArmyOrder.Spearhead },
            new SetArmyStanceCommand { IssuerId = rus.Id, ArmyId = foreign.Id, Stance = ArmyStance.Aggressive },
            new SetArmyReinforcementPriorityCommand { IssuerId = rus.Id, ArmyId = foreign.Id, Priority = ReinforcementPriority.High },
            new SetArmyPlanExecutionCommand { IssuerId = rus.Id, ArmyId = foreign.Id, Active = false },
            new HaltArmyCommand { IssuerId = rus.Id, ArmyId = foreign.Id },
            new DetachArmyFromPlanCommand { IssuerId = rus.Id, ArmyId = foreign.Id },
            new ReturnArmyToPlanCommand { IssuerId = rus.Id, ArmyId = foreign.Id },
            new SupportAttackCommand { IssuerId = rus.Id, ArmyId = foreign.Id },
            new StrategicRedeploymentCommand { IssuerId = rus.Id, ArmyId = foreign.Id, TargetProvinceId = 1 },
            new AssignArmyGroupCommand { IssuerId = rus.Id, ArmyId = foreign.Id, ArmyGroupId = -1 },
            new SplitArmyCommand { IssuerId = rus.Id, ArmyId = foreign.Id }
        };
        foreach (var command in rejected)
        {
            command.Apply(c);
            Check(Snapshot(foreign) == before, command.GetType().Name + " leaves foreign division unchanged");
        }
        new SetArmyStanceCommand { IssuerId = ukr.Id, ArmyId = foreign.Id, Stance = ArmyStance.Cautious }.Apply(c);
        Check(foreign.Stance == ArmyStance.Cautious, "AI/remote issuer still controls its own division");

        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(Globality.EarthData.States, Globality.EarthData.Control,
            Globality.EarthData.Width, Globality.EarthData.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        if (args.Contains("--offensive-only")) { OffensivePlanTests.Run(c); return; }
        if (args.Contains("--pocket-only")) { PocketTests.Run(map); return; }
        if (args.Contains("--loss-only")) { LossTests.Run(map); return; }
        if (args.Contains("--capital-only")) { CapitalTests.Run(map); return; }
        if (args.Contains("--options-only")) { CampaignOptionsTests.Run(map); return; }
        if (args.Contains("--colony-only")) { ColonisationTests.Run(map); return; }
        var war = c.Wars.First(w => !w.Ended && w.Involves(rus.Id) && w.Involves(ukr.Id));
        c.Wars.RemoveAll(w => w != war); c.Battles.Clear(); c.Armies.Clear();
        var border = BorderFronts.Candidates(c, rus.Id, ukr.Id);
        var start = border.First(p => map.Neighbors[p].Any(n => c.ProvinceController[n] == ukr.MapIndex &&
            map.Neighbors[n].Any(f => f != p && c.ProvinceController[f] == ukr.MapIndex)));
        var target = map.Neighbors[start].First(n => c.ProvinceController[n] == ukr.MapIndex &&
            map.Neighbors[n].Any(f => f != start && c.ProvinceController[f] == ukr.MapIndex));
        var flank = map.Neighbors[target].First(f => f != start && c.ProvinceController[f] == ukr.MapIndex);
        own.ProvinceId = start; own.StateMapIndex = map.ProvinceToState[start];
        (own.Latitude, own.Longitude) = map.CenterOf(start);
        own.Divisions = 1; own.Organisation = 1; own.Strength = 1; own.SupplyLevel = 1; own.ReadyDay = 0;
        foreign.ProvinceId = flank; foreign.Divisions = 200; foreign.OrderKind = ArmyOrder.Manual;
        foreign.AssignedWarId = -1;
        c.Armies.Add(own); c.Armies.Add(foreign);
        war.FrontDirty = false; war.LastFrontDay = c.ElapsedDays;
        var march = new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = target };
        march.Apply(c);
        Check(march.Result == ArmyMoveResult.AttackIssued, "RMB order to empty hostile province accepted");
        for (var day = 0; day < 60 && own.ProvinceId != target; day++)
        {
            MilitaryOperations.Tick(c);
            Warfare.Tick(c);
        }
        Check(own.ProvinceId == start && c.ProvinceController[target] == ukr.MapIndex,
            "strong neighboring formations cover an empty province under current zone-of-control rules");
        // Do not turn a pathfinding regression into a demand to bypass defensive cover.
        // The same explicit order must resume once the covering force leaves.
        c.Armies.Remove(foreign);
        for (var day = 0; day < 60 && own.ProvinceId != target; day++)
        { MilitaryOperations.Tick(c); Warfare.Tick(c); }
        Check(own.ProvinceId == target && c.ProvinceController[target] == rus.MapIndex,
            "explicit order reaches an unopposed hostile province after cover is removed");
        c.Armies.Add(foreign);
        Check(!c.Battles.Any(b => b.ProvinceId == target), "unopposed entry creates no fictitious battle");

        // A direct route must survive a front refresh instead of being silently reassigned.
        own.ReadyDay = 0;
        var next = flank;
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = next }.Apply(c);
        var ordered = Snapshot(own);
        FrontPlanner.RefreshAssignments(c, war);
        Check(Snapshot(own) == ordered, "front refresh does not replace an explicit RMB order");

        // A queued hostile-to-friendly leg starts at the previous destination, not the
        // last friendly staging province, and cannot execute before the hostile leg.
        var tail = own.PlannedRoute.Last();
        var prefix = own.PlannedRoute.ToArray();
        own.MoveProgress = 0.25f;
        var append = new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = target, AppendWaypoint = true };
        append.Apply(c);
        Check(append.Result == ArmyMoveResult.AttackIssued && own.PlannedRoute.Take(prefix.Length).SequenceEqual(prefix) &&
            own.PlannedRoute[prefix.Length] == target && map.Neighbors[tail].Contains(target), "queued return leg follows hostile waypoint");
        Check(own.Route.Count == 0 && own.MoveProgress == 0.25f, "appending does not skip hostile leg or reset movement progress");

        // Support orders never walk into a gap or follow their partner after capture.
        c.SetProvinceController(next, (ushort)ukr.MapIndex);
        foreign.ProvinceId = map.Neighbors[next].First(p => p != target);
        own.ProvinceId = target; own.ReadyDay = 0;
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = next }.Apply(c);
        own.SupportOnly = true;
        war.FrontDirty = false; war.LastFrontDay = c.ElapsedDays;
        for (var i = 0; i < 8; i++) Warfare.Tick(c);
        Check(own.ProvinceId == target && c.ProvinceController[next] == ukr.MapIndex, "support alone does not occupy empty ground");
        c.SetProvinceController(next, (ushort)rus.MapIndex);
        Warfare.Tick(c);
        Check(own.ProvinceId == target && own.Route.Count == 0 && !own.PlanActive, "support stays behind when target becomes friendly");
        own.SupportOnly = true;
        var friendly = new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = next };
        friendly.Apply(c);
        Check(friendly.Result == ArmyMoveResult.Issued && !own.SupportOnly, "ordinary march replaces prior support mode");
        for (var i = 0; i < 30 && own.ProvinceId != next; i++) MilitaryOperations.Tick(c);
        Check(own.ProvinceId == next && own.OrderKind == ArmyOrder.Manual, "friendly march completes and stops at destination");

        // A real defender still requires combat; removing invisible cover is not noclip.
        c.SetProvinceController(target, (ushort)ukr.MapIndex);
        foreign.ProvinceId = target; foreign.Divisions = 20; foreign.Organisation = 1; foreign.Strength = 1;
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = target }.Apply(c);
        own.ReadyDay = 0;
        Warfare.Tick(c);
        Check(own.ProvinceId != target && c.Battles.Any(b => b.ProvinceId == target), "defended province starts a battle, not free entry");
        var neutral = c.NationByTag("POL");
        c.SetProvinceController(target, (ushort)neutral.MapIndex);
        var forbidden = new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = target };
        forbidden.Apply(c);
        Check(forbidden.Result == ArmyMoveResult.TargetNotControlled, "neutral territory remains inaccessible without war");

        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "army-tests"), "army-tests");
        Check(restored.Success, "campaign snapshot round trip succeeds");
        var restoredArmy = restored.Campaign.Armies.Find(a => a.Id == own.Id);
        Check(restoredArmy.DirectOrder == own.DirectOrder && restoredArmy.PlannedRoute.SequenceEqual(own.PlannedRoute),
            "explicit order authority and corridor survive save/network snapshot");

        c.Battles.Clear(); own.BattleId = -1; foreign.BattleId = -1;
        own.ReadyDay = 0; own.Organisation = 1; own.Strength = 1; own.Divisions = 1;
        foreign.ReadyDay = 0; foreign.ProvinceId = target; foreign.Divisions = 1; foreign.Strength = 1; foreign.Organisation = 0.01f;
        c.SetProvinceController(target, (ushort)ukr.MapIndex);
        var retreat = map.Neighbors[target].First(p => p != own.ProvinceId);
        c.SetProvinceController(retreat, (ushort)ukr.MapIndex);
        var supportingFrom = own.ProvinceId;
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = target }.Apply(c);
        new SupportAttackCommand { IssuerId = rus.Id, ArmyId = own.Id }.Apply(c);
        Warfare.Tick(c);
        Check(c.Battles.Any(b => b.Ended && b.WinnerId == rus.Id), "support-only battle can break defenders");
        Check(own.ProvinceId == supportingFrom && c.ProvinceController[target] == ukr.MapIndex && !own.PlanActive,
            "support-only victory does not change territorial control or start another attack");
        Check(foreign.ProvinceId != target, "broken defender retreats even without occupation");

        c.Battles.Clear(); own.BattleId = -1; foreign.BattleId = -1;
        own.ReadyDay = 0; own.Organisation = 1;
        foreign.ProvinceId = target; foreign.Organisation = 0.01f; foreign.ReadyDay = 0;
        var assault = new Army { Id = 99001, NationId = rus.Id, ProvinceId = supportingFrom, Divisions = 1, ReadyDay = 0 };
        c.Armies.Add(assault);
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = target }.Apply(c);
        new SupportAttackCommand { IssuerId = rus.Id, ArmyId = own.Id }.Apply(c);
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = assault.Id, TargetProvinceId = target }.Apply(c);
        Warfare.Tick(c);
        Check(assault.ProvinceId == target && own.ProvinceId == supportingFrom && c.ProvinceController[target] == rus.MapIndex,
            "mixed attack occupies with assault division while support stays behind");
        c.Armies.Remove(assault);

        c.Battles.Clear(); own.BattleId = -1; foreign.BattleId = -1;
        own.ProvinceId = start; own.ReadyDay = 0; own.Organisation = 1;
        c.SetProvinceController(start, (ushort)rus.MapIndex);
        c.SetProvinceController(target, (ushort)rus.MapIndex);
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = target }.Apply(c);
        c.SetProvinceController(target, (ushort)ukr.MapIndex);
        MilitaryOperations.Tick(c);
        Check(own.DirectOrder && own.OrderProvinceId == target && own.Route.Count == 0 && own.PlannedRoute.Count > 0,
            "enemy taking a friendly marching step hands the original corridor to combat");
        foreign.ProvinceId = retreat;
        c.Armies.Remove(foreign); // This case isolates route interruption, not defensive coverage.
        for (var i = 0; i < 60 && own.ProvinceId != target; i++) { MilitaryOperations.Tick(c); Warfare.Tick(c); }
        Check(own.ProvinceId == target, "interrupted friendly march can retake newly hostile destination");
        c.Armies.Add(foreign);

        own.ProvinceId = start; own.ReadyDay = 0; c.Battles.Clear(); own.BattleId = -1; foreign.BattleId = -1;
        c.SetProvinceController(target, (ushort)ukr.MapIndex);
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = target }.Apply(c);
        c.SetProvinceController(target, (ushort)neutral.MapIndex);
        Warfare.Tick(c);
        Check(own.OrderKind == ArmyOrder.Manual && !own.PlanActive && own.OrderProvinceId < 0 &&
            own.LastMoveResult == ArmyMoveResult.RouteInterrupted, "neutralised objective stops direct attack instead of choosing an unrelated enemy province");

        c.SetProvinceController(target, (ushort)ukr.MapIndex);
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = target, SupportAttack = true }.Apply(c);
        Check(own.SupportOnly && own.DirectOrder, "support mode is part of the validated movement command");
        var supportCorridor = own.PlannedRoute.ToArray();
        new MoveArmyCommand { IssuerId = rus.Id, ArmyId = own.Id, TargetProvinceId = -1 }.Apply(c);
        Check(own.SupportOnly && own.PlannedRoute.SequenceEqual(supportCorridor), "rejected move preserves prior support mode and corridor");

        c.Fleets.Clear();
        var fleet = new Fleet { Id = 9001, NationId = rus.Id, Hulls = 10, Latitude = 42, Longitude = 179, ReadyDay = 0 };
        c.Fleets.Add(fleet);
        new SetFleetOrderCommand { IssuerId = ukr.Id, FleetId = fleet.Id, HasDestination = true,
            Latitude = 0, Longitude = 0, Mission = FleetMission.Patrol }.Apply(c);
        Check(!fleet.HasOrder && fleet.Mission == FleetMission.Anchored, "foreign fleet command rejected");
        new SetFleetOrderCommand { IssuerId = rus.Id, FleetId = fleet.Id, HasDestination = true,
            Latitude = 42, Longitude = -179, Mission = FleetMission.Patrol }.Apply(c);
        for (var i = 0; i < 30 && fleet.HasOrder; i++) NavalOperations.Tick(c);
        Check(!fleet.HasOrder && fleet.Latitude == 42 && fleet.Longitude == -179,
            "fleet follows explicit sea order across longitude seam");
        RecruitmentFogAudit.Run(c, own, foreign, start, target, war);
        DiagnosticLogTests.Run(c);
        ProvinceVisionTests.Run(c);
        OffensivePlanTests.Run(c);
        PocketTests.Run(map);
        LossTests.Run(map);
        CapitalTests.Run(map);
        CampaignOptionsTests.Run(map);
        ColonisationTests.Run(map);
    }
}
