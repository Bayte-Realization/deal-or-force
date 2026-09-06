using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>What a line drawn on the map tells the formations assigned to it to do.</summary>
public enum FrontLineKind
{
    /// <summary>Hold this line. Spread along it, dig in, and let the enemy come.</summary>
    Defence,

    /// <summary>Take this line. It is drawn across ground the enemy holds.</summary>
    Offensive,

    /// <summary>Stand here instead if the line in front gives way.</summary>
    Fallback,

    /// <summary>
    /// Guard these places. Not a line at all: a set of provinces worth holding, with the
    /// formations spread over the ones that matter rather than along an edge.
    ///
    /// It is what the rear is actually defended with - ports, cities, a coast somebody
    /// might land on - and it is a different question from "where does the front run",
    /// which is why walking these provinces end to end would be meaningless.
    /// </summary>
    AreaDefence
}

/// <summary>
/// How far the planner may drag a formation along its own line to improve the spread.
///
/// The freedom is real and so is its cost. Left free, the planner shuffles the whole line
/// every time the situation moves, and a front of divisions permanently walking sideways
/// defends nothing; pinned, it holds its dispositions and cannot close a hole that opens
/// two provinces away. The player is the one who knows which mistake they would rather make.
/// </summary>
public enum PlanCohesion
{
    /// <summary>Send whoever is best, from anywhere on the line.</summary>
    Flexible,

    /// <summary>Prefer the neighbours. The default, and right nearly always.</summary>
    Balanced,

    /// <summary>Only the next post along, and only when it is empty.</summary>
    Rigid,

    /// <summary>Nobody moves. Where they are put is where they stay.</summary>
    Immobile
}

/// <summary>
/// A line the player drew.
///
/// The front the war computes for itself is whatever provinces happen to touch the enemy,
/// which is a description of the situation rather than an intention. This is the
/// intention: a set of provinces a player brushed onto the map and a set of formations
/// told to make it true. Where a drawn line exists it replaces the computed one for the
/// formations assigned to it, and everything downstream - deployment, routing, the front
/// overlay - reads the same set.
///
/// The line is a set of provinces rather than a path because that is what a brush
/// produces and what the simulation can use: order along the line is recovered by walking
/// it, so a player can paint in any direction, in several strokes, and correct a stroke
/// by painting over it with the right mouse button.
/// </summary>
public sealed class DrawnFrontLine
{
    public int Id;
    public int NationId;
    public FrontLineKind Kind;

    /// <summary>How freely the planner may re-sort the formations along this line.</summary>
    public PlanCohesion Cohesion = PlanCohesion.Balanced;

    public readonly HashSet<int> Provinces = new();
    public readonly List<int> ArmyIds = new();

    public bool IsEmpty => Provinces.Count == 0 || ArmyIds.Count == 0;
}

public static class BattlePlanning
{
    /// <summary>The line a formation was drawn onto, if it is still worth anything.</summary>
    public static DrawnFrontLine LineFor(Campaign campaign, Army army)
    {
        if (campaign is null || army is null || army.FrontLineId < 0) return null;
        var line = campaign.FrontLines.Find(candidate => candidate.Id == army.FrontLineId);
        return line is null || line.Provinces.Count == 0 ? null : line;
    }

    /// <summary>
    /// Where a formation is supposed to stand: the line its player drew if there is one,
    /// and the war's own front if there is not. Every consumer of a front goes through
    /// here, so drawing a line moves deployment, routing and reassignment together.
    /// </summary>
    public static IReadOnlySet<int> Posts(Campaign campaign, StrategicWar war, Army army)
    {
        var line = LineFor(campaign, army);
        if (line is not null) return line.Provinces;
        if (war is null) return EmptyPosts;
        return army.OrderKind == ArmyOrder.ReserveFront
            ? FrontPlanner.ReserveLine(campaign, war, army.NationId)
            : war.FriendlyFront(army.NationId);
    }

    /// <summary>
    /// The province one formation should make for out of the line it shares with others.
    /// Formations are spread evenly along the line by their place in its order of battle,
    /// which is what a player means by drawing a line rather than a point: cover it.
    /// </summary>
    public static int PostFor(DrawnFrontLine line, Army army, IReadOnlyList<int> ordered)
    {
        if (ordered.Count == 0) return -1;
        var slot = line.ArmyIds.IndexOf(army.Id);
        if (slot < 0) slot = Math.Abs(army.Id);
        var count = Math.Max(1, line.ArmyIds.Count);
        var index = (int)((slot + 0.5f) / count * ordered.Count);
        index = Math.Clamp(index, 0, ordered.Count - 1);

        // Where the formation actually is, if that is anywhere on this line. Cohesion is
        // about the distance between here and the post it has been given: a wanted post
        // four provinces away is a division that spends a week walking sideways instead of
        // holding anything, and at the tight end of the scale it is simply refused.
        var standing = -1;
        for (var index2 = 0; index2 < ordered.Count; index2++)
            if (ordered[index2] == army.ProvinceId) { standing = index2; break; }
        if (standing < 0) return ordered[index];

        var allowed = line.Cohesion switch
        {
            PlanCohesion.Flexible => int.MaxValue,
            PlanCohesion.Balanced => Math.Max(2, ordered.Count / 4),
            PlanCohesion.Rigid => 1,
            _ => 0
        };
        if (allowed <= 0) return army.ProvinceId;
        var drift = index - standing;
        if (Math.Abs(drift) <= allowed) return ordered[index];
        var capped = standing + Math.Sign(drift) * allowed;
        return ordered[Math.Clamp(capped, 0, ordered.Count - 1)];
    }

    /// <summary>
    /// Walk a painted blob into an order that runs along it, so spreading formations
    /// across "the line" spreads them along its length rather than across its width. The
    /// walk starts at the province furthest from the centre of mass and repeatedly steps
    /// to the nearest province not yet visited, which for anything a brush draws is the
    /// line read end to end.
    /// </summary>
    public static List<int> Sequence(Campaign campaign, DrawnFrontLine line)
    {
        var topology = campaign?.ProvinceMap;
        var provinces = line.Provinces.Where(province => province > 0 &&
            (topology is null || province <= topology.ProvinceCount)).ToList();
        provinces.Sort();
        if (topology is null || provinces.Count <= 2) return provinces;

        // An area is not a line, so it is not walked. The order is by what is worth
        // standing on - the biggest places first - because spreading formations evenly
        // over a region means covering what is in it, not pacing its outline.
        if (line.Kind == FrontLineKind.AreaDefence)
        {
            var population = new Dictionary<int, long>();
            foreach (var state in campaign.States) population[state.MapIndex] = state.Population;
            var worth = provinces.ToDictionary(province => province, province =>
            {
                var people = population.GetValueOrDefault(topology.ProvinceToState[province]);
                // A place with few land neighbours is an edge - a coast or a corner - and an
                // edge is where somebody else can arrive without asking first.
                var exposed = topology.Neighbors[province].Length < 4 ? 1.35 : 1.0;
                return people * exposed;
            });
            provinces.Sort((left, right) =>
            {
                var order = worth[right].CompareTo(worth[left]);
                return order != 0 ? order : left.CompareTo(right);
            });
            return provinces;
        }

        var centers = provinces.ToDictionary(province => province, topology.CenterOf);
        var midLatitude = centers.Values.Average(point => point.Latitude);
        var midLongitude = centers.Values.Average(point => point.Longitude);
        var start = provinces
            .OrderByDescending(province => Distance(centers[province], midLatitude, midLongitude))
            .ThenBy(province => province)
            .First();

        var remaining = new HashSet<int>(provinces);
        var order = new List<int>(provinces.Count);
        var current = start;
        while (true)
        {
            order.Add(current);
            remaining.Remove(current);
            if (remaining.Count == 0) break;
            var here = centers[current];
            var nearest = -1;
            var best = float.MaxValue;
            foreach (var province in remaining)
            {
                var distance = Distance(centers[province], here.Latitude, here.Longitude);
                if (distance >= best && (distance > best || province >= nearest)) continue;
                best = distance;
                nearest = province;
            }
            current = nearest;
        }
        return order;
    }

    private static float Distance((float Latitude, float Longitude) point, float latitude, float longitude)
    {
        var dy = point.Latitude - latitude;
        var dx = (point.Longitude - longitude) * MathF.Cos(latitude * MathF.PI / 180f);
        return dy * dy + dx * dx;
    }

    /// <summary>Take a formation off any line it was on - it has gone to sea.</summary>
    public static void Forget(Campaign campaign, Army army) => DrawFrontLineCommand.Detach(campaign, army);

    private static readonly HashSet<int> EmptyPosts = new();
}

/// <summary>
/// Commit a drawn line and hand it to a set of formations. Replacing the line a formation
/// was on is the ordinary case - a player redraws rather than edits - so this drops the
/// old assignment and cleans up any line left with nobody on it.
/// </summary>
public sealed class DrawFrontLineCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public FrontLineKind Kind { get; init; }
    public IReadOnlyList<int> Provinces { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> ArmyIds { get; init; } = Array.Empty<int>();

    public void Apply(Campaign campaign)
    {
        var nation = campaign.NationById(IssuerId);
        if (nation is null || Provinces.Count == 0 || ArmyIds.Count == 0) return;
        var topology = campaign.ProvinceMap;

        var line = new DrawnFrontLine
        {
            Id = campaign.NextFrontLineId++,
            NationId = IssuerId,
            Kind = Kind
        };
        foreach (var province in Provinces)
            if (province > 0 && (topology is null || province <= topology.ProvinceCount))
                line.Provinces.Add(province);
        if (line.Provinces.Count == 0) return;

        foreach (var id in ArmyIds)
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null || army.NationId != IssuerId) continue;
            Detach(campaign, army);
            army.FrontLineId = line.Id;
            line.ArmyIds.Add(army.Id);
        }
        if (line.ArmyIds.Count == 0) return;
        campaign.FrontLines.Add(line);

        // A drawn line carries its own order: holding one is a front-line order, taking
        // one is an offensive, and standing behind one is a reserve. The player drew the
        // intention; they should not have to also press the button that names it.
        var order = Kind switch
        {
            FrontLineKind.Offensive => ArmyOrder.AdvanceFront,
            FrontLineKind.Fallback => ArmyOrder.ReserveFront,
            // Guarding the rear is a reserve posture, not a front-line one: these
            // formations are not meant to be drawn into the fighting at the edge.
            FrontLineKind.AreaDefence => ArmyOrder.ReserveFront,
            _ => ArmyOrder.DefendFront
        };
        foreach (var id in line.ArmyIds)
            new SetArmyOrderCommand { IssuerId = IssuerId, ArmyId = id, Order = order }.Apply(campaign);
    }

    internal static void Detach(Campaign campaign, Army army)
    {
        if (army.FrontLineId < 0) return;
        var previous = campaign.FrontLines.Find(candidate => candidate.Id == army.FrontLineId);
        previous?.ArmyIds.Remove(army.Id);
        army.FrontLineId = -1;
        campaign.FrontLines.RemoveAll(candidate => candidate.ArmyIds.Count == 0);
    }
}

/// <summary>
/// Say how freely the planner may move formations along one drawn line.
///
/// Per line rather than per formation, because it is a property of the plan: a player who
/// wants one division pinned takes that division off the plan instead, which is a different
/// order with a different key.
/// </summary>
public sealed class SetPlanCohesionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FrontLineId { get; init; }
    public PlanCohesion Cohesion { get; init; }

    public void Apply(Campaign campaign)
    {
        var line = campaign.FrontLines.Find(candidate => candidate.Id == FrontLineId);
        if (line is null || line.NationId != IssuerId) return;
        line.Cohesion = Cohesion;
    }
}

/// <summary>Take formations off whatever line they were drawn onto.</summary>
public sealed class ClearFrontLineCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public IReadOnlyList<int> ArmyIds { get; init; } = Array.Empty<int>();

    public void Apply(Campaign campaign)
    {
        foreach (var id in ArmyIds)
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null || army.NationId != IssuerId) continue;
            DrawFrontLineCommand.Detach(campaign, army);
        }
    }
}
