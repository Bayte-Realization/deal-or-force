using System;
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
        return 0;
    }
}
