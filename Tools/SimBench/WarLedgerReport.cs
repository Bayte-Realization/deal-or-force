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
        // The ground of the Russo-Ukrainian war, which a war score cannot show: whose provinces
        // each side's soldiers stand in, on the first day and the last.
        var russia = c.NationByTag("RUS");
        var ukraine = c.NationByTag("UKR");
        (int RusInUkr, int UkrInRus) Ground()
        {
            if (russia is null || ukraine is null || c.ProvinceMap is null || c.ProvinceController is null) return (0, 0);
            var map = c.ProvinceMap;
            int rusInUkr = 0, ukrInRus = 0;
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                var owner = map.ProvinceToNation[province];
                var held = c.ProvinceController[province];
                if (owner == ukraine.MapIndex && held == russia.MapIndex) rusInUkr++;
                if (owner == russia.MapIndex && held == ukraine.MapIndex) ukrInRus++;
            }
            return (rusInUkr, ukrInRus);
        }
        var groundBefore = Ground();
        string Forces(Nation n)
        {
            if (n is null) return "";
            var strength = c.Armies.Where(a => a.NationId == n.Id).Sum(a => a.Divisions * a.Strength);
            var formations = c.Armies.Count(a => a.NationId == n.Id);
            return $"{n.Tag} {formations} formations {strength:0} division-strengths reserve {n.ManpowerReserve:0}";
        }
        var forcesBefore = $"{Forces(russia)} | {Forces(ukraine)}";
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            foreach (var nation in c.Nations) tags.TryAdd(nation.Id, nation.Tag);
        }
        var groundAfter = Ground();
        Console.WriteLine($"FRONT RUS holds {groundBefore.RusInUkr} -> {groundAfter.RusInUkr} Ukrainian provinces, " +
                          $"UKR holds {groundBefore.UkrInRus} -> {groundAfter.UkrInRus} Russian; " +
                          $"forces {forcesBefore} -> {Forces(russia)} | {Forces(ukraine)}");
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
                              $"stage={w.EscalationStage} allies={w.AttackerAllies.Count}/{w.DefenderAllies.Count}[{string.Join(",", w.AttackerAllies.Concat(w.DefenderAllies).Select(id => c.NationById(id)?.Tag))}] " +
                              $"losses={w.AttackerLosses:0.000}/{w.DefenderLosses:0.000} score={w.WarScore:0.00} " +
                              $"winner={winner} \"{w.Name}\" {w.PeaceSummary}");
        }
        // What happened short of wars: incidents, crises, backers (Escalation, ProxyWars).
        var kinds = new[] { "incident", "incident_calm", "tension_crisis", "tension_clashes", "tension_eased",
            "war_major", "war_total", "proxy_started", "proxy_ended", "secession", "secession_contested", "war_started", "border_war", "coup_plot", "coup", "coup_counter", "coup_foiled", "regime_change" };
        Console.WriteLine("CHRONICLE " + string.Join(" ", kinds.Select(k => $"{k}={c.Chronicle.Entries.Count(e => e.Kind == k)}")));
        var shaky = c.Nations.Where(n => n.Movement is null && n.FactionId.Length == 0 && n.Population > 0 && c.StatesOf(n).Length > 0)
            .OrderBy(n => n.Stability).Take(15).Select(n => $"{n.Tag}:{n.Stability:0.00}:{n.Government}");
        Console.WriteLine("LOWEST STABILITY " + string.Join(" ", shaky));
        foreach (var e in c.Chronicle.Entries.Where(e => e.Kind is "coup" or "coup_counter" or "regime_change" or "coup_plot"))
            Console.WriteLine($" POWER {e.Kind} day {e.Day} {c.NationById(e.ActorId)?.Tag} {c.NationById(e.TargetId)?.Tag} stab={c.NationById(e.ActorId)?.Stability:0.00}");
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
