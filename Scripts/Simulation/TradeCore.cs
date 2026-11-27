using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation;

/// <summary>One country selling one good to another, this economic week.</summary>
public sealed class TradeRoute
{
    public int SellerId;
    public int BuyerId;
    public Commodity Commodity;

    /// <summary>Units moved this week.</summary>
    public float Volume;

    /// <summary>What they were worth, at the seller's price.</summary>
    public float Value;
}

public sealed partial class Campaign
{
    /// <summary>
    /// Who sold what to whom this week.
    ///
    /// Rebuilt from scratch every economic tick, then saved with its settlement day.
    /// Supply diplomacy reads this attribution between market passes: reconstructing it
    /// from changed prices or diplomacy would not reproduce the week that actually traded.
    /// </summary>
    public readonly List<TradeRoute> TradeRoutes = new();
    // -1 explicitly means attribution is unavailable, including saves predating this field.
    public int TradeRoutesDay = -1;
}

public sealed partial class Nation
{
    /// <summary>
    /// What this country's trade with each other country is worth per week.
    ///
    /// The single number the rest of the game most needed and did not have. It is what an
    /// embargo destroys, what a blockade interrupts, and what an invasion of one's largest
    /// customer costs before a shot is fired - which is to say, it is the first thing a
    /// government should weigh and the last thing the old anonymous market allowed it to.
    /// </summary>
    public readonly Dictionary<int, float> TradePartners = new();

    /// <summary>Value bought from abroad, and sold abroad, this economic week.</summary>
    public float ImportValue;
    public float ExportValue;

    /// <summary>How much of this country's economy runs through one other country.</summary>
    public float TradeDependenceOn(int otherNationId)
    {
        var total = ImportValue + ExportValue;
        if (total <= 0.0001f) return 0f;
        return M.Clamp01(TradePartners.GetValueOrDefault(otherNationId) / total);
    }
}

/// <summary>
/// Who trades with whom, and what it costs them to stop.
///
/// The market used to be one anonymous pool. A country short of a good bought a fixed
/// share of its shortfall from "the world" at a constant rate, and a country with a surplus
/// sold into the same nowhere. That is a clean abstraction and it quietly removed every
/// interesting decision from the economy: nobody could be cut off, an embargo was
/// unthinkable because there was no one to embargo, a blockade took money rather than
/// goods, and a trade agreement was worth exactly one opinion modifier.
///
/// Worst of all, it made war cheap. A country could invade its largest customer and the
/// only thing that changed was the front line.
///
/// So trade is bilateral. Each week every shortfall is matched against real surpluses held
/// by real countries, ranked by whether the two can reach each other, whether they are on
/// speaking terms, and whether anybody has forbidden it. What comes out is a list of routes
/// and, from it, the one number the rest of the game most needed: how much of a country's
/// economy runs through each of its neighbours. That number is a peace treaty's real
/// leverage, an embargo's real bite, and the reason an AI should think twice.
///
/// A thin grey market remains underneath, because a country with no friends should be poor
/// rather than instantly dead, and because smuggling is older than customs.
/// </summary>
public static class TradeNetwork
{
    /// <summary>
    /// How many exporters of one good the world's buyers actually shop among.
    ///
    /// Every buyer scoring every seller is two hundred and sixty squared per good per week,
    /// and it would buy nothing: world trade in any one commodity is dominated by a handful
    /// of exporters, and the two-hundredth largest surplus of natural gas is not a supplier
    /// anybody is choosing between.
    /// </summary>
    private const int SellersPerGood = 24;

    /// <summary>Distance at which a partner is half as attractive, in kilometres.</summary>
    private const float HalfReachKilometres = 4200f;

    /// <summary>What a country can still get with no partners at all.</summary>
    private const float GreyMarketAccess = 0.34f;

    // Scratch lives per commodity buffer: a market week may route goods side by side, and
    // a worker may only touch its own good's lists. Everything surviving a yield is owned
    // by the weak-keyed workspace below, not by another live world's market.
    [ThreadStatic] private static Dictionary<int, (int States, int Infrastructure, float Ports, int Rails)> _countsScratch;
    private static Dictionary<int, (int States, int Infrastructure, float Ports, int Rails)> Counts => _countsScratch ??= new();

    /// <summary>One commodity's week: its own seller and buyer scratch, the routes it
    /// computed and the pair affinities it looked up. Computed possibly off the main
    /// thread; merged in catalogue order, so the shared floats see exactly the sequence
    /// the serial week produced.</summary>
    internal sealed class CommodityBuffer
    {
        internal readonly List<int> SellerIds = new();
        internal readonly List<Nation> SellerNations = new();
        internal readonly List<MarketQuote> SellerQuotes = new();
        internal readonly List<float> SellerPrices = new();
        internal readonly List<float> SellerCapacity = new();
        internal readonly int[] SellerOrderArr = new int[SellersPerGood];
        internal readonly float[] SellerScoreArr = new float[SellersPerGood];
        internal MarketQuote[] NationQuotes = Array.Empty<MarketQuote>();
        internal int[] BuyerIds = Array.Empty<int>();
        internal float[] BuyerKeys = Array.Empty<float>();
        internal int Buyers;
        internal readonly List<TradeDelta> Deltas = new();
        internal readonly List<TradeRoute> Pool = new();
        internal int Used;
        // For the trace only: the good, every exporter's surplus before the pool kept the
        // largest few, and what the pool held before the buyers ran it down.
        internal Commodity Good;
        internal float SurplusOfAllSellers, PoolCapacityAtStart;
        // The share of its weighted shortfall each buyer may take when the pool is short
        // (PrepCommodity); infinity when there is enough for everyone.
        internal float RationPerWeight = float.PositiveInfinity;
        // What each buyer was handed this week under a supply guarantee before the queue formed
        // (PrepCommodity): it is no longer short of that, so the queue and the ration see less.
        internal readonly Dictionary<int, float> GuaranteedTake = new();
        // What each guarantor already gave under its promises of this good this week, so two
        // guarantees on one seller cannot hand out the same spare twice.
        internal readonly Dictionary<int, float> GuaranteeUsed = new();
        // Each guarantee's week, folded into its terms on the calling thread (MergeCommodity).
        internal readonly List<(TradeTerms Terms, float Delivered, bool Counted, bool GuarantorShort)> Deliveries = new();
    }

    /// <summary>
    /// One buyer's week for one good, step by step: what it lacked, every factor that cut the
    /// amount it went looking for, what the exporters gave it and what the grey market added.
    /// Written only when a diagnostic (SimBench --trade-why) sets <see cref="BuyerTraceSink"/>;
    /// otherwise nothing is built. The sink may be called from several threads at once.
    /// </summary>
    public sealed class BuyerTrace
    {
        public int NationId;
        public Commodity Good;
        public float Shortfall, ImportAccess, Ceiling, DeskCeiling, Sought, BlockedFactor, DefconFactor;
        public float Bought, PriceRefused, GreyImported, Rationed;
        public int SellersInPool, SellersReachable;
    }

    /// <summary>One good's exporters for the week: their whole surplus, what entered the pool and what was left unsold.</summary>
    public readonly record struct PoolTrace(Commodity Good, float SurplusOfAllSellers, float PoolCapacity, float Unsold, int Sellers);

    public static Action<BuyerTrace> BuyerTraceSink;
    public static Action<PoolTrace> PoolTraceSink;

    internal readonly record struct TradeDelta(int SellerId, int BuyerId, float Value, int PoolSlot);

    private sealed class TradeWorkspace
    {
        internal readonly Dictionary<int, float> TransportReach = new();
        internal float[] TransportReachById = Array.Empty<float>();
        internal Nation[] NationByIdDense = Array.Empty<Nation>();
        internal readonly List<Nation> TradingNations = new();
        internal readonly List<Nation> DistanceParticipants = new();
        internal float[] ImportAccessById = Array.Empty<float>();
        internal float[] ExportAccessById = Array.Empty<float>();
        internal float[] CeilingById = Array.Empty<float>();
        internal bool[] HasAgreementById = Array.Empty<bool>();
        internal ulong[] CustomsUnionMaskById = Array.Empty<ulong>();
        internal bool CustomsUnionOverflow;
        internal float GlobalTradeMultiplier = 1f;
        internal float[] AffinityValues = Array.Empty<float>();
        internal int AffinityStride;
        internal int[] SellerSlots = Array.Empty<int>();
        internal readonly List<int> UniqueSellers = new();
        internal int[] BuyerSlots = Array.Empty<int>();
        internal int[] ActiveBuyers = Array.Empty<int>();
        internal readonly List<TradeRoute> RouteRecords = new();
        internal int UsedRouteRecords;
        internal float[] DistanceTable;
        internal int DistanceTableStride;
        internal int[] DistanceSlots = Array.Empty<int>();
        internal int DistanceSlotCount;
        internal int LastNationsCount;
        internal float[] CapLatById = Array.Empty<float>();
        internal float[] CapLonById = Array.Empty<float>();
        internal bool[] CapTrackedById = Array.Empty<bool>();
        internal readonly List<int> TrackedCapIds = new();
        internal bool[] DirtyDistanceSlots = Array.Empty<bool>();
        internal bool[] HasPartnerRules = Array.Empty<bool>();
        internal readonly List<Nation> DirtyDistanceNations = new();

        internal int[] NativeDistSlotIds = Array.Empty<int>();
        internal float[] NativeDistLats = Array.Empty<float>();
        internal float[] NativeDistLons = Array.Empty<float>();
        internal int[] NativeDirtyIndices = Array.Empty<int>();

        internal int[] NativeBuyerIds = Array.Empty<int>();
        internal ushort[] NativeBuyerMapIdx = Array.Empty<ushort>();
        internal float[] NativeBuyerReach = Array.Empty<float>();
        internal int[] NativeSellerIds = Array.Empty<int>();
        internal ushort[] NativeSellerMapIdx = Array.Empty<ushort>();
        internal float[] NativeSellerReach = Array.Empty<float>();
        internal float[] NativePairOpinion = Array.Empty<float>();
        internal byte[] NativePairStance = Array.Empty<byte>();
        internal byte[] NativePairFlags = Array.Empty<byte>();
        // Filed while the routes are written, so a screen can ask "who sold to this country"
        // without walking every other country's week. Cleared with the routes and marked
        // ready only when the pass finishes, so a half-run week is not treated as complete.
        internal bool IndexReady;
        internal int FiledCount;
        internal readonly Dictionary<int, List<TradeRoute>> Touching = new();
        internal readonly List<CommodityBuffer> CommodityBuffers = new();
        // Each country's customs unions this week: the organisations it belongs to that have
        // chosen a common market (doctrine "common_market").
        internal readonly Dictionary<int, List<int>> CustomsUnions = new();
        // The terms of every trade agreement in force this week, keyed (lower id << 32) | higher id,
        // read once at the start of the week so a good's worker never opens a relation per pair.
        internal readonly Dictionary<long, TradeTerms> PairTerms = new(PairKeyComparer.Instance);
        // The supply guarantees in force, by the good they promise, in relation order.
        internal readonly Dictionary<Commodity, List<(TradeTerms Terms, int GuarantorId, int ReceiverId)>> Guarantees = new();
    }

    private static readonly TradeRoute[] NoRoutes = Array.Empty<TradeRoute>();

    /// <summary>How many times a finished market week has filed its routes. Reading does not move it.</summary>
    internal static int ImportIndexBuilds { get; private set; }

    /// <summary>How many times a reader had to walk the whole route list because the index was not ready.</summary>
    internal static int RouteScans { get; private set; }

    /// <summary>The week's routes were filed under the countries that touch them.</summary>
    public static bool IndexReady(Campaign campaign) =>
        campaign is not null && Workspace(campaign).IndexReady;

    /// <summary>
    /// Fast internal lookup for callers that only iterate the filed route list without a fallback allocation.
    /// Returns true when the campaign's route index matches <see cref="Campaign.TradeRoutes"/>
    /// (in which case <paramref name="routes"/> is null if this nation touched no routes).
    /// Returns false when the index is missing or stale.
    /// </summary>
    internal static bool TryGetFiledRoutesTouching(Campaign campaign, int nationId, out List<TradeRoute> routes)
    {
        routes = null;
        if (campaign is null) return false;
        var work = Workspace(campaign);
        if (!work.IndexReady || work.FiledCount != campaign.TradeRoutes.Count) return false;
        work.Touching.TryGetValue(nationId, out routes);
        return true;
    }

    /// <summary>
    /// Index routes restored from a save or snapshot without counting as a new market-week build.
    /// </summary>
    internal static void FileLoadedRoutes(Campaign campaign)
    {
        if (campaign is null) return;
        var work = Workspace(campaign);
        BeginRouteIndex(work);
        if (campaign.TradeRoutesDay < 0)
        {
            work.FiledCount = 0;
            return;
        }
        var routes = campaign.TradeRoutes;
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            Touch(work, route.SellerId, route);
            if (route.BuyerId != route.SellerId) Touch(work, route.BuyerId, route);
        }
        work.FiledCount = routes.Count;
        work.IndexReady = true;
    }

    /// <summary>
    /// Routes this country sells or buys. After a finished market week this is the list
    /// written during that week. Before that — a save just loaded, or a test that put
    /// routes in by hand — it is the same filter of <see cref="Campaign.TradeRoutes"/>.
    /// </summary>
    public static IReadOnlyList<TradeRoute> RoutesTouching(Campaign campaign, int nationId)
    {
        if (campaign is null) return NoRoutes;
        var work = Workspace(campaign);
        // A hand-edited route list — a test, or a week that was cleared and rewritten —
        // is not the list that was filed. Count is enough: the live week never adds a
        // route except through Deliver, which files it in the same breath.
        if (work.IndexReady && work.FiledCount == campaign.TradeRoutes.Count)
            return work.Touching.TryGetValue(nationId, out var filed) ? filed : NoRoutes;
        RouteScans++;
        var found = new List<TradeRoute>();
        foreach (var route in campaign.TradeRoutes)
            if (route.SellerId == nationId || route.BuyerId == nationId) found.Add(route);
        return found;
    }

    private static void BeginRouteIndex(TradeWorkspace work)
    {
        work.IndexReady = false;
        foreach (var list in work.Touching.Values) list.Clear();
    }

    private static void Touch(TradeWorkspace work, int nationId, TradeRoute route)
    {
        if (!work.Touching.TryGetValue(nationId, out var list))
            work.Touching[nationId] = list = new List<TradeRoute>();
        list.Add(route);
    }
    private static readonly ConditionalWeakTable<Campaign, TradeWorkspace> Workspaces = new();
    private static TradeWorkspace Workspace(Campaign campaign) =>
        Workspaces.GetValue(campaign, static _ => new TradeWorkspace());

    /// <summary>
    /// Match the world's shortfalls against the world's surpluses, and write the result
    /// into every country's quotes.
    /// </summary>
    public static void Route(Campaign campaign)
    {
        foreach (var _ in RouteStages(campaign)) { }
    }

    /// <summary>
    /// The routing in stages - the preparation, then one good at a time - for the smoothed step.
    /// Commodity-local scratch is reused only during a single MoveNext. Route records,
    /// affinity, reach and distance survive yields in each campaign's own workspace.
    /// </summary>
    public static IEnumerable<int> RouteStages(Campaign campaign)
    {
        var nations = campaign.ActiveNations;
        var work = Workspace(campaign);
        campaign.TradeRoutes.Clear();
        work.UsedRouteRecords = 0;
        work.TradingNations.Clear();
        BeginRouteIndex(work);
        var maxNationId = 0;
        for (var ni = 0; ni < nations.Count; ni++)
        {
            var nation = nations[ni];
            if (nation.Id > maxNationId) maxNationId = nation.Id;
            if (nation.TradePartners.Count > 0) nation.TradePartners.Clear();
            nation.ImportValue = 0f;
            nation.ExportValue = 0f;
            if (nation.Market.Count == 0) continue;
            work.TradingNations.Add(nation);
            foreach (var quote in nation.Market.Values)
            {
                quote.Imports = 0f;
                quote.Exports = 0f;
                quote.Relieved = 0f;
            }
        }
        var idSlots = maxNationId + 1;
        if (work.NationByIdDense.Length < idSlots) work.NationByIdDense = new Nation[idSlots + 64];
        else Array.Clear(work.NationByIdDense, 0, idSlots);
        for (var ni = 0; ni < nations.Count; ni++)
        {
            var n = nations[ni];
            if ((uint)n.Id < (uint)idSlots) work.NationByIdDense[n.Id] = n;
        }

        using (SimProfiler.Measure("    mkt:trade"))
        {
            MeasureReach(campaign, work, idSlots);
            CampaignDiplomacy.SharesLandBorder(campaign, 0, 0);
            if (work.CustomsUnionMaskById.Length < idSlots) work.CustomsUnionMaskById = new ulong[idSlots + 64];
            else Array.Clear(work.CustomsUnionMaskById, 0, idSlots);
            work.CustomsUnionOverflow = false;
            var unionSlot = 0;
            foreach (var organization in campaign.Organizations)
            {
                if (organization.Dissolved || organization.DoctrineId != campaign.Defines.CustomsUnionDoctrine) continue;
                if (unionSlot < 64)
                {
                    var bit = 1UL << unionSlot;
                    foreach (var member in organization.Members)
                        if ((uint)member < (uint)idSlots) work.CustomsUnionMaskById[member] |= bit;
                }
                else
                {
                    work.CustomsUnionOverflow = true;
                    break;
                }
                unionSlot++;
            }
            if (work.CustomsUnionOverflow)
            {
                foreach (var unions in work.CustomsUnions.Values) unions.Clear();
                foreach (var organization in campaign.Organizations)
                {
                    if (organization.Dissolved || organization.DoctrineId != campaign.Defines.CustomsUnionDoctrine) continue;
                    foreach (var member in organization.Members)
                    {
                        if (!work.CustomsUnions.TryGetValue(member, out var unions)) work.CustomsUnions[member] = unions = new List<int>();
                        unions.Add(organization.Id);
                    }
                }
            }
            if (work.ImportAccessById.Length < idSlots)
            {
                work.ImportAccessById = new float[idSlots + 64];
                work.ExportAccessById = new float[idSlots + 64];
                work.CeilingById = new float[idSlots + 64];
                work.HasAgreementById = new bool[idSlots + 64];
            }
            else
            {
                Array.Clear(work.HasAgreementById, 0, idSlots);
            }
            var trading = work.TradingNations;
            for (var ti = 0; ti < trading.Count; ti++)
            {
                var n = trading[ti];
                if ((uint)n.Id >= (uint)idSlots) continue;
                work.ImportAccessById[n.Id] = TradePolicyRules.ImportAccess(n.TradePolicy);
                work.ExportAccessById[n.Id] = TradePolicyRules.ExportAccess(n.TradePolicy);
                work.CeilingById[n.Id] = Ceiling(campaign, n, work);
            }
            work.GlobalTradeMultiplier = DefconSystem.GlobalTradeMultiplier(campaign);
            ReadAgreements(campaign, work);
        }
        // Suspend unconditionally after preparation so step/isolation callers can pause cleanly
        // between workspace setup and commodity routing (CampaignIsolationTests).
        yield return 0;

        var catalog = CommodityCatalog.Snapshot.AllGoods;
        while (work.CommodityBuffers.Count < catalog.Length) work.CommodityBuffers.Add(new CommodityBuffer());
        var parallelTrade = DeterministicParallel.Enabled(campaign) && catalog.Length >= 8 &&
                            System.Environment.GetEnvironmentVariable("BDOF_NO_PARALLEL_TRADE") != "1";
        var prepBatch = parallelTrade || Campaign.IsUnboundedStep ? Math.Max(1, catalog.Length) : 12;
        for (var start = 0; start < catalog.Length; start += prepBatch)
        {
            var end = Math.Min(catalog.Length, start + prepBatch);
            using (SimProfiler.Measure("    mkt:trade"))
            using (SimProfiler.Measure("      trade:prepare"))
            {
                if (parallelTrade)
                    DeterministicParallel.ForRange(campaign, start, end, PrepareCommodities, minItems: 2);
                else
                    PrepareCommodities(start, end);
            }
            if (Campaign.StageOverdue()) yield return 0;
        }
        int activeBuyers;
        using (SimProfiler.Measure("    mkt:trade"))
        using (SimProfiler.Measure("      trade:partners"))
            activeBuyers = PreparePartnerLists(work, catalog.Length, idSlots);
        if (Campaign.StageOverdue()) yield return 0;
        if (!EnsureDistancesUnchanged(campaign, work))
        {
            foreach (var _ in EnsureDistancesStages(campaign, work)) if (Campaign.StageOverdue()) yield return 0;
            if (Campaign.StageOverdue()) yield return 0;
        }
        foreach (var stage in PreparePartnerAffinityStages(campaign, work, activeBuyers))
            yield return stage;
        if (!parallelTrade || Campaign.StageOverdue()) yield return 0;

        if (parallelTrade)
        {
            // Goods never read each other's quotes: every cell a route writes is keyed by
            // the good as well as the country. What the goods share - the route list, the
            // partner totals, the touching index - waits for the merge below, which replays
            // the goods in catalogue order and therefore the same float sequence the serial
            // week wrote. Yield between groups of goods when the current slice runs out.
            var batchSize = Campaign.IsUnboundedStep ? catalog.Length : 6;
            for (var start = 0; start < catalog.Length; start += batchSize)
            {
                var end = Math.Min(catalog.Length, start + batchSize);
                using (SimProfiler.Measure("    mkt:trade"))
                using (SimProfiler.Measure("      trade:route"))
                    DeterministicParallel.For(campaign, start, end, ci =>
                    {
                        var buf = work.CommodityBuffers[ci];
                        RunBuyers(campaign, catalog[ci], work, buf, 0, buf.Buyers);
                    }, minItems: 2);
                if (end < catalog.Length && Campaign.StageOverdue()) yield return 0;
            }
            for (var ci = 0; ci < catalog.Length; ci++)
            {
                using (SimProfiler.Measure("    mkt:trade"))
                    MergeCommodity(campaign, work, work.CommodityBuffers[ci]);
                if (ci + 1 < catalog.Length && Campaign.StageOverdue()) yield return 0;
            }
            if (Campaign.StageOverdue()) yield return 0;
        }
        else
        {
            for (var ci = 0; ci < catalog.Length; ci++)
            {
                // One good's routing used to be a single 76 ms stage: the heaviest
                // indivisible piece left in a late week. The seams sit between buyer
                // batches, so the sellers' capacities still run down in the same order.
                var buf = work.CommodityBuffers[ci];
                var buyers = buf.Buyers;
                for (var from = 0; from < buyers; from += 8)
                {
                    using (SimProfiler.Measure("    mkt:trade"))
                    using (SimProfiler.Measure("      trade:route"))
                        RunBuyers(campaign, catalog[ci], work, buf, from, Math.Min(buyers, from + 8));
                    yield return 0;
                }
                using (SimProfiler.Measure("    mkt:trade"))
                    MergeCommodity(campaign, work, buf);
                yield return 0;
            }
        }
        campaign.TradeRoutesDay = campaign.ElapsedDays;
        work.FiledCount = campaign.TradeRoutes.Count;
        work.IndexReady = true;
        ImportIndexBuilds++;

        void PrepareCommodities(int from, int to)
        {
            for (var ci = from; ci < to; ci++)
            {
                var buffer = work.CommodityBuffers[ci];
                buffer.Buyers = PrepCommodity(campaign, catalog[ci], work, buffer, idSlots);
            }
        }
    }

    private static int PreparePartnerLists(TradeWorkspace work, int commodityCount, int slotsLen)
    {
        if (work.SellerSlots.Length < slotsLen) work.SellerSlots = new int[slotsLen];
        Array.Fill(work.SellerSlots, -1);
        if (work.BuyerSlots.Length < slotsLen) work.BuyerSlots = new int[slotsLen];
        Array.Fill(work.BuyerSlots, -1);
        if (work.ActiveBuyers.Length < slotsLen) work.ActiveBuyers = new int[slotsLen];

        work.UniqueSellers.Clear();
        var activeBuyerCount = 0;
        for (var ci = 0; ci < commodityCount; ci++)
        {
            var buf = work.CommodityBuffers[ci];
            if (buf.SellerIds.Count == 0 || buf.Buyers == 0) continue;
            for (var si = 0; si < buf.SellerIds.Count; si++)
            {
                var sid = buf.SellerIds[si];
                if ((uint)sid >= (uint)slotsLen || work.SellerSlots[sid] >= 0) continue;
                work.SellerSlots[sid] = work.UniqueSellers.Count;
                work.UniqueSellers.Add(sid);
            }
            for (var bi = 0; bi < buf.Buyers; bi++)
            {
                var bid = buf.BuyerIds[bi];
                if ((uint)bid >= (uint)slotsLen || work.BuyerSlots[bid] >= 0) continue;
                work.BuyerSlots[bid] = activeBuyerCount;
                work.ActiveBuyers[activeBuyerCount++] = bid;
            }
        }

        if (work.HasPartnerRules.Length < slotsLen) work.HasPartnerRules = new bool[slotsLen];
        else Array.Clear(work.HasPartnerRules, 0, slotsLen);
        var dense = work.NationByIdDense;
        var distanceParticipants = work.DistanceParticipants;
        distanceParticipants.Clear();
        for (var i = 0; i < work.UniqueSellers.Count; i++)
        {
            var sid = work.UniqueSellers[i];
            if (dense[sid] is not { } seller) continue;
            work.HasPartnerRules[sid] = HasAnyPartnerSetting(seller);
            if (work.BuyerSlots[sid] < 0) distanceParticipants.Add(seller);
        }
        for (var i = 0; i < activeBuyerCount; i++)
        {
            var bid = work.ActiveBuyers[i];
            if (dense[bid] is not { } buyer) continue;
            if (work.SellerSlots[bid] < 0) work.HasPartnerRules[bid] = HasAnyPartnerSetting(buyer);
            distanceParticipants.Add(buyer);
        }
        return activeBuyerCount;
    }

    private static IEnumerable<int> PreparePartnerAffinityStages(Campaign campaign, TradeWorkspace work, int activeBuyerCount)
    {
        var uniqueSellers = work.UniqueSellers;
        var stride = work.AffinityStride = uniqueSellers.Count;
        if (stride == 0 || activeBuyerCount == 0) yield break;
        using (SimProfiler.Measure("    mkt:trade"))
        {
            // Closed countries never occupy rows. Keep the reusable matrix proportional
            // to countries buying this week, even when the save has a long history of tags.
            var needed = activeBuyerCount * stride;
            if (work.AffinityValues.Length < needed) work.AffinityValues = new float[needed];
        }
        if (Campaign.StageOverdue()) yield return 0;

        var dense = work.NationByIdDense;
        var activeBuyers = work.ActiveBuyers;
        var affinityValues = work.AffinityValues;

        if (Kernels.NativeKernelsLoader.EnableTradeDefault &&
            Kernels.NativeKernelsLoader.HasCapability(Kernels.NativeKernelsLoader.CapAbiV1Base) &&
            Kernels.NativeTradeArena.ForCampaign(campaign, Math.Max(4096, Math.Max(activeBuyerCount, stride) + 64)) is { } arena)
        {
            var neededPairs = activeBuyerCount * stride;
            if (work.NativeBuyerIds.Length < activeBuyerCount)
            {
                var cap = Math.Max(activeBuyerCount + 64, 128);
                work.NativeBuyerIds = new int[cap];
                work.NativeBuyerMapIdx = new ushort[cap];
                work.NativeBuyerReach = new float[cap];
            }
            if (work.NativeSellerIds.Length < stride)
            {
                var cap = Math.Max(stride + 64, 128);
                work.NativeSellerIds = new int[cap];
                work.NativeSellerMapIdx = new ushort[cap];
                work.NativeSellerReach = new float[cap];
            }
            if (work.NativePairOpinion.Length < neededPairs)
            {
                work.NativePairOpinion = new float[neededPairs];
                work.NativePairStance = new byte[neededPairs];
                work.NativePairFlags = new byte[neededPairs];
            }

            var distSlots = work.DistanceSlots;
            var reachById = work.TransportReachById;
            for (var bi = 0; bi < activeBuyerCount; bi++)
            {
                var bid = activeBuyers[bi];
                var buyer = dense[bid];
                work.NativeBuyerIds[bi] = (uint)bid < (uint)distSlots.Length ? distSlots[bid] : -1;
                work.NativeBuyerMapIdx[bi] = buyer is not null ? (ushort)buyer.MapIndex : (ushort)0;
                work.NativeBuyerReach[bi] = (uint)bid < (uint)reachById.Length && reachById[bid] >= 0f ? reachById[bid] : 0.5f;
            }
            for (var si = 0; si < stride; si++)
            {
                var sid = uniqueSellers[si];
                var seller = dense[sid];
                work.NativeSellerIds[si] = (uint)sid < (uint)distSlots.Length ? distSlots[sid] : -1;
                work.NativeSellerMapIdx[si] = seller is not null ? (ushort)seller.MapIndex : (ushort)0;
                work.NativeSellerReach[si] = (uint)sid < (uint)reachById.Length && reachById[sid] >= 0f ? reachById[sid] : 0.5f;
            }

            var cuOverflow = work.CustomsUnionOverflow;
            var cuMaskById = work.CustomsUnionMaskById;
            var packBatch = Campaign.IsUnboundedStep || DeterministicParallel.Enabled(campaign)
                ? activeBuyerCount
                : Math.Max(8, 16_384 / stride);
            for (var start = 0; start < activeBuyerCount; start += packBatch)
            {
                var end = Math.Min(activeBuyerCount, start + packBatch);
                using (SimProfiler.Measure("    mkt:trade"))
                using (SimProfiler.Measure("      trade:affinity"))
                    DeterministicParallel.ForRange(campaign, start, end, PackAffinityRows, minItems: 8);
                if (end < activeBuyerCount && Campaign.StageOverdue()) yield return 0;
            }

            bool nativeOk;
            using (SimProfiler.Measure("    mkt:trade"))
            using (SimProfiler.Measure("      trade:affinity"))
            {
                nativeOk = arena.TryComputeAffinity(
                    work.NativeBuyerIds.AsSpan(0, activeBuyerCount),
                    work.NativeBuyerMapIdx.AsSpan(0, activeBuyerCount),
                    work.NativeBuyerReach.AsSpan(0, activeBuyerCount),
                    work.NativeSellerIds.AsSpan(0, stride),
                    work.NativeSellerMapIdx.AsSpan(0, stride),
                    work.NativeSellerReach.AsSpan(0, stride),
                    (work.DistanceTable ?? Array.Empty<float>()).AsSpan(),
                    work.DistanceTableStride,
                    work.DistanceSlotCount,
                    work.NativePairOpinion.AsSpan(0, neededPairs),
                    work.NativePairStance.AsSpan(0, neededPairs),
                    work.NativePairFlags.AsSpan(0, neededPairs),
                    stride,
                    HalfReachKilometres,
                    campaign.Defines.CustomsUnionInsideTrade,
                    campaign.Defines.CustomsUnionOutsideTrade,
                    affinityValues.AsSpan(0, neededPairs),
                    stride);
            }
            if (nativeOk) yield break;

            void PackAffinityRows(int from, int to)
            {
                for (var bi = from; bi < to; bi++)
                {
                    var bid = activeBuyers[bi];
                    var buyer = dense[bid];
                    var row = bi * stride;
                    if (buyer is null)
                    {
                        for (var s = 0; s < stride; s++) work.NativePairFlags[row + s] = 32;
                        continue;
                    }
                    var bMap = buyer.MapIndex;
                    ulong bCuMask = 0UL;
                    var bHasCu = false;
                    List<int> bUnions = null;
                    if (!cuOverflow)
                    {
                        bCuMask = (uint)bid < (uint)cuMaskById.Length ? cuMaskById[bid] : 0UL;
                        bHasCu = bCuMask != 0UL;
                    }
                    else
                    {
                        bUnions = work.CustomsUnions.GetValueOrDefault(bid);
                        bHasCu = bUnions is { Count: > 0 };
                    }
                    for (var s = 0; s < stride; s++)
                    {
                        var sid = uniqueSellers[s];
                        var seller = dense[sid];
                        if (seller is null || sid == bid)
                        {
                            work.NativePairFlags[row + s] = 32;
                            work.NativePairStance[row + s] = 0;
                            work.NativePairOpinion[row + s] = 0f;
                            continue;
                        }
                        byte flags = 0;
                        byte stance = 0;
                        float opinion = 0f;
                        var relation = CampaignDiplomacy.Peek(campaign, bid, sid);
                        if (relation is not null)
                        {
                            flags |= 1;
                            if (relation.Stance == DiplomaticStance.AtWar || relation.EmbargoByA || relation.EmbargoByB)
                            {
                                work.NativePairFlags[row + s] = 2;
                                work.NativePairStance[row + s] = 0;
                                work.NativePairOpinion[row + s] = 0f;
                                continue;
                            }
                            opinion = relation.Opinion;
                            if (relation.Stance == DiplomaticStance.Allied) stance = 1;
                            else if (relation.Stance == DiplomaticStance.Rival) stance = 2;
                        }
                        if (bHasCu)
                        {
                            flags |= 8;
                            if (!cuOverflow)
                            {
                                var sMask = (uint)sid < (uint)cuMaskById.Length ? cuMaskById[sid] : 0UL;
                                if ((bCuMask & sMask) != 0UL) flags |= 4;
                            }
                            else
                            {
                                var sUnions = work.CustomsUnions.GetValueOrDefault(sid);
                                if (sUnions is { Count: > 0 })
                                    foreach (var u in bUnions!)
                                        if (sUnions.Contains(u)) { flags |= 4; break; }
                            }
                        }
                        if (CampaignDiplomacy.SharesLandBorder(campaign, bMap, seller.MapIndex)) flags |= 16;
                        work.NativePairFlags[row + s] = flags;
                        work.NativePairStance[row + s] = stance;
                        work.NativePairOpinion[row + s] = opinion;
                    }
                }
            }
        }

        var batchSize = Campaign.IsUnboundedStep || DeterministicParallel.Enabled(campaign)
            ? activeBuyerCount
            : Math.Max(8, 16_384 / stride);
        for (var start = 0; start < activeBuyerCount; start += batchSize)
        {
            var end = Math.Min(activeBuyerCount, start + batchSize);
            using (SimProfiler.Measure("    mkt:trade"))
            using (SimProfiler.Measure("      trade:affinity"))
                DeterministicParallel.ForRange(campaign, start, end, FillRows, minItems: 8);
            if (end < activeBuyerCount && Campaign.StageOverdue()) yield return 0;
        }

        void FillRows(int from, int to)
        {
            var cuOverflow = work.CustomsUnionOverflow;
            var cuMaskById = work.CustomsUnionMaskById;
            var cuInside = campaign.Defines.CustomsUnionInsideTrade;
            var cuOutside = campaign.Defines.CustomsUnionOutsideTrade;
            var distSlots = work.DistanceSlots;
            var distTable = work.DistanceTable;
            var distStride = work.DistanceTableStride;
            var distSlotCount = work.DistanceSlotCount;
            var reachArr = work.TransportReachById;
            for (var bi = from; bi < to; bi++)
            {
                var bid = activeBuyers[bi];
                if (dense[bid] is not { } buyer) continue;
                var row = bi * stride;
                var bMap = buyer.MapIndex;
                var bReach = (uint)bid < (uint)reachArr.Length && reachArr[bid] >= 0f ? reachArr[bid] : 0.5f;
                var bSlot = distTable is not null && (uint)bid < (uint)distSlots.Length ? distSlots[bid] : -1;
                var bSlotValid = (uint)bSlot < (uint)distSlotCount;
                ulong bCuMask = 0UL;
                List<int> bUnions = null;
                var bHasCu = false;
                if (!cuOverflow)
                {
                    bCuMask = (uint)bid < (uint)cuMaskById.Length ? cuMaskById[bid] : 0UL;
                    bHasCu = bCuMask != 0UL;
                }
                else
                {
                    bUnions = work.CustomsUnions.GetValueOrDefault(bid);
                    bHasCu = bUnions is { Count: > 0 };
                }

                for (var s = 0; s < stride; s++)
                {
                    var sid = uniqueSellers[s];
                    var seller = dense[sid];
                    if (seller is null || sid == bid)
                    {
                        affinityValues[row + s] = 0f;
                        continue;
                    }
                    var relation = CampaignDiplomacy.Peek(campaign, bid, sid);
                    if (relation is not null)
                    {
                        if (relation.Stance == DiplomaticStance.AtWar || relation.EmbargoByA || relation.EmbargoByB)
                        {
                            affinityValues[row + s] = -1f;
                            continue;
                        }
                    }
                    var score = 1f;
                    if (relation is not null)
                    {
                        score *= M.Clamp(1f + relation.Opinion / 160f, 0.35f, 1.65f);
                        if (relation.Stance == DiplomaticStance.Allied) score *= 1.30f;
                        else if (relation.Stance == DiplomaticStance.Rival) score *= 0.68f;
                    }
                    if (bHasCu)
                    {
                        if (!cuOverflow)
                        {
                            var sMask = (uint)sid < (uint)cuMaskById.Length ? cuMaskById[sid] : 0UL;
                            score *= (bCuMask & sMask) != 0UL ? cuInside : cuOutside;
                        }
                        else
                        {
                            var sUnions = work.CustomsUnions.GetValueOrDefault(sid);
                            var shared = false;
                            if (sUnions is { Count: > 0 })
                                foreach (var union in bUnions!)
                                    if (sUnions.Contains(union)) { shared = true; break; }
                            score *= shared ? cuInside : cuOutside;
                        }
                    }
                    if (!CampaignDiplomacy.SharesLandBorder(campaign, bMap, seller.MapIndex))
                    {
                        float km;
                        if (bSlotValid && (uint)sid < (uint)distSlots.Length)
                        {
                            var sSlot = distSlots[sid];
                            km = (uint)sSlot < (uint)distSlotCount
                                ? distTable![bSlot * distStride + sSlot]
                                : Kilometres(campaign, buyer, seller, work);
                        }
                        else
                        {
                            km = Kilometres(campaign, buyer, seller, work);
                        }
                        var span = km / HalfReachKilometres;
                        var falloff = 1f / (1f + span * span);
                        var sReach = (uint)sid < (uint)reachArr.Length && reachArr[sid] >= 0f ? reachArr[sid] : 0.5f;
                        var reach = (bReach + sReach) * 0.5f;
                        score *= falloff * reach;
                    }
                    affinityValues[row + s] = score;
                }
            }
        }
    }

    private static int PrepCommodity(Campaign campaign, Commodity commodity, TradeWorkspace work, CommodityBuffer buf, int idSlots)
    {
        var trading = CollectionsMarshal.AsSpan(work.TradingNations);
        var expById = work.ExportAccessById;
        var impById = work.ImportAccessById;
        var ceilById = work.CeilingById;
        var dense = work.NationByIdDense;
        if (buf.NationQuotes.Length < idSlots) buf.NationQuotes = new MarketQuote[idSlots + 64];
        else Array.Clear(buf.NationQuotes, 0, idSlots);
        var nQuotes = buf.NationQuotes;

        // Who has a surplus, biggest first. Only the top few matter, so the list is
        // trimmed rather than sorted in full.
        buf.SellerIds.Clear();
        buf.SellerNations.Clear();
        buf.SellerQuotes.Clear();
        buf.SellerPrices.Clear();
        buf.SellerCapacity.Clear();
        buf.Deltas.Clear();
        buf.Used = 0;
        buf.SurplusOfAllSellers = 0f;
        var orderedCapacity = true;
        for (var index = 0; index < trading.Length; index++)
        {
            var nation = trading[index];
            if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
            if ((uint)nation.Id < (uint)nQuotes.Length) nQuotes[nation.Id] = quote;
            var expAccess = (uint)nation.Id < (uint)expById.Length
                ? expById[nation.Id]
                : TradePolicyRules.ExportAccess(nation.TradePolicy);
            // What is left after the country has kept back whatever it told itself to
            // keep. At a reserve of one nothing leaves, whatever anybody offers, which is
            // what "do not export food while my own people are short" has to mean.
            var capacity = (quote.Production - quote.Demand) *
                           expAccess *
                           (1f - M.Clamp01(TradeDesk.ForGood(nation, commodity, TradeDesk.ExportReserve)));
            if (PoolTraceSink is not null && quote.Production > quote.Demand) buf.SurplusOfAllSellers += quote.Production - quote.Demand;
            if (capacity <= 0.0001f) continue;
            // A full, ordered shortlist rejects most candidates at its last place.
            // Keep the original comparisons if a damaged quote puts NaN in the list.
            if (float.IsNaN(capacity)) orderedCapacity = false;
            if (orderedCapacity && buf.SellerCapacity.Count == SellersPerGood &&
                buf.SellerCapacity[^1] >= capacity) continue;
            Insert(buf.SellerIds, buf.SellerCapacity, nation.Id, capacity, SellersPerGood);
        }
        if (PoolTraceSink is not null)
        {
            buf.Good = commodity;
            buf.PoolCapacityAtStart = 0f;
            foreach (var capacity in buf.SellerCapacity) buf.PoolCapacityAtStart += capacity;
        }
        buf.GuaranteedTake.Clear();
        buf.GuaranteeUsed.Clear();
        buf.Deliveries.Clear();
        if (work.Guarantees.TryGetValue(commodity, out var lanes) && lanes.Count > 0)
            RunGuarantees(campaign, commodity, work, buf, lanes);
        if (buf.SellerIds.Count == 0)
        {
            GreyMarketOnly(campaign, commodity, work);
            return 0;
        }
        for (var s = 0; s < buf.SellerIds.Count; s++)
        {
            var sid = buf.SellerIds[s];
            var seller = (uint)sid < (uint)dense.Length ? dense[sid] : campaign.NationById(sid);
            var sq = (uint)sid < (uint)nQuotes.Length ? nQuotes[sid] : null;
            if (sq is null && seller is not null) seller.Market.TryGetValue(commodity, out sq);
            buf.SellerNations.Add(seller);
            buf.SellerQuotes.Add(sq);
            buf.SellerPrices.Add(sq is not null ? sq.Price : 0f);
        }

        // Who is short, biggest shortfall first. Order matters - the sellers run out - and
        // serving the largest economies first is both the realistic answer and a stable
        // one, since it does not depend on where a country sits in a list.
        if (buf.BuyerIds.Length < trading.Length)
        {
            buf.BuyerIds = new int[trading.Length + 32];
            buf.BuyerKeys = new float[trading.Length + 32];
        }
        var buyers = 0;
        for (var index = 0; index < trading.Length; index++)
        {
            var nation = trading[index];
            var quote = (uint)nation.Id < (uint)nQuotes.Length ? nQuotes[nation.Id] : null;
            if (quote is null) continue;
            var impAccess = (uint)nation.Id < (uint)impById.Length
                ? impById[nation.Id]
                : TradePolicyRules.ImportAccess(nation.TradePolicy);
            var ceil = (uint)nation.Id < (uint)ceilById.Length
                ? ceilById[nation.Id]
                : Ceiling(campaign, nation, work);
            var shortfall = (quote.Demand - quote.Production) *
                            impAccess *
                            ceil *
                            // A country that has capped its imports of this good goes short
                            // on purpose rather than depend on anybody for the rest.
                            M.Clamp01(TradeDesk.ForGood(nation, commodity, TradeDesk.ImportCeiling));
            // What a guarantee already brought is not sought again, and does not weigh in the ration.
            if (buf.GuaranteedTake.Count > 0) shortfall -= buf.GuaranteedTake.GetValueOrDefault(nation.Id);
            if (shortfall <= 0.0001f) continue;
            buf.BuyerIds[buyers] = nation.Id;
            buf.BuyerKeys[buyers] = -shortfall *
                MathF.Max(0.0001f, TradeDesk.ForGood(nation, commodity, TradeDesk.ImportPriority));
            buyers++;
        }
        var weighted = 0f;
        for (var i = 0; i < buyers; i++) weighted -= buf.BuyerKeys[i];
        var pool = 0f;
        for (var i = 0; i < buf.SellerCapacity.Count; i++) pool += buf.SellerCapacity[i];
        buf.RationPerWeight = weighted > pool && weighted > 0.0001f ? pool / weighted : float.PositiveInfinity;
        if (buyers > 1) Array.Sort(buf.BuyerKeys, buf.BuyerIds, 0, buyers);
        return buyers;
    }

    private static void RunBuyers(Campaign campaign, Commodity commodity, TradeWorkspace work, CommodityBuffer buf, int from, int to)
    {
        var impById = work.ImportAccessById;
        var expById = work.ExportAccessById;
        var ceilById = work.CeilingById;
        var defconMult = work.GlobalTradeMultiplier;
        var sellerCount = buf.SellerIds.Count;
        var dense = work.NationByIdDense;
        var nQuotes = buf.NationQuotes;
        var sellerOrder = buf.SellerOrderArr;
        var sellerScores = buf.SellerScoreArr;
        var hasRules = work.HasPartnerRules;
        var hasAg = work.HasAgreementById;
        var pairTerms = work.PairTerms;
        var hasPairTerms = pairTerms.Count > 0;
        var stride = work.AffinityStride;
        var sellerSlots = work.SellerSlots;
        var affinityValues = work.AffinityValues;
        var buyerSlots = work.BuyerSlots;
        var d = campaign.Defines;
        var partnerAffinityWeight = d.TradeAgreementPartnerAffinity;
        var reliefAffinityWeight = d.TradeAgreementReliefAffinity;
        var needTrace = BuyerTraceSink is not null;

        for (var index = from; index < to; index++)
        {
            var buyerId = buf.BuyerIds[index];
            var buyer = (uint)buyerId < (uint)dense.Length ? dense[buyerId] : campaign.NationById(buyerId);
            if (buyer is null) continue;
            // The true shortfall, not the queue position above.
            var buyerQuote = (uint)buyerId < (uint)nQuotes.Length ? nQuotes[buyerId] : null;
            if (buyerQuote is null && !buyer.Market.TryGetValue(commodity, out buyerQuote)) continue;
            var impAccess = (uint)buyer.Id < (uint)impById.Length
                ? impById[buyer.Id]
                : TradePolicyRules.ImportAccess(buyer.TradePolicy);
            var ceil = (uint)buyer.Id < (uint)ceilById.Length
                ? ceilById[buyer.Id]
                : Ceiling(campaign, buyer, work);
            var deskCeil = M.Clamp01(TradeDesk.ForGood(buyer, commodity, TradeDesk.ImportCeiling));
            var deficit = (buyerQuote.Demand - buyerQuote.Production) *
                          impAccess *
                          ceil *
                          deskCeil;
            if (buf.GuaranteedTake.Count > 0) deficit -= buf.GuaranteedTake.GetValueOrDefault(buyer.Id);
            if (deficit <= 0.0001f) continue;
            var trace = !needTrace ? null : new BuyerTrace
            {
                NationId = buyer.Id, Good = commodity, Shortfall = buyerQuote.Demand - buyerQuote.Production,
                ImportAccess = impAccess, Ceiling = ceil,
                DeskCeiling = deskCeil, Sought = deficit,
                SellersInPool = sellerCount
            };

            var checkBuyerRules = (uint)buyer.Id < (uint)hasRules.Length ? hasRules[buyer.Id] : HasAnyPartnerSetting(buyer);
            var buyerMayHaveAg = hasPairTerms && ((uint)buyer.Id >= (uint)hasAg.Length || hasAg[buyer.Id]);
            var buyerSlot = (uint)buyer.Id < (uint)buyerSlots.Length ? buyerSlots[buyer.Id] : -1;
            var hasAffinityRow = stride > 0 && buyerSlot >= 0;
            var affinityRow = buyerSlot * stride;

            // Rank this week's exporters from where the buyer is standing.
            var orderCount = 0;
            var availableCapacity = 0f;
            var blockedCapacity = 0f;
            for (var seat = 0; seat < sellerCount; seat++)
            {
                var cap = buf.SellerCapacity[seat];
                var sellerId = buf.SellerIds[seat];
                if (cap <= 0.0001f || sellerId == buyer.Id) continue;
                var seller = buf.SellerNations[seat];
                if (seller is null) continue;

                var checkSellerRules = (uint)seller.Id < (uint)hasRules.Length ? hasRules[seller.Id] : HasAnyPartnerSetting(seller);
                var forbidden = (checkBuyerRules && TradeDesk.ForPartner(buyer, seller.Tag, commodity, TradeDesk.Forbid) >= 0.5f) ||
                                (checkSellerRules && TradeDesk.ForPartner(seller, buyer.Tag, commodity, TradeDesk.Forbid) >= 0.5f);

                float score;
                var diplomaticBlocked = false;
                if (forbidden)
                {
                    score = 0f;
                }
                else
                {
                    float rawAffinity;
                    if (hasAffinityRow && (uint)seller.Id < (uint)sellerSlots.Length)
                    {
                        var sellerSlot = sellerSlots[seller.Id];
                        rawAffinity = sellerSlot >= 0
                            ? affinityValues[affinityRow + sellerSlot]
                            : PartnerAffinityOrBlocked(campaign, buyer, seller, work);
                    }
                    else
                    {
                        rawAffinity = PartnerAffinityOrBlocked(campaign, buyer, seller, work);
                    }
                    if (rawAffinity < 0f)
                    {
                        diplomaticBlocked = true;
                        score = 0f;
                    }
                    else
                    {
                        score = rawAffinity;
                    }

                    if (score > 0f && buyerMayHaveAg && ((uint)seller.Id >= (uint)hasAg.Length || hasAg[seller.Id]) &&
                        pairTerms.TryGetValue(PairKey(buyer.Id, seller.Id), out var terms))
                    {
                        score *= 1f + partnerAffinityWeight + reliefAffinityWeight * terms.CutFor(buyer.Id, seller.Id, commodity);
                    }
                    var expAccess = (uint)seller.Id < (uint)expById.Length
                        ? expById[seller.Id]
                        : TradePolicyRules.ExportAccess(seller.TradePolicy);
                    if (checkBuyerRules || checkSellerRules)
                    {
                        var prefBuyer = checkBuyerRules ? TradeDesk.ForPartner(buyer, seller.Tag, commodity, TradeDesk.Preference) : 1f;
                        var prefSeller = checkSellerRules ? TradeDesk.ForPartner(seller, buyer.Tag, commodity, TradeDesk.Preference) : 1f;
                        score *= impAccess * expAccess * (prefBuyer * prefSeller);
                    }
                    else
                    {
                        score *= impAccess * expAccess;
                    }
                }

                if (score <= 0.0001f)
                {
                    if (forbidden || diplomaticBlocked)
                    {
                        var reachWeight = CampaignDiplomacy.SharesLandBorder(campaign, buyer.MapIndex, seller.MapIndex)
                            ? 1f
                            : 1f / (1f + MathF.Pow(Kilometres(campaign, buyer, seller, work) / HalfReachKilometres, 2f));
                        blockedCapacity += cap * reachWeight;
                    }
                    continue;
                }
                availableCapacity += cap * score;
                InsertFixed(sellerOrder, sellerScores, ref orderCount, seat, score);
            }

            if (trace is not null) trace.SellersReachable = orderCount;
            if (blockedCapacity > 0.0001f)
            {
                var blockedShare = blockedCapacity / MathF.Max(0.0001f, availableCapacity + blockedCapacity);
                deficit *= 1f - 0.35f * M.Clamp01(blockedShare);
                if (trace is not null) trace.BlockedFactor = 1f - 0.35f * M.Clamp01(blockedShare);
            }
            else if (trace is not null) trace.BlockedFactor = 1f;
            deficit *= defconMult;
            if (trace is not null) trace.DefconFactor = defconMult;
            // What the grey market sees: the whole unmet need, rationing included.
            var unrationed = deficit;
            if (float.IsFinite(buf.RationPerWeight))
                deficit = MathF.Min(deficit, deficit * MathF.Max(0.0001f,
                    TradeDesk.ForGood(buyer, commodity, TradeDesk.ImportPriority)) * buf.RationPerWeight);
            if (trace is not null) trace.Rationed = unrationed - deficit;
            var askedFromSellers = deficit;

            for (var rank = 0; rank < orderCount && deficit > 0.0001f; rank++)
            {
                var seat = sellerOrder[rank];
                var take = MathF.Min(deficit, buf.SellerCapacity[seat]);
                if (take <= 0.0001f) continue;
                var seller = buf.SellerNations[seat];
                var sellerQuote = buf.SellerQuotes[seat];
                if (seller is null || sellerQuote is null) continue;
                if (!TradeDesk.PriceIsAcceptable(buyer, seller, commodity, buf.SellerPrices[seat]))
                {
                    if (trace is not null) trace.PriceRefused += take;
                    continue;
                }

                buf.SellerCapacity[seat] -= take;
                deficit -= take;
                if (trace is not null) trace.Bought += take;
                Record(buf, seller, buyer, sellerQuote, buyerQuote, commodity, take, CutFor(work, buyer.Id, seller.Id, commodity));
            }

            // Whatever no friend would sell. It arrives anyway, thinly and dearly.
            deficit = MathF.Max(0f, unrationed - (askedFromSellers - deficit));
            if (deficit <= 0.0001f) { if (trace is not null) BuyerTraceSink?.Invoke(trace); continue; }
            buyerQuote.Imports += deficit * GreyMarketAccess * campaign.Defines.WorldMarketAccess;
            if (trace is not null)
            {
                trace.GreyImported = deficit * GreyMarketAccess * campaign.Defines.WorldMarketAccess;
                BuyerTraceSink?.Invoke(trace);
            }
        }
    }

    private static void InsertFixed(int[] keys, float[] values, ref int count, int key, float value)
    {
        var position = count;
        for (var index = 0; index < count; index++)
        {
            if (values[index] >= value) continue;
            position = index;
            break;
        }
        if (count >= keys.Length)
        {
            if (position >= count) return;
            for (var j = count - 1; j > position; j--)
            {
                keys[j] = keys[j - 1];
                values[j] = values[j - 1];
            }
            keys[position] = key;
            values[position] = value;
            return;
        }
        for (var j = count; j > position; j--)
        {
            keys[j] = keys[j - 1];
            values[j] = values[j - 1];
        }
        keys[position] = key;
        values[position] = value;
        count++;
    }

    /// <summary>
    /// The route writes a good's compute, in the order the buyers made them. Everything
    /// shared between goods - the route list, the partner totals, the touching index - is
    /// written here, on the calling thread, in catalogue order, so the floats see exactly
    /// the sequence the serial week produced.
    /// </summary>
    private static void MergeCommodity(Campaign campaign, TradeWorkspace work, CommodityBuffer buf)
    {
        if (PoolTraceSink is not null && buf.SellerIds.Count > 0)
        {
            var unsold = 0f;
            foreach (var left in buf.SellerCapacity) unsold += MathF.Max(0f, left);
            PoolTraceSink(new PoolTrace(buf.Good, buf.SurplusOfAllSellers, buf.PoolCapacityAtStart, unsold, buf.SellerIds.Count));
        }
        var deltas = CollectionsMarshal.AsSpan(buf.Deltas);
        var dense = work.NationByIdDense;
        for (var i = 0; i < deltas.Length; i++)
        {
            ref readonly var delta = ref deltas[i];
            var route = buf.Pool[delta.PoolSlot];
            campaign.TradeRoutes.Add(route);
            Touch(work, delta.SellerId, route);
            if (delta.BuyerId != delta.SellerId) Touch(work, delta.BuyerId, route);
            var seller = (uint)delta.SellerId < (uint)dense.Length ? dense[delta.SellerId] : campaign.NationById(delta.SellerId);
            var buyer = (uint)delta.BuyerId < (uint)dense.Length ? dense[delta.BuyerId] : campaign.NationById(delta.BuyerId);
            CollectionsMarshal.GetValueRefOrAddDefault(seller.TradePartners, buyer.Id, out _) += delta.Value;
            CollectionsMarshal.GetValueRefOrAddDefault(buyer.TradePartners, seller.Id, out _) += delta.Value;
            seller.ExportValue += delta.Value;
            buyer.ImportValue += delta.Value;
            if (work.UsedRouteRecords >= work.RouteRecords.Count) work.RouteRecords.Add(route);
            else work.RouteRecords[work.UsedRouteRecords] = route;
            work.UsedRouteRecords++;
        }
        // A guarantee's week: how much of what it owed arrived, folded into a slow average so one
        // bad week is a dent and eight are a broken promise (AgreementSystem reads ShortWeeks).
        var freeBelow = TradeTermsCatalog.Spec.FreeExitBelow;
        foreach (var (terms, delivered, counted, guarantorShort) in buf.Deliveries)
        {
            terms.GuarantorShortWeeks = guarantorShort ? Math.Min(10000, terms.GuarantorShortWeeks + 1) : 0;
            if (!counted) continue;
            terms.Delivered = 0.75f * terms.Delivered + 0.25f * delivered;
            if (delivered < freeBelow) terms.ShortWeeks = Math.Min(10000, terms.ShortWeeks + 1);
            else if (delivered >= 0.9f) terms.ShortWeeks = 0;
        }
        buf.Deliveries.Clear();
    }

    /// <summary>The good's own cells are written as the routes are computed: a quote cell is
    /// keyed by country AND good, so no two workers ever touch one.</summary>
    private static void Record(CommodityBuffer buf, Nation seller, Nation buyer,
        MarketQuote sellerQuote, MarketQuote buyerQuote, Commodity commodity, float volume, float cut)
    {
        sellerQuote.Exports += volume;
        buyerQuote.Imports += volume;
        // The part of this import that came in under a tariff cut: the importer neither marks it
        // up nor collects duty on it (MarketSystem), in proportion to the cut.
        if (cut > 0f) buyerQuote.Relieved += volume * cut;
        var slot = buf.Used;
        TradeRoute route;
        if (slot < buf.Pool.Count) route = buf.Pool[slot];
        else
        {
            route = new TradeRoute();
            buf.Pool.Add(route);
        }
        buf.Used = slot + 1;
        route.SellerId = seller.Id;
        route.BuyerId = buyer.Id;
        route.Commodity = commodity;
        route.Volume = volume;
        route.Value = volume * MathF.Max(0.0001f, sellerQuote.Price);
        buf.Deltas.Add(new TradeDelta(seller.Id, buyer.Id, route.Value, slot));
    }

    /// <summary>Nobody in the world has a surplus of this. Everyone falls back on smuggling.</summary>
    private static void GreyMarketOnly(Campaign campaign, Commodity commodity, TradeWorkspace work)
    {
        var trading = CollectionsMarshal.AsSpan(work.TradingNations);
        var impById = work.ImportAccessById;
        var greyScale = GreyMarketAccess * campaign.Defines.WorldMarketAccess;
        for (var i = 0; i < trading.Length; i++)
        {
            var nation = trading[i];
            if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
            var imp = (uint)nation.Id < (uint)impById.Length ? impById[nation.Id] : TradePolicyRules.ImportAccess(nation.TradePolicy);
            var deficit = (quote.Demand - quote.Production) * imp;
            if (deficit <= 0.0001f) continue;
            quote.Imports += deficit * greyScale;
        }
    }

    /// <summary>
    /// How willing and able these two are to move goods between them, as a multiplier on
    /// nothing in particular - only the ranking matters. Zero means it cannot happen at all.
    /// </summary>
    private static bool HasAnyPartnerSetting(Nation nation)
    {
        if (nation is null || nation.TradeSettings.Count == 0) return false;
        foreach (var key in nation.TradeSettings.Keys)
            if (key.StartsWith("partner/", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool PartnerForbidden(Nation buyer, Nation seller, Commodity commodity, TradeWorkspace work)
    {
        var has = work.HasPartnerRules;
        var checkBuyer = (uint)buyer.Id < (uint)has.Length ? has[buyer.Id] : HasAnyPartnerSetting(buyer);
        if (checkBuyer && TradeDesk.ForPartner(buyer, seller.Tag, commodity, TradeDesk.Forbid) >= 0.5f) return true;
        var checkSeller = (uint)seller.Id < (uint)has.Length ? has[seller.Id] : HasAnyPartnerSetting(seller);
        return checkSeller && TradeDesk.ForPartner(seller, buyer.Tag, commodity, TradeDesk.Forbid) >= 0.5f;
    }

    private static float PartnerPreferenceBetween(Nation buyer, Nation seller, Commodity commodity, TradeWorkspace work)
    {
        var has = work.HasPartnerRules;
        var checkBuyer = (uint)buyer.Id < (uint)has.Length ? has[buyer.Id] : HasAnyPartnerSetting(buyer);
        var checkSeller = (uint)seller.Id < (uint)has.Length ? has[seller.Id] : HasAnyPartnerSetting(seller);
        if (!checkBuyer && !checkSeller) return 1f;
        var prefBuyer = checkBuyer ? TradeDesk.ForPartner(buyer, seller.Tag, commodity, TradeDesk.Preference) : 1f;
        var prefSeller = checkSeller ? TradeDesk.ForPartner(seller, buyer.Tag, commodity, TradeDesk.Preference) : 1f;
        return prefBuyer * prefSeller;
    }

    private static readonly TradeTerms SharedLegacyTerms = TradeTerms.Legacy();

    private static float Affinity(Campaign campaign, Nation buyer, Nation seller, Commodity commodity, TradeWorkspace work)
    {
        // What the two countries have said about each other, which is an embargo one good
        // wide when it names a good and the ordinary kind when it does not.
        if (PartnerForbidden(buyer, seller, commodity, work)) return 0f;

        float raw;
        var stride = work.AffinityStride;
        var buyerSlot = (uint)buyer.Id < (uint)work.BuyerSlots.Length ? work.BuyerSlots[buyer.Id] : -1;
        if (stride > 0 && buyerSlot >= 0 && (uint)seller.Id < (uint)work.SellerSlots.Length)
        {
            var sellerSlot = work.SellerSlots[seller.Id];
            raw = sellerSlot >= 0
                ? work.AffinityValues[buyerSlot * stride + sellerSlot]
                : PartnerAffinityOrBlocked(campaign, buyer, seller, work);
        }
        else
        {
            raw = PartnerAffinityOrBlocked(campaign, buyer, seller, work);
        }
        var score = raw < 0f ? 0f : raw;
        if (score > 0f && work.PairTerms.Count > 0)
        {
            var hasAg = work.HasAgreementById;
            if (((uint)buyer.Id >= (uint)hasAg.Length || hasAg[buyer.Id]) &&
                ((uint)seller.Id >= (uint)hasAg.Length || hasAg[seller.Id]))
                score *= AgreementFactor(campaign, work, buyer.Id, seller.Id, commodity);
        }
        var imp = (uint)buyer.Id < (uint)work.ImportAccessById.Length
            ? work.ImportAccessById[buyer.Id]
            : TradePolicyRules.ImportAccess(buyer.TradePolicy);
        var exp = (uint)seller.Id < (uint)work.ExportAccessById.Length
            ? work.ExportAccessById[seller.Id]
            : TradePolicyRules.ExportAccess(seller.TradePolicy);
        return score * imp * exp * PartnerPreferenceBetween(buyer, seller, commodity, work);
    }

    private static float PartnerAffinity(Campaign campaign, Nation buyer, Nation seller, TradeWorkspace work) =>
        MathF.Max(0f, PartnerAffinityOrBlocked(campaign, buyer, seller, work));

    private static float PartnerAffinityOrBlocked(Campaign campaign, Nation buyer, Nation seller, TradeWorkspace work)
    {

        // A war stops trade, and so does either side forbidding it. These are the whole
        // point of the exercise: they are what an embargo and a blockade now mean.
        var relation = CampaignDiplomacy.Peek(campaign, buyer.Id, seller.Id);
        if (relation is not null)
        {
            if (relation.Stance == DiplomaticStance.AtWar) return -1f;
            if (relation.EmbargoByA || relation.EmbargoByB) return -1f;
        }

        var score = 1f;
        if (relation is not null)
        {
            score *= M.Clamp(1f + relation.Opinion / 160f, 0.35f, 1.65f);
            // A trade agreement is no longer one multiplier on the pair: it is K per good
            // (AgreementFactor), because the cut it gives is a cut on some goods and not others.
            if (relation.Stance == DiplomaticStance.Allied) score *= 1.30f;
            else if (relation.Stance == DiplomaticStance.Rival) score *= 0.68f;
        }
        // A customs union: members buy from each other first and from outside it less - the
        // common external tariff - which is what makes joining one, and leaving it, matter.
        if (!work.CustomsUnionOverflow)
        {
            var masks = work.CustomsUnionMaskById;
            var buyerMask = (uint)buyer.Id < (uint)masks.Length ? masks[buyer.Id] : 0UL;
            if (buyerMask != 0UL)
            {
                var sellerMask = (uint)seller.Id < (uint)masks.Length ? masks[seller.Id] : 0UL;
                score *= (buyerMask & sellerMask) != 0UL
                    ? campaign.Defines.CustomsUnionInsideTrade
                    : campaign.Defines.CustomsUnionOutsideTrade;
            }
        }
        else
        {
            var buyerUnions = work.CustomsUnions.GetValueOrDefault(buyer.Id);
            if (buyerUnions is { Count: > 0 })
            {
                var sellerUnions = work.CustomsUnions.GetValueOrDefault(seller.Id);
                var shared = false;
                if (sellerUnions is { Count: > 0 })
                    foreach (var union in buyerUnions)
                        if (sellerUnions.Contains(union)) { shared = true; break; }
                score *= shared ? campaign.Defines.CustomsUnionInsideTrade : campaign.Defines.CustomsUnionOutsideTrade;
            }
        }

        // Neighbours trade over a border; everyone else trades over a distance, and needs
        // the ports and railways to do it.
        if (!CampaignDiplomacy.SharesLandBorder(campaign, buyer.MapIndex, seller.MapIndex))
        {
            // A squared falloff rather than a fractional power: the same shape to the eye,
            // and not a hundred thousand calls to MathF.Pow every market week.
            var span = Kilometres(campaign, buyer, seller, work) / HalfReachKilometres;
            var falloff = 1f / (1f + span * span);
            var reach = (ReachOr(work, buyer.Id, 0.5f) + ReachOr(work, seller.Id, 0.5f)) * 0.5f;
            score *= falloff * reach;
        }

        return score;
    }

    private static float ReachOr(TradeWorkspace work, int id, float fallback)
    {
        var arr = work.TransportReachById;
        if ((uint)id < (uint)arr.Length)
        {
            var v = arr[id];
            if (v >= 0f) return v;
        }
        return fallback;
    }

    private static long PairKey(int x, int y) => x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;

    /// <summary>
    /// Every trade agreement's terms and every supply guarantee, read off the relations once a week.
    /// A flag without terms (a save from before them, until AgreementSystem gives it some) reads as
    /// the old terms - TradeTerms.Of - and the market writes nothing back onto the relation.
    /// </summary>
    private static void ReadAgreements(Campaign campaign, TradeWorkspace work)
    {
        work.PairTerms.Clear();
        foreach (var list in work.Guarantees.Values) list.Clear();
        var hasAg = work.HasAgreementById;
        foreach (var relation in campaign.Relations.Values)
        {
            if (!relation.TradeAgreement) continue;
            var terms = relation.Trade ?? SharedLegacyTerms;
            work.PairTerms[PairKey(relation.A, relation.B)] = terms;
            if ((uint)relation.A < (uint)hasAg.Length) hasAg[relation.A] = true;
            if ((uint)relation.B < (uint)hasAg.Length) hasAg[relation.B] = true;
            if (!terms.TryGuaranteeGood(out var good)) continue;
            var receiver = terms.GuarantorId == relation.A ? relation.B : relation.A;
            if (!work.Guarantees.TryGetValue(good, out var lanes)) work.Guarantees[good] = lanes = new();
            lanes.Add((terms, terms.GuarantorId, receiver));
        }
    }

    /// <summary>
    /// K for one buyer, one seller and one good: 1 with no agreement, and 1 + the partner term +
    /// the relief term x the buyer's own cut on this good otherwise. The old agreement (half off
    /// everything) is 1.75, the multiplier the flag gave before it had terms.
    /// </summary>
    private static float AgreementFactor(Campaign campaign, TradeWorkspace work, int buyerId, int sellerId, Commodity good)
    {
        if (!work.PairTerms.TryGetValue(PairKey(buyerId, sellerId), out var terms)) return 1f;
        var d = campaign.Defines;
        return 1f + d.TradeAgreementPartnerAffinity + d.TradeAgreementReliefAffinity * terms.CutFor(buyerId, sellerId, good);
    }

    /// <summary>K for a pair read straight off its relation, for a test or a screen between market weeks.</summary>
    public static float AgreementFactor(Campaign campaign, DiplomaticRelation relation, int buyerId, Commodity good)
    {
        var terms = TradeTerms.Of(relation);
        if (terms is null) return 1f;
        return 1f + campaign.Defines.TradeAgreementPartnerAffinity +
               campaign.Defines.TradeAgreementReliefAffinity * terms.CutFor(buyerId, relation.Other(buyerId), good);
    }

    private static float CutFor(TradeWorkspace work, int buyerId, int sellerId, Commodity good) =>
        work.PairTerms.Count > 0 &&
        ((uint)buyerId >= (uint)work.HasAgreementById.Length || work.HasAgreementById[buyerId]) &&
        ((uint)sellerId >= (uint)work.HasAgreementById.Length || work.HasAgreementById[sellerId]) &&
        work.PairTerms.TryGetValue(PairKey(buyerId, sellerId), out var terms)
            ? terms.CutFor(buyerId, sellerId, good) : 0f;

    /// <summary>
    /// The supply guarantees of one good, served before the queue forms. The guarantor hands its
    /// partner up to the promised share of its spare - the same capacity it offers everybody else,
    /// so its export reserve and its own shortages still hold - but never more than the partner
    /// lacks, and never at a price either of them has refused. What it hands over is gone from the
    /// pool before anybody else is served, which is the whole of what "first" means. War, an
    /// embargo or a ban on the good stops the lane as it stops all trade, and such a week is not
    /// counted against the promise; nor is a week in which nothing was owed.
    /// </summary>
    private static void RunGuarantees(Campaign campaign, Commodity commodity, TradeWorkspace work, CommodityBuffer buf,
        List<(TradeTerms Terms, int GuarantorId, int ReceiverId)> lanes)
    {
        var dense = work.NationByIdDense;
        var nQuotes = buf.NationQuotes;
        foreach (var (terms, guarantorId, receiverId) in lanes)
        {
            var seller = (uint)guarantorId < (uint)dense.Length ? dense[guarantorId] : campaign.NationById(guarantorId);
            var buyer = (uint)receiverId < (uint)dense.Length ? dense[receiverId] : campaign.NationById(receiverId);
            if (seller is null || buyer is null) continue;
            var sellerQuote = (uint)guarantorId < (uint)nQuotes.Length ? nQuotes[guarantorId] : null;
            var buyerQuote = (uint)receiverId < (uint)nQuotes.Length ? nQuotes[receiverId] : null;
            if ((sellerQuote is null && !seller.Market.TryGetValue(commodity, out sellerQuote)) ||
                (buyerQuote is null && !buyer.Market.TryGetValue(commodity, out buyerQuote))) continue;
            var relation = CampaignDiplomacy.Peek(campaign, guarantorId, receiverId);
            if (relation is null || relation.Stance == DiplomaticStance.AtWar || relation.EmbargoByA || relation.EmbargoByB ||
                TradeDesk.Forbidden(buyer, seller, commodity)) continue;
            var guarantorShort = sellerQuote.Production < sellerQuote.Demand;
            var capacity = MathF.Max(0f, (sellerQuote.Production - sellerQuote.Demand) *
                           TradePolicyRules.ExportAccess(seller.TradePolicy) *
                           (1f - M.Clamp01(TradeDesk.ForGood(seller, commodity, TradeDesk.ExportReserve))));
            var promised = M.Clamp01(terms.GuaranteeShare) * capacity;
            var need = (buyerQuote.Demand - buyerQuote.Production) *
                       TradePolicyRules.ImportAccess(buyer.TradePolicy) *
                       Ceiling(campaign, buyer, work) *
                       M.Clamp01(TradeDesk.ForGood(buyer, commodity, TradeDesk.ImportCeiling)) -
                       buf.GuaranteedTake.GetValueOrDefault(receiverId);
            var owed = MathF.Min(promised, need);
            if (!float.IsFinite(owed) || owed <= 0.0001f)
            {
                buf.Deliveries.Add((terms, 1f, false, guarantorShort));
                continue;
            }
            var left = capacity - buf.GuaranteeUsed.GetValueOrDefault(guarantorId);
            var take = MathF.Max(0f, MathF.Min(owed, left));
            if (take > 0.0001f && !TradeDesk.PriceIsAcceptable(buyer, seller, commodity, sellerQuote.Price)) take = 0f;
            if (take > 0.0001f)
            {
                buf.GuaranteeUsed[guarantorId] = buf.GuaranteeUsed.GetValueOrDefault(guarantorId) + take;
                buf.GuaranteedTake[receiverId] = buf.GuaranteedTake.GetValueOrDefault(receiverId) + take;
                var seat = buf.SellerIds.IndexOf(guarantorId);
                if (seat >= 0) buf.SellerCapacity[seat] = MathF.Max(0f, buf.SellerCapacity[seat] - take);
                Record(buf, seller, buyer, sellerQuote, buyerQuote, commodity, take, CutFor(work, receiverId, guarantorId, commodity));
            }
            else take = 0f;
            buf.Deliveries.Add((terms, M.Clamp01(take / owed), true, guarantorShort));
        }
    }

    /// <summary>
    /// How much of its shortfall a country can physically bring in.
    ///
    /// Who supplies a country is decided by diplomacy; how much it can take is decided by
    /// its docks and its railways. Keeping the two separate is what makes both matter: an
    /// embargo by a major exporter removes that exporter's capacity from the world's finite
    /// pool and leaves the buyer scrambling, while a landlocked country with no railways is
    /// short of everything no matter how many friends it has - and can do something about
    /// it, by building.
    /// </summary>
    private static float Ceiling(Campaign campaign, Nation nation, TradeWorkspace work) =>
        M.Clamp(campaign.Defines.WorldMarketAccess * ReachOr(work, nation.Id, 0.5f) /
                MathF.Max(0.05f, campaign.Defines.ImportReachReference), 0.30f, 1.05f);

    /// <summary>
    /// How far every country's goods travel, in one pass over the world's regions.
    ///
    /// Infrastructure is what a country already has; ports and railways are what it can
    /// decide to build, and they are the only way a country far from everything can stop
    /// being at the mercy of its immediate neighbours.
    ///
    /// One pass rather than one per country: the same answer, and not two hundred and sixty
    /// walks of eighteen hundred regions every market week.
    /// </summary>
    private static void MeasureReach(Campaign campaign, TradeWorkspace work, int idSlots)
    {
        if (work.TransportReachById.Length < idSlots) work.TransportReachById = new float[idSlots + 64];
        Array.Fill(work.TransportReachById, -1f, 0, idSlots);
        Counts.Clear();
        const int DenseMapLimit = 4096;
        var maxMap = -1;
        var nations = campaign.ActiveNations;
        for (var i = 0; i < nations.Count; i++)
        {
            var mi = nations[i].MapIndex;
            if (mi >= 0 && mi < DenseMapLimit && mi > maxMap) maxMap = mi;
        }
        var denseCounts = maxMap >= 0 ? System.Buffers.ArrayPool<(int States, int Infrastructure, float Ports, int Rails)>.Shared.Rent(maxMap + 1) : null;
        if (denseCounts is not null) Array.Clear(denseCounts, 0, maxMap + 1);
        try
        {
            var states = campaign.States;
            for (var si = 0; si < states.Count; si++)
            {
                var state = states[si];
                var nmi = state.NationMapIndex;
                var infra = state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure);
                var ports = state.Buildings.GetValueOrDefault(BuildingKind.Port) *
                    Waterfront.Yield(campaign, state.MapIndex, BuildingKind.Port);
                var rails = state.Buildings.GetValueOrDefault(BuildingKind.RailNetwork);
                if (denseCounts is not null && (uint)nmi <= (uint)maxMap)
                {
                    ref var slot = ref denseCounts[nmi];
                    slot.States++;
                    slot.Infrastructure += infra;
                    slot.Ports += ports;
                    slot.Rails += rails;
                }
                else
                {
                    var current = Counts.GetValueOrDefault(nmi);
                    Counts[nmi] = (current.States + 1, current.Infrastructure + infra, current.Ports + ports, current.Rails + rails);
                }
            }
            for (var ni = 0; ni < nations.Count; ni++)
            {
                var nation = nations[ni];
                var counted = denseCounts is not null && (uint)nation.MapIndex <= (uint)maxMap
                    ? denseCounts[nation.MapIndex]
                    : Counts.GetValueOrDefault(nation.MapIndex);
                var averageInfrastructure = counted.States == 0 ? 0f : counted.Infrastructure / (float)counted.States;
                var reach = M.Clamp(
                    ReachFloor + averageInfrastructure * ReachPerInfrastructure + counted.Ports * ReachPerPort +
                    counted.Rails * ReachPerRail + NationalModifiers.OfSlot(nation, NationalModifiers.SlotTradeReach),
                    ReachFloor, ReachCeiling);
                if ((uint)nation.Id < (uint)work.TransportReachById.Length)
                    work.TransportReachById[nation.Id] = reach;
            }
        }
        finally
        {
            if (denseCounts is not null)
                System.Buffers.ArrayPool<(int States, int Infrastructure, float Ports, int Rails)>.Shared.Return(denseCounts);
        }
    }

    /// <summary>
    /// How far a country's goods travel (MeasureReach): a floor every country has, the average
    /// infrastructure of its regions, every port - a river port at its waterfront yield - and
    /// every rail network, to a ceiling half as far again as the world's average. Public so the
    /// build list can say what one more port adds without a copy of its own.
    /// </summary>
    public const float ReachFloor = 0.45f;
    public const float ReachCeiling = 1.50f;
    public const float ReachPerInfrastructure = 0.06f;
    public const float ReachPerPort = 0.05f;
    public const float ReachPerRail = 0.035f;

    /// <summary>What one country's goods can reach, for anything that wants to show it.</summary>
    public static float ReachOf(Campaign campaign, Nation nation) =>
        campaign is not null && nation is not null ? ReachOr(Workspace(campaign), nation.Id, 0.45f) : 0.45f;

    /// <summary>Great-circle distance between two capitals, cached across active trading nations.</summary>
    private static float Kilometres(Campaign campaign, Nation a, Nation b, TradeWorkspace work)
    {
        if (a.Id < 0 || b.Id < 0) return 12000f;
        var slots = work.DistanceSlots;
        if (work.DistanceTable is not null && (uint)a.Id < (uint)slots.Length && (uint)b.Id < (uint)slots.Length)
        {
            var sa = slots[a.Id];
            var sb = slots[b.Id];
            if ((uint)sa < (uint)work.DistanceSlotCount && (uint)sb < (uint)work.DistanceSlotCount)
                return work.DistanceTable[sa * work.DistanceTableStride + sb];
        }
        var lo = a.Id <= b.Id ? a : b;
        var hi = a.Id <= b.Id ? b : a;
        return GreatCircle(lo.CapitalLatitude, lo.CapitalLongitude, hi.CapitalLatitude, hi.CapitalLongitude);
    }

    private static bool DistanceTableNeedsCompaction(TradeWorkspace work) =>
        work.DistanceTableStride >= 256 && work.DistanceParticipants.Count * 4L <= work.DistanceSlotCount * 3L;

    private static bool EnsureDistancesUnchanged(Campaign campaign, TradeWorkspace work)
    {
        var participants = work.DistanceParticipants;
        if (work.DistanceTable is null || campaign.Nations.Count < work.LastNationsCount ||
            DistanceTableNeedsCompaction(work)) return false;
        // A returning trader needs its row rebuilt even if its capital never moved.
        // While it was absent, new traders and relocated capitals did not fill those pairs.
        var buyerSlots = work.BuyerSlots;
        var sellerSlots = work.SellerSlots;
        var tracked = work.CapTrackedById;
        var trackedIds = work.TrackedCapIds;
        for (var i = trackedIds.Count - 1; i >= 0; i--)
        {
            var id = trackedIds[i];
            if ((uint)id >= (uint)buyerSlots.Length ||
                buyerSlots[id] < 0 && sellerSlots[id] < 0)
            {
                if ((uint)id < (uint)tracked.Length) tracked[id] = false;
                trackedIds.RemoveAt(i);
            }
        }
        var slots = work.DistanceSlots;
        var capLat = work.CapLatById;
        var capLon = work.CapLonById;
        for (var i = 0; i < participants.Count; i++)
        {
            var nation = participants[i];
            var id = nation.Id;
            if ((uint)id >= (uint)slots.Length || slots[id] < 0) return false;
            if ((uint)id >= (uint)tracked.Length || !tracked[id] ||
                capLat[id] != nation.CapitalLatitude || capLon[id] != nation.CapitalLongitude)
                return false;
        }
        return true;
    }

    private static IEnumerable<int> EnsureDistancesStages(Campaign campaign, TradeWorkspace work)
    {
        var trading = work.DistanceParticipants;
        var dirty = work.DirtyDistanceNations;
        var fullRebuild = false;
        var oldSlotCount = work.DistanceSlotCount;
        using (SimProfiler.Measure("    mkt:trade"))
        {
            var maxId = 0;
            for (var k = 0; k < campaign.Nations.Count; k++)
                if (campaign.Nations[k].Id > maxId) maxId = campaign.Nations[k].Id;
            if (campaign.Nations.Count < work.LastNationsCount || DistanceTableNeedsCompaction(work))
            {
                fullRebuild = true;
                work.DistanceSlotCount = 0;
                oldSlotCount = 0;
                if (work.DistanceSlots.Length > 0) Array.Fill(work.DistanceSlots, -1);
                Array.Clear(work.CapTrackedById);
                work.TrackedCapIds.Clear();
            }
            work.LastNationsCount = campaign.Nations.Count;
            if (work.DistanceSlots.Length <= maxId)
            {
                var prevLen = work.DistanceSlots.Length;
                Array.Resize(ref work.DistanceSlots, Math.Max(128, maxId + 64));
                Array.Fill(work.DistanceSlots, -1, prevLen, work.DistanceSlots.Length - prevLen);
            }
            if (work.CapTrackedById.Length <= maxId)
            {
                var nextCap = Math.Max(128, maxId + 64);
                Array.Resize(ref work.CapTrackedById, nextCap);
                Array.Resize(ref work.CapLatById, nextCap);
                Array.Resize(ref work.CapLonById, nextCap);
            }
            for (var k = 0; k < trading.Count; k++)
            {
                var n = trading[k];
                if (n.Id >= 0 && work.DistanceSlots[n.Id] < 0)
                    work.DistanceSlots[n.Id] = work.DistanceSlotCount++;
            }
            var slotCount = work.DistanceSlotCount;
            var oldStride = work.DistanceTableStride;
            var newStride = Math.Max(64, (slotCount + 63) & ~63);
            if (work.DistanceTable is null || oldStride < slotCount || fullRebuild && oldStride > newStride)
            {
                var next = new float[newStride * newStride];
                Array.Fill(next, 12000f);
                if (work.DistanceTable is not null && oldSlotCount > 0 && !fullRebuild)
                {
                    var copyCols = Math.Min(oldSlotCount, slotCount);
                    for (var r = 0; r < copyCols; r++)
                        Array.Copy(work.DistanceTable, r * oldStride, next, r * newStride, copyCols);
                }
                else fullRebuild = true;
                work.DistanceTable = next;
                work.DistanceTableStride = newStride;
            }
            if (oldSlotCount == 0 && slotCount > 0) fullRebuild = true;

            dirty.Clear();
            if (fullRebuild)
            {
                Array.Fill(work.DistanceTable, 12000f);
                Array.Clear(work.CapTrackedById);
                work.TrackedCapIds.Clear();
                for (var k = 0; k < trading.Count; k++)
                {
                    var n = trading[k];
                    if ((uint)n.Id >= (uint)work.CapTrackedById.Length) continue;
                    work.CapTrackedById[n.Id] = true;
                    work.CapLatById[n.Id] = n.CapitalLatitude;
                    work.CapLonById[n.Id] = n.CapitalLongitude;
                    work.TrackedCapIds.Add(n.Id);
                }
            }
            else
            {
                for (var k = 0; k < trading.Count; k++)
                {
                    var n = trading[k];
                    if (n.Id < 0) continue;
                    var slot = work.DistanceSlots[n.Id];
                    if (slot >= oldSlotCount || !work.CapTrackedById[n.Id] ||
                        work.CapLatById[n.Id] != n.CapitalLatitude ||
                        work.CapLonById[n.Id] != n.CapitalLongitude)
                    {
                        dirty.Add(n);
                        if (!work.CapTrackedById[n.Id])
                        {
                            work.CapTrackedById[n.Id] = true;
                            work.TrackedCapIds.Add(n.Id);
                        }
                        work.CapLatById[n.Id] = n.CapitalLatitude;
                        work.CapLonById[n.Id] = n.CapitalLongitude;
                    }
                }
            }
        }
        yield return 0;

        var table = work.DistanceTable;
        var stride = work.DistanceTableStride;
        var slots = work.DistanceSlots;
        if (!fullRebuild && dirty.Count == 0) yield break;

        if (Kernels.NativeKernelsLoader.EnableTradeDefault &&
            Kernels.NativeKernelsLoader.HasCapability(Kernels.NativeKernelsLoader.CapAbiV1Base) &&
            trading.Count > 0 &&
            Kernels.NativeTradeArena.ForCampaign(campaign, Math.Max(4096, work.DistanceSlotCount + 64)) is { } arena)
        {
            var tCount = trading.Count;
            if (work.NativeDistSlotIds.Length < tCount)
            {
                var cap = Math.Max(tCount + 64, 128);
                work.NativeDistSlotIds = new int[cap];
                work.NativeDistLats = new float[cap];
                work.NativeDistLons = new float[cap];
            }
            for (var k = 0; k < tCount; k++)
            {
                var n = trading[k];
                work.NativeDistSlotIds[k] = (uint)n.Id < (uint)slots.Length ? slots[n.Id] : -1;
                work.NativeDistLats[k] = n.CapitalLatitude;
                work.NativeDistLons[k] = n.CapitalLongitude;
            }
            var dirtyCount = 0;
            if (!fullRebuild)
            {
                if (work.DirtyDistanceSlots.Length < work.DistanceSlotCount)
                    work.DirtyDistanceSlots = new bool[work.DistanceTableStride];
                var changedSlots = work.DirtyDistanceSlots;
                Array.Clear(changedSlots, 0, work.DistanceSlotCount);
                for (var di = 0; di < dirty.Count; di++)
                {
                    var dSlot = slots[dirty[di].Id];
                    if ((uint)dSlot < (uint)changedSlots.Length) changedSlots[dSlot] = true;
                }
                if (work.NativeDirtyIndices.Length < tCount)
                    work.NativeDirtyIndices = new int[Math.Max(tCount + 64, 128)];
                for (var k = 0; k < tCount; k++)
                {
                    var dSlot = work.NativeDistSlotIds[k];
                    if ((uint)dSlot < (uint)changedSlots.Length && changedSlots[dSlot])
                        work.NativeDirtyIndices[dirtyCount++] = k;
                }
            }
            bool ok;
            using (SimProfiler.Measure("    mkt:trade"))
            using (SimProfiler.Measure("      trade:distance"))
            {
                ok = arena.TryUpdateDistances(
                    work.NativeDistSlotIds.AsSpan(0, tCount),
                    work.NativeDistLats.AsSpan(0, tCount),
                    work.NativeDistLons.AsSpan(0, tCount),
                    work.NativeDirtyIndices.AsSpan(0, dirtyCount),
                    fullRebuild,
                    stride,
                    table.AsSpan());
            }
            if (ok)
            {
                dirty.Clear();
                yield break;
            }
        }

        var rowsPerSlice = Math.Clamp(8192 / Math.Max(1, trading.Count), 4, 96);
        if (!fullRebuild)
        {
            if (work.DirtyDistanceSlots.Length < work.DistanceSlotCount)
                work.DirtyDistanceSlots = new bool[work.DistanceTableStride];
            var changedSlots = work.DirtyDistanceSlots;
            Array.Clear(changedSlots, 0, work.DistanceSlotCount);
            foreach (var nation in dirty) changedSlots[slots[nation.Id]] = true;
            var batchSize = Campaign.IsUnboundedStep ? dirty.Count : rowsPerSlice;
            for (var start = 0; start < dirty.Count; start += batchSize)
            {
                var end = Math.Min(dirty.Count, start + batchSize);
                using (SimProfiler.Measure("    mkt:trade"))
                using (SimProfiler.Measure("      trade:distance"))
                    DeterministicParallel.For(campaign, start, end, index =>
                    {
                        var a = dirty[index];
                        var sa = slots[a.Id];
                        for (var j = 0; j < trading.Count; j++)
                        {
                            var b = trading[j];
                            if (b.Id < 0 || b.Id >= slots.Length) continue;
                            var sb = slots[b.Id];
                            // If both capitals changed, only the lower slot writes this pair.
                            if (sb < 0 || sb < sa && changedSlots[sb]) continue;
                            WriteDistance(a, b, sa, sb);
                        }
                    }, minItems: 4);
                if (end < dirty.Count && Campaign.StageOverdue()) yield return 0;
            }
            dirty.Clear();
            yield break;
        }

        var rebuildBatch = Campaign.IsUnboundedStep ? Math.Max(1, trading.Count) : rowsPerSlice;
        for (var start = 0; start < trading.Count; start += rebuildBatch)
        {
            var end = Math.Min(trading.Count, start + rebuildBatch);
            using (SimProfiler.Measure("    mkt:trade"))
            using (SimProfiler.Measure("      trade:distance"))
                DeterministicParallel.For(campaign, start, end, idx =>
                {
                    var a = trading[idx];
                    if (a.Id < 0 || a.Id >= slots.Length) return;
                    var sa = slots[a.Id];
                    if (sa < 0) return;
                    for (var j = idx; j < trading.Count; j++)
                    {
                        var b = trading[j];
                        if (b.Id < 0 || b.Id >= slots.Length) continue;
                        var sb = slots[b.Id];
                        if (sb < 0) continue;
                        WriteDistance(a, b, sa, sb);
                    }
                }, minItems: 4);
            if (end < trading.Count && Campaign.StageOverdue()) yield return 0;
        }

        void WriteDistance(Nation a, Nation b, int firstSlot, int secondSlot)
        {
            var lo = a.Id <= b.Id ? a : b;
            var hi = a.Id <= b.Id ? b : a;
            var kilometres = GreatCircle(lo.CapitalLatitude, lo.CapitalLongitude, hi.CapitalLatitude, hi.CapitalLongitude);
            table[firstSlot * stride + secondSlot] = kilometres;
            table[secondSlot * stride + firstSlot] = kilometres;
        }
    }

    private static float GreatCircle(float latA, float lonA, float latB, float lonB)
    {
        const float radians = MathF.PI / 180f;
        var phiA = latA * radians;
        var phiB = latB * radians;
        var deltaPhi = (latB - latA) * radians;
        var deltaLambda = (lonB - lonA) * radians;
        var haversine = MathF.Sin(deltaPhi * 0.5f) * MathF.Sin(deltaPhi * 0.5f) +
                        MathF.Cos(phiA) * MathF.Cos(phiB) *
                        MathF.Sin(deltaLambda * 0.5f) * MathF.Sin(deltaLambda * 0.5f);
        return 6371f * 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(MathF.Max(0f, haversine))));
    }

    /// <summary>
    /// Insertion into a descending list, capped.
    ///
    /// By hand rather than by sorting afterwards, because this is called about a hundred
    /// thousand times per market week and every comparison-sort of a List with a lambda
    /// allocates. The lists are short, the insert is a memmove, and ties break on the value
    /// already in the seat - which keeps the whole pass reproducible from a seed.
    /// </summary>
    private static void Insert(List<int> keys, List<float> values, int key, float value, int cap)
    {
        var position = values.Count;
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] >= value) continue;
            position = index;
            break;
        }
        if (position >= cap) return;
        keys.Insert(position, key);
        values.Insert(position, value);
        if (keys.Count <= cap) return;
        keys.RemoveAt(keys.Count - 1);
        values.RemoveAt(values.Count - 1);
    }
}
