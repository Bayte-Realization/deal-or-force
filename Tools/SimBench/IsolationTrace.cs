using System;
using System.Linq;
using DealOrForce.Simulation;

static class IsolationTrace
{
    public static void Print(Campaign c, string stage)
    {
        if (Environment.GetEnvironmentVariable("DOF_ISOLATION_TRACE") != "1") return;
        foreach (var id in new[] {71,90,161})
        {
            var n = c.NationById(id);
            if (n is null) continue;
            var trade = string.Join(",",n.TradePartners.OrderBy(p=>p.Key).Select(p=>$"{p.Key}:{p.Value:R}"));
            Console.WriteLine($"ISOTRACE {stage} seed={c.Seed} day={c.ElapsedDays} {n.Tag}/{id} " +
                $"imports={n.ImportValue:R} exports={n.ExportValue:R} partners={trade} " +
                $"baselines={string.Join(",",n.OrganizationBelongingAtStart.OrderBy(p=>p.Key).Select(p=>$"{p.Key}:{p.Value:R}"))}");
        }
    }
}
