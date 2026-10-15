using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// The side a formation actually belongs to, not the country whose name labels its battle.
/// Kept live: joining/leaving a war changes access without waiting for a map capture.
/// </summary>
public static class WarCoalition
{
    public static bool SameSide(StrategicWar war, int first, int second) => war is not null &&
        ((war.IsAttacking(first) && war.IsAttacking(second)) ||
         (war.IsDefending(first) && war.IsDefending(second)));

    public static void FillControllers(Campaign c, StrategicWar war, bool attacking, ISet<ushort> into)
    {
        foreach (var id in war.Side(attacking))
            if (c.NationById(id) is { MapIndex: > 0 } nation) into.Add((ushort)nation.MapIndex);
    }

    public static bool FriendlyController(Campaign c, StrategicWar war, int nationId, ushort controller)
    {
        if (c.NationById(nationId) is { } own && controller == own.MapIndex) return true;
        if (war is null || war.Ended || !war.Involves(nationId)) return false;
        foreach (var id in war.Side(war.IsAttacking(nationId)))
            if (c.NationById(id) is { } nation && controller == nation.MapIndex) return true;
        return false;
    }

    internal static StrategicWar AssignedWar(Campaign c, Army army)
    {
        foreach (var war in c.Wars)
            if (war.Id == army.AssignedWarId && !war.Ended && war.Involves(army.NationId)) return war;
        return null;
    }

    public static bool HostileController(Campaign c, StrategicWar war, int nationId, ushort controller)
    {
        if (war is null || war.Ended || !war.Involves(nationId)) return false;
        foreach (var id in war.EnemiesOf(nationId))
            if (c.NationById(id) is { } nation && controller == nation.MapIndex) return true;
        return false;
    }

    /// <summary>An offensive corridor may cross the war's countries, never a neutral shortcut.</summary>
    public static int[] OperationalRoute(Campaign c, StrategicWar war, int nationId,
        int from, int to, ushort[] controllers)
    {
        if (c?.ProvinceMap is null || war is null || war.Ended || !war.Involves(nationId))
            return System.Array.Empty<int>();
        // Most wars remain bilateral. Keep their existing allocation-free route filter.
        if (war.BelligerentCount == 2 && c.NationById(nationId) is { } own &&
            c.NationById(war.EnemyLeaderOf(nationId)) is { } enemy)
            return c.ProvinceMap.FindOperationalRoute(from, to, (ushort)own.MapIndex, (ushort)enemy.MapIndex, controllers);
        var allowed = new HashSet<ushort>();
        FillControllers(c, war, true, allowed);
        FillControllers(c, war, false, allowed);
        return c.ProvinceMap.FindOperationalRoute(from, to, allowed, controllers);
    }

    /// <summary>Keep the old own-ground preference; an ally's refuge is better than surrender.</summary>
    internal static int RetreatProvince(Campaign c, StrategicWar war, Army army, int from, bool lowestId = false)
    {
        var map = c.ProvinceMap;
        var controls = c.ProvinceController;
        var own = c.NationById(army.NationId);
        if (map is null || controls is null || own is null || from <= 0 || from >= map.Neighbors.Length) return 0;
        var allied = 0;
        var ownRetreat = 0;
        foreach (var p in map.Neighbors[from])
        {
            if (map.IsWater(p)) continue;
            if (controls[p] == own.MapIndex)
            {
                if (!lowestId) return p;
                if (ownRetreat == 0 || p < ownRetreat) ownRetreat = p;
            }
            else if ((allied == 0 || (lowestId && p < allied)) && FriendlyController(c, war, army.NationId, controls[p])) allied = p;
        }
        return ownRetreat > 0 ? ownRetreat : allied;
    }
}
