using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int DealOfferDays { get; set; } = 30;
    public int DealInboxLimit { get; set; } = 8;
    public int DealHistoryLimit { get; set; } = 128;
    public int DealCounterLimit { get; set; } = 4;
}

public enum DealOfferState { Pending, Accepted, Declined, Withdrawn, Expired, Invalidated, Superseded }
public enum DealReply { Accept, Decline, Withdraw }

/// <summary>An immutable package from the recipient's point of view: changing terms
/// creates another numbered offer, never silently edits something already reviewed.</summary>
public sealed class DealOffer
{
    public int Id, FromId, ToId, CreatedDay, ExpiresDay, ClosedDay = -1;
    public int ParentOfferId, CounterDepth;
    public Ultimatum.Side From = new(), To = new();
    public int[] FromProvinces = Array.Empty<int>(), ToProvinces = Array.Empty<int>();
    public DealOfferState State;
    public string Reason = "";
    /// <summary>Answered by the addressed player's standing rule (DealAnswer), not by the player's own hand.</summary>
    public bool ByRule;
    /// <summary>The trade agreement on the paper (TradeTerms), a term of the pair; null for none.
    /// A paper written before terms existed carries none and reads its sides' old ticks instead.</summary>
    public TradeTerms Trade;
    [JsonIgnore] public Ultimatum.Deal Terms => new(From, To) { Trade = Trade };
    [JsonIgnore] public bool Pending => State == DealOfferState.Pending;
}

public sealed partial class Campaign
{
    public readonly List<DealOffer> DealOffers = new();
    public int NextDealOfferId = 1;
    [JsonIgnore] public int DealInboxRevision;
}

/// <summary>Delivery is neither settlement nor credit. No cash or land is reserved:
/// acceptance revalidates live funds, control, treaties and peace atomically. Pending
/// papers and the next ID travel in saves, snapshots and deterministic fingerprints.</summary>
public static class DealNegotiations
{
    public const int MaxActiveOffers = 256;

    public static int Incoming(Campaign c, int nationId) => c?.DealOffers.Count(o =>
        o.Pending && o.ToId == nationId && o.ExpiresDay > c.ElapsedDays) ?? 0;

    public static Ultimatum.Side Copy(Ultimatum.Side side) => new()
    {
        Regions = (side?.Regions ?? Array.Empty<int>()).Distinct().OrderBy(id => id).ToArray(),
        Cash = side?.Cash ?? 0, ReleaseRegions = side?.ReleaseRegions ?? false,
        LiftEmbargo = side?.LiftEmbargo ?? false, NonAggression = side?.NonAggression ?? false,
        TradeAgreement = side?.TradeAgreement ?? false
    };

    public static DealOffer Copy(DealOffer offer) => new()
    {
        Id = offer.Id, FromId = offer.FromId, ToId = offer.ToId,
        CreatedDay = offer.CreatedDay, ExpiresDay = offer.ExpiresDay, ClosedDay = offer.ClosedDay,
        ParentOfferId = offer.ParentOfferId, CounterDepth = offer.CounterDepth,
        From = Copy(offer.From), To = Copy(offer.To), State = offer.State, Reason = offer.Reason ?? "", ByRule = offer.ByRule,
        Trade = TradeTerms.Copy(offer.Trade),
        FromProvinces = (offer.FromProvinces ?? Array.Empty<int>()).ToArray(),
        ToProvinces = (offer.ToProvinces ?? Array.Empty<int>()).ToArray()
    };

    public static Ultimatum.DealOutcome Submit(Campaign c, Nation from, Nation to,
        Ultimatum.Deal terms, int parentId = 0)
    {
        if (!Ultimatum.ValidateNegotiatedDeal(c, from, to, terms, out var refusal)) return new(false, refusal);
        var parent = parentId == 0 ? null : c.DealOffers.FirstOrDefault(o => o.Id == parentId);
        if (parentId != 0 && (parent is null || !parent.Pending || parent.ToId != from.Id || parent.FromId != to.Id))
            return new(false, Loc.Get("This offer is no longer available for a counterproposal."));
        if (parent is not null && parent.ExpiresDay <= c.ElapsedDays)
            return new(false, Loc.Get("The offer has expired."));
        var depth = parent is null ? 0 : parent.CounterDepth + 1;
        if (depth > Math.Clamp(c.Defines.DealCounterLimit, 0, 16))
            return new(false, Loc.Get("The negotiation has reached its counterproposal limit. Answer or withdraw it."));
        if (c.DealOffers.Any(o => o.Pending && o.ExpiresDay > c.ElapsedDays && o.Id != parentId &&
            ((o.FromId == from.Id && o.ToId == to.Id) || (o.FromId == to.Id && o.ToId == from.Id))))
            return new(false, Loc.Get("An offer between these countries already awaits a reply."));
        if (to.IsHuman && (Incoming(c, to.Id) - (parent?.ToId == to.Id ? 1 : 0) >= Math.Clamp(c.Defines.DealInboxLimit, 1, 32) ||
            c.DealOffers.Count(o => o.Pending) - (parent is null ? 0 : 1) >= MaxActiveOffers))
            return new(false, Loc.Get("Their diplomatic inbox is full. Wait for a reply."));
        if (c.NextDealOfferId <= 0 || c.NextDealOfferId == int.MaxValue)
            return new(false, Loc.Get("No more offers can be made in this campaign."));
        // Counterproposals to governments still use the same current AI assessment.
        // A refusal keeps the original paper on the table.
        var assessment = Ultimatum.AssessNegotiatedDeal(c, from, to, terms);
        // Counted for the bench (DealTally), never read by the world.
        var tallyKey = $"deal.{DealTally.KindOf(terms)}.{DealTally.Seat(from)}>{DealTally.Seat(to)}";
        DealTally.Count(tallyKey + ".put");
        if (!to.IsHuman && DealAnswer.Decide(c, to, from, terms, assessment).Verdict != DealVerdict.Accept)
        {
            DealTally.Count(tallyKey + ".declined");
            if (assessment.Valid) RememberTradeRefusal(c, from, to, terms);
            return new(false, assessment.Reason);
        }
        var paper = new DealOffer
        {
            Id = c.NextDealOfferId++, FromId = from.Id, ToId = to.Id,
            From = Copy(terms.From), To = Copy(terms.To), Trade = TradeTerms.Copy(terms.Trade), CreatedDay = c.ElapsedDays,
            ExpiresDay = (int)Math.Min(int.MaxValue, (long)c.ElapsedDays + Math.Clamp(c.Defines.DealOfferDays, 1, 365)),
            ParentOfferId = parentId, CounterDepth = depth,
            FromProvinces = Ground(c, terms.From), ToProvinces = Ground(c, terms.To)
        };
        c.DealOffers.Add(paper);
        c.DealInboxRevision++;
        if (parent is not null) Close(c, parent, DealOfferState.Superseded, "Replaced by a counterproposal.");
        CampaignDiplomacy.Get(c, from.Id, to.Id).LastActionDay = c.ElapsedDays;
        c.MarkStrategicChange();
        TrimHistory(c);
        if (to.IsHuman)
        {
            // A player's standing rule answers at delivery, by the same assessment.
            if (DealAnswer.HasStandingRules(to) && AnswerByRule(c, paper, assessment) is { } ruled) return ruled;
            return new(false, Loc.Get("Offer delivered; awaiting a reply."), paper.Id);
        }
        var result = Ultimatum.ResolveAgreedDeal(c, from, to, paper.Terms);
        Close(c, paper, result.Accepted ? DealOfferState.Accepted : DealOfferState.Invalidated,
            result.Accepted ? "The bargain is signed" : result.Note);
        DealTally.Count(tallyKey + (result.Accepted ? ".signed" : ".invalid"));
        return result;
    }

    public static Ultimatum.DealOutcome Respond(Campaign c, int issuerId, int offerId, DealReply reply)
    {
        var paper = c?.DealOffers.FirstOrDefault(o => o.Id == offerId);
        if (paper is null || !paper.Pending || !Enum.IsDefined(reply))
            return new(false, Loc.Get("This offer is no longer awaiting a reply."));
        var authorised = reply == DealReply.Withdraw ? issuerId == paper.FromId : issuerId == paper.ToId;
        if (!authorised) return new(false, Loc.Get("Only the addressed country may answer; only the proposer may withdraw."));
        if (paper.ExpiresDay <= c.ElapsedDays)
        {
            Close(c, paper, DealOfferState.Expired, "The offer has expired.");
            return new(false, Loc.Get(paper.Reason));
        }
        if (reply != DealReply.Accept)
        {
            if (reply == DealReply.Decline)
                RememberTradeRefusal(c, c.NationById(paper.FromId), c.NationById(paper.ToId), paper.Terms);
            Close(c, paper, reply == DealReply.Decline ? DealOfferState.Declined : DealOfferState.Withdrawn,
                reply == DealReply.Decline ? "The offer was declined. Nothing was transferred." : "The offer was withdrawn. Nothing was transferred.");
            return new(false, Loc.Get(paper.Reason));
        }
        var from = c.NationById(paper.FromId);
        var to = c.NationById(paper.ToId);
        // Human acceptance is an explicit choice, not a predicted utility threshold.
        if (to?.IsHuman != true)
            return new(false, Loc.Get("The addressed country is no longer controlled by a player."));
        if (!Ultimatum.ValidateNegotiatedDeal(c, from, to, paper.Terms, out var refusal))
        {
            Close(c, paper, DealOfferState.Invalidated, refusal);
            return new(false, refusal);
        }
        if (!SameGround(c, paper))
        {
            Close(c, paper, DealOfferState.Invalidated, "The selected regions changed shape. Send a new offer for the current territory.");
            return new(false, Loc.Get(paper.Reason));
        }
        var result = Ultimatum.ResolveAgreedDeal(c, from, to, paper.Terms);
        Close(c, paper, result.Accepted ? DealOfferState.Accepted : DealOfferState.Invalidated,
            result.Accepted ? "The bargain is signed" : result.Note);
        return result;
    }

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same inbox sweep, yielded between papers.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        // Expiration is administrative cleanup, also when deals are switched off.
        // Don't revalidate every territorial raster per frame or rewrite open drafts.
        var ticked = 0;
        foreach (var paper in c.DealOffers.Where(o => o.Pending).ToArray())
        {
            if (++ticked % 32 == 0) yield return 0;
            if (paper.ExpiresDay <= c.ElapsedDays) Close(c, paper, DealOfferState.Expired, "The offer has expired.");
            else if (!c.FeatureEnabled("deals")) Close(c, paper, DealOfferState.Invalidated, "Deals are disabled in this campaign.");
            else if (c.NationById(paper.ToId)?.IsHuman != true)
                Close(c, paper, DealOfferState.Invalidated, "The addressed country is no longer controlled by a player.");
        }
        yield return 0;
        // Papers that waited before a standing rule was set: oldest first, a batch a day. Only a
        // country a player answers for has a paper waiting at all, so a world without players
        // never gets past the first test.
        if (c.FeatureEnabled("deals") && c.Nations.Any(n => n.IsHuman && DealAnswer.HasStandingRules(n)))
        {
            var batch = Math.Clamp(c.Defines.AutoAnswerDailyBatch, 0, 1024);
            var closed = 0;
            foreach (var paper in c.DealOffers.Where(o => o.Pending && o.ExpiresDay > c.ElapsedDays)
                         .OrderBy(o => o.Id).ToArray())
            {
                if (closed >= batch) break;
                var to = c.NationById(paper.ToId);
                if (to?.IsHuman != true || !DealAnswer.HasStandingRules(to)) continue;
                if (DealAnswer.ModeFor(to, DealAnswerKinds.Classify(paper.Terms)) == AutoAnswerMode.Ask) continue;
                AnswerByRule(c, paper);
                if (!paper.Pending) closed++;
                yield return 0;
            }
        }
        TrimHistory(c);
    }

    /// <summary>
    /// One waiting paper answered by its addressee's standing rule, or null when the rule leaves
    /// it to the player. Acceptance goes the way a player's own does - live funds, territory and
    /// treaties checked again, then <see cref="Ultimatum.ResolveAgreedDeal"/>.
    /// </summary>
    internal static Ultimatum.DealOutcome AnswerByRule(Campaign c, DealOffer paper, Ultimatum.DealAssessment known = null)
    {
        if (c is null || paper is null || !paper.Pending || paper.ExpiresDay <= c.ElapsedDays) return null;
        var from = c.NationById(paper.FromId);
        var to = c.NationById(paper.ToId);
        if (from is null || to?.IsHuman != true) return null;
        var decision = DealAnswer.Decide(c, to, from, paper.Terms, known);
        if (decision.Verdict == DealVerdict.Wait) return null;
        Ultimatum.DealOutcome result;
        if (decision.Verdict == DealVerdict.Decline)
        {
            paper.ByRule = true;
            RememberTradeRefusal(c, from, to, paper.Terms);
            Close(c, paper, DealOfferState.Declined, decision.Reason);
            result = new(false, Loc.Get(paper.Reason));
        }
        else
        {
            // The same consent boundary RespondDealCommand passes for a signature.
            if (!MechanicFeatures.Allows(c, new RespondDealCommand { IssuerId = to.Id, OfferId = paper.Id, Reply = DealReply.Accept }))
                return null;
            paper.ByRule = true;
            if (!Ultimatum.ValidateNegotiatedDeal(c, from, to, paper.Terms, out var refusal))
            {
                Close(c, paper, DealOfferState.Invalidated, refusal);
                result = new(false, refusal);
            }
            else if (!SameGround(c, paper))
            {
                Close(c, paper, DealOfferState.Invalidated, "The selected regions changed shape. Send a new offer for the current territory.");
                result = new(false, Loc.Get(paper.Reason));
            }
            else
            {
                var signed = Ultimatum.ResolveAgreedDeal(c, from, to, paper.Terms);
                Close(c, paper, signed.Accepted ? DealOfferState.Accepted : DealOfferState.Invalidated,
                    signed.Accepted ? decision.Reason : signed.Note);
                result = signed;
            }
        }
        DealAnswer.Remember(c, to, new AutoAnswerNote
        {
            Day = c.ElapsedDays, ProposerId = from.Id, OfferId = paper.Id, Kind = decision.Kind?.Id ?? "",
            Mode = decision.Mode, Accepted = paper.State == DealOfferState.Accepted, Benefit = decision.Benefit
        });
        return result;
    }

    /// <summary>
    /// Whether the latest paper from one country to another was answered today by the addressee's
    /// standing rule. Submit then returns neither a waiting paper nor a signature, which a proposer
    /// would otherwise read as "never asked" and ask again next week: the question was put and
    /// answered, so it waits as it would after any other answer.
    /// </summary>
    public static bool AnsweredByRuleToday(Campaign c, int fromId, int toId)
    {
        if (c is null) return false;
        DealOffer latest = null;
        foreach (var paper in c.DealOffers)
            if (paper.FromId == fromId && paper.ToId == toId && (latest is null || paper.Id > latest.Id)) latest = paper;
        return latest is { ByRule: true, Pending: false } && latest.ClosedDay == c.ElapsedDays;
    }

    /// <summary>
    /// A refused paper whose only term is a trade agreement buys the same silence a refused button
    /// does (AgreementRefusalDays), whoever refused it: one question, one answer, by either route.
    /// A renewal is not a new question and buys nothing.
    /// </summary>
    internal static void RememberTradeRefusal(Campaign c, Nation from, Nation to, Ultimatum.Deal terms)
    {
        if (c is null || from is null || to is null || !Ultimatum.IsTradeOnly(terms) ||
            Ultimatum.TradeOf(terms) is not { Renewal: false }) return;
        CampaignDiplomacy.Get(c, from.Id, to.Id).AgreementRefusedDay = c.ElapsedDays;
    }

    private static void Close(Campaign c, DealOffer paper, DealOfferState state, string reason)
    {
        paper.State = state; paper.ClosedDay = c.ElapsedDays; paper.Reason = reason;
        c.DealInboxRevision++;
        c.MarkStrategicChange();
        // The history kept is the same at every moment, not only after the day's Tick: a bargain
        // closed between two Ticks left one paper over the limit, a save taken then held it, and
        // reading the save back trimmed it - an untouched save that did not load as it was.
        TrimHistory(c);
    }

    private static int[] Ground(Campaign c, Ultimatum.Side side)
    {
        if ((side.Regions?.Length ?? 0) == 0) return Array.Empty<int>();
        var regions = side.Regions.ToHashSet();
        return Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
            .Where(p => regions.Contains(c.ProvinceMap.ProvinceToState[p])).ToArray();
    }

    public static bool SameGround(Campaign c, DealOffer paper) => c?.ProvinceMap is not null &&
        Ground(c, paper.From).SequenceEqual(paper.FromProvinces ?? Array.Empty<int>()) &&
        Ground(c, paper.To).SequenceEqual(paper.ToProvinces ?? Array.Empty<int>());

    private static void TrimHistory(Campaign c)
    {
        var keep = Math.Clamp(c.Defines.DealHistoryLimit, 0, 256);
        var old = c.DealOffers.Where(o => !o.Pending).OrderByDescending(o => o.Id).Skip(keep).ToHashSet();
        if (old.Count > 0) c.DealOffers.RemoveAll(old.Contains);
    }

    /// <param name="today">The saved day: the campaign's own clock is restored after the offers.</param>
    public static void Restore(Campaign c, IEnumerable<DealOffer> papers, int nextId, int today = -1)
    {
        var seen = new HashSet<int>();
        var pendingPairs = new HashSet<(int, int)>();
        var active = 0;
        var highestId = 0;
        c.DealOffers.Clear();
        foreach (var paper in (papers ?? Array.Empty<DealOffer>()).Take(MaxActiveOffers + 256))
        {
            if (paper is null || paper.Id <= 0 || paper.Id == int.MaxValue || !seen.Add(paper.Id) ||
                paper.FromId == paper.ToId || c.NationById(paper.FromId) is null || c.NationById(paper.ToId) is null ||
                paper.From is null || paper.To is null || !Enum.IsDefined(paper.State) ||
                !float.IsFinite(paper.From.Cash) || !float.IsFinite(paper.To.Cash) || paper.From.Cash < 0 || paper.To.Cash < 0 ||
                paper.CreatedDay < 0 || paper.ExpiresDay <= paper.CreatedDay ||
                paper.CounterDepth < 0 || paper.CounterDepth > 16 ||
                (paper.From.Regions?.Length ?? 0) > 100 || (paper.To.Regions?.Length ?? 0) > 100 ||
                (paper.FromProvinces?.Length ?? 0) > 200000 || (paper.ToProvinces?.Length ?? 0) > 200000) continue;
            highestId = Math.Max(highestId, paper.Id);
            // The same test Submit applies: an offer past its date but not yet closed by the day's
            // Tick does not stand in the way of a new one between the same pair, so a save taken
            // on that day holds both. Reading it back dropped the new one.
            var live = paper.Pending && paper.ExpiresDay > (today >= 0 ? today : c.ElapsedDays);
            if (live && (active >= MaxActiveOffers ||
                !pendingPairs.Add((Math.Min(paper.FromId, paper.ToId), Math.Max(paper.FromId, paper.ToId))))) continue;
            if (live) active++;
            c.DealOffers.Add(Copy(paper));
        }
        c.NextDealOfferId = Math.Max(Math.Max(1, nextId), highestId + 1);
        c.DealInboxRevision++;
        TrimHistory(c);
    }
}

public sealed class RespondDealCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int OfferId { get; init; }
    public DealReply Reply { get; init; }
    public Ultimatum.DealOutcome Outcome { get; private set; } = new(false, "Awaiting command application.");
    public void Apply(Campaign c)
    {
        // Refusal/withdrawal remain possible after a mechanic was disabled.
        if (Reply == DealReply.Accept && !MechanicFeatures.Allows(c, this))
        { Outcome = new(false, Loc.Get("This action is disabled in this campaign.")); return; }
        Outcome = DealNegotiations.Respond(c, IssuerId, OfferId, Reply);
    }
}
