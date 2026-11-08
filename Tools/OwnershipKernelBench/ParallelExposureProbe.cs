using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.OwnershipKernelBench;

// Tools-only experiment. Workers receive value snapshots, never Campaign, Godot
// objects, mod delegates or the campaign RNG. No production executor is enabled.
internal static class ParallelExposureProbe
{
    private readonly record struct Quote(Commodity Good, float Demand, float Imports, float Satisfaction);
    private readonly record struct Route(int Seller, float Volume, Commodity Good);
    private sealed record Input(Quote[] Quotes, Route[] Routes);

    private static Input[] Capture(Campaign c, Nation[] nations)
    {
        var input = new Input[nations.Length];
        for (var i = 0; i < nations.Length; i++)
        {
            var n = nations[i];
            var quotes = n.Market.Values.Select(q => new Quote(q.Commodity, q.Demand, q.Imports, q.Satisfaction)).ToArray();
            var routes = c.TradeRoutesDay < 0 ? Array.Empty<Route>() : TradeNetwork.RoutesTouching(c, n.Id)
                .Where(r => r.BuyerId == n.Id && r.SellerId != n.Id && c.NationById(r.SellerId) is not null &&
                    float.IsFinite(r.Volume) && r.Volume > 0)
                .Select(r => new Route(r.SellerId, r.Volume, r.Commodity)).ToArray();
            input[i] = new(quotes, routes);
        }
        return input;
    }

    private static EconomicExposure.Row[] Read(Input input)
    {
        var totals = new Dictionary<Commodity, Dictionary<int, double>>();
        foreach (var r in input.Routes)
        {
            if (!totals.TryGetValue(r.Good, out var sellers)) totals[r.Good] = sellers = new();
            sellers[r.Seller] = sellers.GetValueOrDefault(r.Seller) + r.Volume;
        }
        var rows = new List<EconomicExposure.Row>();
        foreach (var q in input.Quotes)
        {
            if (!float.IsFinite(q.Demand) || q.Demand <= .001f || !float.IsFinite(q.Imports) || q.Imports <= .001f) continue;
            var suppliers = Array.Empty<EconomicExposure.Supplier>();
            if (totals.TryGetValue(q.Good, out var sellers))
            {
                suppliers = sellers.Select(s => new EconomicExposure.Supplier(s.Key, (float)s.Value)).ToArray();
                Array.Sort(suppliers, Order);
            }
            double named = 0; foreach (var s in suppliers) named += s.Volume;
            rows.Add(new(q.Good, q.Demand, q.Imports, q.Satisfaction, Math.Max(0, q.Imports - (float)named), suppliers));
        }
        return rows.OrderByDescending(r => r.LargestDemandShare).ThenByDescending(r => r.ImportShare).ThenBy(r => r.Good).ToArray();
    }

    private static readonly IComparer<EconomicExposure.Supplier> Order = Comparer<EconomicExposure.Supplier>.Create((a, b) =>
    { var volume = b.Volume.CompareTo(a.Volume); return volume != 0 ? volume : a.NationId.CompareTo(b.NationId); });

    private static EconomicExposure.Row[][] Execute(Input[] input, int workers)
    {
        var output = new EconomicExposure.Row[input.Length][];
        if (workers == 1) for (var i = 0; i < input.Length; i++) output[i] = Read(input[i]);
        else Parallel.For(0, input.Length, new ParallelOptions { MaxDegreeOfParallelism = workers }, i => output[i] = Read(input[i]));
        return output; // Stable slots, independent of the order in which workers finish.
    }

    private static void Same(EconomicExposure.Row[][] expected, EconomicExposure.Row[][] actual)
    {
        if (expected.Length != actual.Length) throw new Exception("Nation count differs");
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i].Length != actual[i].Length) throw new Exception("Row count differs at nation " + i);
            for (var j = 0; j < expected[i].Length; j++)
            {
                var a = expected[i][j]; var b = actual[i][j];
                if (a.Good != b.Good || !Bits(a.Demand, b.Demand) || !Bits(a.Imports, b.Imports) ||
                    !Bits(a.Satisfaction, b.Satisfaction) || !Bits(a.UnattributedImports, b.UnattributedImports) ||
                    a.Suppliers.Length != b.Suppliers.Length) throw new Exception("Row differs at nation " + i);
                for (var k = 0; k < a.Suppliers.Length; k++)
                    if (a.Suppliers[k].NationId != b.Suppliers[k].NationId || !Bits(a.Suppliers[k].Volume, b.Suppliers[k].Volume))
                        throw new Exception("Ordered supplier differs at nation " + i);
            }
        }
    }
    private static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static void CheckReuse()
    {
        var snapshot = new ReusableExposureSnapshot();
        var shapes = new[] { (1, 1, 0), (3, 20, 41), (97, 43, 6049), (2, 1, 1),
            (0, 0, 0), (5, 0, 399), (5, 79, 0), (19, 40, 2000) };
        var random = new Random(712026);
        foreach (var (nationCount, quoteCount, routeCount) in shapes)
        {
            var world = new Campaign(777);
            for (var n = 0; n < nationCount; n++)
            {
                var nation = new Nation { Id = n, Tag = "BUFFER" + n, Name = "Buffer check " + n };
                world.Nations.Add(nation);
                for (var q = 0; q < quoteCount; q++)
                    nation.Market[(Commodity)q] = new MarketQuote { Commodity = (Commodity)(q % 7 == 0 ? 0 : q),
                        Demand = q % 17 == 0 ? float.NaN : q % 13 == 0 ? 0 : q + 30,
                        Imports = q % 19 == 0 ? float.PositiveInfinity : q % 11 == 0 ? .0001f : q + 20,
                        Satisfaction = (float)random.NextDouble() };
            }
            world.TradeRoutesDay = 0;
            for (var r = 0; r < routeCount; r++)
                world.TradeRoutes.Add(new TradeRoute { BuyerId = random.Next(-1, nationCount + 2),
                    SellerId = random.Next(-1, nationCount + 2), Commodity = (Commodity)random.Next(-2, quoteCount + 3),
                    Volume = r % 37 == 0 ? float.NaN : r % 31 == 0 ? float.PositiveInfinity :
                        r % 29 == 0 ? -2 : r % 23 == 0 ? 16777216f : r % 5 == 0 ? 1 : (float)random.NextDouble() * 20 });
            var stamp = CampaignFingerprint.Of(world); var rng = world.Random.State;
            // Full batch, reordered subset, empty and full again use the same
            // storage. The smaller batches must never see old rows or routes.
            var nations = world.Nations.ToArray();
            foreach (var selection in new[] { nations, nations.Reverse().Where((_, i) => i % 2 == 0).ToArray(),
                Array.Empty<Nation>(), nations })
            {
                var expected = selection.Select(n => EconomicExposure.Read(world, n)).ToArray();
                snapshot.Capture(world, selection);
                var previousOutput = snapshot.Execute(1);
                Same(expected, previousOutput);
                Same(expected, snapshot.Execute(4));
                snapshot.CheckExclusiveLease(world, selection);
                var retained = snapshot.RetainedValueBytes;
                snapshot.Capture(world, Array.Empty<Nation>());
                Same(expected, previousOutput); // Returned rows own their storage.
                if (snapshot.RetainedValueBytes != retained)
                    throw new Exception("Empty recapture grew reusable storage");
            }
            world.TradeRoutesDay = -1;
            snapshot.Capture(world, nations);
            Same(nations.Select(n => EconomicExposure.Read(world, n)).ToArray(), snapshot.Execute(2));
            world.TradeRoutesDay = 0;
            if (CampaignFingerprint.Of(world) != stamp || world.Random.State != rng)
                throw new Exception("Reuse regression changed campaign/RNG");
        }
        // Attribution after a failed capture is not a partial/previous snapshot.
        var emptyWorld = new Campaign(777);
        try { new ReusableExposureSnapshot().Execute(1); throw new Exception("Uncaptured input remained executable"); }
        catch (InvalidOperationException) { }
        var duplicate = new Nation { Id = 0 };
        try { snapshot.Capture(emptyWorld, new[] { duplicate, duplicate }); throw new Exception("Duplicate ID accepted"); }
        catch (ArgumentException) { }
        try { snapshot.Execute(1); throw new Exception("Failed capture remained executable"); }
        catch (InvalidOperationException) { }
        snapshot.Capture(emptyWorld, Array.Empty<Nation>());
        Same(Array.Empty<EconomicExposure.Row[]>(), snapshot.Execute(1));
        Console.WriteLine("PASS reusable snapshot growth/shrink/empty/reorder, invalid routes/quotes, duplicate goods, detached outputs, exclusive lease, failed-capture recovery and fixture RNG/fingerprint neutrality");
    }

    internal static int Run(Campaign world)
    {
        world.IsPaused = false; for (var day = 0; day < 8; day++) world.TickDay(); world.IsPaused = true;
        var nations = world.Nations.ToArray(); var stamp = CampaignFingerprint.Of(world); var rng = world.Random.State;
        var expected = nations.Select(n => EconomicExposure.Read(world, n)).ToArray();
        var input = Capture(world, nations);
        foreach (var workers in new[] { 1, 2, 4 }) Same(expected, Execute(input, workers));
        CheckReuse();
        var reusable = new ReusableExposureSnapshot();
        var coldBefore = GC.GetTotalAllocatedBytes(true); var coldStart = Stopwatch.GetTimestamp();
        reusable.Capture(world, nations);
        var coldMs = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
        var coldBytes = GC.GetTotalAllocatedBytes(true) - coldBefore;
        foreach (var workers in new[] { 1, 2, 4 }) Same(expected, reusable.Execute(workers));
        var captureBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var repeat = 0; repeat < 16; repeat++) reusable.Capture(world, nations);
        var captureBytes = GC.GetAllocatedBytesForCurrentThread() - captureBefore;
        if (captureBytes != 0) throw new Exception("Steady-state capture allocated " + captureBytes + " bytes");
        Console.WriteLine(FormattableString.Invariant($"SNAPSHOT_STORAGE {reusable.Shape} cold_capture_ms={coldMs:F4} cold_capture_all_threads_bytes={coldBytes} repeated_captures=16 warm_capture_bytes={captureBytes}"));
        // Poison the original mutable fields after capture. A real worker snapshot
        // must still produce its old answer without reading the live country/route.
        var quote = nations.SelectMany(n => n.Market.Values).First(q => q.Imports > .001f && q.Demand > .001f);
        var previous = quote.Imports;
        var route = world.TradeRoutes.First(r => r.Volume > 0);
        var previousVolume = route.Volume; var previousSeller = route.SellerId;
        try
        {
            quote.Imports = 0; route.Volume = float.NaN; route.SellerId = -1;
            Same(expected, Execute(input, 4)); Same(expected, reusable.Execute(4));
        }
        finally { quote.Imports = previous; route.Volume = previousVolume; route.SellerId = previousSeller; }
        Console.WriteLine("PASS original/reusable snapshot isolation after live quote/route poisoning, exact float bits, stable nation/row/supplier order at 1/2/4 workers");

        Func<EconomicExposure.Row[][]>[] methods = {
            () => nations.Select(n => EconomicExposure.Read(world, n)).ToArray(),
            () => Execute(Capture(world, nations), 1),
            () => Execute(Capture(world, nations), 2),
            () => Execute(Capture(world, nations), 4),
            () => { reusable.Capture(world, nations); return reusable.Execute(1); },
            () => { reusable.Capture(world, nations); return reusable.Execute(2); },
            () => { reusable.Capture(world, nations); return reusable.Execute(4); }
        };
        for (var warm = 0; warm < 4; warm++) foreach (var method in methods) Same(expected, method());
        const int count = 31;
        var times = methods.Select(_ => new List<double>()).ToArray();
        var bytes = methods.Select(_ => new List<long>()).ToArray();
        for (var sample = 0; sample < count; sample++)
        for (var offset = 0; offset < methods.Length; offset++)
        {
            var which = (sample + offset) % methods.Length;
            var before = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
            var result = methods[which]();
            times[which].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            bytes[which].Add(GC.GetTotalAllocatedBytes(true) - before);
            Same(expected, result); // Outside the timed region; run it on EVERY sample.
        }
        var names = new[] { "live_sequential", "legacy_snapshot_1", "legacy_snapshot_2", "legacy_snapshot_4",
            "reused_snapshot_1", "reused_snapshot_2", "reused_snapshot_4" };
        for (var i = 0; i < methods.Length; i++)
        {
            times[i].Sort(); bytes[i].Sort();
            Console.WriteLine(FormattableString.Invariant($"PARALLEL_READ method={names[i]} samples={count} median_ms={times[i][15]:F4} p95_ms={times[i][29]:F4} allocated_all_threads_bytes={bytes[i][15]}"));
        }
        if (stamp != CampaignFingerprint.Of(world) || rng != world.Random.State) throw new Exception("Read batch changed campaign/RNG");
        Console.WriteLine($"PARALLEL_READ_PASS nations={nations.Length} routes={world.TradeRoutes.Count} fingerprint={stamp:X16} rng={rng} includes_snapshot_pack=true production_enabled=false");
        return 0;
    }
}
