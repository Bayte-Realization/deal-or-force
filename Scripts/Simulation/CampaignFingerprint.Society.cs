using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class CampaignFingerprint
{
    private static void MixCivilWarOutbreakBudget(Campaign campaign, ref ulong hash)
    {
        var outbreakBudget = CivilWarOutbreak.Budget(campaign);
        if (outbreakBudget.StartsByDay is not { Count: > 0 }) return;
        var outbreakDays = outbreakBudget.StartsByDay.Where(p =>
            CivilWarBudgetState.InWindow(p.Key, campaign.ElapsedDays) && p.Value > 0).OrderBy(p => p.Key).ToArray();
        if (outbreakDays.Length == 0) return;
        MixText(ref hash, "civil_war_budget"); Mix(ref hash, outbreakDays.Length);
        foreach (var day in outbreakDays) { Mix(ref hash, day.Key); Mix(ref hash, day.Value); }
    }

    private static void MixNationLawsAndCourse(Nation nation, ref ulong hash)
    {
        Mix(ref hash, nation.LawsActive ? 1 : 0); Mix(ref hash, nation.AgendasActive ? 1 : 0); Mix(ref hash, nation.LastPolicyReviewDay);
        foreach (var law in nation.Laws.OrderBy(p => p.Key, StringComparer.Ordinal))
        { MixText(ref hash, law.Key); MixText(ref hash, law.Value.Id); MixText(ref hash, law.Value.TargetTag); Mix(ref hash, law.Value.Region); Mix(ref hash, law.Value.AdoptedDay); }
        Mix(ref hash, -8);
        foreach (var clock in nation.PolicyReadyOnDay.OrderBy(p => p.Key, StringComparer.Ordinal)) { MixText(ref hash, clock.Key); Mix(ref hash, clock.Value); }
        Mix(ref hash, -9);
        if (nation.Agenda is { } agenda)
        {
            MixText(ref hash, agenda.Id); MixText(ref hash, agenda.Goal); MixFloat(ref hash, agenda.GoalValue);
            Mix(ref hash, agenda.DurationDays); Mix(ref hash, agenda.ElapsedDays); Mix(ref hash, agenda.SatisfiedDays);
            Mix(ref hash, agenda.Completed ? 1 : 0); Mix(ref hash, agenda.Succeeded ? 1 : 0);
        }
        Mix(ref hash, -10);
        var course = nation.NationalCourse;
        Mix(ref hash, nation.NationalCourseActive ? 1 : 0); Mix(ref hash, course.Initialized ? 1 : 0);
        MixText(ref hash, course.Dominant); MixText(ref hash, course.Challenger); MixText(ref hash, course.ShockReason); MixText(ref hash, course.RememberedPolitics);
        Mix(ref hash, course.ChallengerDays); Mix(ref hash, course.AgeDays); Mix(ref hash, course.LastReviewDay); Mix(ref hash, course.RememberedSecessionDay);
        MixFloat(ref hash, course.Entrenchment); MixFloat(ref hash, course.Shock); MixFloat(ref hash, course.PreviousLiving); MixFloat(ref hash, course.WarExperience);
        foreach (var conviction in course.Convictions.OrderBy(p => p.Key, StringComparer.Ordinal))
        { MixText(ref hash, conviction.Key); MixFloat(ref hash, conviction.Value); }
        Mix(ref hash, -12);
    }

    private static void MixNationCrisesAndParliament(Nation nation, ref ulong hash)
    {
        var crisis = nation.DomesticCrisis ?? new DomesticCrisisState();
        MixFloat(ref hash, crisis.CoupPressure); Mix(ref hash, crisis.DaysAboveThreshold); Mix(ref hash, crisis.LastReviewDay);
        Mix(ref hash, crisis.CooldownUntilDay); MixText(ref hash, crisis.LastOutcomeId); Mix(ref hash, crisis.LastOutcomeDay);
        MixText(ref hash, crisis.ResponseId); Mix(ref hash, crisis.ResponseUntilDay); Mix(ref hash, crisis.ResponseCooldownUntilDay);
        if (crisis.ConciliationUntilDay >= 0)
        { MixText(ref hash, "conciliation"); Mix(ref hash, crisis.ConciliationRegion); Mix(ref hash, crisis.ConciliationUntilDay); }
        if (crisis.SpendingMemory >= 0f)
        {
            MixText(ref hash, "street"); MixFloat(ref hash, crisis.ProtestGrievance); MixFloat(ref hash, crisis.ProtestIntensity);
            MixFloat(ref hash, crisis.SpendingMemory); MixFloat(ref hash, crisis.MilitaryMemory);
            Mix(ref hash, crisis.LastProtestNewsDay); Mix(ref hash, crisis.LastConcessionDay);
        }
        Mix(ref hash, nation.CoupPlotDay); Mix(ref hash, nation.LastCoupDay);
        if (nation.LastPoliticalScandalDay >= 0) { MixText(ref hash, "political_scandal"); Mix(ref hash, nation.LastPoliticalScandalDay); }
        if (nation.LastMilitaryCoupDay >= 0) { MixText(ref hash, "last_military_coup"); Mix(ref hash, nation.LastMilitaryCoupDay); }
        if (nation.Parliament is { } parl && (parl.PeaceMandateActive || parl.MilitaryBudgetCutActive || parl.LastReviewDay >= 0))
        {
            MixText(ref hash, "parliament_state");
            MixFloat(ref hash, parl.RulingBlocShare); MixFloat(ref hash, parl.OppositionShare); MixFloat(ref hash, parl.DovishShare);
            Mix(ref hash, parl.PeaceMandateActive ? 1 : 0); Mix(ref hash, parl.PeaceMandateDay);
            Mix(ref hash, parl.MilitaryBudgetCutActive ? 1 : 0); Mix(ref hash, parl.LastReviewDay);
            MixFloat(ref hash, parl.FactionDiscontent);
            MixText(ref hash, parl.FavoredPortfolio ?? ""); MixText(ref hash, parl.FavoredLawCategory ?? "");
        }
    }

    private static void MixNationCommitments(Nation nation, ref ulong hash)
    {
        var grantClocks = 0;
        foreach (var clock in nation.DecisionAvailableDay.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (clock.Key.StartsWith(RevoltRecovery.Prefix, StringComparison.Ordinal) ||
                clock.Key.StartsWith(SecessionResponse.Prefix, StringComparison.Ordinal))
            { MixText(ref hash, clock.Key); Mix(ref hash, clock.Value); }
        foreach (var clock in nation.DecisionAvailableDay)
            if (clock.Key.StartsWith("crisis_grant/", StringComparison.Ordinal)) grantClocks++;
        if (grantClocks > 0)
        {
            MixText(ref hash, "crisis_grant");
            foreach (var clock in nation.DecisionAvailableDay.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (clock.Key.StartsWith("crisis_grant/", StringComparison.Ordinal))
                { MixText(ref hash, clock.Key); Mix(ref hash, clock.Value); }
        }
        var hedgeClocks = 0;
        foreach (var clock in nation.DecisionAvailableDay)
            if (clock.Key.StartsWith("supply_hedge/", StringComparison.Ordinal)) hedgeClocks++;
        if (hedgeClocks > 0)
        {
            MixText(ref hash, "supply_hedge");
            foreach (var clock in nation.DecisionAvailableDay.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (clock.Key.StartsWith("supply_hedge/", StringComparison.Ordinal))
                { MixText(ref hash, clock.Key); Mix(ref hash, clock.Value); }
        }
        foreach (var origin in nation.RenouncedTerritoryOrigins.OrderBy(id => id)) Mix(ref hash, origin);
        Mix(ref hash, -17);
        if (nation.Promises.Count > 0)
        {
            foreach (var promise in nation.Promises.OrderBy(p => p.Id, StringComparer.Ordinal))
            { MixText(ref hash, promise.Id); Mix(ref hash, promise.MadeDay); Mix(ref hash, promise.DueDay); }
            Mix(ref hash, -30);
        }
        if (nation.Goals.Count > 0)
        {
            foreach (var goal in nation.Goals.OrderBy(g => g.Id, StringComparer.Ordinal))
            { MixText(ref hash, goal.Id); Mix(ref hash, goal.AdoptedDay); Mix(ref hash, goal.CompletedDay); }
            Mix(ref hash, -31);
        }
        if (nation.IdentityChangedDay >= 0)
        { MixText(ref hash, "state_identity"); MixText(ref hash, nation.DeclaredReligion); MixText(ref hash, nation.DeclaredCulture); Mix(ref hash, nation.IdentityChangedDay); }
    }

    private static void MixNationMovement(Nation nation, ref ulong hash)
    {
        Mix(ref hash, nation.Movement is null ? 0 : 1);
        if (nation.Movement is not { } movement) return;
        MixFloat(ref hash, movement.Militancy);
        if (movement.GainedTraits.Count + movement.LostTraits.Count > 0)
        {
            MixText(ref hash, "traits_became");
            foreach (var trait in movement.GainedTraits.OrderBy(t => t, StringComparer.Ordinal)) MixText(ref hash, "+" + trait);
            foreach (var trait in movement.LostTraits.OrderBy(t => t, StringComparer.Ordinal)) MixText(ref hash, "-" + trait);
        }
        if (string.IsNullOrEmpty(movement.Id) && !movement.Dissolved && movement.Landless &&
            movement.Cadres == 0f && movement.Legitimacy == 0f && movement.IncomeWeekly == 0f &&
            movement.Support.Count == 0 && movement.RecognisedBy.Count == 0 && movement.ReadyOnDay.Count == 0 &&
            movement.MovingToStateMapIndex == -1 && movement.ArrivesOnDay == -1 && !movement.Exile &&
            movement.Peoples.Count == 0 && movement.Homeland.Count == 0) return;
        MixText(ref hash, "movement_command_state"); MixText(ref hash, movement.Id);
        Mix(ref hash, movement.Dissolved ? 1 : 0); Mix(ref hash, movement.Landless ? 1 : 0);
        MixFloat(ref hash, movement.Cadres); MixFloat(ref hash, movement.Legitimacy); MixFloat(ref hash, movement.IncomeWeekly);
        Mix(ref hash, movement.Support.Count);
        foreach (var support in movement.Support.OrderBy(p => p.Key)) { Mix(ref hash, support.Key); MixFloat(ref hash, support.Value); }
        Mix(ref hash, movement.RecognisedBy.Count);
        foreach (var country in movement.RecognisedBy.OrderBy(id => id)) Mix(ref hash, country);
        Mix(ref hash, movement.ReadyOnDay.Count);
        foreach (var clock in movement.ReadyOnDay.OrderBy(p => p.Key, StringComparer.Ordinal))
        { MixText(ref hash, clock.Key); Mix(ref hash, clock.Value); }
        Mix(ref hash, movement.MovingToStateMapIndex); Mix(ref hash, movement.ArrivesOnDay); Mix(ref hash, movement.Exile ? 1 : 0);
        Mix(ref hash, movement.Peoples.Count); foreach (var people in movement.Peoples) MixText(ref hash, people);
        Mix(ref hash, movement.Homeland.Count); foreach (var region in movement.Homeland) MixText(ref hash, region);
    }

    private static void MixNationStabilityAndFailure(Nation nation, ref ulong hash)
    {
        Mix(ref hash, nation.ProductionReferenceInitialized ? 1 : 0);
        MixFloat(ref hash, nation.ProductionReferenceGdp); Mix(ref hash, nation.ProductionReferencePopulation);
        Mix(ref hash, nation.LastCivilWarDay);
        MixFloat(ref hash, nation.Stability); MixFloat(ref hash, nation.StabilityBias); MixFloat(ref hash, nation.StabilityGovernance);
        if (nation.TroublesDays > 0) { MixText(ref hash, "troubles"); Mix(ref hash, nation.TroublesDays); }
        MixFloat(ref hash, nation.WarSupport); MixFloat(ref hash, nation.ManpowerReserve);
        Mix(ref hash, nation.LastSecessionDay);
        if (nation.IncomeBaseline != 0f || nation.IncomeFactor != 1f)
        { MixText(ref hash, "income"); MixFloat(ref hash, nation.IncomeBaseline); MixFloat(ref hash, nation.IncomeFactor); }
        if (nation.CapitalIntensityReference != 0f) { MixText(ref hash, "capital"); MixFloat(ref hash, nation.CapitalIntensityReference); }
        if (nation.RecessionDepth != 0f || nation.RecessionUntilDay != 0)
        { MixText(ref hash, "recession"); MixFloat(ref hash, nation.RecessionDepth); Mix(ref hash, nation.RecessionStartDay); Mix(ref hash, nation.RecessionUntilDay); }
        Mix(ref hash, nation.LastCivilWarDay);
        if (nation.CollapseStartDay != -1 || nation.CollapseWaves != 0 || nation.CollapseStartPopulation != 0 ||
            nation.LastCollapseDay != -99999 || nation.LastRevolutionDay != -99999)
        {
            MixText(ref hash, "state_failure"); Mix(ref hash, nation.CollapseStartDay); Mix(ref hash, nation.CollapseWaves);
            Mix(ref hash, nation.CollapseStartPopulation); Mix(ref hash, nation.LastCollapseDay); Mix(ref hash, nation.LastRevolutionDay);
        }
        Mix(ref hash, nation.AssessedDay); MixFloat(ref hash, nation.AssessedStrength);
        MixText(ref hash, nation.RememberedInstitutions); Mix(ref hash, nation.InstitutionAgeDays);
        Mix(ref hash, nation.LastInstitutionReviewDay); Mix(ref hash, nation.LastSocialReviewDay);
        MixFloat(ref hash, nation.InstitutionalHabit); MixFloat(ref hash, nation.ExpectedLivingStandard);
    }

    private static void MixNationAddressAndMigration(Nation nation, ref ulong hash)
    {
        if (nation.LastAddressDay != -9999 || nation.AddressWeariness != 0f ||
            !string.IsNullOrEmpty(nation.AddressPromise) || nation.AddressPromiseDueDay != -1 ||
            nation.AddressPromiseMark != 0f)
        {
            MixText(ref hash, "address_to_nation"); Mix(ref hash, nation.LastAddressDay); MixFloat(ref hash, nation.AddressWeariness);
            MixText(ref hash, nation.AddressPromise); Mix(ref hash, nation.AddressPromiseDueDay); MixFloat(ref hash, nation.AddressPromiseMark);
        }
        if (nation.WeeklyNetMigration != 0 || nation.CumulativeNetMigration != 0 ||
            nation.BrainDrainRate != 0f || nation.BrainDrainDestinationId != -1 ||
            nation.BrainGainSourceId != -1)
        {
            MixText(ref hash, "brain_drain"); Mix(ref hash, nation.WeeklyNetMigration); Mix(ref hash, nation.CumulativeNetMigration);
            MixFloat(ref hash, nation.BrainDrainRate); Mix(ref hash, nation.BrainDrainDestinationId); Mix(ref hash, nation.BrainGainSourceId);
        }
    }

    private static void MixNationDemographicCollapse(Nation nation, ref ulong hash)
    {
        if (nation.CollapseReviews != 0 || nation.CollapseAnnounced)
        { MixText(ref hash, "demographic_collapse"); Mix(ref hash, nation.CollapseReviews); Mix(ref hash, nation.CollapseAnnounced ? 1 : 0); }
    }

    private static void MixNationElectionsAndCabinet(Nation nation, ref ulong hash)
    {
        if (nation.Elections is { } elections)
        {
            MixText(ref hash, "elections_v1"); MixText(ref hash, elections.Government); MixFloat(ref hash, elections.Control);
            Mix(ref hash, elections.Round); Mix(ref hash, elections.IncumbentTerms); MixText(ref hash, elections.IncumbentName);
            Mix(ref hash, elections.LastDay); MixFloat(ref hash, elections.IncumbentVote); MixText(ref hash, elections.Result);
            Mix(ref hash, elections.Suspended ? 1 : 0); Mix(ref hash, elections.LastPostponedRound);
            Mix(ref hash, elections.TransferOnDay); MixText(ref hash, elections.Winner); MixText(ref hash, elections.WinnerTrait);
            Mix(ref hash, elections.WinnerBirthYear); Mix(ref hash, elections.DisputedUntilDay);
            Mix(ref hash, elections.LastControlReviewDay);
        }
        if (nation.CabinetPeople.Count > 0)
        {
            MixText(ref hash, "minister_people_v1"); Mix(ref hash, nation.CabinetPeople.Count);
            foreach (var entry in nation.CabinetPeople.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                MixText(ref hash, entry.Key.ToLowerInvariant()); MixText(ref hash, entry.Value?.Name);
                Mix(ref hash, entry.Value?.BirthYear ?? 0); MixText(ref hash, entry.Value?.TraitId);
                Mix(ref hash, entry.Value?.AppointedDay ?? 0);
            }
        }
        if (nation.PersonPortraits.Count > 0)
        {
            MixText(ref hash, "person_portraits_v1"); Mix(ref hash, nation.PersonPortraits.Count);
            foreach (var entry in nation.PersonPortraits.OrderBy(p => p.Key, StringComparer.Ordinal))
            { MixText(ref hash, entry.Key); MixText(ref hash, entry.Value); }
        }
    }

    private static void MixStatePopulationAndPolitics(ScenarioState state, ref ulong hash)
    {
        foreach (var group in state.PopulationGroups)
        {
            MixText(ref hash, group.Culture); MixText(ref hash, group.Religion); MixText(ref hash, group.Profession);
            Mix(ref hash, (int)group.Class); Mix(ref hash, (int)group.Sector);
            Mix(ref hash, group.Size); MixFloat(ref hash, group.Employment); MixFloat(ref hash, group.Wealth);
            MixFloat(ref hash, group.StandardOfLiving); MixFloat(ref hash, group.Radicalism);
        }
        Mix(ref hash, -20);
        MixFloat(ref hash, state.SecessionPressure);
        if (state.UrbanBaseline != 0) { MixText(ref hash, "urban_baseline"); Mix(ref hash, state.UrbanBaseline); }
        MixFloat(ref hash, state.ResidentAwareness);
        if (state.PoliticalDivergence >= 0f || state.LawApproval >= 0f || state.GovernmentApproval >= 0f)
        {
            MixText(ref hash, "political_cohort");
            MixFloat(ref hash, state.PoliticalDivergence); MixFloat(ref hash, state.LawApproval); MixFloat(ref hash, state.GovernmentApproval);
        }
        if (state.PoliticalBlocs is { Length: 5 })
        {
            MixText(ref hash, "political_blocs");
            for (var i = 0; i < 5; i++) MixFloat(ref hash, state.PoliticalBlocs[i]);
        }
    }

    private static void MixPartisansAndSecessionEvents(Campaign campaign, ref ulong hash)
    {
        // Fighters in hiding (Partisans): nothing mixed for a country that has none.
        foreach (var nation in campaign.Nations)
        {
            if (nation.FightersGoneToGround.Count == 0) continue;
            MixText(ref hash, "gone_to_ground"); Mix(ref hash, nation.Id);
            foreach (var (region, men) in nation.FightersGoneToGround.OrderBy(pair => pair.Key))
            { Mix(ref hash, region); MixFloat(ref hash, men); }
        }
        foreach (var item in campaign.PendingEvents.OrderBy(e => e.Id))
            if (item.Options.Any(o => o.SecessionTargetId >= 0))
            {
                MixText(ref hash, "secession_response_event"); Mix(ref hash, item.Id); Mix(ref hash, item.NationId);
                Mix(ref hash, item.CreatedDay); Mix(ref hash, item.ExpiresDay);
                foreach (var option in item.Options) { Mix(ref hash, option.SecessionTargetId); MixText(ref hash, option.SecessionChoice); }
            }
    }
}
