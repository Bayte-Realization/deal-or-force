using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestSuiteAttribute : Attribute
{
    public string Flag { get; }
    public int Order { get; set; } = 10000;
    public bool IncludeInDefaultRun { get; set; } = true;
    public string Description { get; set; } = "";
    public string[] Aliases { get; set; } = Array.Empty<string>();

    public TestSuiteAttribute(string flag) => Flag = flag;
}

internal static class SuiteRegistry
{
    private sealed record SuiteEntry(
        string Flag,
        string[] Aliases,
        Action<string[]> Run,
        int Order,
        bool IncludeInDefaultRun,
        string Description,
        int Sequence)
    {
        public bool Matches(string[] args) =>
            args.Contains(Flag) || Aliases.Any(args.Contains);
    }

    private static readonly List<SuiteEntry> Suites = new();
    private static readonly List<(int Order, int Sequence, Action Run)> DefaultSteps = new();
    private static readonly HashSet<string> RegisteredFlags = new(StringComparer.Ordinal);
    private static int _sequence;
    private static bool _initialized;

    public static void Register(string flag, Action run, int order = 10000,
        bool includeInDefaultRun = true, string description = "", params string[] aliases) =>
        Register(flag, _ => run(), order, includeInDefaultRun, description, aliases);

    public static void Register(string flag, Action<string[]> run, int order = 10000,
        bool includeInDefaultRun = true, string description = "", params string[] aliases)
    {
        if (!RegisteredFlags.Add(flag)) return;
        foreach (var alias in aliases) RegisteredFlags.Add(alias);
        var seq = ++_sequence;
        Suites.Add(new SuiteEntry(flag, aliases, run, order, includeInDefaultRun, description, seq));
        if (includeInDefaultRun) DefaultSteps.Add((order, seq, () => run(Array.Empty<string>())));
    }

    public static void RegisterDefaultStep(int order, Action run)
    {
        var seq = ++_sequence;
        DefaultSteps.Add((order, seq, run));
    }

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        RegisterBuiltins();
        DiscoverAttributedSuites();
    }

    public static bool TryDispatch(string[] args)
    {
        EnsureInitialized();
        if (args.Length == 0) return false;
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("--list-suites"))
        {
            PrintHelp();
            return true;
        }
        foreach (var suite in Suites)
        {
            if (!suite.Matches(args)) continue;
            suite.Run(args);
            return true;
        }
        Console.Error.WriteLine("Unknown test selection: " + string.Join(" ", args));
        Environment.ExitCode = 2;
        return true;
    }

    public static void RunDefault()
    {
        EnsureInitialized();
        foreach (var step in DefaultSteps.OrderBy(s => s.Order).ThenBy(s => s.Sequence))
            step.Run();
    }

    public static IReadOnlyList<string> AllFlags()
    {
        EnsureInitialized();
        return Suites.SelectMany(s => new[] { s.Flag }.Concat(s.Aliases)).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToArray();
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"FormableTests registered suites ({Suites.Count} entries, {AllFlags().Count} flags):");
        foreach (var suite in Suites.OrderBy(s => s.Flag, StringComparer.Ordinal))
        {
            var aliasText = suite.Aliases.Length > 0 ? " (" + string.Join(", ", suite.Aliases) + ")" : "";
            var desc = string.IsNullOrEmpty(suite.Description) ? "" : " - " + suite.Description;
            Console.WriteLine($"  {suite.Flag}{aliasText}{desc}");
        }
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
                Action<string[]> runner = parameters.Length == 0
                    ? _ => InvokeStatic(method, null)
                    : parameters.Length == 1 && parameters[0].ParameterType == typeof(string[])
                        ? args => InvokeStatic(method, new object[] { args })
                        : throw new InvalidOperationException($"Unsupported [TestSuite] signature on {type.Name}.{method.Name}");
                foreach (var attr in attrs)
                    Register(attr.Flag, runner, attr.Order, attr.IncludeInDefaultRun, attr.Description, attr.Aliases);
            }
        }
    }

    private static void InvokeStatic(MethodInfo method, object[] arguments)
    {
        try { method.Invoke(null, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    private static void RegisterBuiltins()
    {
        Register("--non-countries-only", () => { NonCountriesTests.Run(); }, includeInDefaultRun: false);
        Register("--capital-returns-only", () => { CapitalReturnsTests.Run(); }, includeInDefaultRun: false);
        Register("--industry-fixes-only", () => { IndustryFixTests.Run(); }, includeInDefaultRun: false);
        Register("--construction-capacity-only", () => { ConstructionCapacityTests.Run(); }, includeInDefaultRun: false);
        Register("--money-drains-only", () => { MoneyDrainsTests.Run(); }, includeInDefaultRun: false);
        Register("--recognition-index-only", () => { RecognitionIndexTests.Run(); }, includeInDefaultRun: false);
        Register("--exile-origin-only", () => { ExileOriginTests.Run(); }, includeInDefaultRun: false);
        Register("--region-id-walk-only", () => { RegionIdWalkTests.Run(); }, includeInDefaultRun: false);
        Register("--movement-quiet-think-only", () => { MovementQuietThinkTests.Run(); }, includeInDefaultRun: false);
        Register("--weekly-diplomacy-scan-only", () => { WeeklyDiplomacyScanTests.Run(); }, includeInDefaultRun: false);
        Register("--relation-table-only", () => { RelationTableTests.Run(); }, includeInDefaultRun: false);
        Register("--long-campaign-only", () => { LongCampaignTests.Run(); }, includeInDefaultRun: false);
        Register("--people-index-reuse-only", () => { PeopleIndexReuseTests.Run(); }, includeInDefaultRun: false);
        Register("--people-index-names-only", () => { PeopleIndexNamesTests.Run(); }, includeInDefaultRun: false);
        Register("--society-passes-only", () => { SocietyPassTests.Run(); }, includeInDefaultRun: false);
        Register("--battle-occupancy-hold-only", () => { BattleOccupancyHoldTests.Run(); }, includeInDefaultRun: false);
        Register("--own-province-click-only", () => { OwnProvinceClickTests.Run(); }, includeInDefaultRun: false);
        Register("--battle-damage-isolation-only", () => { BattleDamageIsolationTests.Run(); }, includeInDefaultRun: false);
        Register("--battle-side-isolation-only", () => { BattleSideIsolationTests.Run(); }, includeInDefaultRun: false);
        Register("--smooth-heavy-yield-only", () => { SmoothHeavyYieldTests.Run(); }, includeInDefaultRun: false);
        Register("--war-occupation-isolation-only", () => { WarOccupationIsolationTests.Run(); }, includeInDefaultRun: false);
        Register("--alliance-read-only", () => { AllianceWillingnessReadTests.Run(); }, includeInDefaultRun: false);
        Register("--advance-route-cache-only", () => { AdvanceRouteCacheTests.Run(); }, includeInDefaultRun: false);
        Register("--pocket-scratch-only", () => { PocketScratchTests.Run(); }, includeInDefaultRun: false);
        Register("--crossings-only", () => { StraitCrossingTests.Run(); }, includeInDefaultRun: false);
        Register("--crossings-survey", () => { StraitCrossingTests.Survey(); }, includeInDefaultRun: false);
        Register("--parallel-determinism-only", () => { ParallelDeterminismTests.Run(); }, includeInDefaultRun: false);
        Register("--peace-occupation-transfer-only", () => { PeaceOccupationTransferTests.Run(); }, includeInDefaultRun: false);
        Register("--peace-truce-years-only", () => { PeaceTruceYearsTests.Run(); }, includeInDefaultRun: false);
        Register("--emergency-walk-only", () => { EmergencyWalkTests.Run(); }, includeInDefaultRun: false);
        Register("--quarrel-step-only", () => { QuarrelStepTests.Run(); }, includeInDefaultRun: false);
        Register("--peace-border-why-only", () => { PeaceBorderWhyTests.Run(); }, includeInDefaultRun: false);
        Register("--ended-war-effect-only", () => { EndedWarEffectTests.Run(); }, includeInDefaultRun: false);
        Register("--alliance-break-only", () => { AllianceBreakTests.Run(); }, includeInDefaultRun: false);
        Register("--ended-war-walk-only", () => { EndedWarWalkTests.Run(); }, includeInDefaultRun: false);
        Register("--live-wars-only", () => { LiveWarTests.Run(); }, includeInDefaultRun: false);
        Register("--doctrine-stack-only", () => { DoctrineStackTests.Run(); }, includeInDefaultRun: false);
        Register("--border-adjacency-only", () => { BorderAdjacencyTests.Run(); }, includeInDefaultRun: false);
        Register("--ally-rival-only", () => { AllyRivalShiftTests.Run(); }, includeInDefaultRun: false);
        Register("--pocket-relief-only", () => { PocketReliefTests.Run(); }, includeInDefaultRun: false);
        Register("--army-index-isolation-only", () => { ArmyIndexIsolationTests.Run(); }, includeInDefaultRun: false);
        Register("--war-score-isolation-only", () => { WarScoreIsolationTests.Run(); }, includeInDefaultRun: false);
        Register("--battle-bucket-isolation-only", () => { BattleBucketIsolationTests.Run(); }, includeInDefaultRun: false);
        Register("--front-posting-isolation-only", () => { FrontPostingIsolationTests.Run(); }, includeInDefaultRun: false);
        Register("--ai-coercion-timing-only", () => { AiCoercionTimingTests.Run(); }, includeInDefaultRun: false);
        Register("--recruitment-placement-only", () => { RecruitmentPlacementTests.Run(); }, includeInDefaultRun: false);
        Register("--country-rankings-only", () => { CountryRankingTests.Run(); }, includeInDefaultRun: false);
        Register("--support-repatriation-only", () => { SupportRepatriationTests.Run(); }, includeInDefaultRun: false);
        Register("--support-repatriation-save", args => { SupportRepatriationTests.Replay(args); }, includeInDefaultRun: false);
        Register("--memory-hot-path-only", () => { MemoryHotPathTests.Run(); }, includeInDefaultRun: false);
        Register("--officeholder-modding-only", () => { OfficeholderModdingTests.Run(); }, includeInDefaultRun: false);
        Register("--console-localization-only", () => { ConsoleLocalizationTests.Run(); }, includeInDefaultRun: false);
        Register("--inspect-save", args => { SaveAudit.Run(args); }, includeInDefaultRun: false);
        Register("--save-compression-only", () => { SaveCompressionTests.Run(); }, includeInDefaultRun: false);
        Register("--save-warmup-only", () => { SaveSerializationWarmupTests.Run(); }, includeInDefaultRun: false);
        Register("--save-container-only", () => { SaveContainerTests.Run(); }, includeInDefaultRun: false);
        Register("--region-reunion-only", () => { RegionReunionTests.Run(); }, includeInDefaultRun: false);
        Register("--raster-rivers-only", () => { RasterRiverCourseTests.Run(); }, includeInDefaultRun: false);
        Register("--large-production-only", () => { LargeProductionCatalogTests.Run(); }, includeInDefaultRun: false);
        Register("--construction-placement-only", () => { ConstructionPlacementTests.Run(); }, includeInDefaultRun: false);
        Register("--decision-readability-only", () => { DecisionReadabilityTests.Run(); }, includeInDefaultRun: false);
        Register("--war-declaration-only", () => { WarDeclarationTests.Run(); }, includeInDefaultRun: false);
        Register("--bce-calendar-only", () => { BceCalendarTests.Run(); }, includeInDefaultRun: false);
        Register("--authored-flags-only", () => { AuthoredRegimeFlagTests.Run(); }, includeInDefaultRun: false);
        Register("--strategic-exclaves-only", () => { StrategicExclaveTests.Run(); }, includeInDefaultRun: false);
        Register("--kaliningrad-real-only", () => { ExclaveSupplyTests.RealKaliningrad(); }, includeInDefaultRun: false);
        Register("--exclave-supply-only", () => { ExclaveSupplyTests.Run(); }, includeInDefaultRun: false);
        Register("--map-usability-only", () => { MapUsabilityTests.Run(); }, includeInDefaultRun: false);
        Register("--border-rank-only", () => { BorderRankTests.Run(); }, includeInDefaultRun: false);
        Register("--frontier-field-only", () => { FrontierFieldTests.Run(); }, includeInDefaultRun: false);
        Register("--audit-formable-opening", args => { FormableOpeningAudit.Run(args); }, includeInDefaultRun: false);
        Register("--mod-simulation-passes-only", () => { ModSimulationPassTests.Run(); }, includeInDefaultRun: false);
        Register("--gpu-atlas-contract-only", () => { GpuAtlasContractTests.Run(); }, includeInDefaultRun: false);
        Register("--map-look-only", () => { MapLookTests.Run(); }, includeInDefaultRun: false);
        Register("--map-labels-only", () => { MapLabelPriorityTests.Run(); }, includeInDefaultRun: false);
        Register("--release-0530-only", () => { Release0530Tests.Run(); }, includeInDefaultRun: false);
        Register("--country-name-settings-only", () => { CountryNameSettingTests.Run(); }, includeInDefaultRun: false);
        Register("--field-use-only", () => { FieldUseTests.Run(); }, includeInDefaultRun: false);
        Register("--civil-stalemate-only", () => { CivilStalemateTests.Run(); }, includeInDefaultRun: false);
        Register("--stage-budget-baseline", () => { StageBudgetTests.Allocations(false); }, includeInDefaultRun: false);
        Register("--stage-budget-only", () => { StageBudgetTests.Run(); }, includeInDefaultRun: false);
        Register("--desk-chair-only", () => { DeskChairTests.Run(); }, includeInDefaultRun: false);
        Register("--desk-strain-only", () => { DeskStrainTests.Run(); }, includeInDefaultRun: false);
        Register("--date-line-only", () => { DateLineSeamTests.Run(); }, includeInDefaultRun: false);
        Register("--compact-hud-only", () => { CompactHudTests.Run(); }, includeInDefaultRun: false);
        Register("--river-width-only", () => { RiverWidthTests.Run(); }, includeInDefaultRun: false);
        Register("--version-label-only", () => { VersionLabelTests.Run(); }, includeInDefaultRun: false);
        Register("--staff-reasons-only", () => { StaffReasonTests.Run(); }, includeInDefaultRun: false);
        Register("--depot-attack-only", () => { DepotAttackTests.Run(); }, includeInDefaultRun: false);
        Register("--arms-shipment-only", () => { ArmsShipmentTests.Run(); }, includeInDefaultRun: false);
        Register("--composer-document-only", () => { ComposerDocumentTests.Run(); }, includeInDefaultRun: false);
        Register("--application-exit-only", () => { ApplicationExitTests.Run(); }, includeInDefaultRun: false);
        Register("--mod-scalars-only", () => { ModScalarAccessTests.Run(); }, includeInDefaultRun: false);
        Register("--modder-examples-only", () => { ModderExamplesTests.Run(); }, includeInDefaultRun: false);
        Register("--music-transport-only", () => { MusicTransportTests.Run(); }, includeInDefaultRun: false);
        Register("--morale-terms-only", () => { MoraleTermsTests.Run(); }, includeInDefaultRun: false);
        Register("--religion-only", () => { ReligionDenominationTests.Run(); }, includeInDefaultRun: false);
        Register("--beliefs-only", () => { BeliefDynamicsTests.Run(); }, includeInDefaultRun: false);
        Register("--law-options-only", () => { LawOptionIntegrationTests.Run(); }, includeInDefaultRun: false);
        Register("--ultimatum-persistence-only", () => { UltimatumPersistenceTests.Run(); }, includeInDefaultRun: false);
        Register("--policy-programmes-only", () => { PolicyProgrammeTests.Run(); }, includeInDefaultRun: false);
        Register("--exposure-batch-only", () => { ExposureBatchTests.Run(); }, includeInDefaultRun: false);
        Register("--equipment-replenishment-only", () => { EquipmentReplenishmentTests.Run(); }, includeInDefaultRun: false);
        Register("--covert-politics-only", () => { CovertPoliticalTests.Run(); }, includeInDefaultRun: false);
        Register("--government-supply-only", () => { GovernmentSupplyTests.Run(); }, includeInDefaultRun: false);
        Register("--supply-network-only", () => { SupplyNetworkTests.Run(); }, includeInDefaultRun: false);
        Register("--isolated-depot-only", () => { IsolatedDepotTests.Run(); }, includeInDefaultRun: false);
        Register("--supply-days-only", () => { SupplyDaysTests.Run(); }, includeInDefaultRun: false);
        Register("--news-facts-only", () => { NewsFactTests.Run(); }, includeInDefaultRun: false);
        Register("--campaign-chronicle-only", () => { CampaignChronicleTests.Run(); }, includeInDefaultRun: false);
        Register("--strategic-cover-only", () => { StrategicCoverTests.Run(); }, includeInDefaultRun: false);
        Register("--war-rear-only", () => { WarRearTests.Run(); }, includeInDefaultRun: false);
        Register("--four-systems-only", () => { FourSystemsIntegrationTests.RunAll(); }, includeInDefaultRun: false);
        Register("--revolt-save", args => { RebellionRecoveryTests.InspectSave(args); }, includeInDefaultRun: false);
        Register("--revolt-recovery-only", () => { RebellionRecoveryTests.Run(); }, includeInDefaultRun: false);
        Register("--conquest-aftermath-only", () => { ConquestAftermathTests.Run(); }, includeInDefaultRun: false);
        Register("--paused-doctrines-only", () => { PausedDoctrineTests.Run(); }, includeInDefaultRun: false);
        Register("--battle-orders-only", () => { BattleOrderTests.Run(); }, includeInDefaultRun: false);
        Register("--interface-type-only", () => { InterfaceTypeTests.Run(); }, includeInDefaultRun: false);
        Register("--situation-desk-only", () => { SituationDeskTests.Run(); }, includeInDefaultRun: false);
        Register("--game-folders-only", () => { GameFolderTests.Run(); }, includeInDefaultRun: false);
        Register("--peace-stalemate-only", () => { PeaceStalemateTests.Run(); }, includeInDefaultRun: false);
        Register("--map-polygon-mask-only", () => { MapPolygonMaskTests.Run(); }, includeInDefaultRun: false);
        Register("--support-report-only", () => { SupportReportTests.Run(); }, includeInDefaultRun: false);
        Register("--rules-latency-only", () => { RulesLatencyTests.Run(); }, includeInDefaultRun: false);
        Register("--raster-kernel-only", args => { RasterKernelBenchTests.Run(args); }, includeInDefaultRun: false);
        Register("--heap-census", args => { HeapCensusTests.Run(args); }, includeInDefaultRun: false);
        Register("--diplomatic-acts-only", () => { DiplomaticActTests.Run(); }, includeInDefaultRun: false);
        Register("--geopolitical-dynamics-only", () => { GeopoliticalDynamicsTests.Run(); }, includeInDefaultRun: false);
        Register("--formable-proclamation-ai-only", () => { FormableProclamationAiTests.Run(); }, includeInDefaultRun: false);
        Register("--smaller-polity-only", () => { SmallerPolityTests.Run(); }, includeInDefaultRun: false);
        Register("--headline-popups-only", () => { HeadlinePopupTests.Run(); }, includeInDefaultRun: false);
        Register("--organic-blocs-only", () => { OrganicBlocTests.Run(); }, includeInDefaultRun: false);
        Register("--diplomatic-missions-only", () => { DiplomaticMissionTests.Run(); }, includeInDefaultRun: false);
        Register("--late-diplomacy-stages-only", () => { LateDiplomacyStagesTests.Run(); }, includeInDefaultRun: false);
        Register("--opinion-witnesses-only", () => { OpinionWitnessTests.Run(); }, includeInDefaultRun: false);
        Register("--campaign-alerts-only", () => { CampaignAlertTests.Run(); StaffPortfolioTests.Run(); }, includeInDefaultRun: false);
        Register("--elections-only", () => { ElectionTests.Run(); }, includeInDefaultRun: false);
        Register("--regional-administration-only", () => { RegionalAdministrationTests.Run(); }, includeInDefaultRun: false);
        Register("--political-identity-only", () => { PoliticalIdentityTests.Run(); }, includeInDefaultRun: false);
        Register("--person-names-only", () => { PersonNameCatalogTests.Run(); }, includeInDefaultRun: false);
        Register("--performance-regressions-only", () => { PerformanceRegressionTests.Run(); }, includeInDefaultRun: false);
        Register("--decision-resources-only", () => { DecisionResourceTests.Run(); }, includeInDefaultRun: false);
        Register("--service-exercises-only", () => { ServiceExerciseTests.Run(); }, includeInDefaultRun: false);
        Register("--order-observation-only", () => { ArmyOrderObservationTests.Run(); }, includeInDefaultRun: false);
        Register("--shard-consolidation-only", () => { ShardConsolidationTests.Run(); }, includeInDefaultRun: false);
        Register("--audit-movement-flags", () => { FlagArtworkTests.AuditMovementBanners(); }, includeInDefaultRun: false);
        Register("--audit-content-identities", () => { ContentIdentityAudit.Run(); }, includeInDefaultRun: false);
        Register("--catalogue-identity-only", () => { CatalogIdentityTests.Run(); }, includeInDefaultRun: false);
        Register("--observer-authority-only", () => { ObserverAuthorityTests.Run(); }, includeInDefaultRun: false);
        Register("--ownership-kernel-only", () => { OwnershipComponentTests.Run(); }, includeInDefaultRun: false);
        Register("--core-refactor-only", () => { CoreRefactorTests.Run(); }, includeInDefaultRun: false);
        Register("--agenda-only", () => { AgendaWorkspaceTests.Run(); }, includeInDefaultRun: false);
        Register("--extinction-only", () => { DemographicExtinctionTests.Run(); }, includeInDefaultRun: false);
        Register("--deterrence-only", () => { DeterrenceTests.Run(); }, includeInDefaultRun: false);
        Register("--resistance-only", () => { OccupationResistanceTests.Run(); }, includeInDefaultRun: false);
        Register("--partition-only", () => { PartitionTests.Run(); }, includeInDefaultRun: false);
        Register("--dynasty-only", () => { DynastyTests.Run(); }, includeInDefaultRun: false);
        Register("--rulers-subjects-only", () => { RulersAndSubjectsTests.Run(); }, includeInDefaultRun: false);
        Register("--macao-only", () => { MacaoProbe.Run(); }, includeInDefaultRun: false);
        Register("--dependencies-only", () => { DependencyPlausibilityTests.Run(); }, includeInDefaultRun: false);
        Register("--curses-only", () => { WorldCurseTests.Run(); }, includeInDefaultRun: false);
        Register("--subject-growth-only", () => { SubjectGrowthProbe.Run(); }, includeInDefaultRun: false);
        Register("--micro-enclave-only", () => { MicroEnclaveGrowthTests.Run(); }, includeInDefaultRun: false);
        Register("--tautology-only", () => { TautologyNameProbe.Run(); }, includeInDefaultRun: false);
        Register("--founding-names-only", () => { FoundingNameTests.Run(); }, includeInDefaultRun: false);
        Register("--flag-survey-only", () => { FlagSurvey.Run(); }, includeInDefaultRun: false);
        Register("--tick-bench-only", () => { TickBench.Run(); }, includeInDefaultRun: false);
        Register("--name-survey-only", () => { FoundingNameSurvey.Run(); }, includeInDefaultRun: false);
        Register("--diaspora-only", () => { DiasporaProbe.Run(); }, includeInDefaultRun: false);
        Register("--hot-wars-names-only", () => { HotWarAndNamePoolTests.Run(); }, includeInDefaultRun: false);
        Register("--annexation-only", () => { AnnexationTests.Run(); }, includeInDefaultRun: false);
        Register("--mobilisation-only", () => { MobilisationAndStockpileTests.RunAll(); }, includeInDefaultRun: false);
        Register("--ownership-native-only", () => { OwnershipComponentTests.Run(true); }, includeInDefaultRun: false);
        Register("--ownership-fallback-only", () => { OwnershipComponentTests.Run(false); }, includeInDefaultRun: false);
        Register("--ownership-kernel-fixtures", args => { OwnershipComponentTests.RunFixtures(args.Last()); }, includeInDefaultRun: false);
        Register("--diplomacy-preview-only", () => { DiplomacyPreviewTests.Run(); }, includeInDefaultRun: false);
        Register("--atlas-surfaces-only", () => { AtlasSurfaceTests.Run(); }, includeInDefaultRun: false);
        Register("--layout-keys-only", () => { LayoutKeyPinTests.Run(); }, includeInDefaultRun: false);
        Register("--interface-materials-only", () => { InterfaceMaterialTests.Run(); }, includeInDefaultRun: false);
        Register("--skin-overlay-only", () => { SkinOverlayTests.Run(); }, includeInDefaultRun: false);
        Register("--icon-categories-only", () => { IconCategoryTests.Run(); }, includeInDefaultRun: false);
        Register("--hud-resources-only", () => { HudResourceTests.Run(); }, includeInDefaultRun: false);
        Register("--date-plate-only", () => { DatePlateTests.Run(); }, includeInDefaultRun: false);
        Register("--controls-ergonomics-only", () => { ControlsErgonomicsTests.RunControlsErgonomicsOnly(); }, includeInDefaultRun: false);
        Register("--quick-amount-only", () => { ControlsErgonomicsTests.RunQuickAmountOnly(); }, includeInDefaultRun: false);
        Register("--alert-list-only", () => { AlertListTests.Run(); }, includeInDefaultRun: false);
        Register("--new-shell-rest-only", () => { NewShellTests.RunRestLayout(); }, includeInDefaultRun: false);
        Register("--alert-dedupe-only", () => { NewShellTests.RunAlertDedupe(); }, includeInDefaultRun: false);
        Register("--hotkey-map-only", () => { NewShellTests.RunHotkeyMap(); }, includeInDefaultRun: false);
        Register("--messages-only", () => { NewShellTests.RunMessages(); }, includeInDefaultRun: false);
        Register("--shell-date-plate-only", () => { NewShellTests.RunDatePlate(); }, includeInDefaultRun: false);
        Register("--war-toll-only", () => { WarTollTests.Run(); }, includeInDefaultRun: false);
        Register("--research-route-only", () => { ResearchRouteTests.Run(); }, includeInDefaultRun: false);
        Register("--research-workspace-only", () => { TechnologyTreeTests.Run(); ResearchWorkspaceTests.Run(); ResearchRouteTests.Run(); }, includeInDefaultRun: false);
        Register("--regional-demography-only", () => { RegionalDemographyTests.Run(); }, includeInDefaultRun: false);
        Register("--peace-intent-only", () => { PeaceIntentTests.Run(); }, includeInDefaultRun: false);
        Register("--war-aims-only", () => { WarAimsTests.Run(); CombatTempoTests.Run(); }, includeInDefaultRun: false);
        Register("--combat-tempo-only", () => { CombatTempoTests.Run(); }, includeInDefaultRun: false);
        Register("--combat-tactics-only", () => { CombatTacticsTests.Run(); }, includeInDefaultRun: false);
        Register("--shell-coherence-only", () => { InterfaceSkin.ResetCatalog(); ShellCoherenceTests.Run(); }, includeInDefaultRun: false);
        Register("--decisive-patience-only", () => { DecisivePatienceTests.Run(); }, includeInDefaultRun: false);
        Register("--rules-backends-only", () => { RulesBackendTests.Run(); }, includeInDefaultRun: false);
        Register("--defections-only", () => { DefectionsTests.Run(); }, includeInDefaultRun: false);
        Register("--participant-objective-only", () => { ParticipantWarObjectiveTests.Run(); }, includeInDefaultRun: false);
        Register("--war-aim-resolve-only", () => { WarAimResolveTests.Run(); }, includeInDefaultRun: false);
        Register("--worker-fixture", args => { WorkerBackendTests.Fixture(args.Last()); }, includeInDefaultRun: false);
        Register("--campaign-toggles-only", () => { CampaignToggleTests.Run(); }, includeInDefaultRun: false);
        Register("--conciliation-only", () => { ConciliationTests.Run(); }, includeInDefaultRun: false);
        Register("--corridor-report-only", () => { CorridorReportTests.Run(); }, includeInDefaultRun: false);
        Register("--movement-voice-only", () => { MovementVoiceTests.Run(); }, includeInDefaultRun: false);
        Register("--host-patience-only", () => { HostPatienceTests.Run(); }, includeInDefaultRun: false);
        Register("--peace-compact-only", () => { PeaceCompactTests.Run(); }, includeInDefaultRun: false);
        Register("--army-standdown-only", () => { ArmyStandDownTests.Run(); }, includeInDefaultRun: false);
        Register("--held-conquest-peace-only", () => { HeldConquestPeaceTests.Run(); }, includeInDefaultRun: false);
        Register("--border-tidy-only", () => { BorderTidyTests.Run(); }, includeInDefaultRun: false);
        Register("--delegated-front-only", () => { DelegatedFrontTests.Run(); }, includeInDefaultRun: false);
        Register("--kindred-only", () => { KindredLandTests.Run(); }, includeInDefaultRun: false);
        Register("--player-ultimatum-only", () => { PlayerUltimatumTests.Run(); }, includeInDefaultRun: false);
        Register("--ultimatum-terms-only", () => { UltimatumTermTests.Run(); }, includeInDefaultRun: false);
        Register("--ultimatum-only", () => { UltimatumTests.Run(); }, includeInDefaultRun: false);
        Register("--enclave-swap-only", () => { EnclaveSwapTests.Run(); }, includeInDefaultRun: false);
        Register("--integrity-only", () => { CampaignIntegrityTests.Run(); }, includeInDefaultRun: false);
        Register("--conflict-preview-only", () => { ConflictReadOnlyTests.Run(); }, includeInDefaultRun: false);
        Register("--living-world-fingerprint-only", () => { ClaudeStateFingerprintTests.Run(); }, includeInDefaultRun: false);
        Register("--staged-boundary-only", () => { StagedStepBoundaryTests.Run(); }, includeInDefaultRun: false);
        Register("--mod-frontend-only", () => { ModFrontendActionTests.Run(); }, includeInDefaultRun: false);
        Register("--assistance-presentation-only", () => { ForeignAssistancePresentationTests.Run(); }, includeInDefaultRun: false);
        Register("--assault-presentation-only", () => { ArmyAssaultPresentationTests.Run(); }, includeInDefaultRun: false);
        Register("--assault-rules-only", () => { AssaultRulesBackendTests.Run(); }, includeInDefaultRun: false);
        Register("--garrison-only", () => { GarrisonPlannerTests.Run(); }, includeInDefaultRun: false);
        Register("--historical-only", () => { HistoricalTimelineTests.Run(); }, includeInDefaultRun: false);
        Register("--war-emergency-only", () => { WarEmergencyTests.Run(); }, includeInDefaultRun: false);
        Register("--foreign-assistance-only", () => { ForeignAssistanceTests.Run(); }, includeInDefaultRun: false);
        Register("--deals-only", () => { DealTests.Run(); TerritorialDealProtectionTests.Run(); }, includeInDefaultRun: false);
        Register("--territorial-recovery-only", () => { TerritorialRecoveryTests.Run(); }, includeInDefaultRun: false);
        Register("--secession-colours-only", () => { SecessionColourTests.Run(); }, includeInDefaultRun: false);
        Register("--occupied-uprising-only", () => { RevoltOccupationTests.Run(); }, includeInDefaultRun: false);
        Register("--overrun-war-only", () => { OverrunWarTests.Run(); }, includeInDefaultRun: false);
        Register("--generals-only", () => { GeneralsTests.Run(); }, includeInDefaultRun: false);
        Register("--cities-only", () => { CityTests.Run(); }, includeInDefaultRun: false);
        Register("--desertion-only", () => { DesertionTests.Run(); }, includeInDefaultRun: false);
        Register("--coup-war-only", () => { CoupCivilWarTests.Run(); }, includeInDefaultRun: false);
        Register("--rescue-only", () => { RescueTests.Run(); }, includeInDefaultRun: false);
        Register("--land-only", () => { LandArrangementTests.Run(); }, includeInDefaultRun: false);
        Register("--treasury-only", () => { TreasuryDynamicsTests.Run(); }, includeInDefaultRun: false);
        Register("--customs-only", () => { CustomsUnionTests.Run(); }, includeInDefaultRun: false);
        Register("--org-crisis-only", () => { OrganizationCrisisTests.Run(); }, includeInDefaultRun: false);
        Register("--org-discipline-only", () => { OrganizationDisciplineTests.Run(); }, includeInDefaultRun: false);
        Register("--border-deals-only", () => { BorderDiplomacyTests.Run(); }, includeInDefaultRun: false);
        Register("--defcon-only", () => { DefconTests.Run(); }, includeInDefaultRun: false);
        Register("--sovereign-only", () => { SovereignRemnantTests.Run(); }, includeInDefaultRun: false);
        Register("--world-layers-only", () => { WorldLayersTests.Run(); }, includeInDefaultRun: false);
        Register("--gestures-only", () => { DiplomaticGestureTests.Run(); }, includeInDefaultRun: false);
        Register("--fallen-ground-only", () => { FallenGroundTests.Run(); }, includeInDefaultRun: false);
        Register("--nation-numbering-only", () => { NationNumberingTests.Run(); }, includeInDefaultRun: false);
        Register("--army-group-tidy-only", () => { ArmyGroupTidyTests.Run(); }, includeInDefaultRun: false);
        Register("--territorial-front-only", () => { TerritorialFrontTests.Run(); }, includeInDefaultRun: false);
        Register("--negotiations-only", () => { DealNegotiationTests.Run(); }, includeInDefaultRun: false);
        Register("--deal-autoanswer-only", () => { DealAutoAnswerTests.Run(); }, includeInDefaultRun: false);
        Register("--trade-terms-only", () => { TradeTermsTests.Run(); }, includeInDefaultRun: false, aliases: new[] { "--trade-agreements-only" });
        Register("--situation-only", () => { SituationReportTests.Run(); }, includeInDefaultRun: false);
        Register("--situation-standoff-only", () => { SituationStandoffTests.Run(); }, includeInDefaultRun: false);
        Register("--hybrid-war-only", () => { HybridWarBriefTests.Run(); }, includeInDefaultRun: false);
        Register("--crisis-ladder-only", () => { CrisisLadderTests.Run(); }, includeInDefaultRun: false);
        Register("--resource-cartel-only", () => { ResourceCartelTests.Run(); }, includeInDefaultRun: false);
        Register("--parliament-only", () => { ParliamentaryPoliticsTests.Run(); }, includeInDefaultRun: false);
        Register("--peace-rules-only", () => { PeaceRulesDifferentialTests.Run(); }, includeInDefaultRun: false);
        Register("--crisis-grant-only", () => { CrisisGrantTests.Run(); }, includeInDefaultRun: false);
        Register("--war-score-index-only", () => { WarScoreIndexTests.Run(); }, includeInDefaultRun: false);
        Register("--logistics-isolation-only", () => { LogisticsIsolationTests.Run(); }, includeInDefaultRun: false);
        Register("--war-rhythm-only", () => { WarRhythmRulesTests.Run(); }, includeInDefaultRun: false);
        Register("--supply-hedge-only", () => { SupplyHedgeTests.Run(); }, includeInDefaultRun: false);
        Register("--exposure-only", () => { StrategicReportsTests.Run(); SupplyStressTests.Run(); }, includeInDefaultRun: false);
        Register("--music-only", () => { MusicSituationTests.Run(); }, includeInDefaultRun: false);
        Register("--music-transition-only", () => { MusicTransitionTests.Run(); }, includeInDefaultRun: false);
        Register("--weather-only", () => { WeatherReadabilityTests.Run(); }, includeInDefaultRun: false);
        Register("--political-only", () => { PoliticalCohortTests.Run(); }, includeInDefaultRun: false);
        Register("--promises-only", () => { GovernmentPromiseTests.Run(); }, includeInDefaultRun: false);
        Register("--goals-only", () => { StrategicGoalTests.Run(); }, includeInDefaultRun: false);
        Register("--state-identity-only", () => { StateIdentityTests.Run(); }, includeInDefaultRun: false);
        Register("--rulers-only", () => { RulerNamesTests.Run(); }, includeInDefaultRun: false);
        Register("--ruler-ages-only", () => { RulerAgeTests.Run(); }, includeInDefaultRun: false);
        Register("--quality-profiles-only", () => { QualityProfileTests.Run(); }, includeInDefaultRun: false);
        Register("--counter-cards-only", () => { CounterCardTests.Run(); }, includeInDefaultRun: false);
        Register("--ai-promises-only", () => { AiPromiseTests.Run(); }, includeInDefaultRun: false);
        Register("--startup-only", () => { StartupTimelineTests.Run(); }, includeInDefaultRun: false);
        Register("--doctrine-expansion-only", () => { DoctrineExpansionTests.Run(); }, includeInDefaultRun: false);
        Register("--campaign-cosmetics-only", () => { CampaignCosmeticsTests.Run(); }, includeInDefaultRun: false);
        Register("--doctrine-only", () => { TacticalDoctrineTests.Run(); }, includeInDefaultRun: false);
        Register("--strategy-only", () => { StrategicDoctrineTests.Run(); }, includeInDefaultRun: false);
        Register("--names-only", () => { CountryNameTests.Run(); }, includeInDefaultRun: false);
        Register("--naming-only", () => { NamingTests.Run(); }, includeInDefaultRun: false);
        Register("--name-probe", () => { NameProbe.Run(); }, includeInDefaultRun: false);
        Register("--ruler-names-only", () => { RulerNameTests.Run(); }, includeInDefaultRun: false);
        Register("--federal-outcome-only", () => { FederalFractureOutcomeTests.Run(); }, includeInDefaultRun: false);
        Register("--movement-power-state-only", () => { MovementPowerStateTests.Run(); }, includeInDefaultRun: false);
        Register("--movement-staging-only", () => { MovementPowerStagingTests.Run(); }, includeInDefaultRun: false);
        Register("--campaign-isolation-only", () => { CampaignIsolationTests.Run(); }, includeInDefaultRun: false);
        Register("--movement-power-fingerprint-only", () => { MovementPowerFingerprintTests.Run(); }, includeInDefaultRun: false);
        Register("--feed-only", () => { NewsFeedTests.Run(); }, includeInDefaultRun: false);
        Register("--chart-marks-only", () => { ChartMarkersTests.Run(); }, includeInDefaultRun: false);
        Register("--media-only", () => { MediaLibraryTests.Run(); }, includeInDefaultRun: false);
        Register("--responsive-hud-only", () => { ResponsiveHudLayoutTests.Run(); }, includeInDefaultRun: false);
        Register("--identity-only", () => { IdentityResearchTests.Run(); }, includeInDefaultRun: false);
        Register("--restoration-only", () => { RestorationTests.Run(); }, includeInDefaultRun: false);
        Register("--stability-outlook-only", () => { StabilityOutlookTests.Run(); }, includeInDefaultRun: false);
        Register("--civil-war-budget-only", () => { CivilWarBudgetTests.Run(); }, includeInDefaultRun: false);
        Register("--state-failure-only", () => { StateFailureTests.Run(); }, includeInDefaultRun: false);
        Register("--cue-queue-only", () => { StrategicCueQueueTests.Run(); }, includeInDefaultRun: false);
        Register("--network-cadence-only", () => { NetworkSyncCadenceTests.Run(); }, includeInDefaultRun: false);
        Register("--network-lockstep-only", () => { NetworkLockstepTests.Run(); }, includeInDefaultRun: false);
        Register("--network-recovery-only", () => { NetworkRecoveryTests.Run(); }, includeInDefaultRun: false);
        Register("--network-catchup-only", () => { NetworkCatchUpTests.Run(); }, includeInDefaultRun: false);
        Register("--homelands-only", () => { HomelandIntegrationTests.Run(); }, includeInDefaultRun: false);
        Register("--snapshot-delivery-only", () => { NetworkSnapshotDeliveryTests.Run(); }, includeInDefaultRun: false);
        Register("--statecraft-snapshot-only", () => { StatecraftSnapshotTests.Run(); }, includeInDefaultRun: false);
        Register("--claims-only", () => { TerritorialClaimsTests.Run(); }, includeInDefaultRun: false);
        Register("--diplomatic-participation-only", () => { DiplomaticParticipationTests.Run(); }, includeInDefaultRun: false);
        Register("--diplomatic-symmetry-only", () => { DiplomaticSymmetryTests.Run(); }, includeInDefaultRun: false);
        Register("--organizations-only", () => { OrganizationTests.Run(); }, includeInDefaultRun: false);
        Register("--households-only", () => { HouseholdTests.Run(); }, includeInDefaultRun: false);
        Register("--brain-drain-only", () => { BrainDrainTests.Run(); }, includeInDefaultRun: false);
        Register("--release-only", () => { DismantlementTests.Run(); }, includeInDefaultRun: false);
        Register("--deals-bench", () => { DealTests.Benchmark(); }, includeInDefaultRun: false);
        Register("--social-only", () => { SocialMemoryTests.Run(); }, includeInDefaultRun: false);
        Register("--crises-only", () => { DomesticCrisesTests.Run(); }, includeInDefaultRun: false);
        Register("--ui-icons-only", () => { InterfaceIconTests.Run(); }, includeInDefaultRun: false);
        Register("--worker-only", () => { WorkerBackendTests.Run(); }, includeInDefaultRun: false);
        Register("--movement-zone-only", () => { MovementZoneTests.Run(); }, includeInDefaultRun: false);
        Register("--civic-only", () => { CivicCampaignTests.Run(); }, includeInDefaultRun: false);
        Register("--alliances-only", () => { DynamicAllianceTests.Run(); }, includeInDefaultRun: false);
        Register("--finance-only", () => { SovereignFinanceTests.Run(); }, includeInDefaultRun: false);
        Register("--ai-fiscal-only", () => { AiFiscalPolicyTests.Run(); }, includeInDefaultRun: false);
        Register("--income-penalty-only", () => { IncomePenaltyTests.Run(); }, includeInDefaultRun: false);
        Register("--construction-funding-only", () => { ConstructionFundingTests.Run(); }, includeInDefaultRun: false);
        Register("--methods-only", () => { MovementMethodsTests.Run(); }, includeInDefaultRun: false);
        Register("--kin-only", () => { KinStatecraftTests.Run(); }, includeInDefaultRun: false);
        Register("--revolt-lineage-only", () => { RevoltLineageTests.Run(); }, includeInDefaultRun: false);
        Register("--replay-save", args => { SaveReplay.Run(args); }, includeInDefaultRun: false);
        Register("--decisive-peace-only", () => { DecisivePeaceTests.Run(); }, includeInDefaultRun: false);
        Register("--border-coherence-only", () => { BorderCoherenceTests.Run(); }, includeInDefaultRun: false);
        Register("--colour-distance-only", () => { ColourDistanceTests.Run(); }, includeInDefaultRun: false);
        Register("--policies-only", () => { PoliticalPolicyTests.Run(); }, includeInDefaultRun: false);
        Register("--policy-goal-rules-only", () => { PolicyGoalRulesTests.Run(); }, includeInDefaultRun: false);
        Register("--regional-content-only", () => { RegionalContentTests.Run(); }, includeInDefaultRun: false);
        Register("--courses-only", () => { NationalCourseTests.Run(); }, includeInDefaultRun: false);
        Register("--flags-only", () => { FlagModdingTests.Run(); FlagArtworkTests.Run(); FlagDesignTests.Run(); }, includeInDefaultRun: false);
        Register("--flag-designs-only", () => { FlagDesignTests.Run(); }, includeInDefaultRun: false);
        Register("--country-editor-only", () => { CountryEditorTests.Run(); }, includeInDefaultRun: false);
        Register("--union-mutation-only", () => { UnionCohesionMutationTests.Run(); }, includeInDefaultRun: false);
        Register("--union-only", () => { UnionCohesionTests.Run(); }, includeInDefaultRun: false);
        Register("--union-map-only", () => { UnionCohesionTests.RunOnMap(); }, includeInDefaultRun: false);
        Register("--demography-only", () => { DemographyTests.Run(); }, includeInDefaultRun: false);
        Register("--ai-doctrine-only", () => { AiDoctrineTests.Run(); }, includeInDefaultRun: false);
        Register("--ai-temperament-only", () => { AiTemperamentTests.Run(); }, includeInDefaultRun: false);
        Register("--casus-belli-only", () => { CasusBelliTests.Run(); }, includeInDefaultRun: false);
        Register("--parity-wave1-only", () => { ParityWave1Tests.Run(); }, includeInDefaultRun: false);
        Register("--scenario-manifest-only", () => { ScenarioManifestTests.Run(); }, includeInDefaultRun: false);
        Register("--country-history-only", () => { CountryHistoryTests.Run(); }, includeInDefaultRun: false);
        Register("--country-definition-only", () => { CountryDefinitionTests.Run(); }, includeInDefaultRun: false);
        Register("--alliance-program-only", () => { AllianceProgramOutcomeTests.Run(); }, includeInDefaultRun: false);
        Register("--movement-catalog-only", () => { MovementCatalogTests.Run(); }, includeInDefaultRun: false);
        Register("--movement-representation-only", () => { MovementRepresentationTests.Run(); }, includeInDefaultRun: false);
        Register("--war-stalemate-only", () => { WarStalemateTests.Run(); }, includeInDefaultRun: false);
        Register("--coalition-advance-only", () => { CoalitionAdvanceTests.Run(); }, includeInDefaultRun: false);
        Register("--market-shortage-only", () => { MarketShortageTests.Run(); }, includeInDefaultRun: false);
        Register("--yaml-only", () => { StructuredDataTests.Run(); }, includeInDefaultRun: false);
        Register("--yaml-twin-only", () => { YamlTwinTests.Run(); }, includeInDefaultRun: false);
        Register("--army-localization-only", () => { ArmyCommandLocalizationTests.Run(); }, includeInDefaultRun: false);
        Register("--fleet-localization-only", () => { FleetNameLocalizationTests.Run(); }, includeInDefaultRun: false);
        Register("--icons-only", () => { InterfaceIconTests.Run(); }, includeInDefaultRun: false);
        Register("--production-only", () => { ProductionUnitsTests.Run(); }, includeInDefaultRun: false);
        Register("--events-only", () => { EventCadenceTests.Run(); }, includeInDefaultRun: false);
        Register("--reload-only", () => { PresentationReloadTests.Run(); }, includeInDefaultRun: false);
        Register("--presentation-diplomacy-only", () => { PresentationReloadTests.Run(); UltimatumTests.Run(); NewsFeedTests.Run(); }, includeInDefaultRun: false);

        RegisterDefaultStep(10, () => { CapitalReturnsTests.Run(); });
        RegisterDefaultStep(20, () => { IndustryFixTests.Run(); });
        RegisterDefaultStep(30, () => { ConstructionCapacityTests.Run(); });
        RegisterDefaultStep(40, () => { MoneyDrainsTests.Run(); });
        RegisterDefaultStep(50, () => { ExileOriginTests.Run(); });
        RegisterDefaultStep(60, () => { LongCampaignTests.Run(); });
        RegisterDefaultStep(70, () => { OwnProvinceClickTests.Run(); });
        RegisterDefaultStep(80, () => { StraitCrossingTests.Run(); });
        RegisterDefaultStep(90, () => { AdvanceRouteCacheTests.Run(); PocketScratchTests.Run(); });
        RegisterDefaultStep(100, () => { AllianceBreakTests.Run(); });
        RegisterDefaultStep(110, () => { LiveWarTests.Run(); });
        RegisterDefaultStep(120, () => { DoctrineStackTests.Run(); });
        RegisterDefaultStep(130, () => { AllyRivalShiftTests.Run(); });
        RegisterDefaultStep(140, () => { PocketReliefTests.Run(); });
        RegisterDefaultStep(150, () => { WarOccupationIsolationTests.Run(); });
        RegisterDefaultStep(160, () => { WarScoreIsolationTests.Run(); });
        RegisterDefaultStep(170, () => { BattleBucketIsolationTests.Run(); });
        RegisterDefaultStep(180, () => { FrontPostingIsolationTests.Run(); });
        RegisterDefaultStep(190, () => { AiCoercionTimingTests.Run(); });
        RegisterDefaultStep(200, () => { RecruitmentPlacementTests.Run(); });
        RegisterDefaultStep(210, () => { CountryRankingTests.Run(); });
        RegisterDefaultStep(220, () => { SupportRepatriationTests.Run(); });
        RegisterDefaultStep(230, () => { MemoryHotPathTests.Run(); });
        RegisterDefaultStep(240, () => { OfficeholderModdingTests.Run(); });
        RegisterDefaultStep(250, () => { ConsoleLocalizationTests.Run(); });
        RegisterDefaultStep(260, () => { SaveSerializationWarmupTests.Run(); });
        RegisterDefaultStep(270, () => { SaveCompressionTests.Run(); });
        RegisterDefaultStep(280, () => { SaveContainerTests.Run(); });
        RegisterDefaultStep(290, () => { RegionReunionTests.Run(); });
        RegisterDefaultStep(300, () => { RasterRiverCourseTests.Run(); });
        RegisterDefaultStep(310, () => { LargeProductionCatalogTests.Run(); });
        RegisterDefaultStep(320, () => { ConstructionPlacementTests.Run(); });
        RegisterDefaultStep(330, () => { DecisionReadabilityTests.Run(); });
        RegisterDefaultStep(340, () => { WarDeclarationTests.Run(); });
        RegisterDefaultStep(350, () => { BceCalendarTests.Run(); });
        RegisterDefaultStep(360, () => { AuthoredRegimeFlagTests.Run(); });
        RegisterDefaultStep(370, () => { StrategicExclaveTests.Run(); });
        RegisterDefaultStep(380, () => { ExclaveSupplyTests.Run(); });
        RegisterDefaultStep(390, () => { MapUsabilityTests.Run(); });
        RegisterDefaultStep(400, () => { BorderRankTests.Run(); });
        RegisterDefaultStep(410, () => { FrontierFieldTests.Run(); });
        RegisterDefaultStep(420, () => { ModSimulationPassTests.Run(); });
        RegisterDefaultStep(430, () => { GpuAtlasContractTests.Run(); });
        RegisterDefaultStep(440, () => { MapLookTests.Run(); });
        RegisterDefaultStep(450, () => { MapLabelPriorityTests.Run(); });
        RegisterDefaultStep(460, () => { Release0530Tests.Run(); });
        RegisterDefaultStep(470, () => { CountryNameSettingTests.Run(); });
        RegisterDefaultStep(480, () => { StageBudgetTests.Run(); });
        RegisterDefaultStep(490, () => { RiverWidthTests.Run(); });
        RegisterDefaultStep(500, () => { ComposerDocumentTests.Run(); });
        RegisterDefaultStep(510, () => { ApplicationExitTests.Run(); });
        RegisterDefaultStep(520, () => { MusicTransportTests.Run(); });
        RegisterDefaultStep(530, () => { MoraleTermsTests.Run(); });
        RegisterDefaultStep(540, () => { ModderExamplesTests.Run(); });
        RegisterDefaultStep(550, () => { ModScalarAccessTests.Run(); });
        RegisterDefaultStep(560, () => { ReligionDenominationTests.Run(); });
        RegisterDefaultStep(570, () => { BeliefDynamicsTests.Run(); });
        RegisterDefaultStep(580, () => { LawOptionIntegrationTests.Run(); });
        RegisterDefaultStep(590, () => { UltimatumPersistenceTests.Run(); });
        RegisterDefaultStep(600, () => { PolicyProgrammeTests.Run(); });
        RegisterDefaultStep(610, () => { ExposureBatchTests.Run(); });
        RegisterDefaultStep(620, () => { EquipmentReplenishmentTests.Run(); });
        RegisterDefaultStep(630, () => { CovertPoliticalTests.Run(); });
        RegisterDefaultStep(640, () => { GovernmentSupplyTests.Run(); });
        RegisterDefaultStep(650, () => { IsolatedDepotTests.Run(); });
        RegisterDefaultStep(660, () => { SupplyDaysTests.Run(); });
        RegisterDefaultStep(670, () => { NewsFactTests.Run(); });
        RegisterDefaultStep(680, () => { CampaignChronicleTests.Run(); });
        RegisterDefaultStep(690, () => { StrategicCoverTests.Run(); });
        RegisterDefaultStep(700, () => { WarRearTests.Run(); });
        RegisterDefaultStep(710, () => { SupplyNetworkTests.Run(); });
        RegisterDefaultStep(720, () => { FourSystemsIntegrationTests.RunAll(); });
        RegisterDefaultStep(730, () => { RebellionRecoveryTests.Run(); });
        RegisterDefaultStep(740, () => { ConquestAftermathTests.Run(); });
        RegisterDefaultStep(750, () => { PausedDoctrineTests.Run(); });
        RegisterDefaultStep(760, () => { GeopoliticalDynamicsTests.Run(); });
        RegisterDefaultStep(770, () => { FormableProclamationAiTests.Run(); });
        RegisterDefaultStep(780, () => { SmallerPolityTests.Run(); });
        RegisterDefaultStep(790, () => { HeadlinePopupTests.Run(); });
        RegisterDefaultStep(800, () => { OrganicBlocTests.Run(); });
        RegisterDefaultStep(810, () => { LateDiplomacyStagesTests.Run(); });
        RegisterDefaultStep(820, () => { DiplomaticMissionTests.Run(); OpinionWitnessTests.Run(); });
        RegisterDefaultStep(830, () => { CampaignAlertTests.Run(); StaffPortfolioTests.Run(); });
        RegisterDefaultStep(840, () => { ElectionTests.Run(); });
        RegisterDefaultStep(850, () => { RegionalAdministrationTests.Run(); });
        RegisterDefaultStep(860, () => { PoliticalIdentityTests.Run(); });
        RegisterDefaultStep(870, () => { PersonNameCatalogTests.Run(); });
        RegisterDefaultStep(880, () => { PerformanceRegressionTests.Run(); });
        RegisterDefaultStep(890, () => { DecisionResourceTests.Run(); });
        RegisterDefaultStep(900, () => { ServiceExerciseTests.Run(); });
        RegisterDefaultStep(910, () => { ArmyOrderObservationTests.Run(); });
        RegisterDefaultStep(920, () => { ShardConsolidationTests.Run(); });
        RegisterDefaultStep(930, () => { CatalogIdentityTests.Run(); ObserverAuthorityTests.Run(); });
        RegisterDefaultStep(940, () => { RulersAndSubjectsTests.Run(); MobilisationAndStockpileTests.RunAll(); });
        RegisterDefaultStep(950, () => { DependencyPlausibilityTests.Run(); });
        RegisterDefaultStep(960, () => { WorldCurseTests.Run(); });
        RegisterDefaultStep(970, () => { OwnershipComponentTests.Run(); });
        RegisterDefaultStep(980, () => { CoreRefactorTests.Run(); });
        RegisterDefaultStep(990, () => { AgendaWorkspaceTests.Run(); });
        RegisterDefaultStep(1000, () => { ParticipantWarObjectiveTests.Run(); });
        RegisterDefaultStep(1010, () => { WarAimResolveTests.Run(); });
        RegisterDefaultStep(1020, () => { TerritorialRecoveryTests.Run(); });
        RegisterDefaultStep(1030, () => { SecessionColourTests.Run(); });
        RegisterDefaultStep(1040, () => { GeneralsTests.Run(); });
        RegisterDefaultStep(1050, () => { CityTests.Run(); });
        RegisterDefaultStep(1060, () => { DesertionTests.Run(); });
        RegisterDefaultStep(1070, () => { CoupCivilWarTests.Run(); });
        RegisterDefaultStep(1080, () => { RescueTests.Run(); });
        RegisterDefaultStep(1090, () => { LandArrangementTests.Run(); });
        RegisterDefaultStep(1100, () => { TreasuryDynamicsTests.Run(); });
        RegisterDefaultStep(1110, () => { CustomsUnionTests.Run(); });
        RegisterDefaultStep(1120, () => { OrganizationCrisisTests.Run(); });
        RegisterDefaultStep(1130, () => { DefconTests.Run(); });
        RegisterDefaultStep(1140, () => { RescueTests.Run(); });
        RegisterDefaultStep(1150, () => { SovereignRemnantTests.Run(); });
        RegisterDefaultStep(1160, () => { WorldLayersTests.Run(); });
        RegisterDefaultStep(1170, () => { DiplomaticGestureTests.Run(); });
        RegisterDefaultStep(1180, () => { OverrunWarTests.Run(); });
        RegisterDefaultStep(1190, () => { FallenGroundTests.Run(); });
        RegisterDefaultStep(1200, () => { NationNumberingTests.Run(); });
        RegisterDefaultStep(1210, () => { ArmyGroupTidyTests.Run(); });
        RegisterDefaultStep(1220, () => { TerritorialFrontTests.Run(); });
        RegisterDefaultStep(1230, () => { RevoltOccupationTests.Run(); });
        RegisterDefaultStep(1240, () => { TerritorialDealProtectionTests.Run(); });
        RegisterDefaultStep(1250, () => { SituationStandoffTests.Run(); });
        RegisterDefaultStep(1260, () => { HybridWarBriefTests.Run(); });
        RegisterDefaultStep(1270, () => { CrisisLadderTests.Run(); });
        RegisterDefaultStep(1280, () => { ResourceCartelTests.Run(); });
        RegisterDefaultStep(1290, () => { ParliamentaryPoliticsTests.Run(); });
        RegisterDefaultStep(1300, () => { PeaceRulesDifferentialTests.Run(); });
        RegisterDefaultStep(1310, () => { CrisisGrantTests.Run(); WarScoreIndexTests.Run(); LogisticsIsolationTests.Run(); WarRhythmRulesTests.Run(); SupplyHedgeTests.Run(); });
        RegisterDefaultStep(1320, () => { CampaignIsolationTests.Run(); });
        RegisterDefaultStep(1330, () => { MovementPowerStagingTests.Run(); });
        RegisterDefaultStep(1340, () => { StabilityOutlookTests.Run(); });
        RegisterDefaultStep(1350, () => { DiplomaticParticipationTests.Run(); });
        RegisterDefaultStep(1360, () => { DiplomaticSymmetryTests.Run(); });
        RegisterDefaultStep(1370, () => { RegionalDemographyTests.Run(); });
        RegisterDefaultStep(1380, () => { DynamicAllianceTests.Run(); });
        RegisterDefaultStep(1390, () => { ConflictReadOnlyTests.Run(); });
        RegisterDefaultStep(1400, () => { AiDoctrineTests.Run(); });
        RegisterDefaultStep(1410, () => { AiTemperamentTests.Run(); });
        RegisterDefaultStep(1420, () => { CasusBelliTests.Run(); });
        RegisterDefaultStep(1430, () => { ScenarioManifestTests.Run(); });
        RegisterDefaultStep(1440, () => { CountryHistoryTests.Run(); });
        RegisterDefaultStep(1450, () => { CountryDefinitionTests.Run(); });
        RegisterDefaultStep(1460, () => { AllianceProgramOutcomeTests.Run(); });
        RegisterDefaultStep(1470, () => { ClaudeStateFingerprintTests.Run(); });
        RegisterDefaultStep(1480, () => { CampaignIntegrityTests.Run(); });
        RegisterDefaultStep(1490, () => { TerritorialClaimsTests.Run(); });
        RegisterDefaultStep(1500, () => { OrganizationTests.Run(); });
        RegisterDefaultStep(1510, () => { HouseholdTests.Run(); });
        RegisterDefaultStep(1520, () => { BrainDrainTests.Run(); });
        RegisterDefaultStep(1530, () => { DemographicExtinctionTests.Run(); });
        RegisterDefaultStep(1540, () => { DeterrenceTests.Run(); });
        RegisterDefaultStep(1550, () => { OccupationResistanceTests.Run(); });
        RegisterDefaultStep(1560, () => { PartitionTests.Run(); });
        RegisterDefaultStep(1570, () => { DynastyTests.Run(); });
        RegisterDefaultStep(1580, () => { DismantlementTests.Run(); });
        RegisterDefaultStep(1590, () => { DomesticCrisesTests.Run(); });
        RegisterDefaultStep(1600, () => { FederalFractureOutcomeTests.Run(); });
        RegisterDefaultStep(1610, () => { SovereignFinanceTests.Run(); });
        RegisterDefaultStep(1620, () => { AiFiscalPolicyTests.Run(); });
        RegisterDefaultStep(1630, () => { IncomePenaltyTests.Run(); });
        RegisterDefaultStep(1640, () => { ConstructionFundingTests.Run(); });
        RegisterDefaultStep(1650, () => { MovementMethodsTests.Run(); });
        RegisterDefaultStep(1660, () => { KinStatecraftTests.Run(); });
        RegisterDefaultStep(1670, () => { RevoltLineageTests.Run(); });
        RegisterDefaultStep(1680, () => { MovementCatalogTests.Run(); });
        RegisterDefaultStep(1690, () => { MovementRepresentationTests.Run(); });
        RegisterDefaultStep(1700, () => { WarStalemateTests.Run(); });
        RegisterDefaultStep(1710, () => { ParityWave1Tests.Run(); });
        RegisterDefaultStep(1720, () => { CoalitionAdvanceTests.Run(); });
        RegisterDefaultStep(1730, () => { MarketShortageTests.Run(); });
        RegisterDefaultStep(1740, () => { MovementPowerStateTests.Run(); });
        RegisterDefaultStep(1750, () => { MovementPowerFingerprintTests.Run(); });
        RegisterDefaultStep(1760, () => { PoliticalPolicyTests.Run(); });
        RegisterDefaultStep(1770, () => { PolicyGoalRulesTests.Run(); });
        RegisterDefaultStep(1780, () => { NationalCourseTests.Run(); });
        RegisterDefaultStep(1790, () => { RegionalContentTests.Run(); });
        RegisterDefaultStep(1800, () => { SocialMemoryTests.Run(); });
        RegisterDefaultStep(1810, () => { CivicCampaignTests.Run(); });
        RegisterDefaultStep(1820, () => { MovementZoneTests.Run(); });
        RegisterDefaultStep(1830, () => { WorkerBackendTests.Run(); });
        RegisterDefaultStep(1840, () => { ArmyCommandLocalizationTests.Run(); });
        RegisterDefaultStep(1850, () => { FleetNameLocalizationTests.Run(); });
        RegisterDefaultStep(1860, () => { FlagModdingTests.Run(); });
        RegisterDefaultStep(1870, () => { FlagArtworkTests.Run(); });
        RegisterDefaultStep(1880, () => { FlagDesignTests.Run(); });
        RegisterDefaultStep(1890, () => { CountryEditorTests.Run(); });
        RegisterDefaultStep(1900, () => { CountryNameTests.Run(); });
        RegisterDefaultStep(1910, () => { UnionCohesionTests.Run(); });
        RegisterDefaultStep(1920, () => { UnionCohesionMutationTests.Run(); });
        RegisterDefaultStep(1930, () => { UnionCohesionTests.RunOnMap(); });
        RegisterDefaultStep(1940, () => { DemographyTests.Run(); });
        RegisterDefaultStep(1950, () => { StructuredDataTests.Run(); });
        RegisterDefaultStep(1960, () => { YamlTwinTests.Run(); });
        RegisterDefaultStep(1970, () => { EventCadenceTests.Run(); });
        RegisterDefaultStep(1980, () => { PresentationReloadTests.Run(); });
        RegisterDefaultStep(1990, () => { UltimatumTests.Run(); });
        RegisterDefaultStep(2000, () => { DealTests.Run(); });
        RegisterDefaultStep(2010, () => { DealNegotiationTests.Run(); });
        RegisterDefaultStep(2020, () => { DealAutoAnswerTests.Run(); });
        RegisterDefaultStep(2030, () => { TradeTermsTests.Run(); });
        RegisterDefaultStep(2040, () => { SituationReportTests.Run(); });
        RegisterDefaultStep(2050, () => { GovernmentPromiseTests.Run(); });
        RegisterDefaultStep(2060, () => { AiPromiseTests.Run(); });
        RegisterDefaultStep(2070, () => { VersionLabelTests.Run(); });
        RegisterDefaultStep(2080, () => { DiplomaticActTests.Run(); });
        RegisterDefaultStep(2090, () => { TacticalDoctrineTests.Run(); });
        RegisterDefaultStep(2100, () => { DoctrineExpansionTests.Run(); });
        RegisterDefaultStep(2110, () => { CampaignCosmeticsTests.Run(); });
        RegisterDefaultStep(2120, () => { StrategicDoctrineTests.Run(); });
        RegisterDefaultStep(2130, () => { MusicSituationTests.Run(); });
        RegisterDefaultStep(2140, () => { MusicTransitionTests.Run(); });
        RegisterDefaultStep(2150, () => { WeatherReadabilityTests.Run(); });
        RegisterDefaultStep(2160, () => { MediaLibraryTests.Run(); });
        RegisterDefaultStep(2170, () => { DefconTests.Run(); });
        RegisterDefaultStep(2180, () => { ResponsiveHudLayoutTests.Run(); });
        RegisterDefaultStep(2190, () => { IdentityResearchTests.Run(); });
        RegisterDefaultStep(2200, () => { RestorationTests.Run(); });
        RegisterDefaultStep(2210, () => { QualityProfileTests.Run(); });
        RegisterDefaultStep(2220, () => { PeaceIntentTests.Run(); });
        RegisterDefaultStep(2230, () => { WarAimsTests.Run(); });
        RegisterDefaultStep(2240, () => { CombatTempoTests.Run(); });
        RegisterDefaultStep(2250, () => { DefectionsTests.Run(); });
        RegisterDefaultStep(2260, () => { CivilWarBudgetTests.Run(); });
        RegisterDefaultStep(2270, () => { StrategicCueQueueTests.Run(); });
        RegisterDefaultStep(2280, () => { NetworkSyncCadenceTests.Run(); });
        RegisterDefaultStep(2290, () => { NetworkLockstepTests.Run(); });
        RegisterDefaultStep(2300, () => { NetworkRecoveryTests.Run(); });
        RegisterDefaultStep(2310, () => { NetworkCatchUpTests.Run(); });
        RegisterDefaultStep(2320, () => { HomelandIntegrationTests.Run(); });
        RegisterDefaultStep(2330, () => { NetworkSnapshotDeliveryTests.Run(); });
        RegisterDefaultStep(2340, () => { StatecraftSnapshotTests.Run(); });
        RegisterDefaultStep(2350, () => { StagedStepBoundaryTests.Run(); });
        RegisterDefaultStep(2360, () => { ModFrontendActionTests.Run(); });
        RegisterDefaultStep(2370, () => { ForeignAssistancePresentationTests.Run(); });
        RegisterDefaultStep(2380, () => { ArmyAssaultPresentationTests.Run(); });
        RegisterDefaultStep(2390, () => { AssaultRulesBackendTests.Run(); });
        RegisterDefaultStep(2400, () => { GarrisonPlannerTests.Run(); });
        RegisterDefaultStep(2410, () => { HistoricalTimelineTests.Run(); });
        RegisterDefaultStep(2420, () => { WarEmergencyTests.Run(); });
        RegisterDefaultStep(2430, () => { ForeignAssistanceTests.Run(); });
        RegisterDefaultStep(2440, () => { NewsFeedTests.Run(); });
        RegisterDefaultStep(2450, () => { ChartMarkersTests.Run(); });
        RegisterDefaultStep(2460, () => { RunTerrainCatalogChecks(); });
        RegisterDefaultStep(2470, () => { InterfaceSkin.ResetCatalog(); });
        RegisterDefaultStep(2480, () => { ShellCoherenceTests.Run(); });
        RegisterDefaultStep(2490, () => { LayoutWorkbenchTests.Run(); });
        RegisterDefaultStep(2500, () => { OpportunityAtlasTests.Run(); });
        RegisterDefaultStep(2510, () => { StrategicReportsTests.Run(); });
        RegisterDefaultStep(2520, () => { SupplyStressTests.Run(); });
        RegisterDefaultStep(2530, () => { TechnologyTreeTests.Run(); });
        RegisterDefaultStep(2540, () => { ResearchWorkspaceTests.Run(); });
        RegisterDefaultStep(2550, () => { ResearchRouteTests.Run(); });
        RegisterDefaultStep(2560, () => { AtlasSurfaceTests.Run(); });
        RegisterDefaultStep(2570, () => { LayoutKeyPinTests.Run(); });
        RegisterDefaultStep(2580, () => { InterfaceMaterialTests.Run(); });
        RegisterDefaultStep(2590, () => { SkinOverlayTests.Run(); });
        RegisterDefaultStep(2600, () => { IconCategoryTests.Run(); });
        RegisterDefaultStep(2610, () => { HudResourceTests.Run(); });
        RegisterDefaultStep(2620, () => { DatePlateTests.Run(); });
        RegisterDefaultStep(2630, () => { AlertListTests.Run(); });
        RegisterDefaultStep(2640, () => { NewShellTests.RunAll(); });
        RegisterDefaultStep(2650, () => { WarTollTests.Run(); });
        RegisterDefaultStep(2660, () => { DiplomacyPreviewTests.Run(); });
        RegisterDefaultStep(2670, () => { ProductionUnitsTests.Run(); });
        RegisterDefaultStep(2680, () => { InterfaceIconTests.Run(); });
        RegisterDefaultStep(2690, () => { GC.Collect(); });
        RegisterDefaultStep(2700, () => { GC.WaitForPendingFinalizers(); });
        RegisterDefaultStep(2710, () => { ScenarioMapDraftTests.Run(); });
        RegisterDefaultStep(2720, () => { GC.Collect(); });
        RegisterDefaultStep(2730, () => { GC.WaitForPendingFinalizers(); });
        RegisterDefaultStep(2740, RunCoreScenarioSuites);
    }

    private static void RunTerrainCatalogChecks()
    {
        TerrainCatalog.ResetCatalog();
        var terrainErrors = new List<string>();
        TerrainCatalog.ApplyDirectory(GameDataLoader.DataFolder().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, terrainErrors);
        Check(terrainErrors.Count == 0 && TerrainCatalog.All.Count > 1 && File.Exists(TerrainCatalog.TerrainPath),
            "terrain assets resolve with a trailing directory separator");
        if (OperatingSystem.IsWindows())
        {
            var extendedRoot = Path.GetFullPath(GameDataLoader.DataFolder());
            if (!extendedRoot.StartsWith(@"\\?\")) extendedRoot = @"\\?\" + extendedRoot;
            terrainErrors.Clear();
            TerrainCatalog.ApplyDirectory(extendedRoot, terrainErrors);
            Check(terrainErrors.Count == 0 && TerrainCatalog.TerrainPath.StartsWith(@"\\?\") &&
                File.Exists(TerrainCatalog.TerrainPath), "terrain resolves Godot extended Windows paths");
            TerrainCatalog.ResetCatalog();
        }
    }

    private static void RunCoreScenarioSuites()
    {
        using var navigationDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(),
            "gui", "20_campaign_structure.json")), GameDataLoader.ForgivingJson);
        var declaredNavigation = navigationDocument.RootElement.GetProperty("campaign").GetProperty("navigation")
            .EnumerateArray().Select(row => row.GetProperty("screen").GetString()).ToArray();
        Check(declaredNavigation.Length > 0 && CampaignUiCatalog.NavigationItems.Select(row => row.Screen).SequenceEqual(declaredNavigation),
            "navigation reads every declared destination from game/gui in order");
        Check(CampaignUiCatalog.For("Decisions").Tabs.Any(t => t.Id == "Formables"), "formables tab registered");
        Check(CampaignUiCatalog.For("Decisions").Links.Length == 0, "no spurious decision links");
        var errors = new List<string>();
        InterfaceSkin.Merge("{\"campaign\":{\"screens\":{\"Nation\":{\"links\":[]}}}}", "test", errors);
        Check(CampaignUiCatalog.For("Nation").Links.Length == 0 && CampaignUiCatalog.For("Nation").Tabs.Length == 3, "overlay replaces array and inherits omitted tabs");
        InterfaceSkin.Merge("{\"campaign\":{\"navigation\":[{\"screen\":\"DeleteEverything\"}]}}", "test", errors);
        Check(errors.Count > 0 && CampaignUiCatalog.NavigationItems.Count == 0, "unknown actions do not create dead buttons");
        InterfaceSkin.ResetCatalog();
        Check(!CampaignUiCatalog.LinkVisible(new("Govern", "government", "own", "Government", ""), "Government", true), "foreign government context excluded");
        FormableNations.ResetCatalog();
        Check(new[] { "united_korea", "central_asian_union", "benelux_union", "nordic_union", "iberian_union", "baltic_union" }
            .All(id => FormableNations.Find(id) is not null), "six base unions loaded alongside additional content");
        foreach (var d in FormableNations.All)
            foreach (var tag in d.RequiredTags) Check(ProvinceOwnership.ProvincesOf(tag).Length > 0, "territory resolves " + tag);
        var c = Campaign.CreateBootstrap(42);
        var n = c.NationByTag("KOR"); c.ConfigureSinglePlayer(n.Id);
        SpriteCatalog.Load(); FlagCatalog.ResetCatalog();
        var originalGovernment = n.Government; var originalIdeology = n.Ideology;
        var fingerprint = CampaignFingerprint.Of(c);
        foreach (var ideology in IdeologyCatalog.All)
        {
            Check(SpriteCatalog.Has("icon_ideology_" + ideology.Id), "icon available " + ideology.Id);
            var preview = FlagCatalog.ResolvePreview(c, n, ideology: ideology.Id);
            n.Ideology = ideology.Id;
            Check(preview == FlagCatalog.Resolve(c, n), "flag preview equals adopted ideology " + ideology.Id);
            n.Ideology = originalIdeology;
        }
        foreach (var government in GovernmentCatalog.All)
        {
            var preview = FlagCatalog.ResolvePreview(c, n, government.Id);
            n.Government = government.Id;
            Check(preview == FlagCatalog.Resolve(c, n), "flag preview equals adopted government " + government.Id);
            n.Government = originalGovernment;
        }
        Check(fingerprint == CampaignFingerprint.Of(c), "flag previews leave campaign unchanged");
        var korea = FormableNations.Find("united_korea");
        Check(!FormableNations.Evaluate(c, n, korea).CanForm, "map not ready blocks formation");
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        Check(!FormableNations.Evaluate(c, n, korea).CanForm, "divided Korea blocked");
        c.Wars.Clear();
        var provinces = korea.RequiredTags.SelectMany(ProvinceOwnership.ProvincesOf).ToArray();
        foreach (var p in provinces) c.ProvinceController[p] = (ushort)n.MapIndex;
        Check(!FormableNations.Evaluate(c, n, korea).CanForm, "occupation alone cannot form");
        foreach (var p in provinces) map.ProvinceToNation[p] = (ushort)n.MapIndex;
        n.Stability = .8f; n.Treasury = 10000;
        Check(FormableNations.Evaluate(c, n, korea).CanForm, "ownership control stability peace and treasury allow formation");
        n.Stability = .2f;
        new FormNationCommand { IssuerId = n.Id, FormableId = korea.Id }.Apply(c);
        Check(n.FormedNationId == "", "command rechecks stale UI conditions");
        n.Stability = .8f; n.Treasury = 0;
        Check(!FormableNations.Evaluate(c, n, korea).CanForm, "insufficient treasury blocks");
        n.Treasury = 10000;
        var treasury = n.Treasury; var cost = FormableNations.Evaluate(c, n, korea).Cost;
        var ownerBefore = (ushort[])map.ProvinceToNation.Clone(); var id = n.Id; var tagBefore = n.Tag;
        var command = new FormNationCommand { IssuerId = n.Id, FormableId = korea.Id };
        var formationPreview = FlagCatalog.ResolveFormationPreview(c, n, korea.Id);
        Check(NetworkCommandCodec.TryEncode(command, out var kind, out var json) && NetworkCommandCodec.TryDecode(kind, json, out _), "network command round trip");
        command.Apply(c);
        Check(formationPreview == FlagCatalog.Resolve(c, n), "formation preview equals resulting flag");
        Check(n.FormedNationId == korea.Id && n.Name == korea.Name && n.Id == id && n.Tag == tagBefore, "identity changes without breaking stable identifiers");
        Check(Math.Abs(n.Treasury - (treasury - cost)) < .001f && ownerBefore.SequenceEqual(map.ProvinceToNation), "cost deducted and borders unchanged");
        var after = n.Treasury; command.Apply(c);
        Check(n.Treasury == after, "duplicate proclamation cannot charge or reward twice");
        var snapshot = SaveSystem.ExportNetworkSnapshot(c, "tests");
        var restored = SaveSystem.ImportNetworkSnapshot(snapshot, "tests");
        Check(restored.Success && restored.Campaign.NationById(id).FormedNationId == korea.Id && restored.Campaign.NationById(id).Name == korea.Name, "campaign snapshot preserves identity");
        // Native save uses the same payload, but exercise disk save/load independently as well.
        var temp = Path.Combine(Path.GetTempPath(), "dof-formable-test-" + Guid.NewGuid().ToString("N"));
        SaveSystem.Save(temp, "formation", c, "tests");
        var saved = SaveSystem.Load(temp, "formation", "tests");
        Check(saved.Success && saved.Campaign.NationById(id).FormedNationId == korea.Id, "disk save/load preserves proclamation");
        Console.WriteLine("Test saves retained: " + temp);
        TestScenarios(c, map, temp);
    }

    static void TestScenarios(Campaign c, ProvinceTopology map, string temp)
    {
        var before = CampaignFingerprint.Of(c);
        var world = SaveSystem.ExportScenarioJson(c, "test_world");
        Check(before == CampaignFingerprint.Of(c), "scenario export is read-only");
        var restored = SaveSystem.ImportScenarioJson(world, "test_world", map.ProvinceCount);
        restored.Date = new GameDate(1836, 1, 1);
        restored.Player.Treasury = 7.25f;
        restored.Defines.OrderSettleDays = 77;
        restored.Wars.Clear();
        restored.Armies.Clear();
        restored.AttachProvinceTopology(map);
        Check(restored.ScenarioId == "test_world" && restored.Wars.Count == 0 && restored.Armies.Count == 0,
            "scenario import/attach does not seed contemporary wars or armies");
        var independent = SaveSystem.ExportScenarioJson(restored, "alternate_world");
        var second = SaveSystem.ImportScenarioJson(independent, "alternate_world", map.ProvinceCount);
        Check(second.Date.Year == 1836 && second.Player.Treasury == 7.25f && second.Defines.OrderSettleDays == 77,
            "scenario date treasury and rules isolated from 2024");
        second.AttachProvinceTopology(map);
        Check(second.ScenarioTerritories.Count > 0 && second.ProvinceController.SequenceEqual(restored.ProvinceController), "scenario political layer preserved");
        SaveSystem.Save(temp, "scenario", second, "tests");
        var loaded = SaveSystem.Load(temp, "scenario", "tests").Campaign;
        Check(loaded.ScenarioId == "alternate_world" && loaded.Defines.OrderSettleDays == 77 && loaded.Wars.Count == 0,
            "save roundtrip preserves independent scenario rules");
        var rejected = false;
        try { SaveSystem.ImportScenarioJson(independent, "bad", map.ProvinceCount + 1); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "mismatched map rejected before adoption");
        var root = Path.Combine(temp, "editor");
        ContentWorkbench.EnsureMod(root);
        var path = ContentWorkbench.Write(root, "gui/test.json", "{\"value\":1}");
        ContentWorkbench.Write(root, "gui/test.json", "{\"value\":2}");
        Check(File.ReadAllText(path).Contains("2") && File.ReadAllText(path + ".bak").Contains("1"), "editor atomic overwrite keeps backup");
        rejected = false;
        try { ContentWorkbench.Write(root, "gui/../../outside.json", "{}"); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(temp, "outside.json")), "editor path traversal rejected");
        Check(ContentWorkbench.SetValue("{\"a/b\":[1]}", "/a~1b/0", "42").Contains("42"), "property editor supports escaped paths and arrays");
        ContentWorkbench.Write(root, "scenarios/test/world.json", independent);
        ContentWorkbench.Write(root, "scenarios/test/scenario.json", "{\"id\":\"test\",\"name\":\"Test world\",\"world\":\"world.json\"}");
        var entries = ScenarioLibrary.Discover(Array.Empty<string>(), Path.Combine(root, "scenarios"));
        Check(entries.Any(e => e.Id == "2024") && entries.Any(e => e.Id == "test"), "library discovers base and independent scenarios");
        var scenario = ScenarioLibrary.Create(entries.Single(e => e.Id == "test"), 123, map.ProvinceCount);
        Check(scenario.Wars.Count == 0 && scenario.Date.Year == 1836, "library loads complete scenario without bootstrap");
        Check(scenario.Seed == 123 && scenario.Random.State == new SimRandom(123).State &&
              scenario.Player.Treasury == 7.25f,
            "chosen seed reseeds an authored scenario without replacing its historical data");
        Loc.ResetCatalog(); Loc.Language = "russian";
        var rus = new Nation { Tag = "RUS", Name = "Russia" };
        Check(rus.DisplayName == "Россия" && rus.MapName == "Россия", "Russian identity and map names use stable keys");
        Loc.Language = "english";
        Check(rus.DisplayName == "Russia", "missing localized stable key falls back to country name");
    }

    private static void InspectSave(string[] args)
    {
        var folder = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : @"C:\Users\User\Documents\Deal or Force\save games";
        var slot = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "campaign_20261021_215222";
        Console.WriteLine($"Loading save: {folder} / {slot} ...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = SaveSystem.Load(folder, slot, "");
        sw.Stop();
        Console.WriteLine($"Save loaded in {sw.ElapsedMilliseconds} ms. Success: {result.Success}, Warning: {result.Warning}");
        if (result.Campaign is null) return;
        var c = result.Campaign;
        Console.WriteLine($"Campaign Day: {c.ElapsedDays}, Date: {c.Date:yyyy-MM-dd}");
        Console.WriteLine($"Player: {c.Player?.Tag} ({c.Player?.DisplayName}), Treasury: {c.Player?.Treasury}");
        Console.WriteLine($"Nations: {c.Nations.Count} (Alive: {c.Nations.Count(n => c.HasOwnedLand(n))}, Fallen: {c.Fallen.Count}, Landless: {c.Nations.Count(n => n.Landless)})");
        Console.WriteLine($"States: {c.States.Count}, Armies: {c.Armies.Count}, Wars: {c.Wars.Count}, Battles: {c.Battles.Count}");
        Console.WriteLine($"FrontLines: {c.FrontLines.Count}, Organizations: {c.Organizations.Count}, PendingEvents: {c.PendingEvents.Count}");
        Console.WriteLine($"Chronicle entries: {c.Chronicle.Entries.Count}");
        Console.WriteLine($"Armies in battle: {c.Armies.Count(a => a.InBattle)}, Armies with route: {c.Armies.Count(a => a.Route.Count > 0)}");

        var totalArmies = c.Armies.Count;
        var totalPersonnel = c.Armies.Sum(a => (long)a.Personnel);
        Console.WriteLine($"Total Armies: {totalArmies}, Total Personnel: {totalPersonnel:N0}");

        SimProfiler.Reset();
        c.IsPaused = false;
        Console.WriteLine("\n--- Ticking 5 days and measuring profile ---");
        for (int i = 0; i < 5; i++)
        {
            sw.Restart();
            c.TickDay();
            sw.Stop();
            Console.WriteLine($"Day {c.ElapsedDays} ({c.Date:yyyy-MM-dd}) ticked in {sw.ElapsedMilliseconds} ms ({sw.ElapsedTicks} ticks)");
        }

        Console.WriteLine("\n--- SimProfiler Timings (top 30) ---");
        foreach (var entry in SimProfiler.All.OrderByDescending(p => p.TotalMilliseconds).Take(30))
        {
            Console.WriteLine($"  {entry.Name,-45}: {entry.TotalMilliseconds:0.00} ms total (runs: {entry.Runs}, mean: {entry.MeanMilliseconds:0.00} ms, alloc: {entry.AllocatedBytes / 1024} KB)");
        }
    }
}
