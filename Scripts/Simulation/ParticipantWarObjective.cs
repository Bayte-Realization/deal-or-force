using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public static partial class FrontPlanner
{
    /// <summary>Each participant operates on its own front, not necessarily
    /// the place named by the coalition's original declaration. No new campaign state.</summary>
    internal static int ObjectiveFor(Campaign c, StrategicWar war, Nation nation)
    {
        var map = c?.ProvinceMap;
        var control = c?.ProvinceController;
        if (map is null || control is null || nation is null || war is null)
            return war?.ClaimedStateMapIndex ?? -1;
        var frontier = war.FriendlyFront(nation.Id);
        var declaredIsHostile = HasHostileGround(war.ClaimedStateMapIndex);
        if (frontier.Count == 0) return declaredIsHostile ? war.ClaimedStateMapIndex : -1;
        var enemiesHere = new HashSet<int>();
        var ownFront = new List<int>();
        foreach (var post in frontier)
        {
            if (post <= 0 || post > map.ProvinceCount ||
                control[post] != nation.MapIndex) continue;
            ownFront.Add(post);
            foreach (var next in map.Neighbors[post])
                if (WarCoalition.HostileController(c, war, nation.Id, control[next]))
                {
                    enemiesHere.Add(map.ProvinceToNation[next]);
                    enemiesHere.Add(control[next]);
                }
        }
        var declared = c.States.Find(s => s.MapIndex == war.ClaimedStateMapIndex);
        if (declared is not null && enemiesHere.Contains(declared.NationMapIndex) &&
            declaredIsHostile) return declared.MapIndex;
        if (war.Limited) return declaredIsHostile ? war.ClaimedStateMapIndex : -1;
        var best = -1;
        var distance = float.MaxValue;
        foreach (var enemyId in war.EnemiesOf(nation.Id))
        {
            var enemy = c.NationById(enemyId);
            if (enemy is null || !enemiesHere.Contains(enemy.MapIndex)) continue;
            var capital = enemy.CapitalProvinceId;
            if (capital <= 0 || capital > map.ProvinceCount ||
                !WarCoalition.HostileController(c, war, nation.Id, control[capital])) continue;
            var centre = map.CenterOf(capital);
            var near = SetArmyOrderCommand.FrontDistance(map, ownFront, centre.Latitude, centre.Longitude);
            if (near > distance || near == distance && best >= 0 && map.ProvinceToState[capital] >= best) continue;
            distance = near;
            best = map.ProvinceToState[capital];
        }
        return best;

        bool HasHostileGround(int state)
        {
            if (state < 0) return false;
            for (var p = 1; p <= map.ProvinceCount; p++)
                if (map.ProvinceToState[p] == state &&
                    WarCoalition.HostileController(c, war, nation.Id, control[p])) return true;
            return false;
        }
    }
}
