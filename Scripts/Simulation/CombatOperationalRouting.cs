using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

// Operational corridors are prepared before combat target selection.
public static partial class Warfare
{
    /// <summary>
    /// Keeps the attack corridor executable as control changes. Returns true when the army
    /// has first been assigned a friendly staging leg and therefore should not attack today.
    /// </summary>
    private readonly record struct FailedOperationalRoute(int From, int To, int WarId, int Revision, ushort[] Controllers);
    private static readonly Dictionary<int, FailedOperationalRoute> NoRoute = new();
    private static Campaign _noRouteCampaign;
    // One immutable, exact membership snapshot shared by failures in this war/pass,
    // allocated only if a new search actually fails. No per-formation set or lossy hash.
    private static ushort[] _belligerentSnapshot;

    internal static void InvalidateAccessRoutes(Campaign c)
    {
        if (ReferenceEquals(_noRouteCampaign, c)) NoRoute.Clear();
    }

    private static bool PrepareOperationalRoute(Campaign campaign, StrategicWar war, Army army,
        ushort[] controllers, HashSet<ushort> friendlyMaps)
    {
        var topology = campaign.ProvinceMap;
        var nation = campaign.NationById(army.NationId);
        var enemyId = war.EnemyLeaderOf(army.NationId);
        var enemy = campaign.NationById(enemyId);
        if (topology is null || nation is null || enemy is null) return false;

        var plan = BattlePlanning.LineFor(campaign, army);
        var boundedPlan = !army.DirectOrder && plan is { Kind: FrontLineKind.Offensive } && plan.Objectives.Count > 0;
        if (army.OrderProvinceId <= 0) return boundedPlan;
        if (boundedPlan && army.ProvinceId == army.OrderProvinceId)
        {
            army.PlanActive = false;
            army.Route.Clear(); army.PlannedRoute.Clear();
            return true;
        }

        if (army.SupportOnly && (friendlyMaps.Contains(controllers[army.OrderProvinceId]) ||
            MilitaryAccess.CanEnter(campaign, army.NationId, controllers[army.OrderProvinceId])))
        {
            new HaltArmyCommand { IssuerId = army.NationId, ArmyId = army.Id }.Apply(campaign);
            army.SupportOnly = false;
            army.PlanActive = false;
            army.OrderKind = ArmyOrder.Manual;
            army.AssignedWarId = -1;
            return true;
        }

        while (army.PlannedRoute.Count > 0 && army.PlannedRoute[0] == army.ProvinceId)
            army.PlannedRoute.RemoveAt(0);
        if (army.PlannedRoute.Count == 0 || !topology.Neighbors[army.ProvinceId].Contains(army.PlannedRoute[0]) ||
            (!BelligerentMaps.Contains(controllers[army.PlannedRoute[0]]) &&
             !MilitaryAccess.CanEnter(campaign, army.NationId, controllers[army.PlannedRoute[0]])))
        {
            army.PlannedRoute.Clear();
            if (!ReferenceEquals(_noRouteCampaign, campaign)) { NoRoute.Clear(); _noRouteCampaign = campaign; }
            // The same search on the same map finds the same nothing. A division whose objective
            // it cannot reach - or is already standing on - asked again every hour, flooding its
            // side's whole territory each time: a hundred and twenty such searches a step, half
            // of a day's simulation thirty-six years in. A failed search is remembered until
            // the division moves, its objective changes, or any province changes hands.
            if (!NoRoute.TryGetValue(army.Id, out var failed) || failed.From != army.ProvinceId ||
                failed.To != army.OrderProvinceId || failed.WarId != war.Id || failed.Revision != campaign.MapRevision ||
                !BelligerentMaps.SetEquals(failed.Controllers))
            {
                army.PlannedRoute.AddRange(WarCoalition.OperationalRoute(campaign, war, army.NationId,
                    army.ProvinceId, army.OrderProvinceId, controllers));
                if (army.PlannedRoute.Count == 0) NoRoute[army.Id] = new FailedOperationalRoute(army.ProvinceId,
                    army.OrderProvinceId, war.Id, campaign.MapRevision, _belligerentSnapshot ??= BelligerentMaps.ToArray());
                else NoRoute.Remove(army.Id);
            }
            army.OrderRouteSteps = Math.Max(army.OrderStepsCompleted + army.PlannedRoute.Count,
                army.OrderRouteSteps);
        }
        if ((army.DirectOrder || boundedPlan) && army.PlannedRoute.Count == 0)
        {
            if (boundedPlan) { army.PlanActive = false; army.Route.Clear(); return true; }
            var arrived = army.ProvinceId == army.OrderProvinceId;
            new HaltArmyCommand { IssuerId = army.NationId, ArmyId = army.Id }.Apply(campaign);
            army.LastMoveResult = arrived ? ArmyMoveResult.AlreadyAtDestination : ArmyMoveResult.RouteInterrupted;
            army.LastMoveResultDay = campaign.ElapsedDays;
            return true;
        }
        if (army.PlannedRoute.Count == 0 || (!friendlyMaps.Contains(controllers[army.PlannedRoute[0]]) &&
            !MilitaryAccess.CanEnter(campaign, army.NationId, controllers[army.PlannedRoute[0]]))) return false;

        // By hand. TakeWhile's predicate captured `controllers` and `nation`, and a closure
        // over a parameter is allocated when the method is entered - so every advancing
        // division paid for one every step, whether or not it ever got this far.
        army.Route.Clear();
        foreach (var province in army.PlannedRoute)
        {
            if (!friendlyMaps.Contains(controllers[province]) && !MilitaryAccess.CanEnter(campaign, army.NationId, controllers[province])) break;
            army.Route.Add(province);
        }
        return army.Route.Count > 0;
    }

}
