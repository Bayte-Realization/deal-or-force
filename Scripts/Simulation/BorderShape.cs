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
    /// <summary>Tension a week between two countries per unit of their border grievance, up to the cap.</summary>
    public float BorderGrievanceTension { get; set; } = 0.6f;
    public float BorderGrievanceTensionCap { get; set; } = 45f;
    /// <summary>A region counts as wrapped by a neighbour when at least this share of its land border is that neighbour's.</summary>
    public float BorderWrapShare { get; set; } = 0.7f;
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
    private static readonly Dictionary<int, int> OwnerOf = new();

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
        Grievances.Clear(); OwnerOf.Clear();
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
