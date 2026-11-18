using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class CampaignFingerprint
{
    private static void MixImage(ref ulong hash, PublicImage image)
    {
        Mix(ref hash, image is null ? 0 : 1);
        if (image is null) return;
        MixFloat(ref hash, image.Esteem); MixFloat(ref hash, image.Incident); MixFloat(ref hash, image.Familiarity);
        MixFloat(ref hash, image.Entrenchment); MixFloat(ref hash, image.Target); Mix(ref hash, image.LastReviewDay);
        Mix(ref hash, image.ConsistentDays); Mix(ref hash, image.Status); Mix(ref hash, image.LastIncidentDay);
        MixText(ref hash, image.LastReason);
    }

    private static void MixDiplomacyRules(Campaign campaign, ref ulong hash)
    {
        var diplomacyRules = campaign.Defines;
        if (!diplomacyRules.AiFormableProclamations || diplomacyRules.AiFormableReviewDays != 30 ||
            diplomacyRules.AiFormableEarliestDay != 120 || diplomacyRules.AiFormableMinimumIntervalDays != 1825 ||
            diplomacyRules.AiFormableCrisisRecoveryDays != 180 || diplomacyRules.AiFormableTreasuryReserveShare != .02f ||
            diplomacyRules.AiFormableMinimumScore != .58f)
        {
            MixText(ref hash, "formable_proclamation_ai_rules"); Mix(ref hash, diplomacyRules.AiFormableProclamations ? 1 : 0);
            Mix(ref hash, diplomacyRules.AiFormableReviewDays); Mix(ref hash, diplomacyRules.AiFormableEarliestDay);
            Mix(ref hash, diplomacyRules.AiFormableMinimumIntervalDays); Mix(ref hash, diplomacyRules.AiFormableCrisisRecoveryDays);
            MixFloat(ref hash, diplomacyRules.AiFormableTreasuryReserveShare); MixFloat(ref hash, diplomacyRules.AiFormableMinimumScore);
        }
        if (!diplomacyRules.PeaceAlliedBenefits) { MixText(ref hash, "peace_allied_benefits"); Mix(ref hash, 0); }
        if (!diplomacyRules.OrganicOrganizationReachEnabled || diplomacyRules.AiOrganizationExpansionLimit != 64 ||
            diplomacyRules.AiOrganizationGlobalGdp != 1000 || diplomacyRules.AiOrganizationFoundingPoliticalCost != 30 ||
            diplomacyRules.OrganizationLeaderRiftPenalty != .24f)
        {
            MixText(ref hash, "organization_reach_rules"); Mix(ref hash, diplomacyRules.OrganicOrganizationReachEnabled ? 1 : 0);
            Mix(ref hash, diplomacyRules.AiOrganizationExpansionLimit); MixFloat(ref hash, diplomacyRules.AiOrganizationGlobalGdp);
            MixFloat(ref hash, diplomacyRules.AiOrganizationFoundingPoliticalCost); MixFloat(ref hash, diplomacyRules.OrganizationLeaderRiftPenalty);
        }
        if (!diplomacyRules.DiplomaticMissionsEnabled || diplomacyRules.DiplomaticMissionPoliticalCost != 8 ||
            diplomacyRules.DiplomaticMissionWeeklyPoliticalCost != 1.5f || diplomacyRules.DiplomaticMissionStartCash != .1f ||
            diplomacyRules.DiplomaticMissionWeeklyCash != .01f || diplomacyRules.DiplomaticMissionDays != 180 ||
            diplomacyRules.DiplomaticMissionLimit != 3 || diplomacyRules.DiplomaticMissionWeeklyOpinion != 1.2f ||
            diplomacyRules.DiplomaticMissionOpinionCap != 24)
        {
            MixText(ref hash, "diplomatic_mission_rules"); Mix(ref hash, diplomacyRules.DiplomaticMissionsEnabled ? 1 : 0);
            MixFloat(ref hash, diplomacyRules.DiplomaticMissionPoliticalCost); MixFloat(ref hash, diplomacyRules.DiplomaticMissionWeeklyPoliticalCost);
            MixFloat(ref hash, diplomacyRules.DiplomaticMissionStartCash); MixFloat(ref hash, diplomacyRules.DiplomaticMissionWeeklyCash);
            Mix(ref hash, diplomacyRules.DiplomaticMissionDays); Mix(ref hash, diplomacyRules.DiplomaticMissionLimit);
            MixFloat(ref hash, diplomacyRules.DiplomaticMissionWeeklyOpinion); MixFloat(ref hash, diplomacyRules.DiplomaticMissionOpinionCap);
        }
    }

    private static void MixUltimatums(Campaign campaign, ref ulong hash)
    {
        if (campaign.UltimatumPapers.Count == 0 && campaign.NextUltimatumId == 1) return;
        MixText(ref hash, "ultimatum_desk"); Mix(ref hash, campaign.NextUltimatumId); Mix(ref hash, campaign.UltimatumPapers.Count);
        foreach (var demand in campaign.UltimatumPapers.OrderBy(paper => paper.Id))
        {
            Mix(ref hash, demand.Id); Mix(ref hash, demand.FromId); Mix(ref hash, demand.ToId);
            Mix(ref hash, demand.CreatedDay); Mix(ref hash, demand.ExpiresDay);
            Mix(ref hash, demand.ClosedDay); Mix(ref hash, (int)demand.State); MixFloat(ref hash, demand.Reparations);
            Mix(ref hash, demand.LiftEmbargo ? 1 : 0); Mix(ref hash, demand.NonAggression ? 1 : 0);
            if (demand.Annex || demand.Vassalise || demand.Disarm || demand.BreakAlliances || demand.ReleaseSubjects ||
                demand.TechnologyTransfer || demand.ChangeGovernment || demand.FleetTransfer)
            {
                MixText(ref hash, "extended_ultimatum");
                Mix(ref hash, demand.Annex ? 1 : 0); Mix(ref hash, demand.Vassalise ? 1 : 0);
                Mix(ref hash, demand.Disarm ? 1 : 0); Mix(ref hash, demand.BreakAlliances ? 1 : 0);
                Mix(ref hash, demand.ReleaseSubjects ? 1 : 0); Mix(ref hash, demand.TechnologyTransfer ? 1 : 0);
                Mix(ref hash, demand.ChangeGovernment ? 1 : 0); Mix(ref hash, demand.FleetTransfer ? 1 : 0);
            }
            foreach (var region in demand.Regions ?? Array.Empty<int>()) Mix(ref hash, region);
            if (!string.IsNullOrEmpty(demand.SubjectKind)) { MixText(ref hash, "ult_kind"); MixText(ref hash, demand.SubjectKind); }
            MixUltimatumSettlement(demand, ref hash);
            Mix(ref hash, -104);
        }
    }

    private static void MixUltimatumSettlement(UltimatumPaper demand, ref ulong hash)
    {
        if (!demand.TradeAgreement && !demand.EmbargoThird && !demand.BreakNamed && !demand.CancelGuarantee &&
            !demand.JoinAlliance && !demand.AcceptGuarantee && !demand.MilitaryAccess && !demand.WhitePeace &&
            !demand.CedeOccupied && !demand.Humiliate && !demand.ReleaseNamed && !demand.Liberate) return;
        MixText(ref hash, "ultimatum_settlement");
        if (demand.TradeAgreement) Mix(ref hash, 1);
        if (demand.EmbargoThird) { MixText(ref hash, "embargo"); Mix(ref hash, demand.EmbargoOnId); }
        if (demand.BreakNamed) { MixText(ref hash, "break"); Mix(ref hash, demand.BreakWithId); }
        if (demand.CancelGuarantee) { MixText(ref hash, "guarantee"); Mix(ref hash, demand.CancelGuaranteeId); }
        if (demand.JoinAlliance) MixText(ref hash, "join");
        if (demand.AcceptGuarantee) MixText(ref hash, "accept");
        if (demand.MilitaryAccess) MixText(ref hash, "access");
        if (demand.WhitePeace) MixText(ref hash, "white");
        if (demand.CedeOccupied) MixText(ref hash, "status");
        if (demand.Humiliate) MixText(ref hash, "humiliate");
        if (demand.ReleaseNamed) { MixText(ref hash, "release"); Mix(ref hash, demand.ReleaseSubjectId); }
        if (demand.Liberate) { MixText(ref hash, "liberate"); Mix(ref hash, demand.LiberateRegion); }
    }

    private static void MixRelationsAndDiplomacyTables(Campaign campaign, ref ulong hash)
    {
        // Waking and sleeping alike (RelationDormancy): which table a relation is in is told below.
        foreach (var pair in campaign.Relations.Concat(campaign.DormantRelations).OrderBy(p => p.Key))
        {
            Mix(ref hash, pair.Key);
            var r = pair.Value;
            if (r.MilitaryAccessByA || r.MilitaryAccessByB || r.MilitaryAccessRequestedBy >= 0 || r.MilitaryAccessRequestedDay != -9999)
            {
                MixText(ref hash, "military_access_v1"); Mix(ref hash, r.MilitaryAccessByA ? 1 : 0);
                Mix(ref hash, r.MilitaryAccessByB ? 1 : 0); Mix(ref hash, r.MilitaryAccessRequestedBy); Mix(ref hash, r.MilitaryAccessRequestedDay);
            }
            Mix(ref hash, (int)r.Stance); MixFloat(ref hash, r.Opinion); MixFloat(ref hash, r.AllianceCohesion);
            Mix(ref hash, r.AidSentDay);
            if (r.GuaranteeRequestedBy >= 0)
            { MixText(ref hash, "guarantee_request_v1"); Mix(ref hash, r.GuaranteeRequestedBy); Mix(ref hash, r.GuaranteeRequestedDay); }
            if (r.AgreementOfferedBy >= 0)
            { MixText(ref hash, "agreement_offer_v1"); Mix(ref hash, r.AgreementOfferedBy); Mix(ref hash, (int)r.AgreementOfferedKind); Mix(ref hash, r.AgreementOfferedDay); }
            if (r.Trade is not null) { MixText(ref hash, "trade_terms_v1"); MixTradeTerms(ref hash, r.Trade); }
            if (r.AgreementOfferedTrade is not null) { MixText(ref hash, "offered_trade_terms_v1"); MixTradeTerms(ref hash, r.AgreementOfferedTrade); }
            if (r.AgreementLapsedBy >= 0)
            { MixText(ref hash, "agreement_lapse_v1"); Mix(ref hash, r.AgreementLapsedBy); Mix(ref hash, r.AgreementLapsedDay); }
            if (r.DiplomaticActBy >= 0)
            { MixText(ref hash, "diplomatic_act_v1"); Mix(ref hash, r.DiplomaticActBy); Mix(ref hash, r.DiplomaticActKind); Mix(ref hash, r.DiplomaticActDay); }
            if (r.GuaranteeBrokenBy >= 0 || r.RivalShiftDay != -9999 || r.RivalShiftPaperBy >= 0 ||
                r.RivalShiftPaperDay != -9999 || !string.IsNullOrEmpty(r.RivalShiftReason))
            {
                MixText(ref hash, "ally_rival_v1");
                Mix(ref hash, r.GuaranteeBrokenBy); Mix(ref hash, r.RivalShiftDay); Mix(ref hash, r.RivalShiftPaperBy);
                Mix(ref hash, r.RivalShiftPaperDay); MixText(ref hash, r.RivalShiftReason ?? "");
            }
            MixRelationAllianceAndTension(r, ref hash);
        }
    }

    private static void MixRelationAllianceAndTension(DiplomaticRelation r, ref ulong hash)
    {
        Mix(ref hash, r.AllianceAssessmentDay); Mix(ref hash, r.AllianceStrainedDays); Mix(ref hash, r.AllianceReaffirmedDay);
        Mix(ref hash, r.AllianceRefusedDay); Mix(ref hash, r.AllianceSinceDay);
        foreach (var character in r.AllianceProgram ?? "") Mix(ref hash, character);
        Mix(ref hash, -1); Mix(ref hash, r.AllianceProgramSponsor); Mix(ref hash, r.AllianceProgramStarted);
        Mix(ref hash, r.AllianceProgramLastPaid); Mix(ref hash, r.AllianceProgramDuration); Mix(ref hash, r.AllianceProgramPaidWeeks);
        MixFloat(ref hash, r.AllianceProgramWeeklyCost); MixFloat(ref hash, r.AllianceProgramTrustBudget);
        Mix(ref hash, r.AllianceProgramRelief ? 1 : 0); Mix(ref hash, r.AllianceProgramPausedDay);
        foreach (var reason in r.Reasons.Where(reason => reason.Id == "diplomatic_outreach"))
        { MixText(ref hash, "diplomatic_goodwill"); Mix(ref hash, reason.GrantedDay); Mix(ref hash, reason.ExpiresDay); Mix(ref hash, reason.Stacks); MixFloat(ref hash, reason.Scale); }
        if (r.MissionByA is not null || r.MissionByB is not null)
        {
            MixText(ref hash, "diplomatic_missions_v1");
            foreach (var mission in new[] { r.MissionByA, r.MissionByB })
            {
                Mix(ref hash, mission is null ? 0 : 1); if (mission is null) continue;
                Mix(ref hash, mission.StartedDay); Mix(ref hash, mission.EndsDay); Mix(ref hash, mission.LastPaidDay); Mix(ref hash, mission.PaidWeeks);
                MixFloat(ref hash, mission.WeeklyPoliticalCost); MixFloat(ref hash, mission.WeeklyCash);
                MixFloat(ref hash, mission.WeeklyOpinion); MixFloat(ref hash, mission.OpinionCap);
            }
        }
        if (r.LoanLender >= 0)
        { MixText(ref hash, "development_loan"); Mix(ref hash, r.LoanLender); MixFloat(ref hash, r.LoanOutstanding); MixFloat(ref hash, r.LoanWeekly); Mix(ref hash, r.LoanMissedWeeks); }
        MixFloat(ref hash, r.Tension); MixFloat(ref hash, r.StructuralTension); MixFloat(ref hash, r.TensionBaseline);
        MixFloat(ref hash, r.TensionAnchor); MixFloat(ref hash, r.GrievanceA); MixFloat(ref hash, r.GrievanceB);
        Mix(ref hash, r.TensionStage); Mix(ref hash, r.LastIncidentDay); Mix(ref hash, r.TalksUntilDay); Mix(ref hash, r.LastTalksDay);
        Mix(ref hash, r.MediatorId); Mix(ref hash, r.LastMediationDay); Mix(ref hash, r.InterveningPowerId); Mix(ref hash, r.InterventionTargetId); Mix(ref hash, r.InterventionUntilDay);
        if (r.CrisisOpenedDay >= 0) { MixText(ref hash, "crisis_opened"); Mix(ref hash, r.CrisisOpenedDay); }
        if (r.QuarrelRefusalDay > 0 || r.QuarrelClaimDay > 0 || r.QuarrelRivalDay > 0 || r.QuarrelBorderDay > 0)
        { MixText(ref hash, "quarrel_step"); Mix(ref hash, r.QuarrelRefusalDay); Mix(ref hash, r.QuarrelClaimDay); Mix(ref hash, r.QuarrelRivalDay); Mix(ref hash, r.QuarrelBorderDay); }
    }

    private static void MixOrganizationsAndArmsShipments(Campaign campaign, ref ulong hash)
    {
        Mix(ref hash, campaign.NextOrganizationId);
        foreach (var o in campaign.Organizations.OrderBy(o => o.Id))
        {
            Mix(ref hash, o.Id); Mix(ref hash, o.FounderId); Mix(ref hash, o.FoundedDay); Mix(ref hash, o.LastReviewDay); Mix(ref hash, o.NextMotionId);
            MixText(ref hash, o.Name); MixText(ref hash, o.GeneratedCountryTag); MixText(ref hash, o.CharterId);
            MixFloat(ref hash, o.Budget); MixFloat(ref hash, o.Cohesion); Mix(ref hash, o.Dissolved ? 1 : 0);
            if (!string.IsNullOrEmpty(o.DoctrineId)) { MixText(ref hash, "org_doctrine"); MixText(ref hash, o.DoctrineId); }
            if (o.CrisisMemberId >= 0) { MixText(ref hash, "org_crisis"); Mix(ref hash, o.CrisisMemberId); Mix(ref hash, o.CrisisSinceDay); }
            if (o.Suspended.Count > 0 || o.SuspendedFor.Count > 0)
            {
                MixText(ref hash, "org_suspended");
                foreach (var seat in o.Suspended.OrderBy(entry => entry.Key))
                { Mix(ref hash, seat.Key); Mix(ref hash, seat.Value); MixText(ref hash, o.SuspendedFor.GetValueOrDefault(seat.Key, "")); }
                Mix(ref hash, -24);
            }
            MixText(ref hash, o.LastOutcome); Mix(ref hash, o.LastYes); Mix(ref hash, o.LastVoters);
            foreach (var member in o.Members.OrderBy(id => id)) Mix(ref hash, member); Mix(ref hash, -20);
            foreach (var invitation in o.Invitations.OrderBy(id => id)) Mix(ref hash, invitation); Mix(ref hash, -21);
            foreach (var unpaid in o.UnpaidWeeks.OrderBy(p => p.Key)) { Mix(ref hash, unpaid.Key); Mix(ref hash, unpaid.Value); } Mix(ref hash, -22);
            if (o.Motion is { } m)
            {
                Mix(ref hash, m.Id); Mix(ref hash, m.ProposerId); Mix(ref hash, m.TargetId); Mix(ref hash, m.EndsDay);
                MixText(ref hash, m.Kind); MixFloat(ref hash, m.Amount);
                if (!string.IsNullOrEmpty(m.Subject)) { MixText(ref hash, "motion_subject"); MixText(ref hash, m.Subject); }
                foreach (var v in m.Votes.OrderBy(v => v.Key)) { Mix(ref hash, v.Key); Mix(ref hash, v.Value ? 1 : 0); }
            }
            Mix(ref hash, -23);
        }
        Mix(ref hash, campaign.Sponsorships.Count); Mix(ref hash, campaign.NextSponsorshipId);
        foreach (var s in campaign.Sponsorships)
        {
            Mix(ref hash, s.Id); Mix(ref hash, s.SponsorId); Mix(ref hash, s.ClientId); Mix(ref hash, s.WarId);
            Mix(ref hash, s.Level); Mix(ref hash, s.Ended ? 1 : 0); Mix(ref hash, s.UnpaidWeeks); MixFloat(ref hash, s.Delivered);
        }
        if (campaign.ArmsShipmentRequests.Count > 0 || campaign.ArmsShipmentCooldowns.Count > 0 ||
            campaign.NextArmsShipmentRequestId != 1)
        {
            MixText(ref hash, "arms_shipment_v1"); Mix(ref hash, campaign.NextArmsShipmentRequestId); Mix(ref hash, campaign.ArmsShipmentRequests.Count);
            foreach (var request in campaign.ArmsShipmentRequests.OrderBy(r => r.Id))
            {
                Mix(ref hash, request.Id); Mix(ref hash, request.RequesterId); Mix(ref hash, request.DonorId); Mix(ref hash, request.WarId);
                Mix(ref hash, request.CreatedDay); Mix(ref hash, request.ExpiresDay); Mix(ref hash, request.ClosedDay); Mix(ref hash, (int)request.State);
                MixFloat(ref hash, request.Share); Mix(ref hash, request.Days); MixFloat(ref hash, request.Shipped);
                Mix(ref hash, request.HumanConsent ? 1 : 0); MixText(ref hash, request.Pretext ?? "");
            }
            Mix(ref hash, campaign.ArmsShipmentCooldowns.Count);
            foreach (var clock in campaign.ArmsShipmentCooldowns.OrderBy(x => x.RequesterId).ThenBy(x => x.DonorId))
            { Mix(ref hash, clock.RequesterId); Mix(ref hash, clock.DonorId); Mix(ref hash, clock.ReadyOnDay); }
        }
    }

    private static void MixDormantRelationsOrder(Campaign campaign, ref ulong hash)
    {
        if (!campaign.Nations.Exists(static nation => nation.RetiredDay >= 0)) return;
        MixText(ref hash, "sleeping_order"); Mix(ref hash, campaign.DormantRelations.Count);
        foreach (var key in campaign.DormantRelations.Keys) Mix(ref hash, key);
    }

    private static void MixNationDiplomacyAndCovert(Nation nation, ref ulong hash)
    {
        MixImage(ref hash, nation.DomesticImage);
        Mix(ref hash, nation.PublicImages.Count);
        foreach (var image in nation.PublicImages.OrderBy(p => p.Key, StringComparer.Ordinal))
        { MixText(ref hash, image.Key); MixImage(ref hash, image.Value); }
        Mix(ref hash, nation.Outreach.Count);
        foreach (var entry in nation.Outreach.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var p = entry.Value; MixText(ref hash, entry.Key); MixText(ref hash, p.ActionId); MixText(ref hash, p.TargetTag);
            Mix(ref hash, p.StartedDay); Mix(ref hash, p.LastReviewDay); Mix(ref hash, p.DurationDays); Mix(ref hash, p.ProgressDays);
            Mix(ref hash, p.CooldownDays); Mix(ref hash, p.CooldownUntilDay); Mix(ref hash, p.PausedDay); Mix(ref hash, p.Cancelled ? 1 : 0);
            MixFloat(ref hash, p.PaidCost); MixFloat(ref hash, p.NationImpulse); MixFloat(ref hash, p.CultureImpulse);
        }
        MixFloatDictionary(ref hash, "intel_networks", nation.IntelNetworks);
        MixFloatDictionary(ref hash, "organization_belonging_at_start", nation.OrganizationBelongingAtStart);
        if (nation.OrganizationConcessions.Count > 0)
        {
            MixText(ref hash, "organization_concessions");
            foreach (var pair in nation.OrganizationConcessions.OrderBy(p => p.Key)) { Mix(ref hash, pair.Key); Mix(ref hash, pair.Value); }
        }
        if (nation.CovertOperations.Count > 0)
        {
            MixText(ref hash, "covert_operations"); Mix(ref hash, nation.CovertOperations.Count);
            foreach (var operation in nation.CovertOperations)
            {
                MixText(ref hash, operation.Kind); Mix(ref hash, operation.TargetId); Mix(ref hash, operation.RegionMapIndex);
                Mix(ref hash, operation.StartedDay); Mix(ref hash, operation.DueDay); MixFloat(ref hash, operation.Spent);
            }
        }
        if (nation.CovertQuietUntil.Count > 0)
        {
            MixText(ref hash, "covert_quiet"); Mix(ref hash, nation.CovertQuietUntil.Count);
            foreach (var quiet in nation.CovertQuietUntil.OrderBy(p => p.Key)) { Mix(ref hash, quiet.Key); Mix(ref hash, quiet.Value); }
        }
        if (!string.IsNullOrEmpty(nation.ConsolidationProject) || nation.ConsolidationFocusId != -1 ||
            nation.ConsolidationAmbition != 0f)
        {
            MixText(ref hash, "consolidation"); MixText(ref hash, nation.ConsolidationProject);
            Mix(ref hash, nation.ConsolidationFocusId); MixFloat(ref hash, nation.ConsolidationAmbition);
        }
    }

    private static void MixNationSubjectState(Nation nation, ref ulong hash)
    {
        if (!nation.HasCustomSubjectState) return;
        MixText(ref hash, "subject_interactions");
        Mix(ref hash, nation.SubjectIntegrating ? 1 : 0); MixFloat(ref hash, nation.SubjectIntegrationProgress);
        MixFloat(ref hash, nation.SubjectLibertyModifier); Mix(ref hash, nation.LastSubjectInteractionDay);
        Mix(ref hash, nation.SubjectTributeHolidayUntilDay);
        if (!nation.SubjectConspiring && nation.SubjectForeignBackers.Count == 0 && nation.LastForeignAgitationDay == -9999) return;
        Mix(ref hash, nation.SubjectConspiring ? 1 : 0); Mix(ref hash, nation.LastForeignAgitationDay);
        Mix(ref hash, nation.SubjectForeignBackers.Count);
        foreach (var backerId in nation.SubjectForeignBackers.OrderBy(id => id)) Mix(ref hash, backerId);
    }
}
