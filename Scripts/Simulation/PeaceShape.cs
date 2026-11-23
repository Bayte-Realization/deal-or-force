using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        if (war.IsAttacking(winner.Id) && war.Justification is "irredentism" or "reconquest")
            return Aim.People;
        if (!war.Limited)
        {
            foreach (var enemyId in war.EnemiesOf(winner.Id))
            {
                var enemy = campaign.NationById(enemyId);
                if (enemy is null) continue;
                var enemyStates = campaign.StatesOf(enemy);
                for (var i = 0; i < enemyStates.Length; i++)
                    if (AiStatecraft.ConsolidationAim(campaign, winner, enemyStates[i]))
                        return Aim.People;
            }
        }
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

    private static readonly object _graphGate = new();
    private static ProvinceTopology _builtFor;
    private static int _builtAt = -1;
    private static ulong _builtStateHash;
    private static int _builtCoastalCount = -1;
    private static Regions _built;

    private static readonly HashSet<int> EmptyRegionSet = new();

    private static ulong HashUShorts(ushort[] values, int count)
    {
        if (values is null || count <= 0) return 0UL;
        var len = Math.Min(count + 1, values.Length);
        var span = MemoryMarshal.Cast<ushort, ulong>(values.AsSpan(0, len));
        var h = (ulong)len * 14695981039346656037UL;
        for (var i = 0; i < span.Length; i++)
        {
            h ^= span[i] + 0x9E3779B97F4A7C15UL;
            h *= 1099511628211UL;
        }
        var rem = len & 3;
        var tailStart = len - rem;
        for (var i = tailStart; i < len; i++)
        {
            h ^= values[i];
            h *= 1099511628211UL;
        }
        return h;
    }

    /// <summary>Which regions touch by land, what each is made of and which have a coast; kept until the map changes.</summary>
    private static Regions Graph(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        var mapRev = campaign.MapRevision;
        var coastalCount = SeaLanes.CoastalProvinces(topology).Count;
        lock (_graphGate)
        {
            if (_built is not null && ReferenceEquals(_builtFor, topology) && _builtCoastalCount == coastalCount)
            {
                if (_builtAt == mapRev) return _built;
                var stateHash = HashUShorts(topology.ProvinceToState, topology.ProvinceCount);
                if (stateHash == _builtStateHash)
                {
                    _builtAt = mapRev;
                    return _built;
                }
            }
            var regions = new Regions();
            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                int region = topology.ProvinceToState[province];
                if (region == 0) continue;
                if (!regions.Provinces.TryGetValue(region, out var list)) regions.Provinces[region] = list = new List<int>();
                list.Add(province);
                if (!regions.Next.ContainsKey(region)) regions.Next[region] = new HashSet<int>();
                // Drawn borders, not crossings: a peace hands over ground that is one piece on the map,
                // and a bridge does not make the far shore part of it (ProvinceTopology.LandNeighbors).
                var landNeighbors = topology.LandNeighbors[province];
                for (var ni = 0; ni < landNeighbors.Length; ni++)
                {
                    int other = topology.ProvinceToState[landNeighbors[ni]];
                    if (other == 0 || other == region) continue;
                    regions.Next[region].Add(other);
                    var edge = (region, other);
                    regions.SharedEdges[edge] = regions.SharedEdges.GetValueOrDefault(edge) + 1;
                }
            }
            var coastal = SeaLanes.CoastalProvinces(topology);
            for (var ci = 0; ci < coastal.Count; ci++)
            {
                var province = coastal[ci];
                if (province > 0 && province <= topology.ProvinceCount)
                {
                    regions.Coastal.Add(topology.ProvinceToState[province]);
                    regions.CoastalProvinces.Add(province);
                }
            }
            _builtFor = topology;
            _builtAt = mapRev;
            _builtStateHash = HashUShorts(topology.ProvinceToState, topology.ProvinceCount);
            _builtCoastalCount = coastalCount;
            _built = regions;
            return regions;
        }
    }

    private static HashSet<int> TouchingSet(Regions graph, int region) =>
        graph.Next.TryGetValue(region, out var next) ? next : EmptyRegionSet;

    private static IEnumerable<int> Touching(Regions graph, int region) =>
        graph.Next.TryGetValue(region, out var next) ? next : Enumerable.Empty<int>();

    [ThreadStatic] private static HashSet<int> _occupiedCoastsScratch;
    [ThreadStatic] private static List<(float X, float Y, float Z)> _shoresScratch;

    private static void FillAnchors(Campaign campaign, Regions graph, Nation winner, HashSet<int> winnerRegions, HashSet<int> anchors)
    {
        anchors.Clear();
        foreach (var region in winnerRegions)
            foreach (var next in TouchingSet(graph, region))
                anchors.Add(next);
        if (!campaign.Defines.PeaceShapeAllowCoastalBridgeheads || campaign.ProvinceController is null ||
            campaign.ProvinceOccupation is null) return;
        var map = campaign.ProvinceMap;
        var control = campaign.ProvinceController;
        var occupation = campaign.ProvinceOccupation;
        var occThreshold = campaign.Defines.OccupationSecureThreshold;
        var occupiedCoasts = _occupiedCoastsScratch ??= new HashSet<int>();
        occupiedCoasts.Clear();
        var coastalProvs = graph.CoastalProvinces;
        for (var ci = 0; ci < coastalProvs.Count; ci++)
        {
            var province = coastalProvs[ci];
            if (control[province] == winner.MapIndex && occupation[province] >= occThreshold)
                occupiedCoasts.Add(map.ProvinceToState[province]);
        }
        // Across a strait or a narrow sea, not an ocean. Any secured beach used to anchor a
        // treaty wherever it was, and the treaty then walked inland from it region by region:
        // a Brazilian landing on the far side of the Atlantic was a new Brazilian province, and
        // the next war's treaty grew from that. A country takes land a short crossing from
        // its own shore - Taiwan, Cyprus, the Channel - or it takes none by sea at all.
        var reach = campaign.Defines.PeaceShapeBridgeheadKm;
        List<(float X, float Y, float Z)> shores = null;
        if (reach > 0f && occupiedCoasts.Count > 0)
        {
            shores = _shoresScratch ??= new List<(float X, float Y, float Z)>();
            FillShores(map, graph, winner, shores);
        }
        var nearest = reach > 0f ? MathF.Cos(reach / EarthRadiusKm) : -2f;
        var minOccShare = M.Clamp01(campaign.Defines.PeaceShapeOccupiedShare);
        foreach (var region in occupiedCoasts)
        {
            if (!graph.Provinces.TryGetValue(region, out var provinces)) continue;
            var held = 0;
            for (var pi = 0; pi < provinces.Count; pi++)
                if (control[provinces[pi]] == winner.MapIndex) held++;
            if (held / (float)provinces.Count < minOccShare) continue;
            if (shores is not null)
            {
                var within = false;
                for (var pi = 0; pi < provinces.Count; pi++)
                    if (Within(map, provinces[pi], shores, nearest)) { within = true; break; }
                if (!within) continue;
            }
            anchors.Add(region);
        }
        var states = campaign.States;
        for (var si = 0; si < states.Count; si++)
        {
            var state = states[si];
            if (TerritorialClaims.TitleOf(campaign, winner, state) == TerritorialTitle.Homeland &&
                graph.Provinces.TryGetValue(state.MapIndex, out var provs))
            {
                for (var pi = 0; pi < provs.Count; pi++)
                {
                    if (control[provs[pi]] == (ushort)winner.MapIndex)
                    {
                        anchors.Add(state.MapIndex);
                        break;
                    }
                }
            }
        }
    }

    [ThreadStatic] private static HashSet<int> _canAnchorWinnerRegionsScratch;
    [ThreadStatic] private static HashSet<int> _canAnchorAnchorsScratch;

    /// <summary>Whether a treaty may grow from this region for this winner today: next to its land, or a landing it holds across a narrow sea.</summary>
    internal static bool CanAnchor(Campaign campaign, Nation winner, int region)
    {
        if (campaign.ProvinceMap is null || winner is null) return false;
        var winnerRegions = _canAnchorWinnerRegionsScratch ??= new HashSet<int>();
        winnerRegions.Clear();
        var states = campaign.StatesOf(winner);
        for (var i = 0; i < states.Length; i++) winnerRegions.Add(states[i].MapIndex);
        var anchors = _canAnchorAnchorsScratch ??= new HashSet<int>();
        FillAnchors(campaign, Graph(campaign), winner, winnerRegions, anchors);
        return anchors.Contains(region);
    }

    private sealed class NearBoard
    {
        public readonly object Gate = new();
        public ProvinceTopology Map;
        public int MapRevision = int.MinValue;
        public ulong OwnerHash;
        public int CoastalCount = -1;
        public int OwnerVersion;
        public readonly HashSet<ulong> LandBorders = new();
        public readonly Dictionary<ushort, (float X, float Y, float Z)[]> CoastalShores = new();
        public readonly Dictionary<ushort, List<(float X, float Y, float Z)>> ShoreListPool = new();
        public readonly Dictionary<long, (int OwnerVersion, float Reach, ushort FromMap, ushort OtherMap, float CapLat, float CapLon, bool Near)> Entries = new();
    }

    private static readonly ConditionalWeakTable<Campaign, NearBoard> NearBoards = new();

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
        var ownerFrom = (ushort)from.MapIndex;
        var ownerOther = (ushort)other.MapIndex;
        var capLat = other.CapitalLatitude;
        var capLon = other.CapitalLongitude;
        var key = ((long)from.Id << 32) | (uint)other.Id;
        var mapRev = campaign.MapRevision;
        var coastal = SeaLanes.CoastalProvinces(map);
        var coastalCount = coastal.Count;
        var board = NearBoards.GetOrCreateValue(campaign);

        lock (board.Gate)
        {
            if (!ReferenceEquals(board.Map, map) || board.MapRevision != mapRev || board.CoastalCount != coastalCount)
            {
                var ownerHash = HashUShorts(map.ProvinceToNation, map.ProvinceCount);
                if (!ReferenceEquals(board.Map, map) || board.CoastalCount != coastalCount || ownerHash != board.OwnerHash)
                {
                    board.OwnerVersion++;
                    board.OwnerHash = ownerHash;
                    board.Map = map;
                    board.CoastalCount = coastalCount;
                    board.LandBorders.Clear();
                    var ptn = map.ProvinceToNation;
                    var neighbors = map.LandNeighbors;
                    var pCount = map.ProvinceCount;
                    for (var p = 1; p <= pCount; p++)
                    {
                        var o = ptn[p];
                        if (o == 0) continue;
                        var nList = neighbors[p];
                        for (var ni = 0; ni < nList.Length; ni++)
                        {
                            var nOwner = ptn[nList[ni]];
                            if (nOwner != 0 && nOwner != o)
                                board.LandBorders.Add(((ulong)o << 32) | nOwner);
                        }
                    }

                    foreach (var list in board.ShoreListPool.Values) list.Clear();
                    for (var ci = 0; ci < coastalCount; ci++)
                    {
                        var province = coastal[ci];
                        if (province <= 0 || province > pCount) continue;
                        var o = ptn[province];
                        if (o == 0) continue;
                        if (!board.ShoreListPool.TryGetValue(o, out var list))
                            board.ShoreListPool[o] = list = new List<(float, float, float)>(16);
                        list.Add(OnSphere(map, province));
                    }
                    board.CoastalShores.Clear();
                    foreach (var kv in board.ShoreListPool)
                        if (kv.Value.Count > 0)
                            board.CoastalShores[kv.Key] = kv.Value.ToArray();
                    board.Entries.Clear();
                }
                board.MapRevision = mapRev;
            }

            if (board.Entries.TryGetValue(key, out var cached) &&
                cached.OwnerVersion == board.OwnerVersion &&
                cached.Reach == reach &&
                cached.FromMap == ownerFrom &&
                cached.OtherMap == ownerOther &&
                cached.CapLat == capLat &&
                cached.CapLon == capLon)
                return cached.Near;

            var near = board.LandBorders.Contains(((ulong)ownerOther << 32) | ownerFrom);
            if (!near && ownerFrom != 0 && ownerOther != 0 &&
                board.CoastalShores.TryGetValue(ownerFrom, out var fromShores) &&
                board.CoastalShores.TryGetValue(ownerOther, out var otherShores))
            {
                var nearest = MathF.Cos(reach / EarthRadiusKm);
                var (x, y, z) = OnSphere(capLat, capLon);
                for (var fi = 0; fi < fromShores.Length; fi++)
                {
                    var shore = fromShores[fi];
                    if (x * shore.X + y * shore.Y + z * shore.Z >= nearest) { near = true; break; }
                }
                if (near)
                {
                    near = false;
                    for (var oi = 0; oi < otherShores.Length && !near; oi++)
                    {
                        var os = otherShores[oi];
                        for (var fi = 0; fi < fromShores.Length; fi++)
                        {
                            var fs = fromShores[fi];
                            if (os.X * fs.X + os.Y * fs.Y + os.Z * fs.Z >= nearest) { near = true; break; }
                        }
                    }
                }
            }
            board.Entries[key] = (board.OwnerVersion, reach, ownerFrom, ownerOther, capLat, capLon, near);
            return near;
        }
    }

    private const float EarthRadiusKm = 6371f;

    /// <summary>Where the winner's own coast is, as points on a unit sphere.</summary>
    private static void FillShores(ProvinceTopology map, Regions graph, Nation winner, List<(float X, float Y, float Z)> shores)
    {
        shores.Clear();
        var coastal = graph.CoastalProvinces;
        var ptn = map.ProvinceToNation;
        var own = winner.MapIndex;
        for (var i = 0; i < coastal.Count; i++)
        {
            var province = coastal[i];
            if (ptn[province] == own) shores.Add(OnSphere(map, province));
        }
    }

    /// <summary>Whether a province is within the crossing of any of those shores (a cosine of the angle apart).</summary>
    private static bool Within(ProvinceTopology map, int province, List<(float X, float Y, float Z)> shores, float nearest)
    {
        var (x, y, z) = OnSphere(map, province);
        for (var i = 0; i < shores.Count; i++)
        {
            var shore = shores[i];
            if (x * shore.X + y * shore.Y + z * shore.Z >= nearest) return true;
        }
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
    [ThreadStatic] private static HashSet<string> _writeWinnerCulturesScratch;

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
        var losers = _writeLosersScratch ??= new List<Nation>();
        losers.Clear();
        var loserIndices = _writeLoserIndicesScratch ??= new HashSet<int>();
        loserIndices.Clear();
        foreach (var enemyId in war.EnemiesOf(winner.Id))
        {
            var nation = campaign.NationById(enemyId);
            if (nation is null) continue;
            losers.Add(nation);
            loserIndices.Add(nation.MapIndex);
        }
        var aim = AimOf(campaign, war, winner);

        // What the winner stands on, region by region, and what each region is worth to it.
        var winnerCultures = _writeWinnerCulturesScratch ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WarAims.FillCultures(winner, winnerCultures);
        var winnerRegions = _writeWinnerRegionsScratch ??= new HashSet<int>();
        winnerRegions.Clear();
        var winnerStates = campaign.StatesOf(winner);
        for (var si = 0; si < winnerStates.Length; si++) winnerRegions.Add(winnerStates[si].MapIndex);
        var anchors = _writeAnchorsScratch ??= new HashSet<int>();
        FillAnchors(campaign, graph, winner, winnerRegions, anchors);
        var landlocked = true;
        foreach (var wr in winnerRegions)
        {
            if (graph.Coastal.Contains(wr)) { landlocked = false; break; }
        }
        var nearCapital = _writeNearCapitalScratch ??= new HashSet<int>();
        nearCapital.Clear();
        foreach (var nr in TouchingSet(graph, winner.CapitalStateMapIndex)) nearCapital.Add(nr);
        var candidates = _writeCandidatesScratch ??= new Dictionary<int, float>();
        candidates.Clear();
        // Regions the winner's army mostly stands on. Ground one region past them - or past the
        // winner's own border - may be bought with war score like a player buys it: at the price
        // of ground never taken, which a country still able to fight marks up (PeaceConference.
        // Resistance). Without this a war won on points and not on the map bought only the line,
        // and +42 ended as a status quo.
        var standing = _writeStandingScratch ??= new HashSet<int>();
        standing.Clear();
        var allStates = campaign.States;
        if (defines.PeaceShapeReachUnheld)
        {
            for (var si = 0; si < allStates.Count; si++)
            {
                var state = allStates[si];
                if (!loserIndices.Contains(state.NationMapIndex) || !graph.Provinces.TryGetValue(state.MapIndex, out var stood)) continue;
                var heldCount = 0;
                for (var pi = 0; pi < stood.Count; pi++)
                    if (controllers[stood[pi]] == (ushort)winner.MapIndex) heldCount++;
                if (heldCount >= stood.Count * defines.PeaceShapeOccupiedShare)
                    standing.Add(state.MapIndex);
            }
        }
        for (var si = 0; si < allStates.Count; si++)
        {
            var state = allStates[si];
            if (!loserIndices.Contains(state.NationMapIndex) || !graph.Provinces.TryGetValue(state.MapIndex, out var provinces)) continue;
            var heldCount = 0;
            for (var pi = 0; pi < provinces.Count; pi++)
                if (controllers[provinces[pi]] == (ushort)winner.MapIndex) heldCount++;
            var held = heldCount / (float)provinces.Count;
            var reach = false;
            if (defines.PeaceShapeReachUnheld)
            {
                if (anchors.Contains(state.MapIndex)) reach = true;
                else
                {
                    foreach (var touching in TouchingSet(graph, state.MapIndex))
                        if (standing.Contains(touching)) { reach = true; break; }
                }
            }
            var goal = state.MapIndex == war.ClaimedStateMapIndex;
            var ours = CultureShare(state, winnerCultures);
            var wasOurs = TerritorialClaims.TitleOf(campaign, winner, state) == TerritorialTitle.Homeland;
            var rebelWeight = WarAims.ClaimWeight(campaign, war, winner, state);
            var rebelClaim = rebelWeight > 0f;
            if (!goal && held < defines.PeaceShapeOccupiedShare && !(wasOurs && held > 0f) &&
                !(ours > 0.5f && held > 0.1f) && !rebelClaim && !reach) continue;

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
            if (aim == Aim.People && ours > .5f)
            {
                for (var li = 0; li < losers.Count; li++)
                    if (losers[li].CapitalStateMapIndex == state.MapIndex) { value *= 3f; break; }
            }
            // And the part of any decision nobody can quite explain afterwards.
            value *= 0.8f + 0.45f * MovementRules.Roll(campaign.Seed, war.Id, state.MapIndex, 77);
            candidates[state.MapIndex] = value;
        }
        if (candidates.Count == 0) return;

        float CostOf(int region)
        {
            var sum = 0f;
            var provs = graph.Provinces[region];
            for (var pi = 0; pi < provs.Count; pi++)
            {
                var p = provs[pi];
                if (loserIndices.Contains(topology.ProvinceToNation[p])) sum += ledger.Price(p).Cost;
            }
            return sum;
        }

        var chosen = _writeChosenScratch ??= new HashSet<int>();
        chosen.Clear();
        void Apply()
        {
            demand.Provinces.Clear();
            foreach (var region in chosen)
            {
                var provs = graph.Provinces[region];
                for (var pi = 0; pi < provs.Count; pi++)
                {
                    var province = provs[pi];
                    if (loserIndices.Contains(topology.ProvinceToNation[province])) demand.Provinces.Add(province);
                }
            }
        }
        bool Affordable() { Apply(); return ledger.Cost(demand) <= budget; }

        // Prefer the war goal, but a declaration alone cannot create a remote enclave.
        // A non-adjacent goal may be reached through a corridor of occupied regions.
        List<int> goalPath = null;
        if (war.ClaimedStateMapIndex >= 0 && candidates.ContainsKey(war.ClaimedStateMapIndex))
        {
            var corridorPool = _writeCorridorPoolScratch ??= new HashSet<int>();
            corridorPool.Clear();
            foreach (var k in candidates.Keys) corridorPool.Add(k);
            var goalHeld = false;
            if (graph.Provinces.TryGetValue(war.ClaimedStateMapIndex, out var goalProvinces))
            {
                for (var pi = 0; pi < goalProvinces.Count; pi++)
                    if (controllers[goalProvinces[pi]] == (ushort)winner.MapIndex) { goalHeld = true; break; }
            }
            if (goalHeld)
            {
                for (var si = 0; si < allStates.Count; si++)
                {
                    var state = allStates[si];
                    if (!loserIndices.Contains(state.NationMapIndex) ||
                        !graph.Provinces.TryGetValue(state.MapIndex, out var provs)) continue;
                    for (var pi = 0; pi < provs.Count; pi++)
                        if (controllers[provs[pi]] == (ushort)winner.MapIndex)
                        {
                            corridorPool.Add(state.MapIndex);
                            break;
                        }
                }
            }
            goalPath = GoalCorridor(graph, anchors, corridorPool, war.ClaimedStateMapIndex, CostOf);
            if (goalPath.Count > 0)
            {
                for (var gi = 0; gi < goalPath.Count; gi++) chosen.Add(goalPath[gi]);
                if (Affordable())
                {
                    for (var gi = 0; gi < goalPath.Count; gi++)
                    {
                        var region = goalPath[gi];
                        candidates[region] = MathF.Max(candidates.GetValueOrDefault(region), 1_000_000f);
                    }
                }
                else
                {
                    chosen.Remove(war.ClaimedStateMapIndex);
                    if (Affordable())
                    {
                        for (var gi = 0; gi < goalPath.Count; gi++)
                        {
                            var region = goalPath[gi];
                            if (region != war.ClaimedStateMapIndex)
                                candidates[region] = MathF.Max(candidates.GetValueOrDefault(region), 1_000_000f);
                        }
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
                var joined = anchors.Contains(region);
                if (!joined)
                {
                    foreach (var touching in TouchingSet(graph, region))
                        if (chosen.Contains(touching)) { joined = true; break; }
                }
                if (!joined) continue;
                var score = value / MathF.Max(0.01f, CostOf(region));
                var totalEdges = 0;
                var sharedEdges = 0;
                foreach (var neighbour in TouchingSet(graph, region))
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
            graph.Provinces.TryGetValue(war.ClaimedStateMapIndex, out var goalAll))
        {
            var hasGoalProvince = false;
            foreach (var p in demand.Provinces)
                if (topology.ProvinceToState[p] == war.ClaimedStateMapIndex) { hasGoalProvince = true; break; }
            if (!hasGoalProvince)
            {
                var heldGoal = _writeHeldGoalScratch ??= new List<int>();
                heldGoal.Clear();
                for (var pi = 0; pi < goalAll.Count; pi++)
                {
                    var p = goalAll[pi];
                    if (loserIndices.Contains(topology.ProvinceToNation[p]) && controllers[p] == (ushort)winner.MapIndex)
                        heldGoal.Add(p);
                }
                if (heldGoal.Count > 0)
                {
                    var touches = _writeTouchesScratch ??= new HashSet<int>();
                    touches.Clear();
                    foreach (var p in demand.Provinces) touches.Add(p);

                    var reachableGoal = _writeReachableGoalScratch ??= new List<int>();
                    reachableGoal.Clear();
                    for (var hi = 0; hi < heldGoal.Count; hi++)
                    {
                        var p = heldGoal[hi];
                        var neighbors = topology.LandNeighbors[p];
                        var connects = false;
                        for (var ni = 0; ni < neighbors.Length; ni++)
                        {
                            var n = neighbors[ni];
                            if (topology.ProvinceToNation[n] == (ushort)winner.MapIndex || touches.Contains(n))
                            {
                                connects = true;
                                break;
                            }
                        }
                        if (connects) reachableGoal.Add(p);
                    }
                    if (reachableGoal.Count > 0)
                    {
                        var trialDemand = _writeTrialDemandScratch ??= new PeaceDemand();
                        trialDemand.WarId = war.Id;
                        trialDemand.ProposerId = winner.Id;
                        trialDemand.Provinces.Clear();
                        foreach (var p in demand.Provinces) trialDemand.Provinces.Add(p);
                        for (var ri = 0; ri < reachableGoal.Count; ri++) trialDemand.Provinces.Add(reachableGoal[ri]);

                        var trimmedChosen = _writeTrimmedChosenScratch ??= new HashSet<int>();
                        trimmedChosen.Clear();
                        foreach (var r in chosen) trimmedChosen.Add(r);

                        var canTrim = _writeCanTrimScratch ??= new List<int>();
                        canTrim.Clear();
                        foreach (var r in trimmedChosen)
                        {
                            if (r == war.ClaimedStateMapIndex) continue;
                            if (goalPath is not null && goalPath.Contains(r)) continue;
                            canTrim.Add(r);
                        }
                        if (canTrim.Count > 1)
                            canTrim.Sort((a, b) => candidates.GetValueOrDefault(a, 0f).CompareTo(candidates.GetValueOrDefault(b, 0f)));

                        var trimIndex = 0;
                        while (ledger.Cost(trialDemand) > budget && trimIndex < canTrim.Count)
                        {
                            var toRemove = canTrim[trimIndex++];
                            trimmedChosen.Remove(toRemove);
                            trialDemand.Provinces.Clear();
                            foreach (var r in trimmedChosen)
                            {
                                var provs = graph.Provinces[r];
                                for (var pi = 0; pi < provs.Count; pi++)
                                {
                                    var p = provs[pi];
                                    if (loserIndices.Contains(topology.ProvinceToNation[p]))
                                        trialDemand.Provinces.Add(p);
                                }
                            }
                            for (var ri = 0; ri < reachableGoal.Count; ri++) trialDemand.Provinces.Add(reachableGoal[ri]);
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
    }

    [ThreadStatic] private static List<Nation> _writeLosersScratch;
    [ThreadStatic] private static HashSet<int> _writeLoserIndicesScratch;
    [ThreadStatic] private static HashSet<int> _writeWinnerRegionsScratch;
    [ThreadStatic] private static HashSet<int> _writeAnchorsScratch;
    [ThreadStatic] private static HashSet<int> _writeNearCapitalScratch;
    [ThreadStatic] private static HashSet<int> _writeStandingScratch;
    [ThreadStatic] private static Dictionary<int, float> _writeCandidatesScratch;
    [ThreadStatic] private static HashSet<int> _writeChosenScratch;
    [ThreadStatic] private static HashSet<int> _writeCorridorPoolScratch;
    [ThreadStatic] private static List<int> _writeHeldGoalScratch;
    [ThreadStatic] private static HashSet<int> _writeTouchesScratch;
    [ThreadStatic] private static List<int> _writeReachableGoalScratch;
    [ThreadStatic] private static PeaceDemand _writeTrialDemandScratch;
    [ThreadStatic] private static HashSet<int> _writeTrimmedChosenScratch;
    [ThreadStatic] private static List<int> _writeCanTrimScratch;

    [ThreadStatic] private static Dictionary<int, float> _corridorDistScratch;
    [ThreadStatic] private static Dictionary<int, int> _corridorPrevScratch;
    [ThreadStatic] private static PriorityQueue<int, float> _corridorQueueScratch;
    [ThreadStatic] private static List<int> _corridorPathScratch;

    private static List<int> GoalCorridor(Regions graph, HashSet<int> anchors, HashSet<int> pool,
        int goal, Func<int, float> costOf)
    {
        var path = _corridorPathScratch ??= new List<int>();
        path.Clear();
        if (!pool.Contains(goal)) return path;
        if (anchors.Contains(goal)) { path.Add(goal); return path; }
        var dist = _corridorDistScratch ??= new Dictionary<int, float>();
        dist.Clear();
        var prev = _corridorPrevScratch ??= new Dictionary<int, int>();
        prev.Clear();
        var queue = _corridorQueueScratch ??= new PriorityQueue<int, float>();
        queue.Clear();
        foreach (var start in anchors)
        {
            if (!pool.Contains(start)) continue;
            var c = costOf(start);
            dist[start] = c;
            queue.Enqueue(start, c);
        }
        while (queue.TryDequeue(out var current, out var d))
        {
            if (current == goal) break;
            if (d > dist.GetValueOrDefault(current, float.MaxValue)) continue;
            foreach (var next in TouchingSet(graph, current))
            {
                if (!pool.Contains(next)) continue;
                var nd = d + costOf(next);
                if (nd >= dist.GetValueOrDefault(next, float.MaxValue)) continue;
                dist[next] = nd;
                prev[next] = current;
                queue.Enqueue(next, nd);
            }
        }
        if (!dist.ContainsKey(goal)) return path;
        for (var step = goal; ; step = prev[step])
        {
            path.Add(step);
            if (!prev.ContainsKey(step)) break;
        }
        path.Reverse();
        return path;
    }

    [ThreadStatic] private static HashSet<int> _keepHeldScratch;
    [ThreadStatic] private static HashSet<int> _keepReachedScratch;
    [ThreadStatic] private static Queue<int> _keepQueueScratch;

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
        var held = _keepHeldScratch ??= new HashSet<int>();
        held.Clear();
        foreach (var region in candidates.Keys)
        {
            if (!graph.Provinces.TryGetValue(region, out var provinces) || provinces.Count == 0) continue;
            var standing = 0;
            for (var pi = 0; pi < provinces.Count; pi++)
            {
                var province = provinces[pi];
                if (province > 0 && province < controllers.Length && controllers[province] == flag) standing++;
            }
            var share = standing / (float)provinces.Count;
            if (share >= floor || (region == goal && share > 0f)) held.Add(region);
        }
        if (held.Count == 0) return;
        var reached = _keepReachedScratch ??= new HashSet<int>();
        reached.Clear();
        var queue = _keepQueueScratch ??= new Queue<int>();
        queue.Clear();
        foreach (var region in held)
            if (anchors.Contains(region) && reached.Add(region)) queue.Enqueue(region);
        while (queue.Count > 0)
            foreach (var next in TouchingSet(graph, queue.Dequeue()))
                if (held.Contains(next) && reached.Add(next)) queue.Enqueue(next);
        foreach (var region in reached) chosen.Add(region);
    }

    [ThreadStatic] private static HashSet<int> _frontPoolScratch;
    [ThreadStatic] private static HashSet<int> _frontTakenScratch;
    [ThreadStatic] private static Queue<int> _frontQueueScratch;

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
        var pool = _frontPoolScratch ??= new HashSet<int>();
        pool.Clear();
        var winnerMap = (ushort)winner.MapIndex;
        foreach (var province in PeaceConference.Claimable(campaign, war, winner.Id))
            if (controllers[province] == winnerMap) pool.Add(province);
        if (pool.Count == 0) return;

        var taken = _frontTakenScratch ??= new HashSet<int>();
        taken.Clear();
        bool Home(int province)
        {
            var neighbors = topology.LandNeighbors[province];
            for (var i = 0; i < neighbors.Length; i++)
                if (topology.ProvinceToNation[neighbors[i]] == winnerMap) return true;
            return false;
        }
        bool Joined(int province)
        {
            if (Home(province)) return true;
            var neighbors = topology.LandNeighbors[province];
            for (var i = 0; i < neighbors.Length; i++)
                if (taken.Contains(neighbors[i])) return true;
            return false;
        }

        var score = war is null ? 0f : war.IsAttacking(winner.Id) ? war.WarScore : -war.WarScore;
        if (budget > 0f && score >= 0f)
        {
            var queue = _frontQueueScratch ??= new Queue<int>();
            queue.Clear();
            foreach (var province in pool)
                if (Home(province) && taken.Add(province))
                {
                    demand.Provinces.Add(province);
                    queue.Enqueue(province);
                }
            while (queue.Count > 0)
            {
                var neighbors = topology.LandNeighbors[queue.Dequeue()];
                for (var ni = 0; ni < neighbors.Length; ni++)
                {
                    var next = neighbors[ni];
                    if (pool.Contains(next) && taken.Add(next))
                    {
                        demand.Provinces.Add(next);
                        queue.Enqueue(next);
                    }
                }
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
        for (var i = 0; i < tidy.Count; i++) demand.Provinces.Add(tidy[i]);
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

    [ThreadStatic] private static Dictionary<int, ScenarioState> _settleByIndexScratch;
    [ThreadStatic] private static Dictionary<int, Nation> _settleNationsByMapScratch;
    [ThreadStatic] private static Dictionary<int, List<int>> _settleLoserStatesScratch;
    [ThreadStatic] private static Stack<List<int>> _settleListPool;
    [ThreadStatic] private static HashSet<int> _settleWinnerRegionsScratch;
    [ThreadStatic] private static HashSet<int> _settleAnchorsScratch;
    [ThreadStatic] private static HashSet<int> _settleReachedScratch;
    [ThreadStatic] private static Queue<int> _settleQueueScratch;
    [ThreadStatic] private static HashSet<int> _settleBeforeScratch;
    [ThreadStatic] private static HashSet<int> _settleAfterScratch;
    [ThreadStatic] private static List<int> _settleRemoveScratch;
    [ThreadStatic] private static List<HashSet<int>> _settleBeforePartsScratch;
    [ThreadStatic] private static List<HashSet<int>> _settleAfterPartsScratch;
    [ThreadStatic] private static List<HashSet<int>> _settleRemainingScratch;
    [ThreadStatic] private static Stack<HashSet<int>> _settlePartSetPool;
    [ThreadStatic] private static HashSet<int> _settlePartsSeenScratch;
    [ThreadStatic] private static Queue<int> _settlePartsQueueScratch;
    [ThreadStatic] private static HashSet<int> _leafReachedScratch;
    [ThreadStatic] private static Queue<int> _leafQueueScratch;

    private static void ReleaseParts(List<HashSet<int>> parts)
    {
        var pool = _settlePartSetPool ??= new Stack<HashSet<int>>();
        for (var i = 0; i < parts.Count; i++)
        {
            parts[i].Clear();
            pool.Push(parts[i]);
        }
        parts.Clear();
    }

    private static HashSet<int> RentPartSet()
    {
        var pool = _settlePartSetPool ??= new Stack<HashSet<int>>();
        return pool.Count > 0 ? pool.Pop() : new HashSet<int>();
    }

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
        var byIndex = _settleByIndexScratch ??= new Dictionary<int, ScenarioState>();
        byIndex.Clear();
        var winnerRegions = _settleWinnerRegionsScratch ??= new HashSet<int>();
        winnerRegions.Clear();
        var loserStates = _settleLoserStatesScratch ??= new Dictionary<int, List<int>>();
        var listPool = _settleListPool ??= new Stack<List<int>>();
        foreach (var existing in loserStates.Values)
        {
            existing.Clear();
            listPool.Push(existing);
        }
        loserStates.Clear();

        var winnerMap = winner is null ? int.MinValue : winner.MapIndex;
        var allStates = campaign.States;
        for (var si = 0; si < allStates.Count; si++)
        {
            var state = allStates[si];
            byIndex[state.MapIndex] = state;
            if (state.NationMapIndex == winnerMap) winnerRegions.Add(state.MapIndex);
            if (loserIndices.Contains(state.NationMapIndex))
            {
                if (!loserStates.TryGetValue(state.NationMapIndex, out var list))
                {
                    list = listPool.Count > 0 ? listPool.Pop() : new List<int>(8);
                    loserStates[state.NationMapIndex] = list;
                }
                list.Add(state.MapIndex);
            }
        }

        var nationsByMap = _settleNationsByMapScratch ??= new Dictionary<int, Nation>();
        nationsByMap.Clear();
        var nations = campaign.Nations;
        for (var ni = 0; ni < nations.Count; ni++)
        {
            var n = nations[ni];
            if (loserIndices.Contains(n.MapIndex)) nationsByMap.TryAdd(n.MapIndex, n);
        }

        var anchors = _settleAnchorsScratch ??= new HashSet<int>();
        FillAnchors(campaign, graph, winner, winnerRegions, anchors);
        var goal = war?.ClaimedStateMapIndex ?? -1;
        float Worth(int region) => worth is not null && worth.TryGetValue(region, out var value) ? value : 0f;
        var control = campaign.ProvinceController;
        var flag = winner is null ? (ushort)0 : (ushort)winner.MapIndex;
        bool ArmyHolds(int region)
        {
            if (control is null || flag == 0 || !graph.Provinces.TryGetValue(region, out var provinces)) return false;
            for (var i = 0; i < provinces.Count; i++)
            {
                var province = provinces[i];
                if (province > 0 && province < control.Length && control[province] == flag) return true;
            }
            return false;
        }

        var reached = _settleReachedScratch ??= new HashSet<int>();
        var queue = _settleQueueScratch ??= new Queue<int>();
        var before = _settleBeforeScratch ??= new HashSet<int>();
        var after = _settleAfterScratch ??= new HashSet<int>();
        var toRemove = _settleRemoveScratch ??= new List<int>();
        var beforeParts = _settleBeforePartsScratch ??= new List<HashSet<int>>();
        var afterParts = _settleAfterPartsScratch ??= new List<HashSet<int>>();
        var remaining = _settleRemainingScratch ??= new List<HashSet<int>>();

        for (var pass = 0; pass < 12; pass++)
        {
            var changed = false;

            // Joined to the winner: reached from its own ground through what it takes.
            reached.Clear();
            queue.Clear();
            foreach (var region in chosen)
                if (anchors.Contains(region) && reached.Add(region)) queue.Enqueue(region);
            while (queue.Count > 0)
                foreach (var next in TouchingSet(graph, queue.Dequeue()))
                    if (chosen.Contains(next) && reached.Add(next)) queue.Enqueue(next);
            foreach (var loserIndex in loserIndices)
            {
                if (!loserStates.TryGetValue(loserIndex, out var statesList) || statesList.Count == 0) continue;
                var allChosen = true;
                var anyReached = false;
                for (var i = 0; i < statesList.Count; i++)
                {
                    var r = statesList[i];
                    if (!chosen.Contains(r)) { allChosen = false; break; }
                    if (reached.Contains(r)) anyReached = true;
                }
                if (!allChosen || !anyReached) continue;
                var loser = nationsByMap.GetValueOrDefault(loserIndex);
                if (loser is not null && Reintegration.CanAnnexWhole(campaign, war, winner, loser))
                    for (var i = 0; i < statesList.Count; i++) reached.Add(statesList[i]);
            }
            toRemove.Clear();
            foreach (var region in chosen)
                if (!reached.Contains(region)) toRemove.Add(region);
            if (toRemove.Count > 0)
            {
                for (var i = 0; i < toRemove.Count; i++) chosen.Remove(toRemove[i]);
                changed = true;
            }

            // No loser left in pieces it was not in before.
            foreach (var loserIndex in loserIndices)
            {
                if (!loserStates.TryGetValue(loserIndex, out var statesList) || statesList.Count == 0) continue;
                before.Clear();
                after.Clear();
                for (var i = 0; i < statesList.Count; i++)
                {
                    var r = statesList[i];
                    before.Add(r);
                    if (!chosen.Contains(r)) after.Add(r);
                }
                if (after.Count == 0) continue;
                var loser = nationsByMap.GetValueOrDefault(loserIndex);
                FillParts(graph, before, beforeParts);
                FillParts(graph, after, afterParts);
                // Preserve each old land component separately. Comparing everything to
                // the capital's component silently allowed treaties to bisect exclaves.
                for (var bi = 0; bi < beforeParts.Count; bi++)
                {
                    var original = beforeParts[bi];
                    remaining.Clear();
                    for (var ai = 0; ai < afterParts.Count; ai++)
                    {
                        var part = afterParts[ai];
                        if (part.Overlaps(original)) remaining.Add(part);
                    }
                    if (remaining.Count < 2) continue;
                    HashSet<int> main = null;
                    if (loser is not null)
                    {
                        for (var ri = 0; ri < remaining.Count; ri++)
                            if (remaining[ri].Contains(loser.CapitalStateMapIndex)) { main = remaining[ri]; break; }
                    }
                    long mainPeople;
                    if (main is null)
                    {
                        mainPeople = long.MinValue;
                        for (var ri = 0; ri < remaining.Count; ri++)
                        {
                            var part = remaining[ri];
                            var pop = PartPopulation(part, byIndex);
                            if (pop > mainPeople) { mainPeople = pop; main = part; }
                        }
                    }
                    else
                    {
                        mainPeople = PartPopulation(main, byIndex);
                    }

                    for (var ri = 0; ri < remaining.Count; ri++)
                    {
                        var part = remaining[ri];
                        if (ReferenceEquals(part, main)) continue;
                        var people = PartPopulation(part, byIndex);
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
                        var bestCutter = -1;
                        var bestWorth = float.PositiveInfinity;
                        foreach (var region in chosen)
                        {
                            if (!original.Contains(region) || region == goal || ArmyHolds(region)) continue;
                            if (TerritorialClaims.TitleOf(campaign, winner, byIndex.GetValueOrDefault(region)) == TerritorialTitle.Homeland) continue;
                            var touchesPart = false;
                            foreach (var neighbor in TouchingSet(graph, region))
                                if (part.Contains(neighbor)) { touchesPart = true; break; }
                            if (!touchesPart || !Leaf(graph, chosen, anchors, region)) continue;
                            var w = Worth(region);
                            if (bestCutter < 0 || w < bestWorth) { bestWorth = w; bestCutter = region; }
                        }
                        if (bestCutter < 0) continue;
                        chosen.Remove(bestCutter);
                        changed = true;
                    }
                }
                ReleaseParts(beforeParts);
                ReleaseParts(afterParts);
            }

            // Within what the war score buys: the least worth of the regions at the edge goes first.
            while (affordable is not null && chosen.Count > 0 && !affordable())
            {
                var leaf = -1;
                var leafWorth = float.PositiveInfinity;
                foreach (var region in chosen)
                {
                    if (region == goal || ArmyHolds(region) || !Leaf(graph, chosen, anchors, region)) continue;
                    var w = Worth(region);
                    if (leaf < 0 || w < leafWorth) { leafWorth = w; leaf = region; }
                }
                if (leaf < 0)
                {
                    foreach (var region in chosen)
                    {
                        if (region == goal || ArmyHolds(region)) continue;
                        var w = Worth(region);
                        if (leaf < 0 || w < leafWorth) { leafWorth = w; leaf = region; }
                    }
                }
                if (leaf < 0)
                {
                    var anyHeld = false;
                    foreach (var region in chosen)
                        if (ArmyHolds(region)) { anyHeld = true; break; }
                    if (!anyHeld) chosen.Clear();
                    break;
                }
                chosen.Remove(leaf);
                changed = true;
            }

            if (!changed) break;
        }
        ReleaseParts(beforeParts);
        ReleaseParts(afterParts);
    }

    private static long PartPopulation(HashSet<int> part, Dictionary<int, ScenarioState> byIndex)
    {
        long pop = 0;
        foreach (var region in part)
            if (byIndex.TryGetValue(region, out var s)) pop += s.Population;
        return pop;
    }

    /// <summary>Whether giving this region back leaves the rest of what is taken still joined to the winner.</summary>
    private static bool Leaf(Regions graph, HashSet<int> chosen, HashSet<int> anchors, int region)
    {
        if (PeaceRulesHook.TryLeaf(r => Touching(graph, r), chosen, anchors, region, out var res))
            return res;
        var expected = chosen.Contains(region) ? chosen.Count - 1 : chosen.Count;
        if (expected <= 0) return true;
        var reached = _leafReachedScratch ??= new HashSet<int>();
        reached.Clear();
        var queue = _leafQueueScratch ??= new Queue<int>();
        queue.Clear();
        foreach (var start in chosen)
        {
            if (start != region && anchors.Contains(start) && reached.Add(start))
                queue.Enqueue(start);
        }
        while (queue.Count > 0)
        {
            foreach (var next in TouchingSet(graph, queue.Dequeue()))
            {
                if (next != region && chosen.Contains(next) && reached.Add(next))
                    queue.Enqueue(next);
            }
        }
        return reached.Count == expected;
    }

    /// <summary>The connected pieces a set of regions falls into, by land.</summary>
    private static void FillParts(Regions graph, HashSet<int> regions, List<HashSet<int>> parts)
    {
        ReleaseParts(parts);
        if (PeaceRulesHook.TryParts(r => Touching(graph, r), regions, out var partsArray))
        {
            for (var pi = 0; pi < partsArray.Length; pi++)
            {
                var src = partsArray[pi];
                var set = RentPartSet();
                for (var j = 0; j < src.Length; j++) set.Add(src[j]);
                parts.Add(set);
            }
            return;
        }
        var seen = _settlePartsSeenScratch ??= new HashSet<int>();
        seen.Clear();
        var queue = _settlePartsQueueScratch ??= new Queue<int>();
        queue.Clear();
        foreach (var start in regions)
        {
            if (!seen.Add(start)) continue;
            var part = RentPartSet();
            part.Add(start);
            queue.Enqueue(start);
            while (queue.Count > 0)
                foreach (var next in TouchingSet(graph, queue.Dequeue()))
                    if (regions.Contains(next) && seen.Add(next))
                    {
                        part.Add(next);
                        queue.Enqueue(next);
                    }
            parts.Add(part);
        }
    }

    /// <summary>
    /// The occupied line, drawn so it can be a border: only what touches the winner's own
    /// ground, plus a pore of the enemy shut inside that line, minus a one-province tooth.
    /// A pocket the army holds in the middle of the loser is not a country.
    /// </summary>
    public static List<int> Compact(Campaign campaign, Nation winner, IEnumerable<int> provinces, StrategicWar war) =>
        DrawLine(campaign, winner, provinces, war).Provinces;

    /// <summary>
    /// Why the border on the table looks like this, in one sentence, or empty when none of
    /// the three facts applies. The facts are the ones <see cref="Compact"/> already uses on
    /// the occupation the winner is standing on: occupation that still touches home, a pocket
    /// shut inside that line, a one-province tooth left off. Reading this does not move a
    /// province and does not sign the peace.
    /// </summary>
    public static string BorderWhy(Campaign campaign, Nation winner, IEnumerable<int> shown, StrategicWar war)
    {
        if (campaign is null || winner is null || shown is null) return "";
        var shownSet = new HashSet<int>(shown);
        if (shownSet.Count == 0) return "";
        var standing = Standing(campaign, winner);
        if (standing.Count == 0) return "";
        var line = DrawLine(campaign, winner, standing, war);
        foreach (var hole in line.Holes)
            if (shownSet.Contains(hole))
                return Loc.Get("A pocket shut inside the line is kept with this border.");
        foreach (var tooth in line.Teeth)
            if (!shownSet.Contains(tooth))
                return Loc.Get("A one-province tooth of the occupation was left off this border.");
        var keeps = false;
        foreach (var province in line.Kept)
            if (!line.Teeth.Contains(province) && shownSet.Contains(province)) { keeps = true; break; }
        if (keeps)
            foreach (var pocket in line.LeftOut)
                if (!shownSet.Contains(pocket))
                    return Loc.Get("This border keeps the occupation that touches home.");
        return "";
    }

    /// <summary>
    /// What this settlement does with the occupation, in one sentence, or empty when it
    /// does neither. The facts are the ones <see cref="Compact"/> already decides on the
    /// ground the holder is standing on. Occupied land that still touches home and is on
    /// the table stays with the holder. Otherwise a pocket beyond that line which the
    /// table does not keep goes back. A tooth the line already cut off is not a stay, and
    /// a pocket the table keeps is not a return. Reading this does not move a province
    /// and does not sign the peace.
    /// </summary>
    public static string TransferLine(Campaign campaign, Nation winner, IEnumerable<int> shown, StrategicWar war)
    {
        if (campaign is null || winner is null || shown is null) return "";
        var shownSet = new HashSet<int>(shown);
        if (shownSet.Count == 0) return "";
        var standing = Standing(campaign, winner);
        if (standing.Count == 0) return "";
        var line = DrawLine(campaign, winner, standing, war);
        foreach (var province in line.Kept)
            if (!line.Teeth.Contains(province) && shownSet.Contains(province))
                return Loc.Get("This occupied ground stays with the holder.");
        foreach (var pocket in line.LeftOut)
            if (!shownSet.Contains(pocket))
                return Loc.Get("The pocket beyond the line goes back.");
        return "";
    }

    [ThreadStatic] private static HashSet<int> _standingScratch;
    [ThreadStatic] private static HashSet<int> _lineWantedScratch;
    [ThreadStatic] private static HashSet<int> _lineBodyScratch;
    [ThreadStatic] private static Queue<int> _lineQueueScratch;
    [ThreadStatic] private static HashSet<ushort> _lineLosersScratch;
    [ThreadStatic] private static List<int> _linePoreScratch;
    [ThreadStatic] private static List<int> _lineNextPoreScratch;
    [ThreadStatic] private static HashSet<int> _linePoreSeenScratch;
    [ThreadStatic] private static List<int> _lineTeethScratch;
    [ThreadStatic] private static List<int> _settleProvincesListScratch;
    [ThreadStatic] private static HashSet<int> _settleProvincesLosersScratch;
    [ThreadStatic] private static HashSet<int> _settleProvincesChosenScratch;
    [ThreadStatic] private static HashSet<int> _settleProvincesDedupScratch;

    /// <summary>Enemy ground the winner is standing on. Home it already owns is not occupation.</summary>
    private static HashSet<int> Standing(Campaign campaign, Nation winner)
    {
        var standing = _standingScratch ??= new HashSet<int>();
        standing.Clear();
        var map = campaign?.ProvinceMap;
        var control = campaign?.ProvinceController;
        if (map is null || control is null || winner is null) return standing;
        var flag = (ushort)winner.MapIndex;
        var last = Math.Min(map.ProvinceCount, control.Length - 1);
        for (var province = 1; province <= last; province++)
        {
            if (control[province] != flag || map.IsWater(province) || map.ProvinceToNation[province] == flag) continue;
            standing.Add(province);
        }
        return standing;
    }

    private sealed class LineFacts
    {
        public List<int> Provinces = new();
        public readonly HashSet<int> Holes = new();
        public readonly HashSet<int> Teeth = new();
        public readonly HashSet<int> LeftOut = new();
        public readonly HashSet<int> Kept = new();
    }

    /// <summary>The same walk as <see cref="Compact"/>, keeping the three facts it already decides.</summary>
    private static LineFacts DrawLine(Campaign campaign, Nation winner, IEnumerable<int> provinces, StrategicWar war)
    {
        var facts = new LineFacts();
        var map = campaign?.ProvinceMap;
        var wanted = _lineWantedScratch ??= new HashSet<int>();
        wanted.Clear();
        if (provinces is not null)
            foreach (var p in provinces) wanted.Add(p);
        if (map is null || winner is null || wanted.Count == 0)
        {
            facts.Provinces = new List<int>(wanted.Count);
            foreach (var p in wanted) facts.Provinces.Add(p);
            return facts;
        }

        var pCount = map.ProvinceCount;
        var winnerMap = (ushort)winner.MapIndex;
        var ptn = map.ProvinceToNation;
        var landNeighbors = map.LandNeighbors;

        var body = _lineBodyScratch ??= new HashSet<int>();
        body.Clear();
        var queue = _lineQueueScratch ??= new Queue<int>();
        queue.Clear();
        foreach (var province in wanted)
        {
            if (province <= 0 || province > pCount) continue;
            var neighbors = landNeighbors[province];
            var touchesHome = false;
            for (var ni = 0; ni < neighbors.Length; ni++)
            {
                var next = neighbors[ni];
                if (next > 0 && next <= pCount && !map.IsWater(next) && ptn[next] == winnerMap)
                {
                    touchesHome = true;
                    break;
                }
            }
            if (touchesHome && body.Add(province)) queue.Enqueue(province);
        }
        while (queue.Count > 0)
        {
            var neighbors = landNeighbors[queue.Dequeue()];
            for (var ni = 0; ni < neighbors.Length; ni++)
            {
                var next = neighbors[ni];
                if (next > 0 && next <= pCount && !map.IsWater(next) && wanted.Contains(next) && body.Add(next))
                    queue.Enqueue(next);
            }
        }
        foreach (var province in body) facts.Kept.Add(province);
        foreach (var province in wanted)
            if (!body.Contains(province)) facts.LeftOut.Add(province);

        var losers = _lineLosersScratch ??= new HashSet<ushort>();
        losers.Clear();
        if (war is not null)
            foreach (var enemy in war.EnemiesOf(winner.Id))
                if (campaign.NationById(enemy) is { } nation) losers.Add((ushort)nation.MapIndex);
        if (losers.Count > 0)
        {
            var pores = _linePoreScratch ??= new List<int>();
            var nextPores = _lineNextPoreScratch ??= new List<int>();
            var poreSeen = _linePoreSeenScratch ??= new HashSet<int>();
            var holeLimit = Math.Max(1, campaign.Defines.PeaceCompactHoleProvinces);
            for (var pass = 0; pass < holeLimit; pass++)
            {
                pores.Clear();
                if (pass == 0)
                {
                    for (var province = 1; province <= pCount; province++)
                    {
                        if (map.IsWater(province) || !losers.Contains(ptn[province]) || body.Contains(province)) continue;
                        var neighbors = landNeighbors[province];
                        var hasLand = false;
                        var allInside = true;
                        for (var ni = 0; ni < neighbors.Length; ni++)
                        {
                            var next = neighbors[ni];
                            if (next <= 0 || next > pCount || map.IsWater(next)) continue;
                            hasLand = true;
                            if (ptn[next] != winnerMap && !body.Contains(next)) { allInside = false; break; }
                        }
                        if (hasLand && allInside) pores.Add(province);
                    }
                }
                else
                {
                    poreSeen.Clear();
                    for (var i = 0; i < nextPores.Count; i++)
                    {
                        var prevNeighbors = landNeighbors[nextPores[i]];
                        for (var j = 0; j < prevNeighbors.Length; j++)
                        {
                            var province = prevNeighbors[j];
                            if (province <= 0 || province > pCount || map.IsWater(province)) continue;
                            if (!poreSeen.Add(province) || !losers.Contains(ptn[province]) || body.Contains(province)) continue;
                            var neighbors = landNeighbors[province];
                            var hasLand = false;
                            var allInside = true;
                            for (var ni = 0; ni < neighbors.Length; ni++)
                            {
                                var next = neighbors[ni];
                                if (next <= 0 || next > pCount || map.IsWater(next)) continue;
                                hasLand = true;
                                if (ptn[next] != winnerMap && !body.Contains(next)) { allInside = false; break; }
                            }
                            if (hasLand && allInside) pores.Add(province);
                        }
                    }
                    if (pores.Count > 1) pores.Sort();
                }
                if (pores.Count == 0) break;
                nextPores.Clear();
                for (var pi = 0; pi < pores.Count; pi++)
                {
                    var province = pores[pi];
                    body.Add(province);
                    facts.Holes.Add(province);
                    nextPores.Add(province);
                }
            }
        }

        var goal = war?.ClaimedStateMapIndex ?? -1;
        if (body.Count > 1)
        {
            var teeth = _lineTeethScratch ??= new List<int>();
            teeth.Clear();
            foreach (var province in body)
            {
                if (goal >= 0 && map.ProvinceToState[province] == goal) continue;
                var insideCount = 0;
                var neighbors = landNeighbors[province];
                for (var ni = 0; ni < neighbors.Length; ni++)
                {
                    var next = neighbors[ni];
                    if (next > 0 && next <= pCount && !map.IsWater(next) &&
                        (ptn[next] == winnerMap || body.Contains(next)))
                    {
                        insideCount++;
                        if (insideCount >= 2) break;
                    }
                }
                if (insideCount < 2) teeth.Add(province);
            }
            for (var ti = 0; ti < teeth.Count; ti++)
            {
                var province = teeth[ti];
                body.Remove(province);
                facts.Teeth.Add(province);
            }
        }
        facts.Provinces = new List<int>(body.Count);
        foreach (var province in body) facts.Provinces.Add(province);
        return facts;
    }

    /// <summary>Settle a list of provinces a treaty would move (the enforced peace): whole regions in, a border out.</summary>
    public static List<int> SettleProvinces(Campaign campaign, StrategicWar war, IEnumerable<int> provinces, Nation winner)
    {
        var topology = campaign.ProvinceMap;
        var list = _settleProvincesListScratch ??= new List<int>();
        list.Clear();
        if (provinces is not null)
            foreach (var p in provinces) list.Add(p);
        if (topology is null || list.Count == 0 || winner is null) return new List<int>(list);
        // Named provinces stay named. A province peace still refuses a pocket that does not
        // touch home, a dot shut inside the new line, and a one-province tooth.
        if (!campaign.Defines.PeaceWholeRegions) return Compact(campaign, winner, list, war);
        var graph = Graph(campaign);
        var losers = _settleProvincesLosersScratch ??= new HashSet<int>();
        losers.Clear();
        foreach (var enemyId in war.EnemiesOf(winner.Id))
            if (campaign.NationById(enemyId) is { } nation) losers.Add(nation.MapIndex);
        var chosen = _settleProvincesChosenScratch ??= new HashSet<int>();
        chosen.Clear();
        for (var i = 0; i < list.Count; i++)
        {
            int region = topology.ProvinceToState[list[i]];
            if (region != 0) chosen.Add(region);
        }
        Settle(campaign, war, chosen, winner, losers);
        // The loser's ground and nobody else's. Rounding a region up could reach a province of
        // it that a third country owns - the winner's own ally, as often as not - and the list
        // of what was held used to wave that through as well.
        var dedup = _settleProvincesDedupScratch ??= new HashSet<int>();
        dedup.Clear();
        var settled = new List<int>();
        foreach (var region in chosen)
        {
            if (!graph.Provinces.TryGetValue(region, out var all)) continue;
            for (var pi = 0; pi < all.Count; pi++)
            {
                var province = all[pi];
                if (losers.Contains(topology.ProvinceToNation[province]) && dedup.Add(province))
                    settled.Add(province);
            }
        }
        return settled;
    }
}

public static class PeaceRulesHook
{
    public delegate bool LeafDelegate(Func<int, IEnumerable<int>> adjacency, ISet<int> chosen, ISet<int> anchors, int region);
    public delegate int[][] PartsDelegate(Func<int, IEnumerable<int>> adjacency, IEnumerable<int> nodes);

    /// <summary>The stalemate discount over an achievable war aim: five numbers in, one out.</summary>
    public delegate float StalemateDelegate(int durationDays, bool statusQuo, bool stalled,
        float weariness, float responderOccupied);

    private static LeafDelegate _leaf;
    private static PartsDelegate _parts;
    private static StalemateDelegate _stalemate;

    public static void Install(LeafDelegate leaf, PartsDelegate parts)
    {
        _leaf = leaf;
        _parts = parts;
    }

    /// <summary>
    /// Install the pure F# arithmetic for the stalemate discount. Kept apart from
    /// <see cref="Install"/> so the game still has a working verdict when only one of the two is
    /// wired: a missing hook falls back to the managed formula, never to zero.
    /// </summary>
    public static void InstallStalemate(StalemateDelegate stalemate) => _stalemate = stalemate;

    public static bool TryStalemate(int durationDays, bool statusQuo, bool stalled,
        float weariness, float responderOccupied, out float discount)
    {
        if (_stalemate is not null)
        {
            discount = _stalemate(durationDays, statusQuo, stalled, weariness, responderOccupied);
            return true;
        }
        discount = 0f;
        return false;
    }

    public static bool TryLeaf(Func<int, IEnumerable<int>> adjacency, ISet<int> chosen, ISet<int> anchors, int region, out bool result)
    {
        if (_leaf is not null)
        {
            result = _leaf(adjacency, chosen, anchors, region);
            return true;
        }
        result = false;
        return false;
    }

    public static bool TryParts(Func<int, IEnumerable<int>> adjacency, IEnumerable<int> nodes, out int[][] parts)
    {
        if (_parts is not null)
        {
            parts = _parts(adjacency, nodes);
            return true;
        }
        parts = null;
        return false;
    }
}
