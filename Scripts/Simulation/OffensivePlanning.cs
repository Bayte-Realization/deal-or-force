using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A destination stroke never replaces its departure front or becomes an endless advance.</summary>
public static class OffensivePlanning
{
    public static DrawnFrontLine Source(Campaign c, IEnumerable<Army> armies)
    {
        var lines = armies.Select(a => BattlePlanning.LineFor(c, a)).ToArray();
        if (lines.Length == 0 || lines.Any(l => l is null || l.BorderNationId < 0 ||
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
        var enemy = c.NationById(source.BorderNationId);
        var control = c.ProvinceController ?? map.ProvinceToNation;
        var goals = command.Provinces.Distinct().ToArray();
        if (enemy is null || goals.Length == 0 || goals.Length > 128 || goals.Any(p =>
            p <= 0 || p > map.ProvinceCount || map.IsWater(p) || control[p] != enemy.MapIndex)) return;
        for (var i = 1; i < goals.Length; i++)
            if (!map.Neighbors[goals[i - 1]].Contains(goals[i])) return;
        var departure = source.Kind == FrontLineKind.Defence ? source.Provinces : source.SourceProvinces;
        if (departure.Count == 0) return;
        // Reject unreachable islands/neutral corridors without changing existing orders.
        var seed = departure.OrderBy(x => x).First();
        if (map.FindOperationalRoute(seed, goals[0], (ushort)own.MapIndex, (ushort)enemy.MapIndex, control).Length == 0) return;
        var line = new DrawnFrontLine { Id = existing?.Id ?? c.NextFrontLineId++, NationId = own.Id,
            Kind = FrontLineKind.Offensive, BorderNationId = enemy.Id, WholeBorder = source.WholeBorder,
            StartLatitude = source.StartLatitude, StartLongitude = source.StartLongitude,
            EndLatitude = source.EndLatitude, EndLongitude = source.EndLongitude, Cohesion = source.Cohesion };
        line.SourceProvinces.UnionWith(departure);
        line.BorderRevision = c.MapRevision;
        line.Provinces.UnionWith(goals);
        line.ObjectiveSequence.AddRange(goals);
        // Stable near-first lanes with balanced occupancy; no daily reshuffle of objectives.
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
        var war = c.Wars.Find(w => !w.Ended && w.Involves(own.Id) && w.Involves(enemy.Id));
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
            ? map.FindOperationalRoute(army.ProvinceId, target, (ushort)own.MapIndex, (ushort)enemy.MapIndex, control)
            : map.FindRoute(army.ProvinceId, target, (ushort)own.MapIndex, control);
        army.OrderProvinceId = target; army.OrderStateMapIndex = map.ProvinceToState[target];
        army.OrderIssuedDay = c.ElapsedDays;
        army.PlannedRoute.AddRange(route);
        army.Route.AddRange(route.TakeWhile(p => control[p] == own.MapIndex));
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
