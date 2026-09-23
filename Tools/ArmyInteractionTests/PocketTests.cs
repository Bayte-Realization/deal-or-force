using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A formation with no way back to its own ground.
///
/// Item 54 on the list: divisions "teleport" when they have no passage to friendly
/// territory. The cases here put a formation inside a pocket - enemy ground on every side -
/// and follow it tick by tick, because a teleport is exactly the thing a snapshot at the end
/// cannot see: the formation arrives somewhere plausible, and the fact that it got there in
/// one jump across twenty provinces is visible only in between.
///
/// What counts as a teleport is any change of province that is not to a neighbour of where
/// it stood, and any change of drawn position larger than a province could explain.
/// </summary>
static class PocketTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        // A campaign of its own. Run at the end of the suite it would otherwise inherit a
        // world the earlier cases have moved borders in, and "two rings deep inside Ukraine"
        // stops meaning anything - the first run found province #2.
        var c = Campaign.CreateBootstrap(20260906);
        c.Defines.HoursPerTick = 24;
        var rus = c.NationByTag("RUS");
        var ukr = c.NationByTag("UKR");
        c.ConfigureSinglePlayer(rus.Id);
        c.AttachProvinceTopology(map);
        var war = c.Wars.First(w => !w.Ended && w.Involves(rus.Id) && w.Involves(ukr.Id));
        c.Wars.RemoveAll(w => w != war);
        c.Battles.Clear();

        var army = c.Armies.First(a => a.NationId == rus.Id);
        c.Armies.Clear();
        c.Armies.Add(army);

        var ctrl = c.ProvinceController;
        // Deep inside Ukraine: every neighbour Ukrainian, and every neighbour's neighbour too,
        // so the formation is two provinces from anything it could call its own.
        var pocket = Enumerable.Range(1, map.ProvinceCount).FirstOrDefault(p =>
            ctrl[p] == ukr.MapIndex && map.Neighbors[p].Length >= 3 &&
            map.Neighbors[p].All(n => ctrl[n] == ukr.MapIndex &&
                                      map.Neighbors[n].All(m => ctrl[m] == ukr.MapIndex)));
        Check(pocket > 0, "found a province two rings deep inside Ukraine");

        var home = Enumerable.Range(1, map.ProvinceCount).First(p =>
            ctrl[p] == rus.MapIndex && map.Neighbors[p].Any(n => ctrl[n] == ukr.MapIndex));

        Place(army, pocket, map);
        army.Divisions = 1; army.Organisation = 1; army.Strength = 1; army.SupplyLevel = 1; army.ReadyDay = 0;

        // --- A: the player orders the trapped formation home ---------------------------
        var order = new MoveArmyCommand { IssuerId = rus.Id, ArmyId = army.Id, TargetProvinceId = home };
        order.Apply(c);
        Console.WriteLine($"  pocket #{pocket}, home #{home}: order result {order.Result}, " +
                          $"planned {army.PlannedRoute.Count} step(s), marching {army.Route.Count}");
        // It used to come back NoLandRoute: the order was classed as a march because the
        // destination is friendly, and a march may only cross friendly ground.
        Check(order.Result == ArmyMoveResult.AttackIssued && army.PlannedRoute.Count > 0,
            "an order home from inside a pocket is accepted as a breakout, not refused for want of a corridor");
        var jumps = Trace(c, army, map, 400, out var visited, out var biggestDegrees);
        Console.WriteLine($"  {visited} province(s) visited, largest single move {biggestDegrees:0.00} deg, " +
                          $"ended at #{army.ProvinceId} ({(ctrl[army.ProvinceId] == rus.MapIndex ? "home ground" : "still enemy ground")})");
        foreach (var jump in jumps.Take(6)) Console.WriteLine("    JUMP " + jump);
        Console.WriteLine($"  state: kind={army.OrderKind} plan={army.PlanActive} direct={army.DirectOrder} " +
                          $"order=#{army.OrderProvinceId} war={army.AssignedWarId} planned=[{string.Join(",", army.PlannedRoute)}] " +
                          $"route=[{string.Join(",", army.Route)}] last={army.LastMoveResult} ready={army.ReadyDay} day={c.ElapsedDays} " +
                          $"battle={army.BattleId} progress={army.MoveProgress:0.00}");
        Check(jumps.Count == 0, "an ordered formation in a pocket never moves except to a neighbour");
        Check(army.ProvinceId == home, "the breakout reaches the province it was ordered to");

        // --- B: nobody orders it; the front planner owns it --------------------------------
        Place(army, pocket, map);
        new HaltArmyCommand { IssuerId = rus.Id, ArmyId = army.Id }.Apply(c);
        army.OrderKind = ArmyOrder.AdvanceFront; army.PlanActive = true; army.AssignedWarId = war.Id;
        army.DirectOrder = false; army.ReadyDay = 0;
        war.FrontDirty = true;
        jumps = Trace(c, army, map, 400, out visited, out biggestDegrees, planner: war);
        Console.WriteLine($"  planner-owned: {visited} province(s) visited, largest move {biggestDegrees:0.00} deg");
        foreach (var jump in jumps.Take(6)) Console.WriteLine("    JUMP " + jump);
        Check(jumps.Count == 0, "a planner-owned formation in a pocket never moves except to a neighbour");

        // --- C: no war, no way home: a foreign enclave -------------------------------------
        var pol = c.NationByTag("POL");
        var enclave = Enumerable.Range(1, map.ProvinceCount).FirstOrDefault(p =>
            ctrl[p] == pol.MapIndex && map.Neighbors[p].Length >= 3 &&
            map.Neighbors[p].All(n => ctrl[n] == pol.MapIndex));
        if (enclave > 0)
        {
            Place(army, enclave, map);
            new HaltArmyCommand { IssuerId = rus.Id, ArmyId = army.Id }.Apply(c);
            var stranded = new MoveArmyCommand { IssuerId = rus.Id, ArmyId = army.Id, TargetProvinceId = home };
            stranded.Apply(c);
            Console.WriteLine($"  enclave #{enclave} in neutral Poland: order result {stranded.Result}");
            jumps = Trace(c, army, map, 60, out visited, out biggestDegrees);
            foreach (var jump in jumps.Take(6)) Console.WriteLine("    JUMP " + jump);
            Check(jumps.Count == 0 && army.ProvinceId == enclave,
                "a formation with no legal way home stays where it is rather than appearing somewhere");
        }
    }

    static void Place(Army army, int province, ProvinceTopology map)
    {
        army.ProvinceId = province;
        army.StateMapIndex = map.ProvinceToState[province];
        (army.Latitude, army.Longitude) = map.CenterOf(province);
        army.Route.Clear(); army.PlannedRoute.Clear(); army.MoveProgress = 0; army.BattleId = -1;
    }

    /// <summary>
    /// Steps the world and reports every move that was not to a neighbour. A retreat or a
    /// capture that moves the formation one province is legitimate; anything further is the
    /// defect this file exists to catch.
    /// </summary>
    static List<string> Trace(Campaign c, Army army, ProvinceTopology map, int steps,
        out int visited, out float biggestDegrees, StrategicWar planner = null)
    {
        var jumps = new List<string>();
        var seen = new HashSet<int> { army.ProvinceId };
        biggestDegrees = 0f;
        var province = army.ProvinceId;
        var (lat, lon) = (army.Latitude, army.Longitude);
        for (var step = 0; step < steps; step++)
        {
            // The subsystem ticks do not move the calendar - the main step does - so the rest
            // a formation takes after a capture would never end here. The rest of this suite
            // stands in for the clock the same way (OffensivePlanTests); what is under test is
            // the path, not how tired the formation is at the end of it.
            army.ReadyDay = 0; army.SupplyLevel = 1; army.Organisation = 1;
            if (planner is not null) FrontPlanner.RefreshAssignments(c, planner);
            MilitaryOperations.Tick(c);
            Warfare.Tick(c);
            if (!c.Armies.Contains(army)) { jumps.Add($"step {step}: formation removed"); break; }
            if (army.ProvinceId == 0) continue; // aboard a ship is not a province
            var moved = MathF.Sqrt((army.Latitude - lat) * (army.Latitude - lat) +
                                   (army.Longitude - lon) * (army.Longitude - lon));
            biggestDegrees = MathF.Max(biggestDegrees, moved);
            if (army.ProvinceId != province && !map.Neighbors[province].Contains(army.ProvinceId))
                jumps.Add($"step {step}: #{province} -> #{army.ProvinceId}, {moved:0.00} deg, not neighbours");
            seen.Add(army.ProvinceId);
            province = army.ProvinceId;
            (lat, lon) = (army.Latitude, army.Longitude);
        }
        visited = seen.Count;
        return jumps;
    }
}
