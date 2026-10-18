using System;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

[Flags]
public enum ArmyOrderChange { None = 0, Arrived = 1, Retargeted = 2, ProgressReset = 4 }

/// <summary>Read-only observations at completed simulation steps, not new orders.
/// Arrival is a position crossing; changing a destination alone proves no arrival.</summary>
public readonly record struct ArmyOrderSample(int Position, int Goal, int Issued, float Progress)
{
    public static ArmyOrderSample Of(Army army) =>
        new(army.ProvinceId, army.OrderProvinceId, army.OrderIssuedDay, army.MoveProgress);

    public static ArmyOrderChange Compare(ArmyOrderSample before, ArmyOrderSample after)
    {
        var change = ArmyOrderChange.None;
        if (before.Goal > 0 && before.Position != before.Goal && after.Position == before.Goal)
            change |= ArmyOrderChange.Arrived;
        if (before.Goal > 0 && after.Goal > 0 && before.Goal != after.Goal &&
            before.Position != before.Goal && after.Position != before.Goal)
            change |= ArmyOrderChange.Retargeted;
        if (before.Goal > 0 && after.Goal == before.Goal && after.Position == before.Position &&
            after.Issued != before.Issued && before.Progress > .001f && after.Progress < before.Progress)
            change |= ArmyOrderChange.ProgressReset;
        return change;
    }
}
