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
        void MixFloatDictionary(string label, Dictionary<int, float> table)
        {
            if (table is not { Count: > 0 }) return;
            MixText(label); Mix(table.Count);
            foreach (var entry in table.OrderBy(p => p.Key)) { Mix(entry.Key); MixFloat(entry.Value); }
        }
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
        // Homelands that moved in play (HomelandIntegration); nothing mixed while none has, so
        // older worlds keep their fingerprints.
        if (campaign.HomelandChanges > 0)
        {
            MixText("homeland_changes"); Mix(campaign.HomelandChanges);
            foreach (var (id, regions) in campaign.Homelands.OrderBy(p => p.Key))
            { Mix(id); Mix(regions.Count); foreach (var region in regions.OrderBy(r => r)) Mix(region); }
        }
        if (!campaign.Defines.BeliefConversionEnabled || campaign.Defines.BeliefConversionAnnual != .008f)
        {
            MixText("belief_conversion"); Mix(campaign.Defines.BeliefConversionEnabled ? 1 : 0);
            MixFloat(campaign.Defines.BeliefConversionAnnual);
        }
        if (campaign.Curse.Length > 0)
        {
            MixText("world_curse"); MixText(campaign.Curse); Mix(campaign.CurseDay); Mix(campaign.CurseDrowned);
        }
        if (campaign.DefconLevel != 5 || campaign.DefconDaysInStage > 0 || campaign.DefconPeakLevel != 5)
        {
            MixText("defcon"); Mix(campaign.DefconLevel); Mix(campaign.DefconDaysInStage); Mix(campaign.DefconPeakLevel);
        }
        foreach (var n in campaign.Nations.OrderBy(n => n.Id))
            if (n.NuclearPosture != 5 || n.NuclearPostureDay != -100000 || n.LastNuclearTestDay != -100000 || n.LastHotlineDay != -100000)
            {
                MixText("nuclear_posture"); Mix(n.Id); Mix(n.NuclearPosture); Mix(n.NuclearPostureDay);
                Mix(n.LastNuclearTestDay); Mix(n.LastHotlineDay);
            }
        foreach (var n in campaign.Nations.OrderBy(n => n.Id))
            if (n.LastNuclearStrikeDay != -100000) { MixText("nuclear_strike"); Mix(n.Id); Mix(n.LastNuclearStrikeDay); }
        foreach (var army in campaign.Armies.OrderBy(a => a.Id))
            if (army.LentById >= 0) { MixText("volunteers"); Mix(army.Id); Mix(army.LentById); Mix(army.LentDay); }
        foreach (var n in campaign.Nations.OrderBy(n => n.Id))
            if (n.NuclearStruckDay != -100000 || n.NuclearStruckBy != -1 || n.NuclearFirstUseDay != -100000)
            {
                MixText("nuclear_struck"); Mix(n.Id); Mix(n.NuclearStruckDay); Mix(n.NuclearStruckBy); Mix(n.NuclearFirstUseDay);
            }
        if (!campaign.Defines.FrontStaffDisengages || campaign.Defines.FrontDisengagementMinimumBattleDays != 2 ||
            campaign.Defines.FrontDisengagementMaximumShare != .35f || campaign.Defines.FrontDisengagementDefenceRatio != .8f)
        {
            MixText("front_disengagement_rules"); Mix(campaign.Defines.FrontStaffDisengages ? 1 : 0);
            Mix(campaign.Defines.FrontDisengagementMinimumBattleDays);
            MixFloat(campaign.Defines.FrontDisengagementMaximumShare); MixFloat(campaign.Defines.FrontDisengagementDefenceRatio);
        }
        var equipmentRules = campaign.Defines;
        if (!equipmentRules.EquipmentReplenishmentEnabled || equipmentRules.EquipmentReplenishmentPerDay != .02f ||
            equipmentRules.EquipmentCombatDeliveryShare != .4f || equipmentRules.EquipmentMinimumSupply != .22f)
        {
            MixText("equipment_delivery_rules"); Mix(equipmentRules.EquipmentReplenishmentEnabled ? 1 : 0);
            MixFloat(equipmentRules.EquipmentReplenishmentPerDay); MixFloat(equipmentRules.EquipmentCombatDeliveryShare);
            MixFloat(equipmentRules.EquipmentMinimumSupply);
        }
        if (campaign.Defines.SupplyCutoffDays != 14)
        {
            MixText("supply_cutoff_days");
            Mix(campaign.Defines.SupplyCutoffDays);
        }
        if (MathF.Abs(campaign.Defines.SupplyAttackCoverageFloor - 0.5f) > 0.0001f)
        {
            MixText("supply_attack_cover");
            MixFloat(campaign.Defines.SupplyAttackCoverageFloor);
        }
        if (campaign.Defines.DoctrineBooksPerShelf != 2)
        {
            MixText("doctrine_books_per_shelf");
            Mix(campaign.Defines.DoctrineBooksPerShelf);
        }
        if (MathF.Abs(campaign.Defines.ResearchUseShare - Defines.DefaultResearchUseShare) > 0.0001f ||
            MathF.Abs(campaign.Defines.ResearchUseCeiling - Defines.DefaultResearchUseCeiling) > 0.0001f)
        {
            MixText("research_field_use");
            MixFloat(campaign.Defines.ResearchUseShare);
            MixFloat(campaign.Defines.ResearchUseCeiling);
        }
        // Non-default combat-tactic rules, in the same discipline: a world fought without
        // manoeuvres must not share a fingerprint with one fought with them.
        if (!campaign.Defines.CombatTacticsEnabled ||
            MathF.Abs(campaign.Defines.CombatCounterTacticBonus - 1.15f) > 0.0001f)
        {
            MixText("combat_tactics");
            Mix(campaign.Defines.CombatTacticsEnabled ? 1 : 0);
            MixFloat(campaign.Defines.CombatCounterTacticBonus);
        }
        var diplomacyRules = campaign.Defines;
        if (!diplomacyRules.AiFormableProclamations || diplomacyRules.AiFormableReviewDays != 30 ||
            diplomacyRules.AiFormableEarliestDay != 120 || diplomacyRules.AiFormableMinimumIntervalDays != 1825 ||
            diplomacyRules.AiFormableCrisisRecoveryDays != 180 || diplomacyRules.AiFormableTreasuryReserveShare != .02f ||
            diplomacyRules.AiFormableMinimumScore != .58f)
        {
            MixText("formable_proclamation_ai_rules"); Mix(diplomacyRules.AiFormableProclamations ? 1 : 0);
            Mix(diplomacyRules.AiFormableReviewDays); Mix(diplomacyRules.AiFormableEarliestDay);
            Mix(diplomacyRules.AiFormableMinimumIntervalDays); Mix(diplomacyRules.AiFormableCrisisRecoveryDays);
            MixFloat(diplomacyRules.AiFormableTreasuryReserveShare); MixFloat(diplomacyRules.AiFormableMinimumScore);
        }
        if (!diplomacyRules.PeaceAlliedBenefits)
        { MixText("peace_allied_benefits"); Mix(0); }
        if (!diplomacyRules.OrganicOrganizationReachEnabled || diplomacyRules.AiOrganizationExpansionLimit != 64 ||
            diplomacyRules.AiOrganizationGlobalGdp != 1000 || diplomacyRules.AiOrganizationFoundingPoliticalCost != 30 ||
            diplomacyRules.OrganizationLeaderRiftPenalty != .24f)
        {
            MixText("organization_reach_rules"); Mix(diplomacyRules.OrganicOrganizationReachEnabled ? 1 : 0);
            Mix(diplomacyRules.AiOrganizationExpansionLimit); MixFloat(diplomacyRules.AiOrganizationGlobalGdp);
            MixFloat(diplomacyRules.AiOrganizationFoundingPoliticalCost); MixFloat(diplomacyRules.OrganizationLeaderRiftPenalty);
        }
        if (!diplomacyRules.DiplomaticMissionsEnabled || diplomacyRules.DiplomaticMissionPoliticalCost != 8 ||
            diplomacyRules.DiplomaticMissionWeeklyPoliticalCost != 1.5f || diplomacyRules.DiplomaticMissionStartCash != .1f ||
            diplomacyRules.DiplomaticMissionWeeklyCash != .01f || diplomacyRules.DiplomaticMissionDays != 180 ||
            diplomacyRules.DiplomaticMissionLimit != 3 || diplomacyRules.DiplomaticMissionWeeklyOpinion != 1.2f ||
            diplomacyRules.DiplomaticMissionOpinionCap != 24)
        {
            MixText("diplomatic_mission_rules"); Mix(diplomacyRules.DiplomaticMissionsEnabled ? 1 : 0);
            MixFloat(diplomacyRules.DiplomaticMissionPoliticalCost); MixFloat(diplomacyRules.DiplomaticMissionWeeklyPoliticalCost);
            MixFloat(diplomacyRules.DiplomaticMissionStartCash); MixFloat(diplomacyRules.DiplomaticMissionWeeklyCash);
            Mix(diplomacyRules.DiplomaticMissionDays); Mix(diplomacyRules.DiplomaticMissionLimit);
            MixFloat(diplomacyRules.DiplomaticMissionWeeklyOpinion); MixFloat(diplomacyRules.DiplomaticMissionOpinionCap);
        }
        if (campaign.Defines.AiFiscalReserveWeeks != 8)
        {
            MixText("ai_fiscal_runway");
            Mix(campaign.Defines.AiFiscalReserveWeeks);
        }
        if (!campaign.Defines.AiCrisisGrants || campaign.Defines.AiCrisisGrantRetryDays != 360)
        {
            MixText("crisis_grant_rule");
            Mix(campaign.Defines.AiCrisisGrants ? 1 : 0);
            Mix(campaign.Defines.AiCrisisGrantRetryDays);
        }
        if (!campaign.Defines.AiSupplyHedges || campaign.Defines.AiSupplyHedgeShare != 0.75f ||
            campaign.Defines.AiSupplyHedgeSatisfaction != 0.875f || campaign.Defines.AiSupplyHedgeRetryDays != 180)
        {
            MixText("supply_hedge_rule");
            Mix(campaign.Defines.AiSupplyHedges ? 1 : 0);
            MixFloat(campaign.Defines.AiSupplyHedgeShare);
            MixFloat(campaign.Defines.AiSupplyHedgeSatisfaction);
            Mix(campaign.Defines.AiSupplyHedgeRetryDays);
        }
        // Preserve the old default fingerprint, but detect different modded assault
        // rules before peers reach a battle and make different decisions.
        if (campaign.Defines.AutonomousAssaultMinimumOrganisation != .16f ||
            campaign.Defines.AutonomousAssaultMinimumSupply != .20f)
        {
            MixText("autonomous_assault_rules");
            MixFloat(campaign.Defines.AutonomousAssaultMinimumOrganisation);
            MixFloat(campaign.Defines.AutonomousAssaultMinimumSupply);
        }
        // These rules decide whether a movement strikes, marches or collects a levy.
        // Compiled defaults add no bytes to an untouched legacy world; a scenario's
        // different thresholds must be detected before its first divergent command.
        var movementRules = campaign.Defines;
        if (movementRules.MovementStrikeMomentum != .4f || movementRules.MovementMarchMomentum != .65f ||
            movementRules.MovementParallelMomentum != .5f || movementRules.MovementParallelSupport != .6f ||
            movementRules.MovementParallelLevy != .0006f || movementRules.MovementStrikeDays != 42 ||
            movementRules.MovementMarchSupport != .5f)
        {
            MixText("movement_power_rules");
            MixFloat(movementRules.MovementStrikeMomentum); MixFloat(movementRules.MovementMarchMomentum);
            MixFloat(movementRules.MovementParallelMomentum); MixFloat(movementRules.MovementParallelSupport);
            MixFloat(movementRules.MovementParallelLevy); Mix(movementRules.MovementStrikeDays);
            MixFloat(movementRules.MovementMarchSupport);
        }
        var outbreakBudget = CivilWarOutbreak.Budget(campaign);
        if (outbreakBudget.StartsByDay is { Count: > 0 })
        {
            var outbreakDays = outbreakBudget.StartsByDay.Where(p =>
                CivilWarBudgetState.InWindow(p.Key, campaign.ElapsedDays) && p.Value > 0).OrderBy(p => p.Key).ToArray();
            if (outbreakDays.Length > 0)
            {
                MixText("civil_war_budget"); Mix(outbreakDays.Length);
                foreach (var day in outbreakDays) { Mix(day.Key); Mix(day.Value); }
            }
        }
        if (campaign.UltimatumPapers.Count > 0 || campaign.NextUltimatumId != 1)
        {
            MixText("ultimatum_desk"); Mix(campaign.NextUltimatumId); Mix(campaign.UltimatumPapers.Count);
            foreach (var demand in campaign.UltimatumPapers.OrderBy(paper => paper.Id))
            {
                Mix(demand.Id); Mix(demand.FromId); Mix(demand.ToId); Mix(demand.CreatedDay); Mix(demand.ExpiresDay);
                Mix(demand.ClosedDay); Mix((int)demand.State); MixFloat(demand.Reparations);
                Mix(demand.LiftEmbargo ? 1 : 0); Mix(demand.NonAggression ? 1 : 0);
                if (demand.Annex || demand.Vassalise || demand.Disarm || demand.BreakAlliances || demand.ReleaseSubjects ||
                    demand.TechnologyTransfer || demand.ChangeGovernment || demand.FleetTransfer)
                {
                    MixText("extended_ultimatum");
                    Mix(demand.Annex ? 1 : 0); Mix(demand.Vassalise ? 1 : 0); Mix(demand.Disarm ? 1 : 0);
                    Mix(demand.BreakAlliances ? 1 : 0); Mix(demand.ReleaseSubjects ? 1 : 0);
                    Mix(demand.TechnologyTransfer ? 1 : 0); Mix(demand.ChangeGovernment ? 1 : 0); Mix(demand.FleetTransfer ? 1 : 0);
                }
                foreach (var region in demand.Regions ?? Array.Empty<int>()) Mix(region);
                if (!string.IsNullOrEmpty(demand.SubjectKind)) { MixText("ult_kind"); MixText(demand.SubjectKind); }
                if (demand.TradeAgreement || demand.EmbargoThird || demand.BreakNamed || demand.CancelGuarantee ||
                    demand.JoinAlliance || demand.AcceptGuarantee || demand.MilitaryAccess || demand.WhitePeace ||
                    demand.CedeOccupied || demand.Humiliate || demand.ReleaseNamed || demand.Liberate)
                {
                    MixText("ultimatum_settlement");
                    if (demand.TradeAgreement) Mix(1);
                    if (demand.EmbargoThird) { MixText("embargo"); Mix(demand.EmbargoOnId); }
                    if (demand.BreakNamed) { MixText("break"); Mix(demand.BreakWithId); }
                    if (demand.CancelGuarantee) { MixText("guarantee"); Mix(demand.CancelGuaranteeId); }
                    if (demand.JoinAlliance) MixText("join");
                    if (demand.AcceptGuarantee) MixText("accept");
                    if (demand.MilitaryAccess) MixText("access");
                    if (demand.WhitePeace) MixText("white");
                    if (demand.CedeOccupied) MixText("status");
                    if (demand.Humiliate) MixText("humiliate");
                    if (demand.ReleaseNamed) { MixText("release"); Mix(demand.ReleaseSubjectId); }
                    if (demand.Liberate) { MixText("liberate"); Mix(demand.LiberateRegion); }
                }
                Mix(-104);
            }
        }
        // The world credit cycle (TreasuryDynamics); a campaign that has not started it carries no bytes.
        if (campaign.WorldLeverage >= 0f || campaign.LastFinancialCrisisDay != -100000)
        { MixText("credit_cycle"); MixFloat(campaign.WorldLeverage); Mix(campaign.LastFinancialCrisisDay); }
        // Leases and troops in without a war, and the offers of leases on a player's desk.
        if (campaign.LandArrangements.Count > 0 || campaign.NextLandArrangementId != 1)
        {
            MixText("land_arrangements"); Mix(campaign.NextLandArrangementId); Mix(campaign.LandArrangements.Count);
            foreach (var arrangement in campaign.LandArrangements.OrderBy(a => a.Id))
            {
                Mix(arrangement.Id); MixText(arrangement.Kind); Mix(arrangement.OwnerId); Mix(arrangement.HolderId);
                Mix(arrangement.StartDay); Mix(arrangement.EndDay); MixFloat(arrangement.RentPerYear); Mix(arrangement.UnpaidWeeks);
                MixText(arrangement.Purpose); MixText(arrangement.Origin); Mix(arrangement.LeavingOnDay);
                foreach (var region in arrangement.Regions) Mix(region);
                Mix(-105);
            }
        }
        if (campaign.LeaseOffers.Count > 0 || campaign.NextLeaseOfferId != 1)
        {
            MixText("lease_offers"); Mix(campaign.NextLeaseOfferId); Mix(campaign.LeaseOffers.Count);
            foreach (var offer in campaign.LeaseOffers.OrderBy(o => o.Id))
            {
                Mix(offer.Id); Mix(offer.FromId); Mix(offer.ToId); Mix(offer.Region); Mix(offer.Years);
                Mix(offer.CreatedDay); Mix(offer.ExpiresDay); MixFloat(offer.RentPerYear); Mix(offer.Renewal ? 1 : 0);
                Mix(offer.ArrangementId); MixText(offer.Purpose); MixText(offer.State);
                Mix(-106);
            }
        }
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
        MixText("foreign_assistance"); Mix(campaign.NextAssistanceRequestId); Mix(campaign.AssistanceLastTickDay);
        Mix(campaign.AssistanceRequests.Count);
        foreach (var request in campaign.AssistanceRequests.OrderBy(r => r.Id))
        {
            Mix(request.Id); Mix(request.RequesterId); Mix(request.DonorId); Mix(request.WarId);
            Mix(request.CreatedDay); Mix(request.ExpiresDay); Mix(request.ClosedDay); Mix((int)request.State);
            MixFloat(request.Amount); MixFloat(request.GrantedAmount); Mix(request.HumanConsent ? 1 : 0);
        }
        Mix(campaign.AssistanceCooldowns.Count);
        foreach (var clock in campaign.AssistanceCooldowns.OrderBy(x => x.RequesterId).ThenBy(x => x.DonorId))
        { Mix(clock.RequesterId); Mix(clock.DonorId); Mix(clock.ReadyOnDay); }
        Mix(campaign.Defines.AidRequestCooldownDays); Mix(campaign.Defines.AidRequestOfferDays);
        Mix(campaign.Defines.AiAidRequestReviewDays); MixFloat(campaign.Defines.AiAidRequestWillingness);
        Mix(campaign.Defines.AidIntervalDays); MixFloat(campaign.Defines.AidShareOfRecipientGdp);
        MixFloat(campaign.Defines.AidMaxShareOfTreasury); MixFloat(campaign.Defines.AiAidReserveOfGdp);
        MixFloat(campaign.Defines.AiAidCrisisTreasuryOfGdp);
        // Empty extension state must not alter the original game's deterministic fingerprint.
        if (campaign.CrossoverScars.Count > 0 || campaign.CrossoverWorks.Count > 0 ||
            campaign.CrossoverImpacts.Count > 0 || campaign.DestroyedProvinces.Count > 0)
        {
            MixText("bta_bridge");
            Mix(campaign.CrossoverScars.Count);
            foreach (var cell in campaign.CrossoverScars.OrderBy(k => k)) Mix(cell);
            Mix(campaign.CrossoverWorks.Count);
            foreach (var work in campaign.CrossoverWorks.OrderBy(w => w.Id, StringComparer.Ordinal)) {
                MixText(work.Id); Mix(work.Province); Mix(work.State); Mix(work.Nation); Mix((int)work.Building);
                MixFloat(work.X); MixFloat(work.Y); MixFloat(work.Z); Mix(work.Active ? 1 : 0);
                foreach (var cell in work.Blocks.OrderBy(k => k)) Mix(cell); Mix(-100);
            }
            foreach (var impact in campaign.CrossoverImpacts) {
                Mix(impact.Hour); Mix(impact.Province); MixText(impact.Kind); Mix(impact.Population);
                MixFloat(impact.Gdp); MixText(impact.Message);
            }
            Mix(campaign.DestroyedProvinces.Count);
            foreach (var province in campaign.DestroyedProvinces.OrderBy(p => p)) Mix(province);
        }
        foreach (var character in campaign.ScenarioId ?? "2024") Mix(character);
        Mix(-1);
        foreach (var id in campaign.Defines.Features.Keys.Concat(campaign.FeatureOverrides.Keys).Concat(MechanicFeatures.BuiltIns)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            // A switch added after the table was frozen speaks only when it is not simply on.
            if (MechanicFeatures.LateBuiltIns.Contains(id) && campaign.FeatureEnabled(id) &&
                !campaign.FeatureOverrides.ContainsKey(id)) continue;
            foreach (var character in id.ToLowerInvariant()) Mix(character);
            Mix(-2); Mix(campaign.FeatureEnabled(id) ? 1 : 0);
        }
        // The define bindings count only when a scenario changed them: the shipped table adds no bytes.
        var defineBindings = campaign.Defines.FeatureDefines;
        var shippedBindings = MechanicFeatures.DefineDefaults();
        var boundDefines = defineBindings.Count == shippedBindings.Count &&
            defineBindings.All(p => shippedBindings.TryGetValue(p.Key, out var v) && v == p.Value) ? null : defineBindings;
        foreach (var table in new[] { campaign.Defines.FeatureUnlocks, campaign.Defines.FeatureCommands,
                     campaign.Defines.FeatureScreens, campaign.Defines.FeatureSystems, boundDefines }.Where(t => t is not null))
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
        if (campaign.Nations.Exists(n => n.InstantConstruction))
        {
            MixText("instant_construction");
            foreach (var n in campaign.Nations) if (n.InstantConstruction) Mix(n.Id);
        }
        // Waking and sleeping alike (RelationDormancy): which table a relation is in is told below.
        foreach (var pair in campaign.Relations.Concat(campaign.DormantRelations).OrderBy(p => p.Key))
        {
            Mix(pair.Key); var r = pair.Value;
            if (r.MilitaryAccessByA || r.MilitaryAccessByB || r.MilitaryAccessRequestedBy >= 0 || r.MilitaryAccessRequestedDay != -9999)
            {
                MixText("military_access_v1"); Mix(r.MilitaryAccessByA ? 1 : 0); Mix(r.MilitaryAccessByB ? 1 : 0);
                Mix(r.MilitaryAccessRequestedBy); Mix(r.MilitaryAccessRequestedDay);
            }
            Mix((int)r.Stance); MixFloat(r.Opinion); MixFloat(r.AllianceCohesion);
            Mix(r.AidSentDay);
            // A question waiting on somebody's answer is state: two worlds that disagree about
            // whether a request is pending will disagree about what both sides do next. Mixed
            // only when it is set, so a world with no offers keeps the fingerprint it had.
            if (r.GuaranteeRequestedBy >= 0)
            {
                MixText("guarantee_request_v1");
                Mix(r.GuaranteeRequestedBy); Mix(r.GuaranteeRequestedDay);
            }
            if (r.AgreementOfferedBy >= 0)
            {
                MixText("agreement_offer_v1");
                Mix(r.AgreementOfferedBy); Mix((int)r.AgreementOfferedKind); Mix(r.AgreementOfferedDay);
            }
            if (r.DiplomaticActBy >= 0)
            {
                MixText("diplomatic_act_v1");
                Mix(r.DiplomaticActBy); Mix(r.DiplomaticActKind); Mix(r.DiplomaticActDay);
            }
            Mix(r.AllianceAssessmentDay); Mix(r.AllianceStrainedDays); Mix(r.AllianceReaffirmedDay);
            Mix(r.AllianceRefusedDay); Mix(r.AllianceSinceDay);
            foreach (var character in r.AllianceProgram ?? "") Mix(character);
            Mix(-1); Mix(r.AllianceProgramSponsor); Mix(r.AllianceProgramStarted);
            Mix(r.AllianceProgramLastPaid); Mix(r.AllianceProgramDuration); Mix(r.AllianceProgramPaidWeeks);
            MixFloat(r.AllianceProgramWeeklyCost); MixFloat(r.AllianceProgramTrustBudget);
            Mix(r.AllianceProgramRelief ? 1 : 0);
            Mix(r.AllianceProgramPausedDay);
            foreach (var reason in r.Reasons.Where(reason=>reason.Id=="diplomatic_outreach"))
            {
                MixText("diplomatic_goodwill"); Mix(reason.GrantedDay); Mix(reason.ExpiresDay);
                Mix(reason.Stacks); MixFloat(reason.Scale);
            }
            if (r.MissionByA is not null || r.MissionByB is not null)
            {
                MixText("diplomatic_missions_v1");
                foreach (var mission in new[] { r.MissionByA, r.MissionByB })
                {
                    Mix(mission is null ? 0 : 1); if (mission is null) continue;
                    Mix(mission.StartedDay); Mix(mission.EndsDay); Mix(mission.LastPaidDay); Mix(mission.PaidWeeks);
                    MixFloat(mission.WeeklyPoliticalCost); MixFloat(mission.WeeklyCash); MixFloat(mission.WeeklyOpinion); MixFloat(mission.OpinionCap);
                }
            }
            if (r.LoanLender >= 0)
            {
                MixText("development_loan"); Mix(r.LoanLender); MixFloat(r.LoanOutstanding); MixFloat(r.LoanWeekly); Mix(r.LoanMissedWeeks);
            }
            // How close the two are to fighting, and what they hold against each other (Escalation).
            MixFloat(r.Tension); MixFloat(r.StructuralTension); MixFloat(r.TensionBaseline); MixFloat(r.TensionAnchor); MixFloat(r.GrievanceA); MixFloat(r.GrievanceB);
            Mix(r.TensionStage); Mix(r.LastIncidentDay); Mix(r.TalksUntilDay); Mix(r.LastTalksDay);
            Mix(r.MediatorId); Mix(r.LastMediationDay); Mix(r.InterveningPowerId); Mix(r.InterventionTargetId); Mix(r.InterventionUntilDay);
            if (r.CrisisOpenedDay >= 0) { MixText("crisis_opened"); Mix(r.CrisisOpenedDay); }
        }
        // Who is arming whom in other people's wars (ProxyWars).
        Mix(campaign.NextOrganizationId);
        foreach (var o in campaign.Organizations.OrderBy(o => o.Id))
        {
            Mix(o.Id); Mix(o.FounderId); Mix(o.FoundedDay); Mix(o.LastReviewDay); Mix(o.NextMotionId);
            MixText(o.Name); MixText(o.GeneratedCountryTag); MixText(o.CharterId); MixFloat(o.Budget); MixFloat(o.Cohesion); Mix(o.Dissolved ? 1 : 0);
            if (!string.IsNullOrEmpty(o.DoctrineId)) { MixText("org_doctrine"); MixText(o.DoctrineId); }
            if (o.CrisisMemberId >= 0) { MixText("org_crisis"); Mix(o.CrisisMemberId); Mix(o.CrisisSinceDay); }
            // Suspended seats (OrganizationDiscipline): mixed only when there are any, so a world
            // without a suspension hashes exactly as before.
            if (o.Suspended.Count > 0 || o.SuspendedFor.Count > 0)
            {
                MixText("org_suspended");
                foreach (var seat in o.Suspended.OrderBy(entry => entry.Key)) { Mix(seat.Key); Mix(seat.Value); MixText(o.SuspendedFor.GetValueOrDefault(seat.Key, "")); }
                Mix(-24);
            }
            MixText(o.LastOutcome); Mix(o.LastYes); Mix(o.LastVoters);
            foreach (var member in o.Members.OrderBy(id => id)) Mix(member); Mix(-20);
            foreach (var invitation in o.Invitations.OrderBy(id => id)) Mix(invitation); Mix(-21);
            foreach (var unpaid in o.UnpaidWeeks.OrderBy(p => p.Key)) { Mix(unpaid.Key); Mix(unpaid.Value); } Mix(-22);
            if (o.Motion is { } m)
            {
                Mix(m.Id); Mix(m.ProposerId); Mix(m.TargetId); Mix(m.EndsDay); MixText(m.Kind); MixFloat(m.Amount);
                if (!string.IsNullOrEmpty(m.Subject)) { MixText("motion_subject"); MixText(m.Subject); }
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
        if (campaign.ArmsShipmentRequests.Count > 0 || campaign.ArmsShipmentCooldowns.Count > 0 ||
            campaign.NextArmsShipmentRequestId != 1)
        {
            MixText("arms_shipment_v1");
            Mix(campaign.NextArmsShipmentRequestId);
            Mix(campaign.ArmsShipmentRequests.Count);
            foreach (var request in campaign.ArmsShipmentRequests.OrderBy(r => r.Id))
            {
                Mix(request.Id); Mix(request.RequesterId); Mix(request.DonorId); Mix(request.WarId);
                Mix(request.CreatedDay); Mix(request.ExpiresDay); Mix(request.ClosedDay); Mix((int)request.State);
                MixFloat(request.Share); Mix(request.Days); MixFloat(request.Shipped); Mix(request.HumanConsent ? 1 : 0);
                MixText(request.Pretext ?? "");
            }
            Mix(campaign.ArmsShipmentCooldowns.Count);
            foreach (var clock in campaign.ArmsShipmentCooldowns.OrderBy(x => x.RequesterId).ThenBy(x => x.DonorId))
            { Mix(clock.RequesterId); Mix(clock.DonorId); Mix(clock.ReadyOnDay); }
        }

        foreach (var nation in Ordered(campaign.Nations, nation => nation.Id))
        {
            if (nation.GovernmentSeatProvinceId != 0)
            { MixText("government_seat"); Mix(nation.GovernmentSeatProvinceId); }
            if (nation.Delegated.Count > 0 || nation.ArmyByHand)
            {
                MixText("staff_portfolios_v1"); Mix(nation.ArmyByHand ? 1 : 0); Mix(nation.Delegated.Count);
                foreach (var portfolio in nation.Delegated.OrderBy(p => p, StringComparer.Ordinal)) MixText(portfolio);
            }
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
            if (crisis.ConciliationUntilDay >= 0)
            { MixText("conciliation"); Mix(crisis.ConciliationRegion); Mix(crisis.ConciliationUntilDay); }
            if (crisis.SpendingMemory >= 0f)
            {
                MixText("street"); MixFloat(crisis.ProtestGrievance); MixFloat(crisis.ProtestIntensity);
                MixFloat(crisis.SpendingMemory); MixFloat(crisis.MilitaryMemory);
                Mix(crisis.LastProtestNewsDay); Mix(crisis.LastConcessionDay);
            }
            Mix(nation.CoupPlotDay); Mix(nation.LastCoupDay);
            if (nation.LastPoliticalScandalDay >= 0)
            { MixText("political_scandal"); Mix(nation.LastPoliticalScandalDay); }
            if (nation.LastMilitaryCoupDay >= 0)
            { MixText("last_military_coup"); Mix(nation.LastMilitaryCoupDay); }
            if (nation.Parliament is { } parl && (parl.PeaceMandateActive || parl.MilitaryBudgetCutActive || parl.LastReviewDay >= 0))
            {
                MixText("parliament_state");
                MixFloat(parl.RulingBlocShare);
                MixFloat(parl.OppositionShare);
                MixFloat(parl.DovishShare);
                Mix(parl.PeaceMandateActive ? 1 : 0);
                Mix(parl.PeaceMandateDay);
                Mix(parl.MilitaryBudgetCutActive ? 1 : 0);
                Mix(parl.LastReviewDay);
                MixFloat(parl.FactionDiscontent);
                MixText(parl.FavoredPortfolio ?? "");
                MixText(parl.FavoredLawCategory ?? "");
            }
            var grantClocks = 0;
            foreach (var clock in nation.DecisionAvailableDay.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (clock.Key.StartsWith(RevoltRecovery.Prefix, StringComparison.Ordinal) ||
                    clock.Key.StartsWith(SecessionResponse.Prefix, StringComparison.Ordinal))
                { MixText(clock.Key); Mix(clock.Value); }
            foreach (var clock in nation.DecisionAvailableDay)
                if (clock.Key.StartsWith("crisis_grant/", StringComparison.Ordinal)) grantClocks++;
            if (grantClocks > 0)
            {
                MixText("crisis_grant");
                foreach (var clock in nation.DecisionAvailableDay.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    if (clock.Key.StartsWith("crisis_grant/", StringComparison.Ordinal))
                    { MixText(clock.Key); Mix(clock.Value); }
            }
            var hedgeClocks = 0;
            foreach (var clock in nation.DecisionAvailableDay)
                if (clock.Key.StartsWith("supply_hedge/", StringComparison.Ordinal)) hedgeClocks++;
            if (hedgeClocks > 0)
            {
                MixText("supply_hedge");
                foreach (var clock in nation.DecisionAvailableDay.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    if (clock.Key.StartsWith("supply_hedge/", StringComparison.Ordinal))
                    { MixText(clock.Key); Mix(clock.Value); }
            }
            foreach (var origin in nation.RenouncedTerritoryOrigins.OrderBy(id => id)) Mix(origin);
            Mix(-17);
            // Kept empty-state compatible, like the movement command state above: a country
            // with no promises out hashes exactly as it did before the mechanic existed.
            if (nation.Promises.Count > 0)
            {
                foreach (var promise in nation.Promises.OrderBy(p => p.Id, StringComparer.Ordinal))
                { MixText(promise.Id); Mix(promise.MadeDay); Mix(promise.DueDay); }
                Mix(-30);
            }
            // A goal is private, but it is still state two peer campaigns must agree on.
            if (nation.Goals.Count > 0)
            {
                foreach (var goal in nation.Goals.OrderBy(g => g.Id, StringComparer.Ordinal))
                { MixText(goal.Id); Mix(goal.AdoptedDay); Mix(goal.CompletedDay); }
                Mix(-31);
            }
            // The declared identity is public fact, and it moves who likes whom.
            if (nation.IdentityChangedDay >= 0)
            { MixText("state_identity"); MixText(nation.DeclaredReligion); MixText(nation.DeclaredCulture); Mix(nation.IdentityChangedDay); }
            MixFloat(nation.Gdp);
            MixFloat(nation.WeeklyBalance);
            MixFloat(nation.LivingOffset);
            MixText(nation.StartGovernment); MixText(nation.StartIdeology);
            Mix(nation.Movement is null ? 0 : 1);
            if (nation.Movement is { } movement)
            {
                MixFloat(movement.Militancy);
                if (movement.GainedTraits.Count + movement.LostTraits.Count > 0)
                {
                    MixText("traits_became");
                    foreach (var trait in movement.GainedTraits.OrderBy(t => t, StringComparer.Ordinal)) MixText("+" + trait);
                    foreach (var trait in movement.LostTraits.OrderBy(t => t, StringComparer.Ordinal)) MixText("-" + trait);
                }
                // Panel history is not authority. These inputs and cooldowns are: they
                // can change the next strike/march before any headline total changes.
                // Keep empty legacy state at its old hash, and exclude LastReport,
                // whose translated text differs legitimately between participants.
                if (!string.IsNullOrEmpty(movement.Id) || movement.Dissolved || !movement.Landless ||
                    movement.Cadres != 0f || movement.Legitimacy != 0f || movement.IncomeWeekly != 0f ||
                    movement.Support.Count > 0 || movement.RecognisedBy.Count > 0 || movement.ReadyOnDay.Count > 0 ||
                    movement.MovingToStateMapIndex != -1 || movement.ArrivesOnDay != -1 || movement.Exile ||
                    movement.Peoples.Count > 0 || movement.Homeland.Count > 0)
                {
                    MixText("movement_command_state"); MixText(movement.Id);
                    Mix(movement.Dissolved ? 1 : 0); Mix(movement.Landless ? 1 : 0);
                    MixFloat(movement.Cadres); MixFloat(movement.Legitimacy); MixFloat(movement.IncomeWeekly);
                    Mix(movement.Support.Count);
                    foreach (var support in movement.Support.OrderBy(p => p.Key)) { Mix(support.Key); MixFloat(support.Value); }
                    Mix(movement.RecognisedBy.Count);
                    foreach (var country in movement.RecognisedBy.OrderBy(id => id)) Mix(country);
                    Mix(movement.ReadyOnDay.Count);
                    foreach (var clock in movement.ReadyOnDay.OrderBy(p => p.Key, StringComparer.Ordinal))
                    { MixText(clock.Key); Mix(clock.Value); }
                    Mix(movement.MovingToStateMapIndex); Mix(movement.ArrivesOnDay); Mix(movement.Exile ? 1 : 0);
                    // Identity lists remain ordered, just as the exile definition reads
                    // them. Only unordered maps and recognition sets are canonicalized.
                    Mix(movement.Peoples.Count); foreach (var people in movement.Peoples) MixText(people);
                    Mix(movement.Homeland.Count); foreach (var region in movement.Homeland) MixText(region);
                }
            }
            Mix(nation.ProductionReferenceInitialized ? 1 : 0);
            MixFloat(nation.ProductionReferenceGdp); Mix(nation.ProductionReferencePopulation);
            Mix(nation.LastCivilWarDay);
            MixFloat(nation.Stability);
            // What the country is beyond its conditions, and how it has been governed. Both
            // feed stability, and a world that disagrees about them is a world whose
            // stability is about to disagree too.
            MixFloat(nation.StabilityBias);
            MixFloat(nation.StabilityGovernance);
            if (nation.TroublesDays > 0) { MixText("troubles"); Mix(nation.TroublesDays); }
            MixFloat(nation.WarSupport);
            MixFloat(nation.ManpowerReserve);
            // When it last came apart, which is what decides whether it may again.
            Mix(nation.LastSecessionDay);
            // How far its households' appetite has grown with their income; a country the
            // market has not measured yet carries no bytes.
            if (nation.IncomeBaseline != 0f || nation.IncomeFactor != 1f)
            { MixText("income"); MixFloat(nation.IncomeBaseline); MixFloat(nation.IncomeFactor); }
            if (nation.CapitalIntensityReference != 0f) { MixText("capital"); MixFloat(nation.CapitalIntensityReference); }
            if (nation.RecessionDepth != 0f || nation.RecessionUntilDay != 0)
            { MixText("recession"); MixFloat(nation.RecessionDepth); Mix(nation.RecessionStartDay); Mix(nation.RecessionUntilDay); }
            Mix(nation.LastCivilWarDay);
            // A collapse under way and the dates of the last collapse and revolution: they decide
            // the next wave and the next outbreak before any border moves (StateFailure). A
            // country that never failed emits nothing, keeping untouched worlds' fingerprints.
            if (nation.CollapseStartDay != -1 || nation.CollapseWaves != 0 || nation.CollapseStartPopulation != 0 ||
                nation.LastCollapseDay != -99999 || nation.LastRevolutionDay != -99999)
            {
                MixText("state_failure"); Mix(nation.CollapseStartDay); Mix(nation.CollapseWaves);
                Mix(nation.CollapseStartPopulation);
                Mix(nation.LastCollapseDay); Mix(nation.LastRevolutionDay);
            }
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
            // These memories affect the next intelligence, membership, union or promise
            // review before any visible total changes. Empty/default state emits no new
            // bytes, keeping pre-feature fingerprints compatible with untouched worlds.
            MixFloatDictionary("intel_networks", nation.IntelNetworks);
            MixFloatDictionary("organization_belonging_at_start", nation.OrganizationBelongingAtStart);
            if (nation.OrganizationConcessions.Count > 0)
            {
                MixText("organization_concessions");
                foreach (var pair in nation.OrganizationConcessions.OrderBy(p => p.Key)) { Mix(pair.Key); Mix(pair.Value); }
            }
            if (nation.CovertOperations.Count > 0)
            {
                MixText("covert_operations"); Mix(nation.CovertOperations.Count);
                foreach (var operation in nation.CovertOperations)
                {
                    MixText(operation.Kind); Mix(operation.TargetId); Mix(operation.RegionMapIndex);
                    Mix(operation.StartedDay); Mix(operation.DueDay); MixFloat(operation.Spent);
                }
            }
            if (nation.CovertQuietUntil.Count > 0)
            {
                MixText("covert_quiet"); Mix(nation.CovertQuietUntil.Count);
                foreach (var quiet in nation.CovertQuietUntil.OrderBy(p => p.Key)) { Mix(quiet.Key); Mix(quiet.Value); }
            }
            if (!string.IsNullOrEmpty(nation.ConsolidationProject) || nation.ConsolidationFocusId != -1 ||
                nation.ConsolidationAmbition != 0f)
            {
                MixText("consolidation"); MixText(nation.ConsolidationProject);
                Mix(nation.ConsolidationFocusId); MixFloat(nation.ConsolidationAmbition);
            }
            if (nation.LastAddressDay != -9999 || nation.AddressWeariness != 0f ||
                !string.IsNullOrEmpty(nation.AddressPromise) || nation.AddressPromiseDueDay != -1 ||
                nation.AddressPromiseMark != 0f)
            {
                MixText("address_to_nation"); Mix(nation.LastAddressDay); MixFloat(nation.AddressWeariness);
                MixText(nation.AddressPromise); Mix(nation.AddressPromiseDueDay); MixFloat(nation.AddressPromiseMark);
            }
            if (nation.WeeklyNetMigration != 0 || nation.CumulativeNetMigration != 0 ||
                nation.BrainDrainRate != 0f || nation.BrainDrainDestinationId != -1 ||
                nation.BrainGainSourceId != -1)
            {
                MixText("brain_drain"); Mix(nation.WeeklyNetMigration); Mix(nation.CumulativeNetMigration);
                MixFloat(nation.BrainDrainRate); Mix(nation.BrainDrainDestinationId); Mix(nation.BrainGainSourceId);
            }
            // The army's books of war, and what buys their articles. One book per shelf, so a
            // country reading an air and a naval book has both in the hash. A country with no
            // doctrine has written nothing yet, so an untouched legacy world keeps its old hash.
            var legacyDoctrine = nation.TacticalDoctrine;
            var legacyCopied = legacyDoctrine.Branch.Length > 0 && legacyDoctrine.Tracks.Values.Any(track =>
                string.Equals(track.Branch, legacyDoctrine.Branch, StringComparison.Ordinal) &&
                track.Known.Count == legacyDoctrine.Known.Count);
            if (!legacyCopied && (legacyDoctrine.Branch.Length > 0 || legacyDoctrine.Known.Count > 0 ||
                                  legacyDoctrine.LearningNode.Length > 0))
            {
                MixText("tactical_doctrine_legacy"); MixText(legacyDoctrine.Branch); Mix(legacyDoctrine.Known.Count);
                foreach (var node in legacyDoctrine.Known.OrderBy(id => id, StringComparer.Ordinal)) MixText(node);
                MixText(legacyDoctrine.LearningNode); Mix(legacyDoctrine.LearningDoneDay);
            }
            if (nation.TacticalDoctrine.Tracks.Count > 0)
            {
                foreach (var slot in DoctrineSlots.All)
                {
                    var track = nation.TacticalDoctrine.Tracks.GetValueOrDefault(slot);
                    if (track is null || (track.Branch.Length == 0 && track.Known.Count == 0 &&
                                          track.LearningNode.Length == 0)) continue;
                    MixText("tactical_doctrines"); MixText(slot); MixText(track.Branch); Mix(track.Known.Count);
                    foreach (var node in track.Known.OrderBy(id => id, StringComparer.Ordinal)) MixText(node);
                    MixText(track.LearningNode); Mix(track.LearningDoneDay);
                    var companion = track.Companion;
                    if (companion is null || (companion.Branch.Length == 0 && companion.Known.Count == 0 &&
                                              companion.LearningNode.Length == 0)) continue;
                    MixText("tactical_doctrine_second"); MixText(slot); MixText(companion.Branch);
                    Mix(companion.Known.Count);
                    foreach (var node in companion.Known.OrderBy(id => id, StringComparer.Ordinal)) MixText(node);
                    MixText(companion.LearningNode); Mix(companion.LearningDoneDay);
                }
            }
            // The grand strategy in force, and when the government last changed its mind.
            // A country that has chosen none keeps the hash it always had.
            if (nation.StrategicDoctrine.Id.Length > 0 || nation.StrategicDoctrine.LastChangeDay > -9999)
            {
                MixText("strategic_doctrine"); MixText(nation.StrategicDoctrine.Id);
                Mix(nation.StrategicDoctrine.LastChangeDay);
            }
            // Practice on a technology the country already has. An idle country, and an
            // old save, store nothing here, so an untouched world keeps the hash it had.
            if (nation.TechnologyFamiliarity.Count > 0)
            {
                var wroteFamiliarity = false;
                foreach (var entry in nation.TechnologyFamiliarity.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!(entry.Value > 0f) || !float.IsFinite(entry.Value)) continue;
                    if (!wroteFamiliarity) { MixText("technology_familiarity"); wroteFamiliarity = true; }
                    MixText(entry.Key);
                    MixFloat(entry.Value);
                }
            }
            if (nation.ArmyExperience != 0f || nation.AirExperience != 0f ||
                nation.NavalExperience != 0f || nation.PoliticalPoints != 0f)
            {
                MixText("military_experience");
                MixFloat(nation.ArmyExperience); MixFloat(nation.AirExperience);
                MixFloat(nation.NavalExperience); MixFloat(nation.PoliticalPoints);
            }
            if (MathF.Abs(nation.ImperialOverstretch) > 0.0001f)
            {
                MixText("imperial_overstretch");
                MixFloat(nation.ImperialOverstretch);
            }
            // How many weeks in a row this country has been counted as dying, and whether
            // the world has already said so out loud. Two peers that disagree about either
            // will announce the same death on different weeks, and relinquish its ground a
            // week apart. A country that is not dying emits no bytes, so untouched worlds
            // keep the fingerprint they have always had.
            if (nation.CollapseReviews != 0 || nation.CollapseAnnounced)
            {
                MixText("demographic_collapse");
                Mix(nation.CollapseReviews); Mix(nation.CollapseAnnounced ? 1 : 0);
            }
            if (nation.HasCustomSubjectState)
            {
                MixText("subject_interactions");
                Mix(nation.SubjectIntegrating ? 1 : 0);
                MixFloat(nation.SubjectIntegrationProgress);
                MixFloat(nation.SubjectLibertyModifier);
                Mix(nation.LastSubjectInteractionDay);
                Mix(nation.SubjectTributeHolidayUntilDay);
                if (nation.SubjectConspiring || nation.SubjectForeignBackers.Count > 0 || nation.LastForeignAgitationDay != -9999)
                {
                    Mix(nation.SubjectConspiring ? 1 : 0);
                    Mix(nation.LastForeignAgitationDay);
                    Mix(nation.SubjectForeignBackers.Count);
                    foreach (var backerId in nation.SubjectForeignBackers.OrderBy(id => id)) Mix(backerId);
                }
            }
            if (!string.IsNullOrEmpty(nation.CustomName) || !string.IsNullOrEmpty(nation.CustomFlag))
            {
                MixText("national_cosmetics");
                MixText(nation.CustomName);
                MixText(nation.CustomFlag);
            }
            if (!string.IsNullOrEmpty(nation.FoundNameKind) || nation.NameSettledDay >= 0 || nation.ReleasedFromId >= 0)
            {
                MixText("founded_name");
                MixText(nation.FoundNameKind); MixText(nation.FoundNameId);
                MixText(nation.FoundNameForm); MixText(nation.FoundNameBase);
                MixText(nation.NameConvention); Mix(nation.NameSettledDay); Mix(nation.ReleasedFromId);
            }
            // Who a country was born out of decides what a later war is called and what a peace may
            // take, so a peer that has it and one that does not are not the same world. Guarded at
            // its default: a country that was always here mixes nothing, and the goldens recorded
            // before this field existed still hold.
            if (nation.RevoltParentId >= 0)
            {
                MixText("revolt_parent");
                Mix(nation.RevoltParentId);
            }
            if (nation.HasCustomLeadership)
            {
                MixText("ruler_cabinet");
                MixText(nation.HeadOfStateName); MixText(nation.HeadOfStateTrait);
                Mix(nation.HeadOfStateBirthYear); Mix(nation.HeadOfStateSinceDay);
                MixText(nation.HeadOfGovernmentName); MixText(nation.HeadOfGovernmentTrait);
                Mix(nation.HeadOfGovernmentBirthYear); Mix(nation.HeadOfGovernmentSinceDay);
                Mix(nation.NextElectionDay); Mix(nation.LastCabinetChangeDay); Mix(nation.RulerGeneration);
                Mix(nation.Cabinet.Count);
                foreach (var entry in nation.Cabinet.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                { MixText(entry.Key.ToLowerInvariant()); MixText(entry.Value); }
            }
            if (nation.HasCustomStockpiles)
            {
                MixText("strategic_stockpiles");
                Mix(nation.StockpilesSeeded ? 1 : 0);
                Mix(nation.Stockpiles.Count);
                foreach (var entry in nation.Stockpiles.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                { MixText(entry.Key.ToLowerInvariant()); MixFloat(entry.Value); }
            }
            if (nation.StockpileCoverage.Count > 0)
            {
                MixText("stockpile_coverage");
                foreach (var entry in nation.StockpileCoverage.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                { MixText(entry.Key.ToLowerInvariant()); MixFloat(entry.Value); }
            }
            if (nation.Elections is { } elections)
            {
                MixText("elections_v1"); MixText(elections.Government); MixFloat(elections.Control);
                Mix(elections.Round); Mix(elections.IncumbentTerms); MixText(elections.IncumbentName);
                Mix(elections.LastDay); MixFloat(elections.IncumbentVote); MixText(elections.Result);
                Mix(elections.Suspended ? 1 : 0); Mix(elections.LastPostponedRound);
                Mix(elections.TransferOnDay); MixText(elections.Winner); MixText(elections.WinnerTrait);
                Mix(elections.WinnerBirthYear); Mix(elections.DisputedUntilDay);
                Mix(elections.LastControlReviewDay);
            }
            if (nation.CabinetPeople.Count > 0)
            {
                MixText("minister_people_v1"); Mix(nation.CabinetPeople.Count);
                foreach (var entry in nation.CabinetPeople.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    MixText(entry.Key.ToLowerInvariant()); MixText(entry.Value?.Name);
                    Mix(entry.Value?.BirthYear ?? 0); MixText(entry.Value?.TraitId);
                    Mix(entry.Value?.AppointedDay ?? 0);
                }
            }
            if (nation.PersonPortraits.Count > 0)
            {
                MixText("person_portraits_v1"); Mix(nation.PersonPortraits.Count);
                foreach (var entry in nation.PersonPortraits.OrderBy(p => p.Key, StringComparer.Ordinal))
                { MixText(entry.Key); MixText(entry.Value); }
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
            if (state.ReviewNetMigration != 0 || state.BrainDrainPressure != 0f)
            {
                MixText("state_migration"); Mix(state.ReviewNetMigration); MixFloat(state.BrainDrainPressure);
            }
            // Who administers it although another owns it, and since when (HeldRegions).
            if (state.HeldById != -1 || state.HeldSinceDay != 0) { MixText("held"); Mix(state.HeldById); Mix(state.HeldSinceDay); }
            // Somebody else's offices in this region (ParallelAdministration): who runs them,
            // since when, whether the host has noticed, and what they have taken. Guarded at
            // their default so a world without them hashes exactly as it always did.
            if (state.AdministeredById >= 0)
            {
                MixText("parallel_administration");
                Mix(state.AdministeredById); Mix(state.AdministeredSinceDay);
                Mix(state.AdministrationExposed ? 1 : 0); MixFloat(state.AdministrationTaken);
            }
            foreach (var group in state.PopulationGroups)
            {
                MixText(group.Culture); MixText(group.Religion); MixText(group.Profession); Mix((int)group.Class); Mix((int)group.Sector);
                Mix(group.Size); MixFloat(group.Employment); MixFloat(group.Wealth); MixFloat(group.StandardOfLiving); MixFloat(group.Radicalism);
            }
            Mix(-20);
            MixFloat(state.SecessionPressure);
            if (state.UrbanBaseline != 0) { MixText("urban_baseline"); Mix(state.UrbanBaseline); }
            MixFloat(state.ResidentAwareness);
            if (state.PoliticalDivergence >= 0f || state.LawApproval >= 0f || state.GovernmentApproval >= 0f)
            {
                MixText("political_cohort");
                MixFloat(state.PoliticalDivergence); MixFloat(state.LawApproval); MixFloat(state.GovernmentApproval);
            }
            Mix(state.RememberedOwner); Mix(state.SettledBorderDays); Mix(state.LastBorderReviewDay);
            MixFloat(state.BorderHabit);
            // The age structure is authoritative, not decoration: the young share sets how many of
            // the country's own children are born, the old share how many of them die, and both
            // decide who arrives and which culture a region ends up counted under (DemographicCycle).
            // Mixed only when it differs from the default, so a save written before the structure
            // existed hashes byte for byte as it did.
            if (state.YoungShare != 0.30f || state.OldShare != 0.09f)
            {
                MixText("demographic_cycle");
                MixFloat(state.YoungShare); MixFloat(state.OldShare);
            }
            foreach (var tag in state.CoreTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) MixText(tag);
            Mix(-18);
            foreach (var tag in state.Claims.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) MixText(tag);
            Mix(-19);
        }
        foreach (var line in Ordered(campaign.FrontLines, line => line.Id))
        {
            // The whole front against every enemy (DrawnFrontLine.AllEnemies). Mixed only when
            // set, so a campaign without one hashes exactly as it did before the flag existed.
            if (line.AllEnemies) { MixText("whole_front"); Mix(line.Id); Mix(line.NationId); }
            if (line.Kind != FrontLineKind.Offensive || line.OffensivePhases.Count <= 1 &&
                line.ActiveOffensivePhase == 0 && line.OffensiveRegroupDay < 0) continue;
            MixText("offensive_phases"); Mix(line.Id); Mix(line.OffensivePhases.Count);
            Mix(line.ActiveOffensivePhase); Mix(line.OffensiveRegroupDay);
            foreach (var phase in line.OffensivePhases)
            {
                Mix(phase.ObjectiveSequence.Count);
                foreach (var province in phase.ObjectiveSequence) Mix(province);
            }
            foreach (var assignment in line.Objectives.OrderBy(pair => pair.Key)) { Mix(assignment.Key); Mix(assignment.Value); }
        }
        // The army groups and their commanders (Generals): who leads, how well, and their record.
        foreach (var group in Ordered(campaign.ArmyGroups, group => group.Id))
        {
            MixText("army_group"); Mix(group.Id); Mix(group.NationId); MixText(group.Commander);
            MixFloat(group.StaffSkill); MixText(group.CommanderTrait); Mix(group.Victories);
            Mix(group.Defeats); Mix(group.CommanderSinceDay); Mix(group.CommandersLost);
            if (group.CommandersRelieved != 0) { MixText("relieved"); Mix(group.CommandersRelieved); }
        }
        foreach (var army in Ordered(campaign.Armies, army => army.Id))
        {
            Mix(army.Id);
            Mix(army.ProvinceId);
            Mix(army.Personnel);
            MixFloat(army.Organisation);
            MixFloat(army.Strength);
            if (army.Equipment != .88f)
            { MixText("formation_equipment"); MixFloat(army.Equipment); }
            MixFloat(army.Entrenchment);
            Mix(army.Route.Count);
            if (army.PostBattleWaypoints.Count > 0)
            {
                MixText("post_battle_orders");
                Mix(army.PostBattleSupportAttack ? 1 : 0);
                Mix(army.PostBattleWaypoints.Count);
                foreach (var destination in army.PostBattleWaypoints) Mix(destination);
            }
            if (army.InBattle || army.ReadyDay > 0)
            { MixText("battle_assignment"); Mix(army.BattleId); Mix(army.ReadyDay); }
            if (army.ContractUntilDay >= 0)
            { MixText("contract_until"); Mix(army.ContractUntilDay); }
            // A drilling formation fights differently from a ready one the day it is called.
            if (army.Training) { MixText("training"); Mix(army.Id); }
            // A posture changes what the formation does in the next battle, so two machines
            // that disagree about it are two machines playing different wars. Balanced is the
            // default and emits nothing, so a world that never gave an order keeps its hash.
            if (army.Stance != ArmyStance.Balanced)
            {
                MixText("formation_stance"); Mix(army.Id); Mix((int)army.Stance);
            }
            if (army.StaffNote != StaffNote.None)
            {
                MixText("staff_note"); Mix(army.Id); Mix((int)army.StaffNote);
            }
        }
        // A withdrawal can leave the province, headcount and battle losses unchanged,
        // but change who may fight next hour. Empty legacy battles emit no new bytes.
        foreach (var battle in Ordered(campaign.Battles, b => b.Id))
        {
            if (battle.WithdrawnAttackers.Count == 0 && battle.WithdrawnDefenders.Count == 0) continue;
            MixText("battle_withdrawal"); Mix(battle.Id); Mix(battle.Ended ? 1 : 0);
            Mix(battle.Attackers.Count); foreach (var id in battle.Attackers.OrderBy(id => id)) Mix(id);
            Mix(battle.Defenders.Count); foreach (var id in battle.Defenders.OrderBy(id => id)) Mix(id);
            Mix(battle.WithdrawnAttackers.Count); foreach (var id in battle.WithdrawnAttackers.OrderBy(id => id)) Mix(id);
            Mix(battle.WithdrawnDefenders.Count); foreach (var id in battle.WithdrawnDefenders.OrderBy(id => id)) Mix(id);
        }
        if (campaign.Battles.Any(b => b.WithdrawnAttackers.Count > 0 || b.WithdrawnDefenders.Count > 0))
        {
            foreach (var army in Ordered(campaign.Armies, a => a.Id))
            {
                MixText("withdrawal_formation"); Mix(army.Id); MixFloat(army.Divisions); Mix(army.MaxPersonnel);
                MixFloat(army.Recovering); MixFloat(army.Equipment); Mix(army.BattleId); Mix(army.ReadyDay);
                Mix(army.AssignedWarId); Mix(army.FrontLineId); Mix((int)army.OrderKind);
                Mix(army.DirectOrder ? 1 : 0); Mix(army.OrderProvinceId);
                foreach (var step in army.Route) Mix(step); Mix(-35);
            }
        }
        foreach (var army in Ordered(campaign.Armies, a => a.Id))
        {
            if (army.CutOff) { MixText("cut_off"); Mix(army.Id); }
            if (army.DaysCutOff > 0) { MixText("days_cut_off"); Mix(army.Id); Mix(army.DaysCutOff); }
            if (army.BreakingOut) { MixText("breaking_out"); Mix(army.Id); }
            // Pockets and their stores (StrandedGarrisons): each only off its default, so a world
            // without a pocket fingerprints as it did before these existed.
            if (army.PocketStores >= 0f) { MixText("pocket_stores"); Mix(army.Id); MixFloat(army.PocketStores); Mix(army.PocketSinceDay); }
            if (army.HoldoutSinceDay >= 0) { MixText("holdout"); Mix(army.Id); Mix(army.HoldoutSinceDay); }
            if (army.ReliefDistance != -1) { MixText("relief_distance"); Mix(army.Id); Mix(army.ReliefDistance); }
        }
        foreach (var item in campaign.PendingEvents.OrderBy(e => e.Id))
            if (item.Options.Any(o => o.SecessionTargetId >= 0))
            {
                MixText("secession_response_event"); Mix(item.Id); Mix(item.NationId); Mix(item.CreatedDay); Mix(item.ExpiresDay);
                foreach (var option in item.Options) { Mix(option.SecessionTargetId); MixText(option.SecessionChoice); }
            }
        foreach (var war in Ordered(campaign.Wars, war => war.Id))
        {
            Mix(war.Id);
            Mix(war.Ended ? 1 : 0);
            MixFloat(war.OccupationScore);
            MixFloat(war.BattleScore);
            Mix(war.CivilConflict ? 1 : 0); Mix(war.EscalationStage); Mix(war.AiNegotiationUnlockDay);
            if (war.WorldWarDay >= 0)
            { MixText("world_war"); Mix(war.WorldWarDay); }
            if (war.PendingOffer is { BeneficiaryId: >= 0 } offer)
            { MixText("peace_beneficiary"); Mix(offer.BeneficiaryId); }
            // The spacing of settlements put to a player (PeaceOffers.ToPlayerBlockReason).
            // Only a war a player is in ever sets these, so a world nobody plays mixes nothing.
            if (war.LastOfferToPlayerDay >= 0 || war.RefusedOfferDay >= 0)
            {
                MixText("peace_offer_to_player"); Mix(war.LastOfferToPlayerDay); Mix(war.RefusedOfferDay);
                Mix(war.RefusedOfferWasWritten ? 1 : 0); Mix((int)war.RefusedOfferTerms);
                if (war.RefusedOffer is { } refused)
                {
                    Mix(refused.Provinces.Count);
                    foreach (var province in refused.Provinces.OrderBy(id => id)) Mix(province);
                }
            }
            // Coalition membership changes combat and access before any province moves.
            if (war.SeparatePeaces.Count > 0)
            {
                MixText("separate_peaces");
                foreach (var left in war.SeparatePeaces)
                {
                    Mix(left.NationId); Mix(left.WithId); Mix(left.Day); Mix((int)left.Terms);
                    Mix(left.SwitchedSides ? 1 : 0); MixFloat(left.Payment); Mix(left.ProvincesCeded);
                }
            }
            // Keep the historical bilateral golden when there are no allied members.
            if (war.AttackerAllies.Count > 0 || war.DefenderAllies.Count > 0)
            {
                MixText("war_coalition"); Mix(war.AttackerId); Mix(war.DefenderId);
                Mix(war.AttackerAllies.Count);
                foreach (var member in war.AttackerAllies.OrderBy(id => id)) Mix(member);
                Mix(war.DefenderAllies.Count);
                foreach (var member in war.DefenderAllies.OrderBy(id => id)) Mix(member);
            }
        }
        // Previous-market attribution is input to supply diplomacy between weekly
        // settlements. An unknown legacy week adds no bytes; a known (even empty)
        // week and its original summation order are authoritative campaign state.
        if (campaign.TradeRoutesDay >= 0 || campaign.TradeRoutes.Count > 0)
        {
            MixText("trade_attribution_v1"); Mix(campaign.TradeRoutesDay);
            Mix(campaign.TradeRoutes.Count);
            foreach (var route in campaign.TradeRoutes)
            {
                Mix(route.SellerId); Mix(route.BuyerId); Mix((int)route.Commodity);
                MixFloat(route.Volume); MixFloat(route.Value);
            }
        }
        // The deep-strike war (LongRangeStrikes): its rules when they differ from the shipped
        // ones, and each formation's chosen target when one was chosen. A world that never
        // names a target and keeps the defaults mixes nothing here.
        var strikeRules = LongRangeStrikes.Rules(campaign.Defines);
        if (!strikeRules.SequenceEqual(LongRangeStrikes.DefaultRules))
        {
            MixText("long_range_strike_rules");
            foreach (var rule in strikeRules) MixFloat(rule);
        }
        foreach (var formation in campaign.SupportFormations
                     .Where(f => !string.IsNullOrEmpty(f.StrikeTarget) || f.TargetStateMapIndex >= 0).OrderBy(f => f.Id))
        {
            MixText("strike_target"); Mix(formation.Id); MixText(formation.StrikeTarget); Mix(formation.TargetStateMapIndex);
        }
        var controllers = campaign.ProvinceController ?? campaign.PendingProvinceController;
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
