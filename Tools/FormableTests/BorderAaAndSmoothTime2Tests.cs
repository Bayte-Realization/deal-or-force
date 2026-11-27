// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using DealOrForce;
using DealOrForce.Simulation;

/// <summary>
/// Focused regression tests for Border AA smoothness (C1-continuous label_claim, subpixel 1+JFA
/// frontier field, mid-zoom detail_mix &amp; plate shade suppression, anti-block-edge cushion blend,
/// GLES3 12-sampler budget, map_smoothing=0 raw mode) and Smooth Time 2.0 (asymmetric EMA + spike
/// jitter envelope, anti-burst damping, frame-budget shield, Speed-8 bit-exact determinism parity
/// across all 4 modes, and EN/RU settings localization).
/// </summary>
public static class BorderAaAndSmoothTime2Tests
{
    private static int _passed;

    private static void Check(bool condition, string label)
    {
        if (!condition)
            throw new Exception("FAIL border-aa & smooth-time-2: " + label);
        _passed++;
        Console.WriteLine("PASS border-aa & smooth-time-2: " + label);
    }

    [TestSuite("--border-aa-smooth2-only", Order = 2905, IncludeInDefaultRun = true,
        Description = "Verifies Border AA shader/JFA/detail_mix smoothness contracts and Smooth Time 2.0 pacer, shield, determinism, and EN/RU settings.")]
    public static void Run()
    {
        _passed = 0;
        var root = FindRepoRoot();

        VerifyBorderAaShaderContracts(root);
        VerifySubpixelJfaAndMidZoomContracts(root);
        VerifyMovementLayerPerformanceContracts(root);
        VerifySmoothTime2PacerMathAndPolicies(root);
        VerifySmoothTime2DeterminismParity();
        VerifySmoothTime2SettingsAndLocalization(root);

        Console.WriteLine($"border-aa & smooth-time-2: {_passed} PASS");
    }

    private static void VerifyBorderAaShaderContracts(string root)
    {
        var shaderPath = Path.Combine(root, "game", "shaders", "globe_map.gdshader");
        var shaderSrc = File.ReadAllText(shaderPath);

        // 1. Extract label_claim() body and verify zero discontinuous step(0.48/0.52) cliff.
        var claimMatch = Regex.Match(
            shaderSrc,
            @"float\s+label_claim\s*\([^)]*\)\s*\{(?<body>[^}]+)\}",
            RegexOptions.Singleline);
        Check(claimMatch.Success, "located label_claim() in globe_map.gdshader");
        var claimBody = claimMatch.Groups["body"].Value;
        Check(!Regex.IsMatch(claimBody, @"\bstep\s*\(\s*0\.(48|52)\b"),
            "label_claim() contains zero discontinuous step(0.48/0.52) grid-snapping cliffs");
        Check(claimBody.Contains("smoothstep(vec2(0.22), vec2(0.58), top_own)", StringComparison.Ordinal) &&
              claimBody.Contains("smoothstep(vec2(0.22), vec2(0.58), bottom_own)", StringComparison.Ordinal),
            "label_claim() uses C1-continuous smoothstep(0.22, 0.58, ...) preservation");

        // Mathematical proof of the C1 preservation curve: 1-texel islands/strips survive,
        // shallow diagonal corners stay smooth (no +0.45 cliff), and 2-label sum == 1.0 identically.
        static float Smoothstep(float e0, float e1, float x)
        {
            var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
        static float Preserve(float blurred, float own)
        {
            var ownAmt = own > 0.5f ? blurred : 1f - blurred;
            var pull = 0.50f * (1f - Smoothstep(0.22f, 0.58f, ownAmt));
            return blurred + (own - blurred) * pull;
        }
        var island = Preserve(0.25f, 1f);
        var strip = Preserve(0.50f, 1f);
        var shallowCorner = Preserve(0.4375f, 1f);
        var maxSymErr = 0f;
        for (var i = 0; i <= 100; i++)
        {
            var t = i / 100f;
            var symErr = MathF.Abs(Preserve(t, 1f) + Preserve(1f - t, 0f) - 1f);
            if (symErr > maxSymErr) maxSymErr = symErr;
        }
        Check(island >= 0.58f && strip > 0.50f && shallowCorner < 0.62f && maxSymErr < 1e-6f,
            $"label_claim C1 curve preserves islands ({island:0.000}) & strips ({strip:0.000}), avoids shallow snap ({shallowCorner:0.000}), and is 2-label symmetric (err={maxSymErr:E1})");

        // 2. map_smoothing = 0 raw raster path remains intact.
        Check(shaderSrc.Contains("uniform float map_smoothing", StringComparison.Ordinal) &&
              shaderSrc.Contains("if (map_smoothing > 0.5)", StringComparison.Ordinal) &&
              shaderSrc.Contains("if (frontier_ready > 0.5 && map_smoothing > 0.5)", StringComparison.Ordinal),
            "globe_map.gdshader preserves map_smoothing = 0 raw raster bypass across boundary reconstruction and cushions");

        // 3. Anti-block-edge clipping (cushion_pixels + rebuild_reach_fade) & tuned frontier crossfade.
        Check(shaderSrc.Contains("uniform float frontier_offset = 0.72;", StringComparison.Ordinal) &&
              shaderSrc.Contains("uniform float frontier_reach = 1.65;", StringComparison.Ordinal) &&
              shaderSrc.Contains("float frontier_weight = frontier_ready * smoothstep(0.68, 1.05, id_span);", StringComparison.Ordinal) &&
              shaderSrc.Contains("id_wander_texels * (1.0 - smoothstep(0.8, 2.6, id_span))", StringComparison.Ordinal) &&
              shaderSrc.Contains("float rebuild_reach_fade = mix(1.0 - smoothstep(1.15, 1.48, nation_reach), 1.0, frontier_weight);", StringComparison.Ordinal) &&
              shaderSrc.Contains("float cushion_pixels = nation_edge_pixels;", StringComparison.Ordinal) &&
              shaderSrc.Contains("float cushion_gate = has_nation_border * (1.0 - smoothstep(1.00, 1.48, nation_reach));", StringComparison.Ordinal) &&
              shaderSrc.Contains("region_cushion *= 1.0 - smoothstep(1.00, 1.48, region_reach);", StringComparison.Ordinal),
            "globe_map.gdshader blends wide cushions smoothly via frontier_field / GLES3 reach taper with strict edge0 < edge1 smoothsteps");

        // 4. GLES3 PackedMapShader preserves exact 12-sampler budget and strips frontier_field completely.
        var packed = PackedMapShader.Build(MapColorSpaceShader.Build(shaderSrc));
        var samplerCount = Regex.Matches(packed, @"uniform\s+sampler\w+\s+").Count;
        Check(samplerCount == PackedMapShader.TextureUnits && !packed.Contains("frontier_field", StringComparison.Ordinal),
            $"PackedMapShader preserves exact {PackedMapShader.TextureUnits}-sampler GLES3 budget with zero frontier_field references");
    }

    private static void VerifySubpixelJfaAndMidZoomContracts(string root)
    {
        var gpuSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GpuMapCompute.cs"));
        Check(gpuSrc.Contains("int round_div(int num, int den)", StringComparison.Ordinal) &&
              gpuSrc.Contains("int sx = int(x) * 8 + 4 + round_div(sx_sum, cnt);", StringComparison.Ordinal) &&
              gpuSrc.Contains("int w8 = int(params.width) * 8;", StringComparison.Ordinal) &&
              gpuSrc.Contains("max(1.0, sqrt(fdx * fdx + fdy * fdy) * 0.25)", StringComparison.Ordinal) &&
              gpuSrc.Contains("max(0.0, sum - 0.55 * (1.0 - smoothstep(1.45, 4.5, sum)))", StringComparison.Ordinal),
            "GpuMapCompute JFA uses quarter-cell subpixel centroid seeding, antimeridian wrap, 1.0 anti-pulsation floor, and smooth floor-bias removal");

        // Verify the 7-tap Gaussian kernel weights in JfaBlurHGlsl sum to 1.0 cleanly.
        var weightsMatch = Regex.Match(gpuSrc, @"const\s+float\s+W\[7\]\s*=\s*float\[7\]\s*\(([^)]+)\)", RegexOptions.Singleline);
        Check(weightsMatch.Success, "found 7-tap Gaussian kernel W[7] in GpuMapCompute");
        var weights = weightsMatch.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();
        var weightSum = weights.Sum();
        Check(weights.Length == 7 && Math.Abs(weightSum - 1.0) < 1e-4 &&
              Math.Abs(weights[0] - weights[6]) < 1e-6 &&
              Math.Abs(weights[1] - weights[5]) < 1e-6 &&
              Math.Abs(weights[2] - weights[4]) < 1e-6,
            $"JFA 7-tap Gaussian kernel is symmetric and normalized (sum={weightSum:0.000000})");

        // Verify baked dark plate border rectangles are suppressed when GPU frontier field is active.
        Check(gpuSrc.Contains("var countryShade = _hasFrontier ? shades.Inland : shades.Country;", StringComparison.Ordinal) &&
              gpuSrc.Contains("var regionShade = _hasFrontier ? shades.Inland : shades.Region;", StringComparison.Ordinal),
            "GpuMapCompute suppresses baked 3x3 plate border rectangles when smooth GPU frontier field is active");

        // Verify mid-zoom detailMix reaches 1.0 throughout Europe/regional zoom (d <= 15.5 globe, flatZoom <= 9.5 flat).
        var globeSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.cs"));
        Check(globeSrc.Contains("Mathf.Clamp((13.5f - _flatZoom) / 4.0f, 0.15f, 1f)", StringComparison.Ordinal) &&
              globeSrc.Contains("Mathf.Clamp((21f - _distance) / 5.5f, 0.12f, 1f)", StringComparison.Ordinal),
            "GlobePresentation keeps detail_mix == 1.0 across Europe/regional zoom (globe d <= 15.5, flatZoom <= 9.5)");

        var plateSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePlateRepaint.cs"));
        Check(plateSrc.Contains("5-pass subpixel 1+JFA", StringComparison.Ordinal),
            "GlobePlateRepaint reports 5-pass subpixel 1+JFA GPU frontier summary");
    }

    private static void VerifyMovementLayerPerformanceContracts(string root)
    {
        var movSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeMovements.cs"));
        Check(movSrc.Contains("_globe.ViewDistance > MedallionDistance", StringComparison.Ordinal) &&
              movSrc.Contains("HideLayer();", StringComparison.Ordinal),
            "GlobeMovements fast-exits zoomed-out frames without acquiring read lock or rebuilding");
        Check(movSrc.Contains("FastSimSyncSeconds = 0.30", StringComparison.Ordinal) &&
              movSrc.Contains("CurrentRenderSnapshot()", StringComparison.Ordinal) &&
              movSrc.Contains("(!fastSim && _sinceLastMovementSync >= 0.25)", StringComparison.Ordinal) &&
              movSrc.Contains("TryEnterRead(0, requestWindowOnMiss: requestWindow)", StringComparison.Ordinal),
            "GlobeMovements reads clock lock-free, throttles Speed 7-8 syncs, and never requests read windows on routine fast-sim misses");
        Check(movSrc.Contains("_markersByTag", StringComparison.Ordinal) &&
              movSrc.Contains("PruneStaleMarkers()", StringComparison.Ordinal) &&
              movSrc.Contains("MaxCachedRoundels = 256", StringComparison.Ordinal) &&
              movSrc.Contains("MaxRoundelsPerFrame = 3", StringComparison.Ordinal) &&
              movSrc.Contains("RoundelBudgetMs = 1.5", StringComparison.Ordinal) &&
              movSrc.Contains("FlushPendingRoundels()", StringComparison.Ordinal) &&
              movSrc.Contains("using var image = flag.GetImage();", StringComparison.Ordinal) &&
              movSrc.Contains("LinearWithMipmapsAnisotropic", StringComparison.Ordinal) &&
              movSrc.Contains("GenerateMipmaps()", StringComparison.Ordinal),
            "GlobeMovements reuses marker nodes in place, bounds roundel cache to 256, budgets Lanczos resizes per frame, and preserves mipmapped anisotropic filtering");
    }

    private static void VerifySmoothTime2PacerMathAndPolicies(string root)
    {
        // 1. Mode parsing & legacy smooth_ticks master-toggle compatibility.
        Check(SmoothTimePacer.ParseMode("adaptive") == SmoothTimeMode.Adaptive &&
              SmoothTimePacer.ParseMode("cinema_60fps") == SmoothTimeMode.Cinema60Fps &&
              SmoothTimePacer.ParseMode("max_throughput") == SmoothTimeMode.MaxThroughput &&
              SmoothTimePacer.ParseMode("off") == SmoothTimeMode.Off &&
              SmoothTimePacer.ParseMode("unknown_future_value") == SmoothTimeMode.Adaptive,
            "SmoothTimePacer.ParseMode resolves all 4 profiles and falls back unknown values to Adaptive");
        Check(SmoothTimePacer.ResolveMode(smoothTicksEnabled: false, "adaptive") == SmoothTimeMode.Off &&
              SmoothTimePacer.ResolveMode(smoothTicksEnabled: false, "cinema_60fps") == SmoothTimeMode.Off &&
              SmoothTimePacer.ResolveMode(smoothTicksEnabled: true, "cinema_60fps") == SmoothTimeMode.Cinema60Fps,
            "SmoothTimePacer.ResolveMode keeps legacy smooth_ticks=false master override intact");

        // 2. Asymmetric EMA + spike-jitter envelope + anti-burst damping across weekly Year-2070 spikes.
        var pacer = new SmoothTimePacer();
        Check(pacer.ComputeTargetDayIntervalMs(SmoothTimeMode.Off, 8, false, 0.0) == 0.0 &&
              pacer.ComputeTargetDayIntervalMs(SmoothTimeMode.MaxThroughput, 8, false, 0.0) == 0.0 &&
              pacer.ComputeTargetDayIntervalMs(SmoothTimeMode.Adaptive, 7, false, 0.0) == 0.0,
            "ComputeTargetDayIntervalMs returns 0 for Off/MaxThroughput (uncapped) and non-Speed-8");

        for (var week = 0; week < 4; week++)
        {
            for (var d = 0; d < 6; d++) pacer.ObserveStepWork(8.0, openedNewDay: true);
            pacer.ObserveStepWork(75.0, openedNewDay: true); // heavy weekly economy/AI/movements spike
        }
        var adaptDeskTarget = pacer.ComputeTargetDayIntervalMs(SmoothTimeMode.Adaptive, 8, mobilePacing: false, 0.0);
        var cinemaDeskTarget = pacer.ComputeTargetDayIntervalMs(SmoothTimeMode.Cinema60Fps, 8, mobilePacing: false, 0.0);
        var adaptMobTarget = pacer.ComputeTargetDayIntervalMs(SmoothTimeMode.Adaptive, 8, mobilePacing: true, 0.0);
        var cinemaMobTarget = pacer.ComputeTargetDayIntervalMs(SmoothTimeMode.Cinema60Fps, 8, mobilePacing: true, 0.0);
        Check(pacer.EmaWorkMs > 10.0 && pacer.EmaJitterMs > 5.0 &&
              adaptDeskTarget is >= 14.0 and <= 45.0 &&
              cinemaDeskTarget is >= 24.0 and <= 75.0 &&
              cinemaDeskTarget > adaptDeskTarget &&
              adaptMobTarget is >= 22.0 and <= 60.0 &&
              cinemaMobTarget is >= 34.0 and <= 95.0,
            $"EMA + jitter tracks weekly spikes cleanly (ema={pacer.EmaWorkMs:0.0}ms, jitter={pacer.EmaJitterMs:0.0}ms, adapt={adaptDeskTarget:0.0}ms, cinema={cinemaDeskTarget:0.0}ms)");

        // Anti-burst check: a 90ms weekly spike must NOT cause the following light 6ms day to fire with 0 wait.
        var waitAfterSpike = pacer.ComputePostDayWaitMs(SmoothTimeMode.Adaptive, 8, false, 0.0, elapsedSinceLastDayMs: 90.0);
        var waitNextLightDay = pacer.ComputePostDayWaitMs(SmoothTimeMode.Adaptive, 8, false, 0.0, elapsedSinceLastDayMs: 6.0);
        Check(waitAfterSpike == 0.0 && waitNextLightDay >= adaptDeskTarget * 0.5 - 6.0 && waitNextLightDay > 0.0,
            $"anti-burst debt prevents catch-up bursts after 90ms spike (spikeWait={waitAfterSpike:0.0}ms, nextLightWait={waitNextLightDay:0.0}ms)");

        pacer.Reset();
        Check(pacer.EmaWorkMs == 0.0 && pacer.EmaJitterMs == 0.0, "SmoothTimePacer.Reset clears EMA work and jitter");

        // 3. Accumulator capping & damping across modes.
        const double stepSec = 0.05;
        var debtCinema = SmoothTimePacer.AccumulateSmoothDebt(0.0, 0.50, stepSec, 24, SmoothTimeMode.Cinema60Fps, 7);
        var debtAdaptive = SmoothTimePacer.AccumulateSmoothDebt(0.0, 0.50, stepSec, 24, SmoothTimeMode.Adaptive, 7);
        var debtOff = SmoothTimePacer.AccumulateSmoothDebt(0.0, 0.50, stepSec, 24, SmoothTimeMode.Off, 7);
        Check(debtCinema <= stepSec * 1.02 + 1e-9 &&
              debtAdaptive <= stepSec * 1.12 + 1e-9 &&
              debtCinema < debtAdaptive && debtAdaptive < debtOff,
            $"AccumulateSmoothDebt clamps multi-day catch-up debt (cinema={debtCinema:0.000}s < adaptive={debtAdaptive:0.000}s < off={debtOff:0.000}s)");

        var dampedCinema = SmoothTimePacer.DampWorkerAccumulator(0.04, stepSec, stepWorkSeconds: 0.09, continuedStep: false, SmoothTimeMode.Cinema60Fps, 7);
        var dampedAdaptive = SmoothTimePacer.DampWorkerAccumulator(0.04, stepSec, stepWorkSeconds: 0.09, continuedStep: false, SmoothTimeMode.Adaptive, 7);
        Check(dampedCinema == 0.0 && dampedAdaptive <= stepSec * 0.20 + 1e-9,
            $"DampWorkerAccumulator absorbs heavy step overshoot (cinema={dampedCinema:0.000}s, adaptive={dampedAdaptive:0.000}s)");

        // 4. Policy monotonicity across Cinema60Fps <= Adaptive <= MaxThroughput <= Off.
        foreach (var mobile in new[] { false, true })
        {
            var wc = SmoothTimePacer.WorkerPolicy(SmoothTimeMode.Cinema60Fps, 8, mobile);
            var wa = SmoothTimePacer.WorkerPolicy(SmoothTimeMode.Adaptive, 8, mobile);
            var wm = SmoothTimePacer.WorkerPolicy(SmoothTimeMode.MaxThroughput, 8, mobile);
            var wo = SmoothTimePacer.WorkerPolicy(SmoothTimeMode.Off, 8, mobile);
            Check(wc.StageSliceMs <= wa.StageSliceMs && wa.StageSliceMs <= wm.StageSliceMs && wm.StageSliceMs <= wo.StageSliceMs &&
                  wc.WriteSliceMs <= wa.WriteSliceMs && wa.WriteSliceMs <= wm.WriteSliceMs && wm.WriteSliceMs <= wo.WriteSliceMs &&
                  wc.ReaderYieldAfterMs <= wa.ReaderYieldAfterMs && wa.ReaderYieldAfterMs <= wm.ReaderYieldAfterMs && wm.ReaderYieldAfterMs <= wo.ReaderYieldAfterMs &&
                  wc.MaxBatchSteps == 1 && wa.MaxBatchSteps == 1 && wm.MaxBatchSteps > 1 && wo.MaxBatchSteps >= wm.MaxBatchSteps &&
                  wc.LowerWorkerPriority && wa.LowerWorkerPriority && !wm.LowerWorkerPriority && !wo.LowerWorkerPriority &&
                  wc.MapChangeCoalesceMs > 0.0 && wa.MapChangeCoalesceMs > 0.0 &&
                  wc.HudSnapshotMinIntervalMs > 0.0 && wa.HudSnapshotMinIntervalMs > 0.0,
                $"WorkerPolicy speed-8 monotonicity holds (mobile={mobile})");

            var mc = SmoothTimePacer.MainThreadPolicy(SmoothTimeMode.Cinema60Fps, 8, mobile);
            var ma = SmoothTimePacer.MainThreadPolicy(SmoothTimeMode.Adaptive, 8, mobile);
            var mm = SmoothTimePacer.MainThreadPolicy(SmoothTimeMode.MaxThroughput, 8, mobile);
            var mo = SmoothTimePacer.MainThreadPolicy(SmoothTimeMode.Off, 8, mobile);
            Check(mc.FrameShieldBudgetMs > 0.0 && mc.FrameShieldBudgetMs < ma.FrameShieldBudgetMs &&
                  ma.FrameShieldBudgetMs < mm.FrameShieldBudgetMs && mo.FrameShieldBudgetMs == 0.0 &&
                  mc.LiveReadIntervalMs >= ma.LiveReadIntervalMs && ma.LiveReadIntervalMs >= mm.LiveReadIntervalMs &&
                  mc.FastHudRefreshGapMs >= ma.FastHudRefreshGapMs && ma.FastHudRefreshGapMs >= mm.FastHudRefreshGapMs &&
                  mc.MaxDeferredFencedFrames >= ma.MaxDeferredFencedFrames && ma.MaxDeferredFencedFrames >= mm.MaxDeferredFencedFrames && mo.MaxDeferredFencedFrames == 0 &&
                  mc.CountryNameRefreshDays == 7 && ma.CountryNameRefreshDays == 7 && mo.CountryNameRefreshDays == 1,
                $"MainThreadPolicy frame-budget shield & cadence hierarchy holds (mobile={mobile})");

            var defines = new Defines();
            var bc = SmoothTimePacer.SyncFrameBudgetMs(defines, 8, SmoothTimeMode.Cinema60Fps, mobile);
            var ba = SmoothTimePacer.SyncFrameBudgetMs(defines, 8, SmoothTimeMode.Adaptive, mobile);
            var bm = SmoothTimePacer.SyncFrameBudgetMs(defines, 8, SmoothTimeMode.MaxThroughput, mobile);
            var bo = SmoothTimePacer.SyncFrameBudgetMs(defines, 8, SmoothTimeMode.Off, mobile);
            Check(bc > 0.0 && bc <= ba && ba <= bm && bm <= bo,
                $"SyncFrameBudgetMs speed-8 ordering holds (mobile={mobile}: {bc:0.##} <= {ba:0.##} <= {bm:0.##} <= {bo:0.##} ms)");
        }

        Check(SmoothTimePacer.ShouldYieldAfterHeavyStep(SmoothTimeMode.Cinema60Fps, 8) &&
              SmoothTimePacer.ShouldYieldAfterHeavyStep(SmoothTimeMode.Adaptive, 8) &&
              !SmoothTimePacer.ShouldYieldAfterHeavyStep(SmoothTimeMode.Off, 8) &&
              SmoothTimePacer.SyncMaxStepsPerFrame(new Defines(), SmoothTimeMode.Cinema60Fps, 8) == 1 &&
              SmoothTimePacer.SyncMaxStepsPerFrame(new Defines(), SmoothTimeMode.Adaptive, 8) <= 2,
            "heavy-step yield and per-frame step caps protect 60 FPS in Cinema60Fps and Adaptive");

        // 5. Pure simulation purity + Android/iOS GCSettings guard + .cs.uid sidecar.
        var pacerPath = Path.Combine(root, "Scripts", "Simulation", "SmoothTimePacer.cs");
        var pacerSrc = File.ReadAllText(pacerPath);
        Check(File.Exists(pacerPath + ".uid") &&
              !pacerSrc.Contains("using Godot", StringComparison.Ordinal) &&
              !pacerSrc.Contains("Godot.", StringComparison.Ordinal) &&
              pacerSrc.Contains("OperatingSystem.IsAndroid()", StringComparison.Ordinal) &&
              pacerSrc.Contains("OperatingSystem.IsIOS()", StringComparison.Ordinal) &&
              pacerSrc.Contains("PlatformNotSupportedException", StringComparison.Ordinal),
            "SmoothTimePacer.cs is pure C# with .cs.uid sidecar and Android/iOS + PlatformNotSupportedException GCSettings guards");
    }

    private static void VerifySmoothTime2DeterminismParity()
    {
        const int seed = 98871;
        // 1. BackgroundSimRunner Speed-8 bit-exact CampaignFingerprint parity across all 4 SmoothTimeMode values.
        foreach (var mode in new[] { SmoothTimeMode.Off, SmoothTimeMode.Adaptive, SmoothTimeMode.Cinema60Fps, SmoothTimeMode.MaxThroughput })
        {
            var cBg = Campaign.CreateBootstrap(seed);
            cBg.IsPaused = false;
            cBg.Speed = 8;
            using var runner = new BackgroundSimRunner
            {
                SmoothMode = mode,
                MobilePacing = false,
                AdaptiveStepPacing = true,
                MinSpeed5DayIntervalMs = 0.0
            };
            runner.Start(cBg);
            runner.SyncClockState(paused: false, speed: 8);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6000)
            {
                if (cBg.TryEnterRead(5, requestWindowOnMiss: true))
                {
                    var done = cBg.ElapsedHours >= 48 && !cBg.MidStep;
                    cBg.ExitRead();
                    if (done) break;
                }
                Thread.Sleep(2);
            }

            runner.RunWrite(c =>
            {
                c.IsPaused = true;
                if (c.MidStep) c.FinishStep();
            });
            runner.Stop();

            var cRef = Campaign.CreateBootstrap(seed);
            cRef.IsPaused = false;
            cRef.Speed = 8;
            cRef.RuntimeStepHoursOverride = BackgroundSimRunner.AdaptiveStepHours(8);
            while (cRef.ElapsedHours < cBg.ElapsedHours)
                cRef.Tick();
            cRef.RuntimeStepHoursOverride = 0;

            var bgFp = CampaignFingerprint.Of(cBg);
            var refFp = CampaignFingerprint.Of(cRef);
            Check(cBg.ElapsedHours >= 48 && cBg.ElapsedHours == cRef.ElapsedHours && bgFp == refFp,
                $"BackgroundSimRunner Speed-8 SmoothMode={mode} matches synchronous Campaign.Tick fingerprint bit-for-bit at {cBg.ElapsedHours}h (0x{bgFp:X16})");
        }

        // 2. SimulationFrameClock with frame-budget shield (spentElsewhereMs deferral) matches unbounded Campaign.Tick.
        var cWhole = Campaign.CreateBootstrap(seed + 1);
        cWhole.Defines.HoursPerTick = 24;
        cWhole.IsPaused = false;
        cWhole.Tick();
        cWhole.Tick();
        var expectedFp = CampaignFingerprint.Of(cWhole);
        var expectedHours = cWhole.ElapsedHours;

        foreach (var mode in new[] { SmoothTimeMode.Adaptive, SmoothTimeMode.Cinema60Fps })
        {
            var cClock = Campaign.CreateBootstrap(seed + 1);
            cClock.Defines.HoursPerTick = 24;
            cClock.IsPaused = false;
            var clock = new SimulationFrameClock();
            var deferredFrames = 0;
            var frames = 0;
            while ((cClock.ElapsedHours < expectedHours || cClock.MidStep) && frames < 5000)
            {
                // Alternate a heavy UI frame (triggers frame-budget shield deferral) with normal frames.
                var spentUiMs = (frames % 3 == 0) ? 10.0 : 0.0;
                var finished = clock.RunFrame(
                    cClock,
                    cClock.MidStep ? 0.0 : 1.0 / 60.0,
                    speed: 8,
                    smoothMode: mode,
                    beforeCall: null,
                    afterCall: null,
                    spentElsewhereMs: spentUiMs,
                    mobileProfile: false);
                if (spentUiMs > 0.0 && finished == 0 && !cClock.MidStep) deferredFrames++;
                frames++;
            }
            var clockFp = CampaignFingerprint.Of(cClock);
            Check(cClock.ElapsedHours == expectedHours && !cClock.MidStep && clockFp == expectedFp,
                $"SimulationFrameClock Speed-8 {mode} with frame-budget shield matches unbounded Tick bit-for-bit ({frames} frames, deferrals={deferredFrames}, fp=0x{clockFp:X16})");
        }
    }

    private static void VerifySmoothTime2SettingsAndLocalization(string root)
    {
        var settingsPath = Path.Combine(root, "game", "common", "settings", "00_settings.json");
        using var settingsDoc = JsonDocument.Parse(File.ReadAllText(settingsPath), GameDataLoader.ForgivingJson);
        Check(settingsDoc.RootElement.TryGetProperty("smooth_time_mode", out var opt),
            "00_settings.json declares smooth_time_mode");
        Check(opt.GetProperty("category").GetString() == "game" &&
              opt.GetProperty("kind").GetString() == "choice" &&
              opt.GetProperty("default").GetString() == "adaptive" &&
              !opt.TryGetProperty("scope", out _),
            "smooth_time_mode is a player-scoped Game choice defaulting to adaptive (never campaign-fingerprinted)");
        var values = opt.GetProperty("values").EnumerateArray().Select(e => e.GetString()).ToArray();
        Check(values.SequenceEqual(new[] { "adaptive", "cinema_60fps", "max_throughput", "off" }),
            "smooth_time_mode exposes all 4 choices in order (adaptive, cinema_60fps, max_throughput, off)");

        var catDef = SettingsCatalog.Of("smooth_time_mode");
        Check(catDef is not null && catDef.Values.Count == 4, "SettingsCatalog loads smooth_time_mode with 4 choices");

        var enPath = Path.Combine(root, "game", "localization", "english", "zz_448_maps_and_speeds.json");
        var ruPath = Path.Combine(root, "game", "localization", "russian", "zz_448_maps_and_speeds.json");
        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var requiredKeys = new[]
        {
            "Smooth time profile",
            "smooth_mode.adaptive",
            "smooth_mode.cinema_60fps",
            "smooth_mode.max_throughput",
            "smooth_mode.off",
            "Smooth time: {0}"
        };
        foreach (var key in requiredKeys)
        {
            Check(enMap.TryGetValue(key, out var enVal) && !string.IsNullOrWhiteSpace(enVal) &&
                  ruMap.TryGetValue(key, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal) &&
                  !string.Equals(enVal, ruVal, StringComparison.Ordinal),
                $"EN & RU zz_448_maps_and_speeds.json localize '{key}' with distinct non-empty strings");
        }
        Check(enMap["Smooth time: {0}"].Contains("{0}", StringComparison.Ordinal) &&
              ruMap["Smooth time: {0}"].Contains("{0}", StringComparison.Ordinal),
            "Smooth time tooltip format preserves {0} placeholder in EN and RU");

        var savedLang = Loc.Language;
        try
        {
            foreach (var lang in new[] { "english", "russian" })
            {
                Loc.Language = lang;
                foreach (SmoothTimeMode mode in Enum.GetValues<SmoothTimeMode>())
                {
                    var text = SmoothTimePacer.FormatSpeedTooltipSuffix(mode);
                    Check(!string.IsNullOrWhiteSpace(text) && !text.Contains("smooth_mode.", StringComparison.Ordinal),
                        $"FormatSpeedTooltipSuffix({mode}) resolves cleanly in {lang} ('{text}')");
                }
            }
        }
        finally
        {
            Loc.Language = savedLang;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
