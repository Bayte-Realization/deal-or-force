using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public enum ChokepointStatus
{
    Concentrated = 0,
    HealthyAnchor = 1,
    Squeezed = 2
}

/// <summary>
/// Read-only snapshot of one strategic commodity dependency for a nation, computed from real
/// <see cref="Campaign.TradeRoutes"/> and <see cref="Nation.Market"/> quotes.
/// </summary>
public readonly record struct ChokepointExposure(
    Commodity Commodity,
    int SupplierNationId,
    float DependencyShare,
    float MarketSatisfaction,
    ChokepointStatus Status,
    bool ImpactsActiveMegaproject);

/// <summary>
/// Emergent Supply-Chain &amp; Chokepoint Leverage engine.
///
/// Aggregates <see cref="Campaign.TradeRoutes"/> in a single O(R) flat-array pass whenever the
/// weekly market clearing updates <see cref="Campaign.TradeRoutesDay"/>, exposing:
/// - Single-supplier import concentration (&gt;= 35% of imports of a strategic commodity).
/// - Active weaponized chokepoint squeezes (war, embargo, or hostile supply cutoff) that directly
///   throttle active <see cref="GrandProjects"/> consuming that good (0.65x pace) and record
///   diplomatic grievance (<c>chokepoint_weaponized</c>) + public Chronicle news.
/// - Allied strategic supply anchors (reliable &gt;= 35% supplier with &gt;= 90% satisfaction and
///   diplomatic alignment) that build diplomatic goodwill (<c>strategic_supply_anchor</c>).
/// </summary>
public static class StrategicChokepoints
{
    public const float ConcentrationThreshold = 0.35f;
    public const float SqueezePaceMultiplier = 0.65f;
    private const int DiplomaticCooldownDays = 180;

    private static readonly Commodity[] TrackedGoods =
    {
        Commodity.Oil,
        Commodity.NaturalGas,
        Commodity.Uranium,
        Commodity.RareEarths,
        Commodity.Electronics,
        Commodity.IndustrialMaterials,
        Commodity.Energy,
        Commodity.Fertiliser,
        Commodity.Arms,
        Commodity.Machinery,
        Commodity.Food
    };

    private static readonly int[] CommoditySlot = BuildSlotLookup();

    private static int[] BuildSlotLookup()
    {
        var max = 0;
        for (var i = 0; i < TrackedGoods.Length; i++)
            if ((int)TrackedGoods[i] > max) max = (int)TrackedGoods[i];
        var table = new int[max + 1];
        Array.Fill(table, -1);
        for (var i = 0; i < TrackedGoods.Length; i++)
            table[(int)TrackedGoods[i]] = i;
        return table;
    }

    private static int SlotOf(Commodity commodity)
    {
        var idx = (int)commodity;
        return (uint)idx < (uint)CommoditySlot.Length ? CommoditySlot[idx] : -1;
    }

    private sealed class ChokepointCache
    {
        public int TradeRoutesDay = int.MinValue;
        public int RouteCount = -1;
        public int NationCount = -1;
        public float[] TotalImportValue = Array.Empty<float>();
        public float[] TopSupplierValue = Array.Empty<float>();
        public int[] TopSupplierId = Array.Empty<int>();
    }

    // Weakly keyed per Campaign so worlds are never pinned in memory (--code-health-only safe).
    private static readonly ConditionalWeakTable<Campaign, ChokepointCache> Caches = new();

    private static ChokepointCache EnsureCache(Campaign c)
    {
        var cache = Caches.GetOrCreateValue(c);
        var nationCount = c.Nations.Count;
        var routeCount = c.TradeRoutes.Count;
        if (cache.TradeRoutesDay == c.TradeRoutesDay &&
            cache.RouteCount == routeCount &&
            cache.NationCount == nationCount)
            return cache;

        var stride = TrackedGoods.Length;
        var needed = nationCount * stride;
        if (cache.TotalImportValue.Length < needed)
        {
            cache.TotalImportValue = new float[needed];
            cache.TopSupplierValue = new float[needed];
            cache.TopSupplierId = new int[needed];
        }
        else if (needed > 0)
        {
            Array.Clear(cache.TotalImportValue, 0, needed);
            Array.Clear(cache.TopSupplierValue, 0, needed);
        }
        if (needed > 0) Array.Fill(cache.TopSupplierId, -1, 0, needed);

        // Single O(R) pass over TradeRoutes (no per-nation rescanning, zero heap allocations).
        var routes = c.TradeRoutes;
        for (var i = 0; i < routeCount; i++)
        {
            var r = routes[i];
            if (r is null || r.Value <= 0f) continue;
            var buyer = r.BuyerId;
            if ((uint)buyer >= (uint)nationCount || r.SellerId == buyer || (uint)r.SellerId >= (uint)nationCount) continue;
            var slot = SlotOf(r.Commodity);
            if (slot < 0) continue;

            var cell = buyer * stride + slot;
            cache.TotalImportValue[cell] += r.Value;
            if (r.Value > cache.TopSupplierValue[cell] ||
                (r.Value == cache.TopSupplierValue[cell] && (cache.TopSupplierId[cell] < 0 || r.SellerId < cache.TopSupplierId[cell])))
            {
                cache.TopSupplierValue[cell] = r.Value;
                cache.TopSupplierId[cell] = r.SellerId;
            }
        }

        cache.TradeRoutesDay = c.TradeRoutesDay;
        cache.RouteCount = routeCount;
        cache.NationCount = nationCount;
        return cache;
    }

    /// <summary>
    /// Evaluates whether any key good of an active megaproject is currently squeezed by a
    /// hostile/embargoing dominant supplier (&gt;= 35% import share). Returns 0.65f when squeezed
    /// (unless shielded by an active consortium partner with &gt;= 85% market satisfaction) or 1.0f.
    /// Read-only: never mutates Campaign or CampaignFingerprint.
    /// </summary>
    public static float MegaprojectChokepointFactor(Campaign c, Nation host, Nation partner, GrandProjectDefinition def,
        out string squeezedGoodName, out int squeezingSupplierId, out float dependencyShare)
    {
        squeezedGoodName = "";
        squeezingSupplierId = -1;
        dependencyShare = 0f;
        if (c is null || host is null || def?.KeyGoods is null || def.KeyGoods.Length == 0 || c.TradeRoutes.Count == 0)
            return 1f;

        var cache = EnsureCache(c);
        var stride = TrackedGoods.Length;
        if ((uint)host.Id >= (uint)cache.NationCount) return 1f;

        for (var i = 0; i < def.KeyGoods.Length; i++)
        {
            if (!CommodityCatalog.TryParse(def.KeyGoods[i], out var commodity)) continue;
            var slot = SlotOf(commodity);
            if (slot < 0) continue;

            var cell = host.Id * stride + slot;
            var total = cache.TotalImportValue[cell];
            if (total <= 0.0001f) continue;
            var supplierId = cache.TopSupplierId[cell];
            if (supplierId < 0) continue;
            var share = cache.TopSupplierValue[cell] / total;
            if (share < ConcentrationThreshold) continue;

            var sat = host.Market.TryGetValue(commodity, out var q) ? M.Clamp01(q.Satisfaction) : 1f;
            if (!IsSupplierSqueezing(c, host.Id, supplierId, sat)) continue;

            // Active consortium partner with strong domestic supply shields the project from the squeeze.
            if (partner is not null && partner.Market.TryGetValue(commodity, out var pq) && pq.Satisfaction >= 0.85f)
                continue;

            squeezedGoodName = Loc.Get(CommodityCatalog.Get(commodity).Name);
            squeezingSupplierId = supplierId;
            dependencyShare = M.Clamp01(share);
            return SqueezePaceMultiplier;
        }
        return 1f;
    }

    /// <summary>
    /// Read-only list of a nation's significant single-supplier strategic exposures (&gt;= 35% share),
    /// ordered with Squeezed bottlenecks first, then Concentrated, then Healthy Anchors.
    /// Never mutates Campaign or CampaignFingerprint.
    /// </summary>
    public static IReadOnlyList<ChokepointExposure> ReadForNation(Campaign c, Nation nation)
    {
        if (c is null || nation is null || c.TradeRoutes.Count == 0 || (uint)nation.Id >= (uint)c.Nations.Count)
            return Array.Empty<ChokepointExposure>();

        var cache = EnsureCache(c);
        var stride = TrackedGoods.Length;
        var results = new List<ChokepointExposure>(4);
        var baseIdx = nation.Id * stride;

        for (var slot = 0; slot < stride; slot++)
        {
            var total = cache.TotalImportValue[baseIdx + slot];
            if (total <= 0.0001f) continue;
            var supplierId = cache.TopSupplierId[baseIdx + slot];
            if (supplierId < 0) continue;
            var share = M.Clamp01(cache.TopSupplierValue[baseIdx + slot] / total);
            if (share < ConcentrationThreshold) continue;

            var commodity = TrackedGoods[slot];
            var sat = nation.Market.TryGetValue(commodity, out var q) ? M.Clamp01(q.Satisfaction) : 1f;
            var status = ClassifyStatus(c, nation.Id, supplierId, sat);
            var hitsProject = CommodityUsedByActiveProject(c, nation.Id, commodity);
            results.Add(new ChokepointExposure(commodity, supplierId, share, sat, status, hitsProject));
        }

        if (results.Count > 1)
        {
            results.Sort((a, b) =>
            {
                var rankA = StatusSortRank(a.Status, a.ImpactsActiveMegaproject);
                var rankB = StatusSortRank(b.Status, b.ImpactsActiveMegaproject);
                if (rankA != rankB) return rankA.CompareTo(rankB);
                var cmp = b.DependencyShare.CompareTo(a.DependencyShare);
                return cmp != 0 ? cmp : ((int)a.Commodity).CompareTo((int)b.Commodity);
            });
        }
        return results;
    }

    /// <summary>Zero-allocation count of significant single-supplier strategic exposures for scripted triggers.</summary>
    internal static int CountExposures(Campaign c, Nation nation, ChokepointStatus? statusFilter = null)
    {
        if (c is null || nation is null || c.TradeRoutes.Count == 0 || (uint)nation.Id >= (uint)c.Nations.Count)
            return 0;

        var cache = EnsureCache(c);
        var stride = TrackedGoods.Length;
        var baseIdx = nation.Id * stride;
        var count = 0;

        for (var slot = 0; slot < stride; slot++)
        {
            var total = cache.TotalImportValue[baseIdx + slot];
            if (total <= 0.0001f) continue;
            var supplierId = cache.TopSupplierId[baseIdx + slot];
            if (supplierId < 0) continue;
            var share = M.Clamp01(cache.TopSupplierValue[baseIdx + slot] / total);
            if (share < ConcentrationThreshold) continue;
            if (statusFilter.HasValue)
            {
                var commodity = TrackedGoods[slot];
                var sat = nation.Market.TryGetValue(commodity, out var q) ? M.Clamp01(q.Satisfaction) : 1f;
                if (ClassifyStatus(c, nation.Id, supplierId, sat) != statusFilter.Value) continue;
            }
            count++;
        }
        return count;
    }

    /// <summary>Zero-allocation single-commodity squeezed check for scripted triggers.</summary>
    internal static bool IsCommoditySqueezed(Campaign c, Nation nation, Commodity good)
    {
        if (c is null || nation is null || c.TradeRoutes.Count == 0 || (uint)nation.Id >= (uint)c.Nations.Count)
            return false;
        var slot = -1;
        for (var i = 0; i < TrackedGoods.Length; i++)
            if (TrackedGoods[i] == good) { slot = i; break; }
        if (slot < 0) return false;

        var cache = EnsureCache(c);
        var idx = nation.Id * TrackedGoods.Length + slot;
        var total = cache.TotalImportValue[idx];
        if (total <= 0.0001f) return false;
        var supplierId = cache.TopSupplierId[idx];
        if (supplierId < 0) return false;
        var share = M.Clamp01(cache.TopSupplierValue[idx] / total);
        if (share < ConcentrationThreshold) return false;
        var sat = nation.Market.TryGetValue(good, out var q) ? M.Clamp01(q.Satisfaction) : 1f;
        return ClassifyStatus(c, nation.Id, supplierId, sat) == ChokepointStatus.Squeezed;
    }

    /// <summary>
    /// Weekly diplomatic &amp; chronicle pass (called from <see cref="GrandProjects.Tick"/>).
    /// Runs O(N * 11) flat lookups once per week with zero TradeRoutes rescanning.
    /// </summary>
    public static void TickWeekly(Campaign c)
    {
        foreach (var _ in TickWeeklyStages(c)) { }
    }

    internal static IEnumerable<int> TickWeeklyStages(Campaign c)
    {
        if (c is null || !GrandProjects.IsEnabled(c) || c.TradeRoutes.Count == 0) yield break;
        if (c.ElapsedDays <= 0 || c.ElapsedDays % 7 != 0) yield break;

        var cache = EnsureCache(c);
        if (Campaign.StageOverdue()) yield return 0;
        var stride = TrackedGoods.Length;
        var living = c.ActiveNations;

        for (var i = 0; i < living.Count; i++)
        {
            if (i > 0 && (i & 3) == 0 && Campaign.StageOverdue()) yield return 0;
            var buyer = living[i];
            if (buyer.Movement is not null || (uint)buyer.Id >= (uint)cache.NationCount) continue;

            var baseIdx = buyer.Id * stride;
            var worstSqueezeShare = 0f;
            var worstSqueezeSupplier = -1;
            var worstSqueezeGood = Commodity.Oil;

            var bestAnchorShare = 0f;
            var bestAnchorSupplier = -1;

            for (var slot = 0; slot < stride; slot++)
            {
                var total = cache.TotalImportValue[baseIdx + slot];
                if (total <= 0.0001f) continue;
                var supplierId = cache.TopSupplierId[baseIdx + slot];
                if (supplierId < 0) continue;
                var share = cache.TopSupplierValue[baseIdx + slot] / total;
                if (share < ConcentrationThreshold) continue;

                var commodity = TrackedGoods[slot];
                var sat = buyer.Market.TryGetValue(commodity, out var q) ? M.Clamp01(q.Satisfaction) : 1f;
                var status = ClassifyStatus(c, buyer.Id, supplierId, sat);

                if (status == ChokepointStatus.Squeezed && share > worstSqueezeShare)
                {
                    worstSqueezeShare = share;
                    worstSqueezeSupplier = supplierId;
                    worstSqueezeGood = commodity;
                }
                else if (status == ChokepointStatus.HealthyAnchor && share > bestAnchorShare)
                {
                    bestAnchorShare = share;
                    bestAnchorSupplier = supplierId;
                }
            }

            if (worstSqueezeSupplier >= 0)
            {
                var rel = CampaignDiplomacy.Get(c, buyer.Id, worstSqueezeSupplier);
                if (!HasRecentReason(rel, "chokepoint_weaponized", c.ElapsedDays, DiplomaticCooldownDays))
                {
                    OpinionSystem.Remember(c, rel, "chokepoint_weaponized");
                    c.Chronicle.Record(c, "supply_chokepoint_squeezed", worstSqueezeSupplier, buyer.Id,
                        CommodityCatalog.Get(worstSqueezeGood).Name);
                }
            }

            if (bestAnchorSupplier >= 0)
            {
                var rel = CampaignDiplomacy.Get(c, buyer.Id, bestAnchorSupplier);
                if (!HasRecentReason(rel, "strategic_supply_anchor", c.ElapsedDays, DiplomaticCooldownDays))
                {
                    OpinionSystem.Remember(c, rel, "strategic_supply_anchor");
                }
            }
        }
    }

    private static bool IsSupplierSqueezing(Campaign c, int buyerId, int supplierId, float buyerSat)
    {
        if (CampaignDiplomacy.AtWar(c, buyerId, supplierId)) return true;
        var rel = CampaignDiplomacy.Peek(c, buyerId, supplierId);
        if (rel is null) return false;
        if (rel.EmbargoByA || rel.EmbargoByB) return true;
        return buyerSat < 0.72f && rel.Opinion < -10f;
    }

    private static ChokepointStatus ClassifyStatus(Campaign c, int buyerId, int supplierId, float buyerSat)
    {
        if (IsSupplierSqueezing(c, buyerId, supplierId, buyerSat)) return ChokepointStatus.Squeezed;
        if (buyerSat >= 0.90f && GrandProjects.ShareDiplomaticAlignment(c, buyerId, supplierId))
            return ChokepointStatus.HealthyAnchor;
        return ChokepointStatus.Concentrated;
    }

    private static bool CommodityUsedByActiveProject(Campaign c, int nationId, Commodity commodity)
    {
        var idName = CommodityCatalog.IdOf(commodity);
        for (var i = 0; i < c.GrandProjects.Count; i++)
        {
            var p = c.GrandProjects[i];
            if (p.HostNationId != nationId || p.Status == GrandProjectStatus.Completed) continue;
            var def = GrandProjectCatalog.Find(p.DefinitionId);
            if (def?.KeyGoods is null) continue;
            for (var k = 0; k < def.KeyGoods.Length; k++)
                if (string.Equals(def.KeyGoods[k], idName, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    private static int StatusSortRank(ChokepointStatus status, bool hitsProject) => status switch
    {
        ChokepointStatus.Squeezed => hitsProject ? 0 : 1,
        ChokepointStatus.Concentrated => hitsProject ? 2 : 3,
        _ => 4
    };

    private static bool HasRecentReason(DiplomaticRelation rel, string id, int today, int cooldownDays)
    {
        if (rel?.Reasons is null) return false;
        for (var i = 0; i < rel.Reasons.Count; i++)
        {
            var r = rel.Reasons[i];
            if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase) && today - r.GrantedDay < cooldownDays)
                return true;
        }
        return false;
    }
}
