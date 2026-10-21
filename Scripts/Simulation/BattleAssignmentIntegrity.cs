using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public static partial class Warfare
{
    private static readonly Dictionary<int, LandBattle> AssignmentBattles = new();
    public static int RepairedBattleAssignments { get; private set; }

    /// <summary>Old peace/terrain code ended reports without releasing their formations.
    /// Repair only provably nonexistent assignments, never cancel a live participant.</summary>
    internal static int ReconcileBattleAssignments(Campaign campaign)
    {
        AssignmentBattles.Clear();
        foreach (var battle in campaign.Battles) AssignmentBattles[battle.Id] = battle;
        var repaired = 0;
        foreach (var army in campaign.Armies)
        {
            if (!army.InBattle) continue;
            if (AssignmentBattles.TryGetValue(army.BattleId, out var battle) && !battle.Ended &&
                (battle.Attackers.Contains(army.Id) || battle.Defenders.Contains(army.Id))) continue;
            army.BattleId = -1;
            army.ReadyDay = Math.Max(army.ReadyDay, campaign.ElapsedDays +
                Math.Max(1, campaign.Defines.RestAfterSpentAttackDays));
            repaired++;
        }
        if (repaired > 0)
        {
            RepairedBattleAssignments += repaired;
            campaign.MarkStrategicChange();
            DiagnosticLogs.Write("simulation", $"BATTLE_ASSIGNMENT_REPAIR day={campaign.ElapsedDays} formations={repaired}");
        }
        return repaired;
    }

    internal static void EndBattleWithoutVictory(Campaign campaign, LandBattle battle, string outcome) =>
        Close(campaign, battle, -1, outcome);
}
