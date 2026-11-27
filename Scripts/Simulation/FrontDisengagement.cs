using System;
using System.Buffers;
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
        if (!army.InBattle || army.NationId != nation.Id || !c.Defines.FrontStaffDisengages ||
            war is null || war.Ended || army.DirectOrder || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 ||
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
        if (context is not null && !context.EnsureFresh(c, war, nation.Id))
        { context.Dispose(); context = null; }
        if (!c.Defines.FrontStaffDisengages || war is null || war.Ended) return false;
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController;
        if (map is null || controllers is null || hole <= 0 || hole > map.ProvinceCount || map.IsWater(hole))
            return false;
        var pool = candidates ?? c.Armies;
        // Nobody who could be released: the answer is already "no", so do not pay for the
        // safety/occupancy/context work below before the loop finds no one.
        if (context is null)
        {
            if (!AnyEligible(c, nation, war, line, pool)) return false;
            if (controllers[hole] != (ushort)nation.MapIndex &&
                !WarCoalition.FriendlyController(c, war, nation.Id, controllers[hole])) return false;
            context = SelectionContext.Obtain(c, war, nation.Id, hole);
        }
        if (!context.Safe(hole) || context.Occupied.Contains(hole)) return false;
        context.BeginHole(hole);
        Army best = null;
        var bestRouteOffset = 0;
        var bestRouteLength = 0;
        var bestRetreat = -1;
        var bestShare = 1f;
        var bestScore = float.MaxValue;
        // Indexed, not foreach over IReadOnlyList: that boxed an enumerator on every call.
        // The loop does not edit the list; the command is applied only after it.
        for (var index = 0; index < pool.Count; index++)
        {
            var army = pool[index];
            if (!Eligible(c, nation, war, line, army)) continue;
            var armyBattle = context.Battle(army.BattleId);
            if (armyBattle is null || c.ElapsedDays - armyBattle.StartedDay < Math.Max(0, c.Defines.FrontDisengagementMinimumBattleDays)) continue;
            var attacking = armyBattle.Attackers.Contains(army.Id);
            if (!attacking && !armyBattle.Defenders.Contains(army.Id)) continue;
            if (line?.Cohesion == PlanCohesion.Rigid &&
                !map.Neighbors[army.ProvinceId].AsSpan().Contains(hole)) continue;
            var take = army.Divisions >= 2f
                ? MathF.Min(army.Divisions - 1f, MathF.Max(1f, MathF.Floor(army.Divisions *
                    Math.Clamp(c.Defines.FrontDisengagementMaximumShare, .05f, .5f)))) : army.Divisions;
            var share = take / army.Divisions;
            SideTotals totals;
            using (SimProfiler.Measure("        disengage:participants"))
                totals = context.Totals(armyBattle, attacking);
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
            SelectionContext.RouteInfo routeInfo;
            using (SimProfiler.Measure("        disengage:route"))
                routeInfo = context.InspectRoute(from);
            if (from != hole && routeInfo.Length == 0) continue;
            bool safe;
            using (SimProfiler.Measure("        disengage:safety"))
                safe = routeInfo.AllSafe;
            if (!safe) continue;
            var score = routeInfo.Length + (attacking ? 0 : 1) + army.Power / MathF.Max(.01f, army.Divisions);
            if (score > bestScore || score == bestScore && best is not null && army.Id >= best.Id) continue;
            best = army; bestRouteOffset = routeInfo.Offset; bestRouteLength = routeInfo.Length;
            bestRetreat = from; bestShare = share; bestScore = score;
        }
        if (best is null) return false;
        var objective = line?.Objectives.GetValueOrDefault(best.Id, -1) ?? -1;
        var lineIndex = line is null ? -1 : c.FrontLines.IndexOf(line);
        var battle = context.Battle(best.BattleId);
        var bestAttacking = battle is not null && battle.Attackers.Contains(best.Id);
        var order = new WithdrawArmyFromBattleCommand
        { IssuerId = nation.Id, ArmyId = best.Id, TargetProvinceId = bestRetreat, Share = bestShare };
        order.Apply(c);
        if (!order.Withdrawn) { context.InvalidateAfterArmyCommand(); return false; }
        var withdrawnId = order.WithdrawnArmyId;
        var armies = c.Armies;
        for (var i = 0; i < armies.Count; i++)
            if (armies[i].Id == withdrawnId) { moving = armies[i]; break; }
        if (moving is null) { context.InvalidateAfterArmyCommand(); return false; }
        context.NotifyArmyDetached(best, moving, battle, bestAttacking);
        moving.OrderKind = ArmyOrder.DefendFront;
        moving.AssignedWarId = war.Id;
        moving.DirectOrder = false;
        moving.PlanActive = line?.Kind != FrontLineKind.Offensive;
        moving.FrontLineId = line?.Id ?? -1;
        moving.Route.Clear();
        moving.PlannedRoute.Clear();
        context.CopyRouteSteps(bestRouteOffset, bestRouteLength, moving.Route, moving.PlannedRoute);
        moving.MoveProgress = 0;
        moving.OrderProvinceId = hole;
        moving.OrderStateMapIndex = map.ProvinceToState[hole];
        moving.ObjectiveStateMapIndex = -1;
        moving.OrderIssuedDay = c.ElapsedDays;
        moving.OrderRouteSteps = bestRouteLength;
        moving.OrderStepsCompleted = 0;
        if (line is not null)
        {
            // The generic whole-formation withdrawal detaches its old plan, pruning it
            // if this was its last member. The staff is moving within that SAME plan,
            // not cancelling it: restore the live roster entry before reassigning it.
            if (lineIndex >= 0 && !c.FrontLines.Contains(line))
                c.FrontLines.Insert(Math.Min(lineIndex, c.FrontLines.Count), line);
            if (!line.ArmyIds.Contains(moving.Id)) line.ArmyIds.Add(moving.Id);
            line.Posts[moving.Id] = hole;
            if (objective > 0) line.Objectives[moving.Id] = objective;
        }
        Orders++;
        return true;
    }

    private static bool AnyEligible(Campaign c, Nation nation, StrategicWar war, DrawnFrontLine line,
        IReadOnlyList<Army> pool)
    {
        for (var index = 0; index < pool.Count; index++)
        {
            var army = pool[index];
            if (army.InBattle && army.NationId == nation.Id && Eligible(c, nation, war, line, army)) return true;
        }
        return false;
    }

    internal readonly record struct SideTotals(float OwnDivisions, float OwnPower, float CoveringPower, float Pressure);

    /// <summary>No static campaign cache and no saved state. Only the staff's short read-only
    /// selection window, not a promise that next hour's battlefield is still the same.</summary>
    internal sealed class SelectionContext : IDisposable
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, SelectionContext> Pool = new();
        private bool _inUse;
        private bool _isPooled;
        private bool _needsRefresh;
        private Campaign _campaign;
        private StrategicWar _war;
        private int _nation;
        private ProvinceTopology _map;
        private ushort[] _controllers;
        private int _mapRevision;
        private int _warRevision;
        private int _allianceRevision;
        private int _armyCount;
        private int _battleCount;
        private int _attacker;
        private int _defender;
        private readonly HashSet<int> _attackerAllies = new();
        private readonly HashSet<int> _defenderAllies = new();
        private readonly HashSet<int> _enemies = new();
        private readonly Dictionary<long, Army> _units = new();
        private readonly Dictionary<int, LandBattle> _battles = new();
        private readonly Dictionary<long, SideTotals> _totals = new();
        private readonly HashSet<ushort> _friendly = new();
        private readonly HashSet<ushort> _routeAllowed = new();
        private readonly HashSet<ushort> _hostile = new();
        private readonly HashSet<ushort> _passage = new();
        private readonly HashSet<int> _unsafe = new();
        private readonly List<int> _routeSlab = new();
        private readonly Dictionary<int, RouteInfo> _holeRoutes = new();
        private readonly Dictionary<int, int> _retreats = new();
        private const int MaxPooledRouteSlots = 8;

        private sealed class RouteGraphSlot : IDisposable
        {
            public ProvinceTopology Map;
            public ushort[] Controllers;
            public int MapRevision;
            public int OwnershipRevision;
            public int RouteAllowedCount;
            public ulong RouteAllowedHash;
            public ulong ControllersHash;
            public int LastUseTick;
            public bool Valid;
            public FriendlyRouteComponents Components;
            public readonly Dictionary<int, RouteTree> Searches = new();
            public readonly Queue<int> SearchOrder = new();

            public bool Matches(ProvinceTopology map, ushort[] controllers, int mapRev, int ownershipRev,
                ulong controllersHash, int routeAllowedCount, ulong routeAllowedHash) =>
                Valid && ReferenceEquals(Map, map) && ReferenceEquals(Controllers, controllers) &&
                MapRevision == mapRev && OwnershipRevision == ownershipRev &&
                ControllersHash == controllersHash &&
                RouteAllowedCount == routeAllowedCount && RouteAllowedHash == routeAllowedHash;

            public void ClearSearches(Stack<RouteTree> spareTrees)
            {
                foreach (var search in Searches.Values)
                    if (spareTrees is not null) spareTrees.Push(search);
                    else search.Dispose();
                Searches.Clear();
                SearchOrder.Clear();
            }

            public void Dispose()
            {
                ClearSearches(null);
                Components?.Dispose();
                Components = null;
                Valid = false;
            }
        }

        private readonly List<RouteGraphSlot> _routeSlots = new();
        private RouteGraphSlot _activeSlot;
        private int _routeSlotUseSeq;
        private readonly Stack<RouteTree> _spareTrees = new();
        private readonly Dictionary<int, int> _crowding = new();
        // This country's formations in campaign order, so the crowding census walks them and
        // not the whole world. Counts do not depend on order. Kept exact: built with the
        // index, a detachment is appended as it is announced, and any other change to the
        // roster changes its count, which EnsureFresh/Matches turns into a full rebuild.
        private readonly List<Army> _own = new();
        private readonly Dictionary<long, int> _excludedCrowding = new();
        private bool _crowdingReady;
        private int _ownMapIndex;
        private int _hole;
        internal readonly HashSet<int> Occupied = new();

        internal readonly record struct RouteInfo(int Offset, int Length, int FirstStep, bool AllSafe);

        private sealed class RouteTree : IDisposable
        {
            // One rented array holding [0.._length) = previous and [_length..2*_length) = queue.
            // Keeps ArrayPool<int>.Shared pressure at 1 array per tree (<= 8 arrays total)
            // instead of 24 separate bucket entries that overflow the pool and allocate 256 KB each.
            private int[] _buffer;
            private int _length;
            private int _origin;
            private int _head;
            private int _tail;
            private bool _reachableOrigin;

            internal void Reset(ProvinceTopology map, ushort[] controllers, HashSet<ushort> allowed, int origin)
            {
                var len = map?.Neighbors?.Length ?? 0;
                if (_buffer is null || _length != len)
                {
                    ReturnBuffers();
                    _length = len;
                    if (len > 0) _buffer = ArrayPool<int>.Shared.Rent(len * 2);
                }

                _origin = origin;
                _head = 0;
                _tail = 0;
                _reachableOrigin = map is not null && controllers is not null && controllers.Length >= _length &&
                    origin > 0 && origin < _length && !map.IsWater(origin) && allowed.Contains(controllers[origin]);
                if (!_reachableOrigin) return;

                Array.Clear(_buffer, 0, _length);
                _buffer[origin] = origin;
                _buffer[_length + _tail++] = origin;
            }

            internal bool TryFind(ProvinceTopology map, ushort[] controllers, HashSet<ushort> allowed, int target,
                List<int> slab, out int offset, out int length)
            {
                offset = 0;
                length = 0;
                if (!_reachableOrigin || target <= 0 || target >= _length || map.IsWater(target) ||
                    !allowed.Contains(controllers[target]) || target == _origin)
                    return false;

                var buffer = _buffer;
                var len = _length;
                if (buffer[target] == 0 && _head < _tail)
                {
                    using var measured = SimProfiler.Measure("          disengage:bfs");
                    var neighbors = map.Neighbors;
                    while (_head < _tail && buffer[target] == 0)
                    {
                        var current = buffer[len + _head++];
                        var adj = neighbors[current];
                        for (var i = 0; i < adj.Length; i++)
                        {
                            var next = adj[i];
                            if (next <= 0 || next >= len || buffer[next] != 0 ||
                                map.IsWater(next) || !allowed.Contains(controllers[next])) continue;
                            buffer[next] = current;
                            buffer[len + _tail++] = next;
                        }
                    }
                }
                if (buffer[target] == 0) return false;

                var count = 0;
                for (var cursor = target; cursor != _origin; cursor = buffer[cursor]) count++;
                offset = slab.Count;
                length = count;
                for (var i = 0; i < count; i++) slab.Add(0);
                var write = offset + count;
                for (var cursor = target; cursor != _origin; cursor = buffer[cursor])
                    slab[--write] = cursor;
                return true;
            }

            private void ReturnBuffers()
            {
                var buffer = _buffer;
                _buffer = null;
                _length = 0;
                if (buffer is not null) ArrayPool<int>.Shared.Return(buffer);
            }

            public void Dispose() => ReturnBuffers();
        }

        private static long PairKey(int first, int second) => ((long)first << 32) | (uint)second;

        private static long TotalsKey(int battleId, bool attacking) => ((long)battleId << 1) | (attacking ? 1L : 0L);

        internal static SelectionContext Obtain(Campaign c, StrategicWar war, int nation, int hole)
        {
            var pooled = Pool.GetValue(c, static _ => new SelectionContext { _isPooled = true });
            if (!pooled._inUse)
            {
                pooled.Initialize(c, war, nation, hole);
                return pooled;
            }
            var extra = new SelectionContext();
            extra.Initialize(c, war, nation, hole);
            return extra;
        }

        private SelectionContext() { }

        internal SelectionContext(Campaign c, StrategicWar war, int nation, int hole) =>
            Initialize(c, war, nation, hole);

        internal void InvalidateAfterArmyCommand() => _needsRefresh = true;

        internal void NotifyArmyDetached(Army best, Army moving, LandBattle battle, bool attacking)
        {
            if (_campaign is null) { _needsRefresh = true; return; }
            _armyCount = _campaign.Armies.Count;
            if (battle is not null)
            {
                _totals.Remove(TotalsKey(battle.Id, attacking));
                _totals.Remove(TotalsKey(battle.Id, !attacking));
                if (moving is not null) _units.Remove(PairKey(moving.Id, battle.Id));
            }
            if (moving is not null && moving.Divisions >= 0.1f && moving.NationId == _nation)
                Occupied.Add(moving.ProvinceId);
            if (moving is not null && moving.NationId == _nation && !_own.Contains(moving)) _own.Add(moving);
            _crowdingReady = false;
        }

        internal bool EnsureFresh(Campaign c, StrategicWar war, int nation)
        {
            if (!_needsRefresh) return Matches(c, war, nation);
            _needsRefresh = false;
            if (!ReferenceEquals(c, _campaign) || !ReferenceEquals(war, _war) || nation != _nation)
                return false;
            Initialize(c, war, nation, _hole);
            return true;
        }

        private void Initialize(Campaign c, StrategicWar war, int nation, int hole)
        {
            using var measured = SimProfiler.Measure("        disengage:index");
            _inUse = true;
            _needsRefresh = false;
            _campaign = c; _war = war; _nation = nation; _map = c.ProvinceMap;
            _controllers = c.ProvinceController; _mapRevision = c.MapRevision;
            _warRevision = c.WarRevision; _allianceRevision = c.AllianceRevision;
            _armyCount = c.Armies.Count; _battleCount = c.Battles.Count; _hole = hole;
            _attacker = war.AttackerId; _defender = war.DefenderId;
            _attackerAllies.Clear();
            foreach (var id in war.AttackerAllies) _attackerAllies.Add(id);
            _defenderAllies.Clear();
            foreach (var id in war.DefenderAllies) _defenderAllies.Add(id);
            _enemies.Clear();
            _units.Clear(); _battles.Clear(); _totals.Clear();
            _friendly.Clear(); _routeAllowed.Clear(); _hostile.Clear(); _passage.Clear(); _unsafe.Clear();
            _routeSlab.Clear(); _holeRoutes.Clear(); _retreats.Clear();
            _crowding.Clear(); _excludedCrowding.Clear(); _crowdingReady = false;
            Occupied.Clear(); _own.Clear();
            var own = c.NationById(nation);
            _ownMapIndex = own?.MapIndex ?? -1;
            if (own is not null) _friendly.Add((ushort)own.MapIndex);
            var singleEnemy = -1;
            if (war is not null && !war.Ended && war.Involves(nation))
            {
                var attacking = war.IsAttacking(nation);
                WarCoalition.FillControllers(c, war, attacking, _friendly);
                WarCoalition.FillControllers(c, war, !attacking, _hostile);
                if (war.BelligerentCount == 2 && _attacker != _defender)
                    singleEnemy = attacking ? _defender : _attacker;
                else
                    foreach (var enemyId in war.Side(!attacking)) _enemies.Add(enemyId);
            }
            if (war.BelligerentCount == 2)
            {
                if (own is not null) _routeAllowed.Add((ushort)own.MapIndex);
            }
            else WarCoalition.FillControllers(c, war, war.IsAttacking(nation), _routeAllowed);
            MilitaryAccess.FillControllers(c, nation, _passage);
            _friendly.UnionWith(_passage); _routeAllowed.UnionWith(_passage);

            var ownershipRev = c.OwnershipRevision;
            var routeAllowedHash = HashUShortSet(_routeAllowed);
            var controllersHash = HashControllersSmall(_controllers);
            _activeSlot = ObtainRouteSlot(ownershipRev, controllersHash, _routeAllowed.Count, routeAllowedHash);
            var battles = c.Battles;
            for (var bi = 0; bi < battles.Count; bi++)
            {
                var battle = battles[bi];
                if (battle.Ended) continue;
                _unsafe.Add(battle.ProvinceId);
                if (battle.WarId == war.Id) _battles.TryAdd(battle.Id, battle);
            }
            var armies = c.Armies;
            for (var ai = 0; ai < armies.Count; ai++)
            {
                var unit = armies[ai];
                // TryAdd preserves Find's first matching entry even in a damaged archive.
                // Totals only asks about the live battles of THIS war. Indexing every
                // idle or unrelated division rebuilt a world-sized dictionary for each
                // staff selection. Keep the full scan for occupancy/safety below, but
                // retain participant references only where they can actually be read.
                if (unit.BattleId >= 0 && _battles.ContainsKey(unit.BattleId))
                    _units.TryAdd(PairKey(unit.Id, unit.BattleId), unit);
                if (unit.NationId == nation) _own.Add(unit);
                if (!(unit.Divisions >= .1f)) continue;
                if (unit.NationId == nation) Occupied.Add(unit.ProvinceId);
                if (singleEnemy >= 0 ? unit.NationId == singleEnemy :
                    (_enemies.Count > 0 && _enemies.Contains(unit.NationId)))
                    _unsafe.Add(unit.ProvinceId);
            }
        }

        internal bool Matches(Campaign c, StrategicWar war, int nation) =>
            ReferenceEquals(c, _campaign) && ReferenceEquals(war, _war) && nation == _nation &&
            ReferenceEquals(c.ProvinceMap, _map) && ReferenceEquals(c.ProvinceController, _controllers) &&
            c.MapRevision == _mapRevision && c.WarRevision == _warRevision && c.AllianceRevision == _allianceRevision &&
            c.Armies.Count == _armyCount && c.Battles.Count == _battleCount &&
            war.AttackerId == _attacker && war.DefenderId == _defender &&
            SameMembers(_attackerAllies, war.AttackerAllies) && SameMembers(_defenderAllies, war.DefenderAllies);

        /// <summary>HashSet.SetEquals for two default-comparer sets, without boxing an
        /// enumerator on every staff look: same count, and every live member already known.</summary>
        private static bool SameMembers(HashSet<int> known, HashSet<int> live)
        {
            if (known.Count != live.Count) return false;
            foreach (var id in live)
                if (!known.Contains(id)) return false;
            return true;
        }

        internal void BeginHole(int hole)
        {
            if (_hole == hole) return;
            // Route trees survive target changes, not materialized route slices.
            _routeSlab.Clear(); _holeRoutes.Clear(); _retreats.Clear();
            _crowdingReady = false;
            _hole = hole;
        }

        internal bool Safe(int province) => province > 0 && province <= _map.ProvinceCount &&
            !_map.IsWater(province) && _friendly.Contains(_controllers[province]) && !_unsafe.Contains(province);

        internal LandBattle Battle(int id) => _battles.GetValueOrDefault(id);

        internal SideTotals Totals(LandBattle battle, bool attacking)
        {
            var key = TotalsKey(battle.Id, attacking);
            if (_totals.TryGetValue(key, out var existing)) return existing;
            float divisions = 0, power = 0, covering = 0, pressure = 0;
            // Retain the original participant order and floating-point addition order.
            var participants = attacking ? battle.Attackers : battle.Defenders;
            for (var i = 0; i < participants.Count; i++)
            {
                if (!_units.TryGetValue(PairKey(participants[i], battle.Id), out var unit)) continue;
                covering += unit.Power;
                if (unit.NationId == _nation) { divisions += unit.Divisions; power += unit.Power; }
            }
            if (!attacking)
            {
                var attackers = battle.Attackers;
                for (var i = 0; i < attackers.Count; i++)
                    if (_units.TryGetValue(PairKey(attackers[i], battle.Id), out var enemy)) pressure += enemy.Power;
            }
            return _totals[key] = new(divisions, power, covering, pressure);
        }

        private RouteGraphSlot ObtainRouteSlot(int ownershipRev, ulong controllersHash, int routeAllowedCount, ulong routeAllowedHash)
        {
            for (var i = 0; i < _routeSlots.Count; i++)
            {
                var slot = _routeSlots[i];
                if (slot.Matches(_map, _controllers, _mapRevision, ownershipRev, controllersHash, routeAllowedCount, routeAllowedHash))
                {
                    slot.LastUseTick = ++_routeSlotUseSeq;
                    return slot;
                }
            }

            RouteGraphSlot chosen;
            var limit = _isPooled ? MaxPooledRouteSlots : 1;
            if (_routeSlots.Count < limit)
            {
                chosen = new RouteGraphSlot();
                _routeSlots.Add(chosen);
            }
            else
            {
                chosen = _routeSlots[0];
                for (var i = 1; i < _routeSlots.Count; i++)
                    if (_routeSlots[i].LastUseTick < chosen.LastUseTick) chosen = _routeSlots[i];
                chosen.ClearSearches(_isPooled ? _spareTrees : null);
            }

            chosen.Map = _map;
            chosen.Controllers = _controllers;
            chosen.MapRevision = _mapRevision;
            chosen.OwnershipRevision = ownershipRev;
            chosen.ControllersHash = controllersHash;
            chosen.RouteAllowedCount = routeAllowedCount;
            chosen.RouteAllowedHash = routeAllowedHash;
            chosen.LastUseTick = ++_routeSlotUseSeq;
            chosen.Valid = true;
            chosen.Components?.Dispose();
            chosen.Components = null;
            return chosen;
        }

        internal RouteInfo InspectRoute(int from)
        {
            if (from == _hole) return new(0, 0, 0, true);
            // Materialize once for stacked candidates; continue the same forward BFS
            // for the next hole instead of traversing the country from scratch.
            if (_holeRoutes.TryGetValue(from, out var found)) return found;
            var slot = _activeSlot;
            if (slot.Components is null)
                slot.Components = new FriendlyRouteComponents(_map, _controllers, _routeAllowed);
            if (!slot.Components.MayReach(from, _hole)) return _holeRoutes[from] = new(0, 0, 0, true);
            var searches = slot.Searches;
            var searchOrder = slot.SearchOrder;
            if (!searches.TryGetValue(from, out var tree))
            {
                if (searches.Count >= 8)
                {
                    var oldest = searchOrder.Dequeue();
                    searches.Remove(oldest, out tree);
                }
                else
                    tree = _spareTrees.Count > 0 ? _spareTrees.Pop() : new RouteTree();
                tree.Reset(_map, _controllers, _routeAllowed, from);
                searches.Add(from, tree); searchOrder.Enqueue(from);
            }
            if (!tree.TryFind(_map, _controllers, _routeAllowed, _hole, _routeSlab, out var offset, out var length) ||
                length == 0)
                return _holeRoutes[from] = new(0, 0, 0, true);
            var firstStep = _routeSlab[offset];
            var allSafe = true;
            for (var i = 0; i < length; i++)
                if (!Safe(_routeSlab[offset + i])) { allSafe = false; break; }
            return _holeRoutes[from] = new(offset, length, firstStep, allSafe);
        }

        internal int[] Route(int from)
        {
            var info = InspectRoute(from);
            if (info.Length == 0) return Array.Empty<int>();
            var arr = new int[info.Length];
            _routeSlab.CopyTo(info.Offset, arr, 0, info.Length);
            return arr;
        }

        internal void CopyRouteSteps(int offset, int length, List<int> route, List<int> plannedRoute)
        {
            for (var i = 0; i < length; i++)
            {
                var step = _routeSlab[offset + i];
                route.Add(step);
                plannedRoute.Add(step);
            }
        }

        private static ulong HashUShortSet(HashSet<ushort> values)
        {
            var xor = 0UL;
            var sum = (ulong)values.Count * 0x9E3779B97F4A7C15UL;
            foreach (var v in values)
            {
                var mixed = ((ulong)v + 0x9E3779B97F4A7C15UL) * 0xBF58476D1CE4E5B9UL;
                mixed ^= mixed >> 27;
                xor ^= mixed;
                sum += mixed;
            }
            return xor ^ (sum * 0x94D049BB133111EBUL);
        }

        private static ulong HashControllersSmall(ushort[] controllers)
        {
            if (controllers is null || controllers.Length > 512) return 0UL;
            var h = 14695981039346656037UL ^ (ulong)controllers.Length;
            for (var i = 0; i < controllers.Length; i++)
            {
                h ^= controllers[i];
                h *= 1099511628211UL;
            }
            return h;
        }

        public void Dispose()
        {
            if (_isPooled)
                _activeSlot?.ClearSearches(_spareTrees);
            _activeSlot = null;
            if (!_isPooled)
            {
                for (var i = 0; i < _routeSlots.Count; i++) _routeSlots[i].Dispose();
                _routeSlots.Clear();
                while (_spareTrees.Count > 0) _spareTrees.Pop().Dispose();
            }
            _routeSlab.Clear(); _holeRoutes.Clear(); _retreats.Clear();
            _units.Clear(); _battles.Clear(); _totals.Clear();
            _crowding.Clear(); _excludedCrowding.Clear(); Occupied.Clear(); _own.Clear();
            _friendly.Clear(); _routeAllowed.Clear(); _hostile.Clear(); _passage.Clear(); _unsafe.Clear();
            _attackerAllies.Clear(); _defenderAllies.Clear(); _enemies.Clear();
            _campaign = null; _war = null; _map = null; _controllers = null;
            _needsRefresh = false;
            _inUse = false;
        }

        internal int WithdrawalProvince(Army army)
        {
            var from = army.ProvinceId;
            if (from <= 0 || from >= _map.Neighbors.Length) return 0;
            if (_retreats.TryGetValue(army.Id, out var cached)) return cached;
            return WithdrawalProvinceUncached(army, from);
        }

        private int WithdrawalProvinceUncached(Army army, int from)
        {
            if (_hole > 0 && _hole <= _map.ProvinceCount)
            {
                if (_map.Neighbors[from].AsSpan().Contains(_hole) && Safe(_hole))
                    return _retreats[army.Id] = _hole;
                var info = InspectRoute(from);
                if (info.Length > 0 && Safe(info.FirstStep)) return _retreats[army.Id] = info.FirstStep;
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
                for (var index = 0; index < _own.Count; index++)
                {
                    var other = _own[index];
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
                var crowding = _crowding.GetValueOrDefault(p) - _excludedCrowding.GetValueOrDefault(PairKey(army.Id, p));
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
            var key = PairKey(army, province);
            _excludedCrowding[key] = _excludedCrowding.GetValueOrDefault(key) + 1;
        }
    }
}
