using System;
using Arch.Core;
using Friflo.Engine.ECS;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.KernelBench;

/// <summary>A group's fields that the review only reads.</summary>
public struct PopFixed : Friflo.Engine.ECS.IComponent
{
    public int State, Group;
    public float SizeF, Anchor;
    public double SizeD, ClassFactor;
}

/// <summary>A group's fields that the review writes.</summary>
public struct PopMood : Friflo.Engine.ECS.IComponent
{
    public float Employment, Wealth, Living, Radicalism;
}

/// <summary>
/// (d) Friflo.Engine.ECS and (e) Arch holding the same groups: one entity per population group,
/// two components (what is read, what is written), entities created region by region in the
/// economy's order. The per-region prologue and epilogue are the shared HouseholdSoa code, so
/// what differs is only where the groups live and how they are walked.
///
/// Floating-point order: the region's labour and living sums are added in group order. An ECS
/// walks chunks; a region can straddle two chunks, and if the walk visited the later chunk
/// first that region's sums would be added in a different order - a different float, and a
/// desync in a lockstep game. The walk below is chunk-forward, index-forward, and asserts that
/// the group index only ever goes up, so a library that reordered entities would fail
/// loudly instead of being slightly wrong.
///
/// Serial only. Both libraries have parallel queries, but a parallel walk splits chunks, not
/// regions, so a region's sum would be split across threads; a deterministic parallel version
/// needs per-region chunk boundaries - the array layout above already has them for free.
/// </summary>
internal static class EcsVariants
{
    // ---------------------------------------------------------------- Friflo.Engine.ECS
    public sealed class FrifloWorld
    {
        public EntityStore Store;
        public ArchetypeQuery<PopFixed, PopMood> Query;
    }

    public static FrifloWorld BuildFriflo(HouseholdSoa d)
    {
        var w = new FrifloWorld { Store = new EntityStore() };
        for (var s = 0; s < d.StateCount; s++)
            for (var g = d.GroupStart[s]; g < d.GroupStart[s] + d.GroupLen[s]; g++)
                w.Store.CreateEntity(
                    new PopFixed { State = s, Group = g, SizeF = d.SizeF[g], Anchor = d.Anchor[g], SizeD = d.SizeD[g], ClassFactor = d.ClassFactor[g] },
                    new PopMood { Employment = d.Employment[g], Wealth = d.Wealth[g], Living = d.Living[g], Radicalism = d.Radicalism[g] });
        w.Query = w.Store.Query<PopFixed, PopMood>();
        return w;
    }

    public static void ResetFriflo(FrifloWorld w, HouseholdSoa source)
    {
        foreach (var (fixeds, moods, _) in w.Query.Chunks)
        {
            var f = fixeds.Span;
            var m = moods.Span;
            for (var i = 0; i < m.Length; i++)
                m[i] = new PopMood { Employment = source.Employment[f[i].Group], Wealth = source.Wealth[f[i].Group], Living = source.Living[f[i].Group], Radicalism = source.Radicalism[f[i].Group] };
        }
    }

    public static void RunFriflo(FrifloWorld w, HouseholdSoa d, double[] labourSum, double[] employmentSum, double[] livingSum)
    {
        // Pass 1: the labour sums (and employment, for a region that still needs it), in order.
        Array.Clear(labourSum); Array.Clear(employmentSum); Array.Clear(livingSum);
        var last = -1;
        foreach (var (fixeds, moods, _) in w.Query.Chunks)
        {
            var f = fixeds.Span;
            var m = moods.Span;
            for (var i = 0; i < f.Length; i++)
            {
                var s = f[i].State;
                if (f[i].Group <= last) throw new InvalidOperationException("Friflo walked groups out of order");
                last = f[i].Group;
                var term = f[i].SizeF * d.Share[s] * 1e-6 * f[i].ClassFactor;
                labourSum[s] += term;
                if (d.NonBuildingJobs[s] < 0) employmentSum[s] += term * m[i].Employment;
            }
        }
        var targets = Prologues(d, labourSum, employmentSum);
        // Pass 2: the groups move; the living sums, in order.
        foreach (var (fixeds, moods, _) in w.Query.Chunks)
        {
            var f = fixeds.Span;
            var m = moods.Span;
            for (var i = 0; i < f.Length; i++)
            {
                var s = f[i].State;
                if (d.Due[s] == 0) continue;
                livingSum[s] += f[i].SizeD * (double)Move(ref m[i], f[i].Anchor, in targets[s]);
            }
        }
        Epilogues(d, livingSum);
    }

    // ---------------------------------------------------------------- Arch
    public sealed class ArchWorld
    {
        public World World;
        public QueryDescription Query;
        public Chunk[] Ordered = Array.Empty<Chunk>();
        public string NaturalOrder = "";
    }

    /// <summary>
    /// Arch's query hands chunks over last-first (measured: the walk failed the order check on
    /// its first sample). Iterating that order would add a straddling region's groups out of
    /// order - a different float. So each run lists the chunks and sorts them by the first group
    /// they hold, then walks them forward. The sort is inside the timed run: a game whose
    /// archetypes change would have to redo it whenever they did.
    /// </summary>
    private static int OrderArch(ArchWorld w)
    {
        var count = 0;
        foreach (ref var chunk in w.World.Query(in w.Query))
        {
            if (count == w.Ordered.Length) Array.Resize(ref w.Ordered, Math.Max(16, count * 2));
            w.Ordered[count++] = chunk;
        }
        Array.Sort(w.Ordered, 0, count, ChunkByFirstGroup.Instance);
        return count;
    }

    private sealed class ChunkByFirstGroup : System.Collections.Generic.IComparer<Chunk>
    {
        public static readonly ChunkByFirstGroup Instance = new();
        public int Compare(Chunk a, Chunk b) => a.GetSpan<PopFixed>()[0].Group.CompareTo(b.GetSpan<PopFixed>()[0].Group);
    }

    public static ArchWorld BuildArch(HouseholdSoa d)
    {
        var w = new ArchWorld { World = World.Create(), Query = new QueryDescription().WithAll<PopFixed, PopMood>() };
        for (var s = 0; s < d.StateCount; s++)
            for (var g = d.GroupStart[s]; g < d.GroupStart[s] + d.GroupLen[s]; g++)
                w.World.Create(
                    new PopFixed { State = s, Group = g, SizeF = d.SizeF[g], Anchor = d.Anchor[g], SizeD = d.SizeD[g], ClassFactor = d.ClassFactor[g] },
                    new PopMood { Employment = d.Employment[g], Wealth = d.Wealth[g], Living = d.Living[g], Radicalism = d.Radicalism[g] });
        var firsts = new System.Collections.Generic.List<int>();
        foreach (ref var chunk in w.World.Query(in w.Query)) firsts.Add(chunk.GetSpan<PopFixed>()[0].Group);
        w.NaturalOrder = $"{firsts.Count} chunks, first groups in query order: " + string.Join(", ", firsts.GetRange(0, Math.Min(4, firsts.Count))) + " ...";
        return w;
    }

    public static void ResetArch(ArchWorld w, HouseholdSoa source)
    {
        foreach (ref var chunk in w.World.Query(in w.Query))
        {
            var m = chunk.GetSpan<PopMood>();
            var f = chunk.GetSpan<PopFixed>();
            for (var i = 0; i < chunk.Count; i++)
                m[i] = new PopMood { Employment = source.Employment[f[i].Group], Wealth = source.Wealth[f[i].Group], Living = source.Living[f[i].Group], Radicalism = source.Radicalism[f[i].Group] };
        }
    }

    public static void RunArch(ArchWorld w, HouseholdSoa d, double[] labourSum, double[] employmentSum, double[] livingSum)
    {
        Array.Clear(labourSum); Array.Clear(employmentSum); Array.Clear(livingSum);
        var chunks = OrderArch(w);
        var last = -1;
        for (var c = 0; c < chunks; c++)
        {
            ref var chunk = ref w.Ordered[c];
            var f = chunk.GetSpan<PopFixed>();
            var m = chunk.GetSpan<PopMood>();
            for (var i = 0; i < chunk.Count; i++)
            {
                var s = f[i].State;
                if (f[i].Group <= last) throw new InvalidOperationException("Arch walked groups out of order");
                last = f[i].Group;
                var term = f[i].SizeF * d.Share[s] * 1e-6 * f[i].ClassFactor;
                labourSum[s] += term;
                if (d.NonBuildingJobs[s] < 0) employmentSum[s] += term * m[i].Employment;
            }
        }
        var targets = Prologues(d, labourSum, employmentSum);
        for (var c = 0; c < chunks; c++)
        {
            ref var chunk = ref w.Ordered[c];
            var f = chunk.GetSpan<PopFixed>();
            var m = chunk.GetSpan<PopMood>();
            for (var i = 0; i < chunk.Count; i++)
            {
                var s = f[i].State;
                if (d.Due[s] == 0) continue;
                livingSum[s] += f[i].SizeD * (double)Move(ref m[i], f[i].Anchor, in targets[s]);
            }
        }
        Epilogues(d, livingSum);
    }

    /// <summary>Read the groups back out in walk order, for the checksum.</summary>
    public static void ExportArch(ArchWorld w, HouseholdSoa into)
    {
        foreach (ref var chunk in w.World.Query(in w.Query))
        {
            var m = chunk.GetSpan<PopMood>();
            var f = chunk.GetSpan<PopFixed>();
            for (var i = 0; i < chunk.Count; i++)
            { var g = f[i].Group; into.Employment[g] = m[i].Employment; into.Wealth[g] = m[i].Wealth; into.Living[g] = m[i].Living; into.Radicalism[g] = m[i].Radicalism; }
        }
    }

    public static void ExportFriflo(FrifloWorld w, HouseholdSoa into)
    {
        foreach (var (fixeds, moods, _) in w.Query.Chunks)
        {
            var m = moods.Span;
            var f = fixeds.Span;
            for (var i = 0; i < m.Length; i++)
            { var g = f[i].Group; into.Employment[g] = m[i].Employment; into.Wealth[g] = m[i].Wealth; into.Living[g] = m[i].Living; into.Radicalism[g] = m[i].Radicalism; }
        }
    }

    // ---------------------------------------------------------------- shared
    [ThreadStatic] private static HouseholdSoa.Targets[] _targets;

    private static HouseholdSoa.Targets[] Prologues(HouseholdSoa d, double[] labourSum, double[] employmentSum)
    {
        var targets = _targets is { } t && t.Length == d.StateCount ? t : _targets = new HouseholdSoa.Targets[d.StateCount];
        for (var s = 0; s < d.StateCount; s++)
        {
            if (d.Due[s] == 0) continue;
            var labour = d.GroupLen[s] == 0 ? d.PopulationF[s] * d.Share[s] * 1e-6f : (float)labourSum[s];
            var employment = d.NonBuildingJobs[s] < 0 ? (labour <= 0 ? 0 : (float)employmentSum[s] / labour) : 0f;
            targets[s] = d.Prologue(s, labour, employment);
        }
        return targets;
    }

    private static void Epilogues(HouseholdSoa d, double[] livingSum)
    {
        for (var s = 0; s < d.StateCount; s++)
            if (d.Due[s] != 0 && d.GroupLen[s] > 0) d.Epilogue(s, livingSum[s]);
    }

    private static float Move(ref PopMood p, float anchor, in HouseholdSoa.Targets t)
    {
        var e = M.Lerp(p.Employment, t.Target, t.EmploymentRate);
        p.Employment = e;
        var tw = Math.Clamp(anchor + (e - .9f) * .22f - t.Scarcity18 - t.Damage12, 0, 1);
        var w = M.Lerp(p.Wealth, tw, t.WealthRate);
        p.Wealth = w;
        var tl = Math.Clamp(t.NationLiving + (w - .5f) * .12f - t.Stress25, 0, 1);
        var l = M.Lerp(p.Living, tl, .04f);
        p.Living = l;
        p.Radicalism = Math.Clamp(M.Lerp(p.Radicalism, t.RadicalismTarget, .025f), 0, 1);
        return l;
    }
}
