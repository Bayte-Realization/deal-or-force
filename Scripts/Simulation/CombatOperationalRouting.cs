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
    // The corridor last searched for this order: the provinces after the one the formation
    // stands on, and who held each of them. The same order over the same holders is the same
    // corridor. A capture on one of those provinces drops it. Another campaign has its own index.
    private readonly record struct HeldAdvanceRoute(int From, int To, int WarId, int[] Path, ushort[] Control);

    /// <summary>How many advance corridors were actually searched. A reused corridor does not add one.</summary>
    internal static int AdvanceRouteSearches { get; private set; }

    /// <summary>Times the advance corridor was asked for during resolution.</summary>
    internal static int AdvanceRouteAsks { get; private set; }

    /// <summary>The order's own corridor was still valid, so nothing was searched or copied.</summary>
    internal static int AdvanceRouteKeeps { get; private set; }

    /// <summary>
    /// A remembered corridor was copied onto the order, or a remembered empty search was used again.
    /// Neither is a walk.
    /// </summary>
    internal static int AdvanceRouteReuses { get; private set; }

    internal static void ResetAdvanceRouteSearches()
    {
        AdvanceRouteSearches = 0;
        AdvanceRouteAsks = 0;
        AdvanceRouteKeeps = 0;
        AdvanceRouteReuses = 0;
    }

    internal static void InvalidateAccessRoutes(Campaign c)
    {
        var index = IndexOf(c);
        if (index is null) return;
        index.NoRoute.Clear();
        index.AdvanceRoutes.Clear();
    }

    /// <summary>
    /// The hourly advance corridor for one formation. A hand order is prepared by the same
    /// rules as before and is not replaced from a remembered corridor.
    /// </summary>
    internal static bool PrepareAdvanceCorridor(Campaign campaign, StrategicWar war, Army army)
    {
        if (campaign is null || war is null || army is null || campaign.ProvinceController is null) return false;
        IndexSideMaps(campaign, war);
        var sides = IndexOf(campaign);
        var friendly = war.IsAttacking(army.NationId) ? sides.AttackerMaps : sides.DefenderMaps;
        return PrepareOperationalRoute(campaign, war, army, campaign.ProvinceController, friendly);
    }

    private static bool PrepareOperationalRoute(Campaign campaign, StrategicWar war, Army army,
        ushort[] controllers, HashSet<ushort> friendlyMaps)
    {
        var topology = campaign.ProvinceMap;
        var nation = campaign.NationById(army.NationId);
        var enemyId = war.EnemyLeaderOf(army.NationId);
        var enemy = campaign.NationById(enemyId);
        if (topology is null || nation is null || enemy is null) return false;
        var sides = UseArmyIndex(campaign);

        var plan = !army.DirectOrder && army.FrontLineId >= 0 ? BattlePlanning.LineFor(campaign, army) : null;
        var boundedPlan = !army.DirectOrder && plan is { Kind: FrontLineKind.Offensive } && plan.Objectives.Count > 0;
        if (army.OrderProvinceId <= 0) return boundedPlan;
        if (boundedPlan && army.ProvinceId == army.OrderProvinceId)
        {
            army.PlanActive = OffensivePlanning.HasNextPhase(plan);
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
        // A hand order keeps the corridor the player gave it. The remembered one is only for
        // a staff order, and only while every province still ahead is held by who held it then.
        var corridorBroken = army.PlannedRoute.Count == 0 ||
            Array.IndexOf(topology.Neighbors[army.ProvinceId], army.PlannedRoute[0]) < 0 ||
            (!sides.BelligerentMaps.Contains(controllers[army.PlannedRoute[0]]) &&
             !MilitaryAccess.CanEnter(campaign, army.NationId, controllers[army.PlannedRoute[0]]));
        AdvanceRouteAsks++;
        // 1: the remembered path is on the order. 0: nothing to copy. -1: a holder on it changed.
        var heldUse = CopyHeldAdvance(campaign, war, army, controllers, topology, corridorBroken);
        if (heldUse < 0 || (heldUse == 0 && corridorBroken))
        {
            army.PlannedRoute.Clear();
            var noRoute = sides.NoRoute;
            // A failed search is remembered until the division moves, its objective changes,
            // or the war may cross a different set of controllers. Another province changing
            // hands inside that same set is the same search.
            var rememberedEmpty = noRoute.TryGetValue(army.Id, out var failed) && failed.From == army.ProvinceId &&
                failed.To == army.OrderProvinceId && failed.WarId == war.Id && SameBelligerents(sides, failed.Controllers);
            if (!rememberedEmpty)
            {
                AdvanceRouteSearches++;
                WarCoalition.FillOperationalRoute(campaign, war, army.NationId,
                    army.ProvinceId, army.OrderProvinceId, controllers, army.PlannedRoute);
                if (army.PlannedRoute.Count == 0) noRoute[army.Id] = new FailedOperationalRoute(army.ProvinceId,
                    army.OrderProvinceId, war.Id, campaign.MapRevision, sides.BelligerentSnapshot ??= sides.BelligerentMaps.ToArray());
                else noRoute.Remove(army.Id);
            }
            else AdvanceRouteReuses++;
            RememberAdvanceRoute(campaign, war, army, controllers);
            army.OrderRouteSteps = Math.Max(army.OrderStepsCompleted + army.PlannedRoute.Count,
                army.OrderRouteSteps);
        }
        else if (heldUse == 0) AdvanceRouteKeeps++;
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

    /// <summary>
    /// Puts a remembered corridor back on the order the next ask reads.
    /// A hand order is not replaced. A holder changing on the steps still ahead drops it.
    /// </summary>
    private static int CopyHeldAdvance(Campaign campaign, StrategicWar war, Army army, ushort[] controllers,
        ProvinceTopology topology, bool corridorBroken)
    {
        if (army.DirectOrder) return 0;
        var index = UseArmyIndex(campaign);
        if (!index.AdvanceRoutes.TryGetValue(army.Id, out var held) || held.Path is null ||
            held.To != army.OrderProvinceId || held.WarId != war.Id) return 0;
        var start = held.From == army.ProvinceId ? 0 : Array.IndexOf(held.Path, army.ProvinceId);
        if (start < 0) return 0;
        if (held.From != army.ProvinceId) start++;
        if (start > held.Path.Length) return 0;
        if (!SameControlFrom(held, controllers, held.From == army.ProvinceId ? 0 : start - 1))
        {
            // Still standing where the corridor was searched: the order is unchanged and a holder
            // on it is not, so the next ask has to walk. Once the formation has moved on, a holder
            // behind it is not the corridor it is still following.
            if (held.From != army.ProvinceId) return 0;
            index.AdvanceRoutes.Remove(army.Id);
            return -1;
        }
        if (start >= held.Path.Length) return 0;
        if (Array.IndexOf(topology.Neighbors[army.ProvinceId], held.Path[start]) < 0) return 0;
        if (!SameTail(army.PlannedRoute, held.Path, start))
        {
            // A different corridor that is still executable stays the order. Only a broken one
            // is replaced by the steps that were already found.
            if (!corridorBroken) return 0;
            army.PlannedRoute.Clear();
            for (var i = start; i < held.Path.Length; i++) army.PlannedRoute.Add(held.Path[i]);
            RememberAdvanceRoute(campaign, war, army, controllers);
        }
        if (corridorBroken)
        {
            army.OrderRouteSteps = Math.Max(army.OrderStepsCompleted + army.PlannedRoute.Count, army.OrderRouteSteps);
            index.NoRoute.Remove(army.Id);
        }
        AdvanceRouteReuses++;
        return 1;
    }

    private static bool HeldControllersChanged(Campaign campaign, StrategicWar war, Army army, ushort[] controllers)
    {
        if (army.DirectOrder) return false;
        var routes = UseArmyIndex(campaign).AdvanceRoutes;
        return routes.TryGetValue(army.Id, out var held) &&
            held.From == army.ProvinceId && held.To == army.OrderProvinceId && held.WarId == war.Id &&
            !SameControl(held, controllers);
    }

    private static bool ReuseHeldRoute(Campaign campaign, StrategicWar war, Army army, ushort[] controllers)
    {
        if (army.DirectOrder) return false;
        var index = UseArmyIndex(campaign);
        if (!index.AdvanceRoutes.TryGetValue(army.Id, out var held) ||
            held.From != army.ProvinceId || held.To != army.OrderProvinceId || held.WarId != war.Id ||
            !SameControl(held, controllers)) return false;
        if (!SameSteps(army.PlannedRoute, held.Path))
        {
            army.PlannedRoute.Clear();
            army.PlannedRoute.AddRange(held.Path);
        }
        index.NoRoute.Remove(army.Id);
        return true;
    }

    private static void RememberAdvanceRoute(Campaign campaign, StrategicWar war, Army army, ushort[] controllers)
    {
        var routes = UseArmyIndex(campaign).AdvanceRoutes;
        var count = army.PlannedRoute.Count;
        if (army.DirectOrder || count == 0)
        {
            routes.Remove(army.Id);
            return;
        }
        int[] path;
        ushort[] control;
        if (routes.TryGetValue(army.Id, out var existing) &&
            existing.Path is { Length: > 0 } existingPath && existingPath.Length == count &&
            existing.Control is { Length: > 0 } existingControl && existingControl.Length == count)
        {
            path = existingPath;
            control = existingControl;
        }
        else
        {
            path = new int[count];
            control = new ushort[count];
        }
        for (var i = 0; i < count; i++)
        {
            var province = army.PlannedRoute[i];
            path[i] = province;
            control[i] = (uint)province < (uint)controllers.Length ? controllers[province] : (ushort)0;
        }
        routes[army.Id] = new HeldAdvanceRoute(army.ProvinceId, army.OrderProvinceId, war.Id, path, control);
    }

    private static bool SameControl(HeldAdvanceRoute held, ushort[] controllers)
    {
        var path = held.Path;
        var control = held.Control;
        if (path is null || control is null || controllers is null || path.Length != control.Length) return false;
        for (var i = 0; i < path.Length; i++)
        {
            var province = path[i];
            if ((uint)province >= (uint)controllers.Length || controllers[province] != control[i]) return false;
        }
        return true;
    }

    private static bool SameControlFrom(HeldAdvanceRoute held, ushort[] controllers, int start)
    {
        var path = held.Path;
        var control = held.Control;
        if (path is null || control is null || controllers is null || path.Length != control.Length || start > path.Length)
            return false;
        if (start < 0) start = 0;
        for (var i = start; i < path.Length; i++)
        {
            var province = path[i];
            if ((uint)province >= (uint)controllers.Length || controllers[province] != control[i]) return false;
        }
        return true;
    }

    private static bool SameSteps(List<int> route, int[] path)
    {
        if (route is null || path is null || route.Count != path.Length) return false;
        for (var i = 0; i < path.Length; i++)
            if (route[i] != path[i]) return false;
        return true;
    }

    private static bool SameTail(List<int> route, int[] path, int start)
    {
        if (route is null || path is null || start < 0 || start > path.Length) return false;
        var count = path.Length - start;
        if (route.Count != count) return false;
        for (var i = 0; i < count; i++)
            if (route[i] != path[start + i]) return false;
        return true;
    }

    private static bool SameBelligerents(ArmyIndex index, ushort[] controllers)
    {
        if (index is null || controllers is null) return false;
        if (ReferenceEquals(controllers, index.BelligerentSnapshot)) return true;
        if (controllers.Length != index.BelligerentMaps.Count) return false;
        for (var i = 0; i < controllers.Length; i++)
            if (!index.BelligerentMaps.Contains(controllers[i])) return false;
        return true;
    }
}
