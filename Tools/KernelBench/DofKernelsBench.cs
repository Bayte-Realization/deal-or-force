// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Kernels;

namespace DealOrForce.Tools.KernelBench;

/// <summary>
/// Benchmarks the new ABI v1 Rust SIMD hot-path kernels (<see cref="NativeDiplomacyKernels"/>,
/// <see cref="NativeMarketKernels"/>, <see cref="NativeDistanceKernels"/>) against their
/// bit-compatible pure-C# fallbacks across campaign-realistic batch sizes.
/// </summary>
internal static class DofKernelsBench
{
    public static int Run(int samples, int warmup)
    {
        Console.WriteLine();
        Console.WriteLine("=== dof_kernels (Win64 Rust SIMD vs Pure C#) ===");
        Console.WriteLine($"  IsAvailable={NativeKernelsLoader.IsAvailable}, ABI={NativeKernelsLoader.AbiVersion}, " +
                          $"SimdLevel={NativeKernelsLoader.SimdLevel}, Caps=0x{NativeKernelsLoader.Capabilities:X}, " +
                          $"Path={NativeKernelsLoader.LoadedPath}");
        if (!NativeKernelsLoader.IsAvailable)
        {
            Console.WriteLine("  FAIL: native dof_kernels not loaded: " + NativeKernelsLoader.Diagnostic);
            return 1;
        }

        int failures = 0;
        failures += BenchDiplomacyPairs(samples, warmup);
        failures += BenchDiplomacyReasonDecay(samples, warmup);
        failures += BenchMarketSettlePrices(samples, warmup);
        failures += BenchMarketClearNations(samples, warmup);
        failures += BenchDistanceRangeAndMatrix(samples, warmup);
        failures += BenchRasterPackOverlays(samples, warmup);
        failures += BenchPoliticalPlatePaint(samples, warmup);
        failures += BenchDemoStepCohorts(samples, warmup);
        failures += BenchDemoAggregateNations(samples, warmup);
        failures += BenchEconHouseholdGroups(samples, warmup);
        failures += BenchEconVitalRates(samples, warmup);
        failures += BenchPoliticalPlateDirtyRect(samples, warmup);
        failures += BenchFrontierDirtyRect(samples, warmup);
        failures += BenchPoliticalCohortsDrift(samples, warmup);
        failures += BenchGraphWartimeKernels(samples, warmup);
        failures += BenchTradeAffinityAndEconCombat(samples, warmup);
        return failures == 0 ? 0 : 1;
    }

    private static (double MedianMs, double P95Ms) Measure(int warmup, int samples, Action action)
    {
        for (int i = 0; i < warmup; i++) action();
        var times = new List<double>(samples);
        var sw = new Stopwatch();
        for (int i = 0; i < samples; i++)
        {
            sw.Restart();
            action();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        double med = times[times.Count / 2];
        double p95 = times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * 0.95) - 1)];
        return (med, p95);
    }

    private static void Report(string name, (double MedianMs, double P95Ms) cs, (double MedianMs, double P95Ms) nv, string parity)
    {
        double speedup = nv.MedianMs > 0 ? cs.MedianMs / nv.MedianMs : 0.0;
        Console.WriteLine($"  {name,-34} C# med={cs.MedianMs,7:F3} ms (p95={cs.P95Ms,7:F3}) | " +
                          $"Rust med={nv.MedianMs,7:F3} ms (p95={nv.P95Ms,7:F3}) | {speedup,5:F2}x | {parity}");
    }

    private static bool BitEqual(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                return false;
        }
        return true;
    }

    private static int BenchDiplomacyPairs(int samples, int warmup)
    {
        // 316 nations => ~49,770 unordered pairs.
        const int n = 49_770;
        var rng = new Random(101);
        var le = new float[n]; var la = new float[n]; var re = new float[n]; var ra = new float[n];
        var eab = new float[n]; var eba = new float[n];
        var cl = new float[n]; var cr = new float[n]; var cb = new float[n];
        var bb = new float[n]; var br = new float[n]; var wa = new float[n]; var wf = new float[n];
        var id = new float[n]; var sr = new float[n]; var fr = new float[n];
        var pf = new byte[n];
        for (int i = 0; i < n; i++)
        {
            le[i] = (float)(rng.NextDouble() * 2 - 1); la[i] = (float)(rng.NextDouble() * 2 - 1);
            re[i] = (float)(rng.NextDouble() * 2 - 1); ra[i] = (float)(rng.NextDouble() * 2 - 1);
            eab[i] = (float)(rng.NextDouble() * 2 - 1); eba[i] = (float)(rng.NextDouble() * 2 - 1);
            cl[i] = i % 7 == 0 ? 0.6f : 0f; cr[i] = i % 11 == 0 ? 0.4f : 0f; cb[i] = 0.1f;
            bb[i] = (float)(rng.NextDouble() * 2 - 1); br[i] = (float)(rng.NextDouble() * 120 - 60);
            wa[i] = -12f; wf[i] = 8f; id[i] = 5f; sr[i] = 1.2f; fr[i] = 0.6f;
            pf[i] = (byte)((i & 3) | 4);
        }

        var csP = new float[n]; var csS = new float[n]; var csC = new float[n];
        var csG = new float[n]; var csO = new float[n]; var csF = new float[n];
        var nvP = new float[n]; var nvS = new float[n]; var nvC = new float[n];
        var nvG = new float[n]; var nvO = new float[n]; var nvF = new float[n];

        var cs = Measure(warmup, samples, () => NativeDiplomacyKernels.ManagedScorePairs(
            le, la, re, ra, eab, eba, cl, cr, cb, bb, br, wa, wf, id, sr, fr, pf,
            18f, 8f, 0.8f, -25f, 15f, csP, csS, csC, csG, csO, csF));
        var nv = Measure(warmup, samples, () => NativeDiplomacyKernels.TryNativeScorePairs(
            le, la, re, ra, eab, eba, cl, cr, cb, bb, br, wa, wf, id, sr, fr, pf,
            18f, 8f, 0.8f, -25f, 15f, nvP, nvS, nvC, nvG, nvO, nvF));

        bool ok = BitEqual(csP, nvP) && BitEqual(csS, nvS) && BitEqual(csC, nvC) &&
                  BitEqual(csG, nvG) && BitEqual(csO, nvO) && BitEqual(csF, nvF);
        Report($"dip_pair_score (N={n:N0})", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchDiplomacyReasonDecay(int samples, int warmup)
    {
        const int n = 32_768;
        var rng = new Random(202);
        var baseOp = new float[n]; var stacks = new int[n]; var scale = new float[n];
        var granted = new int[n]; var expires = new int[n]; var decay = new int[n]; var flags = new byte[n];
        for (int i = 0; i < n; i++)
        {
            baseOp[i] = (float)(rng.NextDouble() * 40 - 20);
            stacks[i] = 1 + (i % 3);
            scale[i] = 1.0f;
            granted[i] = 100;
            expires[i] = 400 + (i % 200);
            decay[i] = 90;
            flags[i] = 1;
        }
        var csW = new float[n]; var csP = new byte[n];
        var nvW = new float[n]; var nvP = new byte[n];

        var cs = Measure(warmup, samples, () => NativeDiplomacyKernels.ManagedEvalReasonDecay(
            baseOp, stacks, scale, granted, expires, decay, flags, 250, csW, csP));
        var nv = Measure(warmup, samples, () => NativeDiplomacyKernels.TryNativeEvalReasonDecay(
            baseOp, stacks, scale, granted, expires, decay, flags, 250, nvW, nvP));

        bool ok = BitEqual(csW, nvW) && csP.AsSpan().SequenceEqual(nvP);
        Report($"dip_reason_decay (N={n:N0})", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchMarketSettlePrices(int samples, int warmup)
    {
        const int goods = 24;
        const int nations = 320;
        int total = goods * nations;
        var rng = new Random(303);
        var bp = new float[goods]; var tw = new byte[goods];
        for (int g = 0; g < goods; g++) { bp[g] = 10f + g; tw[g] = (byte)(g % 2); }
        var dem = new float[total]; var prod = new float[total]; var imp = new float[total]; var rel = new float[total];
        var priceInit = new float[total];
        for (int i = 0; i < total; i++)
        {
            dem[i] = (float)(10.0 + rng.NextDouble() * 500);
            prod[i] = (float)(rng.NextDouble() * 480);
            imp[i] = (float)(rng.NextDouble() * 60);
            rel[i] = imp[i] * 0.25f;
            priceInit[i] = 12f;
        }
        var csSat = new float[total]; var csPr = (float[])priceInit.Clone(); var csWs = new float[goods];
        var nvSat = new float[total]; var nvPr = (float[])priceInit.Clone(); var nvWs = new float[goods];

        var cs = Measure(warmup, samples, () => NativeMarketKernels.ManagedSettlePrices(
            goods, nations, nations, bp, tw, dem, prod, imp, rel, true, 1.25f, 0.45f, 0.35f, csSat, csPr, csWs));
        var nv = Measure(warmup, samples, () => NativeMarketKernels.TryNativeSettlePrices(
            goods, nations, nations, bp, tw, dem, prod, imp, rel, true, 1.25f, 0.45f, 0.35f, nvSat, nvPr, nvWs));

        // Reset and run one clean pair for exact parity check (adjustPrice modifies price[] cumulatively).
        priceInit.CopyTo(csPr, 0);
        priceInit.CopyTo(nvPr, 0);
        NativeMarketKernels.ManagedSettlePrices(
            goods, nations, nations, bp, tw, dem, prod, imp, rel, true, 1.25f, 0.45f, 0.35f, csSat, csPr, csWs);
        NativeMarketKernels.TryNativeSettlePrices(
            goods, nations, nations, bp, tw, dem, prod, imp, rel, true, 1.25f, 0.45f, 0.35f, nvSat, nvPr, nvWs);

        bool ok = BitEqual(csWs, nvWs) && BitEqual(csSat, nvSat) && BitEqual(csPr, nvPr);
        Report($"market_settle ({goods}x{nations})", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchMarketClearNations(int samples, int warmup)
    {
        const int nations = 320;
        const int goods = 24;
        int total = nations * goods;
        var rng = new Random(404);
        var bp = new float[goods]; var ew = new float[goods];
        for (int g = 0; g < goods; g++) { bp[g] = 8f + g; ew[g] = g < 6 ? 1.5f : 0.5f; }
        var tariff = new float[nations];
        for (int n = 0; n < nations; n++) tariff[n] = 0.08f;
        var prod = new float[total]; var dem = new float[total]; var imp = new float[total];
        var exp = new float[total]; var rel = new float[total]; var sat = new float[total]; var pr = new float[total];
        for (int i = 0; i < total; i++)
        {
            prod[i] = (float)(rng.NextDouble() * 300); dem[i] = (float)(rng.NextDouble() * 320);
            imp[i] = (float)(rng.NextDouble() * 40); exp[i] = (float)(rng.NextDouble() * 35);
            rel[i] = imp[i] * 0.2f; sat[i] = 0.85f; pr[i] = 11f;
        }
        var csGo = new float[nations]; var csRo = new float[nations]; var csSi = new float[nations];
        var csMa = new float[nations]; var csTb = new float[nations]; var csTi = new float[nations];
        var nvGo = new float[nations]; var nvRo = new float[nations]; var nvSi = new float[nations];
        var nvMa = new float[nations]; var nvTb = new float[nations]; var nvTi = new float[nations];

        var cs = Measure(warmup, samples, () => NativeMarketKernels.ManagedClearNations(
            nations, goods, goods, bp, ew, tariff, prod, dem, imp, exp, rel, sat, pr,
            0.25f, 0.4f, 0.5f, csGo, csRo, csSi, csMa, csTb, csTi));
        var nv = Measure(warmup, samples, () => NativeMarketKernels.TryNativeClearNations(
            nations, goods, goods, bp, ew, tariff, prod, dem, imp, exp, rel, sat, pr,
            0.25f, 0.4f, 0.5f, nvGo, nvRo, nvSi, nvMa, nvTb, nvTi));

        bool ok = BitEqual(csGo, nvGo) && BitEqual(csRo, nvRo) && BitEqual(csSi, nvSi) &&
                  BitEqual(csMa, nvMa) && BitEqual(csTb, nvTb) && BitEqual(csTi, nvTi);
        Report($"market_clear ({nations}x{goods})", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchDistanceRangeAndMatrix(int samples, int warmup)
    {
        const int n = 4096;
        var rng = new Random(505);
        var lats = new float[n]; var lons = new float[n];
        for (int i = 0; i < n; i++)
        {
            lats[i] = (float)(rng.NextDouble() * 140 - 70);
            lons[i] = (float)(rng.NextDouble() * 360 - 180);
        }
        var csIdx = new int[n]; var csKm = new float[n];
        var nvIdx = new int[n]; var nvKm = new float[n];

        int csCnt = 0;
        int nvCnt = 0;
        var cs = Measure(warmup, samples, () =>
            csCnt = NativeDistanceKernels.ManagedWithinRangeBatch(55.75f, 37.61f, lats, lons, 900f, csIdx, csKm));
        var nv = Measure(warmup, samples, () =>
            NativeDistanceKernels.TryNativeWithinRangeBatch(55.75f, 37.61f, lats, lons, 900f, nvIdx, nvKm, out nvCnt));

        bool ok = csCnt == nvCnt;
        float maxDiff = 0f;
        if (ok)
        {
            for (int i = 0; i < csCnt; i++)
            {
                if (csIdx[i] != nvIdx[i]) { ok = false; break; }
                float d = MathF.Abs(csKm[i] - nvKm[i]);
                if (d > maxDiff) maxDiff = d;
            }
        }
        ok &= maxDiff <= 0.01f;
        Report($"distance_within_range (N={n:N0})", cs, nv, ok ? $"MATCH (max_diff={maxDiff:F5}km, hits={csCnt})" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchRasterPackOverlays(int samples, int warmup)
    {
        const int provCount = 12_000;
        const int bufBytes = 256 * 256 * 4;
        var rng = new Random(606);
        var owners = new ushort[provCount + 1];
        var painted = new ushort[provCount + 1];
        var controllers = new ushort[provCount + 1];
        var palette = new uint[65_536];
        for (int p = 1; p <= provCount; p++)
        {
            owners[p] = (ushort)(p % 250);
            painted[p] = (p % 13 == 0) ? (ushort)((p + 7) % 250) : owners[p];
            controllers[p] = (p % 17 == 0) ? (ushort)((p + 19) % 250) : owners[p];
        }
        for (int i = 1; i < 300; i++)
            palette[i] = 0xFF0000FFu | (uint)(rng.Next(40, 240) << 16) | (uint)(rng.Next(40, 240) << 8);

        var csCtl = new byte[bufBytes]; var csOwn = new byte[bufBytes]; var csOid = new byte[bufBytes];
        var nvCtl = new byte[bufBytes]; var nvOwn = new byte[bufBytes]; var nvOid = new byte[bufBytes];
        bool csMoved = false;
        bool nvMoved = false;

        var cs = Measure(warmup, samples, () =>
            csMoved = DealOrForce.Simulation.RasterOverlayKernel.PackProvinceOverlaysManaged(
                provCount, owners, painted, controllers, palette, 180, 65535, 0x6E7B8BFFu, csCtl, csOwn, csOid));
        var nv = Measure(warmup, samples, () =>
            NativeRasterBake.TryPackProvinceOverlays(
                provCount, owners, painted, controllers, palette, 180, 65535, 0x6E7B8BFFu, nvCtl, nvOwn, nvOid, out nvMoved));

        bool ok = csMoved == nvMoved &&
                  csCtl.AsSpan().SequenceEqual(nvCtl) &&
                  csOwn.AsSpan().SequenceEqual(nvOwn) &&
                  csOid.AsSpan().SequenceEqual(nvOid);
        Report($"raster_pack_overlays (N={provCount:N0})", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchPoliticalPlatePaint(int samples, int warmup)
    {
        const int width = 1920;
        const int height = 960;
        const int cells = width * height;
        var holders = new ushort[cells];
        var provs = new ushort[cells];
        var geo = new byte[cells];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int c = row + x;
                if (y < 40 || y >= height - 40 || ((x + y) % 41) == 0)
                {
                    holders[c] = 0;
                    provs[c] = 0;
                }
                else
                {
                    holders[c] = (ushort)(1 + ((x / 64 + y / 48 * 30) % 220));
                    provs[c] = (ushort)(1 + ((x / 16 + y / 16 * 120) % 12000));
                    if ((c % 23) == 0) geo[c] |= DealOrForce.Simulation.PoliticalPlate.Coast;
                    if ((c % 11) == 0) geo[c] |= DealOrForce.Simulation.PoliticalPlate.RegionLine;
                }
            }
        }

        const int provLen = 12_001;
        var owners = new ushort[provLen];
        var authored = new ushort[provLen];
        for (int p = 1; p < provLen; p++)
        {
            authored[p] = (ushort)(1 + (p % 220));
            owners[p] = (p % 19 == 0) ? (ushort)(1 + ((p + 5) % 220)) : authored[p];
        }
        var colours = new Dictionary<int, uint>(256);
        var palette = new uint[65_536];
        Array.Fill(palette, DealOrForce.Simulation.MapBorders.UnownedColour);
        for (int n = 1; n <= 230; n++)
        {
            uint rgba = ((uint)((n * 73) & 0xFF) << 24) |
                        ((uint)((n * 137) & 0xFF) << 16) |
                        ((uint)((n * 199) & 0xFF) << 8) | 0xFFu;
            colours[n] = rgba == 0 ? 0x112233FFu : rgba;
            palette[n] = colours[n];
        }

        var shades = new DealOrForce.Simulation.MapPlateShades(0.34f, 0.70f, 0.92f);
        var source = new DealOrForce.Simulation.PoliticalPlateSource
        {
            Width = width,
            Height = height,
            Holders = holders,
            Provinces = provs,
            Geography = geo,
        };

        byte[] csPixels = Array.Empty<byte>();
        var nvPixels = new byte[cells * 4];
        var cs = Measure(warmup, samples, () =>
            csPixels = DealOrForce.Simulation.PoliticalPlate.PaintManaged(source, owners, authored, colours, shades));
        var nv = Measure(warmup, samples, () =>
            NativeRasterBake.TryPaintPoliticalPlate(
                width, height, holders, provs, geo, owners, authored, palette,
                shades.Country, shades.Region, shades.Inland, nvPixels));

        bool ok = csPixels.AsSpan().SequenceEqual(nvPixels);
        Report($"political_plate ({width}x{height})", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchDemoStepCohorts(int samples, int warmup)
    {
        const int n = 1_847;
        var rng = new Random(7001);
        var pop = new float[n]; var rb = new long[n]; var rd = new long[n];
        var ys = new float[n]; var os = new float[n];
        for (int i = 0; i < n; i++)
        {
            pop[i] = 40_000f + (float)rng.NextDouble() * 9_000_000f;
            rb[i] = rng.Next(10, 40_000);
            rd[i] = rng.Next(10, 35_000);
            ys[i] = 0.12f + (float)rng.NextDouble() * 0.35f;
            os[i] = 0.04f + (float)rng.NextDouble() * 0.25f;
        }

        var csY = new float[n]; var csO = new float[n]; var csW = new float[n];
        var nvY = new float[n]; var nvO = new float[n]; var nvW = new float[n];
        var cs = Measure(warmup, samples, () =>
            NativeEconomyDemoKernels.ManagedStepCohortsBatch(pop, rb, rd, ys, os, 0.055f, 0.018f, 1.4f, 7f / 365f, csY, csO, csW));
        var nv = Measure(warmup, samples, () =>
            NativeEconomyDemoKernels.TryStepCohortsBatch(pop, rb, rd, ys, os, 0.055f, 0.018f, 1.4f, 7f / 365f, nvY, nvO, nvW));

        bool ok = csY.AsSpan().SequenceEqual(nvY) && csO.AsSpan().SequenceEqual(nvO) && csW.AsSpan().SequenceEqual(nvW);
        Report($"demo_step_cohorts ({n} states)", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchDemoAggregateNations(int samples, int warmup)
    {
        const int nc = 220;
        const int sc = 1_847;
        var rng = new Random(7002);
        var offsets = new uint[nc + 1];
        for (int n = 1; n <= nc; n++) offsets[n] = (uint)(n * sc / nc);

        var pop = new long[sc]; var ys = new float[sc]; var os = new float[sc];
        var stB = new long[sc]; var stD = new long[sc]; var stM = new long[sc];
        for (int s = 0; s < sc; s++)
        {
            pop[s] = 50_000L + rng.Next(0, 8_000_000);
            ys[s] = 0.14f + (float)rng.NextDouble() * 0.30f;
            os[s] = 0.05f + (float)rng.NextDouble() * 0.22f;
            stB[s] = rng.Next(10, 5_000);
            stD[s] = rng.Next(10, 4_500);
            stM[s] = rng.Next(-1_000, 1_000);
        }

        var csY = new float[nc]; var csO = new float[nc]; var csW = new float[nc]; var csEx = new float[nc];
        var csB = new long[nc]; var csD = new long[nc]; var csM = new long[nc];
        var nvY = new float[nc]; var nvO = new float[nc]; var nvW = new float[nc]; var nvEx = new float[nc];
        var nvB = new long[nc]; var nvD = new long[nc]; var nvM = new long[nc];

        var cs = Measure(warmup, samples, () =>
            NativeEconomyDemoKernels.ManagedAggregateNations(
                nc, offsets, pop, ys, os, stB, stD, stM, sc, 0.30f, 0.09f, 0.61f, 0.10f,
                csY, csO, csW, csEx, csB, csD, csM));
        var nv = Measure(warmup, samples, () =>
            NativeEconomyDemoKernels.TryAggregateNations(
                nc, offsets, pop, ys, os, stB, stD, stM, sc, 0.30f, 0.09f, 0.61f, 0.10f,
                nvY, nvO, nvW, nvEx, nvB, nvD, nvM));

        bool ok = csY.AsSpan().SequenceEqual(nvY) && csO.AsSpan().SequenceEqual(nvO) &&
                  csW.AsSpan().SequenceEqual(nvW) && csEx.AsSpan().SequenceEqual(nvEx) &&
                  csB.AsSpan().SequenceEqual(nvB) && csD.AsSpan().SequenceEqual(nvD) &&
                  csM.AsSpan().SequenceEqual(nvM);
        Report($"demo_aggregate_nations ({nc}x{sc})", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchEconHouseholdGroups(int samples, int warmup)
    {
        const int gc = 64;
        var rng = new Random(7003);
        var sizes = new long[gc]; var isUpper = new byte[gc]; var anchor = new float[gc];
        var emp0 = new float[gc]; var w0 = new float[gc]; var sol0 = new float[gc]; var rad0 = new float[gc];
        for (int i = 0; i < gc; i++)
        {
            sizes[i] = 10_000L + rng.Next(0, 400_000);
            isUpper[i] = i % 5 == 0 ? (byte)1 : (byte)0;
            anchor[i] = 0.35f + (i % 4) * 0.16f;
            emp0[i] = 0.72f + (float)rng.NextDouble() * 0.22f;
            w0[i] = 0.35f + (float)rng.NextDouble() * 0.50f;
            sol0[i] = 0.35f + (float)rng.NextDouble() * 0.50f;
            rad0[i] = (float)rng.NextDouble() * 0.40f;
        }

        var empCs = new float[gc]; var wCs = new float[gc]; var solCs = new float[gc]; var radCs = new float[gc];
        var empNv = new float[gc]; var wNv = new float[gc]; var solNv = new float[gc]; var radNv = new float[gc];
        float csNb = 0f, csEt = 0f, csBn = 0f, csSt = 0f, nvNb = 0f, nvEt = 0f, nvBn = 0f, nvSt = 0f;
        double csLiv = 0d, nvLiv = 0d;

        var cs = Measure(warmup, samples, () =>
        {
            emp0.AsSpan().CopyTo(empCs);
            w0.AsSpan().CopyTo(wCs);
            sol0.AsSpan().CopyTo(solCs);
            rad0.AsSpan().CopyTo(radCs);
            NativeEconomyDemoKernels.ManagedHouseholdGroups(
                gc, sizes, isUpper, anchor, empCs, wCs, solCs, radCs,
                0.62f, true, 2.2f, -1f, 0.03f, 7f / 365f, 0.12f, 0.88f, 0.78f, 0.65f,
                0.05f, 0.08f, 0.025f, 0.56f, 0.12f,
                out csNb, out csEt, out csBn, out csSt, out csLiv);
        });
        var nv = Measure(warmup, samples, () =>
        {
            emp0.AsSpan().CopyTo(empNv);
            w0.AsSpan().CopyTo(wNv);
            sol0.AsSpan().CopyTo(solNv);
            rad0.AsSpan().CopyTo(radNv);
            NativeEconomyDemoKernels.TryHouseholdGroups(
                gc, sizes, isUpper, anchor, empNv, wNv, solNv, radNv,
                0.62f, true, 2.2f, -1f, 0.03f, 7f / 365f, 0.12f, 0.88f, 0.78f, 0.65f,
                0.05f, 0.08f, 0.025f, 0.56f, 0.12f,
                out nvNb, out nvEt, out nvBn, out nvSt, out nvLiv);
        });

        bool ok = BitConverter.SingleToInt32Bits(csNb) == BitConverter.SingleToInt32Bits(nvNb) &&
                  BitConverter.SingleToInt32Bits(csEt) == BitConverter.SingleToInt32Bits(nvEt) &&
                  BitConverter.SingleToInt32Bits(csBn) == BitConverter.SingleToInt32Bits(nvBn) &&
                  BitConverter.SingleToInt32Bits(csSt) == BitConverter.SingleToInt32Bits(nvSt) &&
                  BitConverter.DoubleToInt64Bits(csLiv) == BitConverter.DoubleToInt64Bits(nvLiv) &&
                  empCs.AsSpan().SequenceEqual(empNv) && wCs.AsSpan().SequenceEqual(wNv) &&
                  solCs.AsSpan().SequenceEqual(solNv) && radCs.AsSpan().SequenceEqual(radNv);
        Report($"econ_household_groups ({gc} groups)", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchEconVitalRates(int samples, int warmup)
    {
        const int n = 1_847;
        var rng = new Random(7004);
        var pop = new long[n]; var wd = new float[n]; var hosp = new int[n];
        var lit = new float[n]; var urb = new float[n]; var sol = new float[n];
        var rad = new float[n]; var ys = new float[n]; var os = new float[n];
        var wel = new float[n]; var inf = new float[n]; var ws = new float[n];
        var ns = new float[n]; var ss = new float[n]; var lat = new float[n];
        var fd = new float[n]; var en = new float[n]; var md = new float[n];
        var flg = new byte[n];
        for (int i = 0; i < n; i++)
        {
            pop[i] = 120_000L + rng.Next(0, 10_000_000);
            wd[i] = i % 5 == 0 ? 0.22f : (float)rng.NextDouble() * 0.1f;
            hosp[i] = i % 7;
            lit[i] = 0.28f + (float)rng.NextDouble() * 0.70f;
            urb[i] = 0.05f + (float)rng.NextDouble() * 0.85f;
            sol[i] = 0.25f + (float)rng.NextDouble() * 0.70f;
            rad[i] = (float)rng.NextDouble() * 0.80f;
            ys[i] = 0.12f + (float)rng.NextDouble() * 0.30f;
            os[i] = 0.05f + (float)rng.NextDouble() * 0.25f;
            wel[i] = (float)rng.NextDouble() * 0.25f;
            inf[i] = (float)rng.NextDouble() * 0.35f;
            ws[i] = 0.40f + (float)rng.NextDouble() * 0.55f;
            ns[i] = 0.35f + (float)rng.NextDouble() * 0.60f;
            ss[i] = (float)rng.NextDouble() * 0.90f;
            lat[i] = (float)(rng.NextDouble() * 140.0 - 70.0);
            fd[i] = 0.30f + (float)rng.NextDouble() * 0.70f;
            en[i] = 0.30f + (float)rng.NextDouble() * 0.70f;
            md[i] = 0.25f + (float)rng.NextDouble() * 0.75f;
            flg[i] = (byte)((i & 31) | NativeEconomyDemoKernels.VitalFlagAgeEnabled);
        }

        var csB = new float[n]; var csD = new float[n]; var csOb = new float[n];
        var csRb = new long[n]; var csRd = new long[n];
        var nvB = new float[n]; var nvD = new float[n]; var nvOb = new float[n];
        var nvRb = new long[n]; var nvRd = new long[n];

        var cs = Measure(warmup, samples, () =>
            NativeEconomyDemoKernels.ManagedVitalRatesBatch(
                pop, wd, hosp, lit, urb, sol, rad, ys, os, wel, inf, ws, ns, ss, lat, fd, en, md, flg,
                0.05f, 0.010f, 0.018f, 0.006f, 0.005f, 0.10f, 1.4f, 7f / 365f,
                csB, csD, csOb, csRb, csRd));
        var nv = Measure(warmup, samples, () =>
            NativeEconomyDemoKernels.TryVitalRatesBatch(
                pop, wd, hosp, lit, urb, sol, rad, ys, os, wel, inf, ws, ns, ss, lat, fd, en, md, flg,
                0.05f, 0.010f, 0.018f, 0.006f, 0.005f, 0.10f, 1.4f, 7f / 365f,
                nvB, nvD, nvOb, nvRb, nvRd));

        bool ok = csB.AsSpan().SequenceEqual(nvB) && csD.AsSpan().SequenceEqual(nvD) &&
                  csOb.AsSpan().SequenceEqual(nvOb) && csRb.AsSpan().SequenceEqual(nvRb) &&
                  csRd.AsSpan().SequenceEqual(nvRd);
        Report($"econ_vital_rates ({n} states)", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchPoliticalPlateDirtyRect(int samples, int warmup)
    {
        const int w = 1920, h = 960;
        var cells = w * h;
        var holders = new ushort[cells];
        var provs = new ushort[cells];
        var geo = new byte[cells];
        var rng = new Random(8001);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                holders[i] = (ushort)(1 + ((x / 64) + (y / 64) * 30) % 240);
                provs[i] = (ushort)(1 + ((x / 16) + (y / 16) * 120) % 32000);
                geo[i] = (byte)rng.Next(0, 4);
            }
        }
        var pal = new uint[65536];
        for (int i = 1; i < 300; i++)
            pal[i] = 0xFF000000u | ((uint)((i * 53) & 255) << 16) | ((uint)((i * 97) & 255) << 8) | (uint)((i * 193) & 255);
        var authored = new ushort[32001];
        var owners = new ushort[32001];
        for (int p = 1; p < authored.Length; p++) authored[p] = owners[p] = (ushort)(1 + (p % 240));
        var shades = MapBorderStyles.PlateShades();

        var fullChainLen = NativeRasterBake.ComputeMipmapChainBytes(w, h, 4);
        var fullBuf = new byte[fullChainLen];
        var rectBuf = new byte[fullChainLen];
        NativeRasterBake.TryPaintPoliticalPlate(w, h, holders, provs, geo, owners, authored, pal,
            shades.Country, shades.Region, shades.Inland, fullBuf);
        NativeRasterBake.TryMipmapRgba8Rect(w, h, 0, 0, w, h, fullBuf);
        Array.Copy(fullBuf, rectBuf, fullChainLen);

        // Mutate a realistic war-front cluster aligned to 16x16 provinces (128x128 rect inside 1920x960); pad by 1px for 4-neighbor border shading.
        const int x0 = 608, y0 = 320, x1 = 736, y1 = 448;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                owners[provs[y * w + x]] = 77;
        const int rx0 = x0 - 1, ry0 = y0 - 1, rx1 = x1 + 1, ry1 = y1 + 1;

        var full = Measure(warmup, samples, () =>
        {
            NativeRasterBake.TryPaintPoliticalPlate(w, h, holders, provs, geo, owners, authored, pal,
                shades.Country, shades.Region, shades.Inland, fullBuf);
            NativeRasterBake.TryMipmapRgba8Rect(w, h, 0, 0, w, h, fullBuf);
        });
        var rect = Measure(warmup, samples, () =>
        {
            NativeRasterBake.TryPaintPoliticalPlateRect(w, h, holders, provs, geo, owners, authored, pal,
                shades.Country, shades.Region, shades.Inland, rx0, ry0, rx1, ry1, rectBuf);
            NativeRasterBake.TryMipmapRgba8Rect(w, h, rx0, ry0, rx1, ry1, rectBuf);
        });

        bool ok = fullBuf.AsSpan().SequenceEqual(rectBuf);
        Report($"plate_dirty_rect+mip (128x128)", full, rect, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchFrontierDirtyRect(int samples, int warmup)
    {
        const int w = 1920, h = 960;
        var cells = w * h;
        var holders = new ushort[cells];
        var provs = new ushort[cells];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                holders[i] = (ushort)(1 + ((x / 96) + (y / 96) * 20) % 180);
                provs[i] = (ushort)(1 + ((x / 16) + (y / 16) * 120) % 32000);
            }
        }
        var authored = new ushort[32001];
        var owners = new ushort[32001];
        for (int p = 1; p < authored.Length; p++) authored[p] = owners[p] = (ushort)(1 + (p % 180));
        var fullChainLen = NativeRasterBake.ComputeMipmapChainBytes(w, h, 1);
        var fullBuf = new byte[fullChainLen];
        var rectBuf = new byte[fullChainLen];
        NativeRasterBake.TryBakeInto(holders, provs, w, h, owners, authored, FrontierField.Cap, FrontierField.Smoothing, fullBuf);
        NativeRasterBake.TryMipmapR8Rect(w, h, 0, 0, w, h, fullBuf);
        Array.Copy(fullBuf, rectBuf, fullChainLen);

        const int x0 = 600, y0 = 320, x1 = 728, y1 = 448;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                owners[provs[y * w + x]] = 55;

        int pad = FrontierField.DirtyPadCells;
        int rx0 = Math.Max(0, x0 - pad), ry0 = Math.Max(0, y0 - pad);
        int rx1 = Math.Min(w, x1 + pad), ry1 = Math.Min(h, y1 + pad);

        var full = Measure(warmup, samples, () =>
        {
            NativeRasterBake.TryBakeInto(holders, provs, w, h, owners, authored, FrontierField.Cap, FrontierField.Smoothing, fullBuf);
            NativeRasterBake.TryMipmapR8Rect(w, h, 0, 0, w, h, fullBuf);
        });
        var rect = Measure(warmup, samples, () =>
        {
            NativeRasterBake.TryBakeFrontierRectInto(holders, provs, w, h, owners, authored, FrontierField.Cap, FrontierField.Smoothing,
                rx0, ry0, rx1, ry1, rectBuf);
            NativeRasterBake.TryMipmapR8Rect(w, h, rx0, ry0, rx1, ry1, rectBuf);
        });

        bool ok = fullBuf.AsSpan().SequenceEqual(rectBuf);
        Report($"frontier_dirty_rect+mip (128^2)", full, rect, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchPoliticalCohortsDrift(int samples, int warmup)
    {
        const int n = 1_847;
        var rng = new Random(8002);
        var flags = new byte[n];
        var step = new float[n]; var blocStep = new float[n];
        var shortage = new float[n]; var stability = new float[n]; var natLiv = new float[n];
        var alien = new float[n]; var mil = new float[n]; var tax = new float[n]; var ma = new float[n];
        var lit = new float[n]; var urb = new float[n]; var sol = new float[n]; var rad = new float[n];
        var initLaw = new float[n]; var initGov = new float[n]; var initDiv = new float[n];
        var initBlocs = new float[n * 5];
        for (int i = 0; i < n; i++)
        {
            flags[i] = (byte)((i & 31) | 32);
            step[i] = 0.0012f; blocStep[i] = 0.0018f;
            shortage[i] = (float)rng.NextDouble() * 0.4f;
            stability[i] = 0.25f + (float)rng.NextDouble() * 0.7f;
            natLiv[i] = 0.3f + (float)rng.NextDouble() * 0.6f;
            alien[i] = (float)rng.NextDouble() * 0.8f;
            mil[i] = (float)rng.NextDouble();
            tax[i] = (float)rng.NextDouble();
            ma[i] = 0.3f + (float)rng.NextDouble() * 0.7f;
            lit[i] = 0.2f + (float)rng.NextDouble() * 0.8f;
            urb[i] = 0.1f + (float)rng.NextDouble() * 0.85f;
            sol[i] = 0.2f + (float)rng.NextDouble() * 0.75f;
            rad[i] = (float)rng.NextDouble() * 0.7f;
            initLaw[i] = 0.5f; initGov[i] = 0.5f; initDiv[i] = 0.12f;
            for (int b = 0; b < 5; b++) initBlocs[i * 5 + b] = 0.2f;
        }
        var csLaw = new float[n]; var csGov = new float[n]; var csDiv = new float[n]; var csBl = new float[n * 5];
        var nvLaw = new float[n]; var nvGov = new float[n]; var nvDiv = new float[n]; var nvBl = new float[n * 5];
        var tgtArr = new float[5];

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        static float Move(float v, float t, float a) => v < t ? MathF.Min(t, v + a) : MathF.Max(t, v - a);
        void RunManaged()
        {
            Array.Copy(initLaw, csLaw, n); Array.Copy(initGov, csGov, n); Array.Copy(initDiv, csDiv, n); Array.Copy(initBlocs, csBl, n * 5);
            for (int i = 0; i < n; i++)
            {
                var f = flags[i];
                bool debt = (f & 1) != 0, losing = (f & 2) != 0, recent = (f & 4) != 0, cap = (f & 8) != 0, war = (f & 16) != 0;
                float dp = debt ? 0.12f : 0f;
                float r = Clamp01(rad[i]);
                float law = Clamp01(0.40f + sol[i] * 0.40f + stability[i] * 0.15f - shortage[i] * 0.40f - dp * 0.5f - r * 0.25f);
                float gov = 0.42f + stability[i] * 0.35f + sol[i] * 0.18f - shortage[i] * 0.35f - dp - r * 0.22f - alien[i] * 0.14f;
                if (losing) gov -= 0.10f;
                if (recent) gov -= 0.08f;
                gov = Clamp01(gov);
                csLaw[i] = Move(Clamp01(csLaw[i]), law, step[i]);
                csGov[i] = Move(Clamp01(csGov[i]), gov, step[i]);
                float rej = Clamp01((1f - gov) * 0.55f + (1f - law) * 0.35f + r * 0.25f);
                float poorer = natLiv[i] > sol[i] ? MathF.Min(1f, natLiv[i] - sol[i]) : 0f;
                float div = Clamp01(rej + poorer * 0.20f + alien[i] * 0.16f);
                if (cap) div *= 0.45f;
                csDiv[i] = Move(Clamp01(csDiv[i]), div, step[i] * 0.65f);
                if ((f & 32) != 0)
                {
                    float lv = Clamp01(lit[i]), uv = Clamp01(urb[i]), sv = Clamp01(sol[i]);
                    float solGap = MathF.Max(0f, natLiv[i] - sv);
                    float t0 = 0.14f + lv * 0.24f + uv * 0.14f + sv * 0.12f - (war ? 0.08f : 0f);
                    float t1 = 0.12f + mil[i] * 0.22f + (war ? 0.20f : 0f) + (losing ? 0.10f : 0f) + r * 0.08f;
                    float t2 = 0.14f + uv * 0.22f + ma[i] * 0.14f - tax[i] * 0.10f - (debt ? 0.06f : 0f);
                    float t3 = 0.14f + (1f - uv) * 0.22f + (1f - lv) * 0.14f + stability[i] * 0.10f;
                    float t4 = 0.06f + alien[i] * 0.34f + solGap * 0.45f + r * 0.20f + shortage[i] * 0.18f + (debt ? 0.08f : 0f) + tax[i] * 0.08f;
                    if (cap) t4 *= 0.40f;
                    tgtArr[0] = MathF.Max(0.03f, t0);
                    tgtArr[1] = MathF.Max(0.03f, t1);
                    tgtArr[2] = MathF.Max(0.03f, t2);
                    tgtArr[3] = MathF.Max(0.03f, t3);
                    tgtArr[4] = MathF.Max(0.02f, t4);
                    float sum = tgtArr[0] + tgtArr[1] + tgtArr[2] + tgtArr[3] + tgtArr[4];
                    float inv = sum > 0f ? 1f / sum : 0.20f;
                    for (int b = 0; b < 5; b++) tgtArr[b] *= inv;
                    int bo = i * 5;
                    sum = 0f;
                    for (int b = 0; b < 5; b++)
                    {
                        float v = Move(csBl[bo + b], tgtArr[b], blocStep[i]);
                        v = float.IsFinite(v) ? MathF.Max(0.02f, v) : 0.20f;
                        csBl[bo + b] = v; sum += v;
                    }
                    inv = 1f / sum;
                    float run = 0f;
                    for (int b = 0; b < 4; b++) { csBl[bo + b] *= inv; run += csBl[bo + b]; }
                    csBl[bo + 4] = MathF.Max(0.001f, 1f - run);
                }
            }
        }
        void RunNative()
        {
            Array.Copy(initLaw, nvLaw, n); Array.Copy(initGov, nvGov, n); Array.Copy(initDiv, nvDiv, n); Array.Copy(initBlocs, nvBl, n * 5);
            NativePoliticalKernels.TryDriftCohortsBatch(n, flags, step, blocStep, shortage, stability, natLiv, alien,
                mil, tax, ma, lit, urb, sol, rad, nvLaw, nvGov, nvDiv, nvBl);
        }

        var cs = Measure(warmup, samples, RunManaged);
        var nv = Measure(warmup, samples, RunNative);
        bool ok = BitEqual(csLaw, nvLaw) && BitEqual(csGov, nvGov) && BitEqual(csDiv, nvDiv) && BitEqual(csBl, nvBl);
        Report($"political_cohorts ({n} states)", cs, nv, ok ? "BIT-EXACT" : "MISMATCH");
        return ok ? 0 : 1;
    }

    private static int BenchGraphWartimeKernels(int samples, int warmup)
    {
        // Build a synthetic ~3,200-province topology via ProvinceTopology.Build.
        const int gw = 1024, gh = 512;
        var states = new ushort[gw * gh];
        var control = new ushort[gw * gh];
        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++)
            {
                ushort nation = (ushort)(1 + ((x / 64) + (y / 64) * 16) % 128);
                states[y * gw + x] = nation;
                control[y * gw + x] = nation;
            }
        }
        var topo = ProvinceTopology.Build(states, control, gw, gh, static _ => false);
        var provCount = topo.ProvinceCount;
        var ptn = topo.ProvinceToNation;
        var neighbors = topo.Neighbors;
        var ctrl = (ushort[])ptn.Clone();
        for (int p = 1; p <= provCount; p++)
            if (p % 17 == 0) ctrl[p] = (ushort)(1 + (ptn[p] % 128));

        var graph = NativeProvinceGraph.ForMap(topo);
        if (graph is null)
        {
            Console.WriteLine("  FAIL: NativeProvinceGraph.ForMap returned null");
            return 1;
        }

        int failures = 0;
        // 1. Border pairs
        var csPairs = new HashSet<uint>();
        var csBorder = Measure(warmup, samples, () =>
        {
            csPairs.Clear();
            for (int p = 1; p <= provCount; p++)
            {
                ushort ow = ctrl[p];
                if (ow == 0) continue;
                var nb = neighbors[p];
                for (int k = 0; k < nb.Length; k++)
                {
                    ushort ot = ctrl[nb[k]];
                    if (ot != 0 && ot != ow) csPairs.Add(((uint)ow << 16) | ot);
                }
            }
        });
        int nvPairCount = 0;
        var nvPairs = new HashSet<uint>();
        var nvBorder = Measure(warmup, samples, () =>
        {
            if (graph.TryBorderPairs(ctrl, out var span)) nvPairCount = span.Length;
        });
        graph.TryBorderPairs(ctrl, out var checkPairs);
        for (int i = 0; i < checkPairs.Length; i++) nvPairs.Add(checkPairs[i]);
        bool borderOk = nvPairCount == csPairs.Count && csPairs.SetEquals(nvPairs);
        Report($"graph_border_pairs ({provCount:N0}p)", csBorder, nvBorder, borderOk ? "BIT-EXACT" : "MISMATCH");
        if (!borderOk) failures++;

        // 2. Occupation census
        var csHomes = new Dictionary<ushort, int>();
        var csOcc = new Dictionary<uint, int>();
        var csCensus = Measure(warmup, samples, () =>
        {
            csHomes.Clear(); csOcc.Clear();
            for (int p = 1; p <= provCount; p++)
            {
                ushort o = ptn[p];
                if (o == 0) continue;
                csHomes[o] = csHomes.GetValueOrDefault(o) + 1;
                ushort c = ctrl[p];
                if (c != 0 && c != o)
                {
                    uint k = ((uint)o << 16) | c;
                    csOcc[k] = csOcc.GetValueOrDefault(k) + 1;
                }
            }
        });
        int nvH = 0, nvO = 0;
        var nvCensus = Measure(warmup, samples, () =>
        {
            if (graph.TryOccupationCensus(ptn, ctrl, provCount, out var hk, out _, out var op, out _))
            { nvH = hk.Length; nvO = op.Length; }
        });
        bool occOk = nvH == csHomes.Count && nvO == csOcc.Count;
        if (occOk && graph.TryOccupationCensus(ptn, ctrl, provCount, out var hk2, out var hc2, out var op2, out var oc2))
        {
            for (int i = 0; i < hk2.Length; i++)
                if (csHomes.GetValueOrDefault(hk2[i]) != hc2[i]) { occOk = false; break; }
            for (int i = 0; occOk && i < op2.Length; i++)
                if (csOcc.GetValueOrDefault(op2[i]) != oc2[i]) { occOk = false; break; }
        }
        Report($"graph_occupation ({provCount:N0}p)", csCensus, nvCensus, occOk ? "BIT-EXACT" : "MISMATCH");
        if (!occOk) failures++;

        // 3. Seed territory vision (owned = ~1,200 provinces, depth = 2)
        var ownedList = new List<int>();
        for (int p = 1; p <= provCount; p++) if (ctrl[p] <= 10) ownedList.Add(p);
        var ownedArr = ownedList.ToArray();
        var csReach = new byte[provCount + 1];
        var nvReach = new byte[provCount + 1];
        var q = new Queue<int>();
        var csVis = Measure(warmup, samples, () =>
        {
            Array.Clear(csReach); q.Clear();
            const byte ownLvl = 3;
            for (int i = 0; i < ownedArr.Length; i++) csReach[ownedArr[i]] = ownLvl;
            for (int i = 0; i < ownedArr.Length; i++)
            {
                int p = ownedArr[i];
                var nb = neighbors[p];
                for (int k = 0; k < nb.Length; k++)
                    if (csReach[nb[k]] < ownLvl) { q.Enqueue(p); break; }
            }
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                int nextLvl = csReach[u] - 1;
                if (nextLvl < 1) continue;
                var nb = neighbors[u];
                for (int k = 0; k < nb.Length; k++)
                {
                    int v = nb[k];
                    if (v > 0 && csReach[v] < nextLvl) { csReach[v] = (byte)nextLvl; q.Enqueue(v); }
                }
            }
        });
        var nvVis = Measure(warmup, samples, () => graph.TrySeedTerritoryVision(ownedArr, 2, nvReach));
        bool visOk = csReach.AsSpan().SequenceEqual(nvReach);
        Report($"graph_vision_seed ({ownedArr.Length} own)", csVis, nvVis, visOk ? "BIT-EXACT" : "MISMATCH");
        if (!visOk) failures++;

        return failures;
    }

    private static int BenchTradeAffinityAndEconCombat(int samples, int warmup)
    {
        int failures = 0;
        var rng = new Random(909);

        // 1. Trade ComputeAffinity SIMD (160x160 = 25,600 pairs)
        const int bCnt = 160;
        const int sCnt = 160;
        var campaign = new Campaign(909);
        var arena = NativeTradeArena.ForCampaign(campaign, 512);
        var bIds = new int[bCnt]; var sIds = new int[sCnt];
        var bMap = new ushort[bCnt]; var sMap = new ushort[sCnt];
        var bReach = new float[bCnt]; var sReach = new float[sCnt];
        var dist = new float[bCnt * sCnt];
        var op = new float[bCnt * sCnt];
        var st = new byte[bCnt * sCnt];
        var fl = new byte[bCnt * sCnt];
        var csAff = new float[bCnt * sCnt];
        var nvAff = new float[bCnt * sCnt];
        for (int i = 0; i < bCnt; i++)
        {
            bIds[i] = i; sIds[i] = i;
            bMap[i] = (ushort)(i + 1); sMap[i] = (ushort)(i + 1);
            bReach[i] = 0.45f + (float)(rng.NextDouble() * 1.05);
            sReach[i] = 0.45f + (float)(rng.NextDouble() * 1.05);
        }
        for (int b = 0; b < bCnt; b++)
        {
            for (int s = 0; s < sCnt; s++)
            {
                int idx = b * sCnt + s;
                dist[idx] = (idx % 11 == 0) ? 0f : (float)(150 + rng.NextDouble() * 9000);
                op[idx] = (float)(rng.NextDouble() * 320 - 160);
                st[idx] = (byte)(rng.Next(0, 3));
                byte f = (byte)(rng.Next(0, 3) != 0 ? 1 : 0);
                if (rng.Next(0, 16) == 0) f |= 2;
                if (rng.Next(0, 4) == 0) f |= 8;
                if (rng.Next(0, 8) == 0) f |= 4;
                if (rng.Next(0, 7) == 0) f |= 16;
                if (b == s) f |= 32;
                fl[idx] = f;
            }
        }
        void ManagedAff()
        {
            for (int b = 0; b < bCnt; b++)
            {
                float br = bReach[b];
                int row = b * sCnt;
                for (int s = 0; s < sCnt; s++)
                {
                    int idx = row + s;
                    byte f = fl[idx];
                    if ((f & 32) != 0) { csAff[idx] = 0f; continue; }
                    if ((f & 2) != 0) { csAff[idx] = -1f; continue; }
                    float sc = 1f;
                    if ((f & 1) != 0)
                    {
                        sc *= M.Clamp(1f + op[idx] / 160f, 0.35f, 1.65f);
                        if (st[idx] == 1) sc *= 1.30f;
                        else if (st[idx] == 2) sc *= 0.68f;
                    }
                    if ((f & 8) != 0) sc *= (f & 4) != 0 ? 1.35f : 0.85f;
                    if ((f & 16) == 0)
                    {
                        float d = dist[idx] > 0f ? dist[idx] : 12000f;
                        float span = d / 4200f;
                        float falloff = 1f / (1f + span * span);
                        float reach = (br + sReach[s]) * 0.5f;
                        sc *= falloff * reach;
                    }
                    csAff[idx] = sc;
                }
            }
        }
        var csA = Measure(warmup, samples, ManagedAff);
        var nvA = Measure(warmup, samples, () => arena!.TryComputeAffinity(
            bIds, bMap, bReach, sIds, sMap, sReach, dist, sCnt, sCnt,
            op, st, fl, sCnt, 4200f, 1.35f, 0.85f, nvAff, sCnt));
        ManagedAff();
        arena!.TryComputeAffinity(bIds, bMap, bReach, sIds, sMap, sReach, dist, sCnt, sCnt,
            op, st, fl, sCnt, 4200f, 1.35f, 0.85f, nvAff, sCnt);
        bool affOk = true;
        for (int i = 0; i < csAff.Length; i++)
            if (BitConverter.SingleToInt32Bits(csAff[i]) != BitConverter.SingleToInt32Bits(nvAff[i]))
            { affOk = false; break; }
        Report($"trade_affinity ({bCnt}x{sCnt})", csA, nvA, affOk ? "BIT-EXACT" : "MISMATCH");
        if (!affOk) failures++;

        // 2. CombatLineProfile + CombatSideTotals (N=64)
        const int cN = 64;
        var div = new float[cN]; var hard = new float[cN]; var arm = new float[cN];
        var prc = new float[cN]; var sup = new float[cN]; var rec = new float[cN];
        var rpow = new float[cN]; var off = new float[cN]; var atr = new float[cN];
        var ast = new float[cN]; var ar = new float[cN]; var mun = new float[cN];
        var enc = new float[cN]; var spr = new float[cN]; var ca = new float[cN];
        var ds = new float[cN]; var dtr = new float[cN]; var dst = new float[cN];
        var dr = new float[cN]; var cd = new float[cN]; var ent = new float[cN];
        for (int i = 0; i < cN; i++)
        {
            div[i] = i == 0 ? 0.001f : (float)(0.5 + rng.NextDouble() * 8.0);
            hard[i] = (float)rng.NextDouble(); arm[i] = (float)(rng.NextDouble() * 80);
            prc[i] = (float)(rng.NextDouble() * 80); sup[i] = (float)(rng.NextDouble() * 1.2 - 0.1);
            rec[i] = (float)rng.NextDouble();
            rpow[i] = i == 3 ? 0.002f : (float)(0.5 + rng.NextDouble() * 10);
            off[i] = (float)(5 + rng.NextDouble() * 30); atr[i] = (float)(0.8 + rng.NextDouble() * 0.4);
            ast[i] = (float)(0.85 + rng.NextDouble() * 0.3); ar[i] = (float)(0.9 + rng.NextDouble() * 0.25);
            mun[i] = (float)(0.6 + rng.NextDouble() * 0.4); enc[i] = (float)(0.7 + rng.NextDouble() * 0.3);
            spr[i] = (float)(0.9 + rng.NextDouble() * 0.2); ca[i] = (float)(0.9 + rng.NextDouble() * 0.2);
            ds[i] = (float)(5 + rng.NextDouble() * 30); dtr[i] = (float)(0.8 + rng.NextDouble() * 0.4);
            dst[i] = (float)(0.85 + rng.NextDouble() * 0.3); dr[i] = (float)(0.9 + rng.NextDouble() * 0.25);
            cd[i] = (float)(0.9 + rng.NextDouble() * 0.2); ent[i] = (float)rng.NextDouble();
        }
        float ch = 0, caOut = 0, cp = 0, csOut = 0, cr = 0;
        float nh = 0, na = 0, np = 0, ns = 0, nr = 0;
        var csLp = Measure(warmup, samples, () => NativeCombatSupportKernels.ManagedCombatLineProfile(div, hard, arm, prc, sup, rec, out ch, out caOut, out cp, out csOut, out cr));
        var nvLp = Measure(warmup, samples, () => NativeCombatSupportKernels.TryNativeCombatLineProfile(div, hard, arm, prc, sup, rec, out nh, out na, out np, out ns, out nr));
        bool lpOk = BitConverter.SingleToInt32Bits(ch) == BitConverter.SingleToInt32Bits(nh) &&
                    BitConverter.SingleToInt32Bits(caOut) == BitConverter.SingleToInt32Bits(na) &&
                    BitConverter.SingleToInt32Bits(cp) == BitConverter.SingleToInt32Bits(np) &&
                    BitConverter.SingleToInt32Bits(csOut) == BitConverter.SingleToInt32Bits(ns) &&
                    BitConverter.SingleToInt32Bits(cr) == BitConverter.SingleToInt32Bits(nr);
        Report($"combat_line_profile (N={cN})", csLp, nvLp, lpOk ? "BIT-EXACT" : "MISMATCH");
        if (!lpOk) failures++;

        var csTot = new double[9]; var nvTot = new double[9];
        var csSt = Measure(warmup, samples, () => NativeCombatSupportKernels.ManagedCombatSideTotals(rpow, off, atr, ast, ar, mun, enc, spr, ca, ds, dtr, dst, dr, cd, ent, true, csTot));
        var nvSt = Measure(warmup, samples, () => NativeCombatSupportKernels.TryNativeCombatSideTotals(rpow, off, atr, ast, ar, mun, enc, spr, ca, ds, dtr, dst, dr, cd, ent, true, nvTot));
        bool stOk = true;
        for (int k = 0; k < 9; k++)
            if (BitConverter.DoubleToInt64Bits(csTot[k]) != BitConverter.DoubleToInt64Bits(nvTot[k]))
            { stOk = false; break; }
        Report($"combat_side_totals (N={cN})", csSt, nvSt, stOk ? "BIT-EXACT" : "MISMATCH");
        if (!stOk) failures++;

        // 3. SupportAttritionBatch + SupportReachBatch (N=128)
        const int sN = 128;
        var th = new float[sN]; var mf = new float[sN];
        var str1 = new float[sN]; var str2 = new float[sN];
        var exp1 = new float[sN]; var exp2 = new float[sN];
        var loss1 = new float[sN]; var loss2 = new float[sN];
        var bLat = new float[sN]; var bLon = new float[sN]; var rk = new float[sN];
        var rw1 = new float[sN]; var rw2 = new float[sN];
        for (int i = 0; i < sN; i++)
        {
            th[i] = i % 5 == 0 ? 0f : (float)(rng.NextDouble() * 15);
            mf[i] = 0.8f + (float)(rng.NextDouble() * 0.4);
            str1[i] = str2[i] = (float)(0.2 + rng.NextDouble() * 0.8);
            exp1[i] = exp2[i] = (float)(rng.NextDouble() * 0.8);
            bLat[i] = (float)(35 + rng.NextDouble() * 25);
            bLon[i] = (float)(10 + rng.NextDouble() * 30);
            rk[i] = 400f + i * 8f;
        }
        var csAt = Measure(warmup, samples, () => NativeCombatSupportKernels.ManagedSupportAttritionBatch(th, mf, 0.12f, 8f, 0.1f, str1, exp1, loss1));
        var nvAt = Measure(warmup, samples, () => NativeCombatSupportKernels.TryNativeSupportAttritionBatch(th, mf, 0.12f, 8f, 0.1f, str2, exp2, loss2));
        for (int i = 0; i < sN; i++) { str1[i] = str2[i] = 0.8f; exp1[i] = exp2[i] = 0.25f; }
        NativeCombatSupportKernels.ManagedSupportAttritionBatch(th, mf, 0.12f, 8f, 0.1f, str1, exp1, loss1);
        NativeCombatSupportKernels.TryNativeSupportAttritionBatch(th, mf, 0.12f, 8f, 0.1f, str2, exp2, loss2);
        bool atOk = true;
        for (int i = 0; i < sN; i++)
            if (BitConverter.SingleToInt32Bits(str1[i]) != BitConverter.SingleToInt32Bits(str2[i]) ||
                BitConverter.SingleToInt32Bits(exp1[i]) != BitConverter.SingleToInt32Bits(exp2[i]) ||
                BitConverter.SingleToInt32Bits(loss1[i]) != BitConverter.SingleToInt32Bits(loss2[i]))
            { atOk = false; break; }
        Report($"support_attrition (N={sN})", csAt, nvAt, atOk ? "BIT-EXACT" : "MISMATCH");
        if (!atOk) failures++;

        var csRc = Measure(warmup, samples, () => NativeCombatSupportKernels.ManagedSupportReachBatch(48f, 20f, bLat, bLon, rk, str1, exp1, rw1));
        var nvRc = Measure(warmup, samples, () => NativeCombatSupportKernels.TryNativeSupportReachBatch(48f, 20f, bLat, bLon, rk, str1, exp1, rw2));
        bool rcOk = true;
        for (int i = 0; i < sN; i++)
            if (MathF.Abs(rw1[i] - rw2[i]) > 1e-5f) { rcOk = false; break; }
        Report($"support_reach (N={sN})", csRc, nvRc, rcOk ? "MATCH" : "MISMATCH");
        if (!rcOk) failures++;

        // 4. EconStateWeekPost (N=700)
        const int eN = 700;
        var nSol = new float[eN]; var wRad = new float[eN]; var cRad = new float[eN];
        var sSol1 = new float[eN]; var sSol2 = new float[eN];
        var sRad1 = new float[eN]; var sRad2 = new float[eN];
        for (int i = 0; i < eN; i++)
        {
            nSol[i] = (float)rng.NextDouble();
            wRad[i] = (float)(rng.NextDouble() * 0.04 - 0.02);
            cRad[i] = (float)(rng.NextDouble() * 0.01);
            sSol1[i] = sSol2[i] = (float)rng.NextDouble();
            sRad1[i] = sRad2[i] = (float)rng.NextDouble();
        }
        var csEc = Measure(warmup, samples, () => NativeCombatSupportKernels.ManagedEconStateWeekPost(nSol, wRad, cRad, sSol1, sRad1));
        var nvEc = Measure(warmup, samples, () => NativeCombatSupportKernels.TryNativeEconStateWeekPost(nSol, wRad, cRad, sSol2, sRad2));
        for (int i = 0; i < eN; i++) { sSol1[i] = sSol2[i] = 0.5f; sRad1[i] = sRad2[i] = 0.25f; }
        NativeCombatSupportKernels.ManagedEconStateWeekPost(nSol, wRad, cRad, sSol1, sRad1);
        NativeCombatSupportKernels.TryNativeEconStateWeekPost(nSol, wRad, cRad, sSol2, sRad2);
        bool ecOk = true;
        for (int i = 0; i < eN; i++)
            if (BitConverter.SingleToInt32Bits(sSol1[i]) != BitConverter.SingleToInt32Bits(sSol2[i]) ||
                BitConverter.SingleToInt32Bits(sRad1[i]) != BitConverter.SingleToInt32Bits(sRad2[i]))
            { ecOk = false; break; }
        Report($"econ_state_week_post (N={eN})", csEc, nvEc, ecOk ? "BIT-EXACT" : "MISMATCH");
        if (!ecOk) failures++;

        return failures;
    }
}
