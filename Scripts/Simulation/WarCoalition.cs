using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
        if (controller == 0) return false;
        if (c.NationById(nationId) is { } own && controller == own.MapIndex) return true;
        if (IsHostileControllerAnyWar(c, nationId, controller)) return false;
        if (MilitaryAccess.CanEnter(c, nationId, controller)) return true;
        if (war is null || war.Ended || !war.Involves(nationId)) return false;
        foreach (var id in war.Side(war.IsAttacking(nationId)))
            if (c.NationById(id) is { } nation && controller == nation.MapIndex) return true;
        return false;
    }

    /// <summary>True if nation may stage a front post on province (own, war ally, subject/overlord, or military access; never hostile).</summary>
    public static bool CanHoldFrontPost(Campaign c, Nation nation, int province)
    {
        var controls = c?.ProvinceController ?? c?.ProvinceMap?.ProvinceToNation;
        if (nation is null || controls is null || province <= 0 || province > c.ProvinceMap.ProvinceCount ||
            province >= controls.Length || c.ProvinceMap.IsWater(province)) return false;
        var controller = controls[province];
        return controller != 0 && (controller == (ushort)nation.MapIndex || MilitaryAccess.CanEnter(c, nation.Id, controller));
    }

    public static StrategicWar FindWarAgainstController(Campaign c, int nationId, ushort controller)
    {
        if (c is null || controller == 0 || !c.NationsByMapIndex().TryGetValue(controller, out var holder) || holder.Id == nationId)
            return null;
        var live = Warfare.LiveWars(c);
        for (var i = 0; i < live.Count; i++)
            if (!live[i].Ended && live[i].Opposes(nationId, holder.Id)) return live[i];
        return null;
    }

    public static bool IsHostileControllerAnyWar(Campaign c, int nationId, ushort controller) =>
        c is not null && controller != 0 && c.NationsByMapIndex().TryGetValue(controller, out var holder) &&
        holder.Id != nationId && MilitaryAccess.Hostile(c, nationId, holder.Id);

    internal static StrategicWar AssignedWar(Campaign c, Army army)
    {
        if (army.AssignedWarId < 0) return null;
        foreach (var war in Warfare.OpenWars(c))
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
    private sealed class CoalitionCache
    {
        public long RouteStamp;
        public readonly Dictionary<(int Nation, int From, int To), int[]> RouteCache = new();
        public readonly Dictionary<(int War, int Nation), HashSet<ushort>> AllowedCache = new();
        public long AllowedHour = -1;
        public int AllowedWarRevision = -1;
        public readonly Dictionary<(int Nation, int War), (int Belligerents, HashSet<ushort> Set)> OperationalAllowed = new();
        public readonly object AllowedGate = new();
    }

    private static readonly ConditionalWeakTable<Campaign, CoalitionCache> Caches = new();

    internal static void InvalidateRoutes(Campaign c)
    {
        if (c is null || !Caches.TryGetValue(c, out var cache)) return;
        cache.RouteCache.Clear();
        cache.AllowedCache.Clear();
        lock (cache.AllowedGate) cache.OperationalAllowed.Clear();
    }

    internal static int[] FriendlyRoute(Campaign c, StrategicWar war, int nationId, int from, int to)
    {
        if (c.ProvinceMap is null || c.ProvinceController is null || c.NationById(nationId) is not { } own)
            return Array.Empty<int>();
        // Wars join and leave without touching the map, so the stamp carries the war
        // revision too: a side change mid-day invalidates the allowed set in the solo run
        // exactly as it does in a run sharing the cache with another campaign.
        var cache = Caches.GetOrCreateValue(c);
        var stamp = ((long)c.ElapsedDays << 24) ^ (uint)c.MapRevision ^ ((long)c.WarRevision << 40);
        if (cache.RouteStamp != stamp)
        {
            cache.RouteCache.Clear();
            cache.AllowedCache.Clear();
            cache.RouteStamp = stamp;
        }
        if (cache.RouteCache.Count > 8192) cache.RouteCache.Clear();
        var key = (nationId, from, to);
        if (cache.RouteCache.TryGetValue(key, out var cached)) return cached;
        int[] route;
        {
            var allowedKey = (war?.Id ?? -1, nationId);
            if (!cache.AllowedCache.TryGetValue(allowedKey, out var allowed))
            {
                allowed = MilitaryAccess.Controllers(c, nationId);
                if (war is not null && !war.Ended) FillControllers(c, war, war.IsAttacking(nationId), allowed);
                cache.AllowedCache[allowedKey] = allowed;
            }
            route = c.ProvinceMap.FindOperationalRoute(from, to, allowed, c.ProvinceController);
        }
        return cache.RouteCache[key] = route;
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

    /// <summary>Populates <paramref name="destination"/> with the operational corridor without allocating an array.</summary>
    internal static void FillOperationalRoute(Campaign c, StrategicWar war, int nationId,
        int from, int to, ushort[] controllers, List<int> destination)
    {
        if (destination is null) return;
        if (c?.ProvinceMap is null || war is null || war.Ended || !war.Involves(nationId))
        {
            destination.Clear();
            return;
        }
        c.ProvinceMap.FillOperationalRoute(from, to, AllowedFor(c, war, nationId), controllers, destination);
    }

    /// <summary>
    /// Where a nation's divisions may go in a war: every controller it may enter, and both sides
    /// of the war. Built once per nation, war and game hour - it was built afresh for every
    /// division's every search, asking every nation in the list each time.
    /// </summary>
    private static HashSet<ushort> AllowedFor(Campaign c, StrategicWar war, int nationId)
    {
        var cache = Caches.GetOrCreateValue(c);
        lock (cache.AllowedGate)
        {
            if (cache.AllowedHour != c.ElapsedHours || cache.AllowedWarRevision != c.WarRevision)
            {
                cache.AllowedHour = c.ElapsedHours;
                cache.AllowedWarRevision = c.WarRevision;
                cache.OperationalAllowed.Clear();
            }
            if (cache.OperationalAllowed.TryGetValue((nationId, war.Id), out var cached) &&
                cached.Belligerents == war.BelligerentCount)
                return cached.Set;
            var allowed = cached.Set ?? new HashSet<ushort>();
            allowed.Clear();
            MilitaryAccess.FillControllers(c, nationId, allowed);
            FillControllers(c, war, true, allowed);
            FillControllers(c, war, false, allowed);
            cache.OperationalAllowed[(nationId, war.Id)] = (war.BelligerentCount, allowed);
            return allowed;
        }
    }

    internal static bool EnemyStandsIn(Campaign c, StrategicWar war, int nationId, int province)
    {
        foreach (var unit in c.Armies)
        {
            if (unit.ProvinceId != province || unit.Divisions < .1f || unit.NationId == nationId) continue;
            if ((war is not null && war.Opposes(nationId, unit.NationId)) ||
                MilitaryAccess.Hostile(c, nationId, unit.NationId))
                return true;
        }
        return false;
    }

    /// <summary>Hard invariant: a retreating division may ONLY enter non-hostile, friendly-controlled land with no enemy troops.</summary>
    internal static bool CanRetreatTo(Campaign c, StrategicWar war, int nationId, int province)
    {
        var map = c?.ProvinceMap;
        var controls = c?.ProvinceController;
        if (map is null || controls is null || province <= 0 || province > map.ProvinceCount ||
            province >= controls.Length || map.IsWater(province)) return false;
        var controller = controls[province];
        if (controller == 0 || IsHostileControllerAnyWar(c, nationId, controller) ||
            HostileController(c, war, nationId, controller)) return false;
        return FriendlyController(c, war, nationId, controller) && !EnemyStandsIn(c, war, nationId, province);
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
            if (!CanRetreatTo(c, war, army.NationId, p)) continue;
            if (controls[p] == own.MapIndex)
            {
                if (!lowestId) return p;
                if (ownRetreat == 0 || p < ownRetreat) ownRetreat = p;
            }
            else if (allied == 0 || (lowestId && p < allied)) allied = p;
        }
        return ownRetreat > 0 ? ownRetreat : allied;
    }

    /// <summary>
    /// Intelligently select a safe retreat province that spreads retreating units across
    /// the frontline rather than bunching them all into a single lowest-id tile.
    /// </summary>
    internal static int SpreadRetreatProvince(Campaign c, StrategicWar war, Army army, int from, bool beaten = false) =>
        SpreadRetreatProvince(c, war, army, from, beaten, slipAway: false, out _);

    /// <summary>
    /// Select a valid friendly retreat province for a retreating or broken formation.
    /// Hard invariant: NEVER returns an enemy-controlled or non-friendly province. When no
    /// friendly non-hostile neighbor is available, returns 0 so encircled divisions surrender cleanly.
    /// </summary>
    internal static int SpreadRetreatProvince(Campaign c, StrategicWar war, Army army, int from, bool beaten,
        bool slipAway, out bool slipped)
    {
        slipped = false;
        var map = c.ProvinceMap;
        var controls = c.ProvinceController;
        var own = c.NationById(army.NationId);
        if (map is null || controls is null || own is null || from <= 0 || from >= map.Neighbors.Length) return 0;

        var bestProvince = 0;
        var bestScore = float.MinValue;

        foreach (var p in map.Neighbors[from])
        {
            if (!CanRetreatTo(c, war, army.NationId, p)) continue;
            var isOwn = controls[p] == own.MapIndex;
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
        if (bestProvince > 0 || !beaten) return bestProvince;
        if (!slipAway) return 0;
        var away = Partisans.SlipAwayProvince(c, war, army, from);
        if (away <= 0 || !CanRetreatTo(c, war, army.NationId, away)) return 0;
        slipped = true;
        return away;
    }
}
