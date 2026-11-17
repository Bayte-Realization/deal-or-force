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
///
/// After the first slice of the rework (LongRangeMovement, 17 November) it also counts what the
/// movement is supposed to have ended: every day, every formation's position against the day
/// before - a road step to anything but a neighbouring province is a jump, a flight or a sea
/// passage is measured in kilometres a day - the formation-days on the road, deploying and cut
/// off, the computer's relocation orders and how many went toward the front, and the overruns.
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
        LongRangeMovement.AiOrders = LongRangeMovement.AiOrdersTowardFront = LongRangeMovement.AiCityCover = LongRangeMovement.Withdrawals =
            LongRangeMovement.CutOffs = LongRangeMovement.Overruns = 0;
        long transitDays = 0, deployingDays = 0, cutOffDays = 0;
        int landJumps = 0, landSteps = 0;
        float maxLandKm = 0f, maxAirKm = 0f, maxSeaKm = 0f;
        var position = c.SupportFormations.ToDictionary(f => f.Id, f => (Province: LongRangeMovement.At(f), Point: LongRangeMovement.Position(c, f),
            Mode: f.RouteModes.Count > 0 ? f.RouteModes[0] : -1));

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
        var missing = new SortedDictionary<string, int>(StringComparer.Ordinal);
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
                // Off the map is a formation with no province at all; a moving or deploying one has
                // no base for the day but is somewhere, and is counted as that instead.
                if (LongRangeMovement.At(f) <= 0) limboDays++;
                if (f.Route.Count > 0) transitDays++;
                else if (f.DeployDaysLeft > 0) deployingDays++;
                if (f.CutOff) cutOffDays++;
                var here = (Province: LongRangeMovement.At(f), Point: LongRangeMovement.Position(c, f), Mode: f.RouteModes.Count > 0 ? f.RouteModes[0] : -1);
                if (position.TryGetValue(f.Id, out var was) && was.Province > 0 && here.Province > 0)
                {
                    var dayKm = Km(was.Point, here.Point);
                    // By what the formation is, not by the route it had last night: a wing whose
                    // base fell this morning is given its flight and makes it the same day.
                    var flies = SupportTypeCatalog.TryGet(f.TypeId, out var kindOf) && kindOf.Movement == SupportMoveMode.Air;
                    var mode = flies ? SupportMoveMode.Air
                        : (SupportMoveMode)(was.Mode >= 0 ? was.Mode : (int)SupportMoveMode.Land);
                    if (was.Province != here.Province && mode is SupportMoveMode.Land or SupportMoveMode.Rail)
                    {
                        landSteps++;
                        if (!map.Neighbors[was.Province].Contains(here.Province)) landJumps++;
                        maxLandKm = MathF.Max(maxLandKm, dayKm);
                    }
                    else if (mode == SupportMoveMode.Air) maxAirKm = MathF.Max(maxAirKm, dayKm);
                    else if (mode == SupportMoveMode.Sea) maxSeaKm = MathF.Max(maxSeaKm, dayKm);
                }
                position[f.Id] = here;
                if (atWar.Contains(f.NationId))
                {
                    atWarFormationDays++;
                    missionDays[f.Mission] = missionDays.GetValueOrDefault(f.Mission) + 1;
                }
                else peaceFormationDays++;
                if (!last.TryGetValue(f.Id, out var before)) { raised++; continue; }
                // A base set to -1 is a formation packing up for the road, not a jump; a base that
                // appears is one deploying where it arrived. Both are counted by position above.
                if (before.Item2 == f.BaseProvinceId || f.BaseProvinceId <= 0 || before.Item2 <= 0) continue;
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
                // Why a sample does not cover, so the share can be read rather than guessed at.
                string Miss(string why) { missing[why] = missing.GetValueOrDefault(why) + 1; return why; }
                if (f.CutOff) { Miss("cut_off"); continue; }
                if (SupportCore.Displaced(c, f)) { Miss(f.Route.Count > 0 ? "on_the_road" : "deploying"); continue; }
                var covers = false;
                var anyBattle = false;
                foreach (var war in c.Wars)
                {
                    if (war.Ended || !war.Involves(f.NationId) || !battlesByWar.TryGetValue(war.Id, out var battles)) continue;
                    anyBattle = true;
                    if (battles.Any(b => SupportCore.DistanceKm(c, f.BaseProvinceId, b.ProvinceId) <= SupportCore.ReachKm(c, type))) { covers = true; break; }
                }
                if (covers) reachCovering++;
                else if (!anyBattle) Miss("no_battle_in_its_wars");
                else if (type.Kind == SupportKind.AirDefence) Miss("air_defence");
                else if (c.NationById(f.NationId)?.IsHuman == true) Miss("human");
                else Miss("out_of_reach_" + type.Id);
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
        Console.WriteLine($"LONGRANGE_LIMBO formation_days_off_the_map={limboDays} at_end={c.SupportFormations.Count(f => LongRangeMovement.At(f) <= 0)}");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "LONGRANGE_MOVEMENT land_steps={0} land_jumps_beyond_neighbour={1} max_land_day_km={2:0} max_air_day_km={3:0} max_sea_day_km={4:0} " +
            "transit_days={5} deploying_days={6} cut_off_days={7} cut_off_at_end={8}",
            landSteps, landJumps, maxLandKm, maxAirKm, maxSeaKm, transitDays, deployingDays, cutOffDays, c.SupportFormations.Count(f => f.CutOff)));
        foreach (var f in c.SupportFormations.Where(f => f.CutOff).OrderBy(f => f.Id).Take(12))
        {
            var nation = c.NationById(f.NationId);
            var at = LongRangeMovement.At(f);
            var holder = at > 0 ? c.Nations.Find(n => n.MapIndex == (c.ProvinceController ?? map.ProvinceToNation)[at]) : null;
            Console.WriteLine($"LONGRANGE_CUTOFF id={f.Id} nation={nation?.Tag} type={f.TypeId} province={at} held_by={holder?.Tag} " +
                              $"at_war_with_holder={(nation is not null && holder is not null && MilitaryAccess.Hostile(c, nation.Id, holder.Id) ? 1 : 0)} " +
                              $"own_provinces={(nation is null ? 0 : (c.ProvinceController ?? map.ProvinceToNation).Count(o => o == nation.MapIndex))}");
        }
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "LONGRANGE_AI relocation_orders={0} toward_front={1} sam_to_cities={5} withdrawals={2} overruns={3} cut_offs={4}",
            LongRangeMovement.AiOrders, LongRangeMovement.AiOrdersTowardFront, LongRangeMovement.Withdrawals,
            LongRangeMovement.Overruns, LongRangeMovement.CutOffs, LongRangeMovement.AiCityCover));
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
        Console.WriteLine("LONGRANGE_REACH_MISSES " + string.Join(" ", missing.Select(p => $"{p.Key}={p.Value}")));
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

    private static float Km((float Latitude, float Longitude) a, (float Latitude, float Longitude) b)
    {
        var radians = MathF.PI / 180f;
        var dLat = (b.Latitude - a.Latitude) * radians; var dLon = (b.Longitude - a.Longitude) * radians;
        var h = MathF.Sin(dLat / 2) * MathF.Sin(dLat / 2) +
                MathF.Cos(a.Latitude * radians) * MathF.Cos(b.Latitude * radians) * MathF.Sin(dLon / 2) * MathF.Sin(dLon / 2);
        return 6371f * 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(h)));
    }
}
