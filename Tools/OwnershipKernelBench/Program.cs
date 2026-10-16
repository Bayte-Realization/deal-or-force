using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.OwnershipKernelBench;

/// <summary>Independent ordered reference, real-map timing and campaign continuation checks.</summary>
internal static class Program
{
    private static int _checks;
    private static ulong _sink;

    public static int Main(string[] args)
    {
        NativeOwnershipKernel native = null;
        try
        {
            if (args.Length == 0 || args.Contains("--help"))
            {
                Console.WriteLine("OwnershipKernelBench --self-test [--native ABSOLUTE_DIRECTORY]");
                Console.WriteLine("OwnershipKernelBench --map [SEED] [--iterations N] [--native ABSOLUTE_DIRECTORY] [--campaign-days N]");
                Console.WriteLine("OwnershipKernelBench --synthetic [--iterations N] [--native ABSOLUTE_DIRECTORY]");
                Console.WriteLine("Campaign records are deterministic stdout; redirect each process to its own log for backend comparisons.");
                return 0;
            }
            var nativeAt = Array.IndexOf(args, "--native");
            Console.WriteLine("CONFIG experimental_kernel=" + (Environment.GetEnvironmentVariable("DOF_EXPERIMENTAL_KERNEL") ?? "managed") +
                " campaign_kernel=original_csharp direct_native=" + (nativeAt >= 0));
            if (nativeAt >= 0)
            {
                Require(nativeAt + 1 < args.Length && Path.IsPathFullyQualified(args[nativeAt + 1]),
                    "--native needs an absolute directory");
                Require(NativeOwnershipKernel.TryOpen(args[nativeAt + 1], out native, out var reason),
                    "native open: " + reason);
                Require(native is not null && native.IsEnabled, "native backend enabled after open");
                Console.WriteLine("NATIVE " + native.Diagnostic);
            }
            if (args.Contains("--self-test")) SelfTest(native);
            if (args.Contains("--synthetic"))
            {
                var iterations = Number(args, "--iterations", 25, 3, 10000);
                foreach (var count in new[] { 512, 300000 })
                    BenchmarkMap(SyntheticMap(count), 3, iterations, native, "synthetic_" + count);
            }
            var mapAt = Array.IndexOf(args, "--map");
            var campaignDays = Number(args, "--campaign-days", 0, 0, 3650);
            if (mapAt >= 0 || campaignDays > 0)
            {
                var seed = mapAt >= 0 && mapAt + 1 < args.Length &&
                    int.TryParse(args[mapAt + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value : 20260830;
                var campaign = MakeWorld(seed);
                Benchmark(campaign, Number(args, "--iterations", 31, 3, 10000), native);
                if (campaignDays > 0) CampaignContinuation(campaign, campaignDays, native);
            }
            Console.WriteLine($"OWNERSHIP_BENCH_PASS checks={_checks} digest={_sink:X16}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("OWNERSHIP_BENCH_FAIL " + exception);
            return 1;
        }
        finally { native?.Dispose(); }
    }

    private static int Number(string[] args, string option, int fallback, int minimum, int maximum)
    {
        var at = Array.IndexOf(args, option);
        if (at < 0) return fallback;
        if (at + 1 >= args.Length || !int.TryParse(args[at + 1], out var value) || value < minimum || value > maximum)
            throw new ArgumentException($"{option} must be in {minimum}..{maximum}.");
        return value;
    }

    private static void Require(bool valid, string label)
    {
        if (!valid) throw new InvalidOperationException(label);
        _checks++;
    }

    private static byte[] PackWater(bool[] water)
    {
        var packed = new byte[water.Length];
        for (var index = 0; index < water.Length; index++) packed[index] = water[index] ? (byte)1 : (byte)0;
        return packed;
    }

    // The clean publication tree predates the working tree's pore rules. Use its
    // authored default for this independent all-owner reference when that define is absent.
    private static int PoreLimit(Campaign campaign) =>
        typeof(Defines).GetProperty("PeaceCompactHoleProvinces")?.GetValue(campaign.Defines) is int value ? Math.Max(1, value) : 3;

    // Keep this reference independent of CSR/kernel code. It follows the original
    // all-owner EnsurePores BFS, including discovery/dequeue order and owner-zero exclusion.
    private static List<HashSet<int>> OriginalComponents(int[][] neighbors, ushort[] owners, bool[] water)
    {
        var count = owners.Length - 1;
        var seen = new bool[count + 1];
        var queue = new Queue<int>();
        var components = new List<HashSet<int>>();
        for (var start = 1; start <= count; start++)
        {
            if (seen[start] || water[start]) continue;
            var owner = owners[start];
            if (owner == 0) { seen[start] = true; continue; }
            var cluster = new HashSet<int>();
            queue.Clear(); queue.Enqueue(start); seen[start] = true;
            while (queue.Count > 0)
            {
                var province = queue.Dequeue(); cluster.Add(province);
                foreach (var next in neighbors[province])
                {
                    if (next <= 0 || next > count || water[next]) continue;
                    if (owners[next] == owner && !seen[next])
                    { seen[next] = true; queue.Enqueue(next); }
                }
            }
            components.Add(cluster);
        }
        return components;
    }

    private static void EqualBatch(int[][] neighbors, ushort[] owners, bool[] water,
        OwnershipComponentBatch actual, string label)
    {
        var reference = OriginalComponents(neighbors, owners, water);
        var labels = new int[owners.Length];
        var order = new List<int>(); var offsets = new List<int> { 0 };
        for (var component = 0; component < reference.Count; component++)
        {
            foreach (var province in reference[component]) { labels[province] = component + 1; order.Add(province); }
            offsets.Add(order.Count);
        }
        Require(actual.ComponentCount == reference.Count && actual.MemberCount == order.Count, label + " counts");
        Require(actual.Labels.AsSpan(0, labels.Length).SequenceEqual(labels), label + " labels");
        Require(actual.MemberOrder.AsSpan(0, actual.MemberCount).SequenceEqual(order.ToArray()), label + " BFS member order");
        Require(actual.ComponentOffsets.AsSpan(0, actual.ComponentCount + 1).SequenceEqual(offsets.ToArray()), label + " offsets");
    }

    private static void SelfTest(NativeOwnershipKernel native)
    {
        void Test(int[][] neighbors, ushort[] owners, bool[] water, string label)
        {
            var oldOwners = (ushort[])owners.Clone(); var oldWater = (bool[])water.Clone();
            var oldNeighbors = neighbors.Select(row => (int[])row.Clone()).ToArray();
            var graph = OwnershipComponentGraph.FromNeighbors(neighbors);
            var packedWater = PackWater(water); var oldPacked = (byte[])packedWater.Clone();
            var oldOffsets = (int[])graph.Offsets.Clone(); var oldEdges = (int[])graph.Neighbors.Clone();
            var managed = graph.ComputeManaged(owners, packedWater);
            EqualBatch(neighbors, owners, water, managed, label + " managed");
            var map = FixtureMap(neighbors, owners, water);
            var expected = Digest(OriginalAllOwnerBfs(map, 3));
            Require(Digest(Materialize(map, managed, 3)) == expected, label + " full managed materialization");
            if (native is not null)
            {
                var result = graph.Compute(owners, packedWater, native);
                EqualBatch(neighbors, owners, water, result, label + " native");
                Require(Digest(Materialize(map, result, 3)) == expected, label + " full native materialization");
                Require(native.IsEnabled, label + " must execute native without fallback: " + native.Diagnostic);
            }
            Require(owners.SequenceEqual(oldOwners) && water.SequenceEqual(oldWater) &&
                packedWater.SequenceEqual(oldPacked) && graph.Offsets.SequenceEqual(oldOffsets) && graph.Neighbors.SequenceEqual(oldEdges) &&
                neighbors.Select((row, index) => row.SequenceEqual(oldNeighbors[index])).All(equal => equal), label + " immutable inputs and pinned CSR");
        }

        Test(new[] { Array.Empty<int>() }, new ushort[1], new bool[1], "empty");
        var diamond = new[] { Array.Empty<int>(), new[] { 4, 2 }, new[] { 1, 3 }, new[] { 4, 2 }, new[] { 1, 3 } };
        Test(diamond, new ushort[] { 0, 1, 1, 1, 1 }, new bool[5], "nonascending BFS diamond");
        var firstMap = FixtureMap(diamond, new ushort[] { 0, 1, 1, 1, 1 }, new bool[5], new ushort[] { 0, 1, 9, 2, 9 });
        Require(OriginalAllOwnerBfs(firstMap, 3).FirstProvince[9] == 4,
            "first state representative follows BFS order rather than smallest province ID");
        var pores = FixtureMap(new[] { Array.Empty<int>(), new[] { 2, 3, 4 }, new[] { 1 }, new[] { 1, 5 }, new[] { 1 }, new[] { 3 } },
            new ushort[] { 0, 1, 2, 1, 2, 1 }, new bool[6]);
        Require(OriginalAllOwnerBfs(pores, 1).Pores[1].SequenceEqual(new[] { 2 }),
            "equal-sized pores keep the first component in global root order");
        Test(new[] { Array.Empty<int>(), new[] { 0, -1, 2, 8, 2, 1 }, new[] { 1, 3 }, new[] { 2, 4 }, new[] { 3 } },
            new ushort[] { 0, 1, 0, 1, 1 }, new bool[] { false, false, false, true, false }, "sentinel duplicate water unclaimed");
        Test(Enumerable.Range(0, 41).Select(_ => Array.Empty<int>()).ToArray(),
            Enumerable.Range(0, 41).Select(index => (ushort)(index == 0 ? 0 : 1)).ToArray(), new bool[41], "forty tied components");
        var random = new Random(6101601);
        for (var test = 0; test < 300; test++)
        {
            var count = random.Next(1, 129);
            var neighbors = new int[count + 1][]; neighbors[0] = Array.Empty<int>();
            var owners = new ushort[count + 1]; var water = new bool[count + 1];
            for (var province = 1; province <= count; province++)
            {
                owners[province] = (ushort)random.Next(0, 7); water[province] = random.Next(0, 7) == 0;
                neighbors[province] = Enumerable.Range(0, random.Next(0, 11)).Select(_ => random.Next(-1, count + 3)).ToArray();
            }
            Test(neighbors, owners, water, "random " + test);
        }
        Console.WriteLine("SELF_TEST ordered fixtures=4 randomized_graphs=300 PASS");
    }

    // Private topology construction is confined to this test fixture. It lets the reference
    // exercise unusual directed/duplicate/sentinel edges without depending on raster generation.
    private static ProvinceTopology FixtureMap(int[][] neighbors, ushort[] owners, bool[] water, ushort[] states = null)
    {
        states ??= Enumerable.Range(0, owners.Length).Select(index => (ushort)(index == 0 ? 0 : index % 5 + 1)).ToArray();
        var map = (ProvinceTopology)Activator.CreateInstance(typeof(ProvinceTopology),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { Array.Empty<ushort>(), states, owners, new int[owners.Length], neighbors, 0, 0 }, CultureInfo.InvariantCulture);
        Array.Copy(water, map.WaterByProvince, water.Length);
        return map;
    }

    private static ProvinceTopology SyntheticMap(int count)
    {
        var watch = Stopwatch.StartNew(); const int width = 768;
        var owners = new ushort[count + 1]; var water = new bool[count + 1];
        var neighbors = new int[count + 1][]; neighbors[0] = Array.Empty<int>();
        for (var province = 1; province <= count; province++)
        {
            owners[province] = province % 211 == 0 ? (ushort)0 : (ushort)(province / 4096 % 7 + 1);
            water[province] = province % 97 == 0;
            var row = new List<int>(4);
            if (province + width <= count) row.Add(province + width);
            if (province > 1 && (province - 1) % width != 0) row.Add(province - 1);
            if (province - width > 0) row.Add(province - width);
            if (province < count && province % width != 0) row.Add(province + 1);
            neighbors[province] = row.ToArray();
        }
        var map = FixtureMap(neighbors, owners, water);
        Console.WriteLine($"SYNTHETIC vertices={count} directed_edges={neighbors.Sum(row => row.Length)} setup_ms={watch.Elapsed.TotalMilliseconds:F3}");
        return map;
    }

    private sealed class Materialized
    {
        public readonly Dictionary<int, List<HashSet<int>>> Owners = new();
        public readonly Dictionary<int, int> FirstProvince = new();
        public readonly Dictionary<int, HashSet<int>> Pores = new();
    }

    // This is the original cached C# walk, rather than repeated calls through the hot cache.
    private static Materialized OriginalAllOwnerBfs(ProvinceTopology map, int limit)
    {
        var result = new Materialized();
        var seen = new bool[map.ProvinceCount + 1]; var queue = new Queue<int>();
        for (var start = 1; start <= map.ProvinceCount; start++)
        {
            if (seen[start] || map.IsWater(start)) continue;
            var owner = map.ProvinceToNation[start];
            if (owner == 0) { seen[start] = true; continue; }
            var cluster = new HashSet<int>(); var rim = new HashSet<int>(); var open = false;
            queue.Clear(); queue.Enqueue(start); seen[start] = true;
            while (queue.Count > 0)
            {
                var province = queue.Dequeue(); cluster.Add(province);
                var state = map.ProvinceToState[province];
                if (state != 0 && !result.FirstProvince.ContainsKey(state)) result.FirstProvince[state] = province;
                foreach (var next in map.Neighbors[province])
                {
                    if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                    var who = map.ProvinceToNation[next];
                    if (who == owner)
                    {
                        if (!seen[next]) { seen[next] = true; queue.Enqueue(next); }
                        continue;
                    }
                    if (who == 0) { open = true; continue; }
                    rim.Add(who);
                }
            }
            Add(result, owner, cluster, rim, open, limit);
        }
        return result;
    }

    private static Materialized Materialize(ProvinceTopology map, OwnershipComponentBatch batch, int limit)
    {
        var result = new Materialized();
        for (var component = 0; component < batch.ComponentCount; component++)
        {
            var begin = batch.ComponentOffsets[component]; var end = batch.ComponentOffsets[component + 1];
            var owner = map.ProvinceToNation[batch.MemberOrder[begin]];
            var cluster = new HashSet<int>(); var rim = new HashSet<int>(); var open = false;
            for (var index = begin; index < end; index++)
            {
                var province = batch.MemberOrder[index]; cluster.Add(province);
                var state = map.ProvinceToState[province];
                if (state != 0 && !result.FirstProvince.ContainsKey(state)) result.FirstProvince[state] = province;
                foreach (var next in map.Neighbors[province])
                {
                    if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                    var who = map.ProvinceToNation[next];
                    if (who == owner) continue;
                    if (who == 0) { open = true; continue; }
                    rim.Add(who);
                }
            }
            Add(result, owner, cluster, rim, open, limit);
        }
        return result;
    }

    private static void Add(Materialized result, int owner, HashSet<int> cluster, HashSet<int> rim, bool open, int limit)
    {
        if (!result.Owners.TryGetValue(owner, out var list)) result.Owners[owner] = list = new();
        list.Add(cluster);
        if (open || cluster.Count == 0 || cluster.Count > limit || rim.Count != 1) return;
        var surrounder = rim.First();
        if (!result.Pores.TryGetValue(surrounder, out var best) || cluster.Count < best.Count) result.Pores[surrounder] = cluster;
    }

    private static ulong Digest(Materialized result)
    {
        var hash = 14695981039346656037UL;
        void Mix(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211UL; } }
        foreach (var (owner, pieces) in result.Owners)
        {
            Mix(-1); Mix(owner); Mix(pieces.Count);
            foreach (var piece in pieces) { Mix(-2); Mix(piece.Count); foreach (var province in piece) Mix(province); }
        }
        foreach (var (state, province) in result.FirstProvince) { Mix(-3); Mix(state); Mix(province); }
        foreach (var (surrounder, pore) in result.Pores)
        { Mix(-4); Mix(surrounder); Mix(pore.Count); foreach (var province in pore) Mix(province); }
        return hash;
    }

    private static Campaign MakeWorld(int seed)
    {
        var clock = Stopwatch.StartNew(); var campaign = Campaign.CreateBootstrap(seed);
        MapRasterCatalog.ResetCatalog(); HydrologyCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var message))
            throw new InvalidOperationException("Real province map unavailable: " + message);
        if (MapRasterCatalog.TryLoadHydrology(WorldMapSource.Width, WorldMapSource.Height, out var hydrology, out _))
            topology.AttachHydrology(hydrology);
        campaign.AttachProvinceTopology(topology);
        Console.WriteLine($"MAP seed={seed} vertices={topology.ProvinceCount} land_edges={topology.EdgeCount} setup_ms={clock.Elapsed.TotalMilliseconds:F3}");
        return campaign;
    }

    private static void Benchmark(Campaign campaign, int iterations, NativeOwnershipKernel native)
    {
        var before = WorldStamp(campaign);
        BenchmarkMap(campaign.ProvinceMap, PoreLimit(campaign), iterations, native, "real_map");
        Require(WorldStamp(campaign) == before, "benchmark does not mutate the campaign or map");
    }

    private static void BenchmarkMap(ProvinceTopology map, int limit, int iterations, NativeOwnershipKernel native, string workload)
    {
        var watch = Stopwatch.StartNew(); var graph = OwnershipComponentGraph.FromNeighbors(map.Neighbors);
        Console.WriteLine($"CSR workload={workload} vertices_including_sentinel={graph.VertexCount} directed_edges={graph.EdgeCount} setup_ms={watch.Elapsed.TotalMilliseconds:F3}");
        var expected = Digest(OriginalAllOwnerBfs(map, limit));
        Materialized Managed() => Materialize(map, graph.ComputeManaged(map.ProvinceToNation, PackWater(map.WaterByProvince)), limit);
        Materialized Native()
        {
            var batch = graph.Compute(map.ProvinceToNation, PackWater(map.WaterByProvince), native);
            if (!native.IsEnabled) throw new InvalidOperationException("Native benchmark must not time managed fallback: " + native.Diagnostic);
            return Materialize(map, batch, limit);
        }
        var methods = new List<(string Name, Func<Materialized> Run)> {
            ("original_all_owner_bfs", () => OriginalAllOwnerBfs(map, limit)), ("managed_csr_materialized", Managed) };
        if (native is not null) methods.Add(("native_abi_materialized", Native));
        Measure(methods, Digest, expected, iterations, workload, "full_materialization");

        OwnershipComponentBatch ManagedKernel() => graph.ComputeManaged(map.ProvinceToNation, PackWater(map.WaterByProvince));
        OwnershipComponentBatch NativeKernel()
        {
            var batch = graph.Compute(map.ProvinceToNation, PackWater(map.WaterByProvince), native);
            if (!native.IsEnabled) throw new InvalidOperationException("Native kernel must not time managed fallback: " + native.Diagnostic);
            return batch;
        }
        var kernels = new List<(string Name, Func<OwnershipComponentBatch> Run)> { ("managed_csr", ManagedKernel) };
        if (native is not null) kernels.Add(("native_abi_validated", NativeKernel));
        Measure(kernels, BatchDigest, BatchDigest(ManagedKernel()), iterations, workload, "kernel_water_packing_included");
    }

    private static ulong BatchDigest(OwnershipComponentBatch batch)
    {
        var hash = 14695981039346656037UL;
        void Mix(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211UL; } }
        Mix(batch.ComponentCount); Mix(batch.MemberCount);
        foreach (var label in batch.Labels) Mix(label);
        for (var at = 0; at < batch.MemberCount; at++) Mix(batch.MemberOrder[at]);
        for (var at = 0; at <= batch.ComponentCount; at++) Mix(batch.ComponentOffsets[at]);
        return hash;
    }

    private static void Measure<T>(List<(string Name, Func<T> Run)> methods, Func<T, ulong> digestOf,
        ulong expected, int iterations, string workload, string stage)
    {
        var watch = new Stopwatch();
        var samples = methods.Select(_ => new List<double>()).ToArray();
        var allocations = new long[methods.Count];
        for (var warm = 0; warm < 5; warm++)
            foreach (var method in methods) Require(digestOf(method.Run()) == expected, method.Name + " warm parity");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        for (var iteration = 0; iteration < iterations; iteration++)
            for (var offset = 0; offset < methods.Count; offset++)
            {
                var index = (iteration + offset) % methods.Count; var method = methods[index];
                var allocated = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
                var result = method.Run(); watch.Stop();
                allocations[index] += GC.GetAllocatedBytesForCurrentThread() - allocated;
                samples[index].Add(watch.Elapsed.TotalMilliseconds);
                // Digest and reference checks deliberately occur outside the timed/allocation region.
                var digest = digestOf(result); Require(digest == expected, method.Name + " " + workload + " ordered parity"); _sink ^= digest;
            }
        for (var index = 0; index < methods.Count; index++)
        {
            var ordered = samples[index].OrderBy(value => value).ToArray();
            double Percentile(double fraction) => ordered[Math.Min(ordered.Length - 1, (int)(ordered.Length * fraction))];
            Console.WriteLine($"BENCH workload={workload} stage={stage} method={methods[index].Name} samples={iterations} p50_ms={Percentile(.5):F3} p95_ms={Percentile(.95):F3} allocated_bytes_per_pass={allocations[index] / iterations} digest={expected:X16}");
        }
    }

    private static string WorldStamp(Campaign campaign)
    {
        var hash = 14695981039346656037UL;
        void Mix(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211UL; } }
        foreach (var value in campaign.ProvinceMap.ProvinceToNation) Mix(value);
        foreach (var value in campaign.ProvinceMap.ProvinceToState) Mix(value);
        foreach (var value in campaign.ProvinceController) Mix(value);
        return $"{CampaignFingerprint.Of(campaign):X16}/{campaign.Random.State:X8}/{campaign.MapRevision}/{campaign.Commands.Count}/{hash:X16}";
    }

    private static ulong CheckReads(Campaign campaign, NativeOwnershipKernel native)
    {
        var before = WorldStamp(campaign); var map = campaign.ProvinceMap;
        var graph = OwnershipComponentGraph.FromNeighbors(map.Neighbors);
        var packed = PackWater(map.WaterByProvince);
        EqualBatch(map.Neighbors, map.ProvinceToNation, map.WaterByProvince,
            graph.ComputeManaged(map.ProvinceToNation, packed), "campaign managed");
        EqualBatch(map.Neighbors, map.ProvinceToNation, map.WaterByProvince,
            OwnershipComponents.Read(campaign), "experimental cached probe");
        if (native is not null)
        {
            EqualBatch(map.Neighbors, map.ProvinceToNation, map.WaterByProvince,
                graph.Compute(map.ProvinceToNation, packed, native), "campaign native");
            Require(native.IsEnabled, "campaign native remains enabled: " + native.Diagnostic);
        }
        foreach (var nation in campaign.Nations) _ = Corridors.Pieces(campaign, nation);
        var digest = Digest(OriginalAllOwnerBfs(map, PoreLimit(campaign)));
        Require(WorldStamp(campaign) == before, "component/report reads preserve fingerprint RNG revision queue and map buffers");
        return digest;
    }

    private static void CampaignContinuation(Campaign campaign, int days, NativeOwnershipKernel native)
    {
        campaign.IsPaused = false;
        Campaign restored = null;
        var continuationFailures = 0;
        var restoreDay = Math.Max(1, days / 2);
        var first = CheckReads(campaign, native);
        Console.WriteLine("EXPERIMENTAL_PROBE " + OwnershipComponents.BackendDiagnostic);
        Console.WriteLine($"CAMPAIGN day={campaign.ElapsedDays} fingerprint={CampaignFingerprint.Of(campaign):X16} rng={campaign.Random.State:X8} components={first:X16}");
        for (var day = 1; day <= days; day++)
        {
            campaign.TickDay();
            Require(!campaign.MidStep && campaign.SimulationFault.Length == 0, "campaign completes day without a numeric fault");
            var digest = CheckReads(campaign, native);
            if (restored is not null)
            {
                restored.TickDay();
                var otherDigest = CheckReads(restored, native);
                if (CampaignFingerprint.Of(restored) != CampaignFingerprint.Of(campaign) ||
                    restored.Random.State != campaign.Random.State || otherDigest != digest)
                {
                    continuationFailures++;
                    Console.Error.WriteLine($"CONTINUATION_DIFFERENCE day={day} " +
                        $"original_day={campaign.ElapsedDays} restored_day={restored.ElapsedDays} " +
                        $"original_paused={campaign.IsPaused} restored_paused={restored.IsPaused} " +
                        $"original_hours={campaign.TickHours} restored_hours={restored.TickHours} " +
                        $"original_fp={CampaignFingerprint.Of(campaign):X16} restored_fp={CampaignFingerprint.Of(restored):X16} " +
                        $"original_rng={campaign.Random.State:X8} restored_rng={restored.Random.State:X8} " +
                        $"original_components={digest:X16} restored_components={otherDigest:X16}");
                    if (continuationFailures == 1) SnapshotDifferences(campaign, restored);
                }
                else _checks++;
            }
            if (day == restoreDay)
            {
                var snapshot = SaveSystem.ExportNetworkSnapshot(campaign, "ownership-kernel-bench");
                Require(snapshot.Length > 0, "network snapshot is nonempty");
                var loaded = SaveSystem.ImportNetworkSnapshot(snapshot, "ownership-kernel-bench");
                Require(loaded.Success && loaded.Campaign is not null, "network snapshot restores: " + loaded.Warning);
                restored = loaded.Campaign;
                var restoredMap = campaign.ProvinceMap.CloneForOverrides();
                var lakesBefore = restoredMap.LakeAccessByProvince.Count(value => value);
                var riversBefore = restoredMap.RiverIntensityByProvince.Count(value => value != 0);
                // Political trial clones intentionally omit derived hydrology. Restore the
                // same physical input as MakeWorld/the real map loader before continuing.
                if (MapRasterCatalog.TryLoadHydrology(WorldMapSource.Width, WorldMapSource.Height, out var hydrology, out _))
                    restoredMap.AttachHydrology(hydrology);
                Require(restoredMap.LakeAccessByProvince.SequenceEqual(campaign.ProvinceMap.LakeAccessByProvince) &&
                    restoredMap.RiverIntensityByProvince.SequenceEqual(campaign.ProvinceMap.RiverIntensityByProvince) &&
                    restoredMap.RiverCrossingCount == campaign.ProvinceMap.RiverCrossingCount,
                    "restored topology has the same physical hydrology inputs");
                Console.WriteLine($"HYDROLOGY_RESTORE lakes_before={lakesBefore} lakes_after={restoredMap.LakeAccessByProvince.Count(value => value)} " +
                    $"rivers_before={riversBefore} rivers_after={restoredMap.RiverIntensityByProvince.Count(value => value != 0)}");
                restored.AttachProvinceTopology(restoredMap);
                Require(CampaignFingerprint.Of(restored) == CampaignFingerprint.Of(campaign) &&
                    restored.Random.State == campaign.Random.State && CheckReads(restored, native) == digest,
                    "save/load initial fingerprint RNG and ordered component parity");
                Console.WriteLine($"ROUNDTRIP day={campaign.ElapsedDays} bytes={snapshot.Length} PASS");
                Console.WriteLine("SNAPSHOT_COMPARE phase=immediate_restore (MapRevision may increase when attaching topology)");
                SnapshotDifferences(campaign, restored);
            }
            Console.WriteLine($"CAMPAIGN day={campaign.ElapsedDays} fingerprint={CampaignFingerprint.Of(campaign):X16} rng={campaign.Random.State:X8} components={digest:X16}");
        }
        Require(continuationFailures == 0, "save/load continuation differed on " + continuationFailures + " days; see CONTINUATION_DIFFERENCE records");
        Console.WriteLine($"CAMPAIGN_CONTINUATION days={days} restored_at={restoreDay} PASS");
    }

    private static void SnapshotDifferences(Campaign original, Campaign restored)
    {
        JsonDocument Capture(Campaign world)
        {
            using var bytes = new MemoryStream(SaveSystem.ExportNetworkSnapshot(world, "ownership-kernel-bench"));
            using var gzip = new GZipStream(bytes, CompressionMode.Decompress);
            return JsonDocument.Parse(gzip);
        }
        using var left = Capture(original); using var right = Capture(restored);
        var count = 0;
        void Difference(string path, string a, string b)
        {
            if (count++ >= 12) return;
            Console.Error.WriteLine($"SNAPSHOT_DIFFERENCE {path} original={a} restored={b}");
        }
        void Walk(JsonElement a, JsonElement b, string path)
        {
            if (count >= 12) return;
            if (a.ValueKind != b.ValueKind) { Difference(path, a.ValueKind.ToString(), b.ValueKind.ToString()); return; }
            if (a.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in a.EnumerateObject())
                {
                    if (!b.TryGetProperty(property.Name, out var other)) Difference(path + "." + property.Name, "present", "missing");
                    else Walk(property.Value, other, path + "." + property.Name);
                    if (count >= 12) break;
                }
            }
            else if (a.ValueKind == JsonValueKind.Array)
            {
                if (a.GetArrayLength() != b.GetArrayLength()) Difference(path + ".Length", a.GetArrayLength().ToString(), b.GetArrayLength().ToString());
                var length = Math.Min(a.GetArrayLength(), b.GetArrayLength());
                for (var index = 0; index < length && count < 12; index++) Walk(a[index], b[index], path + "[" + index + "]");
            }
            else if (a.GetRawText() != b.GetRawText()) Difference(path, a.GetRawText(), b.GetRawText());
        }
        Walk(left.RootElement, right.RootElement, "campaign");
    }
}
