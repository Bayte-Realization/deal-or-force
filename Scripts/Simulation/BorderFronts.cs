using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A front is anchored to a country pair and geographic endpoints, not old province IDs.</summary>
public static class BorderFronts
{
    public static HashSet<int> Candidates(Campaign campaign, int nationId, int foreignId)
    {
        var result = new HashSet<int>();
        var map = campaign.ProvinceMap;
        var own = campaign.NationById(nationId);
        var other = campaign.NationById(foreignId);
        if (map is null || own is null || other is null || own == other) return result;
        var control = campaign.ProvinceController ?? map.ProvinceToNation;
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (!map.IsWater(p) && control[p] == own.MapIndex &&
                map.Neighbors[p].Any(n => !map.IsWater(n) && control[n] == other.MapIndex)) result.Add(p);
        return result;
    }

    public static int ForeignAt(Campaign campaign, int nationId, int province)
    {
        var map = campaign.ProvinceMap;
        var nation = campaign.NationById(nationId);
        if (map is null || nation is null || province <= 0 || province > map.ProvinceCount) return -1;
        var control = campaign.ProvinceController ?? map.ProvinceToNation;
        var owner = control[province];
        if (owner != nation.MapIndex && !map.Neighbors[province].Any(n => control[n] == nation.MapIndex)) return -1;
        var foreign = owner != nation.MapIndex && map.Neighbors[province].Any(n => control[n] == nation.MapIndex)
            ? owner : map.Neighbors[province].Where(n => !map.IsWater(n) && control[n] != 0 && control[n] != nation.MapIndex)
                .Select(n => control[n]).OrderBy(n => n).FirstOrDefault();
        return campaign.Nations.FirstOrDefault(n => n.MapIndex == foreign)?.Id ?? -1;
    }

    /// <summary>
    /// Everybody this country is fighting, in any war: the other side's leader and every
    /// co-belligerent on it, war by war. Filled into a set the caller owns.
    /// </summary>
    public static void FillEnemies(Campaign campaign, int nationId, HashSet<int> into)
    {
        into.Clear();
        var live = Warfare.LiveWars(campaign);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war.Ended || !war.Involves(nationId)) continue;
            foreach (var enemy in war.EnemiesOf(nationId))
                if (enemy != nationId) into.Add(enemy);
        }
    }

    /// <summary>
    /// A fingerprint of who this country is fighting, so a whole front knows when to be worked
    /// out again. The map revision alone is not enough: a war can begin or end, and an ally can
    /// join the other side, without a single province changing hands.
    /// </summary>
    public static long EnemyStamp(Campaign campaign, int nationId)
    {
        long stamp = 17;
        var count = 0;
        var live = Warfare.LiveWars(campaign);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war.Ended || !war.Involves(nationId)) continue;
            foreach (var enemy in war.EnemiesOf(nationId))
            {
                if (enemy == nationId) continue;
                // Order-free: the same enemies in another war order give the same stamp.
                stamp += unchecked((enemy + 1L) * 0x1E3779B97F4A7C15L) >> 7;
                count++;
            }
        }
        return stamp ^ ((long)count << 48);
    }

    private static readonly HashSet<int> WholeFrontEnemies = new();
    private static bool[] WholeFrontHostile = Array.Empty<bool>();

    /// <summary>
    /// The whole front: every land province this country holds that touches land held by
    /// anybody it is at war with. Held, not owned - ground we occupy is ours to stand on, and
    /// ground the enemy occupies is theirs to be faced across - which is what makes the line
    /// follow the fighting rather than the pre-war border. Separate stretches (a border with
    /// one enemy here, with another across a neutral country there) are all on it: the player
    /// asked for the front against everybody, wherever there is one.
    /// </summary>
    public static HashSet<int> WholeFront(Campaign campaign, int nationId)
    {
        var result = new HashSet<int>();
        var map = campaign.ProvinceMap;
        var own = campaign.NationById(nationId);
        if (map is null || own is null) return result;
        var control = campaign.ProvinceController ?? map.ProvinceToNation;
        FillEnemies(campaign, nationId, WholeFrontEnemies);
        if (WholeFrontEnemies.Count == 0) return result;
        var largest = 0;
        foreach (var nation in campaign.Nations) largest = Math.Max(largest, nation.MapIndex);
        if (WholeFrontHostile.Length <= largest) WholeFrontHostile = new bool[largest + 1];
        Array.Clear(WholeFrontHostile);
        foreach (var id in WholeFrontEnemies)
        {
            var enemy = campaign.NationById(id);
            // Map index 0 is nobody; an enemy without ground has no border to face.
            if (enemy is not null && enemy.MapIndex > 0 && enemy.MapIndex != own.MapIndex)
                WholeFrontHostile[enemy.MapIndex] = true;
        }
        var hostile = WholeFrontHostile;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (control[p] != own.MapIndex || map.IsWater(p)) continue;
            foreach (var n in map.Neighbors[p])
            {
                var other = control[n];
                if (other == 0 || other >= hostile.Length || !hostile[other] || map.IsWater(n)) continue;
                result.Add(p);
                break;
            }
        }
        return result;
    }

    /// <summary>An enemy at war with this country holding land next to this province, or -1.</summary>
    public static int EnemyAt(Campaign campaign, int nationId, int province)
    {
        var map = campaign.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return -1;
        var control = campaign.ProvinceController ?? map.ProvinceToNation;
        var best = -1;
        var live = Warfare.LiveWars(campaign);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war.Ended || !war.Involves(nationId)) continue;
            foreach (var enemyId in war.EnemiesOf(nationId))
            {
                var enemy = campaign.NationById(enemyId);
                if (enemy is null || enemy.MapIndex <= 0 || enemyId == nationId) continue;
                foreach (var n in map.Neighbors[province])
                    if (control[n] == enemy.MapIndex && !map.IsWater(n) && (best < 0 || enemyId < best))
                    {
                        best = enemyId;
                        break;
                    }
            }
        }
        return best;
    }

    /// <summary>
    /// The war a post on a whole front belongs to: the one whose own front for this country
    /// has it on, so a division standing on the stretch that faces one enemy is counted in
    /// the war against that enemy - and is handed back to the line, not stood down, when a
    /// different war ends. Null when no war claims the province.
    /// </summary>
    public static StrategicWar WarAt(Campaign campaign, int nationId, int province)
    {
        if (province <= 0) return null;
        var live = Warfare.LiveWars(campaign);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (!war.Ended && war.Involves(nationId) && war.FriendlyFront(nationId).Contains(province)) return war;
        }
        return null;
    }

    /// <summary>
    /// Divisions a peace has just stood down, put back on the whole front they were drawn
    /// onto if any of their country's wars still has one.
    ///
    /// The peace releases every division it counted as serving the war that ended, and a
    /// division on the whole front is counted as serving whichever war its stretch faced.
    /// Released, it would keep its place on the line with no order to hold it, and nothing
    /// would ever give it one - the call-up of idle divisions skips any division on a drawn
    /// line - so peace with one enemy would quietly strip the whole front of every division
    /// that had been facing that enemy. The same rule applies to every country's divisions.
    /// </summary>
    public static void ReturnToWholeFronts(Campaign campaign, IReadOnlyList<Army> released)
    {
        foreach (var army in released)
        {
            if (army.FrontLineId < 0 || army.OrderKind != ArmyOrder.Manual || army.DirectOrder) continue;
            var line = BattlePlanning.LineFor(campaign, army);
            if (line is not { AllEnemies: true, Kind: FrontLineKind.Defence }) continue;
            line.PostsDay = int.MinValue;
            new SetArmyOrderCommand { IssuerId = army.NationId, ArmyId = army.Id, Order = ArmyOrder.DefendFront }.Apply(campaign);
        }
    }

    public static HashSet<int> Resolve(Campaign campaign, DrawnFrontLine line)
    {
        if (line.AllEnemies) return WholeFront(campaign, line.NationId);
        var available = Candidates(campaign, line.NationId, line.BorderNationId);
        if (available.Count == 0) return available;
        var map = campaign.ProvinceMap;
        var start = Nearest(map, available, line.StartLatitude, line.StartLongitude);
        var end = Nearest(map, available, line.EndLatitude, line.EndLongitude);
        var queue = new Queue<int>();
        var previous = new Dictionary<int, int> { [start] = -1 };
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var next in map.Neighbors[p].OrderBy(n => n))
                if (available.Contains(next) && previous.TryAdd(next, p)) queue.Enqueue(next);
        }
        // Never bridge separate borders across sea or neutral land.
        if (line.WholeBorder) return previous.Keys.ToHashSet();
        if (!previous.ContainsKey(end)) end = Nearest(map, previous.Keys, line.EndLatitude, line.EndLongitude);
        var result = new HashSet<int>();
        for (var p = end; p > 0; p = previous[p]) result.Add(p);
        return result;
    }

    private static int Nearest(ProvinceTopology map, IEnumerable<int> candidates, float lat, float lon) =>
        candidates.OrderBy(p => Distance(map.CenterOf(p), lat, lon)).ThenBy(p => p).First();
    private static double Distance((float Latitude, float Longitude) point, float lat, float lon)
    {
        var dx = Math.Abs(point.Longitude - lon);
        dx = MathF.Min(dx, 360 - dx) * MathF.Cos(lat * MathF.PI / 180);
        var dy = point.Latitude - lat;
        return dx * dx + dy * dy;
    }

    public static void Refresh(Campaign campaign, DrawnFrontLine line)
    {
        if (line.Kind == FrontLineKind.Offensive && line.ObjectiveSequence.Count > 0 && line.BorderNationId >= 0)
        {
            if (line.BorderRevision == campaign.MapRevision || line.ActiveOffensivePhase > 0 ||
                OffensivePlanning.HasNextPhase(line)) return;
            line.SourceProvinces.Clear();
            line.SourceProvinces.UnionWith(Resolve(campaign, line));
            line.PostsDay = int.MinValue;
            line.BorderRevision = campaign.MapRevision;
            return;
        }
        if (line.Kind == FrontLineKind.Defence && line.AllEnemies)
        {
            // Again whenever the map moves or the list of enemies does, and only then: this
            // is asked for every division on the line every day.
            var stamp = EnemyStamp(campaign, line.NationId);
            if (line.BorderRevision == campaign.MapRevision && line.EnemyStamp == stamp) return;
            line.Provinces.Clear();
            line.Provinces.UnionWith(WholeFront(campaign, line.NationId));
            line.PostsDay = int.MinValue;
            line.BorderRevision = campaign.MapRevision;
            line.EnemyStamp = stamp;
            return;
        }
        if (line.Kind != FrontLineKind.Defence || line.BorderNationId < 0 ||
            line.BorderRevision == campaign.MapRevision) return;
        line.Provinces.Clear();
        line.Provinces.UnionWith(Resolve(campaign, line));
        line.PostsDay = int.MinValue;
        line.BorderRevision = campaign.MapRevision;
    }
}
