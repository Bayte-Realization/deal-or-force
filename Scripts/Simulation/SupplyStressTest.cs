using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A read-only removal of named imports, with all other market inputs held fixed.
/// Not an embargo simulation: it neither clears the market nor predicts substitutes or prices.</summary>
public static class SupplyStressTest
{
    public sealed record Row(Commodity Good, double RemovedImports, double Demand,
        double CurrentSupply, double RemainingSupply)
    {
        public double AddedShortfall => (CurrentSupply - RemainingSupply) * Demand;
    }
    public sealed record Report(bool Available, int ExcludedGoods, Row[] Rows);

    public static Report Read(Campaign campaign, Nation nation, int supplierId)
    {
        if (campaign is null || nation is null || supplierId == nation.Id ||
            campaign.NationById(supplierId) is null || campaign.TradeRoutesDay < 0 ||
            campaign.TradeRoutesDay > campaign.ElapsedDays)
            return new(false, 0, Array.Empty<Row>());

        var routes = TradeNetwork.RoutesTouching(campaign, nation.Id)
            .Where(r => r.BuyerId == nation.Id && r.SellerId != nation.Id).ToArray();
        var rows = new List<Row>();
        var excluded = 0;
        foreach (var group in routes.Where(r => r.SellerId == supplierId).GroupBy(r => r.Commodity))
        {
            var attributed = routes.Where(r => r.Commodity == group.Key).ToArray();
            // Reject contradictory snapshots rather than silently scaling a supplier's share.
            if (!nation.Market.TryGetValue(group.Key, out var q) ||
                !float.IsFinite(q.Demand) || q.Demand <= .001f ||
                !float.IsFinite(q.Production) || q.Production < 0 ||
                !float.IsFinite(q.Imports) || q.Imports < 0 ||
                attributed.Any(r => !float.IsFinite(r.Volume) || r.Volume < 0 || campaign.NationById(r.SellerId) is null) ||
                attributed.Sum(r => (double)r.Volume) > q.Imports + Math.Max(.001, q.Imports * .00001))
            { excluded++; continue; }

            var removed = Math.Min(q.Imports, group.Sum(r => (double)r.Volume));
            if (removed <= .001) continue;
            // Deliberately matches MarketSystem's physical satisfaction formula, including
            // its production buffer. Subtracting from the capped Satisfaction would lose it.
            var available = (double)q.Production + q.Imports;
            rows.Add(new(group.Key, removed, q.Demand,
                Math.Clamp(available / q.Demand, 0, 1),
                Math.Clamp((available - removed) / q.Demand, 0, 1)));
        }
        return new(true, excluded, rows.OrderByDescending(r => r.CurrentSupply - r.RemainingSupply)
            .ThenBy(r => r.Good).ToArray());
    }
}
