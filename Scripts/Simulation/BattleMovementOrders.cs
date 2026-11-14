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

    /// <summary>
    /// Whether a right-click on the province a fighting formation stands in takes it out of
    /// its battle. Only an attack is broken off that way: an attacker stands in the province
    /// it attacks from, so stopping leaves it exactly where it is. A defender stands in the
    /// province being fought for, and the same withdrawal would march it out and hand the
    /// ground to the enemy - a player who clicks the province his army holds means "hold
    /// it", so the defence stays and the click only clears queued orders (MoveArmyCommand).
    /// Giving ground up is H or Leave battle, behind the last-defender warning.
    /// </summary>
    public static bool OwnProvinceClickBreaksOff(Campaign campaign, Army army, int provinceId)
    {
        if (campaign is null || army is null || !army.InBattle || army.ProvinceId != provinceId) return false;
        var battle = campaign.Battles.Find(candidate => candidate.Id == army.BattleId && !candidate.Ended);
        return battle is not null && battle.ProvinceId != provinceId && battle.Attackers.Contains(army.Id);
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
