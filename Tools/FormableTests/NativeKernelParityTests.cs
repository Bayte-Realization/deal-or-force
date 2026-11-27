// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using DealOrForce.Simulation.Kernels;

static class NativeKernelParityTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL native kernel parity: " + label);
        Console.WriteLine("PASS native kernel parity: " + label);
    }

    private static void CheckBitExact(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string label)
    {
        if (expected.Length != actual.Length)
            throw new Exception($"FAIL native kernel parity: {label} length {actual.Length} != {expected.Length}");
        for (int i = 0; i < expected.Length; i++)
        {
            int eb = BitConverter.SingleToInt32Bits(expected[i]);
            int ab = BitConverter.SingleToInt32Bits(actual[i]);
            if (eb != ab)
            {
                // Allow matching NaNs with identical payload or canonical NaN.
                if (float.IsNaN(expected[i]) && float.IsNaN(actual[i])) continue;
                throw new Exception(
                    $"FAIL native kernel parity: {label}[{i}] native={actual[i]:R} (0x{ab:X8}) != csharp={expected[i]:R} (0x{eb:X8})");
            }
        }
    }

    private static void CheckBitExactBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string label)
    {
        if (expected.Length != actual.Length)
            throw new Exception($"FAIL native kernel parity: {label} byte length {actual.Length} != {expected.Length}");
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                throw new Exception($"FAIL native kernel parity: {label}[{i}] {actual[i]} != {expected[i]}");
        }
    }

    [TestSuite("--native-kernel-parity-only", Order = 2700, IncludeInDefaultRun = true,
        Description = "Native dof_kernels diplomacy/market/distance SIMD parity, capabilities, zero-alloc hot path, and C# fallback.")]
    public static void Run()
    {
        var prevSim = NativeKernelsLoader.SimulateUnavailableForTests;
        NativeKernelsLoader.SimulateUnavailableForTests = false;
        try
        {
            var nativeAvailable = NativeKernelsLoader.IsAvailable;
            Console.WriteLine($"NATIVE_KERNELS available={nativeAvailable} abi={NativeKernelsLoader.AbiVersion} " +
                              $"simd={NativeKernelsLoader.SimdLevel} caps=0x{NativeKernelsLoader.Capabilities:X} " +
                              $"diag=\"{NativeKernelsLoader.Diagnostic}\"");

            var requireNative = string.Equals(Environment.GetEnvironmentVariable("DOF_REQUIRE_NATIVE_KERNELS"), "1", StringComparison.Ordinal);
            if (nativeAvailable)
            {
                Check(NativeKernelsLoader.AbiVersion == 1, "ABI version == 1");
                Check(NativeKernelsLoader.SimdLevel >= 1, $"SIMD level >= 1 (actual={NativeKernelsLoader.SimdLevel})");
                if (OperatingSystem.IsWindows() && Vector256.IsHardwareAccelerated)
                    Check(NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapAvx2), "AVX2 capability detected");
                const uint expectedCaps =
                    NativeKernelsLoader.CapAbiV1Base |
                    NativeKernelsLoader.CapDiplomacy |
                    NativeKernelsLoader.CapMarket |
                    NativeKernelsLoader.CapDistance |
                    NativeKernelsLoader.CapRenderPrep |
                    NativeKernelsLoader.CapEconomyDemo;
                Check(NativeKernelsLoader.HasCapability(expectedCaps),
                    "all v1 diplomacy/market/distance/render-prep/economy-demo capabilities reported");
            }
            else if (requireNative)
            {
                throw new Exception("DOF_REQUIRE_NATIVE_KERNELS=1 set, but native dof_kernels failed to load: " +
                                    NativeKernelsLoader.Diagnostic);
            }
            else
            {
                Console.WriteLine("NOTE: dof_kernels native library absent; running C# fallback + guard assertions only.");
            }

            VerifyDiplomacyPairParity(nativeAvailable);
            VerifyReasonDecayParity(nativeAvailable);
            VerifyPartnerEvalParity(nativeAvailable);
            VerifyMarketSettleParity(nativeAvailable);
            VerifyMarketClearNationsParity(nativeAvailable);
            VerifyDistanceParity(nativeAvailable);
            VerifyPackProvinceOverlaysParity(nativeAvailable);
            VerifyPoliticalPlatePaintParity(nativeAvailable);
            VerifyEconomyDemoParity(nativeAvailable);
            VerifyFallbackAndShapeGuards(nativeAvailable);
            if (nativeAvailable) VerifyZeroManagedAllocations();
        }
        finally
        {
            NativeKernelsLoader.SimulateUnavailableForTests = prevSim;
        }
    }

    private static void VerifyDiplomacyPairParity(bool nativeAvailable)
    {
        int[] lengths = { 0, 1, 3, 4, 7, 8, 9, 15, 16, 17, 257 };
        var rng = new Random(20261126);

        foreach (int n in lengths)
        {
            var leftEcon = new float[n];
            var leftAuth = new float[n];
            var rightEcon = new float[n];
            var rightAuth = new float[n];
            var esteemAb = new float[n];
            var esteemBa = new float[n];
            var claimLeft = new float[n];
            var claimRight = new float[n];
            var claimBonus = new float[n];
            var blocBias = new float[n];
            var baseSum = new float[n];
            var wa = new float[n];
            var wf = new float[n];
            var idAff = new float[n];
            var sr = new float[n];
            var fr = new float[n];
            var flags = new byte[n];

            for (int i = 0; i < n; i++)
            {
                leftEcon[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
                leftAuth[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
                rightEcon[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
                rightAuth[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
                esteemAb[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
                esteemBa[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
                claimLeft[i] = (i % 3 == 0) ? (float)rng.NextDouble() : 0f;
                claimRight[i] = (i % 5 == 0) ? (float)rng.NextDouble() : 0f;
                claimBonus[i] = (i % 2 == 0) ? (float)(rng.NextDouble() * 0.35) : 0f;
                blocBias[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
                baseSum[i] = (float)(rng.NextDouble() * 140.0 - 70.0);
                wa[i] = (float)(-rng.NextDouble() * 55.0);
                wf[i] = (float)(rng.NextDouble() * 45.0);
                idAff[i] = (i % 4 == 0) ? 0f : (float)(rng.NextDouble() * 14.0);
                sr[i] = (float)(0.2 + rng.NextDouble() * 3.5);
                fr[i] = (i % 2 == 0) ? (float)(rng.NextDouble() * 1.2) : 0f;
                flags[i] = (byte)((i % 2) | ((i % 3 == 0 ? 1 : 0) << 1) | ((i % 5 != 0 ? 1 : 0) << 2));
            }

            var csPol = new float[n]; var csSoc = new float[n]; var csClm = new float[n];
            var csGeo = new float[n]; var csOp = new float[n]; var csFr = new float[n];
            NativeDiplomacyKernels.ManagedScorePairs(
                leftEcon, leftAuth, rightEcon, rightAuth, esteemAb, esteemBa,
                claimLeft, claimRight, claimBonus, blocBias, baseSum,
                wa, wf, idAff, sr, fr, flags,
                18.5f, 8.0f, 0.72f, -25.0f, 15.0f,
                csPol, csSoc, csClm, csGeo, csOp, csFr);

            if (nativeAvailable)
            {
                var nvPol = new float[n]; var nvSoc = new float[n]; var nvClm = new float[n];
                var nvGeo = new float[n]; var nvOp = new float[n]; var nvFr = new float[n];
                Check(NativeDiplomacyKernels.TryNativeScorePairs(
                    leftEcon, leftAuth, rightEcon, rightAuth, esteemAb, esteemBa,
                    claimLeft, claimRight, claimBonus, blocBias, baseSum,
                    wa, wf, idAff, sr, fr, flags,
                    18.5f, 8.0f, 0.72f, -25.0f, 15.0f,
                    nvPol, nvSoc, nvClm, nvGeo, nvOp, nvFr), $"TryNativeScorePairs n={n}");

                CheckBitExact(csPol, nvPol, $"dip_pair.politics n={n}");
                CheckBitExact(csSoc, nvSoc, $"dip_pair.social n={n}");
                CheckBitExact(csClm, nvClm, $"dip_pair.claims n={n}");
                CheckBitExact(csGeo, nvGeo, $"dip_pair.geopolitics n={n}");
                CheckBitExact(csOp, nvOp, $"dip_pair.opinion n={n}");
                CheckBitExact(csFr, nvFr, $"dip_pair.fear n={n}");
            }
        }
        Console.WriteLine("PASS native kernel parity: diplomacy pair score batch bit-exact across lengths 0..257");
    }

    private static void VerifyReasonDecayParity(bool nativeAvailable)
    {
        const int n = 131;
        var rng = new Random(9911);
        var baseOp = new float[n];
        var stacks = new int[n];
        var scale = new float[n];
        var granted = new int[n];
        var expires = new int[n];
        var decayDays = new int[n];
        var flags = new byte[n];

        for (int i = 0; i < n; i++)
        {
            baseOp[i] = i % 11 == 0 ? 0f : (float)(rng.NextDouble() * 40.0 - 20.0);
            stacks[i] = i % 4 == 0 ? 3 : 1;
            scale[i] = i % 13 == 0 ? -1f : (float)(0.25 + rng.NextDouble() * 1.5);
            granted[i] = 100 + i;
            expires[i] = i % 5 == 0 ? 0 : (150 + i * 3);
            decayDays[i] = i % 7 == 0 ? 0 : 90;
            flags[i] = (byte)(i & 1);
        }

        const int today = 260;
        var csW = new float[n];
        var csP = new byte[n];
        NativeDiplomacyKernels.ManagedEvalReasonDecay(baseOp, stacks, scale, granted, expires, decayDays, flags, today, csW, csP);

        if (nativeAvailable)
        {
            var nvW = new float[n];
            var nvP = new byte[n];
            Check(NativeDiplomacyKernels.TryNativeEvalReasonDecay(baseOp, stacks, scale, granted, expires, decayDays, flags, today, nvW, nvP),
                "TryNativeEvalReasonDecay");
            CheckBitExact(csW, nvW, "reason_decay.weight");
            CheckBitExactBytes(csP, nvP, "reason_decay.prune");
        }
        Console.WriteLine("PASS native kernel parity: diplomacy reason decay bit-exact");
    }

    private static void VerifyPartnerEvalParity(bool nativeAvailable)
    {
        const int n = 89;
        var rng = new Random(4422);
        var opinion = new float[n];
        var stance = new byte[n];
        var blocBias = new float[n];
        var tradeDep = new float[n];
        var flags = new byte[n];

        for (int i = 0; i < n; i++)
        {
            opinion[i] = (float)(rng.NextDouble() * 200.0 - 100.0);
            stance[i] = (byte)(i % 5); // covers 0..4 including STANCE_AT_WAR == 4
            blocBias[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            tradeDep[i] = (float)(rng.NextDouble() * 0.15);
            flags[i] = (byte)(i & 7);
        }

        foreach (int era in new[] { 0, 1, 2 })
        {
            var csMask = new byte[n];
            var csTh = new float[n];
            NativeDiplomacyKernels.ManagedEvalPartners(
                opinion, stance, blocBias, tradeDep, flags,
                1.15f, 15f, 35f, 60f, era, 30f, -35f, -5f, era != 2,
                csMask, csTh, out int csBest, out int csWorst);

            if (nativeAvailable)
            {
                var nvMask = new byte[n];
                var nvTh = new float[n];
                Check(NativeDiplomacyKernels.TryNativeEvalPartners(
                    opinion, stance, blocBias, tradeDep, flags,
                    1.15f, 15f, 35f, 60f, era, 30f, -35f, -5f, era != 2,
                    nvMask, nvTh, out int nvBest, out int nvWorst), $"TryNativeEvalPartners era={era}");
                CheckBitExactBytes(csMask, nvMask, $"partner_eval.mask era={era}");
                CheckBitExact(csTh, nvTh, $"partner_eval.trade_th era={era}");
                Check(csBest == nvBest && csWorst == nvWorst, $"partner_eval best/worst era={era}");
            }
        }
        Console.WriteLine("PASS native kernel parity: diplomacy partner eval bit-exact across all eras");
    }

    private static void VerifyMarketSettleParity(bool nativeAvailable)
    {
        const int goods = 14;
        const int nations = 19; // intentionally not a multiple of 8/4 to hit SIMD + tail!
        const int stride = 24;
        int total = goods * stride;
        var rng = new Random(7788);

        var basePrice = new float[goods];
        var tradedWorld = new byte[goods];
        for (int g = 0; g < goods; g++)
        {
            basePrice[g] = 5f + g * 2.5f;
            tradedWorld[g] = (byte)(g % 3 == 0 ? 1 : 0);
        }

        var demand = new float[total];
        var prod = new float[total];
        var imp = new float[total];
        var rel = new float[total];
        var priceInit = new float[total];
        for (int i = 0; i < total; i++)
        {
            demand[i] = i % 17 == 0 ? 0f : (float)(1.0 + rng.NextDouble() * 500.0);
            prod[i] = (float)(rng.NextDouble() * 480.0);
            imp[i] = i % 4 == 0 ? (float)(rng.NextDouble() * 80.0) : 0f;
            rel[i] = imp[i] * (float)rng.NextDouble();
            priceInit[i] = (float)(3.0 + rng.NextDouble() * 35.0);
        }

        foreach (bool adjust in new[] { false, true })
        {
            var csSat = new float[total];
            var csPr = (float[])priceInit.Clone();
            var csWs = new float[goods];
            NativeMarketKernels.ManagedSettlePrices(
                goods, nations, stride, basePrice, tradedWorld,
                demand, prod, imp, rel, adjust, 1.25f, 0.45f, 0.35f,
                csSat, csPr, csWs);

            if (nativeAvailable)
            {
                var nvSat = new float[total];
                var nvPr = (float[])priceInit.Clone();
                var nvWs = new float[goods];
                Check(NativeMarketKernels.TryNativeSettlePrices(
                    goods, nations, stride, basePrice, tradedWorld,
                    demand, prod, imp, rel, adjust, 1.25f, 0.45f, 0.35f,
                    nvSat, nvPr, nvWs), $"TryNativeSettlePrices adjust={adjust}");
                CheckBitExact(csWs, nvWs, $"market.world_scarcity adjust={adjust}");
                CheckBitExact(csSat, nvSat, $"market.satisfaction adjust={adjust}");
                CheckBitExact(csPr, nvPr, $"market.price adjust={adjust}");
            }
        }
        Console.WriteLine("PASS native kernel parity: market settle prices bit-exact (adjust=false/true, strided)");
    }

    private static void VerifyMarketClearNationsParity(bool nativeAvailable)
    {
        const int nations = 37;
        const int goods = 16;
        const int stride = 20;
        int total = nations * stride;
        var rng = new Random(5533);

        var basePrice = new float[goods];
        var essWeight = new float[goods];
        for (int g = 0; g < goods; g++)
        {
            basePrice[g] = 4f + g * 1.8f;
            essWeight[g] = g < 5 ? 1.5f : 0.4f;
        }
        var tariff = new float[nations];
        for (int ni = 0; ni < nations; ni++) tariff[ni] = (float)(rng.NextDouble() * 0.25);

        var prod = new float[total];
        var dem = new float[total];
        var imp = new float[total];
        var exp = new float[total];
        var rel = new float[total];
        var sat = new float[total];
        var pr = new float[total];
        for (int i = 0; i < total; i++)
        {
            prod[i] = (float)(rng.NextDouble() * 300.0);
            dem[i] = (float)(rng.NextDouble() * 320.0);
            imp[i] = (float)(rng.NextDouble() * 40.0);
            exp[i] = (float)(rng.NextDouble() * 35.0);
            rel[i] = imp[i] * 0.3f;
            sat[i] = (float)rng.NextDouble();
            pr[i] = (float)(2.0 + rng.NextDouble() * 30.0);
        }

        var csGo = new float[nations]; var csRo = new float[nations]; var csSi = new float[nations];
        var csMa = new float[nations]; var csTb = new float[nations]; var csTi = new float[nations];
        NativeMarketKernels.ManagedClearNations(
            nations, goods, stride, basePrice, essWeight, tariff,
            prod, dem, imp, exp, rel, sat, pr, 0.25f, 0.4f, 0.5f,
            csGo, csRo, csSi, csMa, csTb, csTi);

        if (nativeAvailable)
        {
            var nvGo = new float[nations]; var nvRo = new float[nations]; var nvSi = new float[nations];
            var nvMa = new float[nations]; var nvTb = new float[nations]; var nvTi = new float[nations];
            Check(NativeMarketKernels.TryNativeClearNations(
                nations, goods, stride, basePrice, essWeight, tariff,
                prod, dem, imp, exp, rel, sat, pr, 0.25f, 0.4f, 0.5f,
                nvGo, nvRo, nvSi, nvMa, nvTb, nvTi), "TryNativeClearNations");
            CheckBitExact(csGo, nvGo, "clear_nations.gross_output");
            CheckBitExact(csRo, nvRo, "clear_nations.real_output");
            CheckBitExact(csSi, nvSi, "clear_nations.shortage_index");
            CheckBitExact(csMa, nvMa, "clear_nations.market_access");
            CheckBitExact(csTb, nvTb, "clear_nations.trade_balance");
            CheckBitExact(csTi, nvTi, "clear_nations.tariff_income");
        }
        Console.WriteLine("PASS native kernel parity: market clear nations bit-exact");
    }

    private static void VerifyDistanceParity(bool nativeAvailable)
    {
        const int n = 259;
        var rng = new Random(12345);
        var lats = new float[n];
        var lons = new float[n];
        for (int i = 0; i < n; i++)
        {
            lats[i] = (float)(rng.NextDouble() * 140.0 - 70.0);
            lons[i] = (float)(rng.NextDouble() * 360.0 - 180.0);
        }
        // Inject 3 guaranteed nearby points well inside reach, 1 well outside.
        lats[0] = 55.75f; lons[0] = 37.61f;
        lats[5] = 59.93f; lons[5] = 30.33f;
        lats[99] = 56.83f; lons[99] = 60.60f;

        var csKm = new float[n];
        NativeDistanceKernels.ManagedHaversineBatch(55.75f, 37.61f, lats, lons, csKm);
        Check(MathF.Abs(csKm[0]) < 1e-4f, "self distance == 0");

        if (nativeAvailable)
        {
            var nvKm = new float[n];
            Check(NativeDistanceKernels.TryNativeHaversineBatch(55.75f, 37.61f, lats, lons, nvKm),
                "TryNativeHaversineBatch");
            float maxDiff = 0f;
            for (int i = 0; i < n; i++)
            {
                float d = MathF.Abs(csKm[i] - nvKm[i]);
                if (d > maxDiff) maxDiff = d;
            }
            Check(maxDiff <= 0.01f, $"haversine max diff <= 0.01 km (actual={maxDiff:F6})");

            var csIdx = new int[n];
            var csRangeKm = new float[n];
            int csCnt = NativeDistanceKernels.ManagedWithinRangeBatch(55.75f, 37.61f, lats, lons, 1600f, csIdx, csRangeKm);

            var nvIdx = new int[n];
            var nvRangeKm = new float[n];
            Check(NativeDistanceKernels.TryNativeWithinRangeBatch(55.75f, 37.61f, lats, lons, 1600f, nvIdx, nvRangeKm, out int nvCnt),
                "TryNativeWithinRangeBatch");
            Check(csCnt == nvCnt && csCnt >= 3, $"within range count match (cs={csCnt}, nv={nvCnt})");
            for (int k = 0; k < csCnt; k++)
            {
                Check(csIdx[k] == nvIdx[k], $"within_range idx[{k}] match");
                Check(MathF.Abs(csRangeKm[k] - nvRangeKm[k]) <= 0.01f, $"within_range km[{k}] parity");
            }

            const int m = 29;
            const int stride = 32;
            var csMat = new float[m * stride];
            var nvMat = new float[m * stride];
            NativeDistanceKernels.ManagedPairwiseMatrix(lats.AsSpan(0, m), lons.AsSpan(0, m), stride, csMat);
            Check(NativeDistanceKernels.TryNativePairwiseMatrix(lats.AsSpan(0, m), lons.AsSpan(0, m), stride, nvMat),
                "TryNativePairwiseMatrix");
            float matMaxDiff = 0f;
            for (int r = 0; r < m; r++)
            {
                if (nvMat[r * stride + r] != 0f)
                    throw new Exception($"FAIL native kernel parity: matrix diagonal[{r}]={nvMat[r * stride + r]} != 0");
                for (int c = 0; c < m; c++)
                {
                    float d = MathF.Abs(csMat[r * stride + c] - nvMat[r * stride + c]);
                    if (d > matMaxDiff) matMaxDiff = d;
                    if (nvMat[r * stride + c] != nvMat[c * stride + r])
                        throw new Exception($"FAIL native kernel parity: matrix[{r},{c}] != matrix[{c},{r}]");
                }
            }
            Check(matMaxDiff <= 0.01f, $"pairwise matrix diagonal=0, symmetric & parity max diff <= 0.01 km (actual={matMaxDiff:F6})");
        }
        Console.WriteLine("PASS native kernel parity: distance batch, range prefilter & pairwise matrix verified");
    }

    private static void VerifyFallbackAndShapeGuards(bool nativeAvailable)
    {
        float[] a = { 0.1f, -0.2f, 0.3f, -0.4f };
        byte[] bf = { 1, 0, 2, 7 };
        var z = new float[4];

        // Mismatched shape returns false without throwing in TryNative*
        Check(!NativeDiplomacyKernels.TryNativeScorePairs(
            a, a, a, a, a, a, a, a, a, a, a, a, a, a, a, a, bf.AsSpan(0, 2),
            18f, 8f, 0.5f, -25f, 15f, z, z, z, z, z, z), "mismatched flag len rejected");
        Check(!NativeMarketKernels.TryNativeSettlePrices(
            1, 4, 2, a, bf, a, a, a, a, false, 1.2f, 0.5f, 0.5f, z, z), "stride < nationCount rejected");
        Check(!NativeDistanceKernels.TryNativeWithinRangeBatch(
            0f, 0f, a, a, -1f, new int[4], z, out _), "negative reachKm rejected");

        if (nativeAvailable)
        {
            NativeKernelsLoader.SimulateUnavailableForTests = true;
            try
            {
                Check(!NativeKernelsLoader.IsAvailable, "SimulateUnavailable disables IsAvailable");
                Check(!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapDiplomacy), "HasCapability false under simulation");
                var op = new float[4];
                bool usedNative = NativeDiplomacyKernels.ScorePairs(
                    a, a, a, a, a, a, a, a, a, a, a, a, a, a, a, a, bf,
                    18f, 8f, 0.5f, -25f, 15f, new float[4], new float[4], new float[4], new float[4], op, new float[4]);
                Check(!usedNative, "ScorePairs falls back to C# under simulation");
                var outKm = new float[4];
                Check(!NativeDistanceKernels.HaversineBatch(10f, 20f, a, a, outKm), "HaversineBatch falls back to C# under simulation");
            }
            finally
            {
                NativeKernelsLoader.SimulateUnavailableForTests = false;
            }
        }
        Console.WriteLine("PASS native kernel parity: shape guards and pure-C# fallback verified");
    }

    private static void VerifyZeroManagedAllocations()
    {
        const int n = 128;
        var f = new float[n];
        var b = new byte[n];
        var o1 = new float[n]; var o2 = new float[n]; var o3 = new float[n];
        var o4 = new float[n]; var o5 = new float[n]; var o6 = new float[n];
        for (int i = 0; i < n; i++) f[i] = 0.25f;

        NativeDiplomacyKernels.TryNativeScorePairs(
            f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, b,
            18f, 8f, 0.5f, -25f, 15f, o1, o2, o3, o4, o5, o6);
        NativeDistanceKernels.TryNativeHaversineBatch(55f, 37f, f, f, o1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iter = 0; iter < 50; iter++)
        {
            NativeDiplomacyKernels.TryNativeScorePairs(
                f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, b,
                18f, 8f, 0.5f, -25f, 15f, o1, o2, o3, o4, o5, o6);
            NativeDistanceKernels.TryNativeHaversineBatch(55f, 37f, f, f, o1);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Check(after - before == 0, $"native hot calls allocate 0 bytes (delta={after - before})");
    }

    private static void VerifyPackProvinceOverlaysParity(bool nativeAvailable)
    {
        const int provCount = 500;
        const int bufBytes = 256 * 256 * 4;
        var rng = new Random(8801);
        var owners = new ushort[provCount + 1];
        var painted = new ushort[provCount + 1];
        var controllers = new ushort[provCount + 1];
        var palette = new uint[65_536];
        var colorDict = new System.Collections.Generic.Dictionary<int, uint>();
        for (int n = 1; n <= 40; n++)
        {
            uint rgba = ((uint)((n * 61) & 0xFF) << 24) |
                        ((uint)((n * 113) & 0xFF) << 16) |
                        ((uint)((n * 173) & 0xFF) << 8) | 0xFFu;
            colorDict[n] = rgba;
            palette[n] = rgba;
        }
        for (int p = 1; p <= provCount; p++)
        {
            owners[p] = (p % 13 == 0) ? (ushort)0 : (ushort)(1 + (p % 45)); // includes 0 and unmapped 41..45
            painted[p] = (p % 7 == 0) ? (ushort)(1 + ((p + 3) % 45)) : owners[p];
            controllers[p] = (p % 11 == 0) ? (ushort)(1 + ((p + 9) % 45)) : owners[p];
        }

        var csCtl = new byte[bufBytes]; var csOwn = new byte[bufBytes]; var csOid = new byte[bufBytes];
        var nvCtl = new byte[bufBytes]; var nvOwn = new byte[bufBytes]; var nvOid = new byte[bufBytes];

        var csMoved = DealOrForce.Simulation.RasterOverlayKernel.PackProvinceOverlaysManaged(
            provCount, owners, painted, controllers, palette, 180, 65535, 0x6E7B8BFFu, csCtl, csOwn, csOid);
        if (nativeAvailable)
        {
            Check(NativeRasterBake.TryPackProvinceOverlays(
                provCount, owners, painted, controllers, palette, 180, 65535, 0x6E7B8BFFu, nvCtl, nvOwn, nvOid, out bool nvMoved),
                "TryPackProvinceOverlays full");
            Check(csMoved == nvMoved, "pack_overlays ownersMoved match");
            CheckBitExactBytes(csCtl, nvCtl, "pack_overlays.control");
            CheckBitExactBytes(csOwn, nvOwn, "pack_overlays.owner");
            CheckBitExactBytes(csOid, nvOid, "pack_overlays.owner_ids");

            // Nullable & shorter paintedOwners cases
            var shortPainted = new ushort[120];
            Array.Copy(painted, shortPainted, shortPainted.Length);
            foreach (var testPainted in new ushort[]?[] { null, shortPainted })
            {
                var cMoved = DealOrForce.Simulation.RasterOverlayKernel.PackProvinceOverlaysManaged(
                    provCount, owners, testPainted, controllers, palette, 180, 65535, 0x6E7B8BFFu, csCtl, csOwn, csOid);
                Check(NativeRasterBake.TryPackProvinceOverlays(
                    provCount, owners, testPainted, controllers, palette, 180, 65535, 0x6E7B8BFFu, nvCtl, nvOwn, nvOid, out var nMoved),
                    "TryPackProvinceOverlays partial/null paintedOwners");
                Check(cMoved == nMoved, "pack_overlays partial/null ownersMoved");
                CheckBitExactBytes(csCtl, nvCtl, "pack_overlays.partial.control");
                CheckBitExactBytes(csOwn, nvOwn, "pack_overlays.partial.owner");
                CheckBitExactBytes(csOid, nvOid, "pack_overlays.partial.owner_ids");
            }

            // End-to-end RasterOverlayKernel wrapper call
            var kCtl = new byte[bufBytes]; var kOwn = new byte[bufBytes]; var kOid = new byte[bufBytes];
            bool kMoved = DealOrForce.Simulation.RasterOverlayKernel.PackProvinceOverlays(
                provCount, owners, painted, controllers, colorDict, 180, 65535, 0x6E7B8BFFu, kCtl, kOwn, kOid);
            Check(kMoved == csMoved && DealOrForce.Simulation.RasterOverlayKernel.BackendStatus == "rust-dof-kernels-v1",
                $"RasterOverlayKernel backend={DealOrForce.Simulation.RasterOverlayKernel.BackendStatus}");
            CheckBitExactBytes(csCtl, kCtl, "RasterOverlayKernel.control");
        }
        Console.WriteLine("PASS native kernel parity: raster pack_overlays bit-exact (nullable/partial/full)");
    }

    private static void VerifyPoliticalPlatePaintParity(bool nativeAvailable)
    {
        var colours = new System.Collections.Generic.Dictionary<int, uint>
        {
            [1] = 0xC83232FFu,
            [2] = 0x4080C0FFu,
            [3] = 0x32C864FFu,
            [4] = 0xD4B038FFu
        };
        var palette = new uint[65_536];
        Array.Fill(palette, DealOrForce.Simulation.MapBorders.UnownedColour);
        foreach (var (k, v) in colours) palette[k] = v;
        var shades = new DealOrForce.Simulation.MapPlateShades(0.34f, 0.70f, 0.92f);

        // Case 1: width = 1 + Sea=0 + Unowned=65535 + Coast/RegionLine
        var srcNarrow = new DealOrForce.Simulation.PoliticalPlateSource
        {
            Width = 1,
            Height = 4,
            Holders = new ushort[] { 0, 2, 2, 1 },
            Provinces = new ushort[] { 0, 1, 2, 3 },
            Geography = new byte[] { 0, 0, DealOrForce.Simulation.PoliticalPlate.RegionLine, DealOrForce.Simulation.PoliticalPlate.Coast }
        };
        var owNarrow = new ushort[] { 0, 0, 2 }; // shorter than authored (3) -> prov 1 -> 65535
        var auNarrow = new ushort[] { 0, 2, 2, 1 };
        var csNarrow = DealOrForce.Simulation.PoliticalPlate.PaintManaged(srcNarrow, owNarrow, auNarrow, colours, shades);
        if (nativeAvailable)
        {
            foreach (uint th in new uint[] { 1u, 0u })
            {
                var nvNarrow = new byte[1 * 4 * 4];
                Check(NativeRasterBake.TryPaintPoliticalPlate(
                    1, 4, srcNarrow.Holders, srcNarrow.Provinces, srcNarrow.Geography,
                    owNarrow, auNarrow, palette, shades.Country, shades.Region, shades.Inland, nvNarrow, th),
                    $"TryPaintPoliticalPlate width=1 threads={th}");
                CheckBitExactBytes(csNarrow, nvNarrow, $"paint_plate.width1.t{th}");
            }
        }

        // Case 2: large plate (256x160 = 40,960 cells >= 32,768) exercising multi-thread scope, wrap-around borders, partial/empty arrays
        const int w = 256;
        const int h = 160;
        const int cells = w * h;
        var holders = new ushort[cells];
        var provs = new ushort[cells];
        var geo = new byte[cells];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int c = y * w + x;
                if (y == 0 || y == h - 1 || (x == 128 && y < 60))
                {
                    holders[c] = 0;
                    provs[c] = 0;
                }
                else if (x >= w - 3)
                {
                    holders[c] = 3; // touches x=0 across antimeridian wrap!
                    provs[c] = 3;
                }
                else
                {
                    holders[c] = (ushort)(1 + ((x / 32 + y / 32) % 3));
                    provs[c] = (ushort)(1 + (c % 9));
                    if ((c % 19) == 0) geo[c] |= DealOrForce.Simulation.PoliticalPlate.Coast;
                    if ((c % 7) == 0) geo[c] |= DealOrForce.Simulation.PoliticalPlate.RegionLine;
                }
            }
        }
        var srcWide = new DealOrForce.Simulation.PoliticalPlateSource
        {
            Width = w,
            Height = h,
            Holders = holders,
            Provinces = provs,
            Geography = geo
        };
        var owFull = new ushort[] { 0, 1, 0, 4, 2, 5, 1, 3, 2, 4 }; // 5 has no colour entry -> fallback grey
        var auFull = new ushort[] { 0, 1, 2, 3, 2, 1, 1, 3 }; // owners_len != authored_len!
        var emptyArr = Array.Empty<ushort>();

        foreach (var (ow, au, tag) in new[] { (owFull, auFull, "mixed"), (emptyArr, auFull, "empty_owners"), (owFull, emptyArr, "empty_authored") })
        {
            var csBytes = DealOrForce.Simulation.PoliticalPlate.PaintManaged(srcWide, ow, au, colours, shades);
            var endToEnd = DealOrForce.Simulation.PoliticalPlate.Paint(srcWide, ow, au, colours, shades);
            CheckBitExactBytes(csBytes, endToEnd, $"PoliticalPlate.Paint.{tag}");
            if (nativeAvailable)
            {
                foreach (uint th in new uint[] { 1u, 0u })
                {
                    var nvBytes = new byte[cells * 4];
                    Check(NativeRasterBake.TryPaintPoliticalPlate(
                        w, h, holders, provs, geo, ow, au, palette,
                        shades.Country, shades.Region, shades.Inland, nvBytes, th),
                        $"TryPaintPoliticalPlate {tag} t={th}");
                    CheckBitExactBytes(csBytes, nvBytes, $"paint_plate.{tag}.t{th}");
                }
            }
        }
        Console.WriteLine("PASS native kernel parity: political_plate paint bit-exact (width=1, wrap, unowned, empty/partial, MT)");
    }

    private static void VerifyEconomyDemoParity(bool nativeAvailable)
    {
        int[] lengths = { 0, 1, 3, 4, 7, 8, 9, 15, 16, 17, 65 };
        var rng = new Random(20261127);

        // 1. StepCohortsBatch parity vs DemographyRulesHook.ManagedStepCohorts
        foreach (int n in lengths)
        {
            var pop = new float[n];
            var rb = new long[n];
            var rd = new long[n];
            var ys = new float[n];
            var os = new float[n];
            for (int i = 0; i < n; i++)
            {
                pop[i] = i == 0 ? 0f : 25_000f + (float)rng.NextDouble() * 8_000_000f;
                rb[i] = rng.Next(0, 60_000);
                rd[i] = i % 7 == 0 ? 500_000L : rng.Next(0, 50_000);
                ys[i] = 0.08f + (float)rng.NextDouble() * 0.45f;
                os[i] = 0.03f + (float)rng.NextDouble() * 0.30f;
            }
            var csY = new float[n]; var csO = new float[n]; var csW = new float[n];
            var nvY = new float[n]; var nvO = new float[n]; var nvW = new float[n];
            NativeEconomyDemoKernels.ManagedStepCohortsBatch(
                pop, rb, rd, ys, os, 0.055f, 0.018f, 1.4f, 7f / 365f, csY, csO, csW);
            for (int i = 0; i < n; i++)
            {
                DealOrForce.Simulation.DemographyRulesHook.ManagedStepCohorts(
                    pop[i], rb[i], rd[i], ys[i], os[i], 0.055f, 0.018f, 1.4f, 7f / 365f,
                    out float hy, out float ho, out float hw);
                Check(BitConverter.SingleToInt32Bits(hy) == BitConverter.SingleToInt32Bits(csY[i]), $"hook step y n={n}[{i}]");
                Check(BitConverter.SingleToInt32Bits(ho) == BitConverter.SingleToInt32Bits(csO[i]), $"hook step o n={n}[{i}]");
                Check(BitConverter.SingleToInt32Bits(hw) == BitConverter.SingleToInt32Bits(csW[i]), $"hook step w n={n}[{i}]");
            }
            if (nativeAvailable)
            {
                Check(NativeEconomyDemoKernels.TryStepCohortsBatch(
                    pop, rb, rd, ys, os, 0.055f, 0.018f, 1.4f, 7f / 365f, nvY, nvO, nvW),
                    $"TryStepCohortsBatch n={n}");
                CheckBitExact(csY, nvY, $"step_cohorts.y.n{n}");
                CheckBitExact(csO, nvO, $"step_cohorts.o.n{n}");
                CheckBitExact(csW, nvW, $"step_cohorts.w.n{n}");
            }
        }

        // 2. AggregateNations parity (incl. empty-state nation, negative pop, and huge pop long*float promotion)
        {
            const int nc = 5;
            uint[] offsets = { 0u, 0u, 2u, 3u, 7u, 9u };
            const int sc = 9;
            long[] stPop = { 1_200_000L, -10L, 3_333_333_333L, 850_000L, 0L, 4_120_500L, 910_000L, 2_500_000L, 750_000L };
            float[] stY = { 0.28f, 0.40f, 0.19f, 0.35f, 0.30f, 0.22f, 0.31f, 0.25f, 0.38f };
            float[] stO = { 0.11f, 0.05f, 0.21f, 0.08f, 0.09f, 0.14f, 0.07f, 0.12f, 0.06f };
            long[] stB = { 420L, 0L, 8_100L, 310L, 0L, 980L, 250L, 640L, 305L };
            long[] stD = { 310L, 12L, 9_200L, 180L, 0L, 710L, 190L, 520L, 140L };
            long[] stM = { 35L, -4L, -600L, 88L, 0L, 145L, -22L, 0L, 91L };

            var csY = new float[nc]; var csO = new float[nc]; var csW = new float[nc]; var csEx = new float[nc];
            var csB = new long[nc]; var csD = new long[nc]; var csM = new long[nc];
            NativeEconomyDemoKernels.ManagedAggregateNations(
                nc, offsets, stPop, stY, stO, stB, stD, stM, sc,
                0.30f, 0.09f, 0.61f, 0.10f, csY, csO, csW, csEx, csB, csD, csM);
            if (nativeAvailable)
            {
                var nvY = new float[nc]; var nvO = new float[nc]; var nvW = new float[nc]; var nvEx = new float[nc];
                var nvB = new long[nc]; var nvD = new long[nc]; var nvM = new long[nc];
                Check(NativeEconomyDemoKernels.TryAggregateNations(
                    nc, offsets, stPop, stY, stO, stB, stD, stM, sc,
                    0.30f, 0.09f, 0.61f, 0.10f, nvY, nvO, nvW, nvEx, nvB, nvD, nvM),
                    "TryAggregateNations");
                CheckBitExact(csY, nvY, "agg.young");
                CheckBitExact(csO, nvO, "agg.old");
                CheckBitExact(csW, nvW, "agg.working");
                CheckBitExact(csEx, nvEx, "agg.old_excess");
                for (int n = 0; n < nc; n++)
                {
                    Check(csB[n] == nvB[n] && csD[n] == nvD[n] && csM[n] == nvM[n], $"agg.flows[{n}]");
                }
                uint[] badNonMono = { 0u, 3u, 2u, 7u, 9u, 9u };
                Check(!NativeEconomyDemoKernels.TryAggregateNations(
                    nc, badNonMono, stPop, stY, stO, stB, stD, stM, sc,
                    0.30f, 0.09f, 0.61f, 0.10f, nvY, nvO, nvW, nvEx, nvB, nvD, nvM),
                    "non-monotonic state_offsets rejected");
            }
        }

        // 3. HouseholdGroups parity across lengths & needEmployment modes + zero-labour edge
        foreach (int n in lengths)
        {
            foreach (bool needEmp in new[] { false, true })
            {
                var sizes = new long[n];
                var isUpper = new byte[n];
                var anchor = new float[n];
                var empCs = new float[n]; var wealthCs = new float[n]; var solCs = new float[n]; var radCs = new float[n];
                for (int i = 0; i < n; i++)
                {
                    sizes[i] = 15_000L + (long)(rng.NextDouble() * 450_000.0);
                    isUpper[i] = i % 5 == 0 ? (byte)1 : (byte)0;
                    anchor[i] = 0.30f + (i % 4) * 0.18f;
                    empCs[i] = 0.65f + (float)rng.NextDouble() * 0.30f;
                    wealthCs[i] = 0.25f + (float)rng.NextDouble() * 0.60f;
                    solCs[i] = 0.30f + (float)rng.NextDouble() * 0.55f;
                    radCs[i] = (float)rng.NextDouble() * 0.50f;
                }
                var empNv = (float[])empCs.Clone(); var wealthNv = (float[])wealthCs.Clone();
                var solNv = (float[])solCs.Clone(); var radNv = (float[])radCs.Clone();

                NativeEconomyDemoKernels.ManagedHouseholdGroups(
                    n, sizes, isUpper, anchor, empCs, wealthCs, solCs, radCs,
                    0.62f, needEmp, 2.4f, needEmp ? -1f : 1.75f, 0.04f, 7f / 365f,
                    0.15f, 0.86f, 0.72f, 0.61f, 0.05f, 0.08f, 0.025f, 0.58f, 0.12f,
                    out float csNb, out float csEt, out float csBn, out float csSt, out double csLiv);

                if (nativeAvailable)
                {
                    Check(NativeEconomyDemoKernels.TryHouseholdGroups(
                        n, sizes, isUpper, anchor, empNv, wealthNv, solNv, radNv,
                        0.62f, needEmp, 2.4f, needEmp ? -1f : 1.75f, 0.04f, 7f / 365f,
                        0.15f, 0.86f, 0.72f, 0.61f, 0.05f, 0.08f, 0.025f, 0.58f, 0.12f,
                        out float nvNb, out float nvEt, out float nvBn, out float nvSt, out double nvLiv),
                        $"TryHouseholdGroups n={n} needEmp={needEmp}");
                    Check(BitConverter.SingleToInt32Bits(csNb) == BitConverter.SingleToInt32Bits(nvNb), $"hh.nb n={n}");
                    Check(BitConverter.SingleToInt32Bits(csEt) == BitConverter.SingleToInt32Bits(nvEt), $"hh.et n={n}");
                    Check(BitConverter.SingleToInt32Bits(csBn) == BitConverter.SingleToInt32Bits(nvBn), $"hh.bn n={n}");
                    Check(BitConverter.SingleToInt32Bits(csSt) == BitConverter.SingleToInt32Bits(nvSt), $"hh.st n={n}");
                    Check(BitConverter.DoubleToInt64Bits(csLiv) == BitConverter.DoubleToInt64Bits(nvLiv), $"hh.living n={n}");
                    CheckBitExact(empCs, empNv, $"hh.emp.n{n}");
                    CheckBitExact(wealthCs, wealthNv, $"hh.wealth.n{n}");
                    CheckBitExact(solCs, solNv, $"hh.sol.n{n}");
                    CheckBitExact(radCs, radNv, $"hh.rad.n{n}");
                }
            }
        }

        // 4. VitalRatesBatch parity across lengths + all flags + Math.Round midpoint rounding
        foreach (int n in lengths)
        {
            var pop = new long[n]; var wd = new float[n]; var hosp = new int[n];
            var lit = new float[n]; var urb = new float[n]; var sol = new float[n];
            var rad = new float[n]; var ys = new float[n]; var os = new float[n];
            var wel = new float[n]; var inf = new float[n]; var ws = new float[n];
            var ns = new float[n]; var ss = new float[n]; var lat = new float[n];
            var fd = new float[n]; var en = new float[n]; var md = new float[n];
            var flg = new byte[n];
            for (int i = 0; i < n; i++)
            {
                pop[i] = 100_000L + (long)rng.Next(0, 12_000_000);
                wd[i] = i % 4 == 0 ? 0.25f : (float)rng.NextDouble() * 0.12f;
                hosp[i] = i % 6;
                lit[i] = 0.25f + (float)rng.NextDouble() * 0.72f;
                urb[i] = 0.005f + (float)rng.NextDouble() * 0.90f;
                sol[i] = 0.20f + (float)rng.NextDouble() * 0.75f;
                rad[i] = i % 5 == 0 ? 0.80f : (float)rng.NextDouble() * 0.55f;
                ys[i] = 0.12f + (float)rng.NextDouble() * 0.30f;
                os[i] = 0.05f + (float)rng.NextDouble() * 0.25f;
                wel[i] = (float)rng.NextDouble() * 0.30f;
                inf[i] = (float)rng.NextDouble() * 0.45f;
                ws[i] = 0.30f + (float)rng.NextDouble() * 0.65f;
                ns[i] = 0.30f + (float)rng.NextDouble() * 0.65f;
                ss[i] = i % 5 == 0 ? 0.12f : (float)rng.NextDouble() * 0.85f;
                lat[i] = (float)(rng.NextDouble() * 140.0 - 70.0);
                fd[i] = i % 8 == 0 ? 0.22f : 0.45f + (float)rng.NextDouble() * 0.55f;
                en[i] = i % 6 == 0 ? 0.28f : 0.40f + (float)rng.NextDouble() * 0.60f;
                md[i] = i % 5 == 0 ? 0.18f : 0.35f + (float)rng.NextDouble() * 0.65f;
                flg[i] = (byte)(i & 63);
            }
            var csB = new float[n]; var csD = new float[n]; var csOb = new float[n];
            var csRb = new long[n]; var csRd = new long[n];
            NativeEconomyDemoKernels.ManagedVitalRatesBatch(
                pop, wd, hosp, lit, urb, sol, rad, ys, os, wel, inf, ws, ns, ss, lat, fd, en, md, flg,
                0.05f, 0.010f, 0.018f, 0.006f, 0.005f, 0.10f, 1.4f, 7f / 365f,
                csB, csD, csOb, csRb, csRd);
            if (nativeAvailable)
            {
                var nvB = new float[n]; var nvD = new float[n]; var nvOb = new float[n];
                var nvRb = new long[n]; var nvRd = new long[n];
                Check(NativeEconomyDemoKernels.TryVitalRatesBatch(
                    pop, wd, hosp, lit, urb, sol, rad, ys, os, wel, inf, ws, ns, ss, lat, fd, en, md, flg,
                    0.05f, 0.010f, 0.018f, 0.006f, 0.005f, 0.10f, 1.4f, 7f / 365f,
                    nvB, nvD, nvOb, nvRb, nvRd),
                    $"TryVitalRatesBatch n={n}");
                CheckBitExact(csB, nvB, $"vital.birth.n{n}");
                CheckBitExact(csD, nvD, $"vital.death.n{n}");
                CheckBitExact(csOb, nvOb, $"vital.old_burden.n{n}");
                for (int i = 0; i < n; i++)
                {
                    Check(csRb[i] == nvRb[i] && csRd[i] == nvRd[i], $"vital.review_counts.n{n}[{i}]");
                }
            }
        }

        // 5. Campaign-level integration: HouseholdEconomy.VitalRates / ComputeVitalRatesBatch, Review (>= 16 groups), DemographicCycle.Tick & Aggregate + zero alloc
        bool prevDefault = NativeKernelsLoader.EnableEconomyDemoDefault;
        try
        {
            var cNative = DealOrForce.Simulation.Campaign.CreateBootstrap(20261127);
            var cManaged = DealOrForce.Simulation.Campaign.CreateBootstrap(20261127);

            static void AddSyntheticHouseholds(DealOrForce.Simulation.ScenarioState s)
            {
                s.PopulationGroups.Clear();
                const int groupCount = 24;
                long basePop = Math.Max(groupCount * 10_000L, s.Population);
                long perGroup = basePop / groupCount;
                long rem = basePop - perGroup * groupCount;
                for (int g = 0; g < groupCount; g++)
                {
                    s.PopulationGroups.Add(new DealOrForce.Simulation.PopulationGroup
                    {
                        Class = (DealOrForce.Simulation.SocialClass)(g % 3),
                        Sector = (DealOrForce.Simulation.Sector)(g % 3),
                        Culture = "rus",
                        Religion = "orthodox",
                        Profession = g % 2 == 0 ? "farmers" : "workers",
                        Size = perGroup + (g == 0 ? rem : 0L),
                        Employment = 0.88f,
                        Wealth = 0.45f,
                        StandardOfLiving = 0.55f,
                        Radicalism = 0.08f,
                    });
                }
                s.Population = basePop;
                s.NonBuildingJobs = -1f;
            }

            DealOrForce.Simulation.ScenarioState? target1 = null;
            DealOrForce.Simulation.Nation? owner1 = null;
            for (int i = 0; i < cNative.States.Count; i++)
            {
                var s1 = cNative.States[i];
                if (s1 is not null && !s1.Unclaimed && s1.Population > 0)
                {
                    var o1 = cNative.Nations.Find(x => x.MapIndex == s1.NationMapIndex);
                    if (o1 is not null)
                    {
                        target1 = s1;
                        owner1 = o1;
                        AddSyntheticHouseholds(s1);
                        AddSyntheticHouseholds(cManaged.States[i]);
                        break;
                    }
                }
            }
            Check(target1 is not null && owner1 is not null, "bootstrap campaign has household test target");

            var hoursProp = typeof(DealOrForce.Simulation.Campaign).GetProperty(nameof(DealOrForce.Simulation.Campaign.ElapsedHours))!;
            hoursProp.SetValue(cNative, 7L * 24L);
            hoursProp.SetValue(cManaged, 7L * 24L);

            if (target1 is not null && owner1 is not null)
            {
                int idx = cNative.States.IndexOf(target1);
                var target2 = cManaged.States[idx];
                var owner2 = cManaged.Nations.Find(x => x.MapIndex == target2.NationMapIndex)!;

                NativeKernelsLoader.EnableEconomyDemoDefault = true;
                DealOrForce.Simulation.HouseholdEconomy.Review(cNative, owner1, target1, 0.03f);
                NativeKernelsLoader.EnableEconomyDemoDefault = false;
                DealOrForce.Simulation.HouseholdEconomy.Review(cManaged, owner2, target2, 0.03f);
            }

            foreach (var s in cNative.States)
                if (s is not null && !s.Unclaimed && s.Population > 0) s.LastHouseholdReviewDay = cNative.ElapsedDays;
            foreach (var s in cManaged.States)
                if (s is not null && !s.Unclaimed && s.Population > 0) s.LastHouseholdReviewDay = cManaged.ElapsedDays;

            NativeKernelsLoader.EnableEconomyDemoDefault = true;
            DealOrForce.Simulation.DemographicCycle.Tick(cNative);
            NativeKernelsLoader.EnableEconomyDemoDefault = false;
            DealOrForce.Simulation.DemographicCycle.Tick(cManaged);

            Check(DealOrForce.Simulation.CampaignFingerprint.Of(cNative) == DealOrForce.Simulation.CampaignFingerprint.Of(cManaged),
                "CampaignFingerprint matches bit-for-bit across HouseholdEconomy + DemographicCycle");

            // Check ComputeVitalRatesBatch against VitalRates per state.
            int sampleCount = Math.Min(32, cNative.States.Count);
            var nArr = new DealOrForce.Simulation.Nation[sampleCount];
            var sArr = new DealOrForce.Simulation.ScenarioState[sampleCount];
            var wArr = new bool[sampleCount];
            int filled = 0;
            var liveWars = DealOrForce.Simulation.Warfare.LiveWars(cNative);
            for (int i = 0; i < cNative.States.Count && filled < sampleCount; i++)
            {
                var s = cNative.States[i];
                if (s is null || s.Unclaimed || s.Population <= 0) continue;
                var o = cNative.Nations.Find(x => x.MapIndex == s.NationMapIndex);
                if (o is null) continue;
                bool isWar = false;
                for (int wi = 0; wi < liveWars.Count; wi++)
                {
                    if (liveWars[wi].Involves(o.Id)) { isWar = true; break; }
                }
                nArr[filled] = o;
                sArr[filled] = s;
                wArr[filled] = isWar;
                filled++;
            }
            if (filled > 0)
            {
                NativeKernelsLoader.EnableEconomyDemoDefault = true;
                var bB = new float[filled]; var bD = new float[filled]; var bO = new float[filled];
                var bRb = new long[filled]; var bRd = new long[filled];
                DealOrForce.Simulation.HouseholdEconomy.ComputeVitalRatesBatch(
                    cNative, nArr.AsSpan(0, filled), sArr.AsSpan(0, filled), wArr.AsSpan(0, filled),
                    7f / 365f, bB, bD, bO, bRb, bRd);
                for (int i = 0; i < filled; i++)
                {
                    var (sb, sd, sob) = DealOrForce.Simulation.HouseholdEconomy.VitalRates(cNative, nArr[i], sArr[i]);
                    Check(BitConverter.SingleToInt32Bits(sb) == BitConverter.SingleToInt32Bits(bB[i]), $"VitalRates.b[{i}]");
                    Check(BitConverter.SingleToInt32Bits(sd) == BitConverter.SingleToInt32Bits(bD[i]), $"VitalRates.d[{i}]");
                    Check(BitConverter.SingleToInt32Bits(sob) == BitConverter.SingleToInt32Bits(bO[i]), $"VitalRates.ob[{i}]");
                }
            }

            if (nativeAvailable)
            {
                const int allocN = 64;
                var f = new float[allocN]; var b = new byte[allocN];
                var iArr = new int[allocN]; var lArr = new long[allocN];
                var o1 = new float[allocN]; var o2 = new float[allocN]; var o3 = new float[allocN];
                var e4 = new float[allocN];
                var rB = new long[allocN]; var rD = new long[allocN]; var rM = new long[allocN];
                uint[] off = { 0u, (uint)allocN };
                var f1 = new float[1]; var f2 = new float[1]; var f3 = new float[1]; var f4 = new float[1];
                var l1 = new long[1]; var l2 = new long[1]; var l3 = new long[1];
                for (int i = 0; i < allocN; i++) { f[i] = 0.25f; lArr[i] = 100_000L; e4[i] = 0.5f; }

                NativeEconomyDemoKernels.TryStepCohortsBatch(f, lArr, lArr, f, f, 0.055f, 0.018f, 1.4f, 0.02f, o1, o2, o3);
                NativeEconomyDemoKernels.TryAggregateNations(1, off, lArr, f, f, lArr, lArr, lArr, allocN, 0.3f, 0.09f, 0.61f, 0.1f, f1, f2, f3, f4, l1, l2, l3);
                NativeEconomyDemoKernels.TryHouseholdGroups(allocN, lArr, b, f, o1, o2, o3, e4, 0.62f, true, 1f, -1f, 0.02f, 0.02f, 0.1f, 0.9f, 0.9f, 0.9f, 0.05f, 0.08f, 0.025f, 0.5f, 0.1f, out _, out _, out _, out _, out _);
                NativeEconomyDemoKernels.TryVitalRatesBatch(lArr, f, iArr, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, b, 0.05f, 0.01f, 0.018f, 0.006f, 0.005f, 0.1f, 1.4f, 0.02f, o1, o2, o3, rB, rD);

                long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
                for (int it = 0; it < 20; it++)
                {
                    NativeEconomyDemoKernels.TryStepCohortsBatch(f, lArr, lArr, f, f, 0.055f, 0.018f, 1.4f, 0.02f, o1, o2, o3);
                    NativeEconomyDemoKernels.TryAggregateNations(1, off, lArr, f, f, lArr, lArr, lArr, allocN, 0.3f, 0.09f, 0.61f, 0.1f, f1, f2, f3, f4, l1, l2, l3);
                    NativeEconomyDemoKernels.TryHouseholdGroups(allocN, lArr, b, f, o1, o2, o3, e4, 0.62f, true, 1f, -1f, 0.02f, 0.02f, 0.1f, 0.9f, 0.9f, 0.9f, 0.05f, 0.08f, 0.025f, 0.5f, 0.1f, out _, out _, out _, out _, out _);
                    NativeEconomyDemoKernels.TryVitalRatesBatch(lArr, f, iArr, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, b, 0.05f, 0.01f, 0.018f, 0.006f, 0.005f, 0.1f, 1.4f, 0.02f, o1, o2, o3, rB, rD);
                }
                long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
                Check(afterAlloc - beforeAlloc == 0, $"economy/demo Try* allocate 0 bytes (delta={afterAlloc - beforeAlloc})");
            }
        }
        finally
        {
            NativeKernelsLoader.EnableEconomyDemoDefault = prevDefault;
        }

        Console.WriteLine("PASS native kernel parity: economy & demography kernels bit-exact (StepCohorts, Aggregate, HouseholdGroups, VitalRates, CampaignFingerprint)");
    }
}
