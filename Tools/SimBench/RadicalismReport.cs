using System;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

// Full-world regression, with real markets, policies, wars and AI (not a fixed-pressure toy).
internal static class RadicalismReport
{
    public static int Run(Campaign campaign, int days)
    {
        campaign.IsPaused = false;
        Report(campaign);
        for (var day = 1; day <= days; day++)
        {
            campaign.TickDay();
            if (day % 365 == 0 || day == days) Report(campaign);
        }
        return campaign.States.Any(s => !float.IsFinite(s.Radicalism) || s.Radicalism < 0 || s.Radicalism > 1) ? 1 : 0;
    }

    private static void Report(Campaign c)
    {
        Console.WriteLine($"DAY {c.ElapsedDays}: mean={c.States.Average(s => s.Radicalism):P1}, saturated={c.States.Count(s => s.Radicalism >= .99f)}/{c.States.Count}");
        foreach (var tag in new[] { "RUS", "KOR", "USA", "FRA", "KAZ", "IND", "NGA" })
        {
            var n = c.NationByTag(tag); var regions = c.StatesOf(n);
            if (regions.Length == 0) continue;
            var atWar = c.Wars.Any(w => !w.Ended && w.Involves(n.Id));
            var budget = BudgetCore.Evaluate(n, n.Gdp / 52f, atWar);
            var policy = GovernmentFocusCatalog.Of(n.GovernmentFocus).Radicalism + PoliticsCore.GovernmentOf(n).Radicalism + PoliticsCore.IdeologyOf(n).Radicalism;
            Console.WriteLine($" {tag}: rad={regions.Average(s => s.Radicalism):P1} living={regions.Average(s => s.StandardOfLiving):P1} stability={n.Stability:P1} inflation={n.Inflation:P1} minority={regions.Average(s => EconomySystem.ForeignShare(s, tag)):P1} budget={budget.Radicalism:F6} policy={policy:F6} welfare={n.WelfareSpending:P1}");
        }
    }
}
