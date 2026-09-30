using System;
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
    public static Row[] Read(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return Array.Empty<Row>();
        var routes = campaign.TradeRoutesDay < 0 ? Array.Empty<TradeRoute>() : campaign.TradeRoutes
            .Where(r => r.BuyerId == nation.Id && r.SellerId != nation.Id && campaign.NationById(r.SellerId) is not null &&
                float.IsFinite(r.Volume) && r.Volume > 0).ToArray();
        return nation.Market.Values.Where(q => float.IsFinite(q.Demand) && q.Demand > .001f &&
                float.IsFinite(q.Imports) && q.Imports > .001f)
            .Select(q =>
            {
                var suppliers = routes.Where(r => r.Commodity == q.Commodity).GroupBy(r => r.SellerId)
                    .Select(g => new Supplier(g.Key, g.Sum(r => r.Volume)))
                    .OrderByDescending(s => s.Volume).ThenBy(s => s.NationId).ToArray();
                return new Row(q.Commodity, q.Demand, q.Imports, q.Satisfaction,
                    Math.Max(0, q.Imports - suppliers.Sum(s => s.Volume)), suppliers);
            }).OrderByDescending(r => r.LargestDemandShare).ThenByDescending(r => r.ImportShare).ThenBy(r => r.Good).ToArray();
    }
}
