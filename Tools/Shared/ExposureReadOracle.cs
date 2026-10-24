using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Pre-optimization implementation, retained only for exact differential tests.</summary>
public static class ExposureReadOracle
{
    public static EconomicExposure.Row[] Read(Campaign c, Nation n)
    {
        if (c is null || n is null) return Array.Empty<EconomicExposure.Row>();
        var routes = c.TradeRoutesDay < 0 ? Array.Empty<TradeRoute>() : TradeNetwork.RoutesTouching(c, n.Id)
            .Where(r => r.BuyerId == n.Id && r.SellerId != n.Id && c.NationById(r.SellerId) is not null &&
                float.IsFinite(r.Volume) && r.Volume > 0).ToArray();
        return n.Market.Values.Where(q => float.IsFinite(q.Demand) && q.Demand > .001f &&
                float.IsFinite(q.Imports) && q.Imports > .001f).Select(q =>
            {
                var suppliers = routes.Where(r => r.Commodity == q.Commodity).GroupBy(r => r.SellerId)
                    .Select(g => new EconomicExposure.Supplier(g.Key, g.Sum(r => r.Volume)))
                    .OrderByDescending(s => s.Volume).ThenBy(s => s.NationId).ToArray();
                return new EconomicExposure.Row(q.Commodity, q.Demand, q.Imports, q.Satisfaction,
                    Math.Max(0, q.Imports - suppliers.Sum(s => s.Volume)), suppliers);
            }).OrderByDescending(r => r.LargestDemandShare).ThenByDescending(r => r.ImportShare).ThenBy(r => r.Good).ToArray();
    }

    public static bool Equal(EconomicExposure.Row[] a, EconomicExposure.Row[] b)
    {
        static bool Bits(float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y);
        if (a.Length != b.Length) return false;
        for (var row = 0; row < a.Length; row++)
        {
            var x = a[row]; var y = b[row];
            if (x.Good != y.Good || !Bits(x.Demand, y.Demand) || !Bits(x.Imports, y.Imports) ||
                !Bits(x.Satisfaction, y.Satisfaction) || !Bits(x.UnattributedImports, y.UnattributedImports) ||
                !Bits(x.LargestDemandShare, y.LargestDemandShare) || !Bits(x.LargestImportShare, y.LargestImportShare) ||
                !Bits(x.ImportShare, y.ImportShare) || x.Suppliers.Length != y.Suppliers.Length) return false;
            for (var supplier = 0; supplier < x.Suppliers.Length; supplier++)
                if (x.Suppliers[supplier].NationId != y.Suppliers[supplier].NationId ||
                    !Bits(x.Suppliers[supplier].Volume, y.Suppliers[supplier].Volume)) return false;
        }
        return true;
    }
}
