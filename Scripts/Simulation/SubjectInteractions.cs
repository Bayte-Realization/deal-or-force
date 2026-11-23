using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int SubjectInteractionCooldownDays { get; set; } = 60;
    public float SubjectIntegrationDailyBase { get; set; } = 0.0045f;
    public float SubjectIntegrationTreasuryWeeklyShare { get; set; } = 0.002f;
    public float SubjectSoftPacifyCostShare { get; set; } = 0.06f;
    public float SubjectSoftPacifyCostFloor { get; set; } = 0.05f;
    public float SubjectHardPacifyStabilityCost { get; set; } = 0.02f;
    public float SubjectHardPacifyNotoriety { get; set; } = 4f;
    public float SubjectSabotageThreshold { get; set; } = 0.65f;
    public float SubjectRebellionThreshold { get; set; } = 0.85f;
    public float SubjectAnnexationRevoltThreshold { get; set; } = 0.60f;
    public float SubjectConspiracyThreshold { get; set; } = 0.40f;
    /// <summary>The fewest people a subject needs before foreign money can raise a movement in it.</summary>
    public long SubjectAgitationMinPopulation { get; set; } = 100_000;
    public float AiReleaseOverstretchThreshold { get; set; } = 0.32f;
    public float AiReleaseRegionHeatThreshold { get; set; } = 0.68f;
}

public sealed partial class Nation
{
    public bool SubjectIntegrating;
    public float SubjectIntegrationProgress;
    public float SubjectLibertyModifier;
    public int LastSubjectInteractionDay = -9999;
    public int SubjectTributeHolidayUntilDay = -1;
    public bool SubjectConspiring;
    public List<int> SubjectForeignBackers = new();
    public int LastForeignAgitationDay = -9999;

    [JsonIgnore]
    public bool HasCustomSubjectState =>
        SubjectIntegrating || SubjectIntegrationProgress != 0f ||
        SubjectLibertyModifier != 0f || LastSubjectInteractionDay != -9999 ||
        SubjectTributeHolidayUntilDay != -1 || SubjectConspiring ||
        SubjectForeignBackers.Count > 0 || LastForeignAgitationDay != -9999;
}

public enum SubjectInteractionKind
{
    GrantSubsidies,
    TributeHoliday,
    LoosenAutonomy,
    TightenAutonomy,
    MartialCrackdown,
    ReplaceGovernor,
    SiphonTreasury,
    DraftLevies,
    ToggleIntegration,
    CompleteAnnexation,
    ReleaseFree,
    BreakConspiracy,
    AgitateIndependence
}

public static class SubjectInteractions
{
    private static readonly string[] AutonomyLadder =
    {
        "puppet",
        "satellite",
        "protectorate",
        "client",
        "dominion"
    };

    public static float SubsidyCost(Campaign campaign, Nation subject) =>
        campaign is null || subject is null ? 0f :
        MathF.Max(campaign.Defines.SubjectSoftPacifyCostFloor,
            MathF.Max(0.05f, subject.Gdp) * campaign.Defines.SubjectSoftPacifyCostShare);

    public static float AgitationCost(Campaign campaign, Nation sponsor, Nation subject) =>
        campaign is null || subject is null ? 0f :
        MathF.Max(0.04f, MathF.Max(0.05f, subject.Gdp) * 0.05f);

    public static float SiphonGain(Nation subject) =>
        subject is null ? 0f : MathF.Max(0.05f, MathF.Max(0f, subject.Gdp) * 0.08f);

    public static float LevyGain(Nation subject) =>
        subject is null ? 0f : MathF.Max(1000f, subject.ManpowerReserve * 0.25f);

    /// <summary>All subjects of this overlord currently bound in an inter-subject liberty conspiracy.</summary>
    public static List<Nation> ConspiringSubjectsOf(Campaign campaign, Nation overlord)
    {
        var list = new List<Nation>();
        if (campaign is null || overlord is null) return list;
        foreach (var nation in campaign.Nations)
            if (nation.AnswersTo(overlord.Id) && nation.SubjectConspiring)
                list.Add(nation);
        return list;
    }

    /// <summary>Bind two disgruntled subjects of the same overlord into a mutual liberty pact ("сговор").</summary>
    public static bool FormSubjectConspiracy(Campaign campaign, Nation subjectA, Nation subjectB)
    {
        if (campaign is null || subjectA is null || subjectB is null || subjectA.Id == subjectB.Id) return false;
        if (!subjectA.IsSubject || !subjectB.IsSubject || subjectA.OverlordId != subjectB.OverlordId) return false;
        var newlyFormed = !subjectA.SubjectConspiring || !subjectB.SubjectConspiring;
        subjectA.SubjectConspiring = true;
        subjectB.SubjectConspiring = true;
        var rel = CampaignDiplomacy.Get(campaign, subjectA.Id, subjectB.Id);
        rel.NonAggressionPact = true;
        rel.Opinion = MathF.Max(rel.Opinion, 45f);
        if (newlyFormed)
        {
            subjectA.SubjectLibertyModifier = MathF.Min(0.95f, subjectA.SubjectLibertyModifier + 0.08f);
            subjectB.SubjectLibertyModifier = MathF.Min(0.95f, subjectB.SubjectLibertyModifier + 0.08f);
            subjectA.LibertyDesire = M.Clamp01(subjectA.LibertyDesire + 0.10f);
            subjectB.LibertyDesire = M.Clamp01(subjectB.LibertyDesire + 0.10f);
            campaign.MarkStrategicChange();
        }
        return true;
    }

    private static void TrySpreadConspiracy(Campaign campaign, Nation overlord, Nation instigated)
    {
        if (campaign is null || overlord is null || instigated is null || !instigated.AnswersTo(overlord.Id)) return;
        if (instigated.LibertyDesire < campaign.Defines.SubjectConspiracyThreshold && !instigated.SubjectIntegrating) return;
        foreach (var peer in campaign.Nations)
        {
            if (peer.Id == instigated.Id || !peer.AnswersTo(overlord.Id)) continue;
            if (peer.LibertyDesire >= campaign.Defines.SubjectConspiracyThreshold ||
                (instigated.SubjectIntegrating && peer.LibertyDesire >= 0.32f))
            {
                FormSubjectConspiracy(campaign, instigated, peer);
            }
        }
    }

    /// <summary>Foreign power covertly or diplomatically sponsors a subject's independence movement, sharply raising its liberty desire.</summary>
    public static bool AgitateSubject(Campaign campaign, Nation sponsor, Nation subject, out string feedback)
    {
        feedback = "";
        if (!CanInteract(campaign, sponsor, subject, SubjectInteractionKind.AgitateIndependence, out feedback))
            return false;
        var overlord = campaign.NationById(subject.OverlordId);
        var cost = AgitationCost(campaign, sponsor, subject);
        { var ledgerBefore = sponsor.Treasury; sponsor.Treasury -= cost; MoneyLedger.Book(sponsor, MoneyFlow.Tribute, sponsor.Treasury - ledgerBefore); }
        if (!subject.SubjectForeignBackers.Contains(sponsor.Id))
            subject.SubjectForeignBackers.Add(sponsor.Id);
        subject.LastForeignAgitationDay = campaign.ElapsedDays;

        var boost = 0.24f;
        if (overlord is not null && CampaignDiplomacy.Peek(campaign, sponsor.Id, overlord.Id)?.Stance == DiplomaticStance.Rival)
            boost += 0.06f;
        subject.SubjectLibertyModifier = MathF.Min(0.95f, subject.SubjectLibertyModifier + boost);
        subject.LibertyDesire = M.Clamp01(subject.LibertyDesire + boost);

        var sponsorRel = CampaignDiplomacy.Get(campaign, sponsor.Id, subject.Id);
        sponsorRel.Opinion = MathF.Min(100f, sponsorRel.Opinion + 25f);
        OpinionSystem.Remember(campaign, sponsorRel, "improved_relations");

        if (overlord is not null)
        {
            var overlordRel = CampaignDiplomacy.Get(campaign, sponsor.Id, overlord.Id);
            OpinionSystem.Remember(campaign, overlordRel, "covert_operation_against_us");
            foreach (var peer in campaign.Nations)
            {
                if (peer.Id == subject.Id || !peer.AnswersTo(overlord.Id)) continue;
                peer.SubjectLibertyModifier = MathF.Min(0.95f, peer.SubjectLibertyModifier + 0.08f);
                peer.LibertyDesire = M.Clamp01(peer.LibertyDesire + 0.08f);
            }
            TrySpreadConspiracy(campaign, overlord, subject);
            if (subject.LibertyDesire >= campaign.Defines.SubjectRebellionThreshold)
            {
                TriggerSubjectRebellion(campaign, overlord, subject);
                feedback = Loc.Format("Foreign backing from {0} ignited an independence uprising in {1}!", sponsor.DisplayName, subject.DisplayName);
                return true;
            }
        }

        campaign.MarkStrategicChange();
        feedback = Loc.Format("{0} is now backing independence movements in {1}; their liberty desire surged.", sponsor.DisplayName, subject.DisplayName);
        return true;
    }

    /// <summary>
    /// Launch a full independence rebellion war by <paramref name="subject"/> against <paramref name="overlord"/>,
    /// bringing in all fellow conspiring subjects ("сговор") and any other high-liberty subjects of the same overlord.
    /// </summary>
    public static StrategicWar TriggerSubjectRebellion(Campaign campaign, Nation overlord, Nation subject)
    {
        if (campaign is null || overlord is null || subject is null || !subject.AnswersTo(overlord.Id))
            return null;
        // A kind that does not leave by war leaves by vote (SubjectType.ArmedRebellion).
        if (SubjectCatalog.Of(subject.SubjectType) is { ArmedRebellion: false })
        {
            SubjectSystem.Release(campaign, subject, broke: true);
            campaign.Chronicle.Record(campaign, "subject_broke_free", subject.Id, overlord.Id);
            campaign.MarkStrategicChange();
            return null;
        }

        var coRebels = new List<Nation>();
        foreach (var peer in campaign.Nations)
        {
            if (peer.Id == subject.Id || !peer.AnswersTo(overlord.Id)) continue;
            if (SubjectCatalog.Of(peer.SubjectType) is { ArmedRebellion: false }) continue;
            if ((subject.SubjectConspiring && peer.SubjectConspiring) ||
                peer.LibertyDesire >= campaign.Defines.SubjectRebellionThreshold * 0.85f)
            {
                coRebels.Add(peer);
            }
        }

        var backers = new List<int>(subject.SubjectForeignBackers);
        foreach (var peer in coRebels)
            foreach (var b in peer.SubjectForeignBackers)
                if (!backers.Contains(b)) backers.Add(b);

        var allRebels = new List<Nation> { subject };
        foreach (var ally in coRebels)
            if (ally.AnswersTo(overlord.Id) && !allRebels.Contains(ally))
                allRebels.Add(ally);

        foreach (var reb in allRebels)
        {
            SubjectSystem.Release(campaign, reb, broke: true);
            reb.SecededFromId = overlord.Id;
            // A subject that walked out is born of its overlord for good, whatever a later peace
            // says about the war.
            if (reb.RevoltParentId < 0) reb.RevoltParentId = overlord.Id;
            reb.WarSupport = MathF.Max(reb.WarSupport, 0.82f);
            reb.Stability = MathF.Max(reb.Stability, 0.55f);
            var rel = CampaignDiplomacy.Get(campaign, overlord.Id, reb.Id);
            if (rel.Stance == DiplomaticStance.Allied || rel.GuaranteeByA || rel.GuaranteeByB) campaign.NoteAllianceChange();
            rel.Stance = DiplomaticStance.Rival;
            rel.TruceUntil = -1;
            rel.NonAggressionPact = false;
            rel.GuaranteeByA = false;
            rel.GuaranteeByB = false;
        }

        StrategicWar war = null;
        foreach (var reb in allRebels)
        {
            var claimed = campaign.StatesOf(reb).FirstOrDefault()?.MapIndex ?? -1;
            war = CampaignDiplomacy.DeclareWar(campaign, overlord.Id, reb.Id, claimed, "secession", onOwnInitiative: false);
            if (war is not null) break;
        }
        if (war is null)
        {
            var claimed = campaign.StatesOf(subject).FirstOrDefault()?.MapIndex ?? -1;
            war = new StrategicWar
            {
                Id = campaign.Wars.Count == 0 ? 1 : campaign.Wars.Max(w => w.Id) + 1,
                AttackerId = overlord.Id,
                DefenderId = subject.Id,
                ClaimedStateMapIndex = claimed,
                Justification = "secession",
                CivilConflict = true,
                StartedDay = campaign.ElapsedDays,
                AttackerWarSupport = overlord.WarSupport,
                DefenderWarSupport = subject.WarSupport
            };
            campaign.Wars.Add(war);
            var rel = CampaignDiplomacy.Get(campaign, overlord.Id, subject.Id);
            if (rel.Stance == DiplomaticStance.Allied) campaign.NoteAllianceChange();
            rel.Stance = DiplomaticStance.AtWar;
            rel.LastActionDay = campaign.ElapsedDays;
        }

        war.CivilConflict = true;
        var rebelAttacking = war.IsAttacking(war.AttackerId == overlord.Id ? war.DefenderId : war.AttackerId);
        foreach (var ally in allRebels)
        {
            if (!war.Involves(ally.Id))
                war.Join(ally.Id, rebelAttacking);
            var rel = CampaignDiplomacy.Get(campaign, overlord.Id, ally.Id);
            if (rel.Stance == DiplomaticStance.Allied) campaign.NoteAllianceChange();
            rel.Stance = DiplomaticStance.AtWar;
            rel.LastActionDay = campaign.ElapsedDays;
            if (ally.Id != subject.Id)
            {
                var pactRel = CampaignDiplomacy.Get(campaign, subject.Id, ally.Id);
                if (pactRel.Stance != DiplomaticStance.Allied) campaign.NoteAllianceChange();
                pactRel.Stance = DiplomaticStance.Allied;
                campaign.Chronicle.Record(campaign, "subject_rebellion", ally.Id, overlord.Id);
            }
        }
        Warfare.RebuildFronts(campaign);

        foreach (var backerId in backers)
        {
            var backer = campaign.NationById(backerId);
            if (backer is null || backer.Id == overlord.Id || backer.IsSubject) continue;
            var gift = MathF.Min(MathF.Max(0f, backer.Treasury) * 0.05f, 0.50f);
            if (gift > 0.01f)
            {
                { var ledgerBefore = backer.Treasury; backer.Treasury -= gift; MoneyLedger.Book(backer, MoneyFlow.Tribute, backer.Treasury - ledgerBefore); }
                { var ledgerBefore = subject.Treasury; subject.Treasury += gift; MoneyLedger.Book(subject, MoneyFlow.Tribute, subject.Treasury - ledgerBefore); }
            }
        }

        campaign.Chronicle.Record(campaign, "subject_rebellion", subject.Id, overlord.Id);
        campaign.MarkStrategicChange();
        campaign.MarkWarsChanged();
        return war;
    }

    /// <summary>Whether an AI overlord wants to begin integrating a subject (such as one previously released during unrest once stability recovers).</summary>
    public static bool WantsToIntegrateSubject(Campaign campaign, Nation overlord, Nation subject)
    {
        if (campaign is null || overlord is null || subject is null || !subject.AnswersTo(overlord.Id))
            return false;
        if (subject.SubjectIntegrating || overlord.Treasury < 0.20f) return false;
        if (overlord.ImperialOverstretch >= campaign.Defines.AiReleaseOverstretchThreshold || overlord.Stability < 0.48f)
            return false;
        if (!PeaceShape.Near(campaign, overlord, subject)) return false;

        var heldDays = campaign.ElapsedDays - subject.SubjectSinceDay;
        var wasReleased = subject.ReleasedFromId == overlord.Id || subject.SecededFromId == overlord.Id;
        if (wasReleased)
            return heldDays >= 180 && subject.LibertyDesire <= 0.65f;

        var type = SubjectCatalog.Of(subject.SubjectType);
        if (type is not null && type.AnnexAfterDays >= 0 && heldDays >= type.GraceDays)
            return subject.LibertyDesire <= 0.45f && subject.Population <= overlord.Population * 0.45f;
        return false;
    }

    public static bool CanInteract(Campaign campaign, Nation overlord, Nation subject, SubjectInteractionKind kind, out string refusal)
    {
        refusal = "";
        if (campaign is null || overlord is null || subject is null) { refusal = "No subject selected."; return false; }

        if (kind == SubjectInteractionKind.AgitateIndependence)
        {
            if (!subject.IsSubject)
            {
                refusal = Loc.Format("{0} is already an independent country.", subject.DisplayName);
                return false;
            }
            if (overlord.Id == subject.OverlordId || overlord.Id == subject.Id)
            {
                refusal = Loc.Get("Select a subject of a foreign power.");
                return false;
            }
            if (SubjectCatalog.Of(subject.SubjectType) is { Agitable: false })
            {
                refusal = Loc.Format("{0} is rented land with no people of its own to raise.", subject.DisplayName);
                return false;
            }
            if (subject.Population < campaign.Defines.SubjectAgitationMinPopulation)
            {
                refusal = Loc.Format("{0} has too few people for an independence movement.", subject.DisplayName);
                return false;
            }
            if (overlord.IsSubject)
            {
                refusal = Loc.Get("A dependency cannot sponsor independence abroad.");
                return false;
            }
            if (subject.SubjectForeignBackers.Contains(overlord.Id) &&
                campaign.ElapsedDays - subject.LastForeignAgitationDay < campaign.Defines.SubjectInteractionCooldownDays)
            {
                var left = campaign.Defines.SubjectInteractionCooldownDays - (campaign.ElapsedDays - subject.LastForeignAgitationDay);
                refusal = Loc.Format("Networks in {0} were funded recently ({1} day(s) left).", subject.DisplayName, left);
                return false;
            }
            var agitateCost = AgitationCost(campaign, overlord, subject);
            if (overlord.Treasury < agitateCost)
            {
                refusal = Loc.Format("Requires {0:0.00}B in treasury.", agitateCost);
                return false;
            }
            return true;
        }

        if (!subject.AnswersTo(overlord.Id))
        {
            refusal = Loc.Format("{0} does not answer to {1}.", subject.DisplayName, overlord.DisplayName);
            return false;
        }
        if (kind == SubjectInteractionKind.ReleaseFree) return true;
        if (kind == SubjectInteractionKind.CompleteAnnexation)
        {
            var wasReleased = subject.ReleasedFromId == overlord.Id || subject.SecededFromId == overlord.Id;
            if ((subject.SubjectIntegrationProgress >= 0.999f || (wasReleased && subject.SubjectIntegrationProgress >= 0.50f)) &&
                (wasReleased || PeaceShape.Near(campaign, overlord, subject)))
                return true;
            return SubjectSystem.MayAnnex(campaign, overlord, subject, out refusal);
        }
        if (kind == SubjectInteractionKind.ToggleIntegration)
        {
            if (subject.SubjectIntegrating) return true;
            if (subject.LibertyDesire > 0.75f)
            {
                refusal = Loc.Format("{0} wants out ({1:P0}); calm it below 75% before beginning integration.",
                    subject.DisplayName, subject.LibertyDesire);
                return false;
            }
            return true;
        }
        if (kind == SubjectInteractionKind.BreakConspiracy)
        {
            if (!subject.SubjectConspiring && subject.SubjectForeignBackers.Count == 0)
            {
                refusal = Loc.Get("No active subject conspiracy or foreign agitation network detected.");
                return false;
            }
            var purgeCost = SubsidyCost(campaign, subject) * 0.5f;
            if (overlord.Treasury < purgeCost)
            {
                refusal = Loc.Format("Requires {0:0.00}B in treasury.", purgeCost);
                return false;
            }
            return true;
        }

        var wait = campaign.Defines.SubjectInteractionCooldownDays - (campaign.ElapsedDays - subject.LastSubjectInteractionDay);
        if (wait > 0)
        {
            refusal = Loc.Format("Another directive was issued to {0} recently ({1} day(s) left).", subject.DisplayName, wait);
            return false;
        }

        switch (kind)
        {
            case SubjectInteractionKind.GrantSubsidies:
                var cost = SubsidyCost(campaign, subject);
                if (overlord.Treasury < cost)
                {
                    refusal = Loc.Format("Requires {0:0.00}B in treasury.", cost);
                    return false;
                }
                break;
            case SubjectInteractionKind.TributeHoliday:
                if (subject.SubjectTributeHolidayUntilDay > campaign.ElapsedDays)
                {
                    refusal = Loc.Get("A tribute exemption is already in force.");
                    return false;
                }
                break;
            case SubjectInteractionKind.LoosenAutonomy:
                if (NextLooserKind(subject.SubjectType).Length == 0)
                {
                    refusal = Loc.Get("This dependency already holds the loosest status.");
                    return false;
                }
                break;
            case SubjectInteractionKind.TightenAutonomy:
                if (NextTighterKind(subject.SubjectType).Length == 0)
                {
                    refusal = Loc.Get("This dependency already holds the tightest status.");
                    return false;
                }
                break;
            case SubjectInteractionKind.MartialCrackdown:
                if (overlord.ManpowerReserve < 5000f && overlord.Stability < 0.25f)
                {
                    refusal = Loc.Get("Insufficient manpower or stability for a garrison crackdown.");
                    return false;
                }
                break;
            case SubjectInteractionKind.ReplaceGovernor:
                var replaceCost = SubsidyCost(campaign, subject) * 0.75f;
                if (overlord.Treasury < replaceCost)
                {
                    refusal = Loc.Format("Requires {0:0.00}B to install a new administration.", replaceCost);
                    return false;
                }
                break;
            case SubjectInteractionKind.SiphonTreasury:
                if (subject.Treasury <= 0.02f && subject.Gdp <= 0.01f)
                {
                    refusal = Loc.Get("Their treasury has nothing left to extract.");
                    return false;
                }
                break;
            case SubjectInteractionKind.DraftLevies:
                if (subject.ManpowerReserve < 2000f)
                {
                    refusal = Loc.Get("Their manpower reserves are exhausted.");
                    return false;
                }
                break;
        }
        return true;
    }

    public static bool Execute(Campaign campaign, Nation overlord, Nation subject, SubjectInteractionKind kind, out string feedback)
    {
        if (kind == SubjectInteractionKind.AgitateIndependence)
            return AgitateSubject(campaign, overlord, subject, out feedback);

        if (!CanInteract(campaign, overlord, subject, kind, out feedback)) return false;
        var relation = CampaignDiplomacy.Get(campaign, subject.Id, overlord.Id);
        switch (kind)
        {
            case SubjectInteractionKind.ReleaseFree:
                SubjectSystem.Release(campaign, subject);
                feedback = Loc.Format("{0} has been granted full independence.", subject.DisplayName);
                return true;

            case SubjectInteractionKind.BreakConspiracy:
            {
                var purgeCost = SubsidyCost(campaign, subject) * 0.5f;
                { var ledgerBefore = overlord.Treasury; overlord.Treasury -= purgeCost; MoneyLedger.Book(overlord, MoneyFlow.Tribute, overlord.Treasury - ledgerBefore); }
                overlord.Stability = M.Clamp01(overlord.Stability - 0.015f);
                if (subject.LibertyDesire >= campaign.Defines.SubjectRebellionThreshold)
                {
                    TriggerSubjectRebellion(campaign, overlord, subject);
                    feedback = Loc.Format("{0} resisted the purge and launched a coordinated independence uprising!", subject.DisplayName);
                    return true;
                }
                subject.SubjectConspiring = false;
                subject.SubjectForeignBackers.Clear();
                subject.SubjectLibertyModifier = MathF.Max(-0.55f, subject.SubjectLibertyModifier - 0.10f);
                subject.LibertyDesire = MathF.Max(0f, subject.LibertyDesire - 0.12f);
                var remainingConspirators = ConspiringSubjectsOf(campaign, overlord);
                if (remainingConspirators.Count == 1)
                    remainingConspirators[0].SubjectConspiring = false;
                subject.LastSubjectInteractionDay = campaign.ElapsedDays;
                feedback = Loc.Format("Dismantled the conspiracy network and expelled foreign agitators in {0}.", subject.DisplayName);
                campaign.MarkStrategicChange();
                return true;
            }

            case SubjectInteractionKind.ToggleIntegration:
                subject.SubjectIntegrating = !subject.SubjectIntegrating;
                if (subject.SubjectIntegrating)
                {
                    var wasReleased = subject.ReleasedFromId == overlord.Id || subject.SecededFromId == overlord.Id;
                    var spike = 0.20f + (wasReleased ? 0.10f : 0f);
                    subject.SubjectLibertyModifier = MathF.Min(0.95f, subject.SubjectLibertyModifier + spike);
                    subject.LibertyDesire = M.Clamp01(subject.LibertyDesire + spike);
                    foreach (var peer in campaign.Nations)
                    {
                        if (peer.Id == subject.Id || !peer.AnswersTo(overlord.Id)) continue;
                        peer.SubjectLibertyModifier = MathF.Min(0.95f, peer.SubjectLibertyModifier + 0.08f);
                        peer.LibertyDesire = M.Clamp01(peer.LibertyDesire + 0.08f);
                    }
                    TrySpreadConspiracy(campaign, overlord, subject);
                    if (subject.LibertyDesire >= campaign.Defines.SubjectRebellionThreshold)
                    {
                        TriggerSubjectRebellion(campaign, overlord, subject);
                        feedback = Loc.Format("{0} refused integration and rose in rebellion against {1}!", subject.DisplayName, overlord.DisplayName);
                        return true;
                    }
                }
                feedback = subject.SubjectIntegrating
                    ? Loc.Format("Began administrative integration of {0}.", subject.DisplayName)
                    : Loc.Format("Suspended integration of {0}.", subject.DisplayName);
                campaign.MarkStrategicChange();
                return true;

            case SubjectInteractionKind.CompleteAnnexation:
            {
                var revoltThreshold = campaign.Defines.SubjectAnnexationRevoltThreshold;
                if (subject.LibertyDesire >= revoltThreshold ||
                    (subject.SubjectConspiring && ConspiringSubjectsOf(campaign, overlord).Count >= 2 && subject.LibertyDesire >= 0.45f))
                {
                    TriggerSubjectRebellion(campaign, overlord, subject);
                    feedback = Loc.Format("{0} rejected annexation and declared an armed war of independence!", subject.DisplayName);
                    return true;
                }
                var wasReleased = subject.ReleasedFromId == overlord.Id || subject.SecededFromId == overlord.Id;
                if (subject.SubjectIntegrationProgress >= 0.999f || (wasReleased && subject.SubjectIntegrationProgress >= 0.50f))
                {
                    ForceAnnexIntegrated(campaign, overlord, subject);
                    ShockRemainingSubjectsOnAnnexation(campaign, overlord);
                    feedback = Loc.Format("{0} has been integrated into {1}.", subject.DisplayName, overlord.DisplayName);
                    return true;
                }
                if (SubjectSystem.Annex(campaign, overlord, subject, out feedback))
                {
                    ShockRemainingSubjectsOnAnnexation(campaign, overlord);
                    feedback = Loc.Format("{0} has been integrated into {1}.", subject.DisplayName, overlord.DisplayName);
                    return true;
                }
                return false;
            }

            case SubjectInteractionKind.GrantSubsidies:
            {
                var cost = SubsidyCost(campaign, subject);
                { var ledgerBefore = overlord.Treasury; overlord.Treasury -= cost; MoneyLedger.Book(overlord, MoneyFlow.Tribute, overlord.Treasury - ledgerBefore); }
                { var ledgerBefore = subject.Treasury; subject.Treasury += cost; MoneyLedger.Book(subject, MoneyFlow.Tribute, subject.Treasury - ledgerBefore); }
                subject.SubjectLibertyModifier = MathF.Max(-0.55f, subject.SubjectLibertyModifier - 0.16f);
                subject.LibertyDesire = MathF.Max(0f, subject.LibertyDesire - 0.16f);
                if (subject.LibertyDesire < 0.30f) subject.SubjectConspiring = false;
                OpinionSystem.Remember(campaign, relation, "improved_relations");
                subject.LastSubjectInteractionDay = campaign.ElapsedDays;
                feedback = Loc.Format("Sent {0:0.00}B in development subsidies to {1}; liberty desire fell.", cost, subject.DisplayName);
                campaign.MarkStrategicChange();
                return true;
            }

            case SubjectInteractionKind.TributeHoliday:
                subject.SubjectTributeHolidayUntilDay = campaign.ElapsedDays + 365;
                subject.SubjectLibertyModifier = MathF.Max(-0.55f, subject.SubjectLibertyModifier - 0.14f);
                subject.LibertyDesire = MathF.Max(0f, subject.LibertyDesire - 0.12f);
                if (subject.LibertyDesire < 0.30f) subject.SubjectConspiring = false;
                subject.LastSubjectInteractionDay = campaign.ElapsedDays;
                feedback = Loc.Format("Granted {0} a one-year tribute exemption.", subject.DisplayName);
                campaign.MarkStrategicChange();
                return true;

            case SubjectInteractionKind.LoosenAutonomy:
            {
                var next = NextLooserKind(subject.SubjectType);
                subject.SubjectType = next;
                subject.SubjectLibertyModifier = MathF.Max(-0.55f, subject.SubjectLibertyModifier - 0.18f);
                subject.LibertyDesire = MathF.Max(0f, subject.LibertyDesire - 0.18f);
                if (subject.LibertyDesire < 0.30f) subject.SubjectConspiring = false;
                subject.LastSubjectInteractionDay = campaign.ElapsedDays;
                feedback = Loc.Format("Granted {0} broader autonomy ({1}).", subject.DisplayName, SubjectCatalog.Of(next)?.Name ?? next);
                campaign.MarkStrategicChange();
                return true;
            }

            case SubjectInteractionKind.TightenAutonomy:
            {
                var next = NextTighterKind(subject.SubjectType);
                subject.SubjectType = next;
                subject.SubjectLibertyModifier = MathF.Min(0.85f, subject.SubjectLibertyModifier + 0.16f);
                subject.LibertyDesire = M.Clamp01(subject.LibertyDesire + 0.16f);
                subject.SubjectIntegrationProgress = M.Clamp01(subject.SubjectIntegrationProgress + 0.12f);
                subject.LastSubjectInteractionDay = campaign.ElapsedDays;
                TrySpreadConspiracy(campaign, overlord, subject);
                if (subject.LibertyDesire >= campaign.Defines.SubjectRebellionThreshold)
                {
                    TriggerSubjectRebellion(campaign, overlord, subject);
                    feedback = Loc.Format("{0} revolted rather than accept tighter subjugation!", subject.DisplayName);
                    return true;
                }
                feedback = Loc.Format("Tightened control over {0} ({1}); integration advanced.", subject.DisplayName, SubjectCatalog.Of(next)?.Name ?? next);
                campaign.MarkStrategicChange();
                return true;
            }

            case SubjectInteractionKind.MartialCrackdown:
                overlord.Stability = M.Clamp01(overlord.Stability - campaign.Defines.SubjectHardPacifyStabilityCost);
                overlord.ManpowerReserve = MathF.Max(0f, overlord.ManpowerReserve - 3000f);
                overlord.Notoriety += campaign.Defines.SubjectHardPacifyNotoriety;
                subject.LibertyDesire = MathF.Max(0f, subject.LibertyDesire - 0.25f);
                subject.SubjectLibertyModifier = MathF.Max(-0.55f, subject.SubjectLibertyModifier - 0.12f);
                subject.Stability = M.Clamp01(subject.Stability - 0.08f);
                OpinionSystem.Remember(campaign, relation, "denounced_us");
                subject.LastSubjectInteractionDay = campaign.ElapsedDays;
                feedback = Loc.Format("Enforced martial garrisons in {0}; immediate unrest suppressed at the cost of resentment.", subject.DisplayName);
                campaign.MarkStrategicChange();
                return true;

            case SubjectInteractionKind.ReplaceGovernor:
            {
                var cost = SubsidyCost(campaign, subject) * 0.75f;
                { var ledgerBefore = overlord.Treasury; overlord.Treasury -= cost; MoneyLedger.Book(overlord, MoneyFlow.Tribute, overlord.Treasury - ledgerBefore); }
                subject.Government = overlord.Government;
                subject.Ideology = overlord.Ideology;
                Rulers.ReplaceHeadOfState(campaign, subject, "");
                subject.LibertyDesire = MathF.Max(0f, subject.LibertyDesire - 0.10f);
                subject.SubjectIntegrationProgress = M.Clamp01(subject.SubjectIntegrationProgress + 0.10f);
                subject.LastSubjectInteractionDay = campaign.ElapsedDays;
                feedback = Loc.Format("Installed a loyal administration in {0} under {1}.", subject.DisplayName, Rulers.HeadOfState(campaign, subject).Name);
                campaign.MarkStrategicChange();
                return true;
            }

            case SubjectInteractionKind.SiphonTreasury:
            {
                var amount = SiphonGain(subject);
                { var ledgerBefore = subject.Treasury; subject.Treasury -= amount; MoneyLedger.Book(subject, MoneyFlow.Tribute, subject.Treasury - ledgerBefore); }
                { var ledgerBefore = overlord.Treasury; overlord.Treasury += amount; MoneyLedger.Book(overlord, MoneyFlow.Tribute, overlord.Treasury - ledgerBefore); }
                subject.SubjectLibertyModifier = MathF.Min(0.85f, subject.SubjectLibertyModifier + 0.18f);
                subject.LibertyDesire = M.Clamp01(subject.LibertyDesire + 0.20f);
                OpinionSystem.Remember(campaign, relation, "denounced_us");
                subject.LastSubjectInteractionDay = campaign.ElapsedDays;
                TrySpreadConspiracy(campaign, overlord, subject);
                if (subject.LibertyDesire >= campaign.Defines.SubjectRebellionThreshold)
                {
                    TriggerSubjectRebellion(campaign, overlord, subject);
                    feedback = Loc.Format("Treasury extraction pushed {0} into open rebellion!", subject.DisplayName);
                    return true;
                }
                feedback = Loc.Format("Extracted {0:0.00}B from {1}; liberty desire rose sharply.", amount, subject.DisplayName);
                campaign.MarkStrategicChange();
                return true;
            }

            case SubjectInteractionKind.DraftLevies:
            {
                var men = LevyGain(subject);
                subject.ManpowerReserve = MathF.Max(0f, subject.ManpowerReserve - men);
                overlord.ManpowerReserve += men;
                subject.SubjectLibertyModifier = MathF.Min(0.85f, subject.SubjectLibertyModifier + 0.16f);
                subject.LibertyDesire = M.Clamp01(subject.LibertyDesire + 0.18f);
                subject.LastSubjectInteractionDay = campaign.ElapsedDays;
                TrySpreadConspiracy(campaign, overlord, subject);
                if (subject.LibertyDesire >= campaign.Defines.SubjectRebellionThreshold)
                {
                    TriggerSubjectRebellion(campaign, overlord, subject);
                    feedback = Loc.Format("Forced conscription pushed {0} into open rebellion!", subject.DisplayName);
                    return true;
                }
                feedback = Loc.Format("Conscripted {0:N0} men from {1} into our reserves.", (long)men, subject.DisplayName);
                campaign.MarkStrategicChange();
                return true;
            }
        }
        return false;
    }

    private static void ShockRemainingSubjectsOnAnnexation(Campaign campaign, Nation overlord)
    {
        if (campaign is null || overlord is null) return;
        foreach (var peer in campaign.Nations.ToArray())
        {
            if (!peer.AnswersTo(overlord.Id)) continue;
            peer.SubjectLibertyModifier = MathF.Min(0.95f, peer.SubjectLibertyModifier + 0.14f);
            peer.LibertyDesire = M.Clamp01(peer.LibertyDesire + 0.15f);
            TrySpreadConspiracy(campaign, overlord, peer);
            if (peer.LibertyDesire >= campaign.Defines.SubjectRebellionThreshold)
            {
                TriggerSubjectRebellion(campaign, overlord, peer);
                break;
            }
        }
    }

    public static void ForceAnnexIntegrated(Campaign campaign, Nation overlord, Nation subject)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is null || overlord is null || subject is null) return;
        var taken = new List<int>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToNation[province] == subject.MapIndex) taken.Add(province);
        foreach (var army in campaign.Armies)
            if (army.NationId == subject.Id) army.NationId = overlord.Id;
        SubjectSystem.Release(campaign, subject);
        subject.SubjectIntegrating = false;
        subject.SubjectIntegrationProgress = 0f;
        if (!string.IsNullOrEmpty(subject.DeclaredCulture))
            overlord.FoundedCultures.Remove(subject.DeclaredCulture);
        // A subject written into its overlord: a peaceful absorption, never a rising put down, even
        // when the subject was once a breakaway of the overlord (ConquestAcceptance.ChangedHands).
        if (taken.Count > 0) StatePartition.Annex(campaign, taken, overlord, asConquest: false, byConsent: true);
        campaign.MarkMapChanged();
        campaign.MarkStrategicChange();
    }

    public static void TickSubjectWeek(Campaign campaign, Nation overlord, Nation subject)
    {
        if (campaign is null || overlord is null || subject is null) return;

        // While a subject administration governs its regions, it actively pacifies local unrest and separatism
        foreach (var state in campaign.StatesOf(subject))
        {
            if (state.Radicalism > 0.05f)
                state.Radicalism = MathF.Max(0.05f, state.Radicalism - 0.012f);
            if (state.SecessionPressure > 0f)
                state.SecessionPressure = MathF.Max(0f, state.SecessionPressure - 0.020f);
        }

        // Gradual decay of temporary liberty modifiers toward 0
        if (MathF.Abs(subject.SubjectLibertyModifier) > 0.001f)
        {
            if (subject.SubjectLibertyModifier > 0f)
                subject.SubjectLibertyModifier = MathF.Max(0f, subject.SubjectLibertyModifier - 0.005f);
            else
                subject.SubjectLibertyModifier = MathF.Min(0f, subject.SubjectLibertyModifier + 0.005f);
        }

        // Disgruntled subjects of the same overlord form a secret liberty conspiracy ("сговор")
        if (subject.LibertyDesire >= campaign.Defines.SubjectConspiracyThreshold)
            TrySpreadConspiracy(campaign, overlord, subject);

        // AI overlord begins integrating released or eligible subjects once stable and their regions are pacified
        if (!overlord.IsHuman && !subject.SubjectIntegrating && WantsToIntegrateSubject(campaign, overlord, subject))
            Execute(campaign, overlord, subject, SubjectInteractionKind.ToggleIntegration, out _);
        if (!subject.IsSubject) return;

        // Integration progress when active
        if (subject.SubjectIntegrating)
        {
            if (subject.LibertyDesire > 0.65f || overlord.Treasury < 0f)
            {
                // Integration stalls when the subject is unruly or the overlord is broke
            }
            else
            {
                var sizeFactor = M.Clamp(1f / MathF.Max(0.25f, 1f + subject.Population / (float)Math.Max(1L, overlord.Population) * 4f), 0.25f, 1.2f);
                var weeklyGain = campaign.Defines.SubjectIntegrationDailyBase * 7f * sizeFactor;
                var weeklyCost = MathF.Max(0.01f, subject.Gdp * campaign.Defines.SubjectIntegrationTreasuryWeeklyShare);
                { var ledgerBefore = overlord.Treasury; overlord.Treasury -= weeklyCost; MoneyLedger.Book(overlord, MoneyFlow.Tribute, overlord.Treasury - ledgerBefore); }
                subject.SubjectIntegrationProgress = M.Clamp01(subject.SubjectIntegrationProgress + weeklyGain);
                var wasReleased = subject.ReleasedFromId == overlord.Id || subject.SecededFromId == overlord.Id;
                var popLimit = wasReleased ? overlord.Population * 0.65f : overlord.Population * 0.45f;
                // Finished integration ends in annexation for every overlord. A player used to have
                // to come back and press "Complete annexation", and in the weeks between a hundred
                // per cent and the click the subject could walk out. A computer overlord still
                // weighs the size of what it takes in; a player chose to integrate and is not asked
                // twice. The annexation's own checks apply to both. Its revolt does not start itself
                // for a player: a subject that would answer annexation with a war waits for the
                // player's click (it cannot drift away meanwhile - SubjectCore binds it at 100%).
                var wouldRevolt = subject.LibertyDesire >= campaign.Defines.SubjectAnnexationRevoltThreshold ||
                                  (subject.SubjectConspiring && subject.LibertyDesire >= 0.45f);
                if (subject.SubjectIntegrationProgress >= 1f &&
                    (overlord.IsHuman
                        ? !wouldRevolt
                        : subject.Population <= popLimit && PeaceShape.Near(campaign, overlord, subject)) &&
                    CanInteract(campaign, overlord, subject, SubjectInteractionKind.CompleteAnnexation, out _))
                {
                    Execute(campaign, overlord, subject, SubjectInteractionKind.CompleteAnnexation, out _);
                    return;
                }
            }
        }

        // Disloyal subject hostile actions against overlord (sabotage, withholding, armed rebellion)
        if (subject.LibertyDesire >= campaign.Defines.SubjectSabotageThreshold &&
            campaign.ElapsedDays - subject.SubjectSinceDay > 180)
        {
            var roll = Escalation.Roll(campaign.Seed, subject.Id, 0x5B7, campaign.ElapsedDays, 0x4);
            var isLargeFedVassal = campaign.StatesOf(subject).Length >= 2 || subject.Population >= overlord.Population * 0.18f;
            var rebelThreshold = isLargeFedVassal
                ? MathF.Min(campaign.Defines.SubjectRebellionThreshold, 0.75f)
                : campaign.Defines.SubjectRebellionThreshold;
            var rebelChance = (subject.SubjectConspiring ? 0.065f : 0.035f) + (isLargeFedVassal ? 0.025f : 0f);
            if (subject.LibertyDesire >= rebelThreshold && roll < rebelChance)
            {
                TriggerSubjectRebellion(campaign, overlord, subject);
                return;
            }
            if (roll < 0.06f)
            {
                // Sabotage / tribute strike
                subject.SubjectTributeHolidayUntilDay = Math.Max(subject.SubjectTributeHolidayUntilDay, campaign.ElapsedDays + 90);
                overlord.Stability = M.Clamp01(overlord.Stability - 0.008f);
                var rel = CampaignDiplomacy.Get(campaign, overlord.Id, subject.Id);
                OpinionSystem.Remember(campaign, rel, "denounced_us");
            }
        }
    }

    /// <summary>AI rivals and hostile powers periodically sponsor independence agitation inside their enemies' subjects.</summary>
    public static void TickAiForeignAgitation(Campaign campaign)
    {
        foreach (var _ in TickAiForeignAgitationStages(campaign)) { }
    }

    public static IEnumerable<int> TickAiForeignAgitationStages(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays <= 60 || !campaign.FeatureEnabled("subjects")) yield break;
        var subjects = new List<Nation>();
        foreach (var n in campaign.Nations)
            if (n.IsSubject) subjects.Add(n);
        if (subjects.Count == 0) yield break;
        var checkedAll = 0;
        foreach (var sponsor in campaign.Nations)
        {
            if (++checkedAll % 32 == 0 || Campaign.StageOverdue()) yield return 0;
            if (sponsor.IsHuman || sponsor.IsSubject || sponsor.Movement is not null || sponsor.Population < 10_000_000 || sponsor.Treasury < 0.8f)
                continue;
            var roll = Escalation.Roll(campaign.Seed, sponsor.Id, 0x7A3, campaign.ElapsedDays, 0x2);
            if (roll >= 0.14f) continue;
            yield return 0;

            Nation bestSubject = null;
            var bestScore = 0f;
            for (var si = 0; si < subjects.Count; si++)
            {
                var subject = subjects[si];
                if (!subject.IsSubject || subject.OverlordId == sponsor.Id) continue;
                var overlord = campaign.NationById(subject.OverlordId);
                if (overlord is null) continue;
                var rel = CampaignDiplomacy.Peek(campaign, sponsor.Id, overlord.Id);
                var rival = rel?.Stance == DiplomaticStance.Rival;
                var atWar = CampaignDiplomacy.AtWar(campaign, sponsor.Id, overlord.Id);
                var hostile = rel is not null && rel.Opinion <= -40f;
                if (!rival && !atWar && !hostile) continue;
                if (!CanInteract(campaign, sponsor, subject, SubjectInteractionKind.AgitateIndependence, out _)) continue;
                var score = subject.LibertyDesire + (subject.SubjectConspiring ? 0.25f : 0f) + (atWar ? 0.35f : rival ? 0.20f : 0.05f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSubject = subject;
                }
            }
            if (bestSubject is not null)
            {
                AgitateSubject(campaign, sponsor, bestSubject, out _);
                yield return 0;
            }
        }
    }

    public static string NextLooserKind(string current)
    {
        var idx = Array.FindIndex(AutonomyLadder, k => string.Equals(k, current, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return "dominion";
        return idx + 1 < AutonomyLadder.Length ? AutonomyLadder[idx + 1] : "";
    }

    public static string NextTighterKind(string current)
    {
        var idx = Array.FindIndex(AutonomyLadder, k => string.Equals(k, current, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return "puppet";
        return idx > 0 ? AutonomyLadder[idx - 1] : "";
    }
}

/// <summary>
/// Releasing a new country from one's own regions, with full customization of regions,
/// status (free or any subject type), government, ideology, declared culture, religion and name.
/// </summary>
public static class CountryRelease
{
    public sealed class ReleaseOptions
    {
        public List<int> StateMapIndices = new();
        public bool AsSubject = true;
        public string SubjectType = "puppet";
        public string Culture = "";
        public string Religion = "";
        public string Government = "";
        public string Ideology = "";
        public string CustomName = "";
        public string RestoredTag = "";
    }

    public static List<ScenarioState> ReleasableStates(Campaign campaign, Nation parent)
    {
        if (campaign is null || parent is null) return new();
        return campaign.StatesOf(parent)
            .Where(s => s.Population > 0 && !LakeRegions.IsWaterState(s) && s.MapIndex != parent.CapitalStateMapIndex)
            .OrderByDescending(s => EconomySystem.ForeignShare(s, parent.Tag))
            .ThenByDescending(s => s.Radicalism)
            .ThenBy(s => s.DisplayName)
            .ToList();
    }

    public static bool CanRelease(Campaign campaign, Nation parent, ReleaseOptions options, out string refusal)
    {
        refusal = "";
        if (campaign?.ProvinceMap is null || parent is null || options is null)
        {
            refusal = Loc.Get("No country or regions selected.");
            return false;
        }
        if (parent.IsSubject)
        {
            refusal = Loc.Get("A subject state cannot release territories of its own.");
            return false;
        }
        var owned = campaign.StatesOf(parent);
        if (owned.Length < 2)
        {
            refusal = Loc.Get("A country with only one region cannot divide itself.");
            return false;
        }
        var indices = options.StateMapIndices;
        var validCount = 0;
        var includesCapital = false;
        var seenFirst = -1;
        HashSet<int> seenMore = null;
        for (var i = 0; i < indices.Count; i++)
        {
            var idx = indices[i];
            var s = campaign.StateByMapIndex(idx);
            if (s is null || s.NationMapIndex != parent.MapIndex || s.Population <= 0 || LakeRegions.IsWaterState(s))
                continue;
            if (idx == seenFirst) continue;
            if (seenFirst < 0)
                seenFirst = idx;
            else
            {
                seenMore ??= new HashSet<int> { seenFirst };
                if (!seenMore.Add(idx)) continue;
            }
            validCount++;
            if (s.MapIndex == parent.CapitalStateMapIndex) includesCapital = true;
        }
        if (validCount == 0)
        {
            refusal = Loc.Get("Select at least one non-capital region to release.");
            return false;
        }
        if (includesCapital)
        {
            refusal = Loc.Get("The national capital region cannot be released.");
            return false;
        }
        if (validCount >= owned.Length)
        {
            refusal = Loc.Get("At least one region must remain with the homeland.");
            return false;
        }
        if (options.AsSubject && !SubjectCatalog.Knows(options.SubjectType))
        {
            refusal = Loc.Format("Unknown dependency type '{0}'.", options.SubjectType);
            return false;
        }
        return true;
    }

    private sealed class ReleaseResult
    {
        public Nation Born;
        public string Feedback = "";
    }

    private static readonly List<Nation> TickNationsScratch = new();

    public static Nation Release(Campaign campaign, Nation parent, ReleaseOptions options, out string feedback)
    {
        var result = new ReleaseResult();
        foreach (var _ in ReleaseStages(campaign, parent, options, result)) { }
        feedback = result.Feedback;
        return result.Born;
    }

    [ThreadStatic] private static List<ScenarioState> _releaseStates;
    [ThreadStatic] private static HashSet<ushort> _releaseStateSet;
    [ThreadStatic] private static Dictionary<string, long> _releaseTally;

    private static IEnumerable<int> ReleaseStages(Campaign campaign, Nation parent, ReleaseOptions options, ReleaseResult result)
    {
        if (!CanRelease(campaign, parent, options, out var invalid))
        {
            result.Feedback = invalid;
            yield break;
        }
        var topology = campaign.ProvinceMap;
        // Thirteen release evaluations a day used to allocate a list, a set and two tally
        // dictionaries each: forty-odd kilobytes a call, six and a half megabytes over twelve
        // days. The scratch is thread-static and cleared on entry; one enumeration at a time.
        var states = _releaseStates ??= new List<ScenarioState>();
        states.Clear();
        var stateSetUshort = _releaseStateSet ??= new HashSet<ushort>();
        stateSetUshort.Clear();
        for (var i = 0; i < options.StateMapIndices.Count; i++)
        {
            var idx = options.StateMapIndices[i];
            if (idx <= 0 || idx > ushort.MaxValue) continue;
            var candidate = campaign.StateByMapIndex(idx);
            if (candidate is null || candidate.NationMapIndex != parent.MapIndex ||
                candidate.Population <= 0 || LakeRegions.IsWaterState(candidate) ||
                candidate.MapIndex == parent.CapitalStateMapIndex) continue;
            if (stateSetUshort.Add((ushort)idx))
                states.Add(candidate);
        }
        if (states.Count == 0)
        {
            result.Feedback = Loc.Get("No valid regions selected.");
            yield break;
        }

        // Determine dominant culture & religion across selected states if not explicitly chosen
        var culture = options.Culture;
        if (string.IsNullOrWhiteSpace(culture))
        {
            var tally = _releaseTally ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            tally.Clear();
            for (var si = 0; si < states.Count; si++)
            {
                var groups = states[si].PopulationGroups;
                for (var gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi];
                    tally[g.Culture] = tally.GetValueOrDefault(g.Culture) + g.Size;
                }
            }
            if (tally.Count == 0)
                culture = DemographyCatalog.PrimaryCulture(parent.Tag);
            else
            {
                var bestKey = "";
                var bestCount = long.MinValue;
                foreach (var pair in tally)
                {
                    if (pair.Value > bestCount) { bestCount = pair.Value; bestKey = pair.Key; }
                }
                culture = bestKey;
            }
        }

        var religion = options.Religion;
        if (string.IsNullOrWhiteSpace(religion))
        {
            var tally = _releaseTally ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            tally.Clear();
            for (var si = 0; si < states.Count; si++)
            {
                var groups = states[si].PopulationGroups;
                for (var gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi];
                    tally[g.Religion] = tally.GetValueOrDefault(g.Religion) + g.Size;
                }
            }
            if (tally.Count == 0)
                religion = parent.DeclaredReligion;
            else
            {
                var bestKey = "";
                var bestCount = long.MinValue;
                foreach (var pair in tally)
                {
                    if (pair.Value > bestCount) { bestCount = pair.Value; bestKey = pair.Key; }
                }
                religion = bestKey;
            }
        }

        // Check if a vanished historical tag matches the released ground or was explicitly requested
        Nation born = null;
        var restoredTag = options.RestoredTag;
        using (SimProfiler.Measure("      sub:rel:vanish"))
        if (string.IsNullOrWhiteSpace(restoredTag))
        {
            for (var ni = 0; ni < campaign.Nations.Count; ni++)
            {
                var vanished = campaign.Nations[ni];
                if (!FormableRestorations.Vanished(campaign, vanished)) continue;
                var authored = FormableRestorations.Authored(campaign, vanished.Tag);
                if (authored.Length == 0) continue;
                var matched = false;
                for (var ai = 0; ai < authored.Length; ai++)
                {
                    var p = authored[ai];
                    if (p > 0 && p < topology.ProvinceToState.Length && stateSetUshort.Contains(topology.ProvinceToState[p]))
                    {
                        matched = true;
                        break;
                    }
                }
                if (matched)
                {
                    restoredTag = vanished.Tag;
                    break;
                }
            }
        }
        yield return 0;

        double rawParentPop = 0d;
        var parentStates = campaign.StatesOf(parent);
        for (var si = 0; si < parentStates.Length; si++) rawParentPop += parentStates[si].Population;
        var totalParentPop = Math.Max(1d, rawParentPop);
        long releasedPop = 0;
        var seat = states[0];
        for (var si = 0; si < states.Count; si++)
        {
            var st = states[si];
            releasedPop += st.Population;
            if (st.Population > seat.Population) seat = st;
        }
        var share = (float)Math.Clamp(releasedPop / totalParentPop, 0.01d, 0.90d);

        var provinces = new List<int>();
        for (var p = 1; p <= topology.ProvinceCount; p++)
            if (topology.ProvinceToNation[p] == parent.MapIndex && stateSetUshort.Contains(topology.ProvinceToState[p]))
                provinces.Add(p);

        if (provinces.Count == 0)
        {
            result.Feedback = Loc.Get("Selected regions contain no provinces.");
            yield break;
        }
        yield return 0;

        using (SimProfiler.Measure("      sub:rel:found"))
        if (!string.IsNullOrWhiteSpace(restoredTag) && campaign.NationByTag(restoredTag) is { } existingVanished && !campaign.HasOwnedLand(existingVanished))
        {
            born = existingVanished;
            campaign.Fallen.Remove(born.Id);
            born.Movement = null;
        }
        else
        {
            var pieces = new List<SecessionPiece>(states.Count);
            for (var si = 0; si < states.Count; si++)
            {
                var s = states[si];
                pieces.Add(new SecessionPiece
                {
                    MapIndex = s.MapIndex, Name = s.Name, Population = s.Population, Cause = SecessionCause.Nationality
                });
            }
            born = SecessionEvents.FoundReleased(campaign, parent, pieces, culture, releasedPop);
        }

        if (born is null)
        {
            result.Feedback = Loc.Get("Could not allocate a new country tag.");
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(options.CustomName))
            born.CustomName = options.CustomName.Trim();
        born.Government = GovernmentCatalog.Knows(options.Government) ? options.Government : parent.Government;
        born.Ideology = IdeologyCatalog.Knows(options.Ideology) ? options.Ideology : parent.Ideology;
        born.StartGovernment = born.Government;
        born.StartIdeology = born.Ideology;
        // A reactivated historical tag is a newly established administration, not the
        // defeated government's decades-old suspended ballot or pending successor.
        born.Elections = null;
        born.NextElectionDay = -1;
        born.DeclaredCulture = culture ?? "";
        born.DeclaredReligion = religion ?? "";
        born.IdentityChangedDay = campaign.ElapsedDays;
        born.SecededFromId = parent.Id;
        born.ReleasedFromId = parent.Id;
        // A country released out of another is still born of it: the permanent record is what
        // keeps a later war over the same ground from being read as a war between strangers.
        if (born.RevoltParentId < 0) born.RevoltParentId = parent.Id;

        if (!string.IsNullOrWhiteSpace(culture))
        {
            var hasCulture = false;
            for (var fi = 0; fi < born.FoundedCultures.Count; fi++)
                if (string.Equals(born.FoundedCultures[fi], culture, StringComparison.OrdinalIgnoreCase)) { hasCulture = true; break; }
            if (!hasCulture) born.FoundedCultures.Insert(0, culture);
            DemographyCatalog.Declare(born.Tag, born.FoundedCultures);
        }

        RegionalResourcePartition.Transfer(parent, born, share);
        using (SimProfiler.Measure("      sub:rel:annex"))
            StatePartition.Annex(campaign, provinces, born, asConquest: false);
        RegionalResourcePartition.RecountResidents(campaign, parent, born);
        CampaignDiplomacy.ForgetBorders();
        var bornStates = campaign.StatesOf(born);
        SecessionResearch.Inherit(campaign, parent, born, bornStates, (long)totalParentPop);
        var homelands = new List<int>(bornStates.Length);
        for (var si = 0; si < bornStates.Length; si++) homelands.Add(bornStates[si].MapIndex);
        campaign.Homelands[born.Id] = homelands;
        yield return 0;

        using (SimProfiler.Measure("      sub:rel:capitals"))
            campaign.LocateCapitals(preserveExisting: true);
        yield return 0;

        born.Stability = 0.58f;
        born.WarSupport = 0.50f;
        born.StabilityBias = 0f;
        born.StabilityBias = born.Stability - StabilityCore.Target(campaign, born);

        var provinceSet = new HashSet<int>(provinces.Count);
        for (var pi = 0; pi < provinces.Count; pi++) provinceSet.Add(provinces[pi]);
        using (SimProfiler.Measure("      sub:rel:defect"))
            Reintegration.Defect(campaign, parent, born, provinceSet,
                Math.Max(0.25f, campaign.Defines.PeacefulSecessionDefectionShare));

        if (options.AsSubject)
        {
            var kind = SubjectCatalog.Knows(options.SubjectType) ? options.SubjectType : "puppet";
            SubjectSystem.Subjugate(campaign, parent, born, kind, out _);
            var rel = CampaignDiplomacy.Get(campaign, born.Id, parent.Id);
            OpinionSystem.Forget(campaign, rel, "made_us_a_subject");
            OpinionSystem.Remember(campaign, rel, "let_us_go");
        }
        else
        {
            var rel = CampaignDiplomacy.Get(campaign, born.Id, parent.Id);
            OpinionSystem.Remember(campaign, rel, "let_us_go");
        }

        Rulers.ReplaceHeadOfState(campaign, born, "");
        yield return 0;

        var stateIndices = new int[states.Count];
        for (var si = 0; si < states.Count; si++) stateIndices[si] = states[si].MapIndex;
        using (SimProfiler.Measure("      sub:rel:name"))
            NationNaming.ApplyTo(campaign, born, parent, stateIndices,
                string.IsNullOrWhiteSpace(restoredTag) ? "auto" : "historic", culture);
        NationalModifiers.Invalidate(parent);
        NationalModifiers.Invalidate(born);
        campaign.Chronicle.Record(campaign, "country_released", born.Id, parent.Id, place: seat.Name);
        using (SimProfiler.Measure("      sub:rel:map"))
            campaign.MarkMapChanged();
        campaign.MarkStrategicChange();
        result.Feedback = options.AsSubject
            ? Loc.Format("Released {0} as a {1}.", born.DisplayName, Loc.Get(SubjectCatalog.Of(born.SubjectType)?.Name ?? born.SubjectType))
            : Loc.Format("Released {0} as an independent state.", born.DisplayName);
        result.Born = born;
    }

    /// <summary>
    /// AI countries periodically delegate unruly, culturally foreign peripheral regions to autonomous satellites
    /// when suffering from imperial overstretch or low stability.
    /// </summary>
    public static void TickAi(Campaign campaign)
    {
        foreach (var _ in TickAiStages(campaign)) { }
    }

    public static IEnumerable<int> TickAiStages(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays <= 60 || campaign.ElapsedDays % 30 != 23) yield break;
        if (!campaign.FeatureEnabled("subjects")) yield break;
        using (var agitation = SubjectInteractions.TickAiForeignAgitationStages(campaign).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("    sub:agitation")) more = agitation.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        yield return 0;
        using (var blocs = SubjectBlocs.TickAiStages(campaign).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("    sub:blocs")) more = blocs.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        yield return 0;
        if (!campaign.FeatureEnabled("secession")) yield break;

        var liveWars = Warfare.LiveWars(campaign);
        TickNationsScratch.Clear();
        for (var i = 0; i < campaign.Nations.Count; i++)
            TickNationsScratch.Add(campaign.Nations[i]);
        try
        {
            for (var ni = 0; ni < TickNationsScratch.Count; ni++)
            {
                if (ni > 0 && (ni % 32 == 0 || Campaign.StageOverdue())) yield return 0;
                var nation = TickNationsScratch[ni];
                // A human overlord who holds the colonial office is run on the same review as
                // every other overlord; one who keeps it hands-on is skipped as before.
                if ((nation.IsHuman && !Delegation.Holds(nation, Delegation.Subjects)) ||
                    nation.IsSubject || nation.Movement is not null || nation.Population < 5_000_000) continue;
                var strained = nation.ImperialOverstretch >= campaign.Defines.AiReleaseOverstretchThreshold ||
                    nation.Stability <= 0.42f ||
                    (nation.Government is "military_junta" or "one_party_state" or "absolute_monarchy" or "theocracy" &&
                     nation.Stability < 0.55f);
                if (!strained) continue;
                yield return 0;
                var atWar = false;
                for (var wi = 0; wi < liveWars.Count; wi++)
                {
                    var w = liveWars[wi];
                    if (!w.Ended && w.Involves(nation.Id)) { atWar = true; break; }
                }
                if (atWar) continue;
                if (campaign.ElapsedDays - nation.LastSecessionDay < 365) continue;

                var ownedCount = 0;
                ScenarioState candidate = null;
                var candidateHeat = float.NegativeInfinity;
                var allStates = campaign.States;
                for (var si = 0; si < allStates.Count; si++)
                {
                    var s = allStates[si];
                    if (s.NationMapIndex != nation.MapIndex) continue;
                    ownedCount++;
                    if (s.MapIndex == nation.CapitalStateMapIndex || s.Population < 500_000 ||
                        s.Population >= nation.Population * 0.30f ||
                        EconomySystem.ForeignShare(s, nation.Tag) < 0.65f ||
                        (s.Radicalism < campaign.Defines.AiReleaseRegionHeatThreshold && s.SecessionPressure < 0.65f)) continue;
                    // Not ground it fought a war to take back from rebels in the last two years
                    // (RevoltRecovery): Lipljan, crushed on day 1401 of a measured Yugoslavia, was
                    // released to a reborn Kosovo on day 2003, and the region was counted rising again.
                    if (RevoltRecovery.DaysLeft(campaign, nation, s) > 0) continue;
                    var heat = s.SecessionPressure + s.Radicalism;
                    if (candidate is null || heat > candidateHeat)
                    {
                        candidate = s;
                        candidateHeat = heat;
                    }
                }
                if (ownedCount < 5 || candidate is null) continue;

                var roll = Escalation.Roll(campaign.Seed, nation.Id, 0x8E1, campaign.ElapsedDays, candidate.MapIndex);
                if (roll >= 0.18f) continue;

                var cultures = EconomySystem.Cultures(candidate);
                var topCulture = cultures.Count > 0 ? cultures[0].Culture : "";
                Nation existing;
                int[] package;
                using (SimProfiler.Measure("    sub:rel_match"))
                    existing = RegionalAdministration.MatchingSubject(campaign, nation, candidate, out package);
                if (existing is not null)
                {
                    yield return 0;
                    int[] grant;
                    using (SimProfiler.Measure("    sub:rel_grant_pkg"))
                        grant = RegionalAdministration.Package(campaign, nation, candidate, existing);
                    yield return 0;
                    using (SimProfiler.Measure("    sub:rel_grant_apply"))
                        if (RegionalAdministration.MayGrant(campaign, nation, existing, grant, out _, out _))
                        {
                            new GrantSubjectRegionsCommand { IssuerId = nation.Id, SubjectId = existing.Id,
                                StateMapIndices = grant }.Apply(campaign);
                            nation.LastSecessionDay = campaign.ElapsedDays;
                        }
                    yield return 0;
                    // Do not create a competing micro-vassal because an existing suitable
                    // administration is temporarily reorganising or the budget is short.
                    continue;
                }
                if (package.Length == 0) continue;
                yield return 0;
                var opts = new ReleaseOptions
                {
                    StateMapIndices = package.ToList(),
                    // Severe strain in a low-authority system can favour negotiated independence.
                    AsSubject = !(nation.Stability < .35f && PoliticsCore.IdeologyOf(nation).Authority < .25f &&
                        Escalation.Roll(campaign.Seed, nation.Id, 0x8E2, campaign.ElapsedDays, candidate.MapIndex) < .25f),
                    SubjectType = RegionalAdministration.SubjectTypeFor(nation, candidate, package.Length),
                    Culture = topCulture,
                    Government = nation.Government,
                    Ideology = nation.Ideology
                };
                var relResult = new ReleaseResult();
                foreach (var _ in ReleaseStages(campaign, nation, opts, relResult))
                    yield return 0;
                if (relResult.Born is not null)
                    nation.LastSecessionDay = campaign.ElapsedDays;
                yield return 0;
            }
        }
        finally
        {
            TickNationsScratch.Clear();
        }
    }
}

public sealed class SubjectInteractionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int SubjectId { get; init; }
    public SubjectInteractionKind Kind { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var overlord = campaign?.NationById(IssuerId);
        var subject = campaign?.NationById(SubjectId);
        if (overlord is null || subject is null) return;
        SubjectInteractions.Execute(campaign, overlord, subject, Kind, out _);
    }
}

public sealed class ReleaseCountryCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int[] StateMapIndices { get; init; } = Array.Empty<int>();
    public bool AsSubject { get; init; } = true;
    public string SubjectType { get; init; } = "puppet";
    public string Culture { get; init; } = "";
    public string Religion { get; init; } = "";
    public string Government { get; init; } = "";
    public string Ideology { get; init; } = "";
    public string CustomName { get; init; } = "";
    public string RestoredTag { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var parent = campaign?.NationById(IssuerId);
        if (parent is null) return;
        var opts = new CountryRelease.ReleaseOptions
        {
            StateMapIndices = StateMapIndices?.ToList() ?? new List<int>(),
            AsSubject = AsSubject,
            SubjectType = SubjectType ?? "puppet",
            Culture = Culture ?? "",
            Religion = Religion ?? "",
            Government = Government ?? "",
            Ideology = Ideology ?? "",
            CustomName = CustomName ?? "",
            RestoredTag = RestoredTag ?? ""
        };
        CountryRelease.Release(campaign, parent, opts, out _);
    }
}
