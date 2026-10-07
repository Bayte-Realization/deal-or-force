using System;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--stability TAG[,TAG]` plays the days and every half year prints, for each country, its
/// stability, where the formula is pulling it and why (StabilityCore.Target's own breakdown),
/// and what its regions are made of: living standard, radicalism, literacy. For finding out why
/// a poor country's stability slides when nothing is happening to it.
/// </summary>
internal static class StabilityReport
{
    public static int Run(Campaign c, int days, string tags)
    {
        c.IsPaused = false;
        var nations = tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => c.NationByTag(tag.Trim())).Where(n => n is not null).ToArray();
        void Print()
        {
            foreach (var nation in nations)
            {
                var drivers = new Breakdown();
                var target = StabilityCore.Target(c, nation, drivers);
                var states = c.StatesOf(nation);
                long people = Math.Max(1L, states.Sum(s => Math.Max(0L, s.Population)));
                float Avg(Func<ScenarioState, float> f) => (float)(states.Sum(s => f(s) * (double)Math.Max(0L, s.Population)) / people);
                Console.WriteLine($"STAB day {c.ElapsedDays} {nation.Tag} stability={nation.Stability:0.000} target={target:0.000} " +
                                  $"bias={nation.StabilityBias:+0.000;-0.000} governance={nation.StabilityGovernance:+0.000;-0.000} " +
                                  $"living={Avg(s => s.StandardOfLiving):0.000} radicalism={Avg(s => s.Radicalism):0.000} " +
                                  $"literacy={Avg(s => s.Literacy):0.000} sol_nation={nation.StandardOfLiving:0.000} gov={nation.Government}");
                Console.WriteLine("      " + string.Join(" | ", drivers.Entries.Select(e => $"{e.Source} {e.Value:+0.000;-0.000}")));
            }
        }
        Print();
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 182 == 0) Print();
        }
        return 0;
    }
}
