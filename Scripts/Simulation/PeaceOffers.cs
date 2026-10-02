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

    /// <summary>
    /// Put a settlement to the other side. Against another AI that is the same act as
    /// signing it, because both answers are computed here; against a person it is left on
    /// the table with the reasoning attached.
    /// </summary>
    public static bool Put(Campaign campaign, StrategicWar war, Nation proposer, Nation opponent,
        PeaceDemand demand)
    {
        if (campaign is null || war is null || war.Ended || proposer is null || opponent is null) return false;
        if (!CanAddress(campaign, war, proposer, opponent)) return false;
        if (!opponent.IsHuman) return PeaceConference.Resolve(campaign, war, demand);
        if (war.PendingOffer is not null || war.PendingOfferFromId >= 0) return false;
        if (PeaceConference.BlockReason(campaign, war, demand).Length > 0) return false;

        // Store the same whole-region border that validation priced and acceptance will
        // transfer. The recipient's map and written summary read this saved offer.
        war.PendingOffer = PeaceConference.Round(campaign, war, demand);
        war.PendingOfferIsWritten = true;
        war.PendingOfferFromId = proposer.Id;
        war.PendingOfferDay = campaign.ElapsedDays;
        war.PendingOfferExpiresDay = campaign.ElapsedDays + OfferStandsForDays;
        war.LastPeaceProposalDay = campaign.ElapsedDays;
        war.LastNegotiation = $"{proposer.Name} has put a settlement to you";
        campaign.MarkStrategicChange();
        return true;
    }

    /// <summary>The same, for the terms that come off a menu rather than off the map.</summary>
    public static bool Put(Campaign campaign, StrategicWar war, Nation proposer, Nation opponent,
        PeaceTerms terms)
    {
        if (campaign is null || war is null || war.Ended || proposer is null || opponent is null) return false;
        if (!CanAddress(campaign, war, proposer, opponent)) return false;
        if (!opponent.IsHuman)
        {
            PeaceNegotiation.Resolve(campaign, war, proposer.Id, terms);
            return war.Ended;
        }
        if (war.PendingOffer is not null || war.PendingOfferFromId >= 0) return false;

        war.PendingOffer = null;
        war.PendingOfferIsWritten = false;
        war.PendingOfferTerms = terms;
        war.PendingOfferFromId = proposer.Id;
        war.PendingOfferDay = campaign.ElapsedDays;
        war.PendingOfferExpiresDay = campaign.ElapsedDays + OfferStandsForDays;
        war.LastPeaceProposalDay = campaign.ElapsedDays;
        war.LastNegotiation = $"{proposer.Name} has asked to end the war";
        campaign.MarkStrategicChange();
        return true;
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
            lines.Add($"They demand {demand.Provinces.Count:N0} province(s): {string.Join(", ", named)}.");
        }

        var extras = new List<string>();
        if (demand.Reparations) extras.Add("an indemnity");
        if (demand.TradeConcessions) extras.Add("a trade agreement");
        if (demand.Demilitarise) extras.Add("your army stripped of its equipment");
        if (demand.Humiliate) extras.Add("the terms read out in public");
        if (demand.LongTruce) extras.Add("a ten-year truce instead of five");
        if (demand.Subjugate.Length > 0)
            extras.Add($"your country to answer to theirs as a " +
                       $"{SubjectCatalog.Of(demand.Subjugate)?.Name?.ToLowerInvariant() ?? demand.Subjugate}");
        if (extras.Count > 0) lines.Add($"They also want {string.Join(", ", extras)}.");
        if (lines.Count == 0) lines.Add("They propose an end to the war on the pre-war border.");
        return $"{name} has put a settlement to you.\n\n{string.Join("\n", lines)}";
    }

    /// <summary>Sign what was offered, or send them away. Either way the table is cleared.</summary>
    public static bool Answer(Campaign campaign, StrategicWar war, int answeringNationId, bool accept)
    {
        if (!AwaitingAnswer(war, answeringNationId)) return false;
        var written = war.PendingOfferIsWritten;
        var demand = war.PendingOffer;
        var terms = war.PendingOfferTerms;
        var proposerId = war.PendingOfferFromId;
        Clear(war);

        if (!accept)
        {
            // Refusing costs nothing but time. The proposer waits out its own interval
            // before asking again, which is what LastPeaceProposalDay already does.
            war.LastPeaceProposalDay = campaign.ElapsedDays;
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
