using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How often a country looks at its detached pieces, in days (staggered by country).</summary>
    public int CorridorReviewDays { get; set; } = 60;
    /// <summary>A detached piece smaller than this many provinces is not worth a negotiation.</summary>
    public int CorridorMinimumProvinces { get; set; } = 3;
    /// <summary>The longest corridor, in foreign provinces, a country will try to buy.</summary>
    public int CorridorMaxProvinces { get; set; } = 14;
    /// <summary>How much of its treasury a country will spend on a corridor.</summary>
    public float CorridorTreasuryShare { get; set; } = 0.45f;
    /// <summary>A refused or failed approach is not repeated to the same country for this many days.</summary>
    public int CorridorRetryDays { get; set; } = 540;
    /// <summary>A detached scrap smaller than a corridor, and not the 2024 map, may still be sold.</summary>
    public int BorderTidyMaxProvinces { get; set; } = 2;
    /// <summary>The largest detached piece, in provinces, that may be traded for the neighbour's own.</summary>
    public int EnclaveSwapMaxProvinces { get; set; } = 4;
}

/// <summary>
/// A country cut in two tries to make itself whole again: buy the ground between, or sell the
/// piece that cannot be joined.
///
/// Wars and secessions leave countries in pieces - Turkey holding a district by Baku, a region
/// taken across a neighbour that never fell. A government does not leave that alone: the piece
/// cannot be supplied, defended or governed as the rest is, and every map it prints says so. So
/// every two months each country looks at its land for parts not joined to the rest by land,
/// and for each finds the shortest way through its neighbours' ground back to the rest. A
/// corridor short enough to buy is asked for at the price its holder would take
/// (Ultimatum.CashToBalance) - a player receives it in the diplomatic inbox, a government
/// answers at once. A piece too far to join is offered to the neighbour around it instead.
///
/// Only pieces the country did not hold in January 2024: Kaliningrad, Nakhchivan and Alaska
/// are how those states were drawn, not wounds to be closed.
/// </summary>
public static partial class Corridors
{
    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    private static readonly List<Nation> TickNationsScratch = new();

    /// <summary>The same corridor pass, yielded between batches of countries.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(Corridors))) yield break;
        if (c.ProvinceMap is null || c.ProvinceController is null) yield break;
        var every = Math.Max(7, c.Defines.CorridorReviewDays);
        TickNationsScratch.Clear();
        for (var i = 0; i < c.Nations.Count; i++)
            TickNationsScratch.Add(c.Nations[i]);
        try
        {
            for (var i = 0; i < TickNationsScratch.Count; i++)
            {
                if (i > 0 && (i % 8 == 0 || Campaign.StageOverdue())) yield return 0;
                var nation = TickNationsScratch[i];
                if ((c.ElapsedDays + (long)nation.Id * 11) % every != 0) continue;
                if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Diplomacy)) continue;
                if (nation.Movement is not null || nation.IsSubject || nation.Population <= 0) continue;
                foreach (var _ in EnsurePoresStages(c)) yield return 0;
                yield return 0;
                foreach (var _ in ReviewStages(c, nation)) yield return 0;
            }
        }
        finally
        {
            TickNationsScratch.Clear();
        }
    }

    private static IEnumerable<int> ReviewStages(Campaign c, Nation nation)
    {
        if (TryExchange(c, nation)) yield break;
        yield return 0;
        var parts = Pieces(c, nation);
        var authored = ProvinceOwnership.ProvinceSetOf(nation.Tag);
        if (parts.Count >= 2)
        {
            var main = parts[0];
            for (var i = 1; i < parts.Count; i++)
            {
                var piece = parts[i];
                if (Authored(piece, authored)) continue;
                var tidy = Math.Max(1, c.Defines.BorderTidyMaxProvinces);
                if (piece.Count < Math.Max(1, c.Defines.CorridorMinimumProvinces))
                {
                    if (piece.Count <= tidy)
                    {
                        var sold = TrySell(c, nation, piece);
                        if (sold) yield break;
                        yield return 0;
                    }
                    continue;
                }
                var joined = TryJoin(c, nation, piece, main);
                if (joined) yield break;
                yield return 0;
            }
        }
        TryBuyPores(c, nation);
    }

    private static void Review(Campaign c, Nation nation)
    {
        foreach (var _ in ReviewStages(c, nation)) { }
    }

    private static bool Authored(HashSet<int> piece, IReadOnlySet<int> authored)
    {
        if (piece.Count == 0) return false;
        var count = 0;
        foreach (var p in piece)
            if (authored.Contains(p))
                count++;
        return count * 10 >= piece.Count * 6;
    }

    /// <summary>One land province of this region, or 0 when the region is not on the map. The index is the same walk as the pores.</summary>
    internal static int FirstProvince(Campaign c, int stateMapIndex)
    {
        if (c?.ProvinceMap is null || stateMapIndex == 0) return 0;
        EnsurePores(c);
        return _firstProvince.GetValueOrDefault(stateMapIndex);
    }

    [ThreadStatic] private static List<(HashSet<int> Piece, bool HasCapital)> _piecesScratch;
    [ThreadStatic] private static int[] _corrStampArray;
    [ThreadStatic] private static int[] _corrCost;
    [ThreadStatic] private static int[] _corrFrom;
    [ThreadStatic] private static int[] _corrDeque;
    [ThreadStatic] private static int _corrStamp;
    [ThreadStatic] private static Dictionary<int, HashSet<int>> _joinByHolder;
    [ThreadStatic] private static List<(int Holder, HashSet<int> Regions, int Order)> _joinOrder;
    [ThreadStatic] private static Dictionary<int, int> _sellAround;
    [ThreadStatic] private static List<(int Buyer, int Count, int Order)> _sellOrder;
    [ThreadStatic] private static Dictionary<int, int> _wholeGroundCounts;
    [ThreadStatic] private static List<int> _wholeResult;

    /// <summary>
    /// The country's land in pieces joined by land, the piece with its capital first (or the
    /// largest, for a country without one on the map).
    /// </summary>
    public static List<HashSet<int>> Pieces(Campaign c, Nation nation)
    {
        var map = c.ProvinceMap;
        if (map is null || nation is null) return new List<HashSet<int>>();
        EnsurePores(c);
        var pieces = new List<HashSet<int>>();
        if (!_components.TryGetValue(nation.MapIndex, out var cached) || cached.Count == 0) return pieces;
        var capital = nation.CapitalStateMapIndex;
        var scratch = _piecesScratch ??= new List<(HashSet<int> Piece, bool HasCapital)>(cached.Count);
        scratch.Clear();
        for (var i = 0; i < cached.Count; i++)
        {
            var piece = cached[i];
            var hasCapital = false;
            foreach (var p in piece)
                if (map.ProvinceToState[p] == capital) { hasCapital = true; break; }
            scratch.Add((piece, hasCapital));
        }
        scratch.Sort(static (a, b) =>
        {
            if (a.HasCapital != b.HasCapital) return a.HasCapital ? -1 : 1;
            return b.Piece.Count.CompareTo(a.Piece.Count);
        });
        for (var i = 0; i < scratch.Count; i++)
            pieces.Add(scratch[i].Piece);
        scratch.Clear();
        return pieces;
    }

    /// <summary>
    /// The foreign provinces on the shortest way over land from a piece to the rest of the
    /// country: each foreign province costs one, the country's own cost nothing (a 0-1 search),
    /// and the search stops at the longest corridor anybody would buy. Null when there is none.
    /// </summary>
    public static List<int> Corridor(Campaign c, Nation nation, HashSet<int> piece, HashSet<int> main)
    {
        var map = c.ProvinceMap;
        var limit = Math.Max(1, c.Defines.CorridorMaxProvinces);
        var required = map.ProvinceCount + 1;
        if (_corrStampArray is null || _corrStampArray.Length < required)
        {
            _corrStampArray = new int[required];
            _corrCost = new int[required];
            _corrFrom = new int[required];
        }
        var stamps = _corrStampArray;
        var cost = _corrCost;
        var from = _corrFrom;
        if (++_corrStamp <= 0)
        {
            Array.Clear(stamps, 0, stamps.Length);
            _corrStamp = 1;
        }
        var stamp = _corrStamp;

        var deque = _corrDeque ??= new int[4096];
        var cap = deque.Length;
        var mask = cap - 1;
        var head = 0;
        var tail = 0;
        var count = 0;

        foreach (var p in piece)
        {
            if ((uint)p >= (uint)required) continue;
            stamps[p] = stamp;
            cost[p] = 0;
            from[p] = 0;
            if (count == cap)
            {
                GrowDeque(ref deque, ref cap, ref mask, ref head, ref tail, count);
                _corrDeque = deque;
            }
            deque[tail] = p;
            tail = (tail + 1) & mask;
            count++;
        }

        var reached = -1;
        while (count > 0)
        {
            var p = deque[head];
            head = (head + 1) & mask;
            count--;
            if (main.Contains(p)) { reached = p; break; }
            var here = cost[p];
            // Drawn borders: a corridor is land bought to join a country's pieces, and none can be
            // laid over a strait (ProvinceTopology.LandNeighbors).
            var neighbors = map.LandNeighbors[p];
            for (var i = 0; i < neighbors.Length; i++)
            {
                var next = neighbors[i];
                if (next <= 0 || next > map.ProvinceCount || map.WaterByProvince[next]) continue;
                var own = map.ProvinceToNation[next] == nation.MapIndex;
                var step = own ? 0 : 1;
                var total = here + step;
                if (total > limit) continue;
                if (stamps[next] == stamp && cost[next] <= total) continue;
                stamps[next] = stamp;
                cost[next] = total;
                from[next] = p;
                if (count == cap)
                {
                    GrowDeque(ref deque, ref cap, ref mask, ref head, ref tail, count);
                    _corrDeque = deque;
                }
                if (step == 0)
                {
                    head = (head - 1) & mask;
                    deque[head] = next;
                }
                else
                {
                    deque[tail] = next;
                    tail = (tail + 1) & mask;
                }
                count++;
            }
        }
        if (reached < 0) return null;
        var path = new List<int>();
        for (var p = reached; p > 0 && from[p] > 0; p = from[p])
            if (map.ProvinceToNation[p] != nation.MapIndex) path.Add(p);
        return path;
    }

    private static void GrowDeque(ref int[] deque, ref int cap, ref int mask, ref int head, ref int tail, int count)
    {
        var nextCap = cap * 2;
        var grown = new int[nextCap];
        var first = cap - head;
        if (first >= count)
            Array.Copy(deque, head, grown, 0, count);
        else
        {
            Array.Copy(deque, head, grown, 0, first);
            Array.Copy(deque, 0, grown, first, count - first);
        }
        deque = grown;
        cap = nextCap;
        mask = nextCap - 1;
        head = 0;
        tail = count;
    }

    private static bool TryJoin(Campaign c, Nation nation, HashSet<int> piece, HashSet<int> main)
    {
        var map = c.ProvinceMap;
        var path = Corridor(c, nation, piece, main);
        // Regions, not provinces, change hands: the regions the corridor runs through, by holder.
        var byHolder = _joinByHolder ??= new Dictionary<int, HashSet<int>>();
        byHolder.Clear();
        if (path is not null)
        {
            for (var i = 0; i < path.Count; i++)
            {
                var province = path[i];
                var holder = map.ProvinceToNation[province];
                if (holder == 0) continue;
                if (!byHolder.TryGetValue(holder, out var regions)) byHolder[holder] = regions = new HashSet<int>();
                regions.Add(map.ProvinceToState[province]);
            }
        }
        var order = _joinOrder ??= new List<(int Holder, HashSet<int> Regions, int Order)>();
        order.Clear();
        var seq = 0;
        foreach (var entry in byHolder)
            order.Add((entry.Key, entry.Value, seq++));
        order.Sort(static (a, b) =>
        {
            var cmp = b.Regions.Count.CompareTo(a.Regions.Count);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });
        // One holder at a time - the one with the most of the way.
        for (var i = 0; i < order.Count; i++)
        {
            var (holderIndex, regions, _) = order[i];
            var holder = c.NationByMapIndex(holderIndex);
            if (holder is null || holder.Movement is not null || !Approachable(c, nation, holder)) continue;
            var deal = new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side { Regions = regions.ToArray() });
            var price = Ultimatum.CashToBalance(c, nation, holder, deal);
            if (!float.IsFinite(price) || price < 0f) continue;
            if (price > nation.Treasury * M.Clamp01(c.Defines.CorridorTreasuryShare)) continue;
            deal = deal with { From = deal.From with { Cash = MathF.Max(0.01f, price * 1.05f) } };
            Remember(c, nation, holder);
            DealNegotiations.Submit(c, nation, holder, deal);
            return true;
        }
        return TrySell(c, nation, piece);
    }

    /// <summary>
    /// Sell this piece to the neighbour who touches it most. A region is offered only when
    /// the piece holds every province of it still owned here: a deal cannot hand over half
    /// a region without handing over the rest.
    /// </summary>
    private static bool TrySell(Campaign c, Nation nation, HashSet<int> piece)
    {
        var map = c.ProvinceMap;
        var around = _sellAround ??= new Dictionary<int, int>();
        around.Clear();
        foreach (var province in piece)
        {
            var neighbors = map.LandNeighbors[province];
            for (var i = 0; i < neighbors.Length; i++)
            {
                var next = neighbors[i];
                var owner = next > 0 && next <= map.ProvinceCount ? map.ProvinceToNation[next] : (ushort)0;
                if (owner != 0 && owner != nation.MapIndex) around[owner] = around.GetValueOrDefault(owner) + 1;
            }
        }
        var regions = WholeRegions(c, nation, piece);
        if (regions.Length == 0) return false;
        for (var i = 0; i < regions.Length; i++)
        {
            var id = regions[i];
            if (id == nation.CapitalStateMapIndex ||
                (c.StateByMapIndex(id) is { } state && TerritorialClaims.TitleOf(c, nation, state) == TerritorialTitle.Homeland))
                return false;
        }
        var order = _sellOrder ??= new List<(int Buyer, int Count, int Order)>();
        order.Clear();
        var seq = 0;
        foreach (var entry in around)
            order.Add((entry.Key, entry.Value, seq++));
        order.Sort(static (a, b) =>
        {
            var cmp = b.Count.CompareTo(a.Count);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });
        for (var oi = 0; oi < order.Count; oi++)
        {
            var buyerIndex = order[oi].Buyer;
            var buyer = c.NationByMapIndex(buyerIndex);
            if (buyer is null || buyer.Movement is not null || !Approachable(c, nation, buyer)) continue;
            var sale = new Ultimatum.Deal(new Ultimatum.Side { Regions = regions }, new Ultimatum.Side());
            var worth = 0f;
            for (var i = 0; i < regions.Length; i++)
                if (c.StateByMapIndex(regions[i]) is { } st)
                    worth += Ultimatum.RegionWorth(c, buyer, st);
            var asking = MathF.Max(0.01f, worth * 0.5f / MathF.Max(0.01f, Ultimatum.CashWeight(buyer)));
            if (asking > buyer.Treasury * 0.5f) asking = buyer.Treasury * 0.5f;
            if (asking <= 0.01f) continue;
            sale = sale with { To = sale.To with { Cash = asking } };
            Remember(c, nation, buyer);
            DealNegotiations.Submit(c, nation, buyer, sale);
            return true;
        }
        return false;
    }

    /// <summary>Regions whose every province still owned by this country sits inside <paramref name="ground"/>.</summary>
    private static int[] WholeRegions(Campaign c, Nation nation, HashSet<int> ground)
    {
        var map = c.ProvinceMap;
        EnsurePores(c);
        var owner = (ushort)nation.MapIndex;
        var inGround = _wholeGroundCounts ??= new Dictionary<int, int>();
        inGround.Clear();
        foreach (var p in ground)
        {
            if (p <= 0 || p > map.ProvinceCount) continue;
            var region = (int)map.ProvinceToState[p];
            if (region == 0) continue;
            if (!inGround.TryGetValue(region, out var count))
                count = 0;
            if (map.ProvinceToNation[p] == owner && !map.IsWater(p))
                count++;
            inGround[region] = count;
        }
        if (inGround.Count == 0) return Array.Empty<int>();
        var list = _wholeResult ??= new List<int>();
        list.Clear();
        var ownerShift = (uint)owner << 16;
        foreach (var entry in inGround)
        {
            var totalOwned = _ownerStateCounts.GetValueOrDefault(ownerShift | (uint)entry.Key);
            if (entry.Value == totalOwned)
                list.Add(entry.Key);
        }
        if (list.Count == 0) return Array.Empty<int>();
        list.Sort();
        return list.ToArray();
    }

    /// <summary>
    /// Foreign ground shut inside this border, small enough to be a pore rather than a country.
    /// One offer to its owner, and only for regions that would change hands whole.
    /// </summary>
    internal static bool TryBuyPores(Campaign c, Nation nation)
    {
        if (c?.ProvinceMap is null || nation is null) return false;
        var pore = Enclosed(c, nation);
        if (pore.Count == 0) return false;
        var map = c.ProvinceMap;
        var ownerIndex = map.ProvinceToNation[FirstProvinceIn(pore)];
        var owner = c.NationByMapIndex(ownerIndex);
        if (owner is null || owner.Movement is not null || !Approachable(c, nation, owner)) return false;
        var authored = ProvinceOwnership.ProvinceSetOf(owner.Tag);
        if (Authored(pore, authored)) return false;
        var regions = WholeRegions(c, owner, pore);
        if (regions.Length == 0) return false;
        for (var i = 0; i < regions.Length; i++)
            if (regions[i] == owner.CapitalStateMapIndex) return false;
        var deal = new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side { Regions = regions });
        var price = Ultimatum.CashToBalance(c, nation, owner, deal);
        if (!float.IsFinite(price) || price < 0f) return false;
        if (price > nation.Treasury * M.Clamp01(c.Defines.CorridorTreasuryShare)) return false;
        deal = deal with { From = deal.From with { Cash = MathF.Max(0.01f, price * 1.05f) } };
        Remember(c, nation, owner);
        if (DealNegotiations.Submit(c, nation, owner, deal).Accepted) return true;
        for (var i = 0; i < c.DealOffers.Count; i++)
        {
            var offer = c.DealOffers[i];
            if (offer.Pending && offer.FromId == nation.Id && offer.ToId == owner.Id)
                return true;
        }
        return false;
    }

    private static int FirstProvinceIn(HashSet<int> set)
    {
        foreach (var p in set) return p;
        return 0;
    }

    /// <summary>A pore in this border the player can be told about. Sends nothing.</summary>
    public static bool TryReportPore(Campaign c, Nation nation, out string holder, out int provinces)
    {
        holder = "";
        provinces = 0;
        if (c?.ProvinceMap is null || nation is null) return false;
        var pore = Enclosed(c, nation);
        if (pore.Count == 0) return false;
        var owner = c.NationByMapIndex(c.ProvinceMap.ProvinceToNation[FirstProvinceIn(pore)]);
        if (owner is null) return false;
        var authored = ProvinceOwnership.ProvinceSetOf(owner.Tag);
        if (Authored(pore, authored)) return false;
        if (WholeRegions(c, owner, pore).Length == 0) return false;
        holder = owner.DisplayName;
        provinces = pore.Count;
        return true;
    }

    /// <summary>Ask to buy the pore. A player click. An AI neighbour may sign at once.</summary>
    public static string ProposePore(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return Loc.Get("Choose a country.");
        if (!TryReportPore(c, nation, out _, out _)) return Loc.Get("Nothing foreign is shut inside this border.");
        var before = c.DealOffers.Count;
        if (!TryBuyPores(c, nation) && c.DealOffers.Count == before)
            return Loc.Get("They cannot be asked just now.");
        return "";
    }

    static Campaign _poreCampaign;
    static int _poreRevision = -1;
    static ProvinceTopology _poreMap;
    static int _poreLimit = -1;
    static ushort[] _poreOwners = Array.Empty<ushort>(), _poreStates = Array.Empty<ushort>();
    static bool[] _poreSeen = Array.Empty<bool>();
    static readonly Queue<int> _poreQueue = new();
    static readonly HashSet<int> _poreRim = new();
    static readonly Dictionary<int, HashSet<int>> _pores = new();
    static readonly Dictionary<int, List<HashSet<int>>> _components = new();
    static readonly Dictionary<int, int> _firstProvince = new();
    static readonly Dictionary<uint, int> _ownerStateCounts = new();
    static readonly HashSet<int> NoPore = new();
    static bool _poreReady;

    static void EnsurePores(Campaign c)
    {
        foreach (var _ in EnsurePoresStages(c)) { }
    }

    /// <summary>
    /// One walk of the map, shared by every country until a border moves.
    /// A dot shut against a single neighbour is that neighbour's pore. A sea island,
    /// and anything that touches two countries or nobody, is not a hole in a border.
    /// </summary>
    static IEnumerable<int> EnsurePoresStages(Campaign c)
    {
        var map = c?.ProvinceMap;
        if (map is null) yield break;
        if (_poreReady && ReferenceEquals(_poreCampaign, c) && _poreRevision == c.MapRevision) yield break;
        var limit = Math.Max(1, c.Defines.PeaceCompactHoleProvinces);
        // A new revision is usually a capture - who controls a province, which this index never
        // reads. It is rebuilt only when what it does read moved: who owns the land, which
        // region it lies in, the map itself, the hole size. Otherwise the rebuild would make the
        // same sets in the same order, and the world's clusters in fresh sets every hour of a war
        // were the most garbage any daily system made.
        if (_poreReady && ReferenceEquals(_poreCampaign, c) && ReferenceEquals(_poreMap, map) && _poreLimit == limit &&
            map.ProvinceToNation.AsSpan().SequenceEqual(_poreOwners) &&
            map.ProvinceToState.AsSpan().SequenceEqual(_poreStates))
        {
            _poreRevision = c.MapRevision;
            yield break;
        }
        _poreReady = false;
        _poreRevision = -1;
        _poreCampaign = c;
        var targetRevision = c.MapRevision;
        _poreMap = map;
        _poreLimit = limit;
        if (_poreOwners.Length != map.ProvinceToNation.Length)
            _poreOwners = new ushort[map.ProvinceToNation.Length];
        Array.Copy(map.ProvinceToNation, _poreOwners, map.ProvinceToNation.Length);
        if (_poreStates.Length != map.ProvinceToState.Length)
            _poreStates = new ushort[map.ProvinceToState.Length];
        Array.Copy(map.ProvinceToState, _poreStates, map.ProvinceToState.Length);
        _pores.Clear();
        _components.Clear();
        _firstProvince.Clear();
        _ownerStateCounts.Clear();
        var requiredSeen = map.ProvinceCount + 1;
        if (_poreSeen.Length < requiredSeen)
            _poreSeen = new bool[requiredSeen];
        else
            Array.Clear(_poreSeen, 0, requiredSeen);
        var seen = _poreSeen;
        var queue = _poreQueue;
        var rim = _poreRim;
        queue.Clear();
        var workSinceYield = 0;
        for (var start = 1; start <= map.ProvinceCount; start++)
        {
            if (seen[start] || map.IsWater(start)) continue;
            var owner = _poreOwners[start];
            if (owner == 0) { seen[start] = true; continue; }
            var cluster = new HashSet<int>();
            rim.Clear();
            var open = false;
            queue.Clear();
            queue.Enqueue(start);
            seen[start] = true;
            var ownerShift = (uint)owner << 16;
            while (queue.Count > 0)
            {
                var province = queue.Dequeue();
                cluster.Add(province);
                var state = _poreStates[province];
                if (state != 0)
                {
                    if (!_firstProvince.ContainsKey(state)) _firstProvince[state] = province;
                    var key = ownerShift | (uint)state;
                    _ownerStateCounts[key] = _ownerStateCounts.GetValueOrDefault(key) + 1;
                }
                var neighbors = map.LandNeighbors[province];
                for (var ni = 0; ni < neighbors.Length; ni++)
                {
                    var next = neighbors[ni];
                    if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                    var who = _poreOwners[next];
                    if (who == owner)
                    {
                        if (!seen[next]) { seen[next] = true; queue.Enqueue(next); }
                        continue;
                    }
                    if (who == 0) { open = true; continue; }
                    rim.Add(who);
                }
                if (++workSinceYield >= 2500)
                {
                    workSinceYield = 0;
                    yield return 0;
                }
            }
            if (!_components.TryGetValue(owner, out var list))
                _components[owner] = list = new List<HashSet<int>>();
            list.Add(cluster);
            if (open || cluster.Count == 0 || cluster.Count > limit || rim.Count != 1) continue;
            var surrounder = 0;
            foreach (var who in rim) surrounder = who;
            if (!_pores.TryGetValue(surrounder, out var best) || cluster.Count < best.Count)
                _pores[surrounder] = cluster;
        }
        _poreRevision = targetRevision;
        _poreReady = true;
    }

    /// <summary>The smallest cluster of foreign land whose every land neighbour is this country.</summary>
    private static HashSet<int> Enclosed(Campaign c, Nation nation)
    {
        if (c?.ProvinceMap is null || nation is null) return NoPore;
        EnsurePores(c);
        return _pores.TryGetValue(nation.MapIndex, out var pore) ? pore : NoPore;
    }

    /// <summary>
    /// The first detached piece worth mentioning, and whether a short way through one
    /// neighbour can be asked for. Reading this sends no offer and spends nothing.
    /// </summary>
    public readonly record struct CutOffReport(int Provinces, string Holder, int WayProvinces, float Price, bool CanAsk, string Reason);

    public static bool TryReport(Campaign c, Nation nation, out CutOffReport report)
    {
        report = default;
        if (!TryOpening(c, nation, out var piece, out var main)) return false;
        report = Describe(c, nation, piece, main);
        return true;
    }

    /// <summary>Ask the neighbour who holds the shortest way to sell it. Never sells the piece itself.</summary>
    public static string ProposeBuy(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return Loc.Get("Choose a country.");
        if (!MechanicFeatures.SystemEnabled(c, nameof(Corridors)))
            return Loc.Get("Joining detached land is switched off.");
        if (!TryOpening(c, nation, out var piece, out var main))
            return Loc.Get("The country is in one piece.");
        var report = Describe(c, nation, piece, main);
        if (!report.CanAsk) return Loc.Get(report.Reason);
        var map = c.ProvinceMap;
        var path = Corridor(c, nation, piece, main);
        var regions = new HashSet<int>();
        if (path is not null)
            foreach (var province in path)
            {
                var holder = map.ProvinceToNation[province];
                if (holder == 0 || holder == nation.MapIndex) continue;
                var holderNation = c.NationByMapIndex(holder);
                if (holderNation is null || holderNation.Name != report.Holder && holderNation.DisplayName != report.Holder) continue;
                regions.Add(map.ProvinceToState[province]);
            }
        // The holder named in the report is the one with the most of the way. Ask only them.
        Nation asked = null;
        var askedRegions = new HashSet<int>();
        if (path is not null)
        {
            var byHolder = new Dictionary<int, HashSet<int>>();
            foreach (var province in path)
            {
                var holder = map.ProvinceToNation[province];
                if (holder == 0 || holder == nation.MapIndex) continue;
                if (!byHolder.TryGetValue(holder, out var set)) byHolder[holder] = set = new HashSet<int>();
                set.Add(map.ProvinceToState[province]);
            }
            var best = byHolder.OrderByDescending(entry => entry.Value.Count).FirstOrDefault();
            asked = c.NationByMapIndex(best.Key);
            askedRegions = best.Value ?? askedRegions;
        }
        if (asked is null || askedRegions.Count == 0) return Loc.Get(report.Reason);
        var deal = new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side { Regions = askedRegions.ToArray() });
        var price = Ultimatum.CashToBalance(c, nation, asked, deal);
        if (!float.IsFinite(price) || price < 0f) return Loc.Get("There is no price they would take.");
        deal = deal with { From = deal.From with { Cash = MathF.Max(0.01f, price * 1.05f) } };
        var outcome = DealNegotiations.Submit(c, nation, asked, deal);
        return outcome.Accepted ? "" : outcome.Note;
    }

    private static bool TryOpening(Campaign c, Nation nation, out HashSet<int> piece, out HashSet<int> main)
    {
        piece = null;
        main = null;
        if (c?.ProvinceMap is null || nation is null || nation.Population <= 0) return false;
        var parts = Pieces(c, nation);
        if (parts.Count < 2) return false;
        main = parts[0];
        var authored = ProvinceOwnership.ProvinceSetOf(nation.Tag);
        foreach (var candidate in parts.Skip(1))
        {
            if (candidate.Count < Math.Max(1, c.Defines.CorridorMinimumProvinces)) continue;
            if (candidate.Count(authored.Contains) * 10 >= candidate.Count * 6) continue;
            piece = candidate;
            return true;
        }
        return false;
    }

    private static CutOffReport Describe(Campaign c, Nation nation, HashSet<int> piece, HashSet<int> main)
    {
        var path = Corridor(c, nation, piece, main);
        if (path is null || path.Count == 0)
            return new CutOffReport(piece.Count, "", 0, 0f, false, "No short way through a neighbour can be bought.");
        var map = c.ProvinceMap;
        var byHolder = new Dictionary<int, int>();
        foreach (var province in path)
        {
            var owner = map.ProvinceToNation[province];
            if (owner != 0 && owner != nation.MapIndex) byHolder[owner] = byHolder.GetValueOrDefault(owner) + 1;
        }
        if (byHolder.Count == 0)
            return new CutOffReport(piece.Count, "", path.Count, 0f, false, "No short way through a neighbour can be bought.");
        var holderIndex = byHolder.OrderByDescending(entry => entry.Value).First().Key;
        var holder = c.NationByMapIndex(holderIndex);
        var name = holder?.DisplayName ?? "";
        if (holder is null || holder.Movement is not null)
            return new CutOffReport(piece.Count, name, path.Count, 0f, false, "The way through is not a country that can sell it.");
        if (!Approachable(c, nation, holder))
            return new CutOffReport(piece.Count, name, path.Count, 0f, false, "They cannot be asked just now.");
        var regions = path.Where(p => map.ProvinceToNation[p] == holder.MapIndex).Select(p => (int)map.ProvinceToState[p]).Distinct().ToArray();
        if (regions.Any(id => c.StateByMapIndex(id) is not { } st || st.NationMapIndex != holder.MapIndex))
            return new CutOffReport(piece.Count, name, path.Count, 0f, false, "They cannot yield what they do not hold.");
        var kept = false;
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToNation[province] == holder.MapIndex && !regions.Contains(map.ProvinceToState[province]))
                kept = true;
        if (!kept)
            return new CutOffReport(piece.Count, name, path.Count, 0f, false, "A demand must leave them territory and cannot include occupied land.");
        var deal = new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side { Regions = regions });
        var price = Ultimatum.CashToBalance(c, nation, holder, deal);
        if (!float.IsFinite(price) || price < 0f)
            return new CutOffReport(piece.Count, name, path.Count, 0f, false, "There is no price they would take.");
        var paying = MathF.Max(0.01f, price * 1.05f);
        if (paying > nation.Treasury * M.Clamp01(c.Defines.CorridorTreasuryShare))
            return new CutOffReport(piece.Count, name, path.Count, paying, false, "The way through costs more than this country will spend.");
        return new CutOffReport(piece.Count, name, path.Count, paying, true, "");
    }

    private static string Key(Nation other) => "corridor/" + other.Id;

    private static bool Approachable(Campaign c, Nation nation, Nation other)
    {
        if (nation.DecisionAvailableDay.GetValueOrDefault(Key(other)) > c.ElapsedDays) return false;
        if (CampaignDiplomacy.Peek(c, nation.Id, other.Id) is { Stance: DiplomaticStance.AtWar }) return false;
        return Ultimatum.CanNegotiateDeal(c, nation, other, out _);
    }

    private static void Remember(Campaign c, Nation nation, Nation other) =>
        nation.DecisionAvailableDay[Key(other)] = c.ElapsedDays + Math.Max(30, c.Defines.CorridorRetryDays);
}

/// <summary>The player asks for the way through. It does not sell the detached piece.</summary>
public sealed class AskForCorridorCommand : ICampaignCommand
{
    public int IssuerId { get; init; }

    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        var nation = c?.NationById(IssuerId);
        if (nation is null) return;
        Corridors.ProposeBuy(c, nation);
    }
}
