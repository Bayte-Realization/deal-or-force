using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A fixed, connected defensive position inside controlled land, not an offensive or a border front.</summary>
public static class DefensivePlanning
{
    public const int MaximumPosts = 256;

    public static bool IsPost(Campaign c, int nationId, int p)
    {
        var map = c?.ProvinceMap;
        var nation = c?.NationById(nationId);
        return map is not null && nation is not null && p > 0 && p <= map.ProvinceCount &&
               !map.IsWater(p) && (c.ProvinceController ?? map.ProvinceToNation)[p] == nation.MapIndex;
    }

    // Mouse samples may skip a few provinces. Join through land adjacency, undo on backtracking,
    // and refuse long jumps (including a shortcut across the sea) without damaging the last valid stroke.
    public static bool Extend(Campaign c, int nationId, List<int> stroke, int p)
    {
        if (!IsPost(c, nationId, p)) return false;
        var previous = stroke.IndexOf(p);
        if (previous >= 0) { stroke.RemoveRange(previous + 1, stroke.Count - previous - 1); return true; }
        if (stroke.Count == 0) { stroke.Add(p); return true; }
        var nation = c.NationById(nationId);
        var segment = c.ProvinceMap.FindRoute(stroke[^1], p, (ushort)nation.MapIndex,
            c.ProvinceController ?? c.ProvinceMap.ProvinceToNation);
        if (segment.Length == 0 || segment.Length > 16 || stroke.Count + segment.Length > MaximumPosts) return false;
        foreach (var next in segment)
        {
            previous = stroke.IndexOf(next);
            if (previous >= 0) stroke.RemoveRange(previous + 1, stroke.Count - previous - 1);
            else stroke.Add(next);
        }
        return true;
    }

    public static bool Valid(Campaign c, int nationId, IReadOnlyList<int> stroke)
    {
        if (stroke.Count == 0 || stroke.Count > MaximumPosts || stroke.Distinct().Count() != stroke.Count) return false;
        for (var i = 0; i < stroke.Count; i++)
            if (!IsPost(c, nationId, stroke[i]) || (i > 0 && !c.ProvinceMap.Neighbors[stroke[i - 1]].Contains(stroke[i]))) return false;
        return true;
    }

    public static List<int> Sequence(Campaign c, DrawnFrontLine line)
    {
        // Old saves used an unordered brush; retain their posts without inventing new territory.
        IEnumerable<int> sequence = line.DefensiveSequence.Count > 0 ? line.DefensiveSequence : line.Provinces.OrderBy(p => p);
        var posts = sequence
            .Where(p => IsPost(c, line.NationId, p)).ToList();
        Assign(c, line, posts);
        return posts;
    }

    private static void Assign(Campaign c, DrawnFrontLine line, List<int> posts)
    {
        if (line.PostsDay == c.ElapsedDays && line.BorderRevision == c.MapRevision &&
            line.Posts.Count == line.ArmyIds.Count && line.Posts.Values.All(posts.Contains)) return;
        line.PostsDay = c.ElapsedDays; line.BorderRevision = c.MapRevision;
        var armies = line.ArmyIds.Select(id => c.Armies.Find(a => a.Id == id && a.FrontLineId == line.Id))
            .Where(a => a is not null && a.NationId == line.NationId).OrderBy(a => a.Id).ToArray();
        var old = new Dictionary<int, int>(line.Posts); line.Posts.Clear();
        if (posts.Count == 0) return;
        // Evenly spaced sectors if outnumbered; floor/ceiling quota if there are more units than posts.
        var capacity = new Dictionary<int, int>();
        for (var i = 0; i < armies.Length; i++)
        {
            var p = posts[Math.Min(posts.Count - 1, (int)((i + .5) * posts.Count / armies.Length))];
            capacity[p] = capacity.GetValueOrDefault(p) + 1;
        }
        // Keep a valid previous lane, including after loading (orders are saved, the cache is not).
        foreach (var a in armies)
        {
            var p = old.GetValueOrDefault(a.Id, a.OrderProvinceId);
            if (capacity.GetValueOrDefault(p) <= 0) continue;
            line.Posts[a.Id] = p; capacity[p]--;
        }
        foreach (var a in armies)
        {
            if (line.Posts.ContainsKey(a.Id)) continue;
            var p = capacity.Where(x => x.Value > 0).OrderBy(x => Distance(c.ProvinceMap, a, x.Key))
                .ThenBy(x => x.Key).First().Key;
            line.Posts[a.Id] = p; capacity[p]--;
        }
    }

    private static float Distance(ProvinceTopology map, Army a, int p)
    {
        var target = map.CenterOf(p);
        return NavalOperations.Distance(a.Latitude, a.Longitude, target.Latitude, target.Longitude);
    }

    public static void Route(Campaign c, Army a, DrawnFrontLine line)
    {
        Sequence(c, line);
        a.AssignedWarId = c.Wars.Find(w => !w.Ended && w.Involves(a.NationId))?.Id ?? -1;
        var post = line.Posts.GetValueOrDefault(a.Id, -1);
        a.Route.Clear(); a.PlannedRoute.Clear(); a.MoveProgress = 0;
        a.OrderProvinceId = post;
        a.OrderStateMapIndex = post > 0 ? c.ProvinceMap.ProvinceToState[post] : -1;
        if (post > 0 && post != a.ProvinceId)
        {
            var own = c.NationById(a.NationId);
            a.Route.AddRange(c.ProvinceMap.FindRoute(a.ProvinceId, post, (ushort)own.MapIndex,
                c.ProvinceController ?? c.ProvinceMap.ProvinceToNation));
        }
        a.PlannedRoute.AddRange(a.Route);
        a.OrderRouteSteps = a.Route.Count; a.OrderStepsCompleted = 0;
        a.OrderIssuedDay = c.ElapsedDays;
    }

    public static void Maintain(Campaign c, DrawnFrontLine line)
    {
        Sequence(c, line);
        foreach (var id in line.ArmyIds)
        {
            var a = c.Armies.Find(x => x.Id == id);
            if (a is null || a.InBattle || a.DirectOrder || a.EmbarkedFleetId >= 0 || a.OrderKind != ArmyOrder.DefendFront) continue;
            var wanted = line.Posts.GetValueOrDefault(id, -1);
            if (a.OrderProvinceId != wanted || (wanted > 0 && a.ProvinceId != wanted && a.Route.Count == 0)) Route(c, a, line);
        }
    }

    public static bool CanAssign(Campaign c, DrawFrontLineCommand command, DrawnFrontLine existing)
    {
        if (!Valid(c, command.IssuerId, command.Provinces)) return false;
        var own = c.NationById(command.IssuerId);
        foreach (var id in existing?.ArmyIds ?? command.ArmyIds)
        {
            var a = c.Armies.Find(x => x.Id == id);
            if (a is null || a.NationId != own.Id || a.EmbarkedFleetId >= 0 || a.InBattle ||
                !IsPost(c, own.Id, a.ProvinceId)) return false;
            if (a.ProvinceId != command.Provinces[0] && c.ProvinceMap.FindRoute(a.ProvinceId, command.Provinces[0],
                    (ushort)own.MapIndex, c.ProvinceController ?? c.ProvinceMap.ProvinceToNation).Length == 0) return false;
        }
        return true;
    }
}
