using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Treasury a cultural, research, attaché, exercise, consular or staff-talks offer costs the asker when it is accepted.</summary>
    public float DiplomaticActCost { get; set; } = 0.35f;

    /// <summary>Cash an accepted subsidy moves from the giver to the receiver. Nothing moves before the signature.</summary>
    public float DiplomaticSubsidy { get; set; } = 1.5f;

    /// <summary>How willing the asked country must be, in the same units as an agreement, before a computer signs.</summary>
    public float DiplomaticActWillingness { get; set; } = 0.2f;

    /// <summary>Days before the same act may be asked of the same country again.</summary>
    public int DiplomaticActCooldownDays { get; set; } = 120;

    /// <summary>How long a paper sits on a human table before it lapses, unsigned.</summary>
    public int DiplomaticActReplyDays { get; set; } = 30;

    /// <summary>Army experience both sides gain from an accepted joint exercise.</summary>
    public float DiplomaticActExerciseExperience { get; set; } = 4f;

    /// <summary>How long a cooperation spirit lasts after it is signed.</summary>
    public int DiplomaticActDays { get; set; } = 365;
}

public enum DiplomaticAct
{
    CulturalCooperation,
    ResearchCooperation,
    MilitaryAttache,
    JointExercise,
    LiftEmbargo,
    Subsidy,
    StaffTalks,
    ConsularConvention,
    // Appended, never inserted: the kind is saved as its number, so an older save's
    // ConsularConvention has to keep meaning ConsularConvention.
    StateVisit,
    PortVisit,
    StudentExchange,
    IntelligenceSharing,
    BorderCommission,
    DisasterRelief,
    TechnologyLicence,
    AirspaceCooperation,
    JointNavalPatrol
}

public enum DiplomaticActStep
{
    Propose,
    Accept,
    Decline
}

public sealed partial class DiplomaticRelation
{
    /// <summary>Who has a cooperation paper waiting, or -1. A human answers it; nothing is signed until they do.</summary>
    public int DiplomaticActBy = -1;
    public int DiplomaticActDay = -9999;
    public int DiplomaticActKind = -1;
}

/// <summary>
/// Further bilateral acts on the diplomacy screen the agreements already use.
///
/// Each one is a real command: a price, a refusal, a cooldown, and the same door for the
/// player and the computer. A human who is asked gets a paper in the existing inbox.
/// Land is never on the paper. Cash moves only when the asked side signs a subsidy.
/// </summary>
public static class DiplomaticActs
{
    public static readonly DiplomaticAct[] All =
    {
        DiplomaticAct.CulturalCooperation,
        DiplomaticAct.ResearchCooperation,
        DiplomaticAct.MilitaryAttache,
        DiplomaticAct.JointExercise,
        DiplomaticAct.LiftEmbargo,
        DiplomaticAct.Subsidy,
        DiplomaticAct.StaffTalks,
        DiplomaticAct.ConsularConvention,
        DiplomaticAct.StateVisit,
        DiplomaticAct.PortVisit,
        DiplomaticAct.StudentExchange,
        DiplomaticAct.IntelligenceSharing,
        DiplomaticAct.BorderCommission,
        DiplomaticAct.DisasterRelief,
        DiplomaticAct.TechnologyLicence,
        DiplomaticAct.AirspaceCooperation,
        DiplomaticAct.JointNavalPatrol
    };

    /// <summary>
    /// The acts that are worth a button on the map rather than only in the diplomacy screen:
    /// the ones a government reaches for in the middle of something else.
    /// </summary>
    public static readonly DiplomaticAct[] Quick =
    {
        DiplomaticAct.StateVisit,
        DiplomaticAct.JointExercise,
        DiplomaticAct.PortVisit,
        DiplomaticAct.DisasterRelief
    };

    public static string Name(DiplomaticAct act) => act switch
    {
        DiplomaticAct.CulturalCooperation => "Cultural cooperation",
        DiplomaticAct.ResearchCooperation => "Research cooperation",
        DiplomaticAct.MilitaryAttache => "Military attaché",
        DiplomaticAct.JointExercise => "Joint exercise",
        DiplomaticAct.LiftEmbargo => "Ask them to lift the embargo",
        DiplomaticAct.Subsidy => "Offer a subsidy",
        DiplomaticAct.StaffTalks => "Staff talks",
        DiplomaticAct.ConsularConvention => "Consular convention",
        DiplomaticAct.StateVisit => "State visit",
        DiplomaticAct.PortVisit => "Naval port visit",
        DiplomaticAct.StudentExchange => "Student exchange",
        DiplomaticAct.IntelligenceSharing => "Share intelligence",
        DiplomaticAct.BorderCommission => "Border commission",
        DiplomaticAct.DisasterRelief => "Send disaster relief",
        DiplomaticAct.TechnologyLicence => "Licence a technology",
        DiplomaticAct.AirspaceCooperation => "Airspace cooperation",
        DiplomaticAct.JointNavalPatrol => "Joint naval patrol",
        _ => "Diplomatic act"
    };

    /// <summary>What must be true of their opinion of us before they will even receive it.</summary>
    public static float MinimumOpinion(DiplomaticAct act) =>
        DealOrForce.Rules.DiplomaticActRules.MinimumOpinion((int)act);

    public static bool Pending(Campaign campaign, DiplomaticRelation relation) =>
        campaign is not null && relation is not null && relation.DiplomaticActBy >= 0 &&
        campaign.ElapsedDays - relation.DiplomaticActDay < Math.Max(1, campaign.Defines.DiplomaticActReplyDays);

    public static int Incoming(Campaign campaign, int nationId)
    {
        if (campaign is null) return 0;
        var count = 0;
        foreach (var relation in campaign.Relations.Values)
            if ((relation.A == nationId || relation.B == nationId) && Pending(campaign, relation) &&
                relation.DiplomaticActBy != nationId) count++;
        return count;
    }

    public static string Refusal(Campaign campaign, Nation asking, Nation asked, DiplomaticAct act)
    {
        var validActors = campaign is not null && asking is not null && asked is not null && asking != asked &&
            asking.Population > 0 && asked.Population > 0 && asking.Movement is null && asked.Movement is null;
        if (!validActors) return Loc.Get("Invalid target.");
        var relation = CampaignDiplomacy.Peek(campaign, asking.Id, asked.Id);
        var key = CooldownKey(act, asked.Id);
        var price = Price(campaign, act);
        var code = DealOrForce.Rules.DiplomaticActRules.EvaluateRefusal(
            true,
            relation?.Stance == DiplomaticStance.AtWar,
            Pending(campaign, relation),
            asking.DecisionAvailableDay.GetValueOrDefault(key),
            campaign.ElapsedDays,
            (int)act,
            relation?.EmbargoBy(asked.Id) == true,
            relation?.TalksUntilDay ?? -9999,
            relation?.Opinion ?? 0f,
            asking.Treasury,
            price);
        return code switch
        {
            DealOrForce.Rules.DiplomaticActRefusal.InvalidTarget => Loc.Get("Invalid target."),
            DealOrForce.Rules.DiplomaticActRefusal.AtWar => Loc.Get("Already at war."),
            DealOrForce.Rules.DiplomaticActRefusal.PendingOffer => Loc.Get("They have not answered the last offer yet."),
            DealOrForce.Rules.DiplomaticActRefusal.Cooldown => Loc.Get("Wait before asking again."),
            DealOrForce.Rules.DiplomaticActRefusal.NotEmbargoing => Loc.Get("They are not embargoing you."),
            DealOrForce.Rules.DiplomaticActRefusal.TalksAlreadyActive => Loc.Get("Talks are already under way."),
            DealOrForce.Rules.DiplomaticActRefusal.OpinionTooLow => Loc.Get("They will not receive it: their opinion of us is too low."),
            DealOrForce.Rules.DiplomaticActRefusal.InsufficientTreasury => Loc.Get("The treasury cannot afford it."),
            _ => ""
        };
    }

    public static float Willingness(Campaign campaign, Nation asking, Nation asked, DiplomaticAct act)
    {
        var relation = CampaignDiplomacy.Peek(campaign, asking.Id, asked.Id);
        var stance = relation?.Stance switch
        {
            DiplomaticStance.Friendly => 1,
            DiplomaticStance.Allied => 2,
            DiplomaticStance.Rival => 3,
            DiplomaticStance.AtWar => 4,
            _ => 0
        };
        return DealOrForce.Rules.DiplomaticActRules.Willingness(relation?.Opinion ?? 0f, stance, (int)act);
    }

    /// <summary>
    /// One computer move, using the same refusal a player would hear. Returns false, and
    /// changes nothing, when every act is gated off.
    /// </summary>
    public static bool TryAi(Campaign campaign, Nation asking, Nation asked)
    {
        if (campaign is null || asking is null || asked is null || asking.Id == asked.Id) return false;
        if (asking.IsHuman) return false;
        foreach (var act in All)
        {
            if (Refusal(campaign, asking, asked, act).Length > 0) continue;
            if (Willingness(campaign, asking, asked, act) + 0.001f < campaign.Defines.DiplomaticActWillingness) continue;
            var command = new DiplomaticActCommand
            {
                IssuerId = asking.Id, TargetId = asked.Id, Act = act, Step = DiplomaticActStep.Propose
            };
            command.Apply(campaign);
            if (command.Result.Length > 0 && !command.Result.StartsWith("Refused", StringComparison.Ordinal))
            {
                AiStatecraft.Tally["diplomatic_act"] = AiStatecraft.Tally.GetValueOrDefault("diplomatic_act") + 1;
                return true;
            }
        }
        return false;
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same daily sweep, yielded between relations so a full ledger of pending
    /// acts does not clear in one frame.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null || !campaign.StepOpensNewDay) yield break;
        var scanned = 0;
        foreach (var relation in campaign.Relations.Values)
        {
            if (++scanned % 512 == 0) yield return 0;
            if (relation.DiplomaticActBy < 0 || Pending(campaign, relation)) continue;
            relation.DiplomaticActBy = -1;
            relation.DiplomaticActKind = -1;
            campaign.DealInboxRevision++;
        }
    }

    /// <summary>What this act costs the asking treasury. Public because the diplomacy list shows
    /// the price before it is asked, and a price nobody can see is not a choice.</summary>
    public static float Cost(Campaign campaign, DiplomaticAct act) => Price(campaign, act);

    private static float Price(Campaign campaign, DiplomaticAct act) =>
        DealOrForce.Rules.DiplomaticActRules.Price(
            (int)act, campaign.Defines.DiplomaticActCost, campaign.Defines.DiplomaticSubsidy);

    private static string CooldownKey(DiplomaticAct act, int targetId) => "diplomatic_act/" + act + "/" + targetId;

    internal static bool Accept(Campaign campaign, DiplomaticRelation relation, Nation asking, Nation asked, DiplomaticAct act)
    {
        var price = Price(campaign, act);
        if (asking.Treasury + 0.001f < price) return false;
        asking.Treasury -= price;
        switch (act)
        {
            case DiplomaticAct.CulturalCooperation:
                ModifierSystem.Grant(campaign, asking, "cultural_cooperation", campaign.Defines.DiplomaticActDays);
                ModifierSystem.Grant(campaign, asked, "cultural_cooperation", campaign.Defines.DiplomaticActDays);
                OpinionSystem.Remember(campaign, relation, "cultural_cooperation");
                break;
            case DiplomaticAct.ResearchCooperation:
                ModifierSystem.Grant(campaign, asking, "research_cooperation", campaign.Defines.DiplomaticActDays);
                ModifierSystem.Grant(campaign, asked, "research_cooperation", campaign.Defines.DiplomaticActDays);
                OpinionSystem.Remember(campaign, relation, "research_cooperation");
                break;
            case DiplomaticAct.MilitaryAttache:
                ModifierSystem.Grant(campaign, asking, "military_attache", campaign.Defines.DiplomaticActDays);
                ModifierSystem.Grant(campaign, asked, "military_attache", campaign.Defines.DiplomaticActDays);
                OpinionSystem.Remember(campaign, relation, "military_attache");
                break;
            case DiplomaticAct.JointExercise:
                asking.ArmyExperience += campaign.Defines.DiplomaticActExerciseExperience;
                asked.ArmyExperience += campaign.Defines.DiplomaticActExerciseExperience;
                OpinionSystem.Remember(campaign, relation, "joint_exercise");
                break;
            case DiplomaticAct.LiftEmbargo:
                if (asked.Id == relation.A) relation.EmbargoByA = false;
                else relation.EmbargoByB = false;
                OpinionSystem.Remember(campaign, relation, "embargo_lifted");
                break;
            case DiplomaticAct.Subsidy:
                asked.Treasury += price;
                OpinionSystem.Remember(campaign, relation, "subsidy_paid");
                break;
            case DiplomaticAct.StaffTalks:
                relation.TalksUntilDay = campaign.ElapsedDays + Math.Max(14, campaign.Defines.TalksDays);
                OpinionSystem.Remember(campaign, relation, "staff_talks");
                break;
            case DiplomaticAct.ConsularConvention:
                ModifierSystem.Grant(campaign, asking, "consular_convention", campaign.Defines.DiplomaticActDays);
                ModifierSystem.Grant(campaign, asked, "consular_convention", campaign.Defines.DiplomaticActDays);
                OpinionSystem.Remember(campaign, relation, "consular_convention");
                break;
            case DiplomaticAct.StateVisit:
                // The one act whose whole point is the photograph: a standing reason on both
                // sides, and the government's own standing a little firmer at home for it.
                asking.Stability = Math.Clamp(asking.Stability + 0.004f, 0f, 1f);
                asked.Stability = Math.Clamp(asked.Stability + 0.002f, 0f, 1f);
                OpinionSystem.Remember(campaign, relation, "state_visit");
                break;
            case DiplomaticAct.PortVisit:
                asking.NavalExperience += campaign.Defines.DiplomaticActExerciseExperience * 0.5f;
                asked.NavalExperience += campaign.Defines.DiplomaticActExerciseExperience * 0.5f;
                OpinionSystem.Remember(campaign, relation, "port_visit");
                break;
            case DiplomaticAct.StudentExchange:
                ModifierSystem.Grant(campaign, asking, "student_exchange", campaign.Defines.DiplomaticActDays);
                ModifierSystem.Grant(campaign, asked, "student_exchange", campaign.Defines.DiplomaticActDays);
                OpinionSystem.Remember(campaign, relation, "student_exchange");
                break;
            case DiplomaticAct.IntelligenceSharing:
                ModifierSystem.Grant(campaign, asking, "intelligence_sharing", campaign.Defines.DiplomaticActDays);
                ModifierSystem.Grant(campaign, asked, "intelligence_sharing", campaign.Defines.DiplomaticActDays);
                OpinionSystem.Remember(campaign, relation, "intelligence_sharing");
                break;
            case DiplomaticAct.BorderCommission:
                ModifierSystem.Grant(campaign, asking, "border_commission", campaign.Defines.DiplomaticActDays);
                ModifierSystem.Grant(campaign, asked, "border_commission", campaign.Defines.DiplomaticActDays);
                OpinionSystem.Remember(campaign, relation, "border_commission");
                break;
            case DiplomaticAct.DisasterRelief:
                // Money that leaves the sender and arrives: the asking side pays the price as
                // for every other act, and the asked side receives it, which is the whole point.
                asked.Treasury += price;
                asked.Stability = Math.Clamp(asked.Stability + 0.01f, 0f, 1f);
                OpinionSystem.Remember(campaign, relation, "disaster_relief");
                break;
            case DiplomaticAct.TechnologyLicence:
                // The fee is the act's own price, and it is paid to the country that owns the
                // drawings: nothing is created, and nothing is charged twice.
                asked.Treasury += price;
                ModifierSystem.Grant(campaign, asking, "technology_licence", campaign.Defines.DiplomaticActDays);
                OpinionSystem.Remember(campaign, relation, "technology_licence");
                break;
            case DiplomaticAct.AirspaceCooperation:
                ModifierSystem.Grant(campaign, asking, "airspace_cooperation", campaign.Defines.DiplomaticActDays);
                ModifierSystem.Grant(campaign, asked, "airspace_cooperation", campaign.Defines.DiplomaticActDays);
                OpinionSystem.Remember(campaign, relation, "airspace_cooperation");
                break;
            case DiplomaticAct.JointNavalPatrol:
                asking.NavalExperience += campaign.Defines.DiplomaticActExerciseExperience;
                asked.NavalExperience += campaign.Defines.DiplomaticActExerciseExperience;
                OpinionSystem.Remember(campaign, relation, "joint_naval_patrol");
                break;
        }
        asking.DecisionAvailableDay[CooldownKey(act, asked.Id)] =
            campaign.ElapsedDays + Math.Max(1, campaign.Defines.DiplomaticActCooldownDays);
        relation.LastActionDay = campaign.ElapsedDays;
        campaign.MarkStrategicChange();
        return true;
    }
}

public sealed class DiplomaticActCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public DiplomaticAct Act { get; init; }
    public DiplomaticActStep Step { get; init; }
    public string Result { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var issuer = campaign.NationById(IssuerId);
        var target = campaign.NationById(TargetId);
        if (issuer is null || target is null || issuer == target) { Result = "Invalid target."; return; }
        if (Step == DiplomaticActStep.Propose)
        {
            Result = DiplomaticActs.Refusal(campaign, issuer, target, Act);
            if (Result.Length > 0) return;
            var relation = CampaignDiplomacy.Get(campaign, IssuerId, TargetId);
            if (target.IsHuman)
            {
                relation.DiplomaticActBy = IssuerId;
                relation.DiplomaticActDay = campaign.ElapsedDays;
                relation.DiplomaticActKind = (int)Act;
                campaign.DealInboxRevision++;
                campaign.MarkStrategicChange();
                Result = "Sent for signature.";
                return;
            }
            if (DiplomaticActs.Willingness(campaign, issuer, target, Act) + 0.001f < campaign.Defines.DiplomaticActWillingness)
            {
                issuer.DecisionAvailableDay["diplomatic_act/" + Act + "/" + target.Id] =
                    campaign.ElapsedDays + Math.Max(1, campaign.Defines.DiplomaticActCooldownDays);
                Result = "Refused.";
                return;
            }
            Result = DiplomaticActs.Accept(campaign, relation, issuer, target, Act) ? "Signed." : "Refused.";
            return;
        }

        var paper = CampaignDiplomacy.Peek(campaign, IssuerId, TargetId);
        if (paper is null || !DiplomaticActs.Pending(campaign, paper) || paper.DiplomaticActBy == IssuerId)
        {
            Result = "Nothing is waiting for an answer.";
            return;
        }
        var asker = campaign.NationById(paper.DiplomaticActBy);
        var act = (DiplomaticAct)paper.DiplomaticActKind;
        if (Step == DiplomaticActStep.Decline || asker is null)
        {
            paper.DiplomaticActBy = -1;
            paper.DiplomaticActKind = -1;
            campaign.DealInboxRevision++;
            campaign.MarkStrategicChange();
            Result = "Declined.";
            return;
        }
        if (!DiplomaticActs.Accept(campaign, paper, asker, issuer, act))
        {
            Result = "The treasury cannot afford it.";
            return;
        }
        paper.DiplomaticActBy = -1;
        paper.DiplomaticActKind = -1;
        campaign.DealInboxRevision++;
        Result = "Signed.";
    }
}
