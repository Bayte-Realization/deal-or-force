using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

internal static class Program
{
    private static int _checks;
    private static int _sink;
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        _checks++;
    }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("absolute native library path required");
            using var native = new NativeExposure(args[0]); native.ContractTests();
            ExposureBatchTests.Run(); Differential(native);
            var synthetic = ExposureBatchTests.World();
            foreach (var good in Enum.GetValues<Commodity>())
            for (var seller = 1; seller < 12; seller++)
            for (var part = 0; part < 5; part++) synthetic.TradeRoutes.Add(new() {
                BuyerId = 0, SellerId = seller, Commodity = good, Volume = part + 1 });
            Bench(synthetic, native, "synthetic_1100_routes", 31, 12);
            if (args.Contains("--world"))
            {
                var world = World();
                // Read-only attribution needs an executed trade cycle, not an empty bookmark.
                world.IsPaused = false; for (var day = 0; day < 8; day++) world.TickDay();
                Bench(world, native, "bookmark_day8", 31, 2);
                var save = Array.IndexOf(args, "--save");
                if (save >= 0)
                {
                    var path = Path.GetFullPath(args[save + 1]);
                    var loaded = SaveSystem.Load(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path), "");
                    Check(loaded.Success && loaded.Campaign is not null, "saved input loads: " + loaded.Warning);
                    loaded.Campaign.AttachProvinceTopology(world.ProvinceMap);
                    Bench(loaded.Campaign, native, "saved_day" + loaded.Campaign.ElapsedDays, 31, 2);
                }
            }
            Console.WriteLine($"EXPOSURE_BENCH_PASS checks={_checks} sink={_sink}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("EXPOSURE_BENCH_FAIL " + error); return 1; }
    }

    private static Campaign World()
    {
        var world = Campaign.CreateBootstrap(20260830);
        MapRasterCatalog.ResetCatalog(); HydrologyCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message)) throw new Exception(message);
        if (MapRasterCatalog.TryLoadHydrology(WorldMapSource.Width, WorldMapSource.Height, out var water, out _)) map.AttachHydrology(water);
        world.AttachProvinceTopology(map); return world;
    }

    private static void Differential(NativeExposure native)
    {
        var random = new Random(241024);
        for (var fixture = 0; fixture < 400; fixture++)
        {
            var c = ExposureBatchTests.World(fixture + 1); var n = c.Nations[0];
            if (fixture % 5 == 0) n.Market[Commodity.ConsumerGoods].Commodity = Commodity.Food;
            if (fixture % 7 == 0) n.Market[Commodity.Oil].Demand = float.NaN;
            if (fixture % 11 == 0) n.Market[Commodity.Arms].Imports = float.PositiveInfinity;
            n.Market[(Commodity)(-100)] = new() { Commodity = (Commodity)(-100), Demand = 88, Imports = 77 };
            var routes = random.Next(0, 1800);
            for (var row = 0; row < routes; row++) c.TradeRoutes.Add(new() {
                BuyerId = row % 11 == 0 ? 1 : 0, SellerId = random.Next(-2, 15),
                Commodity = row % 19 == 0 ? (Commodity)(-100) : (Commodity)random.Next(0, 22),
                Volume = row % 17 == 0 ? float.NaN : row % 13 == 0 ? float.PositiveInfinity :
                    row % 7 == 0 ? -1 : (float)(random.NextDouble() * 500) });
            if (fixture % 23 == 0) c.TradeRoutesDay = -1;
            var stamp = CampaignFingerprint.Of(c); var rng = c.Random.State;
            var oracle = ExposureReadOracle.Read(c, n);
            Check(ExposureReadOracle.Equal(oracle, native.Read(c, n)), "native differential fixture " + fixture);
            Check(ExposureReadOracle.Equal(oracle, EconomicExposure.Read(c, n)), "managed differential fixture " + fixture);
            Check(stamp == CampaignFingerprint.Of(c) && rng == c.Random.State, "pure native view " + fixture);
        }
        var precise = ExposureBatchTests.World();
        foreach (var value in new[] { 16777216f, 1f, 1f, float.MaxValue, float.MaxValue })
            precise.TradeRoutes.Add(new() { BuyerId = 0, SellerId = value == float.MaxValue ? 2 : 1, Commodity = Commodity.Food, Volume = value });
        Check(ExposureReadOracle.Equal(ExposureReadOracle.Read(precise, precise.Nations[0]), native.Read(precise, precise.Nations[0])),
            "native double accumulation, float overflow and ordering");
        Console.WriteLine("PASS native differential: 400 random worlds plus precision/overflow");
    }

    private static void Bench(Campaign world, NativeExposure native, string label, int samples, int repeats)
    {
        var nations = world.Nations.ToArray();
        var stamp = CampaignFingerprint.Of(world); var rng = world.Random.State;
        foreach (var n in nations)
        {
            var old = ExposureReadOracle.Read(world, n);
            Check(ExposureReadOracle.Equal(old, EconomicExposure.Read(world, n)), label + " C# parity " + n.Id);
            Check(ExposureReadOracle.Equal(old, native.Read(world, n)), label + " Odin parity " + n.Id);
        }
        Action[] reads = {
            () => { foreach (var n in nations) _sink ^= ExposureReadOracle.Read(world, n).Length; },
            () => { foreach (var n in nations) _sink ^= EconomicExposure.Read(world, n).Length; },
            () => { foreach (var n in nations) _sink ^= native.Read(world, n).Length; }
        };
        foreach (var read in reads) for (var warm = 0; warm < 12; warm++) read();
        var times = reads.Select(_ => new List<double>()).ToArray();
        var bytes = reads.Select(_ => new List<double>()).ToArray();
        // Rotating order limits systematic warm-up/thermal bias; no forced GC inside samples.
        for (var sample = 0; sample < samples; sample++)
        for (var offset = 0; offset < reads.Length; offset++)
        {
            var method = (sample + offset) % reads.Length;
            var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
            for (var repeat = 0; repeat < repeats; repeat++) reads[method]();
            times[method].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / repeats);
            bytes[method].Add((GC.GetAllocatedBytesForCurrentThread() - before) / (double)repeats);
        }
        var names = new[] { "old_csharp", "one_pass_csharp", "odin_full_boundary" };
        for (var method = 0; method < reads.Length; method++)
        {
            times[method].Sort(); bytes[method].Sort();
            Console.WriteLine(FormattableString.Invariant($"MEASURE workload={label} nations={nations.Length} routes={world.TradeRoutes.Count} method={names[method]} samples={samples} median_ms={times[method][samples/2]:F4} p95_ms={times[method][(int)Math.Ceiling(.95*samples)-1]:F4} bytes={bytes[method][samples/2]:F0}"));
        }
        Check(stamp == CampaignFingerprint.Of(world) && rng == world.Random.State, label + " benchmark does not mutate world or RNG");
        Console.WriteLine($"STAMP workload={label} fingerprint={stamp:X16} rng={rng}");
    }
}
