using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation.Kernels;

namespace DealOrForce.Simulation;

/// <summary>How one step of a long-range formation's route is made.</summary>
public enum SupportMoveMode
{
    /// <summary>By road, at a division's pace.</summary>
    Land = 0,
    /// <summary>By train: inland steps away from the front, at the railways' speed.</summary>
    Rail = 1,
    /// <summary>By ship, port to port, with the loading and unloading at each end.</summary>
    Sea = 2,
    /// <summary>A ferry flight in a straight line.</summary>
    Air = 3
}

public sealed partial class Defines
{
    /// <summary>Kilometres a transport carrying a brigade's equipment covers in a day: about ten knots.</summary>
    public float LongRangeSeaKmPerDay { get; set; } = 450f;
    /// <summary>Days of loading at the port of departure, and again of unloading at the port of arrival.</summary>
    public int LongRangePortDays { get; set; } = 2;
    /// <summary>Kilometres a wing relocates in a day. The aircraft are there in hours; the ground
    /// echelon, the fuel, the munitions and the technicians are what take the day.</summary>
    public float LongRangeAirKmPerDay { get; set; } = 1500f;
    /// <summary>A ferry flight's reach as a multiple of the combat reach: unarmed, with drop tanks.</summary>
    public float LongRangeFerryRangeFactor { get; set; } = 3f;
    /// <summary>The shortest ferry leg any aircraft gets: a helicopter is ferried 500-700 km.</summary>
    public float LongRangeFerryMinKm { get; set; } = 600f;
    /// <summary>The longest: past this it is air-to-air refuelling, which the game does not have.</summary>
    public float LongRangeFerryMaxKm { get; set; } = 4000f;
    /// <summary>Strength a formation loses, once, when the province it stands in falls to the enemy:
    /// the heavy and the broken that could not be got out in time.</summary>
    public float LongRangeOverrunLoss { get; set; } = 0.25f;
    /// <summary>Kilometres added to every reach: half a province, because the range is measured from
    /// centre to centre and the guns fire to the edge.</summary>
    public float LongRangeReachSlackKm { get; set; } = 35f;
    /// <summary>What any region bases without crowding, before barracks and cities.</summary>
    public int LongRangeBasingBase { get; set; } = 3;
    /// <summary>More basing for every barracks in the region.</summary>
    public int LongRangeBasingPerBarracks { get; set; } = 2;
    /// <summary>More basing for every leading city of the region its owner holds.</summary>
    public int LongRangeBasingPerCity { get; set; } = 2;
    /// <summary>Long-range formations one country may have in training at once.</summary>
    public int SupportTrainingSlots { get; set; } = 8;
    /// <summary>Days between the computer's reviews of where its long-range formations stand.</summary>
    public int AiLongRangeRepositionDays { get; set; } = 15;
    /// <summary>The share of its reach a formation the computer moves stands back from the front.</summary>
    public float AiLongRangeStandoffShare { get; set; } = 0.6f;
    /// <summary>Formations of shorter range than this may stand on the front itself; longer-ranged ones stand back.</summary>
    public float AiLongRangeFrontlineMaxRangeKm { get; set; } = 100f;
}

/// <summary>
/// Long-range formations on the map: where they stand, how they get somewhere else, what
/// happens when the ground under them is lost, and where the computer puts its own.
///
/// The owner, 17 November 2026: "they move instantly, through the air, without a trace". They
/// did - an order rewrote the base and the formation was there, across a country or a sea, the
/// same day; a lost base threw it into the nearest region with a free post, or off the map.
/// Now every move is a journey:
///
///   - guns, rocket and missile brigades, SAM brigades and drone crews drive - by road at a
///     division's pace, by train inland away from the front, by ship between ports when there
///     is no road - over their own ground and the ground of countries that let them in;
///   - aircraft fly straight ferry legs, each no longer than the type's ferry range;
///   - an order takes the formation off duty at once, and it works again only after its
///     deploy days at the destination;
///   - the province it stands in falling to the enemy costs it a share of its strength, once,
///     and it marches out - or, with nowhere to go, it is cut off where it stands, visible and
///     useless, looking for a way out every day.
///
/// Every day of movement is resolved in formation id order, and every search breaks its ties
/// on province id, so two peers given the same orders see the same columns on the same roads.
/// </summary>
public static partial class LongRangeMovement
{
    /// <summary>Telemetry for the bench (SimBench --long-range): read, never steered by.</summary>
    public static int AiOrders, AiOrdersTowardFront, AiCityCover, Withdrawals, CutOffs, Overruns;

    private const float Arrived = 0.9999f;
    // A land step left over from a fast day carries into the next one, but never a whole step:
    // one province a day is the most any column covers, by road or by rail.
    private const float MaxCarry = 0.999f;

    // ------------------------------------------------------------------ the ground a nation may use

    internal sealed class BattleSet
    {
        public static readonly BattleSet Empty = new(Array.Empty<ulong>(), 0, false);
        private readonly ulong[] _words;
        private readonly int _limit;
        private readonly bool _any;

        public BattleSet(ulong[] words, int limit, bool any)
        {
            _words = words;
            _limit = limit;
            _any = any;
        }

        public int WordCount => _any ? (_limit + 63) >> 6 : 0;
        public ReadOnlySpan<ulong> Words => _any ? _words.AsSpan(0, WordCount) : ReadOnlySpan<ulong>.Empty;

        public bool Contains(int province) =>
            _any && (uint)province < (uint)_limit && (_words[province >> 6] & (1UL << (province & 63))) != 0;
    }

    /// <summary>What one country may drive over and stand on, worked out once per pass.</summary>
    internal sealed class Ground
    {
        public Campaign Campaign;
        public Nation Nation;
        public ProvinceTopology Map;
        public ushort[] Controls;
        public readonly HashSet<ushort> Passable = new();
        public readonly HashSet<ushort> Hostile = new();
        public ulong[] PassableBits = Array.Empty<ulong>();
        public int PassableLimit;
        public ulong[] HostileBits = Array.Empty<ulong>();
        public int HostileLimit;
        public ulong[] BordersCheckedBits;
        public ulong[] BordersTrueBits;
        private bool _bordersDirty;
        public BattleSet Battles = BattleSet.Empty;
        public ulong PassableHash;
        public ulong HostileHash;

        public void ResetForReuse()
        {
            Campaign = null;
            Nation = null;
            Map = null;
            Controls = null;
            Passable.Clear();
            Hostile.Clear();
            PassableLimit = 0;
            HostileLimit = 0;
            PassableHash = 0;
            HostileHash = 0;
            _bordersDirty = true;
            Battles = BattleSet.Empty;
            _shut = null;
        }

        public bool IsPassable(ushort code) =>
            (uint)code < (uint)PassableLimit && (PassableBits[code >> 6] & (1UL << (code & 63))) != 0;

        public bool IsHostile(ushort code) =>
            (uint)code < (uint)HostileLimit && (HostileBits[code >> 6] & (1UL << (code & 63))) != 0;

        public bool Valid(int province) => province > 0 && province <= Map.ProvinceCount;
        public bool Destroyed(int province) => Map.DestroyedByProvince is { } destroyed && province < destroyed.Length && destroyed[province];
        public bool HostileHeld(int province) => Valid(province) && IsHostile(Controls[province]);

        /// <summary>Dry, standing, and held by us or by somebody who lets us in.</summary>
        public bool Enterable(int province) =>
            Valid(province) && !Map.IsWater(province) && !Destroyed(province) && IsPassable(Controls[province]);

        public bool BordersEnemy(int province)
        {
            if (!Valid(province) || HostileLimit <= 0) return false;
            var wordsNeeded = (Map.ProvinceCount + 65) >> 6;
            if (BordersCheckedBits is null || BordersCheckedBits.Length < wordsNeeded)
            {
                BordersCheckedBits = new ulong[wordsNeeded];
                BordersTrueBits = new ulong[wordsNeeded];
                _bordersDirty = false;
            }
            else if (_bordersDirty)
            {
                Array.Clear(BordersCheckedBits, 0, wordsNeeded);
                Array.Clear(BordersTrueBits, 0, wordsNeeded);
                _bordersDirty = false;
            }
            var w = province >> 6;
            var mask = 1UL << (province & 63);
            if ((BordersCheckedBits[w] & mask) != 0)
                return (BordersTrueBits[w] & mask) != 0;
            BordersCheckedBits[w] |= mask;
            foreach (var next in Map.Neighbors[province])
                if (!Map.IsWater(next) && IsHostile(Controls[next]))
                {
                    BordersTrueBits[w] |= mask;
                    return true;
                }
            return false;
        }

        public bool Own(int province) => Valid(province) && Controls[province] == Nation.MapIndex;

        /// <summary>
        /// Dry, standing ground nobody we are fighting holds: what a formation leaving lost
        /// ground may drive across on its way home. A region handed over at a peace is foreign
        /// ground the next morning, and the brigades standing deep inside it are not cut off by
        /// it - its new owner sees them out, as it sees a trespassing division out
        /// (MilitaryAccess.Tick). Only the enemy's ground makes a pocket.
        /// </summary>
        public bool Crossable(int province) =>
            Valid(province) && !Map.IsWater(province) && !Destroyed(province) && Controls[province] != 0 &&
            !IsHostile(Controls[province]);

        /// <summary>The next step of a route this formation may take: its side's ground, or, withdrawing, any ground but the enemy's.</summary>
        public bool Passing(SupportFormation f, int province) => Enterable(province) || f.Overrun && Crossable(province);

        private long? _shut;
        /// <summary>The straits shut to this country today (SeaLanes.ShutMask), asked once per pass.</summary>
        public long Shut => _shut ??= SeaLanes.ShutMask(Campaign, Nation.Id);
    }

    [ThreadStatic] private static bool[] _neededLandScratch;
    [ThreadStatic] private static int[] _firstLandScratch;
    [ThreadStatic] private static List<(SupportFormation Formation, SupportTypeDefinition Type, Nation Nation, ushort MapIndex)> _settlePendingScratch;
    [ThreadStatic] private static List<ushort> _settleKeysScratch;
    [ThreadStatic] private static Dictionary<int, int> _settleSeatScratch;

    internal static Ground GroundOf(Campaign c, int nationId, Dictionary<int, Ground> cache = null, BattleSet battles = null, bool[] activeControllers = null)
        => GroundOf(c, nationId, cache, battles, pool: null);

    internal static Ground GroundOf(Campaign c, int nationId, Dictionary<int, Ground> cache, BattleSet battles, SupportStepCache pool,
        MilitaryAccess.RouteRelations routeRelations = null)
    {
        if (cache is not null && cache.TryGetValue(nationId, out var known)) return known;
        var nation = c.NationById(nationId);
        var map = c.ProvinceMap;
        if (nation is null || map is null) return null;
        var ground = pool is not null ? pool.RentGround() : new Ground();
        ground.Campaign = c;
        ground.Nation = nation;
        ground.Map = map;
        ground.Controls = c.ProvinceController ?? map.ProvinceToNation;
        ground.Battles = battles ?? BattleProvinces(c);
        MilitaryAccess.FillControllers(c, nationId, ground.Passable, ground.Hostile, routeRelations);
        // Hostile ground is never passable, whatever an access grant once said.
        ground.Passable.ExceptWith(ground.Hostile);
        if (ground.Passable.Count > 0)
        {
            var maxP = 0;
            foreach (var code in ground.Passable) if (code > maxP) maxP = code;
            ground.PassableLimit = maxP + 1;
            var wordsNeeded = (maxP + 64) >> 6;
            if (ground.PassableBits.Length < wordsNeeded) ground.PassableBits = new ulong[wordsNeeded];
            else Array.Clear(ground.PassableBits, 0, wordsNeeded);
            foreach (var code in ground.Passable) ground.PassableBits[code >> 6] |= 1UL << (code & 63);
        }
        if (ground.Hostile.Count > 0)
        {
            var maxH = 0;
            foreach (var code in ground.Hostile) if (code > maxH) maxH = code;
            ground.HostileLimit = maxH + 1;
            var wordsNeeded = (maxH + 64) >> 6;
            if (ground.HostileBits.Length < wordsNeeded) ground.HostileBits = new ulong[wordsNeeded];
            else Array.Clear(ground.HostileBits, 0, wordsNeeded);
            foreach (var code in ground.Hostile) ground.HostileBits[code >> 6] |= 1UL << (code & 63);
        }
        ground.PassableHash = HashSortedCodes(ground.Passable);
        ground.HostileHash = HashSortedCodes(ground.Hostile);
        if (cache is not null) cache[nationId] = ground;
        return ground;
    }

    private const ulong FnvOffsetBasis64 = 14695981039346656037UL;
    private const ulong FnvPrime64 = 1099511628211UL;
    [ThreadStatic] private static List<ushort> _hashCodesScratch;

    private static ulong HashSortedCodes(HashSet<ushort> codes)
    {
        var hash = FnvOffsetBasis64;
        if (codes.Count == 0) return hash;
        var list = _hashCodesScratch ??= new List<ushort>(64);
        list.Clear();
        foreach (var code in codes) list.Add(code);
        list.Sort();
        hash ^= (uint)list.Count;
        hash *= FnvPrime64;
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            hash ^= (byte)c;
            hash *= FnvPrime64;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime64;
        }
        return hash;
    }

    internal static BattleSet BattleProvinces(Campaign c)
    {
        var battles = c.Battles;
        var count = battles.Count;
        var maxP = 0;
        for (var i = 0; i < count; i++)
        {
            var b = battles[i];
            if (!b.Ended && b.ProvinceId > maxP) maxP = b.ProvinceId;
        }
        if (maxP <= 0) return BattleSet.Empty;
        var limit = maxP + 1;
        var words = new ulong[(limit + 63) >> 6];
        for (var i = 0; i < count; i++)
        {
            var b = battles[i];
            if (!b.Ended && b.ProvinceId > 0)
                words[b.ProvinceId >> 6] |= 1UL << (b.ProvinceId & 63);
        }
        return new BattleSet(words, limit, true);
    }

    private static BattleSet BattleProvincesPooled(Campaign c, SupportStepCache pool)
    {
        var battles = c.Battles;
        var count = battles.Count;
        var maxP = 0;
        for (var i = 0; i < count; i++)
        {
            var b = battles[i];
            if (!b.Ended && b.ProvinceId > maxP) maxP = b.ProvinceId;
        }
        if (maxP <= 0) return BattleSet.Empty;
        var limit = maxP + 1;
        var wordsNeeded = (limit + 63) >> 6;
        if (pool.BattleWords.Length < wordsNeeded) pool.BattleWords = new ulong[wordsNeeded + 16];
        else Array.Clear(pool.BattleWords, 0, wordsNeeded);
        var words = pool.BattleWords;
        for (var i = 0; i < count; i++)
        {
            var b = battles[i];
            if (!b.Ended && b.ProvinceId > 0)
                words[b.ProvinceId >> 6] |= 1UL << (b.ProvinceId & 63);
        }
        return new BattleSet(words, limit, true);
    }

    // ------------------------------------------------------------------ where a formation is

    /// <summary>
    /// Fill in the position of formations written before positions existed: an old base is where
    /// the formation stands, deployed; a formation that had been parked off the map with no base
    /// at all is put down at the government's seat and starts deploying - deterministically, with
    /// no choice by dictionary order and no dice. And the region follows the province, always.
    /// </summary>
    public static void Settle(Campaign c)
    {
        var map = c?.ProvinceMap;
        if (map is null) return;
        var list = c.SupportFormations;
        if (list.Count == 0) return;
        var pending = _settlePendingScratch ??= new List<(SupportFormation, SupportTypeDefinition, Nation, ushort)>();
        var neededKeys = _settleKeysScratch ??= new List<ushort>();
        pending.Clear();
        neededKeys.Clear();
        var needed = _neededLandScratch ??= new bool[ushort.MaxValue + 1];

        for (var i = 0; i < list.Count; i++)
        {
            var f = list[i];
            if (f.ProvinceId > 0 && f.ProvinceId <= map.ProvinceCount)
            {
                var region = map.ProvinceToState[f.ProvinceId];
                if (f.StateMapIndex != region) f.StateMapIndex = region;
                if (f.RouteModes.Count != f.Route.Count) RepairModes(f);
                continue;
            }
            SupportTypeCatalog.TryGet(f.TypeId, out var type);
            if (f.BaseProvinceId > 0 && f.BaseProvinceId <= map.ProvinceCount)
            {
                f.ProvinceId = f.BaseProvinceId;
                f.StateMapIndex = map.ProvinceToState[f.ProvinceId];
                continue;
            }
            var nation = c.NationById(f.NationId);
            if (nation is null || (uint)nation.MapIndex > ushort.MaxValue) continue;
            var effective = CapitalCore.EffectiveProvinceId(c, nation);
            if (effective > 0)
            {
                f.ProvinceId = effective;
                f.BaseProvinceId = -1;
                f.DeployDaysLeft = Math.Max(1, type?.DeployDays ?? 1);
                f.Route.Clear(); f.RouteModes.Clear(); f.MoveProgress = 0f;
                f.StateMapIndex = map.ProvinceToState[f.ProvinceId];
                continue;
            }
            var code = (ushort)nation.MapIndex;
            pending.Add((f, type, nation, code));
            if (!needed[code])
            {
                needed[code] = true;
                neededKeys.Add(code);
            }
        }

        if (pending.Count == 0) return;

        var first = _firstLandScratch ??= new int[ushort.MaxValue + 1];
        var controls = c.ProvinceController ?? map.ProvinceToNation;
        var remaining = neededKeys.Count;
        var limit = Math.Min(map.ProvinceCount, controls.Length - 1);
        for (var p = 1; p <= limit && remaining > 0; p++)
        {
            var code = controls[p];
            if (!needed[code] || map.IsWater(p)) continue;
            first[code] = p;
            needed[code] = false;
            remaining--;
        }

        var seatCache = _settleSeatScratch ??= new Dictionary<int, int>();
        seatCache.Clear();
        for (var i = 0; i < pending.Count; i++)
        {
            var (f, type, nation, code) = pending[i];
            var firstOwned = first[code];
            if (firstOwned <= 0) continue;
            if (!seatCache.TryGetValue(nation.Id, out var seat))
            {
                seat = CapitalCore.SupplyOriginProvinceId(c, nation);
                if (seat <= 0) seat = firstOwned;
                seatCache[nation.Id] = seat;
            }
            if (seat <= 0) continue;
            f.ProvinceId = seat;
            f.BaseProvinceId = -1;
            f.DeployDaysLeft = Math.Max(1, type?.DeployDays ?? 1);
            f.Route.Clear(); f.RouteModes.Clear(); f.MoveProgress = 0f;
            f.StateMapIndex = map.ProvinceToState[f.ProvinceId];
        }
        seatCache.Clear();

        for (var i = 0; i < neededKeys.Count; i++)
        {
            var code = neededKeys[i];
            needed[code] = false;
            first[code] = 0;
        }
        pending.Clear();
        neededKeys.Clear();
    }

    private static void RepairModes(SupportFormation f)
    {
        // A hand-edited or truncated save: every unnamed step goes by road, which is the slow
        // and safe reading, never by air.
        while (f.RouteModes.Count > f.Route.Count) f.RouteModes.RemoveAt(f.RouteModes.Count - 1);
        while (f.RouteModes.Count < f.Route.Count) f.RouteModes.Add((int)SupportMoveMode.Land);
    }

    /// <summary>The province the formation stands in, reading an old save's base when it has no position.</summary>
    public static int At(SupportFormation f) => f.ProvinceId > 0 ? f.ProvinceId : f.BaseProvinceId;

    /// <summary>On the road, at sea or in the air.</summary>
    public static bool Moving(SupportFormation f) => f.Route.Count > 0;

    /// <summary>Somewhere between two ports or two airfields, where the ground below is nobody's concern.</summary>
    public static bool OverWater(SupportFormation f) =>
        f.Route.Count > 0 && f.MoveProgress > 0f && f.RouteModes.Count > 0 &&
        (SupportMoveMode)f.RouteModes[0] is SupportMoveMode.Sea or SupportMoveMode.Air;

    /// <summary>Where the formation is going: the last province of its route, or where it stands.</summary>
    public static int Destination(SupportFormation f) => f.Route.Count > 0 ? f.Route[^1] : At(f);

    /// <summary>
    /// Where the card is drawn: between the province it left and the next one by the share of
    /// the step made - along the water the ship sails for a sea passage, along the great circle
    /// for a flight or a road step. Read-only.
    /// </summary>
    public static (float Latitude, float Longitude) Position(Campaign c, SupportFormation f)
    {
        var map = c?.ProvinceMap;
        var at = At(f);
        if (map is null || at <= 0 || at > map.ProvinceCount) return (0f, 0f);
        var from = map.CenterOf(at);
        if (f.Route.Count == 0 || f.MoveProgress <= 0f) return from;
        var next = f.Route[0];
        if (next <= 0 || next > map.ProvinceCount) return from;
        var share = Math.Clamp(f.MoveProgress, 0f, 1f);
        if (f.RouteModes.Count > 0 && (SupportMoveMode)f.RouteModes[0] == SupportMoveMode.Sea &&
            CourseOf(c, at, next, f.SeaCourseMask) is { } course)
            return course.At(share);
        return Between(from, map.CenterOf(next), share);
    }

    /// <summary>
    /// The way still ahead as points on the map, from where the card is now: a province centre
    /// for each road step and flight (joined along the great circle), and every bend of the water
    /// for a sea passage. The map draws the route through these, so the line it shows is the way
    /// the formation will really go. Read-only.
    /// </summary>
    public static void Trail(Campaign c, SupportFormation f, List<(float Latitude, float Longitude)> into)
    {
        into.Clear();
        var map = c?.ProvinceMap;
        var at = At(f);
        if (map is null || at <= 0 || at > map.ProvinceCount) return;
        into.Add(Position(c, f));
        long? shut = null;
        var from = at;
        for (var step = 0; step < f.Route.Count; step++)
        {
            var next = f.Route[step];
            if (next <= 0 || next > map.ProvinceCount) break;
            var sea = step < f.RouteModes.Count && (SupportMoveMode)f.RouteModes[step] == SupportMoveMode.Sea;
            // The passage under way keeps the straits it sailed under; one still to come is drawn
            // the way it would be sailed today.
            var mask = step == 0 && f.MoveProgress > 0f ? f.SeaCourseMask : shut ??= SeaLanes.ShutMask(c, f.NationId);
            if (sea && CourseOf(c, from, next, mask) is { } course)
            {
                var made = step == 0 ? Math.Clamp(f.MoveProgress, 0f, 1f) * course.Total : 0f;
                for (var point = 1; point < course.Points.Count; point++)
                    if (course.Km[point] > made) into.Add(course.Points[point]);
            }
            else into.Add(map.CenterOf(next));
            from = next;
        }
    }

    // ------------------------------------------------------------------ the water a ship sails

    /// <summary>
    /// One sea passage as it is sailed: from the centre of the province left, to its harbour,
    /// along the water the navy's own charts give (SeaLanes) - round the coasts, through the
    /// straits that are open - to the far harbour and the centre of the province reached; and
    /// the kilometres to each point. The owner, 17 November: "through the air". A port-to-port
    /// passage drawn and paced on the great circle crossed Ukraine and Poland on its way from
    /// Crimea to Kaliningrad, through a Bosporus shut to belligerents, in a fifth of the
    /// voyage's real distance.
    /// </summary>
    public sealed class SeaCourse
    {
        internal readonly List<(float Latitude, float Longitude)> PointList = new();
        internal readonly List<float> KmList = new();
        public IReadOnlyList<(float Latitude, float Longitude)> Points => PointList;
        /// <summary>Kilometres from the start to each point; the first is 0.</summary>
        public IReadOnlyList<float> Km => KmList;
        public float Total => KmList.Count == 0 ? 0f : KmList[^1];

        /// <summary>The point a share of the way along, by kilometres sailed.</summary>
        public (float Latitude, float Longitude) At(float share)
        {
            if (PointList.Count == 0) return (0f, 0f);
            var target = Math.Clamp(share, 0f, 1f) * Total;
            for (var index = 1; index < PointList.Count; index++)
            {
                if (KmList[index] < target) continue;
                var leg = KmList[index] - KmList[index - 1];
                if (leg <= 0f) return PointList[index];
                // Straight in latitude and longitude between two bends, which is the line the
                // sea lanes checked for land (SeaLanes.Clear) - a great circle between the same
                // two points can cut the corner of a cape the course was plotted round.
                var t = (target - KmList[index - 1]) / leg;
                var (fromLatitude, fromLongitude) = PointList[index - 1];
                var (toLatitude, toLongitude) = PointList[index];
                var deltaLongitude = toLongitude - fromLongitude;
                if (deltaLongitude > 180f) deltaLongitude -= 360f;
                else if (deltaLongitude < -180f) deltaLongitude += 360f;
                var longitude = fromLongitude + deltaLongitude * t;
                if (longitude > 180f) longitude -= 360f;
                else if (longitude < -180f) longitude += 360f;
                return (fromLatitude + (toLatitude - fromLatitude) * t, longitude);
            }
            return PointList[^1];
        }

        /// <summary>
        /// The next bend, with the straight run to it cut every hundred kilometres or so. A run
        /// straight in latitude and longitude is longer than the great circle between its ends,
        /// by a tenth on a long northern leg; measured in short pieces the kilometres are the
        /// kilometres the card covers, and a day's share of the voyage is a day's sailing.
        /// </summary>
        internal void AddAlong((float Latitude, float Longitude) point)
        {
            if (PointList.Count > 0)
            {
                var (fromLatitude, fromLongitude) = PointList[^1];
                var deltaLongitude = point.Longitude - fromLongitude;
                if (deltaLongitude > 180f) deltaLongitude -= 360f;
                else if (deltaLongitude < -180f) deltaLongitude += 360f;
                var pieces = Math.Clamp((int)MathF.Ceiling(KmBetween(PointList[^1], point) / 100f), 1, 200);
                for (var piece = 1; piece < pieces; piece++)
                {
                    var t = piece / (float)pieces;
                    var longitude = fromLongitude + deltaLongitude * t;
                    if (longitude > 180f) longitude -= 360f;
                    else if (longitude < -180f) longitude += 360f;
                    Add((fromLatitude + (point.Latitude - fromLatitude) * t, longitude));
                }
            }
            Add(point);
        }

        internal void Add((float Latitude, float Longitude) point)
        {
            KmList.Add(PointList.Count == 0 ? 0f : KmList[^1] + KmBetween(PointList[^1], point));
            PointList.Add(point);
        }
    }

    private static readonly SeaCourse NoCourse = new();
    private static readonly object CourseGate = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ProvinceTopology, Dictionary<(int, int, long), SeaCourse>> Courses = new();
    private static int _courseRevision = int.MinValue;
    private static readonly List<float> CourseWaypoints = new();

    /// <summary>
    /// The course between two provinces' harbours with these straits shut, or null when the water
    /// does not join them. Always plotted from the lower province id to the higher and read
    /// backwards the other way, so a ship turned about at sea sails home exactly the way it
    /// came - a share of the way out is the same point as one minus that share of the way back.
    /// A pure function of the map, the two provinces and the straits, kept once worked out: the
    /// map draws it every frame and the daily pass paces by it.
    /// </summary>
    public static SeaCourse CourseOf(Campaign c, int from, int to, long shut)
    {
        var map = c?.ProvinceMap;
        if (map is null || from == to || from <= 0 || to <= 0 || from > map.ProvinceCount || to > map.ProvinceCount) return null;
        lock (CourseGate)
        {
            if (_courseRevision != SeaPassageCatalog.Revision)
            {
                // A mod that moves a strait moves every course through it.
                Courses.Clear();
                _courseRevision = SeaPassageCatalog.Revision;
            }
            var known = Courses.GetOrCreateValue(map);
            if (known.TryGetValue((from, to, shut), out var kept)) return ReferenceEquals(kept, NoCourse) ? null : kept;
            if (known.Count > 8192) known.Clear();
            var low = Math.Min(from, to); var high = Math.Max(from, to);
            var forward = NoCourse;
            if (SeaLanes.TryHarbourPoint(map, low, out var quayLatitude, out var quayLongitude) &&
                SeaLanes.TryHarbourPoint(map, high, out var landingLatitude, out var landingLongitude) &&
                SeaLanes.PlanUnder(c, shut, quayLatitude, quayLongitude, landingLatitude, landingLongitude, CourseWaypoints, out _))
            {
                forward = new SeaCourse();
                forward.Add(map.CenterOf(low));
                forward.Add((quayLatitude, quayLongitude));
                for (var i = 0; i + 1 < CourseWaypoints.Count; i += 2) forward.AddAlong((CourseWaypoints[i], CourseWaypoints[i + 1]));
                forward.Add(map.CenterOf(high));
            }
            var backward = NoCourse;
            if (!ReferenceEquals(forward, NoCourse))
            {
                backward = new SeaCourse();
                for (var i = forward.PointList.Count - 1; i >= 0; i--) backward.Add(forward.PointList[i]);
            }
            known[(low, high, shut)] = forward;
            known[(high, low, shut)] = backward;
            var result = from == low ? forward : backward;
            return ReferenceEquals(result, NoCourse) ? null : result;
        }
    }

    /// <summary>Kilometres of a sea passage: the course sailed, or, with no course, the straight line.</summary>
    internal static float SeaKm(Campaign c, int from, int to, long shut) =>
        CourseOf(c, from, to, shut)?.Total ?? SupportCore.DistanceKm(c, from, to);

    private static float KmBetween((float Latitude, float Longitude) from, (float Latitude, float Longitude) to)
    {
        // SupportCore.DistanceKm's haversine, on two points rather than two province centres.
        var radians = MathF.PI / 180f;
        var sinLat = MathF.Sin((to.Latitude - from.Latitude) * radians * 0.5f);
        var sinLon = MathF.Sin((to.Longitude - from.Longitude) * radians * 0.5f);
        var haversine = sinLat * sinLat + MathF.Cos(from.Latitude * radians) * MathF.Cos(to.Latitude * radians) * sinLon * sinLon;
        return 6371f * 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(haversine)));
    }

    /// <summary>
    /// The point a share of the way from one place to another along the great circle, so that a
    /// day's share of a flight is a day's share of its kilometres - a straight line in latitude
    /// and longitude is longer than the flight. The map draws the route the same way.
    /// </summary>
    public static (float Latitude, float Longitude) Between((float Latitude, float Longitude) from, (float Latitude, float Longitude) to, float t)
    {
        var a = Unit(from); var b = Unit(to);
        var cos = Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1f, 1f);
        var angle = MathF.Acos(cos);
        if (angle < 1e-6f) return from;
        var sin = MathF.Sin(angle);
        var wa = MathF.Sin((1f - t) * angle) / sin; var wb = MathF.Sin(t * angle) / sin;
        var x = wa * a.X + wb * b.X; var y = wa * a.Y + wb * b.Y; var z = wa * a.Z + wb * b.Z;
        return (MathF.Asin(Math.Clamp(y, -1f, 1f)) * 180f / MathF.PI, MathF.Atan2(z, x) * 180f / MathF.PI);
    }

    private static (float X, float Y, float Z) Unit((float Latitude, float Longitude) point)
    {
        var lat = point.Latitude * MathF.PI / 180f; var lon = point.Longitude * MathF.PI / 180f;
        return (MathF.Cos(lat) * MathF.Cos(lon), MathF.Sin(lat), MathF.Cos(lat) * MathF.Sin(lon));
    }

    /// <summary>The longest single flight this kind may make.</summary>
    public static float FerryKm(Campaign c, SupportTypeDefinition type)
    {
        if (type is null) return 0f;
        if (type.FerryKm > 0f) return type.FerryKm;
        var d = c.Defines;
        return Math.Clamp(type.RangeKm * d.LongRangeFerryRangeFactor, d.LongRangeFerryMinKm, MathF.Max(d.LongRangeFerryMinKm, d.LongRangeFerryMaxKm));
    }

    // ------------------------------------------------------------------ pace

    /// <summary>
    /// What one day adds to the share of a step made. A road step is a division's march - the same
    /// define, the same terrain, the same roads - times the kind's own speed; inland, where neither
    /// end touches enemy ground, the trains run at the railways' speed. A sea passage takes its
    /// distance at the transports' pace plus the days in port at both ends; a flight its distance
    /// at the relocation pace.
    /// </summary>
    internal static float PaceOf(Ground g, SupportTypeDefinition type, int from, int to, SupportMoveMode planned, out SupportMoveMode mode,
        long? sailing = null)
    {
        var c = g.Campaign;
        var d = c.Defines;
        mode = planned;
        switch (planned)
        {
            case SupportMoveMode.Sea:
            {
                // The kilometres of the water sailed, not of the line between the two ports: round
                // Europe from the Black Sea to the Baltic is seven thousand, not sixteen hundred.
                var km = SeaKm(c, from, to, sailing ?? g.Shut);
                var days = km / MathF.Max(1f, d.LongRangeSeaKmPerDay) + 2f * Math.Max(0, d.LongRangePortDays);
                return 1f / MathF.Max(1f, days);
            }
            case SupportMoveMode.Air:
            {
                var km = MathF.Max(1f, SupportCore.DistanceKm(c, from, to));
                return MathF.Max(1f, d.LongRangeAirKmPerDay) / km;
            }
            default:
            {
                var rail = !g.HostileHeld(from) && !g.BordersEnemy(from) && !g.BordersEnemy(to);
                mode = rail ? SupportMoveMode.Rail : SupportMoveMode.Land;
                var pace = d.MarchProgressPerDay * g.Map.TerrainAt(to).MovementSpeed * MovementPace.Roads(c, to) *
                           LandCrossings.MarchMultiplier(c, from, to) * type.Speed * (rail ? d.RailRedeploymentSpeed : 1f);
                return MathF.Max(0.01f, pace);
            }
        }
    }

    /// <summary>
    /// The days the route takes from where the formation is, made the way the daily pass makes
    /// them - the same pace, the same one step a day, the same carry - so the figure on the
    /// tooltip is the day the card arrives if the front holds still. Arrival plus deployment.
    /// </summary>
    internal static int DaysFor(Ground g, SupportTypeDefinition type, int from, float progress, IReadOnlyList<int> route, IReadOnlyList<int> modes,
        long? sailing = null)
    {
        var days = 0;
        var at = from;
        for (var step = 0; step < route.Count; step++)
        {
            var next = route[step];
            var planned = (SupportMoveMode)modes[step];
            // A passage already under way is sailed on the course it set out on.
            var pace = PaceOf(g, type, at, next, planned, out _, step == 0 && progress > 0f ? sailing : null);
            var guard = 0;
            do { progress += pace; days++; } while (progress < Arrived && ++guard < 10000);
            progress = planned is SupportMoveMode.Land or SupportMoveMode.Rail && step + 1 < route.Count &&
                       (SupportMoveMode)modes[step + 1] is SupportMoveMode.Land or SupportMoveMode.Rail
                ? Math.Clamp(progress - 1f, 0f, MaxCarry) : 0f;
            at = next;
        }
        return days + (route.Count > 0 ? Math.Max(0, type.DeployDays) : 0);
    }

    /// <summary>Days until a moving formation works again - the rest of the road and the deployment - for the card and the panel.</summary>
    public static int DaysLeft(Campaign c, SupportFormation f)
    {
        if (c?.ProvinceMap is null) return 0;
        if (f.Route.Count == 0) return Math.Max(0, f.DeployDaysLeft);
        if (!SupportTypeCatalog.TryGet(f.TypeId, out var type)) return 0;
        var g = GroundOf(c, f.NationId);
        if (g is null || !g.Valid(At(f))) return 0;
        if (f.RouteModes.Count != f.Route.Count) return f.Route.Count;
        return DaysFor(g, type, At(f), f.MoveProgress, f.Route, f.RouteModes, f.SeaCourseMask);
    }

    // ------------------------------------------------------------------ searches

    /// <summary>One breadth-first search over the provinces a country may drive through.</summary>
    private sealed class Search
    {
        private int[] _previous = Array.Empty<int>();
        private int[] _steps = Array.Empty<int>();
        private int[] _stamp = Array.Empty<int>();
        private int _generation;
        private readonly Queue<int> _queue = new();
        public readonly List<int> Reached = new();

        public bool Seen(int province) => province > 0 && province < _stamp.Length && _stamp[province] == _generation;
        public int Steps(int province) => Seen(province) ? _steps[province] : int.MaxValue;

        /// <summary>From <paramref name="start"/> (which may itself be lost ground: a formation
        /// leaving it) over enterable land; stops at <paramref name="target"/> when one is given.</summary>
        public void Run(Ground g, int start, int target = -1, bool corridor = false, bool stopOnFirstOwnQuiet = false)
        {
            var size = g.Map.ProvinceCount + 1;
            if (_stamp.Length < size)
            {
                _previous = new int[size]; _steps = new int[size]; _stamp = new int[size]; _generation = 0;
            }
            _generation++;
            _queue.Clear();
            Reached.Clear();
            if (!g.Valid(start)) return;
            _stamp[start] = _generation; _previous[start] = start; _steps[start] = 0;
            _queue.Enqueue(start);
            Reached.Add(start);
            while (_queue.Count > 0)
            {
                var current = _queue.Dequeue();
                if (current == target) return;
                foreach (var next in g.Map.Neighbors[current])
                {
                    if (_stamp[next] == _generation || !(g.Enterable(next) || corridor && g.Crossable(next))) continue;
                    _stamp[next] = _generation; _previous[next] = current; _steps[next] = _steps[current] + 1;
                    _queue.Enqueue(next);
                    Reached.Add(next);
                    if (stopOnFirstOwnQuiet && next != start && !g.Battles.Contains(next) &&
                        g.Enterable(next) && g.Own(next) && !g.BordersEnemy(next))
                        return;
                }
            }
        }

        /// <summary>The provinces from the start to this one, the start left out, in order.</summary>
        public void PathTo(int province, List<int> into)
        {
            var from = into.Count;
            for (var cursor = province; Seen(cursor) && _previous[cursor] != cursor; cursor = _previous[cursor]) into.Add(cursor);
            into.Reverse(from, into.Count - from);
        }

        /// <summary>The provinces from this one back toward the start, this one left out, the start included.</summary>
        public void PathBack(int province, List<int> into)
        {
            for (var cursor = province; Seen(cursor) && _previous[cursor] != cursor;)
            {
                cursor = _previous[cursor];
                into.Add(cursor);
            }
        }
    }

    [ThreadStatic] private static Search _outbound;
    [ThreadStatic] private static Search _inbound;
    [ThreadStatic] private static bool[] _coastal;
    [ThreadStatic] private static ProvinceTopology _coastalMap;
    [ThreadStatic] private static List<(int Province, int Water)> _portsDepScratch;
    [ThreadStatic] private static List<(int Province, int Water)> _portsArrScratch;
    [ThreadStatic] private static Dictionary<int, int> _portsPerWaterScratch;
    [ThreadStatic] private static List<(float Floor, int From, int To, float Land)> _seaPairsScratch;
    [ThreadStatic] private static HashSet<int> _seaBasinsScratch;

    private static bool[] CoastalOf(ProvinceTopology map)
    {
        if (_coastal is not null && ReferenceEquals(_coastalMap, map)) return _coastal;
        var coastal = new bool[map.ProvinceCount + 1];
        foreach (var province in SeaLanes.CoastalProvinces(map))
            if (province > 0 && province <= map.ProvinceCount) coastal[province] = true;
        _coastalMap = map;
        return _coastal = coastal;
    }

    /// <summary>How many of the nearest ports on each side a sea passage is chosen from.</summary>
    private const int PortsConsidered = 24;
    /// <summary>And how many more on each other water the road reaches, past those.</summary>
    private const int PortsPerOtherWater = 6;
    /// <summary>The most courses one order plots: each is a search over the sea, and the floor
    /// that orders them is close enough that the best is almost always among the first few.</summary>
    private const int CoursesPlotted = 12;

    /// <summary>
    /// Plan the way from one province to another for this kind of formation. Fills the provinces
    /// after <paramref name="from"/> and the way each step is made; returns an English refusal
    /// template, empty when there is a way.
    /// </summary>
    internal static string Plan(Ground g, SupportTypeDefinition type, int from, int to, List<int> route, List<int> modes, out object[] args,
        bool corridor = false, bool wantArgs = true)
    {
        args = Array.Empty<object>();
        if (from == to) return "";
        var c = g.Campaign;
        if (type.Movement == SupportMoveMode.Air)
        {
            var km = SupportCore.DistanceKm(c, from, to);
            var ferry = FerryKm(c, type);
            if (km > ferry)
            {
                args = wantArgs ? new object[] { MathF.Round(km), MathF.Round(ferry) } : Array.Empty<object>();
                return RefuseFerry;
            }
            route.Add(to); modes.Add((int)SupportMoveMode.Air);
            return "";
        }

        var outbound = _outbound ??= new Search();
        outbound.Run(g, from, to, corridor);
        if (outbound.Seen(to))
        {
            var start = route.Count;
            outbound.PathTo(to, route);
            AddLandModes(g, type, from, route, start, modes);
            return "";
        }

        return PlanSeaFromExistingOutbound(g, type, from, to, outbound, route, modes, corridor);
    }

    private static string PlanSeaFromExistingOutbound(Ground g, SupportTypeDefinition type, int from, int to,
        Search outbound, List<int> route, List<int> modes, bool corridor) =>
        PlanSeaFromExistingOutbound(g, type, from, to, outbound, route, modes, corridor, out _);

    private static string PlanSeaFromExistingOutbound(Ground g, SupportTypeDefinition type, int from, int to,
        Search outbound, List<int> route, List<int> modes, bool corridor, out bool hadDepartures)
    {
        var c = g.Campaign;
        var coastal = CoastalOf(g.Map);
        var shut = g.Shut;
        var departures = Ports(c, shut, outbound, coastal, _portsDepScratch ??= new List<(int, int)>(PortsConsidered));
        hadDepartures = departures.Count > 0;
        if (!hadDepartures) return RefuseNoRoute;
        var inbound = _inbound ??= new Search();
        inbound.Run(g, to, corridor: corridor);
        var arrivals = Ports(c, shut, inbound, coastal, _portsArrScratch ??= new List<(int, int)>(PortsConsidered));
        var d = c.Defines;
        // Every pair on one water, with the days it would take if the sea were a straight line -
        // a floor under the real voyage, which is never shorter. The courses are plotted cheapest
        // floor first, and the search stops when no unplotted pair can beat the best real voyage.
        var pairs = _seaPairsScratch ??= new List<(float Floor, int From, int To, float Land)>();
        pairs.Clear();
        var seaInv = 1f / MathF.Max(1f, d.LongRangeSeaKmPerDay);
        var marchInv = 1f / MathF.Max(0.05f, d.MarchProgressPerDay);
        for (var di = 0; di < departures.Count; di++)
        {
            var (departure, water) = departures[di];
            var outSteps = outbound.Steps(departure);
            for (var ai = 0; ai < arrivals.Count; ai++)
            {
                var (arrival, arrivalWater) = arrivals[ai];
                if (arrival == departure || arrivalWater != water) continue;
                var land = (outSteps + inbound.Steps(arrival)) * marchInv;
                pairs.Add((land + SupportCore.DistanceKm(c, departure, arrival) * seaInv, departure, arrival, land));
            }
        }
        pairs.Sort(static (a, b) => a.Floor != b.Floor ? a.Floor.CompareTo(b.Floor) : a.From != b.From ? a.From.CompareTo(b.From) : a.To.CompareTo(b.To));
        var best = float.MaxValue;
        int bestFrom = -1, bestTo = -1, plotted = 0;
        for (var pi = 0; pi < pairs.Count; pi++)
        {
            var pair = pairs[pi];
            if (pair.Floor >= best || plotted >= CoursesPlotted) break;
            plotted++;
            if (CourseOf(c, pair.From, pair.To, shut) is not { } course) continue;
            var days = pair.Land + course.Total * seaInv;
            if (days >= best) continue;
            best = days; bestFrom = pair.From; bestTo = pair.To;
        }
        if (bestFrom < 0) return pairs.Count == 0 && shut != 0 && SameSea(g.Map, departures, arrivals) ? RefuseStrait : RefuseNoRoute;
        var first = route.Count;
        outbound.PathTo(bestFrom, route);
        AddLandModes(g, type, from, route, first, modes);
        route.Add(bestTo); modes.Add((int)SupportMoveMode.Sea);
        if (bestTo != to)
        {
            var landing = route.Count;
            inbound.PathBack(bestTo, route);
            AddLandModes(g, type, bestTo, route, landing, modes);
        }
        return "";
    }

    /// <summary>
    /// The harbours a passage may start or end at, nearest by road first, with the water each opens
    /// on: the nearest <see cref="PortsConsidered"/> of all, and the nearest few on every other water
    /// the road reaches. Only the nearest two dozen, and a brigade in Crimea with the Bosporus shut
    /// saw nothing but the Black Sea - its own country's Baltic ports, a long drive away, were
    /// never asked.
    /// </summary>
    private static List<(int Province, int Water)> Ports(Campaign c, long shut, Search search, bool[] coastal,
        List<(int Province, int Water)> ports)
    {
        ports.Clear();
        var perWater = _portsPerWaterScratch ??= new Dictionary<int, int>();
        perWater.Clear();
        var reached = search.Reached;
        for (var i = 0; i < reached.Count; i++)
        {
            var province = reached[i];
            if (province >= coastal.Length || !coastal[province]) continue;
            var water = SeaLanes.WaterOf(c, shut, province);
            if (water < 0) continue;
            var onThisWater = perWater.GetValueOrDefault(water);
            if (ports.Count >= PortsConsidered && onThisWater >= PortsPerOtherWater) continue;
            perWater[water] = onThisWater + 1;
            ports.Add((province, water));
        }
        return ports;
    }

    /// <summary>Whether any departure and arrival share a sea once the straits are open: the refusal then names the strait.</summary>
    private static bool SameSea(ProvinceTopology map, List<(int Province, int Water)> departures, List<(int Province, int Water)> arrivals)
    {
        var basins = _seaBasinsScratch ??= new HashSet<int>();
        basins.Clear();
        for (var i = 0; i < departures.Count; i++)
        {
            var basin = SeaLanes.HarbourBasin(map, departures[i].Province);
            if (basin >= 0) basins.Add(basin);
        }
        for (var i = 0; i < arrivals.Count; i++)
            if (basins.Contains(SeaLanes.HarbourBasin(map, arrivals[i].Province))) return true;
        return false;
    }

    private static void AddLandModes(Ground g, SupportTypeDefinition type, int from, List<int> route, int start, List<int> modes)
    {
        var at = start > 0 ? route[start - 1] : from;
        for (var index = start; index < route.Count; index++)
        {
            PaceOf(g, type, at, route[index], SupportMoveMode.Land, out var mode);
            modes.Add((int)mode);
            at = route[index];
        }
    }

    // ------------------------------------------------------------------ where it may stand

    public const string RefuseSea = "This is sea.";
    public const string RefuseDestroyed = "The province is destroyed.";
    public const string RefuseEnemy = "This ground is held by the enemy.";
    public const string RefuseAccess = "No military access: that country does not let our troops in.";
    public const string RefuseBattle = "A battle is being fought here.";
    public const string RefuseNoRoute = "No route: neither over our own ground nor through ports.";
    public const string RefuseStrait = "No route: the only way by sea runs through a strait shut to us.";
    public const string RefuseFerry = "Too far for one ferry flight: {0} km, ferry range {1} km. Shift+right-click adds a waypoint.";
    public const string RefuseForeign = "Not our formation.";
    public const string RefuseDisabled = "This mechanic is disabled in this scenario.";

    /// <summary>Why a formation of this country may not be sent to stand here; empty when it may.</summary>
    internal static string StandRefusal(Ground g, int province)
    {
        // Destroyed ground reads as water to the map (ProvinceTopology.IsWater), so it is named first.
        if (g.Valid(province) && g.Destroyed(province)) return RefuseDestroyed;
        if (!g.Valid(province) || g.Map.IsWater(province)) return RefuseSea;
        if (g.Hostile.Contains(g.Controls[province])) return RefuseEnemy;
        if (!g.Passable.Contains(g.Controls[province])) return RefuseAccess;
        if (g.Battles.Contains(province)) return RefuseBattle;
        return "";
    }

    // ------------------------------------------------------------------ the order

    /// <summary>One formation's answer to "go there": the way, how long, and what it will find.</summary>
    public sealed class RelocatePreview
    {
        public int FormationId;
        public int Destination;
        public int StateMapIndex = -1;
        /// <summary>The full route the formation will follow, from the next province on.</summary>
        public readonly List<int> Route = new();
        public readonly List<int> Modes = new();
        /// <summary>Whether the step it is making now is kept: it finishes the step before turning.</summary>
        public bool KeepsStep;
        /// <summary>Arrival plus deployment, if the front holds still.</summary>
        public int Days;
        public float BasingUsed;
        public float BasingCapacity;
        /// <summary>An English template; empty when the order is accepted.</summary>
        public string Refusal = "";
        public object[] RefusalArgs = Array.Empty<object>();
        public bool Accepted => Refusal.Length == 0;
        public string RefusalText => RefusalArgs.Length == 0 ? Refusal : string.Format(CultureInfo.InvariantCulture, Refusal, RefusalArgs);
        public bool Uses(SupportMoveMode mode) => Modes.Contains((int)mode);
    }

    /// <summary>
    /// What sending each of these formations to that province would do. Read-only: the map's
    /// right-click tooltip and the command ask the same question and get the same answer.
    /// </summary>
    public static List<RelocatePreview> PreviewRelocate(Campaign c, int issuer, IEnumerable<int> formationIds, int province, bool append = false)
    {
        var result = new List<RelocatePreview>();
        if (c?.ProvinceMap is null || formationIds is null) return result;
        var grounds = new Dictionary<int, Ground>();
        var battles = BattleProvinces(c);
        foreach (var id in formationIds.Distinct().OrderBy(id => id).Take(4096))
            result.Add(PreviewOne(c, issuer, id, province, append, grounds, battles));
        return result;
    }

    internal sealed class CachedGroundPlan
    {
        public string Refusal = "";
        public object[] Args = Array.Empty<object>();
        public readonly List<int> Route = new();
        public readonly List<int> Modes = new();
    }

    internal static RelocatePreview PreviewOne(Campaign c, int issuer, int formationId, int province, bool append,
        Dictionary<int, Ground> grounds = null, BattleSet battles = null,
        Dictionary<int, SupportFormation> byId = null, bool includeBasing = true,
        Dictionary<(int, int), CachedGroundPlan> planMemo = null) =>
        PreviewOneInto(c, new RelocatePreview(), issuer, formationId, province, append, grounds, battles, byId, includeBasing, planMemo);

    internal static RelocatePreview PreviewOneInto(Campaign c, RelocatePreview preview, int issuer, int formationId, int province, bool append,
        Dictionary<int, Ground> grounds = null, BattleSet battles = null,
        Dictionary<int, SupportFormation> byId = null, bool includeBasing = true,
        Dictionary<(int, int), CachedGroundPlan> planMemo = null,
        Stack<CachedGroundPlan> planPool = null, bool wantArgs = true)
    {
        preview.FormationId = formationId;
        preview.Destination = province;
        preview.StateMapIndex = -1;
        preview.Route.Clear();
        preview.Modes.Clear();
        preview.KeepsStep = false;
        preview.Days = 0;
        preview.BasingUsed = 0;
        preview.BasingCapacity = 0;
        preview.Refusal = "";
        preview.RefusalArgs = Array.Empty<object>();
        SupportFormation formation;
        if (byId is not null)
        {
            if (!byId.TryGetValue(formationId, out formation) || formation.NationId != issuer) formation = null;
        }
        else
        {
            formation = c.SupportFormations.Find(f => f.Id == formationId && f.NationId == issuer);
        }
        if (formation is null) { preview.Refusal = RefuseForeign; return preview; }
        if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type) || !SupportCore.Enabled(c, type))
        { preview.Refusal = RefuseDisabled; return preview; }
        var g = GroundOf(c, issuer, grounds, battles);
        if (g is null) { preview.Refusal = RefuseForeign; return preview; }
        var refusal = StandRefusal(g, province);
        if (refusal.Length > 0) { preview.Refusal = refusal; return preview; }

        int origin;
        var progress = 0f;
        if (append && formation.Route.Count > 0)
        {
            // A waypoint: the new leg starts where the route now ends, and the route so far stands.
            preview.Route.AddRange(formation.Route);
            preview.Modes.AddRange(formation.RouteModes);
            preview.KeepsStep = true;
            progress = formation.MoveProgress;
            origin = formation.Route[^1];
        }
        else if (formation.Route.Count > 0 && formation.MoveProgress > 0f)
        {
            // Half-way along a road, a crossing or a flight, the column finishes the step it is on
            // and turns there: nothing jumps back to where it set out.
            preview.Route.Add(formation.Route[0]);
            preview.Modes.Add(formation.RouteModes.Count > 0 ? formation.RouteModes[0] : (int)SupportMoveMode.Land);
            preview.KeepsStep = true;
            progress = formation.MoveProgress;
            origin = formation.Route[0];
        }
        else origin = At(formation);
        if (!g.Valid(origin)) { preview.Refusal = RefuseNoRoute; return preview; }

        if (planMemo is not null && type.Movement != SupportMoveMode.Air && !preview.KeepsStep)
        {
            var key = (origin, province);
            if (planMemo.TryGetValue(key, out var cached))
            {
                if (cached.Refusal.Length > 0)
                {
                    preview.Refusal = cached.Refusal;
                    preview.RefusalArgs = cached.Args;
                    return preview;
                }
                preview.Route.AddRange(cached.Route);
                preview.Modes.AddRange(cached.Modes);
            }
            else
            {
                var planned = Plan(g, type, origin, province, preview.Route, preview.Modes, out var args, wantArgs: wantArgs);
                if (planned.Length > 0)
                {
                    var cachedPlan = planPool is { Count: > 0 } ? planPool.Pop() : new CachedGroundPlan();
                    cachedPlan.Refusal = planned;
                    cachedPlan.Args = args;
                    cachedPlan.Route.Clear();
                    cachedPlan.Modes.Clear();
                    planMemo[key] = cachedPlan;
                    preview.Refusal = planned;
                    preview.RefusalArgs = args;
                    preview.Route.Clear();
                    preview.Modes.Clear();
                    return preview;
                }
                var okPlan = planPool is { Count: > 0 } ? planPool.Pop() : new CachedGroundPlan();
                okPlan.Refusal = "";
                okPlan.Args = Array.Empty<object>();
                okPlan.Route.Clear();
                okPlan.Route.AddRange(preview.Route);
                okPlan.Modes.Clear();
                okPlan.Modes.AddRange(preview.Modes);
                planMemo[key] = okPlan;
            }
        }
        else
        {
            var planned = Plan(g, type, origin, province, preview.Route, preview.Modes, out var args, wantArgs: wantArgs);
            if (planned.Length > 0) { preview.Refusal = planned; preview.RefusalArgs = args; preview.Route.Clear(); preview.Modes.Clear(); return preview; }
        }

        preview.Days = preview.Route.Count == 0 ? (formation.DeployDaysLeft > 0 ? formation.DeployDaysLeft : 0)
            : DaysFor(g, type, At(formation), progress, preview.Route, preview.Modes, formation.SeaCourseMask);
        preview.StateMapIndex = c.ProvinceMap.ProvinceToState[province];
        if (includeBasing)
        {
            var state = c.StateByMapIndex(preview.StateMapIndex);
            preview.BasingCapacity = SupportCore.CapacityOf(c, state);
            preview.BasingUsed = SupportCore.BasingUsed(c, preview.StateMapIndex, formation.Id) + type.Basing;
        }
        return preview;
    }

    /// <summary>
    /// What "stop" means for one formation. Part-way along a step - a road, a passage, a ferry
    /// leg - it finishes that step and deploys at its end: the province, the harbour or the
    /// airfield ahead. An order to the province it last reached, which is what the button used
    /// to send, planned the way back from there, so a ship six days out turned round at the far
    /// port and sailed ten days home. Only when the province ahead may not be stood in (a battle
    /// there, the enemy's now) does it go back to the one it left. Not moving, it deploys where
    /// it stands. Read-only, like every preview.
    /// </summary>
    internal static RelocatePreview PreviewHalt(Campaign c, int issuer, int formationId,
        Dictionary<int, Ground> grounds = null, BattleSet battles = null)
    {
        var formation = c?.SupportFormations.Find(f => f.Id == formationId && f.NationId == issuer);
        if (c?.ProvinceMap is null || formation is null) return new RelocatePreview { FormationId = formationId, Refusal = RefuseForeign };
        if (formation.Route.Count > 0 && formation.MoveProgress > 0f)
        {
            var ahead = PreviewOne(c, issuer, formationId, formation.Route[0], false, grounds, battles);
            if (ahead.Accepted) return ahead;
        }
        return PreviewOne(c, issuer, formationId, At(formation), false, grounds, battles);
    }

    /// <summary>
    /// Let go of every selected id that is not one of this country's formations any more: one
    /// disbanded or destroyed, or the selection of the seat before in a hot-seat game. The map
    /// asks before it acts on a selection, so a stale one neither swallows a right-click nor
    /// moves somebody else's formations.
    /// </summary>
    public static void KeepCommandable(Campaign c, int issuer, ICollection<int> selection)
    {
        if (selection is null || selection.Count == 0) return;
        if (c is null) { selection.Clear(); return; }
        var own = new HashSet<int>();
        foreach (var f in c.SupportFormations) if (f.NationId == issuer) own.Add(f.Id);
        foreach (var id in selection.Where(id => !own.Contains(id)).ToList()) selection.Remove(id);
    }

    /// <summary>Put an accepted plan on the formation. True when anything changed.</summary>
    internal static bool Commit(Campaign c, SupportFormation formation, SupportTypeDefinition type, RelocatePreview plan)
    {
        if (!plan.Accepted) return false;
        if (plan.Route.Count == 0)
        {
            // Sent where it already stands. Deployed or deploying there: nothing to do. Stopped on
            // the road in that province, or found standing there cut off: it deploys where it is.
            if (formation.Route.Count == 0 && !formation.CutOff) return false;
            formation.Route.Clear(); formation.RouteModes.Clear(); formation.MoveProgress = 0f; formation.SeaCourseMask = 0;
            formation.CutOff = false;
            formation.BaseProvinceId = -1;
            formation.DeployDaysLeft = Math.Max(0, type.DeployDays);
            if (formation.DeployDaysLeft == 0) formation.BaseProvinceId = At(formation);
            return true;
        }
        if (!plan.KeepsStep) { formation.MoveProgress = 0f; formation.SeaCourseMask = 0; }
        formation.Route.Clear(); formation.Route.AddRange(plan.Route);
        formation.RouteModes.Clear(); formation.RouteModes.AddRange(plan.Modes);
        // Packing up is the first thing an order does: a battery limbering its guns fires nothing.
        formation.BaseProvinceId = -1;
        formation.DeployDaysLeft = 0;
        formation.CutOff = false;
        if (DiagnosticLogs.VerboseSimulation)
            DiagnosticLogs.Write("orders", $"SUPPORT_RELOCATE formation={formation.Id} nation={formation.NationId} " +
                $"from={At(formation)} to={plan.Destination} steps={plan.Route.Count} days={plan.Days}");
        return true;
    }

    // ------------------------------------------------------------------ the daily pass

    [ThreadStatic] private static List<SupportFormation> _ordered;

    private static List<SupportFormation> InIdOrder(Campaign c)
    {
        var list = _ordered ??= new List<SupportFormation>();
        InIdOrderInto(c, list);
        return list;
    }

    private static void InIdOrderInto(Campaign c, List<SupportFormation> list)
    {
        list.Clear();
        list.AddRange(c.SupportFormations);
        var sorted = true;
        for (var i = 1; i < list.Count && sorted; i++) sorted = list[i - 1].Id <= list[i].Id;
        if (!sorted) list.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    /// <summary>
    /// One day of movement and deployment, for every country's formations, in id order: a step
    /// made, a column turned back by a road that fell, an arrival, a day of deployment, a cut-off
    /// formation looking again for a way out.
    /// </summary>
    public static void Advance(Campaign c)
    {
        foreach (var _ in AdvanceStages(c)) { }
    }

    internal static IEnumerable<int> AdvanceStages(Campaign c, bool skipSettle = false)
    {
        if (c?.ProvinceMap is null) yield break;
        if (!skipSettle) Settle(c);
        if (c.SupportFormations.Count == 0) yield break;
        var map = c.ProvinceMap;
        var controls = c.ProvinceController ?? map.ProvinceToNation;
        var pass = c.Caches.SupportPass;
        var formations = pass.MovementOrdered;
        InIdOrderInto(c, formations);
        var pool = c.Caches.SupportStep;
        Dictionary<int, Ground> grounds = null;
        BattleSet battles = null;
        MilitaryAccess.RouteRelations routeRelations = null;
        try
        {
            var ops = 0;
            for (var i = 0; i < formations.Count; i++)
            {
                var f = formations[i];
                // Most of the world's formations, most days: deployed on their own ground, nothing to do.
                if (f.Route.Count == 0 && f.DeployDaysLeft == 0 && !f.CutOff && !f.Overrun && f.ProvinceId > 0 &&
                    f.BaseProvinceId == f.ProvinceId && f.ProvinceId <= map.ProvinceCount &&
                    c.NationById(f.NationId) is { } owner && controls[f.ProvinceId] == owner.MapIndex) continue;
                if (!SupportTypeCatalog.TryGet(f.TypeId, out var type) || !SupportCore.Enabled(c, type)) continue;
                grounds ??= pool.WithdrawGrounds;
                battles ??= BattleProvincesPooled(c, pool);
                // A handful of routes is cheaper to read directly. Large passes make
                // many repeated reads against the same diplomatic tables.
                if (routeRelations is null && grounds.Count >= 4 && !grounds.ContainsKey(f.NationId))
                {
                    routeRelations = pool.AdvanceRelations;
                    routeRelations.Rebuild(c);
                    if (Campaign.StageOverdue()) yield return 0;
                }
                var g = GroundOf(c, f.NationId, grounds, battles, pool, routeRelations);
                if (g is null) continue;
                var at = At(f);
                if (!g.Valid(at)) continue;

                if (g.Enterable(at))
                {
                    // Back on ground of its own side: whatever was lost is lost, and nothing more is.
                    f.Overrun = false;
                    if (f.CutOff)
                    {
                        f.CutOff = false;
                        if (f.Route.Count == 0) StartDeploying(f, type);
                    }
                }
                else if (!OverWater(f) && (f.CutOff || !f.Overrun || f.Route.Count == 0 || !g.Passing(f, f.Route[0])))
                {
                    // Standing on ground it may not stand on: a fresh loss the hourly pass has not seen
                    // yet, or a cut-off formation looking for a way out again today.
                    WithdrawOne(c, g, f, type);
                    if (i + 1 < formations.Count && Campaign.StageOverdue()) yield return 0;
                }
                if (f.CutOff) continue;

                if (f.Route.Count > 0) Step(c, g, f, type);
                else if (f.DeployDaysLeft > 0)
                {
                    if (!g.Enterable(at)) continue;
                    f.DeployDaysLeft--;
                    if (f.DeployDaysLeft == 0) { f.BaseProvinceId = at; c.MarkStrategicChange(); }
                }
                else if (f.BaseProvinceId != at && g.Enterable(at))
                {
                    // Standing, not deploying, and no base: an old save's formation or one stopped on
                    // the road by a lost destination. It works from where it stands.
                    f.BaseProvinceId = at;
                }
                if ((++ops & 7) == 7 && i + 1 < formations.Count && Campaign.StageOverdue()) yield return 0;
            }
        }
        finally
        {
            formations.Clear();
            routeRelations?.Clear();
            if (grounds is not null) pool.ReturnGrounds(grounds);
        }
    }

    private static void StartDeploying(SupportFormation f, SupportTypeDefinition type)
    {
        f.BaseProvinceId = -1;
        f.MoveProgress = 0f;
        f.SeaCourseMask = 0;
        f.DeployDaysLeft = Math.Max(0, type.DeployDays);
        if (f.DeployDaysLeft == 0) f.BaseProvinceId = At(f);
    }

    private static void Step(Campaign c, Ground g, SupportFormation f, SupportTypeDefinition type)
    {
        if (f.RouteModes.Count != f.Route.Count) RepairModes(f);
        var at = At(f);
        var next = f.Route[0];
        var planned = (SupportMoveMode)f.RouteModes[0];
        // A passage not yet begun sails only if the water is still open to it today: a strait
        // shut since the order was given keeps the ship in port, and the way is looked for again.
        var sailing = planned == SupportMoveMode.Sea && f.MoveProgress <= 0f;
        var blocked = !g.Passing(f, next) || !g.Enterable(f.Route[^1]) || sailing && CourseOf(c, at, next, g.Shut) is null;
        if (blocked)
        {
            if (f.MoveProgress > 0f && g.Passing(f, next) && !g.Enterable(f.Route[^1]))
            {
                // Only the place it was going has fallen; the province, the harbour or the
                // airfield ahead is still its side's. It finishes the step it is on and deploys
                // there - a wing on the first hop of four does not fly back to where it set out
                // because the fourth airfield fell, nor a ship five days out sail five days home
                // past an open port.
                var mode0 = f.RouteModes[0];
                f.Route.Clear(); f.RouteModes.Clear();
                f.Route.Add(next); f.RouteModes.Add(mode0);
                DiagnosticLogs.Write("orders", $"SUPPORT_STOP_AHEAD formation={f.Id} nation={g.Nation.Tag} at={next}");
            }
            // A ship or a ferry flight with no harbour or airfield of its side behind it either goes
            // on: there is nowhere to turn back to, and it is dealt with where it comes ashore.
            else if (!(OverWater(f) && !g.Enterable(at)))
            {
                // The road ahead has fallen, or the place it was going and it is not yet on its
                // way. Find another way to the same place from here; with none, stop - and deploy
                // here if here is ours to stand on.
                Replan(c, g, f, type, at);
                return;
            }
        }
        if (sailing) f.SeaCourseMask = g.Shut;
        var pace = PaceOf(g, type, at, next, planned, out var mode, f.SeaCourseMask);
        f.RouteModes[0] = (int)mode;
        f.MoveProgress += pace;
        if (f.MoveProgress < Arrived) return;
        var land = mode is SupportMoveMode.Land or SupportMoveMode.Rail;
        var carry = land ? Math.Clamp(f.MoveProgress - 1f, 0f, MaxCarry) : 0f;
        f.ProvinceId = next;
        f.StateMapIndex = g.Map.ProvinceToState[next];
        f.SeaCourseMask = 0;
        f.Route.RemoveAt(0);
        f.RouteModes.RemoveAt(0);
        f.MoveProgress = land && f.Route.Count > 0 && (SupportMoveMode)f.RouteModes[0] is SupportMoveMode.Land or SupportMoveMode.Rail
            ? carry : 0f;
        if (f.Route.Count > 0) return;
        StartDeploying(f, type);
        c.MarkStrategicChange();
    }

    [ThreadStatic] private static List<int> _replanOnwardScratch;
    [ThreadStatic] private static List<int> _replanOnwardModesScratch;

    private static void Replan(Campaign c, Ground g, SupportFormation f, SupportTypeDefinition type, int at)
    {
        var goal = f.Route[^1];
        if (OverWater(f))
        {
            // Half-way across a sea or along a flight, the harbour or airfield ahead has fallen.
            // Dropping the share already made would put the card back in the port it left that
            // very day - a jump of up to a whole ocean. Instead it turns about where it is: the
            // fallen point becomes where it came from, the port it left where it is going, and
            // the share still to go back is the share already made. From there it goes on to
            // its destination another way, if it still has one.
            var ahead = f.Route[0];
            var mode = f.RouteModes[0];
            var back = M.Clamp(1f - f.MoveProgress, 0.0001f, 1f);
            f.ProvinceId = ahead;
            f.StateMapIndex = g.Map.ProvinceToState[ahead];
            f.Route.Clear(); f.RouteModes.Clear();
            f.Route.Add(at); f.RouteModes.Add(mode);
            f.MoveProgress = back;
            if (goal != at && g.Enterable(goal))
            {
                var onward = _replanOnwardScratch ??= new List<int>(16);
                var onwardModes = _replanOnwardModesScratch ??= new List<int>(16);
                onward.Clear();
                onwardModes.Clear();
                if (Plan(g, type, at, goal, onward, onwardModes, out _, corridor: f.Overrun).Length == 0)
                {
                    f.Route.AddRange(onward); f.RouteModes.AddRange(onwardModes);
                }
                onward.Clear();
                onwardModes.Clear();
            }
            DiagnosticLogs.Write("orders", $"SUPPORT_TURN_BACK formation={f.Id} nation={g.Nation.Tag} toward={at} goal={goal}");
            return;
        }
        f.Route.Clear(); f.RouteModes.Clear(); f.MoveProgress = 0f; f.SeaCourseMask = 0;
        if (g.Enterable(goal) && Plan(g, type, at, goal, f.Route, f.RouteModes, out _, corridor: f.Overrun).Length == 0 && f.Route.Count > 0) return;
        f.Route.Clear(); f.RouteModes.Clear();
        if (g.Enterable(at)) StartDeploying(f, type);
        else WithdrawOne(c, g, f, type);
    }

    // ------------------------------------------------------------------ withdrawal

    /// <summary>
    /// Every formation standing on ground it may not stand on that has not yet been dealt with:
    /// it pays the overrun once if the enemy took the ground, and leaves - by road to the nearest
    /// province of its own that does not touch the enemy, or by air within its ferry range - or,
    /// with no way out, is cut off where it stands. Replaces the jump of SupportRepatriation, and
    /// runs every step so that nothing projects from lost ground for even an hour.
    /// </summary>
    public static int Withdraw(Campaign c)
    {
        var map = c?.ProvinceMap;
        if (map is null) return 0;
        var formations = c.SupportFormations;
        var count = formations.Count;
        if (count == 0) return 0;
        var controls = c.ProvinceController ?? map.ProvinceToNation;
        var destroyed = map.DestroyedByProvince;
        var provinceCount = map.ProvinceCount;

        // Zero-alloc hourly fast path: if every formation is cut off, over water, or standing on
        // dry undestroyed ground it is allowed to enter (own ground or military access / coalition),
        // skip sorting, BattleSet bitset allocation, and full Ground construction completely.
        if (!AnyFormationNeedsWithdrawCheck(c, map, formations, controls, destroyed, provinceCount))
            return 0;

        var pool = c.Caches.SupportStep;
        Dictionary<int, Ground> grounds = null;
        BattleSet battles = null;
        var changed = 0;
        try
        {
            var ordered = InIdOrder(c);
            for (var i = 0; i < ordered.Count; i++)
            {
                var f = ordered[i];
                if (f.CutOff || OverWater(f)) continue;
                var at = At(f);
                if (at <= 0 || at > provinceCount) continue;
                var nation = c.NationById(f.NationId);
                if (nation is null) continue;
                var code = controls[at];
                if (!map.IsWater(at) && (destroyed is null || at >= destroyed.Length || !destroyed[at]) &&
                    (code == nation.MapIndex || MilitaryAccess.CanEnter(c, nation.Id, code)))
                    continue;
                if (!SupportTypeCatalog.TryGet(f.TypeId, out var type) || !SupportCore.Enabled(c, type)) continue;
                grounds ??= pool.WithdrawGrounds;
                battles ??= BattleProvincesPooled(c, pool);
                var g = GroundOf(c, f.NationId, grounds, battles, pool);
                if (g is null || g.Enterable(at)) continue;
                // Already leaving by a road that is still open: it has paid, and it is going.
                if (f.Route.Count > 0 && f.Overrun && g.Passing(f, f.Route[0])) continue;
                WithdrawOne(c, g, f, type);
                changed++;
            }
        }
        finally
        {
            if (grounds is not null) pool.ReturnGrounds(grounds);
        }
        return changed;
    }

    internal static IEnumerable<int> WithdrawStages(Campaign c)
    {
        var pass = c?.Caches.SupportPass;
        if (pass is not null) pass.LastWithdrawChanged = 0;
        var map = c?.ProvinceMap;
        if (map is null) yield break;
        var formations = c.SupportFormations;
        if (formations.Count == 0) yield break;
        var controls = c.ProvinceController ?? map.ProvinceToNation;
        var destroyed = map.DestroyedByProvince;
        var provinceCount = map.ProvinceCount;
        if (!AnyFormationNeedsWithdrawCheck(c, map, formations, controls, destroyed, provinceCount))
            yield break;

        var pool = c.Caches.SupportStep;
        var ordered = pass.MovementOrdered;
        InIdOrderInto(c, ordered);
        Dictionary<int, Ground> grounds = null;
        BattleSet battles = null;
        var changed = 0;
        try
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var f = ordered[i];
                if (f.CutOff || OverWater(f)) continue;
                var at = At(f);
                if (at <= 0 || at > provinceCount) continue;
                var nation = c.NationById(f.NationId);
                if (nation is null) continue;
                var code = controls[at];
                if (!map.IsWater(at) && (destroyed is null || at >= destroyed.Length || !destroyed[at]) &&
                    (code == nation.MapIndex || MilitaryAccess.CanEnter(c, nation.Id, code)))
                    continue;
                if (!SupportTypeCatalog.TryGet(f.TypeId, out var type) || !SupportCore.Enabled(c, type)) continue;
                grounds ??= pool.WithdrawGrounds;
                battles ??= BattleProvincesPooled(c, pool);
                var g = GroundOf(c, f.NationId, grounds, battles, pool);
                if (g is null || g.Enterable(at)) continue;
                if (f.Route.Count > 0 && f.Overrun && g.Passing(f, f.Route[0])) continue;
                WithdrawOne(c, g, f, type);
                changed++;
                if (i + 1 < ordered.Count && Campaign.StageOverdue()) yield return 0;
            }
            pass.LastWithdrawChanged = changed;
        }
        finally
        {
            ordered.Clear();
            if (grounds is not null) pool.ReturnGrounds(grounds);
        }
    }

    private static bool AnyFormationNeedsWithdrawCheck(Campaign c, ProvinceTopology map,
        List<SupportFormation> formations, ushort[] controls, bool[] destroyed, int provinceCount)
    {
        for (var i = 0; i < formations.Count; i++)
        {
            var f = formations[i];
            if (f.CutOff || OverWater(f)) continue;
            var at = At(f);
            if (at <= 0 || at > provinceCount) continue;
            var nation = c.NationById(f.NationId);
            if (nation is null) continue;
            var code = controls[at];
            if (!map.IsWater(at) && (destroyed is null || at >= destroyed.Length || !destroyed[at]) &&
                (code == nation.MapIndex || MilitaryAccess.CanEnter(c, nation.Id, code)))
                continue;
            return true;
        }
        return false;
    }

    private static void EnsureCutOffCache(Campaign c, CampaignCaches.SupportPassCache pass, BattleSet battles)
    {
        var d = c.Defines;
        var map = c.ProvinceMap;
        var mapRev = c.MapRevision;
        var warRev = c.WarRevision;
        var allyRev = c.AllianceRevision;
        var protVer = DiplomaticRelation.ProtectorMutationVersion;
        var treatyVer = DiplomaticRelation.TreatyMutationVersion;
        var seaRev = SeaPassageCatalog.Revision;
        var destCount = c.DestroyedProvinces.Count;
        var battleWords = battles.Words;
        var wordCount = battleWords.Length;

        var mapChanged = !ReferenceEquals(pass.CutOffMap, map);
        var structureMatch = !mapChanged &&
            pass.CutOffMapRevision == mapRev &&
            pass.CutOffWarRevision == warRev &&
            pass.CutOffAllianceRevision == allyRev &&
            pass.CutOffProtectorVersion == protVer &&
            pass.CutOffTreatyVersion == treatyVer &&
            pass.CutOffSeaPassageRevision == seaRev &&
            pass.CutOffDestroyedCount == destCount &&
            pass.CutOffPortDays == d.LongRangePortDays &&
            pass.CutOffSeaKm == d.LongRangeSeaKmPerDay &&
            pass.CutOffAirKm == d.LongRangeAirKmPerDay &&
            pass.CutOffFerryFactor == d.LongRangeFerryRangeFactor &&
            pass.CutOffFerryMin == d.LongRangeFerryMinKm &&
            pass.CutOffFerryMax == d.LongRangeFerryMaxKm &&
            pass.CutOffSlackKm == d.LongRangeReachSlackKm;
        if (structureMatch &&
            pass.CutOffBattleWordCount == wordCount &&
            (wordCount == 0 || pass.CutOffBattleWords.AsSpan(0, wordCount).SequenceEqual(battleWords)))
            return;

        // If only active battles changed while map/diplomacy/straits stayed identical, new battles
        // can only close paths, never unblock a cut-off search that already failed. Only drop the
        // failure caches when at least one existing battle ended.
        var onlyNewBattles = false;
        if (structureMatch && pass.CutOffBattleWordCount == wordCount)
        {
            onlyNewBattles = true;
            var prev = pass.CutOffBattleWords;
            for (var wi = 0; wi < wordCount; wi++)
            {
                if ((prev[wi] & ~battleWords[wi]) != 0)
                {
                    onlyNewBattles = false;
                    break;
                }
            }
        }
        if (!onlyNewBattles)
        {
            pass.CutOffFailed.Clear();
            pass.CutOffOrder.Clear();
            pass.CutOffGlobalEpoch++;
            if (mapChanged)
            {
                pass.PocketCutOffFailed.Clear();
                pass.PocketCutOffOrder.Clear();
            }
        }
        pass.CutOffMap = map;
        pass.CutOffMapRevision = mapRev;
        pass.CutOffWarRevision = warRev;
        pass.CutOffAllianceRevision = allyRev;
        pass.CutOffProtectorVersion = protVer;
        pass.CutOffTreatyVersion = treatyVer;
        pass.CutOffSeaPassageRevision = seaRev;
        pass.CutOffDestroyedCount = destCount;
        pass.CutOffPortDays = d.LongRangePortDays;
        pass.CutOffSeaKm = d.LongRangeSeaKmPerDay;
        pass.CutOffAirKm = d.LongRangeAirKmPerDay;
        pass.CutOffFerryFactor = d.LongRangeFerryRangeFactor;
        pass.CutOffFerryMin = d.LongRangeFerryMinKm;
        pass.CutOffFerryMax = d.LongRangeFerryMaxKm;
        pass.CutOffSlackKm = d.LongRangeReachSlackKm;
        pass.CutOffBattleWordCount = wordCount;
        if (wordCount > 0)
        {
            if (pass.CutOffBattleWords.Length < wordCount)
                pass.CutOffBattleWords = new ulong[wordCount + 16];
            battleWords.CopyTo(pass.CutOffBattleWords);
        }
    }

    private const int MaxPocketCacheProvinces = 2048;
    [ThreadStatic] private static int[] _watchStamp;
    [ThreadStatic] private static int _watchGen;
    [ThreadStatic] private static List<int> _watchListScratch;

    private static int[] BuildWatchProvinces(ProvinceTopology map, List<int> reached)
    {
        var size = map.ProvinceCount + 1;
        if (_watchStamp is null || _watchStamp.Length < size)
        {
            _watchStamp = new int[size];
            _watchGen = 0;
        }
        if (++_watchGen == int.MaxValue)
        {
            Array.Clear(_watchStamp, 0, _watchStamp.Length);
            _watchGen = 1;
        }
        var gen = _watchGen;
        var list = _watchListScratch ??= new List<int>(512);
        list.Clear();
        var count = reached.Count;
        for (var i = 0; i < count; i++)
        {
            var p = reached[i];
            if (p > 0 && p < size && _watchStamp[p] != gen)
            {
                _watchStamp[p] = gen;
                list.Add(p);
            }
            foreach (var n in map.Neighbors[p])
            {
                if (n > 0 && n < size && _watchStamp[n] != gen)
                {
                    _watchStamp[n] = gen;
                    list.Add(n);
                }
            }
        }
        return list.ToArray();
    }

    private static ulong ComputeWatchStateHash(int[] watchProvinces, ushort[] controls, bool[] destroyed, BattleSet battles)
    {
        var hash = FnvOffsetBasis64;
        for (var i = 0; i < watchProvinces.Length; i++)
        {
            var p = watchProvinces[i];
            hash ^= (uint)p;
            hash *= FnvPrime64;
            hash ^= controls[p];
            hash *= FnvPrime64;
            var isDestroyed = destroyed is not null && p < destroyed.Length && destroyed[p] ? 1UL : 0UL;
            var inBattle = battles.Contains(p) ? 2UL : 0UL;
            hash ^= isDestroyed | inBattle;
            hash *= FnvPrime64;
        }
        return hash;
    }

    private static void StorePocketCutOff(CampaignCaches.SupportPassCache pass,
        CampaignCaches.SupportPassCache.PocketCutOffKey key,
        CampaignCaches.SupportPassCache.PocketCutOffEntry entry)
    {
        if (pass.PocketCutOffFailed.ContainsKey(key))
        {
            pass.PocketCutOffFailed[key] = entry;
            return;
        }
        while (pass.PocketCutOffFailed.Count >= CampaignCaches.SupportPassCache.MaxCutOffEntries &&
               pass.PocketCutOffOrder.Count > 0)
        {
            var oldest = pass.PocketCutOffOrder.Dequeue();
            pass.PocketCutOffFailed.Remove(oldest);
        }
        if (pass.PocketCutOffOrder.Count > CampaignCaches.SupportPassCache.MaxCutOffEntries * 4)
        {
            pass.PocketCutOffFailed.Clear();
            pass.PocketCutOffOrder.Clear();
        }
        pass.PocketCutOffFailed[key] = entry;
        pass.PocketCutOffOrder.Enqueue(key);
    }

    private static void StoreCutOffFailed(CampaignCaches.SupportPassCache pass,
        CampaignCaches.SupportPassCache.CutOffSearchKey key)
    {
        if (pass.CutOffFailed.Contains(key)) return;
        while (pass.CutOffFailed.Count >= CampaignCaches.SupportPassCache.MaxCutOffEntries &&
               pass.CutOffOrder.Count > 0)
        {
            var oldest = pass.CutOffOrder.Dequeue();
            pass.CutOffFailed.Remove(oldest);
        }
        if (pass.CutOffOrder.Count > CampaignCaches.SupportPassCache.MaxCutOffEntries * 4)
        {
            pass.CutOffFailed.Clear();
            pass.CutOffOrder.Clear();
        }
        pass.CutOffFailed.Add(key);
        pass.CutOffOrder.Enqueue(key);
    }

    internal static void WithdrawOne(Campaign c, Ground g, SupportFormation f, SupportTypeDefinition type)
    {
        var at = At(f);
        if (g.HostileHeld(at) && !f.Overrun)
        {
            f.Strength = M.Clamp01(f.Strength * (1f - M.Clamp01(c.Defines.LongRangeOverrunLoss)));
            Overruns++;
            DiagnosticLogs.Write("orders", $"SUPPORT_OVERRUN formation={f.Id} nation={g.Nation.Tag} province={at} strength={f.Strength:0.00}");
        }
        // Losing ground to a country that is merely no longer letting us in costs nothing but the move.
        f.Overrun = true;
        f.BaseProvinceId = -1;
        f.DeployDaysLeft = 0;
        // A road out that is still open is kept: the column is already on it.
        if (f.Route.Count > 0 && g.Passing(f, f.Route[0]) && g.Enterable(f.Route[^1])) { f.CutOff = false; return; }
        f.Route.Clear(); f.RouteModes.Clear(); f.MoveProgress = 0f; f.SeaCourseMask = 0;

        var pass = c.Caches.SupportPass;
        EnsureCutOffCache(c, pass, g.Battles);
        var ferryBits = type.Movement == SupportMoveMode.Air ? BitConverter.SingleToInt32Bits(FerryKm(c, type)) : 0;
        var seatSig = (g.Nation.GovernmentSeatProvinceId * 397) ^ g.Nation.CapitalProvinceId;

        CampaignCaches.SupportPassCache.PocketCutOffKey pocketKey = default;
        var usePocket = type.Movement != SupportMoveMode.Air;
        if (usePocket)
        {
            pocketKey = new CampaignCaches.SupportPassCache.PocketCutOffKey(g.Nation.Id, at, (int)type.Movement, g.Shut, seatSig);
            if (pass.PocketCutOffFailed.TryGetValue(pocketKey, out var pocket))
            {
                if ((!pocket.HadDepartures || pocket.GlobalEpoch == pass.CutOffGlobalEpoch) &&
                    pocket.PassableHash == g.PassableHash &&
                    pocket.HostileHash == g.HostileHash &&
                    ComputeWatchStateHash(pocket.WatchProvinces, g.Controls, g.Map.DestroyedByProvince, g.Battles) == pocket.WatchStateHash)
                {
                    if (!f.CutOff)
                    {
                        f.CutOff = true;
                        CutOffs++;
                        DiagnosticLogs.Write("orders", $"SUPPORT_CUT_OFF formation={f.Id} nation={g.Nation.Tag} province={at}");
                    }
                    return;
                }
                pass.PocketCutOffFailed.Remove(pocketKey);
            }
        }

        var cutKey = new CampaignCaches.SupportPassCache.CutOffSearchKey(g.Nation.Id, at, (int)type.Movement, g.Shut, seatSig, ferryBits);
        if (pass.CutOffFailed.Contains(cutKey))
        {
            if (!f.CutOff)
            {
                f.CutOff = true;
                CutOffs++;
                DiagnosticLogs.Write("orders", $"SUPPORT_CUT_OFF formation={f.Id} nation={g.Nation.Tag} province={at}");
            }
            return;
        }

        var near = NearbySafeProvince(g, type, at);
        int target;
        bool away;
        var hadDepartures = false;
        var checkedDepartures = false;
        if (near > 0)
        {
            target = near;
            if (type.Movement == SupportMoveMode.Air)
            {
                away = SupportCore.DistanceKm(c, at, target) > FerryKm(c, type)
                    ? FerryHops(g, type, at, target, f.Route, f.RouteModes)
                    : Plan(g, type, at, target, f.Route, f.RouteModes, out _, corridor: true).Length == 0;
            }
            else
            {
                var start = f.Route.Count;
                _outbound.PathTo(target, f.Route);
                AddLandModes(g, type, at, f.Route, start, f.RouteModes);
                away = true;
            }
        }
        else
        {
            var seat = CapitalCore.SupplyOriginProvinceId(g.Campaign, g.Nation);
            target = seat > 0 && seat != at && g.Enterable(seat) && !g.Battles.Contains(seat) ? seat : -1;
            if (target <= 0)
            {
                away = false;
            }
            else if (type.Movement == SupportMoveMode.Air)
            {
                away = SupportCore.DistanceKm(c, at, target) > FerryKm(c, type)
                    ? FerryHops(g, type, at, target, f.Route, f.RouteModes)
                    : Plan(g, type, at, target, f.Route, f.RouteModes, out _, corridor: true).Length == 0;
            }
            else
            {
                away = PlanSeaFromExistingOutbound(g, type, at, target, _outbound, f.Route, f.RouteModes, corridor: true, out hadDepartures).Length == 0;
                checkedDepartures = true;
            }
        }

        if (away && f.Route.Count > 0)
        {
            if (f.CutOff) f.CutOff = false;
            Withdrawals++;
            DiagnosticLogs.Write("orders", $"SUPPORT_WITHDRAW formation={f.Id} nation={g.Nation.Tag} from={at} to={target}");
            return;
        }
        f.Route.Clear(); f.RouteModes.Clear();
        if (usePocket && _outbound is not null && _outbound.Reached.Count <= MaxPocketCacheProvinces)
        {
            if (!checkedDepartures)
                hadDepartures = Ports(c, g.Shut, _outbound, CoastalOf(g.Map), _portsDepScratch ??= new List<(int, int)>(PortsConsidered)).Count > 0;
            var watchProvinces = BuildWatchProvinces(g.Map, _outbound.Reached);
            var watchHash = ComputeWatchStateHash(watchProvinces, g.Controls, g.Map.DestroyedByProvince, g.Battles);
            StorePocketCutOff(pass, pocketKey, new CampaignCaches.SupportPassCache.PocketCutOffEntry(
                g.PassableHash, g.HostileHash, hadDepartures, pass.CutOffGlobalEpoch, watchProvinces, watchHash));
        }
        else
        {
            StoreCutOffFailed(pass, cutKey);
        }
        if (!f.CutOff)
        {
            f.CutOff = true;
            CutOffs++;
            DiagnosticLogs.Write("orders", $"SUPPORT_CUT_OFF formation={f.Id} nation={g.Nation.Tag} province={at}");
        }
    }

    /// <summary>
    /// Where a formation leaving lost ground goes: the nearest province of its own that does not
    /// touch the enemy, then any of its side's that does not, then any of its own at all - nearest
    /// by road for what drives, by straight line within the ferry range for what flies, ties to
    /// the lower province id. -1 when there is nowhere.
    /// </summary>
    private static int SafeProvince(Ground g, SupportFormation f, SupportTypeDefinition type, int at)
    {
        var near = NearbySafeProvince(g, type, at);
        if (near > 0) return near;
        // Nothing of its side within a road or one flight: a battery left on a far ally's ground
        // when the alliance lapsed, an expedition across a sea when the access was withdrawn. It
        // goes home - to the government's seat, by ship for what drives (Plan finds the ports)
        // and in ferry hops for what flies - instead of standing there cut off for years while
        // its own country is a sea away (55,000 cut-off formation-days in one five-year run
        // before this).
        var seat = CapitalCore.SupplyOriginProvinceId(g.Campaign, g.Nation);
        return seat > 0 && seat != at && g.Enterable(seat) && !g.Battles.Contains(seat) ? seat : -1;
    }

    private static int NearbySafeProvince(Ground g, SupportTypeDefinition type, int at)
    {
        if (type.Movement == SupportMoveMode.Air)
        {
            var ferry = FerryKm(g.Campaign, type);
            var map = g.Map;
            var originLat = map.CenterOf(at).Latitude;
            int best = -1, fallback = -1, last = -1;
            float bestKm = float.MaxValue, fallbackKm = float.MaxValue, lastKm = float.MaxValue;
            foreach (var code in g.Passable)
            {
                var held = CampaignIntel.ProvincesHeldBy(g.Campaign, g.Controls, code);
                for (var i = 0; i < held.Length; i++)
                {
                    var p = held[i];
                    if (p == at || map.IsWater(p) || g.Destroyed(p) || g.Battles.Contains(p)) continue;
                    if (MathF.Abs(map.CenterOf(p).Latitude - originLat) * 111.0f > ferry) continue;
                    var km = SupportCore.DistanceKm(g.Campaign, at, p);
                    if (km > ferry) continue;
                    var quiet = !g.BordersEnemy(p);
                    if (g.Own(p) && quiet && (km < bestKm || (km == bestKm && p < best))) { best = p; bestKm = km; }
                    else if (quiet && (km < fallbackKm || (km == fallbackKm && p < fallback))) { fallback = p; fallbackKm = km; }
                    else if (g.Own(p) && (km < lastKm || (km == lastKm && p < last))) { last = p; lastKm = km; }
                }
            }
            return best > 0 ? best : fallback > 0 ? fallback : last;
        }
        var search = _outbound ??= new Search();
        search.Run(g, at, corridor: true, stopOnFirstOwnQuiet: true);
        int sideQuiet = -1, ownAny = -1;
        var reached = search.Reached;
        for (var i = 0; i < reached.Count; i++)
        {
            var p = reached[i];
            if (p == at || g.Battles.Contains(p) || !g.Enterable(p)) continue;
            var quiet = !g.BordersEnemy(p);
            if (g.Own(p) && quiet) return p;
            if (quiet && sideQuiet < 0) sideQuiet = p;
            if (g.Own(p) && ownAny < 0) ownAny = p;
        }
        return sideQuiet > 0 ? sideQuiet : ownAny;
    }

    /// <summary>
    /// Ferry legs home for a wing with nothing of its side within one flight: each leg to a city
    /// airfield on ground nobody we fight holds, within the ferry range and at least a hundred
    /// kilometres nearer home, the last onto the goal - the way a wing is really ferried, across
    /// neutral airfields. Cities rather than every province, because a cut-off wing asks again
    /// every day and a search over every province of the world each time would be the day's
    /// slowest thing. Fills the route; false when there is no such chain.
    /// </summary>
    private static bool FerryHops(Ground g, SupportTypeDefinition type, int from, int goal, List<int> route, List<int> modes)
    {
        var c = g.Campaign;
        var ferry = FerryKm(c, type);
        var airfields = CityCatalog.ProvincesOf(g.Map);
        var start = route.Count;
        var at = from;
        // Eight legs is a wing across the widest ocean the game has; more is no withdrawal.
        for (var leg = 0; leg < 8; leg++)
        {
            var left = SupportCore.DistanceKm(c, at, goal);
            if (left <= ferry)
            {
                route.Add(goal); modes.Add((int)SupportMoveMode.Air);
                return true;
            }
            int next = -1; var nextLeft = left - 100f;
            foreach (var p in airfields)
            {
                if (p <= 0 || p > g.Map.ProvinceCount || p == at || !g.Crossable(p) || g.Battles.Contains(p)) continue;
                if (SupportCore.DistanceKm(c, at, p) > ferry) continue;
                var remaining = SupportCore.DistanceKm(c, p, goal);
                if (remaining < nextLeft || remaining == nextLeft && p < next) { next = p; nextLeft = remaining; }
            }
            if (next <= 0) break;
            route.Add(next); modes.Add((int)SupportMoveMode.Air);
            at = next;
        }
        route.RemoveRange(start, route.Count - start);
        modes.RemoveRange(start, modes.Count - start);
        return false;
    }

    // ------------------------------------------------------------------ the computer

    /// <summary>
    /// Where the computer's formations stand, reviewed every <see cref="Defines.AiLongRangeRepositionDays"/>
    /// days for every country at war that nobody plays - the whole coalition, not its two leaders:
    ///
    ///   - a formation whose reach covers no battle and no front province of its wars goes to the
    ///     nearest province (by days on the road, or straight for a wing) from which it would,
    ///     standing back <see cref="Defines.AiLongRangeStandoffShare"/> of its reach so the next
    ///     fortnight's front does not walk out of it; never on the front itself unless it is a
    ///     gun of less than <see cref="Defines.AiLongRangeFrontlineMaxRangeKm"/> km;
    ///   - a SAM brigade covering no ground the enemy's strike arm can reach goes to the most
    ///     populous region that the enemy can and nobody covers yet.
    /// One order per formation per review, by the same command as a player's, and nothing on the
    /// road is re-planned. At peace nothing moves: the review is for wars.
    /// </summary>
    internal sealed class RepositionScratch
    {
        public int LastRoadChosen;
        public int LastDriveOrdered;
        public int LastSeaChosen;
        public int LastAirOrdered;
        public int LastAirNearest;
        public readonly int[] SeaTopProvs = new int[4];
        public readonly float[] SeaTopKm = new float[4];
        public float[] Near = Array.Empty<float>();
        public readonly List<int> Within = new();
        public float[] RoadDays = Array.Empty<float>();
        public readonly List<int> RoadTouched = new();
        public readonly PriorityQueue<int, (float, int)> Queue = new();
        public readonly Dictionary<(int Origin, float Standoff, bool MayTouch, float Speed), int> RoadMemo = new();
        public readonly Dictionary<(int Origin, float Standoff, bool MayTouch), int> SeaMemo = new();
        public readonly Dictionary<(int Origin, float Standoff, bool MayTouch, float Ferry), List<int>> AirMemo = new();
        public readonly Dictionary<(int, int), CachedGroundPlan> PlanMemo = new();
        public ulong[] TargetBits = Array.Empty<ulong>();
        public readonly List<int> Targets = new();
        public readonly List<(SupportFormation Formation, SupportTypeDefinition Type)> Idle = new();
        public readonly List<(SupportFormation Formation, SupportTypeDefinition Type)> Sams = new();
        public readonly LongRangeStrikes.Plan StrikePlan = new();
        public readonly Dictionary<int, LongRangeStrikes.Sides> StrikeSides = new();
        public readonly List<(ushort OwnerMap, int Province, float Reach, float Lat)> AllStrikers = new();
        public readonly Dictionary<int, List<ScenarioState>> StatesByOwnerMap = new();
        public readonly Stack<List<ScenarioState>> StatesListPool = new();
        public readonly List<(SupportFormation Formation, SupportTypeDefinition Type)> CoverSpare = new();
        public readonly List<(int Province, float Reach, float Lat)> CoverStrikers = new();
        public readonly Dictionary<int, List<int>> CoverHeld = new();
        public readonly List<ScenarioState> CoverRegions = new();
        public readonly List<(int Province, float Reach, float Lat)> CoverGuarded = new();
        public readonly Dictionary<int, int> CoverAnchors = new();
        public readonly Dictionary<int, (int Anchor, float Lat, bool Eligible)> CoverRegionAnchors = new();
        public readonly RelocatePreview PreviewScratch = new();
        public readonly Dictionary<(int Comp, float Standoff, bool MayTouch), bool> CompSuitMemo = new();
        public readonly Dictionary<(float Standoff, bool MayTouch), (int Count, int FirstProv)> SuitSummaryMemo = new();
        public readonly List<int> AirHopsScratch = new();
        public readonly Stack<List<int>> AirPool = new();
        public readonly Stack<CachedGroundPlan> PlanPool = new();
        public bool HasStrikeSides;
        public bool HasAllStrikers;
        public bool HasStatesByOwnerMap;

        public void ResetPlanMemo()
        {
            if (PlanMemo.Count == 0) return;
            foreach (var entry in PlanMemo.Values)
            {
                entry.Route.Clear();
                entry.Modes.Clear();
                entry.Args = Array.Empty<object>();
                entry.Refusal = "";
                if (PlanPool.Count < 256) PlanPool.Push(entry);
            }
            PlanMemo.Clear();
        }

        public void ResetAirMemo()
        {
            if (AirMemo.Count == 0) return;
            foreach (var list in AirMemo.Values)
            {
                list.Clear();
                if (AirPool.Count < 256) AirPool.Push(list);
            }
            AirMemo.Clear();
        }

        public List<int> RentAirHops()
        {
            var list = AirPool.Count > 0 ? AirPool.Pop() : new List<int>(4);
            list.Clear();
            return list;
        }
    }


    internal static float[] Rules(Defines d) => new[]
    {
        d.LongRangeSeaKmPerDay, d.LongRangePortDays, d.LongRangeAirKmPerDay, d.LongRangeFerryRangeFactor,
        d.LongRangeFerryMinKm, d.LongRangeFerryMaxKm, d.LongRangeOverrunLoss, d.LongRangeReachSlackKm,
        d.LongRangeBasingBase, d.LongRangeBasingPerBarracks, d.LongRangeBasingPerCity, d.SupportTrainingSlots,
        d.AiLongRangeRepositionDays, d.AiLongRangeStandoffShare, d.AiLongRangeFrontlineMaxRangeKm
    };

    internal static readonly float[] DefaultRules = Rules(new Defines());
}

/// <summary>
/// "Go there": each of the named formations travels to the province by its own way - road, rail,
/// ship or ferry flight - and works again once it has arrived and deployed. Every formation is
/// judged alone, so one that cannot go does not hold back the rest; the refusal says which stayed
/// and why. With <see cref="Append"/> the province is a waypoint after the route already given.
/// With <see cref="Halt"/> there is no province: each stops where it is (LongRangeMovement.PreviewHalt).
/// </summary>
public sealed class RelocateSupportCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int[] FormationIds { get; init; } = Array.Empty<int>();
    public int ProvinceId { get; init; }
    public bool Append { get; init; }
    /// <summary>
    /// Stop: a formation part-way along a step finishes it and deploys at its end, one standing
    /// deploys where it stands. Worked out when the order is carried out, not when it was given,
    /// so an order that reaches the world a tick late halts the column where it then is.
    /// </summary>
    public bool Halt { get; init; }
    public string Refusal { get; private set; } = "";
    public int Moved { get; private set; }
    public int Refused { get; private set; }

    public void Apply(Campaign campaign) => ApplyFast(campaign, null, null, null, null);

    internal void ApplyFast(Campaign campaign, Dictionary<int, LongRangeMovement.Ground> grounds,
        LongRangeMovement.BattleSet battles, Dictionary<int, SupportFormation> byId,
        Dictionary<(int, int), LongRangeMovement.CachedGroundPlan> planMemo)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        Moved = 0; Refused = 0; Refusal = "";
        var rawIds = FormationIds;
        if (rawIds is null || rawIds.Length == 0 || campaign.ProvinceMap is null)
        {
            Refusal = LongRangeMovement.RefuseForeign;
            return;
        }
        int[] ids = rawIds.Length == 1 ? rawIds : rawIds.Distinct().OrderBy(id => id).Take(4096).ToArray();
        grounds ??= new Dictionary<int, LongRangeMovement.Ground>();
        battles ??= LongRangeMovement.BattleProvinces(campaign);
        List<string> lines = null;
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            var plan = Halt
                ? LongRangeMovement.PreviewHalt(campaign, IssuerId, id, grounds, battles)
                : LongRangeMovement.PreviewOne(campaign, IssuerId, id, ProvinceId, Append, grounds, battles, byId, includeBasing: false, planMemo: planMemo);
            SupportFormation formation;
            if (byId is not null)
            {
                if (!byId.TryGetValue(id, out formation) || formation.NationId != IssuerId) formation = null;
            }
            else
            {
                formation = campaign.SupportFormations.Find(f => f.Id == id && f.NationId == IssuerId);
            }
            if (!plan.Accepted || formation is null)
            {
                Refused++;
                var name = formation is not null && SupportTypeCatalog.TryGet(formation.TypeId, out var named) ? named.Name : "Formation";
                lines ??= new List<string>();
                lines.Add($"{name} #{id}: {plan.RefusalText}");
                continue;
            }
            if (LongRangeMovement.Commit(campaign, formation, SupportTypeCatalog.Get(formation.TypeId), plan)) Moved++;
        }
        Refusal = lines is null ? "" : string.Join("\n", lines);
        if (Moved > 0) campaign.MarkStrategicChange();
    }
}
