using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
        internal readonly List<float> SellerCapacity = new();
        internal readonly List<float> SellerScore = new();
        internal readonly List<int> SellerOrder = new();
        internal int[] BuyerIds = Array.Empty<int>();
        internal float[] BuyerKeys = Array.Empty<float>();
        internal readonly List<TradeDelta> Deltas = new();
        internal readonly List<TradeRoute> Pool = new();
        // Keyed by (buyer << 32) | seller, and a long's own hash is the XOR of its halves:
        // five hundred countries are a quarter of a million pairs under 511 codes. Every
        // lookup walked a chain of other pairs, and folding a good's scores into the week's
        // table (MergeCommodity) walked a longer one - 80 ms of the trade week's 190 over
        // fourteen days of a save from 2050. PairKeyComparer spreads the two ids over the
        // whole code; a dictionary lists its entries in the order they were added whatever
        // the hash, so the fold below is in the order it always was.
        internal readonly Dictionary<long, float> Affinity = new(PairKeyComparer.Instance);
        internal int Used;
        // For the trace only: the good, every exporter's surplus before the pool kept the
        // largest few, and what the pool held before the buyers ran it down.
        internal Commodity Good;
        internal float SurplusOfAllSellers, PoolCapacityAtStart;
        // The share of its weighted shortfall each buyer may take when the pool is short
        // (PrepCommodity); infinity when there is enough for everyone.
        internal float RationPerWeight = float.PositiveInfinity;
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
        internal readonly Dictionary<long, float> PartnerAffinityScores = new(PairKeyComparer.Instance);
        internal readonly List<TradeRoute> RouteRecords = new();
        internal int UsedRouteRecords;
        internal float[] DistanceTable;
        internal int DistanceNationCount;
        internal readonly Dictionary<int, (float Latitude, float Longitude)> CapitalCoordinates = new();
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
        var nations = campaign.Nations;
        var work = Workspace(campaign);
        campaign.TradeRoutes.Clear();
        work.UsedRouteRecords = 0;
        BeginRouteIndex(work);
        foreach (var nation in nations)
        {
            nation.TradePartners.Clear();
            nation.ImportValue = 0f;
            nation.ExportValue = 0f;
            foreach (var quote in nation.Market.Values)
            {
                quote.Imports = 0f;
                quote.Exports = 0f;
            }
        }

        if (!EnsureDistancesUnchanged(campaign, work))
        {
            foreach (var _ in EnsureDistancesStages(campaign, work)) yield return 0;
            yield return 0;
        }
        using (SimProfiler.Measure("    mkt:trade"))
        {
            MeasureReach(campaign, work);
            CampaignDiplomacy.SharesLandBorder(campaign, 0, 0);
            work.PartnerAffinityScores.Clear();
            work.CustomsUnions.Clear();
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
        yield return 0;

        var catalog = new List<Commodity>();
        foreach (var commodity in CommodityCatalog.All) catalog.Add(commodity);
        while (work.CommodityBuffers.Count < catalog.Count) work.CommodityBuffers.Add(new CommodityBuffer());
        var parallelTrade = DeterministicParallel.Enabled(campaign) && catalog.Count >= 8 &&
                            System.Environment.GetEnvironmentVariable("BDOF_NO_PARALLEL_TRADE") != "1";
        if (parallelTrade)
        {
            // Goods never read each other's quotes: every cell a route writes is keyed by
            // the good as well as the country. What the goods share - the route list, the
            // partner totals, the touching index - waits for the merge below, which replays
            // the goods in catalogue order and therefore the same float sequence the serial
            // week wrote. Batched with yields so smooth ticks never swallow a 20 ms block.
            const int batchSize = 6;
            for (var start = 0; start < catalog.Count; start += batchSize)
            {
                var end = Math.Min(catalog.Count, start + batchSize);
                using (SimProfiler.Measure("    mkt:trade"))
                    DeterministicParallel.For(campaign, start, end, ci =>
                        RouteOneCompute(campaign, catalog[ci], work, work.CommodityBuffers[ci]), minItems: 2);
                yield return 0;
            }
            for (var ci = 0; ci < catalog.Count; ci++)
            {
                using (SimProfiler.Measure("    mkt:trade"))
                    MergeCommodity(campaign, work, work.CommodityBuffers[ci]);
                if ((ci + 1) % 4 == 0 && ci + 1 < catalog.Count) yield return 0;
            }
            yield return 0;
        }
        else
        {
            for (var ci = 0; ci < catalog.Count; ci++)
            {
                // One good's routing used to be a single 76 ms stage: the heaviest
                // indivisible piece left in a late week. The seams sit between buyer
                // batches, so the sellers' capacities still run down in the same order.
                var buf = work.CommodityBuffers[ci];
                var buyers = PrepCommodity(campaign, catalog[ci], work, buf);
                for (var from = 0; from < buyers; from += 8)
                {
                    using (SimProfiler.Measure("    mkt:trade"))
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
    }

    private static int PrepCommodity(Campaign campaign, Commodity commodity, TradeWorkspace work, CommodityBuffer buf)
    {
        var nations = campaign.Nations;

        // Who has a surplus, biggest first. Only the top few matter, so the list is
        // trimmed rather than sorted in full.
        buf.SellerIds.Clear();
        buf.SellerCapacity.Clear();
        buf.Deltas.Clear();
        buf.Affinity.Clear();
        buf.Used = 0;
        buf.SurplusOfAllSellers = 0f;
        for (var index = 0; index < nations.Count; index++)
        {
            var nation = nations[index];
            if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
            // What is left after the country has kept back whatever it told itself to
            // keep. At a reserve of one nothing leaves, whatever anybody offers, which is
            // what "do not export food while my own people are short" has to mean.
            var capacity = (quote.Production - quote.Demand) *
                           TradePolicyRules.ExportAccess(nation.TradePolicy) *
                           (1f - M.Clamp01(TradeDesk.ForGood(nation, commodity, TradeDesk.ExportReserve)));
            if (PoolTraceSink is not null && quote.Production > quote.Demand) buf.SurplusOfAllSellers += quote.Production - quote.Demand;
            if (capacity <= 0.0001f) continue;
            Insert(buf.SellerIds, buf.SellerCapacity, nation.Id, capacity, SellersPerGood);
        }
        if (PoolTraceSink is not null)
        {
            buf.Good = commodity;
            buf.PoolCapacityAtStart = 0f;
            foreach (var capacity in buf.SellerCapacity) buf.PoolCapacityAtStart += capacity;
        }
        if (buf.SellerIds.Count == 0)
        {
            GreyMarketOnly(campaign, commodity);
            return 0;
        }

        // Who is short, biggest shortfall first. Order matters - the sellers run out - and
        // serving the largest economies first is both the realistic answer and a stable
        // one, since it does not depend on where a country sits in a list.
        // Ordering two hundred buyers by insertion is forty thousand list moves per good and
        // eight hundred thousand per market week, which was most of what this pass cost. A
        // sort of two reused arrays does the same job; it is deterministic for identical
        // input, which is what the seed has to be able to rely on.
        if (buf.BuyerIds.Length < nations.Count)
        {
            buf.BuyerIds = new int[nations.Count + 32];
            buf.BuyerKeys = new float[nations.Count + 32];
        }
        var buyers = 0;
        for (var index = 0; index < nations.Count; index++)
        {
            var nation = nations[index];
            if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
            var shortfall = (quote.Demand - quote.Production) *
                            TradePolicyRules.ImportAccess(nation.TradePolicy) *
                            Ceiling(campaign, nation, work) *
                            // A country that has capped its imports of this good goes short
                            // on purpose rather than depend on anybody for the rest.
                            M.Clamp01(TradeDesk.ForGood(nation, commodity, TradeDesk.ImportCeiling));
            if (shortfall <= 0.0001f) continue;
            buf.BuyerIds[buyers] = nation.Id;
            // Ordered by need, weighted by how badly the country says it wants this good.
            // The weight decides the QUEUE and not the amount: a priority of three does not
            // buy three times as much, it is served before somebody equally short who said
            // nothing. What is actually taken is worked out again below from the shortfall
            // itself, so an instruction cannot conjure goods that were never produced.
            buf.BuyerKeys[buyers] = -shortfall *
                MathF.Max(0.0001f, TradeDesk.ForGood(nation, commodity, TradeDesk.ImportPriority));
            buyers++;
        }
        // When the exporters cannot cover everybody, everybody gets the same share of what it
        // lacks, weighed by its priority - not the largest buyer all of it and the smallest
        // none. Served strictly in order, the giants emptied the pool and Brunei, the Maldives
        // and the Bahamas, whose grain needs are a rounding error, got a quarter of them from the
        // grey market and a famine alert. The weight is the queue key itself, so a country (or
        // its ministry) that raises an import priority still gets more of a scarce good.
        var weighted = 0f;
        for (var i = 0; i < buyers; i++) weighted -= buf.BuyerKeys[i];
        var pool = 0f;
        foreach (var capacity in buf.SellerCapacity) pool += capacity;
        buf.RationPerWeight = weighted > pool && weighted > 0.0001f ? pool / weighted : float.PositiveInfinity;
        if (buyers > 1) Array.Sort(buf.BuyerKeys, buf.BuyerIds, 0, buyers);
        return buyers;
    }

    private static void RunBuyers(Campaign campaign, Commodity commodity, TradeWorkspace work, CommodityBuffer buf, int from, int to)
    {
        for (var index = from; index < to; index++)
        {
            var buyer = campaign.NationById(buf.BuyerIds[index]);
            if (buyer is null) continue;
            // The true shortfall, not the queue position above.
            if (!buyer.Market.TryGetValue(commodity, out var buyerQuote)) continue;
            var deficit = (buyerQuote.Demand - buyerQuote.Production) *
                          TradePolicyRules.ImportAccess(buyer.TradePolicy) *
                          Ceiling(campaign, buyer, work) *
                          M.Clamp01(TradeDesk.ForGood(buyer, commodity, TradeDesk.ImportCeiling));
            if (deficit <= 0.0001f) continue;
            var trace = BuyerTraceSink is null ? null : new BuyerTrace
            {
                NationId = buyer.Id, Good = commodity, Shortfall = buyerQuote.Demand - buyerQuote.Production,
                ImportAccess = TradePolicyRules.ImportAccess(buyer.TradePolicy), Ceiling = Ceiling(campaign, buyer, work),
                DeskCeiling = M.Clamp01(TradeDesk.ForGood(buyer, commodity, TradeDesk.ImportCeiling)), Sought = deficit,
                SellersInPool = buf.SellerIds.Count
            };

            // Rank this week's exporters from where the buyer is standing.
            buf.SellerScore.Clear();
            buf.SellerOrder.Clear();
            var availableCapacity = 0f;
            var blockedCapacity = 0f;
            for (var seat = 0; seat < buf.SellerIds.Count; seat++)
            {
                var cap = buf.SellerCapacity[seat];
                if (cap <= 0.0001f || buf.SellerIds[seat] == buyer.Id) continue;
                var seller = campaign.NationById(buf.SellerIds[seat]);
                if (seller is null) continue;
                var score = Affinity(campaign, buyer, seller, commodity, work, buf.Affinity);
                if (score <= 0.0001f)
                {
                    // If a real exporter is cut off by an embargo or war, track the lost
                    // supplier capacity so an embargo inflicts real switching friction.
                    if (TradeDesk.Forbidden(buyer, seller, commodity) ||
                        CampaignDiplomacy.Peek(campaign, buyer.Id, seller.Id) is { } rel &&
                        (rel.Stance == DiplomaticStance.AtWar || rel.EmbargoByA || rel.EmbargoByB))
                    {
                        var reachWeight = CampaignDiplomacy.SharesLandBorder(campaign, buyer.MapIndex, seller.MapIndex)
                            ? 1f
                            : 1f / (1f + MathF.Pow(Kilometres(campaign, buyer, seller, work) / HalfReachKilometres, 2f));
                        blockedCapacity += cap * reachWeight;
                    }
                    continue;
                }
                availableCapacity += cap * score;
                Insert(buf.SellerOrder, buf.SellerScore, seat, score, int.MaxValue);
            }

            if (trace is not null) trace.SellersReachable = buf.SellerOrder.Count;
            if (blockedCapacity > 0.0001f)
            {
                var blockedShare = blockedCapacity / MathF.Max(0.0001f, availableCapacity + blockedCapacity);
                deficit *= 1f - 0.35f * M.Clamp01(blockedShare);
                if (trace is not null) trace.BlockedFactor = 1f - 0.35f * M.Clamp01(blockedShare);
            }
            else if (trace is not null) trace.BlockedFactor = 1f;
            deficit *= DefconSystem.GlobalTradeMultiplier(campaign);
            if (trace is not null) trace.DefconFactor = DefconSystem.GlobalTradeMultiplier(campaign);
            // What the grey market sees: the whole unmet need, rationing included.
            var unrationed = deficit;
            if (float.IsFinite(buf.RationPerWeight))
                deficit = MathF.Min(deficit, deficit * MathF.Max(0.0001f,
                    TradeDesk.ForGood(buyer, commodity, TradeDesk.ImportPriority)) * buf.RationPerWeight);
            if (trace is not null) trace.Rationed = unrationed - deficit;
            var askedFromSellers = deficit;

            for (var rank = 0; rank < buf.SellerOrder.Count && deficit > 0.0001f; rank++)
            {
                var seat = buf.SellerOrder[rank];
                var take = MathF.Min(deficit, buf.SellerCapacity[seat]);
                if (take <= 0.0001f) continue;
                var seller = campaign.NationById(buf.SellerIds[seat]);
                if (seller is null) continue;
                // A price either side has refused in advance. The seller's own price is what
                // a route costs - see Record - so this is the same number the deal would be
                // written at, against the limits the two countries set as multiples of what
                // the good is worth at base.
                if (!TradeDesk.PriceIsAcceptable(buyer, seller, commodity,
                        seller.Market.TryGetValue(commodity, out var offer) ? offer.Price : 0f))
                {
                    if (trace is not null) trace.PriceRefused += take;
                    continue;
                }

                buf.SellerCapacity[seat] -= take;
                deficit -= take;
                if (trace is not null) trace.Bought += take;
                Record(buf, seller, buyer, commodity, take);
            }

            // Whatever no friend would sell. It arrives anyway, thinly and dearly, because
            // a country with no partners should be poor rather than dead on the same day.
            // The grey market sees everything still unmet: what the sellers could not give, and
            // what rationing held back.
            deficit = MathF.Max(0f, unrationed - (askedFromSellers - deficit));
            if (deficit <= 0.0001f) { if (trace is not null) BuyerTraceSink?.Invoke(trace); continue; }
            var quote = buyer.Market[commodity];
            quote.Imports += deficit * GreyMarketAccess * campaign.Defines.WorldMarketAccess;
            if (trace is not null)
            {
                trace.GreyImported = deficit * GreyMarketAccess * campaign.Defines.WorldMarketAccess;
                BuyerTraceSink?.Invoke(trace);
            }
        }
    }

    /// <summary>A whole good's week, for one worker: the preparation and every buyer.</summary>
    private static void RouteOneCompute(Campaign campaign, Commodity commodity, TradeWorkspace work, CommodityBuffer buf)
    {
        var buyers = PrepCommodity(campaign, commodity, work, buf);
        RunBuyers(campaign, commodity, work, buf, 0, buyers);
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
        for (var i = 0; i < buf.Deltas.Count; i++)
        {
            var delta = buf.Deltas[i];
            var route = buf.Pool[delta.PoolSlot];
            campaign.TradeRoutes.Add(route);
            Touch(work, delta.SellerId, route);
            if (delta.BuyerId != delta.SellerId) Touch(work, delta.BuyerId, route);
            var seller = campaign.NationById(delta.SellerId);
            var buyer = campaign.NationById(delta.BuyerId);
            seller.TradePartners[buyer.Id] = seller.TradePartners.GetValueOrDefault(buyer.Id) + delta.Value;
            buyer.TradePartners[seller.Id] = buyer.TradePartners.GetValueOrDefault(seller.Id) + delta.Value;
            seller.ExportValue += delta.Value;
            buyer.ImportValue += delta.Value;
            if (work.UsedRouteRecords >= work.RouteRecords.Count) work.RouteRecords.Add(route);
            else work.RouteRecords[work.UsedRouteRecords] = route;
            work.UsedRouteRecords++;
        }
        foreach (var (key, score) in buf.Affinity) work.PartnerAffinityScores.TryAdd(key, score);
    }

    /// <summary>The good's own cells are written as the routes are computed: a quote cell is
    /// keyed by country AND good, so no two workers ever touch one.</summary>
    private static void Record(CommodityBuffer buf, Nation seller, Nation buyer, Commodity commodity, float volume)
    {
        var sellerQuote = seller.Market[commodity];
        var buyerQuote = buyer.Market[commodity];
        sellerQuote.Exports += volume;
        buyerQuote.Imports += volume;
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
    private static void GreyMarketOnly(Campaign campaign, Commodity commodity)
    {
        foreach (var nation in campaign.Nations)
        {
            if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
            var deficit = (quote.Demand - quote.Production) *
                          TradePolicyRules.ImportAccess(nation.TradePolicy);
            if (deficit <= 0.0001f) continue;
            quote.Imports += deficit * GreyMarketAccess * campaign.Defines.WorldMarketAccess;
        }
    }

    /// <summary>
    /// How willing and able these two are to move goods between them, as a multiplier on
    /// nothing in particular - only the ranking matters. Zero means it cannot happen at all.
    /// </summary>
    private static float Affinity(Campaign campaign, Nation buyer, Nation seller, Commodity commodity, TradeWorkspace work) =>
        Affinity(campaign, buyer, seller, commodity, work, work.PartnerAffinityScores);

    private static float Affinity(Campaign campaign, Nation buyer, Nation seller, Commodity commodity,
        TradeWorkspace work, Dictionary<long, float> affinity)
    {
        // What the two countries have said about each other, which is an embargo one good
        // wide when it names a good and the ordinary kind when it does not.
        if (TradeDesk.Forbidden(buyer, seller, commodity)) return 0f;

        var key = ((long)buyer.Id << 32) | (uint)seller.Id;
        if (!affinity.TryGetValue(key, out var score))
        {
            score = PartnerAffinity(campaign, buyer, seller, work);
            affinity[key] = score;
        }
        return score * TradePolicyRules.ImportAccess(buyer.TradePolicy) *
               TradePolicyRules.ExportAccess(seller.TradePolicy) *
               TradeDesk.PreferenceBetween(buyer, seller, commodity);
    }

    private static float PartnerAffinity(Campaign campaign, Nation buyer, Nation seller, TradeWorkspace work)
    {

        // A war stops trade, and so does either side forbidding it. These are the whole
        // point of the exercise: they are what an embargo and a blockade now mean.
        var relation = CampaignDiplomacy.Peek(campaign, buyer.Id, seller.Id);
        if (relation is not null)
        {
            if (relation.Stance == DiplomaticStance.AtWar) return 0f;
            if (relation.EmbargoByA || relation.EmbargoByB) return 0f;
        }

        var score = 1f;
        if (relation is not null)
        {
            score *= M.Clamp(1f + relation.Opinion / 160f, 0.35f, 1.65f);
            if (relation.TradeAgreement) score *= 1.75f;
            if (relation.Stance == DiplomaticStance.Allied) score *= 1.30f;
            else if (relation.Stance == DiplomaticStance.Rival) score *= 0.68f;
        }
        // A customs union: members buy from each other first and from outside it less - the
        // common external tariff - which is what makes joining one, and leaving it, matter.
        var buyerUnions = work.CustomsUnions.GetValueOrDefault(buyer.Id);
        if (buyerUnions is not null)
        {
            var sellerUnions = work.CustomsUnions.GetValueOrDefault(seller.Id);
            var shared = false;
            if (sellerUnions is not null)
                foreach (var union in buyerUnions)
                    if (sellerUnions.Contains(union)) { shared = true; break; }
            score *= shared ? campaign.Defines.CustomsUnionInsideTrade : campaign.Defines.CustomsUnionOutsideTrade;
        }

        // Neighbours trade over a border; everyone else trades over a distance, and needs
        // the ports and railways to do it.
        if (!CampaignDiplomacy.SharesLandBorder(campaign, buyer.MapIndex, seller.MapIndex))
        {
            // A squared falloff rather than a fractional power: the same shape to the eye,
            // and not a hundred thousand calls to MathF.Pow every market week.
            var span = Kilometres(campaign, buyer, seller, work) / HalfReachKilometres;
            var falloff = 1f / (1f + span * span);
            var reach = (work.TransportReach.GetValueOrDefault(buyer.Id, 0.5f) + work.TransportReach.GetValueOrDefault(seller.Id, 0.5f)) * 0.5f;
            score *= falloff * reach;
        }

        return score;
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
        M.Clamp(campaign.Defines.WorldMarketAccess * work.TransportReach.GetValueOrDefault(nation.Id, 0.5f) /
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
    private static void MeasureReach(Campaign campaign, TradeWorkspace work)
    {
        work.TransportReach.Clear();
        Counts.Clear();
        foreach (var state in campaign.States)
        {
            var current = Counts.GetValueOrDefault(state.NationMapIndex);
            Counts[state.NationMapIndex] = (
                current.States + 1,
                current.Infrastructure + state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure),
                // A river port reaches less of the world than a sea port. Counting them
                // alike was how a landlocked country could trade its way to the far side of
                // the planet on barge terminals.
                current.Ports + state.Buildings.GetValueOrDefault(BuildingKind.Port) *
                    Waterfront.Yield(campaign, state.MapIndex, BuildingKind.Port),
                current.Rails + state.Buildings.GetValueOrDefault(BuildingKind.RailNetwork));
        }
        foreach (var nation in campaign.Nations)
        {
            var counted = Counts.GetValueOrDefault(nation.MapIndex);
            var averageInfrastructure = counted.States == 0 ? 0f : counted.Infrastructure / (float)counted.States;
            work.TransportReach[nation.Id] = M.Clamp(
                ReachFloor + averageInfrastructure * ReachPerInfrastructure + counted.Ports * ReachPerPort +
                counted.Rails * ReachPerRail + NationalModifiers.Of(nation, "trade_reach"),
                ReachFloor, ReachCeiling);
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
        Workspace(campaign).TransportReach.TryGetValue(nation.Id, out var reach) ? reach : 0.45f;

    /// <summary>Great-circle distance between two capitals, cached for the whole campaign.</summary>
    private static float Kilometres(Campaign campaign, Nation a, Nation b, TradeWorkspace work)
    {
        var count = work.DistanceNationCount;
        if (a.Id < 0 || b.Id < 0 || a.Id >= count || b.Id >= count) return 12000f;
        return work.DistanceTable[a.Id * count + b.Id];
    }

    private static bool EnsureDistancesUnchanged(Campaign campaign, TradeWorkspace work)
    {
        var count = 0;
        foreach (var nation in campaign.Nations) count = Math.Max(count, nation.Id + 1);
        var unchanged = work.DistanceTable is not null && work.DistanceNationCount == count &&
            work.CapitalCoordinates.Count == campaign.Nations.Count;
        if (!unchanged) return false;
        foreach (var nation in campaign.Nations)
            if (!work.CapitalCoordinates.TryGetValue(nation.Id, out var coordinates) ||
                coordinates != (nation.CapitalLatitude, nation.CapitalLongitude))
                return false;
        return true;
    }

    private static IEnumerable<int> EnsureDistancesStages(Campaign campaign, TradeWorkspace work)
    {
        var count = 0;
        using (SimProfiler.Measure("    mkt:trade"))
        {
            foreach (var nation in campaign.Nations) count = Math.Max(count, nation.Id + 1);
            work.DistanceNationCount = count;
            work.DistanceTable = new float[count * count];
            work.CapitalCoordinates.Clear();
            foreach (var nation in campaign.Nations)
                work.CapitalCoordinates[nation.Id] = (nation.CapitalLatitude, nation.CapitalLongitude);
            for (var index = 0; index < work.DistanceTable.Length; index++) work.DistanceTable[index] = 12000f;
        }
        yield return 0;

        var nations = campaign.Nations;
        var i = 0;
        while (i < nations.Count)
        {
            using (SimProfiler.Measure("    mkt:trade"))
            {
                var end = Math.Min(nations.Count, i + 24);
                for (; i < end; i++)
                {
                    var a = nations[i];
                    if (a.Id < 0 || a.Id >= count) continue;
                    for (var j = 0; j < nations.Count; j++)
                    {
                        var b = nations[j];
                        if (b.Id < a.Id || b.Id >= count) continue;
                        var kilometres = GreatCircle(
                            a.CapitalLatitude, a.CapitalLongitude, b.CapitalLatitude, b.CapitalLongitude);
                        work.DistanceTable[a.Id * count + b.Id] = kilometres;
                        work.DistanceTable[b.Id * count + a.Id] = kilometres;
                    }
                }
            }
            if (i < nations.Count) yield return 0;
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
