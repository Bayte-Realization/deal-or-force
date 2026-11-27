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

    private static bool Flying(Campaign c, SupportFormation formation, out SupportTypeDefinition type)
    {
        type = null;
        return formation.Mission == Mission && formation.BaseProvinceId > 0 &&
               SupportTypeCatalog.TryGet(formation.TypeId, out type) && SupportCore.Enabled(c, type) &&
               type.Kind is SupportKind.Air or SupportKind.Drone && !SupportCore.Displaced(c, formation);
    }

    [ThreadStatic] private static HashSet<int> _revealEnemiesScratch;

    /// <summary>Lift the fog over the enemy ground the viewer's reconnaissance flights reach: seen, not seen past.</summary>
    internal static void RevealInto(Campaign c, int viewerId, byte[] reach)
    {
        if (c.ProvinceMap is null || !c.SupportByNation.TryGetValue(viewerId, out var formations)) return;
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        HashSet<int> enemies = null;
        List<int> hidden = null;
        for (var fi = 0; fi < formations.Count; fi++)
        {
            var formation = formations[fi];
            if (!Flying(c, formation, out var type)) continue;
            if (enemies is null)
            {
                enemies = _revealEnemiesScratch ??= new HashSet<int>();
                enemies.Clear();
                foreach (var war in Warfare.OpenWars(c))
                {
                    if (war.Ended || !war.Involves(viewerId)) continue;
                    var attacking = war.IsAttacking(viewerId);
                    foreach (var enemyId in war.Side(!attacking))
                        if (c.NationById(enemyId) is { } enemy) enemies.Add(enemy.MapIndex);
                }
                if (enemies.Count == 0) return;
                // Walk only the provinces each enemy actually controls via CampaignIntel's
                // cached per-controller grouping instead of scanning all 35,000 map provinces.
                hidden = HiddenEnemyGround(c, reach, controllers, enemies);
            }
            if (hidden.Count == 0) return;
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

    /// <summary>Every province an enemy controls that the viewer does not yet see.</summary>
    private static List<int> HiddenEnemyGround(Campaign c, byte[] reach, ushort[] controllers, HashSet<int> enemies)
    {
        var hidden = _hiddenScratch ??= new List<int>();
        hidden.Clear();
        var len = reach.Length;
        foreach (var enemyMapIndex in enemies)
        {
            var held = CampaignIntel.ProvincesHeldBy(c, controllers, enemyMapIndex);
            for (var i = 0; i < held.Length; i++)
            {
                var p = held[i];
                if ((uint)p < (uint)len && reach[p] == 0) hidden.Add(p);
            }
        }
        return hidden;
    }

    /// <summary>
    /// What a day of flying costs: the sky over the nearest enemy ground in sight, charged to the
    /// formation's account before the day's losses are counted.
    /// </summary>
    internal static void Expose(Campaign c)
    {
        foreach (var _ in ExposeStages(c)) { }
    }

    internal static IEnumerable<int> ExposeStages(Campaign c)
    {
        if (c.ProvinceMap is null || c.SupportByNation.Count == 0) yield break;
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        var pass = c.Caches.SupportPass;
        var flights = pass.ExposeFlights;
        var enemyProvs = pass.ExposeEnemyProvs;
        var ownIds = pass.ExposeOwn;
        var enemyIds = pass.ExposeEnemy;
        var enemyMaps = pass.ExposeEnemyMaps;
        var ownReach = pass.ExposeOwnReach;
        var enemyReach = pass.ExposeEnemyReach;
        var openWars = Warfare.OpenWars(c);
        var exposureShare = M.Clamp01(c.Defines.ReconExposureShare);

        try
        {
            var nationsChecked = 0;
            foreach (var (nationId, formations) in c.SupportByNation)
            {
                if (++nationsChecked % 32 == 0 && Campaign.StageOverdue()) yield return 0;
                flights.Clear();
                for (var i = 0; i < formations.Count; i++)
                    if (Flying(c, formations[i], out _)) flights.Add(formations[i]);
                if (flights.Count == 0) continue;

                enemyMaps.Clear();
                ownIds.Clear();
                enemyIds.Clear();
                ownIds.Add(nationId);
                for (var wi = 0; wi < openWars.Count; wi++)
                {
                    var war = openWars[wi];
                    if (war.Ended || !war.Involves(nationId)) continue;
                    var attacking = war.IsAttacking(nationId);
                    foreach (var id in war.Side(attacking)) ownIds.Add(id);
                    foreach (var id in war.Side(!attacking))
                    {
                        enemyIds.Add(id);
                        if (c.NationById(id) is { } enemy) enemyMaps.Add(enemy.MapIndex);
                    }
                }
                if (enemyMaps.Count == 0) continue;

                var minLat = float.MaxValue;
                var maxLat = float.MinValue;
                var maxBand = 0f;
                for (var fi = 0; fi < flights.Count; fi++)
                {
                    var formation = flights[fi];
                    if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) continue;
                    var band = SightKm(c, formation, type) / 111f;
                    if (band > maxBand) maxBand = band;
                    var lat = map.CenterOf(formation.BaseProvinceId).Latitude;
                    if (lat < minLat) minLat = lat;
                    if (lat > maxLat) maxLat = lat;
                }
                var globalMinLat = minLat - maxBand;
                var globalMaxLat = maxLat + maxBand;

                enemyProvs.Clear();
                foreach (var enemyCode in enemyMaps)
                {
                    var held = CampaignIntel.ProvincesHeldBy(c, controllers, enemyCode);
                    for (var i = 0; i < held.Length; i++)
                    {
                        var p = held[i];
                        var pLat = map.CenterOf(p).Latitude;
                        if (pLat < globalMinLat || pLat > globalMaxLat) continue;
                        enemyProvs.Add((p, pLat));
                    }
                    if (Campaign.StageOverdue()) yield return 0;
                }
                if (enemyProvs.Count == 0) continue;
                if (Campaign.StageOverdue()) yield return 0;

                for (var fi = 0; fi < flights.Count; fi++)
                {
                    var formation = flights[fi];
                    if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) continue;
                    var sight = SightKm(c, formation, type);
                    var origin = map.CenterOf(formation.BaseProvinceId);
                    var band = sight / 111f;
                    var cosLat = MathF.Max(0.15f, MathF.Cos(origin.Latitude * (MathF.PI / 180f)));
                    var lonBand = band / cosLat + 0.5f;
                    var nearest = -1;
                    var nearestKm = float.MaxValue;
                    for (var i = 0; i < enemyProvs.Count; i++)
                    {
                        var (p, pLat) = enemyProvs[i];
                        if (MathF.Abs(pLat - origin.Latitude) <= band)
                        {
                            var center = map.CenterOf(p);
                            var dLon = MathF.Abs(center.Longitude - origin.Longitude);
                            if (dLon <= lonBand || 360f - dLon <= lonBand)
                            {
                                var km = SupportCore.DistanceKm(c, formation.BaseProvinceId, p);
                                if (km <= sight && (km < nearestKm || (km == nearestKm && p < nearest)))
                                {
                                    nearestKm = km;
                                    nearest = p;
                                }
                            }
                        }
                        if ((i & 511) == 511 && Campaign.StageOverdue()) yield return 0;
                    }
                    if (nearest >= 0)
                    {
                        SupportCore.FillReachOf(c, ownIds, nearest, ownReach);
                        SupportCore.FillReachOf(c, enemyIds, nearest, enemyReach);
                        var threat = SupportCore.ThreatTo(type, Mission, ownReach, enemyReach) * exposureShare;
                        if (threat > 0f && (!c.SupportExposure.TryGetValue(formation.Id, out var worst) || threat > worst))
                            c.SupportExposure[formation.Id] = threat;
                    }
                    if (fi + 1 < flights.Count && Campaign.StageOverdue()) yield return 0;
                }
                if (Campaign.StageOverdue()) yield return 0;
            }
        }
        finally
        {
            flights.Clear();
            enemyProvs.Clear();
            ownIds.Clear();
            enemyIds.Clear();
            enemyMaps.Clear();
        }
    }
}
