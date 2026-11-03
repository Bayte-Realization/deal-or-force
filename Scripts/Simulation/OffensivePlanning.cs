using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A destination stroke preserves its departure front; phases regroup together and stop at the final objective.</summary>
public static class OffensivePlanning
{
    public const int MaxPhases = 24;
    private const float FullPlanning = .99f;

    public static DrawnFrontLine Source(Campaign c, IEnumerable<Army> armies)
    {
        var lines = armies.Select(a => BattlePlanning.LineFor(c, a)).ToArray();
        if (lines.Length == 0 || lines.Any(l => l is null || (l.BorderNationId < 0 && !l.AllEnemies) ||
            (l.Kind != FrontLineKind.Defence && l.Kind != FrontLineKind.Offensive))) return null;
        // Ambiguous mixed fronts need an explicit selection, not a silently chosen country.
        return lines.All(l => l.Id == lines[0].Id) ? lines[0] : null;
    }

    public static void Draw(Campaign c, DrawFrontLineCommand command, DrawnFrontLine existing)
    {
        var map = c.ProvinceMap;
        var armies = (existing?.ArmyIds ?? command.ArmyIds).Distinct().OrderBy(x => x)
            .Select(id => c.Armies.Find(a => a.Id == id)).ToArray();
        if (map is null || armies.Length == 0 || armies.Any(a => a is null || a.NationId != command.IssuerId)) return;
        if (existing is not null && existing.Kind != FrontLineKind.Offensive) return;
        var source = Source(c, armies);
        if (source is null) return;
        var own = c.NationById(command.IssuerId);
        var control = c.ProvinceController ?? map.ProvinceToNation;
        var goals = command.Provinces.Distinct().ToArray();
        var enemy = c.NationById(goals.Length > 0 ? TargetOf(c, source, goals[0]) : source.BorderNationId);
        if (enemy is null || own is null || goals.Length == 0 || goals.Length > 128 || goals.Any(p =>
            p <= 0 || p > map.ProvinceCount || map.IsWater(p) || control[p] != enemy.MapIndex)) return;
        for (var i = 1; i < goals.Length; i++)
            if (!map.Neighbors[goals[i - 1]].Contains(goals[i])) return;

        var count = existing is null ? 0 : PhaseCount(existing);
        var phaseIndex = command.AppendOffensivePhase ? count : command.EditOffensivePhase >= 0
            ? command.EditOffensivePhase : existing?.ActiveOffensivePhase ?? 0;
        if (command.AppendOffensivePhase && (existing is null || count >= MaxPhases) ||
            existing is not null && !command.AppendOffensivePhase &&
            (phaseIndex < existing.ActiveOffensivePhase || phaseIndex >= count)) return;
        IReadOnlyCollection<int> departure = phaseIndex == 0
            ? source.Kind == FrontLineKind.Defence ? source.Provinces : source.SourceProvinces
            : PhaseGoals(existing, phaseIndex - 1).ToArray();
        // From the whole front, an offensive departs from the stretch facing the country it
        // is aimed at, not from every border the country is fighting on.
        if (phaseIndex == 0 && source.AllEnemies)
        {
            var facing = BorderFronts.Candidates(c, own.Id, enemy.Id);
            facing.IntersectWith(source.Provinces);
            if (facing.Count > 0) departure = facing;
        }
        if (departure.Count == 0) return;
        var seed = departure.OrderBy(x => x).First();
        var war = c.Wars.Find(w => !w.Ended && w.Opposes(own.Id, enemy.Id));
        var corridor = war is null
            ? map.FindOperationalRoute(seed, goals[0], (ushort)own.MapIndex, (ushort)enemy.MapIndex, control)
            : WarCoalition.OperationalRoute(c, war, own.Id, seed, goals[0], control);
        if (corridor.Length == 0) return;
        var phase = new OffensivePhase();
        phase.ObjectiveSequence.AddRange(goals);

        if (existing is not null && (existing.OffensivePhases.Count > 0 || command.AppendOffensivePhase))
        {
            if (existing.OffensivePhases.Count == 0)
            {
                var legacy = new OffensivePhase();
                legacy.ObjectiveSequence.AddRange(existing.ObjectiveSequence);
                existing.OffensivePhases.Add(legacy);
            }
            if (command.AppendOffensivePhase) existing.OffensivePhases.Add(phase);
            else existing.OffensivePhases[phaseIndex] = phase;
            if (!command.AppendOffensivePhase && phaseIndex == existing.ActiveOffensivePhase)
            {
                ActivatePhase(c, existing, phaseIndex);
                foreach (var army in armies) Route(c, army, existing);
            }
            DiagnosticLogs.Write("orders", $"OFFENSIVE_PHASE line={existing.Id} phase={phaseIndex} total={PhaseCount(existing)} goal_posts={goals.Length} append={command.AppendOffensivePhase}");
            return;
        }

        var line = new DrawnFrontLine { Id = existing?.Id ?? c.NextFrontLineId++, NationId = own.Id,
            Kind = FrontLineKind.Offensive, BorderNationId = enemy.Id, WholeBorder = source.WholeBorder,
            StartLatitude = source.StartLatitude, StartLongitude = source.StartLongitude,
            EndLatitude = source.EndLatitude, EndLongitude = source.EndLongitude, Cohesion = source.Cohesion };
        if (source.AllEnemies)
        {
            // An offensive is against one country, and its departure is re-found along the
            // border with that country as the line moves: anchor it on the whole stretch
            // of that border nearest the first objective.
            var anchor = departure.OrderBy(p => Distance(map, p, goals[0])).ThenBy(p => p).First();
            var (latitude, longitude) = map.CenterOf(anchor);
            line.WholeBorder = true;
            line.StartLatitude = line.EndLatitude = latitude;
            line.StartLongitude = line.EndLongitude = longitude;
        }
        line.SourceProvinces.UnionWith(departure);
        line.BorderRevision = c.MapRevision;
        line.Provinces.UnionWith(goals);
        line.ObjectiveSequence.AddRange(goals);
        line.OffensivePhases.Add(phase);
        var used = new Dictionary<int, int>();
        foreach (var army in armies)
        {
            var goal = goals.OrderBy(p => used.GetValueOrDefault(p)).ThenBy(p => Distance(map, army.ProvinceId, p))
                .ThenBy(p => p).First();
            used[goal] = used.GetValueOrDefault(goal) + 1;
            line.Objectives[army.Id] = goal;
            line.ArmyIds.Add(army.Id);
        }
        if (existing is not null) c.FrontLines.Remove(existing);
        foreach (var army in armies)
        {
            DrawFrontLineCommand.Detach(c, army);
            army.FrontLineId = line.Id;
            army.PlanActive = false;
            army.SupportOnly = false;
        }
        c.FrontLines.Add(line);
        foreach (var army in armies)
            new SetArmyOrderCommand { IssuerId = own.Id, ArmyId = army.Id, Order = ArmyOrder.AdvanceFront }.Apply(c);
        DiagnosticLogs.Write("orders", $"OFFENSIVE_PLAN line={line.Id} owner={own.Id} target={enemy.Id} divisions={armies.Length} source_posts={departure.Count} goal_posts={goals.Length} edit={existing is not null}");
    }

    /// <summary>
    /// The country an offensive from this front would be aimed at, starting at this province:
    /// the front's own country for a border front, and for the whole front whoever holds the
    /// province, if that is somebody this country is at war with. -1 when it is nobody's.
    /// </summary>
    public static int TargetOf(Campaign c, DrawnFrontLine source, int province)
    {
        if (source is null) return -1;
        if (!source.AllEnemies || source.Kind != FrontLineKind.Defence) return source.BorderNationId;
        var map = c.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return -1;
        var control = c.ProvinceController ?? map.ProvinceToNation;
        var holder = c.Nations.FirstOrDefault(n => n.MapIndex == control[province] && n.MapIndex > 0);
        if (holder is null) return -1;
        foreach (var war in c.Wars)
            if (!war.Ended && war.Opposes(source.NationId, holder.Id)) return holder.Id;
        return -1;
    }

    public static int PhaseCount(DrawnFrontLine line) => Math.Max(1, line.OffensivePhases.Count);

    public static IReadOnlyList<int> PhaseGoals(DrawnFrontLine line, int index) =>
        line.OffensivePhases.Count > 0 && index >= 0 && index < line.OffensivePhases.Count
            ? line.OffensivePhases[index].ObjectiveSequence : line.ObjectiveSequence;

    public static bool HasNextPhase(DrawnFrontLine line) => line.ActiveOffensivePhase + 1 < PhaseCount(line);

    private static void ActivatePhase(Campaign c, DrawnFrontLine line, int index)
    {
        var goals = PhaseGoals(line, index);
        var departure = index == 0 ? line.SourceProvinces.ToArray() : PhaseGoals(line, index - 1).ToArray();
        line.ActiveOffensivePhase = index;
        line.OffensiveRegroupDay = -1;
        line.SourceProvinces.Clear(); line.SourceProvinces.UnionWith(departure);
        line.ObjectiveSequence.Clear(); line.ObjectiveSequence.AddRange(goals);
        line.Provinces.Clear(); line.Provinces.UnionWith(goals);
        line.Objectives.Clear(); line.Posts.Clear(); line.PostsDay = int.MinValue;
        var used = new Dictionary<int, int>();
        foreach (var army in line.ArmyIds.Distinct().OrderBy(id => id).Select(id => c.Armies.Find(a => a.Id == id))
            .Where(a => a is not null && a.NationId == line.NationId && a.FrontLineId == line.Id))
        {
            var goal = goals.OrderBy(p => used.GetValueOrDefault(p))
                .ThenBy(p => Distance(c.ProvinceMap, army.ProvinceId, p)).ThenBy(p => p).First();
            used[goal] = used.GetValueOrDefault(goal) + 1;
            line.Objectives[army.Id] = goal;
        }
    }

    private static Army[] Members(Campaign c, DrawnFrontLine line) => line.ArmyIds.Distinct().OrderBy(id => id)
        .Select(id => c.Armies.Find(a => a.Id == id))
        .Where(a => a is not null && a.NationId == line.NationId && a.FrontLineId == line.Id && a.Divisions >= .1f)
        .ToArray();

    private static bool FullyPrepared(Campaign c, DrawnFrontLine line, Army army) => army.PlanActive &&
        !army.InBattle && army.Planning >= FullPlanning &&
        army.ProvinceId == line.Objectives.GetValueOrDefault(army.Id, -1) &&
        AssaultReadiness.Assess(c, army).Ready;

    public static int RegroupDays(Campaign c, DrawnFrontLine line)
    {
        var participants = Members(c, line).Where(a => line.Objectives.ContainsKey(a.Id)).ToArray();
        if (participants.Length == 0) return 3;
        var preparedShare = participants.Count(a => FullyPrepared(c, line, a)) / (float)participants.Length;
        return Math.Max(3, (int)Math.Ceiling(3f / Math.Clamp(preparedShare, .25f, 1f)));
    }

    public static void Tick(Campaign c)
    {
        if (c.ProvinceMap is null || c.ProvinceController is null) return;
        foreach (var line in c.FrontLines)
        {
            if (line.Kind != FrontLineKind.Offensive || !HasNextPhase(line)) continue;
            var own = c.NationById(line.NationId);
            var members = Members(c, line);
            var participants = members.Where(a => line.Objectives.ContainsKey(a.Id)).ToArray();
            if (own is null || participants.Length == 0 || !participants.Any(a => a.PlanActive))
            {
                line.OffensiveRegroupDay = -1;
                continue;
            }
            var war = c.Wars.Find(w => !w.Ended && w.Opposes(line.NationId, line.BorderNationId));
            var ready = war is not null && participants.Any(a => FullyPrepared(c, line, a)) &&
                line.ObjectiveSequence.All(p => c.ProvinceController[p] == own.MapIndex);
            if (!ready) { line.OffensiveRegroupDay = -1; continue; }
            if (line.OffensiveRegroupDay < 0)
            {
                line.SourceProvinces.Clear();
                line.SourceProvinces.UnionWith(line.ObjectiveSequence);
                line.PostsDay = int.MinValue;
                line.OffensiveRegroupDay = c.ElapsedDays + RegroupDays(c, line);
                continue;
            }
            if (c.ElapsedDays < line.OffensiveRegroupDay) continue;
            ActivatePhase(c, line, line.ActiveOffensivePhase + 1);
            foreach (var army in members)
            {
                army.PlanActive = true;
                Route(c, army, line);
            }
            DiagnosticLogs.Write("orders", $"OFFENSIVE_PHASE_ADVANCE line={line.Id} phase={line.ActiveOffensivePhase} divisions={members.Length}");
        }
    }

    private static float Distance(ProvinceTopology map, int a, int b)
    {
        if (a <= 0) return 0;
        var x = map.CenterOf(a); var y = map.CenterOf(b);
        var dx = Math.Abs(x.Longitude - y.Longitude); dx = Math.Min(dx, 360 - dx);
        return dx * dx + (x.Latitude - y.Latitude) * (x.Latitude - y.Latitude);
    }

    public static void Route(Campaign c, Army army, DrawnFrontLine line)
    {
        var map = c.ProvinceMap; var own = c.NationById(army.NationId); var enemy = c.NationById(line.BorderNationId);
        if (map is null || own is null || enemy is null) return;
        var control = c.ProvinceController ?? map.ProvinceToNation;
        var war = c.Wars.Find(w => !w.Ended && w.Opposes(own.Id, enemy.Id));
        army.AssignedWarId = war?.Id ?? -1;
        army.Route.Clear(); army.PlannedRoute.Clear(); army.MoveProgress = 0;
        var goal = line.Objectives.GetValueOrDefault(army.Id, -1);
        if (goal <= 0) return;
        var target = goal;
        if (!army.PlanActive || war is null)
        {
            // Waiting to go, which is a line to stand on rather than a point to stand at.
            //
            // Ordering the departure provinces by their distance to the goal and taking the
            // first is the same answer for every division that shares that goal, so all of
            // them marched to one tile - and where the goals are close together, every
            // division on the plan marched to the same one. The division's own position was
            // in the ordering, but only as a tie-break between departures equally far from
            // the goal, which is a case that essentially never arises.
            target = DeparturePostFor(c, army, line);
            if (target <= 0) return;
        }
        var route = army.PlanActive && war is not null
            ? WarCoalition.OperationalRoute(c, war, army.NationId, army.ProvinceId, target, control)
            : map.FindRoute(army.ProvinceId, target, (ushort)own.MapIndex, control);
        army.OrderProvinceId = target; army.OrderStateMapIndex = map.ProvinceToState[target];
        army.OrderIssuedDay = c.ElapsedDays;
        army.PlannedRoute.AddRange(route);
        army.Route.AddRange(route.TakeWhile(p => WarCoalition.FriendlyController(c, war, army.NationId, control[p])));
        army.OrderRouteSteps = route.Length; army.OrderStepsCompleted = 0;
    }

    /// <summary>Allocate the whole departure front, not a separate front for each goal.</summary>
    public static int DeparturePostFor(Campaign c, Army army, DrawnFrontLine line)
    {
        var map = c.ProvinceMap;
        var own = c.NationById(line.NationId);
        if (map is null || own is null) return -1;
        var control = c.ProvinceController ?? map.ProvinceToNation;
        var posts = line.SourceProvinces.Where(p => p > 0 && p <= map.ProvinceCount &&
            !map.IsWater(p) && control[p] == own.MapIndex).OrderBy(p => p).ToArray();
        var members = line.ArmyIds.Distinct().OrderBy(id => id)
            .Select(id => c.Armies.Find(a => a.Id == id)).Where(a => a is not null &&
                a.NationId == line.NationId && a.FrontLineId == line.Id && a.Divisions >= .1f).ToArray();
        if (posts.Length == 0 || members.Length == 0) { line.Posts.Clear(); return -1; }

        // Follow distance along the connected border, including bends, rather than grouping
        // by target. For a sparse army choose spaced posts across the entire departure line.
        var first = posts.OrderBy(p => {
            var point = map.CenterOf(p);
            var dx = point.Longitude - line.StartLongitude;
            var dy = point.Latitude - line.StartLatitude;
            return dx * dx + dy * dy;
        }).ThenBy(p => p).First();
        var available = posts.ToHashSet();
        var steps = new Dictionary<int, int> { [first] = 0 };
        var queue = new Queue<int>(); queue.Enqueue(first);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var n in map.Neighbors[p].OrderBy(n => n))
                if (available.Contains(n) && steps.TryAdd(n, steps[p] + 1)) queue.Enqueue(n);
        }
        posts = posts.OrderBy(p => steps.GetValueOrDefault(p, int.MaxValue)).ThenBy(p => p).ToArray();
        if (members.Length < posts.Length)
            posts = Enumerable.Range(0, members.Length).Select(i => posts[members.Length == 1
                ? posts.Length / 2 : (int)Math.Round(i * (posts.Length - 1d) / (members.Length - 1))]).ToArray();

        var occupancy = posts.ToDictionary(p => p, _ => 0);
        var ceiling = (members.Length + posts.Length - 1) / posts.Length;
        var valid = line.PostsDay != int.MinValue && line.Posts.Count == members.Length;
        foreach (var member in members)
        {
            if (!line.Posts.TryGetValue(member.Id, out var post) || !occupancy.ContainsKey(post)) { valid = false; continue; }
            occupancy[post]++;
        }
        if (valid && occupancy.Values.Max() <= ceiling && occupancy.Values.Max() - occupancy.Values.Min() <= 1)
            return line.Posts.GetValueOrDefault(army.Id, -1);

        var previous = new Dictionary<int, int>(line.Posts);
        line.Posts.Clear();
        foreach (var post in posts) occupancy[post] = 0;
        // Label friendly connected components once. Trying a full pathfinder for every
        // division/post pair makes a large army's preparation unnecessarily quadratic.
        var components = new Dictionary<int, int>();
        int Component(int province)
        {
            if (province <= 0 || province > map.ProvinceCount || map.IsWater(province) ||
                control[province] != own.MapIndex) return -1;
            if (components.TryGetValue(province, out var known)) return known;
            var id = components.Count;
            var pending = new Queue<int>(); pending.Enqueue(province); components[province] = id;
            while (pending.Count > 0)
            {
                var p = pending.Dequeue();
                foreach (var next in map.Neighbors[p])
                    if (!map.IsWater(next) && control[next] == own.MapIndex && components.TryAdd(next, id))
                        pending.Enqueue(next);
            }
            return id;
        }
        var postComponents = posts.ToDictionary(p => p, Component);
        foreach (var member in members)
        {
            // Balanced occupancy first. Keep a former post or the division's current tile
            // where possible; an unchanged front must never reshuffle on camera/day changes.
            var component = Component(member.ProvinceId);
            var reachable = posts.Where(p => component >= 0 && postComponents[p] == component).ToArray();
            if (reachable.Length == 0) continue;
            var post = reachable.OrderBy(p => occupancy[p])
                .ThenBy(p => previous.GetValueOrDefault(member.Id, -1) == p ? 0 : 1)
                .ThenBy(p => p == member.ProvinceId ? 0 : 1)
                .ThenBy(p => Distance(map, member.ProvinceId, p)).ThenBy(p => p).First();
            line.Posts[member.Id] = post; occupancy[post]++;
        }
        line.PostsDay = c.ElapsedDays;
        return line.Posts.GetValueOrDefault(army.Id, -1);
    }
}
