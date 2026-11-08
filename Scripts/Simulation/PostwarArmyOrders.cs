namespace DealOrForce.Simulation;

/// <summary>The ended operation owns these orders, not the next war or the peacetime staff.</summary>
internal static class PostwarArmyOrders
{
    internal static void Release(Army army)
    {
        army.AssignedWarId = -1;
        army.OrderKind = ArmyOrder.Manual;
        army.PlanActive = army.DirectOrder = army.SupportOnly = false;
        army.OrderProvinceId = army.OrderStateMapIndex = army.ObjectiveStateMapIndex = -1;
        army.RedeployTarget = -1;
        army.Route.Clear();
        army.PlannedRoute.Clear();
        army.MoveProgress = 0f;
        army.OrderRouteSteps = army.OrderStepsCompleted = 0;
        BattleMovementOrders.Clear(army);
        // Keep FrontLineId: ReturnToWholeFronts may still find another active war on it.
        // Position, losses, readiness and entrenchment are not a peace-order cleanup's to edit.
    }
}
