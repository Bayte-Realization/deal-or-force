using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class Corridors
{
    /// <summary>Two countries each hold a small piece of the other. Reading this sends nothing.</summary>
    public readonly record struct EnclaveSwapReport(string Partner, int OurProvinces, int TheirProvinces);

    public static bool TryReportSwap(Campaign c, Nation nation, out EnclaveSwapReport report)
    {
        report = default;
        if (!FindSwap(c, nation, out var other, out var give, out var take, out var ours, out var theirs)) return false;
        if (!Approachable(c, nation, other) || Fit(c, nation, other, give, take) is null) return false;
        report = new(other.DisplayName, ours, theirs);
        return true;
    }

    /// <summary>Lay the exchange on the table. Empty when the paper is sent or signed.</summary>
    public static string ProposeSwap(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return Loc.Get("Choose a country.");
        if (!MechanicFeatures.SystemEnabled(c, nameof(Corridors)))
            return Loc.Get("Joining detached land is switched off.");
        if (!TryReportSwap(c, nation, out _)) return Loc.Get("There is no enclave to exchange.");
        var before = c.DealOffers.Count;
        if (!TryExchange(c, nation) && c.DealOffers.Count == before)
            return Loc.Get("They cannot be asked just now.");
        return "";
    }

    internal static bool TryExchange(Campaign c, Nation nation)
    {
        if (!MechanicFeatures.SystemEnabled(c, nameof(Corridors))) return false;
        if (!FindSwap(c, nation, out var other, out var give, out var take, out _, out _)) return false;
        if (!Approachable(c, nation, other)) return false;
        var deal = Fit(c, nation, other, give, take);
        if (deal is null) return false;
        Remember(c, nation, other);
        var outcome = DealNegotiations.Submit(c, nation, other, deal);
        return outcome.Accepted || c.DealOffers.Any(offer =>
            offer.Pending && offer.FromId == nation.Id && offer.ToId == other.Id);
    }

    private static bool FindSwap(Campaign c, Nation nation, out Nation other, out int[] give, out int[] take,
        out int ourCount, out int theirCount)
    {
        other = null;
        give = null;
        take = null;
        ourCount = 0;
        theirCount = 0;
        if (c?.ProvinceMap is null || nation is null || nation.Population <= 0 || nation.Movement is not null) return false;
        var parts = Pieces(c, nation);
        if (parts.Count < 2) return false;
        var limit = Math.Max(1, c.Defines.EnclaveSwapMaxProvinces);
        var authored = ProvinceOwnership.ProvinceSetOf(nation.Tag);
        var best = int.MaxValue;
        foreach (var piece in parts.Skip(1))
        {
            if (piece.Count > limit || Authored(piece, authored)) continue;
            var regions = OfferRegions(c, nation, piece);
            if (regions is null || !Dominant(c, nation, piece, out var holder)) continue;
            if (holder.Movement is not null || holder.Population <= 0) continue;
            var theirParts = Pieces(c, holder);
            if (theirParts.Count < 2) continue;
            var theirAuthored = ProvinceOwnership.ProvinceSetOf(holder.Tag);
            foreach (var theirs in theirParts.Skip(1))
            {
                if (theirs.Count > limit || Authored(theirs, theirAuthored)) continue;
                var back = OfferRegions(c, holder, theirs);
                if (back is null || !Dominant(c, holder, theirs, out var around) || around.Id != nation.Id) continue;
                var size = piece.Count + theirs.Count;
                if (size >= best) continue;
                best = size;
                other = holder;
                give = regions;
                take = back;
                ourCount = piece.Count;
                theirCount = theirs.Count;
            }
        }
        return other is not null;
    }

    private static int[] OfferRegions(Campaign c, Nation nation, HashSet<int> piece)
    {
        var regions = WholeRegions(c, nation, piece);
        if (regions.Length == 0) return null;
        foreach (var id in regions)
        {
            if (id == nation.CapitalStateMapIndex) return null;
            var state = c.States.Find(candidate => candidate.MapIndex == id);
            if (state is not null && TerritorialClaims.TitleOf(c, nation, state) == TerritorialTitle.Homeland) return null;
        }
        return regions;
    }

    private static bool Dominant(Campaign c, Nation nation, HashSet<int> piece, out Nation holder)
    {
        holder = null;
        var map = c.ProvinceMap;
        var around = new Dictionary<int, int>();
        foreach (var province in piece)
            foreach (var next in map.Neighbors[province])
            {
                if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                var owner = map.ProvinceToNation[next];
                if (owner == 0 || owner == (ushort)nation.MapIndex) continue;
                around[owner] = around.GetValueOrDefault(owner) + 1;
            }
        if (around.Count == 0) return false;
        var ordered = around.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key).ToArray();
        if (ordered.Length > 1 && ordered[0].Value == ordered[1].Value) return false;
        holder = c.Nations.Find(candidate => candidate.MapIndex == ordered[0].Key);
        return holder is not null && holder.Id != nation.Id;
    }

    private static Ultimatum.Deal Fit(Campaign c, Nation from, Nation to, int[] give, int[] take)
    {
        if (give is null || take is null || give.Length == 0 || take.Length == 0) return null;
        var share = M.Clamp01(c.Defines.CorridorTreasuryShare);
        var deal = new Ultimatum.Deal(new Ultimatum.Side { Regions = give }, new Ultimatum.Side { Regions = take });
        var payingThem = Cheque(c, from, to, deal);
        if (!float.IsFinite(payingThem) || payingThem < 0f) return null;
        if (payingThem > 0f)
        {
            var cash = MathF.Max(0.01f, payingThem * 1.05f);
            if (cash > MathF.Max(0f, from.Treasury) * share) return null;
            deal = deal with { From = deal.From with { Cash = cash } };
        }
        var assessment = Ultimatum.AssessNegotiatedDeal(c, from, to, deal);
        if (!assessment.Valid || !assessment.Acceptable) return null;
        if (assessment.FromValue >= 0f) return deal;
        if (deal.From.Cash > 0f) return null;
        var inverted = new Ultimatum.Deal(new Ultimatum.Side { Regions = take }, new Ultimatum.Side { Regions = give });
        var payingUs = Cheque(c, to, from, inverted);
        if (!float.IsFinite(payingUs) || payingUs <= 0f) return null;
        var back = MathF.Max(0.01f, payingUs * 1.05f);
        var theirShare = float.IsFinite(c.Defines.DealMaxTreasuryShare)
            ? Math.Clamp(c.Defines.DealMaxTreasuryShare, 0f, 1f) : 1f;
        if (back > MathF.Max(0f, to.Treasury) * theirShare) return null;
        deal = deal with { To = deal.To with { Cash = back } };
        assessment = Ultimatum.AssessNegotiatedDeal(c, from, to, deal);
        return assessment.Valid && assessment.Acceptable && assessment.FromValue >= 0f ? deal : null;
    }

    private static float Cheque(Campaign c, Nation from, Nation to, Ultimatum.Deal deal)
    {
        var assessment = Ultimatum.AssessNegotiatedDeal(c, from, to, deal);
        if (!assessment.Valid) return -1f;
        if (assessment.Acceptable) return 0f;
        var shortfall = assessment.GrudgePenalty - assessment.ToValuation.Total;
        var weight = Ultimatum.CashWeight(to);
        if (!float.IsFinite(shortfall) || !float.IsFinite(weight) || weight <= 0f) return -1f;
        var needed = shortfall / weight + 0.01f;
        var room = MathF.Max(0f, from.Treasury) - deal.From.Cash + deal.To.Cash;
        return needed <= room ? MathF.Ceiling(needed * 100f) / 100f : -1f;
    }
}
