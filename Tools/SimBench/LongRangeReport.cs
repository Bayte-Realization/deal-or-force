using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// `--long-range`: what the world does with its long-range forces when nobody is playing.
///
/// Written for the owner's complaint of 17 November - "they move instantly, through the air,
/// without a trace; strange limits; detached from the game". It counts, day by day:
///   - how many formations there are, of what kind, how many belong to countries at war;
///   - every change of base, and of which sort: an ordered rebase (the base still held) or an
///     evacuation (the base lost - SupportRepatriation), how far it jumped in one day, and
///     whether a march over the country's own ground could have made the trip at all - the
///     "through the air" the owner saw;
///   - formations parked off the map with no base at all;
///   - which missions the at-war formations fly, and whether their reach covers any battle of
///     their own wars (sampled monthly) - "where they stand" against "where the war is";
///   - the salvos (LongRangeStrikes telemetry), the formations raised and lost, readiness at
///     the attrition floor;
///   - how many battle-days had any long-range support on either side, and how much.
/// It only reads the world: the campaign runs exactly as without it.
/// </summary>
internal static class LongRangeReport
{
    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        var map = c.ProvinceMap;
        LongRangeStrikes.Salvos = LongRangeStrikes.DryLaunches = LongRangeStrikes.Procured = 0;
        LongRangeStrikes.LaunchedPoints = LongRangeStrikes.InterceptedPoints = LongRangeStrikes.LandedPoints = 0f;
        LongRangeStrikes.ByTarget.Clear();

        var startIds = new HashSet<int>(c.SupportFormations.Select(f => f.Id));
        Console.WriteLine($"LONGRANGE seed={c.Seed} days={days} formations_at_start={c.SupportFormations.Count} " +
                          $"nations_with_any={c.SupportFormations.Select(f => f.NationId).Distinct().Count()}");
        foreach (var group in c.SupportFormations.GroupBy(f => f.TypeId).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"LONGRANGE_START_TYPE {group.Key} {group.Count()}");

        var last = c.SupportFormations.ToDictionary(f => f.Id, f => (f.StateMapIndex, f.BaseProvinceId, f.NationId));
        int ordered = 0, evacuated = 0, toLimbo = 0, noLandRoute = 0, over500 = 0, raised = 0, lost = 0;
        var jumpKm = new List<float>();
        var missionDays = new Dictionary<string, long>(StringComparer.Ordinal);
        long atWarFormationDays = 0, peaceFormationDays = 0, limboDays = 0;
        long battleDays = 0, battleDaysWithSupport = 0;
        double supportPoints = 0;
        long reachSamples = 0, reachCovering = 0;
        var lines = new List<string>();
        var byYear = new SortedDictionary<int, int[]>();   // formations, at war, evacuations, ordered, salvos

        for (var day = 1; day <= days; day++)
        {
            var salvosBefore = LongRangeStrikes.Salvos;
            c.TickDay();
            var year = 2024 + (day - 1) / 365;
            if (!byYear.TryGetValue(year, out var row)) byYear[year] = row = new int[5];
            row[4] += LongRangeStrikes.Salvos - salvosBefore;

            var atWar = new HashSet<int>();
            foreach (var war in c.Wars)
            {
                if (war.Ended) continue;
                foreach (var id in war.Side(true)) atWar.Add(id);
                foreach (var id in war.Side(false)) atWar.Add(id);
            }
            var controllers = c.ProvinceController ?? map.ProvinceToNation;
            var now = new Dictionary<int, (int, int, int)>();
            foreach (var f in c.SupportFormations)
            {
                now[f.Id] = (f.StateMapIndex, f.BaseProvinceId, f.NationId);
                if (f.BaseProvinceId <= 0) limboDays++;
                if (atWar.Contains(f.NationId))
                {
                    atWarFormationDays++;
                    missionDays[f.Mission] = missionDays.GetValueOrDefault(f.Mission) + 1;
                }
                else peaceFormationDays++;
                if (!last.TryGetValue(f.Id, out var before)) { raised++; continue; }
                if (before.Item2 == f.BaseProvinceId) continue;
                var nation = c.NationById(f.NationId);
                var baseHeld = before.Item2 > 0 && before.Item2 <= map.ProvinceCount && nation is not null &&
                               controllers[before.Item2] == nation.MapIndex;
                if (f.BaseProvinceId <= 0) { toLimbo++; evacuated++; row[2]++; continue; }
                if (baseHeld) { ordered++; row[3]++; } else { evacuated++; row[2]++; }
                if (before.Item2 <= 0) continue;
                var km = SupportCore.DistanceKm(c, before.Item2, f.BaseProvinceId);
                jumpKm.Add(km);
                if (km > 500f) over500++;
                // The march the owner expected: over the country's own ground, start to end.
                // The old base may be lost, so the start is allowed to be uncontrolled.
                var route = nation is null ? Array.Empty<int>()
                    : map.FindRoute(before.Item2, f.BaseProvinceId, (ushort)nation.MapIndex, controllers, allowUncontrolledStart: true);
                var landed = route.Length > 0;
                if (!landed) noLandRoute++;
                if (lines.Count < 60)
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "LONGRANGE_MOVE day={0} id={1} nation={2} type={3} from={4} to={5} km={6:0} kind={7} land_route={8}",
                        day, f.Id, nation?.Tag, f.TypeId, before.Item2, f.BaseProvinceId, km, baseHeld ? "ordered" : "evacuated",
                        landed ? "yes" : "no"));
            }
            foreach (var id in last.Keys) if (!now.ContainsKey(id)) lost++;
            last = now;
            row[0] = c.SupportFormations.Count;
            row[1] = c.SupportFormations.Count(f => atWar.Contains(f.NationId));

            foreach (var battle in c.Battles)
            {
                if (battle.Ended) continue;
                battleDays++;
                var points = battle.AttackerSupport + battle.DefenderSupport;
                if (points > 0f) { battleDaysWithSupport++; supportPoints += points; }
            }

            if (day % 30 != 0) continue;
            // Monthly: does the formation's reach cover any battle of a war its country is in?
            var battlesByWar = c.Battles.Where(b => !b.Ended).GroupBy(b => b.WarId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var f in c.SupportFormations)
            {
                if (!atWar.Contains(f.NationId) || !SupportTypeCatalog.TryGet(f.TypeId, out var type)) continue;
                reachSamples++;
                if (SupportCore.Displaced(c, f)) continue;
                var covers = false;
                foreach (var war in c.Wars)
                {
                    if (war.Ended || !war.Involves(f.NationId) || !battlesByWar.TryGetValue(war.Id, out var battles)) continue;
                    if (battles.Any(b => SupportCore.DistanceKm(c, f.BaseProvinceId, b.ProvinceId) <= type.RangeKm)) { covers = true; break; }
                }
                if (covers) reachCovering++;
            }
        }

        foreach (var line in lines) Console.WriteLine(line);
        Console.WriteLine("LONGRANGE_YEAR_COLUMNS year formations at_war evacuations ordered_rebases salvos");
        foreach (var (year, row) in byYear) Console.WriteLine($"LONGRANGE_YEAR {year} {string.Join(" ", row)}");
        jumpKm.Sort();
        float Pct(float p) => jumpKm.Count == 0 ? 0f : jumpKm[Math.Min(jumpKm.Count - 1, (int)(p * jumpKm.Count))];
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "LONGRANGE_MOVES ordered={0} evacuated={1} to_limbo={2} jumps_measured={3} median_km={4:0} p90_km={5:0} max_km={6:0} over_500km={7} no_land_route={8}",
            ordered, evacuated, toLimbo, jumpKm.Count, Pct(.5f), Pct(.9f), jumpKm.Count == 0 ? 0 : jumpKm[^1], over500, noLandRoute));
        Console.WriteLine($"LONGRANGE_LIMBO formation_days_without_base={limboDays} at_end={c.SupportFormations.Count(f => f.BaseProvinceId <= 0)}");
        Console.WriteLine($"LONGRANGE_FORCE raised={raised} ai_procured={LongRangeStrikes.Procured} lost={lost} at_end={c.SupportFormations.Count} " +
                          $"started_and_survived={c.SupportFormations.Count(f => startIds.Contains(f.Id))}");
        var atWarEnd = c.SupportFormations.Where(f => c.Wars.Any(w => !w.Ended && w.Involves(f.NationId))).ToList();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "LONGRANGE_READINESS at_war_end={0} mean_strength={1:0.00} at_floor={2}",
            atWarEnd.Count, atWarEnd.Count == 0 ? 0 : atWarEnd.Average(f => f.Strength), atWarEnd.Count(f => f.Strength <= c.Defines.SupportAttritionFloor + 0.01f)));
        Console.WriteLine("LONGRANGE_MISSIONS_AT_WAR formation_days=" + atWarFormationDays + " peace_formation_days=" + peaceFormationDays + " " +
                          string.Join(" ", missionDays.OrderByDescending(p => p.Value).Select(p =>
                              string.Format(CultureInfo.InvariantCulture, "{0}={1:0.0}%", p.Key, 100.0 * p.Value / Math.Max(1, atWarFormationDays)))));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "LONGRANGE_REACH monthly_samples={0} covering_a_battle_of_own_war={1} share={2:0.0}%",
            reachSamples, reachCovering, 100.0 * reachCovering / Math.Max(1, reachSamples)));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "LONGRANGE_STRIKES salvos={0} dry={1} launched={2:0} intercepted={3:0} landed={4:0} {5}",
            LongRangeStrikes.Salvos, LongRangeStrikes.DryLaunches, LongRangeStrikes.LaunchedPoints,
            LongRangeStrikes.InterceptedPoints, LongRangeStrikes.LandedPoints,
            string.Join(" ", LongRangeStrikes.ByTarget.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"))));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "LONGRANGE_BATTLES battle_days={0} with_support={1} share={2:0.0}% mean_points_when_present={3:0.0}",
            battleDays, battleDaysWithSupport, 100.0 * battleDaysWithSupport / Math.Max(1, battleDays),
            battleDaysWithSupport == 0 ? 0 : supportPoints / battleDaysWithSupport));
        foreach (var group in c.SupportFormations.GroupBy(f => f.NationId)
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(10))
        {
            var nation = c.NationById(group.Key);
            Console.WriteLine($"LONGRANGE_TOP {nation?.Tag} count={group.Count()} regions={group.Select(f => f.StateMapIndex).Distinct().Count()} " +
                              $"at_war={(c.Wars.Any(w => !w.Ended && w.Involves(group.Key)) ? 1 : 0)} " +
                              string.Join(",", group.GroupBy(f => f.Mission).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}")));
        }
        return 0;
    }
}
