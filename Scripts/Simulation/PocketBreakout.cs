using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Whether a computer-run formation that has been cut off tries to break out (PocketBreakout).</summary>
    public bool AiBreaksOutOfPockets { get; set; } = true;

    /// <summary>How many provinces from a pocket the way home is looked for.</summary>
    public int BreakoutSearchDepth { get; set; } = 12;
}

public sealed partial class Army
{
    /// <summary>
    /// The staff sent this formation home out of a pocket; it goes back under the staff's orders
    /// once it stands on friendly ground again. Saved.
    /// </summary>
    public bool BreakingOut;
}

/// <summary>
/// Formations nobody plays break out of a pocket.
///
/// A player's formation inside a pocket could already be sent home - the order is routed through
/// the captor's ground and fought for a province at a time (MoveArmyCommand) - but nothing ever gave
/// that order to a formation the computer ran. It stood in the pocket until it starved or until the
/// province it held fell and, with nowhere to go, it surrendered whole: fifteen Ukrainian
/// formations in one pocket on one day, six hundred thousand prisoners in eight months. Now every
/// day a cut-off formation of a computer-run country that is not fighting and not already marching
/// is ordered to the nearest friendly province, by the same order a player would give, and is
/// handed back to the staff when it gets there. Whether it makes it is the fighting's business.
/// </summary>
public static class PocketBreakout
{
    private static readonly Queue<int> Frontier = new();
    private static readonly Dictionary<int, int> CameFrom = new();
    private static readonly List<int> Pocketed = new();
    private static readonly HashSet<int> Pocket = new();
    private const int PocketLimit = 200;

    public static void Tick(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || !campaign.StepOpensNewDay) return;
        if (!campaign.Defines.AiBreaksOutOfPockets) return;

        Pocketed.Clear();
        foreach (var army in campaign.Armies)
        {
            if (army.BreakingOut && !army.CutOff && !army.IsMoving && !army.InBattle)
            {
                // Home: the staff has it back.
                army.BreakingOut = false;
                army.DirectOrder = false;
                army.OrderKind = ArmyOrder.Manual;
                army.AssignedWarId = -1;
                continue;
            }
            if (!army.CutOff || army.InBattle || army.IsMoving || army.Divisions < 0.1f ||
                army.EmbarkedFleetId >= 0 || army.SupportOnly) continue;
            Pocketed.Add(army.Id);
        }

        foreach (var id in Pocketed)
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null) continue;
            var nation = campaign.NationById(army.NationId);
            if (nation is null || !Delegation.RunByAi(nation)) continue;
            if (army.DirectOrder && !army.BreakingOut) continue;
            var path = WayHome(campaign, nation, army.ProvinceId);
            if (path is null) continue;
            if (Order(campaign, nation, army, path[^1]) || path.Count > 1 && Order(campaign, nation, army, path[1]))
                army.BreakingOut = true;
        }
    }

    private static bool Order(Campaign campaign, Nation nation, Army army, int target)
    {
        var command = new MoveArmyCommand { IssuerId = nation.Id, ArmyId = army.Id, TargetProvinceId = target };
        command.Apply(campaign);
        return command.Result is ArmyMoveResult.Issued or ArmyMoveResult.AttackIssued or ArmyMoveResult.QueuedAfterBattle;
    }

    /// <summary>
    /// The shortest way over land from a pocket to ground the formation's own country controls:
    /// the provinces in order, the pocket first and the friendly province last; null if there is
    /// none within BreakoutSearchDepth.
    /// </summary>
    private static List<int> WayHome(Campaign campaign, Nation nation, int start)
    {
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (start <= 0 || start >= map.Neighbors.Length) return null;
        // The pocket itself: the formation's own ground it can reach without crossing the enemy's.
        // Home is own ground outside it. A "pocket" of hundreds of provinces is a country cut in
        // two, not a pocket, and is the staff's to deal with.
        Pocket.Clear();
        Frontier.Clear();
        Frontier.Enqueue(start);
        Pocket.Add(start);
        while (Frontier.Count > 0)
        {
            var here = Frontier.Dequeue();
            foreach (var next in map.Neighbors[here])
            {
                if (next <= 0 || next >= map.Neighbors.Length || map.IsWater(next) || Pocket.Contains(next) ||
                    controllers[next] != (ushort)nation.MapIndex) continue;
                Pocket.Add(next);
                if (Pocket.Count > PocketLimit) return null;
                Frontier.Enqueue(next);
            }
        }
        Frontier.Clear();
        CameFrom.Clear();
        Frontier.Enqueue(start);
        CameFrom[start] = -1;
        var depth = new Dictionary<int, int> { [start] = 0 };
        var limit = Math.Max(1, campaign.Defines.BreakoutSearchDepth);
        while (Frontier.Count > 0)
        {
            var here = Frontier.Dequeue();
            if (!Pocket.Contains(here) && controllers[here] == (ushort)nation.MapIndex)
            {
                var path = new List<int>();
                for (var step = here; step >= 0; step = CameFrom[step]) path.Add(step);
                path.Reverse();
                return path;
            }
            if (depth[here] >= limit) continue;
            foreach (var next in map.Neighbors[here])
            {
                if (next <= 0 || next >= map.Neighbors.Length || map.IsWater(next) || CameFrom.ContainsKey(next)) continue;
                CameFrom[next] = here;
                depth[next] = depth[here] + 1;
                Frontier.Enqueue(next);
            }
        }
        return null;
    }
}
