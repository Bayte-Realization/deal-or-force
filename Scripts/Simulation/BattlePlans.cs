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
    // Persistent border intent; Provinces below is only its current deployment cache.
    public int BorderNationId = -1;
    public bool WholeBorder;
    public float StartLatitude, StartLongitude, EndLatitude, EndLongitude;
    internal int BorderRevision = int.MinValue;

    /// <summary>How freely the planner may re-sort the formations along this line.</summary>
    public PlanCohesion Cohesion = PlanCohesion.Balanced;

    public readonly HashSet<int> Provinces = new();
    public readonly List<int> ArmyIds = new();

    /// <summary>
    /// Which formation stands where, worked out for the line as a whole.
    ///
    /// It has to be decided for everybody at once. "Where should this division go" has no
    /// answer on its own - the answer depends on where the others are and on which stretch
    /// of the line is actually in danger - and asking it one division at a time is how a
    /// front ends up evenly covered and everywhere too thin.
    /// </summary>
    internal readonly Dictionary<int, int> Posts = new();

    /// <summary>The day the posts were last worked out. Once a day is often enough.</summary>
    internal int PostsDay = int.MinValue;

    /// <summary>
    /// Who is holding what, for anything that wants to show or check it.
    ///
    /// Not saved: it is worked out from the line and the war, both of which are, so writing
    /// it down would only give a reload something to disagree with.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<int, int> Assignment => Posts;

    public bool IsEmpty => Provinces.Count == 0 || ArmyIds.Count == 0;
}

public static class BattlePlanning
{
    /// <summary>The line a formation was drawn onto, if it is still worth anything.</summary>
    public static DrawnFrontLine LineFor(Campaign campaign, Army army)
    {
        if (campaign is null || army is null || army.FrontLineId < 0) return null;
        var line = campaign.FrontLines.Find(candidate => candidate.Id == army.FrontLineId);
        if (line is not null) BorderFronts.Refresh(campaign, line);
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

        // The assignment worked out for the whole line wins where it has an opinion. The
        // even spread below is what is left for formations it did not place - a line with
        // more divisions than posts worth defending, which is a happy problem.
        if (line.Posts.TryGetValue(army.Id, out var assigned) && ordered.Contains(assigned))
            return assigned;

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
        if (topology is null || provinces.Count <= 2)
        {
            AssignPosts(campaign, line, provinces);
            return provinces;
        }

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
            AssignPosts(campaign, line, provinces);
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
        AssignPosts(campaign, line, order);
        return order;
    }

    /// <summary>
    /// Decide who holds what, for one line, weighing what each post is worth defending.
    ///
    /// The rule this replaces was "spread them evenly along the line", which is a good
    /// first answer and a bad final one: a line half of which faces an army and half of
    /// which faces a swamp gets the same number of divisions in each, and the half facing
    /// the army breaks. What a post is worth is the pressure on it against what already
    /// holds it, and what would be lost if it fell.
    ///
    /// The other half of the problem is churn. A planner that re-optimises freely moves
    /// divisions up and down the line for the rest of the war, and a division walking
    /// sideways is a division defending nothing - so a formation already standing on a post
    /// stays there unless somewhere else is clearly worse off, "clearly" being a margin
    /// rather than a tie-break. Cohesion is the player's control over how far it may reach.
    /// </summary>
    private static void AssignPosts(Campaign campaign, DrawnFrontLine line, IReadOnlyList<int> ordered)
    {
        if (campaign is null || ordered.Count == 0) return;
        if (line.PostsDay == campaign.ElapsedDays) return;
        line.PostsDay = campaign.ElapsedDays;

        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController ?? topology?.ProvinceToNation;
        var nation = campaign.NationById(line.NationId);
        if (topology is null || controllers is null || nation is null) return;

        var garrison = line.ArmyIds
            .Select(id => campaign.Armies.Find(candidate => candidate.Id == id))
            .Where(army => army is not null && army.NationId == line.NationId)
            .ToList();
        if (garrison.Count == 0) return;

        // What is standing on each post already, and how much pressure each is under.
        var standing = new Dictionary<int, float>();
        foreach (var army in campaign.Armies)
        {
            if (army.ProvinceId <= 0) continue;
            var friendly = army.NationId == line.NationId;
            var key = army.ProvinceId;
            standing[key] = standing.GetValueOrDefault(key) + (friendly ? army.Power : -army.Power);
        }

        var people = new Dictionary<int, long>();
        foreach (var state in campaign.States) people[state.MapIndex] = state.Population;

        var need = new float[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
        {
            var province = ordered[index];
            float threat = 0f;
            foreach (var neighbour in topology.Neighbors[province])
            {
                if (controllers[neighbour] == nation.MapIndex) continue;
                // Somebody else's ground next door is pressure whether or not an army is
                // standing on it; an army standing on it is a great deal more.
                threat += 1f;
                var hostile = standing.GetValueOrDefault(neighbour);
                if (hostile < 0f) threat += -hostile * 2.5f;
                // A river between them is the cheapest defence there is, so a post behind
                // one needs less holding than the same post on open ground.
                threat *= M.Lerp(1f, 0.55f, topology.RiverCrossingStrength(province, neighbour));
            }

            // What is behind the post, in people. Losing a province with a city in it is
            // not the same as losing one with a hillside.
            var worth = people.GetValueOrDefault(topology.ProvinceToState[province]) / 4_000_000f;
            var held = Math.Max(0f, standing.GetValueOrDefault(province));
            need[index] = (threat + M.Clamp(worth, 0f, 3f)) / (1f + held);
        }

        // Where each formation is now, in posts along the line.
        var at = new Dictionary<int, int>();
        for (var index = 0; index < ordered.Count; index++)
            foreach (var army in garrison)
                if (army.ProvinceId == ordered[index]) at[army.Id] = index;

        var reach = line.Cohesion switch
        {
            PlanCohesion.Flexible => int.MaxValue,
            PlanCohesion.Balanced => Math.Max(2, ordered.Count / 4),
            PlanCohesion.Rigid => 1,
            _ => 0
        };

        line.Posts.Clear();
        var taken = new bool[ordered.Count];
        var placed = new HashSet<int>();

        // The most pressed posts first, each filled by the formation that can hold it and
        // is nearest to it - power decides between two who are equally close, distance
        // decides between two of equal power, and a formation already there wins both.
        foreach (var slot in Enumerable.Range(0, ordered.Count).OrderByDescending(index => need[index])
                     .ThenBy(index => index))
        {
            if (taken[slot]) continue;
            Army best = null;
            var bestScore = float.MinValue;
            foreach (var army in garrison)
            {
                if (placed.Contains(army.Id)) continue;
                var here = at.TryGetValue(army.Id, out var where) ? where : -1;
                var walk = here < 0 ? ordered.Count : Math.Abs(here - slot);
                if (here >= 0 && walk > reach) continue;

                var score = army.Power / (1f + walk * 0.35f);
                // Staying put is worth something in itself: the digging, the local
                // knowledge, and the week it does not spend walking.
                if (here == slot) score *= 1.6f;
                // And leaving a post that is itself under pressure has to be worth it.
                else if (here >= 0 && need[here] > need[slot] * 0.75f) score *= 0.5f;
                if (score <= bestScore) continue;
                bestScore = score;
                best = army;
            }
            if (best is null) continue;
            line.Posts[best.Id] = ordered[slot];
            placed.Add(best.Id);
            taken[slot] = true;
            if (placed.Count == garrison.Count) break;
        }
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
    public int BorderNationId { get; init; } = -1;
    public int EditLineId { get; init; } = -1;
    public bool WholeBorder { get; init; }
    public float StartLatitude { get; init; }
    public float StartLongitude { get; init; }
    public float EndLatitude { get; init; }
    public float EndLongitude { get; init; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign.NationById(IssuerId);
        if (nation is null || Provinces.Count == 0 || ArmyIds.Count == 0) return;
        var topology = campaign.ProvinceMap;

        var existing = EditLineId >= 0 ? campaign.FrontLines.Find(x => x.Id == EditLineId && x.NationId == IssuerId) : null;
        if (EditLineId >= 0 && existing is null) return;
        var line = new DrawnFrontLine
        {
            Id = existing?.Id ?? campaign.NextFrontLineId++,
            NationId = IssuerId,
            Kind = Kind,
            BorderNationId = BorderNationId,
            WholeBorder = WholeBorder,
            StartLatitude = StartLatitude, StartLongitude = StartLongitude,
            EndLatitude = EndLatitude, EndLongitude = EndLongitude,
            Cohesion = existing?.Cohesion ?? PlanCohesion.Balanced
        };
        if (Kind == FrontLineKind.Defence)
        {
            if (line.BorderNationId < 0)
            {
                // Old callers/AI still supply province hints. Snap those to a real border.
                var seed = Provinces.FirstOrDefault(p => BorderFronts.ForeignAt(campaign, IssuerId, p) >= 0);
                if (seed <= 0) return;
                line.BorderNationId = BorderFronts.ForeignAt(campaign, IssuerId, seed);
                var first = topology.CenterOf(seed);
                var last = topology.CenterOf(Provinces.Last());
                line.StartLatitude = first.Latitude; line.StartLongitude = first.Longitude;
                line.EndLatitude = last.Latitude; line.EndLongitude = last.Longitude;
            }
            BorderFronts.Refresh(campaign, line);
        }
        else foreach (var province in Provinces)
            if (province > 0 && (topology is null || province <= topology.ProvinceCount)) line.Provinces.Add(province);
        if (line.Provinces.Count == 0) return;

        var assigned = existing is null ? ArmyIds : existing.ArmyIds.ToArray();
        if (existing is not null) campaign.FrontLines.Remove(existing);

        foreach (var id in assigned)
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
