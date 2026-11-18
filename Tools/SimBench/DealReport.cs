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
/// prices instead of guessed from the code. Since trade agreements have terms (TradeTerms) a fourth:
/// what the terms say and how they end, and in the same week what the tariff cuts did to the import
/// markup and what the supply guarantees did for the buyers they were written for.
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
        var delivered = new List<double>();

        for (var day = 1; day <= days; day++)
        {
            campaign.TickDay();
            Watch(campaign, live, signings, ended, lifetimes);
            if (day % 180 == 0) Preview(campaign, previews);
            // How well the guarantees in force are kept, sampled each quarter (TradeTerms.Delivered).
            if (day % 90 == 0)
                foreach (var r in campaign.Relations.Values)
                    if (r.Trade is { HasGuarantee: true } g) delivered.Add(g.Delivered);
            if (day % 365 == 0 || day == days) Year(campaign, live.Count, signings.Count, ended);
        }

        Console.WriteLine();
        Console.WriteLine("FLOW (DealTally: put / signed / declined, by kind and seat)");
        foreach (var (key, count) in DealTally.Snapshot()) Console.WriteLine($"  {key} = {count}");
        Console.WriteLine($"  ai.supply_hedge (AiStatecraft) = {AiStatecraft.Tally.GetValueOrDefault("supply_hedge") - hedgeBefore}");

        Console.WriteLine();
        Terms(campaign, delivered);

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
                // The bench's own "player" answers its papers by hand, not by the government's
                // count, so it is left out of the trade rows: the comparison is government to government.
                if (relation?.TradeAgreement != true && !asked.IsHuman)
                {
                    // One paper, one answer: the button sends the ordinary terms and is answered by
                    // the deal table's count, so the table with the same terms must say the same.
                    // There is no column any more - the terms are the pair's - so one table row.
                    var ordinary = TradeTermsCatalog.Ordinary(c, asker, asked);
                    var table = Ultimatum.AssessNegotiatedDeal(c, asker, asked,
                        new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side()) { Trade = ordinary });
                    // Asked of the same pairs: a table that cannot be laid (a fresh refusal's
                    // pause, say) is a question neither route may put.
                    if (table.Valid)
                    {
                        var button = DealAnswer.GovernmentSignsAgreement(c, asked, asker, AgreementKind.TradeAgreement, ordinary);
                        Add("trade.button (agreement screen)", button);
                        Add("trade.table, ordinary terms, for nothing", table.Acceptable, table.ToValue);
                        Add("trade.bypass: button refuses, table signs", !button && table.Acceptable);
                        Add("trade.points for US when it signs", table.Acceptable, table.FromValue);
                    }

                    // What the composer would ask of them, and whether they would sign it.
                    var best = TradeTermsComposer.BestFor(c, asker, asked, TradeTermsComposer.Candidates(c, asker, asked));
                    Add("trade.composer finds terms they sign", best is not null);
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
        // The supply guarantees in force: whose shortage of which good each one is meant to cover.
        var lanes = new List<(int Receiver, Commodity Good)>();
        foreach (var key in pairs)
            if (c.Relations.GetValueOrDefault(key) is { Trade: { } t } held && t.TryGuaranteeGood(out var g))
                lanes.Add((held.Other(t.GuarantorId), g));
        (double Markup, double Guaranteed) TermsWeek()
        {
            // The import markup each signatory paid, weighted by what it imported (MarketSystem's
            // rule: a relieved share sheds the markup), and how fully guaranteed buyers were served.
            double paid = 0, weight = 0;
            foreach (var id in parties)
            {
                var n = c.NationById(id);
                if (n is null) continue;
                foreach (var q in n.Market.Values)
                {
                    if (q.Imports <= 0.001f) continue;
                    var w = q.Imports * CommodityCatalog.Get(q.Commodity).BasePrice;
                    paid += w * (1 + (c.Defines.ImportMarkup - 1) *
                                 (1 - Math.Clamp(c.Defines.TradeAgreementMarkupRelief * Math.Clamp(q.Relieved / q.Imports, 0f, 1f), 0f, 1f)));
                    weight += w;
                }
            }
            double served = 0;
            foreach (var (receiver, good) in lanes)
                if (c.NationById(receiver)?.Market.TryGetValue(good, out var q) == true)
                    served += q.Demand <= 0.001f ? 1 : Math.Clamp((q.Production + q.Imports) / q.Demand, 0f, 1f);
            return (weight > 0 ? paid / weight : 1, lanes.Count > 0 ? served / lanes.Count : 1);
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
        var termsWith = TermsWeek();
        var flags = new List<DiplomaticRelation>();
        foreach (var key in pairs)
            if (c.Relations.GetValueOrDefault(key) is { TradeAgreement: true } r) { r.TradeAgreement = false; flags.Add(r); }
        // The flag down takes the terms with it: the market reads terms only where the flag is up.
        var without = Measure();
        var termsWithout = TermsWeek();
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
                          $"({(priceWith / priceWithout - 1):+0.00%;-0.00%;0.00%})");
        Console.WriteLine($"  import markup paid by the signatories (import-weighted): {termsWith.Markup:0.0000} with, {termsWithout.Markup:0.0000} without " +
                          $"({(termsWith.Markup / termsWithout.Markup - 1):+0.00%;-0.00%;0.00%})");
        Console.WriteLine($"  guaranteed buyers' satisfaction of the guaranteed good ({lanes.Count} guarantees): {termsWith.Guaranteed:P1} with, " +
                          $"{termsWithout.Guaranteed:P1} without ({(termsWith.Guaranteed - termsWithout.Guaranteed) * 100:+0.0;-0.0;0.0} pp)");
        Console.WriteLine($"  shortage of countries outside agreements: {Mean(with.Shortage, outsiders) - Mean(without.Shortage, outsiders):+0.0000;-0.0000;0.0000} from the agreements");
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

    /// <summary>
    /// What the agreements say (DealTally "trade_terms.*", counted where they are signed and ended):
    /// how the presets, guarantees, goods lists and lengths are spread, how terms end - run out,
    /// renewed, left at a price or for free - and how well the guarantees are kept. A mechanic with
    /// one outcome looks like a working one in a log; these shares are what tell them apart.
    /// </summary>
    private static void Terms(Campaign c, List<double> delivered)
    {
        int T(string key) => DealTally.Get(key);
        int Sum(string prefix) => DealTally.Snapshot().Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(e => e.Value);
        var presets = DealTally.Snapshot().Where(e => e.Key.StartsWith("trade_terms.signed.preset.", StringComparison.Ordinal))
            .Select(e => (Id: e.Key["trade_terms.signed.preset.".Length..], e.Value)).OrderByDescending(p => p.Value).ToList();
        var signed = presets.Sum(p => p.Value);
        Console.WriteLine($"TERMS: {signed} trade agreements signed with terms");
        if (signed > 0)
        {
            Console.WriteLine("  by preset: " + string.Join(", ", presets.Select(p => $"{p.Id} {p.Value} ({p.Value / (double)signed:P0})")));
            var largest = presets.Count > 0 ? presets[0].Value / (double)signed : 0;
            Console.WriteLine($"  largest preset {largest:P0}; presets with at least 3 % each: {presets.Count(p => p.Value / (double)signed >= 0.03)}");
            Console.WriteLine($"  with a guarantee {T("trade_terms.signed.with_guarantee") / (double)signed:P1}; " +
                              $"with a goods list {T("trade_terms.signed.with_goods_list") / (double)signed:P1}");
            var years = DealTally.Snapshot().Where(e => e.Key.StartsWith("trade_terms.signed.years.", StringComparison.Ordinal))
                .Select(e => (Years: int.Parse(e.Key["trade_terms.signed.years.".Length..]), e.Value)).OrderBy(y => y.Years == 0 ? 99 : y.Years);
            Console.WriteLine("  by length: " + string.Join(", ", years.Select(y => $"{(y.Years == 0 ? "no end" : y.Years + "y")} {y.Value / (double)signed:P1}")));
        }
        var renewed = Sum("trade_terms.renewed.preset.");
        var expired = T("trade_terms.end.expired");
        var ends = renewed + expired;
        Console.WriteLine($"  at the end of a term: renewed {renewed} (of them on smaller terms {T("trade_terms.renewed.concession")}), " +
                          $"expired {expired}" + (ends > 0 ? $" - renewed {renewed / (double)ends:P0}, expired {expired / (double)ends:P0}" : ""));
        Console.WriteLine($"  left early: paid {T("trade_terms.exit.paid")}, free {T("trade_terms.exit.free")}; sanctioned away {T("trade_terms.end.sanctioned")}; " +
                          $"renewal papers to a player {T("trade_terms.renewal_paper")}");
        var toHuman = Sum("agreement.TradeAgreement.ai>human.") + Sum("deal.trade_agreement.ai>human.put");
        Console.WriteLine($"  AI trade papers to the bench human: {toHuman}");
        var live = c.Relations.Values.Where(r => r.Trade is { HasGuarantee: true }).Select(r => (double)r.Trade.Delivered).ToList();
        Console.WriteLine($"  guarantees in force at the end: {live.Count}, delivered median {(live.Count > 0 ? Median(live) : 0):0.00}; " +
                          $"quarterly samples {delivered.Count}, median {(delivered.Count > 0 ? Median(delivered) : 0):0.00}, " +
                          $"under half {(delivered.Count > 0 ? delivered.Count(d => d < 0.5) / (double)delivered.Count : 0):P0}");
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
