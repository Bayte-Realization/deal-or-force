using System;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--peace`: what the settlements the world signed actually contained. Every peace that went
/// through the conference is counted by whoever wrote it (ai/human) and by term - land, a
/// released country, a subject of which kind, reparations, trade, demilitarisation, a long truce -
/// and the wars that ended in a capitulation, a status quo or a white peace are listed by how.
/// The question is the user's: does a victor do anything but take land.
/// </summary>
internal static class PeaceReport
{
    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        PeaceConference.Tally.Clear();
        for (var day = 1; day <= days; day++) c.TickDay();
        foreach (var pair in PeaceConference.Tally.OrderBy(p => p.Key))
            Console.WriteLine($"TERM {pair.Key} {pair.Value}");
        var ended = c.Wars.Where(w => w.Ended).ToList();
        Console.WriteLine($"ENDED {ended.Count} wars");
        foreach (var group in ended.GroupBy(w => Kind(w.PeaceSummary)).OrderByDescending(g => g.Count()))
            Console.WriteLine($"ENDING {group.Key} {group.Count()}");
        foreach (var war in ended.Where(w => !w.CivilConflict).OrderBy(w => w.EndedDay).Take(40))
            Console.WriteLine($"PEACE day {war.EndedDay} {c.NationById(war.AttackerId)?.Tag}->{c.NationById(war.DefenderId)?.Tag} winner={c.NationById(war.WinnerId)?.Tag ?? "-"} {war.PeaceSummary}");
        return 0;
    }

    /// <summary>`--navy`: fleets, landings by outcome, and who put an army ashore across water.</summary>
    public static int Navy(Campaign c, int days)
    {
        c.IsPaused = false;
        NavalInvasion.Tally.Clear();
        var startFleets = c.Fleets.Count;
        var startHulls = c.Fleets.Sum(f => f.Hulls);
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 365 != 0) continue;
            Console.WriteLine($"YEAR {day / 365} fleets={c.Fleets.Count} hulls={c.Fleets.Sum(f => f.Hulls)} at_sea={c.Fleets.Count(f => !f.InPort)} " +
                              $"carrying={c.Fleets.Count(f => f.Carried.Count > 0)} " + string.Join(" ", NavalInvasion.Tally.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")));
        }
        Console.WriteLine($"START fleets={startFleets} hulls={startHulls}");
        return 0;
    }

    private static string Kind(string summary)
    {
        summary ??= "";
        if (summary.Contains("capitulated")) return "capitulation";
        if (summary.Contains("deadlock") || summary.Contains("status quo", StringComparison.OrdinalIgnoreCase)) return "status_quo";
        if (summary.Contains("white peace", StringComparison.OrdinalIgnoreCase)) return "white_peace";
        if (summary.Contains("took")) return "land";
        if (summary.Contains("subject", StringComparison.OrdinalIgnoreCase) || summary.Contains("puppet", StringComparison.OrdinalIgnoreCase)) return "subject";
        return summary.Length == 0 ? "none" : "other";
    }
}
