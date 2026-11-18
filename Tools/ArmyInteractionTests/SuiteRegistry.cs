using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using DealOrForce.Simulation;

public enum ArmySuitePhase
{
    PreCampaign,
    Campaign
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestSuiteAttribute : Attribute
{
    public string Flag { get; }
    public ArmySuitePhase Phase { get; set; } = ArmySuitePhase.Campaign;
    public int Order { get; set; } = 10000;
    public bool IncludeInDefaultRun { get; set; } = true;
    public string Description { get; set; } = "";

    public TestSuiteAttribute(string flag) => Flag = flag;
}

internal static class SuiteRegistry
{
    private sealed record PreSuite(string Flag, Action<string[]> Run, string Description);
    private sealed record CampaignSuite(string Flag, Action<Campaign, ProvinceTopology> Run, string Description);

    private static readonly List<PreSuite> PreSuites = new();
    private static readonly List<CampaignSuite> CampaignSuites = new();
    private static readonly List<(int Order, int Seq, bool AlwaysBeforeCampaign, Action Run)> PreSteps = new();
    private static readonly List<(int Order, int Seq, Action<Campaign, ProvinceTopology> Run)> CampaignDefaultSteps = new();
    private static readonly HashSet<string> RegisteredFlags = new(StringComparer.Ordinal);
    private static int _seq;
    private static bool _initialized;

    public static void RegisterPreCampaign(string flag, Action<string[]> run, bool includeInDefaultRun = false,
        int order = 10000, string description = "")
    {
        if (!RegisteredFlags.Add(flag)) return;
        PreSuites.Add(new PreSuite(flag, run, description));
        if (includeInDefaultRun) PreSteps.Add((order, ++_seq, false, () => run(Array.Empty<string>())));
    }

    public static void RegisterPreCampaignStep(int order, bool alwaysBeforeCampaign, Action run) =>
        PreSteps.Add((order, ++_seq, alwaysBeforeCampaign, run));

    public static void RegisterCampaign(string flag, Action<Campaign, ProvinceTopology> run, bool includeInDefaultRun = false,
        int order = 10000, string description = "")
    {
        if (!RegisteredFlags.Add(flag)) return;
        CampaignSuites.Add(new CampaignSuite(flag, run, description));
        if (includeInDefaultRun) CampaignDefaultSteps.Add((order, ++_seq, run));
    }

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        RegisterBuiltins();
        DiscoverAttributedSuites();
    }

    public static int Run(string[] args)
    {
        EnsureInitialized();
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("--list-suites"))
        {
            PrintHelp();
            return 0;
        }
        if (args.Length > 0)
        {
            foreach (var suite in PreSuites)
                if (args.Contains(suite.Flag)) { suite.Run(args); return 0; }

            var matchedCampaign = CampaignSuites.FirstOrDefault(s => args.Contains(s.Flag));
            if (matchedCampaign is null)
            {
                Console.Error.WriteLine("Unknown test selection: " + string.Join(" ", args));
                return 2;
            }
            foreach (var step in PreSteps.Where(s => s.AlwaysBeforeCampaign).OrderBy(s => s.Order).ThenBy(s => s.Seq))
                step.Run();
            var (c, map, _, _, _, _, _, _) = BuildBootstrapAndMap();
            matchedCampaign.Run(c, map);
            return 0;
        }

        foreach (var step in PreSteps.OrderBy(s => s.Order).ThenBy(s => s.Seq))
            step.Run();
        var (campaign, topology, own, foreign, startSourceRus, ukr, warBuilderMarker, _) = BuildBootstrapAndMap();
        _ = (startSourceRus, ukr, warBuilderMarker);
        RunDefaultCampaignTail(campaign, topology, own, foreign);
        foreach (var step in CampaignDefaultSteps.OrderBy(s => s.Order).ThenBy(s => s.Seq))
            step.Run(campaign, topology);
        return 0;
    }

    public static IReadOnlyList<string> AllFlags()
    {
        EnsureInitialized();
        return RegisteredFlags.OrderBy(f => f, StringComparer.Ordinal).ToArray();
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"ArmyInteractionTests registered suites ({RegisteredFlags.Count} flags):");
        foreach (var flag in AllFlags()) Console.WriteLine($"  {flag}");
    }

    private static void DiscoverAttributedSuites()
    {
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                var attrs = method.GetCustomAttributes<TestSuiteAttribute>(false).ToArray();
                if (attrs.Length == 0) continue;
                var parameters = method.GetParameters();
                foreach (var attr in attrs)
                {
                    if (attr.Phase == ArmySuitePhase.PreCampaign)
                    {
                        Action<string[]> runPre = parameters.Length == 0
                            ? _ => InvokeStatic(method, null)
                            : parameters.Length == 1 && parameters[0].ParameterType == typeof(string[])
                                ? a => InvokeStatic(method, new object[] { a })
                                : throw new InvalidOperationException($"Unsupported PreCampaign signature on {type.Name}.{method.Name}");
                        RegisterPreCampaign(attr.Flag, runPre, attr.IncludeInDefaultRun, attr.Order, attr.Description);
                    }
                    else
                    {
                        Action<Campaign, ProvinceTopology> runCamp = parameters.Length == 0
                            ? (_, _) => InvokeStatic(method, null)
                            : parameters.Length == 1 && parameters[0].ParameterType == typeof(Campaign)
                                ? (c, _) => InvokeStatic(method, new object[] { c })
                                : parameters.Length == 1 && parameters[0].ParameterType == typeof(ProvinceTopology)
                                    ? (_, m) => InvokeStatic(method, new object[] { m })
                                    : parameters.Length == 2 && parameters[0].ParameterType == typeof(Campaign) && parameters[1].ParameterType == typeof(ProvinceTopology)
                                        ? (c, m) => InvokeStatic(method, new object[] { c, m })
                                        : throw new InvalidOperationException($"Unsupported Campaign signature on {type.Name}.{method.Name}");
                        RegisterCampaign(attr.Flag, runCamp, attr.IncludeInDefaultRun, attr.Order, attr.Description);
                    }
                }
            }
        }
    }

    private static void InvokeStatic(MethodInfo method, object[] arguments)
    {
        try { method.Invoke(null, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static string Snapshot(Army army) => JsonSerializer.Serialize(army, new JsonSerializerOptions { IncludeFields = true });

    private static void RegisterBuiltins()
    {
        RegisterPreCampaign("--peacetime-army-only", _ => { PeacetimeArmyTests.Run(); });
        RegisterPreCampaign("--withdrawal-preview-only", _ => { BattleWithdrawalPreviewTests.Run(); });
        RegisterPreCampaign("--pocket-resistance-only", _ => { PocketResistanceTests.Run(); });
        RegisterPreCampaign("--pocket-rules-only", _ => { PocketRuleTests.Run(); });
        RegisterPreCampaign("--pocket-hunger-only", _ => { PocketRuleTests.RunHunger(); });
        RegisterPreCampaign("--partisans-only", _ => { PocketRuleTests.RunPartisans(); });
        RegisterPreCampaign("--front-staff-mutation-only", _ => { FrontStaffMutationTests.Run(); });
        RegisterPreCampaign("--front-staff-save", args => { FrontStaffMutationTests.Replay(args); });
        RegisterPreCampaign("--equipment-replenishment-only", _ => { EquipmentReplenishmentTests.Run(); });
        RegisterPreCampaign("--four-systems-only", _ => { FourSystemsIntegrationTests.RunAll(); });
        RegisterPreCampaign("--post-battle-orders-only", _ => { PostBattleOrderTests.Run(); });
        RegisterPreCampaign("--battle-reinforcement-only", _ => { BattleReinforcementTests.RunFixture(); });
        RegisterPreCampaign("--military-access-only", _ => { MilitaryAccessTests.Run(); });
        RegisterPreCampaign("--insurgent-supply-only", _ => { InsurgentSupplyTests.Run(); });
        RegisterPreCampaign("--capital-loss-only", _ => { CapitalLossTests.Run(); });
        RegisterPreCampaign("--peace-beneficiary-only", _ => { PeaceBeneficiaryTests.Run(); });
        RegisterPreCampaign("--peace-battle-assignment-only", _ => { PeaceBattleAssignmentTests.Run(); });
        RegisterPreCampaign("--partial-front-only", _ => { PartialFrontWithdrawalTests.Run(); });
        RegisterPreCampaign("--front-disengagement-performance-only", _ => { FrontDisengagementPerformanceTests.Run(); });
        RegisterPreCampaign("--friendly-route-search-only", _ => { FriendlyRouteSearchTests.Run(); });
        RegisterPreCampaign("--friendly-route-components-only", _ => { FriendlyRouteComponentsTests.Run(); });
        RegisterPreCampaign("--enemy-readiness-ai-only", _ => { EnemyReadinessAiTests.Run(); });
        RegisterPreCampaign("--military-condition-readout-only", _ => { MilitaryConditionReadoutTests.Run(); });
        RegisterPreCampaign("--move-battle-preflight-only", _ => { MoveBattlePreflightTests.Run(); });
        RegisterPreCampaign("--open-advance-only", _ => { OpenAdvanceTests.Run(); });
        RegisterPreCampaign("--front-cache-isolation-only", _ => { FrontCacheIsolationTests.Run(); });
        RegisterPreCampaign("--march-continuity-only", _ => { MarchContinuityTests.Run(); });
        RegisterPreCampaign("--combat-attrition-only", _ => { CombatAttritionRefactorTests.Run(); });
        RegisterPreCampaign("--coalition-combat-only", _ => { CoalitionCombatTests.Run(); });
        RegisterPreCampaign("--assault-readiness-only", _ => { AssaultReadinessTests.Run(); });
        RegisterPreCampaign("--ai-front-recovery-only", _ => { AiFrontRecoveryTests.Run(); });
        RegisterPreCampaign("--advance-rotation-only", _ => { AdvanceTargetTests.Run(); });
        RegisterPreCampaign("--mobilisation-only", _ => { MobilisationTests.Run(); });
        RegisterPreCampaign("--plan-erasing-only", _ => { PlanErasingTests.Run(); });
        RegisterPreCampaign("--features-only", _ => { FeatureTests.Run(); });
        RegisterPreCampaign("--battle-readout-only", _ => { BattleReadoutTests.Run(); });
        RegisterPreCampaign("--withdrawal-only", _ => { BattleWithdrawalTests.RunFixture(); });
        RegisterPreCampaign("--advance-layout-only", _ => { AdvanceLayoutTests.Run(); });
        RegisterPreCampaign("--peace81-only", _ => { Peace81Tests.Run(); });
        RegisterPreCampaign("--peace-offer-throttle-only", _ => { PeaceOfferThrottleTests.Run(); });
        RegisterPreCampaign("--peace-coalition-only", _ => { PeaceCoalitionTests.Run(); });

        RegisterPreCampaignStep(10, alwaysBeforeCampaign: false, () => { PeacetimeArmyTests.Run(); });
        RegisterPreCampaignStep(20, alwaysBeforeCampaign: false, () => { BattleWithdrawalPreviewTests.Run(); });
        RegisterPreCampaignStep(30, alwaysBeforeCampaign: false, () => { PocketResistanceTests.Run(); });
        RegisterPreCampaignStep(40, alwaysBeforeCampaign: false, () => { PocketRuleTests.Run(); });
        RegisterPreCampaignStep(50, alwaysBeforeCampaign: false, () => { FrontStaffMutationTests.Run(); });
        RegisterPreCampaignStep(60, alwaysBeforeCampaign: false, () => { EquipmentReplenishmentTests.Run(); });
        RegisterPreCampaignStep(70, alwaysBeforeCampaign: false, () => { FourSystemsIntegrationTests.RunAll(); });
        RegisterPreCampaignStep(80, alwaysBeforeCampaign: false, () => { PostBattleOrderTests.Run(); });
        RegisterPreCampaignStep(90, alwaysBeforeCampaign: false, () => { MilitaryAccessTests.Run(); });
        RegisterPreCampaignStep(100, alwaysBeforeCampaign: false, () => { CapitalLossTests.Run(); });
        RegisterPreCampaignStep(110, alwaysBeforeCampaign: false, () => { PeaceBeneficiaryTests.Run(); });
        RegisterPreCampaignStep(120, alwaysBeforeCampaign: false, () => { PeaceBattleAssignmentTests.Run(); });
        RegisterPreCampaignStep(130, alwaysBeforeCampaign: false, () => { PartialFrontWithdrawalTests.Run(); });
        RegisterPreCampaignStep(140, alwaysBeforeCampaign: false, () => { FrontDisengagementPerformanceTests.Run(); });
        RegisterPreCampaignStep(150, alwaysBeforeCampaign: false, () => { FriendlyRouteSearchTests.Run(); });
        RegisterPreCampaignStep(160, alwaysBeforeCampaign: false, () => { FriendlyRouteComponentsTests.Run(); });
        RegisterPreCampaignStep(170, alwaysBeforeCampaign: false, () => { EnemyReadinessAiTests.Run(); });
        RegisterPreCampaignStep(180, alwaysBeforeCampaign: false, () => { MilitaryConditionReadoutTests.Run(); });
        RegisterPreCampaignStep(190, alwaysBeforeCampaign: false, () => { MoveBattlePreflightTests.Run(); });
        RegisterPreCampaignStep(200, alwaysBeforeCampaign: true, () => { PlanErasingTests.Run(); });
        RegisterPreCampaignStep(210, alwaysBeforeCampaign: true, () => { BattleReinforcementTests.RunFixture(); });
        RegisterPreCampaignStep(220, alwaysBeforeCampaign: true, () => { OpenAdvanceTests.Run(); });
        RegisterPreCampaignStep(230, alwaysBeforeCampaign: true, () => { FrontCacheIsolationTests.Run(); });
        RegisterPreCampaignStep(240, alwaysBeforeCampaign: true, () => { MarchContinuityTests.Run(); });

        RegisterCampaign("--map-peace-only", (c, map) => { MapAndPeaceRegressionTests.Run(map); });
        RegisterCampaign("--country-merges-map-only", (c, map) => { CountryMergeTests.Run(c); });
        RegisterCampaign("--sea-lift-only", (c, map) => { SeaLiftTests.Run(c); });
        RegisterCampaign("--support-only", (c, map) => { SupportTests.Run(map); });
        RegisterCampaign("--long-range-movement-only", (c, map) => { LongRangeMovementTests.Run(map); });
        RegisterCampaign("--airwar-only", (c, map) => { AirWarTests.Run(c); });
        RegisterCampaign("--long-range-only", (c, map) => { LongRangeTests.Run(c); SupportTests.Run(map); LongRangeMovementTests.Run(map); });
        RegisterCampaign("--militia-only", (c, map) => { MilitiaSeatTests.Run(c); });
        RegisterCampaign("--gaza-only", (c, map) => { HeldGroundTests.Run(c); });
        RegisterCampaign("--ally-ground-only", (c, map) => { AllyGroundTests.Run(c); });
        RegisterCampaign("--lost-land-only", (c, map) => { LostLandTests.Run(c); });
        RegisterCampaign("--relations-only", (c, map) => { RelationsTests.Run(c); });
        RegisterCampaign("--restraint-only", (c, map) => { RestraintTests.Run(c); });
        RegisterCampaign("--incitement-only", (c, map) => { IncitementTests.Run(c); });
        RegisterCampaign("--secession-name-only", (c, map) => { SecessionNameTests.Run(c); });
        RegisterCampaign("--capital-point-only", (c, map) => { CapitalPointTests.Run(c); });
        RegisterCampaign("--overrun-only", (c, map) => { OverrunWarTests.Run(c); });
        RegisterCampaign("--ai-diplomacy-only", (c, map) => { AiDiplomacyTests.Run(c); });
        RegisterCampaign("--save-seed-mature-only", (c, map) => { c.IsPaused = false; for (var day = 0; day < 365; day++) c.TickDay(); c.IsPaused = true; SaveAndSeedTests.Run(c); });
        RegisterCampaign("--save-seed-only", (c, map) => { SaveAndSeedTests.Run(c); });
        RegisterCampaign("--quiet-war-only", (c, map) => { QuietWarTests.Run(c); });
        RegisterCampaign("--opening-population-only", (c, map) => { OpeningPopulationTests.Run(c); });
        RegisterCampaign("--peace-tidy-only", (c, map) => { PeaceTidyTests.Run(c); });
        RegisterCampaign("--peace-maritime-only", (c, map) => { MaritimePeaceTests.Run(c); });
        RegisterCampaign("--peace-real-only", (c, map) => { PeaceCoalitionTests.RunReal(c); });
        RegisterCampaign("--movement82-only", (c, map) => { Movement82Tests.Run(c); });
        RegisterCampaign("--ai-war-only", (c, map) => { AiWarTests.Run(map); });
        RegisterCampaign("--nuclear-strike-only", (c, map) => { NuclearStrikeTests.Run(map); });
        RegisterCampaign("--expeditions-only", (c, map) => { ExpeditionTests.Run(map); });
        RegisterCampaign("--backlog69-only", (c, map) => { Backlog69Tests.Run(c); });
        RegisterCampaign("--defence-only", (c, map) => { DefensivePlanTests.Run(c); });
        RegisterCampaign("--radicalism-only", (c, map) => { RadicalismTests.Run(c); });
        RegisterCampaign("--offensive-only", (c, map) => { OffensivePlanTests.Run(c); });
        RegisterCampaign("--pocket-only", (c, map) => { PocketTests.Run(map); });
        RegisterCampaign("--stranded-garrison-only", (c, map) => { StrandedGarrisonTests.RunRising(map); });
        RegisterCampaign("--pocket-stores-only", (c, map) => { StrandedGarrisonTests.RunPocket(map); });
        RegisterCampaign("--loss-only", (c, map) => { LossTests.Run(map); });
        RegisterCampaign("--capital-only", (c, map) => { CapitalTests.Run(map); });
        RegisterCampaign("--options-only", (c, map) => { CampaignOptionsTests.Run(map); });
        RegisterCampaign("--colony-only", (c, map) => { ColonisationTests.Run(map); });
        RegisterCampaign("--subject-only", (c, map) => { SubjectIdentityTests.Run(map); });
        RegisterCampaign("--submission-only", (c, map) => { SubmissionTests.Run(map); });
        RegisterCampaign("--agreement-only", (c, map) => { AgreementTests.Run(map); });
        RegisterCampaign("--research-only", (c, map) => { TechnologyTests.Run(map); });
        RegisterCampaign("--naval-only", (c, map) => { NavalTests.Run(map); });
        RegisterCampaign("--staff-only", (c, map) => { StaffTests.Run(map); });
        RegisterCampaign("--front-staff-only", (c, map) => { FrontStaffTests.Run(map); });
        RegisterCampaign("--modifiers-only", (c, map) => { ModifierTests.Run(map); });
        RegisterCampaign("--conflicts-only", (c, map) => { ConflictTests.Run(map); });
        RegisterCampaign("--separate-peace-only", (c, map) => { SeparatePeaceTests.Run(map); });
        RegisterCampaign("--whole-front-only", (c, map) => { WholeFrontTests.Run(map); });
    }

    private static (Campaign Campaign, ProvinceTopology Map, Army Own, Army Foreign, Nation Rus, Nation Ukr, int MarkerA, int MarkerB) BuildBootstrapAndMap()
    {
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
        return (c, map, own, foreign, rus, ukr, 0, 0);
    }

    private static void RunDefaultCampaignTail(Campaign c, ProvinceTopology map, Army own, Army foreign)
    {
        var rus = c.NationByTag("RUS");
        var ukr = c.NationByTag("UKR");
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
        // Long-range forces: basing and the old region orders, then movement on the map.
        SupportTests.Run(map);
        LongRangeMovementTests.Run(map);
    }
}
