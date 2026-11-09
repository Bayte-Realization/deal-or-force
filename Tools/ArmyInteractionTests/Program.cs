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
        if (args.Contains("--front-staff-mutation-only")) { FrontStaffMutationTests.Run(); return; }
        if (args.Contains("--front-staff-save")) { FrontStaffMutationTests.Replay(args); return; }
        if (args.Length == 0) FrontStaffMutationTests.Run();
        if (args.Contains("--equipment-replenishment-only")) { EquipmentReplenishmentTests.Run(); return; }
        if (args.Length == 0) EquipmentReplenishmentTests.Run();
        if (args.Contains("--four-systems-only")) { FourSystemsIntegrationTests.RunAll(); return; }
        if (args.Length == 0) FourSystemsIntegrationTests.RunAll();
        if (args.Contains("--post-battle-orders-only")) { PostBattleOrderTests.Run(); return; }
        if (args.Length == 0) PostBattleOrderTests.Run();
        if (args.Contains("--battle-reinforcement-only")) { BattleReinforcementTests.RunFixture(); return; }
        if (args.Contains("--military-access-only")) { MilitaryAccessTests.Run(); return; }
        if (args.Contains("--insurgent-supply-only")) { InsurgentSupplyTests.Run(); return; }
        if (args.Length == 0) MilitaryAccessTests.Run();
        if (args.Contains("--capital-loss-only")) { CapitalLossTests.Run(); return; }
        if (args.Length == 0) CapitalLossTests.Run();
        if (args.Contains("--peace-beneficiary-only")) { PeaceBeneficiaryTests.Run(); return; }
        if (args.Length == 0) PeaceBeneficiaryTests.Run();
        if (args.Contains("--peace-battle-assignment-only")) { PeaceBattleAssignmentTests.Run(); return; }
        if (args.Length == 0) PeaceBattleAssignmentTests.Run();
        if (args.Contains("--partial-front-only")) { PartialFrontWithdrawalTests.Run(); return; }
        if (args.Length == 0) PartialFrontWithdrawalTests.Run();
        if (args.Contains("--front-disengagement-performance-only")) { FrontDisengagementPerformanceTests.Run(); return; }
        if (args.Length == 0) FrontDisengagementPerformanceTests.Run();
        if (args.Contains("--friendly-route-search-only")) { FriendlyRouteSearchTests.Run(); return; }
        if (args.Length == 0) FriendlyRouteSearchTests.Run();
        if (args.Contains("--friendly-route-components-only")) { FriendlyRouteComponentsTests.Run(); return; }
        if (args.Length == 0) FriendlyRouteComponentsTests.Run();
        if (args.Contains("--enemy-readiness-ai-only")) { EnemyReadinessAiTests.Run(); return; }
        if (args.Length == 0) EnemyReadinessAiTests.Run();
        if (args.Contains("--military-condition-readout-only")) { MilitaryConditionReadoutTests.Run(); return; }
        if (args.Length == 0) MilitaryConditionReadoutTests.Run();
        if (args.Contains("--move-battle-preflight-only")) { MoveBattlePreflightTests.Run(); return; }
        if (args.Length == 0) MoveBattlePreflightTests.Run();
        if (args.Contains("--open-advance-only")) { OpenAdvanceTests.Run(); return; }
        if (args.Contains("--front-cache-isolation-only")) { FrontCacheIsolationTests.Run(); return; }
        if (args.Contains("--march-continuity-only")) { MarchContinuityTests.Run(); return; }
        if (args.Contains("--combat-attrition-only")) { CombatAttritionRefactorTests.Run(); return; }
        if (args.Contains("--coalition-combat-only")) { CoalitionCombatTests.Run(); return; }
        if (args.Contains("--assault-readiness-only")) { AssaultReadinessTests.Run(); return; }
        if (args.Contains("--ai-front-recovery-only")) { AiFrontRecoveryTests.Run(); return; }
        if (args.Contains("--advance-rotation-only")) { AdvanceTargetTests.Run(); return; }
        if (args.Contains("--mobilisation-only")) { MobilisationTests.Run(); return; }
        if (args.Contains("--plan-erasing-only")) { PlanErasingTests.Run(); return; }
        PlanErasingTests.Run();
        if (args.Contains("--features-only")) { FeatureTests.Run(); return; }
        if (args.Contains("--battle-readout-only")) { BattleReadoutTests.Run(); return; }
        if (args.Contains("--withdrawal-only")) { BattleWithdrawalTests.RunFixture(); return; }
        if (args.Contains("--battle-reinforcement-only")) { BattleReinforcementTests.RunFixture(); return; }
        BattleReinforcementTests.RunFixture();
        if (args.Contains("--advance-layout-only")) { AdvanceLayoutTests.Run(); return; }
        if (args.Contains("--peace81-only")) { Peace81Tests.Run(); return; }
        if (args.Contains("--peace-offer-throttle-only")) { PeaceOfferThrottleTests.Run(); return; }
        if (args.Contains("--peace-coalition-only")) { PeaceCoalitionTests.Run(); return; }
        OpenAdvanceTests.Run();
        FrontCacheIsolationTests.Run();
        MarchContinuityTests.Run();
        var c = Campaign.CreateBootstrap(20260906);
        // These isolated subsystem fixtures count steps in days, without running the
        // world's economy/AI. Do not silently inherit the campaign's hourly UI clock.
        c.Defines.HoursPerTick = 24;
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
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        if (args.Contains("--map-peace-only")) { MapAndPeaceRegressionTests.Run(map); return; }
        if (args.Contains("--country-merges-map-only")) { CountryMergeTests.Run(c); return; }
        if (args.Contains("--support-only")) { SupportTests.Run(c); return; }
        if (args.Contains("--airwar-only")) { AirWarTests.Run(c); return; }
        if (args.Contains("--long-range-only")) { LongRangeTests.Run(c); return; }
        if (args.Contains("--militia-only")) { MilitiaSeatTests.Run(c); return; }
        if (args.Contains("--gaza-only")) { HeldGroundTests.Run(c); return; }
        if (args.Contains("--ally-ground-only")) { AllyGroundTests.Run(c); return; }
        if (args.Contains("--lost-land-only")) { LostLandTests.Run(c); return; }
        if (args.Contains("--relations-only")) { RelationsTests.Run(c); return; }
        if (args.Contains("--restraint-only")) { RestraintTests.Run(c); return; }
        if (args.Contains("--incitement-only")) { IncitementTests.Run(c); return; }
        if (args.Contains("--secession-name-only")) { SecessionNameTests.Run(c); return; }
        if (args.Contains("--capital-point-only")) { CapitalPointTests.Run(c); return; }
        if (args.Contains("--overrun-only")) { OverrunWarTests.Run(c); return; }
        if (args.Contains("--ai-diplomacy-only")) { AiDiplomacyTests.Run(c); return; }
        if (args.Contains("--save-seed-mature-only"))
        {
            c.IsPaused = false;
            for (var day = 0; day < 365; day++) c.TickDay();
            c.IsPaused = true;
            SaveAndSeedTests.Run(c);
            return;
        }
        if (args.Contains("--save-seed-only")) { SaveAndSeedTests.Run(c); return; }
        if (args.Contains("--quiet-war-only")) { QuietWarTests.Run(c); return; }
        if (args.Contains("--opening-population-only")) { OpeningPopulationTests.Run(c); return; }
        if (args.Contains("--peace-tidy-only")) { PeaceTidyTests.Run(c); return; }
        if (args.Contains("--peace-maritime-only")) { MaritimePeaceTests.Run(c); return; }
        if (args.Contains("--peace-real-only")) { PeaceCoalitionTests.RunReal(c); return; }
        if (args.Contains("--movement82-only")) { Movement82Tests.Run(c); return; }
        if (args.Contains("--ai-war-only")) { AiWarTests.Run(map); return; }
        if (args.Contains("--nuclear-strike-only")) { NuclearStrikeTests.Run(map); return; }
        if (args.Contains("--expeditions-only")) { ExpeditionTests.Run(map); return; }
        if (args.Contains("--backlog69-only")) { Backlog69Tests.Run(c); return; }
        if (args.Contains("--defence-only")) { DefensivePlanTests.Run(c); return; }
        if (args.Contains("--radicalism-only")) { RadicalismTests.Run(c); return; }
        if (args.Contains("--offensive-only")) { OffensivePlanTests.Run(c); return; }
        if (args.Contains("--pocket-only")) { PocketTests.Run(map); return; }
        if (args.Contains("--stranded-garrison-only")) { StrandedGarrisonTests.RunRising(map); return; }
        if (args.Contains("--pocket-stores-only")) { StrandedGarrisonTests.RunPocket(map); return; }
        if (args.Contains("--loss-only")) { LossTests.Run(map); return; }
        if (args.Contains("--capital-only")) { CapitalTests.Run(map); return; }
        if (args.Contains("--options-only")) { CampaignOptionsTests.Run(map); return; }
        if (args.Contains("--colony-only")) { ColonisationTests.Run(map); return; }
        if (args.Contains("--subject-only")) { SubjectIdentityTests.Run(map); return; }
        if (args.Contains("--submission-only")) { SubmissionTests.Run(map); return; }
        if (args.Contains("--agreement-only")) { AgreementTests.Run(map); return; }
        if (args.Contains("--research-only")) { TechnologyTests.Run(map); return; }
        if (args.Contains("--naval-only")) { NavalTests.Run(map); return; }
        if (args.Contains("--staff-only")) { StaffTests.Run(map); return; }
        if (args.Contains("--front-staff-only")) { FrontStaffTests.Run(map); return; }
        if (args.Contains("--modifiers-only")) { ModifierTests.Run(map); return; }
        if (args.Contains("--conflicts-only")) { ConflictTests.Run(map); return; }
        if (args.Contains("--separate-peace-only")) { SeparatePeaceTests.Run(map); return; }
        if (args.Contains("--whole-front-only")) { WholeFrontTests.Run(map); return; }
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
        float hourlyCoveredProgress = 0;
        foreach (var hours in new[] { 1, 24 })
        {
            c.Defines.HoursPerTick = hours;
            own.MoveProgress = 0;
            own.DirectOrder = false;
            Warfare.Tick(c);
            Check(own.ProvinceId == start && own.MoveProgress == 0,
                $"{hours}h: defensive cover blocks an autonomous gap exploit");
            own.DirectOrder = true;
            Warfare.Tick(c);
            var coveredProgress = own.MoveProgress;
            Check(own.ProvinceId == start && coveredProgress > 0,
                $"{hours}h: explicit order advances through cover without teleporting");
            if (hours == 1) hourlyCoveredProgress = coveredProgress;
            else Check(Math.Abs(coveredProgress - hourlyCoveredProgress * 24) < 0.00001f,
                "covered advance scales with elapsed hours rather than number of ticks");
            c.Armies.Remove(foreign);
            own.MoveProgress = 0;
            Warfare.Tick(c);
            Check(own.MoveProgress > coveredProgress && own.ProvinceId == start,
                $"{hours}h: covering force slows the same explicit order");
            c.Armies.Add(foreign);
        }
        c.Defines.HoursPerTick = 24;
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
        SubjectIdentityTests.Run(map);
        SubmissionTests.Run(map);
        AgreementTests.Run(map);
        TechnologyTests.Run(map);
        NavalTests.Run(map);
        StaffTests.Run(map);
        FrontStaffTests.Run(map);
        AiFrontRecoveryTests.Run();
        AssaultReadinessTests.Run();
        CombatAttritionRefactorTests.Run();
        CoalitionCombatTests.Run();
        ModifierTests.Run(map);
    }
}
