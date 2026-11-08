using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>At peace, free AI/delegated guests march home over a legal land corridor.
    /// Explicit deployments, drawn-line garrisons and volunteers keep their duties.</summary>
    public bool ArmyHomecomingEnabled { get; set; } = true;
}

internal static class ArmyHomecoming
{
    // Per-review scratch, not campaign state: reused, cleared even after a failed search,
    // with at most one graph walk per distinct guest origin. No all-world scan per division.
    private static readonly List<int> Frontier = new();
    private static readonly HashSet<int> Seen = new();
    private static readonly Dictionary<int, int> Destinations = new();

    internal static int OrderReturns(Campaign c, Nation nation, List<Army> free, int budget)
    {
        if (!c.Defines.ArmyHomecomingEnabled || budget <= 0) return 0;
        var map = c.ProvinceMap;
        var controls = c.ProvinceController;
        HashSet<ushort> allowed = null;
        Destinations.Clear();
        var moves = 0;
        for (var index = 0; index < free.Count && moves < budget; index++)
        {
            var army = free[index];
            if (army.LentById >= 0 || army.ReadyDay > c.ElapsedDays ||
                army.ProvinceId > map.ProvinceCount || army.ProvinceId >= controls.Length ||
                controls[army.ProvinceId] == nation.MapIndex) continue;
            allowed ??= MilitaryAccess.Controllers(c, nation.Id);
            if (!allowed.Contains(controls[army.ProvinceId])) continue; // access revocation has its own repatriation rules
            if (!Destinations.TryGetValue(army.ProvinceId, out var destination))
                Destinations[army.ProvinceId] = destination = FindHome(c, nation, army.ProvinceId, allowed);
            if (destination <= 0) continue; // no land corridor: do not fake a transport or a border crossing
            var order = new MoveArmyCommand { IssuerId = nation.Id, ArmyId = army.Id, TargetProvinceId = destination };
            order.Apply(c);
            if (order.Result != ArmyMoveResult.Issued) continue;
            army.DirectOrder = false; // normal command validation; autonomous staff ownership
            free.RemoveAt(index--); // no garrison/relief order may turn this column around in the same review
            moves++;
        }
        return moves;
    }

    private static int FindHome(Campaign c, Nation nation, int origin, HashSet<ushort> allowed)
    {
        Frontier.Clear(); Seen.Clear();
        Frontier.Add(origin); Seen.Add(origin);
        for (var read = 0; read < Frontier.Count; read++)
        {
            var current = Frontier[read];
            if (c.ProvinceController[current] == nation.MapIndex &&
                c.ProvinceMap.ProvinceToNation[current] == nation.MapIndex) return current;
            foreach (var next in c.ProvinceMap.Neighbors[current])
            {
                if (next <= 0 || next >= c.ProvinceController.Length || c.ProvinceMap.IsWater(next) ||
                    !allowed.Contains(c.ProvinceController[next]) || !Seen.Add(next)) continue;
                Frontier.Add(next);
            }
        }
        return 0;
    }
}
