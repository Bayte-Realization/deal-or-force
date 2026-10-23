using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

static class OwnershipComponentTests
{
    private static void Check(bool valid, string label)
    { if (!valid) throw new Exception("ownership kernel: " + label); Console.WriteLine("PASS ownership kernel: " + label); }

    private static void Reject(Action action, string label)
    {
        var rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, label);
    }

    public static void Run(bool? expectedNative = null)
    {
        var graph = OwnershipComponentGraph.FromNeighbors(new[] {
            Array.Empty<int>(), new[] { 4, 0, -1, 2, 99 }, new[] { 1, 3 }, new[] { 4, 2 }, new[] { 1, 3 }
        });
        var owners = new ushort[] { 0, 7, 7, 7, 7 };
        var water = new byte[5];
        var batch = graph.ComputeManaged(owners, water);
        Check(batch.MemberOrder.Take(batch.MemberCount).SequenceEqual(new[] { 1, 4, 2, 3 }), "BFS neighbor order is not sorted or canonicalized");
        Check(batch.ComponentCount == 1 && batch.ComponentOffsets.Take(2).SequenceEqual(new[] { 0, 4 }), "one contiguous owner partition with sentinel excluded");
        Check(graph.EdgeCount == 8 && graph.VertexCount == 5, "invalid and sentinel adjacency filtered without changing valid order");
        Check(owners.SequenceEqual(new ushort[] { 0, 7, 7, 7, 7 }) && water.All(value => value == 0), "batch reads do not mutate inputs");
        Check(batch.IsStructurallyValid(owners, water), "reference output passes the structural boundary");
        var fallback = graph.Compute(owners, water, null);
        Check(fallback.MemberOrder.SequenceEqual(batch.MemberOrder) && fallback.Labels.SequenceEqual(batch.Labels), "absent native backend uses exact managed reference");
        water[4] = 1;
        owners[2] = 0;
        var separated = graph.ComputeManaged(owners, water);
        Check(separated.ComponentCount == 2 && separated.MemberCount == 2 && separated.Labels[2] == 0 && separated.Labels[4] == 0,
            "water and unclaimed bridges do not join owned ground");
        Check(separated.MemberOrder.Take(2).SequenceEqual(new[] { 1, 3 }), "disconnected roots stay in ascending start order");
        Reject(() => OwnershipComponentGraph.FromNeighbors(null), "null graph rejected before native dispatch");
        Reject(() => OwnershipComponentGraph.FromNeighbors(Array.Empty<int[]>()), "graph without sentinel rejected");
        Reject(() => OwnershipComponentGraph.FromNeighbors(new int[][] { null }), "null adjacency row rejected");
        Reject(() => graph.ComputeManaged(new ushort[4], water), "owner shape mismatch rejected");
        Reject(() => graph.ComputeManaged(owners, new byte[4]), "water shape mismatch rejected");
        Reject(() => graph.ComputeManaged(owners, new byte[] { 0, 0, 0, 2, 0 }), "nonbinary water flag rejected");
        var empty = OwnershipComponentGraph.FromNeighbors(new[] { Array.Empty<int>() }).ComputeManaged(new ushort[1], new byte[1]);
        Check(empty.ComponentCount == 0 && empty.MemberCount == 0 && empty.IsStructurallyValid(new ushort[1], new byte[1]), "sentinel-only map is valid and empty");

        var validOwners = new ushort[] { 0, 7, 7, 7, 7 };
        var validWater = new byte[5];
        batch.ComponentCount = int.MaxValue;
        Check(!batch.IsStructurallyValid(validOwners, validWater), "oversized native count rejected before indexing");
        batch.ComponentCount = 1;
        batch.MemberOrder[1] = int.MinValue;
        Check(!batch.IsStructurallyValid(validOwners, validWater), "negative native member rejected before indexing");
        batch.MemberOrder[1] = 1;
        Check(!batch.IsStructurallyValid(validOwners, validWater), "duplicate native member rejected");
        batch.MemberOrder[1] = 4;
        batch.Labels[4] = 2;
        Check(!batch.IsStructurallyValid(validOwners, validWater), "native labels must match the complete partition");
        Check(!NativeOwnershipKernel.TryOpen("relative/folder", out _, out _), "relative DLL directory never searched");
        Check(!NativeOwnershipKernel.TryOpen(null, out _, out _), "unset DLL directory stays managed");
        Check(!NativeOwnershipKernel.TryOpen(Path.Combine(Path.GetTempPath(), "dof-absent-" + Guid.NewGuid().ToString("N")), out _, out _), "missing optional DLL rejected cleanly");
        CacheAndCorridors(expectedNative);
    }

    private static (Campaign World, Nation Country) World(int seed)
    {
        var world = new Campaign(seed);
        var nation = new Nation { Id = 1, MapIndex = 501, Tag = "KRN", Name = "Kernel fixture", Population = 1_000_000 };
        world.Nations.Add(nation);
        world.States.Add(new ScenarioState { MapIndex = 3001, NationMapIndex = 501, NationId = nation.Tag,
            Id = "kernel_fixture", Name = "Kernel fixture", Population = 1_000_000 });
        world.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)3001, 1024).ToArray(),
            Enumerable.Repeat((ushort)501, 1024).ToArray(), 32, 32, _ => false));
        world.ConfigureSinglePlayer(nation.Id);
        return (world, nation);
    }

    private static void CacheAndCorridors(bool? expectedNative)
    {
        var (world, nation) = World(71);
        var before = CampaignFingerprint.Of(world);
        var randomBefore = world.Random.State;
        var revisionBefore = world.MapRevision;
        var batch = OwnershipComponents.Read(world);
        if (expectedNative.HasValue)
            Check(OwnershipComponents.TryReadNative(world, out _) == expectedNative.Value,
                expectedNative.Value ? "requested experimental native backend really executed" : "rejected experimental backend stays managed");
        Check(ReferenceEquals(batch, OwnershipComponents.Read(world)), "same-world revision reuses a complete immutable-by-consumer batch");
        var (other, _) = World(72);
        var otherBatch = OwnershipComponents.Read(other);
        Check(!ReferenceEquals(batch, otherBatch) && ReferenceEquals(batch, OwnershipComponents.Read(world)), "interleaved campaigns keep separate caches");
        var pieces = OwnershipComponents.Pieces(world, nation.MapIndex);
        var originalCount = pieces.Sum(piece => piece.Count);
        pieces[0].Clear();
        Check(OwnershipComponents.Pieces(world, nation.MapIndex).Sum(piece => piece.Count) == originalCount,
            "callers cannot erase cached components by editing returned sets");
        var legacy = OriginalPieces(world, nation);
        var actual = Corridors.Pieces(world, nation);
        Check(SamePieces(legacy, actual), "live corridor pieces retain exact legacy component and member order");
        Check(before == CampaignFingerprint.Of(world) && randomBefore == world.Random.State && revisionBefore == world.MapRevision,
            "component/cache/corridor reads leave fingerprint, RNG and revision unchanged");
        if (world.ProvinceController.Length > 1) world.ProvinceController[1] = 502;
        Check(ReferenceEquals(batch, OwnershipComponents.Read(world)), "occupation alone does not alter ownership connectivity");
        world.ProvinceMap.ProvinceToNation[1] = 502;
        world.MarkMapChanged();
        var changed = OwnershipComponents.Read(world);
        Check(!ReferenceEquals(batch, changed) && changed.Labels[1] != changed.Labels[2], "ownership revision rebuilds the complete partition");
        Check(SamePieces(OriginalPieces(world, nation), Corridors.Pieces(world, nation)), "corridor behavior stays equivalent after ownership change");
    }

    private static bool SamePieces(List<HashSet<int>> first, List<HashSet<int>> second) =>
        first.Count == second.Count && first.Zip(second).All(pair => pair.First.SequenceEqual(pair.Second));

    public static void RunFixtures(string root)
    {
        var leaf = Path.Combine("x86_64-pc-windows-msvc", "release");
        Check(!NativeOwnershipKernel.TryOpen(Path.Combine(root, "abi2", leaf), out var abi, out var reason) &&
            abi is null && reason.Contains("ABI mismatch"), "ABI mismatch is rejected before computation");
        var graph = OwnershipComponentGraph.FromNeighbors(new[] { Array.Empty<int>(), new[] { 2 }, new[] { 1 } });
        var owners = new ushort[] { 0, 12, 12 };
        var water = new byte[3];
        var expected = graph.ComputeManaged(owners, water);
        foreach (var kind in new[] { "status3", "malformed" })
        {
            Check(NativeOwnershipKernel.TryOpen(Path.Combine(root, kind, leaf), out var native, out reason), kind + " fixture is a real native library");
            using (native)
            {
                var fallback = graph.Compute(owners, water, native);
                Check(!native.IsEnabled && native.Diagnostic.Contains("managed fallback"), kind + " failure disables further native attempts");
                Check(fallback.Labels.SequenceEqual(expected.Labels) && fallback.MemberOrder.SequenceEqual(expected.MemberOrder) &&
                    fallback.ComponentOffsets.SequenceEqual(expected.ComponentOffsets), kind + " partial outputs are discarded before complete C# recomputation");
                var again = graph.Compute(owners, water, native);
                Check(again.Labels.SequenceEqual(expected.Labels) && !native.IsEnabled, kind + " subsequent dispatch stays managed");
                Check(owners.SequenceEqual(new ushort[] { 0, 12, 12 }) && water.All(flag => flag == 0), kind + " error never mutates ownership/water inputs");
            }
        }
    }

    private static List<HashSet<int>> OriginalPieces(Campaign world, Nation nation)
    {
        var map = world.ProvinceMap;
        var owned = new HashSet<int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToNation[province] == nation.MapIndex && !map.WaterByProvince[province]) owned.Add(province);
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        var pieces = new List<HashSet<int>>();
        foreach (var start in owned)
        {
            if (!seen.Add(start)) continue;
            var piece = new HashSet<int> { start };
            queue.Enqueue(start);
            while (queue.Count != 0)
                foreach (var next in map.Neighbors[queue.Dequeue()])
                    if (owned.Contains(next) && seen.Add(next)) { piece.Add(next); queue.Enqueue(next); }
            pieces.Add(piece);
        }
        pieces.Sort((a, b) => {
            var firstCapital = a.Any(p => map.ProvinceToState[p] == nation.CapitalStateMapIndex);
            var secondCapital = b.Any(p => map.ProvinceToState[p] == nation.CapitalStateMapIndex);
            return firstCapital != secondCapital ? firstCapital ? -1 : 1 : b.Count.CompareTo(a.Count);
        });
        return pieces;
    }
}
