using System;
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
        if (MilitaryAccess.CanEnter(c, nationId, controller)) return true;
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

    // The staff asks the same question dozens of times a day - one route per candidate per
    // hole per arrangement - so the answers live for the day and die with the first border
    // change (a capture bumps the map revision). Same inputs, same answer; only the work
    // is shared.
    private static Campaign _routeCacheFor;
    private static long _routeCacheStamp;
    private static readonly Dictionary<(int Nation, int From, int To), int[]> RouteCache = new();
    private static readonly Dictionary<(int War, int Nation), HashSet<ushort>> AllowedCache = new();

    internal static void InvalidateRoutes(Campaign c)
    {
        if (!ReferenceEquals(_routeCacheFor, c)) return;
        RouteCache.Clear(); AllowedCache.Clear();
    }

    internal static int[] FriendlyRoute(Campaign c, StrategicWar war, int nationId, int from, int to)
    {
        if (c.ProvinceMap is null || c.ProvinceController is null || c.NationById(nationId) is not { } own)
            return Array.Empty<int>();
        // Wars join and leave without touching the map, so the stamp carries the war
        // revision too: a side change mid-day invalidates the allowed set in the solo run
        // exactly as it does in a run sharing the cache with another campaign.
        var stamp = ((long)c.ElapsedDays << 24) ^ (uint)c.MapRevision ^ ((long)c.WarRevision << 40);
        if (!ReferenceEquals(_routeCacheFor, c) || _routeCacheStamp != stamp)
        {
            RouteCache.Clear();
            AllowedCache.Clear();
            _routeCacheFor = c;
            _routeCacheStamp = stamp;
        }
        if (RouteCache.Count > 8192) RouteCache.Clear();
        var key = (nationId, from, to);
        if (RouteCache.TryGetValue(key, out var cached)) return cached;
        int[] route;
        {
            var allowedKey = (war?.Id ?? -1, nationId);
            if (!AllowedCache.TryGetValue(allowedKey, out var allowed))
            {
                allowed = MilitaryAccess.Controllers(c, nationId);
                if (war is not null && !war.Ended) FillControllers(c, war, war.IsAttacking(nationId), allowed);
                AllowedCache[allowedKey] = allowed;
            }
            route = c.ProvinceMap.FindOperationalRoute(from, to, allowed, c.ProvinceController);
        }
        return RouteCache[key] = route;
    }

    internal static bool SafeRetreatProvince(Campaign c, StrategicWar war, int nationId, int province)
    {
        if (c.ProvinceMap is null || c.ProvinceController is null || province <= 0 ||
            province > c.ProvinceMap.ProvinceCount || c.ProvinceMap.IsWater(province) ||
            !FriendlyController(c, war, nationId, c.ProvinceController[province])) return false;
        foreach (var battle in c.Battles)
            if (!battle.Ended && battle.ProvinceId == province) return false;
        foreach (var unit in c.Armies)
            if (unit.ProvinceId == province && unit.Divisions >= .1f &&
                war is not null && war.Opposes(nationId, unit.NationId)) return false;
        return true;
    }

    /// <summary>An offensive corridor may cross the war's countries, never a neutral shortcut.</summary>
    public static int[] OperationalRoute(Campaign c, StrategicWar war, int nationId,
        int from, int to, ushort[] controllers)
    {
        if (c?.ProvinceMap is null || war is null || war.Ended || !war.Involves(nationId))
            return System.Array.Empty<int>();
        return c.ProvinceMap.FindOperationalRoute(from, to, AllowedFor(c, war, nationId), controllers);
    }

    private static Campaign _allowedCampaign;
    private static long _allowedHour = -1;
    private static readonly Dictionary<(int Nation, int War), (int Belligerents, HashSet<ushort> Set)> OperationalAllowed = new();
    private static readonly object AllowedGate = new();

    /// <summary>
    /// Where a nation's divisions may go in a war: every controller it may enter, and both sides
    /// of the war. Built once per nation, war and game hour - it was built afresh for every
    /// division's every search, asking every nation in the list each time.
    /// </summary>
    private static HashSet<ushort> AllowedFor(Campaign c, StrategicWar war, int nationId)
    {
        lock (AllowedGate)
        {
            if (!ReferenceEquals(_allowedCampaign, c) || _allowedHour != c.ElapsedHours)
            {
                _allowedCampaign = c;
                _allowedHour = c.ElapsedHours;
                OperationalAllowed.Clear();
            }
            if (OperationalAllowed.TryGetValue((nationId, war.Id), out var cached) && cached.Belligerents == war.BelligerentCount)
                return cached.Set;
            var allowed = MilitaryAccess.Controllers(c, nationId);
            FillControllers(c, war, true, allowed);
            FillControllers(c, war, false, allowed);
            OperationalAllowed[(nationId, war.Id)] = (war.BelligerentCount, allowed);
            return allowed;
        }
    }

    private static bool EnemyStandsIn(Campaign c, StrategicWar war, int nationId, int province)
    {
        if (war is null) return false;
        foreach (var unit in c.Armies)
            if (unit.ProvinceId == province && unit.Divisions >= .1f && war.Opposes(nationId, unit.NationId)) return true;
        return false;
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

    /// <summary>
    /// Intelligently select a safe retreat province that spreads retreating units across
    /// the frontline rather than bunching them all into a single lowest-id tile.
    /// </summary>
    internal static int SpreadRetreatProvince(Campaign c, StrategicWar war, Army army, int from, bool beaten = false)
    {
        var map = c.ProvinceMap;
        var controls = c.ProvinceController;
        var own = c.NationById(army.NationId);
        if (map is null || controls is null || own is null || from <= 0 || from >= map.Neighbors.Length) return 0;

        var bestProvince = 0;
        var bestScore = float.MinValue;

        foreach (var p in map.Neighbors[from])
        {
            if (map.IsWater(p)) continue;
            var isOwn = controls[p] == own.MapIndex;
            var isAllied = !isOwn && FriendlyController(c, war, army.NationId, controls[p]);
            if (!isOwn && !isAllied || EnemyStandsIn(c, war, army.NationId, p)) continue;
            // A formation choosing to step out of a fight goes somewhere quiet. One that has just
            // been beaten out of its province falls back onto its own side's line even where that
            // line is under attack - a province with a battle in it is still held - and it used
            // to be refused that too: on a front where every province was fighting, every beaten
            // division surrendered whole, 1.6 million Ukrainian prisoners in eight months.
            var safe = SafeRetreatProvince(c, war, army.NationId, p);
            if (!safe && !beaten) continue;

            // Score: own ground strongly preferred, quiet ground over a fight.
            var score = isOwn ? 100f : 50f;
            if (!safe) score -= 40f;

            // Count friendly formations already in or heading to this province
            var crowding = 0;
            foreach (var other in c.Armies)
            {
                if (other.Id == army.Id || other.NationId != army.NationId) continue;
                if (other.ProvinceId == p || (other.Route.Count > 0 && other.OrderProvinceId == p))
                    crowding++;
            }
            score -= crowding * 15f;

            // Give a bonus if this province borders an enemy and is currently unheld / under-defended
            var bordersEnemy = false;
            foreach (var n in map.Neighbors[p])
            {
                if (map.IsWater(n)) continue;
                if (HostileController(c, war, army.NationId, controls[n])) { bordersEnemy = true; break; }
            }
            if (bordersEnemy && crowding == 0) score += 25f;

            // Deterministic tie-breaker: prefer lowest ID if score is equal
            if (score > bestScore || (MathF.Abs(score - bestScore) < 0.001f && (bestProvince == 0 || p < bestProvince)))
            {
                bestScore = score;
                bestProvince = p;
            }
        }
        if (bestProvince > 0 || !beaten || c.Defines.RetreatSearchDepth <= 1) return bestProvince;
        return DeepRetreatProvince(c, war, army, from, c.Defines.RetreatSearchDepth);
    }

    /// <summary>
    /// A beaten formation with no friendly ground next to it breaks out towards its own line: the
    /// nearest province its side holds and no enemy stands in, through a legal, undefended
    /// corridor. Return only its FIRST step, never teleport across the whole corridor.
    /// </summary>
    private static int DeepRetreatProvince(Campaign c, StrategicWar war, Army army, int from, int depth)
    {
        var map = c.ProvinceMap;
        var controls = c.ProvinceController;
        var own = c.NationById(army.NationId);
        var seen = new HashSet<int> { from };
        var ring = new List<int> { from };
        var firstStep = new Dictionary<int, int>();
        var allowed = MilitaryAccess.Controllers(c, army.NationId);
        if (war is not null && !war.Ended && war.Involves(army.NationId))
        {
            FillControllers(c, war, true, allowed);
            FillControllers(c, war, false, allowed);
        }
        for (var step = 1; step <= depth && ring.Count > 0; step++)
        {
            var next = new List<int>();
            foreach (var at in ring)
                foreach (var p in map.Neighbors[at])
                {
                    if (map.IsWater(p) || !allowed.Contains(controls[p]) || !seen.Add(p) ||
                        EnemyStandsIn(c, war, army.NationId, p)) continue;
                    firstStep[p] = at == from ? p : firstStep[at];
                    next.Add(p);
                }
            next.Sort();
            foreach (var p in next)
                if (controls[p] == own.MapIndex || FriendlyController(c, war, army.NationId, controls[p]))
                    return firstStep[p];
            ring = next;
        }
        return 0;
    }
}
