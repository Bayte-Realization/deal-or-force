using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// What a country wants before it signs a trade agreement, in the alliance's units (how it
    /// regards the asker / 100, plus the reasons below). The computer signs its own at the same
    /// point: AiTradeThreshold / 100.
    /// </summary>
    public float TradeAgreementWillingness { get; set; } = 0.18f;

    /// <summary>How much the trade already running between them argues for writing it down, per whole of the asked country's trade.</summary>
    public float TradeAgreementTradeWeight { get; set; } = 0.5f;

    /// <summary>What a country wants before it promises not to attack: AiPactThreshold / 100 for the computer's own.</summary>
    public float NonAggressionPactWillingness { get; set; } = 0.42f;

    /// <summary>How much a country already fighting somebody else wants its other borders quiet.</summary>
    public float NonAggressionPactWarBonus { get; set; } = 0.1f;

    /// <summary>What a rival's offer is worth less, for either agreement.</summary>
    public float AgreementRivalPenalty { get; set; } = 0.2f;

    /// <summary>How much an asker's notoriety counts against its word, per point, at most AgreementNotorietyCap.</summary>
    public float AgreementNotorietyWeight { get; set; } = 0.005f;

    public float AgreementNotorietyCap { get; set; } = 0.3f;

    /// <summary>How long a country will not be asked again - by the same country - after saying no.</summary>
    public int AgreementRefusalDays { get; set; } = 180;

    /// <summary>
    /// How long an offer waits on a player's table before it lapses.
    ///
    /// The same thirty days an alliance offer gets (AllianceOfferDays): a question a player never
    /// got round to answering should stop occupying the tie, and the offer is not a refusal, so
    /// nothing is held against them for it.
    /// </summary>
    public int AgreementReplyDays { get; set; } = 30;

    /// <summary>What breaking a non-aggression pact does to the breaker's name with everybody else.</summary>
    public float NonAggressionPactBreakNotoriety { get; set; } = 8f;

    /// <summary>An aid package, as a share of the receiving country's GDP.</summary>
    public float AidShareOfRecipientGdp { get; set; } = 0.01f;

    /// <summary>And never more than this share of the giver's treasury.</summary>
    public float AidMaxShareOfTreasury { get; set; } = 0.1f;

    /// <summary>How often one country may send another a package.</summary>
    public int AidIntervalDays { get; set; } = 90;
}

public enum AgreementKind
{
    TradeAgreement,
    NonAggressionPact
}

public enum AgreementResult
{
    NotAsked,
    Signed,
    Refused,
    NotAllowed,
    /// <summary>Put on a player's table. Nobody has signed anything; they answer it themselves.</summary>
    Offered
}

/// <summary>
/// The agreements short of an alliance, as something a player can ask for (item 65).
///
/// The computer has signed trade agreements and non-aggression pacts all along - with its
/// friends, and with players - and a player could do neither: not ask for one, and not get
/// out of one. The second was the worse half. A pact forbids a declaration of war, the
/// computer could sign one with a player without asking, and nothing anywhere could end it,
/// so a country could find a neighbour it was never going to be allowed to fight.
///
/// Asking is answered on the spot, the way an alliance is: how the asked country regards the
/// asker, and a few named reasons, against a bar a mod can move (common/defines). The bar is
/// the one the computer signs its own at, so a player gets the same answer it would. Breaking
/// is always possible and never free: the opinion the agreement bought goes, a grudge comes,
/// and breaking a promise not to attack costs the breaker's name with everybody.
///
/// The other direction is the same offer. A computer that wants an agreement with a country a
/// player answers for does not sign it and does not silently lose the turn: the offer goes on
/// that player's table and waits (AgreementResult.Offered), the way an alliance offer does.
///
/// Aid is the third thing a player can do here: money from the treasury for goodwill, scaled to
/// what the money is to the country receiving it.
/// </summary>
public static class AgreementSystem
{
    public static bool Signed(DiplomaticRelation relation, AgreementKind kind) =>
        relation is not null && (kind == AgreementKind.TradeAgreement ? relation.TradeAgreement : relation.NonAggressionPact);

    /// <summary>Whether this country may ask for this at all today, and if not, why not.</summary>
    public static bool CanPropose(Campaign campaign, Nation asking, Nation asked, AgreementKind kind, out string reason,
        bool preview = false)
    {
        reason = "";
        if (campaign is null || asking is null || asked is null || asking == asked)
        {
            reason = Loc.Get("Invalid target.");
            return false;
        }
        var relation = preview ? CampaignDiplomacy.ReadOrNeutral(campaign, asking.Id, asked.Id)
            : CampaignDiplomacy.Get(campaign, asking.Id, asked.Id);
        if (relation.Stance == DiplomaticStance.AtWar) { reason = Loc.Get("Already at war."); return false; }
        if (Signed(relation, kind)) { reason = Loc.Get("Already signed."); return false; }
        // An offer on a player's table is a question, not a signature. It blocks a second ask -
        // from either side, which is what stops a computer and a player asking each other in
        // circles - and it lapses on its own (AgreementReplyDays).
        if (OfferPending(campaign, relation))
        {
            reason = relation.AgreementOfferedBy == asking.Id
                ? Loc.Get("They have not answered the last offer yet.")
                : Loc.Get("They have an offer on your table; answer that one first.");
            return false;
        }
        var since = campaign.ElapsedDays - relation.AgreementRefusedDay;
        if (since < campaign.Defines.AgreementRefusalDays)
        {
            reason = Loc.Format("They said no {0} day(s) ago; they will hear it again in {1}.",
                since, campaign.Defines.AgreementRefusalDays - since);
            return false;
        }
        var busy = campaign.ElapsedDays - relation.LastActionDay;
        if (busy < 14)
        {
            reason = Loc.Format("A mission to them is still under way: {0} day(s).", 14 - busy);
            return false;
        }
        return true;
    }

    /// <summary>The asked country's answer, as a number against <see cref="Needed"/>, and what it is made of.</summary>
    public static float Willingness(Campaign campaign, Nation asked, Nation asking, AgreementKind kind,
        Breakdown why = null, bool preview = false)
    {
        why?.Begin("Whether they would sign", "share");
        if (campaign is null || asked is null || asking is null) return 0f;
        var defines = campaign.Defines;
        var relation = preview ? CampaignDiplomacy.ReadOrNeutral(campaign, asked.Id, asking.Id)
            : CampaignDiplomacy.Get(campaign, asked.Id, asking.Id);

        var willingness = relation.Opinion / 100f;
        why?.Add("How they regard us", relation.Opinion / 100f);

        if (relation.Stance == DiplomaticStance.Rival)
        {
            willingness -= defines.AgreementRivalPenalty;
            why?.Add("We are rivals", -defines.AgreementRivalPenalty);
        }

        var name = MathF.Min(defines.AgreementNotorietyCap, MathF.Max(0f, asking.Notoriety) * defines.AgreementNotorietyWeight);
        if (name > 0.0001f)
        {
            willingness -= name;
            why?.Add("Our notoriety", -name);
        }

        if (kind == AgreementKind.TradeAgreement)
        {
            // Trade already moving between them is the argument for writing it down: the
            // agreement makes the route cheaper, and the route is already there to be cheaper.
            var interest = asked.TradeDependenceOn(asking.Id) * defines.TradeAgreementTradeWeight;
            if (interest > 0.0001f)
            {
                willingness += interest;
                why?.Add("The trade already between us", interest);
            }
        }
        else
        {
            foreach (var war in campaign.Wars)
            {
                if (war.Ended || !war.Involves(asked.Id) || war.Involves(asking.Id)) continue;
                willingness += defines.NonAggressionPactWarBonus;
                why?.Add("They are fighting somebody else", defines.NonAggressionPactWarBonus);
                break;
            }
        }
        if (!asked.IsHuman && AiTemper.Agreeableness(campaign) != 1f)
        {
            var dialled = AiTemper.Willing(campaign, willingness);
            why?.Add("Agreeableness setting", dialled - willingness);
            willingness = dialled;
        }
        return willingness;
    }

    public static float Needed(Campaign campaign, AgreementKind kind) =>
        kind == AgreementKind.TradeAgreement
            ? campaign.Defines.TradeAgreementWillingness
            : campaign.Defines.NonAggressionPactWillingness;

    /// <summary>Write it down between the two, with what that is worth to how each regards the other.</summary>
    public static void Sign(Campaign campaign, DiplomaticRelation relation, AgreementKind kind)
    {
        if (kind == AgreementKind.TradeAgreement)
        {
            relation.TradeAgreement = true;
            OpinionSystem.Forget(campaign, relation, "cancelled_trade_agreement");
            OpinionSystem.Remember(campaign, relation, "trade_agreement");
        }
        else
        {
            relation.NonAggressionPact = true;
            OpinionSystem.Remember(campaign, relation, "non_aggression_pact");
        }
        relation.LastActionDay = campaign.ElapsedDays;
        campaign.MarkStrategicChange();
    }

    /// <summary>Whether an offer is still on this tie, from either side.</summary>
    public static bool OfferPending(Campaign campaign, DiplomaticRelation relation) =>
        campaign is not null && relation is not null && relation.AgreementOfferedBy >= 0 &&
        campaign.ElapsedDays - relation.AgreementOfferedDay < Math.Max(1, campaign.Defines.AgreementReplyDays);

    /// <summary>The offer one country has on another's table, if it is still waiting.</summary>
    public static bool OfferedBy(Campaign campaign, Nation asked, int proposerId, out AgreementKind kind)
    {
        kind = AgreementKind.TradeAgreement;
        if (campaign is null || asked is null) return false;
        var relation = CampaignDiplomacy.Peek(campaign, asked.Id, proposerId);
        if (relation is null || relation.AgreementOfferedBy != proposerId) return false;
        if (!OfferPending(campaign, relation)) return false;
        kind = relation.AgreementOfferedKind;
        return true;
    }

    /// <summary>The offers waiting on this country's table, oldest first.</summary>
    public static List<(Nation Proposer, AgreementKind Kind)> OfferedTo(Campaign campaign, Nation asked)
    {
        var found = new List<(Nation, AgreementKind)>();
        if (campaign is null || asked is null) return found;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != asked.Id && relation.B != asked.Id) continue;
            var proposerId = relation.AgreementOfferedBy;
            if (proposerId < 0 || proposerId == asked.Id) continue;
            if (!OfferPending(campaign, relation)) continue;
            var proposer = campaign.NationById(proposerId);
            if (proposer is null) continue;
            found.Add((proposer, relation.AgreementOfferedKind));
        }
        return found;
    }

    /// <summary>
    /// Answer one. Accepting is the player's own signature, so it is the same <see cref="Sign"/>
    /// the computer would have made - after checking the tie is still askable at all, since they
    /// may have gone to war with each other while the offer sat there.
    /// </summary>
    public static AgreementResult Answer(Campaign campaign, Nation answering, int proposerId, bool accept,
        out string outcome)
    {
        outcome = "";
        if (!OfferedBy(campaign, answering, proposerId, out var kind))
        {
            outcome = Loc.Get("They have not offered anything.");
            return AgreementResult.NotAsked;
        }
        var relation = CampaignDiplomacy.Get(campaign, answering.Id, proposerId);
        relation.AgreementOfferedBy = -1;
        relation.AgreementOfferedKind = AgreementKind.TradeAgreement;
        relation.AgreementOfferedDay = -9999;
        relation.LastActionDay = campaign.ElapsedDays;

        var proposer = campaign.NationById(proposerId);
        var name = kind == AgreementKind.TradeAgreement ? Loc.Get("trade agreement") : Loc.Get("non-aggression pact");
        if (!accept)
        {
            // A refusal, not a grudge: asking is not an insult. It does buy the asker the same
            // silence a refusal always buys it.
            relation.AgreementRefusedDay = campaign.ElapsedDays;
            campaign.MarkStrategicChange();
            outcome = Loc.Format("{0} declined the {1}.", answering.DisplayName, name);
            DiagnosticLogs.Write("simulation",
                $"AGREEMENT_REFUSED by={answering.Tag} from={proposer?.Tag} kind={kind} day={campaign.ElapsedDays}");
            return AgreementResult.Refused;
        }

        if (relation.Stance == DiplomaticStance.AtWar || Signed(relation, kind))
        {
            campaign.MarkStrategicChange();
            outcome = Loc.Get("The offer no longer stands.");
            return AgreementResult.NotAllowed;
        }
        Sign(campaign, relation, kind);
        outcome = Loc.Format("{0} signed the {1}.", answering.DisplayName, name);
        DiagnosticLogs.Write("simulation",
            $"AGREEMENT_SIGNED by={answering.Tag} with={proposer?.Tag} kind={kind} day={campaign.ElapsedDays}");
        return AgreementResult.Signed;
    }

    /// <summary>Let the unanswered ones lapse. A lapsed offer is not a refusal and costs nobody anything.</summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign is null) return;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.AgreementOfferedBy < 0) continue;
            if (OfferPending(campaign, relation)) continue;
            relation.AgreementOfferedBy = -1;
            relation.AgreementOfferedKind = AgreementKind.TradeAgreement;
            relation.AgreementOfferedDay = -9999;
        }
    }

    /// <summary>What an aid package from this country to that one would be worth, or zero if it cannot be sent.</summary>
    public static float AidAmount(Campaign campaign, Nation giving, Nation receiving, out string reason, bool preview = false)
    {
        reason = "";
        if (campaign is null || giving is null || receiving is null || giving == receiving)
        {
            reason = Loc.Get("Invalid target.");
            return 0f;
        }
        var relation = preview ? CampaignDiplomacy.ReadOrNeutral(campaign, giving.Id, receiving.Id)
            : CampaignDiplomacy.Get(campaign, giving.Id, receiving.Id);
        if (relation.Stance == DiplomaticStance.AtWar) { reason = Loc.Get("Already at war."); return 0f; }
        var since = campaign.ElapsedDays - relation.AidSentDay;
        if (since < campaign.Defines.AidIntervalDays)
        {
            reason = Loc.Format("The last package arrived {0} day(s) ago; the next can go in {1}.",
                since, campaign.Defines.AidIntervalDays - since);
            return 0f;
        }
        var amount = MathF.Min(receiving.Gdp * campaign.Defines.AidShareOfRecipientGdp,
            MathF.Max(0f, giving.Treasury) * campaign.Defines.AidMaxShareOfTreasury);
        if (amount < 0.01f)
        {
            reason = Loc.Get("The treasury cannot spare a package worth sending.");
            return 0f;
        }
        return amount;
    }
}

/// <summary>Ask for a trade agreement or a non-aggression pact. Answered on the spot.</summary>
public sealed class ProposeAgreementCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public AgreementKind Kind { get; init; }

    public AgreementResult Result { get; private set; } = AgreementResult.NotAsked;
    public string Reason { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var asking = campaign.NationById(IssuerId);
        var asked = campaign.NationById(TargetId);
        if (!AgreementSystem.CanPropose(campaign, asking, asked, Kind, out var refusal))
        {
            Result = AgreementResult.NotAllowed;
            Reason = refusal;
            return;
        }
        var relation = CampaignDiplomacy.Get(campaign, IssuerId, TargetId);
        // A player signs for themselves. The offer is put on their table and waits, and this
        // command's one action is spent either way - the same turn an offer to a computer costs.
        if (asked.IsHuman)
        {
            relation.AgreementOfferedBy = IssuerId;
            relation.AgreementOfferedKind = Kind;
            relation.AgreementOfferedDay = campaign.ElapsedDays;
            relation.LastActionDay = campaign.ElapsedDays;
            campaign.MarkStrategicChange();
            Result = AgreementResult.Offered;
            Reason = Loc.Format("{0} will consider it.", asked.DisplayName);
            DiagnosticLogs.Write("simulation",
                $"AGREEMENT_OFFER from={asking.Tag} to={asked.Tag} kind={Kind} day={campaign.ElapsedDays}");
            return;
        }
        if (AgreementSystem.Willingness(campaign, asked, asking, Kind) >= AgreementSystem.Needed(campaign, Kind))
        {
            AgreementSystem.Sign(campaign, relation, Kind);
            Result = AgreementResult.Signed;
            return;
        }
        // Asked and refused: remembered as a date rather than a grudge. Asking is not an insult.
        relation.AgreementRefusedDay = campaign.ElapsedDays;
        relation.LastActionDay = campaign.ElapsedDays;
        Result = AgreementResult.Refused;
    }
}

/// <summary>Answer an offer that is on your table. Nothing is signed until somebody says yes.</summary>
public sealed class AnswerAgreementCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public bool Accept { get; init; }

    public AgreementResult Result { get; private set; } = AgreementResult.NotAsked;
    public string Reason { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var answering = campaign.NationById(IssuerId);
        if (answering is null || campaign.NationById(TargetId) is null) return;
        Result = AgreementSystem.Answer(campaign, answering, TargetId, Accept, out var outcome);
        Reason = outcome;
    }
}

/// <summary>End a trade agreement or break a non-aggression pact. Always possible, never free.</summary>
public sealed class CancelAgreementCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public AgreementKind Kind { get; init; }

    public bool Cancelled { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var issuer = campaign.NationById(IssuerId);
        if (issuer is null || campaign.NationById(TargetId) is null) return;
        var relation = CampaignDiplomacy.Get(campaign, IssuerId, TargetId);
        if (!AgreementSystem.Signed(relation, Kind)) return;
        if (Kind == AgreementKind.TradeAgreement)
        {
            relation.TradeAgreement = false;
            OpinionSystem.Forget(campaign, relation, "trade_agreement");
            OpinionSystem.Remember(campaign, relation, "cancelled_trade_agreement");
        }
        else
        {
            relation.NonAggressionPact = false;
            OpinionSystem.Forget(campaign, relation, "non_aggression_pact");
            OpinionSystem.Remember(campaign, relation, "broke_non_aggression_pact");
            issuer.Notoriety += campaign.Defines.NonAggressionPactBreakNotoriety;
        }
        relation.LastActionDay = campaign.ElapsedDays;
        Cancelled = true;
        campaign.MarkStrategicChange();
    }
}

/// <summary>Send a package of money from the treasury, for goodwill.</summary>
public sealed class SendAidCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }

    public float Sent { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var giving = campaign.NationById(IssuerId);
        var receiving = campaign.NationById(TargetId);
        var amount = AgreementSystem.AidAmount(campaign, giving, receiving, out _);
        if (amount <= 0f) return;
        var relation = CampaignDiplomacy.Get(campaign, IssuerId, TargetId);
        giving.Treasury -= amount;
        receiving.Treasury += amount;
        // A full package - a hundredth of what the country makes in a year - is the whole of
        // the modifier; a package the giver could only half afford is half of it.
        var full = MathF.Max(0.0001f, receiving.Gdp * campaign.Defines.AidShareOfRecipientGdp);
        OpinionSystem.Remember(campaign, relation, "economic_aid", MathF.Min(1f, amount / full));
        relation.AidSentDay = campaign.ElapsedDays;
        Sent = amount;
        campaign.Chronicle.Record(campaign, "aid_sent", IssuerId, TargetId);
        campaign.MarkStrategicChange();
    }
}
