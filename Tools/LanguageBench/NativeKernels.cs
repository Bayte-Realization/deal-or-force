using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DealOrForce.Tools.LanguageBench;

/// <summary>
/// One lb_*.dll (Rust, Zig, Odin, Nim): the same C ABI v1 in every language, loaded by an
/// explicit absolute path and called through unmanaged function pointers - the cheapest
/// crossing .NET has (no delegate marshalling; blittable arguments; arrays pinned with
/// <c>fixed</c>, never copied).
/// </summary>
internal sealed unsafe class NativeKernels : IDisposable
{
    public readonly string Name;
    public readonly string Path;
    private IntPtr _library;
    private readonly delegate* unmanaged[Cdecl]<ushort*, ushort*, int, int, ushort*, uint, ushort*, uint, byte*, int> _frontier;
    private readonly delegate* unmanaged[Cdecl]<ushort*, ushort*, int, int, ushort*, uint, ushort*, uint, byte*, int, int> _frontierParallel;
    private readonly delegate* unmanaged[Cdecl]<int, int, float, float, float*, int*, int*, int*, float*, float*, float*, float*, float*, int> _budget;
    private readonly delegate* unmanaged[Cdecl]<nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, int> _noop16;

    private readonly delegate* unmanaged[Cdecl]<ushort*, ushort*, int, int, ushort*, uint, ushort*, uint, byte*, int, int> _frontierTuned;
    public bool HasParallelFrontier => _frontierParallel != null;
    public bool HasTunedFrontier => _frontierTuned != null;

    private NativeKernels(string name, string path, IntPtr library)
    {
        Name = name; Path = path; _library = library;
        var abi = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, "lb_abi_version");
        if (abi() != 1) throw new InvalidOperationException(name + ": lb ABI mismatch");
        _frontier = (delegate* unmanaged[Cdecl]<ushort*, ushort*, int, int, ushort*, uint, ushort*, uint, byte*, int>)NativeLibrary.GetExport(library, "lb_frontier_v1");
        _budget = (delegate* unmanaged[Cdecl]<int, int, float, float, float*, int*, int*, int*, float*, float*, float*, float*, float*, int>)NativeLibrary.GetExport(library, "lb_budget_v1");
        _noop16 = (delegate* unmanaged[Cdecl]<nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, nuint, int>)NativeLibrary.GetExport(library, "lb_noop16");
        if (NativeLibrary.TryGetExport(library, "lb_frontier_parallel_v1", out var parallel))
            _frontierParallel = (delegate* unmanaged[Cdecl]<ushort*, ushort*, int, int, ushort*, uint, ushort*, uint, byte*, int, int>)parallel;
        if (NativeLibrary.TryGetExport(library, "lb_frontier_tuned_v1", out var tuned))
            _frontierTuned = (delegate* unmanaged[Cdecl]<ushort*, ushort*, int, int, ushort*, uint, ushort*, uint, byte*, int, int>)tuned;
    }

    public static NativeKernels TryOpen(string name, string absolutePath)
    {
        if (!System.IO.Path.IsPathFullyQualified(absolutePath) || !File.Exists(absolutePath)) return null;
        var handle = NativeLibrary.Load(absolutePath);
        return new NativeKernels(name, absolutePath, handle);
    }

    public byte[] Frontier(ushort[] authored, ushort[] cellToProvince, int width, int height, ushort[] owners, ushort[] authoredOwners)
    {
        var output = new byte[width * height];
        fixed (ushort* a = authored, c = cellToProvince, o = owners, ao = authoredOwners)
        fixed (byte* outp = output)
        {
            var status = _frontier(a, c, width, height, o, (uint)owners.Length, ao, (uint)authoredOwners.Length, outp);
            if (status != 0) throw new InvalidOperationException(Name + " frontier status " + status);
        }
        return output;
    }

    public byte[] FrontierParallel(ushort[] authored, ushort[] cellToProvince, int width, int height, ushort[] owners, ushort[] authoredOwners, int threads)
    {
        var output = new byte[width * height];
        fixed (ushort* a = authored, c = cellToProvince, o = owners, ao = authoredOwners)
        fixed (byte* outp = output)
        {
            var status = _frontierParallel(a, c, width, height, o, (uint)owners.Length, ao, (uint)authoredOwners.Length, outp, threads);
            if (status != 0) throw new InvalidOperationException(Name + " frontier status " + status);
        }
        return output;
    }

    public byte[] FrontierTuned(ushort[] authored, ushort[] cellToProvince, int width, int height, ushort[] owners, ushort[] authoredOwners, int threads)
    {
        var output = new byte[width * height];
        fixed (ushort* a = authored, c = cellToProvince, o = owners, ao = authoredOwners)
        fixed (byte* outp = output)
        {
            var status = _frontierTuned(a, c, width, height, o, (uint)owners.Length, ao, (uint)authoredOwners.Length, outp, threads);
            if (status != 0) throw new InvalidOperationException(Name + " frontier status " + status);
        }
        return output;
    }

    public void Budget(BudgetInput b, float[] output)
    {
        fixed (float* lf = b.LineFloats, fw = b.FactorWeights, slots = b.Slots, nf = b.NationFloats, rates = b.Rates, outp = output)
        fixed (int* li = b.LineInts, fo = b.FactorOffsets, fs = b.FactorSlots)
        {
            var status = _budget(b.Nations, b.Lines, b.TaxTolerance, b.TaxEvasion, lf, li, fo, fs, fw, slots, nf, rates, outp);
            if (status != 0) throw new InvalidOperationException(Name + " budget status " + status);
        }
    }

    /// <summary>The crossing alone: pin the same arrays a budget call pins, call a function that returns.</summary>
    public int NoopBudgetShape(BudgetInput b, float[] output)
    {
        fixed (float* lf = b.LineFloats, fw = b.FactorWeights, slots = b.Slots, nf = b.NationFloats, rates = b.Rates, outp = output)
        fixed (int* li = b.LineInts, fo = b.FactorOffsets, fs = b.FactorSlots)
            return _noop16((nuint)b.Nations, (nuint)b.Lines, 0, 0, (nuint)lf, (nuint)li, (nuint)fo, (nuint)fs, (nuint)fw,
                (nuint)slots, (nuint)nf, (nuint)rates, (nuint)outp, 0, 0, 0);
    }

    public int NoopFrontierShape(ushort[] authored, ushort[] cellToProvince, ushort[] owners, ushort[] authoredOwners, byte[] output)
    {
        fixed (ushort* a = authored, c = cellToProvince, o = owners, ao = authoredOwners)
        fixed (byte* outp = output)
            return _noop16((nuint)a, (nuint)c, 5760, 2880, (nuint)o, (nuint)owners.Length, (nuint)ao, (nuint)authoredOwners.Length,
                (nuint)outp, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>Eight pinned arrays and sixteen arguments: the shape of the ownership ABI call.</summary>
    public int NoopOwnershipShape(int[] offsets, int[] neighbors, ushort[] owners, byte[] water, int[] labels, int[] members, int[] componentOffsets, int[] counts)
    {
        fixed (int* of = offsets, ne = neighbors, la = labels, me = members, co = componentOffsets, cn = counts)
        fixed (ushort* ow = owners)
        fixed (byte* wa = water)
            return _noop16((nuint)of, (nuint)offsets.Length, (nuint)ne, (nuint)neighbors.Length, (nuint)ow, (nuint)owners.Length,
                (nuint)wa, (nuint)water.Length, (nuint)la, (nuint)labels.Length, (nuint)me, (nuint)members.Length,
                (nuint)co, (nuint)componentOffsets.Length, (nuint)cn, (nuint)(cn + 1));
    }

    public void Dispose()
    {
        if (_library == IntPtr.Zero) return;
        NativeLibrary.Free(_library);
        _library = IntPtr.Zero;
    }
}
