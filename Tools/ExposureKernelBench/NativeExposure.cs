// SPDX-License-Identifier: GPL-3.0-only. Experiment only; not compiled into the game.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DealOrForce.Simulation;

public sealed unsafe class NativeExposure : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Route { public int Good, Seller; public float Volume; }
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<uint> _version;
    private readonly delegate* unmanaged[Cdecl]<Route*, uint, Route*, uint, double*, uint, int*, uint, uint*, int> _sum;
    private static readonly IComparer<EconomicExposure.Supplier> Order = Comparer<EconomicExposure.Supplier>.Create((a,b) => {
        var volume = b.Volume.CompareTo(a.Volume); return volume != 0 ? volume : a.NationId.CompareTo(b.NationId); });

    public NativeExposure(string path)
    {
        _library = NativeLibrary.Load(System.IO.Path.GetFullPath(path));
        try
        {
            _version = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(_library, "dof_exposure_abi_version");
            _sum = (delegate* unmanaged[Cdecl]<Route*, uint, Route*, uint, double*, uint, int*, uint, uint*, int>)
                NativeLibrary.GetExport(_library, "dof_exposure_sum_v1");
            if (_version() != 1 || sizeof(Route) != 12) throw new InvalidOperationException("exposure ABI mismatch");
        }
        catch { NativeLibrary.Free(_library); throw; }
    }
    public void Dispose() => NativeLibrary.Free(_library);

    public EconomicExposure.Row[] Read(Campaign c, Nation n)
    {
        if (c is null || n is null) return Array.Empty<EconomicExposure.Row>();
        var routes = c.TradeRoutesDay < 0 ? Array.Empty<TradeRoute>() : TradeNetwork.RoutesTouching(c, n.Id);
        if (routes.Count > 262144) throw new InvalidOperationException("pilot capacity exceeded");
        var capacity = Math.Max(routes.Count, 1);
        var tableSize = 1; while (tableSize < 2 * capacity) tableSize *= 2;
        var input = ArrayPool<Route>.Shared.Rent(capacity);
        var output = ArrayPool<Route>.Shared.Rent(capacity);
        var sums = ArrayPool<double>.Shared.Rent(capacity);
        var slots = ArrayPool<int>.Shared.Rent(tableSize);
        try
        {
            var count = 0;
            foreach (var r in routes)
                if (r.BuyerId == n.Id && r.SellerId != n.Id && c.NationById(r.SellerId) is not null &&
                    float.IsFinite(r.Volume) && r.Volume > 0)
                    input[count++] = new Route { Good = (int)r.Commodity, Seller = r.SellerId, Volume = r.Volume };
            uint written = 0;
            fixed (Route* p = input, q = output)
            fixed (double* s = sums)
            fixed (int* t = slots)
            {
                var status = _sum(p, (uint)count, q, (uint)capacity, s, (uint)capacity, t, (uint)tableSize, &written);
                if (status != 0 || written > count) throw new InvalidOperationException("native exposure status " + status);
            }
            var groups = new Dictionary<Commodity, List<EconomicExposure.Supplier>>();
            for (var index = 0; index < written; index++)
            {
                var r = output[index]; var good = (Commodity)r.Good;
                if (r.Seller < 0 || c.NationById(r.Seller) is null || float.IsNaN(r.Volume) || r.Volume <= 0)
                    throw new InvalidOperationException("invalid native output");
                if (!groups.TryGetValue(good, out var group)) groups[good] = group = new();
                group.Add(new(r.Seller, r.Volume));
            }
            var rows = new List<EconomicExposure.Row>();
            foreach (var quote in n.Market.Values)
            {
                if (!float.IsFinite(quote.Demand) || quote.Demand <= .001f ||
                    !float.IsFinite(quote.Imports) || quote.Imports <= .001f) continue;
                var suppliers = groups.TryGetValue(quote.Commodity, out var group) ? group.ToArray() : Array.Empty<EconomicExposure.Supplier>();
                Array.Sort(suppliers, Order);
                double named = 0; foreach (var supplier in suppliers) named += supplier.Volume;
                rows.Add(new(quote.Commodity, quote.Demand, quote.Imports, quote.Satisfaction,
                    Math.Max(0, quote.Imports - (float)named), suppliers));
            }
            return rows.OrderByDescending(r => r.LargestDemandShare).ThenByDescending(r => r.ImportShare).ThenBy(r => r.Good).ToArray();
        }
        finally
        {
            ArrayPool<Route>.Shared.Return(input); ArrayPool<Route>.Shared.Return(output);
            ArrayPool<double>.Shared.Return(sums); ArrayPool<int>.Shared.Return(slots);
        }
    }

    public void ContractTests()
    {
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("native exposure contract failure"); }
        Route input = new() { Good = 0, Seller = 1, Volume = 1 }, output = default;
        double sum = 0; var slots = stackalloc int[2]; uint written = 999;
        Check(_sum(&input, 1, &output, 1, &sum, 1, slots, 2, &written) == 0 && written == 1 && output.Volume == 1);
        Check(_sum(null, 0, null, 0, null, 0, slots, 1, &written) == 0 && written == 0);
        Check(_sum(null, 1, &output, 1, &sum, 1, slots, 2, &written) == 1);
        Check(_sum(&input, 1, &output, 0, &sum, 1, slots, 2, &written) == 2);
        Check(_sum(&input, 1, &output, 1, &sum, 1, slots, 3, &written) == 2);
        Check(_sum(&input, 1, &input, 1, &sum, 1, slots, 2, &written) == 1);
        Check(_sum(&input, 1, &output, 1, &sum, 1, slots, 2, null) == 1);
        input.Volume = float.NaN; written = 987; output.Volume = 765;
        Check(_sum(&input, 1, &output, 1, &sum, 1, slots, 2, &written) == 1 && written == 987 && output.Volume == 765);
        input.Volume = float.PositiveInfinity;
        Check(_sum(&input, 1, &output, 1, &sum, 1, slots, 2, &written) == 1);
        input.Volume = -1;
        Check(_sum(&input, 1, &output, 1, &sum, 1, slots, 2, &written) == 1);
        Console.WriteLine("PASS native ABI: 10 capacity/empty/null/alias/invalid-input cases");
    }
}
