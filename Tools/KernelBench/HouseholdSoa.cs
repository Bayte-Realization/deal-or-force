using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.KernelBench;

/// <summary>One region the week's review visits, in the order the economy visits it.</summary>
internal readonly record struct ReviewItem(Nation Nation, ScenarioState State, float Annual);

/// <summary>
/// The household review's data as plain arrays: one array per field, groups of a region stored
/// next to each other, regions in the economy's visiting order. Nothing here is a game type -
/// that is the point of the layout: the loop reads 40 bytes per group instead of chasing a
/// reference to a 72-byte object that also carries three strings.
/// </summary>
internal sealed class HouseholdSoa
{
    public int StateCount, GroupCount;

    // Per region, read.
    public int[] GroupStart, GroupLen;
    public byte[] Due;
    public float[] Share, BuildingJobs, WarDamage, Annual, Food, Energy, Medicine, NationLiving, StateRadicalism, PopulationF;
    public double[] PopulationD;
    // Per region, written.
    public float[] NonBuildingJobs, StateLiving, EmploymentTarget, BasicNeeds, Stress;
    // Per group, read. Size is kept as float and as double because the game's two sums use the
    // two conversions ((float)Size in the labour sum, (double)Size in the living sum); computing
    // them once here is exact and keeps a long-to-float conversion out of the vector loop.
    public float[] SizeF, Anchor;
    public double[] SizeD, ClassFactor;
    // Per group, written.
    public float[] Employment, Wealth, Living, Radicalism;

    // Constants of the week.
    public float Interval, Tolerance, EmploymentRate, WealthRate;

    public static HouseholdSoa Allocate(int states, int groups)
    {
        var d = new HouseholdSoa { StateCount = states, GroupCount = groups };
        d.GroupStart = new int[states]; d.GroupLen = new int[states]; d.Due = new byte[states];
        d.Share = new float[states]; d.BuildingJobs = new float[states]; d.WarDamage = new float[states];
        d.Annual = new float[states]; d.Food = new float[states]; d.Energy = new float[states];
        d.Medicine = new float[states]; d.NationLiving = new float[states]; d.StateRadicalism = new float[states];
        d.PopulationF = new float[states]; d.PopulationD = new double[states];
        d.NonBuildingJobs = new float[states]; d.StateLiving = new float[states]; d.EmploymentTarget = new float[states];
        d.BasicNeeds = new float[states]; d.Stress = new float[states];
        d.SizeF = new float[groups]; d.Anchor = new float[groups]; d.SizeD = new double[groups]; d.ClassFactor = new double[groups];
        d.Employment = new float[groups]; d.Wealth = new float[groups]; d.Living = new float[groups]; d.Radicalism = new float[groups];
        return d;
    }

    /// <summary>
    /// The per-region inputs, gathered from the game's objects: what a game that kept its groups
    /// in arrays would still have to gather every week (buildings, market, war damage change).
    /// </summary>
    public void PackStates(Campaign c, List<ReviewItem> items)
    {
        var defines = c.Defines;
        Interval = Math.Clamp(defines.EconomyTickDays, 1, 365) / 365f;
        Tolerance = defines.ScarcityTolerance;
        EmploymentRate = Math.Clamp(defines.HouseholdEmploymentApproach, 0, 1);
        WealthRate = Math.Clamp(defines.HouseholdWealthApproach, 0, 1);
        var start = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var (n, s, annual) = items[i];
            var count = s.PopulationGroups.Count;
            GroupStart[i] = start; GroupLen[i] = count; start += count;
            Due[i] = (byte)(defines.HouseholdDynamicsEnabled && s.LastHouseholdReviewDay != c.ElapsedDays && !s.Unclaimed && s.Population > 0 ? 1 : 0);
            Share[i] = DemographicCycle.WorkingShare(s);
            BuildingJobs[i] = MarketSystem.JobsDemanded(s);
            WarDamage[i] = s.WarDamage;
            Annual[i] = annual;
            Food[i] = GameKernel.Supply(n, Commodity.Food);
            Energy[i] = GameKernel.Supply(n, Commodity.Energy);
            Medicine[i] = GameKernel.Supply(n, Commodity.Pharmaceuticals);
            NationLiving[i] = n.StandardOfLiving;
            StateRadicalism[i] = s.Radicalism;
            PopulationF[i] = s.Population;
            PopulationD[i] = s.Population;
            NonBuildingJobs[i] = s.NonBuildingJobs;
            StateLiving[i] = s.StandardOfLiving;
            EmploymentTarget[i] = s.EmploymentTarget;
            BasicNeeds[i] = s.BasicNeedsCoverage;
            Stress[i] = s.LivelihoodStress;
        }
    }

    /// <summary>
    /// The per-group fields, copied out of the game's PopulationGroup objects. The profession's
    /// wealth anchor is a string-keyed dictionary lookup per group in the game's loop; a game
    /// that stored groups as arrays would store a profession index instead, so it is resolved
    /// here once and counted as packing, not as the kernel.
    /// </summary>
    public void PackGroups(List<ReviewItem> items)
    {
        var g = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var groups = items[i].State.PopulationGroups;
            for (var k = 0; k < groups.Count; k++, g++)
            {
                var group = groups[k];
                SizeF[g] = group.Size;
                SizeD[g] = group.Size;
                ClassFactor[g] = group.Class == SocialClass.Upper ? .15 : 1;
                Anchor[g] = DemographyCatalog.Profession(group.Profession)?.Wealth ??
                            (group.Class == SocialClass.Upper ? .9f : group.Class == SocialClass.Middle ? .6f : .35f);
                Employment[g] = group.Employment;
                Wealth[g] = group.Wealth;
                Living[g] = group.StandardOfLiving;
                Radicalism[g] = group.Radicalism;
            }
        }
    }

    /// <summary>The results back into the game's objects - the other half of the bridge.</summary>
    public void Unpack(List<ReviewItem> items)
    {
        var g = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var s = items[i].State;
            var groups = s.PopulationGroups;
            for (var k = 0; k < groups.Count; k++, g++)
            {
                var group = groups[k];
                group.Employment = Employment[g];
                group.Wealth = Wealth[g];
                group.StandardOfLiving = Living[g];
                group.Radicalism = Radicalism[g];
            }
            s.NonBuildingJobs = NonBuildingJobs[i];
            s.StandardOfLiving = StateLiving[i];
            s.EmploymentTarget = EmploymentTarget[i];
            s.BasicNeedsCoverage = BasicNeeds[i];
            s.LivelihoodStress = Stress[i];
        }
    }

    public void CopyMutableFrom(HouseholdSoa o)
    {
        Array.Copy(o.Employment, Employment, GroupCount); Array.Copy(o.Wealth, Wealth, GroupCount);
        Array.Copy(o.Living, Living, GroupCount); Array.Copy(o.Radicalism, Radicalism, GroupCount);
        Array.Copy(o.NonBuildingJobs, NonBuildingJobs, StateCount); Array.Copy(o.StateLiving, StateLiving, StateCount);
        Array.Copy(o.EmploymentTarget, EmploymentTarget, StateCount); Array.Copy(o.BasicNeeds, BasicNeeds, StateCount);
        Array.Copy(o.Stress, Stress, StateCount);
    }

    public HouseholdSoa Clone()
    {
        var d = Allocate(StateCount, GroupCount);
        foreach (var f in typeof(HouseholdSoa).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (f.FieldType.IsArray) f.SetValue(d, ((Array)f.GetValue(this)).Clone());
            else f.SetValue(d, f.GetValue(this));
        }
        return d;
    }

    /// <summary>FNV-1a over the bit patterns of every written field: equal bytes, equal hash.</summary>
    public ulong Checksum()
    {
        var h = 14695981039346656037UL;
        void Mix(ReadOnlySpan<float> v)
        {
            foreach (var x in v)
            {
                var bits = BitConverter.SingleToUInt32Bits(x);
                for (var b = 0; b < 4; b++) { h ^= (bits >> (8 * b)) & 0xFF; h *= 1099511628211UL; }
            }
        }
        Mix(Employment); Mix(Wealth); Mix(Living); Mix(Radicalism);
        Mix(NonBuildingJobs); Mix(StateLiving); Mix(EmploymentTarget); Mix(BasicNeeds); Mix(Stress);
        return h;
    }

    // ----------------------------------------------------------------------------------------
    // The region's scalar prologue and epilogue, shared by every C# array variant and by the
    // two ECS variants. Same expressions as HouseholdEconomy.Review, same Math.Clamp/Math.Max.
    // ----------------------------------------------------------------------------------------

    /// <summary>HouseholdEconomy.Labour over a run of groups: in order, in double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Labour(int s, int start, int len)
    {
        var share = Share[s];
        if (len == 0) return PopulationF[s] * share * 1e-6f;
        var sum = 0.0;
        for (var g = start; g < start + len; g++) sum += SizeF[g] * share * 1e-6 * ClassFactor[g];
        return (float)sum;
    }

    /// <summary>HouseholdEconomy.Employment, read before any group has moved.</summary>
    public float EmploymentOf(int s, int start, int len, float labour)
    {
        if (labour <= 0) return 0;
        var share = Share[s];
        var sum = 0.0;
        for (var g = start; g < start + len; g++) sum += SizeF[g] * share * 1e-6 * ClassFactor[g] * Employment[g];
        return (float)sum / labour;
    }

    /// <summary>What a region's groups are pulled towards this week.</summary>
    public struct Targets
    {
        public float Target, EmploymentRate, WealthRate, Scarcity18, Damage12, NationLiving, Stress25, RadicalismTarget;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Targets Prologue(int s, float labour, float employmentIfNeeded)
    {
        var buildingJobs = BuildingJobs[s];
        var nbj = NonBuildingJobs[s];
        if (nbj < 0) nbj = Math.Max(0, labour * employmentIfNeeded - buildingJobs);
        var damage = Math.Clamp(WarDamage[s], 0, 1);
        nbj = Math.Max(0, nbj * (1 + Math.Clamp(Annual[s], -.25f, .15f) * Interval));
        NonBuildingJobs[s] = nbj;
        var target = labour <= 0 ? 0 : Math.Clamp((nbj + buildingJobs) * (1 - damage * .5f) / labour, 0, 1);
        EmploymentTarget[s] = target;
        var coverage = Food[s] * .6f + Energy[s] * .25f + Medicine[s] * .15f;
        BasicNeeds[s] = coverage;
        var scarcity = Math.Max(0, 1 - coverage - Tolerance);
        var unemployment = Math.Max(0, .9f - target);
        var stress = Math.Clamp(scarcity * .65f + unemployment * .25f + damage * .25f, 0, 1);
        Stress[s] = stress;
        return new Targets
        {
            Target = target,
            // M.Lerp clamps its weight; the rates are clamped once, the same value results.
            EmploymentRate = M.Clamp01(EmploymentRate),
            WealthRate = M.Clamp01(WealthRate),
            Scarcity18 = scarcity * .18f,
            Damage12 = damage * .12f,
            NationLiving = NationLiving[s],
            Stress25 = stress * .25f,
            RadicalismTarget = StateRadicalism[s] + stress * .2f,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Epilogue(int s, double living)
    {
        StateLiving[s] = M.Lerp(StateLiving[s], (float)(living / PopulationD[s]), .08f);
    }

    // ----------------------------------------------------------------------------------------
    // (b) plain structure of arrays, scalar.
    // ----------------------------------------------------------------------------------------

    public void ReviewStateScalar(int s)
    {
        if (Due[s] == 0) return;
        var start = GroupStart[s];
        var len = GroupLen[s];
        var labour = Labour(s, start, len);
        var emp = NonBuildingJobs[s] < 0 ? EmploymentOf(s, start, len, labour) : 0f;
        var t = Prologue(s, labour, emp);
        var e = Employment.AsSpan(start, len);
        var w = Wealth.AsSpan(start, len);
        var l = Living.AsSpan(start, len);
        var r = Radicalism.AsSpan(start, len);
        var a = Anchor.AsSpan(start, len);
        var sd = SizeD.AsSpan(start, len);
        var living = 0.0;
        for (var i = 0; i < e.Length; i++)
        {
            var ei = M.Lerp(e[i], t.Target, t.EmploymentRate);
            e[i] = ei;
            var tw = Math.Clamp(a[i] + (ei - .9f) * .22f - t.Scarcity18 - t.Damage12, 0, 1);
            var wi = M.Lerp(w[i], tw, t.WealthRate);
            w[i] = wi;
            var tl = Math.Clamp(t.NationLiving + (wi - .5f) * .12f - t.Stress25, 0, 1);
            var li = M.Lerp(l[i], tl, .04f);
            l[i] = li;
            r[i] = Math.Clamp(M.Lerp(r[i], t.RadicalismTarget, .025f), 0, 1);
            living += sd[i] * (double)li;
        }
        if (len > 0) Epilogue(s, living);
    }

    // ----------------------------------------------------------------------------------------
    // (c) the same with Vector256 (8 floats at a time), and a scalar tail.
    //
    // Bit-identity rules: Multiply and Add stay separate (never FusedMultiplyAdd); clamp is two
    // ConditionalSelects on LessThan/GreaterThan, which is exactly Math.Clamp including NaN and
    // -0 (Vector256.Min/Max are not: MINPS returns the second operand for NaN and for +-0);
    // the two double sums stay sequential scalar loops in group order.
    // ----------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Clamp01(Vector256<float> v)
    {
        var zero = Vector256<float>.Zero;
        var one = Vector256.Create(1f);
        return Vector256.ConditionalSelect(Vector256.LessThan(v, zero), zero,
            Vector256.ConditionalSelect(Vector256.GreaterThan(v, one), one, v));
    }

    public void ReviewStateSimd(int s)
    {
        if (Due[s] == 0) return;
        var start = GroupStart[s];
        var len = GroupLen[s];
        var labour = Labour(s, start, len);
        var emp = NonBuildingJobs[s] < 0 ? EmploymentOf(s, start, len, labour) : 0f;
        var t = Prologue(s, labour, emp);
        var e = Employment.AsSpan(start, len);
        var w = Wealth.AsSpan(start, len);
        var l = Living.AsSpan(start, len);
        var r = Radicalism.AsSpan(start, len);
        var a = Anchor.AsSpan(start, len);
        var i = 0;
        if (Vector256.IsHardwareAccelerated && len >= 8)
        {
            var target = Vector256.Create(t.Target);
            var er = Vector256.Create(t.EmploymentRate);
            var wr = Vector256.Create(t.WealthRate);
            var nine = Vector256.Create(.9f);
            var c22 = Vector256.Create(.22f);
            var sc18 = Vector256.Create(t.Scarcity18);
            var dm12 = Vector256.Create(t.Damage12);
            var nl = Vector256.Create(t.NationLiving);
            var half = Vector256.Create(.5f);
            var c12 = Vector256.Create(.12f);
            var st25 = Vector256.Create(t.Stress25);
            var c04 = Vector256.Create(.04f);
            var radT = Vector256.Create(t.RadicalismTarget);
            var c025 = Vector256.Create(.025f);
            for (; i <= len - 8; i += 8)
            {
                var ev = Vector256.Create<float>(e.Slice(i, 8));
                ev = ev + (target - ev) * er;
                ev.CopyTo(e.Slice(i, 8));
                var tw = Clamp01(Vector256.Create<float>(a.Slice(i, 8)) + (ev - nine) * c22 - sc18 - dm12);
                var wv = Vector256.Create<float>(w.Slice(i, 8));
                wv = wv + (tw - wv) * wr;
                wv.CopyTo(w.Slice(i, 8));
                var tl = Clamp01(nl + (wv - half) * c12 - st25);
                var lv = Vector256.Create<float>(l.Slice(i, 8));
                lv = lv + (tl - lv) * c04;
                lv.CopyTo(l.Slice(i, 8));
                var rv = Vector256.Create<float>(r.Slice(i, 8));
                Clamp01(rv + (radT - rv) * c025).CopyTo(r.Slice(i, 8));
            }
        }
        for (; i < len; i++)
        {
            var ei = M.Lerp(e[i], t.Target, t.EmploymentRate);
            e[i] = ei;
            var tw = Math.Clamp(a[i] + (ei - .9f) * .22f - t.Scarcity18 - t.Damage12, 0, 1);
            var wi = M.Lerp(w[i], tw, t.WealthRate);
            w[i] = wi;
            var tl = Math.Clamp(t.NationLiving + (wi - .5f) * .12f - t.Stress25, 0, 1);
            l[i] = M.Lerp(l[i], tl, .04f);
            r[i] = Math.Clamp(M.Lerp(r[i], t.RadicalismTarget, .025f), 0, 1);
        }
        if (len > 0)
        {
            var sd = SizeD.AsSpan(start, len);
            var living = 0.0;
            for (var k = 0; k < len; k++) living += sd[k] * (double)l[k];
            Epilogue(s, living);
        }
    }

    // ----------------------------------------------------------------------------------------
    // Drivers. Regions are independent and own disjoint runs of groups, so handing ranges of
    // regions to DeterministicParallel gives the same bytes as the serial loop by construction.
    // Ranges of 32 regions, not one region per work item: a region is about a microsecond of
    // work, less than the thread pool's hand-off.
    // ----------------------------------------------------------------------------------------

    public const int Block = 32;

    public void RunScalar() { for (var s = 0; s < StateCount; s++) ReviewStateScalar(s); }
    public void RunSimd() { for (var s = 0; s < StateCount; s++) ReviewStateSimd(s); }

    public void RunScalarParallel(Campaign c)
    {
        var blocks = (StateCount + Block - 1) / Block;
        DeterministicParallel.For(c, 0, blocks, b =>
        {
            var end = Math.Min(StateCount, (b + 1) * Block);
            for (var s = b * Block; s < end; s++) ReviewStateScalar(s);
        }, minItems: 2);
    }

    public void RunSimdParallel(Campaign c)
    {
        var blocks = (StateCount + Block - 1) / Block;
        DeterministicParallel.For(c, 0, blocks, b =>
        {
            var end = Math.Min(StateCount, (b + 1) * Block);
            for (var s = b * Block; s < end; s++) ReviewStateSimd(s);
        }, minItems: 2);
    }
}
