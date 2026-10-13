using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// One number for the whole world.
///
/// Peers do not exchange the world; they exchange the orders given to it and each runs the
/// same simulation over the same seed. That works exactly as long as the two simulations
/// agree, and the failure when they stop agreeing is the worst kind: no error, no crash,
/// just two players slowly coming to believe different things about where the front is.
/// By the time anybody notices, neither save can be trusted and neither can be blamed.
///
/// So both sides compute this, the host says what it has, and a client that disagrees asks
/// for the world again rather than playing on in a different one. It is made of the things
/// a divergence shows up in first - who holds what, where every formation is, and what each
/// country is worth - and it is ordered by id rather than by whatever order the lists are
/// in, because list order is itself a thing two runs could differ about.
/// </summary>
public static class CampaignFingerprint
{
    public static ulong Of(Campaign campaign)
    {
        if (campaign is null) return 0;
        var hash = 14695981039346656037UL;

        void Mix(long value)
        {
            for (var shift = 0; shift < 64; shift += 8)
                hash = (hash ^ (byte)(value >> shift)) * 1099511628211UL;
        }

        void MixFloat(float value) => Mix(BitConverter.SingleToInt32Bits(value));
        void MixText(string value) { foreach (var character in value ?? "") Mix(character); Mix(-8); }
        void MixImage(PublicImage image)
        {
            Mix(image is null ? 0 : 1);
            if (image is null) return;
            MixFloat(image.Esteem); MixFloat(image.Incident); MixFloat(image.Familiarity);
            MixFloat(image.Entrenchment); MixFloat(image.Target); Mix(image.LastReviewDay);
            Mix(image.ConsistentDays); Mix(image.Status); Mix(image.LastIncidentDay); MixText(image.LastReason);
        }

        // Hours, not days. A step can be a fraction of a day or several of them, so two
        // worlds can agree on the date and still be a step apart - which is a divergence
        // that would otherwise be invisible until it had grown into a real one.
        Mix(campaign.ElapsedHours);
        if (campaign.DealOffers.Count > 0 || campaign.NextDealOfferId != 1)
        {
            MixText("deal_negotiations"); Mix(campaign.NextDealOfferId); Mix(campaign.DealOffers.Count);
            foreach (var paper in campaign.DealOffers.OrderBy(o => o.Id))
            {
                Mix(paper.Id); Mix(paper.FromId); Mix(paper.ToId); Mix(paper.CreatedDay); Mix(paper.ExpiresDay);
                // Reason is presentation text; locale must not diverge authoritative state.
                Mix(paper.ClosedDay); Mix(paper.ParentOfferId); Mix(paper.CounterDepth); Mix((int)paper.State);
                foreach (var province in paper.FromProvinces ?? Array.Empty<int>()) Mix(province);
                Mix(-102);
                foreach (var province in paper.ToProvinces ?? Array.Empty<int>()) Mix(province);
                Mix(-103);
                foreach (var side in new[] { paper.From, paper.To })
                {
                    MixFloat(side.Cash); Mix(side.ReleaseRegions ? 1 : 0); Mix(side.LiftEmbargo ? 1 : 0);
                    Mix(side.NonAggression ? 1 : 0); Mix(side.TradeAgreement ? 1 : 0);
                    foreach (var region in side.Regions ?? Array.Empty<int>()) Mix(region);
                    Mix(-101);
                }
            }
        }
        foreach (var character in campaign.ScenarioId ?? "2024") Mix(character);
        Mix(-1);
        foreach (var id in campaign.Defines.Features.Keys.Concat(campaign.FeatureOverrides.Keys).Concat(MechanicFeatures.BuiltIns)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var character in id.ToLowerInvariant()) Mix(character);
            Mix(-2); Mix(campaign.FeatureEnabled(id) ? 1 : 0);
        }
        foreach (var table in new[] { campaign.Defines.FeatureUnlocks, campaign.Defines.FeatureCommands,
                     campaign.Defines.FeatureScreens, campaign.Defines.FeatureSystems })
        {
            Mix(-3);
            foreach (var entry in table.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var character in entry.Key.ToLowerInvariant()) Mix(character);
                Mix(-4);
                foreach (var character in entry.Value ?? "") Mix(character);
                Mix(-5);
            }
        }

        // The region list is no longer fixed: a border cuts a region in two and a border
        // going away puts it back. Two worlds that disagree about how many regions exist
        // disagree about everything downstream of that.
        Mix(campaign.States.Count);
        foreach (var pair in campaign.Relations.OrderBy(p => p.Key))
        {
            Mix(pair.Key); var r = pair.Value;
            Mix((int)r.Stance); MixFloat(r.Opinion); MixFloat(r.AllianceCohesion);
            Mix(r.AllianceAssessmentDay); Mix(r.AllianceStrainedDays); Mix(r.AllianceReaffirmedDay);
            Mix(r.AllianceRefusedDay); Mix(r.AllianceSinceDay);
            foreach (var character in r.AllianceProgram ?? "") Mix(character);
            Mix(-1); Mix(r.AllianceProgramSponsor); Mix(r.AllianceProgramStarted);
            Mix(r.AllianceProgramLastPaid); Mix(r.AllianceProgramDuration); Mix(r.AllianceProgramPaidWeeks);
            MixFloat(r.AllianceProgramWeeklyCost); MixFloat(r.AllianceProgramTrustBudget);
            Mix(r.AllianceProgramRelief ? 1 : 0);
            Mix(r.AllianceProgramPausedDay);
            // How close the two are to fighting, and what they hold against each other (Escalation).
            MixFloat(r.Tension); MixFloat(r.StructuralTension); MixFloat(r.TensionBaseline); MixFloat(r.TensionAnchor); MixFloat(r.GrievanceA); MixFloat(r.GrievanceB);
            Mix(r.TensionStage); Mix(r.LastIncidentDay); Mix(r.TalksUntilDay); Mix(r.LastTalksDay);
        }
        // Who is arming whom in other people's wars (ProxyWars).
        Mix(campaign.NextOrganizationId);
        foreach (var o in campaign.Organizations.OrderBy(o => o.Id))
        {
            Mix(o.Id); Mix(o.FounderId); Mix(o.FoundedDay); Mix(o.LastReviewDay); Mix(o.NextMotionId);
            MixText(o.Name); MixText(o.GeneratedCountryTag); MixText(o.CharterId); MixFloat(o.Budget); MixFloat(o.Cohesion); Mix(o.Dissolved ? 1 : 0);
            MixText(o.LastOutcome); Mix(o.LastYes); Mix(o.LastVoters);
            foreach (var member in o.Members.OrderBy(id => id)) Mix(member); Mix(-20);
            foreach (var invitation in o.Invitations.OrderBy(id => id)) Mix(invitation); Mix(-21);
            foreach (var unpaid in o.UnpaidWeeks.OrderBy(p => p.Key)) { Mix(unpaid.Key); Mix(unpaid.Value); } Mix(-22);
            if (o.Motion is { } m)
            {
                Mix(m.Id); Mix(m.ProposerId); Mix(m.TargetId); Mix(m.EndsDay); MixText(m.Kind); MixFloat(m.Amount);
                foreach (var v in m.Votes.OrderBy(v => v.Key)) { Mix(v.Key); Mix(v.Value ? 1 : 0); }
            }
            Mix(-23);
        }
        Mix(campaign.Sponsorships.Count); Mix(campaign.NextSponsorshipId);
        foreach (var s in campaign.Sponsorships)
        {
            Mix(s.Id); Mix(s.SponsorId); Mix(s.ClientId); Mix(s.WarId); Mix(s.Level); Mix(s.Ended ? 1 : 0);
            Mix(s.UnpaidWeeks); MixFloat(s.Delivered);
        }

        foreach (var nation in Ordered(campaign.Nations, nation => nation.Id))
        {
            Mix(nation.Id);
            foreach (var character in nation.FormedNationId ?? "") Mix(character);
            Mix(-1);
            Mix(nation.FormedNationDay);
            foreach (var id in nation.FormedNationHistory.OrderBy(id => id, StringComparer.Ordinal)) MixText(id);
            Mix(-11);
            Mix(nation.Population);
            MixFloat(nation.Treasury);
            Mix(nation.DebtInitialized ? 1 : 0); Mix(nation.DebtMechanicActive ? 1 : 0);
            MixFloat(nation.PublicDebt); MixFloat(nation.DebtArrears); MixFloat(nation.PaymentArrears);
            MixFloat(nation.CreditConfidence); MixFloat(nation.DebtSpreadAnnual);
            MixFloat(nation.DebtIssuedWeekly); MixFloat(nation.DebtInterestWeekly);
            Mix(nation.DebtMissedWeeks); Mix(nation.DebtCrisisWeeks); Mix((int)nation.DebtCondition);
            Mix(nation.DebtRestructuringProgress); Mix(nation.DebtRestructuringLastDay);
            Mix(nation.LawsActive ? 1 : 0); Mix(nation.AgendasActive ? 1 : 0); Mix(nation.LastPolicyReviewDay);
            foreach (var law in nation.Laws.OrderBy(p => p.Key, StringComparer.Ordinal))
            { MixText(law.Key); MixText(law.Value.Id); MixText(law.Value.TargetTag); Mix(law.Value.Region); Mix(law.Value.AdoptedDay); }
            Mix(-8);
            foreach (var clock in nation.PolicyReadyOnDay.OrderBy(p => p.Key, StringComparer.Ordinal)) { MixText(clock.Key); Mix(clock.Value); }
            Mix(-9);
            if (nation.Agenda is { } agenda)
            {
                MixText(agenda.Id); MixText(agenda.Goal); MixFloat(agenda.GoalValue); Mix(agenda.DurationDays);
                Mix(agenda.ElapsedDays); Mix(agenda.SatisfiedDays); Mix(agenda.Completed ? 1 : 0); Mix(agenda.Succeeded ? 1 : 0);
            }
            Mix(-10);
            var course = nation.NationalCourse;
            Mix(nation.NationalCourseActive ? 1 : 0); Mix(course.Initialized ? 1 : 0);
            MixText(course.Dominant); MixText(course.Challenger); MixText(course.ShockReason); MixText(course.RememberedPolitics);
            Mix(course.ChallengerDays); Mix(course.AgeDays); Mix(course.LastReviewDay); Mix(course.RememberedSecessionDay);
            MixFloat(course.Entrenchment); MixFloat(course.Shock); MixFloat(course.PreviousLiving); MixFloat(course.WarExperience);
            foreach (var conviction in course.Convictions.OrderBy(p => p.Key, StringComparer.Ordinal))
            { MixText(conviction.Key); MixFloat(conviction.Value); }
            Mix(-12);
            var crisis = nation.DomesticCrisis ?? new DomesticCrisisState();
            MixFloat(crisis.CoupPressure); Mix(crisis.DaysAboveThreshold); Mix(crisis.LastReviewDay);
            Mix(crisis.CooldownUntilDay); MixText(crisis.LastOutcomeId); Mix(crisis.LastOutcomeDay);
            MixText(crisis.ResponseId); Mix(crisis.ResponseUntilDay); Mix(crisis.ResponseCooldownUntilDay);
            Mix(nation.CoupPlotDay); Mix(nation.LastCoupDay);
            foreach (var origin in nation.RenouncedTerritoryOrigins.OrderBy(id => id)) Mix(origin);
            Mix(-17);
            MixFloat(nation.Gdp);
            MixFloat(nation.LivingOffset);
            MixText(nation.StartGovernment); MixText(nation.StartIdeology);
            Mix(nation.Movement is null ? 0 : 1);
            if (nation.Movement is { } movement) MixFloat(movement.Militancy);
            Mix(nation.ProductionReferenceInitialized ? 1 : 0);
            MixFloat(nation.ProductionReferenceGdp); Mix(nation.ProductionReferencePopulation);
            Mix(nation.LastCivilWarDay);
            MixFloat(nation.Stability);
            // What the country is beyond its conditions, and how it has been governed. Both
            // feed stability, and a world that disagrees about them is a world whose
            // stability is about to disagree too.
            MixFloat(nation.StabilityBias);
            MixFloat(nation.StabilityGovernance);
            MixFloat(nation.WarSupport);
            MixFloat(nation.ManpowerReserve);
            // When it last came apart, which is what decides whether it may again.
            Mix(nation.LastSecessionDay);
            Mix(nation.LastCivilWarDay);
            Mix(nation.AssessedDay); MixFloat(nation.AssessedStrength);
            MixText(nation.RememberedInstitutions); Mix(nation.InstitutionAgeDays);
            Mix(nation.LastInstitutionReviewDay); Mix(nation.LastSocialReviewDay);
            MixFloat(nation.InstitutionalHabit); MixFloat(nation.ExpectedLivingStandard);
            MixImage(nation.DomesticImage);
            Mix(nation.PublicImages.Count);
            foreach (var image in nation.PublicImages.OrderBy(p => p.Key, StringComparer.Ordinal))
            { MixText(image.Key); MixImage(image.Value); }
            Mix(nation.Outreach.Count);
            foreach (var entry in nation.Outreach.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var p = entry.Value; MixText(entry.Key); MixText(p.ActionId); MixText(p.TargetTag);
                Mix(p.StartedDay); Mix(p.LastReviewDay); Mix(p.DurationDays); Mix(p.ProgressDays);
                Mix(p.CooldownDays); Mix(p.CooldownUntilDay); Mix(p.PausedDay); Mix(p.Cancelled ? 1 : 0);
                MixFloat(p.PaidCost); MixFloat(p.NationImpulse); MixFloat(p.CultureImpulse);
            }
        }

        // How far each region has gone towards leaving. Two peers that disagree about this
        // agree about everything for years and then one of them loses Tatarstan - which is
        // the worst shape a desync can take, because nothing before the moment it shows
        // looks wrong at all.
        foreach (var state in Ordered(campaign.States, state => state.MapIndex))
        {
            Mix(state.MapIndex);
            Mix(state.Population); MixFloat(state.StandardOfLiving); MixFloat(state.Radicalism); MixFloat(state.WarDamage);
            MixFloat(state.NonBuildingJobs); Mix(state.LastHouseholdReviewDay); Mix(state.ReviewBirths); Mix(state.ReviewDeaths);
            MixFloat(state.BasicNeedsCoverage); MixFloat(state.EmploymentTarget); MixFloat(state.LivelihoodStress);
            foreach (var group in state.PopulationGroups)
            {
                MixText(group.Culture); MixText(group.Religion); MixText(group.Profession); Mix((int)group.Class); Mix((int)group.Sector);
                Mix(group.Size); MixFloat(group.Employment); MixFloat(group.Wealth); MixFloat(group.StandardOfLiving); MixFloat(group.Radicalism);
            }
            Mix(-20);
            MixFloat(state.SecessionPressure);
            MixFloat(state.ResidentAwareness);
            Mix(state.RememberedOwner); Mix(state.SettledBorderDays); Mix(state.LastBorderReviewDay);
            MixFloat(state.BorderHabit);
            foreach (var tag in state.CoreTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) MixText(tag);
            Mix(-18);
            foreach (var tag in state.Claims.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) MixText(tag);
            Mix(-19);
        }
        foreach (var army in Ordered(campaign.Armies, army => army.Id))
        {
            Mix(army.Id);
            Mix(army.ProvinceId);
            Mix(army.Personnel);
            MixFloat(army.Organisation);
            MixFloat(army.Strength);
            MixFloat(army.Entrenchment);
            Mix(army.Route.Count);
        }
        foreach (var war in Ordered(campaign.Wars, war => war.Id))
        {
            Mix(war.Id);
            Mix(war.Ended ? 1 : 0);
            MixFloat(war.OccupationScore);
            MixFloat(war.BattleScore);
            Mix(war.CivilConflict ? 1 : 0); Mix(war.EscalationStage); Mix(war.AiNegotiationUnlockDay);
        }
        var controllers = campaign.ProvinceController;
        if (controllers is null) return hash;
        for (var province = 0; province < controllers.Length; province++) Mix(controllers[province]);
        return hash;
    }

    /// <summary>
    /// The same list, in id order, without building a new one every time.
    ///
    /// This is asked of the host every few days with fourteen hundred formations in it, and
    /// sorting by a key with LINQ would allocate a projection and an array each call - which
    /// is exactly the sort of habit the rest of this simulation has just spent an afternoon
    /// getting rid of.
    /// </summary>
    private static IEnumerable<T> Ordered<T>(List<T> source, Func<T, int> id)
    {
        if (source.Count < 2)
        {
            foreach (var item in source) yield return item;
            yield break;
        }
        // Selection by id, which is O(n²) in principle and fine in practice: the lists are
        // short, the comparison is an integer, and it runs once every few days rather than
        // once a tick. Correctness here is worth more than cleverness.
        var taken = new bool[source.Count];
        for (var emitted = 0; emitted < source.Count; emitted++)
        {
            var best = -1;
            for (var index = 0; index < source.Count; index++)
            {
                if (taken[index]) continue;
                if (best >= 0 && id(source[index]) >= id(source[best])) continue;
                best = index;
            }
            if (best < 0) yield break;
            taken[best] = true;
            yield return source[best];
        }
    }
}
