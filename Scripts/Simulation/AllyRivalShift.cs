using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// When false, an existing ally or guarantor is not moved to rivalry by the facts below.
    /// The alliance button already on the diplomacy screen still works.
    /// </summary>
    public bool AllyRivalShift { get; set; } = true;

    /// <summary>Days after a shift, a refusal, or a lapsed letter before the same pair is considered again.</summary>
    public int AllyRivalShiftCooldownDays { get; set; } = 180;

    /// <summary>Days a letter sits with a human before it lapses unsigned. Their alliance does not end on its own.</summary>
    public int AllyRivalShiftPaperDays { get; set; } = 30;
}

public sealed partial class DiplomaticRelation
{
    /// <summary>Who last withdrew a guarantee on this pair, or -1. A repaired guarantee clears it.</summary>
    public int GuaranteeBrokenBy = -1;

    /// <summary>The day this pair last shifted, refused, or let the letter lapse, or -9999.</summary>
    public int RivalShiftDay = -9999;

    /// <summary>
    /// Who raised the rivalry: the computer that sent the letter, or the side named in the
    /// chronicle after it happens. -1 when nobody has. A letter is pending only while
    /// <see cref="RivalShiftPaperDay"/> is set.
    /// </summary>
    public int RivalShiftPaperBy = -1;

    /// <summary>The day the letter was delivered, or -9999 when there is no letter.</summary>
    public int RivalShiftPaperDay = -9999;

    /// <summary>guarantee, friend, land, or opinion. Empty when this pair has not been asked.</summary>
    public string RivalShiftReason = "";
}

/// <summary>
/// An ally or a guarantor becomes a rival when a fact already on the relationship says so.
///
/// The facts are a withdrawn guarantee (or a protection abandoned while the memory is still
/// fresh), a war against the other side's ally or guarantor, land one holds that the other
/// lost, and opinion at or under <see cref="Defines.AiRivalryThreshold"/>. A healthy regard,
/// an unbroken promise, and the cooldown each refuse the shift. Two computers change stance.
/// A human is handed a letter in the diplomatic inbox and the alliance stays until they end
/// it. No province changes owner and no direct order is rewritten.
/// </summary>
public static class AllyRivalShift
{
    public const string Guarantee = "guarantee";
    public const string Friend = "friend";
    public const string Land = "land";
    public const string Opinion = "opinion";
    public const string EndedText = "The partnership is over. No land changed hands.";
    public const string KeptText = "The partnership stays. No land changed hands.";

    public static bool Pending(Campaign campaign, DiplomaticRelation relation) =>
        campaign is not null && relation is not null && relation.RivalShiftPaperBy >= 0 &&
        relation.RivalShiftPaperDay >= 0 &&
        campaign.ElapsedDays - relation.RivalShiftPaperDay < Math.Max(1, campaign.Defines.AllyRivalShiftPaperDays);

    public static int Incoming(Campaign campaign, int nationId)
    {
        if (campaign is null || nationId < 0) return 0;
        var count = 0;
        foreach (var relation in campaign.Relations.Values)
            if (Pending(campaign, relation) && relation.RivalShiftPaperBy != nationId &&
                (relation.A == nationId || relation.B == nationId)) count++;
        return count;
    }

    /// <summary>The one sentence a chronicle line or a relation tooltip shows. Empty when nothing was asked.</summary>
    public static string Line(Campaign campaign, DiplomaticRelation relation)
    {
        if (campaign is null || relation is null || string.IsNullOrEmpty(relation.RivalShiftReason)) return "";
        var speakerId = relation.RivalShiftPaperBy >= 0 ? relation.RivalShiftPaperBy : Math.Min(relation.A, relation.B);
        var speaker = campaign.NationById(speakerId);
        var other = campaign.NationById(speakerId == relation.A ? relation.B : relation.A);
        if (speaker is null || other is null) return "";
        return Pending(campaign, relation)
            ? PaperSentence(speaker, relation.RivalShiftReason)
            : ChronicleSentence(speaker, other, relation.RivalShiftReason);
    }

    public static string PaperSentence(Nation speaker, string code) =>
        Loc.Format("paper.ally_rival_" + code, speaker?.DisplayName ?? "");

    public static string ChronicleSentence(Nation actor, Nation other, string code) =>
        Loc.Format("chronicle.alliance_rival_" + code, actor?.DisplayName ?? "", other?.DisplayName ?? "");

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null || !campaign.Defines.AllyRivalShift ||
            !MechanicFeatures.SystemEnabled(campaign, nameof(AllyRivalShift))) yield break;
        Expire(campaign);
        var interval = Math.Max(1, campaign.Defines.AllianceReviewDays);
        if (campaign.ElapsedDays % interval != 0) yield break;

        var due = new List<DiplomaticRelation>();
        foreach (var relation in campaign.Relations.Values)
            if (Due(campaign, relation)) due.Add(relation);
        if (due.Count > 1) due.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        for (var index = 0; index < due.Count; index++)
        {
            yield return 0;
            Consider(campaign, due[index]);
        }
        yield return 0;
    }

    public static void Answer(Campaign campaign, AnswerAllyRivalCommand command)
    {
        if (command is null) return;
        if (campaign is null || !campaign.Defines.AllyRivalShift)
        {
            command.Result = "The offer has expired.";
            return;
        }
        var relation = CampaignDiplomacy.Peek(campaign, command.IssuerId, command.TargetId);
        if (relation is null || !Pending(campaign, relation) || command.IssuerId == relation.RivalShiftPaperBy ||
            (command.IssuerId != relation.A && command.IssuerId != relation.B))
        {
            command.Result = "Only the addressed country may answer.";
            return;
        }
        if (!command.Accept)
        {
            relation.RivalShiftDay = campaign.ElapsedDays;
            ClearLetter(relation, keepSpeaker: false);
            campaign.DealInboxRevision++;
            campaign.MarkStrategicChange();
            command.Result = KeptText;
            return;
        }
        var speaker = relation.RivalShiftPaperBy;
        var listener = speaker == relation.A ? relation.B : relation.A;
        Commit(campaign, relation, relation.RivalShiftReason, speaker, listener);
        command.Result = EndedText;
    }

    private static void Expire(Campaign campaign)
    {
        var term = Math.Max(1, campaign.Defines.AllyRivalShiftPaperDays);
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.RivalShiftPaperBy < 0 || relation.RivalShiftPaperDay < 0) continue;
            if (campaign.ElapsedDays - relation.RivalShiftPaperDay < term) continue;
            relation.RivalShiftDay = campaign.ElapsedDays;
            ClearLetter(relation, keepSpeaker: false);
            campaign.DealInboxRevision++;
            campaign.MarkStrategicChange();
        }
    }

    private static bool Due(Campaign campaign, DiplomaticRelation relation)
    {
        if (relation.Stance is DiplomaticStance.AtWar or DiplomaticStance.Rival or DiplomaticStance.Truce) return false;
        if (relation.RivalShiftDay >= 0 &&
            campaign.ElapsedDays - relation.RivalShiftDay < Math.Max(1, campaign.Defines.AllyRivalShiftCooldownDays)) return false;
        if (Pending(campaign, relation)) return false;
        return relation.Stance == DiplomaticStance.Allied || relation.GuaranteeByA || relation.GuaranteeByB ||
            relation.GuaranteeBrokenBy >= 0 || FreshAbandoned(campaign, relation);
    }

    private static void Consider(Campaign campaign, DiplomaticRelation relation)
    {
        var first = campaign.NationById(relation.A);
        var second = campaign.NationById(relation.B);
        if (first is null || second is null || first.Population <= 0 || second.Population <= 0) return;
        if (first.Movement is not null || second.Movement is not null || first.IsSubject || second.IsSubject) return;
        if (first.IsHuman && second.IsHuman) return;
        if ((first.IsHuman || second.IsHuman) && campaign.ObserverMode) return;
        if (!TryCause(campaign, relation, first, second, out var code, out var actor, out var other)) return;
        if (first.IsHuman || second.IsHuman)
        {
            var speaker = first.IsHuman ? second.Id : first.Id;
            Deliver(campaign, relation, speaker, code);
            return;
        }
        Commit(campaign, relation, code, actor, other);
    }

    private static bool TryCause(Campaign campaign, DiplomaticRelation relation, Nation first, Nation second,
        out string code, out int actor, out int other)
    {
        code = "";
        actor = -1;
        other = -1;
        if (relation.GuaranteeBrokenBy == first.Id || relation.GuaranteeBrokenBy == second.Id ||
            OpinionSystem.Has(relation, "withdrew_guarantee") || FreshAbandoned(campaign, relation))
        {
            var breaker = relation.GuaranteeBrokenBy == first.Id || relation.GuaranteeBrokenBy == second.Id
                ? relation.GuaranteeBrokenBy : Math.Min(first.Id, second.Id);
            code = Guarantee;
            actor = breaker == first.Id ? second.Id : first.Id;
            other = breaker;
            return true;
        }
        if (WarOnFriend(campaign, first.Id, second.Id, out var offended, out var aggressor))
        {
            code = Friend;
            actor = offended;
            other = aggressor;
            return true;
        }
        if (Stolen(campaign, relation, first, second, out var loser, out var holder))
        {
            code = Land;
            actor = loser;
            other = holder;
            return true;
        }
        if (relation.Opinion <= campaign.Defines.AiRivalryThreshold)
        {
            code = Opinion;
            actor = Math.Min(first.Id, second.Id);
            other = Math.Max(first.Id, second.Id);
            return true;
        }
        return false;
    }

    private static bool FreshAbandoned(Campaign campaign, DiplomaticRelation relation)
    {
        var window = Math.Max(1, campaign.Defines.AllyRivalShiftCooldownDays);
        for (var index = 0; index < relation.Reasons.Count; index++)
        {
            var reason = relation.Reasons[index];
            if (!string.Equals(reason.Id, "abandoned_us", StringComparison.OrdinalIgnoreCase)) continue;
            return campaign.ElapsedDays - reason.GrantedDay <= window;
        }
        return false;
    }

    private static bool WarOnFriend(Campaign campaign, int first, int second, out int offended, out int aggressor)
    {
        offended = -1;
        aggressor = -1;
        var fighting = false;
        for (var index = 0; index < campaign.Wars.Count; index++)
            if (!campaign.Wars[index].Ended) { fighting = true; break; }
        if (!fighting) return false;
        foreach (var third in campaign.Nations)
        {
            var id = third.Id;
            if (id == first || id == second || third.Population <= 0) continue;
            if (Protects(campaign, first, id) && CampaignDiplomacy.AtWar(campaign, second, id))
            {
                offended = first;
                aggressor = second;
                return true;
            }
            if (Protects(campaign, second, id) && CampaignDiplomacy.AtWar(campaign, first, id))
            {
                offended = second;
                aggressor = first;
                return true;
            }
        }
        return false;
    }

    private static bool Protects(Campaign campaign, int patron, int other)
    {
        var relation = CampaignDiplomacy.Peek(campaign, patron, other);
        return relation is { Stance: DiplomaticStance.Allied } || (relation is not null && relation.GuaranteedBy(patron));
    }

    private static bool Stolen(Campaign campaign, DiplomaticRelation relation, Nation first, Nation second,
        out int loser, out int holder)
    {
        loser = -1;
        holder = -1;
        if (campaign.States.Count > 0)
        {
            var firstLost = LostLands.Weight(campaign, first, second) > 0f;
            var secondLost = LostLands.Weight(campaign, second, first) > 0f;
            if (firstLost || secondLost)
            {
                loser = firstLost && !secondLost ? first.Id : secondLost && !firstLost ? second.Id : Math.Min(first.Id, second.Id);
                holder = loser == first.Id ? second.Id : first.Id;
                return true;
            }
        }
        if (!OpinionSystem.Has(relation, "took_our_land")) return false;
        loser = Math.Min(first.Id, second.Id);
        holder = Math.Max(first.Id, second.Id);
        return true;
    }

    private static void Deliver(Campaign campaign, DiplomaticRelation relation, int speaker, string code)
    {
        relation.RivalShiftReason = code;
        relation.RivalShiftPaperBy = speaker;
        relation.RivalShiftPaperDay = campaign.ElapsedDays;
        campaign.DealInboxRevision++;
        campaign.MarkStrategicChange();
    }

    private static void Commit(Campaign campaign, DiplomaticRelation relation, string code, int actor, int other)
    {
        var left = campaign.NationById(actor);
        var right = campaign.NationById(other);
        if (left is null || right is null || string.IsNullOrEmpty(code)) return;
        if (relation.Stance == DiplomaticStance.Allied)
            AllianceSystem.Break(campaign, left, right, out _);
        if (relation.GuaranteeByA || relation.GuaranteeByB)
        {
            relation.GuaranteeByA = false;
            relation.GuaranteeByB = false;
            OpinionSystem.Forget(campaign, relation, "guaranteed_us");
        }
        relation.Stance = DiplomaticStance.Rival;
        relation.RivalShiftReason = code;
        relation.RivalShiftDay = campaign.ElapsedDays;
        relation.RivalShiftPaperBy = actor;
        relation.RivalShiftPaperDay = -9999;
        relation.LastActionDay = campaign.ElapsedDays;
        campaign.Chronicle.Record(campaign, "alliance_rival_" + code, actor, other);
        campaign.DealInboxRevision++;
        campaign.MarkStrategicChange();
    }

    private static void ClearLetter(DiplomaticRelation relation, bool keepSpeaker)
    {
        if (!keepSpeaker) relation.RivalShiftPaperBy = -1;
        relation.RivalShiftPaperDay = -9999;
        relation.RivalShiftReason = "";
    }
}

/// <summary>A human answers the letter. Accepting ends their own partnership. Declining leaves it.</summary>
public sealed class AnswerAllyRivalCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public bool Accept { get; init; }
    public string Result { get; set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        AllyRivalShift.Answer(campaign, this);
    }
}
