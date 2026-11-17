using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
public static class LongRangeMovement
{
    /// <summary>Telemetry for the bench (SimBench --long-range): read, never steered by.</summary>
    public static int AiOrders, AiOrdersTowardFront, AiCityCover, Withdrawals, CutOffs, Overruns;

    private const float Arrived = 0.9999f;
    // A land step left over from a fast day carries into the next one, but never a whole step:
    // one province a day is the most any column covers, by road or by rail.
    private const float MaxCarry = 0.999f;

    // ------------------------------------------------------------------ the ground a nation may use

    /// <summary>What one country may drive over and stand on, worked out once per pass.</summary>
    internal sealed class Ground
    {
        public Campaign Campaign;
        public Nation Nation;
        public ProvinceTopology Map;
        public ushort[] Controls;
        public readonly HashSet<ushort> Passable = new();
        public readonly HashSet<ushort> Hostile = new();
        public HashSet<int> Battles;

        public bool Valid(int province) => province > 0 && province <= Map.ProvinceCount;
        public bool Destroyed(int province) => Map.DestroyedByProvince is { } destroyed && province < destroyed.Length && destroyed[province];
        public bool HostileHeld(int province) => Valid(province) && Hostile.Contains(Controls[province]);

        /// <summary>Dry, standing, and held by us or by somebody who lets us in.</summary>
        public bool Enterable(int province) =>
            Valid(province) && !Map.IsWater(province) && !Destroyed(province) && Passable.Contains(Controls[province]);

        public bool BordersEnemy(int province)
        {
            if (!Valid(province)) return false;
            foreach (var next in Map.Neighbors[province])
                if (!Map.IsWater(next) && Hostile.Contains(Controls[next])) return true;
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
            !Hostile.Contains(Controls[province]);

        /// <summary>The next step of a route this formation may take: its side's ground, or, withdrawing, any ground but the enemy's.</summary>
        public bool Passing(SupportFormation f, int province) => Enterable(province) || f.Overrun && Crossable(province);
    }

    internal static Ground GroundOf(Campaign c, int nationId, Dictionary<int, Ground> cache = null, HashSet<int> battles = null)
    {
        if (cache is not null && cache.TryGetValue(nationId, out var known)) return known;
        var nation = c.NationById(nationId);
        var map = c.ProvinceMap;
        if (nation is null || map is null) return null;
        var ground = new Ground
        {
            Campaign = c, Nation = nation, Map = map,
            Controls = c.ProvinceController ?? map.ProvinceToNation,
            Battles = battles ?? BattleProvinces(c)
        };
        MilitaryAccess.FillControllers(c, nationId, ground.Passable);
        // The same enemies MilitaryAccess.Hostile names - everybody across a live war, and anybody
        // the relation says we are at war with - gathered once rather than asked pair by pair.
        foreach (var war in Warfare.LiveWars(c))
        {
            if (war.Ended || !war.Involves(nationId)) continue;
            foreach (var enemy in war.EnemiesOf(nationId))
                if (c.NationById(enemy) is { MapIndex: > 0 } foe) ground.Hostile.Add((ushort)foe.MapIndex);
        }
        foreach (var other in c.Nations)
            if (other.Id != nationId && other.MapIndex > 0 &&
                CampaignDiplomacy.Peek(c, nationId, other.Id)?.Stance == DiplomaticStance.AtWar)
                ground.Hostile.Add((ushort)other.MapIndex);
        // Hostile ground is never passable, whatever an access grant once said.
        ground.Passable.ExceptWith(ground.Hostile);
        if (cache is not null) cache[nationId] = ground;
        return ground;
    }

    internal static HashSet<int> BattleProvinces(Campaign c)
    {
        var set = new HashSet<int>();
        foreach (var battle in c.Battles)
            if (!battle.Ended) set.Add(battle.ProvinceId);
        return set;
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
        foreach (var f in c.SupportFormations)
        {
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
            }
            else
            {
                var nation = c.NationById(f.NationId);
                var seat = nation is null ? 0 : CapitalCore.SupplyOriginProvinceId(c, nation);
                if (seat <= 0 && nation is not null)
                {
                    var controls = c.ProvinceController ?? map.ProvinceToNation;
                    for (var p = 1; p <= map.ProvinceCount && seat <= 0; p++)
                        if (controls[p] == nation.MapIndex && !map.IsWater(p)) seat = p;
                }
                if (seat <= 0) continue;
                f.ProvinceId = seat;
                f.BaseProvinceId = -1;
                f.DeployDaysLeft = Math.Max(1, type?.DeployDays ?? 1);
                f.Route.Clear(); f.RouteModes.Clear(); f.MoveProgress = 0f;
            }
            f.StateMapIndex = map.ProvinceToState[f.ProvinceId];
        }
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
    /// the step made, along the straight line for a sea passage or a flight. Read-only.
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
        var to = map.CenterOf(next);
        return Between(from, to, Math.Clamp(f.MoveProgress, 0f, 1f));
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
    internal static float PaceOf(Ground g, SupportTypeDefinition type, int from, int to, SupportMoveMode planned, out SupportMoveMode mode)
    {
        var c = g.Campaign;
        var d = c.Defines;
        mode = planned;
        switch (planned)
        {
            case SupportMoveMode.Sea:
            {
                var km = SupportCore.DistanceKm(c, from, to);
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
    internal static int DaysFor(Ground g, SupportTypeDefinition type, int from, float progress, IReadOnlyList<int> route, IReadOnlyList<int> modes)
    {
        var days = 0;
        var at = from;
        for (var step = 0; step < route.Count; step++)
        {
            var next = route[step];
            var planned = (SupportMoveMode)modes[step];
            var pace = PaceOf(g, type, at, next, planned, out _);
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
        return DaysFor(g, type, At(f), f.MoveProgress, f.Route, f.RouteModes);
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
        public void Run(Ground g, int start, int target = -1, bool corridor = false)
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

    /// <summary>
    /// Plan the way from one province to another for this kind of formation. Fills the provinces
    /// after <paramref name="from"/> and the way each step is made; returns an English refusal
    /// template, empty when there is a way.
    /// </summary>
    internal static string Plan(Ground g, SupportTypeDefinition type, int from, int to, List<int> route, List<int> modes, out object[] args,
        bool corridor = false)
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
                args = new object[] { MathF.Round(km), MathF.Round(ferry) };
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

        // No road: to a port, over the water, from a port. Only ports in the same sea are joined -
        // a ship from the Black Sea does not come out in the Caspian.
        var coastal = CoastalOf(g.Map);
        outbound.Run(g, from, corridor: corridor);
        var inbound = _inbound ??= new Search();
        inbound.Run(g, to, corridor: corridor);
        var departures = Ports(outbound, coastal);
        var arrivals = Ports(inbound, coastal);
        var d = c.Defines;
        var best = float.MaxValue;
        int bestFrom = -1, bestTo = -1;
        foreach (var departure in departures)
        {
            var basin = SeaLanes.HarbourBasin(g.Map, departure);
            if (basin < 0) continue;
            foreach (var arrival in arrivals)
            {
                if (arrival == departure || SeaLanes.HarbourBasin(g.Map, arrival) != basin) continue;
                var days = (outbound.Steps(departure) + inbound.Steps(arrival)) / MathF.Max(0.05f, d.MarchProgressPerDay) +
                           SupportCore.DistanceKm(c, departure, arrival) / MathF.Max(1f, d.LongRangeSeaKmPerDay);
                if (days > best || days == best && (departure > bestFrom || departure == bestFrom && arrival >= bestTo)) continue;
                best = days; bestFrom = departure; bestTo = arrival;
            }
        }
        if (bestFrom < 0) return RefuseNoRoute;
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

    private static List<int> Ports(Search search, bool[] coastal)
    {
        var ports = new List<int>(PortsConsidered);
        foreach (var province in search.Reached)
        {
            if (province >= coastal.Length || !coastal[province]) continue;
            ports.Add(province);
            if (ports.Count >= PortsConsidered) break;
        }
        return ports;
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

    internal static RelocatePreview PreviewOne(Campaign c, int issuer, int formationId, int province, bool append,
        Dictionary<int, Ground> grounds = null, HashSet<int> battles = null)
    {
        var preview = new RelocatePreview { FormationId = formationId, Destination = province };
        var formation = c.SupportFormations.Find(f => f.Id == formationId && f.NationId == issuer);
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

        var planned = Plan(g, type, origin, province, preview.Route, preview.Modes, out var args);
        if (planned.Length > 0) { preview.Refusal = planned; preview.RefusalArgs = args; preview.Route.Clear(); preview.Modes.Clear(); return preview; }

        preview.Days = preview.Route.Count == 0 ? (formation.DeployDaysLeft > 0 ? formation.DeployDaysLeft : 0)
            : DaysFor(g, type, At(formation), progress, preview.Route, preview.Modes);
        preview.StateMapIndex = c.ProvinceMap.ProvinceToState[province];
        var state = c.StateByMapIndex(preview.StateMapIndex);
        preview.BasingCapacity = SupportCore.CapacityOf(c, state);
        preview.BasingUsed = SupportCore.BasingUsed(c, preview.StateMapIndex, formation.Id) + type.Basing;
        return preview;
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
            formation.Route.Clear(); formation.RouteModes.Clear(); formation.MoveProgress = 0f;
            formation.CutOff = false;
            formation.BaseProvinceId = -1;
            formation.DeployDaysLeft = Math.Max(0, type.DeployDays);
            if (formation.DeployDaysLeft == 0) formation.BaseProvinceId = At(formation);
            return true;
        }
        if (!plan.KeepsStep) formation.MoveProgress = 0f;
        formation.Route.Clear(); formation.Route.AddRange(plan.Route);
        formation.RouteModes.Clear(); formation.RouteModes.AddRange(plan.Modes);
        // Packing up is the first thing an order does: a battery limbering its guns fires nothing.
        formation.BaseProvinceId = -1;
        formation.DeployDaysLeft = 0;
        formation.CutOff = false;
        DiagnosticLogs.Write("orders", $"SUPPORT_RELOCATE formation={formation.Id} nation={formation.NationId} " +
            $"from={At(formation)} to={plan.Destination} steps={plan.Route.Count} days={plan.Days}");
        return true;
    }

    // ------------------------------------------------------------------ the daily pass

    [ThreadStatic] private static List<SupportFormation> _ordered;

    private static List<SupportFormation> InIdOrder(Campaign c)
    {
        var list = _ordered ??= new List<SupportFormation>();
        list.Clear();
        list.AddRange(c.SupportFormations);
        var sorted = true;
        for (var i = 1; i < list.Count && sorted; i++) sorted = list[i - 1].Id <= list[i].Id;
        if (!sorted) list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    /// <summary>
    /// One day of movement and deployment, for every country's formations, in id order: a step
    /// made, a column turned back by a road that fell, an arrival, a day of deployment, a cut-off
    /// formation looking again for a way out.
    /// </summary>
    public static void Advance(Campaign c)
    {
        if (c?.ProvinceMap is null) return;
        Settle(c);
        var grounds = new Dictionary<int, Ground>();
        var battles = BattleProvinces(c);
        var map = c.ProvinceMap;
        var controls = c.ProvinceController ?? map.ProvinceToNation;
        var formations = InIdOrder(c);
        foreach (var f in formations)
        {
            // Most of the world's formations, most days: deployed on their own ground, nothing to do.
            if (f.Route.Count == 0 && f.DeployDaysLeft == 0 && !f.CutOff && !f.Overrun && f.ProvinceId > 0 &&
                f.BaseProvinceId == f.ProvinceId && f.ProvinceId <= map.ProvinceCount &&
                c.NationById(f.NationId) is { } owner && controls[f.ProvinceId] == owner.MapIndex) continue;
            if (!SupportTypeCatalog.TryGet(f.TypeId, out var type) || !SupportCore.Enabled(c, type)) continue;
            var g = GroundOf(c, f.NationId, grounds, battles);
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
        }
    }

    private static void StartDeploying(SupportFormation f, SupportTypeDefinition type)
    {
        f.BaseProvinceId = -1;
        f.MoveProgress = 0f;
        f.DeployDaysLeft = Math.Max(0, type.DeployDays);
        if (f.DeployDaysLeft == 0) f.BaseProvinceId = At(f);
    }

    private static void Step(Campaign c, Ground g, SupportFormation f, SupportTypeDefinition type)
    {
        if (f.RouteModes.Count != f.Route.Count) RepairModes(f);
        var at = At(f);
        var next = f.Route[0];
        var planned = (SupportMoveMode)f.RouteModes[0];
        // A ship or a ferry flight with no harbour or airfield of its side behind it either goes
        // on: there is nowhere to turn back to, and it is dealt with where it comes ashore.
        if ((!g.Passing(f, next) || !g.Enterable(f.Route[^1])) && !(OverWater(f) && !g.Enterable(at)))
        {
            // The road ahead, or the place it was going, has fallen. Find another way to the same
            // place from here; with none, stop - and deploy here if here is ours to stand on.
            Replan(c, g, f, type, at);
            return;
        }
        var pace = PaceOf(g, type, at, next, planned, out var mode);
        f.RouteModes[0] = (int)mode;
        f.MoveProgress += pace;
        if (f.MoveProgress < Arrived) return;
        var land = mode is SupportMoveMode.Land or SupportMoveMode.Rail;
        var carry = land ? Math.Clamp(f.MoveProgress - 1f, 0f, MaxCarry) : 0f;
        f.ProvinceId = next;
        f.StateMapIndex = g.Map.ProvinceToState[next];
        f.Route.RemoveAt(0);
        f.RouteModes.RemoveAt(0);
        f.MoveProgress = land && f.Route.Count > 0 && (SupportMoveMode)f.RouteModes[0] is SupportMoveMode.Land or SupportMoveMode.Rail
            ? carry : 0f;
        if (f.Route.Count > 0) return;
        StartDeploying(f, type);
        c.MarkStrategicChange();
    }

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
                var onward = new List<int>(); var onwardModes = new List<int>();
                if (Plan(g, type, at, goal, onward, onwardModes, out _, corridor: f.Overrun).Length == 0)
                {
                    f.Route.AddRange(onward); f.RouteModes.AddRange(onwardModes);
                }
            }
            DiagnosticLogs.Write("orders", $"SUPPORT_TURN_BACK formation={f.Id} nation={g.Nation.Tag} toward={at} goal={goal}");
            return;
        }
        f.Route.Clear(); f.RouteModes.Clear(); f.MoveProgress = 0f;
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
        var controls = c.ProvinceController ?? map.ProvinceToNation;
        Dictionary<int, Ground> grounds = null;
        HashSet<int> battles = null;
        var changed = 0;
        foreach (var f in InIdOrder(c))
        {
            if (f.CutOff || OverWater(f)) continue;
            var at = At(f);
            if (at <= 0 || at > map.ProvinceCount) continue;
            // The common case, answered without building anything: standing on its own ground.
            var nation = c.NationById(f.NationId);
            if (nation is null || controls[at] == nation.MapIndex && !map.IsWater(at) &&
                (map.DestroyedByProvince is not { } destroyed || !destroyed[at])) continue;
            if (!SupportTypeCatalog.TryGet(f.TypeId, out var type) || !SupportCore.Enabled(c, type)) continue;
            grounds ??= new Dictionary<int, Ground>();
            battles ??= BattleProvinces(c);
            var g = GroundOf(c, f.NationId, grounds, battles);
            if (g is null || g.Enterable(at)) continue;
            // Already leaving by a road that is still open: it has paid, and it is going.
            if (f.Route.Count > 0 && f.Overrun && g.Passing(f, f.Route[0])) continue;
            WithdrawOne(c, g, f, type);
            changed++;
        }
        return changed;
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
        f.Route.Clear(); f.RouteModes.Clear(); f.MoveProgress = 0f;
        var target = SafeProvince(g, f, type, at);
        var away = target > 0 && (type.Movement == SupportMoveMode.Air && SupportCore.DistanceKm(c, at, target) > FerryKm(c, type)
            ? FerryHops(g, type, at, target, f.Route, f.RouteModes)
            : Plan(g, type, at, target, f.Route, f.RouteModes, out _, corridor: true).Length == 0);
        if (away && f.Route.Count > 0)
        {
            if (f.CutOff) f.CutOff = false;
            Withdrawals++;
            DiagnosticLogs.Write("orders", $"SUPPORT_WITHDRAW formation={f.Id} nation={g.Nation.Tag} from={at} to={target}");
            return;
        }
        f.Route.Clear(); f.RouteModes.Clear();
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
            int best = -1, fallback = -1, last = -1;
            float bestKm = float.MaxValue, fallbackKm = float.MaxValue, lastKm = float.MaxValue;
            for (var p = 1; p <= g.Map.ProvinceCount; p++)
            {
                if (p == at || !g.Enterable(p) || g.Battles.Contains(p)) continue;
                var km = SupportCore.DistanceKm(g.Campaign, at, p);
                if (km > ferry) continue;
                var quiet = !g.BordersEnemy(p);
                if (g.Own(p) && quiet && km < bestKm) { best = p; bestKm = km; }
                else if (quiet && km < fallbackKm) { fallback = p; fallbackKm = km; }
                else if (g.Own(p) && km < lastKm) { last = p; lastKm = km; }
            }
            return best > 0 ? best : fallback > 0 ? fallback : last;
        }
        var search = _outbound ??= new Search();
        search.Run(g, at, corridor: true);
        int sideQuiet = -1, ownAny = -1;
        foreach (var p in search.Reached)
        {
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
    public static void Reposition(Campaign c)
    {
        var map = c?.ProvinceMap;
        if (map is null) return;
        var d = c.Defines;
        if (c.ElapsedDays % Math.Max(1, d.AiLongRangeRepositionDays) != 0) return;
        var wars = Warfare.LiveWars(c);
        if (wars.Count == 0) return;
        var byNation = new Dictionary<int, List<SupportFormation>>();
        foreach (var f in InIdOrder(c))
        {
            if (!byNation.TryGetValue(f.NationId, out var list)) byNation[f.NationId] = list = new List<SupportFormation>();
            list.Add(f);
        }
        var battlesByWar = new Dictionary<int, List<int>>();
        foreach (var battle in c.Battles)
        {
            if (battle.Ended) continue;
            if (!battlesByWar.TryGetValue(battle.WarId, out var list)) battlesByWar[battle.WarId] = list = new List<int>();
            list.Add(battle.ProvinceId);
        }
        var battles = BattleProvinces(c);
        var grounds = new Dictionary<int, Ground>();
        foreach (var nation in c.Nations.OrderBy(n => n.Id))
        {
            if (nation.IsHuman || !byNation.TryGetValue(nation.Id, out var own)) continue;
            var inWar = false;
            foreach (var war in wars) if (!war.Ended && war.Involves(nation.Id)) { inWar = true; break; }
            if (!inWar) continue;
            var g = GroundOf(c, nation.Id, grounds, battles);
            if (g is null) continue;
            RepositionNation(c, g, own, wars, battlesByWar);
        }
    }

    private static void RepositionNation(Campaign c, Ground g, List<SupportFormation> own,
        IReadOnlyList<StrategicWar> wars, Dictionary<int, List<int>> battlesByWar)
    {
        var map = g.Map;
        var d = c.Defines;
        // What the war is, for this country: the battles of its wars - where the fire is wanted
        // today - or, while its wars have none, its side's front: every province of its side that
        // touches the enemy's ground. A battery covering a quiet stretch of front while the
        // fighting is two hundred kilometres up the line is standing in the wrong place.
        var targets = new List<int>();
        var targetSet = new HashSet<int>();
        foreach (var war in wars)
        {
            if (war.Ended || !war.Involves(g.Nation.Id) || !battlesByWar.TryGetValue(war.Id, out var list)) continue;
            foreach (var province in list) if (g.Valid(province) && targetSet.Add(province)) targets.Add(province);
        }
        if (targets.Count == 0)
            for (var p = 1; p <= map.ProvinceCount; p++)
            {
                if (map.IsWater(p) || !g.Passable.Contains(g.Controls[p]) || targetSet.Contains(p)) continue;
                if (g.BordersEnemy(p) && targetSet.Add(p)) targets.Add(p);
            }
        if (targets.Count == 0) return;
        targets.Sort();

        var idle = new List<(SupportFormation Formation, SupportTypeDefinition Type)>();
        var sams = new List<(SupportFormation Formation, SupportTypeDefinition Type)>();
        foreach (var f in own)
        {
            if (!SupportTypeCatalog.TryGet(f.TypeId, out var type) || !SupportCore.Enabled(c, type)) continue;
            // Only the standing: nothing on the road, deploying, or cut off is re-planned.
            if (f.Route.Count > 0 || f.DeployDaysLeft > 0 || f.CutOff || f.BaseProvinceId <= 0 || SupportCore.Displaced(c, f)) continue;
            if (type.Kind == SupportKind.AirDefence) { sams.Add((f, type)); continue; }
            // A missile brigade or a long-range drone group on strike work answers to its targets,
            // not to the battles: depots and power stations a thousand kilometres behind the line
            // are what it is for, and while any of them is in reach it stays where it is.
            if (f.Mission == SupportCore.MissionStrike && type.Strike > 0f)
            {
                if (LongRangeStrikes.Preview(c, f).Province <= 0) idle.Add((f, type));
                continue;
            }
            var reach = SupportCore.ReachKm(c, type);
            var covers = false;
            foreach (var target in targets)
                if (SupportCore.DistanceKm(c, f.BaseProvinceId, target) <= reach) { covers = true; break; }
            if (!covers) idle.Add((f, type));
        }
        // SAM brigades guard the cities first; the ones no city needs - nothing of the enemy's
        // can reach any, or every threatened one is covered - go where the fighting is, to keep
        // the enemy's aircraft off it, like any other formation that reaches no battle.
        if (sams.Count > 0)
            foreach (var (f, type) in CoverWithSams(c, g, sams))
            {
                var reach = SupportCore.ReachKm(c, type);
                if (!targets.Any(target => SupportCore.DistanceKm(c, f.BaseProvinceId, target) <= reach)) idle.Add((f, type));
            }

        if (idle.Count > 0)
        {
            var bound = 0f;
            foreach (var (_, type) in idle) bound = MathF.Max(bound, Standoff(c, type));
            var near = DistanceField(c, map, targets, bound, out var within);
            foreach (var (f, type) in idle)
            {
                var standoff = Standoff(c, type);
                var mayTouch = type.RangeKm < d.AiLongRangeFrontlineMaxRangeKm;
                var origin = f.BaseProvinceId;
                var ordered = type.Movement == SupportMoveMode.Air
                    ? FlyToward(c, g, f, type, origin, within, near, standoff, mayTouch)
                    : DriveToward(c, g, f, type, origin, within, near, standoff, mayTouch);
                if (ordered > 0 && near.GetValueOrDefault(ordered, float.MaxValue) < near.GetValueOrDefault(origin, float.MaxValue))
                    AiOrdersTowardFront++;
            }
        }
    }

    /// <summary>
    /// A formation that drives: the suiting province the road reaches soonest; with none by road -
    /// a war across the water - the nearest suiting ones in a straight line, the first the order
    /// accepts, by ship if it must. Returns where it was sent, or -1.
    /// </summary>
    private static int DriveToward(Campaign c, Ground g, SupportFormation f, SupportTypeDefinition type, int origin,
        List<int> within, Dictionary<int, float> near, float standoff, bool mayTouch)
    {
        var chosen = NearestByRoad(c, g, type, origin, near, standoff, mayTouch);
        if (chosen > 0 && chosen != origin) return Order(c, g.Nation.Id, f.Id, chosen) ? chosen : -1;
        var tried = 0;
        foreach (var province in within.Where(p => p != origin && Suits(g, p, near, standoff, mayTouch))
                     .OrderBy(p => SupportCore.DistanceKm(c, origin, p)).ThenBy(p => p))
        {
            if (++tried > 4) break;
            if (!PreviewOne(c, g.Nation.Id, f.Id, province, false).Accepted) continue;
            return Order(c, g.Nation.Id, f.Id, province) ? province : -1;
        }
        return -1;
    }

    /// <summary>
    /// A formation that flies: the suiting province nearest in one flight; with none that close,
    /// the nearest at all, reached in hops - each to a province of its side within one ferry
    /// flight and nearer the destination - given as one order with its waypoints, the way a
    /// player gives it with Shift+right-click. Returns where it was sent, or -1.
    /// </summary>
    private static int FlyToward(Campaign c, Ground g, SupportFormation f, SupportTypeDefinition type, int origin,
        List<int> within, Dictionary<int, float> near, float standoff, bool mayTouch)
    {
        var chosen = NearestByAir(c, g, type, origin, within, near, standoff, mayTouch);
        if (chosen > 0 && chosen != origin) return Order(c, g.Nation.Id, f.Id, chosen) ? chosen : -1;
        var goal = -1; var goalKm = float.MaxValue;
        foreach (var province in within)
        {
            if (province == origin || !Suits(g, province, near, standoff, mayTouch)) continue;
            var km = SupportCore.DistanceKm(c, origin, province);
            if (km < goalKm) { goal = province; goalKm = km; }
        }
        if (goal <= 0) return -1;
        var ferry = FerryKm(c, type);
        var hops = new List<int>();
        var at = origin;
        // Six legs is a wing across a continent; a war further than that is not this wing's.
        while (SupportCore.DistanceKm(c, at, goal) > ferry && hops.Count < 6)
        {
            var next = -1; var nextKm = SupportCore.DistanceKm(c, at, goal) - 100f;
            for (var p = 1; p <= g.Map.ProvinceCount; p++)
            {
                if (!g.Enterable(p) || g.Battles.Contains(p) || SupportCore.DistanceKm(c, at, p) > ferry) continue;
                var left = SupportCore.DistanceKm(c, p, goal);
                if (left < nextKm) { next = p; nextKm = left; }
            }
            if (next <= 0) return -1;
            hops.Add(next);
            at = next;
        }
        if (SupportCore.DistanceKm(c, at, goal) > ferry) return -1;
        hops.Add(goal);
        if (!Order(c, g.Nation.Id, f.Id, hops[0])) return -1;
        for (var leg = 1; leg < hops.Count; leg++)
            new RelocateSupportCommand { IssuerId = g.Nation.Id, FormationIds = new[] { f.Id }, ProvinceId = hops[leg], Append = true }.Apply(c);
        return goal;
    }

    /// <summary>
    /// How far from the fighting the computer puts a formation: a share of its reach, so the front
    /// can move and it still reaches - except for the guns that belong on the front itself, which
    /// take their whole reach. A province is about 65 km across, and three fifths of a 45 km gun
    /// line's reach is less than one: held to the share, it could never stand next to a battle.
    /// </summary>
    private static float Standoff(Campaign c, SupportTypeDefinition type)
    {
        var d = c.Defines;
        var reach = SupportCore.ReachKm(c, type);
        return type.RangeKm < d.AiLongRangeFrontlineMaxRangeKm ? reach : d.AiLongRangeStandoffShare * reach;
    }

    private static bool Order(Campaign c, int nationId, int formationId, int province)
    {
        var order = new RelocateSupportCommand { IssuerId = nationId, FormationIds = new[] { formationId }, ProvinceId = province };
        order.Apply(c);
        if (order.Moved == 0) return false;
        AiOrders++;
        return true;
    }

    /// <summary>
    /// Kilometres from the nearest battle or front province, along the province graph, out to a
    /// bound - never shorter than the straight line, so a province inside the bound by this
    /// measure is inside it in fact. Ties never matter: a shortest distance is the same however
    /// equal-cost provinces are popped.
    /// </summary>
    private static Dictionary<int, float> DistanceField(Campaign c, ProvinceTopology map, List<int> sources, float bound, out List<int> within)
    {
        var distance = new Dictionary<int, float>();
        within = new List<int>();
        var queue = new PriorityQueue<int, (float, int)>();
        foreach (var source in sources) { distance[source] = 0f; queue.Enqueue(source, (0f, source)); }
        while (queue.TryDequeue(out var province, out var key))
        {
            if (key.Item1 > distance[province]) continue;
            within.Add(province);
            foreach (var next in map.Neighbors[province])
            {
                var km = key.Item1 + SupportCore.DistanceKm(c, province, next);
                if (km > bound || distance.TryGetValue(next, out var known) && known <= km) continue;
                distance[next] = km;
                queue.Enqueue(next, (km, next));
            }
        }
        within.Sort();
        return distance;
    }

    private static bool Suits(Ground g, int province, Dictionary<int, float> near, float standoff, bool mayTouch) =>
        g.Enterable(province) && !g.Battles.Contains(province) &&
        near.TryGetValue(province, out var km) && km <= standoff &&
        (mayTouch || !g.BordersEnemy(province));

    /// <summary>The province suiting the formation that the road reaches soonest, ties to the lower id.</summary>
    private static int NearestByRoad(Campaign c, Ground g, SupportTypeDefinition type, int origin,
        Dictionary<int, float> near, float standoff, bool mayTouch)
    {
        var days = new Dictionary<int, float> { [origin] = 0f };
        var queue = new PriorityQueue<int, (float, int)>();
        queue.Enqueue(origin, (0f, origin));
        var popped = 0;
        while (queue.TryDequeue(out var province, out var key))
        {
            if (key.Item1 > days[province]) continue;
            if (province != origin && Suits(g, province, near, standoff, mayTouch)) return province;
            // A month of driving is not a repositioning, and a continent is not a search space.
            if (++popped > 6000 || key.Item1 > 45f) break;
            foreach (var next in g.Map.Neighbors[province])
            {
                if (!g.Enterable(next)) continue;
                var pace = PaceOf(g, type, province, next, SupportMoveMode.Land, out _);
                var arrive = key.Item1 + MathF.Max(1f, 1f / pace);
                if (days.TryGetValue(next, out var known) && known <= arrive) continue;
                days[next] = arrive;
                queue.Enqueue(next, (arrive, next));
            }
        }
        return -1;
    }

    /// <summary>The suiting province nearest in a straight line within one ferry flight, ties to the lower id.</summary>
    private static int NearestByAir(Campaign c, Ground g, SupportTypeDefinition type, int origin, List<int> within,
        Dictionary<int, float> near, float standoff, bool mayTouch)
    {
        var ferry = FerryKm(c, type);
        var best = -1;
        var bestKm = float.MaxValue;
        foreach (var province in within)
        {
            if (province == origin || !Suits(g, province, near, standoff, mayTouch)) continue;
            var km = SupportCore.DistanceKm(c, origin, province);
            if (km > ferry || km >= bestKm) continue;
            best = province; bestKm = km;
        }
        return best;
    }

    /// <summary>
    /// SAM brigades go where the enemy's strike arm can hit and nobody covers yet - the most
    /// populous such region first - unless the brigade already stands under that threat.
    /// </summary>
    private static List<(SupportFormation Formation, SupportTypeDefinition Type)> CoverWithSams(Campaign c, Ground g,
        List<(SupportFormation Formation, SupportTypeDefinition Type)> sams)
    {
        // The brigades no city needs, for the review to send toward the fighting instead.
        var spare = new List<(SupportFormation Formation, SupportTypeDefinition Type)>();
        var map = g.Map;
        var strikers = new List<(int Province, float Reach)>();
        foreach (var f in c.SupportFormations)
        {
            var at = f.BaseProvinceId > 0 ? f.BaseProvinceId : At(f);
            if (at <= 0 || at > map.ProvinceCount) continue;
            if (!SupportTypeCatalog.TryGet(f.TypeId, out var type) || type.Strike <= 0f || !SupportCore.Enabled(c, type)) continue;
            var owner = c.NationById(f.NationId);
            if (owner is null || !g.Hostile.Contains((ushort)owner.MapIndex)) continue;
            strikers.Add((at, SupportCore.ReachKm(c, type)));
        }
        if (strikers.Count == 0) return sams;
        bool Threatened(int province)
        {
            foreach (var (at, reach) in strikers)
                if (SupportCore.DistanceKm(c, at, province) <= reach) return true;
            return false;
        }

        // The regions in danger, most people first; each named by the province its bases take.
        var held = new Dictionary<int, List<int>>();
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (!g.Own(p) || map.IsWater(p)) continue;
            var region = map.ProvinceToState[p];
            if (!held.TryGetValue(region, out var list)) held[region] = list = new List<int>();
            list.Add(p);
        }
        var regions = c.States.Where(s => s.NationMapIndex == g.Nation.MapIndex && held.ContainsKey(s.MapIndex))
            .OrderByDescending(s => s.Population).ThenBy(s => s.MapIndex).Take(40).ToList();
        var guarded = new List<(int Province, float Reach)>();
        foreach (var (f, type) in sams) guarded.Add((Destination(f), SupportCore.ReachKm(c, type)));
        foreach (var other in c.SupportFormations)
        {
            if (other.NationId != g.Nation.Id || other.Route.Count == 0) continue;
            if (SupportTypeCatalog.TryGet(other.TypeId, out var otherType) && otherType.Kind == SupportKind.AirDefence)
                guarded.Add((Destination(other), SupportCore.ReachKm(c, otherType)));
        }
        var anchors = new Dictionary<int, int>();
        foreach (var (f, type) in sams)
        {
            if (Threatened(f.BaseProvinceId)) continue;
            var sent = false;
            foreach (var region in regions)
            {
                if (!anchors.TryGetValue(region.MapIndex, out var anchor))
                    anchors[region.MapIndex] = anchor = SupportCore.ChooseBase(map, held[region.MapIndex]);
                if (anchor <= 0 || !Threatened(anchor) || !g.Enterable(anchor) || g.Battles.Contains(anchor)) continue;
                var covered = false;
                foreach (var (at, reach) in guarded)
                    if (at > 0 && SupportCore.DistanceKm(c, at, anchor) <= reach) { covered = true; break; }
                if (covered) continue;
                if (Order(c, g.Nation.Id, f.Id, anchor))
                {
                    guarded.Add((anchor, SupportCore.ReachKm(c, type)));
                    AiCityCover++;
                    sent = true;
                }
                break;
            }
            if (!sent) spare.Add((f, type));
        }
        return spare;
    }

    // ------------------------------------------------------------------ the fingerprint

    /// <summary>The rules a peer must agree on, in a fixed order; compared against the shipped defaults.</summary>
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
/// </summary>
public sealed class RelocateSupportCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int[] FormationIds { get; init; } = Array.Empty<int>();
    public int ProvinceId { get; init; }
    public bool Append { get; init; }
    public string Refusal { get; private set; } = "";
    public int Moved { get; private set; }
    public int Refused { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        Moved = 0; Refused = 0; Refusal = "";
        var ids = (FormationIds ?? Array.Empty<int>()).Distinct().OrderBy(id => id).Take(4096).ToArray();
        if (ids.Length == 0 || campaign.ProvinceMap is null) { Refusal = LongRangeMovement.RefuseForeign; return; }
        var lines = new List<string>();
        var grounds = new Dictionary<int, LongRangeMovement.Ground>();
        var battles = LongRangeMovement.BattleProvinces(campaign);
        foreach (var id in ids)
        {
            var plan = LongRangeMovement.PreviewOne(campaign, IssuerId, id, ProvinceId, Append, grounds, battles);
            var formation = campaign.SupportFormations.Find(f => f.Id == id && f.NationId == IssuerId);
            if (!plan.Accepted || formation is null)
            {
                Refused++;
                var name = formation is not null && SupportTypeCatalog.TryGet(formation.TypeId, out var named) ? named.Name : "Formation";
                lines.Add($"{name} #{id}: {plan.RefusalText}");
                continue;
            }
            if (LongRangeMovement.Commit(campaign, formation, SupportTypeCatalog.Get(formation.TypeId), plan)) Moved++;
        }
        Refusal = string.Join("\n", lines);
        if (Moved > 0) campaign.MarkStrategicChange();
    }
}
