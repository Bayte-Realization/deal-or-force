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
            var departures = line.SourceProvinces.Where(p => control[p] == own.MapIndex)
                .OrderBy(p => Distance(map, p, goal)).ThenBy(p => p).ToList();
            if (departures.Count == 0) return;

            // Where this division stands among the ones sharing its goal. By id, so it is
            // the same answer every day and two divisions never swap jumping-off points
            // between one tick and the next.
            var sharing = 0;
            var rank = 0;
            foreach (var pair in line.Objectives)
            {
                if (pair.Value != goal) continue;
                sharing++;
                if (pair.Key < army.Id) rank++;
            }

            // One province each while there are enough to go round, and from the near end
            // again when there are not: an attack forms up along the closest stretch of its
            // own front, not spread over the whole border and not stacked on one tile.
            var spread = Math.Max(1, Math.Min(departures.Count, Math.Max(1, sharing)));
            target = departures[rank % spread];
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
}
