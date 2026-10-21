using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool FrontStaffDisengages { get; set; } = true;
    public int FrontDisengagementMinimumBattleDays { get; set; } = 2;
    public float FrontDisengagementMaximumShare { get; set; } = .35f;
    public float FrontDisengagementDefenceRatio { get; set; } = .8f;
}

/// <summary>Release a reserve from a pinned surplus, never the whole covering force.
/// Called only for an empty post, after free troops and reserves have been considered.</summary>
public static class FrontDisengagement
{
    public static int Orders { get; private set; }

    internal static bool Eligible(Campaign c, Nation nation, StrategicWar war, DrawnFrontLine line, Army army)
    {
        if (!c.Defines.FrontStaffDisengages || war is null || war.Ended || army.NationId != nation.Id ||
            !army.InBattle || army.DirectOrder || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 ||
            army.ReadyDay > c.ElapsedDays || army.OrderKind is not (ArmyOrder.DefendFront or ArmyOrder.AdvanceFront)) return false;
        if (line is not null)
            return line.Cohesion != PlanCohesion.Immobile && army.FrontLineId == line.Id;
        return army.FrontLineId < 0 && army.AssignedWarId == war.Id &&
            (army.OrderKind == ArmyOrder.DefendFront || !nation.IsHuman || Delegation.Holds(nation, Delegation.Command));
    }

    internal static bool TryCoverHole(Campaign c, Nation nation, StrategicWar war, DrawnFrontLine line,
        int hole, out Army moving, IReadOnlyList<Army> candidates = null)
    {
        moving = null;
        if (!c.Defines.FrontStaffDisengages || war is null || war.Ended ||
            !WarCoalition.SafeRetreatProvince(c, war, nation.Id, hole)) return false;
        // An assault's departure tile is already covered by its fighting troops, even
        // when advances are deliberately excluded from routine defensive redistribution.
        foreach (var unit in c.Armies)
            if (unit.NationId == nation.Id && unit.ProvinceId == hole && unit.Divisions >= .1f) return false;
        Army best = null;
        int[] bestRoute = null;
        var bestRetreat = -1;
        var bestShare = 1f;
        var bestScore = float.MaxValue;
        foreach (var army in candidates ?? c.Armies)
        {
            if (!Eligible(c, nation, war, line, army)) continue;
            var battle = c.Battles.Find(b => b.Id == army.BattleId && !b.Ended && b.WarId == war.Id);
            if (battle is null || c.ElapsedDays - battle.StartedDay < Math.Max(0, c.Defines.FrontDisengagementMinimumBattleDays)) continue;
            var attacking = battle.Attackers.Contains(army.Id);
            if (!attacking && !battle.Defenders.Contains(army.Id)) continue;
            if (line?.Cohesion == PlanCohesion.Rigid &&
                !c.ProvinceMap.Neighbors[army.ProvinceId].AsSpan().Contains(hole)) continue;
            var take = army.Divisions >= 2f
                ? MathF.Min(army.Divisions - 1f, MathF.Max(1f, MathF.Floor(army.Divisions *
                    Math.Clamp(c.Defines.FrontDisengagementMaximumShare, .05f, .5f)))) : army.Divisions;
            var share = take / army.Divisions;
            var ownDivisions = 0f;
            var ownPower = 0f;
            var coveringPower = 0f;
            foreach (var id in attacking ? battle.Attackers : battle.Defenders)
            {
                var unit = c.Armies.Find(a => a.Id == id && a.BattleId == battle.Id);
                if (unit is null) continue;
                coveringPower += unit.Power;
                if (unit.NationId == nation.Id) { ownDivisions += unit.Divisions; ownPower += unit.Power; }
            }
            // Do not leave the healthy anchor and call two exhausted counters a rearguard.
            if (ownDivisions - take < .99f || ownPower - army.Power * share < MathF.Max(.05f, ownPower * .5f)) continue;
            if (!attacking)
            {
                var pressure = 0f;
                foreach (var id in battle.Attackers)
                    if (c.Armies.Find(a => a.Id == id && a.BattleId == battle.Id) is { } enemy) pressure += enemy.Power;
                if (coveringPower - army.Power * share < pressure *
                    Math.Clamp(c.Defines.FrontDisengagementDefenceRatio, 0f, 2f)) continue;
            }
            var from = attacking ? army.ProvinceId : Warfare.WithdrawalProvince(c, war, army, hole);
            if (from <= 0) continue;
            // All routing is checked BEFORE the command can detach a formation or yield ground.
            var route = from == hole ? Array.Empty<int>() : WarCoalition.FriendlyRoute(c, war, nation.Id, from, hole);
            if (from != hole && route.Length == 0) continue;
            var safe = true;
            foreach (var step in route)
                if (!WarCoalition.SafeRetreatProvince(c, war, nation.Id, step)) { safe = false; break; }
            if (!safe) continue;
            var score = route.Length + (attacking ? 0 : 1) + army.Power / MathF.Max(.01f, army.Divisions);
            if (score > bestScore || score == bestScore && best is not null && army.Id >= best.Id) continue;
            best = army; bestRoute = route; bestRetreat = from; bestShare = share; bestScore = score;
        }
        if (best is null) return false;
        var objective = line?.Objectives.GetValueOrDefault(best.Id, -1) ?? -1;
        var order = new WithdrawArmyFromBattleCommand
        { IssuerId = nation.Id, ArmyId = best.Id, TargetProvinceId = bestRetreat, Share = bestShare };
        order.Apply(c);
        if (!order.Withdrawn) return false;
        moving = c.Armies.Find(a => a.Id == order.WithdrawnArmyId);
        moving.OrderKind = ArmyOrder.DefendFront;
        moving.AssignedWarId = war.Id;
        moving.DirectOrder = false;
        moving.PlanActive = line?.Kind != FrontLineKind.Offensive;
        moving.FrontLineId = line?.Id ?? -1;
        moving.Route.Clear(); moving.Route.AddRange(bestRoute);
        moving.PlannedRoute.Clear(); moving.PlannedRoute.AddRange(bestRoute);
        moving.MoveProgress = 0;
        moving.OrderProvinceId = hole;
        moving.OrderStateMapIndex = c.ProvinceMap.ProvinceToState[hole];
        moving.ObjectiveStateMapIndex = -1;
        moving.OrderIssuedDay = c.ElapsedDays;
        moving.OrderRouteSteps = bestRoute.Length;
        moving.OrderStepsCompleted = 0;
        if (line is not null)
        {
            if (!line.ArmyIds.Contains(moving.Id)) line.ArmyIds.Add(moving.Id);
            line.Posts[moving.Id] = hole;
            if (objective > 0) line.Objectives[moving.Id] = objective;
        }
        Orders++;
        return true;
    }
}
