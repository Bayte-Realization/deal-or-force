// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Rules;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Kernels;

static class PolyglotKernelsTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("polyglot kernels: " + label);
        Console.WriteLine("PASS polyglot kernels: " + label);
    }

    private static double Percentile(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var idx = Math.Clamp((int)Math.Floor((sorted.Count - 1) * q), 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static (double MedianMs, double P95Ms) MeasureMs(int warmup, int samples, Action action)
    {
        for (var i = 0; i < warmup; i++) action();
        var timings = new List<double>(samples);
        var sw = new Stopwatch();
        for (var i = 0; i < samples; i++)
        {
            sw.Restart();
            action();
            sw.Stop();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        return (Percentile(timings, 0.50), Percentile(timings, 0.95));
    }

    private static ulong HashBytes(byte[] bytes)
    {
        var h = 14695981039346656037UL;
        for (var i = 0; i < bytes.Length; i += 17)
            h = (h ^ bytes[i]) * 1099511628211UL;
        return h ^ (ulong)bytes.Length;
    }

    [TestSuite("--polyglot-kernels-only", IncludeInDefaultRun = false, Description = "Native dof_kernels ABI, F# struct rule parity, pure-C# fallback, and kernel benchmarks.")]
    public static void Run()
    {
        Console.WriteLine($"POLYGLOT_KERNELS diagnostic={NativeKernelsLoader.Diagnostic}");
        var dllExpected = Environment.GetEnvironmentVariable("DOF_EXPECT_NATIVE_KERNELS") == "1";
        var nativePresent = NativeKernelsLoader.IsAvailable;
        if (dllExpected)
            Check(nativePresent && NativeKernelsLoader.AbiVersion == 1,
                $"native dof_kernels.dll expected & loaded (abi={NativeKernelsLoader.AbiVersion}, simd={NativeKernelsLoader.SimdLevel})");

        RunFSharpDifferentialAndAllocTests();
        RunFallbackAndSafetyTests();
        RunRealWorldKernelSuitesAndBenchmarks(nativePresent);
    }

    private static void RunFSharpDifferentialAndAllocTests()
    {
        // Confirm all 4 new boot hooks + existing hooks are active under FormableTests.
        Check(EconomyRulesHook.UsingFSharp, "EconomyRulesHook active with F# boot");
        Check(DemographyRulesHook.UsingFSharp, "DemographyRulesHook active with F# boot");
        Check(DiplomacyRulesHook.UsingFSharp, "DiplomacyRulesHook active with F# boot");
        Check(CombatTempoRulesHook.UsingFSharp, "CombatTempoRulesHook active with F# boot");
        Check(WarScoreRulesHook.IsInstalled, "WarScoreRulesHook active with F# boot");
        Check(AssaultRulesHook.UsingFSharp, "AssaultRulesHook active with F# boot");
        Check(WarRhythm.IsInstalled, "WarRhythm F# boot active");

        // Boundary + 12,000-sample deterministic fuzz comparing F# [<Struct>] vs Managed C# bit-for-bit.
        uint rng = 0x517CC1B7u;
        float NextFloat(float min, float max)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            var u = (rng & 0x00FFFFFFu) / 16777216f;
            return min + u * (max - min);
        }
        int NextInt(int min, int maxInclusive)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            return min + (int)(rng % (uint)(maxInclusive - min + 1));
        }

        // Pre-JIT all paths before measuring zero-alloc assertions.
        EconomyRules.EvaluateCreditTerms(10f, 100f, 0.7f, 0.03f, 0f, 0f, false, 0.8f, 1f, 0.25f);
        EconomyRulesHook.EvaluateCreditTerms(10f, 100f, 0.7f, 0.03f, 0f, 0f, false, 0.8f, 1f, 0.25f, out _, out _, out _);
        EconomyRulesHook.ManagedEvaluateCreditTerms(10f, 100f, 0.7f, 0.03f, 0f, 0f, false, 0.8f, 1f, 0.25f, out _, out _, out _);
        EconomyRules.EvaluateTradeCost(10f, 4f, 2f, 1.25f, 0.5f, 0.1f);
        EconomyRulesHook.EvaluateTradeCost(10f, 4f, 2f, 1.25f, 0.5f, 0.1f, out _, out _);
        EconomyRulesHook.ManagedEvaluateTradeCost(10f, 4f, 2f, 1.25f, 0.5f, 0.1f, out _, out _);
        EconomyRules.TaxCompliance(true, 0.35f, 0.8f, 0.2f);
        EconomyRulesHook.TaxCompliance(true, 0.35f, 0.8f, 0.2f);
        EconomyRules.EvaluateBudgetLineAmount(100f, 0.2f, 1f, 1f, true, 50f, 0.04f, true, 0.9f, 1.05f, false);
        DemographyRules.StepCohorts(100000f, 1200, 900, 0.22f, 0.18f, 0.05f, 0.03f, 0.6f, 0.1f);
        DemographyRulesHook.StepCohorts(100000f, 1200, 900, 0.22f, 0.18f, 0.05f, 0.03f, 0.6f, 0.1f, out _, out _, out _);
        DiplomacyRules.EvaluateDeterrence(2, 1, 6f, true);
        DiplomacyRulesHook.EvaluateDeterrence(2, 1, 6f, true, out _, out _);
        CombatTempoRules.EvaluateTempoBundle(true, true, 4, 0.3f, 10, 3, 4, 0.1f, 1.6f, 0.5f, 0.7f, 0.2f, 1f, 0.3f, 0.4f, 1.1f, 0.8f, 0.2f, 1.3f, 0.4f, 0.7f, 0.5f, 0.6f, false, 0.2f, 0.4f, 1.1f, 0.8f, 1.5f, 0.2f);
        CombatTempoRules.EvaluateSupplyDelivery(100f, 80f, 4, 20, 1f, 0.2f, 0.6f, 0.9f, 1.1f, 0.5f);
        CombatTempoRulesHook.EvaluateSupplyDelivery(100f, 80f, 4, 20, 1f, 0.2f, 0.6f, 0.9f, 1.1f, 0.5f, out _, out _, out _, out _, out _);
        PeaceQuotaRules.EvaluateQuota(3, 5, 12.5f, 20f);

        const int fuzzCases = 12_000;
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < fuzzCases; i++)
        {
            // Special boundary corner injections on first 20 iterations
            var debt = i == 0 ? 0f : i == 1 ? -10f : NextFloat(-5f, 5000f);
            var gdp = i == 2 ? 0f : i == 3 ? -1f : NextFloat(0f, 5000f);
            var stab = NextFloat(-0.5f, 1.5f);
            var infl = NextFloat(-0.2f, 1.5f);
            var arr1 = NextFloat(0f, 400f);
            var arr2 = NextFloat(0f, 400f);
            var def = (i & 7) == 0;
            var conf = NextFloat(-0.2f, 1.3f);
            var borrow = NextFloat(0.2f, 2.5f);
            var maxSpread = NextFloat(-0.2f, 1.2f);

            var fsCredit = EconomyRules.EvaluateCreditTerms(debt, gdp, stab, infl, arr1, arr2, def, conf, borrow, maxSpread);
            EconomyRulesHook.EvaluateCreditTerms(debt, gdp, stab, infl, arr1, arr2, def, conf, borrow, maxSpread, out var ht, out var hn, out var hs);
            EconomyRulesHook.ManagedEvaluateCreditTerms(debt, gdp, stab, infl, arr1, arr2, def, conf, borrow, maxSpread, out var mt, out var mn, out var ms);
            AssertBitsEqual(fsCredit.TargetConfidence, mt, "Credit.Target");
            AssertBitsEqual(fsCredit.NextConfidence, mn, "Credit.Next");
            AssertBitsEqual(fsCredit.AnnualSpread, ms, "Credit.Spread");
            AssertBitsEqual(ht, mt, "HookCredit.Target");
            AssertBitsEqual(hn, mn, "HookCredit.Next");
            AssertBitsEqual(hs, ms, "HookCredit.Spread");

            var imports = i == 4 ? 0.001f : i == 5 ? 0f : NextFloat(-1f, 200f);
            var relieved = NextFloat(-1f, 220f);
            var price = NextFloat(0f, 50f);
            var mkDef = NextFloat(1f, 2f);
            var relDef = NextFloat(0f, 1f);
            var tar = NextFloat(0f, 0.5f);
            var fsTrade = EconomyRules.EvaluateTradeCost(imports, relieved, price, mkDef, relDef, tar);
            EconomyRulesHook.EvaluateTradeCost(imports, relieved, price, mkDef, relDef, tar, out var hmk, out var hdu);
            EconomyRulesHook.ManagedEvaluateTradeCost(imports, relieved, price, mkDef, relDef, tar, out var mmk, out var mdu);
            AssertBitsEqual(fsTrade.ImportMarkup, mmk, "TradeCost.Markup");
            AssertBitsEqual(fsTrade.Duty, mdu, "TradeCost.Duty");
            AssertBitsEqual(hmk, mmk, "HookTradeCost.Markup");
            AssertBitsEqual(hdu, mdu, "HookTradeCost.Duty");

            var hasPop = (i & 3) != 0;
            var burden = NextFloat(-0.2f, 2f);
            var evas = i == 6 ? 0f : NextFloat(-0.5f, 3f);
            var tol = NextFloat(0f, 1f);
            AssertBitsEqual(EconomyRules.TaxCompliance(hasPop, burden, evas, tol), EconomyRulesHook.ManagedTaxCompliance(hasPop, burden, evas, tol), "TaxCompliance");
            AssertBitsEqual(EconomyRulesHook.TaxCompliance(hasPop, burden, evas, tol), EconomyRulesHook.ManagedTaxCompliance(hasPop, burden, evas, tol), "HookTaxCompliance");

            var fsBgt = EconomyRules.EvaluateBudgetLineAmount(gdp, burden, 0.9f, 1.2f, (i & 1) == 0, debt, 0.04f, (i & 2) == 0, 0.85f, 1.1f, (i & 4) == 0);
            var mBgt = EconomyRulesHook.ManagedEvaluateBudgetLineAmount(gdp, burden, 0.9f, 1.2f, (i & 1) == 0, debt, 0.04f, (i & 2) == 0, 0.85f, 1.1f, (i & 4) == 0);
            AssertBitsEqual(fsBgt, mBgt, "BudgetLineAmount");

            // Demography
            var pop = i == 7 ? 0f : NextFloat(0f, 1e8f);
            var births = NextInt(0, 50000);
            var deaths = NextInt(0, 50000);
            var ys = NextFloat(0.01f, 0.7f);
            var os = NextFloat(0.01f, 0.5f);
            var fsCohorts = DemographyRules.StepCohorts(pop, births, deaths, ys, os, 0.04f, 0.02f, 0.5f, 0.08f);
            DemographyRulesHook.ManagedStepCohorts(pop, births, deaths, ys, os, 0.04f, 0.02f, 0.5f, 0.08f, out var my, out var mo, out var mw);
            AssertBitsEqual(fsCohorts.YoungShare, my, "Demography.Young");
            AssertBitsEqual(fsCohorts.OldShare, mo, "Demography.Old");
            AssertBitsEqual(fsCohorts.WorkingShare, mw, "Demography.Working");
            AssertBitsEqual(DemographyRules.FertilityFactor(ys), DemographyRulesHook.ManagedFertilityFactor(ys), "Demography.Fertility");
            AssertBitsEqual(DemographyRules.MortalityFactor(os, 0.14f, 0.6f), DemographyRulesHook.ManagedMortalityFactor(os, 0.14f, 0.6f), "Demography.Mortality");

            // Diplomacy & Deterrence
            var auth = NextFloat(-1.5f, 1.5f);
            var nat = (i & 1) == 0;
            var rest = NextFloat(-0.2f, 1.2f);
            AssertBitsEqual(DiplomacyRules.ConsolidationAmbition(auth, nat, stab, rest),
                DiplomacyRulesHook.ManagedConsolidationAmbition(auth, nat, stab, rest), "Diplomacy.Ambition");
            var share = NextFloat(-0.1f, 1.2f);
            var kin = NextFloat(0f, 1.2f);
            AssertBitsEqual(DiplomacyRules.ConsolidationWarValue(0.6f, 0.5f, share, kin, 1.2f),
                DiplomacyRulesHook.ManagedConsolidationWarValue(0.6f, 0.5f, share, kin, 1.2f), "Diplomacy.WarValue");
            AssertBitsEqual(DiplomacyRules.BuyPeaceOffer(gdp, debt, conf + 1f, borrow + 1f),
                DiplomacyRulesHook.ManagedBuyPeaceOffer(gdp, debt, conf + 1f, borrow + 1f), "Diplomacy.BuyPeace");

            var tA = NextInt(0, 3);
            var tB = NextInt(0, 3);
            var en = (i & 3) != 0;
            var fsDet = DiplomacyRules.EvaluateDeterrence(tA, tB, 6f, en);
            DiplomacyRulesHook.ManagedEvaluateDeterrence(tA, tB, 6f, en, out var mdp, out var mdb);
            AssertBitsEqual(fsDet.MutualPrice, mdp, "Deterrence.MutualPrice");
            AssertBitsEqual(fsDet.Brinkmanship, mdb, "Deterrence.Brinkmanship");

            // CombatTempo & SupplyDelivery
            var hasDef = (i & 15) != 0;
            var cutOff = (i & 1) == 0;
            var daysCut = NextInt(-1, 25);
            var dirs = NextInt(0, 6);
            var pr = NextFloat(0f, 3f);
            var pl = NextFloat(-0.1f, 1.2f);
            var sup = NextFloat(-0.1f, 1.2f);
            var ent = NextFloat(-0.1f, 1.2f);
            var ao = NextFloat(0f, 1.2f);
            var do_ = NextFloat(0f, 1.2f);
            var desp = (i & 3) == 0;
            var bundle = CombatTempoRules.EvaluateTempoBundle(
                hasDef, cutOff, daysCut, 0.35f, 14, dirs, 4, 0.1f,
                pr, pl, sup, ent, 0.9f, 0.25f, 0.35f, 1.1f, 0.8f, 0.15f,
                pr, ent, sup, ao, do_, desp, 0.2f, 0.3f, 1.1f, 0.8f, 1.5f, 0.25f);
            AssertBitsEqual(bundle.Encirclement, CombatTempoRulesHook.ManagedEncirclementFactor(hasDef, cutOff, daysCut, 0.35f, 14), "Combat.Encirclement");
            AssertBitsEqual(bundle.Envelopment, CombatTempoRulesHook.ManagedEnvelopmentFactor(hasDef, dirs, 4, 0.1f), "Combat.Envelopment");
            AssertBitsEqual(bundle.Breakthrough, CombatTempoRulesHook.ManagedBreakthrough(hasDef, pr, pl, sup, ent, dirs, 0.9f, 0.25f, 0.35f, 1.1f, 0.8f, 0.15f), "Combat.Breakthrough");
            AssertBitsEqual(bundle.Counterattack, CombatTempoRulesHook.ManagedCounterattack(hasDef, pr, ent, sup, ao, do_, desp, 0.2f, 0.3f, 1.1f, 0.8f, 1.5f, 0.25f), "Combat.Counterattack");

            var sOut = CombatTempoRules.EvaluateSupplyDelivery(gdp, debt, daysCut, 18, 0.95f, 0.3f, 0.5f, 0.85f, 1.1f, pl);
            CombatTempoRulesHook.ManagedEvaluateSupplyDelivery(gdp, debt, daysCut, 18, 0.95f, 0.3f, 0.5f, 0.85f, 1.1f, pl,
                out var sc, out var sd, out var sb, out var st, out var sn);
            AssertBitsEqual(sOut.Congestion, sc, "Supply.Congestion");
            AssertBitsEqual(sOut.DistanceFactor, sd, "Supply.Distance");
            AssertBitsEqual(sOut.BlockadeFactor, sb, "Supply.Blockade");
            AssertBitsEqual(sOut.TargetSupply, st, "Supply.Target");
            AssertBitsEqual(sOut.NextSupplyLevel, sn, "Supply.NextLevel");

            _ = PeaceQuotaRules.EvaluateQuota(dirs, 5, price, 25f);
        }
        var allocBytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        Check(allocBytes == 0, $"F# [<Struct>] + C# differential fuzz allocated {allocBytes} bytes across {fuzzCases:N0} multi-rule steps");
    }

    private static void AssertBitsEqual(float a, float b, string name)
    {
        var ba = BitConverter.SingleToInt32Bits(a);
        var bb = BitConverter.SingleToInt32Bits(b);
        if (ba != bb && !(float.IsNaN(a) && float.IsNaN(b)))
            throw new Exception($"polyglot differential mismatch on {name}: a={a} (0x{ba:X8}) b={b} (0x{bb:X8})");
    }

    private static void RunFallbackAndSafetyTests()
    {
        // Null/invalid shape guards never throw and return false cleanly.
        Check(!NativeRasterBake.TryBuildFrontier(null!, null, 0, 0, null, null, 20f, 1f, out _),
            "NativeRasterBake rejects null/zero dimensions silently");
        Check(NativeProvinceGraph.ForMap(null) is null,
            "NativeProvinceGraph.ForMap(null) returns null safely");

        // Verify simulated unavailability falls back cleanly to managed C# with identical results.
        var prevSim = NativeKernelsLoader.SimulateUnavailableForTests;
        try
        {
            var holders = new ushort[] { 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2 };
            var provs = new ushort[] { 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2 };
            var owners = new ushort[] { 0, 1, 2 };
            var expected = FrontierField.BuildManaged(holders, provs, 4, 4, owners, owners);

            NativeKernelsLoader.SimulateUnavailableForTests = true;
            var fallback = FrontierField.Build(holders, provs, 4, 4, owners, owners);
            Check(fallback.SequenceEqual(expected), "FrontierField silent fallback produces identical bytes when native disabled");

            // Faulting hook delegate disables itself once and returns bit-identical C# fallback without crashing.
            EconomyRulesHook.Install(
                (float _, float _, float _, float _, float _, float _, bool _, float _, float _, float _,
                    out float _, out float _, out float _) => throw new InvalidOperationException("injected fault"),
                EconomyRulesHook.ManagedDebtCapacity,
                EconomyRulesHook.ManagedAvailableCredit,
                EconomyRulesHook.ManagedEvaluateTradeCost,
                EconomyRulesHook.ManagedTaxCompliance,
                EconomyRulesHook.ManagedEvaluateBudgetLineAmount);
            EconomyRulesHook.EvaluateCreditTerms(10f, 100f, 0.7f, 0.03f, 0f, 0f, false, 0.8f, 1f, 0.25f, out var ft, out var fn, out var fs);
            EconomyRulesHook.ManagedEvaluateCreditTerms(10f, 100f, 0.7f, 0.03f, 0f, 0f, false, 0.8f, 1f, 0.25f, out var mt, out var mn, out var ms);
            AssertBitsEqual(ft, mt, "FaultFallback.Target");
            AssertBitsEqual(fn, mn, "FaultFallback.Next");
            AssertBitsEqual(fs, ms, "FaultFallback.Spread");
            Check(!EconomyRulesHook.UsingFSharp, "faulting F# hook unhooks itself cleanly");
            DealOrForce.EconomyRulesFSharpBoot.Install();
            Check(EconomyRulesHook.UsingFSharp, "EconomyRulesFSharpBoot reinstalls cleanly after fault test");
        }
        finally
        {
            NativeKernelsLoader.SimulateUnavailableForTests = prevSim;
            DealOrForce.EconomyRulesFSharpBoot.Install();
        }
    }

    private static void RunRealWorldKernelSuitesAndBenchmarks(bool nativePresent)
    {
        var campaign = Campaign.CreateBootstrap(20260830);
        MapRasterCatalog.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var builtMap, out var mapMsg))
            throw new Exception("polyglot kernels: failed to build province topology: " + mapMsg);
        campaign.AttachProvinceTopology(builtMap);
        var map = campaign.ProvinceMap;
        Check(map is not null && map.ProvinceCount > 30_000, $"loaded real world topology ({map?.ProvinceCount ?? 0} provinces, {map?.Width}x{map?.Height})");
        if (map is null) return;

        // ---- 1. Whole-world 5760x2880 (16.6M pixel) FrontierField benchmark + bit-exact check ----
        var w = map.Width;
        var h = map.Height;
        var cells = w * h;
        var cellToProv = map.CellToProvince;
        var authoredOwners = (ushort[])map.ProvinceToNation.Clone();
        var alteredOwners = (ushort[])map.ProvinceToNation.Clone();
        for (var p = 1; p < alteredOwners.Length; p += 9)
            if (alteredOwners[p] != 0 && !map.IsWater(p)) alteredOwners[p] = (ushort)((alteredOwners[p] % 50) + 1);

        var authoredHolders = new ushort[cells];
        for (var i = 0; i < cells; i++)
        {
            var p = cellToProv[i];
            authoredHolders[i] = p > 0 && p < authoredOwners.Length && !map.IsWater(p) ? authoredOwners[p] : MapBorders.Sea;
        }

        // Compare C# vs Native on BOTH initial borders & wartime annexation changes (zero discrepancies allowed across 16.6M bytes).
        var csInitial = FrontierField.BuildManaged(authoredHolders, cellToProv, w, h, authoredOwners, authoredOwners);
        var csWar = FrontierField.BuildManaged(authoredHolders, cellToProv, w, h, alteredOwners, authoredOwners);
        byte[]? natColdWar = null;

        if (nativePresent)
        {
            var swCold = Stopwatch.StartNew();
            var okInit = NativeRasterBake.TryBuildFrontier(authoredHolders, cellToProv, w, h, authoredOwners, authoredOwners, FrontierField.Cap, FrontierField.Smoothing, out var natInit);
            swCold.Stop();
            var swWarm = Stopwatch.StartNew();
            var okWar = NativeRasterBake.TryBuildFrontier(authoredHolders, cellToProv, w, h, alteredOwners, authoredOwners, FrontierField.Cap, FrontierField.Smoothing, out natColdWar);
            swWarm.Stop();

            Check(okInit && natInit is not null && natInit.SequenceEqual(csInitial),
                $"5760x2880 FrontierField native == C# exact (initial, {cells:N0} cells, hash={HashBytes(csInitial):X16}, coldCreate+run={swCold.Elapsed.TotalMilliseconds:F1}ms)");
            Check(okWar && natColdWar is not null && natColdWar.SequenceEqual(csWar),
                $"5760x2880 FrontierField native == C# exact (wartime annexations, hash={HashBytes(csWar):X16}, warm={swWarm.Elapsed.TotalMilliseconds:F1}ms)");

            // Fair end-to-end interleaved timing (allocating final byte[] in both paths, plus pre-allocated output test)
            var csList = new List<double>();
            var natList = new List<double>();
            var sw = new Stopwatch();
            for (var r = 0; r < 9; r++)
            {
                if ((r & 1) == 0)
                {
                    sw.Restart(); _ = FrontierField.BuildManaged(authoredHolders, cellToProv, w, h, alteredOwners, authoredOwners); sw.Stop(); csList.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart(); NativeRasterBake.TryBuildFrontier(authoredHolders, cellToProv, w, h, alteredOwners, authoredOwners, FrontierField.Cap, FrontierField.Smoothing, out _); sw.Stop(); natList.Add(sw.Elapsed.TotalMilliseconds);
                }
                else
                {
                    sw.Restart(); NativeRasterBake.TryBuildFrontier(authoredHolders, cellToProv, w, h, alteredOwners, authoredOwners, FrontierField.Cap, FrontierField.Smoothing, out _); sw.Stop(); natList.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart(); _ = FrontierField.BuildManaged(authoredHolders, cellToProv, w, h, alteredOwners, authoredOwners); sw.Stop(); csList.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            csList.Sort(); natList.Sort();
            var csMed = Percentile(csList, 0.50);
            var natMed = Percentile(natList, 0.50);
            Console.WriteLine($"BENCH raster_frontier_5760x2880 cs_med={csMed:F2}ms cs_p95={Percentile(csList, 0.95):F2}ms native_med={natMed:F2}ms native_p95={Percentile(natList, 0.95):F2}ms speedup={csMed / Math.Max(0.01, natMed):F2}x");
        }

        // ---- 2. Real 34k-Province DistanceField & OwnerComponents benchmark + bit-exact check ----
        var sources = new List<int>();
        for (var p = 1; p <= map.ProvinceCount && sources.Count < 16; p += 1900)
            if (!map.IsWater(p)) sources.Add(p);

        var managedDist = LongRangeMovement.DistanceFieldManaged(campaign, map, sources, 1600f, out var managedWithin);
        if (nativePresent)
        {
            var swBuild = Stopwatch.StartNew();
            var nGraph = NativeProvinceGraph.ForMap(map);
            swBuild.Stop();
            Check(nGraph is not null, $"NativeProvinceGraph built for {map.ProvinceCount:N0} provinces in {swBuild.Elapsed.TotalMilliseconds:F1}ms");

            var okDf = nGraph!.TryDistanceField(sources, 1600f, out var nativeDist, out var nativeWithin);
            Check(okDf && nativeDist is not null && nativeWithin is not null &&
                  nativeWithin.Count == managedWithin.Count &&
                  nativeWithin.SequenceEqual(managedWithin.OrderBy(p => p)),
                $"DistanceField within exact match ({managedWithin.Count:N0} provinces)");
            foreach (var p in managedWithin)
                AssertBitsEqual(nativeDist![p], managedDist[p], $"DistanceField[{p}]");
            Console.WriteLine($"PASS polyglot kernels: DistanceField 34k float bits 100% identical across {managedWithin.Count:N0} reached provinces");

            var csDf = MeasureMs(5, 25, () => _ = LongRangeMovement.DistanceFieldManaged(campaign, map, sources, 1600f, out _));
            var natDf = MeasureMs(5, 25, () => _ = nGraph.TryDistanceField(sources, 1600f, out _, out _));
            Console.WriteLine($"BENCH distance_field_1600km reached={managedWithin.Count} cs_med={csDf.MedianMs:F3}ms cs_p95={csDf.P95Ms:F3}ms native_med={natDf.MedianMs:F3}ms native_p95={natDf.P95Ms:F3}ms speedup={csDf.MedianMs / Math.Max(0.001, natDf.MedianMs):F2}x");

            // OwnerComponents on real 34,138-province map
            var ocGraph = OwnershipComponentGraph.FromNeighbors(map.Neighbors);
            var expectedOc = ocGraph.ComputeManaged(map.ProvinceToNation, OwnershipComponents.PackWater(map));
            var natLabels = new int[map.ProvinceCount + 1];
            var natMembers = new int[map.ProvinceCount + 1];
            var natOffsets = new int[map.ProvinceCount + 2];
            var okOc = nGraph.TryComputeOwnerComponents(map.ProvinceToNation, natLabels, natMembers, natOffsets, out var cc, out var mc);
            Check(okOc && cc == expectedOc.ComponentCount && mc == expectedOc.MemberCount &&
                  natLabels.SequenceEqual(expectedOc.Labels) &&
                  natMembers.AsSpan(0, mc).SequenceEqual(expectedOc.MemberOrder.AsSpan(0, mc)) &&
                  natOffsets.AsSpan(0, cc + 1).SequenceEqual(expectedOc.ComponentOffsets.AsSpan(0, cc + 1)),
                $"OwnerComponents 34k bit-exact match (components={cc}, members={mc})");

            var water = OwnershipComponents.PackWater(map);
            var csOc = MeasureMs(5, 25, () => _ = ocGraph.ComputeManaged(map.ProvinceToNation, water));
            var natOc = MeasureMs(5, 25, () => _ = nGraph.TryComputeOwnerComponents(map.ProvinceToNation, natLabels, natMembers, natOffsets, out _, out _));
            Console.WriteLine($"BENCH owner_components_34k cs_med={csOc.MedianMs:F3}ms cs_p95={csOc.P95Ms:F3}ms native_med={natOc.MedianMs:F3}ms native_p95={natOc.P95Ms:F3}ms speedup={csOc.MedianMs / Math.Max(0.001, natOc.MedianMs):F2}x");

            // Supply BFS kernel (dof_graph_solve_supply_v1) parity & timing vs LogisticsSystem.Tick on real world
            LogisticsSystem.Tick(campaign);
            var controllers = campaign.ProvinceController ?? map.ProvinceToNation;
            var emptyBits = new ulong[Math.Max(1, (ushort.MaxValue + 63) / 64)];
            var barBits = new ulong[(ushort.MaxValue + 63) / 64];
            var landMap = new Dictionary<ushort, int>();
            for (var p = 1; p <= map.ProvinceCount; p++)
            {
                if (map.IsWater(p)) continue;
                var stId = map.ProvinceToState[p];
                if (stId > 0) landMap[stId] = landMap.GetValueOrDefault(stId) + 1;
            }
            var landIds = landMap.Keys.ToArray();
            var landCounts = landIds.Select(k => landMap[k]).ToArray();
            var checkedSupplyArmies = 0;
            var matchedSupplyArmies = 0;
            foreach (var stEntry in campaign.States)
                if (stEntry.MapIndex > 0 && stEntry.Buildings.GetValueOrDefault(BuildingKind.Barracks) > 0)
                    barBits[stEntry.MapIndex >> 6] |= 1UL << (stEntry.MapIndex & 63);

            var testNation = campaign.NationById(
                campaign.Armies.Where(a => a.EmbarkedFleetId < 0 && a.ProvinceId > 0)
                    .GroupBy(a => a.NationId)
                    .OrderByDescending(g => g.Count())
                    .First().Key);
            if (testNation is not null)
            {
                var terrList = new List<int>();
                var heldMap = new Dictionary<ushort, int>();
                for (var p = 1; p <= map.ProvinceCount; p++)
                {
                    if (controllers[p] != testNation.MapIndex) continue;
                    terrList.Add(p);
                    if (!map.IsWater(p) && map.ProvinceToState[p] > 0)
                        heldMap[map.ProvinceToState[p]] = heldMap.GetValueOrDefault(map.ProvinceToState[p]) + 1;
                }
                var terrArr = terrList.ToArray();
                var heldIds = heldMap.Keys.ToArray();
                var heldCounts = heldIds.Select(k => heldMap[k]).ToArray();
                var nArmies = campaign.Armies.Where(a => a.NationId == testNation.Id && a.EmbarkedFleetId < 0 && a.ProvinceId > 0).ToList();
                var armProvs = nArmies.Select(a => a.ProvinceId).ToArray();
                var roots = new[] { CapitalCore.SupplyOriginProvinceId(campaign, testNation) };
                var flagsOut = new byte[armProvs.Length];
                var distOut = new int[armProvs.Length];
                var srcOut = new int[armProvs.Length];
                var okSup = nGraph.TrySolveSupply(
                    controllers, map.ProvinceToNation, emptyBits, (ushort)testNation.MapIndex,
                    map.ProvinceToState, roots, terrArr, barBits, landIds, landCounts,
                    heldIds, heldCounts, false, campaign.Defines.ForwardDepotHeldShare,
                    armProvs, flagsOut, distOut, srcOut);
                Check(okSup, $"dof_graph_solve_supply_v1 succeeded for {testNation.Tag} ({armProvs.Length} armies)");
                for (var i = 0; i < nArmies.Count; i++)
                {
                    if ((flagsOut[i] & 2) != 0 && distOut[i] <= campaign.Defines.SupplyTraceRange)
                    {
                        checkedSupplyArmies++;
                        if (distOut[i] == nArmies[i].SupplyDistance && srcOut[i] == nArmies[i].SupplySourceStateMapIndex)
                            matchedSupplyArmies++;
                    }
                }
                Check(checkedSupplyArmies > 0 && matchedSupplyArmies == checkedSupplyArmies,
                    $"dof_graph_solve_supply_v1 matched LogisticsSystem distance & source on all {checkedSupplyArmies} land-traced divisions of {testNation.Tag}");
                var natSup = MeasureMs(5, 30, () => nGraph.TrySolveSupply(
                    controllers, map.ProvinceToNation, emptyBits, (ushort)testNation.MapIndex,
                    map.ProvinceToState, roots, terrArr, barBits, landIds, landCounts,
                    heldIds, heldCounts, false, campaign.Defines.ForwardDepotHeldShare,
                    armProvs, flagsOut, distOut, srcOut));
                var csSup = MeasureMs(2, 10, () => LogisticsSystem.Tick(campaign));
                Console.WriteLine($"BENCH supply_single_nation({testNation.Tag},armies={armProvs.Length})={natSup.MedianMs:F3}ms supply_all_nations_managed={csSup.MedianMs:F3}ms");
            }
        }

        // ---- 3. Trade Affinity N x N benchmark (200 x 200 dense market batch) ----
        const int tn = 200;
        var ids = new int[tn];
        var maps = new ushort[tn];
        var reach = new float[tn];
        var lats = new float[tn];
        var lons = new float[tn];
        var op = new float[tn * tn];
        var st = new byte[tn * tn];
        var fl = new byte[tn * tn];
        var dist = new float[tn * tn];
        var csOut = new float[tn * tn];
        var natOut = new float[tn * tn];
        for (var i = 0; i < tn; i++)
        {
            ids[i] = i; maps[i] = (ushort)(i + 1);
            reach[i] = 0.55f + (i % 9) * 0.04f;
            lats[i] = -50f + (i % 100);
            lons[i] = -170f + (i * 1.7f % 340f);
            for (var j = 0; j < tn; j++)
            {
                var k = i * tn + j;
                op[k] = -80f + ((i * 31 + j * 17) % 180);
                st[k] = (byte)((i + j) % 3);
                fl[k] = (byte)(((i ^ j) & 1) | (((i + j) % 19 == 0) ? 16 : 0) | ((i % 4 == 0) ? 8 : 0) | ((i % 8 == 0 && j % 8 == 0) ? 4 : 0));
                dist[k] = 500f + ((i * 97 + j * 53) % 12000);
            }
        }
        if (nativePresent)
        {
            var arena = NativeTradeArena.ForCampaign(campaign, 512);
            Check(arena is not null, "NativeTradeArena created");
            Check(arena!.TryComputeAffinity(ids, maps, reach, ids, maps, reach, dist, tn, tn, op, st, fl, tn, 2600f, 1.35f, 0.85f, natOut, tn),
                "NativeTradeArena.TryComputeAffinity succeeds");
            ManagedTradeAffinityRef(ids, reach, ids, reach, dist, tn, op, st, fl, 2600f, 1.35f, 0.85f, csOut);
            for (var k = 0; k < tn * tn; k++) AssertBitsEqual(natOut[k], csOut[k], $"TradeAffinity[{k}]");
            Console.WriteLine($"PASS polyglot kernels: TradeAffinity {tn}x{tn} float bits 100% identical ({tn * tn:N0} pairs)");

            var csTr = MeasureMs(5, 40, () => ManagedTradeAffinityRef(ids, reach, ids, reach, dist, tn, op, st, fl, 2600f, 1.35f, 0.85f, csOut));
            var natTr = MeasureMs(5, 40, () => arena.TryComputeAffinity(ids, maps, reach, ids, maps, reach, dist, tn, tn, op, st, fl, tn, 2600f, 1.35f, 0.85f, natOut, tn));
            Console.WriteLine($"BENCH trade_affinity_200x200 cs_med={csTr.MedianMs:F3}ms cs_p95={csTr.P95Ms:F3}ms native_med={natTr.MedianMs:F3}ms native_p95={natTr.P95Ms:F3}ms speedup={csTr.MedianMs / Math.Max(0.001, natTr.MedianMs):F2}x");

            // Also measure 360-nation GreatCircle table update + trig ULP parity check
            const int gn = 360;
            var gIds = new int[gn];
            var gLats = new float[gn];
            var gLons = new float[gn];
            for (var i = 0; i < gn; i++) { gIds[i] = i; gLats[i] = -60f + (i * 120f / gn); gLons[i] = -180f + (i * 360f / gn); }
            var csDistTbl = new float[gn * gn];
            var natDistTbl = new float[gn * gn];
            var dirty24 = Enumerable.Range(0, 24).ToArray();
            Check(arena.TryUpdateDistances(gIds, gLats, gLons, dirty24, true, gn, natDistTbl), "NativeTradeArena.TryUpdateDistances full rebuild succeeds");
            ManagedUpdateDistancesRef(gIds, gLats, gLons, gn, csDistTbl);
            var exactFloatMatches = 0;
            var maxAbsKmDiff = 0f;
            for (var k = 0; k < gn * gn; k++)
            {
                if (BitConverter.SingleToInt32Bits(natDistTbl[k]) == BitConverter.SingleToInt32Bits(csDistTbl[k])) exactFloatMatches++;
                maxAbsKmDiff = MathF.Max(maxAbsKmDiff, MathF.Abs(natDistTbl[k] - csDistTbl[k]));
            }
            Console.WriteLine($"PARITY trade_great_circle_360x360 exact_bit_matches={exactFloatMatches}/{gn * gn} max_abs_diff_km={maxAbsKmDiff:G6}");
            var csGc = MeasureMs(3, 15, () => ManagedUpdateDistancesRef(gIds, gLats, gLons, gn, csDistTbl));
            var natGc = MeasureMs(3, 15, () => arena.TryUpdateDistances(gIds, gLats, gLons, dirty24, true, gn, natDistTbl));
            Console.WriteLine($"BENCH trade_great_circle_360x360 cs_med={csGc.MedianMs:F3}ms native_med={natGc.MedianMs:F3}ms speedup={csGc.MedianMs / Math.Max(0.001, natGc.MedianMs):F2}x");
        }
    }

    private static void ManagedUpdateDistancesRef(int[] ids, float[] lats, float[] lons, int stride, float[] table)
    {
        const float rad = MathF.PI / 180f;
        for (var i = 0; i < ids.Length; i++)
        {
            var a = ids[i];
            var phiA = lats[i] * rad;
            var row = a * stride;
            for (var j = 0; j < ids.Length; j++)
            {
                var b = ids[j];
                if (b < a) continue;
                var phiB = lats[j] * rad;
                var dp = (lats[j] - lats[i]) * rad;
                var dl = (lons[j] - lons[i]) * rad;
                var sp = MathF.Sin(dp * 0.5f);
                var sl = MathF.Sin(dl * 0.5f);
                var hav = sp * sp + MathF.Cos(phiA) * MathF.Cos(phiB) * sl * sl;
                var km = 6371f * 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(MathF.Max(0f, hav))));
                table[row + b] = km;
                table[b * stride + a] = km;
            }
        }
    }

    private static void ManagedTradeAffinityRef(
        int[] bIds, float[] bReach, int[] sIds, float[] sReach, float[] dist, int n,
        float[] op, byte[] st, byte[] fl, float halfReachKm, float inside, float outside, float[] outScore)
    {
        for (var bi = 0; bi < bIds.Length; bi++)
        {
            var br = bReach[bi];
            var row = bi * n;
            for (var si = 0; si < sIds.Length; si++)
            {
                var idx = row + si;
                var flags = fl[idx];
                if ((flags & 2) != 0) { outScore[idx] = 0f; continue; }
                var score = 1f;
                if ((flags & 1) != 0)
                {
                    score *= Math.Clamp(1f + op[idx] / 160f, 0.35f, 1.65f);
                    if (st[idx] == 1) score *= 1.30f;
                    else if (st[idx] == 2) score *= 0.68f;
                }
                if ((flags & 8) != 0)
                    score *= (flags & 4) != 0 ? inside : outside;
                if ((flags & 16) == 0)
                {
                    var d = dist[bIds[bi] * n + sIds[si]];
                    var span = (d > 0f ? d : 20_000f) / halfReachKm;
                    var falloff = 1f / (1f + span * span);
                    var r = (br + sReach[si]) * 0.5f;
                    score *= falloff * r;
                }
                outScore[idx] = score;
            }
        }
    }
}
