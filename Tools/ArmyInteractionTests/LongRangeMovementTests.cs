using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Long-range forces on the map (LongRangeMovement), pinned to what the owner said on
/// 17 November 2026:
///
///   "they move instantly, through the air, without a trace" - no formation changes position
///       without travelling: a road step a day at most, a flight no faster than the relocation
///       pace, a sea passage through ports, and every day it is somewhere on the map along a path;
///   "let them move to ANY province" - any province they may stand in can be ordered, and one
///       they may not names why;
///   "let it be possible to deselect them, and select them normally" - the selection rule is the
///       divisions' rule (MapSelection), the one the map applies to both;
///   and what any of it must keep: old saves load, a save mid-move continues identically, two
///   worlds from one seed agree, the computer uses the same order, coalition allies fly.
/// Run with --long-range-movement-only; part of the full run.
/// </summary>
static class LongRangeMovementTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL long-range movement: " + name);
        Console.WriteLine("PASS long-range movement: " + name);
    }

    static Campaign World(ProvinceTopology map, string player = "RUS", int seed = 20261117)
    {
        var c = Campaign.CreateBootstrap(seed);
        c.Defines.HoursPerTick = 24;
        c.ConfigureSinglePlayer(c.NationByTag(player).Id);
        c.AttachProvinceTopology(map);
        SupportTypeCatalog.ResetCatalog();
        return c;
    }

    static int _nextId = 880000;

    static SupportFormation Put(Campaign c, Nation nation, string type, int province, string mission = SupportCore.MissionStandby)
    {
        var f = new SupportFormation
        {
            Id = _nextId++, NationId = nation.Id, TypeId = type, ProvinceId = province, BaseProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], Strength = 1f, Experience = .3f, Mission = mission
        };
        c.SupportFormations.Add(f);
        return f;
    }

    static RelocateSupportCommand Order(Campaign c, Nation issuer, int province, bool append, params SupportFormation[] formations)
    {
        var order = new RelocateSupportCommand { IssuerId = issuer.Id, FormationIds = formations.Select(f => f.Id).ToArray(), ProvinceId = province, Append = append };
        order.Apply(c);
        return order;
    }

    /// <summary>One day of the movement passes alone: the hourly withdrawal check and the daily march.</summary>
    static void Day(Campaign c)
    {
        SupportRepatriation.Reconcile(c);
        LongRangeMovement.Advance(c);
    }

    static ushort[] Controls(Campaign c) => c.ProvinceController ?? c.ProvinceMap.ProvinceToNation;

    /// <summary>Steps from a province over one controller's dry ground, breadth-first.</summary>
    static Dictionary<int, int> Steps(Campaign c, int start, ushort owner, int limit)
    {
        var map = c.ProvinceMap; var controls = Controls(c);
        var steps = new Dictionary<int, int> { [start] = 0 };
        var queue = new Queue<int>(); queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (steps[at] >= limit) continue;
            foreach (var next in map.Neighbors[at])
            {
                if (steps.ContainsKey(next) || map.IsWater(next) || controls[next] != owner) continue;
                steps[next] = steps[at] + 1; queue.Enqueue(next);
            }
        }
        return steps;
    }

    static bool BordersHostile(Campaign c, int province, HashSet<ushort> hostile) =>
        c.ProvinceMap.Neighbors[province].Any(n => !c.ProvinceMap.IsWater(n) && hostile.Contains(Controls(c)[n]));

    static StrategicWar EnsureWar(Campaign c, Nation a, Nation b)
    {
        var war = c.Wars.FirstOrDefault(w => !w.Ended && w.Opposes(a.Id, b.Id));
        if (war is not null) return war;
        war = new StrategicWar { Id = 99 + c.Wars.Count * 1000, AttackerId = a.Id, DefenderId = b.Id };
        c.Wars.Add(war);
        return war;
    }

    public static void Run(ProvinceTopology map)
    {
        Selection();
        Land(map);
        Rail(map);
        Sea(map);
        Air(map);
        Refusals(map);
        AnyProvince(map);
        Crowding(map);
        Withdrawal(map);
        Stranded(map);
        SaveMidMove(map);
        OldSaves(map);
        Codec(map);
        Computer(map);
        Determinism(map);
        Halt(map);
        SeaFollowsTheWater(map);
        ShutStrait(map);
        StopAhead(map);
        StaleSelection(map);
        SelectionWiring();
    }

    // ------------------------------------------------------------------ selection

    static void Selection()
    {
        // The rule the map applies to divisions and formations alike.
        var set = new HashSet<int> { 1, 2 };
        MapSelection.Apply(set, new[] { 3 }, adding: false, removing: false);
        Check(set.SetEquals(new[] { 3 }), "a plain click replaces the selection, as for divisions");
        MapSelection.Apply(set, new[] { 4, 5 }, adding: true, removing: false);
        Check(set.SetEquals(new[] { 3, 4, 5 }), "the add key puts a box or a click beside what is selected");
        MapSelection.Apply(set, new[] { 4 }, adding: false, removing: true);
        Check(set.SetEquals(new[] { 3, 5 }), "the remove key takes a click or a box out of the selection");
        var armies = new List<string> { "a" };
        MapSelection.Apply(armies, new[] { "b", "a" }, adding: true, removing: false);
        Check(armies.SequenceEqual(new[] { "a", "b" }), "divisions answer the same rule, keeping their order");
        MapSelection.Apply(set, Array.Empty<int>(), adding: false, removing: false);
        Check(set.Count == 0, "an empty pick - Esc, a click on empty ground - lets go of everything");
    }

    // ------------------------------------------------------------------ 1. by road

    static void Land(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS");
        c.SupportFormations.Clear();
        var start = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var steps = Steps(c, start, (ushort)rus.MapIndex, 5);
        var target = steps.Where(p => p.Value == 5).Select(p => p.Key).OrderBy(p => p).First();
        var f = Put(c, rus, "artillery_brigade", start, SupportCore.MissionCloseAirSupport);
        SupportCore.IndexReach(c);
        Check(SupportCore.ReachOf(c, new[] { rus.Id }, start).Fire > 0f, "a deployed battery fires into its own province");
        var preview = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { f.Id }, target).Single();
        Check(preview.Accepted && preview.Route.Count == 5 && preview.Route[^1] == target, "a province five steps away is planned as five steps");
        Check(f.ProvinceId == start && f.BaseProvinceId == start && f.Route.Count == 0, "the preview moves nothing");
        var order = Order(c, rus, target, false, f);
        Check(order.Moved == 1 && f.BaseProvinceId == -1 && SupportCore.Displaced(c, f), "an order packs the battery up at once");
        SupportCore.IndexReach(c);
        Check(SupportCore.ReachOf(c, new[] { rus.Id }, start).Fire == 0f && !SupportCore.InRange(c, f, SupportTypeCatalog.Get(f.TypeId), start),
            "a packed-up battery reaches nothing");

        // The pace by hand: a division's march, the terrain, the roads, the kind's speed; one step a day at most.
        var type = SupportTypeCatalog.Get(f.TypeId);
        var expected = 0; var progress = 0f; var at = start;
        foreach (var (next, mode) in preview.Route.Zip(preview.Modes))
        {
            var pace = c.Defines.MarchProgressPerDay * map.TerrainAt(next).MovementSpeed * MovementPace.Roads(c, next) *
                       LandCrossings.MarchMultiplier(c, at, next) * type.Speed *
                       ((SupportMoveMode)mode == SupportMoveMode.Rail ? c.Defines.RailRedeploymentSpeed : 1f);
            do { progress += pace; expected++; } while (progress < .9999f);
            progress = Math.Clamp(progress - 1f, 0f, .999f);
            at = next;
        }
        expected += type.DeployDays;
        Check(preview.Days == expected, $"the days shown are the pace formula's: {preview.Days} against {expected}");

        var days = 0; at = f.ProvinceId; var trail = new List<int> { at };
        while (SupportCore.Displaced(c, f) && days < 60)
        {
            Day(c); days++;
            Check(f.ProvinceId == at || map.Neighbors[at].Contains(f.ProvinceId), $"day {days}: no farther than the next province");
            var (lat, lon) = LongRangeMovement.Position(c, f);
            Check(lat != 0f || lon != 0f, $"day {days}: the formation is somewhere on the map");
            if (f.ProvinceId != at) trail.Add(f.ProvinceId);
            at = f.ProvinceId;
            SupportCore.IndexReach(c);
            if (SupportCore.Displaced(c, f))
                Check(SupportCore.ReachOf(c, new[] { rus.Id }, f.ProvinceId).Fire == 0f, $"day {days}: nothing projected on the road");
        }
        Check(days == preview.Days, $"it works again on the day the preview said: {days} against {preview.Days}");
        Check(trail.Count == 6 && trail[^1] == target && f.BaseProvinceId == target, "it walked the five provinces and works from the last");
        SupportCore.IndexReach(c);
        Check(SupportCore.ReachOf(c, new[] { rus.Id }, target).Fire > 0f, "after deploying it fires from its new province");
    }

    // ------------------------------------------------------------------ 2. by rail

    static void Rail(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR");
        EnsureWar(c, rus, ukr);
        c.SupportFormations.Clear();
        var hostile = new HashSet<ushort> { (ushort)ukr.MapIndex };
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var inland = Steps(c, capital, (ushort)rus.MapIndex, 4).Where(p => p.Value == 4 && !BordersHostile(c, p.Key, hostile))
            .Select(p => p.Key).OrderBy(p => p).First();
        var f = Put(c, rus, "mlrs_battalion", capital);
        var deep = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { f.Id }, inland).Single();
        Check(deep.Accepted && deep.Modes.All(m => (SupportMoveMode)m == SupportMoveMode.Rail), "inland, away from the front, every step is by rail");
        Check(deep.Days == deep.Route.Count + SupportTypeCatalog.Get(f.TypeId).DeployDays, "by rail a province a day - faster than any road march");

        // Two neighbouring provinces of ours on the front.
        var controls = Controls(c);
        var front = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && controls[p] == rus.MapIndex && BordersHostile(c, p, hostile)).ToHashSet();
        var a = front.OrderBy(p => p).First(p => map.Neighbors[p].Any(front.Contains));
        var b = map.Neighbors[a].Where(front.Contains).Min();
        var g = Put(c, rus, "mlrs_battalion", a);
        var line = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { g.Id }, b).Single();
        Check(line.Accepted && line.Modes.Single() == (int)SupportMoveMode.Land, "along the front there are no trains: it marches");
        Check(line.Days >= deep.Days / Math.Max(1, deep.Route.Count) || line.Days > 1, "a march at the front takes its days");
    }

    // ------------------------------------------------------------------ 3. by sea

    static void Sea(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS");
        c.SupportFormations.Clear();
        var controls = Controls(c);
        // Kaliningrad: Russian ground with no road to the rest of Russia that we may drive.
        var exclave = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && controls[p] == rus.MapIndex &&
            map.CenterOf(p).Longitude < 23f && map.CenterOf(p).Latitude > 54f && map.CenterOf(p).Latitude < 56f).OrderBy(p => p).FirstOrDefault();
        Check(exclave > 0, "the Kaliningrad exclave is Russian ground on this map");
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var f = Put(c, rus, "artillery_brigade", capital);
        var plan = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { f.Id }, exclave).Single();
        Check(plan.Accepted && plan.Modes.Count(m => (SupportMoveMode)m == SupportMoveMode.Sea) == 1, "with no road it ships once, port to port");
        var seaIndex = plan.Modes.IndexOf((int)SupportMoveMode.Sea);
        var departure = seaIndex == 0 ? capital : plan.Route[seaIndex - 1];
        var arrival = plan.Route[seaIndex];
        var coastal = SeaLanes.CoastalProvinces(map).ToHashSet();
        Check(coastal.Contains(departure) && coastal.Contains(arrival) &&
              SeaLanes.HarbourBasin(map, departure) == SeaLanes.HarbourBasin(map, arrival), "the passage is between two harbours of one sea");
        var km = SupportCore.DistanceKm(c, departure, arrival);
        Check(plan.Days >= km / c.Defines.LongRangeSeaKmPerDay + 2 * c.Defines.LongRangePortDays,
            $"the passage takes its distance at sea plus the days in port: {plan.Days} d for {km:0} km");
        Order(c, rus, exclave, false, f);
        var days = 0; var last = LongRangeMovement.Position(c, f);
        while (SupportCore.Displaced(c, f) && days < 120)
        {
            var wasAt = f.ProvinceId; var mode = f.RouteModes.Count > 0 ? (SupportMoveMode)f.RouteModes[0] : SupportMoveMode.Land;
            Day(c); days++;
            var now = LongRangeMovement.Position(c, f);
            var moved = Distance(last, now);
            Check(mode == SupportMoveMode.Sea ? moved <= c.Defines.LongRangeSeaKmPerDay + 1f
                : f.ProvinceId == wasAt || map.Neighbors[wasAt].Contains(f.ProvinceId), $"day {days}: no jump by road or by sea");
            last = now;
        }
        Check(f.ProvinceId == exclave && f.BaseProvinceId == exclave, $"it arrived and deployed in Kaliningrad after {days} days");

        // The harbour ahead falls half-way across: the ship turns about where it is - it does not
        // reappear in the port it left that morning, a whole sea back.
        var back = Put(c, rus, "artillery_brigade", capital);
        Order(c, rus, exclave, false, back);
        for (var day = 0; day < 60 && !(LongRangeMovement.OverWater(back) && back.MoveProgress > .3f); day++) Day(c);
        Check(LongRangeMovement.OverWater(back) && (SupportMoveMode)back.RouteModes[0] == SupportMoveMode.Sea, "a second brigade is at sea");
        var harbour = back.Route[0]; var leftFrom = back.ProvinceId;
        var stranger = c.Nations.First(n => n.MapIndex > 0 && n.Id != rus.Id && !MilitaryAccess.CanEnter(c, rus.Id, (ushort)n.MapIndex) &&
                                            !MilitaryAccess.Hostile(c, rus.Id, n.Id));
        var heldBy = controls[harbour];
        c.SetProvinceController(harbour, (ushort)stranger.MapIndex);
        try
        {
            var before = LongRangeMovement.Position(c, back);
            Day(c);
            var after = LongRangeMovement.Position(c, back);
            Check(back.Route.Count > 0 && back.Route[0] == leftFrom && Distance(before, after) <= c.Defines.LongRangeSeaKmPerDay + 1f,
                $"the harbour ahead fell at sea: it turns about where it is, {Distance(before, after):0} km from last night");
            last = after; days = 0;
            while ((back.Route.Count > 0 || back.DeployDaysLeft > 0) && days < 160)
            {
                var wasAt = back.ProvinceId; var mode = back.RouteModes.Count > 0 ? (SupportMoveMode)back.RouteModes[0] : SupportMoveMode.Land;
                Day(c); days++;
                var now = LongRangeMovement.Position(c, back);
                Check(mode == SupportMoveMode.Sea ? Distance(last, now) <= c.Defines.LongRangeSeaKmPerDay + 1f
                    : back.ProvinceId == wasAt || map.Neighbors[wasAt].Contains(back.ProvinceId), $"turned back, day {days}: no jump");
                last = now;
            }
            Check(!back.CutOff && back.ProvinceId != harbour && controls[back.ProvinceId] == rus.MapIndex,
                $"and it ends on its own ground, not in the fallen harbour, after {days} days");
        }
        finally { c.SetProvinceController(harbour, heldBy); }

        // An exclave with no coast and no road we may drive: Nakhchivan, from Baku.
        var aze = c.NationByTag("AZE");
        if (aze is not null)
        {
            var azControls = (ushort)aze.MapIndex;
            var seat = CapitalCore.SupplyOriginProvinceId(c, aze);
            var home = Steps(c, seat, azControls, 10000);
            var cut = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && controls[p] == azControls && !home.ContainsKey(p) && !coastal.Contains(p))
                .OrderBy(p => p).FirstOrDefault();
            if (cut > 0)
            {
                var gun = Put(c, aze, "artillery_brigade", seat);
                var refused = LongRangeMovement.PreviewRelocate(c, aze.Id, new[] { gun.Id }, cut).Single();
                Check(refused.Refusal == LongRangeMovement.RefuseNoRoute || refused.Accepted && refused.Modes.All(m => m != (int)SupportMoveMode.Sea),
                    "an exclave with no port and no road we may drive is refused: " + refused.RefusalText);
            }
            else Console.WriteLine("NOTE long-range movement: no landlocked Azerbaijani exclave on this map; the no-port refusal is pinned by the refusal test");
        }
    }

    static float Distance((float Latitude, float Longitude) a, (float Latitude, float Longitude) b)
    {
        var radians = MathF.PI / 180f;
        var dLat = (b.Latitude - a.Latitude) * radians; var dLon = (b.Longitude - a.Longitude) * radians;
        var h = MathF.Sin(dLat / 2) * MathF.Sin(dLat / 2) + MathF.Cos(a.Latitude * radians) * MathF.Cos(b.Latitude * radians) * MathF.Sin(dLon / 2) * MathF.Sin(dLon / 2);
        return 6371f * 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(h)));
    }

    // ------------------------------------------------------------------ 4. by air

    static void Air(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS");
        c.SupportFormations.Clear();
        var controls = Controls(c);
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var ours = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && controls[p] == rus.MapIndex).ToList();
        var wing = Put(c, rus, "fighter_wing", capital);
        var ferry = LongRangeMovement.FerryKm(c, SupportTypeCatalog.Get(wing.TypeId));
        var far = ours.OrderByDescending(p => SupportCore.DistanceKm(c, capital, p)).ThenBy(p => p).First();
        var refused = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { wing.Id }, far).Single();
        Check(refused.Refusal == LongRangeMovement.RefuseFerry && refused.RefusalArgs.Length == 2 &&
              (float)refused.RefusalArgs[1] == MathF.Round(ferry) && (float)refused.RefusalArgs[0] > ferry,
            $"a flight beyond the ferry range is refused with both numbers: {refused.RefusalText}");
        var leg = ours.Where(p => SupportCore.DistanceKm(c, capital, p) is > 1200f and < 1450f).OrderBy(p => p).First();
        var km = SupportCore.DistanceKm(c, capital, leg);
        var plan = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { wing.Id }, leg).Single();
        Check(plan.Accepted && plan.Modes.Single() == (int)SupportMoveMode.Air &&
              plan.Days == (int)MathF.Ceiling(km / c.Defines.LongRangeAirKmPerDay) + 1,
            $"a ferry flight takes its distance at the relocation pace, then a day to deploy: {plan.Days} d for {km:0} km");
        Order(c, rus, leg, false, wing);
        // A waypoint: the next leg from where this one ends.
        var onward = ours.Where(p => SupportCore.DistanceKm(c, leg, p) is > 900f and < 1400f && SupportCore.DistanceKm(c, capital, p) > ferry)
            .OrderBy(p => p).FirstOrDefault();
        Check(onward > 0, "there is a province one leg beyond the first and out of reach of one flight");
        var appended = Order(c, rus, onward, true, wing);
        Check(appended.Moved == 1 && wing.Route.Count == 2 && wing.Route[0] == leg && wing.Route[1] == onward,
            "Shift+right-click adds a waypoint: two legs, each within the ferry range");
        var last = LongRangeMovement.Position(c, wing); var days = 0;
        while (SupportCore.Displaced(c, wing) && days < 30)
        {
            Day(c); days++;
            var now = LongRangeMovement.Position(c, wing);
            Check(Distance(last, now) <= c.Defines.LongRangeAirKmPerDay + 1f, $"day {days}: no faster than {c.Defines.LongRangeAirKmPerDay} km a day");
            last = now;
        }
        Check(wing.BaseProvinceId == onward, $"the wing works from the far end after {days} days");
    }

    // ------------------------------------------------------------------ 5. every refusal

    static void Refusals(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR");
        EnsureWar(c, rus, ukr);
        c.SupportFormations.Clear();
        var controls = Controls(c);
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var gun = Put(c, rus, "mlrs_battalion", capital);
        string Why(int province) => LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { gun.Id }, province).Single().Refusal;

        var sea = Enumerable.Range(1, map.ProvinceCount).First(p => map.IsWater(p));
        Check(Why(sea) == LongRangeMovement.RefuseSea, "the sea is refused");
        var enemy = Enumerable.Range(1, map.ProvinceCount).First(p => !map.IsWater(p) && controls[p] == ukr.MapIndex);
        Check(Why(enemy) == LongRangeMovement.RefuseEnemy, "the enemy's ground is refused");
        var stranger = c.Nations.First(n => n.MapIndex > 0 && n.Id != rus.Id && !MilitaryAccess.CanEnter(c, rus.Id, (ushort)n.MapIndex) &&
                                            !MilitaryAccess.Hostile(c, rus.Id, n.Id) &&
                                            Enumerable.Range(1, map.ProvinceCount).Any(p => controls[p] == n.MapIndex && !map.IsWater(p)));
        var closed = Enumerable.Range(1, map.ProvinceCount).First(p => !map.IsWater(p) && controls[p] == stranger.MapIndex);
        Check(Why(closed) == LongRangeMovement.RefuseAccess, "a country that does not let us in is refused: " + stranger.Tag);
        // The same country letting us in: its ground is ours to stand on.
        var relation = CampaignDiplomacy.Get(c, rus.Id, stranger.Id);
        relation.MilitaryAccessByA = relation.MilitaryAccessByB = true;
        Check(Why(closed) is "" or LongRangeMovement.RefuseNoRoute, "a country that lets us in is ground we may be sent to");
        relation.MilitaryAccessByA = relation.MilitaryAccessByB = false;
        var near = Steps(c, capital, (ushort)rus.MapIndex, 2).Where(p => p.Value == 2).Select(p => p.Key).Min();
        c.Battles.Add(new LandBattle { Id = 77001, WarId = -1, ProvinceId = near, AttackerId = ukr.Id, DefenderId = rus.Id, StartedDay = c.ElapsedDays });
        Check(Why(near) == LongRangeMovement.RefuseBattle, "a province with a battle in it is refused");
        c.Battles.RemoveAll(b => b.Id == 77001);
        var destroyed = c.ProvinceMap.DestroyedByProvince;
        var ruin = destroyed[near];
        destroyed[near] = true;
        try { Check(Why(near) == LongRangeMovement.RefuseDestroyed, "a destroyed province is refused"); }
        finally { destroyed[near] = ruin; }
        Check(Why(near) == "", "the same province, whole and quiet, is accepted");
        var foreign = Put(c, ukr, "artillery_brigade", enemy);
        Check(LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { foreign.Id }, near).Single().Refusal == LongRangeMovement.RefuseForeign,
            "somebody else's formation is refused");
        c.SetFeature("artillery", false);
        var art = Put(c, rus, "artillery_brigade", capital);
        Check(LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { art.Id }, near).Single().Refusal == LongRangeMovement.RefuseDisabled,
            "a kind switched off in the scenario is refused");
        c.SetFeature("artillery", true);
    }

    // ------------------------------------------------------------------ 6. any province

    static void AnyProvince(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS");
        c.SupportFormations.Clear();
        var controls = Controls(c);
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var near = Steps(c, capital, (ushort)rus.MapIndex, 6);
        var region = near.Keys.Select(p => map.ProvinceToState[p]).Distinct().OrderBy(r => r)
            .First(r => r != map.ProvinceToState[capital] && near.Keys.Count(p => map.ProvinceToState[p] == r) >= 2);
        var leading = SupportCore.BaseProvinceOf(c, rus, region);
        var other = near.Keys.Where(p => map.ProvinceToState[p] == region && p != leading).Min();
        var gun = Put(c, rus, "mlrs_battalion", capital);
        Check(Order(c, rus, other, false, gun).Moved == 1, "a province that is not its region's leading one is accepted");
        for (var day = 0; day < 40 && SupportCore.Displaced(c, gun); day++) Day(c);
        Check(gun.ProvinceId == other && gun.BaseProvinceId == other && gun.StateMapIndex == region,
            "and the formation stands exactly there - the game no longer picks the province");
    }

    // ------------------------------------------------------------------ 7. crowding

    static void Crowding(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS");
        c.SupportFormations.Clear(); c.SupportOrders.Clear();
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var crowded = map.ProvinceToState[capital];
        var state = c.StateByMapIndex(crowded);
        var capacity = SupportCore.CapacityOf(c, state);
        var stack = new List<SupportFormation>();
        for (var i = 0; i < capacity + 3; i++) stack.Add(Put(c, rus, "artillery_brigade", capital));
        var used = SupportCore.BasingUsed(c, crowded);
        Check(used > capacity, $"a region may be crowded past its capacity: used {used} of {capacity}");
        var away = Steps(c, capital, (ushort)rus.MapIndex, 8).Where(p => p.Value == 8 && map.ProvinceToState[p.Key] != crowded).Select(p => p.Key).Min();
        var lone = Put(c, rus, "artillery_brigade", away);
        var newcomer = Put(c, rus, "artillery_brigade", away);
        var plan = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { newcomer.Id }, capital).Single();
        Check(plan.Accepted && plan.BasingUsed > plan.BasingCapacity && plan.BasingCapacity == capacity,
            $"an order into a crowded region is accepted and says 'used {plan.BasingUsed} of {plan.BasingCapacity}'");
        c.SupportFormations.Remove(newcomer);
        foreach (var f in stack) f.Strength = .5f;
        lone.Strength = .5f;
        SupportCore.Tick(c);
        var crowdedGain = stack[0].Strength - .5f;
        var freeGain = lone.Strength - .5f;
        Check(freeGain > 0f && MathF.Abs(crowdedGain / freeGain - capacity / used) < .02f,
            $"recovery in the crowded region is multiplied by capacity / used: {crowdedGain / freeGain:0.000} against {capacity / used:0.000}");
    }

    // ------------------------------------------------------------------ 8. lost ground

    static void Withdrawal(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR");
        EnsureWar(c, rus, ukr);
        c.SupportFormations.Clear();
        var controls = Controls(c);
        var hostile = new HashSet<ushort> { (ushort)ukr.MapIndex };
        var front = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && controls[p] == rus.MapIndex && BordersHostile(c, p, hostile))
            .OrderBy(p => p).ToList();
        var stand = front[front.Count / 2];
        var gun = Put(c, rus, "artillery_brigade", stand);
        var wing = Put(c, rus, "fighter_wing", stand);
        c.SetProvinceController(stand, (ushort)ukr.MapIndex);
        SupportRepatriation.Reconcile(c);
        var loss = 1f - c.Defines.LongRangeOverrunLoss;
        Check(MathF.Abs(gun.Strength - loss) < 1e-6f && gun.ProvinceId == stand && gun.Route.Count > 0 &&
              map.Neighbors[stand].Contains(gun.Route[0]) && (SupportMoveMode)gun.RouteModes[0] is SupportMoveMode.Land or SupportMoveMode.Rail,
            "the overrun battery loses its share once and marches out from where it stood - no jump");
        Check(wing.Route.Count == 1 && (SupportMoveMode)wing.RouteModes[0] == SupportMoveMode.Air &&
              SupportCore.DistanceKm(c, stand, wing.Route[0]) <= LongRangeMovement.FerryKm(c, SupportTypeCatalog.Get(wing.TypeId)),
            "the overrun wing flies out within its ferry range");
        var at = gun.ProvinceId;
        for (var day = 0; day < 40 && (gun.Route.Count > 0 || gun.DeployDaysLeft > 0); day++)
        {
            Day(c);
            Check(gun.ProvinceId == at || map.Neighbors[at].Contains(gun.ProvinceId), "the withdrawal is a march, a province a day at most");
            at = gun.ProvinceId;
        }
        Check(MathF.Abs(gun.Strength - loss) < .02f && !SupportCore.Displaced(c, gun) && !BordersHostile(c, gun.ProvinceId, hostile),
            "the loss was charged once, and it works again away from the front");

        // Surrounded: nowhere to drive.
        var pocket = front[0];
        var trapped = Put(c, rus, "artillery_brigade", pocket);
        c.SetProvinceController(pocket, (ushort)ukr.MapIndex);
        foreach (var next in map.Neighbors[pocket]) if (!map.IsWater(next)) c.SetProvinceController(next, (ushort)ukr.MapIndex);
        SupportRepatriation.Reconcile(c);
        Check(trapped.CutOff && trapped.ProvinceId == pocket && trapped.Route.Count == 0 && SupportCore.Displaced(c, trapped),
            "with no way out it is cut off in place: on the map, doing nothing");
        var strength = trapped.Strength;
        Day(c); Day(c);
        Check(trapped.CutOff && trapped.Strength == strength && trapped.ProvinceId == pocket, "cut off it waits, and pays nothing more");
    }

    /// <summary>
    /// Left on ground it may not stand on with no road of its side to drive: a brigade in a far
    /// country that does not let it in. It goes home - by ship for what drives, in ferry hops for
    /// what flies - instead of standing there cut off while its own country is a sea away.
    /// </summary>
    static void Stranded(ProvinceTopology map)
    {
        var shipped = false; var flown = false;
        foreach (var tag in new[] { "BRA", "USA", "AUS", "ZAF", "IND", "ARG" })
        {
            if (shipped && flown) break;
            var c = World(map);
            var rus = c.NationByTag("RUS"); var far = c.NationByTag(tag);
            if (far is null || MilitaryAccess.CanEnter(c, rus.Id, (ushort)far.MapIndex)) continue;
            c.SupportFormations.Clear();
            var there = CapitalCore.SupplyOriginProvinceId(c, far);
            if (there <= 0) continue;
            var gun = Put(c, rus, "artillery_brigade", there);
            var wing = Put(c, rus, "fighter_wing", there);
            var strength = gun.Strength;
            SupportRepatriation.Reconcile(c);
            Check(gun.Strength == strength, $"{tag}: ground lost to nobody we fight costs nothing but the journey");
            if (!gun.CutOff && gun.RouteModes.Contains((int)SupportMoveMode.Sea))
            {
                shipped = true;
                var last = LongRangeMovement.Position(c, gun); var days = 0;
                while ((gun.Route.Count > 0 || gun.DeployDaysLeft > 0) && days < 240)
                {
                    var wasAt = gun.ProvinceId; var mode = gun.RouteModes.Count > 0 ? (SupportMoveMode)gun.RouteModes[0] : SupportMoveMode.Land;
                    Day(c); days++;
                    var now = LongRangeMovement.Position(c, gun);
                    Check(mode == SupportMoveMode.Sea ? Distance(last, now) <= c.Defines.LongRangeSeaKmPerDay + 1f
                        : gun.ProvinceId == wasAt || map.Neighbors[wasAt].Contains(gun.ProvinceId), $"{tag}: on the way home, day {days}: no jump (mode {mode}, {wasAt} -> {gun.ProvinceId}, {Distance(last, now):0} km, progress {gun.MoveProgress:0.000}, route {string.Join(',', gun.Route)} modes {string.Join(',', gun.RouteModes)})");
                    last = now;
                }
                Check(!gun.CutOff && Controls(c)[gun.ProvinceId] == rus.MapIndex && !SupportCore.Displaced(c, gun),
                    $"{tag}: a brigade stranded a sea away sails home and works there after {days} days");
            }
            if (!wing.CutOff && wing.Route.Count > 1 && wing.RouteModes.All(m => m == (int)SupportMoveMode.Air))
            {
                flown = true;
                var ferry = LongRangeMovement.FerryKm(c, SupportTypeCatalog.Get(wing.TypeId));
                var at = wing.ProvinceId; var legsOk = true;
                foreach (var hop in wing.Route) { legsOk &= SupportCore.DistanceKm(c, at, hop) <= ferry + 1f; at = hop; }
                Check(legsOk && Controls(c)[wing.Route[^1]] == rus.MapIndex, $"{tag}: a stranded wing is ferried home in legs within its ferry range");
            }
        }
        Check(shipped, "a brigade stranded across a sea is shipped home, not cut off");
        Check(flown, "a wing stranded beyond one flight from home hops home, not cut off");
    }

    // ------------------------------------------------------------------ 9. a save mid-move

    static void SaveMidMove(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS");
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var far = Steps(c, capital, (ushort)rus.MapIndex, 12).Where(p => p.Value == 12).Select(p => p.Key).Min();
        var gun = Put(c, rus, "artillery_brigade", capital);
        var wing = Put(c, rus, "fighter_wing", capital);
        Order(c, rus, far, false, gun);
        var ours = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && Controls(c)[p] == rus.MapIndex);
        Order(c, rus, ours.Where(p => SupportCore.DistanceKm(c, capital, p) is > 1540f and < 1640f).Min(), false, wing);
        c.IsPaused = false;
        c.TickDay();
        Check(gun.Route.Count > 0 && wing.Route.Count > 0 && wing.MoveProgress > 0f, "both are on their way when the game is saved");
        var folder = Path.Combine(Path.GetTempPath(), "dof-longrange-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            SaveSystem.Save(folder, "midmove", c, "test-mod");
            var loaded = SaveSystem.Load(folder, "midmove", "test-mod");
            Check(loaded.Success, "a save written mid-move loads: " + loaded.Warning);
            var r = loaded.Campaign;
            r.AttachProvinceTopology(map.CloneForOverrides());
            r.Defines.HoursPerTick = c.Defines.HoursPerTick;
            var restored = r.SupportFormations.Single(f => f.Id == wing.Id);
            Check(restored.MoveProgress == wing.MoveProgress && restored.Route.SequenceEqual(wing.Route) &&
                  restored.RouteModes.SequenceEqual(wing.RouteModes) && r.SupportFormations.Single(f => f.Id == gun.Id).Route.SequenceEqual(gun.Route),
                "the route and the share of the step made come back exactly");
            Check(CampaignFingerprint.Of(r) == CampaignFingerprint.Of(c), "the loaded world has the saved world's fingerprint");
            r.IsPaused = false;
            for (var day = 1; day <= 30; day++)
            {
                c.TickDay(); r.TickDay();
                if (CampaignFingerprint.Of(r) != CampaignFingerprint.Of(c))
                    Check(false, $"the loaded and the unsaved worlds part on day {day}");
            }
            Check(r.SupportFormations.Single(f => f.Id == gun.Id).ProvinceId == gun.ProvinceId,
                "thirty days on, the loaded and the unsaved worlds are the same world, column for column");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    // ------------------------------------------------------------------ 10. old saves

    static void OldSaves(ProvinceTopology map)
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        var c = World(map);
        var rus = c.NationByTag("RUS");
        c.SupportFormations.Clear();
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var based = JsonSerializer.Deserialize<SupportFormation>(
            $"{{\"Id\":5,\"NationId\":{rus.Id},\"TypeId\":\"mlrs_battalion\",\"StateMapIndex\":{map.ProvinceToState[capital]},\"BaseProvinceId\":{capital},\"Mission\":\"interdiction\"}}", options);
        var limbo = JsonSerializer.Deserialize<SupportFormation>(
            $"{{\"Id\":6,\"NationId\":{rus.Id},\"TypeId\":\"sam_brigade\",\"StateMapIndex\":-1,\"BaseProvinceId\":-1}}", options);
        Check(based.ProvinceId == 0 && based.Route.Count == 0 && !based.CutOff, "a formation saved before positions loads with none");
        c.SupportFormations.Add(based); c.SupportFormations.Add(limbo);
        SupportRepatriation.Reconcile(c);
        Check(based.ProvinceId == capital && based.BaseProvinceId == capital && !SupportCore.Displaced(c, based),
            "an old base becomes where the formation stands, deployed - the same world");
        var seat = CapitalCore.SupplyOriginProvinceId(c, rus);
        Check(limbo.ProvinceId == seat && limbo.BaseProvinceId == -1 && limbo.DeployDaysLeft == SupportTypeCatalog.Get("sam_brigade").DeployDays,
            "a formation parked off the map is put down at the government's seat, deploying");
        for (var day = 0; day < 3; day++) Day(c);
        Check(!SupportCore.Displaced(c, limbo) && limbo.BaseProvinceId == seat, "and after its deploy days it works from there");
        Check(c.SupportFormations.All(f => f.ProvinceId > 0), "nothing is off the map any more");
    }

    // ------------------------------------------------------------------ 11. the wire

    static void Codec(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR");
        c.SupportFormations.Clear();
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var near = Steps(c, capital, (ushort)rus.MapIndex, 3).Where(p => p.Value == 3).Select(p => p.Key).Min();
        var ours = Put(c, rus, "mlrs_battalion", capital);
        var theirs = Put(c, ukr, "artillery_brigade", CapitalCore.SupplyOriginProvinceId(c, ukr));
        var order = new RelocateSupportCommand { IssuerId = rus.Id, FormationIds = new[] { theirs.Id, ours.Id, int.MaxValue, ours.Id }, ProvinceId = near, Append = true };
        Check(NetworkCommandCodec.TryEncode(order, out var wireType, out var wire) &&
              NetworkCommandCodec.TryDecode(wireType, wire, out var decoded) && decoded is RelocateSupportCommand back &&
              back.IssuerId == order.IssuerId && back.ProvinceId == near && back.Append && back.FormationIds.SequenceEqual(order.FormationIds),
            "the relocation order goes through the network and replay codec unchanged");
        var halt = new RelocateSupportCommand { IssuerId = rus.Id, FormationIds = new[] { ours.Id }, Halt = true };
        Check(NetworkCommandCodec.TryEncode(halt, out var haltType, out var haltWire) &&
              NetworkCommandCodec.TryDecode(haltType, haltWire, out var haltBack) && haltBack is RelocateSupportCommand { Halt: true },
            "the halt order goes through the codec as a halt");
        var plain = new RelocateSupportCommand { IssuerId = rus.Id, FormationIds = order.FormationIds, ProvinceId = near };
        plain.Apply(c);
        Check(plain.Moved == 1 && plain.Refused == 2 && plain.Refusal.Contains("#" + theirs.Id) && plain.Refusal.Contains("#" + int.MaxValue) &&
              !plain.Refusal.Contains("#" + ours.Id) && ours.Route.Count > 0 && theirs.Route.Count == 0,
            "a stale or foreign id is refused by name, and ours goes regardless");
    }

    // ------------------------------------------------------------------ 12. the computer

    static void Computer(ProvinceTopology map)
    {
        var c = World(map, "ISL");
        // Exercise mission assignment and relocation together on the fixture's opening day.
        // With the default staggered cycle, the countries can have different review days.
        c.Defines.AiLongRangeRepositionDays = 1;
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR"); var blr = c.NationByTag("BLR");
        var war = EnsureWar(c, rus, ukr);
        if (blr is not null && !war.Involves(blr.Id)) war.Join(blr.Id, war.IsAttacking(rus.Id));
        c.SupportFormations.Clear();
        var capital = CapitalCore.SupplyOriginProvinceId(c, rus);
        var deep = Put(c, rus, "artillery_brigade", capital);
        SupportFormation ally = null;
        if (blr is not null) ally = Put(c, blr, "artillery_brigade", CapitalCore.SupplyOriginProvinceId(c, blr));
        var orders = LongRangeMovement.AiOrders;
        SupportCore.Tick(c);
        Check(LongRangeMovement.AiOrders > orders && deep.Route.Count > 0 && deep.BaseProvinceId == -1,
            "the computer sends a battery that reaches no battle toward the front, by the relocation order");
        Check(deep.Route.All(p => Controls(c)[p] == rus.MapIndex || MilitaryAccess.CanEnter(c, rus.Id, Controls(c)[p])),
            "its road runs over its own side's ground");
        if (ally is not null)
            Check(ally.Mission != SupportCore.MissionStandby, "a coalition ally's formations fly missions - not standby for the whole war");

        var peace = World(map, "ISL", 20261118);
        peace.Defines.AiLongRangeRepositionDays = 1;
        foreach (var w in peace.Wars) w.Ended = true;
        peace.SupportFormations.Clear();
        var calm = Put(peace, peace.NationByTag("RUS"), "artillery_brigade", CapitalCore.SupplyOriginProvinceId(peace, peace.NationByTag("RUS")));
        var before = LongRangeMovement.AiOrders;
        SupportCore.Tick(peace);
        Check(LongRangeMovement.AiOrders == before && calm.Route.Count == 0, "at peace nothing is moved");
    }

    // ------------------------------------------------------------------ 13. two worlds, one seed

    static void Determinism(ProvinceTopology map)
    {
        // Each world on its own copy of the map, as two peers' worlds are.
        var a = World(map.CloneForOverrides(), "ISL", 20261119);
        var b = World(map.CloneForOverrides(), "ISL", 20261119);
        foreach (var w in new[] { a, b })
        {
            var rus = w.NationByTag("RUS"); var ukr = w.NationByTag("UKR");
            EnsureWar(w, rus, ukr);
            w.IsPaused = false;
        }
        Check(CampaignFingerprint.Of(a) == CampaignFingerprint.Of(b), "two worlds from one seed start the same");
        var moving = 0;
        for (var day = 1; day <= 90; day++)
        {
            a.TickDay(); b.TickDay();
            moving = Math.Max(moving, a.SupportFormations.Count(f => f.Route.Count > 0));
            if (CampaignFingerprint.Of(a) != CampaignFingerprint.Of(b)) Check(false, $"two worlds from one seed part on day {day}");
        }
        Check(moving > 0, "formations moved in those ninety days");
        Check(a.SupportFormations.Select(f => (f.Id, f.ProvinceId, f.Route.Count, f.MoveProgress))
                .SequenceEqual(b.SupportFormations.Select(f => (f.Id, f.ProvinceId, f.Route.Count, f.MoveProgress))),
            "ninety days of movement give the same fingerprint and the same columns in both worlds");
    }

    // ------------------------------------------------------------------ 14. halt

    static int Exclave(Campaign c, Nation rus)
    {
        var map = c.ProvinceMap; var controls = Controls(c);
        return Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && controls[p] == rus.MapIndex &&
            map.CenterOf(p).Longitude < 23f && map.CenterOf(p).Latitude > 54f && map.CenterOf(p).Latitude < 56f).OrderBy(p => p).FirstOrDefault();
    }

    static IEnumerable<int> Ours(Campaign c, Nation nation) =>
        Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => !c.ProvinceMap.IsWater(p) && Controls(c)[p] == nation.MapIndex);

    static RelocateSupportCommand HaltOrder(Campaign c, Nation issuer, params SupportFormation[] formations)
    {
        var order = new RelocateSupportCommand { IssuerId = issuer.Id, FormationIds = formations.Select(f => f.Id).ToArray(), Halt = true };
        order.Apply(c);
        return order;
    }

    /// <summary>
    /// HALT, the button: "finishes the step it is making and deploys there". It used to send each
    /// formation to the province it last reached, and a column part-way along a step planned the
    /// way back from the end of it - a ship six days out landed, re-embarked and sailed home.
    /// </summary>
    static void Halt(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS");
        c.SupportFormations.Clear();
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);

        // At sea: it lands at the harbour ahead and deploys there.
        var exclave = Exclave(c, rus);
        var ship = Put(c, rus, "artillery_brigade", capital);
        Order(c, rus, exclave, false, ship);
        for (var day = 0; day < 80 && !(LongRangeMovement.OverWater(ship) && ship.MoveProgress > .3f); day++) Day(c);
        Check(LongRangeMovement.OverWater(ship) && (SupportMoveMode)ship.RouteModes[0] == SupportMoveMode.Sea, "halt: a brigade is at sea");
        var harbour = ship.Route[0]; var leftFrom = ship.ProvinceId; var share = ship.MoveProgress;
        var halted = HaltOrder(c, rus, ship);
        Check(halted.Moved == 1 && ship.Route.Count == 1 && ship.Route[0] == harbour && ship.MoveProgress == share,
            "halt at sea: the route becomes the harbour ahead alone, and the share sailed is kept - no way back is planned");
        var last = ship.MoveProgress; var days = 0;
        while (ship.Route.Count > 0 && days < 60)
        {
            Day(c); days++;
            Check(ship.ProvinceId == leftFrom || ship.ProvinceId == harbour, $"halt at sea, day {days}: it never turns for the port it left");
            if (ship.Route.Count > 0) { Check(ship.MoveProgress > last, $"halt at sea, day {days}: still sailing on"); last = ship.MoveProgress; }
        }
        for (var day = 0; day < 10 && SupportCore.Displaced(c, ship); day++) Day(c);
        Check(ship.ProvinceId == harbour && ship.BaseProvinceId == harbour, "halted at sea, it lands at the harbour ahead and deploys there");

        // On the road, part-way along its first step - the state the daily march leaves a column
        // in between two provinces: the province ahead, and none back.
        var target = Steps(c, capital, (ushort)rus.MapIndex, 6).Where(p => p.Value == 6).Select(p => p.Key).Min();
        var gun = Put(c, rus, "artillery_brigade", capital);
        Order(c, rus, target, false, gun);
        gun.MoveProgress = .4f;
        var ahead = gun.Route[0];
        HaltOrder(c, rus, gun);
        Check(gun.Route.Count == 1 && gun.Route[0] == ahead && gun.MoveProgress == .4f, "halt on the road: it finishes the step it is on");
        for (var day = 0; day < 20 && SupportCore.Displaced(c, gun); day++)
        {
            Day(c);
            Check(gun.ProvinceId == capital || gun.ProvinceId == ahead, "halt on the road: one province forward, none back");
        }
        Check(gun.ProvinceId == ahead && gun.BaseProvinceId == ahead, "and deploys in the province ahead");

        // In the air, part-way along a ferry leg: the airfield ahead.
        var wing = Put(c, rus, "fighter_wing", capital);
        var field = Ours(c, rus).Where(p => SupportCore.DistanceKm(c, capital, p) is > 1540f and < 1640f).Min();
        Order(c, rus, field, false, wing);
        Day(c);
        Check(LongRangeMovement.OverWater(wing) && wing.Route[0] == field, "halt: a wing is part-way along its ferry leg");
        HaltOrder(c, rus, wing);
        Check(wing.Route.Count == 1 && wing.Route[0] == field && wing.ProvinceId == capital, "halt in the air: it flies on to the airfield ahead");
        for (var day = 0; day < 6 && SupportCore.Displaced(c, wing); day++) Day(c);
        Check(wing.BaseProvinceId == field, "and works from there, not from the field it left");

        // Not yet moving: it deploys where it stands.
        var still = Put(c, rus, "artillery_brigade", capital);
        Order(c, rus, target, false, still);
        Check(still.Route.Count > 0 && still.MoveProgress == 0f, "halt: a battery has its orders and has not set off");
        HaltOrder(c, rus, still);
        Check(still.Route.Count == 0 && still.ProvinceId == capital, "halted before it set off, it stays where it stands");
    }

    // ------------------------------------------------------------------ 15. the sea is water

    static bool OnWaterOrNearEnd(ProvinceTopology map, LongRangeMovement.SeaCourse course, (float Latitude, float Longitude) point) =>
        SeaLanes.IsWaterAt(map, point.Latitude, point.Longitude) ||
        Distance(point, course.Points[0]) < 250f || Distance(point, course.Points[1]) < 250f ||
        Distance(point, course.Points[^2]) < 250f || Distance(point, course.Points[^1]) < 250f;

    /// <summary>
    /// A passage is the water a ship sails, not the straight line between two ports: the card
    /// is at sea every day of it, and the days are the course's kilometres.
    /// </summary>
    static void SeaFollowsTheWater(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS");
        c.SupportFormations.Clear();
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var exclave = Exclave(c, rus);
        var f = Put(c, rus, "artillery_brigade", capital);
        var plan = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { f.Id }, exclave).Single();
        var seaIndex = plan.Modes.IndexOf((int)SupportMoveMode.Sea);
        Check(plan.Accepted && seaIndex >= 0, "to Kaliningrad by sea");
        var departure = seaIndex == 0 ? capital : plan.Route[seaIndex - 1];
        var arrival = plan.Route[seaIndex];
        var shut = SeaLanes.ShutMask(c, rus.Id);
        var course = LongRangeMovement.CourseOf(c, departure, arrival, shut);
        Check(course is not null && course.Points.Count >= 4, "the passage has a course: quay, the water, the far quay");
        var dry = 0;
        for (var i = 0; i <= 200; i++) if (!OnWaterOrNearEnd(map, course, course.At(i / 200f))) dry++;
        Check(dry == 0, $"every point of the course is on the water, the quays apart: {dry} of 201 on land");
        Check(course.Total >= SupportCore.DistanceKm(c, departure, arrival) - 1f, "the course is never shorter than the straight line");
        Check(plan.Days >= course.Total / c.Defines.LongRangeSeaKmPerDay + 2 * c.Defines.LongRangePortDays,
            $"the days are the course's kilometres at sea plus the days in port: {plan.Days} d for {course.Total:0} km");
        var back = LongRangeMovement.CourseOf(c, arrival, departure, shut);
        Check(back is not null && MathF.Abs(back.Total - course.Total) < 1f && Distance(back.At(.3f), course.At(.7f)) < 2f,
            "the way back is the way out reversed: a ship turned about at sea sails home the way it came");

        // Sailing it: every day the card is on the water or at a quay, and no day is a jump.
        Order(c, rus, exclave, false, f);
        var last = LongRangeMovement.Position(c, f); var days = 0; var seaDays = 0;
        var trail = new List<(float Latitude, float Longitude)>();
        while (SupportCore.Displaced(c, f) && days < 160)
        {
            var mode = f.RouteModes.Count > 0 ? (SupportMoveMode)f.RouteModes[0] : SupportMoveMode.Land;
            Day(c); days++;
            var now = LongRangeMovement.Position(c, f);
            if (f.Route.Count > 0 && f.MoveProgress > 0f && (SupportMoveMode)f.RouteModes[0] == SupportMoveMode.Sea)
            {
                seaDays++;
                Check(OnWaterOrNearEnd(map, course, now), $"day {days}: at sea means on the water");
                LongRangeMovement.Trail(c, f, trail);
                Check(trail.Count >= 2 && trail[0] == now, $"day {days}: the route drawn starts where the card is");
            }
            if (mode == SupportMoveMode.Sea) Check(Distance(last, now) <= c.Defines.LongRangeSeaKmPerDay + 1f, $"day {days}: no jump at sea");
            last = now;
        }
        Check(seaDays > 0 && f.BaseProvinceId == exclave, $"it sailed {seaDays} days on the water and deployed in Kaliningrad");
    }

    /// <summary>
    /// The owner's case: Russia at war, the Bosporus shut to belligerents, a brigade in the south
    /// sent to Kaliningrad. The old passage was one straight Sea step from a Black Sea port over
    /// Ukraine and Poland in about eight days; now no Black Sea harbour may be the start of it.
    /// </summary>
    static void ShutStrait(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR");
        EnsureWar(c, rus, ukr);
        c.SupportFormations.Clear();
        var controls = Controls(c);
        var shut = SeaLanes.ShutMask(c, rus.Id);
        var bosporus = SeaPassageCatalog.All.ToList().FindIndex(p => p.Id == "bosporus");
        Check(bosporus >= 0 && (shut & (1L << bosporus)) != 0, "at war, the Bosporus is shut to Russia");
        var exclave = Exclave(c, rus);
        var coastal = SeaLanes.CoastalProvinces(map).ToHashSet();
        var south = Enumerable.Range(1, map.ProvinceCount).Where(p => coastal.Contains(p) && controls[p] == rus.MapIndex &&
            map.CenterOf(p).Latitude is > 43f and < 47.5f && map.CenterOf(p).Longitude is > 33f and < 41f &&
            SeaLanes.WaterOf(c, 0, p) >= 0 && SeaLanes.WaterOf(c, 0, p) == SeaLanes.WaterOf(c, 0, exclave) &&
            SeaLanes.WaterOf(c, shut, p) != SeaLanes.WaterOf(c, shut, exclave)).OrderBy(p => p).FirstOrDefault();
        Check(south > 0, "a Russian Black Sea or Azov harbour province exists on this map");
        Check(SeaLanes.HarbourBasin(map, south) == SeaLanes.HarbourBasin(map, exclave),
            "the Black Sea and the Baltic are one basin - which is all the old passage asked");
        Check(LongRangeMovement.CourseOf(c, south, exclave, shut) is null && LongRangeMovement.CourseOf(c, south, exclave, 0) is not null,
            "no course runs through a shut strait; with it open there is one");
        var f = Put(c, rus, "artillery_brigade", south);
        var plan = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { f.Id }, exclave).Single();
        if (plan.Accepted)
        {
            var at = south; var sailsFromBlackSea = false;
            for (var i = 0; i < plan.Route.Count; i++)
            {
                if (plan.Modes[i] == (int)SupportMoveMode.Sea)
                {
                    sailsFromBlackSea |= SeaLanes.WaterOf(c, shut, at) == SeaLanes.WaterOf(c, shut, south);
                    var leg = LongRangeMovement.CourseOf(c, at, plan.Route[i], shut);
                    Check(leg is not null && leg.Points.All(p => Distance(p, (41.1f, 29.0f)) > 120f),
                        "the passage it takes has a course, and the course keeps clear of the Bosporus");
                }
                at = plan.Route[i];
            }
            Check(!sailsFromBlackSea, "it does not put to sea on the Black Sea: the way out of it is shut");
            Check(plan.Days > 12, $"and the journey is a journey: {plan.Days} days, not the eight of a straight line");
        }
        else Check(plan.Refusal is LongRangeMovement.RefuseStrait or LongRangeMovement.RefuseNoRoute, "or it is refused: " + plan.RefusalText);

        // Ordered at peace, the strait shut before it sails: it does not sail through it.
        var p2 = World(map);
        var rus2 = p2.NationByTag("RUS"); var ukr2 = p2.NationByTag("UKR");
        foreach (var w in p2.Wars) w.Ended = true;
        p2.SupportFormations.Clear();
        var g = Put(p2, rus2, "artillery_brigade", south);
        var peaceful = LongRangeMovement.PreviewRelocate(p2, rus2.Id, new[] { g.Id }, exclave).Single();
        if (SeaLanes.ShutMask(p2, rus2.Id) == 0 && peaceful.Accepted && peaceful.Modes.Count > 0 && peaceful.Modes[0] == (int)SupportMoveMode.Sea)
        {
            Order(p2, rus2, exclave, false, g);
            EnsureWar(p2, rus2, ukr2);
            var nowShut = SeaLanes.ShutMask(p2, rus2.Id);
            Check(nowShut != 0, "war shuts the strait before the ship sails");
            Day(p2);
            var atSea = g.Route.Count > 0 && g.MoveProgress > 0f && g.RouteModes[0] == (int)SupportMoveMode.Sea;
            Check(!atSea || g.SeaCourseMask == nowShut && LongRangeMovement.CourseOf(p2, LongRangeMovement.At(g), g.Route[0], nowShut) is not null,
                "a passage not yet begun when the strait shut is not sailed through it: the way is looked for again");
        }
        else Console.WriteLine("NOTE long-range movement: at peace this map's southern port does not ship straight to Kaliningrad; the order above covers the shut strait");
    }

    // ------------------------------------------------------------------ 16. only the goal fell

    /// <summary>
    /// A wing on the first of several hops, a ship at sea toward an inland province: when only
    /// the final destination falls, the airfield or the harbour ahead is still ours, and the
    /// formation lands there and deploys - it does not fly or sail all the way back.
    /// </summary>
    static void StopAhead(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS");
        c.SupportFormations.Clear();
        var controls = Controls(c);
        var capital = rus.CapitalProvinceId > 0 ? rus.CapitalProvinceId : CapitalCore.SupplyOriginProvinceId(c, rus);
        var ours = Ours(c, rus).ToList();
        var stranger = c.Nations.First(n => n.MapIndex > 0 && n.Id != rus.Id && !MilitaryAccess.CanEnter(c, rus.Id, (ushort)n.MapIndex) &&
                                            !MilitaryAccess.Hostile(c, rus.Id, n.Id));
        var wing = Put(c, rus, "fighter_wing", capital);
        var ferry = LongRangeMovement.FerryKm(c, SupportTypeCatalog.Get(wing.TypeId));
        var first = ours.Where(p => SupportCore.DistanceKm(c, capital, p) is > 1540f and < 1640f).Min();
        var last = ours.Where(p => SupportCore.DistanceKm(c, first, p) is > 600f && SupportCore.DistanceKm(c, first, p) < MathF.Min(1400f, ferry) &&
                                   SupportCore.DistanceKm(c, capital, p) > SupportCore.DistanceKm(c, capital, first) + 300f)
            .OrderBy(p => p).FirstOrDefault();
        Check(last > 0, "a second hop exists beyond the first");
        Order(c, rus, first, false, wing);
        Order(c, rus, last, true, wing);
        Day(c);
        Check(LongRangeMovement.OverWater(wing) && wing.Route.Count == 2 && wing.ProvinceId == capital, "the wing is on its first hop");
        var held = controls[last];
        c.SetProvinceController(last, (ushort)stranger.MapIndex);
        try
        {
            Day(c);
            Check(!wing.Route.Contains(capital) &&
                  (wing.ProvinceId == first && wing.Route.Count == 0 || wing.ProvinceId == capital && wing.Route.SequenceEqual(new[] { first })),
                "the last airfield fell: the wing flies on to the first, it does not turn for home");
            for (var day = 0; day < 6 && SupportCore.Displaced(c, wing); day++) Day(c);
            Check(wing.ProvinceId == first && wing.BaseProvinceId == first, "and deploys at the airfield ahead");
        }
        finally { c.SetProvinceController(last, held); }

        // At sea toward a province inland of the harbour.
        var ship = Put(c, rus, "artillery_brigade", capital);
        var inland = -1;
        foreach (var candidate in ours.Where(p => map.CenterOf(p).Longitude < 23f && map.CenterOf(p).Latitude is > 54f and < 56f).OrderBy(p => p))
        {
            var plan = LongRangeMovement.PreviewRelocate(c, rus.Id, new[] { ship.Id }, candidate).Single();
            var sea = plan.Modes.IndexOf((int)SupportMoveMode.Sea);
            if (plan.Accepted && sea >= 0 && sea < plan.Route.Count - 1) { inland = candidate; break; }
        }
        if (inland < 0)
        {
            Console.WriteLine("NOTE long-range movement: no Kaliningrad province lies inland of its arrival harbour on this map; the sea case rests on the wing's");
            return;
        }
        Order(c, rus, inland, false, ship);
        for (var day = 0; day < 80 && !(LongRangeMovement.OverWater(ship) && ship.MoveProgress > .3f); day++) Day(c);
        Check(LongRangeMovement.OverWater(ship) && ship.Route.Count > 1, "a brigade is at sea toward an inland province");
        var harbour = ship.Route[0]; var leftFrom = ship.ProvinceId;
        var heldInland = controls[inland];
        c.SetProvinceController(inland, (ushort)stranger.MapIndex);
        try
        {
            Day(c);
            Check(!ship.Route.Contains(leftFrom) &&
                  (ship.ProvinceId == harbour && ship.Route.Count == 0 || ship.ProvinceId == leftFrom && ship.Route.SequenceEqual(new[] { harbour })),
                "the destination inland fell: the ship sails on to the harbour ahead, it does not sail home");
            for (var day = 0; day < 40 && SupportCore.Displaced(c, ship); day++) Day(c);
            Check(ship.ProvinceId == harbour && ship.BaseProvinceId == harbour, "and lands and deploys there");
        }
        finally { c.SetProvinceController(inland, heldInland); }
    }

    // ------------------------------------------------------------------ 17. a stale selection

    /// <summary>
    /// The seat changed in a hot-seat game with formations selected: the next player's
    /// right-click must not be swallowed by "Not our formation", nor move anything not theirs.
    /// </summary>
    static void StaleSelection(ProvinceTopology map)
    {
        var c = World(map);
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR");
        c.SupportFormations.Clear();
        var a = Put(c, rus, "artillery_brigade", CapitalCore.SupplyOriginProvinceId(c, rus));
        var b = Put(c, rus, "mlrs_battalion", CapitalCore.SupplyOriginProvinceId(c, rus));
        var mine = Put(c, ukr, "artillery_brigade", CapitalCore.SupplyOriginProvinceId(c, ukr));
        var selection = new HashSet<int> { a.Id, b.Id };
        LongRangeMovement.KeepCommandable(c, ukr.Id, selection);
        Check(selection.Count == 0, "the last seat's formations are not the next seat's selection");
        selection = new HashSet<int> { a.Id, mine.Id, 999999 };
        LongRangeMovement.KeepCommandable(c, ukr.Id, selection);
        Check(selection.SetEquals(new[] { mine.Id }), "a mixed or stale selection keeps only what this player may command");
        selection = new HashSet<int> { a.Id, b.Id };
        LongRangeMovement.KeepCommandable(c, rus.Id, selection);
        Check(selection.Count == 2, "and its own selection is left alone");
    }

    // ------------------------------------------------------------------ 18. the interface's wiring

    static string Source(string relative)
    {
        DirectoryInfo Find(string start)
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scripts/MainMenuScene.cs"))) directory = directory.Parent;
            return directory;
        }
        var root = Find(AppContext.BaseDirectory) ?? Find(Directory.GetCurrentDirectory()) ??
                   throw new Exception("FAIL long-range movement: game sources not found above the test output");
        return File.ReadAllText(Path.Combine(root.FullName, relative));
    }

    /// <summary>The text of one method: from its signature to the next member at the same indentation.</summary>
    static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) throw new Exception("FAIL long-range movement: not found: " + signature);
        var end = source.Length;
        foreach (var marker in new[] { "\n    private ", "\n    public ", "\n    internal ", "\n    /// <summary>", "\n    /// <param" })
        {
            var next = source.IndexOf(marker, start + signature.Length, StringComparison.Ordinal);
            if (next > 0 && next < end) end = next;
        }
        return source[start..end];
    }

    /// <summary>
    /// The selection rules live in the interface, which the tests cannot click; what they can
    /// read is that every way to pick divisions, every way the world or the seat changes, the
    /// right button and HALT go through the rules the simulation tests above pin.
    /// </summary>
    static void SelectionWiring()
    {
        var operations = Source("Scripts/ui/ArmyOperationsUI.cs");
        var support = Source("Scripts/ui/SupportScreenUI.cs");
        Check(Body(operations, "private void OnArmiesBoxPicked(").Contains("PickArmies(armies, formationsTaken: false)"),
            "a click on a stack, the army bar and select-all pick divisions without formations");
        var pick = Body(operations, "private void PickArmies(");
        Check(pick.Contains("if (!formationsTaken)") && pick.Contains("ClearSupportMapSelection()"),
            "and a plain pick of divisions lets the selected formations go, as a single click does");
        Check(Body(support, "private void OnSelectionBoxPicked(").Contains("PickArmies(divisions, formationsTaken: true)"),
            "a drawn box, which took its formations itself, does not let them go again");
        var command = Body(operations, "private void OnProvinceCommanded(");
        Check(command.Contains("KeepCommandable") && command.IndexOf("KeepCommandable", StringComparison.Ordinal) <
                  command.IndexOf("RelocateSupportSelection", StringComparison.Ordinal),
            "the right button drops formations that are not this player's before it acts on the selection");
        Check(Body(Source("Scripts/ui/LocalMultiplayerUI.cs"), "private void PassHotseat(").Contains("ClearSupportMapSelection()"),
            "passing the device lets the formation selection go");
        Check(Body(Source("Scripts/ui/SaveLoadUI.cs"), "Task RestoreCampaignFromSlot(").Contains("ClearSupportMapSelection()"),
            "loading a save lets the formation selection go");
        Check(Body(Source("Scripts/ui/NetworkMultiplayerUI.cs"), "private void AdoptNetworkCampaign(").Contains("ClearSupportMapSelection()"),
            "adopting the host's world lets the formation selection go");
        var halt = Body(support, "private void HaltSupportSelection(");
        Check(halt.Contains("Halt = true") && !halt.Contains("ProvinceId = LongRangeMovement.At("),
            "HALT sends the halt order, not an order back to the province last reached");
    }
}
