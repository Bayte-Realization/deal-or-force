using System;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Why a war is still on the map after a year.
///
/// The simulation already writes down what its last attempt at peace came to (WarCore.LastNegotiation)
/// and whether an offer is sitting unanswered. Ten years of the bench showed wars at a hundred
/// points - decided by every measure the game keeps - running for five hundred days; this prints the
/// war's own account of why, instead of guessing from the score.
/// </summary>
internal static class WarStuckReport
{
    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (c.SimulationFault.Length > 0)
            {
                Console.WriteLine($"WARS_FAULT day={c.ElapsedDays} fault={c.SimulationFault}");
                return 1;
            }
            if (day % 90 == 0 || day == days) Report(c);
        }
        return 0;
    }

    private static void Report(Campaign c)
    {
        var open = c.Wars.Count(w => !w.Ended);
        var old = c.Wars.Where(w => !w.Ended && c.ElapsedDays - w.StartedDay >= 365)
            .OrderByDescending(w => c.ElapsedDays - w.StartedDay).Take(6).ToArray();
        Console.WriteLine($"DAY {c.ElapsedDays}: open={open} older_than_a_year={old.Length}");
        foreach (var war in old)
        {
            Console.WriteLine($"  WAR {c.NationById(war.AttackerId)?.Tag}->{c.NationById(war.DefenderId)?.Tag} " +
                              $"civil={(war.CivilConflict ? 1 : 0)} " +
                              $"age={c.ElapsedDays - war.StartedDay} score={war.WarScore:0.0} " +
                              $"attOcc={war.AttackerOccupiedValue:0.00} defOcc={war.DefenderOccupiedValue:0.00} " +
                              $"pending={war.PendingOfferFromId} lastProposal={war.LastPeaceProposalDay} " +
                              $"stalled={(war.Stalled ? 1 : 0)} last=[{war.LastNegotiation}]");
            // The numbers the decision is actually made of, so a war that never speaks can be told
            // apart from one that speaks and is refused.
            var a = c.NationById(war.AttackerId);
            var d = c.NationById(war.DefenderId);
            if (a is null || d is null) continue;
            Console.WriteLine($"    WHY budgetA={PeaceConference.Budget(c, war, a.Id):0.0} " +
                              $"floorA={WarAims.PeaceFloor(c, a):0.0} budgetD={PeaceConference.Budget(c, war, d.Id):0.0} " +
                              $"floorD={WarAims.PeaceFloor(c, d):0.0} stopA={WarWeariness.TimeToStop(c, war, true)} " +
                              $"stopD={WarWeariness.TimeToStop(c, war, false)} sinceLast={c.ElapsedDays - war.LastPeaceProposalDay} " +
                              $"interval={c.Defines.AiPeaceProposalInterval} minDays={c.Defines.AiMinimumWarDays} " +
                              $"acceptWhiteA={PeaceNegotiation.Acceptance(c, war, a.Id, PeaceTerms.WhitePeace):0.0} " +
                              $"acceptWhiteD={PeaceNegotiation.Acceptance(c, war, d.Id, PeaceTerms.WhitePeace):0.0} " +
                              $"acceptStatusA={PeaceNegotiation.Acceptance(c, war, a.Id, PeaceTerms.StatusQuo):0.0} " +
                              $"acceptStatusD={PeaceNegotiation.Acceptance(c, war, d.Id, PeaceTerms.StatusQuo):0.0} " +
                              $"supportA={a.WarSupport:0.00} supportD={d.WarSupport:0.00}");
        }
    }
}
