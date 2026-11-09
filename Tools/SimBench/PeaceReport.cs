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
            Console.WriteLine($"YEAR {day / 365} navies={c.Fleets.Select(f => f.NationId).Distinct().Count()} fleets={c.Fleets.Count} hulls={c.Fleets.Sum(f => f.Hulls)} at_sea={c.Fleets.Count(f => !f.InPort)} " +
                              $"carrying={c.Fleets.Count(f => f.Carried.Count > 0)} " + string.Join(" ", NavalInvasion.Tally.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")));
            foreach (var n in c.Nations.Where(n => n.Population > 0 && n.Movement is null).OrderByDescending(n => n.Gdp).Take(14))
            {
                var own = c.Fleets.Where(f => f.NationId == n.Id).ToList();
                Console.WriteLine($"   {n.Tag} gdp={n.Gdp:0} mil={n.MilitarySpending:0.000} treasury={n.Treasury:0} fleets={own.Count} hulls={own.Sum(f => f.Hulls)} ordered={own.Sum(f => f.HullsOrdered)} " +
                                  $"est={own.Sum(f => f.Establishment)} target={own.Sum(f => Shipbuilding.TargetHulls(c, n, f))} ambition={Shipbuilding.Ambition(c, n)} slips={Shipbuilding.Slipways(c, n):0.0} wars={c.Wars.Count(w => !w.Ended && w.Involves(n.Id))} [{string.Join(",", c.Wars.Where(w => !w.Ended && w.Involves(n.Id)).Take(3).Select(w => w.Name))}]");
            }
        }
        Console.WriteLine($"START fleets={startFleets} hulls={startHulls}");
        foreach (var w in c.Wars.Where(w => w.WorldWarDay >= 0))
            Console.WriteLine($"WORLDWAR {w.Name} began day {w.StartedDay} world day {w.WorldWarDay} ended={w.Ended} first={c.NationById(w.AttackerId)?.Tag}-{c.NationById(w.DefenderId)?.Tag} | " +
                              $"A: {string.Join(",", w.Side(true).Select(id => c.NationById(id)?.Tag))} | D: {string.Join(",", w.Side(false).Select(id => c.NationById(id)?.Tag))}");
        return 0;
    }

    /// <summary>`--why TAG`: every half year, a country's stability, its target and each part of it.</summary>
    public static int Why(Campaign c, int days, string tag)
    {
        c.IsPaused = false;
        var nation = c.NationByTag(tag);
        if (nation is null) { Console.WriteLine("no such country " + tag); return 2; }
        var drivers = new Breakdown();
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 182 != 0) continue;
            var target = StabilityCore.Target(c, nation, drivers);
            var weekly = NationalModifiers.Sources(nation)
                .Where(p => p.Modifiers.TryGetValue("stability_weekly", out var v) && v != 0f)
                .Select(p => $"{p.Source}={p.Modifiers["stability_weekly"]:+0.000;-0.000}");
            Console.WriteLine($"WHY {tag} day {day} stability={nation.Stability:0.00} target={target:0.00} war_support={nation.WarSupport:0.00} " +
                              $"overstretch={nation.ImperialOverstretch:0.00} wars={c.Wars.Count(w => !w.Ended && w.Involves(nation.Id))} regions={c.StatesOf(nation).Length}");
            Console.WriteLine("   " + string.Join(" | ", drivers.Entries.Select(e => $"{e.Source} {e.Value:+0.00;-0.00}")));
            Console.WriteLine("   weekly: " + string.Join(" ", weekly));
        }
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
