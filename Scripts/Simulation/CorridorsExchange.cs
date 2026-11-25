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

    internal sealed class ExchangeStageResult
    {
        public bool Submitted;
    }

    internal sealed class SwapStageResult
    {
        public bool Found;
        public Nation Other;
        public int[] Give;
        public int[] Take;
        public int OurCount;
        public int TheirCount;
    }

    [ThreadStatic] private static Dictionary<int, int> _dominantAroundScratch;

    internal static bool TryExchange(Campaign c, Nation nation)
    {
        var state = new ExchangeStageResult();
        foreach (var _ in TryExchangeStages(c, nation, state)) { }
        return state.Submitted;
    }

    private static IEnumerable<int> TryExchangeStages(Campaign c, Nation nation, ExchangeStageResult result)
    {
        if (!MechanicFeatures.SystemEnabled(c, nameof(Corridors))) yield break;
        var swap = new SwapStageResult();
        foreach (var _ in FindSwapStages(c, nation, swap)) yield return 0;
        if (!swap.Found || !Approachable(c, nation, swap.Other)) yield break;
        if (Campaign.StageOverdue()) yield return 0;
        var deal = Fit(c, nation, swap.Other, swap.Give, swap.Take);
        if (deal is null) yield break;
        Remember(c, nation, swap.Other);
        var outcome = DealNegotiations.Submit(c, nation, swap.Other, deal);
        if (outcome.Accepted)
        {
            result.Submitted = true;
            yield break;
        }
        for (var i = 0; i < c.DealOffers.Count; i++)
        {
            var offer = c.DealOffers[i];
            if (offer.Pending && offer.FromId == nation.Id && offer.ToId == swap.Other.Id)
            {
                result.Submitted = true;
                yield break;
            }
        }
    }

    private static bool FindSwap(Campaign c, Nation nation, out Nation other, out int[] give, out int[] take,
        out int ourCount, out int theirCount)
    {
        var result = new SwapStageResult();
        foreach (var _ in FindSwapStages(c, nation, result)) { }
        other = result.Other;
        give = result.Give;
        take = result.Take;
        ourCount = result.OurCount;
        theirCount = result.TheirCount;
        return result.Found;
    }

    private static IEnumerable<int> FindSwapStages(Campaign c, Nation nation, SwapStageResult result)
    {
        if (c?.ProvinceMap is null || nation is null || nation.Population <= 0 || nation.Movement is not null) yield break;
        var parts = GetOrderedPieces(c, nation);
        if (parts.Count < 2) yield break;
        var limit = Math.Max(1, c.Defines.EnclaveSwapMaxProvinces);
        var authored = ProvinceOwnership.ProvinceSetOf(nation.Tag);
        var best = int.MaxValue;
        for (var pi = 1; pi < parts.Count; pi++)
        {
            if (pi > 1 && Campaign.StageOverdue()) yield return 0;
            var piece = parts[pi];
            if (piece.Count > limit || Authored(piece, authored)) continue;
            var regions = OfferRegions(c, nation, piece);
            if (regions is null || !Dominant(c, nation, piece, out var holder)) continue;
            if (holder.Movement is not null || holder.Population <= 0) continue;
            var theirParts = GetOrderedPieces(c, holder);
            if (theirParts.Count < 2) continue;
            var theirAuthored = ProvinceOwnership.ProvinceSetOf(holder.Tag);
            for (var ti = 1; ti < theirParts.Count; ti++)
            {
                if (ti > 1 && Campaign.StageOverdue()) yield return 0;
                var theirs = theirParts[ti];
                if (theirs.Count > limit || Authored(theirs, theirAuthored)) continue;
                var back = OfferRegions(c, holder, theirs);
                if (back is null || !Dominant(c, holder, theirs, out var around) || around.Id != nation.Id) continue;
                var size = piece.Count + theirs.Count;
                if (size < best)
                {
                    best = size;
                    result.Other = holder;
                    result.Give = regions;
                    result.Take = back;
                    result.OurCount = piece.Count;
                    result.TheirCount = theirs.Count;
                    result.Found = true;
                }
            }
        }
    }

    private static int[] OfferRegions(Campaign c, Nation nation, HashSet<int> piece)
    {
        var regions = WholeRegions(c, nation, piece);
        if (regions.Length == 0) return null;
        foreach (var id in regions)
        {
            if (id == nation.CapitalStateMapIndex) return null;
            var state = c.StateByMapIndex(id);
            if (state is not null && TerritorialClaims.TitleOf(c, nation, state) == TerritorialTitle.Homeland) return null;
        }
        return regions;
    }

    private static bool Dominant(Campaign c, Nation nation, HashSet<int> piece, out Nation holder)
    {
        holder = null;
        var map = c.ProvinceMap;
        var around = _dominantAroundScratch ??= new Dictionary<int, int>();
        around.Clear();
        var count = map.ProvinceCount;
        var ptn = map.ProvinceToNation;
        var landNeighbors = map.LandNeighbors;
        var ownMap = (ushort)nation.MapIndex;
        foreach (var province in piece)
        {
            // Drawn borders: who surrounds a piece of ground is who it touches on the map, not who
            // is a ferry away (ProvinceTopology.LandNeighbors).
            var neighbors = landNeighbors[province];
            for (var i = 0; i < neighbors.Length; i++)
            {
                var next = neighbors[i];
                if (next <= 0 || next > count || map.IsWater(next)) continue;
                var owner = ptn[next];
                if (owner == 0 || owner == ownMap) continue;
                around[owner] = around.GetValueOrDefault(owner) + 1;
            }
        }
        if (around.Count == 0) return false;
        var bestKey = -1;
        var bestCount = -1;
        var tie = false;
        foreach (var entry in around)
        {
            if (entry.Value > bestCount)
            {
                bestCount = entry.Value;
                bestKey = entry.Key;
                tie = false;
            }
            else if (entry.Value == bestCount)
            {
                tie = true;
                if (entry.Key < bestKey) bestKey = entry.Key;
            }
        }
        around.Clear();
        if (tie) return false;
        holder = c.NationByMapIndex(bestKey);
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
