using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// What building costs a country against what it earns, and how much of its money goes there.
///
/// A friend's report, 15 November 2026: "the flasks are green and the money flies away". The
/// owner's answer was that construction eats the treasury - but nobody had the number. This run
/// prints it: for a handful of reference countries and for the world, what one civilian factory
/// costs a week at the country's pace, as a share of the week's gross revenue; how many sites the
/// country has open, how many it works at once, how many wait for money; and what construction
/// took last week as a share of everything the treasury paid out. The last line is one
/// machine-readable summary, so two runs with different defines (`--define`) can be compared.
///
/// `SimBench &lt;days&gt; &lt;seed&gt; --construction`
/// </summary>
internal static class ConstructionEconomyReport
{
    private static readonly string[] Reference = { "ALB", "ITA", "FRA", "DEU", "POL", "IND", "CHN", "USA", "NGA", "BRA" };

    public static int Run(Campaign campaign, int days)
    {
        campaign.IsPaused = false;
        var start = campaign.ElapsedDays;
        var startGdp = World(campaign).Sum(n => (double)n.Gdp);
        var startBuildings = campaign.Nations.ToDictionary(n => n.Id, n => Buildings(campaign, n));
        var startWorldBuildings = startBuildings.Values.Sum();
        Console.WriteLine($"Construction economy: days={days}, step={campaign.TickHours}h, world GDP={startGdp:0}B, buildings={startWorldBuildings}");
        Console.WriteLine("  site/rev: one civilian factory's weekly payment at the country's pace, as a share of the week's budget income (positive budget lines)");
        Console.WriteLine("  drain: construction paid last week, as a share of everything the treasury paid out");

        double drainSum = 0; var drainSamples = 0;
        double frozenSum = 0; var frozenSamples = 0;
        double waitingSum = 0;
        var failure = "";
        var next = 0;
        var drains = new Drains();
        MoneyLedger.Sources = new();
        while (true)
        {
            var day = campaign.ElapsedDays - start;
            drains.Collect(campaign);
            if (day >= next || day >= days)
            {
                var s = Sample(campaign, startBuildings, day);
                if (day > 0) { drainSum += s.Drain; drainSamples++; frozenSum += s.Frozen; waitingSum += s.WaitingShare; frozenSamples++; }
                next = (day / 91 + 1) * 91;
            }
            if (day >= days) break;
            if (!SimulationReportProgress.TickDay(campaign, out failure)) break;
        }
        if (failure.Length > 0) { Console.WriteLine("FAIL: " + failure); return 1; }

        var economies = World(campaign);
        var endGdp = economies.Sum(n => (double)n.Gdp);
        var debt = economies.Sum(n => (double)(float.IsFinite(n.PublicDebt) ? n.PublicDebt : 0f));
        var inflation = Median(economies.Select(n => (double)n.Inflation));
        var defaults = economies.Count(n => n.DebtCondition is DebtCondition.Default or DebtCondition.Insolvent or DebtCondition.Restructuring);
        var built = campaign.Nations.Sum(n => Buildings(campaign, n)) - startWorldBuildings;
        var years = Math.Max(1d, days / 365d);
        var growth = startGdp > 0 ? Math.Pow(endGdp / startGdp, 1d / years) - 1d : 0d;
        var siteToRevenue = Median(economies.Select(n => SiteShare(campaign, n)).Where(double.IsFinite));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CONSTRUCTION_SUMMARY days={days} gdp={endGdp:0} growth/yr={growth:P2} debt/gdp={(endGdp > 0 ? debt / endGdp : 0):P1} " +
            $"inflation_median={inflation:P1} defaults={defaults} built={built} built/yr={built / years:0} " +
            $"drain_mean={(drainSamples > 0 ? drainSum / drainSamples : 0):P1} frozen_mean={(frozenSamples > 0 ? frozenSum / frozenSamples : 0):0.0} " +
            $"waiting_mean={(frozenSamples > 0 ? waitingSum / frozenSamples : 0):P1} site/rev_median={siteToRevenue:P1}"));
        drains.Print(campaign);
        MoneyLedger.Sources = null;
        return 0;
    }

    /// <summary>
    /// Covert operations, diplomacy and the upkeep of a raised alert, summed over every closed week
    /// of the run against the budget income of the same weeks. One week's snapshot misleads for
    /// payments that come in lumps (an operation is paid in full the day it is ordered), so the
    /// share is the run's total over the run's income. Found while doing the construction rework:
    /// these three, not the building sites, were what emptied the large treasuries.
    /// </summary>
    private sealed class Drains
    {
        private static readonly MoneyFlow[] Flows = { MoneyFlow.Covert, MoneyFlow.Diplomacy, MoneyFlow.AlertUpkeep };
        private readonly Dictionary<int, int> _seen = new();
        private readonly Dictionary<int, double> _income = new();
        private readonly Dictionary<int, double[]> _out = new();
        private readonly Dictionary<int, double[]> _in = new();

        public void Collect(Campaign campaign)
        {
            foreach (var nation in World(campaign))
            {
                if (nation.LedgerLastWeek is null || nation.LedgerClosedDay < 0) continue;
                if (_seen.TryGetValue(nation.Id, out var day) && day == nation.LedgerClosedDay) continue;
                _seen[nation.Id] = nation.LedgerClosedDay;
                _income[nation.Id] = _income.GetValueOrDefault(nation.Id) + Income(nation);
                if (!_out.TryGetValue(nation.Id, out var spent)) _out[nation.Id] = spent = new double[Flows.Length];
                if (!_in.TryGetValue(nation.Id, out var received)) _in[nation.Id] = received = new double[Flows.Length];
                for (var i = 0; i < Flows.Length; i++)
                {
                    spent[i] -= MoneyLedger.LastOut(nation, Flows[i]);
                    received[i] += MoneyLedger.LastIn(nation, Flows[i]);
                }
            }
        }

        public void Print(Campaign campaign)
        {
            Console.WriteLine("Money drains over the run (paid out, share of the same weeks' budget income):");
            foreach (var tag in Reference)
            {
                var nation = campaign.Nations.FirstOrDefault(n => string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase));
                if (nation is null || !_income.TryGetValue(nation.Id, out var income) || income <= 0) continue;
                var spent = _out[nation.Id];
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {tag,-4} income {income,9:0.00}  covert {spent[0],8:0.00} ({spent[0] / income,6:P1})  diplomacy {spent[1],7:0.000} ({spent[1] / income,6:P1})  alert {spent[2],7:0.00} ({spent[2] / income,6:P1})  treasury {nation.Treasury,8:0.0}"));
            }
            var ids = _income.Where(p => p.Value > 0).Select(p => p.Key).ToArray();
            var totalIncome = ids.Sum(id => _income[id]);
            double Total(int flow) => ids.Sum(id => _out[id][flow]);
            double MedianShare(int flow) => Median(ids.Select(id => _out[id][flow] / _income[id]));
            int Above(int flow, double share) => ids.Count(id => _out[id][flow] / _income[id] > share);
            var line = string.Create(CultureInfo.InvariantCulture,
                $"MONEY_DRAINS countries={ids.Length} covert/income={Total(0) / totalIncome:P2} diplomacy/income={Total(1) / totalIncome:P2} alert/income={Total(2) / totalIncome:P2} " +
                $"covert_median={MedianShare(0):P2} diplomacy_median={MedianShare(1):P2} alert_median={MedianShare(2):P2} " +
                $"covert>20%={Above(0, 0.20)} diplomacy>10%={Above(1, 0.10)} alert>10%={Above(2, 0.10)}");
            Console.WriteLine(line);
            // The worst payers, each with the line of code most of its money left by.
            string Site(int id, MoneyFlow flow) => MoneyLedger.Sources?.Where(p => p.Key.Nation == id && p.Key.Flow == flow && p.Value < 0)
                .OrderBy(p => p.Value).Select(p => " " + p.Key.Site).FirstOrDefault() ?? "";
            string Worst(int flow, double share) => string.Join(", ", ids.Where(id => _out[id][flow] / _income[id] > share)
                .OrderByDescending(id => _out[id][flow] / _income[id]).Take(8)
                .Select(id => string.Create(CultureInfo.InvariantCulture, $"{campaign.NationById(id)?.Tag} {_out[id][flow] / _income[id]:P0}") + Site(id, Flows[flow])));
            Console.WriteLine("  covert>20%: " + Worst(0, 0.20));
            Console.WriteLine("  diplomacy>10%: " + Worst(1, 0.10));
            Console.WriteLine("  alert>10%: " + Worst(2, 0.10));
            if (MoneyLedger.Sources is not { } sources) return;
            // Which line of code the money left by, summed over the world: the place to change.
            foreach (var flow in Flows)
            {
                var top = sources.Where(p => p.Key.Flow == flow && p.Value < 0)
                    .GroupBy(p => p.Key.Site).Select(g => (Site: g.Key, Paid: -g.Sum(p => p.Value)))
                    .OrderByDescending(p => p.Paid).Take(6)
                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Site} {p.Paid:0.0}"));
                Console.WriteLine($"  {flow} paid by: " + string.Join("; ", top));
            }
        }
    }

    private readonly record struct Snapshot(double Drain, int Frozen, double WaitingShare);

    private static Snapshot Sample(Campaign campaign, Dictionary<int, int> startBuildings, int day)
    {
        Console.WriteLine($"DAY {day,5} ({campaign.Date})");
        Console.WriteLine("  tag  gdp/wk  income/wk  revenue/wk  civ cost  days  site/wk  site/rev  open work wait$  paid/wk  drain  treasury  debt/gdp  built  civ");
        foreach (var tag in Reference)
        {
            var nation = campaign.Nations.FirstOrDefault(n => string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (nation is null || nation.Gdp <= 0f) continue;
            var cost = ConstructionCost.Of(campaign, nation, BuildingKind.CivilianIndustry);
            var expected = ConstructionCost.ExpectedDays(campaign, nation, BuildingKind.CivilianIndustry);
            var weekly = WeeklyPayment(campaign, nation, BuildingKind.CivilianIndustry);
            var open = campaign.Construction.Count(p => p.NationId == nation.Id);
            var waiting = campaign.Construction.Count(p => p.NationId == nation.Id && p.Waiting == ConstructionQueue.Treasury);
            var paid = SitesPaid(nation);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {tag,-4} {nation.Gdp / 52f,7:0.00} {Income(nation),10:0.000} {nation.WeeklyGrossIncome,10:0.000} {cost,9:0.000} {expected,5} {weekly,8:0.000} {SiteShare(campaign, nation),8:P0} " +
                $"{open,5} {ConstructionCost.WorkingSites(campaign, nation),4} {waiting,5} {paid,8:0.000} {Drain(nation),6:P0} " +
                $"{nation.Treasury,9:0.00} {(nation.Gdp > 0 ? nation.PublicDebt / nation.Gdp : 0),9:P0} {Buildings(campaign, nation) - startBuildings.GetValueOrDefault(nation.Id),6} {Civilian(campaign, nation),4}"));
        }
        // Where each reference country's money went last week: its three largest payments, from
        // the same rows the ledger screen draws (MoneyLedgerView).
        foreach (var tag in Reference)
        {
            var nation = campaign.Nations.FirstOrDefault(n => string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (nation is null || nation.Gdp <= 0f || nation.LedgerLastWeek is null) continue;
            var top = MoneyLedgerView.ForNation(campaign, nation).Where(r => !r.Total && !r.Note && r.Weekly < 0f)
                .OrderBy(r => r.Weekly).Take(4).Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Caption} {r.Weekly:0.000}"));
            Console.WriteLine($"    {tag} out: " + string.Join("; ", top));
        }
        // Who keeps its forces on a raised alert, at what posture, and against whom: an alert with no
        // war behind it is the one that empties a treasury for nothing.
        var alerted = campaign.Nations.Where(n => n.Population > 0 && n.Movement is null && DefconSystem.UpkeepMultiplier(campaign, n) > 1f)
            .OrderByDescending(n => DefconSystem.WeeklyUpkeep(campaign, n)).Take(6)
            .Select(n => string.Create(CultureInfo.InvariantCulture,
                $"{n.Tag} posture {n.NuclearPosture} paid {DefconSystem.WeeklyUpkeep(campaign, n):0.00} at war with ") +
                string.Join(",", campaign.Wars.Where(w => !w.Ended && w.Involves(n.Id)).SelectMany(w => w.EnemiesOf(n.Id))
                    .Select(id => campaign.NationById(id)?.Tag).Distinct())).ToArray();
        Console.WriteLine($"    alert (world DEFCON {DefconSystem.CurrentLevel(campaign)}): " + (alerted.Length > 0 ? string.Join("; ", alerted) : "nobody pays"));
        var economies = World(campaign);
        var drains = economies.Select(Drain).Where(double.IsFinite).ToArray();
        var meanDrain = drains.Length > 0 ? drains.Average() : 0;
        var frozen = economies.Count(n => ConstructionQueue.FrozenForMoney(campaign, n));
        var sites = campaign.Construction.Count;
        var waitingMoney = campaign.Construction.Count(p => p.Waiting == ConstructionQueue.Treasury);
        var waitingTurn = campaign.Construction.Count(p => p.Waiting == ConstructionQueue.Queued);
        var working = campaign.Construction.Count(p => p.Waiting == ConstructionQueue.Working);
        var share = Median(economies.Select(n => SiteShare(campaign, n)).Where(double.IsFinite));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  world: sites={sites} working={working} waiting-turn={waitingTurn} waiting-money={waitingMoney} frozen-countries={frozen} " +
            $"site/rev median={share:P0} drain mean={meanDrain:P1} GDP={economies.Sum(n => (double)n.Gdp):0}B"));
        return new Snapshot(meanDrain, frozen, sites > 0 ? (double)(waitingMoney + waitingTurn) / sites : 0);
    }

    /// <summary>What one site of this kind pays a week at the country's pace (ConstructionSystem's rule).</summary>
    internal static float WeeklyPayment(Campaign campaign, Nation nation, BuildingKind kind)
    {
        var cost = ConstructionCost.Of(campaign, nation, kind);
        var speed = NationalModifiers.Factor(nation, "construction_speed");
        if (!float.IsFinite(speed) || speed <= 0f) speed = 1f;
        var work = Math.Max(1, (int)MathF.Round(BuildingCatalog.Get(kind).Days / speed));
        return cost / work * ConstructionCost.Pace(campaign, nation) * 7f;
    }

    private static double SiteShare(Campaign campaign, Nation nation)
    {
        var income = Income(nation);
        return income > 0f ? WeeklyPayment(campaign, nation, BuildingKind.CivilianIndustry) / income : double.NaN;
    }

    /// <summary>
    /// What the budget lines raised last week - taxes and the other revenue lines, the "income" the
    /// ledger shows - without the trade balance, which can be a loss and makes a small importer's
    /// gross revenue read near nothing.
    /// </summary>
    internal static float Income(Nation nation)
    {
        var income = 0f;
        if (nation.LedgerBudgetLastWeek is { } budget)
            foreach (var amount in budget) if (amount > 0f) income += amount;
        return income;
    }

    /// <summary>Construction paid last week as a share of every payment the treasury made.</summary>
    private static double Drain(Nation nation)
    {
        var outgoing = 0d;
        if (nation.LedgerBudgetLastWeek is { } budget)
            foreach (var amount in budget) if (amount < 0f) outgoing -= amount;
        for (var flow = 0; flow < MoneyLedger.FlowCount; flow++)
            outgoing -= MoneyLedger.LastOut(nation, (MoneyFlow)flow);
        if (outgoing <= 0d) return double.NaN;
        return SitesPaid(nation) / outgoing;
    }

    /// <summary>
    /// What the building sites took last week. Since 15 November 2026 they are their own ledger line
    /// (MoneyFlow.BuildingSites); before, they were booked with rebuilding and resettlement on
    /// MoneyFlow.Construction, so both are read and a run of either build reports the same thing.
    /// </summary>
    private static float SitesPaid(Nation nation) =>
        -MoneyLedger.LastOut(nation, MoneyFlow.Construction) - MoneyLedger.LastOut(nation, MoneyFlow.BuildingSites);

    private static int Civilian(Campaign campaign, Nation nation)
    {
        var total = 0;
        foreach (var state in campaign.StatesOf(nation)) total += state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry);
        return total;
    }

    private static int Buildings(Campaign campaign, Nation nation)
    {
        var total = 0;
        foreach (var state in campaign.StatesOf(nation))
            foreach (var count in state.Buildings.Values) total += count;
        return total;
    }

    private static Nation[] World(Campaign campaign) =>
        campaign.Nations.Where(n => n.Movement is null && n.Gdp > 1f && float.IsFinite(n.Gdp)).ToArray();

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
}
