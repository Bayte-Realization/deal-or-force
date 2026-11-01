using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// A settlement one government has put to another and is waiting on.
///
/// Until now the AI simply refused to talk to a person: the negotiation step began with
/// "if the other side is human, return", so a player's war could only ever end when the
/// player ended it. A country losing to a player fought to the last division without once
/// asking to stop, and a country beating a player annexed nothing and said nothing, because
/// the one thing it knew how to do - sign a treaty on the spot - is not a thing you may do
/// to somebody who is sitting there.
///
/// So it makes an offer instead. The AI writes exactly the same settlement it would have
/// imposed on another AI, and the player is shown it and answers. Nothing is signed until
/// they do; an unanswered offer lapses, which is a refusal that costs nobody a decision.
///
/// The offer lives on the war rather than in a queue of its own, because that is where its
/// lifetime already is: a war that ends takes its offer with it, and a save that restores a
/// war restores what was on the table when it was written.
/// </summary>
public static class PeaceOffers
{
    /// <summary>How long a government waits for an answer before withdrawing the offer.</summary>
    public const int OfferStandsForDays = 60;

    /// <summary>The authority and pending-offer guard shared by execution and the conference preview.</summary>
    public static string DeliveryBlockReason(Campaign campaign, StrategicWar war, Nation proposer, Nation opponent)
    {
        if (campaign is null || war is null || war.Ended || proposer is null || opponent is null)
            return Loc.Get("There is no war to end");
        if (!CanAddress(campaign, war, proposer, opponent) ||
            (opponent.IsHuman && (war.PendingOffer is not null || war.PendingOfferFromId >= 0)))
            return Loc.Get("The offer cannot bypass a human coalition member or replace a pending offer.");
        return "";
    }

    /// <summary>
    /// Put a settlement to the other side. Against another AI that is the same act as
    /// signing it, because both answers are computed here; against a person it is left on
    /// the table with the reasoning attached.
    /// </summary>
    public static bool Put(Campaign campaign, StrategicWar war, Nation proposer, Nation opponent,
        PeaceDemand demand)
    {
        if (campaign is null || war is null || war.Ended || proposer is null || opponent is null) return false;
        if (demand is null || demand.ProposerId != proposer.Id || demand.WarId != war.Id) return false;
        if (DeliveryBlockReason(campaign, war, proposer, opponent).Length > 0) return false;
        if (!opponent.IsHuman) return PeaceConference.Resolve(campaign, war, demand);
        if (PeaceConference.BlockReason(campaign, war, demand).Length > 0) return false;

        // Store the same whole-region border that validation priced and acceptance will
        // transfer. The recipient's map and written summary read this saved offer.
        var rounded = PeaceConference.Round(campaign, war, demand);
        if (!proposer.IsHuman && ToPlayerBlockReason(campaign, war, true, rounded, PeaceTerms.WhitePeace).Length > 0)
            return false;
        war.PendingOffer = rounded;
        war.PendingOfferIsWritten = true;
        war.PendingOfferFromId = proposer.Id;
        war.PendingOfferDay = campaign.ElapsedDays;
        war.PendingOfferExpiresDay = campaign.ElapsedDays + OfferStandsForDays;
        war.LastPeaceProposalDay = campaign.ElapsedDays;
        if (!proposer.IsHuman) war.LastOfferToPlayerDay = campaign.ElapsedDays;
        war.LastNegotiation = $"{proposer.Name} has put a settlement to you";
        campaign.MarkStrategicChange();
        return true;
    }

    /// <summary>The same, for the terms that come off a menu rather than off the map.</summary>
    public static bool Put(Campaign campaign, StrategicWar war, Nation proposer, Nation opponent,
        PeaceTerms terms)
    {
        if (campaign is null || war is null || war.Ended || proposer is null || opponent is null) return false;
        if (DeliveryBlockReason(campaign, war, proposer, opponent).Length > 0) return false;
        if (!opponent.IsHuman)
        {
            PeaceNegotiation.Resolve(campaign, war, proposer.Id, terms);
            return war.Ended;
        }
        if (!proposer.IsHuman && ToPlayerBlockReason(campaign, war, false, null, terms).Length > 0) return false;
        war.PendingOffer = null;
        war.PendingOfferIsWritten = false;
        war.PendingOfferTerms = terms;
        war.PendingOfferFromId = proposer.Id;
        war.PendingOfferDay = campaign.ElapsedDays;
        war.PendingOfferExpiresDay = campaign.ElapsedDays + OfferStandsForDays;
        war.LastPeaceProposalDay = campaign.ElapsedDays;
        if (!proposer.IsHuman) war.LastOfferToPlayerDay = campaign.ElapsedDays;
        war.LastNegotiation = $"{proposer.Name} has asked to end the war";
        campaign.MarkStrategicChange();
        return true;
    }

    /// <summary>
    /// Why a computer government may not put this settlement to a player in this war today,
    /// or "" when it may.
    ///
    /// Players complained, rightly, of being pestered: a war against a weaker neighbour
    /// produced a letter every few weeks, each one the same terms on a new sheet of paper,
    /// because the proposer's own interval (AiPeaceProposalInterval) restarts at every
    /// refusal and its war score sends it back to the table the moment that runs out. Two
    /// computer governments never notice, because their offers are answered in the same
    /// step; a person has to stop and read every one. So, for a player and only for a
    /// player, two rules on top of the proposer's own:
    ///
    /// - one offer per AiPeaceOfferToPlayerIntervalDays per war, counted from the last offer
    ///   delivered and from the last one refused or left to lapse, whichever is later;
    /// - after a refusal, the next offer has to be materially different from the refused one
    ///   (MateriallyDifferent) - other terms, or a border that has moved - unless
    ///   AiPeaceOfferRepeatRefusedDays have passed, by which time the war may have changed
    ///   enough that asking the same thing again is a fair question.
    ///
    /// Nothing here touches what an offer says, only whether it is sent; the same rules hold
    /// for every country, and a war between two computer governments never reaches here.
    /// </summary>
    public static string ToPlayerBlockReason(Campaign campaign, StrategicWar war, bool written,
        PeaceDemand demand, PeaceTerms terms)
    {
        if (campaign is null || war is null) return Loc.Get("There is no war to end");
        var defines = campaign.Defines;
        var today = campaign.ElapsedDays;
        var lastAsked = System.Math.Max(war.LastOfferToPlayerDay, war.RefusedOfferDay);
        if (lastAsked >= 0 && today - lastAsked < System.Math.Max(0, defines.AiPeaceOfferToPlayerIntervalDays))
            return "interval";
        if (war.RefusedOfferDay >= 0 &&
            !MateriallyDifferent(war.RefusedOfferWasWritten, war.RefusedOffer, war.RefusedOfferTerms,
                written, demand, terms, defines.AiPeaceOfferMaterialProvinceShare) &&
            (defines.AiPeaceOfferRepeatRefusedDays <= 0 || today - war.RefusedOfferDay < defines.AiPeaceOfferRepeatRefusedDays))
            return "already refused";
        return "";
    }

    /// <summary>
    /// Whether a new offer is a different offer from a refused one, as the person reading it
    /// would judge it. A standing term against a written settlement, or one standing term
    /// against another, is always different. Two written settlements are the same paper
    /// unless something in the package changed - the beneficiary, a released country instead
    /// of an annexation, an indemnity, a trade treaty, disarmament, the humiliation, the long
    /// truce, the subject arrangement - or the ground changed by at least the given share of
    /// the larger of the two province lists (and by one province at the very least).
    /// </summary>
    public static bool MateriallyDifferent(bool refusedWritten, PeaceDemand refused, PeaceTerms refusedTerms,
        bool written, PeaceDemand offer, PeaceTerms terms, float provinceShare)
    {
        if (refusedWritten != written) return true;
        if (!written) return refusedTerms != terms;
        if (refused is null || offer is null) return (refused is null) != (offer is null);
        if (refused.BeneficiaryId != offer.BeneficiaryId || refused.ReleaseRegions != offer.ReleaseRegions ||
            refused.Reparations != offer.Reparations || refused.TradeConcessions != offer.TradeConcessions ||
            refused.Demilitarise != offer.Demilitarise || refused.Humiliate != offer.Humiliate ||
            refused.LongTruce != offer.LongTruce || (refused.Subjugate ?? "") != (offer.Subjugate ?? ""))
            return true;
        var changed = 0;
        foreach (var province in offer.Provinces)
            if (!refused.Provinces.Contains(province)) changed++;
        foreach (var province in refused.Provinces)
            if (!offer.Provinces.Contains(province)) changed++;
        if (changed == 0) return false;
        var larger = System.Math.Max(refused.Provinces.Count, offer.Provinces.Count);
        var needed = System.Math.Max(1, (int)System.Math.Ceiling(System.Math.Max(0f, provinceShare) * larger));
        return changed >= needed;
    }

    /// <summary>
    /// Keep the offer a player has just refused or let lapse, so that the next one must be a
    /// different offer (ToPlayerBlockReason). Only an offer a computer government made is
    /// remembered: what one player says to another is between them.
    /// </summary>
    private static void RememberRefusal(Campaign campaign, StrategicWar war, int proposerId, bool written,
        PeaceDemand demand, PeaceTerms terms)
    {
        if (campaign?.NationById(proposerId) is not { IsHuman: false }) return;
        war.RefusedOfferDay = campaign.ElapsedDays;
        war.RefusedOfferWasWritten = written;
        war.RefusedOfferTerms = written ? PeaceTerms.WhitePeace : terms;
        war.RefusedOffer = written ? demand : null;
    }

    /// <summary>Whether this war is waiting on an answer from this country.</summary>
    public static bool AwaitingAnswer(StrategicWar war, int nationId) =>
        war is { Ended: false } && war.PendingOfferFromId >= 0 &&
        war.Opposes(war.PendingOfferFromId, nationId);

    // The current offer has one recipient, not a multilateral consent ballot.
    // Do not let AI settle over a human coalition ally's head. Such coalitions
    // can still be settled manually; a full shared approval UI is separate work.
    private static bool CanAddress(Campaign campaign, StrategicWar war, Nation proposer, Nation opponent) =>
        war.Opposes(proposer.Id, opponent.Id) && !campaign.Nations.Any(nation =>
            nation.IsHuman && war.Involves(nation.Id) && nation.Id != proposer.Id && nation.Id != opponent.Id);

    /// <summary>Every offer waiting on this country, in the order they were made.</summary>
    public static List<StrategicWar> AwaitingAnswer(Campaign campaign, int nationId)
    {
        var waiting = new List<StrategicWar>();
        if (campaign is null) return waiting;
        foreach (var war in campaign.Wars)
            if (AwaitingAnswer(war, nationId))
                waiting.Add(war);
        waiting.Sort((left, right) => left.PendingOfferDay.CompareTo(right.PendingOfferDay));
        return waiting;
    }

    /// <summary>
    /// What is being asked for, in the words a foreign minister would use. Regions rather
    /// than province counts, because "eleven provinces" is a number and "Donetsk and most of
    /// Luhansk" is a decision.
    /// </summary>
    public static string Describe(Campaign campaign, StrategicWar war)
    {
        if (campaign is null || war is null || war.PendingOfferFromId < 0) return "";
        var proposer = campaign.NationById(war.PendingOfferFromId);
        var name = proposer?.Name ?? "The other side";
        if (!war.PendingOfferIsWritten)
            return $"{name} proposes {PeaceNegotiation.Description(war.PendingOfferTerms).TrimEnd('.')}.";

        var demand = war.PendingOffer;
        var topology = campaign.ProvinceMap;
        if (demand is null || topology is null) return $"{name} proposes a settlement.";

        var lines = new List<string>();
        var beneficiary = PeaceConference.Beneficiary(campaign, demand);
        if (demand.BeneficiaryId >= 0 && beneficiary is not null)
            lines.Add(Loc.Format("Peace beneficiary: {0}.", beneficiary.DisplayName));
        if (demand.Provinces.Count > 0)
        {
            var perState = new Dictionary<int, int>();
            foreach (var province in demand.Provinces)
            {
                var state = (int)topology.ProvinceToState[province];
                perState[state] = perState.GetValueOrDefault(state) + 1;
            }
            var named = perState
                .OrderByDescending(entry => entry.Value)
                .Take(6)
                .Select(entry =>
                {
                    var state = campaign.States.Find(candidate => candidate.MapIndex == entry.Key);
                    var whole = PeaceConference.ProvincesOfState(topology, entry.Key).Count;
                    var label = state?.Name ?? $"region {entry.Key}";
                    return whole > 0 && entry.Value >= whole ? label : $"part of {label}";
                })
                .ToList();
            var rest = perState.Count - named.Count;
            if (rest > 0) named.Add($"and {rest:N0} more region(s)");
            lines.Add(demand.ReleaseRegions ? Loc.Format("They require an independent country in {0}.", string.Join(", ", named))
                : $"They demand {demand.Provinces.Count:N0} province(s): {string.Join(", ", named)}.");
        }

        var extras = new List<string>();
        if (demand.Reparations) extras.Add("an indemnity");
        if (demand.TradeConcessions) extras.Add("a trade agreement");
        if (demand.Demilitarise) extras.Add("your army stripped of its equipment");
        if (demand.Humiliate) extras.Add("the terms read out in public");
        if (demand.LongTruce) extras.Add("a ten-year truce instead of five");
        if (demand.Subjugate.Length > 0)
            extras.Add(demand.BeneficiaryId >= 0 && beneficiary is not null
                ? Loc.Format("your country to answer to {0} as a {1}", beneficiary.DisplayName,
                    Loc.Get(SubjectCatalog.Of(demand.Subjugate)?.Name ?? demand.Subjugate))
                : $"your country to answer to theirs as a " +
                  $"{SubjectCatalog.Of(demand.Subjugate)?.Name?.ToLowerInvariant() ?? demand.Subjugate}");
        if (extras.Count > 0) lines.Add($"They also want {string.Join(", ", extras)}.");
        if (lines.Count == 0) lines.Add("They propose an end to the war on the pre-war border.");
        return $"{name} has put a settlement to you.\n\n{string.Join("\n", lines)}";
    }

    /// <summary>Sign a still-valid offer, or send it away. A stale or unsafe offer stays on the table.</summary>
    public static bool Answer(Campaign campaign, StrategicWar war, int answeringNationId, bool accept)
    {
        if (!AwaitingAnswer(war, answeringNationId)) return false;
        var written = war.PendingOfferIsWritten;
        var demand = war.PendingOffer;
        var terms = war.PendingOfferTerms;
        var proposerId = war.PendingOfferFromId;
        if (accept)
        {
            var proposer = campaign?.NationById(proposerId);
            var opponent = campaign?.NationById(answeringNationId);
            if (proposer is null || opponent is null || !CanAddress(campaign, war, proposer, opponent)) return false;
            if (written && (demand is null || demand.ProposerId != proposerId || demand.WarId != war.Id ||
                PeaceConference.BlockReason(campaign, war, demand).Length > 0)) return false;
        }
        Clear(war);

        if (!accept)
        {
            // Refusing costs nothing but time. The proposer waits out its own interval
            // before asking again, which is what LastPeaceProposalDay already does.
            war.LastPeaceProposalDay = campaign.ElapsedDays;
            RememberRefusal(campaign, war, proposerId, written, demand, terms);
            war.LastNegotiation = "The offer was refused";
            campaign.MarkStrategicChange();
            return true;
        }

        if (written && demand is not null) return PeaceConference.Resolve(campaign, war, demand);
        PeaceNegotiation.Resolve(campaign, war, proposerId, terms);
        return war.Ended;
    }

    /// <summary>Withdraw offers nobody answered. Called once a day; usually does nothing.</summary>
    public static void Expire(Campaign campaign)
    {
        if (campaign is null) return;
        foreach (var war in campaign.Wars)
        {
            if (war.PendingOfferFromId < 0) continue;
            if (!war.Ended && campaign.ElapsedDays < war.PendingOfferExpiresDay) continue;
            var lapsed = !war.Ended;
            // Ignoring a letter is an answer too: the lapsed terms are remembered exactly as
            // refused ones are, so silence is not met with the same letter again.
            if (lapsed)
                RememberRefusal(campaign, war, war.PendingOfferFromId, war.PendingOfferIsWritten,
                    war.PendingOffer, war.PendingOfferTerms);
            Clear(war);
            if (!lapsed) continue;
            war.LastNegotiation = "The offer lapsed unanswered";
            campaign.MarkStrategicChange();
        }
    }

    private static void Clear(StrategicWar war)
    {
        war.PendingOffer = null;
        war.PendingOfferIsWritten = false;
        war.PendingOfferFromId = -1;
        war.PendingOfferDay = -1;
        war.PendingOfferExpiresDay = -1;
    }
}

/// <summary>
/// A player's answer to a settlement somebody put to them.
///
/// A command rather than a method call because every other decision a player makes is one:
/// it is what carries the answer across a multiplayer wire, and what keeps two peers ending
/// the same war on the same day.
/// </summary>
public sealed class AnswerPeaceOfferCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int WarId { get; init; }
    public bool Accept { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var war = campaign.Wars.Find(candidate => candidate.Id == WarId);
        if (war is not null) PeaceOffers.Answer(campaign, war, IssuerId, Accept);
    }
}
