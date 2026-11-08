using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// What an awkward border is worth to the AI's judgement of a war with the neighbour on the
    /// other side of it (BorderShape.Grievance, 0..1). Both sides count it: the one whose land is
    /// a pocket wants a way through, the one around it wants the pocket.
    /// </summary>
    public float AiBorderAppetite { get; set; } = 0.35f;
    /// <summary>What a rump of a country we took ground from is worth to the AI's war judgement (AiJudgement "finish the rump").</summary>
    public float AiRumpAppetite { get; set; } = 0.45f;
    /// <summary>Tension a week between two countries per unit of their border grievance, up to the cap.</summary>
    public float BorderGrievanceTension { get; set; } = 0.6f;
    public float BorderGrievanceTensionCap { get; set; } = 45f;
    /// <summary>A region counts as wrapped by a neighbour when at least this share of its land border is that neighbour's.</summary>
    public float BorderWrapShare { get; set; } = 0.7f;
    /// <summary>
    /// A border exactly as the January 2024 map drew it is nobody's grievance: Alaska inside
    /// Canada's land border, Sabah and Sarawak against Kalimantan, Kaliningrad - states have lived
    /// with these shapes for generations, and the coarse region map makes many more of them than
    /// the real world has. Only a shape the campaign itself made - a war's new line, a secession,
    /// a sale - is resented. Off, every wrapped region counts, as before.
    /// </summary>
    public bool BorderShapeIgnoresDrawnBorders { get; set; } = true;
}

/// <summary>
/// Borders a government cannot live with: a region of the neighbour that juts into ours or sits
/// inside it (an enclave, a salient), and a region of ours that the neighbour has cut off from
/// the rest of the country or squeezed to a corridor. Real states fight over less - Nagorno-
/// Karabakh, the Fergana valley's enclaves, the Suwałki gap - and a map left with a corridor deep
/// into a neighbour after a war is a war waiting for its next round. Read from ownership only
/// (the map's administrative shape), counted for every pair the same way.
/// </summary>
public static class BorderShape
{
    private static Campaign _campaign;
    private static int _revision = -1;
    private static readonly Dictionary<long, float> Grievances = new();
    // The same sums before the cap at 1, so a hypothetical change can be added to them exactly.
    private static readonly Dictionary<long, float> RawGrievances = new();
    private static readonly Dictionary<int, int> OwnerOf = new();
    // Each region's provinces, and whether a region was a given country's on the 2024 map.
    private static readonly Dictionary<int, List<int>> ProvincesOf = new();
    private static readonly Dictionary<long, bool> DrawnFor = new();

    /// <summary>How much this country resents the shape of its border with that one, 0..1.</summary>
    public static float Grievance(Campaign c, Nation nation, Nation other)
    {
        if (c?.ProvinceMap is null || nation is null || other is null || nation.Id == other.Id) return 0f;
        Ensure(c);
        return Grievances.GetValueOrDefault(Key(nation.MapIndex, other.MapIndex));
    }

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    private static void Ensure(Campaign c)
    {
        if (ReferenceEquals(_campaign, c) && _revision == c.MapRevision) return;
        _campaign = c; _revision = c.MapRevision;
        Grievances.Clear(); RawGrievances.Clear(); OwnerOf.Clear(); ProvincesOf.Clear(); DrawnFor.Clear();
        var map = c.ProvinceMap;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            int region = map.ProvinceToState[p];
            if (region == 0) continue;
            if (!ProvincesOf.TryGetValue(region, out var list)) ProvincesOf[region] = list = new List<int>();
            list.Add(p);
        }
        foreach (var state in c.States)
            if (state.NationMapIndex > 0 && !state.Unclaimed && !LakeRegions.IsWaterState(state))
                OwnerOf[state.MapIndex] = state.NationMapIndex;
        var wrap = M.Clamp(c.Defines.BorderWrapShare, 0.5f, 1f);
        var lengths = new Dictionary<int, int>();
        foreach (var (region, owner) in OwnerOf)
        {
            // The region's land border, by owner.
            lengths.Clear();
            var total = 0; var own = 0;
            foreach (var (next, edges) in TerritorialCoherence.RegionBorders(c, region))
            {
                if (!OwnerOf.TryGetValue(next, out var by)) continue;   // sea, lakes, nobody's
                total += edges;
                if (by == owner) own += edges;
                else lengths[by] = lengths.GetValueOrDefault(by) + edges;
            }
            if (total <= 0) continue;
            foreach (var (neighbour, edges) in lengths)
            {
                var share = edges / (float)total;
                if (share < wrap) continue;
                if (AsDrawn(c, region, owner, neighbour, r => OwnerOf.GetValueOrDefault(r))) continue;
                // Wrapped by the neighbour: the neighbour wants it, and if it hangs on to the rest
                // of its own country by a thread (or not at all) its owner wants a way through.
                Add(neighbour, owner, 1f / 3f);
                var thread = own / (float)total;
                if (thread < 0.2f) Add(owner, neighbour, thread <= 0f ? 1f / 3f : 1f / 6f);
            }
        }
    }

    private static void Add(int nationMapIndex, int otherMapIndex, float amount)
    {
        var key = Key(nationMapIndex, otherMapIndex);
        Grievances[key] = MathF.Min(1f, Grievances.GetValueOrDefault(key) + amount);
        RawGrievances[key] = RawGrievances.GetValueOrDefault(key) + amount;
    }

    /// <summary>
    /// What one region adds to the grievance of <paramref name="nation"/> against <paramref name="other"/>
    /// (map indices), with ownership read through <paramref name="ownerOf"/> - today's map or a
    /// map with a few regions handed over. Exactly the two rules of <see cref="Ensure"/>: a region of
    /// the other's wrapped by ours, a region of ours wrapped by theirs and cut off from the rest.
    /// </summary>
    /// <summary>
    /// Whether a region was its owner's on the 2024 map, by its provinces (three in five), asked of
    /// the bookmark file the map was drawn with and kept until the map changes.
    /// </summary>
    private static bool Drawn(Campaign c, int region, int ownerMapIndex)
    {
        var key = Key(region, ownerMapIndex);
        if (DrawnFor.TryGetValue(key, out var known)) return known;
        var drawn = false;
        if (c.NationsByMapIndex().TryGetValue((ushort)ownerMapIndex, out var owner) && ProvincesOf.TryGetValue(region, out var provinces) && provinces.Count > 0)
        {
            var authored = ProvinceOwnership.ProvinceSetOf(owner.Tag);
            var held = 0;
            foreach (var province in provinces) if (authored.Contains(province)) held++;
            drawn = held * 5 >= provinces.Count * 3;
        }
        DrawnFor[key] = drawn;
        return drawn;
    }

    /// <summary>
    /// Whether a region wrapped by a neighbour is wrapped exactly as the 2024 map drew it: the
    /// region was its owner's then, and every neighbouring region the wrapper now holds was the
    /// wrapper's then. Such a shape is how the two states were drawn, not a wound.
    /// </summary>
    private static bool AsDrawn(Campaign c, int region, int owner, int wrapper, Func<int, int> ownerOf)
    {
        if (!c.Defines.BorderShapeIgnoresDrawnBorders || !Drawn(c, region, owner)) return false;
        foreach (var next in TerritorialCoherence.RegionBorders(c, region).Keys)
            if (ownerOf(next) == wrapper && !Drawn(c, next, wrapper)) return false;
        return true;
    }

    private static float Contribution(Campaign c, int region, int nation, int other, Func<int, int> ownerOf, float wrap)
    {
        var owner = ownerOf(region);
        if (owner != nation && owner != other) return 0f;
        var total = 0; var own = 0; var byOther = 0; var byNation = 0;
        foreach (var (next, edges) in TerritorialCoherence.RegionBorders(c, region))
        {
            var by = ownerOf(next);
            if (by <= 0) continue;
            total += edges;
            if (by == owner) own += edges;
            else if (by == nation) byNation += edges;
            else if (by == other) byOther += edges;
        }
        if (total <= 0) return 0f;
        if (owner == other) return byNation / (float)total >= wrap && !AsDrawn(c, region, owner, nation, ownerOf) ? 1f / 3f : 0f;
        if (byOther / (float)total < wrap || AsDrawn(c, region, owner, other, ownerOf)) return 0f;
        var thread = own / (float)total;
        return thread >= 0.2f ? 0f : thread <= 0f ? 1f / 3f : 1f / 6f;
    }

    /// <summary>
    /// How much of its grievance against <paramref name="other"/> a country would be rid of if it
    /// gained <paramref name="gain"/> from it and gave it <paramref name="yield"/>: grievance now
    /// minus grievance after, -1..1. Negative when a package makes the border worse - a new pocket,
    /// a new salient. Only the regions handed over and their neighbours change shape, so only they
    /// are weighed again.
    /// </summary>
    public static float Relief(Campaign c, Nation nation, Nation other, IEnumerable<int> gain, IEnumerable<int> yield)
    {
        if (c?.ProvinceMap is null || nation is null || other is null || nation.Id == other.Id) return 0f;
        Ensure(c);
        var moved = new Dictionary<int, int>();
        foreach (var id in gain ?? Array.Empty<int>())
            if (OwnerOf.GetValueOrDefault(id) == other.MapIndex) moved[id] = nation.MapIndex;
        foreach (var id in yield ?? Array.Empty<int>())
            if (OwnerOf.GetValueOrDefault(id) == nation.MapIndex) moved[id] = other.MapIndex;
        if (moved.Count == 0) return 0f;
        var affected = new HashSet<int>(moved.Keys);
        foreach (var id in moved.Keys)
            foreach (var next in TerritorialCoherence.RegionBorders(c, id).Keys) affected.Add(next);
        var wrap = M.Clamp(c.Defines.BorderWrapShare, 0.5f, 1f);
        int Now(int r) => OwnerOf.GetValueOrDefault(r);
        int After(int r) => moved.TryGetValue(r, out var to) ? to : OwnerOf.GetValueOrDefault(r);
        var before = 0f; var after = 0f;
        foreach (var r in affected)
        {
            before += Contribution(c, r, nation.MapIndex, other.MapIndex, Now, wrap);
            after += Contribution(c, r, nation.MapIndex, other.MapIndex, After, wrap);
        }
        var raw = RawGrievances.GetValueOrDefault(Key(nation.MapIndex, other.MapIndex));
        return MathF.Min(1f, raw) - M.Clamp(raw - before + after, 0f, 1f);
    }

    /// <summary>
    /// The regions behind a grievance: <paramref name="wanted"/>, the other's regions wrapped by
    /// this country (the enclave, the salient it wants), and <paramref name="pockets"/>, this
    /// country's own regions wrapped by the other and cut off from the rest (the piece it cannot
    /// hold). Both ordered by region id.
    /// </summary>
    public static void Awkward(Campaign c, Nation nation, Nation other, List<int> wanted, List<int> pockets)
    {
        wanted.Clear(); pockets.Clear();
        if (c?.ProvinceMap is null || nation is null || other is null || nation.Id == other.Id) return;
        Ensure(c);
        var wrap = M.Clamp(c.Defines.BorderWrapShare, 0.5f, 1f);
        int Now(int r) => OwnerOf.GetValueOrDefault(r);
        foreach (var (region, owner) in OwnerOf)
        {
            if (owner != nation.MapIndex && owner != other.MapIndex) continue;
            if (Contribution(c, region, nation.MapIndex, other.MapIndex, Now, wrap) <= 0f) continue;
            (owner == other.MapIndex ? wanted : pockets).Add(region);
        }
        wanted.Sort(); pockets.Sort();
    }

    /// <summary>Whether a region is a pocket: wrapped by one neighbour and all but cut off from its owner's other land.</summary>
    public static bool IsCutOffPocket(Campaign c, ScenarioState state)
    {
        if (c?.ProvinceMap is null || state is null) return false;
        Ensure(c);
        if (!OwnerOf.TryGetValue(state.MapIndex, out var owner)) return false;
        var wrap = M.Clamp(c.Defines.BorderWrapShare, 0.5f, 1f);
        var total = 0; var own = 0;
        var by = new Dictionary<int, int>();
        foreach (var (next, edges) in TerritorialCoherence.RegionBorders(c, state.MapIndex))
        {
            if (!OwnerOf.TryGetValue(next, out var holder)) continue;
            total += edges;
            if (holder == owner) own += edges; else by[holder] = by.GetValueOrDefault(holder) + edges;
        }
        if (total <= 0 || own / (float)total >= 0.2f) return false;
        foreach (var (holder, edges) in by)
            if (edges / (float)total >= wrap && !AsDrawn(c, state.MapIndex, owner, holder, r => OwnerOf.GetValueOrDefault(r))) return true;
        return false;
    }

    /// <summary>Every pair with a grievance, as (nation map index, other map index, grievance) - for benches and readers.</summary>
    public static List<(int Nation, int Other, float Grievance)> Pairs(Campaign c)
    {
        var result = new List<(int, int, float)>();
        if (c?.ProvinceMap is null) return result;
        Ensure(c);
        foreach (var (key, grievance) in Grievances)
            if (grievance > 0f) result.Add(((int)(key >> 32), (int)(key & 0xFFFFFFFF), grievance));
        result.Sort((a, b) => b.Item3 != a.Item3 ? b.Item3.CompareTo(a.Item3) : a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
        return result;
    }

    /// <summary>The countries this one has a border grievance against, worst first.</summary>
    public static List<(int OtherMapIndex, float Grievance)> Against(Campaign c, Nation nation)
    {
        var result = new List<(int, float)>();
        if (c?.ProvinceMap is null || nation is null) return result;
        Ensure(c);
        foreach (var (key, grievance) in Grievances)
            if (grievance > 0f && (int)(key >> 32) == nation.MapIndex) result.Add(((int)(key & 0xFFFFFFFF), grievance));
        result.Sort((a, b) => b.Item2 != a.Item2 ? b.Item2.CompareTo(a.Item2) : a.Item1.CompareTo(b.Item1));
        return result;
    }

    /// <summary>Weekly: a border nobody can live with keeps the two governments on edge.</summary>
    public static void Tick(Campaign c)
    {
        if (!c.StepOpensNewDay || c.ElapsedDays % 7 != 3 || c.Defines.BorderGrievanceTension <= 0f || c.ProvinceMap is null) return;
        Ensure(c);
        var byIndex = c.NationsByMapIndex();
        foreach (var (key, grievance) in Grievances)
        {
            if (grievance <= 0f) continue;
            if (!byIndex.TryGetValue((ushort)(key >> 32), out var nation) || !byIndex.TryGetValue((ushort)(key & 0xFFFFFFFF), out var other)) continue;
            if (nation.Movement is not null || other.Movement is not null) continue;
            var relation = CampaignDiplomacy.Get(c, nation.Id, other.Id);
            if (relation is null || relation.Tension >= c.Defines.BorderGrievanceTensionCap) continue;
            relation.Tension = MathF.Min(c.Defines.BorderGrievanceTensionCap, relation.Tension + grievance * c.Defines.BorderGrievanceTension);
        }
    }
}
