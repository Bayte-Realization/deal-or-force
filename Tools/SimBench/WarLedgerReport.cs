using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--war-ledger [TAG]` plays the days and then lists every war the world fought: who,
/// whether it was a civil war, when it began and ended, how long it lasted and how it ended.
/// With a tag, only the wars that country or a country born of it took part in. It answers
/// "do the wars last" with lengths rather than impressions.
/// </summary>
internal static class WarLedgerReport
{
    public static int Run(Campaign c, int days, string tag)
    {
        c.IsPaused = false;
        var tags = c.Nations.ToDictionary(n => n.Id, n => n.Tag);
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            foreach (var nation in c.Nations) tags.TryAdd(nation.Id, nation.Tag);
        }
        string Tag(int id) => tags.TryGetValue(id, out var t) ? t : $"#{id}";
        bool Concerns(StrategicWar w) => string.IsNullOrEmpty(tag) ||
            new[] { w.AttackerId, w.DefenderId }.Concat(w.AttackerAllies).Concat(w.DefenderAllies)
                .Any(id => Tag(id).Equals(tag, StringComparison.OrdinalIgnoreCase)) ||
            w.Name.Contains(tag, StringComparison.OrdinalIgnoreCase);
        var wars = c.Wars.Where(Concerns).OrderBy(w => w.StartedDay).ToArray();
        var ended = wars.Where(w => w.Ended).ToArray();
        Console.WriteLine($"WARS {wars.Length} ({ended.Length} ended) over {days} days" + (string.IsNullOrEmpty(tag) ? "" : $" touching {tag}"));
        if (ended.Length > 0)
        {
            var lengths = ended.Select(w => w.EndedDay - w.StartedDay).OrderBy(d => d).ToArray();
            Console.WriteLine($"ENDED median={lengths[lengths.Length / 2]}d mean={lengths.Average():0}d " +
                              $"under90={lengths.Count(d => d < 90)} under365={lengths.Count(d => d < 365)}");
            var civil = ended.Where(w => w.CivilConflict).Select(w => w.EndedDay - w.StartedDay).OrderBy(d => d).ToArray();
            if (civil.Length > 0)
                Console.WriteLine($"CIVIL ended={civil.Length} median={civil[civil.Length / 2]}d mean={civil.Average():0}d under180={civil.Count(d => d < 180)}");
        }
        foreach (var w in wars)
        {
            var length = (w.Ended ? w.EndedDay : c.ElapsedDays) - w.StartedDay;
            var winner = w.WinnerId >= 0 ? Tag(w.WinnerId) : "-";
            Console.WriteLine($" {(w.CivilConflict ? "CIVIL" : "WAR  ")} {Tag(w.AttackerId)}->{Tag(w.DefenderId)} " +
                              $"day {w.StartedDay}-{(w.Ended ? w.EndedDay.ToString() : "...")} ({length}d) " +
                              $"stage={w.EscalationStage} allies={w.AttackerAllies.Count}/{w.DefenderAllies.Count} " +
                              $"losses={w.AttackerLosses:0.000}/{w.DefenderLosses:0.000} score={w.WarScore:0.00} " +
                              $"winner={winner} \"{w.Name}\" {w.PeaceSummary}");
        }
        // What happened short of wars: incidents, crises, backers (Escalation, ProxyWars).
        var kinds = new[] { "incident", "incident_calm", "tension_crisis", "tension_clashes", "tension_eased",
            "war_major", "war_total", "proxy_started", "proxy_ended", "secession", "secession_contested", "war_started" };
        Console.WriteLine("CHRONICLE " + string.Join(" ", kinds.Select(k => $"{k}={c.Chronicle.Entries.Count(e => e.Kind == k)}")));
        foreach (var s in c.Sponsorships.Where(s => !s.Ended).OrderByDescending(s => s.Delivered).Take(20))
        {
            var war = c.Wars.FirstOrDefault(w => w.Id == s.WarId);
            var sponsor = c.NationById(s.SponsorId);
            var client = c.NationById(s.ClientId);
            var enemy = war is null ? null : c.NationById(war.EnemyLeaderOf(s.ClientId));
            var why = war is null || sponsor is null || client is null || enemy is null ? "" :
                $" stake={ProxyWars.HasStake(c, sponsor, client, enemy, war)} interest={ProxyWars.Interest(c, sponsor, client, enemy, war):0.00} gdp={sponsor.Gdp:0}";
            Console.WriteLine($" BACKING {Tag(s.SponsorId)}->{Tag(s.ClientId)} level {s.Level} since day {s.StartedDay} sent ${s.Delivered:0.0}B war {war?.Name}{why}");
        }
        foreach (var m in c.Nations.Where(n => n.Movement is not null).OrderBy(n => n.Tag))
        {
            var s = m.Movement;
            var top = s.Support.Count == 0 ? 0f : s.Support.Values.Max();
            var best = c.States.Where(r => s.Support.GetValueOrDefault(r.MapIndex) > 0.05f)
                .Select(r => (Region: r, Chance: MovementRules.UprisingChance(c, m, r)))
                .OrderByDescending(x => x.Chance).FirstOrDefault();
            Console.WriteLine($" MOVEMENT {m.Tag} landless={s.Landless} dissolved={s.Dissolved} cadres={s.Cadres:0} support_max={top:0.00} heat={s.Heat:0.00} regions={c.StatesOf(m).Length} treasury={m.Treasury:0.00} best_rising={best.Region?.Id}:{best.Chance:0.00} last=\"{s.LastReport}\"");
        }
        var hot = Escalation.Hottest(c, 49f).Take(15).ToArray();
        foreach (var r in hot)
            Console.WriteLine($" TENSION {Tag(r.A)}-{Tag(r.B)} {r.Tension:0} {Escalation.StageOf(r)} grievance {r.GrievanceA:0}/{r.GrievanceB:0} clashes {r.ClashLosses:0}");
        return 0;
    }
}
