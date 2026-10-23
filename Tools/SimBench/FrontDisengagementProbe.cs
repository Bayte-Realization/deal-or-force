using System;
using System.Collections.Generic;
using System.Diagnostics;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Bounded daily observation of the real campaign, including failures present with staff withdrawal disabled.</summary>
internal static class FrontDisengagementProbe
{
    private static readonly string[] Checks =
    {
        "front.world.topology", "front.world.day_completed", "front.world.simulation_fault", "front.world.tick_exception",
        "front.army.numeric", "front.army.province", "front.army.route", "front.army.planned_route",
        "front.army.battle_reference", "front.army.unique_id", "front.battle.unique_id", "front.world.front_province",
        "equipment.world.depots"
    };

    public static int Run(Campaign c, int days)
    {
        var requestedDays = Math.Clamp(days, 1, 3650);
        var startedDay = c.ElapsedDays;
        var targetDay = startedDay + requestedDays;
        var startingOrders = FrontDisengagement.Orders;
        var startingRepairs = Warfare.RepairedBattleAssignments;
        var errors = new int[Checks.Length];
        var failures = 0;
        var details = 0;
        var samples = 0;
        var clock = Stopwatch.StartNew();
        // Retain only reusable indexes and scalar totals; never serialize the world per day.
        var armyIds = new HashSet<int>();
        var battles = new Dictionary<int, LandBattle>();
        var occupied = new HashSet<(int Nation, int Province)>();
        var marching = new HashSet<(int Nation, int Province)>();
        var posts = new HashSet<(int Nation, int Province)>();
        long postDays = 0, occupiedDays = 0, marchingDays = 0;
        var ownPosts = 0;
        var ownOccupied = 0;
        var ownMarching = 0;

        void Fail(int check, string detail)
        {
            failures++;
            errors[check]++;
            // Preserve examples of every category even if an unrelated baseline defect repeats.
            if (errors[check] <= 4)
            {
                details++;
                Console.WriteLine($"[FAIL] {Checks[check]} day={c.ElapsedDays} {detail}");
            }
        }

        void Scan()
        {
            var map = c.ProvinceMap;
            if (map is null) { Fail(0, "province topology missing"); return; }
            var controllers = c.ProvinceController ?? map.ProvinceToNation;
            if (controllers.Length <= map.ProvinceCount)
            { Fail(0, $"controllers={controllers.Length} provinces={map.ProvinceCount}"); return; }

            armyIds.Clear();
            battles.Clear();
            occupied.Clear();
            marching.Clear();
            posts.Clear();
            foreach (var battle in c.Battles)
                if (!battles.TryAdd(battle.Id, battle)) Fail(10, $"battle={battle.Id}");

            foreach (var army in c.Armies)
            {
                if (!armyIds.Add(army.Id)) Fail(9, $"army={army.Id} nation={army.NationId}");
                if (!Nonnegative(army.Divisions) || !Nonnegative(army.Organisation) || !Nonnegative(army.Strength) ||
                    army.Personnel < 0 || !Nonnegative(army.Recovering) || !Nonnegative(army.Equipment) || army.Equipment > 1f)
                    Fail(4, $"army={army.Id} nation={army.NationId} divisions={army.Divisions:R} " +
                        $"organisation={army.Organisation:R} strength={army.Strength:R} personnel={army.Personnel} recovering={army.Recovering:R} equipment={army.Equipment:R}");

                var onLand = army.EmbarkedFleetId < 0;
                if (onLand ? !LandProvince(map, army.ProvinceId) : army.ProvinceId != 0)
                    Fail(5, $"army={army.Id} province={army.ProvinceId} fleet={army.EmbarkedFleetId}");
                if (army.OrderProvinceId != -1 && !LandProvince(map, army.OrderProvinceId))
                    Fail(5, $"army={army.Id} order_province={army.OrderProvinceId}");

                if (!ValidRoute(map, army.ProvinceId, army.Route, out var routeError))
                    Fail(6, $"army={army.Id} {routeError}");
                if (!ValidRoute(map, army.ProvinceId, army.PlannedRoute, out var plannedError))
                    Fail(7, $"army={army.Id} {plannedError}");

                if (army.InBattle && (!battles.TryGetValue(army.BattleId, out var battle) || battle.Ended ||
                    (!battle.Attackers.Contains(army.Id) && !battle.Defenders.Contains(army.Id))))
                    Fail(8, $"army={army.Id} nation={army.NationId} battle={army.BattleId} " +
                        $"exists={battle is not null} ended={battle?.Ended} " +
                        $"attacker={battle?.Attackers.Contains(army.Id)} defender={battle?.Defenders.Contains(army.Id)}");

                if (!onLand || !Nonnegative(army.Divisions) || army.Divisions < .1f) continue;
                if (LandProvince(map, army.ProvinceId)) occupied.Add((army.NationId, army.ProvinceId));
                if (!army.InBattle && army.Route.Count > 0 && army.OrderProvinceId == army.Route[^1] &&
                    LandProvince(map, army.OrderProvinceId))
                    marching.Add((army.NationId, army.OrderProvinceId));
            }

            foreach (var nation in c.Nations)
                foreach (var good in StrategicStockpiles.TrackedGoods)
                    if (!Nonnegative(nation.Stockpiles.GetValueOrDefault(good)))
                        Fail(12, $"nation={nation.Tag} good={good} stock={nation.Stockpiles.GetValueOrDefault(good):R}");

            // A coalition's front contains allied ground too. Only a country's own controllers
            // and its own formations count here, with a post shared by wars counted once.
            ownPosts = ownOccupied = ownMarching = 0;
            foreach (var war in c.Wars)
            {
                if (war.Ended) continue;
                foreach (var nation in c.Nations)
                {
                    if (!war.Involves(nation.Id)) continue;
                    foreach (var province in war.FriendlyFront(nation.Id))
                    {
                        if (!LandProvince(map, province))
                        { Fail(11, $"war={war.Id} nation={nation.Id} province={province}"); continue; }
                        var post = (nation.Id, province);
                        if (controllers[province] != nation.MapIndex || !posts.Add(post)) continue;
                        ownPosts++;
                        if (occupied.Contains(post)) ownOccupied++;
                        else if (marching.Contains(post)) ownMarching++;
                    }
                }
            }
            postDays += ownPosts;
            occupiedDays += ownOccupied;
            marchingDays += ownMarching;
            samples++;
        }

        Console.WriteLine($"FRONT_WORLD_START seed={c.Seed} day={startedDay} requested_days={requestedDays} " +
            $"staff_disengages={c.Defines.FrontStaffDisengages}");
        c.IsPaused = false;
        if (c.ProvinceMap is null) Fail(0, "province topology missing");
        else
        {
            var nextProgressDay = startedDay + 90;
            while (c.ElapsedDays < targetDay)
            {
                var before = c.ElapsedDays;
                try { c.TickDay(); c.FinishStep(); }
                catch (Exception exception)
                {
                    Fail(3, $"type={exception.GetType().Name} message={exception.Message}");
                    Console.WriteLine(exception.StackTrace);
                    break;
                }
                if (!string.IsNullOrEmpty(c.SimulationFault)) Fail(2, c.SimulationFault);
                if (c.ElapsedDays <= before)
                { Fail(1, $"before={before} after={c.ElapsedDays} paused={c.IsPaused}"); break; }
                Scan();
                if (c.ElapsedDays >= nextProgressDay || c.ElapsedDays >= targetDay)
                {
                    Console.WriteLine($"FRONT_WORLD_PROGRESS seed={c.Seed} day={c.ElapsedDays} days={c.ElapsedDays - startedDay} " +
                        $"formations={c.Armies.Count} nations={c.Nations.Count} releases={FrontDisengagement.Orders - startingOrders} " +
                        $"own_front_posts={ownPosts} own_front_occupied={ownOccupied} own_front_marching={ownMarching} " +
                        $"own_front_uncovered={ownPosts - ownOccupied - ownMarching} failures={failures} seconds={clock.Elapsed.TotalSeconds:0.0}");
                    nextProgressDay = c.ElapsedDays + 90;
                }
                if (!string.IsNullOrEmpty(c.SimulationFault)) break;
            }
        }
        if (failures > details) Console.WriteLine($"FRONT_WORLD_DETAILS omitted={failures - details}");
        for (var check = 0; check < Checks.Length; check++)
            Console.WriteLine($"{(errors[check] == 0 ? "[ ok ]" : "[FAIL]")} {Checks[check]} occurrences={errors[check]}");
        Console.WriteLine($"FRONT_WORLD_COVERAGE samples={samples} own_front_post_days={postDays} " +
            $"own_front_occupied_days={occupiedDays} own_front_marching_days={marchingDays} " +
            $"own_front_uncovered_days={postDays - occupiedDays - marchingDays} " +
            $"covered_share={(postDays == 0 ? 0 : (occupiedDays + marchingDays) / (double)postDays):0.000000}");
        Console.WriteLine($"FRONT_WORLD failures={failures} releases={FrontDisengagement.Orders - startingOrders} " +
            $"repaired_assignments={Warfare.RepairedBattleAssignments - startingRepairs} " +
            $"formations={c.Armies.Count} nations={c.Nations.Count} days={c.ElapsedDays - startedDay} " +
            $"seed={c.Seed} samples={samples} seconds={clock.Elapsed.TotalSeconds:0.0}");
        foreach (var nation in c.Nations)
            if (nation.FormedNationDay > startedDay && nation.FormedNationDay <= c.ElapsedDays &&
                !string.IsNullOrEmpty(nation.FormedNationId))
                Console.WriteLine($"WORLD_PROCLAMATION nation={nation.Tag} project={nation.FormedNationId} day={nation.FormedNationDay}");
        return failures == 0 ? 0 : 1;
    }

    private static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0f;

    private static bool LandProvince(ProvinceTopology map, int province) =>
        province > 0 && province <= map.ProvinceCount && !map.IsWater(province);

    private static bool ValidRoute(ProvinceTopology map, int from, List<int> route, out string error)
    {
        error = null;
        if (route.Count == 0) return true;
        if (!LandProvince(map, from)) { error = $"from={from} steps={route.Count}"; return false; }
        for (var index = 0; index < route.Count; index++)
        {
            var next = route[index];
            if (!LandProvince(map, next)) { error = $"index={index} province={next} steps={route.Count}"; return false; }
            var adjacent = false;
            foreach (var neighbor in map.Neighbors[from])
                if (neighbor == next) { adjacent = true; break; }
            if (!adjacent) { error = $"index={index} nonadjacent={from}->{next} steps={route.Count}"; return false; }
            from = next;
        }
        return true;
    }
}
