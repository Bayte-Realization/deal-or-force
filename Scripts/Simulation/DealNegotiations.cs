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
    [JsonIgnore] public Ultimatum.Deal Terms => new(From, To);
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
        From = Copy(offer.From), To = Copy(offer.To), State = offer.State, Reason = offer.Reason ?? "",
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
            return new(false, Loc.Get("No further negotiation numbers are available."));
        // Counterproposals to governments still use the same current AI assessment.
        // A refusal keeps the original paper on the table.
        var assessment = Ultimatum.AssessNegotiatedDeal(c, from, to, terms);
        if (!to.IsHuman && !assessment.Acceptable) return new(false, assessment.Reason);
        var paper = new DealOffer
        {
            Id = c.NextDealOfferId++, FromId = from.Id, ToId = to.Id,
            From = Copy(terms.From), To = Copy(terms.To), CreatedDay = c.ElapsedDays,
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
        if (to.IsHuman) return new(false, Loc.Get("Offer delivered; awaiting a reply."), paper.Id);
        var result = Ultimatum.ResolveAgreedDeal(c, from, to, paper.Terms);
        Close(c, paper, result.Accepted ? DealOfferState.Accepted : DealOfferState.Invalidated,
            result.Accepted ? "The bargain is signed" : result.Note);
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
        // Expiration is administrative cleanup, also when deals are switched off.
        // Don't revalidate every territorial raster per frame or rewrite open drafts.
        foreach (var paper in c.DealOffers.Where(o => o.Pending).ToArray())
        {
            if (paper.ExpiresDay <= c.ElapsedDays) Close(c, paper, DealOfferState.Expired, "The offer has expired.");
            else if (!c.FeatureEnabled("deals")) Close(c, paper, DealOfferState.Invalidated, "Deals are disabled in this campaign.");
            else if (c.NationById(paper.ToId)?.IsHuman != true)
                Close(c, paper, DealOfferState.Invalidated, "The addressed country is no longer controlled by a player.");
        }
        TrimHistory(c);
    }

    private static void Close(Campaign c, DealOffer paper, DealOfferState state, string reason)
    {
        paper.State = state; paper.ClosedDay = c.ElapsedDays; paper.Reason = reason;
        c.DealInboxRevision++;
        c.MarkStrategicChange();
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

    public static void Restore(Campaign c, IEnumerable<DealOffer> papers, int nextId)
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
            if (paper.Pending && (active >= MaxActiveOffers ||
                !pendingPairs.Add((Math.Min(paper.FromId, paper.ToId), Math.Max(paper.FromId, paper.ToId))))) continue;
            if (paper.Pending) active++;
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
