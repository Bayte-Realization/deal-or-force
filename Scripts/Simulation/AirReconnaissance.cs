using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The recon figure (common/support) at which a formation photographs everything it flies over.</summary>
    public float ReconFullSight { get; set; } = 6f;
    /// <summary>The least share of its range any formation on reconnaissance sees, however poor its sensors.</summary>
    public float ReconMinimumSight { get; set; } = 0.2f;
    /// <summary>What the enemy's sky costs a reconnaissance flight, against what it costs one over a battle.</summary>
    public float ReconExposureShare { get; set; } = 0.6f;
}

/// <summary>
/// Reconnaissance from the air: drones and aircraft sent to look instead of to strike.
///
/// Every flying formation has a recon figure in common/support, and until now it was a
/// number on a card. Given the "reconnaissance" mission, a formation photographs the
/// enemy's ground inside its reach - all of it for a drone group built for the work (recon
/// six), a share of the radius for a fighter wing passing over - and the fog of war lifts
/// there for as long as it flies. Only over countries the viewer is at war with: in peacetime
/// the sky over a neighbour is the neighbour's.
///
/// And it is not free. A flight over enemy ground meets the enemy's fighters and missile
/// brigades the way a strike does - a little less, since it does not loiter over the battle -
/// and pays for it from the same account (SupportCore.Attrition): a reconnaissance wing sent
/// over a defended front is worn down over weeks, one whose sky has been swept is not.
/// </summary>
public static class AirReconnaissance
{
    public const string Mission = "reconnaissance";

    /// <summary>How far the formation sees, in km: its reach, scaled by its sensors and its readiness.</summary>
    public static float SightKm(Campaign c, SupportFormation formation, SupportTypeDefinition type)
    {
        var sensors = M.Clamp(type.Recon / MathF.Max(0.1f, c.Defines.ReconFullSight), M.Clamp01(c.Defines.ReconMinimumSight), 1f);
        return type.RangeKm * sensors * (0.5f + 0.5f * M.Clamp01(formation.Strength));
    }

    /// <summary>The map indices of every country the viewer is at war with.</summary>
    private static HashSet<int> Enemies(Campaign c, int viewerId)
    {
        var enemies = new HashSet<int>();
        foreach (var war in Warfare.OpenWars(c))
        {
            if (war.Ended || !war.Involves(viewerId)) continue;
            var attacking = war.Side(true).Contains(viewerId);
            foreach (var enemyId in war.Side(!attacking))
                if (c.NationById(enemyId) is { } enemy) enemies.Add(enemy.MapIndex);
        }
        return enemies;
    }

    private static bool Flying(Campaign c, SupportFormation formation, out SupportTypeDefinition type)
    {
        type = null;
        return formation.Mission == Mission && formation.BaseProvinceId > 0 &&
               SupportTypeCatalog.TryGet(formation.TypeId, out type) && SupportCore.Enabled(c, type) &&
               type.Kind is SupportKind.Air or SupportKind.Drone && !SupportCore.Displaced(c, formation);
    }

    /// <summary>Lift the fog over the enemy ground the viewer's reconnaissance flights reach: seen, not seen past.</summary>
    internal static void RevealInto(Campaign c, int viewerId, byte[] reach)
    {
        if (c.ProvinceMap is null || !c.SupportByNation.TryGetValue(viewerId, out var formations)) return;
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        HashSet<int> enemies = null;
        List<int> hidden = null;
        foreach (var formation in formations)
        {
            if (!Flying(c, formation, out var type)) continue;
            if (enemies is null)
            {
                enemies = Enemies(c, viewerId);
                if (enemies.Count == 0) return;
                // The enemy ground still in the fog, read off the map once for all the flights.
                // Each flight used to walk all 34,000 provinces asking a hash set whose each
                // one was - a third of a millisecond a formation, and while a war's flights
                // were up nearly half of every rebuild of a country's sight (intel.visibility),
                // which the advance asks for again every hour. What a flight lifts the fog from
                // is unchanged: a province another flight has already shown is passed over
                // here as it was passed over there.
                hidden = HiddenEnemyGround(reach, controllers, enemies);
            }
            var sight = SightKm(c, formation, type);
            var origin = map.CenterOf(formation.BaseProvinceId);
            // A degree of latitude is 111 km anywhere: most of the map is out of sight by that alone.
            var band = sight / 111f;
            for (var i = 0; i < hidden.Count; i++)
            {
                var p = hidden[i];
                if (reach[p] > 0) continue;
                if (MathF.Abs(map.CenterOf(p).Latitude - origin.Latitude) > band) continue;
                if (SupportCore.DistanceKm(c, formation.BaseProvinceId, p) <= sight) reach[p] = 1;
            }
        }
    }

    [ThreadStatic] private static List<int> _hiddenScratch;
    [ThreadStatic] private static bool[] _enemyMapScratch;

    /// <summary>Every province an enemy controls that the viewer does not yet see, in ascending order.</summary>
    private static List<int> HiddenEnemyGround(byte[] reach, ushort[] controllers, HashSet<int> enemies)
    {
        var hidden = _hiddenScratch ??= new List<int>();
        hidden.Clear();
        // A controller is sixteen bits, so the set is a table for the walk. An index outside
        // that range can control nothing on this map and is left out, as the set left it unmatched.
        var isEnemy = _enemyMapScratch ??= new bool[ushort.MaxValue + 1];
        foreach (var index in enemies)
            if ((uint)index < (uint)isEnemy.Length) isEnemy[index] = true;
        var end = Math.Min(reach.Length, controllers.Length);
        for (var p = 1; p < end; p++)
            if (reach[p] == 0 && isEnemy[controllers[p]]) hidden.Add(p);
        foreach (var index in enemies)
            if ((uint)index < (uint)isEnemy.Length) isEnemy[index] = false;
        return hidden;
    }

    /// <summary>
    /// What a day of flying costs: the sky over the nearest enemy ground in sight, charged to the
    /// formation's account before the day's losses are counted.
    /// </summary>
    internal static void Expose(Campaign c)
    {
        if (c.ProvinceMap is null) return;
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        foreach (var (nationId, formations) in c.SupportByNation)
        {
            HashSet<int> enemies = null;
            foreach (var formation in formations)
            {
                if (!Flying(c, formation, out var type)) continue;
                enemies ??= Enemies(c, nationId);
                if (enemies.Count == 0) break;
                var sight = SightKm(c, formation, type);
                var origin = map.CenterOf(formation.BaseProvinceId);
                var band = sight / 111f;
                var nearest = -1;
                var nearestKm = float.MaxValue;
                for (var p = 1; p < controllers.Length && p <= map.ProvinceCount; p++)
                {
                    if (!enemies.Contains(controllers[p])) continue;
                    if (MathF.Abs(map.CenterOf(p).Latitude - origin.Latitude) > band) continue;
                    var km = SupportCore.DistanceKm(c, formation.BaseProvinceId, p);
                    if (km <= sight && km < nearestKm) { nearestKm = km; nearest = p; }
                }
                if (nearest < 0) continue;
                var enemyIds = new List<int>();
                var ownIds = new List<int> { nationId };
                foreach (var war in Warfare.OpenWars(c))
                {
                    if (war.Ended || !war.Involves(nationId)) continue;
                    var attacking = war.Side(true).Contains(nationId);
                    enemyIds.AddRange(war.Side(!attacking));
                    ownIds.AddRange(war.Side(attacking));
                }
                var threat = SupportCore.ThreatTo(type, Mission, SupportCore.ReachOf(c, ownIds, nearest), SupportCore.ReachOf(c, enemyIds, nearest)) *
                             M.Clamp01(c.Defines.ReconExposureShare);
                if (threat <= 0f) continue;
                if (!c.SupportExposure.TryGetValue(formation.Id, out var worst) || threat > worst)
                    c.SupportExposure[formation.Id] = threat;
            }
        }
    }
}
