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

    // Scratch used atomically within one commodity. There is no yield in RouteOne.
    // A campaign may yield BETWEEN commodities; everything surviving that yield is
    // owned by its weak-keyed workspace below, not by another live world's market.
    private static readonly List<int> SellerIds = new();
    private static readonly List<float> SellerCapacity = new();
    private static readonly List<float> SellerScore = new();
    private static readonly List<int> SellerOrder = new();
    private static int[] _buyerIds;
    private static float[] _buyerKeys;
    private static readonly Dictionary<int, (int States, int Infrastructure, float Ports, int Rails)> Counts = new();

    private sealed class TradeWorkspace
    {
        internal readonly Dictionary<int, float> TransportReach = new();
        internal readonly Dictionary<long, float> PartnerAffinityScores = new();
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

        using (SimProfiler.Measure("    mkt:trade"))
        {
            EnsureDistances(campaign, work);
            MeasureReach(campaign, work);
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

        foreach (var commodity in CommodityCatalog.All)
        {
            using (SimProfiler.Measure("    mkt:trade")) RouteOne(campaign, commodity, work);
            yield return 0;
        }
        campaign.TradeRoutesDay = campaign.ElapsedDays;
        work.FiledCount = campaign.TradeRoutes.Count;
        work.IndexReady = true;
        ImportIndexBuilds++;
    }

    private static void RouteOne(Campaign campaign, Commodity commodity, TradeWorkspace work)
    {
        var nations = campaign.Nations;

        // Who has a surplus, biggest first. Only the top few matter, so the list is
        // trimmed rather than sorted in full.
        SellerIds.Clear();
        SellerCapacity.Clear();
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
            if (capacity <= 0.0001f) continue;
            Insert(SellerIds, SellerCapacity, nation.Id, capacity, SellersPerGood);
        }
        if (SellerIds.Count == 0)
        {
            GreyMarketOnly(campaign, commodity);
            return;
        }

        // Who is short, biggest shortfall first. Order matters - the sellers run out - and
        // serving the largest economies first is both the realistic answer and a stable
        // one, since it does not depend on where a country sits in a list.
        // Ordering two hundred buyers by insertion is forty thousand list moves per good and
        // eight hundred thousand per market week, which was most of what this pass cost. A
        // sort of two reused arrays does the same job; it is deterministic for identical
        // input, which is what the seed has to be able to rely on.
        if (_buyerIds is null || _buyerIds.Length < nations.Count)
        {
            _buyerIds = new int[nations.Count + 32];
            _buyerKeys = new float[nations.Count + 32];
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
            _buyerIds[buyers] = nation.Id;
            // Ordered by need, weighted by how badly the country says it wants this good.
            // The weight decides the QUEUE and not the amount: a priority of three does not
            // buy three times as much, it is served before somebody equally short who said
            // nothing. What is actually taken is worked out again below from the shortfall
            // itself, so an instruction cannot conjure goods that were never produced.
            _buyerKeys[buyers] = -shortfall *
                MathF.Max(0.0001f, TradeDesk.ForGood(nation, commodity, TradeDesk.ImportPriority));
            buyers++;
        }
        if (buyers > 1) Array.Sort(_buyerKeys, _buyerIds, 0, buyers);

        for (var index = 0; index < buyers; index++)
        {
            var buyer = campaign.NationById(_buyerIds[index]);
            if (buyer is null) continue;
            // The true shortfall, not the queue position above.
            if (!buyer.Market.TryGetValue(commodity, out var buyerQuote)) continue;
            var deficit = (buyerQuote.Demand - buyerQuote.Production) *
                          TradePolicyRules.ImportAccess(buyer.TradePolicy) *
                          Ceiling(campaign, buyer, work) *
                          M.Clamp01(TradeDesk.ForGood(buyer, commodity, TradeDesk.ImportCeiling));
            if (deficit <= 0.0001f) continue;

            // Rank this week's exporters from where the buyer is standing.
            SellerScore.Clear();
            SellerOrder.Clear();
            var availableCapacity = 0f;
            var blockedCapacity = 0f;
            for (var seat = 0; seat < SellerIds.Count; seat++)
            {
                var cap = SellerCapacity[seat];
                if (cap <= 0.0001f || SellerIds[seat] == buyer.Id) continue;
                var seller = campaign.NationById(SellerIds[seat]);
                if (seller is null) continue;
                var score = Affinity(campaign, buyer, seller, commodity, work);
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
                Insert(SellerOrder, SellerScore, seat, score, int.MaxValue);
            }

            if (blockedCapacity > 0.0001f)
            {
                var blockedShare = blockedCapacity / MathF.Max(0.0001f, availableCapacity + blockedCapacity);
                deficit *= 1f - 0.35f * M.Clamp01(blockedShare);
            }

            for (var rank = 0; rank < SellerOrder.Count && deficit > 0.0001f; rank++)
            {
                var seat = SellerOrder[rank];
                var take = MathF.Min(deficit, SellerCapacity[seat]);
                if (take <= 0.0001f) continue;
                var seller = campaign.NationById(SellerIds[seat]);
                if (seller is null) continue;
                // A price either side has refused in advance. The seller's own price is what
                // a route costs - see Deliver - so this is the same number the deal would be
                // written at, against the limits the two countries set as multiples of what
                // the good is worth at base.
                if (!TradeDesk.PriceIsAcceptable(buyer, seller, commodity,
                        seller.Market.TryGetValue(commodity, out var offer) ? offer.Price : 0f))
                    continue;

                SellerCapacity[seat] -= take;
                deficit -= take;
                Deliver(campaign, seller, buyer, commodity, take, work);
            }

            // Whatever no friend would sell. It arrives anyway, thinly and dearly, because
            // a country with no partners should be poor rather than dead on the same day.
            if (deficit <= 0.0001f) continue;
            var quote = buyer.Market[commodity];
            quote.Imports += deficit * GreyMarketAccess * campaign.Defines.WorldMarketAccess;
        }
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

    private static void Deliver(Campaign campaign, Nation seller, Nation buyer, Commodity commodity, float volume, TradeWorkspace work)
    {
        var sellerQuote = seller.Market[commodity];
        var buyerQuote = buyer.Market[commodity];
        sellerQuote.Exports += volume;
        buyerQuote.Imports += volume;

        var value = volume * MathF.Max(0.0001f, sellerQuote.Price);
        if (work.UsedRouteRecords >= work.RouteRecords.Count) work.RouteRecords.Add(new TradeRoute());
        var route = work.RouteRecords[work.UsedRouteRecords++];
        route.SellerId = seller.Id;
        route.BuyerId = buyer.Id;
        route.Commodity = commodity;
        route.Volume = volume;
        route.Value = value;
        campaign.TradeRoutes.Add(route);
        Touch(work, seller.Id, route);
        if (buyer.Id != seller.Id) Touch(work, buyer.Id, route);

        seller.TradePartners[buyer.Id] = seller.TradePartners.GetValueOrDefault(buyer.Id) + value;
        buyer.TradePartners[seller.Id] = buyer.TradePartners.GetValueOrDefault(seller.Id) + value;
        seller.ExportValue += value;
        buyer.ImportValue += value;
    }

    /// <summary>
    /// How willing and able these two are to move goods between them, as a multiplier on
    /// nothing in particular - only the ranking matters. Zero means it cannot happen at all.
    /// </summary>
    private static float Affinity(Campaign campaign, Nation buyer, Nation seller, Commodity commodity, TradeWorkspace work)
    {
        // What the two countries have said about each other, which is an embargo one good
        // wide when it names a good and the ordinary kind when it does not.
        if (TradeDesk.Forbidden(buyer, seller, commodity)) return 0f;

        var key = ((long)buyer.Id << 32) | (uint)seller.Id;
        if (!work.PartnerAffinityScores.TryGetValue(key, out var score))
        {
            score = PartnerAffinity(campaign, buyer, seller, work);
            work.PartnerAffinityScores[key] = score;
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
        M.Clamp(campaign.Defines.WorldMarketAccess * work.TransportReach.GetValueOrDefault(nation.Id, 0.5f), 0.30f, 1.05f);

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
                0.45f + averageInfrastructure * 0.06f + counted.Ports * 0.05f + counted.Rails * 0.035f +
                NationalModifiers.Of(nation, "trade_reach"),
                0.45f, 1.50f);
        }
    }

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

    private static void EnsureDistances(Campaign campaign, TradeWorkspace work)
    {
        var count = 0;
        foreach (var nation in campaign.Nations) count = Math.Max(count, nation.Id + 1);
        var unchanged = work.DistanceTable is not null && work.DistanceNationCount == count &&
            work.CapitalCoordinates.Count == campaign.Nations.Count;
        if (unchanged)
            foreach (var nation in campaign.Nations)
                if (!work.CapitalCoordinates.TryGetValue(nation.Id, out var coordinates) ||
                    coordinates != (nation.CapitalLatitude, nation.CapitalLongitude)) { unchanged = false; break; }
        if (unchanged) return;

        work.DistanceNationCount = count;
        work.DistanceTable = new float[count * count];
        work.CapitalCoordinates.Clear();
        foreach (var nation in campaign.Nations)
            work.CapitalCoordinates[nation.Id] = (nation.CapitalLatitude, nation.CapitalLongitude);
        for (var index = 0; index < work.DistanceTable.Length; index++) work.DistanceTable[index] = 12000f;
        foreach (var a in campaign.Nations)
        foreach (var b in campaign.Nations)
        {
            if (a.Id < 0 || b.Id < a.Id) continue;
            var kilometres = GreatCircle(
                a.CapitalLatitude, a.CapitalLongitude, b.CapitalLatitude, b.CapitalLongitude);
            work.DistanceTable[a.Id * count + b.Id] = kilometres;
            work.DistanceTable[b.Id * count + a.Id] = kilometres;
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
