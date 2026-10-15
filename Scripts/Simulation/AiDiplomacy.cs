using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Minimum opinion for discretionary aid to a country without an alliance.</summary>
    public float AiAidFriendshipThreshold { get; set; } = 50f;
    /// <summary>Reserve an AI country keeps before sending discretionary aid, as a share of its GDP.</summary>
    public float AiAidReserveOfGdp { get; set; } = 0.06f;
    /// <summary>A friend's treasury below this share of GDP counts as an aid-worthy crisis.</summary>
    public float AiAidCrisisTreasuryOfGdp { get; set; } = 0.015f;
    /// <summary>Opinion at which an AI country lifts its own embargo.</summary>
    public float AiEmbargoReconcileOpinion { get; set; } = 0f;
}

public static partial class AiStatecraft
{
    private static readonly List<Nation> FriendlyCandidates = new();

    /// <summary>Use the same bilateral rules as a player, spending at most one diplomatic move.</summary>
    internal static bool TryFriendlyMove(Campaign campaign, Nation nation, Nation friend)
    {
        if (campaign is null || nation is null || friend is null || nation.Id == friend.Id) return false;
        var relation = CampaignDiplomacy.Get(campaign, nation.Id, friend.Id);
        if (relation.Stance == DiplomaticStance.AtWar) return false;
        var opinion = relation.Opinion;
        var defines = campaign.Defines;

        if (relation.EmbargoBy(nation.Id) && opinion >= defines.AiEmbargoReconcileOpinion)
        {
            new SetEmbargoCommand { IssuerId = nation.Id, TargetId = friend.Id, Embargo = false }.Apply(campaign);
            Count("embargo_lifted");
            return true;
        }

        if (relation.Stance == DiplomaticStance.Rival && opinion >= 35f &&
            campaign.ElapsedDays - relation.LastActionDay >= 14 && nation.Treasury >= 0.1f)
        {
            new ImproveRelationsCommand { IssuerId = nation.Id, TargetId = friend.Id }.Apply(campaign);
            if (relation.Stance == DiplomaticStance.Friendly)
            {
                Count("rivalry_reconciled");
                return true;
            }
        }

        if (opinion >= defines.AiTradeThreshold &&
            TryAgreement(campaign, nation, friend, AgreementKind.TradeAgreement)) return true;
        if (opinion >= defines.AiPactThreshold &&
            TryAgreement(campaign, nation, friend, AgreementKind.NonAggressionPact)) return true;

        if (relation.Stance != DiplomaticStance.Allied && opinion >= defines.AiAllianceThreshold &&
            AllianceSystem.CanPropose(campaign, nation, friend, out _))
        {
            // Human recipients get a real offer; an AI recipient may refuse it.
            AllianceSystem.Propose(campaign, nation, friend, out _);
            Count("alliance_offered");
            return true;
        }

        if (TryAid(campaign, nation, friend, relation)) return true;
        if (relation.Stance == DiplomaticStance.Neutral)
        {
            relation.Stance = DiplomaticStance.Friendly;
            campaign.MarkStrategicChange();
            return true;
        }
        return false;
    }

    private static bool TryAgreement(Campaign campaign, Nation asking, Nation asked, AgreementKind kind)
    {
        // An AI has no authority to sign for a player. The command also guards this, but
        // checking before answering avoids burning a turn on an impossible offer.
        if (!AgreementSystem.CanPropose(campaign, asking, asked, kind, out _)) return false;
        if (AgreementSystem.Willingness(campaign, asked, asking, kind) < AgreementSystem.Needed(campaign, kind))
            return false;
        var offer = new ProposeAgreementCommand
        {
            IssuerId = asking.Id, TargetId = asked.Id, Kind = kind
        };
        offer.Apply(campaign);
        if (offer.Result != AgreementResult.Signed) return false;
        Count(kind == AgreementKind.TradeAgreement ? "trade_signed" : "pact_signed");
        return true;
    }

    private static bool TryAid(Campaign campaign, Nation giver, Nation recipient, DiplomaticRelation relation)
    {
        var defines = campaign.Defines;
        if (relation.Stance != DiplomaticStance.Allied &&
            relation.Opinion < defines.AiAidFriendshipThreshold) return false;
        var fiscal = AiFiscalPolicy.Assess(campaign, giver, defines.AiAidReserveOfGdp);
        if (!fiscal.CanSpend) { Count("aid_fiscal_" + fiscal.Reason); return false; }
        var crisis = recipient.Treasury < recipient.Gdp * defines.AiAidCrisisTreasuryOfGdp;
        if (!crisis)
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(recipient.Id) && !war.Involves(giver.Id))
                {
                    crisis = true;
                    break;
                }
        if (!crisis) return false;
        var package = AgreementSystem.AidAmount(campaign, giver, recipient, out _);
        // The ordinary aid command sends a full package. Do not invent a different
        // transfer here, or spend the reserve which was checked only before payment.
        if (!float.IsFinite(package) || package <= 0f || package > fiscal.DiscretionaryCash)
        { Count("aid_reserved_cash"); return false; }
        var aid = new SendAidCommand { IssuerId = giver.Id, TargetId = recipient.Id };
        aid.Apply(campaign);
        if (aid.Sent <= 0f) return false;
        Count("aid_sent");
        return true;
    }
}
