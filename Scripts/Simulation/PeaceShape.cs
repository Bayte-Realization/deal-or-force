using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The least of a region the winner must stand on before it asks for it (the war goal excepted).</summary>
    public float PeaceShapeOccupiedShare { get; set; } = 0.25f;
    /// <summary>A pocket a treaty would cut off is taken with it when it has at most this share of the people the loser keeps.</summary>
    public float PeaceShapePocketShare { get; set; } = 0.3f;
    /// <summary>Secured occupied coasts can anchor overseas settlements without a land border.</summary>
    public bool PeaceShapeAllowCoastalBridgeheads { get; set; } = true;
    /// <summary>How far across the sea from the winner's own coast such a landing may be, in km; 0 for any distance.</summary>
    public float PeaceShapeBridgeheadKm { get; set; } = 600f;
    /// <summary>Preference for filling a common frontier rather than extending narrow salients.</summary>
    public float PeaceShapeCompactnessWeight { get; set; } = 3f;
    /// <summary>A pocket of enemy ground this small, shut in by the new border, is taken with it.</summary>
    public int PeaceCompactHoleProvinces { get; set; } = 3;
}

/// <summary>
/// What a winner takes, drawn as a border rather than as a list.
///
/// The treaties this replaces were written province by province out of wherever the front had
/// stopped, then rounded to whole regions - which removed the cuts inside regions and kept
/// everything else: a region in the middle of the loser taken because an army had wandered
/// into it, a blob of the winner with a hole in it, a corner of the loser left behind with no
/// road home. A border nobody would draw on purpose.
///
/// A treaty is written here region by region, from the winner's own border outwards: every
/// region it asks for touches ground it already holds or has just asked for, so what it takes
/// is joined to its country or to a secured occupied coastal bridgehead.
/// Which regions it wants is what countries really say when they take land - our people live
/// there, it was ours, it gives us the sea, it keeps them away from our capital - weighted by
/// how much of it the army actually holds and by what it costs; and each war has one of those
/// arguments as its own, so two wars between the same countries do not end the same way.
///
/// Then the loser's side of the line: any part of it the treaty would cut off from the rest
/// is either taken as well, when it is small, or the treaty gives back the regions that cut
/// it off. A peace leaves neither side in pieces it was not in before.
/// </summary>
public static class PeaceShape
{
    /// <summary>The argument a winner makes for what it takes, one per war.</summary>
    public enum Aim { People, Sea, Buffer, Wealth }

    public static Aim AimOf(Campaign campaign, StrategicWar war, Nation winner)
    {
        if (war is null || winner is null) return Aim.Wealth;
        if (war.CivilConflict || WarAims.IsRebel(winner)) return Aim.People;
        if ((war.IsAttacking(winner.Id) && war.Justification is "irredentism" or "reconquest") ||
            (!war.Limited && campaign.States.Any(s => war.EnemiesOf(winner.Id).Any(id =>
                campaign.NationById(id)?.MapIndex == s.NationMapIndex) && AiStatecraft.ConsolidationAim(campaign, winner, s))))
            return Aim.People;
        // A border incident and a coalition war are about the shape of the frontier, not
        // about a particular people; a total war is fought over the country itself.
        return WarAims.Of(campaign, war, winner) switch
        {
            WarAimKind.Limited or WarAimKind.Coalition => Aim.Buffer,
            WarAimKind.Total => Aim.People,
            _ => (Aim)(int)(MovementRules.Roll(campaign.Seed, winner.Id, war.Id, 4242) * 4f % 4f)
        };
    }

    // ------------------------------------------------------------------ the map of regions --

    private sealed class Regions
    {
        public readonly Dictionary<int, HashSet<int>> Next = new();
        public readonly Dictionary<int, List<int>> Provinces = new();
        public readonly HashSet<int> Coastal = new();
        public readonly List<int> CoastalProvinces = new();
        public readonly Dictionary<(int Region, int Other), int> SharedEdges = new();
    }

    private static ProvinceTopology _builtFor;
    private static int _builtAt = -1;
    private static Regions _built;

    /// <summary>Which regions touch by land, what each is made of and which have a coast; kept until the map changes.</summary>
    private static Regions Graph(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (_built is not null && ReferenceEquals(_builtFor, topology) && _builtAt == campaign.MapRevision) return _built;
        var regions = new Regions();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            int region = topology.ProvinceToState[province];
            if (region == 0) continue;
            if (!regions.Provinces.TryGetValue(region, out var list)) regions.Provinces[region] = list = new List<int>();
            list.Add(province);
            if (!regions.Next.ContainsKey(region)) regions.Next[region] = new HashSet<int>();
            foreach (var neighbour in topology.Neighbors[province])
            {
                int other = topology.ProvinceToState[neighbour];
                if (other == 0 || other == region) continue;
                regions.Next[region].Add(other);
                var edge = (region, other);
                regions.SharedEdges[edge] = regions.SharedEdges.GetValueOrDefault(edge) + 1;
            }
        }
        foreach (var province in SeaLanes.CoastalProvinces(topology))
            if (province > 0 && province <= topology.ProvinceCount)
            {
                regions.Coastal.Add(topology.ProvinceToState[province]);
                regions.CoastalProvinces.Add(province);
            }
        _builtFor = topology;
        _builtAt = campaign.MapRevision;
        _built = regions;
        return regions;
    }

    private static IEnumerable<int> Touching(Regions graph, int region) =>
        graph.Next.TryGetValue(region, out var next) ? next : Enumerable.Empty<int>();

    private static HashSet<int> Anchors(Campaign campaign, Regions graph, Nation winner, HashSet<int> winnerRegions)
    {
        var anchors = winnerRegions.SelectMany(region => Touching(graph, region)).ToHashSet();
        if (!campaign.Defines.PeaceShapeAllowCoastalBridgeheads || campaign.ProvinceController is null ||
            campaign.ProvinceOccupation is null) return anchors;
        var map = campaign.ProvinceMap;
        var occupiedCoasts = new HashSet<int>();
        foreach (var province in graph.CoastalProvinces)
            if (campaign.ProvinceController[province] == winner.MapIndex &&
                campaign.ProvinceOccupation[province] >= campaign.Defines.OccupationSecureThreshold)
                occupiedCoasts.Add(map.ProvinceToState[province]);
        // Across a strait or a narrow sea, not an ocean. Any secured beach used to anchor a
        // treaty wherever it was, and the treaty then walked inland from it region by region:
        // a Brazilian landing on the far side of the Atlantic was a new Brazilian province, and
        // the next war's treaty grew from that. A country takes land a short crossing from
        // its own shore - Taiwan, Cyprus, the Channel - or it takes none by sea at all.
        var reach = campaign.Defines.PeaceShapeBridgeheadKm;
        var shores = reach > 0f && occupiedCoasts.Count > 0 ? Shores(map, graph, winner) : null;
        var nearest = reach > 0f ? MathF.Cos(reach / EarthRadiusKm) : -2f;
        foreach (var region in occupiedCoasts)
        {
            if (!graph.Provinces.TryGetValue(region, out var provinces)) continue;
            var held = provinces.Count(province => campaign.ProvinceController[province] == winner.MapIndex);
            if (held / (float)provinces.Count < M.Clamp01(campaign.Defines.PeaceShapeOccupiedShare)) continue;
            if (shores is not null && !provinces.Any(province => Within(map, province, shores, nearest))) continue;
            anchors.Add(region);
        }
        foreach (var state in campaign.States)
        {
            if (TerritorialClaims.TitleOf(campaign, winner, state) == TerritorialTitle.Homeland &&
                graph.Provinces.TryGetValue(state.MapIndex, out var provs) &&
                provs.Any(p => campaign.ProvinceController[p] == (ushort)winner.MapIndex))
            {
                anchors.Add(state.MapIndex);
            }
        }
        return anchors;
    }

    /// <summary>Whether a treaty may grow from this region for this winner today: next to its land, or a landing it holds across a narrow sea.</summary>
    internal static bool CanAnchor(Campaign campaign, Nation winner, int region)
    {
        if (campaign.ProvinceMap is null || winner is null) return false;
        var winnerRegions = campaign.States.Where(state => state.NationMapIndex == winner.MapIndex).Select(state => state.MapIndex).ToHashSet();
        return Anchors(campaign, Graph(campaign), winner, winnerRegions).Contains(region);
    }

    private static readonly Dictionary<long, (int Revision, bool Near)> NearCache = new();

    /// <summary>
    /// Whether a whole country is within reach of another: ground the two own side by side,
    /// or - across the sea - both the other's capital and a stretch of its coast within
    /// <see cref="Defines.PeaceShapeBridgeheadKm"/> of this one's coast.
    ///
    /// The capital, because a country is taken in whole: Indonesia is a strait from the Torres
    /// Strait islands and a continent from Canberra. And the coast, because the reach is a sea
    /// crossing and not a line drawn over somebody else's country: Prague is within that
    /// distance of the Italian Adriatic, and Czechia has no shore to cross to - a country with
    /// no coast is taken in by a neighbour or not at all. Kept until the map changes - asked
    /// daily of every dependency.
    /// </summary>
    public static bool Near(Campaign campaign, Nation from, Nation other)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || from is null || other is null) return false;
        var reach = campaign.Defines.PeaceShapeBridgeheadKm;
        if (reach <= 0f) return true;
        var key = ((long)from.Id << 32) | (uint)other.Id;
        if (NearCache.TryGetValue(key, out var known) && known.Revision == campaign.MapRevision) return known.Near;
        // By who owns the ground, not by whose troops stand on it: the border table the AI
        // reads is drawn from control, and an army on a beach is not a border.
        var ownerFrom = (ushort)from.MapIndex;
        var ownerOther = (ushort)other.MapIndex;
        var near = false;
        for (var province = 1; province <= map.ProvinceCount && !near; province++)
        {
            if (map.ProvinceToNation[province] != ownerOther) continue;
            foreach (var neighbour in map.Neighbors[province])
                if (map.ProvinceToNation[neighbour] == ownerFrom) { near = true; break; }
        }
        if (!near)
        {
            var shores = new List<(float X, float Y, float Z)>();
            foreach (var province in SeaLanes.CoastalProvinces(map))
                if (province > 0 && province <= map.ProvinceCount && map.ProvinceToNation[province] == ownerFrom)
                    shores.Add(OnSphere(map, province));
            var nearest = MathF.Cos(reach / EarthRadiusKm);
            var (x, y, z) = OnSphere(other.CapitalLatitude, other.CapitalLongitude);
            foreach (var shore in shores)
                if (x * shore.X + y * shore.Y + z * shore.Z >= nearest) { near = true; break; }
            if (near)
            {
                near = false;
                foreach (var province in SeaLanes.CoastalProvinces(map))
                    if (province > 0 && province <= map.ProvinceCount && map.ProvinceToNation[province] == ownerOther &&
                        Within(map, province, shores, nearest)) { near = true; break; }
            }
        }
        NearCache[key] = (campaign.MapRevision, near);
        return near;
    }

    private const float EarthRadiusKm = 6371f;

    /// <summary>Where the winner's own coast is, as points on a unit sphere.</summary>
    private static List<(float X, float Y, float Z)> Shores(ProvinceTopology map, Regions graph, Nation winner)
    {
        var shores = new List<(float, float, float)>();
        foreach (var province in graph.CoastalProvinces)
            if (map.ProvinceToNation[province] == winner.MapIndex) shores.Add(OnSphere(map, province));
        return shores;
    }

    /// <summary>Whether a province is within the crossing of any of those shores (a cosine of the angle apart).</summary>
    private static bool Within(ProvinceTopology map, int province, List<(float X, float Y, float Z)> shores, float nearest)
    {
        var (x, y, z) = OnSphere(map, province);
        foreach (var shore in shores)
            if (x * shore.X + y * shore.Y + z * shore.Z >= nearest) return true;
        return false;
    }

    private static (float X, float Y, float Z) OnSphere(ProvinceTopology map, int province)
    {
        var (latitude, longitude) = map.CenterOf(province);
        return OnSphere(latitude, longitude);
    }

    private static (float X, float Y, float Z) OnSphere(float latitude, float longitude)
    {
        var phi = latitude * MathF.PI / 180f;
        var lambda = longitude * MathF.PI / 180f;
        return (MathF.Cos(phi) * MathF.Cos(lambda), MathF.Cos(phi) * MathF.Sin(lambda), MathF.Sin(phi));
    }

    // ------------------------------------------------------------------ writing a demand ----

    /// <summary>
    /// The land in an AI's demand: regions from its own border outwards, the best argument for
    /// the money first, until the war score is spent; then settled so neither side is left in
    /// pieces. Writes into <paramref name="demand"/>; the rest of the treaty is written after.
    /// </summary>
    internal static void Write(Campaign campaign, Nation winner, StrategicWar war, PeaceLedger ledger,
        PeaceDemand demand, float budget)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;
        // The campaign asked for the line the armies actually hold. Whole regions are the
        // other setting; filling them here is what made "Peace by provinces" change nothing.
        if (!campaign.Defines.PeaceWholeRegions)
        {
            WriteAlongTheFront(campaign, winner, war, ledger, demand, budget);
            return;
        }
        var graph = Graph(campaign);
        var defines = campaign.Defines;
        var losers = war.EnemiesOf(winner.Id).Select(campaign.NationById).Where(nation => nation is not null).ToList();
        var loserIndices = losers.Select(nation => nation.MapIndex).ToHashSet();
        var aim = AimOf(campaign, war, winner);

        // What the winner stands on, region by region, and what each region is worth to it.
        var winnerCultures = WarAims.Cultures(winner);
        var winnerRegions = campaign.States.Where(state => state.NationMapIndex == winner.MapIndex).Select(state => state.MapIndex).ToHashSet();
        var anchors = Anchors(campaign, graph, winner, winnerRegions);
        var landlocked = !winnerRegions.Any(graph.Coastal.Contains);
        var nearCapital = Touching(graph, winner.CapitalStateMapIndex).ToHashSet();
        var candidates = new Dictionary<int, float>();
        foreach (var state in campaign.States)
        {
            if (!loserIndices.Contains(state.NationMapIndex) || !graph.Provinces.TryGetValue(state.MapIndex, out var provinces)) continue;
            var held = provinces.Count(province => controllers[province] == (ushort)winner.MapIndex) / (float)provinces.Count;
            var goal = state.MapIndex == war.ClaimedStateMapIndex;
            var ours = CultureShare(state, winnerCultures);
            var wasOurs = TerritorialClaims.TitleOf(campaign, winner, state) == TerritorialTitle.Homeland;
            var rebelWeight = WarAims.ClaimWeight(campaign, war, winner, state);
            var rebelClaim = rebelWeight > 0f;
            if (!goal && held < defines.PeaceShapeOccupiedShare && !(wasOurs && held > 0f) &&
                !(ours > 0.5f && held > 0.1f) && !rebelClaim) continue;

            var perProvince = state.Population / MathF.Max(1f, provinces.Count);
            var value = (state.Population / 1_000_000f + 0.05f) * (0.20f + perProvince / 100_000f) * (0.35f + held);
            value *= 1f + (aim == Aim.People ? 4f : 2f) * ours;
            if (wasOurs) value *= 2.2f;
            if (graph.Coastal.Contains(state.MapIndex)) value *= landlocked ? 3f : aim == Aim.Sea ? 2.2f : 1.15f;
            if (nearCapital.Contains(state.MapIndex)) value *= aim == Aim.Buffer ? 2.2f : 1.25f;
            if (aim == Aim.Wealth)
            {
                // What is built there for the people there: the plants, mines and ports a war is fought for.
                var built = 0;
                foreach (var count in state.Buildings.Values) built += count;
                value *= 0.6f + MathF.Min(2f, built / MathF.Max(1f, state.Population / 1_000_000f) * 0.1f);
            }
            if (goal) value *= 5f;
            if (rebelClaim) value *= rebelWeight;
            if (aim == Aim.People && ours > .5f &&
                losers.Any(loser => loser.CapitalStateMapIndex == state.MapIndex)) value *= 3f;
            // And the part of any decision nobody can quite explain afterwards.
            value *= 0.8f + 0.45f * MovementRules.Roll(campaign.Seed, war.Id, state.MapIndex, 77);
            candidates[state.MapIndex] = value;
        }
        if (candidates.Count == 0) return;

        float CostOf(int region) => graph.Provinces[region]
            .Where(province => loserIndices.Contains(topology.ProvinceToNation[province]))
            .Sum(province => ledger.Price(province).Cost);

        var chosen = new HashSet<int>();
        void Apply()
        {
            demand.Provinces.Clear();
            foreach (var region in chosen)
                foreach (var province in graph.Provinces[region])
                    if (loserIndices.Contains(topology.ProvinceToNation[province])) demand.Provinces.Add(province);
        }
        bool Affordable() { Apply(); return ledger.Cost(demand) <= budget; }

        // Prefer the war goal, but a declaration alone cannot create a remote enclave.
        // A non-adjacent goal may be reached through a corridor of occupied regions.
        List<int> goalPath = null;
        if (war.ClaimedStateMapIndex >= 0 && candidates.ContainsKey(war.ClaimedStateMapIndex))
        {
            var corridorPool = new HashSet<int>(candidates.Keys);
            if (graph.Provinces.TryGetValue(war.ClaimedStateMapIndex, out var goalProvinces) &&
                goalProvinces.Any(p => controllers[p] == (ushort)winner.MapIndex))
            {
                foreach (var state in campaign.States)
                {
                    if (!loserIndices.Contains(state.NationMapIndex) ||
                        !graph.Provinces.TryGetValue(state.MapIndex, out var provs)) continue;
                    if (provs.Any(p => controllers[p] == (ushort)winner.MapIndex))
                        corridorPool.Add(state.MapIndex);
                }
            }
            goalPath = GoalCorridor(graph, anchors, corridorPool, war.ClaimedStateMapIndex, CostOf);
            if (goalPath.Count > 0)
            {
                foreach (var region in goalPath) chosen.Add(region);
                if (Affordable())
                {
                    foreach (var region in goalPath)
                        candidates[region] = MathF.Max(candidates.GetValueOrDefault(region), 1_000_000f);
                }
                else
                {
                    chosen.Remove(war.ClaimedStateMapIndex);
                    if (Affordable())
                    {
                        foreach (var region in goalPath.Where(r => r != war.ClaimedStateMapIndex))
                            candidates[region] = MathF.Max(candidates.GetValueOrDefault(region), 1_000_000f);
                    }
                    else
                    {
                        chosen.Clear();
                    }
                }
            }
        }

        // Ground the army already holds, joined to the country, is the peace. War score
        // buys what has not been taken. It does not buy the occupation back down to the
        // one cheap region on the border.
        KeepHeldConquest(campaign, winner, war, graph, controllers, anchors, candidates, chosen, budget);

        // Then outwards from the border, best argument per point of war score first.
        while (true)
        {
            var best = -1;
            var bestScore = 0f;
            foreach (var (region, value) in candidates)
            {
                if (chosen.Contains(region)) continue;
                var joined = anchors.Contains(region) || Touching(graph, region).Any(chosen.Contains);
                if (!joined) continue;
                var score = value / MathF.Max(0.01f, CostOf(region));
                var totalEdges = 0;
                var sharedEdges = 0;
                foreach (var neighbour in Touching(graph, region))
                {
                    var length = graph.SharedEdges.GetValueOrDefault((region, neighbour), 1);
                    totalEdges += length;
                    if (winnerRegions.Contains(neighbour) || chosen.Contains(neighbour)) sharedEdges += length;
                }
                // Land contact alone permits a cross-shaped treaty. A broad shared frontier
                // makes a compact addition; a single narrow connection makes a costly salient.
                score *= .35f + MathF.Max(0f, defines.PeaceShapeCompactnessWeight) *
                    sharedEdges / Math.Max(1f, totalEdges);
                if (score <= bestScore) continue;
                bestScore = score;
                best = region;
            }
            if (best < 0) break;
            chosen.Add(best);
            if (Affordable()) continue;
            chosen.Remove(best);
            candidates.Remove(best);
        }

        Settle(campaign, war, chosen, winner, loserIndices, candidates, Affordable);
        Apply();

        // If the war goal was not affordable as a whole region, taking its occupied provinces
        // is allowed (SimBench exempts ClaimedStateMapIndex from the indivisibility rule).
        if (war.ClaimedStateMapIndex >= 0 &&
            !demand.Provinces.Any(p => topology.ProvinceToState[p] == war.ClaimedStateMapIndex) &&
            graph.Provinces.TryGetValue(war.ClaimedStateMapIndex, out var goalAll))
        {
            var heldGoal = goalAll.Where(p => loserIndices.Contains(topology.ProvinceToNation[p]) &&
                                             controllers[p] == (ushort)winner.MapIndex).ToList();
            if (heldGoal.Count > 0)
            {
                var touches = new HashSet<int>(demand.Provinces);
                bool Connects(int p) => topology.Neighbors[p].Any(n =>
                    topology.ProvinceToNation[n] == (ushort)winner.MapIndex || touches.Contains(n));

                var reachableGoal = heldGoal.Where(Connects).ToList();
                if (reachableGoal.Count > 0)
                {
                    var trialProvinces = new HashSet<int>(demand.Provinces);
                    foreach (var p in reachableGoal) trialProvinces.Add(p);

                    var trialDemand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
                    foreach (var p in trialProvinces) trialDemand.Provinces.Add(p);

                    var trimmedChosen = new HashSet<int>(chosen);
                    var canTrim = trimmedChosen
                        .Where(r => r != war.ClaimedStateMapIndex && (goalPath == null || !goalPath.Contains(r)))
                        .OrderBy(r => candidates.GetValueOrDefault(r, 0f))
                        .ToList();

                    while (ledger.Cost(trialDemand) > budget && canTrim.Count > 0)
                    {
                        var toRemove = canTrim[0];
                        canTrim.RemoveAt(0);
                        trimmedChosen.Remove(toRemove);
                        trialDemand.Provinces.Clear();
                        foreach (var r in trimmedChosen)
                            foreach (var p in graph.Provinces[r])
                                if (loserIndices.Contains(topology.ProvinceToNation[p]))
                                    trialDemand.Provinces.Add(p);
                        foreach (var p in reachableGoal) trialDemand.Provinces.Add(p);
                    }

                    if (ledger.Cost(trialDemand) <= budget)
                    {
                        var rounded = PeaceConference.Round(campaign, war, trialDemand);
                        if (budget > ledger.Budget || rounded is null || ledger.Cost(rounded) <= ledger.Budget)
                        {
                            chosen.Clear();
                            foreach (var r in trimmedChosen) chosen.Add(r);
                            demand.Provinces.Clear();
                            foreach (var p in trialDemand.Provinces) demand.Provinces.Add(p);
                        }
                    }
                }
            }
        }
    }

    private static List<int> GoalCorridor(Regions graph, HashSet<int> anchors, IEnumerable<int> allowed,
        int goal, Func<int, float> costOf)
    {
        var pool = allowed.ToHashSet();
        if (!pool.Contains(goal)) return new List<int>();
        if (anchors.Contains(goal)) return new List<int> { goal };
        var dist = new Dictionary<int, float>();
        var prev = new Dictionary<int, int>();
        var queue = new PriorityQueue<int, float>();
        foreach (var start in anchors.Where(pool.Contains))
        {
            var c = costOf(start);
            dist[start] = c;
            queue.Enqueue(start, c);
        }
        while (queue.TryDequeue(out var current, out var d))
        {
            if (current == goal) break;
            if (d > dist.GetValueOrDefault(current, float.MaxValue)) continue;
            foreach (var next in Touching(graph, current))
            {
                if (!pool.Contains(next)) continue;
                var nd = d + costOf(next);
                if (nd >= dist.GetValueOrDefault(next, float.MaxValue)) continue;
                dist[next] = nd;
                prev[next] = current;
                queue.Enqueue(next, nd);
            }
        }
        if (!dist.ContainsKey(goal)) return new List<int>();
        var path = new List<int>();
        for (var step = goal; ; step = prev[step])
        {
            path.Add(step);
            if (!prev.ContainsKey(step)) break;
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Regions the winner already controls, grown from its own border through other ground
    /// it controls. Added before price, and only for a side that is not losing. A zero
    /// budget still buys nothing.
    /// </summary>
    private static void KeepHeldConquest(Campaign campaign, Nation winner, StrategicWar war, Regions graph,
        ushort[] controllers, HashSet<int> anchors, Dictionary<int, float> candidates, HashSet<int> chosen, float budget)
    {
        if (campaign is null || winner is null || controllers is null || budget <= 0f) return;
        var score = war is null ? 0f : war.IsAttacking(winner.Id) ? war.WarScore : -war.WarScore;
        if (score < 0f) return;
        var flag = (ushort)winner.MapIndex;
        var floor = M.Clamp01(campaign.Defines.PeaceShapeOccupiedShare);
        var goal = war?.ClaimedStateMapIndex ?? -1;
        var held = new HashSet<int>();
        foreach (var region in candidates.Keys)
        {
            if (!graph.Provinces.TryGetValue(region, out var provinces) || provinces.Count == 0) continue;
            var standing = 0;
            foreach (var province in provinces)
                if (province > 0 && province < controllers.Length && controllers[province] == flag) standing++;
            var share = standing / (float)provinces.Count;
            if (share >= floor || (region == goal && share > 0f)) held.Add(region);
        }
        if (held.Count == 0) return;
        var reached = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var region in held)
            if (anchors.Contains(region) && reached.Add(region)) queue.Enqueue(region);
        while (queue.Count > 0)
            foreach (var next in Touching(graph, queue.Dequeue()))
                if (held.Contains(next) && reached.Add(next)) queue.Enqueue(next);
        foreach (var region in reached) chosen.Add(region);
    }

    /// <summary>
    /// The same demand, province by province: only ground the winner is standing on, grown
    /// from its own border, and never the rest of a region the army did not reach.
    /// </summary>
    private static void WriteAlongTheFront(Campaign campaign, Nation winner, StrategicWar war, PeaceLedger ledger,
        PeaceDemand demand, float budget)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        demand.Provinces.Clear();
        var pool = new HashSet<int>();
        foreach (var province in PeaceConference.Claimable(campaign, war, winner.Id))
            if (controllers[province] == (ushort)winner.MapIndex) pool.Add(province);
        if (pool.Count == 0) return;

        var taken = new HashSet<int>();
        bool Home(int province) =>
            topology.Neighbors[province].Any(neighbour => topology.ProvinceToNation[neighbour] == (ushort)winner.MapIndex);
        bool Joined(int province) =>
            Home(province) || topology.Neighbors[province].Any(taken.Contains);

        var score = war is null ? 0f : war.IsAttacking(winner.Id) ? war.WarScore : -war.WarScore;
        if (budget > 0f && score >= 0f)
        {
            var queue = new Queue<int>();
            foreach (var province in pool)
                if (Home(province) && taken.Add(province))
                {
                    demand.Provinces.Add(province);
                    queue.Enqueue(province);
                }
            while (queue.Count > 0)
                foreach (var next in topology.Neighbors[queue.Dequeue()])
                    if (pool.Contains(next) && taken.Add(next))
                    {
                        demand.Provinces.Add(next);
                        queue.Enqueue(next);
                    }
            foreach (var province in taken) pool.Remove(province);
        }

        // The front that touches home first, then any other ground the army is standing on.
        for (var pass = 0; pass < 2; pass++)
        {
            var progress = true;
            while (progress)
            {
                progress = false;
                var best = -1;
                var bestKey = float.NegativeInfinity;
                foreach (var province in pool)
                {
                    var joined = Joined(province);
                    if (pass == 0 ? !joined : joined) continue;
                    var goal = topology.ProvinceToState[province] == war.ClaimedStateMapIndex ? 1000f : 0f;
                    var key = goal + (Home(province) ? 10f : 0f) - ledger.Price(province).Cost;
                    if (key <= bestKey) continue;
                    bestKey = key;
                    best = province;
                }
                if (best < 0) break;
                pool.Remove(best);
                demand.Provinces.Add(best);
                if (ledger.Cost(demand) > budget)
                {
                    demand.Provinces.Remove(best);
                    progress = true;
                    continue;
                }
                taken.Add(best);
                progress = true;
            }
        }
        var tidy = Compact(campaign, winner, demand.Provinces, war);
        demand.Provinces.Clear();
        foreach (var province in tidy) demand.Provinces.Add(province);
    }

    /// <summary>The share of a region's people who are one of these peoples.</summary>
    private static float CultureShare(ScenarioState state, HashSet<string> cultures)
    {
        if (cultures.Count == 0) return 0f;
        long total = 0, ours = 0;
        foreach (var (culture, size) in EconomySystem.Cultures(state))
        {
            total += size;
            if (cultures.Contains(culture)) ours += size;
        }
        return total <= 0 ? 0f : ours / (float)total;
    }

    // ------------------------------------------------------------------ settling a border ---

    /// <summary>
    /// Make a set of regions a border: every one joined to the winner, no part of a loser cut
    /// off from the rest of it, and within what <paramref name="affordable"/> allows. Changes
    /// <paramref name="chosen"/> in place. <paramref name="worth"/> says which regions to give
    /// back first when some must go - the least worth first; regions it does not know go first.
    /// </summary>
    public static void Settle(Campaign campaign, StrategicWar war, HashSet<int> chosen, Nation winner,
        HashSet<int> loserIndices, IReadOnlyDictionary<int, float> worth = null, Func<bool> affordable = null)
    {
        if (campaign.ProvinceMap is null || chosen.Count == 0) return;
        var graph = Graph(campaign);
        var byIndex = campaign.States.ToDictionary(state => state.MapIndex);
        var winnerRegions = campaign.States.Where(state => state.NationMapIndex == winner.MapIndex)
            .Select(state => state.MapIndex).ToHashSet();
        var anchors = Anchors(campaign, graph, winner, winnerRegions);
        var goal = war?.ClaimedStateMapIndex ?? -1;
        float Worth(int region) => worth is not null && worth.TryGetValue(region, out var value) ? value : 0f;
        var control = campaign.ProvinceController;
        var flag = winner is null ? (ushort)0 : (ushort)winner.MapIndex;
        bool ArmyHolds(int region)
        {
            if (control is null || flag == 0 || !graph.Provinces.TryGetValue(region, out var provinces)) return false;
            foreach (var province in provinces)
                if (province > 0 && province < control.Length && control[province] == flag) return true;
            return false;
        }

        for (var pass = 0; pass < 12; pass++)
        {
            var changed = false;

            // Joined to the winner: reached from its own ground through what it takes.
            var reached = new HashSet<int>();
            var queue = new Queue<int>(chosen.Where(anchors.Contains));
            foreach (var start in queue) reached.Add(start);
            while (queue.Count > 0)
                foreach (var next in Touching(graph, queue.Dequeue()))
                    if (chosen.Contains(next) && reached.Add(next)) queue.Enqueue(next);
            foreach (var loserIndex in loserIndices)
            {
                var before = campaign.States.Where(state => state.NationMapIndex == loserIndex).Select(state => state.MapIndex).ToHashSet();
                if (before.Count == 0 || !before.All(chosen.Contains) || !reached.Overlaps(before)) continue;
                var loser = campaign.Nations.Find(nation => nation.MapIndex == loserIndex);
                if (loser is not null && Reintegration.CanAnnexWhole(campaign, war, winner, loser))
                    foreach (var region in before) reached.Add(region);
            }
            foreach (var region in chosen.Where(region => !reached.Contains(region)).ToList())
            {
                chosen.Remove(region);
                changed = true;
            }

            // No loser left in pieces it was not in before.
            foreach (var loserIndex in loserIndices)
            {
                var before = campaign.States.Where(state => state.NationMapIndex == loserIndex).Select(state => state.MapIndex).ToHashSet();
                if (before.Count == 0) continue;
                var after = before.Where(region => !chosen.Contains(region)).ToHashSet();
                if (after.Count == 0) continue;
                var loser = campaign.Nations.Find(nation => nation.MapIndex == loserIndex);
                var beforeParts = Parts(graph, before);
                var afterParts = Parts(graph, after);
                // Preserve each old land component separately. Comparing everything to
                // the capital's component silently allowed treaties to bisect exclaves.
                foreach (var original in beforeParts)
                {
                    var remaining = afterParts.Where(part => part.Overlaps(original)).ToList();
                    if (remaining.Count < 2) continue;
                    var main = remaining.FirstOrDefault(part => loser is not null && part.Contains(loser.CapitalStateMapIndex)) ??
                               remaining.OrderByDescending(part => part.Sum(region => byIndex.TryGetValue(region, out var s) ? s.Population : 0L)).First();
                    var mainPeople = main.Sum(region => byIndex.TryGetValue(region, out var s) ? s.Population : 0L);
                    foreach (var part in remaining)
                    {
                        if (ReferenceEquals(part, main)) continue;
                        var people = part.Sum(region => byIndex.TryGetValue(region, out var s) ? s.Population : 0L);
                        if (people <= mainPeople * campaign.Defines.PeaceShapePocketShare)
                        {
                            // A pocket: it falls with the ground around it.
                            foreach (var region in part) chosen.Add(region);
                            if (affordable is null || affordable())
                            {
                                changed = true;
                                continue;
                            }
                            foreach (var region in part) chosen.Remove(region);
                        }
                        // Too big to swallow, or too dear: give back what cuts it off, least worth first.
                        // Ground the army holds, war goals, and homeland titles are not given back to unsplit the loser.
                        var cutters = chosen.Where(region => original.Contains(region) &&
                            region != goal &&
                            !ArmyHolds(region) &&
                            TerritorialClaims.TitleOf(campaign, winner, byIndex.GetValueOrDefault(region)) != TerritorialTitle.Homeland &&
                            Leaf(graph, chosen, anchors, region) &&
                            Touching(graph, region).Any(part.Contains))
                            .OrderBy(Worth).ToList();
                        if (cutters.Count == 0) continue;
                        chosen.Remove(cutters[0]);
                        changed = true;
                    }
                }
            }

            // Within what the war score buys: the least worth of the regions at the edge goes first.
            while (affordable is not null && chosen.Count > 0 && !affordable())
            {
                var leaf = chosen.Where(region => region != goal && !ArmyHolds(region) && Leaf(graph, chosen, anchors, region))
                    .OrderBy(Worth).FirstOrDefault(-1);
                if (leaf < 0) leaf = chosen.Where(region => region != goal && !ArmyHolds(region)).OrderBy(Worth).FirstOrDefault(-1);
                if (leaf < 0)
                {
                    if (!chosen.Any(ArmyHolds)) chosen.Clear();
                    break;
                }
                chosen.Remove(leaf);
                changed = true;
            }

            if (!changed) break;
        }
    }

    /// <summary>Whether giving this region back leaves the rest of what is taken still joined to the winner.</summary>
    private static bool Leaf(Regions graph, HashSet<int> chosen, HashSet<int> anchors, int region)
    {
        var rest = chosen.Where(other => other != region).ToHashSet();
        if (rest.Count == 0) return true;
        var reached = new HashSet<int>();
        var queue = new Queue<int>(rest.Where(anchors.Contains));
        foreach (var start in queue) reached.Add(start);
        while (queue.Count > 0)
            foreach (var next in Touching(graph, queue.Dequeue()))
                if (rest.Contains(next) && reached.Add(next)) queue.Enqueue(next);
        return reached.Count == rest.Count;
    }

    /// <summary>The connected pieces a set of regions falls into, by land.</summary>
    private static List<HashSet<int>> Parts(Regions graph, HashSet<int> regions)
    {
        var parts = new List<HashSet<int>>();
        var seen = new HashSet<int>();
        foreach (var start in regions)
        {
            if (!seen.Add(start)) continue;
            var part = new HashSet<int> { start };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
                foreach (var next in Touching(graph, queue.Dequeue()))
                    if (regions.Contains(next) && seen.Add(next))
                    {
                        part.Add(next);
                        queue.Enqueue(next);
                    }
            parts.Add(part);
        }
        return parts;
    }

    /// <summary>
    /// The occupied line, drawn so it can be a border: only what touches the winner's own
    /// ground, plus a pore of the enemy shut inside that line, minus a one-province tooth.
    /// A pocket the army holds in the middle of the loser is not a country.
    /// </summary>
    public static List<int> Compact(Campaign campaign, Nation winner, IEnumerable<int> provinces, StrategicWar war)
    {
        var map = campaign?.ProvinceMap;
        var wanted = new HashSet<int>(provinces ?? Array.Empty<int>());
        if (map is null || winner is null || wanted.Count == 0) return wanted.ToList();
        bool Land(int province) => province > 0 && province <= map.ProvinceCount && !map.IsWater(province);
        bool Home(int province) => Land(province) && map.ProvinceToNation[province] == (ushort)winner.MapIndex;
        IEnumerable<int> Beside(int province) => map.Neighbors[province].Where(Land);

        var body = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var province in wanted)
            if (Beside(province).Any(Home))
            {
                if (body.Add(province)) queue.Enqueue(province);
            }
        while (queue.Count > 0)
            foreach (var next in Beside(queue.Dequeue()))
                if (wanted.Contains(next) && body.Add(next)) queue.Enqueue(next);

        var losers = new HashSet<ushort>();
        if (war is not null)
            foreach (var enemy in war.EnemiesOf(winner.Id))
                if (campaign.NationById(enemy) is { } nation) losers.Add((ushort)nation.MapIndex);
        bool Enemy(int province) => Land(province) && losers.Contains(map.ProvinceToNation[province]);
        bool Inside(int province) => Home(province) || body.Contains(province);
        var holeLimit = Math.Max(1, campaign.Defines.PeaceCompactHoleProvinces);
        for (var pass = 0; pass < holeLimit; pass++)
        {
            var pore = new List<int>();
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                if (!Enemy(province) || body.Contains(province)) continue;
                var beside = Beside(province).ToArray();
                if (beside.Length == 0 || beside.Any(next => !Inside(next))) continue;
                pore.Add(province);
            }
            if (pore.Count == 0) break;
            foreach (var province in pore) body.Add(province);
        }

        var goal = war?.ClaimedStateMapIndex ?? -1;
        if (body.Count > 1)
        {
            var teeth = new List<int>();
            foreach (var province in body)
            {
                if (goal >= 0 && map.ProvinceToState[province] == goal) continue;
                if (Beside(province).Count(Inside) >= 2) continue;
                teeth.Add(province);
            }
            foreach (var province in teeth) body.Remove(province);
        }
        return body.ToList();
    }

    /// <summary>Settle a list of provinces a treaty would move (the enforced peace): whole regions in, a border out.</summary>
    public static List<int> SettleProvinces(Campaign campaign, StrategicWar war, IEnumerable<int> provinces, Nation winner)
    {
        var topology = campaign.ProvinceMap;
        var list = provinces.ToList();
        if (topology is null || list.Count == 0 || winner is null) return list;
        // Named provinces stay named. A province peace still refuses a pocket that does not
        // touch home, a dot shut inside the new line, and a one-province tooth.
        if (!campaign.Defines.PeaceWholeRegions) return Compact(campaign, winner, list, war);
        var graph = Graph(campaign);
        var losers = war.EnemiesOf(winner.Id).Select(campaign.NationById).Where(nation => nation is not null)
            .Select(nation => nation.MapIndex).ToHashSet();
        var chosen = list.Select(province => (int)topology.ProvinceToState[province]).Where(region => region != 0).ToHashSet();
        Settle(campaign, war, chosen, winner, losers);
        // The loser's ground and nobody else's. Rounding a region up could reach a province of
        // it that a third country owns - the winner's own ally, as often as not - and the list
        // of what was held used to wave that through as well.
        return chosen.SelectMany(region => graph.Provinces.TryGetValue(region, out var all) ? all : new List<int>())
            .Where(province => losers.Contains(topology.ProvinceToNation[province]))
            .Distinct()
            .ToList();
    }
}
