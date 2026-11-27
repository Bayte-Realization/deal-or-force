// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
}
