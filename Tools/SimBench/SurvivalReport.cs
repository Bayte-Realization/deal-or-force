using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--survival`: what a country losing a war does about it. Every war of the run is watched
/// weekly: how much of the losing leader's own ground fell, who came to either side after the
/// first week, what was asked for and answered (interventions, aid, arms), and how it ended.
/// The question is the user's: does a government in trouble use what it has to stay alive.
/// </summary>
internal static class SurvivalReport
{
    sealed class Watch
    {
        public StrategicWar War;
        public int AttackerHome, DefenderHome;
        public int AttackersAtStart, DefendersAtStart;
        public float WorstAttackerLoss, WorstDefenderLoss;
        public readonly HashSet<int> JoinedAttackers = new(), JoinedDefenders = new();
    }

    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        AiStatecraft.Tally.Clear();
        var watched = new Dictionary<int, Watch>();
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 7 != 0 && day != days) continue;
            foreach (var war in c.Wars)
            {
                if (!watched.TryGetValue(war.Id, out var w))
                {
                    if (war.Ended) continue;
                    watched[war.Id] = w = new Watch
                    {
                        War = war,
                        AttackerHome = Home(c, war.AttackerId) + Lost(war, c.NationById(war.AttackerId)),
                        DefenderHome = Home(c, war.DefenderId) + Lost(war, c.NationById(war.DefenderId)),
                        AttackersAtStart = war.Side(true).Count(),
                        DefendersAtStart = war.Side(false).Count(),
                    };
                    if (c.ElapsedDays - war.StartedDay > 7) w.AttackersAtStart = w.DefendersAtStart = -1; // seen late
                }
                if (war.Ended) continue;
                w.WorstAttackerLoss = MathF.Max(w.WorstAttackerLoss, Share(war, c.NationById(war.AttackerId), w.AttackerHome));
                w.WorstDefenderLoss = MathF.Max(w.WorstDefenderLoss, Share(war, c.NationById(war.DefenderId), w.DefenderHome));
                if (c.ElapsedDays - war.StartedDay > 7)
                {
                    foreach (var id in war.Side(true)) if (id != war.AttackerId) w.JoinedAttackers.Add(id);
                    foreach (var id in war.Side(false)) if (id != war.DefenderId) w.JoinedDefenders.Add(id);
                }
            }
        }

        var losing = 0; var helped = 0; var survived = 0;
        foreach (var w in watched.Values.OrderBy(w => w.War.Id))
        {
            var war = w.War;
            var a = c.NationById(war.AttackerId); var d = c.NationById(war.DefenderId);
            var worst = MathF.Max(w.WorstAttackerLoss, w.WorstDefenderLoss);
            var loserIsDefender = w.WorstDefenderLoss >= w.WorstAttackerLoss;
            if (worst >= 0.25f)
            {
                losing++;
                var cameLate = loserIsDefender ? w.JoinedDefenders.Count - Math.Max(0, w.DefendersAtStart - 1)
                                               : w.JoinedAttackers.Count - Math.Max(0, w.AttackersAtStart - 1);
                if (cameLate > 0) helped++;
                if (war.Ended && war.WinnerId != (loserIsDefender ? war.AttackerId : war.DefenderId)) survived++;
            }
            if (worst < 0.10f && war.Ended) continue; // a war nobody lost much in is not this report's business
            Console.WriteLine(
                $"WAR {war.Id} {a?.Tag}->{d?.Tag} day={war.StartedDay} age={(war.Ended ? war.EndedDay : c.ElapsedDays) - war.StartedDay} " +
                $"civil={war.CivilConflict} lost_att={w.WorstAttackerLoss:0.00} lost_def={w.WorstDefenderLoss:0.00} " +
                $"sides={w.AttackersAtStart}/{w.DefendersAtStart} joined_att={w.JoinedAttackers.Count} joined_def={w.JoinedDefenders.Count} " +
                $"end={(war.Ended ? c.NationById(war.WinnerId)?.Tag ?? "none" : "live")} {war.PeaceSummary}" +
                (w.JoinedAttackers.Count + w.JoinedDefenders.Count > 0 ? $" ATT[{string.Join(",", w.JoinedAttackers.Select(id => c.NationById(id)?.Tag))}] DEF[{string.Join(",", w.JoinedDefenders.Select(id => c.NationById(id)?.Tag))}]" : ""));
        }

        string States<T>(IEnumerable<T> items, Func<T, string> key) =>
            string.Join(" ", items.GroupBy(key).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));
        Console.WriteLine($"LOSING wars={losing} helped_late={helped} not_lost={survived} of {watched.Count} watched");
        Console.WriteLine("INTERVENTIONS " + States(c.InterventionRequests,
            r => (c.NationById(r.DonorId)?.IsHuman == true ? "to_human_" : "to_ai_") + r.State));
        Console.WriteLine("AID " + States(c.AssistanceRequests, r => r.State.ToString()));
        Console.WriteLine("ARMS " + States(c.ArmsShipmentRequests, r => r.State.ToString()));
        Console.WriteLine(" TALLY " + string.Join(" ", AiStatecraft.Tally.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")));
        return 0;
    }

    static int Home(Campaign c, int nationId)
    {
        var nation = c.NationById(nationId);
        var control = c.ProvinceController;
        if (nation is null || control is null) return 0;
        var own = (ushort)nation.MapIndex;
        var count = 0;
        for (var p = 1; p < control.Length; p++) if (control[p] == own) count++;
        return count;
    }

    static int Lost(StrategicWar war, Nation nation)
    {
        if (nation is null) return 0;
        var own = (ushort)nation.MapIndex;
        return war.OriginalController.Count(p => p.Value == own);
    }

    static float Share(StrategicWar war, Nation nation, int home) =>
        home <= 0 ? 0f : Lost(war, nation) / (float)home;
}
