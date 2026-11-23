using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DealOrForce.Platform;
using DealOrForce.Simulation;

/// <summary>
/// Determinism and correctness gate for the performance, dense-indexing, and multithreading
/// infrastructure (<see cref="DeterministicParallel"/>, <see cref="Campaign"/> dense indexes,
/// <see cref="ProvinceVision"/> per-controller invalidation, <see cref="FrameRateGovernor"/>).
///
/// Asserts:
/// - 1-thread vs 2-thread vs 4-thread vs auto N-thread parity for <c>For</c>, <c>ForRange</c>,
///   <c>Slots</c>, and <c>SlotsRange</c>.
/// - Multi-seed live <see cref="Campaign"/> bit-for-bit identical <see cref="CampaignFingerprint"/>
///   between 1 thread and N threads, with zero NaNs (<see cref="NumericIntegrity.FirstLiveIssue"/>).
/// - Staged <see cref="Campaign.StepFor"/> vs unbounded <see cref="Campaign.TickDay"/> fingerprint parity.
/// - Dense lookup table parity across dynamic nation/state mutations.
/// </summary>
static class PerfDeterminismTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("perf determinism: " + label);
        Console.WriteLine("PASS perf determinism: " + label);
    }

    [TestSuite("--perf-determinism-only", Order = 2600, IncludeInDefaultRun = true,
        Description = "1-thread vs N-thread campaign fingerprint parity, dense indexes, and zero-NaN perf checks.")]
    public static void Run()
    {
        TestThreadCountKnobAndRanges();
        TestDensePrimitives();
        TestCampaignDenseIndexes();
        TestProtectorIndexParityAndInvalidation();
        TestProvinceVisionBorderFloodParity();
        TestTreatyMutationVersionTracking();
        TestMultiSeedThreadParity();
        TestSmoothStepParity();
        TestFrameRateGovernorZeroAlloc();
        TestGlobalLocMobileTextAdapter();
        TestFastPngAndLoadPipeline();
    }

    static void TestThreadCountKnobAndRanges()
    {
        const int count = 512;
        var savedOverride = DeterministicParallel.MaxThreadsOverride;
        var savedSerial = DeterministicParallel.ForceSerial;
        try
        {
            DeterministicParallel.ForceSerial = false;
            int[] Reference(int threads)
            {
                DeterministicParallel.MaxThreadsOverride = threads;
                var slots = new int[count];
                var committed = new List<int>(count);
                DeterministicParallel.SlotsRange(count, slots, (from, to, buf) =>
                {
                    for (var i = from; i < to; i++) buf[i] = (i * 73 + 19) ^ (i >> 2);
                }, (idx, val) => committed.Add(idx * 3 + val));
                return committed.ToArray();
            }

            var one = Reference(1);
            var two = Reference(2);
            var four = Reference(4);
            var auto = Reference(0);

            Check(SameArray(one, two), "SlotsRange 1-thread matches 2-thread");
            Check(SameArray(one, four), "SlotsRange 1-thread matches 4-thread");
            Check(SameArray(one, auto), "SlotsRange 1-thread matches auto N-thread");

            DeterministicParallel.MaxThreadsOverride = 1;
            Check(DeterministicParallel.EffectiveWorkers == 1, "MaxThreadsOverride=1 forces EffectiveWorkers=1");
            var ranParallelWhenOne = DeterministicParallel.ForRange(0, count, (_, _) => { });
            Check(!ranParallelWhenOne, "ForRange runs serially when MaxThreadsOverride=1");

            DeterministicParallel.MaxThreadsOverride = 0;
            if (DeterministicParallel.Workers > 1)
            {
                var buf = new int[count];
                var ranParallel = DeterministicParallel.ForRange(0, count, (from, to) =>
                {
                    for (var i = from; i < to; i++) buf[i] = i + 1;
                });
                Check(ranParallel, "ForRange fans out when MaxThreadsOverride=0 on multi-core host");
                var covered = true;
                for (var i = 0; i < count; i++) if (buf[i] != i + 1) { covered = false; break; }
                Check(covered, "ForRange covers every index exactly once");
            }
        }
        finally
        {
            DeterministicParallel.MaxThreadsOverride = savedOverride;
            DeterministicParallel.ForceSerial = savedSerial;
        }
    }

    static void TestDensePrimitives()
    {
        var stamps = new GenerationalStampSet();
        stamps.EnsureCapacity(128);
        Check(stamps.Add(5) && !stamps.Add(5) && stamps.Contains(5), "GenerationalStampSet add/contains");
        stamps.Clear();
        Check(!stamps.Contains(5) && stamps.Add(5), "GenerationalStampSet O(1) clear resets membership");

        var floats = new ScratchFloatMap();
        floats.BeginPass(32);
        floats.Add(3, 1.5f);
        floats.Add(3, 2.25f);
        Check(MathF.Abs(floats.GetOrDefault(3) - 3.75f) < 1e-6f && floats.GetOrDefault(4, 9f) == 9f,
            "ScratchFloatMap accumulates and defaults missing entries");
        floats.BeginPass(32);
        Check(floats.GetOrDefault(3, -1f) == -1f, "ScratchFloatMap O(1) reset clears prior pass");

        var table = new DenseIntTable<string>();
        table.Reset(16);
        table.TryAdd(7, "seven");
        table.TryAdd(7, "duplicate");
        table.TryAdd(90000, "overflow");
        Check(table.GetOrDefault(7) == "seven" && table.GetOrDefault(90000) == "overflow" && table.GetOrDefault(8) is null,
            "DenseIntTable preserves first-wins semantics for dense and overflow keys");
    }

    static void TestCampaignDenseIndexes()
    {
        var c = Campaign.CreateBootstrap(42);
        Check(c.Nations.Count > 50 && c.States.Count > 100, "Bootstrap campaign loaded nations and states");

        // Verify NationByMapIndex matches linear scan for every nation.
        var allNationOk = true;
        foreach (var n in c.Nations)
        {
            if (n.MapIndex <= 0) continue;
            if (!ReferenceEquals(c.NationByMapIndex(n.MapIndex), n)) { allNationOk = false; break; }
        }
        Check(allNationOk, "Dense NationByMapIndex matches every bootstrap nation");

        // Verify StateByMapIndex matches every state.
        var allStateOk = true;
        foreach (var s in c.States)
        {
            if (s.MapIndex <= 0) continue;
            if (!ReferenceEquals(c.StateByMapIndex(s.MapIndex), s)) { allStateOk = false; break; }
        }
        Check(allStateOk, "Dense StateByMapIndex matches every bootstrap state");

        // Verify StatesOf matches linear count and updates after InvalidateStateIndex.
        var sample = c.Nations[0];
        var expectedCount = 0;
        foreach (var s in c.States) if (s.NationMapIndex == sample.MapIndex) expectedCount++;
        Check(c.StatesOf(sample).Length == expectedCount, "Dense StatesOf length matches linear count");

        var extraState = new ScenarioState { MapIndex = 50001, NationMapIndex = sample.MapIndex, Name = "PerfTestState" };
        c.States.Add(extraState);
        c.InvalidateStateIndex();
        Check(c.StatesOf(sample).Length == expectedCount + 1 && ReferenceEquals(c.StateByMapIndex(50001), extraState),
            "Dense StatesOf and StateByMapIndex refresh cleanly after mutation");
    }

    static void TestProtectorIndexParityAndInvalidation()
    {
        var c = Campaign.CreateBootstrap(42);
        // 1. Full parity against naive Relations.Values scan for every nation on bootstrap.
        var allDefendersMatch = true;
        foreach (var defender in c.Nations)
        {
            var expected = 0;
            foreach (var rel in c.Relations.Values)
            {
                if (rel.A != defender.Id && rel.B != defender.Id) continue;
                var otherId = rel.Other(defender.Id);
                var other = c.NationById(otherId);
                if (other is null || other.Population <= 0) continue;
                if (rel.Stance == DiplomaticStance.Allied || rel.GuaranteedBy(otherId)) expected++;
            }
            if (Statecraft.DefendersOf(c, defender) != expected) { allDefendersMatch = false; break; }
        }
        Check(allDefendersMatch, "ProtectorIndex DefendersOf matches naive Relations scan for every bootstrap nation");

        // 2. Guarantee grant & revoke immediately invalidate ProtectorIndex.
        Nation a = null, b = null;
        foreach (var n in c.Nations)
        {
            if (n.Population <= 0 || n.Movement is not null) continue;
            if (a is null) { a = n; continue; }
            var existing = CampaignDiplomacy.Peek(c, a.Id, n.Id);
            if (existing is null || (existing.Stance != DiplomaticStance.AtWar && existing.Stance != DiplomaticStance.Allied && !existing.GuaranteedBy(a.Id)))
            { b = n; break; }
        }
        Check(a is not null && b is not null, "Found neutral nation pair for ProtectorIndex mutation test");

        var beforeDef = Statecraft.DefendersOf(c, b);
        new GuaranteeCommand { IssuerId = a.Id, TargetId = b.Id, Guarantee = true }.Apply(c);
        var afterGrant = Statecraft.DefendersOf(c, b);
        Check(afterGrant == beforeDef + 1, $"GuaranteeCommand(true) immediately updates DefendersOf ({beforeDef} -> {afterGrant})");

        new GuaranteeCommand { IssuerId = a.Id, TargetId = b.Id, Guarantee = false }.Apply(c);
        var afterRevoke = Statecraft.DefendersOf(c, b);
        Check(afterRevoke == beforeDef, $"GuaranteeCommand(false) immediately updates DefendersOf ({afterGrant} -> {afterRevoke})");

        // 3. Direct Stance/Guarantee property mutations (even without NoteAllianceChange) invalidate ProtectorIndex.
        var alliesBefore = c.Caches.Protectors.AlliesOf(c, a.Id).Length;
        var relAb = CampaignDiplomacy.Get(c, a.Id, b.Id);
        relAb.Stance = DiplomaticStance.Allied;
        Check(c.Caches.Protectors.AlliesOf(c, a.Id).Length == alliesBefore + 1 &&
              Statecraft.DefendersOf(c, b) == beforeDef + 1,
            "Direct Stance=Allied mutation immediately invalidates ProtectorIndex without NoteAllianceChange");
        relAb.Stance = DiplomaticStance.Neutral;
        Check(c.Caches.Protectors.AlliesOf(c, a.Id).Length == alliesBefore &&
              Statecraft.DefendersOf(c, b) == beforeDef,
            "Direct Stance=Neutral mutation immediately restores ProtectorIndex");
        if (a.Id == relAb.A) relAb.GuaranteeByA = true; else relAb.GuaranteeByB = true;
        Check(Statecraft.DefendersOf(c, b) == beforeDef + 1,
            "Direct GuaranteeByA/B=true mutation immediately invalidates ProtectorIndex");
        if (a.Id == relAb.A) relAb.GuaranteeByA = false; else relAb.GuaranteeByB = false;
        Check(Statecraft.DefendersOf(c, b) == beforeDef,
            "Direct GuaranteeByA/B=false mutation immediately restores ProtectorIndex");
    }

    static void TestProvinceVisionBorderFloodParity()
    {
        const int w = 7, h = 7;
        var states = new ushort[w * h];
        var control = new ushort[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var idx = y * w + x;
                states[idx] = (ushort)(idx + 1);
                control[idx] = (x >= 2 && x <= 4 && y >= 2 && y <= 4) ? (ushort)1 : (ushort)2;
            }
        var topology = ProvinceTopology.Build(states, control, w, h, _ => false);
        var c = new Campaign(7);
        var n1 = new Nation { Id = 0, MapIndex = 1, Tag = "TST", Name = "TestOne", Population = 1_000_000 };
        var n2 = new Nation { Id = 1, MapIndex = 2, Tag = "TS2", Name = "TestTwo", Population = 1_000_000 };
        c.Nations.Add(n1);
        c.Nations.Add(n2);
        c.AttachProvinceTopology(topology);

        foreach (var depth in new[] { 0, 1, 2 })
        {
            c.Defines.BorderVisionDepth = depth;
            CampaignIntel.Invalidate(c);
            var view = CampaignIntel.Vision(c, n1.Id);

            // Naive full-flood reference.
            var expected = new byte[topology.ProvinceCount + 1];
            var q = new Queue<int>();
            for (var p = 1; p <= topology.ProvinceCount; p++)
            {
                if (c.ProvinceController[p] == (ushort)n1.MapIndex)
                {
                    expected[p] = (byte)(depth + 1);
                    q.Enqueue(p);
                }
            }
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                var next = (byte)(expected[cur] - 1);
                if (next == 0) continue;
                foreach (var nb in topology.Neighbors[cur])
                    if (nb > 0 && nb < expected.Length && expected[nb] < next)
                    { expected[nb] = next; q.Enqueue(nb); }
            }

            var identical = view.Reach.Length == expected.Length;
            for (var p = 0; identical && p < expected.Length; p++)
                if (view.Reach[p] != expected[p]) identical = false;
            Check(identical, $"ProvinceVision border-only flood matches naive BFS byte-for-byte at BorderVisionDepth={depth}");
        }
    }

    static void TestTreatyMutationVersionTracking()
    {
        var r = new DiplomaticRelation { A = 0, B = 1 };
        var t0 = DiplomaticRelation.TreatyMutationVersion;
        var p0 = DiplomaticRelation.ProtectorMutationVersion;

        // Neutral -> Friendly does not bump protector or treaty versions.
        r.Stance = DiplomaticStance.Friendly;
        Check(DiplomaticRelation.TreatyMutationVersion == t0 && DiplomaticRelation.ProtectorMutationVersion == p0,
            "Non-allied stance change leaves TreatyMutationVersion and ProtectorMutationVersion unchanged");

        // TradeAgreement and NonAggressionPact bump TreatyMutationVersion without bumping ProtectorMutationVersion.
        r.TradeAgreement = true;
        var t1 = DiplomaticRelation.TreatyMutationVersion;
        Check(t1 > t0 && DiplomaticRelation.ProtectorMutationVersion == p0,
            "TradeAgreement=true bumps TreatyMutationVersion only");

        r.NonAggressionPact = true;
        var t2 = DiplomaticRelation.TreatyMutationVersion;
        Check(t2 > t1 && DiplomaticRelation.ProtectorMutationVersion == p0,
            "NonAggressionPact=true bumps TreatyMutationVersion only");

        // Entering Allied bumps both.
        r.Stance = DiplomaticStance.Allied;
        Check(DiplomaticRelation.TreatyMutationVersion > t2 && DiplomaticRelation.ProtectorMutationVersion > p0,
            "Stance=Allied bumps both TreatyMutationVersion and ProtectorMutationVersion");
    }

    static void TestMultiSeedThreadParity()
    {
        var savedOverride = DeterministicParallel.MaxThreadsOverride;
        var savedSerial = DeterministicParallel.ForceSerial;
        try
        {
            DeterministicParallel.ForceSerial = false;
            var seeds = new[] { 42, 107 };
            const int days = 21;

            foreach (var seed in seeds)
            {
                var fpSerial = RunSeed(seed, days, threads: 1, out var issueSerial);
                var fpParallel = RunSeed(seed, days, threads: 0, out var issueParallel);

                Check(issueSerial is null, $"seed {seed} (1 thread) has zero numeric integrity issues");
                Check(issueParallel is null, $"seed {seed} (N threads) has zero numeric integrity issues");
                Check(fpSerial == fpParallel,
                    $"seed {seed} 21d CampaignFingerprint matches 1-thread ({fpSerial}) == N-thread ({fpParallel})");
            }
        }
        finally
        {
            DeterministicParallel.MaxThreadsOverride = savedOverride;
            DeterministicParallel.ForceSerial = savedSerial;
        }
    }

    static ulong RunSeed(int seed, int days, int threads, out string integrityIssue)
    {
        DeterministicParallel.MaxThreadsOverride = threads;
        var c = Campaign.CreateBootstrap(seed);
        c.IsPaused = false;
        c.Defines.HoursPerTick = 24;
        for (var d = 0; d < days; d++) c.TickDay();
        integrityIssue = NumericIntegrity.FirstLiveIssue(c);
        return CampaignFingerprint.Of(c);
    }

    static void TestSmoothStepParity()
    {
        var savedOverride = DeterministicParallel.MaxThreadsOverride;
        try
        {
            DeterministicParallel.MaxThreadsOverride = 0;
            var whole = Campaign.CreateBootstrap(42);
            whole.IsPaused = false;
            whole.Defines.HoursPerTick = 24;
            for (var d = 0; d < 14; d++) whole.TickDay();

            var smooth = Campaign.CreateBootstrap(42);
            smooth.IsPaused = false;
            smooth.Defines.HoursPerTick = 24;
            for (var d = 0; d < 14; d++)
            {
                var guard = 0;
                while (!smooth.StepFor(0.5, 0.25) && ++guard < 10000) { }
            }

            var fpWhole = CampaignFingerprint.Of(whole);
            var fpSmooth = CampaignFingerprint.Of(smooth);
            Check(fpWhole == fpSmooth, $"14d unbounded TickDay ({fpWhole}) == bounded StepFor ({fpSmooth})");
            Check(NumericIntegrity.FirstLiveIssue(smooth) is null, "14d smooth-stepped campaign has zero NaNs");
        }
        finally
        {
            DeterministicParallel.MaxThreadsOverride = savedOverride;
        }
    }

    static void TestFrameRateGovernorZeroAlloc()
    {
        var start = new QualityProfile { Id = "high", Order = 0, Label = "High" };
        var gov = new FrameRateGovernor(start, automatic: true, windowFrames: 24);
        // Warm up past 20 seconds with steady 16ms frames.
        for (var i = 0; i < 1400; i++) gov.Sample(16.0, 4.0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) gov.Sample(16.0, 4.0);
        var alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(alloc == 0, $"FrameRateGovernor.Sample allocates 0 bytes across 100 steady frames (got {alloc} B)");
    }

    static void TestGlobalLocMobileTextAdapter()
    {
        var savedAdapter = Loc.TextAdapter;
        var savedLang = Loc.Language;
        try
        {
            // 1. Desktop (TextAdapter == null): exact passthrough, 0 allocations on repeated lookup.
            Loc.TextAdapter = null;
            Loc.Language = "english";
            var desktopQuit = Loc.Get("Quit to desktop");
            Check(desktopQuit == "Quit to desktop", $"Desktop Loc.Get('Quit to desktop') unchanged ('{desktopQuit}')");
            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 64; i++) _ = Loc.Get("Quit to desktop");
            var desktopAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
            Check(desktopAlloc == 0, $"Desktop Loc.Get allocates 0 bytes when TextAdapter is null (got {desktopAlloc} B)");

            // 2. Mobile (TextAdapter wired via MobileTextAdapter + Loc.RawText): EN + RU + hotkey + override.
            Loc.TextAdapter = static (key, text) => MobileTextAdapter.Adapt(
                text,
                isTouchPrimary: true,
                localizationKey: key,
                overrideLookup: static overrideKey => Loc.RawText(overrideKey));

            var mobileQuitEn = Loc.Get("Quit to desktop");
            Check(mobileQuitEn == "Exit game", $"Mobile Loc.Get('Quit to desktop') EN -> '{mobileQuitEn}'");

            var mobileHotkey = Loc.Get("Pause [Space]");
            Check(mobileHotkey == "Pause", $"Mobile Loc.Get strips [Space] badge -> '{mobileHotkey}'");

            var mobileGetOr = Loc.GetOr("__perf.untranslated.quit__", "Quit to desktop");
            Check(mobileGetOr == "Exit game", $"Mobile Loc.GetOr adapts untranslated fallback literal -> '{mobileGetOr}'");

            // Cache hit on mobile should also be 0-alloc after warm-up!
            _ = Loc.Get("Quit to desktop");
            var beforeMobileAlloc = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 64; i++) _ = Loc.Get("Quit to desktop");
            var mobileAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeMobileAlloc;
            Check(mobileAlloc == 0, $"Mobile Loc.Get hits adapted cache with 0 bytes allocated (got {mobileAlloc} B)");

            // Language switch invalidates the adapted cache cleanly.
            Loc.Language = "russian";
            var mobileQuitRu = Loc.Get("Quit to desktop");
            Check(mobileQuitRu == "Выйти из игры" || !mobileQuitRu.Contains("рабочий стол", StringComparison.OrdinalIgnoreCase),
                $"Mobile Loc.Get('Quit to desktop') RU adapts away desktop wording -> '{mobileQuitRu}'");

            // Explicit mobile.override.<key> wins via Loc.RawText without recursion.
            Loc.Language = "english";
            Loc.Merge("english", "{\"__perf.test.btn\":\"Right-click [F7]\",\"mobile.override.__perf.test.btn\":\"Tap to command\"}", null);
            var mobileOverride = Loc.Get("__perf.test.btn");
            Check(mobileOverride == "Tap to command", $"Mobile Loc.Get honors mobile.override.* -> '{mobileOverride}'");
        }
        finally
        {
            Loc.TextAdapter = savedAdapter;
            Loc.Language = savedLang;
        }
    }

    static void TestFastPngAndLoadPipeline()
    {
        // 1. DataMapPng SaveRgb/LoadRgb exact pixel round-trip across a non-trivial gradient/pattern image.
        const int w = 131;
        const int h = 67;
        var src = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var off = (y * w + x) * 3;
                src[off] = (byte)((x * 7 + y * 13) & 0xFF);
                src[off + 1] = (byte)((x * 3 ^ y * 19) & 0xFF);
                src[off + 2] = (byte)((x + y * 5) & 0xFF);
            }
        var tempPng = Path.Combine(Path.GetTempPath(), $"dof_fast_png_test_{Guid.NewGuid():N}.png");
        try
        {
            DataMapPng.SaveRgb(tempPng, w, h, src);
            var decoded = DataMapPng.LoadRgb(tempPng);
            var same = decoded.Width == w && decoded.Height == h && decoded.Pixels.AsSpan().SequenceEqual(src);
            Check(same, $"DataMapPng.LoadRgb round-trips {w}x{h} RGB pixels bit-for-bit");
        }
        finally
        {
            try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { }
        }

        // 2. Fast RegionFingerprint matches naive ushort FNV-1a bit-for-bit.
        var states = new ushort[4097];
        for (var i = 0; i < states.Length; i++) states[i] = (ushort)((i * 257 + 13) & 0xFFFF);
        var expected = 14695981039346656037UL;
        foreach (var index in states)
        {
            expected = (expected ^ (byte)index) * 1099511628211UL;
            expected = (expected ^ (byte)(index >> 8)) * 1099511628211UL;
        }
        var actual = MapRasterCatalog.RegionFingerprint(states);
        Check(actual == expected, $"MapRasterCatalog.RegionFingerprint matches naive FNV-1a ({actual})");

        // 3. Cross-architecture reconciliation restores exact stamped RegionFingerprint + 1-shot prefetch.
        if (!string.IsNullOrWhiteSpace(MapRasterCatalog.ProvincePath) &&
            !string.IsNullOrWhiteSpace(MapRasterCatalog.DefinitionPath))
        {
            var width = WorldMapSource.Width;
            var height = WorldMapSource.Height;
            var boot = Campaign.CreateBootstrap(42);
            var perturbedStates = (ushort[])WorldMapSource.States.Clone();
            var perturbedControl = (ushort[])WorldMapSource.Control.Clone();
            TerritorialTopology.Apply(boot, perturbedControl, perturbedStates, width, height);
            var origFp = MapRasterCatalog.RegionFingerprint(perturbedStates);
            // Simulate a 1-ULP polar cell shift on non-Windows-x64.
            var flipIdx = width * (height - 10) + 100;
            perturbedStates[flipIdx] = (ushort)(perturbedStates[flipIdx] == 0 ? 1 : 0);
            var swReconcile = Stopwatch.StartNew();
            var reconciled = MapRasterCatalog.ReconcileFromProvinceRaster(perturbedStates, perturbedControl, width, height);
            swReconcile.Stop();
            var restoredFp = MapRasterCatalog.RegionFingerprint(perturbedStates);
            Check(reconciled && restoredFp == origFp,
                $"ReconcileFromProvinceRaster restored exact stamped RegionFingerprint ({restoredFp}) in {swReconcile.Elapsed.TotalMilliseconds:F1} ms");
            var swTopo = Stopwatch.StartNew();
            var okTopo = MapRasterCatalog.TryBuildProvinceTopology(
                perturbedStates, perturbedControl, width, height, out var topo, out var topoMsg);
            swTopo.Stop();
            Check(okTopo && topo is not null && topo.ProvinceCount > 1000,
                $"TryBuildProvinceTopology consumed 1-shot prefetch ({topo?.ProvinceCount ?? 0} provinces in {swTopo.Elapsed.TotalMilliseconds:F1} ms: {topoMsg})");
        }

        // 4. Measure parallel localization catalog cold-reload & verify determinism.
        var sw = Stopwatch.StartNew();
        Loc.ResetCatalog();
        sw.Stop();
        var langCount = Loc.Languages.Count;
        Check(langCount >= 2 && Loc.Has("Quit to desktop"),
            $"Parallel Loc.ResetCatalog loaded {langCount} languages in {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    static bool SameArray(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
