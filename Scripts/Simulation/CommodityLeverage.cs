using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Strategic high-tech resources and commodity leverage between trade partners.
/// Measures reliance on critical inputs and calculates economic leverage one nation exerts
/// over another through trade routes. Which goods count, and how much, is the good's own
/// `leverage` field in common/goods (CommodityDefinition.Leverage): rare earths and machinery
/// as ever, and now advanced chips, chipmaking tools, gallium, grain, potash and the rest -
/// whatever a file says can be withheld.
/// </summary>
public static class CommodityLeverage
{
    public static bool IsHighTech(Commodity c) =>
        CommodityCatalog.Get(c).Leverage == CommodityLeverageTier.HighTech;

    public static bool IsStrategic(Commodity c) =>
        CommodityCatalog.Get(c).Leverage != CommodityLeverageTier.None;

    /// <summary>
    /// Computes the strategic commodity leverage that seller holds over buyer in range [0, 1].
    /// Reflects what share of buyer's strategic imports comes from seller, weighted by import reliance.
    ///
    /// One walk over the buyer's routes, summing every good's imports and the seller's part of
    /// them, rather than one walk per strategic good: with twenty-odd levers the per-good walk
    /// cost twenty passes over the same list for every pair of countries the AI weighs.
    /// </summary>
    public static float Calculate(Campaign c, Nation seller, Nation buyer)
    {
        if (c is null || seller is null || buyer is null || seller.Id == buyer.Id ||
            seller.Population <= 0 || buyer.Population <= 0 || buyer.Market is null)
            return 0f;

        var routes = TradeNetwork.RoutesTouching(c, buyer.Id);
        if (routes.Count == 0) return 0f;

        var size = 0;
        foreach (var commodity in CommodityCatalog.All) size = Math.Max(size, (int)commodity + 1);
        Span<float> totalVolume = size <= 256 ? stackalloc float[size] : new float[size];
        Span<float> sellerVolume = size <= 256 ? stackalloc float[size] : new float[size];
        totalVolume.Clear();
        sellerVolume.Clear();
        var any = false;
        for (var i = 0; i < routes.Count; i++)
        {
            var r = routes[i];
            if (r.BuyerId != buyer.Id) continue;
            var index = (int)r.Commodity;
            if (index < 0 || index >= size || !float.IsFinite(r.Volume) || r.Volume <= 0f) continue;
            totalVolume[index] += r.Volume;
            if (r.SellerId == seller.Id) { sellerVolume[index] += r.Volume; any = true; }
        }
        if (!any) return 0f;

        var totalWeightedLeverage = 0f;
        var totalWeight = 0f;

        foreach (var commodity in CommodityCatalog.All)
        {
            var tier = CommodityCatalog.Get(commodity).Leverage;
            if (tier == CommodityLeverageTier.None) continue;

            buyer.Market.TryGetValue(commodity, out var quote);
            var demand = quote?.Demand ?? 0f;
            var imports = quote?.Imports ?? 0f;

            if (demand <= 0.001f && imports <= 0.001f) continue;

            var weight = tier == CommodityLeverageTier.HighTech ? 2.0f : 1.0f;
            totalWeight += weight;

            var fromSeller = sellerVolume[(int)commodity];
            if (fromSeller <= 0.001f) continue;

            var effectiveImports = MathF.Max(imports, totalVolume[(int)commodity]);
            var supplierShare = effectiveImports > 0.001f ? Math.Clamp(fromSeller / effectiveImports, 0f, 1f) : 1f;

            var effectiveDemand = MathF.Max(demand, effectiveImports);
            var importReliance = effectiveDemand > 0.001f ? Math.Clamp(effectiveImports / effectiveDemand, 0f, 1f) : 1f;

            var commodityLeverage = supplierShare * (0.35f + 0.65f * importReliance);
            totalWeightedLeverage += commodityLeverage * weight;
        }

        if (totalWeight <= 0.001f) return 0f;
        return Math.Clamp(totalWeightedLeverage / totalWeight, 0f, 1f);
    }

    /// <summary>
    /// Measures how vulnerable the buyer is to a sudden cut-off of supplies from seller.
    /// Factors in domestic production share, market satisfaction, and seller's share of critical goods.
    /// </summary>
    public static float StrategicVulnerability(Campaign c, Nation buyer, Nation seller)
    {
        if (c is null || buyer is null || seller is null || buyer.Id == seller.Id ||
            seller.Population <= 0 || buyer.Population <= 0 || buyer.Market is null)
            return 0f;

        var baseLeverage = Calculate(c, seller, buyer);
        if (baseLeverage <= 0.001f) return 0f;

        var satisfactionSum = 0f;
        var count = 0;

        foreach (var commodity in CommodityCatalog.All)
        {
            if (!IsStrategic(commodity)) continue;
            if (buyer.Market.TryGetValue(commodity, out var quote))
            {
                satisfactionSum += Math.Clamp(quote.Satisfaction, 0f, 1f);
                count++;
            }
        }

        var avgSatisfaction = count > 0 ? (satisfactionSum / count) : 1f;
        var stressMultiplier = 1f + (1f - avgSatisfaction) * 0.5f;

        return Math.Clamp(baseLeverage * stressMultiplier, 0f, 1.5f);
    }

    /// <summary>
    /// The three strategic goods the trade screen names. Oil, arms and electronics are
    /// already both a traded commodity and a depot stock. Reading the lines does not route
    /// the week again and does not seed a depot.
    /// </summary>
    public static readonly Commodity[] ReportedGoods =
    {
        Commodity.Oil,
        Commodity.Arms,
        Commodity.Electronics
    };

    /// <summary>One sentence a good: who sells it to this country, the import share, and days of depot cover.</summary>
    public static string[] CoverLines(Campaign campaign, Nation buyer)
    {
        var lines = new string[ReportedGoods.Length];
        for (var index = 0; index < ReportedGoods.Length; index++)
            lines[index] = CoverLine(campaign, buyer, ReportedGoods[index]);
        return lines;
    }

    public static string CoverLine(Campaign campaign, Nation buyer, Commodity good)
    {
        if (buyer is null) return "";
        var name = Loc.Get(CommodityCatalog.Get(good).Name);
        var seller = LargestSeller(campaign, buyer, good);
        var who = seller is null
            ? Loc.Get("Nobody is selling it.")
            : Loc.Format("{0} sells it.", seller.DisplayName);
        var share = ImportShare(buyer, good).ToString("P0", Loc.Culture);
        var imported = Loc.Format("Imports are {0} of supply.", share);
        var days = campaign is not null && campaign.Defines.StrategicStockpilesEnabled
            ? StrategicStockpiles.DaysOnHand(buyer, good.ToString())
            : null;
        var cover = days is int n
            ? Loc.Format("The depot covers {0} days.", n)
            : Loc.Get("The depot is not drawing it.");
        return name + ": " + who + " " + imported + " " + cover;
    }

    private static float ImportShare(Nation buyer, Commodity good)
    {
        if (buyer?.Market is null || !buyer.Market.TryGetValue(good, out var quote) || quote is null)
            return 0f;
        var arrived = quote.Production + quote.Imports;
        if (!float.IsFinite(arrived) || arrived <= 0.0001f) return 0f;
        var share = quote.Imports / arrived;
        return float.IsFinite(share) ? Math.Clamp(share, 0f, 1f) : 0f;
    }

    private static Nation LargestSeller(Campaign campaign, Nation buyer, Commodity good)
    {
        if (campaign is null || buyer is null) return null;
        Nation best = null;
        var bestVolume = 0f;
        var bestId = int.MaxValue;
        foreach (var route in campaign.TradeRoutes)
        {
            if (route is null || route.BuyerId != buyer.Id || route.Commodity != good) continue;
            if (!float.IsFinite(route.Volume) || route.Volume <= 0.0001f) continue;
            var seller = campaign.NationById(route.SellerId);
            if (seller is null || seller.Id == buyer.Id) continue;
            var tied = Math.Abs(route.Volume - bestVolume) <= 0.0001f;
            if (best is not null && (route.Volume < bestVolume - 0.0001f || (tied && seller.Id >= bestId)))
                continue;
            best = seller;
            bestVolume = route.Volume;
            bestId = seller.Id;
        }
        return best;
    }
}
