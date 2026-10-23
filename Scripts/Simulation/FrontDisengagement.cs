using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool FrontStaffDisengages { get; set; } = true;
    public int FrontDisengagementMinimumBattleDays { get; set; } = 2;
    public float FrontDisengagementMaximumShare { get; set; } = .35f;
    public float FrontDisengagementDefenceRatio { get; set; } = .8f;
}

/// <summary>Release a reserve from a pinned surplus, never the whole covering force.
/// Called only for an empty post, after free troops and reserves have been considered.</summary>
public static class FrontDisengagement
{
    public static int Orders { get; private set; }

    internal static bool Eligible(Campaign c, Nation nation, StrategicWar war, DrawnFrontLine line, Army army)
    {
        if (!c.Defines.FrontStaffDisengages || war is null || war.Ended || army.NationId != nation.Id ||
            !army.InBattle || army.DirectOrder || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 ||
            army.ReadyDay > c.ElapsedDays || army.OrderKind is not (ArmyOrder.DefendFront or ArmyOrder.AdvanceFront)) return false;
        if (line is not null)
            return line.Cohesion != PlanCohesion.Immobile && army.FrontLineId == line.Id;
        return army.FrontLineId < 0 && army.AssignedWarId == war.Id &&
            (army.OrderKind == ArmyOrder.DefendFront || !nation.IsHuman || Delegation.Holds(nation, Delegation.Command));
    }

    internal static bool TryCoverHole(Campaign c, Nation nation, StrategicWar war, DrawnFrontLine line,
        int hole, out Army moving, IReadOnlyList<Army> candidates = null)
    {
        SelectionContext context = null;
        try { return TryCoverHole(c, nation, war, line, hole, out moving, ref context, candidates); }
        finally { context?.Dispose(); }
    }

    /// <summary>The caller may reuse read-only battle/position facts within ONE arrangement.
    /// Orders can change, so fallback retreat crowding is still read live. A detachment
    /// invalidates this context BEFORE its command is applied.</summary>
    internal static bool TryCoverHole(Campaign c, Nation nation, StrategicWar war, DrawnFrontLine line,
        int hole, out Army moving, ref SelectionContext context, IReadOnlyList<Army> candidates = null)
    {
        using var measured = SimProfiler.Measure("      staff:disengage");
        moving = null;
        if (context is not null && !context.Matches(c, war, nation.Id))
        { context.Dispose(); context = null; }
        if (!c.Defines.FrontStaffDisengages || war is null || war.Ended ||
            !(context?.Safe(hole) ?? WarCoalition.SafeRetreatProvince(c, war, nation.Id, hole))) return false;
        // An assault's departure tile is already covered by its fighting troops, even
        // when advances are deliberately excluded from routine defensive redistribution.
        if (context is not null)
        {
            if (context.Occupied.Contains(hole)) return false;
        }
        else
            foreach (var unit in c.Armies)
                if (unit.NationId == nation.Id && unit.ProvinceId == hole && unit.Divisions >= .1f) return false;
        context?.BeginHole(hole);
        Army best = null;
        int[] bestRoute = null;
        var bestRetreat = -1;
        var bestShare = 1f;
        var bestScore = float.MaxValue;
        foreach (var army in candidates ?? c.Armies)
        {
            if (!Eligible(c, nation, war, line, army)) continue;
            context ??= new SelectionContext(c, war, nation.Id, hole);
            var battle = context.Battle(army.BattleId);
            if (battle is null || c.ElapsedDays - battle.StartedDay < Math.Max(0, c.Defines.FrontDisengagementMinimumBattleDays)) continue;
            var attacking = battle.Attackers.Contains(army.Id);
            if (!attacking && !battle.Defenders.Contains(army.Id)) continue;
            if (line?.Cohesion == PlanCohesion.Rigid &&
                !c.ProvinceMap.Neighbors[army.ProvinceId].AsSpan().Contains(hole)) continue;
            var take = army.Divisions >= 2f
                ? MathF.Min(army.Divisions - 1f, MathF.Max(1f, MathF.Floor(army.Divisions *
                    Math.Clamp(c.Defines.FrontDisengagementMaximumShare, .05f, .5f)))) : army.Divisions;
            var share = take / army.Divisions;
            SideTotals totals;
            using (SimProfiler.Measure("        disengage:participants"))
                totals = context.Totals(battle, attacking);
            // Do not leave the healthy anchor and call two exhausted counters a rearguard.
            if (totals.OwnDivisions - take < .99f || totals.OwnPower - army.Power * share < MathF.Max(.05f, totals.OwnPower * .5f)) continue;
            if (!attacking)
            {
                if (totals.CoveringPower - army.Power * share < totals.Pressure *
                    Math.Clamp(c.Defines.FrontDisengagementDefenceRatio, 0f, 2f)) continue;
            }
            int from;
            using (SimProfiler.Measure("        disengage:retreat"))
                from = attacking ? army.ProvinceId : context.WithdrawalProvince(army);
            if (from <= 0) continue;
            // All routing is checked BEFORE the command can detach a formation or yield ground.
            int[] route;
            using (SimProfiler.Measure("        disengage:route"))
                route = context.Route(from);
            if (from != hole && route.Length == 0) continue;
            var safe = true;
            using (SimProfiler.Measure("        disengage:safety"))
            foreach (var step in route)
                if (!context.Safe(step)) { safe = false; break; }
            if (!safe) continue;
            var score = route.Length + (attacking ? 0 : 1) + army.Power / MathF.Max(.01f, army.Divisions);
            if (score > bestScore || score == bestScore && best is not null && army.Id >= best.Id) continue;
            best = army; bestRoute = route; bestRetreat = from; bestShare = share; bestScore = score;
        }
        if (best is null) return false;
        var objective = line?.Objectives.GetValueOrDefault(best.Id, -1) ?? -1;
        var order = new WithdrawArmyFromBattleCommand
        { IssuerId = nation.Id, ArmyId = best.Id, TargetProvinceId = bestRetreat, Share = bestShare };
        context?.Dispose();
        context = null; // Splitting/withdrawing changes positions, powers and participant lists.
        order.Apply(c);
        if (!order.Withdrawn) return false;
        moving = c.Armies.Find(a => a.Id == order.WithdrawnArmyId);
        moving.OrderKind = ArmyOrder.DefendFront;
        moving.AssignedWarId = war.Id;
        moving.DirectOrder = false;
        moving.PlanActive = line?.Kind != FrontLineKind.Offensive;
        moving.FrontLineId = line?.Id ?? -1;
        moving.Route.Clear(); moving.Route.AddRange(bestRoute);
        moving.PlannedRoute.Clear(); moving.PlannedRoute.AddRange(bestRoute);
        moving.MoveProgress = 0;
        moving.OrderProvinceId = hole;
        moving.OrderStateMapIndex = c.ProvinceMap.ProvinceToState[hole];
        moving.ObjectiveStateMapIndex = -1;
        moving.OrderIssuedDay = c.ElapsedDays;
        moving.OrderRouteSteps = bestRoute.Length;
        moving.OrderStepsCompleted = 0;
        if (line is not null)
        {
            if (!line.ArmyIds.Contains(moving.Id)) line.ArmyIds.Add(moving.Id);
            line.Posts[moving.Id] = hole;
            if (objective > 0) line.Objectives[moving.Id] = objective;
        }
        Orders++;
        return true;
    }

    internal readonly record struct SideTotals(float OwnDivisions, float OwnPower, float CoveringPower, float Pressure);

    /// <summary>No static campaign cache and no saved state. Only the staff's short read-only
    /// selection window, not a promise that next hour's battlefield is still the same.</summary>
    internal sealed class SelectionContext : IDisposable
    {
        private readonly Campaign _campaign;
        private readonly StrategicWar _war;
        private readonly int _nation;
        private readonly ProvinceTopology _map;
        private readonly ushort[] _controllers;
        private readonly int _mapRevision;
        private readonly int _armyCount;
        private readonly int _battleCount;
        private readonly int _attacker;
        private readonly int _defender;
        private readonly HashSet<int> _attackerAllies;
        private readonly HashSet<int> _defenderAllies;
        private readonly Dictionary<(int Id, int Battle), Army> _units = new();
        private readonly Dictionary<int, LandBattle> _battles = new();
        private readonly Dictionary<(LandBattle Battle, bool Attacking), SideTotals> _totals = new();
        private readonly HashSet<ushort> _friendly = new();
        private readonly HashSet<ushort> _routeAllowed = new();
        private readonly HashSet<ushort> _hostile = new();
        private readonly HashSet<int> _unsafe = new();
        private readonly Dictionary<(int From, int Hole), int[]> _holeRoutes = new();
        private readonly Dictionary<(int Army, int Hole), int> _retreats = new();
        // Bounded origins; arrays belong only to this arrangement, not the campaign.
        private readonly Dictionary<int, FriendlyRouteSearch> _searches = new();
        private readonly Queue<int> _searchOrder = new();
        private FriendlyRouteComponents _components;
        private readonly Dictionary<int, int> _crowding = new();
        private readonly Dictionary<(int Army, int Province), int> _excludedCrowding = new();
        private bool _crowdingReady;
        private readonly int _ownMapIndex;
        private int _hole;
        internal readonly HashSet<int> Occupied = new();

        internal SelectionContext(Campaign c, StrategicWar war, int nation, int hole)
        {
            using var measured = SimProfiler.Measure("        disengage:index");
            _campaign = c; _war = war; _nation = nation; _map = c.ProvinceMap;
            _controllers = c.ProvinceController; _mapRevision = c.MapRevision;
            _armyCount = c.Armies.Count; _battleCount = c.Battles.Count; _hole = hole;
            _attacker = war.AttackerId; _defender = war.DefenderId;
            _attackerAllies = new(war.AttackerAllies); _defenderAllies = new(war.DefenderAllies);
            var own = c.NationById(nation);
            _ownMapIndex = own?.MapIndex ?? -1;
            if (own is not null) _friendly.Add((ushort)own.MapIndex);
            if (war is not null && !war.Ended && war.Involves(nation))
            {
                WarCoalition.FillControllers(c, war, war.IsAttacking(nation), _friendly);
                WarCoalition.FillControllers(c, war, !war.IsAttacking(nation), _hostile);
            }
            if (war.BelligerentCount == 2)
            {
                if (own is not null) _routeAllowed.Add((ushort)own.MapIndex);
            }
            else WarCoalition.FillControllers(c, war, war.IsAttacking(nation), _routeAllowed);
            var passage = MilitaryAccess.Controllers(c, nation);
            _friendly.UnionWith(passage); _routeAllowed.UnionWith(passage);
            foreach (var battle in c.Battles)
            {
                if (battle.Ended) continue;
                _unsafe.Add(battle.ProvinceId);
                if (battle.WarId == war.Id) _battles.TryAdd(battle.Id, battle);
            }
            foreach (var unit in c.Armies)
            {
                // TryAdd preserves Find's first matching entry even in a damaged archive.
                _units.TryAdd((unit.Id, unit.BattleId), unit);
                if (!(unit.Divisions >= .1f)) continue;
                if (unit.NationId == nation) Occupied.Add(unit.ProvinceId);
                if (war.Opposes(nation, unit.NationId)) _unsafe.Add(unit.ProvinceId);
            }
        }

        internal bool Matches(Campaign c, StrategicWar war, int nation) =>
            ReferenceEquals(c, _campaign) && ReferenceEquals(war, _war) && nation == _nation &&
            ReferenceEquals(c.ProvinceMap, _map) && ReferenceEquals(c.ProvinceController, _controllers) &&
            c.MapRevision == _mapRevision && c.Armies.Count == _armyCount && c.Battles.Count == _battleCount &&
            war.AttackerId == _attacker && war.DefenderId == _defender &&
            _attackerAllies.SetEquals(war.AttackerAllies) && _defenderAllies.SetEquals(war.DefenderAllies);

        internal void BeginHole(int hole)
        {
            if (_hole == hole) return;
            // Route trees survive target changes, not materialized route arrays.
            _holeRoutes.Clear(); _retreats.Clear();
            _crowdingReady = false;
            _hole = hole;
        }

        internal bool Safe(int province) => province > 0 && province <= _map.ProvinceCount &&
            !_map.IsWater(province) && _friendly.Contains(_controllers[province]) && !_unsafe.Contains(province);

        internal LandBattle Battle(int id) => _battles.GetValueOrDefault(id);

        internal SideTotals Totals(LandBattle battle, bool attacking)
        {
            var key = (battle, attacking);
            if (_totals.TryGetValue(key, out var existing)) return existing;
            float divisions = 0, power = 0, covering = 0, pressure = 0;
            // Retain the original participant order and floating-point addition order.
            foreach (var id in attacking ? battle.Attackers : battle.Defenders)
            {
                if (!_units.TryGetValue((id, battle.Id), out var unit)) continue;
                covering += unit.Power;
                if (unit.NationId == _nation) { divisions += unit.Divisions; power += unit.Power; }
            }
            if (!attacking)
                foreach (var id in battle.Attackers)
                    if (_units.TryGetValue((id, battle.Id), out var enemy)) pressure += enemy.Power;
            return _totals[key] = new(divisions, power, covering, pressure);
        }

        internal int[] Route(int from)
        {
            if (from == _hole) return Array.Empty<int>();
            // Materialize once for stacked candidates; continue the same forward BFS
            // for the next hole instead of traversing the country from scratch.
            var key = (from, _hole);
            if (_holeRoutes.TryGetValue(key, out var found)) return found;
            _components ??= new FriendlyRouteComponents(_map, _controllers, _routeAllowed);
            if (!_components.MayReach(from, _hole)) return _holeRoutes[key] = Array.Empty<int>();
            if (!_searches.TryGetValue(from, out var search))
            {
                if (_searches.Count >= 8)
                {
                    var oldest = _searchOrder.Dequeue();
                    _searches[oldest].Dispose(); _searches.Remove(oldest);
                }
                search = new FriendlyRouteSearch(_map, _controllers, from, _routeAllowed);
                _searches.Add(from, search); _searchOrder.Enqueue(from);
            }
            return _holeRoutes[key] = search.Route(_hole, out _);
        }

        public void Dispose()
        {
            foreach (var search in _searches.Values) search.Dispose();
            _components?.Dispose(); _components = null;
            _searches.Clear(); _searchOrder.Clear(); _holeRoutes.Clear(); _retreats.Clear();
        }

        internal int WithdrawalProvince(Army army)
        {
            var from = army.ProvinceId;
            if (from <= 0 || from >= _map.Neighbors.Length) return 0;
            var key = (army.Id, _hole);
            if (_retreats.TryGetValue(key, out var cached)) return cached;
            return WithdrawalProvinceUncached(army, from);
        }

        private int WithdrawalProvinceUncached(Army army, int from)
        {
            if (_hole > 0 && _hole <= _map.ProvinceCount)
            {
                if (_map.Neighbors[from].AsSpan().Contains(_hole) && Safe(_hole))
                    return _retreats[(army.Id, _hole)] = _hole;
                var route = Route(from);
                if (route.Length > 0 && Safe(route[0])) return _retreats[(army.Id, _hole)] = route[0];
            }
            // Reposting reserves can change their queued destinations between holes.
            // Keep this rare crowding-sensitive fallback live, not in the position cache.
            return SpreadRetreatProvince(army, from);
        }

        private int SpreadRetreatProvince(Army army, int from)
        {
            if (_ownMapIndex < 0) return 0;
            // Repost changes destinations, not positions. Refresh this small live-order
            // census at each hole, only if a crowding-sensitive fallback is needed.
            if (!_crowdingReady)
            {
                _crowding.Clear(); _excludedCrowding.Clear();
                foreach (var other in _campaign.Armies)
                {
                    if (other.NationId != _nation) continue;
                    AddCrowding(other.Id, other.ProvinceId);
                    if (other.Route.Count > 0 && other.OrderProvinceId != other.ProvinceId)
                        AddCrowding(other.Id, other.OrderProvinceId);
                }
                _crowdingReady = true;
            }
            var best = 0;
            var bestScore = float.MinValue;
            foreach (var p in _map.Neighbors[from])
            {
                if (!Safe(p)) continue;
                var score = _controllers[p] == _ownMapIndex ? 100f : 50f;
                var crowding = _crowding.GetValueOrDefault(p) - _excludedCrowding.GetValueOrDefault((army.Id, p));
                score -= crowding * 15f;
                var bordersEnemy = false;
                foreach (var next in _map.Neighbors[p])
                    if (!_map.IsWater(next) && _hostile.Contains(_controllers[next])) { bordersEnemy = true; break; }
                if (bordersEnemy && crowding == 0) score += 25f;
                if (score > bestScore || MathF.Abs(score - bestScore) < .001f && (best == 0 || p < best))
                { best = p; bestScore = score; }
            }
            return best;
        }

        private void AddCrowding(int army, int province)
        {
            _crowding[province] = _crowding.GetValueOrDefault(province) + 1;
            var key = (army, province);
            _excludedCrowding[key] = _excludedCrowding.GetValueOrDefault(key) + 1;
        }
    }
}
