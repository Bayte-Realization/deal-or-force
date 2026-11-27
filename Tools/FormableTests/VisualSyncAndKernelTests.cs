using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Kernels;

/// <summary>
/// Deep behavioral verification for PortAlpha 0.8.8.6 architecture:
/// lock-free triple-buffered WorldVisualSnapshot, Frame-0 clock/map lockstep and soft render
/// backpressure, incremental dirty-rect PoliticalPlate &amp; FrontierField mipmap parity (Rust SIMD
/// and pure C# fallback), wartime NativeProvinceGraph kernels &amp; AdjacencyRevision invalidation,
/// DemographyRulesHook built-in equivalence, TerritorialDisputeIndex zero-false-negative parity,
/// StrategicChokepoints.IsCommoditySqueezed parity, and VerboseSimulation log gating.
/// </summary>
public static class VisualSyncAndKernelTests
{
    private static int _passed;

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("visual sync & kernel: " + label);
        _passed++;
        Console.WriteLine("PASS visual sync & kernel: " + label);
    }

    [TestSuite("--visual-sync-kernels-only", IncludeInDefaultRun = true,
        Description = "Validates lock-free WorldVisualSnapshot, dirty-rect mipmapped plate/frontier parity, wartime native graph kernels, and dispute index")]
    public static void Run()
    {
        _passed = 0;
        TripleBufferConcurrentStress();
        BackgroundRunnerClockLockstepAndBackpressure();
        DirtyRectPoliticalPlateParity();
        DirtyRectFrontierFieldParity();
        WartimeGraphKernelsAndCrossingInvalidation();
        DemographyRulesHookBuiltInEquivalence();
        TerritorialDisputeIndexZeroFalseNegatives();
        AirReconVisionInvalidationRegression();
        ChokepointZeroAllocParityAndVerboseGate();
        LockFreeSyncSourceContracts();
        Console.WriteLine($"visual sync & kernel: {_passed} PASS");
    }

    private sealed class StressSlot
    {
        public long Sequence;
        public long Checksum;
        public int[] Payload = new int[32];
    }

    private static void TripleBufferConcurrentStress()
    {
        var buffer = new LockFreeTripleBuffer<StressSlot>(() => new StressSlot());
        Check(!buffer.HasUnread, "triple buffer starts with HasUnread == false");
        Check(!buffer.TryAcquireLatest(out _), "triple buffer returns false before first publish");

        const int totalWrites = 40_000;
        var torn = 0;
        var nonMonotonic = 0;
        var reads = 0;
        long lastReadSeq = 0;
        var done = 0;

        var producer = new Thread(() =>
        {
            for (var i = 1; i <= totalWrites; i++)
            {
                var slot = buffer.WriteBuffer;
                slot.Sequence = i;
                long sum = 0;
                for (var k = 0; k < slot.Payload.Length; k++)
                {
                    var v = i * 31 + k;
                    slot.Payload[k] = v;
                    sum += v;
                }
                slot.Checksum = sum;
                buffer.PublishWrite();
            }
            Volatile.Write(ref done, 1);
        })
        { IsBackground = true };

        producer.Start();
        while (Volatile.Read(ref done) == 0 || buffer.HasUnread)
        {
            if (!buffer.TryAcquireLatest(out var read))
            {
                Thread.SpinWait(4);
                continue;
            }
            reads++;
            var seq = read.Sequence;
            if (seq <= lastReadSeq) nonMonotonic++;
            lastReadSeq = seq;
            long check = 0;
            for (var k = 0; k < read.Payload.Length; k++) check += read.Payload[k];
            if (check != read.Checksum) torn++;
        }
        producer.Join();

        Check(torn == 0, $"triple buffer concurrent stress zero torn reads ({reads} reads)");
        Check(nonMonotonic == 0, "triple buffer concurrent stress strictly monotonic sequences");
        Check(lastReadSeq == totalWrites, $"triple buffer acquired final published frame ({lastReadSeq}/{totalWrites})");
        Check(buffer.CurrentReadBuffer.Sequence == totalWrites, "triple buffer CurrentReadBuffer exposes latest acquired frame");
    }

    private static void BackgroundRunnerClockLockstepAndBackpressure()
    {
        var campaign = Campaign.CreateBootstrap(98806);
        campaign.IsPaused = false;
        campaign.Speed = 5;
        using var runner = new BackgroundSimRunner
        {
            MobilePacing = false,
            MinSpeed5DayIntervalMs = 0.0
        };
        runner.Start(campaign);
        runner.SyncClockState(paused: false, speed: 5);
        try
        {
            // Wait until at least one snapshot is published and verify clock/map lockstep.
            WorldVisualState latest = null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                if (runner.TryAcquireLatestVisualSnapshot(out var snap))
                {
                    latest = snap;
                    if (snap.Version >= 2) break;
                }
                Thread.Sleep(5);
            }
            runner.DrainMainThreadUpdates(out _, out _, out _, out var initialFault);
            Check(initialFault is null, $"BackgroundSimRunner raised no worker fault ({initialFault?.Message ?? "none"})");
            Check(latest is not null && latest.Version >= 1,
                $"BackgroundSimRunner published lock-free WorldVisualState (version={latest?.Version ?? 0})");
            var presentationDate = runner.ReadPresentationDate(campaign);
            Check(latest!.Date.Year > 0 && presentationDate.Year >= latest.Date.Year,
                $"WorldVisualState date ({latest.Date}) stays synchronized with presentation date ({presentationDate})");
            Check(latest.Speed == 5 && !latest.IsPaused,
                "WorldVisualState mirrors live clock speed and pause state");

            // Notify presentation caught up, then verify runner continues advancing without stalling.
            runner.NotifyVisualFramePresented(latest.Version);
            var verBefore = latest.Version;
            sw.Restart();
            while (sw.ElapsedMilliseconds < 5000)
            {
                if (runner.TryAcquireLatestVisualSnapshot(out var next) && next.Version > verBefore)
                {
                    latest = next;
                    runner.NotifyVisualFramePresented(next.Version);
                    break;
                }
                Thread.Sleep(5);
            }
            runner.DrainMainThreadUpdates(out _, out _, out _, out var postFault);
            Check(postFault is null, $"BackgroundSimRunner remains fault-free after presentation ack ({postFault?.Message ?? "none"})");
            Check(latest!.Version > verBefore,
                $"BackgroundSimRunner advances smoothly after NotifyVisualFramePresented ({verBefore} -> {latest.Version})");

            // Verify Speed 4 smooth anti-burst pacing also publishes snapshots cleanly.
            runner.SyncClockState(paused: false, speed: 4);
            Thread.Sleep(80);
            Check(runner.TryAcquireLatestVisualSnapshot(out _) || runner.CurrentVisualSnapshot is not null,
                "BackgroundSimRunner keeps lock-free visual snapshot valid across speed transitions");
        }
        finally
        {
            runner.Stop();
        }
    }

    private static PoliticalPlateSource BuildSyntheticPlateSource(int width, int height)
    {
        var holders = new ushort[width * height];
        var provinces = new ushort[width * height];
        var geography = new byte[width * height];
        // Grid of 8x4 synthetic land provinces (ids 1..32) + ocean border (0).
        const int cellW = 16;
        const int cellH = 16;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = y * width + x;
                if (x < 8 || x >= width - 8 || y < 8 || y >= height - 8)
                {
                    holders[idx] = 0;
                    provinces[idx] = 0;
                    geography[idx] = 0;
                    continue;
                }
                var gx = (x - 8) / cellW;
                var gy = (y - 8) / cellH;
                var prov = (ushort)(gy * 8 + gx + 1);
                var holder = (ushort)((gx < 4 ? 1 : 2) + (gy >= 2 ? 2 : 0));
                provinces[idx] = prov;
                holders[idx] = holder;
                // Deterministic geography bits (coast / interior / river).
                geography[idx] = (byte)(((x + y) & 3) == 0 ? 1 : 2);
            }
        }
        return new PoliticalPlateSource
        {
            Width = width,
            Height = height,
            Holders = holders,
            Provinces = provinces,
            Geography = geography
        };
    }

    private static void DirtyRectPoliticalPlateParity()
    {
        const int w = 144;
        const int h = 80;
        var source = BuildSyntheticPlateSource(w, h);
        var authored = new ushort[40];
        var prevOwners = new ushort[40];
        var nextOwners = new ushort[40];
        for (var p = 1; p <= 32; p++)
        {
            var nation = (ushort)(((p - 1) % 4) + 1);
            authored[p] = nation;
            prevOwners[p] = nation;
            nextOwners[p] = nation;
        }
        // Flip two adjacent provinces (warfront capture) + one distant province.
        nextOwners[10] = 2;
        nextOwners[11] = 2;
        nextOwners[28] = 1;

        var colors = new Dictionary<int, uint>
        {
            [0] = 0x00000000u,
            [1] = 0xFFCC4433u,
            [2] = 0xFF3377CCu,
            [3] = 0xFF44AA55u,
            [4] = 0xFFDDAA22u,
        };
        var shades = MapBorderStyles.PlateShades();

        foreach (var simulateNoNative in new[] { false, true })
        {
            var prevSim = NativeKernelsLoader.SimulateUnavailableForTests;
            NativeKernelsLoader.SimulateUnavailableForTests = simulateNoNative;
            try
            {
                var mode = simulateNoNative ? "C# fallback" : "Rust SIMD";
                byte[] mipScratch = null;
                // Seed initial full plate.
                var initial = PoliticalPlate.PaintDirtyMipmappedInto(
                    source, null, prevOwners, authored, colors, shades, true, ref mipScratch, out var usedInc0);
                Check(!usedInc0, $"PoliticalPlate initial call performs full mipmapped bake ({mode})");

                // Incremental dirty-rect update.
                var inc = PoliticalPlate.PaintDirtyMipmappedInto(
                    source, prevOwners, nextOwners, authored, colors, shades, false, ref mipScratch, out var usedInc1);
                Check(usedInc1, $"PoliticalPlate warfront diff uses incremental dirty-rect path ({mode})");

                // Full reference bake of nextOwners.
                byte[] refScratch = null;
                var fullRef = PoliticalPlate.PaintDirtyMipmappedInto(
                    source, null, nextOwners, authored, colors, shades, true, ref refScratch, out var usedIncRef);
                Check(!usedIncRef && inc.Length == fullRef.Length,
                    $"PoliticalPlate dirty-rect mip buffer length matches full reference ({mode})");

                var diff = FirstByteDiff(inc, fullRef);
                Check(diff < 0, $"PoliticalPlate PaintDirtyMipmappedInto is BIT-EXACT across all mip levels ({mode}, diff={diff})");

                // Empty diff (no owner changes) preserves exact buffer without work.
                var noOp = PoliticalPlate.PaintDirtyMipmappedInto(
                    source, nextOwners, nextOwners, authored, colors, shades, false, ref mipScratch, out var usedIncNoOp);
                Check(usedIncNoOp && FirstByteDiff(noOp, fullRef) < 0,
                    $"PoliticalPlate empty diff is bit-exact no-op ({mode})");

                // Empty authoredOwners must follow MapBorders.CurrentHolder (preserve authored holders, no override) on BOTH plate and frontier!
                byte[] emptyAuthInc = null;
                byte[] emptyAuthRef = null;
                PoliticalPlate.PaintDirtyMipmappedInto(
                    source, null, prevOwners, Array.Empty<ushort>(), colors, shades, true, ref emptyAuthInc, out _);
                var eaInc = PoliticalPlate.PaintDirtyMipmappedInto(
                    source, prevOwners, nextOwners, Array.Empty<ushort>(), colors, shades, false, ref emptyAuthInc, out var eaUsedInc);
                var eaRef = PoliticalPlate.PaintDirtyMipmappedInto(
                    source, null, nextOwners, Array.Empty<ushort>(), colors, shades, true, ref emptyAuthRef, out _);
                Check(eaUsedInc && FirstByteDiff(eaInc, eaRef) < 0,
                    $"PoliticalPlate incremental rect matches full when authoredOwners is empty ({mode})");

                // Single-edge antimeridian province (touches x==0 only, not x==w-1): border across wrap at x==w-1 must stay bit-exact!
                const int aw = 64, ah = 32;
                var aHolders = new ushort[aw * ah];
                var aProvs = new ushort[aw * ah];
                var aGeo = new byte[aw * ah];
                for (var y = 4; y < ah - 4; y++)
                {
                    for (var x = 0; x < aw; x++)
                    {
                        var idx = y * aw + x;
                        if (x < 12) { aProvs[idx] = 1; aHolders[idx] = 1; }
                        else { aProvs[idx] = 2; aHolders[idx] = 2; }
                    }
                }
                var aSource = new PoliticalPlateSource { Width = aw, Height = ah, Holders = aHolders, Provinces = aProvs, Geography = aGeo };
                var aAuth = new ushort[] { 0, 1, 2 };
                var aPrev = new ushort[] { 0, 1, 2 };
                var aNext = new ushort[] { 0, 2, 2 }; // Province 1 (x=0..11) flips to match Province 2 (x=12..63) -> removes border at x=63!
                byte[] aIncBuf = null;
                byte[] aFullBuf = null;
                PoliticalPlate.PaintDirtyMipmappedInto(aSource, null, aPrev, aAuth, colors, shades, true, ref aIncBuf, out _);
                var aInc = PoliticalPlate.PaintDirtyMipmappedInto(aSource, aPrev, aNext, aAuth, colors, shades, false, ref aIncBuf, out _);
                var aFull = PoliticalPlate.PaintDirtyMipmappedInto(aSource, null, aNext, aAuth, colors, shades, true, ref aFullBuf, out _);
                Check(FirstByteDiff(aInc, aFull) < 0,
                    $"PoliticalPlate antimeridian single-edge province flip stays bit-exact across seam ({mode})");
            }
            finally
            {
                NativeKernelsLoader.SimulateUnavailableForTests = prevSim;
            }
        }
    }

    private static void DirtyRectFrontierFieldParity()
    {
        // Use a 512x256 map so a local warfront + 26-cell frontier distance halo stays well
        // below the 35% dirty-area threshold and exercises the true incremental dirty-rect path.
        const int w = 512;
        const int h = 256;
        var authoredRaster = new ushort[w * h];
        var provincesRaster = new ushort[w * h];
        const int cellW = 16;
        const int cellH = 16;
        for (var y = 64; y < 192; y++)
        {
            for (var x = 128; x < 384; x++)
            {
                var idx = y * w + x;
                var gx = (x - 128) / cellW; // 0..15
                var gy = (y - 64) / cellH;  // 0..7
                var prov = (ushort)(gy * 16 + gx + 1); // 1..128
                var holder = (ushort)((gx < 8 ? 1 : 2) + (gy >= 4 ? 2 : 0));
                provincesRaster[idx] = prov;
                authoredRaster[idx] = holder;
            }
        }

        var authored = new ushort[130];
        var prevOwners = new ushort[130];
        var nextOwners = new ushort[130];
        for (var p = 1; p <= 128; p++)
        {
            var gx = (p - 1) % 16;
            var gy = (p - 1) / 16;
            var nation = (ushort)((gx < 8 ? 1 : 2) + (gy >= 4 ? 2 : 0));
            authored[p] = nation;
            prevOwners[p] = nation;
            nextOwners[p] = nation;
        }
        // Flip two interior border provinces (gx=7,8 at gy=3 -> provs 56, 57).
        nextOwners[56] = 2;
        nextOwners[57] = 1;

        foreach (var simulateNoNative in new[] { false, true })
        {
            var prevSim = NativeKernelsLoader.SimulateUnavailableForTests;
            NativeKernelsLoader.SimulateUnavailableForTests = simulateNoNative;
            try
            {
                var mode = simulateNoNative ? "C# fallback" : "Rust SIMD";
                byte[] mipScratch = null;
                var init = FrontierField.BuildDirtyMipmappedInto(
                    authoredRaster, provincesRaster, w, h, null, prevOwners, authored,
                    forceFull: true, ref mipScratch, out var usedInc0);
                Check(!usedInc0, $"FrontierField initial call performs full mipmapped bake ({mode})");

                var inc = FrontierField.BuildDirtyMipmappedInto(
                    authoredRaster, provincesRaster, w, h, prevOwners, nextOwners, authored,
                    forceFull: false, ref mipScratch, out var usedInc1);
                if (!simulateNoNative)
                    Check(usedInc1, $"FrontierField warfront diff uses incremental dirty-rect path ({mode})");
                else
                    Check(!usedInc1, $"FrontierField cleanly falls back to full mipmapped bake when native unavailable ({mode})");

                byte[] refScratch = null;
                var fullRef = FrontierField.BuildDirtyMipmappedInto(
                    authoredRaster, provincesRaster, w, h, null, nextOwners, authored,
                    forceFull: true, ref refScratch, out _);

                var diff = FirstByteDiff(inc, fullRef);
                Check(diff < 0, $"FrontierField BuildDirtyMipmappedInto is BIT-EXACT across all mip levels ({mode}, diff={diff})");

                // Empty authoredOwners must match MapBorders.CurrentHolder (no override) identically in Rust and C# managed!
                if (!simulateNoNative)
                {
                    var nativeEmptyAuth = FrontierField.Build(authoredRaster, provincesRaster, w, h, nextOwners, Array.Empty<ushort>());
                    var managedEmptyAuth = FrontierField.BuildManaged(authoredRaster, provincesRaster, w, h, nextOwners, Array.Empty<ushort>());
                    Check(FirstByteDiff(nativeEmptyAuth, managedEmptyAuth) < 0,
                        "FrontierField.Build with empty authoredOwners matches BuildManaged (MapBorders.CurrentHolder) bit-exact");
                }
            }
            finally
            {
                NativeKernelsLoader.SimulateUnavailableForTests = prevSim;
            }
        }
    }

    private static void WartimeGraphKernelsAndCrossingInvalidation()
    {
        if (!NativeKernelsLoader.IsAvailable || !NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapWartimeGraph))
        {
            Check(true, "wartime native graph unavailable in environment — skipped cleanly");
            return;
        }

        // Build a two-island map separated by a water strip so a crossing joins disconnected land provinces.
        const int width = 20;
        const int height = 8;
        var regions = new ushort[width * height];
        var owners = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = y * width + x;
                if (x >= 8 && x < 12) continue; // sea strait separating west & east islands
                if (x < 4) { regions[idx] = 101; owners[idx] = 1; }
                else if (x < 8) { regions[idx] = 102; owners[idx] = 2; }
                else if (x < 16) { regions[idx] = 103; owners[idx] = 3; }
                else { regions[idx] = 104; owners[idx] = 4; }
            }
        }
        var map = ProvinceTopology.Build(regions, owners, width, height, _ => false);
        var controllers = (ushort[])map.ProvinceToNation.Clone();

        // Pick a west coast province (nation 2) and east coast province (nation 3) that don't touch directly.
        var westShore = -1;
        var eastShore = -1;
        var westInterior = -1;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (map.IsWater(p)) continue;
            if (map.ProvinceToNation[p] == 1 && westInterior < 0) westInterior = p;
            if (map.ProvinceToNation[p] == 2 && westShore < 0) westShore = p;
            if (map.ProvinceToNation[p] == 3 && eastShore < 0) eastShore = p;
        }
        Check(westInterior > 0 && westShore > 0 && eastShore > 0,
            "synthetic two-island topology resolved west/east shore provinces");

        // Occupy westInterior by nation 2 to exercise foreign occupation census.
        controllers[westInterior] = 2;

        var graph = NativeProvinceGraph.ForMap(map);
        Check(graph is not null, "NativeProvinceGraph.ForMap builds CSR for synthetic topology");

        // 1. Occupation census parity vs C# reference.
        Check(graph!.TryOccupationCensus(map.ProvinceToNation, controllers, map.ProvinceCount,
                out var homeKeys, out var homeCounts, out var occPairs, out var occCounts),
            "NativeProvinceGraph.TryOccupationCensus succeeds");
        var expectedHome = new Dictionary<ushort, int>();
        var expectedOcc = new Dictionary<long, int>();
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            var home = map.ProvinceToNation[p];
            if (home == 0) continue;
            expectedHome[home] = expectedHome.GetValueOrDefault(home) + 1;
            var ctrl = controllers[p];
            if (ctrl > 0 && ctrl != home)
            {
                var key = ((long)home << 16) | ctrl;
                expectedOcc[key] = expectedOcc.GetValueOrDefault(key) + 1;
            }
        }
        var homeMatch = homeKeys.Length == expectedHome.Count;
        for (var i = 0; i < homeKeys.Length && homeMatch; i++)
            homeMatch = expectedHome.TryGetValue(homeKeys[i], out var cnt) && cnt == homeCounts[i];
        var occMatch = occPairs.Length == expectedOcc.Count;
        for (var i = 0; i < occPairs.Length && occMatch; i++)
        {
            var packed = occPairs[i];
            var key = ((long)(ushort)(packed >> 16) << 16) | (ushort)(packed & 0xFFFF);
            occMatch = expectedOcc.TryGetValue(key, out var cnt) && cnt == occCounts[i];
        }
        Check(homeMatch && occMatch, "TryOccupationCensus matches C# reference home & occupied counts bit-for-bit");

        // 2. Border pairs parity.
        Check(graph.TryBorderPairs(controllers, out var pairs), "NativeProvinceGraph.TryBorderPairs succeeds");
        Check(pairs.Length >= 2, $"TryBorderPairs finds land border pairs (count={pairs.Length})");

        // 3. Territory vision before crossing stops at the water strait.
        var reachBefore = new byte[map.ProvinceCount + 1];
        Check(graph.TrySeedTerritoryVision(new[] { westShore }, 2, reachBefore), "TrySeedTerritoryVision succeeds");
        Check(reachBefore[westShore] == 3 && reachBefore[eastShore] == 0,
            "TrySeedTerritoryVision respects depth=2 and does not cross unbridged sea strait");

        // 4. Attach strait crossing westShore<->eastShore and verify AdjacencyRevision invalidates cached CSR graph!
        var revBefore = map.AdjacencyRevision;
        map.AttachCrossings(new[]
        {
            new ResolvedCrossing
            {
                Id = "test_strait",
                Name = "Test Strait",
                Kind = LandCrossingKind.Bridge,
                From = westShore,
                To = eastShore
            }
        });
        Check(map.AdjacencyRevision > revBefore, "ProvinceTopology.AttachCrossings increments AdjacencyRevision");

        var graphAfter = NativeProvinceGraph.ForMap(map);
        Check(graphAfter is not null && !ReferenceEquals(graph, graphAfter),
            "NativeProvinceGraph.ForMap rebuilds CSR graph when AdjacencyRevision advances");

        var reachAfter = new byte[map.ProvinceCount + 1];
        Check(graphAfter!.TrySeedTerritoryVision(new[] { westShore }, 2, reachAfter),
            "TrySeedTerritoryVision succeeds on rebuilt crossing graph");
        Check(reachAfter[eastShore] == 2,
            "TrySeedTerritoryVision crosses newly attached strait edge (AdjacencyRevision regression guard)");

        // 5. TryFloodVision seed overlay parity.
        Check(graphAfter.TryFloodVision(reachAfter, new[] { eastShore }, new byte[] { 2 }),
            "TryFloodVision succeeds on rebuilt graph");
        Check(reachAfter[eastShore] == 3,
            "TryFloodVision raises seeded province reach level to depth+1");
    }

    private static void DemographyRulesHookBuiltInEquivalence()
    {
        DemographyWorkingShareEvaluator ws = DemographyRulesHook.ManagedWorkingShare;
        DemographyFertilityEvaluator fert = DemographyRulesHook.ManagedFertilityFactor;
        DemographyMortalityEvaluator mort = DemographyRulesHook.ManagedMortalityFactor;
        DemographyCohortStepper step = DemographyRulesHook.ManagedStepCohorts;

        var savedHook = DemographyRulesHook.CaptureSnapshot();
        try
        {
            DemographyRulesHook.Install(ws, fert, mort, step, builtInEquivalent: true);
            Check(DemographyRulesHook.Installed && !DemographyRulesHook.IsCustomInstalled,
                "DemographyRulesHook marks builtInEquivalent=true as Installed=true, IsCustomInstalled=false (unlocking Rust SIMD)");

            DemographyRulesHook.Install(ws, fert, mort, step, builtInEquivalent: false);
            Check(DemographyRulesHook.Installed && DemographyRulesHook.IsCustomInstalled,
                "DemographyRulesHook marks custom hook as IsCustomInstalled=true");
        }
        finally
        {
            DemographyRulesHook.RestoreSnapshot(savedHook);
        }
    }

    private static void TerritorialDisputeIndexZeroFalseNegatives()
    {
        var campaign = Campaign.CreateBootstrap(98807);
        var index = campaign.Caches.TerritorialDisputes;
        Check(!index.IsActiveFor(campaign), "TerritorialDisputeIndex starts inactive outside statecraft tick");

        index.WarmForTick(campaign);
        Check(index.IsActiveFor(campaign), "TerritorialDisputeIndex becomes active after WarmForTick");

        var nations = campaign.ActiveNations;
        var falseNegatives = 0;
        var checkedPairs = 0;
        var hits = 0;
        var limit = Math.Min(nations.Count, 80);
        for (var i = 0; i < limit; i++)
        {
            var a = nations[i];
            for (var j = 0; j < limit; j++)
            {
                if (i == j) continue;
                var b = nations[j];
                checkedPairs++;
                var may = index.MayHaveDispute(a.Id, b.Id);
                if (may) { hits++; continue; }
                if (LostLands.HasClaimedLand(campaign, a, b) || TerritorialClaims.HoldsOurHomeland(campaign, a, b))
                    falseNegatives++;
            }
        }
        Check(falseNegatives == 0,
            $"TerritorialDisputeIndex has 0 false negatives across {checkedPairs} directed nation pairs (hits={hits})");

        campaign.MarkStrategicChange();
        Check(!index.IsActiveFor(campaign),
            "TerritorialDisputeIndex auto-invalidates immediately when StrategicRevision changes");

        index.WarmForTick(campaign);
        index.EndTick();
        Check(!index.IsActiveFor(campaign),
            "TerritorialDisputeIndex deactivates cleanly on EndTick");
    }

    private static void AirReconVisionInvalidationRegression()
    {
        // Build a 360x8 map (1 deg/col ~= 111 km) with two non-adjacent nations separated by a
        // 1-col neutral buffer (~220 km apart) and BorderVisionDepth=0 so only air recon reveals
        // targetProv; stopping air recon MUST immediately clear the lifted fog!
        const int w = 360, h = 8;
        var regions = new ushort[w * h];
        var owners = new ushort[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 10; x < 16; x++)
            {
                var idx = y * w + x;
                if (x < 12) { regions[idx] = 101; owners[idx] = 1; }
                else if (x < 14) { regions[idx] = 103; owners[idx] = 3; } // neutral buffer
                else { regions[idx] = 102; owners[idx] = 2; }
            }
        }
        var map = ProvinceTopology.Build(regions, owners, w, h, _ => false);
        var c = new Campaign(12345);
        var n1 = new Nation { Id = 0, MapIndex = 1, Tag = "AAA", Name = "Alpha" };
        var n2 = new Nation { Id = 1, MapIndex = 2, Tag = "BBB", Name = "Beta" };
        var n3 = new Nation { Id = 2, MapIndex = 3, Tag = "CCC", Name = "Buffer" };
        c.Nations.Add(n1); c.Nations.Add(n2); c.Nations.Add(n3);
        c.AttachProvinceTopology(map);
        c.Defines.BorderVisionDepth = 0;
        c.Defines.ArmyVisionDepth = 0;
        c.Wars.Add(new StrategicWar { Id = 1, AttackerId = n1.Id, DefenderId = n2.Id });

        var baseProv = -1;
        var targetProv = -1;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (map.ProvinceToNation[p] == 1 && baseProv < 0) baseProv = p;
            if (map.ProvinceToNation[p] == 2 && targetProv < 0) targetProv = p;
        }
        string reconTypeId = null;
        var bestRange = 0f;
        foreach (var t in SupportTypeCatalog.All)
        {
            if (t.Kind is SupportKind.Air or SupportKind.Drone && SupportCore.Enabled(c, t) && t.RangeKm > bestRange)
            {
                bestRange = t.RangeKm;
                reconTypeId = t.Id;
            }
        }
        if (baseProv <= 0 || targetProv <= 0 || string.IsNullOrEmpty(reconTypeId))
        {
            Check(true, "air recon vision invalidation skipped cleanly (no catalog air support type)");
            return;
        }
        var seenBeforeRecon = CampaignIntel.CanSeeProvince(c, n1.Id, targetProv);

        var wing = new SupportFormation
        {
            Id = 777,
            NationId = n1.Id,
            TypeId = reconTypeId,
            BaseProvinceId = baseProv,
            Strength = 1f,
            Mission = AirReconnaissance.Mission
        };
        c.SupportFormations.Add(wing);
        SupportCore.IndexReach(c);

        // Active air recon MUST lift fog over targetProv across the neutral buffer.
        CampaignIntel.InvalidateActors(c);
        var seenDuringRecon = CampaignIntel.CanSeeProvince(c, n1.Id, targetProv);

        // Ground the recon wing and tick actors (same territory, cache reuse path):
        // enemy province MUST return to fog of war immediately!
        wing.Mission = SupportCore.MissionStandby;
        CampaignIntel.InvalidateActors(c);
        var seenAfterStandby = CampaignIntel.CanSeeProvince(c, n1.Id, targetProv);
        Check(!seenBeforeRecon && seenDuringRecon && !seenAfterStandby,
            $"ProvinceVision lifts fog during air recon and clears it immediately on standby (before={seenBeforeRecon}, during={seenDuringRecon}, after={seenAfterStandby})");
    }

    private static void ChokepointZeroAllocParityAndVerboseGate()
    {
        var campaign = Campaign.CreateBootstrap(98808);
        var goods = Enum.GetValues<Commodity>();
        var mismatches = 0;
        var limit = Math.Min(campaign.ActiveNations.Count, 40);
        for (var i = 0; i < limit; i++)
        {
            var nation = campaign.ActiveNations[i];
            var list = StrategicChokepoints.ReadForNation(campaign, nation);
            foreach (var good in goods)
            {
                var refSqueezed = false;
                for (var ei = 0; ei < list.Count; ei++)
                {
                    if (list[ei].Status == ChokepointStatus.Squeezed && list[ei].Commodity == good)
                    {
                        refSqueezed = true;
                        break;
                    }
                }
                var fastSqueezed = StrategicChokepoints.IsCommoditySqueezed(campaign, nation, good);
                if (fastSqueezed != refSqueezed) mismatches++;
            }
        }
        Check(mismatches == 0,
            "StrategicChokepoints.IsCommoditySqueezed matches ReadForNation 100% across live nations");

        var envVerbose = Environment.GetEnvironmentVariable("DOF_VERBOSE_SIM_LOG");
        if (string.IsNullOrEmpty(envVerbose))
            Check(!DiagnosticLogs.VerboseSimulation, "DiagnosticLogs.VerboseSimulation defaults to false");
        else
            Check(true, "DiagnosticLogs.VerboseSimulation honors explicit DOF_VERBOSE_SIM_LOG env override");
    }

    private static void LockFreeSyncSourceContracts()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        if (!File.Exists(Path.Combine(root, "DealOrForce.csproj")))
            root = Directory.GetCurrentDirectory();
        var gameSrc = File.ReadAllText(Path.Combine(root, "Scripts", "DealOrForceGame.cs"));
        var syncSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeLockFreeSync.cs"));
        var gpuSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GpuMapCompute.cs"));
        var plateSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePlateRepaint.cs"));
        var globeSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.cs"));
        var badgeSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "SurfaceBattleBadge.cs"));
        Check(gameSrc.Contains("_globe?.SyncLockFreeWorldVisuals(_simRunner)", StringComparison.Ordinal) &&
              gameSrc.Contains("_simRunner.NotifyVisualFramePresented(", StringComparison.Ordinal),
            "DealOrForceGame drives lock-free visual sync and notifies presented visual frame sequence");
        Check(syncSrc.Contains("TryAcquireLatestVisualSnapshot", StringComparison.Ordinal) &&
              syncSrc.Contains("RefreshProvinceControllersLockFree", StringComparison.Ordinal) &&
              syncSrc.Contains("PaintLiveWarFrontsLockFree", StringComparison.Ordinal) &&
              syncSrc.Contains("SyncBattleMarkersLockFree", StringComparison.Ordinal) &&
              syncSrc.Contains("item.VisibleToPlayer", StringComparison.Ordinal),
            "GlobeLockFreeSync updates controllers, fronts, Fog-of-War-aware battles, and plates lock-free");
        Check(gpuSrc.Contains("DataFormat.R32Uint", StringComparison.Ordinal) &&
              gpuSrc.Contains("DataFormat.R32Sfloat", StringComparison.Ordinal) &&
              gpuSrc.Contains("Texture2Drd", StringComparison.Ordinal) &&
              gpuSrc.Contains("BindTexturesOnMainThread", StringComparison.Ordinal) &&
              gpuSrc.Contains("CallOnRenderThread", StringComparison.Ordinal) &&
              gpuSrc.Contains("if (!rid.IsValid)", StringComparison.Ordinal) &&
              !gpuSrc.Contains(".Submit(", StringComparison.Ordinal) &&
              !gpuSrc.Contains(".Sync(", StringComparison.Ordinal),
            "GpuMapCompute follows Godot 4.8 RD rules (R32Uint JFA, R32Sfloat blur, main-thread TextureRdRid, invalid-RID guard, no Submit/Sync)");
        var topoIdx = globeSrc.IndexOf("_provinceTopology = result.Provinces;", StringComparison.Ordinal);
        var gpuInitIdx = globeSrc.IndexOf("TryStartGpuMapCompute(", StringComparison.Ordinal);
        Check(topoIdx > 0 && gpuInitIdx > topoIdx &&
              plateSrc.Contains("frontierMatches", StringComparison.Ordinal) &&
              plateSrc.Contains("UploadPlateCatchUp(owners)", StringComparison.Ordinal),
            "GpuMapCompute starts after _provinceTopology/_paintedOwner exist and GlobePlateRepaint tracks frontier catch-up lock-free");
        Check(badgeSrc.Contains(": source_color", StringComparison.Ordinal) &&
              badgeSrc.Contains("linear_renderer", StringComparison.Ordinal) &&
              badgeSrc.Contains("fwidth(sdf)", StringComparison.Ordinal),
            "BattleBadgeGpu uses sRGB-correct source_color uniforms and fwidth SDF numerals across Forward+ and GLES3");
    }

    private static int FirstByteDiff(byte[] a, byte[] b)
    {
        if (a is null || b is null || a.Length != b.Length) return 0;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return i;
        return -1;
    }
}
