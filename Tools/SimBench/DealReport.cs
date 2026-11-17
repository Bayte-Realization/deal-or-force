using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// `SimBench &lt;days&gt; &lt;seed&gt; --deals`: what the world does with deals and trade agreements, and what
/// a trade agreement actually does to trade.
///
/// Three things, because "trade agreements are one-sided" is three questions. First, the flow:
/// every agreement and deal paper the world put, by kind and by who asked whom, and how it was
/// answered (DealTally, counted where the offer is made - the chronicle is a 2048-line ring and
/// would report the last two thousand lines as if they were five years). Second, the offers a
/// player can make from the agreement buttons and the deal table, asked of real pairs every half
/// year as read-only previews, so "would the player get this?" is a share and not an anecdote.
/// Third, at the end, one market week routed twice - with every agreement in force and with none -
/// so what an agreement changes in bilateral trade, imports and shortages is measured on the same
/// prices instead of guessed from the code.
/// </summary>
internal static class DealReport
{
    private sealed class Signing
    {
        public int Day, A, B;
        public float Opinion, DependenceA, DependenceB;
        public bool Border, Allied, BigA, BigB;
    }

    public static int Run(Campaign campaign, int days)
    {
        campaign.IsPaused = false;
        DealTally.Reset();
        var hedgeBefore = AiStatecraft.Tally.GetValueOrDefault("supply_hedge");
        var live = new Dictionary<long, int>();
        foreach (var (key, relation) in campaign.Relations)
            if (relation.TradeAgreement) live[key] = 0;
        Console.WriteLine($"DEALS day {campaign.ElapsedDays}: trade agreements in force {live.Count}, " +
                          $"pacts {campaign.Relations.Values.Count(r => r.NonAggressionPact)}");
        var signings = new List<Signing>();
        var ended = new Dictionary<string, int>();
        var lifetimes = new List<int>();
        var previews = new Dictionary<string, (int Asked, int Yes, double Points)>();

        for (var day = 1; day <= days; day++)
        {
            campaign.TickDay();
            Watch(campaign, live, signings, ended, lifetimes);
            if (day % 180 == 0) Preview(campaign, previews);
            if (day % 365 == 0 || day == days) Year(campaign, live.Count, signings.Count, ended);
        }

        Console.WriteLine();
        Console.WriteLine("FLOW (DealTally: put / signed / declined, by kind and seat)");
        foreach (var (key, count) in DealTally.Snapshot()) Console.WriteLine($"  {key} = {count}");
        Console.WriteLine($"  ai.supply_hedge (AiStatecraft) = {AiStatecraft.Tally.GetValueOrDefault("supply_hedge") - hedgeBefore}");

        Console.WriteLine();
        Console.WriteLine($"WHO SIGNS: {signings.Count} new trade agreements over {days} days");
        if (signings.Count > 0)
        {
            double Share(Func<Signing, bool> f) => signings.Count(f) / (double)signings.Count;
            Console.WriteLine($"  land border {Share(s => s.Border):P0}; allied {Share(s => s.Allied):P0}; " +
                              $"both top-30 economies {Share(s => s.BigA && s.BigB):P0}; one {Share(s => s.BigA ^ s.BigB):P0}; neither {Share(s => !s.BigA && !s.BigB):P0}");
            Console.WriteLine($"  opinion at signing: median {Median(signings.Select(s => (double)s.Opinion)):0} " +
                              $"(min {signings.Min(s => s.Opinion):0}, max {signings.Max(s => s.Opinion):0})");
            Console.WriteLine($"  trade already between them: median dependence {Median(signings.Select(s => (double)Math.Max(s.DependenceA, s.DependenceB))):P1}; " +
                              $"no trade at all on either side {Share(s => s.DependenceA < 0.001f && s.DependenceB < 0.001f):P0}");
        }
        Console.WriteLine($"  ended: {string.Join(", ", ended.OrderBy(e => e.Key).Select(e => $"{e.Key} {e.Value}"))}; " +
                          $"median life of an ended one {(lifetimes.Count > 0 ? Median(lifetimes.Select(l => (double)l)) : 0):0} days");

        Console.WriteLine();
        Console.WriteLine("WHAT A PLAYER WOULD GET (read-only previews of real pairs every 180 days)");
        foreach (var (key, row) in previews.OrderBy(p => p.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {key}: asked {row.Asked}, would sign {row.Yes} ({row.Yes / Math.Max(1.0, row.Asked):P0})" +
                              (row.Points != 0 ? $", mean points for them {row.Points / Math.Max(1, row.Asked):+0.0;-0.0}" : ""));

        Console.WriteLine();
        Effect(campaign, live.Keys.ToList());
        return 0;
    }

    private static void Watch(Campaign c, Dictionary<long, int> live, List<Signing> signings,
        Dictionary<string, int> ended, List<int> lifetimes)
    {
        var big = new HashSet<int>(c.Nations.Where(n => n.Population > 0).OrderByDescending(n => n.Gdp).Take(30).Select(n => n.Id));
        foreach (var (key, relation) in c.Relations)
        {
            if (!relation.TradeAgreement || live.ContainsKey(key)) continue;
            live[key] = c.ElapsedDays;
            var a = c.NationById(relation.A);
            var b = c.NationById(relation.B);
            if (a is null || b is null) continue;
            signings.Add(new Signing
            {
                Day = c.ElapsedDays, A = a.Id, B = b.Id, Opinion = relation.Opinion,
                DependenceA = a.TradeDependenceOn(b.Id), DependenceB = b.TradeDependenceOn(a.Id),
                Border = CampaignDiplomacy.SharesLandBorder(c, a.MapIndex, b.MapIndex),
                Allied = relation.Stance == DiplomaticStance.Allied, BigA = big.Contains(a.Id), BigB = big.Contains(b.Id)
            });
        }
        List<long> gone = null;
        foreach (var (key, since) in live)
        {
            var relation = c.Relations.GetValueOrDefault(key) ?? c.DormantRelations.GetValueOrDefault(key);
            if (relation is { TradeAgreement: true }) continue;
            (gone ??= new()).Add(key);
            var why = relation is null ? "relation_gone"
                : relation.Stance == DiplomaticStance.AtWar ? "war"
                : relation.EmbargoByA || relation.EmbargoByB ? "embargo"
                : c.NationById(relation.A) is not { Population: > 0 } || c.NationById(relation.B) is not { Population: > 0 } ? "country_gone"
                : "cancelled";
            ended[why] = ended.GetValueOrDefault(why) + 1;
            lifetimes.Add(c.ElapsedDays - since);
        }
        if (gone is not null) foreach (var key in gone) live.Remove(key);
    }

    private static void Year(Campaign c, int live, int signed, Dictionary<string, int> ended)
    {
        var pacts = c.Relations.Values.Count(r => r.NonAggressionPact);
        var nations = c.Nations.Count(n => n.Population > 0 && !c.IsRetired(n));
        Console.WriteLine($"DAY {c.ElapsedDays}: trade agreements {live} (signed {signed}, ended {ended.Values.Sum()}), pacts {pacts}, " +
                          $"countries {nations}, agreements per country {2.0 * live / Math.Max(1, nations):0.00}");
    }

    /// <summary>
    /// The offers a player can make, asked of real pairs as previews. The askers are the forty
    /// largest economies (where a player usually sits); the asked are each one's land neighbours and
    /// its five largest trading partners. Nothing is sent and nothing changes.
    /// </summary>
    private static void Preview(Campaign c, Dictionary<string, (int Asked, int Yes, double Points)> rows)
    {
        void Add(string key, bool yes, float points = 0f)
        {
            var row = rows.GetValueOrDefault(key);
            rows[key] = (row.Asked + 1, row.Yes + (yes ? 1 : 0), row.Points + (float.IsFinite(points) ? points : 0f));
        }
        var askers = c.Nations.Where(n => n.Population > 0 && !c.IsRetired(n) && !n.Landless && n.Movement is null)
            .OrderByDescending(n => n.Gdp).Take(40).ToList();
        foreach (var asker in askers)
        {
            var targets = new HashSet<int>();
            foreach (var other in c.Nations)
                if (other.Id != asker.Id && other.Population > 0 && !other.Landless && other.Movement is null &&
                    CampaignDiplomacy.SharesLandBorder(c, asker.MapIndex, other.MapIndex)) targets.Add(other.Id);
            foreach (var partner in asker.TradePartners.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(5)) targets.Add(partner.Key);
            foreach (var id in targets.OrderBy(id => id))
            {
                var asked = c.NationById(id);
                if (asked is null || asked.Population <= 0 || asked.Landless || asked.Movement is not null) continue;
                var relation = CampaignDiplomacy.Peek(c, asker.Id, asked.Id);
                if (relation is { Stance: DiplomaticStance.AtWar }) continue;
                if (relation?.TradeAgreement != true)
                {
                    var button = AgreementSystem.Willingness(c, asked, asker, AgreementKind.TradeAgreement, preview: true) >=
                                 AgreementSystem.Needed(c, AgreementKind.TradeAgreement);
                    Add("trade.button (agreement screen)", button);
                    var give = Ultimatum.AssessNegotiatedDeal(c, asker, asked,
                        new Ultimatum.Deal(new Ultimatum.Side { TradeAgreement = true }, new Ultimatum.Side()));
                    if (give.Valid) Add("trade.table, in our column, for nothing", give.Acceptable, give.ToValue);
                    var ask = Ultimatum.AssessNegotiatedDeal(c, asker, asked,
                        new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side { TradeAgreement = true }));
                    if (ask.Valid) Add("trade.table, in their column, for nothing", ask.Acceptable, ask.ToValue);
                    if (give.Valid) Add("trade.bypass: button refuses, table signs", !button && give.Acceptable);
                    if (give.Valid) Add("trade.points for US when it signs (our column)", give.Acceptable, give.FromValue);
                }
                if (relation?.NonAggressionPact != true)
                {
                    var button = AgreementSystem.Willingness(c, asked, asker, AgreementKind.NonAggressionPact, preview: true) >=
                                 AgreementSystem.Needed(c, AgreementKind.NonAggressionPact);
                    Add("pact.button (agreement screen)", button);
                    var give = Ultimatum.AssessNegotiatedDeal(c, asker, asked,
                        new Ultimatum.Deal(new Ultimatum.Side { NonAggression = true }, new Ultimatum.Side()));
                    if (give.Valid) Add("pact.table, in our column, for nothing", give.Acceptable, give.ToValue);
                    if (give.Valid) Add("pact.bypass: button refuses, table signs", !button && give.Acceptable);
                }
                var gift = Ultimatum.AssessNegotiatedDeal(c, asker, asked,
                    new Ultimatum.Deal(new Ultimatum.Side { Cash = 0.01f }, new Ultimatum.Side()));
                if (gift.Valid) Add("gift.$10 million (buys a 180-day truce)", gift.Acceptable, gift.ToValue);
            }
        }
    }

    /// <summary>
    /// One market week routed twice on the same prices: as the world stands, and with every trade
    /// agreement taken away. The difference is everything an agreement does to trade - the code gives
    /// it one lever, a 1.75 multiplier on how a buyer ranks a seller - so this is that lever's whole
    /// effect: who it moves volume from, and whether anybody ends up with more of anything.
    /// </summary>
    private static void Effect(Campaign c, List<long> pairs)
    {
        if (pairs.Count == 0) { Console.WriteLine("EFFECT: no trade agreements in force"); return; }
        var parties = new HashSet<int>();
        foreach (var key in pairs)
        {
            var r = c.Relations.GetValueOrDefault(key);
            if (r is null) continue;
            parties.Add(r.A); parties.Add(r.B);
        }
        (double Pair, double World, Dictionary<int, double> Imports, Dictionary<int, double> Shortage, Dictionary<int, double> Satisfied) Measure()
        {
            TradeNetwork.Route(c);
            var pair = 0.0;
            foreach (var key in pairs)
            {
                var r = c.Relations.GetValueOrDefault(key);
                if (r is null) continue;
                pair += c.NationById(r.A)?.TradePartners.GetValueOrDefault(r.B) ?? 0f;
            }
            var world = c.Nations.Sum(n => (double)n.ExportValue);
            var imports = new Dictionary<int, double>();
            var shortage = new Dictionary<int, double>();
            var satisfied = new Dictionary<int, double>();
            foreach (var n in c.Nations)
            {
                if (n.Population <= 0) continue;
                double bought = 0, weight = 0, missing = 0, covered = 0, wanted = 0;
                foreach (var q in n.Market.Values)
                {
                    var def = CommodityCatalog.Get(q.Commodity);
                    bought += q.Imports * def.BasePrice;
                    var sat = q.Demand <= 0.001f ? 1f : Math.Clamp((q.Production + q.Imports) / q.Demand, 0f, 1f);
                    weight += def.EssentialWeight;
                    missing += (1f - sat) * def.EssentialWeight;
                    covered += Math.Min(q.Demand, q.Production + q.Imports) * def.BasePrice;
                    wanted += q.Demand * def.BasePrice;
                }
                imports[n.Id] = bought;
                shortage[n.Id] = weight <= 0 ? 0 : missing / weight;
                satisfied[n.Id] = wanted <= 0 ? 1 : covered / wanted;
            }
            return (pair, world, imports, shortage, satisfied);
        }

        var with = Measure();
        var flags = new List<DiplomaticRelation>();
        foreach (var key in pairs)
            if (c.Relations.GetValueOrDefault(key) is { TradeAgreement: true } r) { r.TradeAgreement = false; flags.Add(r); }
        var without = Measure();
        foreach (var r in flags) r.TradeAgreement = true;
        TradeNetwork.Route(c);

        Console.WriteLine($"EFFECT of {pairs.Count} trade agreements, one market week routed with and without them (same prices):");
        Console.WriteLine($"  trade between the signed pairs: {with.Pair:0.000} with, {without.Pair:0.000} without " +
                          $"({(with.Pair / Math.Max(1e-9, without.Pair) - 1):+0%;-0%})");
        Console.WriteLine($"  world trade (all exports): {with.World:0.000} with, {without.World:0.000} without " +
                          $"({(with.World / Math.Max(1e-9, without.World) - 1):+0.0%;-0.0%})");
        double Sum(Dictionary<int, double> d, IEnumerable<int> ids) => ids.Sum(id => d.GetValueOrDefault(id));
        var outsiders = c.Nations.Where(n => n.Population > 0 && !parties.Contains(n.Id)).Select(n => n.Id).ToList();
        Console.WriteLine($"  imports at base prices, the {parties.Count} signatories: {Sum(with.Imports, parties):0.0} with, {Sum(without.Imports, parties):0.0} without " +
                          $"({(Sum(with.Imports, parties) / Math.Max(1e-9, Sum(without.Imports, parties)) - 1):+0.0%;-0.0%})");
        Console.WriteLine($"  imports at base prices, the {outsiders.Count} others: {Sum(with.Imports, outsiders):0.0} with, {Sum(without.Imports, outsiders):0.0} without " +
                          $"({(Sum(with.Imports, outsiders) / Math.Max(1e-9, Sum(without.Imports, outsiders)) - 1):+0.0%;-0.0%})");
        double Mean(Dictionary<int, double> d, IEnumerable<int> ids) { var l = ids.ToList(); return l.Count == 0 ? 0 : l.Average(id => d.GetValueOrDefault(id)); }
        Console.WriteLine($"  shortage index (essential-weighted), signatories: {Mean(with.Shortage, parties):0.0000} with, {Mean(without.Shortage, parties):0.0000} without; " +
                          $"others {Mean(with.Shortage, outsiders):0.0000} with, {Mean(without.Shortage, outsiders):0.0000} without");
        // The market's own price rule: local scarcity is 1 + 1.4 x the unmet share, so this is the
        // price change an agreement implies through supply, before any world-price movement.
        var priceWith = 1 + 1.4 * (1 - Mean(with.Satisfied, parties));
        var priceWithout = 1 + 1.4 * (1 - Mean(without.Satisfied, parties));
        Console.WriteLine($"  implied local price factor, signatories: {priceWith:0.0000} with, {priceWithout:0.0000} without " +
                          $"({(priceWith / priceWithout - 1):+0.00%;-0.00%;0.00%}); tariffs: unchanged by construction (rate is the importer's policy, any partner)");
        var moved = parties.Select(id => (Id: id, Delta: with.Imports.GetValueOrDefault(id) - without.Imports.GetValueOrDefault(id)))
            .Concat(outsiders.Select(id => (Id: id, Delta: with.Imports.GetValueOrDefault(id) - without.Imports.GetValueOrDefault(id))))
            .OrderBy(x => x.Delta).ToList();
        string Tag(int id) => c.NationById(id)?.Tag ?? id.ToString();
        Console.WriteLine("  imports lost to the agreements (base-price value): " +
                          string.Join(", ", moved.Take(6).Where(x => x.Delta < -1e-6).Select(x => $"{Tag(x.Id)}{(parties.Contains(x.Id) ? "*" : "")} {x.Delta:0.00}")));
        Console.WriteLine("  imports gained: " +
                          string.Join(", ", moved.AsEnumerable().Reverse().Take(6).Where(x => x.Delta > 1e-6).Select(x => $"{Tag(x.Id)}{(parties.Contains(x.Id) ? "*" : "")} +{x.Delta:0.00}")));
        var dead = pairs.Count(key => c.Relations.GetValueOrDefault(key) is { } r &&
                                      (c.NationById(r.A)?.TradePartners.GetValueOrDefault(r.B) ?? 0f) <= 0.0001f);
        Console.WriteLine($"  agreements with no trade between the pair this week: {dead} of {pairs.Count} ({dead / (double)pairs.Count:P0})");
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
