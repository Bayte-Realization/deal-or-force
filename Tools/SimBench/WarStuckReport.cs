using System;
using System.Collections.Generic;
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
        var tally = new Tally(c);
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (c.SimulationFault.Length > 0)
            {
                Console.WriteLine($"WARS_FAULT day={c.ElapsedDays} fault={c.SimulationFault}");
                return 1;
            }
            tally.Observe(c);
            if (day % 90 == 0 || day == days) Report(c);
        }
        tally.Print(c);
        return 0;
    }

    /// <summary>
    /// The whole run in one line, for an A/B: wars begun and ended, how long the ended ones lasted,
    /// how many ended inside a month (a blitz) and how many are over a year old (a freeze), and how
    /// often ground changed hands between countries at war. Wars are followed by id, because an
    /// ended war may be dropped from the list before the run is over.
    /// </summary>
    private sealed class Tally
    {
        private readonly Dictionary<int, int> started = new();
        private readonly Dictionary<int, int> ended = new();
        private readonly HashSet<int> initial = new();
        private ushort[] control;
        private long changes;

        public Tally(Campaign c)
        {
            control = (ushort[])c.ProvinceController?.Clone();
            foreach (var war in c.Wars) if (!war.Ended) { initial.Add(war.Id); started[war.Id] = war.StartedDay; }
        }

        public void Observe(Campaign c)
        {
            var live = new HashSet<int>();
            foreach (var war in c.Wars)
            {
                if (!started.ContainsKey(war.Id)) started[war.Id] = war.StartedDay;
                if (war.Ended) { if (!ended.ContainsKey(war.Id)) ended[war.Id] = war.EndedDay >= 0 ? war.EndedDay : c.ElapsedDays; }
                else live.Add(war.Id);
            }
            foreach (var id in started.Keys)
                if (!live.Contains(id) && !ended.ContainsKey(id)) ended[id] = c.ElapsedDays;
            var now = c.ProvinceController;
            if (now is null || control is null || now.Length != control.Length) { control = (ushort[])now?.Clone(); return; }
            for (var p = 1; p < now.Length; p++)
                if (now[p] != control[p]) { changes++; control[p] = now[p]; }
        }

        public void Print(Campaign c)
        {
            var lengths = ended.Select(kv => kv.Value - started[kv.Key]).OrderBy(x => x).ToArray();
            var open = started.Keys.Where(id => !ended.ContainsKey(id)).Select(id => c.ElapsedDays - started[id]).ToArray();
            Console.WriteLine($"WARS_SUMMARY days={c.ElapsedDays} begun={started.Count - initial.Count} initial={initial.Count} " +
                $"ended={lengths.Length} median_days={(lengths.Length > 0 ? lengths[lengths.Length / 2] : 0)} " +
                $"ended_within_30={lengths.Count(x => x <= 30)} ended_within_90={lengths.Count(x => x <= 90)} " +
                $"open={open.Length} open_over_year={open.Count(x => x >= 365)} control_changes={changes}");
        }
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
            // What the score is made of, and what the war has cost each side.
            Console.WriteLine($"    PARTS occ={war.OccupationScore:0.0} cap={war.CapitalScore:0.0} army={war.ArmyScore:0.0} " +
                              $"battle={war.BattleScore:0.0} goal={war.ObjectiveScore:0.0} lossA={war.AttackerLosses:0} lossD={war.DefenderLosses:0} " +
                              $"leftA={war.AttackerStrengthLeft:0.00} leftD={war.DefenderStrengthLeft:0.00}");
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
