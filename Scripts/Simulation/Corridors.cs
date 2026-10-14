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
public static class Corridors
{
    public static void Tick(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(Corridors))) return;
        if (c.ProvinceMap is null || c.ProvinceController is null) return;
        var every = Math.Max(7, c.Defines.CorridorReviewDays);
        foreach (var nation in c.Nations.ToArray())
        {
            if ((c.ElapsedDays + (long)nation.Id * 11) % every != 0) continue;
            if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Diplomacy)) continue;
            if (nation.Movement is not null || nation.IsSubject || nation.Population <= 0) continue;
            Review(c, nation);
        }
    }

    private static void Review(Campaign c, Nation nation)
    {
        var parts = Pieces(c, nation);
        if (parts.Count < 2) return;
        var main = parts[0];
        var authored = new HashSet<int>(ProvinceOwnership.ProvincesOf(nation.Tag));
        foreach (var piece in parts.Skip(1))
        {
            if (piece.Count < Math.Max(1, c.Defines.CorridorMinimumProvinces)) continue;
            // How the state was drawn in 2024 is not a wound.
            if (piece.Count(authored.Contains) * 10 >= piece.Count * 6) continue;
            if (TryJoin(c, nation, piece, main)) return;
        }
    }

    /// <summary>
    /// The country's land in pieces joined by land, the piece with its capital first (or the
    /// largest, for a country without one on the map).
    /// </summary>
    public static List<HashSet<int>> Pieces(Campaign c, Nation nation)
    {
        var map = c.ProvinceMap;
        var owned = new HashSet<int>();
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (map.ProvinceToNation[p] == nation.MapIndex && !map.WaterByProvince[p]) owned.Add(p);
        var pieces = new List<HashSet<int>>();
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var start in owned)
        {
            if (!seen.Add(start)) continue;
            var piece = new HashSet<int> { start };
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                foreach (var next in map.Neighbors[p])
                    if (owned.Contains(next) && seen.Add(next)) { piece.Add(next); queue.Enqueue(next); }
            }
            pieces.Add(piece);
        }
        var capital = nation.CapitalStateMapIndex;
        pieces.Sort((a, b) =>
        {
            var ca = a.Any(p => map.ProvinceToState[p] == capital);
            var cb = b.Any(p => map.ProvinceToState[p] == capital);
            if (ca != cb) return ca ? -1 : 1;
            return b.Count.CompareTo(a.Count);
        });
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
        var cost = new Dictionary<int, int>();
        var from = new Dictionary<int, int>();
        var deque = new LinkedList<int>();
        foreach (var p in piece) { cost[p] = 0; deque.AddLast(p); }
        var reached = -1;
        while (deque.Count > 0)
        {
            var p = deque.First!.Value;
            deque.RemoveFirst();
            if (main.Contains(p)) { reached = p; break; }
            var here = cost[p];
            foreach (var next in map.Neighbors[p])
            {
                if (next <= 0 || next > map.ProvinceCount || map.WaterByProvince[next]) continue;
                var own = map.ProvinceToNation[next] == nation.MapIndex;
                var step = own ? 0 : 1;
                var total = here + step;
                if (total > limit) continue;
                if (cost.TryGetValue(next, out var known) && known <= total) continue;
                cost[next] = total;
                from[next] = p;
                if (step == 0) deque.AddFirst(next); else deque.AddLast(next);
            }
        }
        if (reached < 0) return null;
        var path = new List<int>();
        for (var p = reached; from.TryGetValue(p, out var previous); p = previous)
            if (map.ProvinceToNation[p] != nation.MapIndex) path.Add(p);
        return path;
    }

    private static bool TryJoin(Campaign c, Nation nation, HashSet<int> piece, HashSet<int> main)
    {
        var map = c.ProvinceMap;
        var path = Corridor(c, nation, piece, main);
        // Regions, not provinces, change hands: the regions the corridor runs through, by holder.
        var byHolder = new Dictionary<int, HashSet<int>>();
        if (path is not null)
            foreach (var province in path)
            {
                var holder = map.ProvinceToNation[province];
                if (holder == 0) continue;
                if (!byHolder.TryGetValue(holder, out var regions)) byHolder[holder] = regions = new HashSet<int>();
                regions.Add(map.ProvinceToState[province]);
            }
        // One holder at a time - the one with the most of the way.
        foreach (var (holderIndex, regions) in byHolder.OrderByDescending(entry => entry.Value.Count))
        {
            var holder = c.Nations.Find(n => n.MapIndex == holderIndex);
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
        // No way through to buy: the piece goes to the neighbour around it, for what it is worth.
        var around = new Dictionary<int, int>();
        foreach (var province in piece)
            foreach (var next in map.Neighbors[province])
            {
                var owner = next > 0 && next <= map.ProvinceCount ? map.ProvinceToNation[next] : (ushort)0;
                if (owner != 0 && owner != nation.MapIndex) around[owner] = around.GetValueOrDefault(owner) + 1;
            }
        foreach (var (buyerIndex, _) in around.OrderByDescending(entry => entry.Value))
        {
            var buyer = c.Nations.Find(n => n.MapIndex == buyerIndex);
            if (buyer is null || buyer.Movement is not null || !Approachable(c, nation, buyer)) continue;
            var regions = piece.Select(p => (int)map.ProvinceToState[p]).Where(s => s != 0).Distinct().ToArray();
            // Not a region the country could not give: its capital, or a homeland of its own.
            if (regions.Any(id => id == nation.CapitalStateMapIndex ||
                    (c.States.Find(s => s.MapIndex == id) is { } state && TerritorialClaims.TitleOf(c, nation, state) == TerritorialTitle.Homeland)))
                return false;
            var sale = new Ultimatum.Deal(new Ultimatum.Side { Regions = regions }, new Ultimatum.Side());
            // What the buyer would pay for it: the most it would still call a fair bargain.
            var worth = regions.Sum(id => c.States.Find(s => s.MapIndex == id) is { } state ? Ultimatum.RegionWorth(c, buyer, state) : 0f);
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
