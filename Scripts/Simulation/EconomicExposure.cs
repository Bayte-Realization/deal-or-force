using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Current physical exposure, not a counterfactual market simulation or a price forecast.</summary>
public static class EconomicExposure
{
    public sealed record Supplier(int NationId, float Volume);
    public sealed record Row(Commodity Good, float Demand, float Imports, float Satisfaction,
        float UnattributedImports, Supplier[] Suppliers)
    {
        public float ImportShare => Math.Clamp(Imports / Demand, 0, 1);
        public float LargestDemandShare => Suppliers.Length == 0 ? 0 : Math.Clamp(Suppliers[0].Volume / Demand, 0, 1);
        public float LargestImportShare => Suppliers.Length == 0 || Imports <= 0 ? 0 : Math.Clamp(Suppliers[0].Volume / Imports, 0, 1);
    }
    /// <summary>The share of imports above which one partner is named as the supplier. A mod moves <see cref="Defines.AiSupplyHedgeShare"/>.</summary>
    public static float DominantShare(Defines defines)
    {
        var line = defines?.AiSupplyHedgeShare ?? 0.75f;
        if (!float.IsFinite(line)) line = 0.75f;
        return Math.Clamp(line, 0.5f, 0.99f);
    }

    /// <summary>
    /// Why this good is short, from the same routes the exposure card counts.
    /// Empty when the good is not short of a partner: a full domestic supply has nothing to add.
    /// </summary>
    public static string Cause(Campaign campaign, Nation nation, Commodity good)
    {
        if (campaign is null || nation is null) return Loc.Get("The week's partners are not on the books yet.");
        if (campaign.TradeRoutesDay < 0) return Loc.Get("The week's partners are not on the books yet.");
        nation.Market.TryGetValue(good, out var quote);
        var demand = quote?.Demand ?? 0f;
        var production = quote?.Production ?? 0f;
        var imports = quote?.Imports ?? 0f;
        if (!float.IsFinite(demand) || !float.IsFinite(production) || !float.IsFinite(imports))
            return Loc.Get("The week's partners are not on the books yet.");
        if (imports <= 0.001f)
        {
            if (production > 0.001f && demand > production + 0.001f)
                return Loc.Get("What is on hand is made at home, and it does not cover demand.");
            if (demand > 0.001f)
                return Loc.Get("Demand is recorded and almost nothing is being made or brought in.");
            return "";
        }

        // One seller can have more than one route in the week. The exposure card sums them;
        // naming the largest single route would disagree with that card.
        Dictionary<int, float> bySeller = null;
        var named = 0f;
        foreach (var route in TradeNetwork.RoutesTouching(campaign, nation.Id))
        {
            if (route.BuyerId != nation.Id || route.SellerId == nation.Id || route.Commodity != good) continue;
            if (!float.IsFinite(route.Volume) || route.Volume <= 0f || campaign.NationById(route.SellerId) is null) continue;
            bySeller ??= new Dictionary<int, float>();
            bySeller[route.SellerId] = bySeller.GetValueOrDefault(route.SellerId) + route.Volume;
            named += route.Volume;
        }
        var topId = -1;
        var top = 0f;
        if (bySeller is not null)
            foreach (var (sellerId, volume) in bySeller)
                if (topId < 0 || volume > top || volume == top && sellerId < topId)
                {
                    topId = sellerId;
                    top = volume;
                }
        if (topId < 0 || named <= 0.001f)
            return Loc.Get("No partner is selling this. What arrives comes through the grey market.");
        var unattributed = imports - named;
        if (unattributed > named)
            return Loc.Get("Most of what arrives is the grey market, not a named partner.");
        var share = Math.Clamp(top / imports, 0f, 1f);
        var seller = campaign.NationById(topId);
        if (seller is not null && share + 0.0001f >= DominantShare(campaign.Defines))
            return Loc.Format("{0} supplies {1:P0} of the imports of this good.", seller.DisplayName, share);
        return Loc.Get("Imports of this good are split across more than one partner.");
    }

    // в”Ђв”Ђв”Ђ Scratch, reused across calls в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    // This function is asked for by every AI country every week (TryHedgeSupply),
    // and each call used to allocate a fresh nested dictionary, a list, a supplier
    // array per good and a LINQ ordering chain вЂ” 49 kB a call, 113 MB over a
    // 60-day profile. The scratch is the pattern LogisticsCore and WarScoring
    // already use: cleared per call, kept between them.
    //
    // It is per campaign, not per process: the returned row array is handed to the
    // caller, and a client rebuilding a second campaign beside the host's would
    // otherwise read rows the other world had just written.
    private sealed class Scratch
    {
        public readonly Dictionary<Commodity, Dictionary<int, double>> Totals = new();
        public readonly List<Dictionary<int, double>> Used = new();
        public readonly List<Row> Rows = new();
    }
    [ThreadStatic] private static Scratch _threadScratch;

    private static Scratch ScratchOf() => _threadScratch ??= new Scratch();

    internal readonly record struct HedgeCandidate(Commodity Good, int TopSupplierId, int SecondSupplierId);

    private static void ClearTotals(Scratch scratch)
    {
        var used = scratch.Used;
        for (var i = 0; i < used.Count; i++) used[i].Clear();
        used.Clear();
    }

    private static void AccumulateRoute(Campaign campaign, int buyerId, TradeRoute route,
        Dictionary<Commodity, Dictionary<int, double>> totals, List<Dictionary<int, double>> used)
    {
        if (route.BuyerId != buyerId || route.SellerId == buyerId ||
            !float.IsFinite(route.Volume) || route.Volume <= 0f ||
            campaign.NationById(route.SellerId) is null) return;
        if (!totals.TryGetValue(route.Commodity, out var sellers))
            totals[route.Commodity] = sellers = new Dictionary<int, double>();
        if (sellers.Count == 0) used.Add(sellers);
        sellers[route.SellerId] = sellers.GetValueOrDefault(route.SellerId) + route.Volume;
    }

    public static Row[] Read(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return Array.Empty<Row>();
        var scratch = ScratchOf();
        var totals = scratch.Totals;
        var used = scratch.Used;
        var rows = scratch.Rows;
        ClearTotals(scratch);
        rows.Clear();
        // One attribution pass, not a filtered route array and a GroupBy for every good.
        // Keep double accumulators: LINQ Sum(float selector) also sums in double before
        // rounding once to float. Float += here would silently change supplier ties.
        if (campaign.TradeRoutesDay >= 0)
        {
            if (TradeNetwork.TryGetFiledRoutesTouching(campaign, nation.Id, out var filed))
            {
                if (filed is not null)
                    for (var i = 0; i < filed.Count; i++)
                        AccumulateRoute(campaign, nation.Id, filed[i], totals, used);
            }
            else
            {
                foreach (var route in TradeNetwork.RoutesTouching(campaign, nation.Id))
                    AccumulateRoute(campaign, nation.Id, route, totals, used);
            }
        }
        foreach (var quote in nation.Market.Values)
        {
            if (!float.IsFinite(quote.Demand) || quote.Demand <= .001f ||
                !float.IsFinite(quote.Imports) || quote.Imports <= .001f) continue;
            Supplier[] suppliers = null;
            if (totals.TryGetValue(quote.Commodity, out var sellers) && sellers.Count > 0)
            {
                suppliers = new Supplier[sellers.Count];
                var at = 0;
                foreach (var seller in sellers) suppliers[at++] = new Supplier(seller.Key, (float)seller.Value);
                Array.Sort(suppliers, SupplierOrder);
            }
            // Same sorted float inputs and double accumulation as the former LINQ sum.
            double named = 0;
            if (suppliers is not null)
                foreach (var supplier in suppliers) named += supplier.Volume;
            rows.Add(new Row(quote.Commodity, quote.Demand, quote.Imports, quote.Satisfaction,
                Math.Max(0, quote.Imports - (float)named), suppliers ?? Array.Empty<Supplier>()));
        }
        // Preserve stable ordering even for hand-authored markets with duplicate goods.
        // This is a caller-owned snapshot, not scratch. Reusing the returned array
        // silently rewrote earlier UI/AI readings on the very next call, even in the
        // same campaign. The nested accumulation tables above are safe to reuse.
        var result = rows.ToArray();
        Array.Sort(result, RowOrder);
        return result;
    }

    internal static Row PickHedgeCandidate(Campaign campaign, Nation nation, float line, float shortAt,
        Dictionary<string, int> decisionAvailableDay, int elapsedDays, Func<Commodity, string> keyOf)
    {
        if (campaign is null || nation is null) return null;
        var scratch = ScratchOf();
        var totals = scratch.Totals;
        var used = scratch.Used;
        ClearTotals(scratch);
        if (campaign.TradeRoutesDay >= 0)
        {
            if (TradeNetwork.TryGetFiledRoutesTouching(campaign, nation.Id, out var filed))
            {
                if (filed is not null)
                    for (var i = 0; i < filed.Count; i++)
                        AccumulateRoute(campaign, nation.Id, filed[i], totals, used);
            }
            else
            {
                foreach (var route in TradeNetwork.RoutesTouching(campaign, nation.Id))
                    AccumulateRoute(campaign, nation.Id, route, totals, used);
            }
        }
        Row chosen = null;
        foreach (var quote in nation.Market.Values)
        {
            if (!float.IsFinite(quote.Demand) || quote.Demand <= .001f ||
                !float.IsFinite(quote.Imports) || quote.Imports <= .001f) continue;
            if (decisionAvailableDay.GetValueOrDefault(keyOf(quote.Commodity)) > elapsedDays) continue;
            var shortOf = float.IsFinite(quote.Satisfaction) && quote.Satisfaction < shortAt;
            if (!shortOf && quote.Imports / quote.Demand < 0.5f) continue;

            Supplier[] suppliers = null;
            if (totals.TryGetValue(quote.Commodity, out var sellers) && sellers.Count > 0)
            {
                suppliers = new Supplier[sellers.Count];
                var at = 0;
                foreach (var seller in sellers) suppliers[at++] = new Supplier(seller.Key, (float)seller.Value);
                Array.Sort(suppliers, SupplierOrder);
            }
            double named = 0;
            if (suppliers is not null)
                foreach (var supplier in suppliers) named += supplier.Volume;
            var row = new Row(quote.Commodity, quote.Demand, quote.Imports, quote.Satisfaction,
                Math.Max(0, quote.Imports - (float)named), suppliers ?? Array.Empty<Supplier>());
            var concentrated = row.Suppliers.Length > 0 && row.LargestImportShare + 0.0001f >= line;
            var unnamed = row.Suppliers.Length == 0 && row.Imports > 0.001f;
            if (concentrated && (shortOf || row.ImportShare >= 0.5f) || unnamed && shortOf)
            {
                if (chosen is null || row.LargestImportShare > chosen.LargestImportShare ||
                    row.LargestImportShare == chosen.LargestImportShare && (int)row.Good < (int)chosen.Good)
                    chosen = row;
            }
        }
        return chosen;
    }

    internal static bool TryPickHedgeCandidate(Campaign campaign, Nation nation, float line, float shortAt,
        Dictionary<string, int> decisionAvailableDay, int elapsedDays, Func<Commodity, string> keyOf,
        out HedgeCandidate candidate)
    {
        candidate = default;
        if (campaign is null || nation is null) return false;
        var scratch = ScratchOf();
        var totals = scratch.Totals;
        var used = scratch.Used;
        ClearTotals(scratch);
        if (campaign.TradeRoutesDay >= 0)
        {
            if (TradeNetwork.TryGetFiledRoutesTouching(campaign, nation.Id, out var filed))
            {
                if (filed is not null)
                    for (var i = 0; i < filed.Count; i++)
                        AccumulateRoute(campaign, nation.Id, filed[i], totals, used);
            }
            else
            {
                var routes = campaign.TradeRoutes;
                for (var i = 0; i < routes.Count; i++)
                    AccumulateRoute(campaign, nation.Id, routes[i], totals, used);
            }
        }

        var hasChosen = false;
        var bestShare = 0f;
        foreach (var quote in nation.Market.Values)
        {
            if (!float.IsFinite(quote.Demand) || quote.Demand <= .001f ||
                !float.IsFinite(quote.Imports) || quote.Imports <= .001f) continue;
            if (decisionAvailableDay.GetValueOrDefault(keyOf(quote.Commodity)) > elapsedDays) continue;
            var shortOf = float.IsFinite(quote.Satisfaction) && quote.Satisfaction < shortAt;
            if (!shortOf && quote.Imports / quote.Demand < 0.5f) continue;

            var topId = -1;
            var topVol = 0f;
            var secondId = -1;
            var secondVol = 0f;
            if (totals.TryGetValue(quote.Commodity, out var sellers) && sellers.Count > 0)
            {
                foreach (var seller in sellers)
                {
                    var vol = (float)seller.Value;
                    var id = seller.Key;
                    if (topId < 0 || vol > topVol || vol == topVol && id < topId)
                    {
                        secondId = topId;
                        secondVol = topVol;
                        topId = id;
                        topVol = vol;
                    }
                    else if (secondId < 0 || vol > secondVol || vol == secondVol && id < secondId)
                    {
                        secondId = id;
                        secondVol = vol;
                    }
                }
            }

            var largestImportShare = topId < 0 || quote.Imports <= 0f ? 0f : Math.Clamp(topVol / quote.Imports, 0f, 1f);
            var importShare = Math.Clamp(quote.Imports / quote.Demand, 0f, 1f);
            var concentrated = topId >= 0 && largestImportShare + 0.0001f >= line;
            var unnamed = topId < 0 && quote.Imports > 0.001f;
            if (concentrated && (shortOf || importShare >= 0.5f) || unnamed && shortOf)
            {
                if (!hasChosen || largestImportShare > bestShare ||
                    largestImportShare == bestShare && (int)quote.Commodity < (int)candidate.Good)
                {
                    hasChosen = true;
                    bestShare = largestImportShare;
                    candidate = new HedgeCandidate(quote.Commodity, topId, secondId);
                }
            }
        }
        return hasChosen;
    }

    private static readonly Comparison<Row> RowOrder = (left, right) =>
    {
        var byShare = right.LargestDemandShare.CompareTo(left.LargestDemandShare);
        if (byShare != 0) return byShare;
        byShare = right.ImportShare.CompareTo(left.ImportShare);
        if (byShare != 0) return byShare;
        return left.Good.CompareTo(right.Good);
    };

    private static readonly IComparer<Supplier> SupplierOrder = Comparer<Supplier>.Create((left, right) =>
    {
        var byVolume = right.Volume.CompareTo(left.Volume);
        return byVolume != 0 ? byVolume : left.NationId.CompareTo(right.NationId);
    });
}
