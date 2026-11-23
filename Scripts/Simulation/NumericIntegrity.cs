using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>Reject non-finite state, without turning a broken calculation into a believable zero.</summary>
public static class NumericIntegrity
{
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> RecordProperties = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> UnknownScalarFieldsCache = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> FallbackScalarFieldsCache = new();

    private static FieldInfo[] UnknownScalarFieldsFor(Type type, HashSet<string> known) =>
        UnknownScalarFieldsCache.GetOrAdd(type, t =>
        {
            var raw = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            List<FieldInfo> extra = null;
            for (var i = 0; i < raw.Length; i++)
            {
                var f = raw[i];
                if ((f.FieldType == typeof(float) || f.FieldType == typeof(double)) && !known.Contains(f.Name))
                    (extra ??= new List<FieldInfo>()).Add(f);
            }
            return extra is null ? Array.Empty<FieldInfo>() : extra.ToArray();
        });

    private static string CheckFallbackFields(object value, string kind, object id, FieldInfo[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            var raw = f.GetValue(value);
            if (raw is float fv) { if (!float.IsFinite(fv)) return kind + id + "." + f.Name + " = " + fv; }
            else if (raw is double dv && !double.IsFinite(dv)) return kind + id + "." + f.Name + " = " + dv;
        }
        return null;
    }

    private static readonly FieldInfo[] NationUnknownFields = UnknownScalarFieldsFor(typeof(Nation), new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Nation.AddressWeariness), nameof(Nation.AddressPromiseMark), nameof(Nation.FoundingDivisions),
        nameof(Nation.EstablishedDivisions), nameof(Nation.BrainDrainRate), nameof(Nation.AssessedStrength),
        nameof(Nation.ConsolidationAmbition), nameof(Nation.YoungShare), nameof(Nation.OldShare),
        nameof(Nation.WorkingShare), nameof(Nation.OldShareExcess), nameof(Nation.HealthPreparedness),
        nameof(Nation.DisasterPreparedness), nameof(Nation.LivingOffset), nameof(Nation.LiteracyOffset),
        nameof(Nation.HumanCapital), nameof(Nation.WeeklyGrossIncome), nameof(Nation.WeeklyIncomePenalty),
        nameof(Nation.LedgerPenaltyLastWeek), nameof(Nation.LedgerPenaltyWeek), nameof(Nation.LedgerTreasuryAtOpen),
        nameof(Nation.ProductionReferenceGdp), nameof(Nation.ArmsFocus), nameof(Nation.SecularDrift),
        nameof(Nation.StabilityBias), nameof(Nation.StabilityGovernance), nameof(Nation.ImperialOverstretch),
        nameof(Nation.CapitalLatitude), nameof(Nation.CapitalLongitude), nameof(Nation.Treasury),
        nameof(Nation.Gdp), nameof(Nation.MilitarySpending), nameof(Nation.TaxRate),
        nameof(Nation.WelfareSpending), nameof(Nation.EducationSpending), nameof(Nation.TributePaidWeekly),
        nameof(Nation.TributeTakenWeekly), nameof(Nation.LibertyDesire), nameof(Nation.WeeklyTaxIncome),
        nameof(Nation.WeeklyExpenses), nameof(Nation.WeeklyBalance), nameof(Nation.Inflation),
        nameof(Nation.Productivity), nameof(Nation.GrossOutput), nameof(Nation.IncomeBaseline),
        nameof(Nation.IncomeFactor), nameof(Nation.CapitalIntensityReference), nameof(Nation.OutputScale),
        nameof(Nation.EmployedShare), nameof(Nation.StandardOfLiving), nameof(Nation.Literacy),
        nameof(Nation.Urbanisation), nameof(Nation.AgricultureShare), nameof(Nation.Stability),
        nameof(Nation.ConstructionCapacity), nameof(Nation.WarSupport), nameof(Nation.ManpowerReserve),
        nameof(Nation.ReinforcementCapacity), nameof(Nation.ReinforcedLastWeek), nameof(Nation.MarketAccess),
        nameof(Nation.ShortageIndex), nameof(Nation.WeeklyTradeBalance), nameof(Nation.WeeklyTariffIncome),
        nameof(Nation.MoneyPrinting), nameof(Nation.EmissionMemory), nameof(Nation.InstitutionalHabit),
        nameof(Nation.ExpectedLivingStandard), nameof(Nation.PublicDebt), nameof(Nation.DebtArrears),
        nameof(Nation.PaymentArrears), nameof(Nation.CreditConfidence), nameof(Nation.DebtSpreadAnnual),
        nameof(Nation.DebtIssuedWeekly), nameof(Nation.DebtInterestWeekly), nameof(Nation.Notoriety),
        nameof(Nation.SubjectIntegrationProgress), nameof(Nation.SubjectLibertyModifier),
        nameof(Nation.ArmyExperience), nameof(Nation.AirExperience), nameof(Nation.NavalExperience),
        nameof(Nation.PoliticalPoints), nameof(Nation.ImportValue), nameof(Nation.ExportValue),
        nameof(Nation.RecessionDepth), nameof(Nation.UnionCohesion)
    });

    private static readonly FieldInfo[] MovementUnknownFields = UnknownScalarFieldsFor(typeof(MovementState), new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(MovementState.HeadquartersLatitude), nameof(MovementState.HeadquartersLongitude),
        nameof(MovementState.Cadres), nameof(MovementState.Legitimacy), nameof(MovementState.Heat),
        nameof(MovementState.Militancy), nameof(MovementState.IncomeWeekly), nameof(MovementState.DonationsWeekly),
        nameof(MovementState.SponsorsWeekly)
    });

    private static readonly FieldInfo[] StateUnknownFields = UnknownScalarFieldsFor(typeof(ScenarioState), new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ScenarioState.BrainDrainPressure), nameof(ScenarioState.ColonisationProgress),
        nameof(ScenarioState.Acceptance), nameof(ScenarioState.YoungShare), nameof(ScenarioState.OldShare),
        nameof(ScenarioState.ResidentAwareness), nameof(ScenarioState.NonBuildingJobs),
        nameof(ScenarioState.BasicNeedsCoverage), nameof(ScenarioState.EmploymentTarget),
        nameof(ScenarioState.LivelihoodStress), nameof(ScenarioState.AdministrationTaken),
        nameof(ScenarioState.AdministrationPopulationShare), nameof(ScenarioState.PoliticalDivergence),
        nameof(ScenarioState.LawApproval), nameof(ScenarioState.GovernmentApproval),
        nameof(ScenarioState.RegionalOutputValue), nameof(ScenarioState.Stability),
        nameof(ScenarioState.Literacy), nameof(ScenarioState.StandardOfLiving), nameof(ScenarioState.Radicalism),
        nameof(ScenarioState.SecessionPressure), nameof(ScenarioState.Urbanization),
        nameof(ScenarioState.BorderHabit), nameof(ScenarioState.WarDamage)
    });

    private static readonly FieldInfo[] ArmyUnknownFields = UnknownScalarFieldsFor(typeof(Army), new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Army.Experience), nameof(Army.Equipment), nameof(Army.Cohesion), nameof(Army.Recon),
        nameof(Army.Engineers), nameof(Army.Artillery), nameof(Army.Planning), nameof(Army.MoveProgress),
        nameof(Army.Divisions), nameof(Army.Organisation), nameof(Army.Strength), nameof(Army.Recovering),
        nameof(Army.Entrenchment), nameof(Army.SupplyLevel), nameof(Army.SupplyCongestion),
        nameof(Army.LastReinforced), nameof(Army.Latitude), nameof(Army.Longitude),
        nameof(Army.Power), nameof(Army.Presence), nameof(Army.PocketStores)
    });

    private static readonly FieldInfo[] BattleUnknownFields = UnknownScalarFieldsFor(typeof(LandBattle), new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(LandBattle.AttackerLosses), nameof(LandBattle.DefenderLosses),
        nameof(LandBattle.AttackerWidth), nameof(LandBattle.DefenderWidth),
        nameof(LandBattle.RiverCrossingStrength), nameof(LandBattle.AttackerSupport),
        nameof(LandBattle.DefenderSupport)
    });

    private static readonly FieldInfo[] WarUnknownFields = UnknownScalarFieldsFor(typeof(StrategicWar), new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(StrategicWar.AttackerWarSupport), nameof(StrategicWar.DefenderWarSupport),
        nameof(StrategicWar.AttackerLosses), nameof(StrategicWar.DefenderLosses),
        nameof(StrategicWar.AttackerKilled), nameof(StrategicWar.AttackerWounded), nameof(StrategicWar.AttackerCaptured),
        nameof(StrategicWar.DefenderKilled), nameof(StrategicWar.DefenderWounded), nameof(StrategicWar.DefenderCaptured),
        nameof(StrategicWar.OccupationScore), nameof(StrategicWar.BattleScore), nameof(StrategicWar.ObjectiveScore),
        nameof(StrategicWar.SupportScore), nameof(StrategicWar.WarScore), nameof(StrategicWar.Reparations),
        nameof(StrategicWar.DefenderOccupiedValue), nameof(StrategicWar.AttackerOccupiedValue),
        nameof(StrategicWar.CapitalScore), nameof(StrategicWar.ArmyScore),
        nameof(StrategicWar.AttackerStartStrength), nameof(StrategicWar.DefenderStartStrength),
        nameof(StrategicWar.AttackerStrengthLeft), nameof(StrategicWar.DefenderStrengthLeft),
        nameof(StrategicWar.TickingScore), nameof(StrategicWar.ScoreBack)
    });

    private static readonly FieldInfo[] FleetUnknownFields = UnknownScalarFieldsFor(typeof(Fleet), new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Fleet.Strength), nameof(Fleet.Organisation), nameof(Fleet.Supply), nameof(Fleet.Experience),
        nameof(Fleet.Readiness), nameof(Fleet.Latitude), nameof(Fleet.Longitude),
        nameof(Fleet.HomeLatitude), nameof(Fleet.HomeLongitude),
        nameof(Fleet.OrderLatitude), nameof(Fleet.OrderLongitude),
        nameof(Fleet.RoutedLatitude), nameof(Fleet.RoutedLongitude),
        nameof(Fleet.HullProgress), nameof(Fleet.Power), nameof(Fleet.Presence)
    });

    private static readonly FieldInfo[] SupportUnknownFields = UnknownScalarFieldsFor(typeof(SupportFormation), new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(SupportFormation.MoveProgress), nameof(SupportFormation.Strength), nameof(SupportFormation.Experience)
    });

    private static readonly FieldInfo[] RelationUnknownFields = UnknownScalarFieldsFor(typeof(DiplomaticRelation), new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(DiplomaticRelation.Opinion), nameof(DiplomaticRelation.AllianceCohesion),
        nameof(DiplomaticRelation.AllianceProgramWeeklyCost), nameof(DiplomaticRelation.AllianceProgramTrustBudget),
        nameof(DiplomaticRelation.LoanOutstanding), nameof(DiplomaticRelation.LoanWeekly),
        nameof(DiplomaticRelation.Tension), nameof(DiplomaticRelation.StructuralTension),
        nameof(DiplomaticRelation.GrievanceA), nameof(DiplomaticRelation.GrievanceB),
        nameof(DiplomaticRelation.ClashLosses), nameof(DiplomaticRelation.TensionBaseline),
        nameof(DiplomaticRelation.TensionAnchor)
    });

    private static string Bad(string kind, object id, string field, float v) => kind + id + "." + field + " = " + v;

    private static string NationScalarIssue(Nation n)
    {
        if (n is null) return null;
        if (!float.IsFinite(n.AddressWeariness)) return Bad("country ", n.Tag, nameof(n.AddressWeariness), n.AddressWeariness);
        if (!float.IsFinite(n.AddressPromiseMark)) return Bad("country ", n.Tag, nameof(n.AddressPromiseMark), n.AddressPromiseMark);
        if (!float.IsFinite(n.FoundingDivisions)) return Bad("country ", n.Tag, nameof(n.FoundingDivisions), n.FoundingDivisions);
        if (!float.IsFinite(n.EstablishedDivisions)) return Bad("country ", n.Tag, nameof(n.EstablishedDivisions), n.EstablishedDivisions);
        if (!float.IsFinite(n.BrainDrainRate)) return Bad("country ", n.Tag, nameof(n.BrainDrainRate), n.BrainDrainRate);
        if (!float.IsFinite(n.AssessedStrength)) return Bad("country ", n.Tag, nameof(n.AssessedStrength), n.AssessedStrength);
        if (!float.IsFinite(n.ConsolidationAmbition)) return Bad("country ", n.Tag, nameof(n.ConsolidationAmbition), n.ConsolidationAmbition);
        if (!float.IsFinite(n.YoungShare)) return Bad("country ", n.Tag, nameof(n.YoungShare), n.YoungShare);
        if (!float.IsFinite(n.OldShare)) return Bad("country ", n.Tag, nameof(n.OldShare), n.OldShare);
        if (!float.IsFinite(n.WorkingShare)) return Bad("country ", n.Tag, nameof(n.WorkingShare), n.WorkingShare);
        if (!float.IsFinite(n.OldShareExcess)) return Bad("country ", n.Tag, nameof(n.OldShareExcess), n.OldShareExcess);
        if (!float.IsFinite(n.HealthPreparedness)) return Bad("country ", n.Tag, nameof(n.HealthPreparedness), n.HealthPreparedness);
        if (!float.IsFinite(n.DisasterPreparedness)) return Bad("country ", n.Tag, nameof(n.DisasterPreparedness), n.DisasterPreparedness);
        if (!float.IsFinite(n.LivingOffset)) return Bad("country ", n.Tag, nameof(n.LivingOffset), n.LivingOffset);
        if (!float.IsFinite(n.LiteracyOffset)) return Bad("country ", n.Tag, nameof(n.LiteracyOffset), n.LiteracyOffset);
        if (!float.IsFinite(n.HumanCapital)) return Bad("country ", n.Tag, nameof(n.HumanCapital), n.HumanCapital);
        if (!float.IsFinite(n.WeeklyGrossIncome)) return Bad("country ", n.Tag, nameof(n.WeeklyGrossIncome), n.WeeklyGrossIncome);
        if (!float.IsFinite(n.WeeklyIncomePenalty)) return Bad("country ", n.Tag, nameof(n.WeeklyIncomePenalty), n.WeeklyIncomePenalty);
        if (!float.IsFinite(n.LedgerPenaltyLastWeek)) return Bad("country ", n.Tag, nameof(n.LedgerPenaltyLastWeek), n.LedgerPenaltyLastWeek);
        if (!float.IsFinite(n.LedgerPenaltyWeek)) return Bad("country ", n.Tag, nameof(n.LedgerPenaltyWeek), n.LedgerPenaltyWeek);
        if (!float.IsFinite(n.LedgerTreasuryAtOpen)) return Bad("country ", n.Tag, nameof(n.LedgerTreasuryAtOpen), n.LedgerTreasuryAtOpen);
        if (!float.IsFinite(n.ProductionReferenceGdp)) return Bad("country ", n.Tag, nameof(n.ProductionReferenceGdp), n.ProductionReferenceGdp);
        if (!float.IsFinite(n.ArmsFocus)) return Bad("country ", n.Tag, nameof(n.ArmsFocus), n.ArmsFocus);
        if (!float.IsFinite(n.SecularDrift)) return Bad("country ", n.Tag, nameof(n.SecularDrift), n.SecularDrift);
        if (!float.IsFinite(n.StabilityBias)) return Bad("country ", n.Tag, nameof(n.StabilityBias), n.StabilityBias);
        if (!float.IsFinite(n.StabilityGovernance)) return Bad("country ", n.Tag, nameof(n.StabilityGovernance), n.StabilityGovernance);
        if (!float.IsFinite(n.ImperialOverstretch)) return Bad("country ", n.Tag, nameof(n.ImperialOverstretch), n.ImperialOverstretch);
        if (!float.IsFinite(n.CapitalLatitude)) return Bad("country ", n.Tag, nameof(n.CapitalLatitude), n.CapitalLatitude);
        if (!float.IsFinite(n.CapitalLongitude)) return Bad("country ", n.Tag, nameof(n.CapitalLongitude), n.CapitalLongitude);
        if (!float.IsFinite(n.Treasury)) return Bad("country ", n.Tag, nameof(n.Treasury), n.Treasury);
        if (!float.IsFinite(n.Gdp)) return Bad("country ", n.Tag, nameof(n.Gdp), n.Gdp);
        if (!float.IsFinite(n.MilitarySpending)) return Bad("country ", n.Tag, nameof(n.MilitarySpending), n.MilitarySpending);
        if (!float.IsFinite(n.TaxRate)) return Bad("country ", n.Tag, nameof(n.TaxRate), n.TaxRate);
        if (!float.IsFinite(n.WelfareSpending)) return Bad("country ", n.Tag, nameof(n.WelfareSpending), n.WelfareSpending);
        if (!float.IsFinite(n.EducationSpending)) return Bad("country ", n.Tag, nameof(n.EducationSpending), n.EducationSpending);
        if (!float.IsFinite(n.TributePaidWeekly)) return Bad("country ", n.Tag, nameof(n.TributePaidWeekly), n.TributePaidWeekly);
        if (!float.IsFinite(n.TributeTakenWeekly)) return Bad("country ", n.Tag, nameof(n.TributeTakenWeekly), n.TributeTakenWeekly);
        if (!float.IsFinite(n.LibertyDesire)) return Bad("country ", n.Tag, nameof(n.LibertyDesire), n.LibertyDesire);
        if (!float.IsFinite(n.WeeklyTaxIncome)) return Bad("country ", n.Tag, nameof(n.WeeklyTaxIncome), n.WeeklyTaxIncome);
        if (!float.IsFinite(n.WeeklyExpenses)) return Bad("country ", n.Tag, nameof(n.WeeklyExpenses), n.WeeklyExpenses);
        if (!float.IsFinite(n.WeeklyBalance)) return Bad("country ", n.Tag, nameof(n.WeeklyBalance), n.WeeklyBalance);
        if (!float.IsFinite(n.Inflation)) return Bad("country ", n.Tag, nameof(n.Inflation), n.Inflation);
        if (!float.IsFinite(n.Productivity)) return Bad("country ", n.Tag, nameof(n.Productivity), n.Productivity);
        if (!float.IsFinite(n.GrossOutput)) return Bad("country ", n.Tag, nameof(n.GrossOutput), n.GrossOutput);
        if (!float.IsFinite(n.IncomeBaseline)) return Bad("country ", n.Tag, nameof(n.IncomeBaseline), n.IncomeBaseline);
        if (!float.IsFinite(n.IncomeFactor)) return Bad("country ", n.Tag, nameof(n.IncomeFactor), n.IncomeFactor);
        if (!float.IsFinite(n.CapitalIntensityReference)) return Bad("country ", n.Tag, nameof(n.CapitalIntensityReference), n.CapitalIntensityReference);
        if (!float.IsFinite(n.OutputScale)) return Bad("country ", n.Tag, nameof(n.OutputScale), n.OutputScale);
        if (!float.IsFinite(n.EmployedShare)) return Bad("country ", n.Tag, nameof(n.EmployedShare), n.EmployedShare);
        if (!float.IsFinite(n.StandardOfLiving)) return Bad("country ", n.Tag, nameof(n.StandardOfLiving), n.StandardOfLiving);
        if (!float.IsFinite(n.Literacy)) return Bad("country ", n.Tag, nameof(n.Literacy), n.Literacy);
        if (!float.IsFinite(n.Urbanisation)) return Bad("country ", n.Tag, nameof(n.Urbanisation), n.Urbanisation);
        if (!float.IsFinite(n.AgricultureShare)) return Bad("country ", n.Tag, nameof(n.AgricultureShare), n.AgricultureShare);
        if (!float.IsFinite(n.Stability)) return Bad("country ", n.Tag, nameof(n.Stability), n.Stability);
        if (!float.IsFinite(n.ConstructionCapacity)) return Bad("country ", n.Tag, nameof(n.ConstructionCapacity), n.ConstructionCapacity);
        if (!float.IsFinite(n.WarSupport)) return Bad("country ", n.Tag, nameof(n.WarSupport), n.WarSupport);
        if (!float.IsFinite(n.ManpowerReserve)) return Bad("country ", n.Tag, nameof(n.ManpowerReserve), n.ManpowerReserve);
        if (!float.IsFinite(n.ReinforcementCapacity)) return Bad("country ", n.Tag, nameof(n.ReinforcementCapacity), n.ReinforcementCapacity);
        if (!float.IsFinite(n.ReinforcedLastWeek)) return Bad("country ", n.Tag, nameof(n.ReinforcedLastWeek), n.ReinforcedLastWeek);
        if (!float.IsFinite(n.MarketAccess)) return Bad("country ", n.Tag, nameof(n.MarketAccess), n.MarketAccess);
        if (!float.IsFinite(n.ShortageIndex)) return Bad("country ", n.Tag, nameof(n.ShortageIndex), n.ShortageIndex);
        if (!float.IsFinite(n.WeeklyTradeBalance)) return Bad("country ", n.Tag, nameof(n.WeeklyTradeBalance), n.WeeklyTradeBalance);
        if (!float.IsFinite(n.WeeklyTariffIncome)) return Bad("country ", n.Tag, nameof(n.WeeklyTariffIncome), n.WeeklyTariffIncome);
        if (!float.IsFinite(n.MoneyPrinting)) return Bad("country ", n.Tag, nameof(n.MoneyPrinting), n.MoneyPrinting);
        if (!float.IsFinite(n.EmissionMemory)) return Bad("country ", n.Tag, nameof(n.EmissionMemory), n.EmissionMemory);
        if (!float.IsFinite(n.InstitutionalHabit)) return Bad("country ", n.Tag, nameof(n.InstitutionalHabit), n.InstitutionalHabit);
        if (!float.IsFinite(n.ExpectedLivingStandard)) return Bad("country ", n.Tag, nameof(n.ExpectedLivingStandard), n.ExpectedLivingStandard);
        if (!float.IsFinite(n.PublicDebt)) return Bad("country ", n.Tag, nameof(n.PublicDebt), n.PublicDebt);
        if (!float.IsFinite(n.DebtArrears)) return Bad("country ", n.Tag, nameof(n.DebtArrears), n.DebtArrears);
        if (!float.IsFinite(n.PaymentArrears)) return Bad("country ", n.Tag, nameof(n.PaymentArrears), n.PaymentArrears);
        if (!float.IsFinite(n.CreditConfidence)) return Bad("country ", n.Tag, nameof(n.CreditConfidence), n.CreditConfidence);
        if (!float.IsFinite(n.DebtSpreadAnnual)) return Bad("country ", n.Tag, nameof(n.DebtSpreadAnnual), n.DebtSpreadAnnual);
        if (!float.IsFinite(n.DebtIssuedWeekly)) return Bad("country ", n.Tag, nameof(n.DebtIssuedWeekly), n.DebtIssuedWeekly);
        if (!float.IsFinite(n.DebtInterestWeekly)) return Bad("country ", n.Tag, nameof(n.DebtInterestWeekly), n.DebtInterestWeekly);
        if (!float.IsFinite(n.Notoriety)) return Bad("country ", n.Tag, nameof(n.Notoriety), n.Notoriety);
        if (!float.IsFinite(n.SubjectIntegrationProgress)) return Bad("country ", n.Tag, nameof(n.SubjectIntegrationProgress), n.SubjectIntegrationProgress);
        if (!float.IsFinite(n.SubjectLibertyModifier)) return Bad("country ", n.Tag, nameof(n.SubjectLibertyModifier), n.SubjectLibertyModifier);
        if (!float.IsFinite(n.ArmyExperience)) return Bad("country ", n.Tag, nameof(n.ArmyExperience), n.ArmyExperience);
        if (!float.IsFinite(n.AirExperience)) return Bad("country ", n.Tag, nameof(n.AirExperience), n.AirExperience);
        if (!float.IsFinite(n.NavalExperience)) return Bad("country ", n.Tag, nameof(n.NavalExperience), n.NavalExperience);
        if (!float.IsFinite(n.PoliticalPoints)) return Bad("country ", n.Tag, nameof(n.PoliticalPoints), n.PoliticalPoints);
        if (!float.IsFinite(n.ImportValue)) return Bad("country ", n.Tag, nameof(n.ImportValue), n.ImportValue);
        if (!float.IsFinite(n.ExportValue)) return Bad("country ", n.Tag, nameof(n.ExportValue), n.ExportValue);
        if (!float.IsFinite(n.RecessionDepth)) return Bad("country ", n.Tag, nameof(n.RecessionDepth), n.RecessionDepth);
        if (!float.IsFinite(n.UnionCohesion)) return Bad("country ", n.Tag, nameof(n.UnionCohesion), n.UnionCohesion);
        return NationUnknownFields.Length == 0 ? null : CheckFallbackFields(n, "country ", n.Tag, NationUnknownFields);
    }

    private static string MovementScalarIssue(MovementState m, string tag)
    {
        if (m is null) return null;
        if (!float.IsFinite(m.HeadquartersLatitude)) return Bad("movement ", tag, nameof(m.HeadquartersLatitude), m.HeadquartersLatitude);
        if (!float.IsFinite(m.HeadquartersLongitude)) return Bad("movement ", tag, nameof(m.HeadquartersLongitude), m.HeadquartersLongitude);
        if (!float.IsFinite(m.Cadres)) return Bad("movement ", tag, nameof(m.Cadres), m.Cadres);
        if (!float.IsFinite(m.Legitimacy)) return Bad("movement ", tag, nameof(m.Legitimacy), m.Legitimacy);
        if (!float.IsFinite(m.Heat)) return Bad("movement ", tag, nameof(m.Heat), m.Heat);
        if (!float.IsFinite(m.Militancy)) return Bad("movement ", tag, nameof(m.Militancy), m.Militancy);
        if (!float.IsFinite(m.IncomeWeekly)) return Bad("movement ", tag, nameof(m.IncomeWeekly), m.IncomeWeekly);
        if (!float.IsFinite(m.DonationsWeekly)) return Bad("movement ", tag, nameof(m.DonationsWeekly), m.DonationsWeekly);
        if (!float.IsFinite(m.SponsorsWeekly)) return Bad("movement ", tag, nameof(m.SponsorsWeekly), m.SponsorsWeekly);
        return MovementUnknownFields.Length == 0 ? null : CheckFallbackFields(m, "movement ", tag, MovementUnknownFields);
    }

    private static string StateScalarIssue(ScenarioState s)
    {
        if (s is null) return null;
        if (!float.IsFinite(s.BrainDrainPressure)) return Bad("region ", s.Id, nameof(s.BrainDrainPressure), s.BrainDrainPressure);
        if (!float.IsFinite(s.ColonisationProgress)) return Bad("region ", s.Id, nameof(s.ColonisationProgress), s.ColonisationProgress);
        if (!float.IsFinite(s.Acceptance)) return Bad("region ", s.Id, nameof(s.Acceptance), s.Acceptance);
        if (!float.IsFinite(s.YoungShare)) return Bad("region ", s.Id, nameof(s.YoungShare), s.YoungShare);
        if (!float.IsFinite(s.OldShare)) return Bad("region ", s.Id, nameof(s.OldShare), s.OldShare);
        if (!float.IsFinite(s.ResidentAwareness)) return Bad("region ", s.Id, nameof(s.ResidentAwareness), s.ResidentAwareness);
        if (!float.IsFinite(s.NonBuildingJobs)) return Bad("region ", s.Id, nameof(s.NonBuildingJobs), s.NonBuildingJobs);
        if (!float.IsFinite(s.BasicNeedsCoverage)) return Bad("region ", s.Id, nameof(s.BasicNeedsCoverage), s.BasicNeedsCoverage);
        if (!float.IsFinite(s.EmploymentTarget)) return Bad("region ", s.Id, nameof(s.EmploymentTarget), s.EmploymentTarget);
        if (!float.IsFinite(s.LivelihoodStress)) return Bad("region ", s.Id, nameof(s.LivelihoodStress), s.LivelihoodStress);
        if (!float.IsFinite(s.AdministrationTaken)) return Bad("region ", s.Id, nameof(s.AdministrationTaken), s.AdministrationTaken);
        if (!float.IsFinite(s.AdministrationPopulationShare)) return Bad("region ", s.Id, nameof(s.AdministrationPopulationShare), s.AdministrationPopulationShare);
        if (!float.IsFinite(s.PoliticalDivergence)) return Bad("region ", s.Id, nameof(s.PoliticalDivergence), s.PoliticalDivergence);
        if (!float.IsFinite(s.LawApproval)) return Bad("region ", s.Id, nameof(s.LawApproval), s.LawApproval);
        if (!float.IsFinite(s.GovernmentApproval)) return Bad("region ", s.Id, nameof(s.GovernmentApproval), s.GovernmentApproval);
        if (!float.IsFinite(s.RegionalOutputValue)) return Bad("region ", s.Id, nameof(s.RegionalOutputValue), s.RegionalOutputValue);
        if (!float.IsFinite(s.Stability)) return Bad("region ", s.Id, nameof(s.Stability), s.Stability);
        if (!float.IsFinite(s.Literacy)) return Bad("region ", s.Id, nameof(s.Literacy), s.Literacy);
        if (!float.IsFinite(s.StandardOfLiving)) return Bad("region ", s.Id, nameof(s.StandardOfLiving), s.StandardOfLiving);
        if (!float.IsFinite(s.Radicalism)) return Bad("region ", s.Id, nameof(s.Radicalism), s.Radicalism);
        if (!float.IsFinite(s.SecessionPressure)) return Bad("region ", s.Id, nameof(s.SecessionPressure), s.SecessionPressure);
        if (!float.IsFinite(s.Urbanization)) return Bad("region ", s.Id, nameof(s.Urbanization), s.Urbanization);
        if (!float.IsFinite(s.BorderHabit)) return Bad("region ", s.Id, nameof(s.BorderHabit), s.BorderHabit);
        if (!float.IsFinite(s.WarDamage)) return Bad("region ", s.Id, nameof(s.WarDamage), s.WarDamage);
        return StateUnknownFields.Length == 0 ? null : CheckFallbackFields(s, "region ", s.Id, StateUnknownFields);
    }

    private static string ArmyIssue(Army a)
    {
        if (a is null) return null;
        if (!float.IsFinite(a.Experience)) return Bad("army ", a.Id, nameof(a.Experience), a.Experience);
        if (!float.IsFinite(a.Equipment)) return Bad("army ", a.Id, nameof(a.Equipment), a.Equipment);
        if (!float.IsFinite(a.Cohesion)) return Bad("army ", a.Id, nameof(a.Cohesion), a.Cohesion);
        if (!float.IsFinite(a.Recon)) return Bad("army ", a.Id, nameof(a.Recon), a.Recon);
        if (!float.IsFinite(a.Engineers)) return Bad("army ", a.Id, nameof(a.Engineers), a.Engineers);
        if (!float.IsFinite(a.Artillery)) return Bad("army ", a.Id, nameof(a.Artillery), a.Artillery);
        if (!float.IsFinite(a.Planning)) return Bad("army ", a.Id, nameof(a.Planning), a.Planning);
        if (!float.IsFinite(a.MoveProgress)) return Bad("army ", a.Id, nameof(a.MoveProgress), a.MoveProgress);
        if (!float.IsFinite(a.Divisions)) return Bad("army ", a.Id, nameof(a.Divisions), a.Divisions);
        if (!float.IsFinite(a.Organisation)) return Bad("army ", a.Id, nameof(a.Organisation), a.Organisation);
        if (!float.IsFinite(a.Strength)) return Bad("army ", a.Id, nameof(a.Strength), a.Strength);
        if (!float.IsFinite(a.Recovering)) return Bad("army ", a.Id, nameof(a.Recovering), a.Recovering);
        if (!float.IsFinite(a.Entrenchment)) return Bad("army ", a.Id, nameof(a.Entrenchment), a.Entrenchment);
        if (!float.IsFinite(a.SupplyLevel)) return Bad("army ", a.Id, nameof(a.SupplyLevel), a.SupplyLevel);
        if (!float.IsFinite(a.SupplyCongestion)) return Bad("army ", a.Id, nameof(a.SupplyCongestion), a.SupplyCongestion);
        if (!float.IsFinite(a.LastReinforced)) return Bad("army ", a.Id, nameof(a.LastReinforced), a.LastReinforced);
        if (!float.IsFinite(a.Latitude)) return Bad("army ", a.Id, nameof(a.Latitude), a.Latitude);
        if (!float.IsFinite(a.Longitude)) return Bad("army ", a.Id, nameof(a.Longitude), a.Longitude);
        if (!float.IsFinite(a.Power)) return Bad("army ", a.Id, nameof(a.Power), a.Power);
        if (!float.IsFinite(a.Presence)) return Bad("army ", a.Id, nameof(a.Presence), a.Presence);
        if (!float.IsFinite(a.PocketStores)) return Bad("army ", a.Id, nameof(a.PocketStores), a.PocketStores);
        return ArmyUnknownFields.Length == 0 ? null : CheckFallbackFields(a, "army ", a.Id, ArmyUnknownFields);
    }

    private static string BattleIssue(LandBattle b)
    {
        if (b is null) return null;
        if (!float.IsFinite(b.AttackerLosses)) return Bad("battle ", b.Id, nameof(b.AttackerLosses), b.AttackerLosses);
        if (!float.IsFinite(b.DefenderLosses)) return Bad("battle ", b.Id, nameof(b.DefenderLosses), b.DefenderLosses);
        if (!float.IsFinite(b.AttackerWidth)) return Bad("battle ", b.Id, nameof(b.AttackerWidth), b.AttackerWidth);
        if (!float.IsFinite(b.DefenderWidth)) return Bad("battle ", b.Id, nameof(b.DefenderWidth), b.DefenderWidth);
        if (!float.IsFinite(b.RiverCrossingStrength)) return Bad("battle ", b.Id, nameof(b.RiverCrossingStrength), b.RiverCrossingStrength);
        if (!float.IsFinite(b.AttackerSupport)) return Bad("battle ", b.Id, nameof(b.AttackerSupport), b.AttackerSupport);
        if (!float.IsFinite(b.DefenderSupport)) return Bad("battle ", b.Id, nameof(b.DefenderSupport), b.DefenderSupport);
        return BattleUnknownFields.Length == 0 ? null : CheckFallbackFields(b, "battle ", b.Id, BattleUnknownFields);
    }

    private static string WarIssue(StrategicWar w)
    {
        if (w is null) return null;
        if (!float.IsFinite(w.AttackerWarSupport)) return Bad("war ", w.Id, nameof(w.AttackerWarSupport), w.AttackerWarSupport);
        if (!float.IsFinite(w.DefenderWarSupport)) return Bad("war ", w.Id, nameof(w.DefenderWarSupport), w.DefenderWarSupport);
        if (!float.IsFinite(w.AttackerLosses)) return Bad("war ", w.Id, nameof(w.AttackerLosses), w.AttackerLosses);
        if (!float.IsFinite(w.DefenderLosses)) return Bad("war ", w.Id, nameof(w.DefenderLosses), w.DefenderLosses);
        if (!float.IsFinite(w.AttackerKilled)) return Bad("war ", w.Id, nameof(w.AttackerKilled), w.AttackerKilled);
        if (!float.IsFinite(w.AttackerWounded)) return Bad("war ", w.Id, nameof(w.AttackerWounded), w.AttackerWounded);
        if (!float.IsFinite(w.AttackerCaptured)) return Bad("war ", w.Id, nameof(w.AttackerCaptured), w.AttackerCaptured);
        if (!float.IsFinite(w.DefenderKilled)) return Bad("war ", w.Id, nameof(w.DefenderKilled), w.DefenderKilled);
        if (!float.IsFinite(w.DefenderWounded)) return Bad("war ", w.Id, nameof(w.DefenderWounded), w.DefenderWounded);
        if (!float.IsFinite(w.DefenderCaptured)) return Bad("war ", w.Id, nameof(w.DefenderCaptured), w.DefenderCaptured);
        if (!float.IsFinite(w.OccupationScore)) return Bad("war ", w.Id, nameof(w.OccupationScore), w.OccupationScore);
        if (!float.IsFinite(w.BattleScore)) return Bad("war ", w.Id, nameof(w.BattleScore), w.BattleScore);
        if (!float.IsFinite(w.ObjectiveScore)) return Bad("war ", w.Id, nameof(w.ObjectiveScore), w.ObjectiveScore);
        if (!float.IsFinite(w.SupportScore)) return Bad("war ", w.Id, nameof(w.SupportScore), w.SupportScore);
        if (!float.IsFinite(w.WarScore)) return Bad("war ", w.Id, nameof(w.WarScore), w.WarScore);
        if (!float.IsFinite(w.Reparations)) return Bad("war ", w.Id, nameof(w.Reparations), w.Reparations);
        if (!float.IsFinite(w.DefenderOccupiedValue)) return Bad("war ", w.Id, nameof(w.DefenderOccupiedValue), w.DefenderOccupiedValue);
        if (!float.IsFinite(w.AttackerOccupiedValue)) return Bad("war ", w.Id, nameof(w.AttackerOccupiedValue), w.AttackerOccupiedValue);
        if (!float.IsFinite(w.CapitalScore)) return Bad("war ", w.Id, nameof(w.CapitalScore), w.CapitalScore);
        if (!float.IsFinite(w.ArmyScore)) return Bad("war ", w.Id, nameof(w.ArmyScore), w.ArmyScore);
        if (!float.IsFinite(w.AttackerStartStrength)) return Bad("war ", w.Id, nameof(w.AttackerStartStrength), w.AttackerStartStrength);
        if (!float.IsFinite(w.DefenderStartStrength)) return Bad("war ", w.Id, nameof(w.DefenderStartStrength), w.DefenderStartStrength);
        if (!float.IsFinite(w.AttackerStrengthLeft)) return Bad("war ", w.Id, nameof(w.AttackerStrengthLeft), w.AttackerStrengthLeft);
        if (!float.IsFinite(w.DefenderStrengthLeft)) return Bad("war ", w.Id, nameof(w.DefenderStrengthLeft), w.DefenderStrengthLeft);
        if (!float.IsFinite(w.TickingScore)) return Bad("war ", w.Id, nameof(w.TickingScore), w.TickingScore);
        if (!float.IsFinite(w.ScoreBack)) return Bad("war ", w.Id, nameof(w.ScoreBack), w.ScoreBack);
        return WarUnknownFields.Length == 0 ? null : CheckFallbackFields(w, "war ", w.Id, WarUnknownFields);
    }

    private static string FleetIssue(Fleet f)
    {
        if (f is null) return null;
        if (!float.IsFinite(f.Strength)) return Bad("fleet ", f.Id, nameof(f.Strength), f.Strength);
        if (!float.IsFinite(f.Organisation)) return Bad("fleet ", f.Id, nameof(f.Organisation), f.Organisation);
        if (!float.IsFinite(f.Supply)) return Bad("fleet ", f.Id, nameof(f.Supply), f.Supply);
        if (!float.IsFinite(f.Experience)) return Bad("fleet ", f.Id, nameof(f.Experience), f.Experience);
        if (!float.IsFinite(f.Readiness)) return Bad("fleet ", f.Id, nameof(f.Readiness), f.Readiness);
        if (!float.IsFinite(f.Latitude)) return Bad("fleet ", f.Id, nameof(f.Latitude), f.Latitude);
        if (!float.IsFinite(f.Longitude)) return Bad("fleet ", f.Id, nameof(f.Longitude), f.Longitude);
        if (!float.IsFinite(f.HomeLatitude)) return Bad("fleet ", f.Id, nameof(f.HomeLatitude), f.HomeLatitude);
        if (!float.IsFinite(f.HomeLongitude)) return Bad("fleet ", f.Id, nameof(f.HomeLongitude), f.HomeLongitude);
        if (!float.IsFinite(f.OrderLatitude)) return Bad("fleet ", f.Id, nameof(f.OrderLatitude), f.OrderLatitude);
        if (!float.IsFinite(f.OrderLongitude)) return Bad("fleet ", f.Id, nameof(f.OrderLongitude), f.OrderLongitude);
        if (!float.IsFinite(f.RoutedLatitude)) return Bad("fleet ", f.Id, nameof(f.RoutedLatitude), f.RoutedLatitude);
        if (!float.IsFinite(f.RoutedLongitude)) return Bad("fleet ", f.Id, nameof(f.RoutedLongitude), f.RoutedLongitude);
        if (!float.IsFinite(f.HullProgress)) return Bad("fleet ", f.Id, nameof(f.HullProgress), f.HullProgress);
        if (!float.IsFinite(f.Power)) return Bad("fleet ", f.Id, nameof(f.Power), f.Power);
        if (!float.IsFinite(f.Presence)) return Bad("fleet ", f.Id, nameof(f.Presence), f.Presence);
        return FleetUnknownFields.Length == 0 ? null : CheckFallbackFields(f, "fleet ", f.Id, FleetUnknownFields);
    }

    private static string SupportIssue(SupportFormation f)
    {
        if (f is null) return null;
        if (!float.IsFinite(f.MoveProgress)) return Bad("support ", f.Id, nameof(f.MoveProgress), f.MoveProgress);
        if (!float.IsFinite(f.Strength)) return Bad("support ", f.Id, nameof(f.Strength), f.Strength);
        if (!float.IsFinite(f.Experience)) return Bad("support ", f.Id, nameof(f.Experience), f.Experience);
        return SupportUnknownFields.Length == 0 ? null : CheckFallbackFields(f, "support ", f.Id, SupportUnknownFields);
    }

    private static string RelationIssue(DiplomaticRelation r)
    {
        if (r is null) return null;
        if (!float.IsFinite(r.Opinion)) return Bad("relation", "", nameof(r.Opinion), r.Opinion);
        if (!float.IsFinite(r.AllianceCohesion)) return Bad("relation", "", nameof(r.AllianceCohesion), r.AllianceCohesion);
        if (!float.IsFinite(r.AllianceProgramWeeklyCost)) return Bad("relation", "", nameof(r.AllianceProgramWeeklyCost), r.AllianceProgramWeeklyCost);
        if (!float.IsFinite(r.AllianceProgramTrustBudget)) return Bad("relation", "", nameof(r.AllianceProgramTrustBudget), r.AllianceProgramTrustBudget);
        if (!float.IsFinite(r.LoanOutstanding)) return Bad("relation", "", nameof(r.LoanOutstanding), r.LoanOutstanding);
        if (!float.IsFinite(r.LoanWeekly)) return Bad("relation", "", nameof(r.LoanWeekly), r.LoanWeekly);
        if (!float.IsFinite(r.Tension)) return Bad("relation", "", nameof(r.Tension), r.Tension);
        if (!float.IsFinite(r.StructuralTension)) return Bad("relation", "", nameof(r.StructuralTension), r.StructuralTension);
        if (!float.IsFinite(r.GrievanceA)) return Bad("relation", "", nameof(r.GrievanceA), r.GrievanceA);
        if (!float.IsFinite(r.GrievanceB)) return Bad("relation", "", nameof(r.GrievanceB), r.GrievanceB);
        if (!float.IsFinite(r.ClashLosses)) return Bad("relation", "", nameof(r.ClashLosses), r.ClashLosses);
        if (!float.IsFinite(r.TensionBaseline)) return Bad("relation", "", nameof(r.TensionBaseline), r.TensionBaseline);
        if (!float.IsFinite(r.TensionAnchor)) return Bad("relation", "", nameof(r.TensionAnchor), r.TensionAnchor);
        return RelationUnknownFields.Length == 0 ? null : CheckFallbackFields(r, "relation", "", RelationUnknownFields);
    }

    public static List<string> Inspect(object snapshot, int limit = 24)
    {
        var issues = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(snapshot, new StringBuilder("campaign"), issues, seen, Math.Max(1, limit));
        return issues;
    }

    private static void Walk(object value, StringBuilder path, List<string> issues, HashSet<object> seen, int limit)
    {
        if (value is null || issues.Count >= limit) return;
        if (value is float f) { if (!float.IsFinite(f)) issues.Add(path + " = " + f); return; }
        if (value is double d) { if (!double.IsFinite(d)) issues.Add(path + " = " + d); return; }
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string || value is decimal) return;
        if (!type.IsValueType && !seen.Add(value)) return;
        var length = path.Length;
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                path.Append('[').Append(entry.Key).Append(']');
                Walk(entry.Value, path, issues, seen, limit);
                path.Length = length;
                if (issues.Count >= limit) return;
            }
            return;
        }
        if (value is IEnumerable list)
        {
            var index = 0;
            foreach (var item in list)
            {
                path.Append('[').Append(index++).Append(']');
                Walk(item, path, issues, seen, limit);
                path.Length = length;
                if (issues.Count >= limit) return;
            }
            return;
        }
        if (type.Namespace != typeof(Campaign).Namespace) return;
        var fields = Fields.GetOrAdd(type, t => t.GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(field => field.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always).ToArray());
        foreach (var field in fields)
        {
            path.Append('.').Append(field.Name);
            Walk(field.GetValue(value), path, issues, seen, limit);
            path.Length = length;
            if (issues.Count >= limit) return;
        }
        // Positional records (notably history samples and market quotes) have properties,
        // not public fields. Do not invoke computed UI properties of mutable entities.
        if (!type.IsValueType || fields.Length != 0) return;
        foreach (var property in RecordProperties.GetOrAdd(type, t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 &&
                                 p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always).ToArray()))
        {
            path.Append('.').Append(property.Name);
            Walk(property.GetValue(value), path, issues, seen, limit);
            path.Length = length;
            if (issues.Count >= limit) return;
        }
    }

    /// <summary>Cheap weekly checkpoint; a full, nested validation also runs before saving.</summary>
    public static string FirstLiveIssue(Campaign c)
    {
        var nationIssue = FirstInOrder(c.Nations, NationIssue);
        if (nationIssue is not null) return nationIssue;
        // The regions and their people are most of the walk - two thousand regions, eighty
        // thousand groups, read through reflection - and reading is all it does, so they are read
        // side by side. The answer is the first bad region in list order, exactly as one at a time.
        var regionIssue = FirstInOrder(c.States, RegionIssue);
        if (regionIssue is not null) return regionIssue;
        var armyIssue = FirstInOrder(c.Armies, ArmyIssue);
        if (armyIssue is not null) return armyIssue;
        var battleIssue = FirstInOrder(c.Battles, BattleIssue);
        if (battleIssue is not null) return battleIssue;
        var warIssue = FirstInOrder(c.Wars, WarIssue);
        if (warIssue is not null) return warIssue;
        var fleetIssue = FirstInOrder(c.Fleets, FleetIssue);
        if (fleetIssue is not null) return fleetIssue;
        var supportIssue = FirstInOrder(c.SupportFormations, SupportIssue);
        if (supportIssue is not null) return supportIssue;
        if (c.ProvinceOccupation is { } occupation)
            for (var index = 0; index < occupation.Length; index++)
                if (!float.IsFinite(occupation[index])) return "occupation[" + index + "]";
        foreach (var r in c.Relations.Values)
            if (RelationIssue(r) is { } relationIssue) return relationIssue;
        return null;
    }

    /// <summary>
    /// Weekly numeric sanity pass spread over small slices so inspecting tens of thousands of
    /// relations never occupies a full frame, while returning the exact same first defect.
    /// </summary>
    public static IEnumerable<int> FirstLiveIssueStages(Campaign c, Action<string> onIssue)
    {
        for (var i = 0; i < c.Nations.Count; i++)
        {
            if (i > 0 && i % 128 == 0) yield return 0;
            if (NationIssue(c.Nations[i]) is { } issue) { onIssue(issue); yield break; }
        }
        yield return 0;
        for (var i = 0; i < c.States.Count; i++)
        {
            if (i > 0 && i % 192 == 0) yield return 0;
            if (RegionIssue(c.States[i]) is { } issue) { onIssue(issue); yield break; }
        }
        yield return 0;
        for (var i = 0; i < c.Armies.Count; i++)
        {
            if (i > 0 && i % 256 == 0) yield return 0;
            if (ArmyIssue(c.Armies[i]) is { } issue) { onIssue(issue); yield break; }
        }
        for (var i = 0; i < c.Battles.Count; i++)
        {
            if (i > 0 && i % 256 == 0) yield return 0;
            if (BattleIssue(c.Battles[i]) is { } issue) { onIssue(issue); yield break; }
        }
        for (var i = 0; i < c.Wars.Count; i++)
        {
            if (i > 0 && i % 256 == 0) yield return 0;
            if (WarIssue(c.Wars[i]) is { } issue) { onIssue(issue); yield break; }
        }
        for (var i = 0; i < c.Fleets.Count; i++)
        {
            if (i > 0 && i % 256 == 0) yield return 0;
            if (FleetIssue(c.Fleets[i]) is { } issue) { onIssue(issue); yield break; }
        }
        for (var i = 0; i < c.SupportFormations.Count; i++)
        {
            if (i > 0 && i % 256 == 0) yield return 0;
            if (SupportIssue(c.SupportFormations[i]) is { } issue) { onIssue(issue); yield break; }
        }
        if (c.ProvinceOccupation is { } occupation)
            for (var index = 0; index < occupation.Length; index++)
            {
                if (index > 0 && index % 8192 == 0) yield return 0;
                if (!float.IsFinite(occupation[index])) { onIssue("occupation[" + index + "]"); yield break; }
            }
        if (c.Relations.Count == 0) yield break;
        yield return 0;
        foreach (var r in c.Relations.Values)
            if (RelationIssue(r) is { } issue) { onIssue(issue); yield break; }
    }

    private static string NationIssue(Nation n)
    {
        var issue = NationScalarIssue(n) ?? MovementScalarIssue(n.Movement, n.Tag);
        if (issue is not null) return issue;
        foreach (var q in n.Market)
            if (!float.IsFinite(q.Value.Price) || !float.IsFinite(q.Value.Production) ||
                !float.IsFinite(q.Value.Demand)) return "market " + n.Tag + "." + q.Key;
        if (n.Movement is { } movement)
            foreach (var support in movement.Support)
                if (!float.IsFinite(support.Value)) return "support " + n.Tag + "[" + support.Key + "]";
        return null;
    }

    private static string RegionIssue(ScenarioState s)
    {
        var issue = StateScalarIssue(s);
        if (issue is not null) return issue;
        var groups = s.PopulationGroups;
        for (var i = 0; i < groups.Count; i++)
        {
            var p = groups[i];
            if (!float.IsFinite(p.Employment) || !float.IsFinite(p.Wealth) ||
                !float.IsFinite(p.StandardOfLiving) || !float.IsFinite(p.Radicalism)) return "population " + s.Id;
        }
        return null;
    }

    /// <summary>The first bad element in list order, read side by side past a small size.</summary>
    private static string FirstInOrder<T>(IReadOnlyList<T> items, Func<T, string> issue)
    {
        if (items.Count < 256)
        {
            for (var index = 0; index < items.Count; index++) { var found = issue(items[index]); if (found is not null) return found; }
            return null;
        }
        var first = int.MaxValue;
        string foundIssue = null;
        var gate = new object();
        System.Threading.Tasks.Parallel.For(0, items.Count,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8)) },
            index =>
            {
                if (index > System.Threading.Volatile.Read(ref first)) return;
                var found = issue(items[index]);
                if (found is null) return;
                lock (gate) if (index < first) { first = index; foundIssue = found; }
            });
        return foundIssue;
    }

    /// <summary>Fallback reflection scalar check for unknown types (used only outside core hot path).</summary>
    private static string ScalarIssue(object value, string kind, object id)
    {
        if (value is null) return null;
        var fields = FallbackScalarFieldsCache.GetOrAdd(value.GetType(), k => k.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(float) || f.FieldType == typeof(double)).ToArray());
        return CheckFallbackFields(value, kind, id, fields);
    }
}

public sealed class CampaignIntegrityException : InvalidOperationException
{
    public string Issue { get; }
    public string DiagnosticPath { get; }
    public CampaignIntegrityException(string issue, string diagnosticPath = "") : base("Invalid campaign numbers: " + issue)
    { Issue = issue; DiagnosticPath = diagnosticPath; }
}

public sealed partial class Campaign
{
    [JsonIgnore] public string SimulationFault { get; private set; } = "";

    /// <summary>A failed, partially executed step must not be resumed as if it succeeded.</summary>
    public void BlockSimulation(string reason, Exception exception = null)
    {
        SimulationFault = reason ?? "simulation error";
        IsPaused = true;
        // The exception goes into the envelope when it exists: the diagnostic snapshot quotes
        // only the message, and a message without a stack is a bug report nobody can act on.
        CrashEnvelope.Dump(this, exception, reason);
        MarkStrategicChange();
    }
}
