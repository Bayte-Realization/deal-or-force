using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// `--movement-births`: when new movements appear in a campaign, and of what kind.
///
/// Written for the owner's question after the landless-movement spam was cut (15 November):
/// "do new movements appear at all now, and when?". A movement is counted as born on the
/// first day it stands - not dissolved - that it did not stand the day before: a new country
/// with a movement, or an old record that comes back to life. Its kind is read from the record
/// itself, the way the rules made it:
///   scripted     - a movement of common/movements woken by pressure (MovementLifecycle);
///   exile        - a fallen state's government abroad (MovementFall.GoIntoExile);
///   underground  - a fallen side gone underground (MovementFall.GoUnderground);
///   kin_proxy    - a movement for a people founded and sponsored by its kin state (KinStatecraft);
///   territorial  - a movement that holds ground the day it appears (a rising or a front).
/// It only reads the world: the campaign runs exactly as without it.
/// </summary>
internal static class MovementBirthReport
{
    static readonly string[] Kinds = { "scripted", "exile", "underground", "kin_proxy", "territorial" };

    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        var standing = new HashSet<int>(Standing(c).Select(n => n.Id));
        var atStart = standing.Count;
        var byYear = new SortedDictionary<int, Dictionary<string, int>>();
        var total = Kinds.ToDictionary(k => k, _ => 0);
        var dissolvedTotal = 0;
        var firstBirthDay = -1;
        var lines = new List<string>();
        Console.WriteLine($"BIRTHS seed={c.Seed} days={days} standing_at_start={atStart}");
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            var now = Standing(c).ToList();
            var nowIds = new HashSet<int>(now.Select(n => n.Id));
            dissolvedTotal += standing.Count(id => !nowIds.Contains(id));
            foreach (var nation in now)
            {
                if (standing.Contains(nation.Id)) continue;
                var kind = Kind(nation);
                var year = 2024 + (day - 1) / 365;
                if (!byYear.TryGetValue(year, out var row)) byYear[year] = row = Kinds.ToDictionary(k => k, _ => 0);
                row[kind]++;
                total[kind]++;
                if (firstBirthDay < 0) firstBirthDay = day;
                if (lines.Count < 400)
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "BIRTH day={0} year={1} kind={2} tag={3} name=\"{4}\" hq={5}",
                        day, year, kind, nation.Tag, nation.DisplayName, nation.Movement.HeadquartersStateMapIndex));
            }
            standing = nowIds;
        }
        foreach (var line in lines) Console.WriteLine(line);
        Console.WriteLine("BIRTHS_COLUMNS year " + string.Join(" ", Kinds) + " all");
        foreach (var (year, row) in byYear)
            Console.WriteLine($"BIRTHS_YEAR {year} " + string.Join(" ", Kinds.Select(k => row[k])) + " " + row.Values.Sum());
        Console.WriteLine("BIRTHS_TOTAL " + string.Join(" ", Kinds.Select(k => $"{k}={total[k]}")) + $" all={total.Values.Sum()}");
        Console.WriteLine($"BIRTHS_END standing_at_end={standing.Count} landless_at_end={Standing(c).Count(n => n.Movement.Landless)} " +
                          $"ended_during_run={dissolvedTotal} first_birth_day={firstBirthDay}");
        return 0;
    }

    static IEnumerable<Nation> Standing(Campaign c) =>
        c.Nations.Where(n => n.Movement is { Dissolved: false });

    static string Kind(Nation nation)
    {
        var state = nation.Movement;
        if (state.Id.StartsWith(MovementCatalog.UndergroundPrefix, StringComparison.Ordinal)) return "underground";
        if (state.Exile) return "exile";
        if (MovementCatalog.Of(state.Id) is not null) return "scripted";
        if (!state.Landless) return "territorial";
        return "kin_proxy";
    }
}
