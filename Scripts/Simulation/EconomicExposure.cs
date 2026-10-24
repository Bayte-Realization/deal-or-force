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

    public static Row[] Read(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return Array.Empty<Row>();
        // One attribution pass, not a filtered route array and a GroupBy for every good.
        // Keep double accumulators: LINQ Sum(float selector) also sums in double before
        // rounding once to float. Float += here would silently change supplier ties.
        var totals = new Dictionary<Commodity, Dictionary<int, double>>();
        if (campaign.TradeRoutesDay >= 0)
            foreach (var route in TradeNetwork.RoutesTouching(campaign, nation.Id))
            {
                if (route.BuyerId != nation.Id || route.SellerId == nation.Id ||
                    campaign.NationById(route.SellerId) is null || !float.IsFinite(route.Volume) || route.Volume <= 0) continue;
                if (!totals.TryGetValue(route.Commodity, out var sellers))
                    totals[route.Commodity] = sellers = new Dictionary<int, double>();
                sellers[route.SellerId] = sellers.GetValueOrDefault(route.SellerId) + route.Volume;
            }
        var rows = new List<Row>();
        foreach (var quote in nation.Market.Values)
        {
            if (!float.IsFinite(quote.Demand) || quote.Demand <= .001f ||
                !float.IsFinite(quote.Imports) || quote.Imports <= .001f) continue;
            var suppliers = Array.Empty<Supplier>();
            if (totals.TryGetValue(quote.Commodity, out var sellers))
            {
                suppliers = new Supplier[sellers.Count];
                var at = 0;
                foreach (var seller in sellers) suppliers[at++] = new Supplier(seller.Key, (float)seller.Value);
                Array.Sort(suppliers, SupplierOrder);
            }
            // Same sorted float inputs and double accumulation as the former LINQ sum.
            double named = 0;
            foreach (var supplier in suppliers) named += supplier.Volume;
            rows.Add(new Row(quote.Commodity, quote.Demand, quote.Imports, quote.Satisfaction,
                Math.Max(0, quote.Imports - (float)named), suppliers));
        }
        // Preserve stable ordering even for hand-authored markets with duplicate goods.
        return rows.OrderByDescending(r => r.LargestDemandShare).ThenByDescending(r => r.ImportShare)
            .ThenBy(r => r.Good).ToArray();
    }

    private static readonly IComparer<Supplier> SupplierOrder = Comparer<Supplier>.Create((left, right) =>
    {
        var byVolume = right.Volume.CompareTo(left.Volume);
        return byVolume != 0 ? byVolume : left.NationId.CompareTo(right.NationId);
    });
}
