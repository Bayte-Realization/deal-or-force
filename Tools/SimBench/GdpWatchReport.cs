using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--gdp-watch [TAG ...]`: what a country's GDP is made of, every 90 days, and how the
/// world's economies ended against how they began.
///
/// GDP is GrossOutput x 52 x OutputScale, eased a tenth a week (EconomySystem.TickStages):
/// GrossOutput is the market value of what the country sold, which is its plant times
/// EmployedShare x Productivity x modifiers x capital returns x recession, region by region
/// with war damage; OutputScale is the 2024 calibration, drifted every week by SecularCycle.
/// A falling GDP is one of those terms falling, so each is printed beside it.
/// </summary>
internal static class GdpWatchReport
{
    /// <summary>`--gdp-detail`: the first country's market, good by good, at every report.</summary>
    private static bool Detail;

    private static readonly string[] Default = { "RUS", "USA", "CHN", "IND", "DEU", "JPN", "BRA", "TUR", "IRN", "UKR", "KAZ" };

    public static int Run(Campaign c, int days, string[] args)
    {
        var at = Array.IndexOf(args, "--gdp-watch");
        var tags = new List<string>();
        for (var i = at + 1; i < args.Length && !args[i].StartsWith("--"); i++) tags.Add(args[i]);
        if (tags.Count == 0) tags.AddRange(Default);
        Detail = Array.IndexOf(args, "--gdp-detail") >= 0;

        c.IsPaused = false;
        var start = c.Nations.Where(n => n.Movement is null && n.Population > 0 && n.Gdp > 0.01f)
            .GroupBy(n => n.Tag).ToDictionary(g => g.Key, g => g.First().Gdp);
        var yearly = tags.ToDictionary(t => t, _ => new List<float>());
        void Year()
        {
            foreach (var tag in tags) yearly[tag].Add(c.Nations.FirstOrDefault(n => n.Tag == tag)?.Gdp ?? 0f);
        }
        Year();
        Report(c, tags);
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 90 == 0 || day == days) Report(c, tags);
            if (day % 365 == 0) Year();
        }

        Console.WriteLine("GDP_YEARLY (scenario units, start then each 365 days)");
        foreach (var tag in tags)
            Console.WriteLine($"  {tag}: " + string.Join(" ", yearly[tag].Select(v => v.ToString("0"))) +
                $"  | end/start {(yearly[tag][0] > 0 ? yearly[tag][^1] / yearly[tag][0] : 0):0.000}");

        var ratios = new List<(string Tag, float Ratio, float Start)>();
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is not null || nation.Population <= 0 || !start.TryGetValue(nation.Tag, out var s)) continue;
            ratios.Add((nation.Tag, nation.Gdp / s, s));
        }
        ratios.Sort((a, b) => a.Ratio.CompareTo(b.Ratio));
        float Pct(double q) => ratios.Count == 0 ? 0 : ratios[Math.Min(ratios.Count - 1, (int)(ratios.Count * q))].Ratio;
        var lower = ratios.Count(r => r.Ratio < 1f);
        var big = ratios.Where(r => r.Start >= 100f).ToList();
        Console.WriteLine($"GDP_WORLD: {ratios.Count} surviving countries, {lower} end lower than they began " +
                          $"({ratios.Count(r => r.Ratio < 0.9f)} by >10%, {ratios.Count(r => r.Ratio < 0.75f)} by >25%, {ratios.Count(r => r.Ratio < 0.5f)} by >50%); " +
                          $"end/start p10 {Pct(0.1):0.000} p25 {Pct(0.25):0.000} median {Pct(0.5):0.000} p75 {Pct(0.75):0.000} p90 {Pct(0.9):0.000}");
        Console.WriteLine($"GDP_WORLD large (start GDP >= 100): {big.Count}, lower {big.Count(r => r.Ratio < 1f)}, median {(big.Count == 0 ? 0 : big[big.Count / 2].Ratio):0.000}");
        Console.WriteLine("GDP_WORLD worst large: " + string.Join(" ", big.Take(15).Select(r => $"{r.Tag}:{r.Ratio:0.00}")));
        Console.WriteLine("GDP_WORLD best large: " + string.Join(" ", big.AsEnumerable().Reverse().Take(10).Select(r => $"{r.Tag}:{r.Ratio:0.00}")));
        var world0 = start.Values.Sum();
        var world1 = c.Nations.Where(n => n.Movement is null && n.Population > 0).Sum(n => n.Gdp);
        Console.WriteLine($"GDP_WORLD total {world0:0} -> {world1:0} ({world1 / world0:0.000})");
        return 0;
    }

    private static void Report(Campaign c, List<string> tags)
    {
        Console.WriteLine($"DAY {c.ElapsedDays} {c.Date}");
        var live = Warfare.LiveWars(c);
        // Who the world's countries are at war with: a state, or only a movement (* landed, ! landless).
        string Foe(int id) => c.NationById(id) is not { } f ? "?" :
            f.Tag + (f.Movement is null ? "" : f.Movement.Landless ? "!" : "*");
        int atWarCount = 0, onlyMovements = 0, countries = 0;
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is not null || nation.Population <= 0 || c.StatesOf(nation).Length == 0) continue;
            countries++;
            var foesOf = live.Where(w => w.Involves(nation.Id))
                .Select(w => w.IsAttacking(nation.Id) ? w.DefenderId : w.AttackerId).ToList();
            if (foesOf.Count == 0) continue;
            atWarCount++;
            if (foesOf.All(id => c.NationById(id)?.Movement is not null)) onlyMovements++;
        }
        Console.WriteLine($"  WORLD at war {atWarCount}/{countries} countries, {onlyMovements} of them only with movements");
        foreach (var tag in tags)
        {
            var n = c.Nations.FirstOrDefault(x => x.Tag == tag);
            if (n is null) { Console.WriteLine($"  {tag}: gone"); continue; }
            var wars = live.Where(w => w.Involves(n.Id)).ToList();
            var foes = string.Join(",", wars.Select(w => w.IsAttacking(n.Id) ? w.DefenderId : w.AttackerId)
                .Distinct().Select(Foe).Take(4));
            var regions = c.StatesOf(n).Length;
            var held = HeldRegions.EconomyOf(c, n, refresh: false).Count;
            var perHead = n.Population > 0 ? n.Gdp * 1e9 / n.Population : 0;
            var sold = 0f; var made = 0f;
            foreach (var q in n.Market.Values)
            {
                var bp = CommodityCatalog.Get(q.Commodity).BasePrice;
                made += q.Production * bp;
                sold += MathF.Min(q.Production, MathF.Max(0f, q.Demand - q.Imports) + q.Exports) * bp;
            }
            Console.WriteLine($"  {tag}: GDP {n.Gdp,8:0.0} gross {n.GrossOutput,7:0.00} scale {n.OutputScale,7:0.000} drift {n.SecularDrift,6:+0.000;-0.000} " +
                              $"prod {n.Productivity:0.000} empl {n.EmployedShare:0.000} recess {TreasuryDynamics.OutputFactor(c, n):0.000} " +
                              $"mod {NationalModifiers.Factor(n, "output"):0.000} sold/made {(made > 0 ? sold / made : 0):0.000} " +
                              $"pop {n.Population / 1e6:0.0}M perHead {perHead:0} short {n.ShortageIndex:0.000} stab {n.Stability:0.00} hc {n.HumanCapital:0.00} " +
                              $"infl {n.Inflation:P1} cash {n.Treasury:0.0} debt {(n.Gdp > 0 ? n.PublicDebt / n.Gdp : 0):P0} {n.DebtCondition} " +
                              $"regions {regions}/{held} war[{foes}] frontier {SecularCycle.Frontier(c):0}");
            // The labour side of EmployedShare (MarketSystem.WorkersAvailable / JobsDemanded) and
            // the household employment that feeds it (HouseholdEconomy.Review), in millions.
            double labour = 0, employed = 0, plantJobs = 0, otherJobs = 0, workers = 0;
            foreach (var s in c.StatesOf(n))
            {
                var l = HouseholdEconomy.Labour(s);
                labour += l;
                employed += l * HouseholdEconomy.Employment(s);
                plantJobs += MarketSystem.JobsDemanded(s);
                otherJobs += Math.Max(0, s.NonBuildingJobs);
                workers += MarketSystem.WorkersAvailable(s);
            }
            // EconomySystem's `conditions`, written out again: the annual rate that moves
            // productivity and that HouseholdEconomy.Review compounds into service jobs.
            var atWar = wars.Count > 0;
            var budget = BudgetCore.Evaluate(n, n.Gdp / 52f, atWar);
            var industrial = n.Market.GetValueOrDefault(Commodity.IndustrialMaterials)?.Satisfaction ?? 1f;
            var energy = n.Market.GetValueOrDefault(Commodity.Energy)?.Satisfaction ?? 1f;
            var stabilityTerm = (n.Stability - 0.5f) * 0.018f;
            var shortageTerm = -n.ShortageIndex * c.Defines.ShortageAnnualGrowthPenalty;
            var policyTerm = budget.Growth + GovernmentFocusRules.AnnualGrowth(n.GovernmentFocus) +
                             PoliticsCore.GovernmentOf(n).Growth + PoliticsCore.IdeologyOf(n).Growth;
            var marketTerm = -(1f - (0.65f + industrial * 0.18f + energy * 0.17f)) * 0.020f;
            var conditions = 0.006f + stabilityTerm - (atWar ? 0.018f : 0f) + shortageTerm + policyTerm + marketTerm;
            Console.WriteLine($"      {tag} conditions {conditions:+0.0000;-0.0000} = base +0.006 stab {stabilityTerm:+0.0000;-0.0000} war {(atWar ? -0.018f : 0f):+0.000;-0.000} " +
                              $"short {shortageTerm:+0.0000;-0.0000} policy {policyTerm:+0.0000;-0.0000} market {marketTerm:+0.0000;-0.0000}");
            // What the OutputScale drift is made of, and what the stability it reads is made of.
            var secular = SecularCycle.Explain(c, n);
            Console.WriteLine($"      {tag} secular " + string.Join(" ", secular.Entries.Select(e => $"[{e.Source} {e.Value:+0.0000;-0.0000}]")) +
                              $" overstretch {StabilityCore.Overstretch(c, n):0.00}");
            Console.WriteLine($"      {tag} stability " + string.Join(" ", n.StabilityDrivers.Entries
                .OrderByDescending(e => MathF.Abs(e.Value)).Take(9).Select(e => $"[{e.Source} {e.Value:+0.000;-0.000}]")));
            Console.WriteLine($"      {tag} labour {labour:0.0} employed {employed:0.0} ({(labour > 0 ? employed / labour : 0):P1}) " +
                              $"plantJobs {plantJobs:0.0} serviceJobs {otherJobs:0.0} workersForPlant {workers:0.0} " +
                              $"workers/plantJobs {(plantJobs > 0 ? workers / plantJobs : 0):0.000} buildings {c.StatesOf(n).Sum(s => s.Buildings.Values.Sum())}");
            if (!Detail || tag != tags[0]) continue;
            // Good by good, at base price: made, wanted at home, bought in, sent out, sold.
            foreach (var q in n.Market.Values.OrderByDescending(q => q.Production * CommodityCatalog.Get(q.Commodity).BasePrice))
            {
                var bp = CommodityCatalog.Get(q.Commodity).BasePrice;
                var soldQ = MathF.Min(q.Production, MathF.Max(0f, q.Demand - q.Imports) + q.Exports);
                if (q.Production * bp < 0.05f && q.Demand * bp < 0.05f) continue;
                Console.WriteLine($"     {q.Commodity,-20} made {q.Production * bp,7:0.00} demand {q.Demand * bp,7:0.00} imp {q.Imports * bp,6:0.00} " +
                                  $"exp {q.Exports * bp,6:0.00} sold {soldQ * bp,7:0.00} sat {q.Satisfaction:0.00} price {q.Price / bp:0.00}x");
            }
        }
    }
}
