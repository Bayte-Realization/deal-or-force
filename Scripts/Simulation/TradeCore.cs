using System;
using System.Collections.Generic;

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
    /// Rebuilt from scratch every economic tick and deliberately not saved: it is a
    /// consequence of production, prices and diplomacy, all of which are saved, so a loaded
    /// game works it out again on its first market pass rather than carrying a stale copy.
    /// </summary>
    public readonly List<TradeRoute> TradeRoutes = new();
    // Routes are derived and not saved. -1 explicitly means attribution is unavailable.
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

    private static readonly List<int> SellerIds = new();
    private static readonly List<float> SellerCapacity = new();
    private static readonly List<float> SellerScore = new();
    private static readonly List<int> SellerOrder = new();
    private static int[] _buyerIds;
    private static float[] _buyerKeys;

    private static readonly Dictionary<int, float> Reach = new();
    private static readonly Dictionary<int, (int States, int Infrastructure, float Ports, int Rails)> Counts = new();

    /// <summary>
    /// Route objects, reused.
    ///
    /// Two thousand two hundred of them a week is ninety kilobytes of garbage for a list
    /// that is emptied and refilled from scratch every time. They are plain data with no
    /// identity, so the same objects are handed out again.
    /// </summary>
    private static readonly List<TradeRoute> RoutePool = new();
    private static int _routesInUse;

    private static float[] _distance;
    private static int _distanceNations;

    /// <summary>
    /// Which campaign the distance table was built for. Two worlds with the same number of
    /// countries are not the same world, and handing one the other's geography is a silent
    /// divergence rather than an error.
    /// </summary>
    private static Campaign _distanceCampaign;

    /// <summary>
    /// Match the world's shortfalls against the world's surpluses, and write the result
    /// into every country's quotes.
    /// </summary>
    public static void Route(Campaign campaign)
    {
        var nations = campaign.Nations;
        campaign.TradeRoutes.Clear();
        _routesInUse = 0;
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

        EnsureDistances(campaign);
        MeasureReach(campaign);

        foreach (var commodity in CommodityCatalog.All)
            RouteOne(campaign, commodity);
        campaign.TradeRoutesDay = campaign.ElapsedDays;
    }

    private static void RouteOne(Campaign campaign, Commodity commodity)
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
                            Ceiling(campaign, nation) *
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
                          Ceiling(campaign, buyer) *
                          M.Clamp01(TradeDesk.ForGood(buyer, commodity, TradeDesk.ImportCeiling));
            if (deficit <= 0.0001f) continue;

            // Rank this week's exporters from where the buyer is standing.
            SellerScore.Clear();
            SellerOrder.Clear();
            for (var seat = 0; seat < SellerIds.Count; seat++)
            {
                if (SellerCapacity[seat] <= 0.0001f || SellerIds[seat] == buyer.Id) continue;
                var seller = campaign.NationById(SellerIds[seat]);
                if (seller is null) continue;
                var score = Affinity(campaign, buyer, seller, commodity);
                if (score <= 0.0001f) continue;
                Insert(SellerOrder, SellerScore, seat, score, int.MaxValue);
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
                Deliver(campaign, seller, buyer, commodity, take);
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

    private static void Deliver(Campaign campaign, Nation seller, Nation buyer, Commodity commodity, float volume)
    {
        var sellerQuote = seller.Market[commodity];
        var buyerQuote = buyer.Market[commodity];
        sellerQuote.Exports += volume;
        buyerQuote.Imports += volume;

        var value = volume * MathF.Max(0.0001f, sellerQuote.Price);
        if (_routesInUse >= RoutePool.Count) RoutePool.Add(new TradeRoute());
        var route = RoutePool[_routesInUse++];
        route.SellerId = seller.Id;
        route.BuyerId = buyer.Id;
        route.Commodity = commodity;
        route.Volume = volume;
        route.Value = value;
        campaign.TradeRoutes.Add(route);

        seller.TradePartners[buyer.Id] = seller.TradePartners.GetValueOrDefault(buyer.Id) + value;
        buyer.TradePartners[seller.Id] = buyer.TradePartners.GetValueOrDefault(seller.Id) + value;
        seller.ExportValue += value;
        buyer.ImportValue += value;
    }

    /// <summary>
    /// How willing and able these two are to move goods between them, as a multiplier on
    /// nothing in particular - only the ranking matters. Zero means it cannot happen at all.
    /// </summary>
    private static float Affinity(Campaign campaign, Nation buyer, Nation seller, Commodity commodity)
    {
        // What the two countries have said about each other, which is an embargo one good
        // wide when it names a good and the ordinary kind when it does not.
        if (TradeDesk.Forbidden(buyer, seller, commodity)) return 0f;

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

        // Neighbours trade over a border; everyone else trades over a distance, and needs
        // the ports and railways to do it.
        if (!CampaignDiplomacy.SharesLandBorder(campaign, buyer.MapIndex, seller.MapIndex))
        {
            // A squared falloff rather than a fractional power: the same shape to the eye,
            // and not a hundred thousand calls to MathF.Pow every market week.
            var span = Kilometres(campaign, buyer, seller) / HalfReachKilometres;
            var falloff = 1f / (1f + span * span);
            var reach = (Reach.GetValueOrDefault(buyer.Id, 0.5f) + Reach.GetValueOrDefault(seller.Id, 0.5f)) * 0.5f;
            score *= falloff * reach;
        }

        return score * TradePolicyRules.ImportAccess(buyer.TradePolicy) *
               TradePolicyRules.ExportAccess(seller.TradePolicy) *
               TradeDesk.PreferenceBetween(buyer, seller, commodity);
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
    private static float Ceiling(Campaign campaign, Nation nation) =>
        M.Clamp(campaign.Defines.WorldMarketAccess * Reach.GetValueOrDefault(nation.Id, 0.5f), 0.30f, 1.05f);

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
    private static void MeasureReach(Campaign campaign)
    {
        Reach.Clear();
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
            Reach[nation.Id] = M.Clamp(
                0.45f + averageInfrastructure * 0.06f + counted.Ports * 0.05f + counted.Rails * 0.035f +
                NationalModifiers.Of(nation, "trade_reach"),
                0.45f, 1.50f);
        }
    }

    /// <summary>What one country's goods can reach, for anything that wants to show it.</summary>
    public static float ReachOf(Campaign campaign, Nation nation) =>
        Reach.TryGetValue(nation.Id, out var reach) ? reach : 0.45f;

    /// <summary>Great-circle distance between two capitals, cached for the whole campaign.</summary>
    private static float Kilometres(Campaign campaign, Nation a, Nation b)
    {
        var count = _distanceNations;
        if (a.Id < 0 || b.Id < 0 || a.Id >= count || b.Id >= count) return 12000f;
        return _distance[a.Id * count + b.Id];
    }

    private static void EnsureDistances(Campaign campaign)
    {
        var count = 0;
        foreach (var nation in campaign.Nations) count = Math.Max(count, nation.Id + 1);
        if (_distance is not null && _distanceNations == count &&
            ReferenceEquals(_distanceCampaign, campaign)) return;

        _distanceCampaign = campaign;
        _distanceNations = count;
        _distance = new float[count * count];
        for (var index = 0; index < _distance.Length; index++) _distance[index] = 12000f;
        foreach (var a in campaign.Nations)
        foreach (var b in campaign.Nations)
        {
            if (a.Id < 0 || b.Id < 0) continue;
            _distance[a.Id * count + b.Id] = GreatCircle(
                a.CapitalLatitude, a.CapitalLongitude, b.CapitalLatitude, b.CapitalLongitude);
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
