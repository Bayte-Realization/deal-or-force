// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Kernels;
using DealOrForce.Simulation.Shell;

/// <summary>
/// Regression and contract verification for PortAlpha 0.8.8.6 real-time smoothness,
/// lock-free presentation sync, 8 speeds, map profiles/cache scoping, tick-rarity presets,
/// modding parity, and bit-exact Rust SIMD / managed C# simulation parity.
/// </summary>
public static class RealTimeSmoothnessAndKernelParityTests
{
    private static int _passed;

    [TestSuite("--realtime-smooth-only", Order = 2900, IncludeInDefaultRun = true,
        Description = "Verifies lock-free WorldVisualState fronts/stacks, 8 speeds, map profiles, tempo presets, mod parity, and SIMD kernel parity")]
    public static void Run()
    {
        _passed = 0;
        var repoRoot = ResolveRepoRoot();

        WorldVisualStateFrontsDestroyedArmiesAndZeroAlloc();
        BackgroundSimRunnerNonBlockingClockControl();
        ImageDisposalHygieneContracts(repoRoot);
        EightSpeedScaleAndClockDifferentiation(repoRoot);
        MapCatalogSyntheticProfilesSaveAndCacheScoping();
        TickRarityTempoPresetsAndSettingsContract(repoRoot);
        ModStudioCliTemplatesAndApiParity(repoRoot);
        RustSimdCombatEconAndDiplomacyBordersParity();

        Console.WriteLine($"realtime smoothness & kernel parity: {_passed} PASS");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
            throw new Exception("FAIL realtime smoothness & kernel parity: " + label);
        _passed++;
        Console.WriteLine("PASS realtime smoothness & kernel parity: " + label);
    }

    private static void CheckBitExactFloat(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string label)
    {
        if (expected.Length != actual.Length)
            throw new Exception($"FAIL realtime smoothness & kernel parity: {label} len {actual.Length} != {expected.Length}");
        for (var i = 0; i < expected.Length; i++)
        {
            var eb = BitConverter.SingleToInt32Bits(expected[i]);
            var ab = BitConverter.SingleToInt32Bits(actual[i]);
            if (eb != ab)
            {
                if (float.IsNaN(expected[i]) && float.IsNaN(actual[i])) continue;
                throw new Exception(
                    $"FAIL realtime smoothness & kernel parity: {label}[{i}] native={actual[i]:R} (0x{ab:X8}) != csharp={expected[i]:R} (0x{eb:X8})");
            }
        }
    }

    private static void CheckBitExactDouble(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual, string label)
    {
        if (expected.Length != actual.Length)
            throw new Exception($"FAIL realtime smoothness & kernel parity: {label} len {actual.Length} != {expected.Length}");
        for (var i = 0; i < expected.Length; i++)
        {
            var eb = BitConverter.DoubleToInt64Bits(expected[i]);
            var ab = BitConverter.DoubleToInt64Bits(actual[i]);
            if (eb != ab)
            {
                if (double.IsNaN(expected[i]) && double.IsNaN(actual[i])) continue;
                throw new Exception(
                    $"FAIL realtime smoothness & kernel parity: {label}[{i}] native={actual[i]:R} (0x{ab:X16}) != csharp={expected[i]:R} (0x{eb:X16})");
            }
        }
    }

    private static string ResolveRepoRoot()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var candidate = Path.GetFullPath(Path.Combine(dataRoot, ".."));
        if (File.Exists(Path.Combine(candidate, "project.godot")))
            return candidate;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate project.godot repository root.");
    }

    private static (Campaign Campaign, Nation Alpha, Nation Beta, int AlphaBorderProv, int AlphaRearProv, int BetaBorderProv)
        CreateTwoNationWarFixture(int seed)
    {
        var campaign = new Campaign(seed) { ScenarioId = "realtime_smooth_fixture" };
        var alpha = new Nation
        {
            Id = 0,
            MapIndex = 701,
            Tag = "ALP",
            Name = "Alpha Republic",
            Treasury = 120f,
            Population = 24_000_000L,
            Gdp = 240f,
            Stability = 0.65f,
            WarSupport = 0.70f,
            ManpowerReserve = 400_000f
        };
        var beta = new Nation
        {
            Id = 1,
            MapIndex = 702,
            Tag = "BET",
            Name = "Beta Union",
            Treasury = 120f,
            Population = 24_000_000L,
            Gdp = 240f,
            Stability = 0.65f,
            WarSupport = 0.70f,
            ManpowerReserve = 400_000f
        };
        campaign.Nations.Add(alpha);
        campaign.Nations.Add(beta);

        const int cols = 8;
        const int rows = 4;
        const int block = 16;
        static int StateId(int c, int r) => 6101 + c + r * cols;

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var owner = c < cols / 2 ? alpha : beta;
                campaign.States.Add(new ScenarioState
                {
                    MapIndex = StateId(c, r),
                    NationMapIndex = owner.MapIndex,
                    NationId = owner.Tag,
                    Id = $"rt_state_{c}_{r}",
                    Name = $"State {c}-{r}",
                    Population = 750_000L
                });
            }
        }

        const int width = cols * block;
        const int height = rows * block;
        var states = new ushort[width * height];
        var owners = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = x / block;
                var r = y / block;
                states[y * width + x] = (ushort)StateId(c, r);
                owners[y * width + x] = (ushort)(c < cols / 2 ? alpha.MapIndex : beta.MapIndex);
            }
        }

        var topo = ProvinceTopology.Build(states, owners, width, height, _ => false);
        campaign.AttachProvinceTopology(topo);
        campaign.ConfigureSinglePlayer(0);
        alpha.CapitalStateMapIndex = StateId(0, 1);
        beta.CapitalStateMapIndex = StateId(cols - 1, 1);

        // Locate border & rear provinces directly from built topology neighbors.
        var alphaBorder = -1;
        var alphaRear = -1;
        var betaBorder = -1;
        for (var p = 1; p <= topo.ProvinceCount; p++)
        {
            var ctrl = campaign.ProvinceController[p];
            var touchesEnemy = topo.Neighbors[p].Any(nb =>
                campaign.ProvinceController[nb] != 0 && campaign.ProvinceController[nb] != ctrl);
            if (ctrl == alpha.MapIndex)
            {
                if (touchesEnemy && alphaBorder < 0) alphaBorder = p;
                else if (!touchesEnemy && alphaRear < 0) alphaRear = p;
            }
            else if (ctrl == beta.MapIndex && touchesEnemy && betaBorder < 0)
            {
                betaBorder = p;
            }
        }

        if (alphaBorder <= 0 || alphaRear <= 0 || betaBorder <= 0)
            throw new InvalidOperationException("Failed to locate border/rear provinces in synthetic topology.");

        return (campaign, alpha, beta, alphaBorder, alphaRear, betaBorder);
    }

    private static void WorldVisualStateFrontsDestroyedArmiesAndZeroAlloc()
    {
        var (campaign, alpha, beta, alphaBorder, alphaRear, betaBorder) = CreateTwoNationWarFixture(98861);
        var war = CampaignDiplomacy.DeclareWar(campaign, alpha.Id, beta.Id, justification: "border_dispute", onOwnInitiative: false);
        Check(war is not null, "synthetic two-nation war created");

        campaign.DestroyedProvinces.Add(alphaRear);

        var line = new DrawnFrontLine
        {
            Id = campaign.NextFrontLineId++,
            NationId = alpha.Id,
            Kind = FrontLineKind.Defence,
            AllEnemies = true,
            WholeBorder = true
        };
        campaign.FrontLines.Add(line);
        BorderFronts.Refresh(campaign, line);

        var marchNext = campaign.ProvinceMap.Neighbors[alphaBorder].First(nb => campaign.ProvinceController[nb] == alpha.MapIndex);
        var nextArmyId = 1;
        var a1 = new Army
        {
            Id = nextArmyId++,
            NationId = alpha.Id,
            ProvinceId = alphaBorder,
            StateMapIndex = campaign.ProvinceMap.ProvinceToState[alphaBorder],
            Name = "1st Strike Corps",
            Divisions = 3f,
            Personnel = 36_000,
            MaxPersonnel = 36_000,
            MoveProgress = 0.625f,
            FrontLineId = line.Id,
            OrderKind = ArmyOrder.DefendFront
        };
        a1.Route.Add(marchNext);
        a1.PlannedRoute.Add(marchNext);

        var a2 = new Army
        {
            Id = nextArmyId++,
            NationId = alpha.Id,
            ProvinceId = alphaBorder,
            StateMapIndex = campaign.ProvinceMap.ProvinceToState[alphaBorder],
            Name = "2nd Support Brigade",
            Divisions = 2f,
            Personnel = 24_000,
            MaxPersonnel = 24_000,
            MoveProgress = 0.625f,
            FrontLineId = line.Id,
            OrderKind = ArmyOrder.DefendFront
        };
        a2.Route.Add(marchNext);
        a2.PlannedRoute.Add(marchNext);

        var b1 = new Army
        {
            Id = nextArmyId++,
            NationId = beta.Id,
            ProvinceId = betaBorder,
            StateMapIndex = campaign.ProvinceMap.ProvinceToState[betaBorder],
            Name = "Beta Guard",
            Divisions = 4f,
            Personnel = 48_000,
            MaxPersonnel = 48_000
        };
        campaign.Armies.Add(a1);
        campaign.Armies.Add(a2);
        campaign.Armies.Add(b1);

        var renderSnap = CampaignRenderSnapshot.Capture(campaign, CampaignRenderSnapshot.Empty, CountryRankings.Weights.Default, 1);
        var visual = new WorldVisualState();
        visual.CaptureFrom(campaign, renderSnap);
        visual.CaptureFrom(campaign, renderSnap);

        Check(visual.HasPlayerFrontLines, "WorldVisualState sets HasPlayerFrontLines == true when player has active front line");
        Check(visual.PlayerFrontProvinceCount > 0 && visual.PlayerFrontGeometryHash != 0,
            $"WorldVisualState captured {visual.PlayerFrontProvinceCount} player front provinces (hash=0x{visual.PlayerFrontGeometryHash:X8})");
        Check(visual.DestroyedProvinceCount == 1 && visual.DestroyedProvinces[0] == alphaRear,
            "WorldVisualState captures DestroyedProvinceCount and province id cleanly");

        Check(visual.ArmyStackCount == 2 && visual.StackArmyIdCount == 3,
            $"WorldVisualState grouped 3 Campaign.Armies into 2 province stacks (stacks={visual.ArmyStackCount}, ids={visual.StackArmyIdCount})");

        var foundAlphaStack = false;
        var foundBetaStack = false;
        for (var i = 0; i < visual.ArmyStackCount; i++)
        {
            ref readonly var stack = ref visual.ArmyStacks[i];
            if (stack.NationId == alpha.Id)
            {
                foundAlphaStack = true;
                var ids = visual.StackArmyIds.AsSpan(stack.ArmyIdOffset, stack.ArmyIdCount).ToArray();
                Check(stack.LocationId == alphaBorder && stack.LeadProvinceId == alphaBorder,
                    "player VisualArmyStack preserves LocationId and LeadProvinceId");
                Check(stack.NextProvinceId == marchNext && stack.IsMoving && MathF.Abs(stack.MarchProgress - 0.625f) < 1e-4f,
                    $"player VisualArmyStack captures march progress ({stack.MarchProgress:0.000}) and NextProvinceId ({stack.NextProvinceId})");
                var expectedPersonnel =
                    MathF.Round(a1.Divisions * MilitaryBootstrap.DivisionSize * a1.Strength) +
                    MathF.Round(a2.Divisions * MilitaryBootstrap.DivisionSize * a2.Strength);
                Check(MathF.Abs(stack.Divisions - 5f) < 1e-4f && MathF.Abs(stack.Personnel - expectedPersonnel) < 1e-2f,
                    $"player VisualArmyStack aggregates Divisions ({stack.Divisions}) and Personnel ({stack.Personnel}) across stack members");
                Check(stack.ArmyIdCount == 2 && ids.Contains(a1.Id) && ids.Contains(a2.Id),
                    "player StackArmyIds slice matches grouped Campaign.Armies IDs");
            }
            else if (stack.NationId == beta.Id)
            {
                foundBetaStack = true;
                var ids = visual.StackArmyIds.AsSpan(stack.ArmyIdOffset, stack.ArmyIdCount).ToArray();
                Check(stack.LocationId == betaBorder && !stack.IsMoving && stack.ArmyIdCount == 1 && ids[0] == b1.Id,
                    "enemy VisualArmyStack matches stationary Campaign.Armies entry");
            }
        }
        Check(foundAlphaStack && foundBetaStack, "both player and enemy VisualArmyStack buckets present");

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iter = 0; iter < 32; iter++)
            visual.CaptureFrom(campaign, renderSnap);
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var delta = allocAfter - allocBefore;

        var flagAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        var flagSink = 0;
        for (var iter = 0; iter < 32; iter++)
            flagSink ^= FlagCatalog.VisualStateHash(campaign);
        var flagAllocDelta = GC.GetAllocatedBytesForCurrentThread() - flagAllocBefore;

        Check(delta == 0,
            $"steady-state WorldVisualState.CaptureFrom incurs zero allocations across 32 captures (delta={delta}, FlagCatalog.VisualStateHash={flagAllocDelta}, sink={flagSink})");
    }

    private static void BackgroundSimRunnerNonBlockingClockControl()
    {
        var campaign = Campaign.CreateBootstrap(98862);
        campaign.IsPaused = true;
        campaign.Speed = 1;
        using var runner = new BackgroundSimRunner
        {
            MobilePacing = false,
            MinSpeed5DayIntervalMs = 0.0
        };

        runner.Start(campaign);
        try
        {
            // While worker is running and AccessGate is write-locked, SetDesiredClockState must not touch AccessGate.
            campaign.AccessGate.EnterWrite();
            try
            {
                runner.SetDesiredClockState(paused: false, speed: 8);
                Check(!runner.DesiredPaused && runner.DesiredSpeed == 8,
                    "SetDesiredClockState updates DesiredPaused/DesiredSpeed without acquiring AccessGate");
            }
            finally
            {
                campaign.AccessGate.ExitWrite();
            }

            WorldVisualState? latest = null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                if (runner.TryAcquireLatestVisualSnapshot(out var snap))
                {
                    latest = snap;
                    if (snap.Speed == 8 && !snap.IsPaused && snap.Version >= 2)
                        break;
                }
                Thread.Sleep(5);
            }
            runner.DrainMainThreadUpdates(out _, out _, out _, out var fault);
            Check(fault is null, $"BackgroundSimRunner produced no worker fault ({fault?.Message ?? "none"})");
            Check(latest is not null && latest.Speed == 8 && !latest.IsPaused,
                $"BackgroundSimRunner published Speed=8 snapshot without blocking (ver={latest?.Version ?? 0}, speed={latest?.Speed ?? 0})");

            runner.SetDesiredClockState(paused: true, speed: 3);
            Check(runner.DesiredPaused && runner.DesiredSpeed == 3,
                "SetDesiredClockState transitions back to paused speed 3 while worker is active");
        }
        finally
        {
            runner.Stop();
        }
    }

    private static void ImageDisposalHygieneContracts(string repoRoot)
    {
        var minimapSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellMinimapView.cs"));
        Check(minimapSrc.Contains("Image? _minimapImage;") || minimapSrc.Contains("Image _minimapImage;"),
            "ShellMinimapView keeps persistent reusable _minimapImage field");
        Check(minimapSrc.Contains("_minimapImage.SetData("),
            "ShellMinimapView updates minimap texture in-place via SetData");
        Check(!minimapSrc.Contains("using var img = Image.CreateFromData(") && !minimapSrc.Contains("var img = Image.CreateFromData("),
            "ShellMinimapView no longer allocates transient per-update Image instances");
        Check(minimapSrc.Contains("_minimapImage?.Dispose();") && minimapSrc.Contains("_panel.TreeExiting += DisposeResources;"),
            "ShellMinimapView disposes _minimapImage in DisposeResources wired to TreeExiting");

        var plateSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "gfx", "GlobePlateRepaint.cs"));
        Check(plateSrc.Contains("_pendingPlateUpload?.Dispose();"),
            "GlobePlateRepaint disposes _pendingPlateUpload when superseded");
        Check(plateSrc.Contains("finally { frontier.Dispose(); }") && plateSrc.Contains("image.Dispose();"),
            "GlobePlateRepaint disposes staging Plate and Frontier Images after GPU upload");
        Check(plateSrc.Contains("done.Result.Plate?.Dispose();") && plateSrc.Contains("done.Result.Frontier?.Dispose();"),
            "GlobePlateRepaint disposes orphaned Images if background repaint finishes partially");
    }

    private static void EightSpeedScaleAndClockDifferentiation(string repoRoot)
    {
        var defines = new Defines();
        Check(defines.MaxSpeed == 8 && defines.SpeedFactors.Count == 8,
            $"Defines exposes 8 simulation speeds (MaxSpeed={defines.MaxSpeed}, Count={defines.SpeedFactors.Count})");
        Check(MathF.Abs(defines.SpeedFactors[0] - 2.40f) < 1e-6f && MathF.Abs(defines.SpeedFactors[7] - 0.002f) < 1e-6f,
            "Defines speed factors span 2.40x (Speed 1) down to 0.002x (Speed 8)");

        for (var s = 1; s < 8; s++)
        {
            var cur = defines.SecondsForSpeed(s);
            var next = defines.SecondsForSpeed(s + 1);
            Check(cur > next && (cur / next) >= 1.8f,
                $"SecondsForSpeed({s})={cur:0.####} > SecondsForSpeed({s + 1})={next:0.####} with geometric step ({cur / next:0.##}x)");
        }
        Check(MathF.Abs(defines.SecondsForSpeed(0) - defines.SecondsForSpeed(1)) < 1e-6f &&
              MathF.Abs(defines.SecondsForSpeed(99) - defines.SecondsForSpeed(8)) < 1e-6f,
            "Defines.SecondsForSpeed clamps out-of-range speeds to 1..8");

        var b0 = SimulationFrameClock.FrameBudgetMs(defines, 1, smooth: true);
        var b6 = SimulationFrameClock.FrameBudgetMs(defines, 6, smooth: true);
        var b8 = SimulationFrameClock.FrameBudgetMs(defines, 8, smooth: true);
        Check(b8 >= b6 && b6 >= b0 && b8 > b0,
            $"SimulationFrameClock.FrameBudgetMs expands across speeds 1/6/8 ({b0:0.##} -> {b6:0.##} -> {b8:0.##} ms)");

        var expectedHours = new[] { 1, 1, 1, 2, 4, 6, 12, 24 };
        double prevMobile = double.MaxValue;
        double prevDesktop = double.MaxValue;
        for (var s = 1; s <= 8; s++)
        {
            var stepHours = BackgroundSimRunner.MobileAdaptiveStepHours(s);
            Check(stepHours == expectedHours[s - 1],
                $"MobileAdaptiveStepHours({s}) == {expectedHours[s - 1]} (actual {stepHours})");

            var mobileSec = BackgroundSimRunner.MobileStepSecondsPerDay(defines, s);
            var desktopSec = BackgroundSimRunner.AdaptiveStepSecondsPerDay(defines, s, mobilePacing: false);
            Check(mobileSec < prevMobile && desktopSec < prevDesktop,
                $"Speed {s} pacing strictly faster than Speed {s - 1} (desktop={desktopSec:0.######}, mobile={mobileSec:0.######})");
            prevMobile = mobileSec;
            prevDesktop = desktopSec;
        }

        var speed5Msg = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.ArmyEncircled, true, 5);
        var speed8Msg = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.ArmyEncircled, true, 8);
        Check(!speed5Msg.Speed5SafetyPause && speed8Msg.Speed5SafetyPause && speed8Msg.PauseClock,
            "ShellMessages triggers Speed5SafetyPause at Speed 8 while Speed 5 stays unpaused");

        var datePlateClockSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "DatePlateClock.cs"));
        Check(datePlateClockSrc.Contains("Fastest = 8") &&
              datePlateClockSrc.Contains("Math.Clamp(speed, Slowest, Fastest)"),
            "DatePlateClock defines Fastest = 8 and clamps speed to Slowest..Fastest");
    }

    private static void MapCatalogSyntheticProfilesSaveAndCacheScoping()
    {
        var previousMap = MapCatalog.ActiveMapId;
        var tempDir = Path.Combine(Path.GetTempPath(), "dof-map-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            MapCatalog.RefreshCatalog();
            Check(MapCatalog.TryGet(MapCatalog.StandardId, out var stdPack) && stdPack.IsPlayable && stdPack.CompiledProvinceCount == 34_138,
                $"MapCatalog discovers {MapCatalog.StandardId} with 34,138 compiled provinces");
            Check(MapCatalog.TryGet(MapCatalog.TacticalId, out var tacPack) && tacPack.IsPlayable && tacPack.CompiledProvinceCount == 18_493,
                $"MapCatalog discovers {MapCatalog.TacticalId} with 18,493 compiled provinces");
            Check(MapCatalog.TryGet(MapCatalog.CompactId, out var cmpPack) && cmpPack.IsPlayable && cmpPack.CompiledProvinceCount == 10_370,
                $"MapCatalog discovers {MapCatalog.CompactId} with 10,370 compiled provinces");

            // Synthetic 2048x128 world raster so width >= WorldRasterWidth triggers SiberiaMultiplier & PolarMultiplier.
            const int w = ProvinceTopology.WorldRasterWidth;
            const int h = 128;
            var states = new ushort[w * h];
            var control = new ushort[w * h];
            const ushort StatePolar = 10;
            const ushort StateSiberia = 20;
            const ushort StateTemperate = 30;
            const ushort StateDisputed = 40;

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (y is >= 4 and <= 13 && x is >= 200 and <= 360)
                    {
                        states[y * w + x] = StatePolar;
                        control[y * w + x] = 1;
                    }
                    else if (y is >= 14 and <= 26 && x is >= 1400 and <= 1560)
                    {
                        states[y * w + x] = StateSiberia;
                        control[y * w + x] = 2;
                    }
                    else if (y is >= 48 and <= 64 && x is >= 900 and <= 1060)
                    {
                        states[y * w + x] = StateTemperate;
                        control[y * w + x] = 3;
                    }
                    else if (y is >= 50 and <= 64 && x is >= 500 and <= 620)
                    {
                        states[y * w + x] = StateDisputed;
                        control[y * w + x] = 4;
                    }
                }
            }

            bool IsDisputed(int cell) => states[cell] == StateDisputed;
            var stdTopo1 = ProvinceTopology.Build(states, control, w, h, IsDisputed, ProvinceBuildProfile.Standard);
            var stdTopo2 = ProvinceTopology.Build(states, control, w, h, IsDisputed, ProvinceBuildProfile.Standard);
            Check(stdTopo1.ProvinceCount == stdTopo2.ProvinceCount &&
                  stdTopo1.ProvinceToState.SequenceEqual(stdTopo2.ProvinceToState) &&
                  stdTopo1.ProvinceToNation.SequenceEqual(stdTopo2.ProvinceToNation) &&
                  stdTopo1.CellToProvince.SequenceEqual(stdTopo2.CellToProvince),
                $"synthetic Standard ProvinceBuildProfile is bit-identical across runs ({stdTopo1.ProvinceCount} provinces)");

            var coarseProfile = new ProvinceBuildProfile(
                CellsPerProvince: 324,
                DisputedCellsPerProvince: 108,
                PolarFromLatitude: 60f,
                PopulatedShare: 1.0f,
                SiberiaMultiplier: 2.0f,
                PolarMultiplier: 2.0f,
                MinimumProvinceCells: 48);
            var coarseTopo = ProvinceTopology.Build(states, control, w, h, IsDisputed, coarseProfile);
            Check(coarseTopo.ProvinceCount > 0 && coarseTopo.ProvinceCount < stdTopo1.ProvinceCount,
                $"coarser ProvinceBuildProfile reduced synthetic province count ({stdTopo1.ProvinceCount} -> {coarseTopo.ProvinceCount})");

            static int CountForState(ProvinceTopology t, ushort stateId) =>
                t.ProvinceToState.Count(s => s == stateId);
            Check(CountForState(coarseTopo, StateSiberia) < CountForState(stdTopo1, StateSiberia) &&
                  CountForState(coarseTopo, StatePolar) <= CountForState(stdTopo1, StatePolar) &&
                  CountForState(coarseTopo, StateTemperate) < CountForState(stdTopo1, StateTemperate) &&
                  CountForState(coarseTopo, StateDisputed) < CountForState(stdTopo1, StateDisputed),
                "SiberiaMultiplier / PolarMultiplier / PopulatedShare / DisputedCellsPerProvince coarsen each climate/region tier");

            // MapBootCache scoping & shipped standard fallback isolation.
            var fakeStdFallback = Path.Combine(tempDir, "shipped_standard_map_boot_v2.bin");
            var fakeBootData = new MapBootCacheData { Provinces = stdTopo1, ProvinceSource = "synthetic" };
            Check(MapBootCache.TryWriteToFile(fakeStdFallback, regionFingerprint: 0x123456789ABCDEF0UL, fakeBootData, out var writeErr),
                $"wrote synthetic fallback boot cache ({writeErr})");

            MapCatalog.SetActiveMap(MapCatalog.StandardId);
            var stdCachePath = MapBootCache.ResolveWritableCachePath(tempDir);
            Check(MapBootCache.TryRead(tempDir, 0x123456789ABCDEF0UL, w, h, out _, out var stdHitReason, fakeStdFallback, attachCrossings: false),
                $"Standard map reads shipped fallback ({stdHitReason})");

            MapCatalog.SetActiveMap(MapCatalog.CompactId);
            var compactCachePath = MapBootCache.ResolveWritableCachePath(tempDir);
            Check(!string.Equals(stdCachePath, compactCachePath, StringComparison.OrdinalIgnoreCase) &&
                  compactCachePath!.Contains("world_compact_10370", StringComparison.OrdinalIgnoreCase),
                $"MapBootCache scopes writable path separately for world_compact ({compactCachePath})");
            Check(!MapBootCache.TryRead(tempDir, 0x123456789ABCDEF0UL, w, h, out _, out var cmpReason, fakeStdFallback, attachCrossings: false),
                $"MapBootCache refuses world_standard fallback while world_compact is active ({cmpReason})");

            MapCatalog.SetActiveMap(MapCatalog.TacticalId);
            var tacticalCachePath = MapBootCache.ResolveWritableCachePath(tempDir);
            Check(tacticalCachePath!.Contains("world_tactical_18493", StringComparison.OrdinalIgnoreCase),
                $"MapBootCache scopes writable path separately for world_tactical ({tacticalCachePath})");

            // Save compatibility & legacy save auto-selecting world_standard.
            Check(SaveSystem.MapDataVersion == "2024.11.09-a",
                $"SaveSystem.MapDataVersion matches contract ('{SaveSystem.MapDataVersion}')");

            MapCatalog.SetActiveMap(MapCatalog.CompactId);
            var legacyPayload = new SaveSystem.CampaignSave
            {
                FormatVersion = SaveSystem.FormatVersion,
                GameVersion = SaveSystem.GameVersion,
                MapDataVersion = SaveSystem.MapDataVersion,
                ProvinceCount = 34_138,
                Seed = 777,
                ScenarioId = "legacy_fixture",
                MapId = "",
                ProvinceController = new ushort[34_139],
                ProvinceOccupation = new float[34_139],
                ProvinceOwner = new ushort[34_139],
                ProvinceState = new ushort[34_139],
                ScenarioRules = new Defines(),
                Nations = new List<Nation>
                {
                    new() { Id = 0, MapIndex = 1, Tag = "TST", Name = "Testland", Population = 1_000_000L }
                },
                States = new List<ScenarioState>
                {
                    new() { MapIndex = 10, NationMapIndex = 1, NationId = "TST", Id = "st1", Name = "State 1", Population = 1_000_000L }
                },
                PlayerId = 0,
                Date = new GameDate(2024, 1, 1)
            };

            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    JsonSerializer.Serialize(gzip, legacyPayload, new JsonSerializerOptions { IncludeFields = true });
                compressed = ms.ToArray();
            }

            var loadedLegacy = SaveSystem.ImportNetworkSnapshot(compressed, "");
            Check(loadedLegacy.Success && MapCatalog.ActiveMapId == MapCatalog.StandardId,
                $"legacy 34,138-province save with empty MapId auto-selects '{MapCatalog.StandardId}' (warning='{loadedLegacy.Warning}')");
        }
        finally
        {
            MapCatalog.SetActiveMap(previousMap);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TickRarityTempoPresetsAndSettingsContract(string repoRoot)
    {
        var settingsJsonPath = Path.Combine(repoRoot, "game", "common", "settings", "00_settings.json");
        var settingsText = File.ReadAllText(settingsJsonPath);
        using var doc = JsonDocument.Parse(settingsText, GameDataLoader.ForgivingJson);
        var root = doc.RootElement;

        foreach (var key in new[] { "sim_tempo_preset", "economy_tick_days", "ai_calm_cadence", "map_pack" })
        {
            Check(root.TryGetProperty(key, out _), $"00_settings.json declares '{key}'");
            Check(SettingsCatalog.Of(key) is not null, $"SettingsCatalog loads '{key}'");
        }

        var tempoDef = SettingsCatalog.Of("sim_tempo_preset")!;
        var optionKeys = tempoDef.Values.ToArray();
        Check(optionKeys.SequenceEqual(new[] { "detailed", "balanced", "fast_mobile", "grand_rts" }),
            $"sim_tempo_preset declares all 4 presets ({string.Join(", ", optionKeys)})");

        var detailedDefaults = new Defines();
        Check(detailedDefaults.HoursPerTick == 1 && detailedDefaults.EconomyTickDays == 7 && detailedDefaults.AiCalmCadenceDays == 7,
            "default Defines (detailed preset) preserves HoursPerTick=1, EconomyTickDays=7, AiCalmCadenceDays=7");

        var applySrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "SettingsApply.cs"));
        Check(applySrc.Contains("\"balanced\" => (6, 7, 14)") &&
              applySrc.Contains("\"fast_mobile\" => (12, 14, 30)") &&
              applySrc.Contains("\"grand_rts\" => (24, 30, 60)"),
            "SettingsApply maps tempo presets to exact tick/economy/AI-cadence tuples");
    }

    private static void ModStudioCliTemplatesAndApiParity(string repoRoot)
    {
        var categories = Enum.GetValues<ModStudioCategory>();
        Check(categories.Length == 17, $"ModStudioCategory exposes all 17 categories (actual={categories.Length})");
        Check(Enum.IsDefined(ModStudioCategory.FormableNation) &&
              Enum.IsDefined(ModStudioCategory.Country) &&
              Enum.IsDefined(ModStudioCategory.MapPack),
            "ModStudioCategory includes FormableNation, Country, and MapPack");

        var snippets = ModStudioSnippets.All;
        Check(snippets.Count >= 8, $"ModStudioSnippets exposes complete snippet library ({snippets.Count} items)");

        var cliPath = Path.Combine(repoRoot, "Tools", "new-mod.ps1");
        Check(File.Exists(cliPath), "Tools/new-mod.ps1 mod scaffolding CLI script exists");

        var templateMapModPath = Path.Combine(repoRoot, "mods", "_templates", "custom_map_pack");
        var templateReport = ModValidator.ValidateMod(templateMapModPath);
        Check(templateReport.ErrorCount == 0 && templateReport.WarningCount == 0,
            $"custom_map_pack template validates cleanly (errors={templateReport.ErrorCount}, warnings={templateReport.WarningCount})");

        var tempModsDir = Path.Combine(Path.GetTempPath(), "dof-scaffold-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mapResult = ModStudioScaffolder.Scaffold(new ModScaffoldRequest(
                tempModsDir, "test_compact_pack", "Test Compact Map", "QA", ModScaffoldKind.MapPack));
            var mapValidation = ModValidator.ValidateMod(mapResult.ModRoot);
            Check(mapValidation.ErrorCount == 0,
                $"ModStudioScaffolder.Scaffold(MapPack) passes ModValidator with 0 errors (warnings={mapValidation.WarningCount})");

            var formableResult = ModStudioScaffolder.Scaffold(new ModScaffoldRequest(
                tempModsDir, "test_formable_pack", "Test Formable Nation", "QA", ModScaffoldKind.Formable));
            var formableValidation = ModValidator.ValidateMod(formableResult.ModRoot);
            Check(formableValidation.ErrorCount == 0,
                $"ModStudioScaffolder.Scaffold(Formable) passes ModValidator with 0 errors (warnings={formableValidation.WarningCount})");
        }
        finally
        {
            try { Directory.Delete(tempModsDir, recursive: true); } catch { }
        }

        // Verify 1:1 parity between ModApi.get_capabilities() and EngineScriptMod.CreateApi.
        var modApiSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "Modding", "ModApi.cs"));
        var engineModSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "Modding", "EngineScriptMod.cs"));

        var capMatch = Regex.Match(modApiSrc, @"get_capabilities\(\)\s*=>\s*new\[\]\s*\{(?<body>[^}]+)\}", RegexOptions.Singleline);
        Check(capMatch.Success, "found ModApi.get_capabilities() declaration");
        var capHooks = Regex.Matches(capMatch.Groups["body"].Value, "\"([a-z0-9_]+)\"")
            .Select(m => m.Groups[1].Value)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var createMatch = Regex.Match(engineModSrc, @"var\s+bridge\s*=\s*new\s+Godot\.Collections\.Dictionary\s*\{(?<body>[^}]+)\}", RegexOptions.Singleline);
        Check(createMatch.Success, "found EngineScriptMod.CreateApi bridge dictionary");
        var apiKeys = Regex.Matches(createMatch.Groups["body"].Value, "\\[\"([a-z0-9_]+)\"\\]")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Check(capHooks.Length == 23, $"ModApi.get_capabilities lists 23 mod hooks (actual={capHooks.Length})");
        Check(apiKeys.Contains("get_capabilities"), "EngineScriptMod.CreateApi exposes get_capabilities");
        apiKeys.Remove("get_capabilities");
        Check(capHooks.SequenceEqual(apiKeys.OrderBy(s => s, StringComparer.Ordinal)),
            "ModApi.get_capabilities() and EngineScriptMod.CreateApi match 1:1 with zero drift");
    }

    private static void RustSimdCombatEconAndDiplomacyBordersParity()
    {
        var prevDisable = NativeKernelsLoader.SimulateUnavailableForTests;
        NativeKernelsLoader.SimulateUnavailableForTests = false;
        try
        {
            var nativeAvailable = NativeKernelsLoader.IsAvailable;
            if (nativeAvailable)
            {
                Check(NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconCombat),
                    $"native dof_kernels reports CapEconCombat (caps=0x{NativeKernelsLoader.Capabilities:X})");
            }

            VerifyEconCombatKernelBitParity(nativeAvailable);
            VerifyDiplomacyBordersAndGraphParity(nativeAvailable);
            VerifyActiveWar35DayCampaignFingerprintParity(nativeAvailable);
        }
        finally
        {
            NativeKernelsLoader.SimulateUnavailableForTests = prevDisable;
        }
    }

    private static void VerifyEconCombatKernelBitParity(bool nativeAvailable)
    {
        int[] lengths = { 0, 1, 3, 7, 8, 9, 15, 16, 17, 257 };
        var rng = new Random(20261127);

        foreach (var n in lengths)
        {
            var fA = new float[n];
            var fB = new float[n];
            var fC = new float[n];
            var fD = new float[n];
            var fE = new float[n];
            var fF = new float[n];
            var fG = new float[n];
            var fH = new float[n];
            var fI = new float[n];
            var fJ = new float[n];
            var fK = new float[n];
            var fL = new float[n];
            var fM = new float[n];
            var fN = new float[n];
            var fO = new float[n];

            for (var i = 0; i < n; i++)
            {
                fA[i] = 0.25f + (float)rng.NextDouble() * 6f;
                fB[i] = (float)rng.NextDouble();
                fC[i] = (float)rng.NextDouble() * 1.4f;
                fD[i] = (float)rng.NextDouble() * 1.4f;
                fE[i] = 0.2f + (float)rng.NextDouble() * 0.8f;
                fF[i] = (float)rng.NextDouble();
                fG[i] = 0.7f + (float)rng.NextDouble() * 0.6f;
                fH[i] = 0.8f + (float)rng.NextDouble() * 0.5f;
                fI[i] = 0.6f + (float)rng.NextDouble() * 0.7f;
                fJ[i] = 1f + (float)rng.NextDouble() * 10f;
                fK[i] = 0.75f + (float)rng.NextDouble() * 0.5f;
                fL[i] = 0.85f + (float)rng.NextDouble() * 0.4f;
                fM[i] = 0.7f + (float)rng.NextDouble() * 0.5f;
                fN[i] = 0.8f + (float)rng.NextDouble() * 0.4f;
                fO[i] = (float)rng.NextDouble() * 0.6f;
            }

            // 1. CombatLineProfile
            NativeCombatSupportKernels.ManagedCombatLineProfile(
                fA, fB, fC, fD, fE, fF, out var ch, out var ca, out var cp, out var cs, out var cr);
            if (nativeAvailable)
            {
                var ok = NativeCombatSupportKernels.TryNativeCombatLineProfile(
                    fA, fB, fC, fD, fE, fF, out var nh, out var na, out var np, out var ns, out var nr);
                Check(ok, $"TryNativeCombatLineProfile succeeds (n={n})");
                CheckBitExactFloat(new[] { ch, ca, cp, cs, cr }, new[] { nh, na, np, ns, nr }, $"CombatLineProfile(n={n})");
            }

            // 2. CombatSideTotals (both telemetry modes)
            foreach (var wOff in new[] { false, true })
            {
                var cTot = new double[9];
                NativeCombatSupportKernels.ManagedCombatSideTotals(
                    fA, fB, fC, fD, fE, fF, fG, fH, fI, fJ, fK, fL, fM, fN, fO, wOff, cTot);
                if (nativeAvailable)
                {
                    var nTot = new double[9];
                    var ok = NativeCombatSupportKernels.TryNativeCombatSideTotals(
                        fA, fB, fC, fD, fE, fF, fG, fH, fI, fJ, fK, fL, fM, fN, fO, wOff, nTot);
                    Check(ok, $"TryNativeCombatSideTotals succeeds (n={n}, off={wOff})");
                    CheckBitExactDouble(cTot, nTot, $"CombatSideTotals(n={n},off={wOff})");
                }
            }

            // 3. SupportAttritionBatch
            var strC = (float[])fE.Clone();
            var expC = (float[])fF.Clone();
            var lossC = new float[n];
            NativeCombatSupportKernels.ManagedSupportAttritionBatch(fB, fG, 0.045f, 12f, 0.1f, strC, expC, lossC);
            if (nativeAvailable)
            {
                var strN = (float[])fE.Clone();
                var expN = (float[])fF.Clone();
                var lossN = new float[n];
                var ok = NativeCombatSupportKernels.TryNativeSupportAttritionBatch(fB, fG, 0.045f, 12f, 0.1f, strN, expN, lossN);
                Check(ok, $"TryNativeSupportAttritionBatch succeeds (n={n})");
                CheckBitExactFloat(strC, strN, $"SupportAttrition.Strength(n={n})");
                CheckBitExactFloat(expC, expN, $"SupportAttrition.Experience(n={n})");
                CheckBitExactFloat(lossC, lossN, $"SupportAttrition.Loss(n={n})");
            }

            // 4. SupportReachBatch
            var bLat = new float[n];
            var bLon = new float[n];
            var reachKm = new float[n];
            for (var i = 0; i < n; i++)
            {
                bLat[i] = (float)(rng.NextDouble() * 140.0 - 70.0);
                bLon[i] = (float)(rng.NextDouble() * 340.0 - 170.0);
                reachKm[i] = 150f + (i % 5) * 400f;
            }
            var wC = new float[n];
            NativeCombatSupportKernels.ManagedSupportReachBatch(48.5f, 32.2f, bLat, bLon, reachKm, fE, fF, wC);
            if (nativeAvailable)
            {
                var wN = new float[n];
                var ok = NativeCombatSupportKernels.TryNativeSupportReachBatch(48.5f, 32.2f, bLat, bLon, reachKm, fE, fF, wN);
                Check(ok, $"TryNativeSupportReachBatch succeeds (n={n})");
                CheckBitExactFloat(wC, wN, $"SupportReachBatch(n={n})");
            }

            // 5. EconStateWeekPost
            var stSolC = (float[])fE.Clone();
            var stRadC = (float[])fB.Clone();
            var weekDelta = new float[n];
            var cycRad = new float[n];
            for (var i = 0; i < n; i++)
            {
                weekDelta[i] = (float)(rng.NextDouble() * 0.08 - 0.02);
                cycRad[i] = (float)(rng.NextDouble() * 0.03 - 0.01);
            }
            NativeCombatSupportKernels.ManagedEconStateWeekPost(fF, weekDelta, cycRad, stSolC, stRadC);
            if (nativeAvailable)
            {
                var stSolN = (float[])fE.Clone();
                var stRadN = (float[])fB.Clone();
                var ok = NativeCombatSupportKernels.TryNativeEconStateWeekPost(fF, weekDelta, cycRad, stSolN, stRadN);
                Check(ok, $"TryNativeEconStateWeekPost succeeds (n={n})");
                CheckBitExactFloat(stSolC, stSolN, $"EconStateWeekPost.SoL(n={n})");
                CheckBitExactFloat(stRadC, stRadN, $"EconStateWeekPost.Rad(n={n})");
            }
        }
    }

    private static void VerifyDiplomacyBordersAndGraphParity(bool nativeAvailable)
    {
        static Campaign BuildFourNationBorderWorld()
        {
            var c = new Campaign(98863) { ScenarioId = "border_parity_fixture" };
            var mapIndices = new ushort[] { 711, 712, 713, 714 };
            for (var i = 0; i < 4; i++)
            {
                c.Nations.Add(new Nation
                {
                    Id = i,
                    MapIndex = mapIndices[i],
                    Tag = $"N{i}P",
                    Name = $"Nation {i}",
                    Population = 10_000_000L
                });
                c.States.Add(new ScenarioState
                {
                    MapIndex = 6200 + i,
                    NationMapIndex = mapIndices[i],
                    NationId = $"N{i}P",
                    Id = $"bp_s_{i}",
                    Name = $"State {i}",
                    Population = 10_000_000L
                });
            }

            // 64x64 grid: 4 quadrants + inner ring so nation 0 borders 1, 2, 3 (tests sorted multi-neighbour sets).
            const int size = 64;
            var states = new ushort[size * size];
            var owners = new ushort[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var idx = (y < size / 2 ? 0 : 2) + (x < size / 2 ? 0 : 1);
                    if (x is >= 24 and < 40 && y is >= 24 and < 40) idx = 0;
                    states[y * size + x] = (ushort)(6200 + idx);
                    owners[y * size + x] = mapIndices[idx];
                }
            }
            c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, size, size, _ => false));
            return c;
        }

        var campaignA = BuildFourNationBorderWorld();
        var campaignB = BuildFourNationBorderWorld();

        NativeKernelsLoader.SimulateUnavailableForTests = false;
        campaignA.MarkMapChanged();
        CampaignDiplomacy.ForgetBorders();
        var nativeNbrs = campaignA.Nations.ToDictionary(n => n.MapIndex, n => CampaignDiplomacy.Neighbours(campaignA, n.MapIndex).ToArray());

        NativeKernelsLoader.SimulateUnavailableForTests = true;
        campaignB.MarkMapChanged();
        CampaignDiplomacy.ForgetBorders();
        var managedNbrs = campaignB.Nations.ToDictionary(n => n.MapIndex, n => CampaignDiplomacy.Neighbours(campaignB, n.MapIndex).ToArray());
        NativeKernelsLoader.SimulateUnavailableForTests = false;

        foreach (var kv in nativeNbrs)
        {
            Check(kv.Value.Length >= 2 && managedNbrs.TryGetValue(kv.Key, out var mSet) && kv.Value.SequenceEqual(mSet),
                $"DiplomacyCore.Borders sorted neighbour set matches bit-exact for mapIndex={kv.Key} (native={string.Join(",", kv.Value)})");
        }

        if (nativeAvailable)
        {
            var graph = NativeProvinceGraph.ForMap(campaignA.ProvinceMap);
            Check(graph is not null && graph.TryBorderPairs(campaignA.ProvinceController, out var pairs) && pairs.Length > 0,
                "NativeProvinceGraph.TryBorderPairs emits packed (owner<<16)|other pairs");
        }
    }

    private static void VerifyActiveWar35DayCampaignFingerprintParity(bool nativeAvailable)
    {
        static Campaign BuildActiveWarCampaign()
        {
            var (c, alpha, beta, alphaBorder, _, betaBorder) = CreateTwoNationWarFixture(98869);
            c.Defines.HoursPerTick = 24;
            c.IsPaused = false;
            alpha.IsHuman = false;
            beta.IsHuman = false;

            var war = CampaignDiplomacy.DeclareWar(c, alpha.Id, beta.Id, justification: "parity_war", onOwnInitiative: false);
            if (war is null) throw new InvalidOperationException("Could not start parity test war.");

            var attackFront = new DrawnFrontLine
            {
                Id = c.NextFrontLineId++,
                NationId = alpha.Id,
                Kind = FrontLineKind.Offensive,
                AllEnemies = true,
                WholeBorder = true
            };
            var defendFront = new DrawnFrontLine
            {
                Id = c.NextFrontLineId++,
                NationId = beta.Id,
                Kind = FrontLineKind.Defence,
                AllEnemies = true,
                WholeBorder = true
            };
            c.FrontLines.Add(attackFront);
            c.FrontLines.Add(defendFront);
            BorderFronts.Refresh(c, attackFront);
            BorderFronts.Refresh(c, defendFront);

            var nextArmyId = 1;
            c.Armies.Add(new Army
            {
                Id = nextArmyId++,
                NationId = alpha.Id,
                ProvinceId = alphaBorder,
                StateMapIndex = c.ProvinceMap.ProvinceToState[alphaBorder],
                Name = "Alpha Spearhead",
                Divisions = 5f,
                Personnel = 60_000,
                MaxPersonnel = 60_000,
                FrontLineId = attackFront.Id,
                AssignedWarId = war.Id,
                OrderKind = ArmyOrder.AdvanceFront,
                Stance = ArmyStance.Aggressive,
                PlanActive = true
            });
            c.Armies.Add(new Army
            {
                Id = nextArmyId++,
                NationId = beta.Id,
                ProvinceId = betaBorder,
                StateMapIndex = c.ProvinceMap.ProvinceToState[betaBorder],
                Name = "Beta Line",
                Divisions = 4f,
                Personnel = 48_000,
                MaxPersonnel = 48_000,
                FrontLineId = defendFront.Id,
                AssignedWarId = war.Id,
                OrderKind = ArmyOrder.DefendFront,
                PlanActive = true
            });
            return c;
        }

        NativeKernelsLoader.SimulateUnavailableForTests = false;
        var campNative = BuildActiveWarCampaign();
        var campNativeRepeat = BuildActiveWarCampaign();
        var fpN0 = CampaignFingerprint.Of(campNative);
        var fpN0Repeat = CampaignFingerprint.Of(campNativeRepeat);

        NativeKernelsLoader.SimulateUnavailableForTests = true;
        var campManaged = BuildActiveWarCampaign();
        var fpM0 = CampaignFingerprint.Of(campManaged);
        Check(fpN0 == fpN0Repeat && fpN0 == fpM0,
            $"Day 0 active war setup fingerprint matches before stepping (n=0x{fpN0:X16}, repeat=0x{fpN0Repeat:X16}, cs=0x{fpM0:X16})");

        for (var day = 0; day < 35; day++)
        {
            NativeKernelsLoader.SimulateUnavailableForTests = false;
            campNative.TickDay();
            var fpNative = CampaignFingerprint.Of(campNative);

            NativeKernelsLoader.SimulateUnavailableForTests = true;
            campManaged.TickDay();
            var fpManaged = CampaignFingerprint.Of(campManaged);

            if (fpManaged != fpNative)
            {
                Check(false,
                    $"Day {day + 1} CampaignFingerprint matches bit-exact (native=0x{fpNative:X16}, csharp=0x{fpManaged:X16}, " +
                    $"rngN={campNative.Random.State}, rngC={campManaged.Random.State}, " +
                    $"stabN={campNative.Nations[1].Stability:R}, stabC={campManaged.Nations[1].Stability:R})");
            }
            _passed++;
        }

        Check(campNative.Random.State == campManaged.Random.State,
            $"35-day active war Random.State matches bit-exact ({campNative.Random.State})");
        Check(campNative.ElapsedDays == 35 && campManaged.ElapsedDays == 35,
            $"both simulations completed 35 war days (nativeAvailable={nativeAvailable})");
    }
}
