using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Army
{
    /// <summary>Future destinations, not the route of the current battle. Empty in old saves.</summary>
    public readonly List<int> PostBattleWaypoints = new();
    public bool PostBattleSupportAttack;
}

/// <summary>Bounded authoritative order state; no UI callbacks, clock changes or teleportation.</summary>
public static class BattleMovementOrders
{
    public const int MaxWaypoints = 32;

    public static void Clear(Army army)
    {
        army.PostBattleWaypoints.Clear();
        army.PostBattleSupportAttack = false;
    }

    internal static void Resume(Campaign campaign, Army army)
    {
        if (army.InBattle || army.ReadyDay > campaign.ElapsedDays || army.PostBattleWaypoints.Count == 0) return;
        var destinations = army.PostBattleWaypoints.ToArray();
        var support = army.PostBattleSupportAttack;
        Clear(army);
        var append = false;
        foreach (var target in destinations)
        {
            var order = new MoveArmyCommand { IssuerId = army.NationId, ArmyId = army.Id,
                TargetProvinceId = target, AppendWaypoint = append, SupportAttack = support };
            order.Apply(campaign);
            if (order.Result == ArmyMoveResult.AlreadyAtDestination) continue;
            if (order.Result is not (ArmyMoveResult.Issued or ArmyMoveResult.AttackIssued))
            {
                // Borders/access may have changed while fighting. Do not retry forever,
                // turn a neutral country hostile, or carry on under an obsolete objective.
                if (!append) new HaltArmyCommand { IssuerId = army.NationId, ArmyId = army.Id }.Apply(campaign);
                army.LastMoveResult = ArmyMoveResult.RouteInterrupted;
                army.LastMoveResultDay = campaign.ElapsedDays;
                break;
            }
            append = true;
        }
    }

    internal static void Normalise(Campaign campaign)
    {
        foreach (var army in campaign.Armies)
        {
            army.PostBattleWaypoints.RemoveAll(id => id <= 0);
            if (army.PostBattleWaypoints.Count > MaxWaypoints)
                army.PostBattleWaypoints.RemoveRange(MaxWaypoints, army.PostBattleWaypoints.Count - MaxWaypoints);
            if (army.PostBattleWaypoints.Count == 0) army.PostBattleSupportAttack = false;
        }
    }
}
