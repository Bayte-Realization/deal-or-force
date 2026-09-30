using System;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static class AiWarReport
{
    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        AiStatecraft.Tally.Clear();
        Report(c);
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 365 == 0 || day == days) Report(c);
        }
        return c.Armies.Any(a => !float.IsFinite(a.Organisation)) ? 1 : 0;
    }

    static void Report(Campaign c)
    {
        var live = c.Wars.Where(w => !w.Ended).ToArray();
        var field = c.Armies.Where(a => a.TemplateId != MilitiaCore.TemplateId &&
            !c.NationById(a.NationId).IsHuman && live.Any(w => w.Involves(a.NationId))).ToArray();
        var attacks = field.Count(a => a.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead);
        Console.WriteLine($"DAY {c.ElapsedDays}: wars={live.Length}/{c.Wars.Count} field={field.Length} offensive={attacks} moving={field.Count(a => a.IsMoving)} battle={field.Count(a => a.InBattle)} unassigned={field.Count(a => a.AssignedWarId < 0)}");
        Console.WriteLine(" TALLY " + string.Join(" ", AiStatecraft.Tally.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")));
        foreach (var w in live.Where(w => c.ElapsedDays - w.StartedDay > 90 && w.AttackerLosses + w.DefenderLosses < .001f).Take(12))
        {
            var own = field.Where(a => a.NationId == w.AttackerId).ToArray();
            var attacker = c.NationById(w.AttackerId);
            var defender = c.NationById(w.DefenderId);
            var reachable = AiStatecraft.CanStageWar(c, attacker, defender);
            var counterReachable = AiStatecraft.CanStageWar(c, defender, attacker);
            var border = BorderFronts.Candidates(c, attacker.Id, defender.Id).Count;
            var fleets = c.Fleets.Count(f => f.NationId == attacker.Id && f.Hulls >= c.Defines.AiInvasionMinimumHulls);
            var defenderRegions = c.StatesOf(defender).Length;
            var human = WarWeariness.PlayedByAnyone(c, w);
            var battles = c.Battles.Count(b => !b.Ended && b.WarId == w.Id);
            Console.WriteLine($" QUIET {attacker.Tag}->{defender.Tag} age={c.ElapsedDays-w.StartedDay} fronts={w.AttackerFront.Count}/{w.DefenderFront.Count} border={border} stage={reachable}/{counterReachable} allies={w.BelligerentCount} human={human} occupied={w.OriginalController.Count} battles={battles} unlock={w.AiNegotiationUnlockDay-c.ElapsedDays} fleets={fleets} defender_regions={defenderRegions} landless={defender.Landless} armies={own.Length} duties={string.Join(',', own.GroupBy(a => a.OrderKind).Select(g => $"{g.Key}:{g.Count()}"))}");
        }
    }
}
