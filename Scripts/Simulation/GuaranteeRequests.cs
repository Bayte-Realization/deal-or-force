using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// What a country must want before it agrees to shelter somebody who asks it to.
    ///
    /// The computer has always measured this for its own plea (AiStatecraft.AskForShelter,
    /// Defines.AiShelterWillingness). A player's request is answered on exactly the same
    /// arithmetic, so asking is neither cheaper nor dearer from the player's chair.
    /// </summary>
    public float GuaranteeWillingness { get; set; } = 0.20f;

    /// <summary>How long a request sits on a player's table before it lapses unanswered.</summary>
    public int GuaranteeReplyDays { get; set; } = 30;

    /// <summary>How long a country waits before asking the same patron again.</summary>
    public int GuaranteeRetryDays { get; set; } = 90;

    /// <summary>How many unanswered requests one player is asked to hold at once.</summary>
    public int GuaranteeInboxLimit { get; set; } = 4;
}

public sealed partial class DiplomaticRelation
{
    /// <summary>
    /// Who has asked the other side to promise to fight for them, and when.
    ///
    /// -1 is nobody, and it is the initialiser rather than a sentinel the code has to remember:
    /// country 0 exists (Indonesia), which is the mistake the alliance offer fields made once
    /// and paid for in a comment - see AllianceCore.CanPropose.
    /// </summary>
    public int GuaranteeRequestedBy = -1;
    public int GuaranteeRequestedDay = -9999;
}


/// <summary>
/// Asking somebody to promise to fight for you, and answering that request.
///
/// The computer has always done this. A country frightened of a large neighbour went looking
/// for a power willing to stand behind it, scored every candidate by what it would actually
/// answer, and took the best one (AiStatecraft.AskForShelter). A player could only ever
/// <i>give</i> a guarantee. So the one diplomatic move a small country actually needs - finding
/// a patron - was the one the player did not have, and the only way to be defended was to be
/// chosen.
///
/// Asking and giving are the same command and the same pair of fields; what the request adds is
/// a question in the middle. The patron answers with the arithmetic the computer already used
/// when it was the one doing the asking: how it regards the asker, whether it is steady, whether
/// it is already fighting, and above all whether it fears the same country. When the patron is
/// one the computer speaks for, the answer is computed now. When it is a player's country, the
/// request goes on their table and waits, the way an alliance offer does - the rule that a
/// player is never committed to a war without being asked would mean very little if their
/// guarantee could be promised for them.
/// </summary>
public static class Guarantee
{
    /// <summary>Whether a request on this tie is still waiting for an answer.</summary>
    public static bool Pending(Campaign campaign, DiplomaticRelation relation) =>
        campaign is not null && relation is not null && relation.GuaranteeRequestedBy >= 0 &&
        campaign.ElapsedDays - relation.GuaranteeRequestedDay < Math.Max(1, campaign.Defines.GuaranteeReplyDays);

    /// <summary>Who is waiting on this country's answer, if anybody. The oldest request first.</summary>
    public static Nation RequesterTo(Campaign campaign, Nation patron)
    {
        if (campaign is null || patron is null) return null;
        Nation found = null;
        var oldest = int.MaxValue;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != patron.Id && relation.B != patron.Id) continue;
            var askerId = relation.GuaranteeRequestedBy;
            if (askerId < 0 || askerId == patron.Id) continue;
            if (!Pending(campaign, relation)) continue;
            if (relation.GuaranteeRequestedDay >= oldest) continue;
            var asker = campaign.NationById(askerId);
            if (asker is null) continue;
            found = asker;
            oldest = relation.GuaranteeRequestedDay;
        }
        return found;
    }

    /// <summary>How many answers this country owes. A player should not be buried in them.</summary>
    public static int Outstanding(Campaign campaign, int patronId)
    {
        if (campaign is null) return 0;
        var count = 0;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != patronId && relation.B != patronId) continue;
            var askerId = relation.GuaranteeRequestedBy;
            if (askerId < 0 || askerId == patronId) continue;
            if (Pending(campaign, relation)) count++;
        }
        return count;
    }

    /// <summary>Why this ask cannot be put today, or an empty string. The asker's side.</summary>
    public static string Refusal(Campaign campaign, Nation asker, Nation patron)
    {
        if (campaign is null || asker is null || patron is null || asker == patron)
            return Loc.Get("Invalid target.");
        var relation = CampaignDiplomacy.Peek(campaign, asker.Id, patron.Id);
        if (relation is { Stance: DiplomaticStance.AtWar }) return Loc.Get("Already at war.");
        if (relation is not null && relation.GuaranteedBy(patron.Id)) return Loc.Get("They already guarantee you.");
        if (relation is not null && relation.GuaranteeRequestedBy == asker.Id && Pending(campaign, relation))
            return Loc.Get("They have not answered the last request yet.");
        if (relation is not null && relation.GuaranteeRequestedBy >= 0 && relation.GuaranteeRequestedBy != asker.Id &&
            Pending(campaign, relation))
            return Loc.Get("They are weighing somebody else's request first.");
        var retry = asker.DecisionAvailableDay.GetValueOrDefault("guarantee/" + patron.Id);
        if (retry > campaign.ElapsedDays)
            return Loc.Format("Ask again in {0} day(s).", retry - campaign.ElapsedDays);
        if (patron.IsHuman && Outstanding(campaign, patron.Id) >= Math.Max(1, campaign.Defines.GuaranteeInboxLimit))
            return Loc.Get("They have other requests to answer first.");
        return "";
    }

    /// <summary>
    /// What the patron would answer, as a number against <see cref="Defines.GuaranteeWillingness"/>.
    ///
    /// Deliberately the same terms AiStatecraft.AskForShelter uses to choose whom to ask: their
    /// opinion of the asker counts for half of what it would in an alliance - a great power does
    /// not shelter a small country because it is fond of it - and what the shelter is against
    /// counts for most of it. One arithmetic, whichever side of the table is being played.
    /// </summary>
    public static float Willingness(Campaign campaign, Nation patron, Nation asker, Breakdown why = null)
    {
        why?.Begin("Whether they would promise", "share");
        if (campaign is null || patron is null || asker is null || patron == asker) return 0f;
        var defines = campaign.Defines;
        var relation = CampaignDiplomacy.ReadOrNeutral(campaign, patron.Id, asker.Id);

        var opinion = relation.Opinion / 200f;
        var steady = M.Clamp(patron.Stability - 0.5f, -0.3f, 0.3f);
        var willingness = opinion + steady;
        why?.Add("How they regard us", opinion);
        why?.Add("How steady they are", steady);

        var shared = SharedRival(campaign, patron, asker);
        if (shared is not null)
        {
            willingness += defines.AiShelterSharedRival;
            why?.Add("They fear " + shared.Tag + " too", defines.AiShelterSharedRival);
        }

        // Not a refusal: a guarantee commits nothing today, and a power already fighting is if
        // anything more interested in who else is drawn in. It makes them harder to ask.
        if (Fighting(campaign, patron.Id))
        {
            willingness -= defines.AiShelterWarPenalty;
            why?.Add("They are already at war", -defines.AiShelterWarPenalty);
        }

        // Shelter for a rival is not shelter, it is arming the other side.
        if (relation.Stance == DiplomaticStance.Rival)
        {
            willingness -= defines.AiShelterWarPenalty;
            why?.Add("We are rivals", -defines.AiShelterWarPenalty);
        }
        return willingness;
    }

    private static bool Fighting(Campaign campaign, int nationId)
    {
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nationId)) return true;
        return false;
    }

    /// <summary>The powerful country both of them are wary of, or null.</summary>
    public static Nation SharedRival(Campaign campaign, Nation one, Nation other)
    {
        if (campaign is null || one is null || other is null) return null;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != one.Id && relation.B != one.Id) continue;
            var rivalId = relation.Other(one.Id);
            if (rivalId == other.Id) continue;
            if (relation.Opinion > campaign.Defines.AiRivalryThreshold) continue;
            var rival = campaign.NationById(rivalId);
            if (rival is null || rival.IsSubject) continue;
            var theirs = CampaignDiplomacy.Peek(campaign, other.Id, rivalId);
            if (theirs is null || theirs.Opinion > campaign.Defines.AiRivalryThreshold) continue;
            return rival;
        }
        return null;
    }


    /// <summary>
    /// Ask. A patron the computer speaks for answers now; a player's country is asked, and the
    /// request waits for them. Returns whether the promise was made.
    /// </summary>
    public static bool Ask(Campaign campaign, Nation asker, Nation patron, out string answer)
    {
        answer = "";
        var refusal = Refusal(campaign, asker, patron);
        if (refusal.Length != 0) { answer = refusal; return false; }
        var relation = CampaignDiplomacy.Get(campaign, asker.Id, patron.Id);
        relation.LastActionDay = campaign.ElapsedDays;
        relation.GuaranteeRequestedBy = asker.Id;
        relation.GuaranteeRequestedDay = campaign.ElapsedDays;
        asker.DecisionAvailableDay["guarantee/" + patron.Id] =
            campaign.ElapsedDays + Math.Max(1, campaign.Defines.GuaranteeRetryDays);

        if (patron.IsHuman)
        {
            campaign.MarkStrategicChange();
            answer = Loc.Format("{0} will consider it.", patron.DisplayName);
            DiagnosticLogs.Write("simulation",
                $"GUARANTEE_ASK from={asker.Tag} to={patron.Tag} day={campaign.ElapsedDays}");
            return false;
        }

        // The computer's own answer, on the computer's own arithmetic - the same terms it uses
        // when it is the one choosing whom to ask (AiStatecraft.AskForShelter).
        if (Willingness(campaign, patron, asker, null) < campaign.Defines.GuaranteeWillingness)
        {
            relation.GuaranteeRequestedBy = -1;
            relation.GuaranteeRequestedDay = -9999;
            campaign.MarkStrategicChange();
            answer = Loc.Format("{0} declines.", patron.DisplayName);
            DiagnosticLogs.Write("simulation",
                $"GUARANTEE_ASK_REFUSED by={patron.Tag} to={asker.Tag} day={campaign.ElapsedDays}");
            return false;
        }

        Answer(campaign, patron, asker, true, out answer, quiet: true);
        return relation.GuaranteedBy(patron.Id);
    }

    /// <summary>
    /// Answer a request.
    ///
    /// A refusal is remembered the way the computer remembers one - as a date, not a grudge:
    /// asking is not an insult. Going back on a promise already made is a different thing and is
    /// not this method; that is GuaranteeCommand with Guarantee = false, which withdraws, and is
    /// remembered.
    /// </summary>
    public static bool Answer(Campaign campaign, Nation patron, bool accept, out string outcome, bool quiet = false)
        => Answer(campaign, patron, RequesterTo(campaign, patron), accept, out outcome, quiet);

    /// <summary>Answer this country's request, never substitute another pending requester.</summary>
    public static bool Answer(Campaign campaign, Nation patron, Nation asker, bool accept, out string outcome, bool quiet = false)
    {
        outcome = "";
        if (campaign is null || patron is null) { outcome = Loc.Get("No such country."); return false; }
        if (asker is null) { outcome = Loc.Get("Nobody has asked you to guarantee them."); return false; }
        var relation = CampaignDiplomacy.Peek(campaign, patron.Id, asker.Id);
        if (relation is null || relation.GuaranteeRequestedBy != asker.Id || !Pending(campaign, relation))
        { outcome = Loc.Get("Nobody has asked you to guarantee them."); return false; }
        if (accept && (relation.Stance == DiplomaticStance.AtWar || CampaignDiplomacy.AtWar(campaign, patron.Id, asker.Id)))
        { outcome = Loc.Get("Already at war."); return false; }
        relation.GuaranteeRequestedBy = -1;
        relation.GuaranteeRequestedDay = -9999;
        relation.LastActionDay = campaign.ElapsedDays;

        if (!accept)
        {
            asker.DecisionAvailableDay["guarantee/" + patron.Id] =
                campaign.ElapsedDays + Math.Max(1, campaign.Defines.GuaranteeRetryDays);
            campaign.MarkStrategicChange();
            outcome = Loc.Format("{0} was turned down.", asker.DisplayName);
            DiagnosticLogs.Write("simulation",
                $"GUARANTEE_REFUSED by={patron.Tag} to={asker.Tag} day={campaign.ElapsedDays}");
            return false;
        }

        new GuaranteeCommand { IssuerId = patron.Id, TargetId = asker.Id, Guarantee = true }.Apply(campaign);
        campaign.MarkStrategicChange();
        outcome = Loc.Format("{0} guarantees {1}.", patron.DisplayName, asker.DisplayName);
        if (!quiet)
            DiagnosticLogs.Write("simulation",
                $"GUARANTEE_ANSWERED by={patron.Tag} for={asker.Tag} day={campaign.ElapsedDays}");
        return true;
    }

    /// <summary>
    /// Let the unanswered ones lapse. A request a player never answered is not a refusal and is
    /// not held against them; it simply stops waiting, and the asker may try again later.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same guarantee sweep, yielded between batches of relationships.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null) yield break;
        var scanned = 0;
        foreach (var relation in campaign.Relations.Values)
        {
            if (++scanned % 512 == 0) yield return 0;
            if (relation.GuaranteeRequestedBy < 0) continue;
            if (Pending(campaign, relation)) continue;
            relation.GuaranteeRequestedBy = -1;
            relation.GuaranteeRequestedDay = -9999;
        }
    }
}

/// <summary>Ask somebody to promise to fight for you. Answered on the spot, or left waiting.</summary>
public sealed class RequestGuaranteeCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }

    public bool Granted { get; private set; }
    public bool Waiting { get; private set; }
    public string Answer { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var asker = campaign.NationById(IssuerId);
        var patron = campaign.NationById(TargetId);
        if (asker is null || patron is null) return;
        Granted = Guarantee.Ask(campaign, asker, patron, out var answer);
        Answer = answer;
        // Waiting means THIS ask is the one on the table - not that the tie happens to have
        // somebody else's request on it.
        var relation = CampaignDiplomacy.Peek(campaign, IssuerId, TargetId);
        Waiting = !Granted && relation is not null && relation.GuaranteeRequestedBy == IssuerId &&
                  Guarantee.Pending(campaign, relation);
    }
}

/// <summary>Answer a request that is on your table.</summary>
public sealed class AnswerGuaranteeRequestCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public bool Accept { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var patron = campaign.NationById(IssuerId);
        var asker = campaign.NationById(TargetId);
        if (patron is null || asker is null) return;
        Guarantee.Answer(campaign, patron, asker, Accept, out _);
    }
}
