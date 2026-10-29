using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using DealOrForce.Simulation;

static class Program
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static void Main(string[] args)
    {
        if (args.Contains("--exposure-batch-only")) { ExposureBatchTests.Run(); return; }
        if (args.Length == 0) ExposureBatchTests.Run();
        if (args.Contains("--equipment-replenishment-only")) { EquipmentReplenishmentTests.Run(); return; }
        if (args.Length == 0) EquipmentReplenishmentTests.Run();
        if (args.Contains("--covert-politics-only")) { CovertPoliticalTests.Run(); return; }
        if (args.Length == 0) CovertPoliticalTests.Run();
        if (args.Contains("--government-supply-only")) { GovernmentSupplyTests.Run(); return; }
        if (args.Length == 0) GovernmentSupplyTests.Run();
        if (args.Contains("--supply-network-only")) { SupplyNetworkTests.Run(); return; }
        if (args.Contains("--isolated-depot-only")) { IsolatedDepotTests.Run(); return; }
        if (args.Length == 0) IsolatedDepotTests.Run();
        if (args.Contains("--supply-days-only")) { SupplyDaysTests.Run(); return; }
        if (args.Length == 0) SupplyDaysTests.Run();
        if (args.Contains("--news-facts-only")) { NewsFactTests.Run(); return; }
        if (args.Length == 0) NewsFactTests.Run();
        if (args.Contains("--campaign-chronicle-only")) { CampaignChronicleTests.Run(); return; }
        if (args.Length == 0) CampaignChronicleTests.Run();
        if (args.Contains("--strategic-cover-only")) { StrategicCoverTests.Run(); return; }
        if (args.Length == 0) StrategicCoverTests.Run();
        if (args.Contains("--war-rear-only")) { WarRearTests.Run(); return; }
        if (args.Length == 0) WarRearTests.Run();
        if (args.Length == 0) SupplyNetworkTests.Run();
        if (args.Contains("--four-systems-only")) { FourSystemsIntegrationTests.RunAll(); return; }
        if (args.Length == 0) FourSystemsIntegrationTests.RunAll();
        if (args.Contains("--inspect-save")) { InspectSave(args); return; }
        if (args.Contains("--revolt-save")) { RebellionRecoveryTests.InspectSave(args); return; }
        if (args.Contains("--revolt-recovery-only")) { RebellionRecoveryTests.Run(); return; }
        if (args.Length == 0) RebellionRecoveryTests.Run();
        if (args.Contains("--paused-doctrines-only")) { PausedDoctrineTests.Run(); return; }
        if (args.Length == 0) PausedDoctrineTests.Run();
        if (args.Contains("--geopolitical-dynamics-only")) { GeopoliticalDynamicsTests.Run(); return; }
        if (args.Length == 0) GeopoliticalDynamicsTests.Run();
        if (args.Contains("--formable-proclamation-ai-only")) { FormableProclamationAiTests.Run(); return; }
        if (args.Length == 0) FormableProclamationAiTests.Run();
        if (args.Contains("--smaller-polity-only")) { SmallerPolityTests.Run(); return; }
        if (args.Length == 0) SmallerPolityTests.Run();
        if (args.Contains("--organic-blocs-only")) { OrganicBlocTests.Run(); return; }
        if (args.Length == 0) OrganicBlocTests.Run();
        if (args.Contains("--diplomatic-missions-only")) { DiplomaticMissionTests.Run(); return; }
        if (args.Length == 0) DiplomaticMissionTests.Run();
        if (args.Contains("--campaign-alerts-only")) { CampaignAlertTests.Run(); StaffPortfolioTests.Run(); return; }
        if (args.Length == 0) { CampaignAlertTests.Run(); StaffPortfolioTests.Run(); }
        if (args.Contains("--elections-only")) { ElectionTests.Run(); return; }
        if (args.Length == 0) ElectionTests.Run();
        if (args.Contains("--regional-administration-only")) { RegionalAdministrationTests.Run(); return; }
        if (args.Length == 0) RegionalAdministrationTests.Run();
        if (args.Contains("--political-identity-only")) { PoliticalIdentityTests.Run(); return; }
        if (args.Length == 0) PoliticalIdentityTests.Run();
        if (args.Contains("--person-names-only")) { PersonNameCatalogTests.Run(); return; }
        if (args.Length == 0) PersonNameCatalogTests.Run();
        if (args.Contains("--performance-regressions-only")) { PerformanceRegressionTests.Run(); return; }
        if (args.Length == 0) PerformanceRegressionTests.Run();
        if (args.Contains("--decision-resources-only")) { DecisionResourceTests.Run(); return; }
        if (args.Length == 0) DecisionResourceTests.Run();
        if (args.Contains("--service-exercises-only")) { ServiceExerciseTests.Run(); return; }
        if (args.Length == 0) ServiceExerciseTests.Run();
        if (args.Contains("--order-observation-only")) { ArmyOrderObservationTests.Run(); return; }
        if (args.Length == 0) ArmyOrderObservationTests.Run();
        if (args.Contains("--shard-consolidation-only")) { ShardConsolidationTests.Run(); return; }
        if (args.Length == 0) ShardConsolidationTests.Run();
        if (args.Contains("--audit-movement-flags")) { FlagArtworkTests.AuditMovementBanners(); return; }
        if (args.Contains("--audit-content-identities")) { ContentIdentityAudit.Run(); return; }
        if (args.Contains("--catalogue-identity-only")) { CatalogIdentityTests.Run(); return; }
        if (args.Contains("--observer-authority-only")) { ObserverAuthorityTests.Run(); return; }
        if (args.Length == 0) { CatalogIdentityTests.Run(); ObserverAuthorityTests.Run(); }
        if (args.Contains("--ownership-kernel-only")) { OwnershipComponentTests.Run(); return; }
        if (args.Contains("--core-refactor-only")) { CoreRefactorTests.Run(); return; }
        if (args.Contains("--agenda-only")) { AgendaWorkspaceTests.Run(); return; }
        if (args.Contains("--extinction-only")) { DemographicExtinctionTests.Run(); return; }
        if (args.Contains("--deterrence-only")) { DeterrenceTests.Run(); return; }
        if (args.Contains("--resistance-only")) { OccupationResistanceTests.Run(); return; }
        if (args.Contains("--partition-only")) { PartitionTests.Run(); return; }
        if (args.Contains("--dynasty-only")) { DynastyTests.Run(); return; }
        if (args.Contains("--rulers-subjects-only")) { RulersAndSubjectsTests.Run(); return; }
        if (args.Contains("--macao-only")) { MacaoProbe.Run(); return; }
        if (args.Contains("--dependencies-only")) { DependencyPlausibilityTests.Run(); return; }
        if (args.Contains("--curses-only")) { WorldCurseTests.Run(); return; }
        if (args.Contains("--subject-growth-only")) { SubjectGrowthProbe.Run(); return; }
        if (args.Contains("--micro-enclave-only")) { MicroEnclaveGrowthTests.Run(); return; }
        if (args.Contains("--tautology-only")) { TautologyNameProbe.Run(); return; }
        if (args.Contains("--founding-names-only")) { FoundingNameTests.Run(); return; }
        if (args.Contains("--flag-survey-only")) { FlagSurvey.Run(); return; }
        if (args.Contains("--tick-bench-only")) { TickBench.Run(); return; }
        if (args.Contains("--name-survey-only")) { FoundingNameSurvey.Run(); return; }
        if (args.Contains("--diaspora-only")) { DiasporaProbe.Run(); return; }
        if (args.Contains("--hot-wars-names-only")) { HotWarAndNamePoolTests.Run(); return; }
        if (args.Contains("--annexation-only")) { AnnexationTests.Run(); return; }
        if (args.Contains("--mobilisation-only")) { MobilisationAndStockpileTests.RunAll(); return; }
        if (args.Length == 0) { RulersAndSubjectsTests.Run(); MobilisationAndStockpileTests.RunAll(); }
        if (args.Length == 0) DependencyPlausibilityTests.Run();
        if (args.Length == 0) WorldCurseTests.Run();
        if (args.Contains("--ownership-native-only")) { OwnershipComponentTests.Run(true); return; }
        if (args.Contains("--ownership-fallback-only")) { OwnershipComponentTests.Run(false); return; }
        if (args.Contains("--ownership-kernel-fixtures")) { OwnershipComponentTests.RunFixtures(args.Last()); return; }
        if (args.Length == 0) OwnershipComponentTests.Run();
        if (args.Length == 0) CoreRefactorTests.Run();
        if (args.Length == 0) AgendaWorkspaceTests.Run();
        if (args.Contains("--diplomacy-preview-only")) { DiplomacyPreviewTests.Run(); return; }
        if (args.Contains("--atlas-surfaces-only")) { AtlasSurfaceTests.Run(); return; }
        if (args.Contains("--research-workspace-only")) { TechnologyTreeTests.Run(); ResearchWorkspaceTests.Run(); return; }
        if (args.Contains("--regional-demography-only")) { RegionalDemographyTests.Run(); return; }
        if (args.Contains("--peace-intent-only")) { PeaceIntentTests.Run(); return; }
        if (args.Contains("--war-aims-only")) { WarAimsTests.Run(); CombatTempoTests.Run(); return; }
        if (args.Contains("--combat-tempo-only")) { CombatTempoTests.Run(); return; }
        if (args.Contains("--defections-only")) { DefectionsTests.Run(); return; }
        if (args.Contains("--participant-objective-only")) { ParticipantWarObjectiveTests.Run(); return; }
        if (args.Length == 0) ParticipantWarObjectiveTests.Run();
        if (args.Contains("--war-aim-resolve-only")) { WarAimResolveTests.Run(); return; }
        if (args.Length == 0) WarAimResolveTests.Run();
        if (args.Contains("--worker-fixture")) { WorkerBackendTests.Fixture(args.Last()); return; }
        if (args.Contains("--campaign-toggles-only")) { CampaignToggleTests.Run(); return; }
        if (args.Contains("--conciliation-only")) { ConciliationTests.Run(); return; }
        if (args.Contains("--corridor-report-only")) { CorridorReportTests.Run(); return; }
        if (args.Contains("--movement-voice-only")) { MovementVoiceTests.Run(); return; }
        if (args.Contains("--host-patience-only")) { HostPatienceTests.Run(); return; }
        if (args.Contains("--peace-compact-only")) { PeaceCompactTests.Run(); return; }
        if (args.Contains("--army-standdown-only")) { ArmyStandDownTests.Run(); return; }
        if (args.Contains("--held-conquest-peace-only")) { HeldConquestPeaceTests.Run(); return; }
        if (args.Contains("--border-tidy-only")) { BorderTidyTests.Run(); return; }
        if (args.Contains("--delegated-front-only")) { DelegatedFrontTests.Run(); return; }
        if (args.Contains("--kindred-only")) { KindredLandTests.Run(); return; }
        if (args.Contains("--player-ultimatum-only")) { PlayerUltimatumTests.Run(); return; }
        if (args.Contains("--ultimatum-only")) { UltimatumTests.Run(); return; }
        if (args.Contains("--enclave-swap-only")) { EnclaveSwapTests.Run(); return; }
        if (args.Contains("--integrity-only")) { CampaignIntegrityTests.Run(); return; }
        if (args.Contains("--conflict-preview-only")) { ConflictReadOnlyTests.Run(); return; }
        if (args.Contains("--living-world-fingerprint-only")) { ClaudeStateFingerprintTests.Run(); return; }
        if (args.Contains("--staged-boundary-only")) { StagedStepBoundaryTests.Run(); return; }
        if (args.Contains("--mod-frontend-only")) { ModFrontendActionTests.Run(); return; }
        if (args.Contains("--assistance-presentation-only")) { ForeignAssistancePresentationTests.Run(); return; }
        if (args.Contains("--assault-presentation-only")) { ArmyAssaultPresentationTests.Run(); return; }
        if (args.Contains("--assault-rules-only")) { AssaultRulesBackendTests.Run(); return; }
        if (args.Contains("--garrison-only")) { GarrisonPlannerTests.Run(); return; }
        if (args.Contains("--historical-only")) { HistoricalTimelineTests.Run(); return; }
        if (args.Contains("--war-emergency-only")) { WarEmergencyTests.Run(); return; }
        if (args.Contains("--foreign-assistance-only")) { ForeignAssistanceTests.Run(); return; }
        if (args.Contains("--deals-only")) { DealTests.Run(); TerritorialDealProtectionTests.Run(); return; }
        if (args.Contains("--territorial-recovery-only")) { TerritorialRecoveryTests.Run(); return; }
        if (args.Length == 0) TerritorialRecoveryTests.Run();
        if (args.Contains("--secession-colours-only")) { SecessionColourTests.Run(); return; }
        if (args.Length == 0) SecessionColourTests.Run();
        if (args.Contains("--occupied-uprising-only")) { RevoltOccupationTests.Run(); return; }
        if (args.Contains("--overrun-war-only")) { OverrunWarTests.Run(); return; }
        if (args.Contains("--generals-only")) { GeneralsTests.Run(); return; }
        if (args.Length == 0) GeneralsTests.Run();
        if (args.Contains("--desertion-only")) { DesertionTests.Run(); return; }
        if (args.Length == 0) DesertionTests.Run();
        if (args.Contains("--coup-war-only")) { CoupCivilWarTests.Run(); return; }
        if (args.Length == 0) CoupCivilWarTests.Run();
        if (args.Contains("--land-only")) { LandArrangementTests.Run(); return; }
        if (args.Length == 0) LandArrangementTests.Run();
        if (args.Contains("--treasury-only")) { TreasuryDynamicsTests.Run(); return; }
        if (args.Length == 0) TreasuryDynamicsTests.Run();
        if (args.Contains("--customs-only")) { CustomsUnionTests.Run(); return; }
        if (args.Length == 0) CustomsUnionTests.Run();
        if (args.Contains("--world-layers-only")) { WorldLayersTests.Run(); return; }
        if (args.Length == 0) WorldLayersTests.Run();
        if (args.Contains("--gestures-only")) { DiplomaticGestureTests.Run(); return; }
        if (args.Length == 0) DiplomaticGestureTests.Run();
        if (args.Length == 0) OverrunWarTests.Run();
        if (args.Contains("--territorial-front-only")) { TerritorialFrontTests.Run(); return; }
        if (args.Length == 0) TerritorialFrontTests.Run();
        if (args.Length == 0) RevoltOccupationTests.Run();
        if (args.Length == 0) TerritorialDealProtectionTests.Run();
        if (args.Contains("--negotiations-only")) { DealNegotiationTests.Run(); return; }
        if (args.Contains("--situation-only")) { SituationReportTests.Run(); return; }
        if (args.Contains("--situation-standoff-only")) { SituationStandoffTests.Run(); return; }
        if (args.Length == 0) SituationStandoffTests.Run();
        if (args.Contains("--hybrid-war-only")) { HybridWarBriefTests.Run(); return; }
        if (args.Length == 0) HybridWarBriefTests.Run();
        if (args.Contains("--crisis-ladder-only")) { CrisisLadderTests.Run(); return; }
        if (args.Length == 0) CrisisLadderTests.Run();
        if (args.Contains("--resource-cartel-only")) { ResourceCartelTests.Run(); return; }
        if (args.Length == 0) ResourceCartelTests.Run();
        if (args.Contains("--parliament-only")) { ParliamentaryPoliticsTests.Run(); return; }
        if (args.Length == 0) ParliamentaryPoliticsTests.Run();
        if (args.Contains("--peace-rules-only")) { PeaceRulesDifferentialTests.Run(); return; }
        if (args.Length == 0) PeaceRulesDifferentialTests.Run();
        if (args.Contains("--crisis-grant-only")) { CrisisGrantTests.Run(); return; }
        if (args.Contains("--war-score-index-only")) { WarScoreIndexTests.Run(); return; }
        if (args.Contains("--war-rhythm-only")) { WarRhythmRulesTests.Run(); return; }
        if (args.Contains("--supply-hedge-only")) { SupplyHedgeTests.Run(); return; }
        if (args.Contains("--exposure-only")) { StrategicReportsTests.Run(); SupplyStressTests.Run(); return; }
        if (args.Length == 0) { CrisisGrantTests.Run(); WarScoreIndexTests.Run(); WarRhythmRulesTests.Run(); SupplyHedgeTests.Run(); }
        if (args.Contains("--music-only")) { MusicSituationTests.Run(); return; }
        if (args.Contains("--music-transition-only")) { MusicTransitionTests.Run(); return; }
        if (args.Contains("--weather-only")) { WeatherReadabilityTests.Run(); return; }
        if (args.Contains("--political-only")) { PoliticalCohortTests.Run(); return; }
        if (args.Contains("--promises-only")) { GovernmentPromiseTests.Run(); return; }
        if (args.Contains("--goals-only")) { StrategicGoalTests.Run(); return; }
        if (args.Contains("--state-identity-only")) { StateIdentityTests.Run(); return; }
        if (args.Contains("--rulers-only")) { RulerNamesTests.Run(); return; }
        if (args.Contains("--ruler-ages-only")) { RulerAgeTests.Run(); return; }
        if (args.Contains("--quality-profiles-only")) { QualityProfileTests.Run(); return; }
        if (args.Contains("--counter-cards-only")) { CounterCardTests.Run(); return; }
        if (args.Contains("--ai-promises-only")) { AiPromiseTests.Run(); return; }
        if (args.Contains("--doctrine-only")) { TacticalDoctrineTests.Run(); return; }
        if (args.Contains("--strategy-only")) { StrategicDoctrineTests.Run(); return; }
        if (args.Contains("--names-only")) { CountryNameTests.Run(); return; }
        if (args.Contains("--naming-only")) { NamingTests.Run(); return; }
        if (args.Contains("--tautology-only")) { TautologyNameProbe.Run(); return; }
        if (args.Contains("--name-probe")) { NameProbe.Run(); return; }

        if (args.Contains("--ruler-names-only")) { RulerNameTests.Run(); return; }


        if (args.Contains("--federal-outcome-only")) { FederalFractureOutcomeTests.Run(); return; }
        if (args.Contains("--movement-power-state-only")) { MovementPowerStateTests.Run(); return; }
        if (args.Contains("--movement-staging-only")) { MovementPowerStagingTests.Run(); return; }
        if (args.Contains("--campaign-isolation-only")) { CampaignIsolationTests.Run(); return; }
        if (args.Length == 0) CampaignIsolationTests.Run();
        if (args.Length == 0) MovementPowerStagingTests.Run();
        if (args.Contains("--movement-power-fingerprint-only")) { MovementPowerFingerprintTests.Run(); return; }
        if (args.Contains("--feed-only")) { NewsFeedTests.Run(); return; }
        if (args.Contains("--chart-marks-only")) { ChartMarkersTests.Run(); return; }
        if (args.Contains("--media-only")) { MediaLibraryTests.Run(); return; }
        if (args.Contains("--responsive-hud-only")) { ResponsiveHudLayoutTests.Run(); return; }
        if (args.Contains("--identity-only")) { IdentityResearchTests.Run(); return; }
        if (args.Contains("--restoration-only")) { RestorationTests.Run(); return; }
        if (args.Contains("--civil-war-budget-only")) { CivilWarBudgetTests.Run(); return; }
        if (args.Contains("--cue-queue-only")) { StrategicCueQueueTests.Run(); return; }
        if (args.Contains("--network-cadence-only")) { NetworkSyncCadenceTests.Run(); return; }
        if (args.Contains("--snapshot-delivery-only")) { NetworkSnapshotDeliveryTests.Run(); return; }
        if (args.Contains("--claims-only")) { TerritorialClaimsTests.Run(); return; }
        if (args.Contains("--diplomatic-participation-only")) { DiplomaticParticipationTests.Run(); return; }
        if (args.Length == 0) DiplomaticParticipationTests.Run();
        if (args.Contains("--diplomatic-symmetry-only")) { DiplomaticSymmetryTests.Run(); return; }
        if (args.Length == 0) DiplomaticSymmetryTests.Run();
        if (args.Contains("--organizations-only")) { OrganizationTests.Run(); return; }
        if (args.Contains("--households-only")) { HouseholdTests.Run(); return; }
        if (args.Contains("--brain-drain-only")) { BrainDrainTests.Run(); return; }
        if (args.Contains("--release-only")) { DismantlementTests.Run(); return; }
        if (args.Contains("--deals-bench")) { DealTests.Benchmark(); return; }
        if (args.Contains("--social-only")) { SocialMemoryTests.Run(); return; }
        if (args.Contains("--crises-only")) { DomesticCrisesTests.Run(); return; }
        if (args.Contains("--ui-icons-only")) { InterfaceIconTests.Run(); return; }
        if (args.Contains("--worker-only")) { WorkerBackendTests.Run(); return; }
        if (args.Contains("--movement-zone-only")) { MovementZoneTests.Run(); return; }
        if (args.Contains("--civic-only")) { CivicCampaignTests.Run(); return; }
        if (args.Contains("--alliances-only")) { DynamicAllianceTests.Run(); return; }
        if (args.Contains("--finance-only")) { SovereignFinanceTests.Run(); return; }
        if (args.Contains("--ai-fiscal-only")) { AiFiscalPolicyTests.Run(); return; }
        if (args.Contains("--construction-funding-only")) { ConstructionFundingTests.Run(); return; }
        if (args.Contains("--methods-only")) { MovementMethodsTests.Run(); return; }
        if (args.Contains("--policies-only")) { PoliticalPolicyTests.Run(); return; }
        if (args.Contains("--regional-content-only")) { RegionalContentTests.Run(); return; }
        if (args.Contains("--courses-only")) { NationalCourseTests.Run(); return; }
        if (args.Contains("--flags-only")) { FlagModdingTests.Run(); FlagArtworkTests.Run(); return; }
        if (args.Contains("--names-only")) { CountryNameTests.Run(); return; }
        if (args.Contains("--union-mutation-only")) { UnionCohesionMutationTests.Run(); return; }
        if (args.Contains("--union-only")) { UnionCohesionTests.Run(); return; }
        if (args.Contains("--union-map-only")) { UnionCohesionTests.RunOnMap(); return; }
        if (args.Contains("--demography-only")) { DemographyTests.Run(); return; }
        if (args.Contains("--ai-doctrine-only")) { AiDoctrineTests.Run(); return; }
        if (args.Contains("--ai-temperament-only")) { AiTemperamentTests.Run(); return; }
        if (args.Contains("--casus-belli-only")) { CasusBelliTests.Run(); return; }
        if (args.Contains("--scenario-manifest-only")) { ScenarioManifestTests.Run(); return; }
        if (args.Contains("--country-history-only")) { CountryHistoryTests.Run(); return; }
        if (args.Contains("--country-definition-only")) { CountryDefinitionTests.Run(); return; }
        if (args.Contains("--alliance-program-only")) { AllianceProgramOutcomeTests.Run(); return; }
        if (args.Contains("--movement-catalog-only")) { MovementCatalogTests.Run(); return; }
        if (args.Contains("--movement-representation-only")) { MovementRepresentationTests.Run(); return; }
        if (args.Contains("--war-stalemate-only")) { WarStalemateTests.Run(); return; }
        RegionalDemographyTests.Run();
        DynamicAllianceTests.Run();
        ConflictReadOnlyTests.Run();
        AiDoctrineTests.Run();
        AiTemperamentTests.Run();
        CasusBelliTests.Run();
        ScenarioManifestTests.Run();
        CountryHistoryTests.Run();
        CountryDefinitionTests.Run();
        AllianceProgramOutcomeTests.Run();
        ClaudeStateFingerprintTests.Run();
        CampaignIntegrityTests.Run();
        TerritorialClaimsTests.Run();
        OrganizationTests.Run();
        HouseholdTests.Run();
        BrainDrainTests.Run();
        DemographicExtinctionTests.Run();
        DeterrenceTests.Run();
        OccupationResistanceTests.Run();
        PartitionTests.Run();
        DynastyTests.Run();
        DismantlementTests.Run();
        DomesticCrisesTests.Run();
        FederalFractureOutcomeTests.Run();
        SovereignFinanceTests.Run();
        AiFiscalPolicyTests.Run();
        ConstructionFundingTests.Run();
        MovementMethodsTests.Run();
        MovementCatalogTests.Run();
        MovementRepresentationTests.Run();
        WarStalemateTests.Run();
        MovementPowerStateTests.Run();
        MovementPowerFingerprintTests.Run();
        PoliticalPolicyTests.Run();
        NationalCourseTests.Run();
        RegionalContentTests.Run();
        SocialMemoryTests.Run();
        CivicCampaignTests.Run();
        MovementZoneTests.Run();
        WorkerBackendTests.Run();
        if (args.Contains("--yaml-only")) { StructuredDataTests.Run(); return; }
        if (args.Contains("--army-localization-only")) { ArmyCommandLocalizationTests.Run(); return; }
        if (args.Contains("--fleet-localization-only")) { FleetNameLocalizationTests.Run(); return; }
        if (args.Contains("--icons-only")) { InterfaceIconTests.Run(); return; }
        if (args.Contains("--production-only")) { ProductionUnitsTests.Run(); return; }
        ArmyCommandLocalizationTests.Run();
        FleetNameLocalizationTests.Run();
        if (args.Contains("--events-only")) { EventCadenceTests.Run(); return; }
        if (args.Contains("--reload-only")) { PresentationReloadTests.Run(); return; }
        if (args.Contains("--ultimatum-only")) { UltimatumTests.Run(); return; }
        if (args.Contains("--feed-only")) { NewsFeedTests.Run(); return; }
        if (args.Contains("--chart-marks-only")) { ChartMarkersTests.Run(); return; }
        if (args.Contains("--presentation-diplomacy-only"))
        {
            PresentationReloadTests.Run();
            UltimatumTests.Run();
            NewsFeedTests.Run();
            return;
        }
        FlagModdingTests.Run();
        FlagArtworkTests.Run();
        CountryNameTests.Run();
        UnionCohesionTests.Run();
        UnionCohesionMutationTests.Run();
        // The ground change itself, on the campaign the game plays: slower, and the only place the
        // end of the union chain is checked at all.
        UnionCohesionTests.RunOnMap();
        DemographyTests.Run();
        StructuredDataTests.Run();
        EventCadenceTests.Run();
        PresentationReloadTests.Run();
        UltimatumTests.Run();
        DealTests.Run();
        DealNegotiationTests.Run();
        SituationReportTests.Run();
        GovernmentPromiseTests.Run();
        AiPromiseTests.Run();
        TacticalDoctrineTests.Run();
        StrategicDoctrineTests.Run();
        MusicSituationTests.Run();
        MusicTransitionTests.Run();
        WeatherReadabilityTests.Run();
        MediaLibraryTests.Run();
        ResponsiveHudLayoutTests.Run();
        IdentityResearchTests.Run();
        RestorationTests.Run();
        PeaceIntentTests.Run();
        WarAimsTests.Run();
        CombatTempoTests.Run();
        DefectionsTests.Run();
        CivilWarBudgetTests.Run();
        StrategicCueQueueTests.Run();
        NetworkSyncCadenceTests.Run();
        NetworkSnapshotDeliveryTests.Run();
        StagedStepBoundaryTests.Run();
        ModFrontendActionTests.Run();
        ForeignAssistancePresentationTests.Run();
        ArmyAssaultPresentationTests.Run();
        AssaultRulesBackendTests.Run();
        GarrisonPlannerTests.Run();
        HistoricalTimelineTests.Run();
        WarEmergencyTests.Run();
        ForeignAssistanceTests.Run();
        NewsFeedTests.Run();
        ChartMarkersTests.Run();
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
        InterfaceSkin.ResetCatalog();
        LayoutWorkbenchTests.Run();
        OpportunityAtlasTests.Run();
        StrategicReportsTests.Run();
        SupplyStressTests.Run();
        TechnologyTreeTests.Run();
        ResearchWorkspaceTests.Run();
        AtlasSurfaceTests.Run();
        DiplomacyPreviewTests.Run();
        ProductionUnitsTests.Run();
        InterfaceIconTests.Run();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        ScenarioMapDraftTests.Run();
        GC.Collect();
        GC.WaitForPendingFinalizers();
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
