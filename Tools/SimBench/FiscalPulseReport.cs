using System;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--fiscal-pulse`: why the world's governments can or cannot spend, every quarter.
///
/// AiFiscalPolicy.Assess is the one door every discretionary AI act walks through - a war, a
/// gift, a building site, a welfare rise - so when the world goes quiet the first question is how
/// many countries that door is shut for, and which of its four reasons shuts it: bills overdue,
/// a debt workout, or a runway too short. For the runway it also says which part of the reserve
/// is the weight: the floor, eight weeks of deficit, building sites or promised cheques.
/// </summary>
internal static class FiscalPulseReport
{
    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        Report(c);
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 90 == 0 || day == days) Report(c);
        }
        return 0;
    }

    private static void Report(Campaign c)
    {
        var d = c.Defines;
        var live = c.Nations.Where(n => n.Population > 0 && c.StatesOf(n).Length > 0).ToList();
        var assessed = live.Select(n => (n, a: AiFiscalPolicy.Assess(c, n, d.AiTreasuryFloor))).ToList();
        string Share(int k) => $"{k}/{live.Count}";
        var reasons = string.Join(" ", assessed.GroupBy(x => x.a.Reason).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}={Share(g.Count())}"));
        var shortRun = assessed.Where(x => x.a.Reason == AiFiscalReason.ShortRunway).ToList();
        float Median(System.Collections.Generic.IEnumerable<float> xs)
        {
            var sorted = xs.Where(float.IsFinite).OrderBy(x => x).ToArray();
            return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
        }
        var cashOfGdp = Median(live.Select(n => n.Gdp > 0 ? n.Treasury / n.Gdp : 0));
        var deficit = assessed.Count(x => x.a.WeeklyBalance < 0);
        var debtOfGdp = Median(live.Select(n => n.Gdp > 0 ? n.PublicDebt / n.Gdp : 0));
        var warGate = assessed.Count(x => !x.a.CanSpend &&
            (x.a.Reason != AiFiscalReason.ShortRunway || x.n.Treasury < x.n.Gdp * d.AiTreasuryFloor * 0.5f));
        // What weighs on the runway of the countries it is too short for, as a share of their reserve.
        float Part(Func<(Nation n, AiFiscalAssessment a), float> part) =>
            shortRun.Count == 0 ? 0 : Median(shortRun.Select(x => x.a.Reserve > 0 ? part(x) / x.a.Reserve : 0));
        Console.WriteLine($"DAY {c.ElapsedDays,4}: {reasons} | war door shut {Share(warGate)} | " +
                          $"weekly deficit {Share(deficit)} | median cash {cashOfGdp:P2} of GDP, debt {debtOfGdp:P0} | " +
                          $"short runway weighed by floor {Part(x => x.n.Gdp * d.AiTreasuryFloor):P0}, " +
                          $"deficit {Part(x => MathF.Max(0, -x.a.WeeklyBalance) * Math.Clamp(d.AiFiscalReserveWeeks, 0, 104)):P0}, " +
                          $"sites {Part(x => x.a.ConstructionPayments):P0}, promises {Part(x => x.a.DiplomaticPayments):P0}");
        Console.WriteLine("  short runway by debt standing: " + string.Join(" ", shortRun.GroupBy(x => x.n.DebtCondition)
            .OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")) +
            $"; median credit left this week {Median(shortRun.Select(x => x.n.Gdp > 0 ? SovereignFinance.AvailableCredit(c, x.n) / x.n.Gdp : 0)):P3} of GDP" +
            $", confidence {Median(shortRun.Select(x => x.n.CreditConfidence)):0.00}" +
            $", issued this week {Median(shortRun.Select(x => x.n.Gdp > 0 ? x.n.DebtIssuedWeekly / x.n.Gdp : 0)):P3}");
        foreach (var tag in new[] { "USA", "CHN", "RUS", "DEU", "IND", "TUR", "IRN", "PAK", "ETH", "VEN" })
        {
            var hit = assessed.FirstOrDefault(x => x.n.Tag == tag);
            if (hit.n is null) continue;
            Console.Write($" {tag}:{hit.a.Reason} cash {hit.n.Treasury / MathF.Max(1e-6f, hit.n.Gdp):P1} bal {hit.a.WeeklyBalance:0.00};");
        }
        Console.WriteLine();
    }
}
