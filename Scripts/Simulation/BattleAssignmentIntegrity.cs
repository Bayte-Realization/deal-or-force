using System;
using System.Collections.Generic;
using System.Linq;

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
                (battle.Attackers.Contains(army.Id) || battle.Defenders.Contains(army.Id)))
            {
                var map = campaign.ProvinceMap;
                var attacking = battle.Attackers.Contains(army.Id);
                if (map is null || army.ProvinceId == battle.ProvinceId ||
                    (attacking && army.ProvinceId > 0 && army.ProvinceId < map.Neighbors.Length &&
                     map.Neighbors[army.ProvinceId].Contains(battle.ProvinceId))) continue;
                // Legacy ghost reserves fought from a different province. Release the
                // bogus assignment without teleportation, rest penalty or healing.
                battle.Attackers.Remove(army.Id);
                battle.Defenders.Remove(army.Id);
                army.BattleId = -1;
                repaired++;
                continue;
            }
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
