using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.KernelBench;

/// <summary>
/// Data layout and native code, decided by measurement on the heaviest numeric loop of a late
/// week: the household review over every population group of a loaded save.
///
///   KernelBench &lt;save.dofsave&gt; [--samples N] [--warmup N]
///
/// The save is read, never written. Every variant starts every sample from the same bytes,
/// and every sample's result is hashed and compared to the game's code; a variant that is
/// fast and one bit off is disqualified, not reported as a win. At the end the game's own
/// HouseholdEconomy.Review is run on the loaded world and its groups are compared to the
/// reference, so "the game's code" is checked against the game itself.
/// </summary>
internal static class Program
{
    private sealed record Variant(string Name, string Note, Action Reset, Action Run, Func<ulong> Checksum);

    private static int Main(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.WriteLine("usage: KernelBench <save.dofsave> [--samples N] [--warmup N]");
            return 2;
        }
        var samples = Arg(args, "--samples", 31);
        var warmup = Arg(args, "--warmup", 10);
        Console.WriteLine("KernelBench - household review over population groups");
        Console.WriteLine($"  runtime {Environment.Version}, {Environment.ProcessorCount} logical cores, " +
                          $"DeterministicParallel.Workers={DeterministicParallel.Workers}, Vector256 accelerated={Vector256.IsHardwareAccelerated}, " +
                          $"server GC={System.Runtime.GCSettings.IsServerGC}");

        var clock = Stopwatch.StartNew();
        Campaign.CreateBootstrap(20260830);
        Console.WriteLine($"  catalogues (bootstrap world) in {clock.ElapsedMilliseconds:N0} ms");
        clock.Restart();
        var path = Path.GetFullPath(args[0]);
        var loaded = SaveSystem.Load(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), "");
        if (!loaded.Success || loaded.Campaign is null) { Console.WriteLine("  load failed: " + loaded.Warning); return 2; }
        var c = loaded.Campaign;
        Console.WriteLine($"  loaded {Path.GetFileName(path)} in {clock.ElapsedMilliseconds:N0} ms: {c.Date}, day {c.ElapsedDays:N0}");

        // The regions in the order EconomySystem visits them: country by country, the country's
        // regions in StatesOf order, a region reviewed once (Review's own guard does that in the
        // game). The annual conditions are the first terms of EconomySystem's own formula; the
        // rest (tax, focus, government) only shift NonBuildingJobs by a different constant, and
        // every variant and the oracle are handed the same value.
        var items = new List<ReviewItem>();
        var byNation = new List<(Nation, float, ScenarioState[])>();
        var seen = new HashSet<ScenarioState>(ReferenceEqualityComparer.Instance);
        foreach (var n in c.Nations)
        {
            if (n.Movement is null && (!c.HasOwnedLand(n) || c.Fallen.Contains(n.Id))) continue;
            var annual = 0.006f + (n.Stability - 0.5f) * 0.018f - n.ShortageIndex * c.Defines.ShortageAnnualGrowthPenalty;
            var mine = new List<ScenarioState>();
            foreach (var s in c.StatesOf(n))
                if (s is not null && seen.Add(s)) { items.Add(new ReviewItem(n, s, annual)); mine.Add(s); }
            if (mine.Count > 0) byNation.Add((n, annual, mine.ToArray()));
        }
        var groupCount = items.Sum(i => i.State.PopulationGroups.Count);
        var src = HouseholdSoa.Allocate(items.Count, groupCount);
        src.PackStates(c, items);
        src.PackGroups(items);
        var due = src.Due.Count(b => b != 0);
        var lengths = items.Select(i => i.State.PopulationGroups.Count).OrderBy(x => x).ToArray();
        Console.WriteLine($"  regions reviewed {items.Count:N0} (due {due:N0}) of {c.States.Count:N0}, countries {byNation.Count:N0}, " +
                          $"population groups {groupCount:N0}; groups per region median {lengths[lengths.Length / 2]}, " +
                          $"p95 {lengths[(int)(lengths.Length * 0.95)]}, max {lengths[^1]}; regions still migrating NonBuildingJobs {src.NonBuildingJobs.Count(x => x < 0)}");
        Console.WriteLine("  per group the array loop reads 24 bytes and writes 16; the game's loop reaches a 72-byte object " +
                          "(header, three string references, a long, two enums, four floats) and looks the profession up by string");

        // The reference: the game's code, once, from the snapshot.
        src.Unpack(items);
        GameKernel.RunSerial(c, items);
        var reference = HouseholdSoa.Allocate(items.Count, groupCount);
        Capture(items, reference);
        var referenceSum = reference.Checksum();
        Console.WriteLine($"  reference checksum {referenceSum:X16}");

        // Working copies for the variants.
        var work = src.Clone();
        var scratch = src.Clone();
        var labourSum = new double[items.Count];
        var employmentSum = new double[items.Count];
        var livingSum = new double[items.Count];
        clock.Restart();
        var friflo = EcsVariants.BuildFriflo(src);
        var frifloBuild = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var arch = EcsVariants.BuildArch(src);
        var archBuild = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine("  Arch: " + arch.NaturalOrder);
        Console.WriteLine($"  ECS worlds built: Friflo {frifloBuild:N1} ms, Arch {archBuild:N1} ms (one-off, not in the samples)");
        var bridge = HouseholdSoa.Allocate(items.Count, groupCount);
        Console.WriteLine($"  Rust ABI: sse2 v{NativeKernel.AbiV1()}, avx2 v{NativeKernel.AbiV3()}");

        void RestoreObjects() => src.Unpack(items);
        ulong ObjectsChecksum() { Capture(items, scratch); return scratch.Checksum(); }
        void RestoreWork() => work.CopyMutableFrom(src);
        var threads = DeterministicParallel.Workers;

        var variants = new List<Variant>
        {
            new("a  game code, serial", "List<PopulationGroup> objects, string lookup", RestoreObjects, () => GameKernel.RunSerial(c, items), ObjectsChecksum),
            new("a  game code, as scheduled", "per country, DeterministicParallel when >=16 regions", RestoreObjects, () => GameKernel.RunAsGame(c, byNation), ObjectsChecksum),
            new("b  arrays (SoA), serial", "Span, scalar", RestoreWork, work.RunScalar, work.Checksum),
            new("b  arrays (SoA), parallel", "DeterministicParallel, 32 regions a block", RestoreWork, () => work.RunScalarParallel(c), work.Checksum),
            new("c  arrays + Vector256, serial", "AVX2, exact clamp, sequential sums", RestoreWork, work.RunSimd, work.Checksum),
            new("c  arrays + Vector256, parallel", "DeterministicParallel, 32 regions a block", RestoreWork, () => work.RunSimdParallel(c), work.Checksum),
            new("d  Friflo.Engine.ECS, serial", "entity per group, 2 components", () => { RestoreWork(); EcsVariants.ResetFriflo(friflo, src); },
                () => EcsVariants.RunFriflo(friflo, work, labourSum, employmentSum, livingSum), () => { EcsVariants.ExportFriflo(friflo, work); return work.Checksum(); }),
            new("e  Arch, serial", "entity per group, 2 components", () => { RestoreWork(); EcsVariants.ResetArch(arch, src); },
                () => EcsVariants.RunArch(arch, work, labourSum, employmentSum, livingSum), () => { EcsVariants.ExportArch(arch, work); return work.Checksum(); }),
            new("f  Rust x86-64 (SSE2), 1 thread", "one call, pinned arrays", RestoreWork, () => Check(NativeKernel.Run(work, false, 1)), work.Checksum),
            new("f  Rust x86-64-v3 (AVX2), 1 thread", "one call, pinned arrays", RestoreWork, () => Check(NativeKernel.Run(work, true, 1)), work.Checksum),
            new($"f  Rust x86-64-v3, {threads} threads", "std::thread::scope, spawned per call", RestoreWork, () => Check(NativeKernel.Run(work, true, threads)), work.Checksum),
            // The bridge: what it costs to keep the game's objects and convert every week.
            new("   bridge: pack regions", "JobsDemanded, market, war damage", () => { }, () => bridge.PackStates(c, items), () => referenceSum),
            new("   bridge: pack groups", "objects -> arrays, profession lookup", () => { }, () => bridge.PackGroups(items), () => referenceSum),
            new("   bridge: unpack", "arrays -> objects", () => { bridge.CopyMutableFrom(reference); }, () => bridge.Unpack(items), () => referenceSum),
            new("   end to end: c parallel from objects", "pack + kernel + unpack", RestoreObjects,
                () => { bridge.PackStates(c, items); bridge.PackGroups(items); bridge.RunSimdParallel(c); bridge.Unpack(items); }, ObjectsChecksum),
            new($"   end to end: Rust {threads}t from objects", "pack + one call + unpack", RestoreObjects,
                () => { bridge.PackStates(c, items); bridge.PackGroups(items); Check(NativeKernel.Run(bridge, true, threads)); bridge.Unpack(items); }, ObjectsChecksum),
        };

        var times = variants.Select(_ => new List<double>()).ToArray();
        var allocs = variants.Select(_ => new List<long>()).ToArray();
        var verdict = new string[variants.Count];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        for (var round = 0; round < warmup + samples; round++)
        {
            // Rotate the order every round so no variant always runs after the same neighbour.
            for (var k = 0; k < variants.Count; k++)
            {
                var index = (k + round) % variants.Count;
                var v = variants[index];
                v.Reset();
                var before = GC.GetTotalAllocatedBytes(true);
                var watch = Stopwatch.StartNew();
                v.Run();
                watch.Stop();
                var allocated = GC.GetTotalAllocatedBytes(true) - before;
                var sum = v.Checksum();
                if (sum != referenceSum && verdict[index] is null)
                    verdict[index] = $"DIFFERS ({sum:X16})";
                if (round < warmup) continue;
                times[index].Add(watch.Elapsed.TotalMilliseconds);
                allocs[index].Add(allocated);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Samples {samples} after {warmup} warm-up rounds; order rotated each round; ms per whole week (all regions).");
        Console.WriteLine($"{"variant",-42} {"median",9} {"p95",9} {"min",9} {"alloc/run",12}  result");
        for (var i = 0; i < variants.Count; i++)
        {
            var sorted = times[i].OrderBy(x => x).ToArray();
            var median = sorted[sorted.Length / 2];
            var p95 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
            var alloc = allocs[i].OrderBy(x => x).ElementAt(allocs[i].Count / 2);
            var result = variants[i].Name.StartsWith("   bridge") ? "-" : verdict[i] ?? "bit-identical";
            Console.WriteLine($"{variants[i].Name,-42} {median,9:0.000} {p95,9:0.000} {sorted[0],9:0.000} {alloc,12:N0}  {result}   [{variants[i].Note}]");
        }

        // The oracle: the game's own HouseholdEconomy.Review on the world, compared to the reference.
        // Belief conversion is switched off for this one run: it blends converts into another
        // group of the region after the loop (BeliefDynamics, BlendMean), which rewrites those
        // destination groups' four fields - a later step of Review, not a difference in the loop.
        src.Unpack(items);
        var groupsBefore = items.Select(i => i.State.PopulationGroups.ToArray()).ToArray();
        var sizesBefore = groupsBefore.Select(list => list.Select(g => g.Size).ToArray()).ToArray();
        var populationBefore = items.Select(i => i.State.Population).ToArray();
        var reviewDayBefore = items.Select(i => i.State.LastHouseholdReviewDay).ToArray();
        var conversion = c.Defines.BeliefConversionEnabled;
        c.Defines.BeliefConversionEnabled = false;
        foreach (var (n, s, annual) in items) HouseholdEconomy.Review(c, n, s, annual);
        c.Defines.BeliefConversionEnabled = conversion;
        var mismatches = new Dictionary<string, int>();
        void Compare(string field, float a, float b)
        {
            if (BitConverter.SingleToUInt32Bits(a) != BitConverter.SingleToUInt32Bits(b))
                mismatches[field] = mismatches.GetValueOrDefault(field) + 1;
        }
        for (int i = 0, g = 0; i < items.Count; i++)
        {
            var s = items[i].State;
            foreach (var group in groupsBefore[i])
            {
                Compare("group.Employment", group.Employment, reference.Employment[g]);
                Compare("group.Wealth", group.Wealth, reference.Wealth[g]);
                Compare("group.StandardOfLiving", group.StandardOfLiving, reference.Living[g]);
                Compare("group.Radicalism", group.Radicalism, reference.Radicalism[g]);
                g++;
            }
            Compare("state.NonBuildingJobs", s.NonBuildingJobs, reference.NonBuildingJobs[i]);
            Compare("state.EmploymentTarget", s.EmploymentTarget, reference.EmploymentTarget[i]);
            Compare("state.BasicNeedsCoverage", s.BasicNeedsCoverage, reference.BasicNeeds[i]);
            Compare("state.LivelihoodStress", s.LivelihoodStress, reference.Stress[i]);
            Compare("state.StandardOfLiving", s.StandardOfLiving, reference.StateLiving[i]);
        }
        Console.WriteLine();
        Console.WriteLine(mismatches.Count == 0
            ? $"ORACLE the game's HouseholdEconomy.Review (belief conversion off) wrote every one of the {groupCount:N0} groups' four fields and the regions' five fields equal to the reference, bit for bit"
            : "ORACLE differs: " + string.Join(", ", mismatches.Select(kv => $"{kv.Key} x{kv.Value}")));

        // What the whole stage costs: the game's whole Review (vital rates, births and deaths,
        // group sizes, belief conversion on), from the same world each sample. The world is put
        // back by reference - lists, sizes, populations, the review day and the loop's fields -
        // so every sample starts where the save does.
        void RestoreWorld()
        {
            for (var i = 0; i < items.Count; i++)
            {
                var s = items[i].State;
                s.PopulationGroups.Clear();
                s.PopulationGroups.AddRange(groupsBefore[i]);
                for (var k = 0; k < groupsBefore[i].Length; k++) groupsBefore[i][k].Size = sizesBefore[i][k];
                s.Population = populationBefore[i];
                s.LastHouseholdReviewDay = reviewDayBefore[i];
            }
            src.Unpack(items);
        }
        var fullSerial = new List<double>();
        var fullScheduled = new List<double>();
        for (var round = 0; round < 5 + 15; round++)
        {
            RestoreWorld();
            var watch = Stopwatch.StartNew();
            foreach (var (n, s, annual) in items) HouseholdEconomy.Review(c, n, s, annual);
            if (round >= 5) fullSerial.Add(watch.Elapsed.TotalMilliseconds);
            RestoreWorld();
            watch.Restart();
            foreach (var (n, annual, states) in byNation)
                DeterministicParallel.For(c, 0, states.Length, index => HouseholdEconomy.Review(c, n, states[index], annual));
            if (round >= 5) fullScheduled.Add(watch.Elapsed.TotalMilliseconds);
        }
        static string Stats(List<double> v)
        {
            var o = v.OrderBy(x => x).ToArray();
            return $"median {o[o.Length / 2]:0.000} ms, p95 {o[Math.Min(o.Length - 1, (int)Math.Ceiling(o.Length * 0.95) - 1)]:0.000} ms";
        }
        Console.WriteLine($"STAGE the game's whole HouseholdEconomy.Review over the same regions, 15 samples after 5: serial {Stats(fullSerial)}; " +
                          $"as scheduled (per country, DeterministicParallel) {Stats(fullScheduled)}");
        if (mismatches.Count > 0) return 1;
        return verdict.Where((v, i) => !variants[i].Name.StartsWith("   bridge")).Any(v => v is not null) ? 1 : 0;
    }

    /// <summary>The fields the loop writes, read off the game's objects.</summary>
    private static void Capture(List<ReviewItem> items, HouseholdSoa into)
    {
        var g = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var s = items[i].State;
            foreach (var group in s.PopulationGroups)
            {
                into.Employment[g] = group.Employment; into.Wealth[g] = group.Wealth;
                into.Living[g] = group.StandardOfLiving; into.Radicalism[g] = group.Radicalism;
                g++;
            }
            into.NonBuildingJobs[i] = s.NonBuildingJobs; into.StateLiving[i] = s.StandardOfLiving;
            into.EmploymentTarget[i] = s.EmploymentTarget; into.BasicNeeds[i] = s.BasicNeedsCoverage; into.Stress[i] = s.LivelihoodStress;
        }
    }

    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException("native kernel returned " + status);
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var v) ? v : fallback;
    }
}
