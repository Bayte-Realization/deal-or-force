using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.OwnershipKernelBench;

// A single-owner, tools-only snapshot. Capture and Execute take the same exclusive
// lease: synchronous Parallel.For completes before the lease is released, so a
// later capture cannot overwrite a worker's inputs. No live object is retained.
internal sealed class ReusableExposureSnapshot
{
    private readonly record struct Quote(Commodity Good, float Demand, float Imports, float Satisfaction);
    private readonly record struct Route(int Seller, float Volume, Commodity Good);
    private struct Slice { internal int QuoteAt, QuoteCount, RouteAt, RouteCount; }

    private Quote[] _quotes = Array.Empty<Quote>();
    private Route[] _routes = Array.Empty<Route>();
    private Slice[] _slices = Array.Empty<Slice>();
    private int[] _writes = Array.Empty<int>();
    private readonly Dictionary<int, int> _nationSlots = new();
    private int _nationCount, _quoteCount, _routeCount, _busy;
    private bool _captured;

    // Value-buffer capacity, not a GC heap/RSS measurement. Dictionary backing
    // storage and output objects are deliberately not included in this figure.
    internal long RetainedValueBytes => (long)_quotes.Length * 16 + (long)_routes.Length * 12 +
        (long)_slices.Length * 16 + (long)_writes.Length * sizeof(int);
    internal string Shape => $"nations={_nationCount} quotes={_quoteCount} routes={_routeCount} retained_value_bytes={RetainedValueBytes}";

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Snapshot is already being captured or read");
    }

    private static void Grow<T>(ref T[] values, int required)
    {
        if (required <= values.Length) return;
        // One bounded set of flat arrays; no per-day/per-nation cache entries.
        // Beyond the eight-element minimum, capacity is at most twice this
        // snapshot's highest observed requirement.
        var capacity = Math.Max(required, Math.Max(8L, (long)values.Length * 2));
        Array.Resize(ref values, checked((int)capacity));
    }

    internal void Capture(Campaign world, Nation[] nations)
    {
        Enter();
        try
        {
            _captured = false;
            _nationCount = nations.Length;
            _quoteCount = _routeCount = 0;
            _nationSlots.Clear();
            Grow(ref _slices, nations.Length);
            Grow(ref _writes, nations.Length);
            for (var slot = 0; slot < nations.Length; slot++)
            {
                if (!_nationSlots.TryAdd(nations[slot].Id, slot))
                    throw new ArgumentException("This batch requires unique nation IDs", nameof(nations));
                _slices[slot] = new Slice { QuoteAt = _quoteCount, QuoteCount = nations[slot].Market.Count };
                _quoteCount = checked(_quoteCount + nations[slot].Market.Count);
            }
            Grow(ref _quotes, _quoteCount);
            for (var slot = 0; slot < nations.Length; slot++)
            {
                var at = _slices[slot].QuoteAt;
                foreach (var quote in nations[slot].Market.Values)
                    _quotes[at++] = new(quote.Commodity, quote.Demand, quote.Imports, quote.Satisfaction);
            }

            if (world.TradeRoutesDay >= 0)
            {
                // Count then pack by buyer in original route order. A single
                // global pass per phase also avoids RoutesTouching's allocating
                // fallback after loading a save whose route index is not filed.
                foreach (var route in world.TradeRoutes)
                    if (Valid(world, route) && _nationSlots.TryGetValue(route.BuyerId, out var slot))
                        _slices[slot].RouteCount++;
                for (var slot = 0; slot < nations.Length; slot++)
                {
                    _slices[slot].RouteAt = _writes[slot] = _routeCount;
                    _routeCount = checked(_routeCount + _slices[slot].RouteCount);
                }
                Grow(ref _routes, _routeCount);
                foreach (var route in world.TradeRoutes)
                    if (Valid(world, route) && _nationSlots.TryGetValue(route.BuyerId, out var slot))
                        _routes[_writes[slot]++] = new(route.SellerId, route.Volume, route.Commodity);
            }
            _captured = true;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static bool Valid(Campaign world, TradeRoute route) => route.SellerId != route.BuyerId &&
        world.NationById(route.SellerId) is not null && float.IsFinite(route.Volume) && route.Volume > 0;

    internal EconomicExposure.Row[][] Execute(int workers)
    {
        Enter();
        try
        {
            if (!_captured) throw new InvalidOperationException("Capture must succeed before Execute");
            if (workers < 1) throw new ArgumentOutOfRangeException(nameof(workers));
            var output = new EconomicExposure.Row[_nationCount][];
            if (workers == 1)
                for (var slot = 0; slot < _nationCount; slot++) output[slot] = Read(slot);
            else Parallel.For(0, _nationCount, new ParallelOptions { MaxDegreeOfParallelism = workers },
                slot => output[slot] = Read(slot));
            return output;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private EconomicExposure.Row[] Read(int slot)
    {
        var slice = _slices[slot];
        var totals = new Dictionary<Commodity, Dictionary<int, double>>();
        for (var at = slice.RouteAt; at < slice.RouteAt + slice.RouteCount; at++)
        {
            var route = _routes[at];
            if (!totals.TryGetValue(route.Good, out var sellers)) totals[route.Good] = sellers = new();
            sellers[route.Seller] = sellers.GetValueOrDefault(route.Seller) + route.Volume;
        }
        var rows = new List<EconomicExposure.Row>();
        for (var at = slice.QuoteAt; at < slice.QuoteAt + slice.QuoteCount; at++)
        {
            var quote = _quotes[at];
            if (!float.IsFinite(quote.Demand) || quote.Demand <= .001f ||
                !float.IsFinite(quote.Imports) || quote.Imports <= .001f) continue;
            var suppliers = Array.Empty<EconomicExposure.Supplier>();
            if (totals.TryGetValue(quote.Good, out var sellers))
            {
                suppliers = new EconomicExposure.Supplier[sellers.Count];
                var supplierAt = 0;
                foreach (var seller in sellers)
                    suppliers[supplierAt++] = new(seller.Key, (float)seller.Value);
                Array.Sort(suppliers, SupplierOrder);
            }
            double named = 0;
            foreach (var supplier in suppliers) named += supplier.Volume;
            rows.Add(new(quote.Good, quote.Demand, quote.Imports, quote.Satisfaction,
                Math.Max(0, quote.Imports - (float)named), suppliers));
        }
        return rows.OrderByDescending(row => row.LargestDemandShare).ThenByDescending(row => row.ImportShare)
            .ThenBy(row => row.Good).ToArray();
    }

    private static readonly IComparer<EconomicExposure.Supplier> SupplierOrder =
        Comparer<EconomicExposure.Supplier>.Create((left, right) =>
        {
            var volume = right.Volume.CompareTo(left.Volume);
            return volume != 0 ? volume : left.NationId.CompareTo(right.NationId);
        });

    // Deterministic lease regression, without racing a live campaign or depending
    // on timing/sleeps. The same guard covers real simultaneous callers.
    internal void CheckExclusiveLease(Campaign world, Nation[] nations)
    {
        Enter();
        try
        {
            try { Capture(world, nations); throw new Exception("Recapture was allowed while leased"); }
            catch (InvalidOperationException) { }
            try { Execute(1); throw new Exception("Reentrant execute was allowed while leased"); }
            catch (InvalidOperationException) { }
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
