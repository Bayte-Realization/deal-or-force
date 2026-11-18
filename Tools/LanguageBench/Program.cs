using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DealOrForce.LanguageBench;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.LanguageBench;

/// <summary>
/// Second languages against C# on three kernels of different shapes. See README.md.
///
/// Every contender of a kernel runs once per round, in an order that rotates each round
/// (A B C, B C A, C A B ...), so load from the other simulations on this machine falls on all
/// of them alike; each round also yields a ratio per pair, which is what is reported first.
/// Warm-up rounds are discarded; outputs are compared with the reference outside the timed part
/// on every sample.
/// </summary>
internal static class Program
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly List<Dictionary<string, object>> Records = new();
    private static int _series;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("LanguageBench [--native DIR] [--luau EXE] [--series N] [--only frontier,graph,budget,luau]");
                Console.WriteLine("              [--frontier-samples 30] [--graph-samples 200] [--budget-samples 300] [--luau-samples 200]");
                Console.WriteLine("              [--seed 20260830] [--days 30] [--json PATH]");
                Console.WriteLine("Run from the repository root (the real map and campaign data are read from game/).");
                return 0;
            }
            var repo = Directory.GetCurrentDirectory();
            var nativeDirectory = Option(args, "--native", Path.Combine(repo, "tmp_logs", "languages", "langbench", "native"));
            var luauExe = Option(args, "--luau", Path.Combine(repo, "build", "luau-worker", "Release", "deal-or-force-luau.exe"));
            var series = Number(args, "--series", 2);
            var only = Option(args, "--only", "frontier,graph,budget,luau").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var frontierSamples = Number(args, "--frontier-samples", 30);
            var graphSamples = Number(args, "--graph-samples", 200);
            var budgetSamples = Number(args, "--budget-samples", 300);
            var luauSamples = Number(args, "--luau-samples", 200);
            var seed = Number(args, "--seed", 20260830);
            var days = Number(args, "--days", 30);
            var jsonPath = Option(args, "--json", Path.Combine(repo, "tmp_logs", "languages", "langbench",
                "results-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Invariant) + ".json"));

            Console.WriteLine($"ENV runtime={Environment.Version} os={Environment.OSVersion} cores={Environment.ProcessorCount} " +
                $"servergc={System.Runtime.GCSettings.IsServerGC} vector_lanes_float={System.Numerics.Vector<float>.Count} " +
                $"avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} avx512={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");

            var natives = new List<NativeKernels>();
            foreach (var (name, file) in new[] { ("rust", "lb_rust.dll"), ("rust_v3", "lb_rust_v3.dll"), ("zig", "lb_zig.dll"),
                         ("odin", "lb_odin.dll"), ("nim", "lb_nim.dll") })
            {
                var path = Path.Combine(nativeDirectory, file);
                var kernel = NativeKernels.TryOpen(name, path);
                Console.WriteLine(kernel is null ? $"NATIVE {name} MISSING {path}" :
                    $"NATIVE {name} {path} sha256={Sha256(path)}");
                if (kernel is not null) natives.Add(kernel);
            }
            var ownershipNatives = new List<(string Name, NativeOwnershipKernel Kernel)>();
            foreach (var name in new[] { "rust", "odin", "nim" })
            {
                var directory = Path.Combine(nativeDirectory, "ownership-" + name);
                if (!Directory.Exists(directory)) { Console.WriteLine($"OWNERSHIP_NATIVE {name} MISSING {directory}"); continue; }
                if (!NativeOwnershipKernel.TryOpen(directory, out var kernel, out var reason))
                    throw new InvalidOperationException($"ownership {name}: {reason}");
                Console.WriteLine($"OWNERSHIP_NATIVE {name} {directory} sha256={Sha256(Path.Combine(directory, "dof_graph_kernel.dll"))}");
                ownershipNatives.Add((name, kernel));
            }

            var world = MakeWorld(seed, days);
            for (_series = 1; _series <= series; _series++)
            {
                Console.WriteLine($"SERIES {_series} of {series} started {DateTime.Now:HH:mm:ss}");
                if (only.Contains("frontier")) Frontier(world.Campaign, natives, frontierSamples);
                if (only.Contains("graph")) Graph(world.Campaign, ownershipNatives, natives, graphSamples);
                if (only.Contains("budget")) Budget(world.Campaign, natives, budgetSamples);
                if (only.Contains("luau")) Luau(world.Campaign, luauExe, luauSamples);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(Records, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("JSON " + jsonPath);
            Console.WriteLine("LANGUAGE_BENCH_PASS");
            foreach (var native in natives) native.Dispose();
            foreach (var (_, kernel) in ownershipNatives) kernel.Dispose();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("LANGUAGE_BENCH_FAIL " + exception);
            return 1;
        }
    }

    // ------------------------------------------------------------------------------- world

    private sealed record World(Campaign Campaign);

    private static World MakeWorld(int seed, int days)
    {
        var clock = Stopwatch.StartNew();
        var campaign = Campaign.CreateBootstrap(seed);
        MapRasterCatalog.ResetCatalog(); HydrologyCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var message))
            throw new InvalidOperationException("Real province map unavailable: " + message);
        if (MapRasterCatalog.TryLoadHydrology(WorldMapSource.Width, WorldMapSource.Height, out var hydrology, out _))
            topology.AttachHydrology(hydrology);
        campaign.AttachProvinceTopology(topology);
        Console.WriteLine($"MAP seed={seed} provinces={topology.ProvinceCount} land_edges={topology.EdgeCount} " +
            $"grid={topology.Width}x{topology.Height} nations={campaign.Nations.Count} setup_ms={clock.Elapsed.TotalMilliseconds:F0}");
        if (days > 0)
        {
            clock.Restart();
            campaign.IsPaused = false;
            for (var day = 0; day < days; day++) campaign.TickDay();
            Console.WriteLine($"CAMPAIGN advanced {days} days to day {campaign.ElapsedDays} in {clock.Elapsed.TotalSeconds:F1} s " +
                $"(so budgets carry debt, trade and war state rather than the first morning's)");
        }
        return new World(campaign);
    }

    // ------------------------------------------------------------------------------- frontier

    private static void Frontier(Campaign campaign, List<NativeKernels> natives, int samples)
    {
        var map = campaign.ProvinceMap;
        var authored = WorldMapSource.Control;
        var width = map.Width; var height = map.Height;
        var authoredOwners = (ushort[])map.ProvinceToNation.Clone();
        // A repaint after a war: 3% of provinces change hands to a neighbour's owner and one in
        // five hundred falls to nobody, so CurrentHolder takes every branch it has.
        var owners = (ushort[])authoredOwners.Clone();
        var moved = 0;
        for (var p = 1; p < owners.Length; p++)
        {
            var hash = unchecked((uint)p * 2654435761u) >> 7;
            if (hash % 500 == 0) { owners[p] = 0; moved++; continue; }
            if (hash % 100 >= 3) continue;
            foreach (var next in map.Neighbors[p])
                if (next > 0 && next < owners.Length && authoredOwners[next] != 0 && authoredOwners[next] != authoredOwners[p])
                { owners[p] = authoredOwners[next]; moved++; break; }
        }
        var reference = FrontierField.Build(authored, map.CellToProvince, width, height, owners, authoredOwners);
        Console.WriteLine($"FRONTIER grid={width}x{height} cells={width * height} provinces_moved={moved} " +
            $"input=real WorldMapSource.Control + MapRasterCatalog topology; owners=bootstrap owners with a deterministic 3% conquest + 0.2% abandonment");

        var threads = Environment.ProcessorCount;
        var scratch = new FrontierCSharp.Scratch();
        var contenders = new List<Contender>
        {
            new("cs_as_written", "reference: FrontierField.Build from the game (Parallel.For in 4 of 5 stages)",
                () => FrontierField.Build(authored, map.CellToProvince, width, height, owners, authoredOwners)),
            new("cs_sequential", "same C# text, Parallel.For written as for",
                () => FrontierCSharp.BuildSequential(authored, map.CellToProvince, width, height, owners, authoredOwners)),
            new("cs_tuned", "C# tuned: parallel row blocks, Vector<float> chamfer candidates + Gaussian, scratch kept between calls",
                () => FrontierCSharp.BuildTuned(authored, map.CellToProvince, width, height, owners, authoredOwners, scratch)),
            new("fsharp_sequential", "F# line-by-line port, single thread",
                () => FSharpKernels.frontierSequential(authored, map.CellToProvince, width, height, owners, authoredOwners)),
        };
        foreach (var native in natives)
        {
            var n = native;
            contenders.Add(new(n.Name + "_sequential", $"{n.Name} line-by-line port via C ABI, single thread",
                () => n.Frontier(authored, map.CellToProvince, width, height, owners, authoredOwners)));
            if (n.HasParallelFrontier && n.Name == "rust")
                contenders.Add(new(n.Name + "_parallel", $"{n.Name}, same port, row stages on {threads} std threads (labelled: threads, not language)",
                    () => n.FrontierParallel(authored, map.CellToProvince, width, height, owners, authoredOwners, threads)));
            if (n.HasTunedFrontier)
                contenders.Add(new(n.Name + "_tuned", $"{n.Name} tuned, the counterpart of cs_tuned: {threads} threads, vectorisable loops, scratch kept",
                    () => n.FrontierTuned(authored, map.CellToProvince, width, height, owners, authoredOwners, threads)));
        }
        var times = Rounds("frontier", contenders, o => ((byte[])o).AsSpan().SequenceEqual(reference), warmup: 4, samples, collect: true);
        Report("frontier", contenders, times, new[] { "cs_as_written", "cs_sequential" });

        // The crossing alone: pin the same four inputs and the output, call a function that returns.
        var output = new byte[width * height];
        foreach (var native in natives)
            Crossing("frontier", native.Name, "pin 5 arrays + call (no-op export, 16 args)",
                () => native.NoopFrontierShape(authored, map.CellToProvince, owners, authoredOwners, output), 1000);
    }

    // ------------------------------------------------------------------------------- graph

    private static void Graph(Campaign campaign, List<(string Name, NativeOwnershipKernel Kernel)> ownershipNatives,
        List<NativeKernels> natives, int samples)
    {
        var map = campaign.ProvinceMap;
        var limit = OwnershipReference.PoreLimit(campaign);
        var graph = OwnershipComponentGraph.FromNeighbors(map.Neighbors);
        Console.WriteLine($"GRAPH vertices_including_sentinel={graph.VertexCount} directed_edges={graph.EdgeCount} pore_limit={limit} " +
            "kernel=Tools/GodotLanguages/kernels/ownership (ABI v1, existing host NativeOwnershipKernel)");

        OwnershipComponentBatch FSharpBatch()
        {
            var water = OwnershipComponents.PackWater(map);
            graph.ValidateInputs(map.ProvinceToNation, water);
            var batch = new OwnershipComponentBatch(graph.VertexCount);
            var struct_ = FSharpKernels.ownershipComponents(graph.Offsets, graph.Neighbors, map.ProvinceToNation, water,
                batch.Labels, batch.MemberOrder, batch.ComponentOffsets);
            batch.ComponentCount = struct_.Item1; batch.MemberCount = struct_.Item2;
            return batch;
        }
        OwnershipComponentBatch NativeBatch(NativeOwnershipKernel kernel)
        {
            var batch = graph.Compute(map.ProvinceToNation, OwnershipComponents.PackWater(map), kernel);
            if (!kernel.IsEnabled) throw new InvalidOperationException("native ownership fell back to managed: " + kernel.Diagnostic);
            return batch;
        }

        // Stage 1: the whole question the game asks - components, rims and pores as HashSets.
        var expected = OwnershipReference.Digest(OwnershipReference.OriginalAllOwnerBfs(map, limit));
        var full = new List<Contender>
        {
            new("cs_as_written", "reference: original all-owner BFS from the Corridors pore cache (Queue + HashSet)",
                () => OwnershipReference.OriginalAllOwnerBfs(map, limit)),
            new("cs_csr", "C# CSR kernel (OwnershipComponentGraph.ComputeManaged) + materialisation",
                () => OwnershipReference.Materialize(map, graph.ComputeManaged(map.ProvinceToNation, OwnershipComponents.PackWater(map)), limit)),
            new("fsharp_csr", "F# CSR port + the same materialisation", () => OwnershipReference.Materialize(map, FSharpBatch(), limit)),
        };
        foreach (var (name, kernel) in ownershipNatives)
            full.Add(new(name + "_abi", $"{name} kernel via the existing ABI host (pin, call, structural validation) + materialisation",
                () => OwnershipReference.Materialize(map, NativeBatch(kernel), limit)));
        var times = Rounds("graph_full", full, o => OwnershipReference.Digest((OwnershipReference.Materialized)o) == expected,
            warmup: 5, samples, collect: false);
        Report("graph_full", full, times, new[] { "cs_as_written", "cs_csr" });

        // Stage 2: the kernel alone, water packing included (as OwnershipKernelBench's second stage).
        var expectedBatch = OwnershipReference.BatchDigest(graph.ComputeManaged(map.ProvinceToNation, OwnershipComponents.PackWater(map)));
        var kernels = new List<Contender>
        {
            new("cs_csr", "reference: C# CSR kernel, water packing included",
                () => graph.ComputeManaged(map.ProvinceToNation, OwnershipComponents.PackWater(map))),
            new("fsharp_csr", "F# CSR port, water packing included", FSharpBatch),
        };
        foreach (var (name, kernel) in ownershipNatives)
            kernels.Add(new(name + "_abi", $"{name} via ABI v1: water packing, pinning, call, structural validation", () => NativeBatch(kernel)));
        times = Rounds("graph_kernel", kernels, o => OwnershipReference.BatchDigest((OwnershipComponentBatch)o) == expectedBatch,
            warmup: 5, samples, collect: false);
        Report("graph_kernel", kernels, times, new[] { "cs_csr" });

        // The crossing in parts.
        Crossing("graph", "managed", "water packing (bool[] -> byte[], every call)", () => OwnershipComponents.PackWater(map).Length, 50);
        var sampleBatch = graph.ComputeManaged(map.ProvinceToNation, OwnershipComponents.PackWater(map));
        var packed = OwnershipComponents.PackWater(map);
        Crossing("graph", "managed", "structural validation of a native batch (IsStructurallyValid)",
            () => sampleBatch.IsStructurallyValid(map.ProvinceToNation, packed) ? 1 : 0, 50);
        Crossing("graph", "managed", "fresh output batch (3 int arrays of V)", () => new OwnershipComponentBatch(graph.VertexCount).Labels.Length, 50);
        var counts = new int[2];
        foreach (var native in natives.Where(n => n.Name == "rust"))
            Crossing("graph", native.Name, "pin 8 arrays + call (no-op export, 16 args)",
                () => native.NoopOwnershipShape(graph.Offsets, graph.Neighbors, map.ProvinceToNation, packed, sampleBatch.Labels,
                    sampleBatch.MemberOrder, sampleBatch.ComponentOffsets, counts), 1000);
    }

    // ------------------------------------------------------------------------------- budget

    private static void Budget(Campaign campaign, List<NativeKernels> natives, int samples)
    {
        var nations = campaign.Nations.ToArray();
        var weeklyOutput = nations.Select(n => n.Gdp / 52f).ToArray();
        var atWar = nations.Select(n => AiFiscalPolicy.AtWar(campaign, n)).ToArray();
        var input = BudgetInput.Catalogue(nations.Length);
        input.Pack(nations, weeklyOutput, atWar);
        var reference = new float[nations.Length * BudgetInput.OutputColumns];
        BudgetFlat.AsWritten(nations, weeklyOutput, atWar, reference);
        Console.WriteLine($"BUDGET nations={nations.Length} lines={input.Lines} factors={input.FactorSlots.Length} at_war={atWar.Count(w => w)} " +
            $"debt_active={nations.Count(n => n.DebtMechanicActive)} " +
            $"input=real campaign Nation objects, BudgetCore catalogue from game/common/budget");

        bool Same(object o) => BitEqual((float[])o, reference);
        var tunedScratch = new float[input.Lines];
        var contenders = new List<Contender>
        {
            new("cs_as_written", "reference: BudgetCore.Evaluate on Nation objects, every country",
                () => { var o = new float[reference.Length]; BudgetFlat.AsWritten(nations, weeklyOutput, atWar, o); return o; }),
            new("cs_flat", "C# flat port, line by line (pre-packed input)",
                () => { var o = new float[reference.Length]; BudgetFlat.Evaluate(input, o); return o; }),
            new("cs_flat_tuned", "C# tuned: spans, one scale per line per country (pre-packed input)",
                () => { var o = new float[reference.Length]; BudgetFlat.EvaluateTuned(input, o, tunedScratch); return o; }),
            new("cs_flat_parallel", "C# flat port, countries on Parallel.For (pre-packed input)",
                () => { var o = new float[reference.Length]; BudgetFlat.EvaluateParallel(input, o); return o; }),
            new("fsharp_flat", "F# flat port (pre-packed input)",
                () => { var o = new float[reference.Length];
                        FSharpKernels.budgetEvaluate(input.Nations, input.Lines, input.TaxTolerance, input.TaxEvasion, input.LineFloats,
                            input.LineInts, input.FactorOffsets, input.FactorSlots, input.FactorWeights, input.Slots, input.NationFloats,
                            input.Rates, o); return o; }),
            new("cs_flat+pack", "C# flat port including the packing from Nation objects",
                () => { var o = new float[reference.Length]; input.Pack(nations, weeklyOutput, atWar); BudgetFlat.Evaluate(input, o); return o; }),
        };
        foreach (var native in natives)
        {
            var n = native;
            contenders.Add(new(n.Name + "_flat", $"{n.Name} flat port via C ABI (pre-packed input; pin + call included)",
                () => { var o = new float[reference.Length]; n.Budget(input, o); return o; }));
        }
        var rust = natives.FirstOrDefault(n => n.Name == "rust");
        if (rust is not null)
            contenders.Add(new("rust_flat+pack", "Rust via C ABI including the packing from Nation objects",
                () => { var o = new float[reference.Length]; input.Pack(nations, weeklyOutput, atWar); rust.Budget(input, o); return o; }));
        var times = Rounds("budget", contenders, Same, warmup: 50, samples, collect: false);
        Report("budget", contenders, times, new[] { "cs_as_written", "cs_flat" });

        Crossing("budget", "managed", "packing Nation objects into the flat arrays (FillSlots, RateOf, Outstanding, modifier)",
            () => { input.Pack(nations, weeklyOutput, atWar); return 0; }, 200);
        var scratchOut = new float[reference.Length];
        foreach (var native in natives)
            Crossing("budget", native.Name, "pin 9 arrays + call (no-op export, 16 args)", () => native.NoopBudgetShape(input, scratchOut), 2000);
    }

    // ------------------------------------------------------------------------------- luau

    private static void Luau(Campaign campaign, string workerExe, int samples)
    {
        if (!File.Exists(workerExe)) { Console.WriteLine("LUAU MISSING " + workerExe); return; }
        var nations = campaign.Nations.ToArray();
        var weeklyOutput = nations.Select(n => n.Gdp / 52f).ToArray();
        var atWar = nations.Select(n => AiFiscalPolicy.AtWar(campaign, n)).ToArray();
        var input = BudgetInput.Catalogue(nations.Length);
        input.Pack(nations, weeklyOutput, atWar);
        var reference = new float[nations.Length * BudgetInput.OutputColumns];
        BudgetFlat.AsWritten(nations, weeklyOutput, atWar, reference);

        var script = Path.GetFullPath(Path.Combine("Tools", "LanguageBench", "native", "luau", "budget.luau"));
        var scratch = Path.Combine(Path.GetTempPath(), "dof-languagebench");
        Directory.CreateDirectory(scratch);
        var noop = Path.Combine(scratch, "noop.luau");
        File.WriteAllText(noop, "function on_hook(request: string): string return \"{}\" end\n");
        var request = LuauRequest(input);
        Console.WriteLine($"LUAU worker={workerExe} sha256={Sha256(workerExe)} script={script} request_bytes={request.Length} " +
            "bridge=one worker process per request (the game's Luau mod bridge, JSON over stdin/stdout)");

        var kernelMicros = new List<double>();
        var contenders = new List<Contender>
        {
            new("cs_as_written", "reference: BudgetCore.Evaluate in-process",
                () => { var o = new float[reference.Length]; BudgetFlat.AsWritten(nations, weeklyOutput, atWar, o); return o; }),
            new("luau_worker", "Luau budget kernel through the worker: spawn, compile, JSON in/out, float32-exact",
                () =>
                {
                    var response = RunWorker(workerExe, script, request);
                    var (micros, values) = ParseLuau(response, reference.Length);
                    kernelMicros.Add(micros);
                    return values;
                }),
            new("luau_worker_noop", "the crossing: same worker, same request, on_hook returns {}",
                () => { RunWorker(workerExe, noop, request); return reference; }),
        };
        var times = Rounds("luau_budget", contenders, o => BitEqual((float[])o, reference), warmup: 3, samples, collect: false);
        Report("luau_budget", contenders, times, new[] { "cs_as_written", "luau_worker_noop" });
        var sorted = kernelMicros.TakeLast(samples).OrderBy(v => v).ToArray(); // the rest were warm-up
        var record = new Dictionary<string, object>
        {
            ["series"] = _series, ["kernel"] = "luau_budget", ["contender"] = "luau_in_vm_kernel",
            ["note"] = "os.clock around the kernel inside the worker (parsing and encoding excluded)",
            ["samples"] = sorted.Length, ["median_ms"] = Percentile(sorted, 0.5) / 1000.0, ["p95_ms"] = Percentile(sorted, 0.95) / 1000.0,
        };
        Records.Add(record);
        Console.WriteLine($"BENCH series={_series} kernel=luau_budget contender=luau_in_vm_kernel samples={sorted.Length} " +
            $"median_ms={Percentile(sorted, 0.5) / 1000.0:F4} p95_ms={Percentile(sorted, 0.95) / 1000.0:F4} (inside the VM, os.clock)");
    }

    private static string LuauRequest(BudgetInput b)
    {
        var text = new StringBuilder(1 << 18);
        text.Append("{\"v\":[");
        var first = true;
        void Int(long value) { if (!first) text.Append(','); first = false; text.Append(value.ToString(Invariant)); }
        void Float(float value) => Int(BitConverter.SingleToUInt32Bits(value));
        Int(b.Nations); Int(b.Lines); Float(b.TaxTolerance); Float(b.TaxEvasion);
        foreach (var v in b.LineFloats) Float(v);
        foreach (var v in b.LineInts) Int(v);
        foreach (var v in b.FactorOffsets) Int(v);
        foreach (var v in b.FactorSlots) Int(v);
        foreach (var v in b.FactorWeights) Float(v);
        foreach (var v in b.Slots) Float(v);
        foreach (var v in b.NationFloats) Float(v);
        foreach (var v in b.Rates) Float(v);
        text.Append("]}");
        return text.ToString();
    }

    private static string RunWorker(string exe, string script, string request)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("worker did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(request);
        process.StandardInput.Close();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"luau worker exit {process.ExitCode}: {stderr.Result}");
        return stdout.Result;
    }

    private static (double Micros, float[] Values) ParseLuau(string response, int count)
    {
        using var document = JsonDocument.Parse(response);
        var micros = document.RootElement.GetProperty("us").GetDouble();
        var values = new float[count];
        var at = 0;
        foreach (var item in document.RootElement.GetProperty("out").EnumerateArray())
            values[at++] = BitConverter.UInt32BitsToSingle(item.GetUInt32());
        if (at != count) throw new InvalidOperationException($"luau returned {at} values, expected {count}");
        return (micros, values);
    }

    // ------------------------------------------------------------------------------- measuring

    private sealed record Contender(string Name, string Description, Func<object> Run);

    private static Dictionary<string, double[]> Rounds(string kernel, List<Contender> contenders, Func<object, bool> same,
        int warmup, int samples, bool collect)
    {
        // Warm-up, discarded: at least `warmup` rounds and, for the small kernels, at least three
        // seconds, so the JIT has promoted the managed contenders past tier 0 before timing starts.
        var warmClock = Stopwatch.StartNew();
        for (var round = 0; round < warmup || (!collect && warmClock.Elapsed.TotalSeconds < 3); round++)
            foreach (var contender in contenders)
                if (!same(contender.Run()))
                    throw new InvalidOperationException($"{kernel}/{contender.Name}: output differs from the reference (warm-up)");
        var times = contenders.ToDictionary(c => c.Name, _ => new double[samples]);
        var watch = new Stopwatch();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        for (var round = 0; round < samples; round++)
            for (var k = 0; k < contenders.Count; k++)
            {
                var contender = contenders[(round + k) % contenders.Count];
                if (collect) { GC.Collect(); GC.WaitForPendingFinalizers(); }
                watch.Restart();
                var result = contender.Run();
                watch.Stop();
                times[contender.Name][round] = watch.Elapsed.TotalMilliseconds;
                // Outside the timed part: every sample's output against the reference.
                if (!same(result)) throw new InvalidOperationException($"{kernel}/{contender.Name}: output differs from the reference (round {round})");
            }
        return times;
    }

    private static void Report(string kernel, List<Contender> contenders, Dictionary<string, double[]> times, string[] references)
    {
        foreach (var contender in contenders)
        {
            var samples = times[contender.Name];
            var sorted = samples.OrderBy(v => v).ToArray();
            var record = new Dictionary<string, object>
            {
                ["series"] = _series, ["kernel"] = kernel, ["contender"] = contender.Name, ["note"] = contender.Description,
                ["samples"] = samples.Length, ["median_ms"] = Percentile(sorted, 0.5), ["p95_ms"] = Percentile(sorted, 0.95),
                ["identical_output"] = true, ["raw_ms"] = samples,
            };
            var line = new StringBuilder($"BENCH series={_series} kernel={kernel} contender={contender.Name} samples={samples.Length} " +
                $"median_ms={Percentile(sorted, 0.5):F4} p95_ms={Percentile(sorted, 0.95):F4}");
            foreach (var reference in references)
            {
                if (reference == contender.Name || !times.ContainsKey(reference)) continue;
                var against = times[reference];
                var ratios = samples.Select((value, round) => value / against[round]).OrderBy(v => v).ToArray();
                var wins = samples.Where((value, round) => value < against[round]).Count();
                record["ratio_vs_" + reference + "_median"] = Percentile(ratios, 0.5);
                record["ratio_vs_" + reference + "_p25"] = Percentile(ratios, 0.25);
                record["ratio_vs_" + reference + "_p75"] = Percentile(ratios, 0.75);
                record["rounds_faster_than_" + reference] = wins;
                line.Append($" ratio_vs_{reference}={Percentile(ratios, 0.5):F3} [p25 {Percentile(ratios, 0.25):F3}, p75 {Percentile(ratios, 0.75):F3}] faster_in={wins}/{samples.Length}");
            }
            Records.Add(record);
            Console.WriteLine(line.ToString());
        }
    }

    private static void Crossing(string kernel, string contender, string what, Func<int> run, int batch)
    {
        const int samples = 201;
        var values = new double[samples];
        var sink = 0;
        for (var i = 0; i < batch; i++) sink += run();
        var watch = new Stopwatch();
        for (var s = 0; s < samples; s++)
        {
            watch.Restart();
            for (var i = 0; i < batch; i++) sink += run();
            watch.Stop();
            values[s] = watch.Elapsed.TotalMilliseconds / batch;
        }
        Array.Sort(values);
        Records.Add(new Dictionary<string, object>
        {
            ["series"] = _series, ["kernel"] = kernel, ["contender"] = contender, ["crossing"] = what,
            ["samples"] = samples, ["batch"] = batch, ["median_ms"] = Percentile(values, 0.5), ["p95_ms"] = Percentile(values, 0.95),
        });
        Console.WriteLine($"CROSSING series={_series} kernel={kernel} contender={contender} what=\"{what}\" median_ms={Percentile(values, 0.5):F6} " +
            $"p95_ms={Percentile(values, 0.95):F6} (median of {samples} batches of {batch}) sink={sink & 1}");
    }

    private static double Percentile(double[] sorted, double fraction) =>
        sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * fraction))];

    private static bool BitEqual(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (BitConverter.SingleToUInt32Bits(a[i]) != BitConverter.SingleToUInt32Bits(b[i])) return false;
        return true;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string Option(string[] args, string name, string fallback)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
    }

    private static int Number(string[] args, string name, int fallback)
    {
        var at = Array.IndexOf(args, name);
        if (at < 0) return fallback;
        if (at + 1 >= args.Length || !int.TryParse(args[at + 1], NumberStyles.Integer, Invariant, out var value) || value < 0)
            throw new ArgumentException(name + " needs a non-negative number");
        return value;
    }
}
