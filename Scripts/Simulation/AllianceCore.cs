using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// An alliance as a decision two countries take, rather than a number one of them crosses.
///
/// The stance already meant something: an attack on an ally is answered, a promise refused
/// is an alliance ended in public, and nobody may declare war on somebody they are allied
/// to without leaving first. What was missing was the door. Only the computer could form
/// one, and it formed them by fiat - opinion reached sixty-six and the stance changed, with
/// nobody asked and nothing to refuse. A player could watch alliances appear on the map and
/// never be in one.
///
/// So: somebody asks, somebody answers. The answer is the interesting half, because it is
/// where a country says what it thinks an alliance is for - shelter from something larger,
/// a friend against a shared rival, or a war it would be signing up to. A country that says
/// no says why, and will not be asked again for half a year.
/// </summary>
public static class AllianceSystem
{
    /// <summary>Every country this one is allied to.</summary>
    public static List<Nation> AlliesOf(Campaign campaign, Nation nation)
    {
        var found = new List<Nation>();
        if (campaign is null || nation is null) return found;
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id) continue;
            var relation = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
            if (relation is { Stance: DiplomaticStance.Allied }) found.Add(other);
        }
        return found;
    }

    /// <summary>Who has an alliance on this country's table, if anybody.</summary>
    public static Nation OfferedTo(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return null;
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id) continue;
            var relation = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
            if (relation is null || relation.AllianceOfferedBy != other.Id) continue;
            if (Expired(campaign, relation)) continue;
            return other;
        }
        return null;
    }

    private static bool Expired(Campaign campaign, DiplomaticRelation relation) =>
        campaign.ElapsedDays - relation.AllianceOfferedDay > campaign.Defines.AllianceOfferDays;

    /// <summary>
    /// Whether these two could sign anything at all, and if not, which of the several
    /// reasons it is. Every one of them is a fact a player can act on rather than a refusal.
    /// </summary>
    public static bool CanPropose(Campaign campaign, Nation proposer, Nation target, out string refusal)
    {
        refusal = "";
        if (campaign is null || proposer is null || target is null || proposer.Id == target.Id)
        {
            refusal = "No such country.";
            return false;
        }

        var relation = CampaignDiplomacy.Get(campaign, proposer.Id, target.Id);
        if (relation.Stance == DiplomaticStance.Allied)
        {
            refusal = $"{proposer.Tag} and {target.Tag} are already allied.";
            return false;
        }
        if (CampaignDiplomacy.AtWar(campaign, proposer.Id, target.Id))
        {
            refusal = "They are at war with each other.";
            return false;
        }

        // A country that answers to another one does not have a foreign policy to pledge,
        // which is the whole of what answering to somebody means - see SubjectSystem.
        if (proposer.IsSubject)
        {
            var overlord = campaign.NationById(proposer.OverlordId);
            refusal = $"{proposer.Tag} answers to {overlord?.Tag ?? "somebody"}; its alliances are theirs to make.";
            return false;
        }
        if (target.IsSubject)
        {
            var overlord = campaign.NationById(target.OverlordId);
            refusal = $"{target.Tag} answers to {overlord?.Tag ?? "somebody"}; ask them instead.";
            return false;
        }

        // Whether an offer is pending is the date, not who made it: an offer from country 0
        // (Indonesia) was stored as the same 0 that meant "no offer".
        if (!Expired(campaign, relation))
        {
            refusal = relation.AllianceOfferedBy == proposer.Id
                ? $"{target.Tag} has not answered the last offer yet."
                : $"{target.Tag} has an offer on your table; answer that one first.";
            return false;
        }

        var since = campaign.ElapsedDays - relation.AllianceRefusedDay;
        if (since < campaign.Defines.AllianceRefusalDays)
        {
            refusal = $"They refused {since:N0} day(s) ago; asking again this soon is not diplomacy.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// What the asked country thinks of being asked, where anything above the define is yes.
    ///
    /// The terms are the ones a foreign ministry would actually weigh, and they are named so
    /// the interface can show them: how they feel about you, whether signing means joining a
    /// war you are already in, whether you are worth sheltering behind or a liability to
    /// shelter, and whether you both fear the same country - which is what most alliances in
    /// history were actually about.
    /// </summary>
    public static float Willingness(Campaign campaign, Nation asked, Nation asking, Breakdown why = null)
    {
        why?.Begin("Whether they would sign", "share");
        if (campaign is null || asked is null || asking is null) return 0f;
        var relation = CampaignDiplomacy.Get(campaign, asked.Id, asking.Id);
        var defines = campaign.Defines;

        var willingness = relation.Opinion / 100f;
        why?.Add("How we regard them", relation.Opinion / 100f);

        // An alliance with a country already at war is a war, and everybody knows it.
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(asking.Id))
            {
                willingness -= defines.AllianceWarPenalty;
                why?.Add("They are already fighting", -defines.AllianceWarPenalty);
                break;
            }

        // What they are worth as a friend, and what we are worth to them. A small country
        // wants somebody large to stand behind; a large one gains little from somebody it
        // would have to carry.
        var mine = MathF.Max(0.01f, AiStatecraft.StrengthOf(campaign, asked));
        var theirs = CampaignIntel.EstimatedStrength(campaign, asked.Id, asking);
        var shelter = M.Clamp(theirs / mine - 1f, -0.6f, 1.2f) * defines.AllianceShelterWeight;
        willingness += shelter;
        why?.Add("What they bring", shelter);

        // A pariah is a liability: their enemies become yours and their reputation with them.
        if (asking.Notoriety > 0f)
        {
            var cost = -M.Clamp(asking.Notoriety / 100f, 0f, 1f) * defines.AllianceNotorietyWeight;
            willingness += cost;
            why?.Add("Their reputation", cost);
        }

        // And the reason most alliances were ever signed: the same country frightens both.
        var shared = 0;
        foreach (var third in campaign.Nations)
        {
            if (third.Id == asked.Id || third.Id == asking.Id) continue;
            var ours = CampaignDiplomacy.Peek(campaign, asked.Id, third.Id);
            var theirsToo = CampaignDiplomacy.Peek(campaign, asking.Id, third.Id);
            if (ours is null || theirsToo is null) continue;
            if (ours.Opinion > defines.AiRivalryThreshold || theirsToo.Opinion > defines.AiRivalryThreshold) continue;
            shared++;
        }
        if (shared > 0)
        {
            var common = MathF.Min(shared, 3) * defines.AllianceSharedRivalWeight;
            willingness += common;
            why?.Add($"We fear the same {shared:N0} country(ies)", common);
        }

        // A country in no state to promise anything does not promise anything.
        var steady = M.Clamp(asked.Stability - 0.5f, -0.3f, 0.3f);
        willingness += steady;
        why?.Add("What we can afford to promise", steady);

        return willingness;
    }

    /// <summary>
    /// Ask. If the country asked is one the computer speaks for, it answers now; if it is a
    /// player's, the offer goes on their table and waits, the way a peace offer does - the
    /// rule that a player is never committed to a war without being asked would mean very
    /// little if their alliances could be signed for them.
    /// </summary>
    public static bool Propose(Campaign campaign, Nation proposer, Nation target, out string answer)
    {
        if (!CanPropose(campaign, proposer, target, out answer)) return false;

        var relation = CampaignDiplomacy.Get(campaign, proposer.Id, target.Id);
        relation.LastActionDay = campaign.ElapsedDays;

        if (target.IsHuman)
        {
            relation.AllianceOfferedBy = proposer.Id;
            relation.AllianceOfferedDay = campaign.ElapsedDays;
            campaign.MarkStrategicChange();
            answer = $"{target.Name} will consider it.";
            DiagnosticLogs.Write("simulation",
                $"ALLIANCE_OFFER from={proposer.Tag} to={target.Tag} day={campaign.ElapsedDays}");
            return false;
        }

        var willingness = Willingness(campaign, target, proposer);
        if (willingness <= campaign.Defines.AllianceWillingness)
        {
            relation.AllianceRefusedDay = campaign.ElapsedDays;
            campaign.MarkStrategicChange();
            answer = $"{target.Name} declines.";
            DiagnosticLogs.Write("simulation",
                $"ALLIANCE_REFUSED by={target.Tag} to={proposer.Tag} willingness={willingness:0.00}");
            return false;
        }

        Sign(campaign, relation, proposer, target);
        answer = $"{target.Name} accepts.";
        return true;
    }

    /// <summary>The other side of an offer somebody was given time to think about.</summary>
    public static bool Answer(Campaign campaign, Nation answering, bool accept, out string outcome)
    {
        outcome = "";
        var proposer = OfferedTo(campaign, answering);
        if (proposer is null)
        {
            outcome = "Nobody has offered an alliance.";
            return false;
        }
        var relation = CampaignDiplomacy.Get(campaign, answering.Id, proposer.Id);
        relation.AllianceOfferedBy = 0;
        relation.AllianceOfferedDay = -9999;
        relation.LastActionDay = campaign.ElapsedDays;

        if (!accept)
        {
            relation.AllianceRefusedDay = campaign.ElapsedDays;
            campaign.MarkStrategicChange();
            outcome = $"{proposer.Name} was turned down.";
            return false;
        }

        Sign(campaign, relation, answering, proposer);
        outcome = $"{answering.Name} and {proposer.Name} are allied.";
        return true;
    }

    private static void Sign(Campaign campaign, DiplomaticRelation relation, Nation one, Nation other)
    {
        relation.Stance = DiplomaticStance.Allied;
        relation.AllianceOfferedBy = 0;
        relation.AllianceOfferedDay = -9999;
        relation.AllianceSinceDay = campaign.ElapsedDays;
        relation.LastActionDay = campaign.ElapsedDays;
        OpinionSystem.Remember(campaign, relation, "alliance");
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation",
            $"ALLIANCE {one.Tag}+{other.Tag} day={campaign.ElapsedDays}");
    }

    /// <summary>
    /// Walk away from one.
    ///
    /// Cheaper than refusing a call to arms, which is the same promise broken at the moment
    /// it was worth something, and dearer than never having signed: they remember it, and
    /// everybody who was watching remembers that this is a country whose signature has a
    /// shelf life.
    /// </summary>
    public static bool Break(Campaign campaign, Nation nation, Nation other, out string refusal)
    {
        refusal = "";
        if (campaign is null || nation is null || other is null) { refusal = "No such country."; return false; }
        var relation = CampaignDiplomacy.Get(campaign, nation.Id, other.Id);
        if (relation.Stance != DiplomaticStance.Allied)
        {
            refusal = $"{nation.Tag} and {other.Tag} are not allied.";
            return false;
        }

        relation.Stance = DiplomaticStance.Neutral;
        relation.AllianceSinceDay = -9999;
        relation.LastActionDay = campaign.ElapsedDays;
        OpinionSystem.Forget(campaign, relation, "alliance");
        OpinionSystem.Remember(campaign, relation, "broke_our_alliance");
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation",
            $"ALLIANCE_END {nation.Tag}-{other.Tag} day={campaign.ElapsedDays}");
        return true;
    }
}

/// <summary>Ask another country to be an ally.</summary>
public sealed class ProposeAllianceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }

    public void Apply(Campaign campaign)
    {
        var proposer = campaign.NationById(IssuerId);
        var target = campaign.NationById(TargetId);
        if (proposer is null || target is null) return;
        AllianceSystem.Propose(campaign, proposer, target, out _);
    }
}

/// <summary>Answer one that is on the table.</summary>
public sealed class AnswerAllianceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public bool Accept { get; init; }

    public void Apply(Campaign campaign)
    {
        var answering = campaign.NationById(IssuerId);
        if (answering is null) return;
        AllianceSystem.Answer(campaign, answering, Accept, out _);
    }
}

/// <summary>Leave one.</summary>
public sealed class BreakAllianceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign.NationById(IssuerId);
        var other = campaign.NationById(TargetId);
        if (nation is null || other is null) return;
        AllianceSystem.Break(campaign, nation, other, out _);
    }
}
