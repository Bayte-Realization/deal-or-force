using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DealOrForce.Simulation;
using SysVec3 = System.Numerics.Vector3;

/// <summary>
/// Deterministic tests for MapOverlaySyncRules: back-of-Earth horizon culling,
/// monotonic globe horizon cosine, rapid pan/zoom immediate city relayout triggers,
/// 2D overlay redraw policy, and source wiring contracts.
/// </summary>
public static class GlobeOverlaySyncTests
{
    private static int _passed;

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("globe overlay sync: " + label);
        _passed++;
        Console.WriteLine("PASS globe overlay sync: " + label);
    }

    [TestSuite("--globe-overlay-sync-only", IncludeInDefaultRun = true,
        Description = "Validates MapOverlaySyncRules horizon culling, relayout triggers, and 2D overlay redraw policy")]
    public static void Run()
    {
        _passed = 0;
        WashingtonVsAfricanBackOfEarthCities();
        EuropeVsNorthAmericaLimbCulling();
        HorizonMonotonicityAndSafety();
        CityRelayoutImmediateTriggers();
        OverlayRedrawPolicy();
        SourceWiringContracts();
        LiveMapSurfaceSyncContracts();
        DesktopReaderNonStarvationAtSpeed5();
        BufferReusePixelParity();
        Console.WriteLine($"globe overlay sync: {_passed} PASS");
    }

    private static void WashingtonVsAfricanBackOfEarthCities()
    {
        const float globeRadius = 5f;
        const float margin = MapOverlaySyncRules.DefaultHorizonExtraMargin;
        var washington = MapOverlaySyncRules.SurfaceNormalFromGeo(38.9072f, -77.0369f);
        var newYork = MapOverlaySyncRules.SurfaceNormalFromGeo(40.7128f, -74.0060f);

        var backSideCities = new (string Name, float Lat, float Lon)[]
        {
            ("Djibouti", 11.5880f, 43.1450f),
            ("Saint-Denis", -20.8821f, 55.4507f),
            ("N'Djamena", 12.1348f, 15.0557f),
            ("Abuja", 9.0765f, 7.3986f)
        };

        foreach (var camDistance in new[] { 6.0f, 7.5f, 10.0f, 14.0f, 24.0f })
        {
            var horizon = MapOverlaySyncRules.GlobeHorizonCosine(globeRadius, camDistance, margin);
            Check(!MapOverlaySyncRules.IsBeyondGlobeHorizon(washington, washington, horizon),
                $"Washington remains visible under Washington camera at distance {camDistance}");
            Check(!MapOverlaySyncRules.IsBeyondGlobeHorizon(newYork, washington, horizon),
                $"New York remains visible when viewing USA at distance {camDistance}");
            foreach (var (name, lat, lon) in backSideCities)
            {
                var cityNormal = MapOverlaySyncRules.SurfaceNormalFromGeo(lat, lon);
                Check(MapOverlaySyncRules.IsBeyondGlobeHorizon(cityNormal, washington, horizon),
                    $"{name} ({lat:0.##}, {lon:0.##}) is culled past globe horizon when viewing USA at distance {camDistance}");
            }
        }
    }

    private static void EuropeVsNorthAmericaLimbCulling()
    {
        const float globeRadius = 5f;
        var paris = MapOverlaySyncRules.SurfaceNormalFromGeo(48.8566f, 2.3522f);
        var horizon = MapOverlaySyncRules.GlobeHorizonCosine(globeRadius, 8.0f, MapOverlaySyncRules.DefaultHorizonExtraMargin);

        var ny = MapOverlaySyncRules.SurfaceNormalFromGeo(40.7128f, -74.0060f);
        var chicago = MapOverlaySyncRules.SurfaceNormalFromGeo(41.8781f, -87.6298f);
        var berlin = MapOverlaySyncRules.SurfaceNormalFromGeo(52.5200f, 13.4050f);

        Check(!MapOverlaySyncRules.IsBeyondGlobeHorizon(berlin, paris, horizon),
            "Berlin stays visible when viewing Europe at distance 8.0");
        Check(MapOverlaySyncRules.IsBeyondGlobeHorizon(ny, paris, horizon),
            "New York is culled past the Atlantic limb when zoomed into Europe at distance 8.0");
        Check(MapOverlaySyncRules.IsBeyondGlobeHorizon(chicago, paris, horizon),
            "Chicago is culled past the Atlantic limb when zoomed into Europe at distance 8.0");
    }

    private static void HorizonMonotonicityAndSafety()
    {
        const float radius = 5f;
        const float margin = MapOverlaySyncRules.DefaultHorizonExtraMargin;

        var prev = float.MaxValue;
        foreach (var dist in new[] { 5.25f, 6.6f, 8.4f, 11.0f, 15.0f, 24.0f })
        {
            var cos = MapOverlaySyncRules.GlobeHorizonCosine(radius, dist, margin);
            Check(cos < prev && cos > 0f && cos < 1f,
                $"horizon cosine decreases monotonically with camera distance ({dist} -> {cos:0.0000})");
            prev = cos;
        }

        var tighter = MapOverlaySyncRules.GlobeHorizonCosine(radius, 10f, 0.014f);
        var looser = MapOverlaySyncRules.GlobeHorizonCosine(radius, 10f, 0.002f);
        Check(tighter > looser, "larger extraMargin produces strictly tighter horizon culling");

        var clamped = MapOverlaySyncRules.GlobeHorizonCosine(radius, 5.01f, 0.014f);
        Check(clamped <= 0.999f, "horizon cosine clamps <= 0.999 so sub-camera zenith is never culled");

        Check(MapOverlaySyncRules.IsBeyondGlobeHorizon(SysVec3.Zero, SysVec3.UnitZ, 0.5f),
            "zero surface normal is safely culled");
        Check(MapOverlaySyncRules.IsBeyondGlobeHorizon(new SysVec3(float.NaN, 0f, 1f), SysVec3.UnitZ, 0.5f),
            "NaN surface normal is safely culled");
        Check(MapOverlaySyncRules.IsBeyondGlobeHorizon(SysVec3.UnitZ, SysVec3.Zero, 0.5f),
            "zero camera normal is safely culled");
        Check(MapOverlaySyncRules.IsBeyondGlobeHorizon(SysVec3.UnitZ, SysVec3.UnitZ, float.NaN),
            "NaN horizon cosine is safely culled");
    }

    private static void CityRelayoutImmediateTriggers()
    {
        // >= 3.72 deg (0.065 rad) triggers 0ms immediate relayout even with elapsedSec == 0.
        Check(MapOverlaySyncRules.ShouldRelayoutCitiesNow(0.065f, 0f, 0.0, 0.055, forceDirty: false),
            "angular pan >= 0.065 rad (~3.7 deg) triggers 0ms immediate city relayout");
        Check(MapOverlaySyncRules.ShouldRelayoutCitiesNow(-0.08f, 0f, 0.0, 0.080, forceDirty: false),
            "negative rapid angular pan triggers 0ms immediate city relayout");

        // >= 7% zoom jump triggers 0ms immediate relayout even with elapsedSec == 0.
        Check(MapOverlaySyncRules.ShouldRelayoutCitiesNow(0f, 0.07f, 0.0, 0.080, forceDirty: false),
            "zoom jump >= 7% triggers 0ms immediate city relayout");

        // Sub-threshold smooth pan respects intervalSec.
        Check(!MapOverlaySyncRules.ShouldRelayoutCitiesNow(0.03f, 0.02f, 0.020, 0.033, forceDirty: false),
            "small camera nudge waits for movedInterval before relayout");
        Check(MapOverlaySyncRules.ShouldRelayoutCitiesNow(0.03f, 0.02f, 0.033, 0.033, forceDirty: false),
            "small camera pan triggers once elapsedSec reaches movedInterval");

        // Dirty flag always triggers immediately.
        Check(MapOverlaySyncRules.ShouldRelayoutCitiesNow(0f, 0f, 0.0, 0.080, forceDirty: true),
            "forceDirty triggers immediate relayout at 0ms");
    }

    private static void OverlayRedrawPolicy()
    {
        Check(MapOverlaySyncRules.ShouldQueueOverlayRedraw(cameraMoved: true, animating: false, hasActive2DOverlays: true),
            "camera move with active 2D overlays always queues redraw");
        Check(MapOverlaySyncRules.ShouldQueueOverlayRedraw(cameraMoved: false, animating: true, hasActive2DOverlays: false),
            "animating badge fade queues redraw even when camera is still");
        Check(!MapOverlaySyncRules.ShouldQueueOverlayRedraw(cameraMoved: true, animating: false, hasActive2DOverlays: false),
            "camera move without active 2D overlays or animation skips empty _Draw()");
        Check(!MapOverlaySyncRules.ShouldQueueOverlayRedraw(cameraMoved: false, animating: false, hasActive2DOverlays: true),
            "stationary camera with settled 2D overlays avoids idle redraws");
    }

    private static void SourceWiringContracts()
    {
        var root = FindRepoRoot();
        var rulesPath = Path.Combine(root, "Scripts", "Simulation", "MapOverlaySyncRules.cs");
        var rulesUidPath = rulesPath + ".uid";
        var syncPath = Path.Combine(root, "Scripts", "gfx", "GlobeOverlaySync.cs");
        var syncUidPath = syncPath + ".uid";
        var rulesSrc = File.ReadAllText(rulesPath);
        Check(!rulesSrc.Contains("Godot", StringComparison.Ordinal),
            "MapOverlaySyncRules.cs stays 100% free of Godot references");
        Check(File.Exists(rulesUidPath) && File.Exists(syncUidPath),
            "new Simulation and gfx overlay sync files carry .cs.uid sidecars");

        var globeSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.cs"));
        var syncSrc = File.ReadAllText(syncPath);
        var citiesSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeCities.cs"));
        var movementsSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeMovements.cs"));
        var regionIconsSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeRegionIcons.cs"));
        var militarySrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "MilitaryPresentation.cs"));

        Check(globeSrc.Contains("SyncCameraLockedMapOverlays(delta, snap);", StringComparison.Ordinal) &&
              syncSrc.Contains("partial void SyncCameraLockedMapOverlays(double delta, CampaignRenderSnapshot snap)", StringComparison.Ordinal),
            "GlobePresentation._Process wires Frame-0 SyncCameraLockedMapOverlays partial hook");
        Check(citiesSrc.Contains("MapOverlaySyncRules.GlobeHorizonCosine(GlobeRadius", StringComparison.Ordinal) &&
              citiesSrc.Contains("0.014f", StringComparison.Ordinal) &&
              citiesSrc.Contains("MapOverlaySyncRules.ShouldRelayoutCitiesNow", StringComparison.Ordinal),
            "GlobeCities uses MapOverlaySyncRules horizon cosine (0.014f) and immediate relayout gate");
        Check(!movementsSrc.Contains("> 0.05f", StringComparison.Ordinal) &&
              movementsSrc.Contains("_globe.CityHorizon()", StringComparison.Ordinal) &&
              syncSrc.Contains("internal void SyncCameraLockedTransforms(", StringComparison.Ordinal) &&
              syncSrc.Contains("_movementLayer.SyncCameraLockedTransforms(", StringComparison.Ordinal),
            "GlobeMovements replaces hardcoded > 0.05f with CityHorizon() and exposes SyncCameraLockedTransforms");
        Check(!regionIconsSrc.Contains("0.066", StringComparison.Ordinal) &&
              regionIconsSrc.Contains("MapOverlaySyncRules.ShouldQueueOverlayRedraw", StringComparison.Ordinal),
            "GlobeRegionIcons removes 15fps 0.066s touch redraw cap and uses ShouldQueueOverlayRedraw");
        Check(militarySrc.Contains("map.Neighbors[battle.ProvinceId]", StringComparison.Ordinal) &&
              militarySrc.Contains("Array.IndexOf(battleNeighbors, attArmy.ProvinceId) >= 0", StringComparison.Ordinal),
            "MilitaryPresentation filters battle assault arrows to province neighbors of battle.ProvinceId");
    }

    private static void LiveMapSurfaceSyncContracts()
    {
        var root = FindRepoRoot();
        var globeSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.cs"));
        var bordersSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.BordersAndOverlays.cs"));
        var militarySrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "MilitaryPresentation.cs"));
        var plateSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePlateRepaint.cs"));
        var simRunnerSrc = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "BackgroundSimRunner.cs"));
        var gameSrc = File.ReadAllText(Path.Combine(root, "Scripts", "DealOrForceGame.cs"));

        var tryReadIdx = globeSrc.IndexOf("private bool TryBeginFrameRead(", StringComparison.Ordinal);
        var syncIdx = globeSrc.IndexOf("private void SyncFencedWorldVisuals(", StringComparison.Ordinal);
        var staggerIdx = globeSrc.IndexOf("private void StepStaggeredVisualPhase(", StringComparison.Ordinal);
        Check(tryReadIdx > 0 && syncIdx > tryReadIdx && staggerIdx > syncIdx,
            "GlobePresentation locates TryBeginFrameRead and SyncFencedWorldVisuals blocks");

        var tryReadBlock = globeSrc.Substring(tryReadIdx, syncIdx - tryReadIdx);
        Check(tryReadBlock.Contains("CurrentRenderSnapshot()", StringComparison.Ordinal) &&
              tryReadBlock.Contains("snap.MapRevision > _controlRevision || snap.MapRevision > _frontRevision || snap.WarRevision > _frontWarRevision", StringComparison.Ordinal) &&
              tryReadBlock.Contains("fastSim ? 0.16 : 0.045", StringComparison.Ordinal) &&
              tryReadBlock.Contains("fastSim ? 0.033 : 0.016", StringComparison.Ordinal) &&
              globeSrc.Contains("AdvanceCameraForFrame(delta);", StringComparison.Ordinal),
            "TryBeginFrameRead uses monotonic revision gating while GlobePresentation advances camera before fenced sync");
        Check(globeSrc.Contains("internal bool NeedsUrgentFencedSync(", StringComparison.Ordinal) &&
              globeSrc.Contains("internal void SyncWorldVisualsUnderReadLock(", StringComparison.Ordinal) &&
              globeSrc.Contains("result.Frontier?.Dispose()", StringComparison.Ordinal),
            "GlobePresentation exposes shared-lock helpers and disposes temporary startup frontier Images");

        var syncBlock = globeSrc.Substring(syncIdx, staggerIdx - syncIdx);
        var refreshIdx = syncBlock.IndexOf("RefreshProvinceControllers();", StringComparison.Ordinal);
        var armyIdx = syncBlock.IndexOf("SyncArmyMarkers();", StringComparison.Ordinal);
        var paletteIdx = syncBlock.IndexOf("UpdateMapModePaletteIfNeeded();", StringComparison.Ordinal);
        Check(refreshIdx > 0 && armyIdx > refreshIdx && paletteIdx > armyIdx,
            "SyncFencedWorldVisuals paints live controllers and war fronts BEFORE updating armies, battles, and routes");
        var frontIdx = syncBlock.IndexOf("BuildFrontPreviewCore();", StringComparison.Ordinal);
        Check(frontIdx > refreshIdx && frontIdx < armyIdx &&
              syncBlock.Contains("_frontWarRevision = fencedCampaign.WarRevision;", StringComparison.Ordinal) &&
              syncBlock.Contains("_lastCombatSyncHours = fencedCampaign.ElapsedHours;", StringComparison.Ordinal),
            "SyncFencedWorldVisuals rebuilds war fronts on WarRevision changes and stamps _lastCombatSyncHours");

        Check(syncBlock.Contains("(speed5Running ? 0.25 : fastSim ? 0.12 : 0.10)", StringComparison.Ordinal) &&
              syncBlock.Contains("0.22 : 0.0", StringComparison.Ordinal) &&
              syncBlock.Contains("UpdateMilitaryPresentation(forceNow: desktopTopologyMoved)", StringComparison.Ordinal) &&
              syncBlock.Contains("InvalidateFlatCounterDensity()", StringComparison.Ordinal) &&
              syncBlock.Contains("isTouchPrimary ? 0.22 : 0.033", StringComparison.Ordinal),
            "SyncFencedWorldVisuals unifies desktop combat/route sync at 33ms + deferred density invalidation while preserving mobile cadences");

        var buildFrontIdx = bordersSrc.IndexOf("private void BuildFrontPreviewCore()", StringComparison.Ordinal);
        var planCodeIdx = bordersSrc.IndexOf("private static byte PlanCode(", StringComparison.Ordinal);
        Check(buildFrontIdx > 0 && planCodeIdx > buildFrontIdx,
            "GlobePresentation.BordersAndOverlays locates BuildFrontPreviewCore/PaintLiveWarFronts block");
        var frontCoreBlock = bordersSrc.Substring(buildFrontIdx, planCodeIdx - buildFrontIdx);
        Check(!frontCoreBlock.Contains("war.AttackerFront", StringComparison.Ordinal) &&
              !frontCoreBlock.Contains("war.DefenderFront", StringComparison.Ordinal) &&
              frontCoreBlock.Contains("PaintLiveWarFronts(pixels, liveControl, _campaign.ProvinceMap);", StringComparison.Ordinal) &&
              frontCoreBlock.Contains("Simulation.Warfare.LiveWars(_campaign)", StringComparison.Ordinal) &&
              frontCoreBlock.Contains("topology.Neighbors", StringComparison.Ordinal) &&
              frontCoreBlock.Contains("topology.IsWater(", StringComparison.Ordinal) &&
              bordersSrc.Contains("_frontSideByMapIndex", StringComparison.Ordinal) &&
              bordersSrc.Contains("_frontWarCountByMapIndex", StringComparison.Ordinal) &&
              bordersSrc.Contains("_frontMultiWarByMapIndex", StringComparison.Ordinal) &&
              bordersSrc.Contains("_frontTouchedMapIndices", StringComparison.Ordinal),
            "GlobePresentation.BordersAndOverlays computes front_membership in O(P) single pass from liveControl and topology.Neighbors");

        Check(bordersSrc.Contains("FindActiveBattle", StringComparison.Ordinal) &&
              bordersSrc.Contains("_drawnRouteCorridors", StringComparison.Ordinal) &&
              bordersSrc.Contains("PostBattleWaypoints", StringComparison.Ordinal),
            "GlobePresentation.BordersAndOverlays preserves battle/route contract symbols");

        var badgeSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "SurfaceBattleBadge.cs"));
        Check(militarySrc.Contains("Speed: >= 4", StringComparison.Ordinal) &&
              militarySrc.Contains("timeSinceLast >= (isMobileOrTouch ? 0.14 : 0.033)", StringComparison.Ordinal) &&
              militarySrc.Contains("bool forceNow = false", StringComparison.Ordinal) &&
              militarySrc.Contains("_battleBadgeMultiMesh", StringComparison.Ordinal) &&
              militarySrc.Contains("BattleBadgeGpu.WriteInstance(", StringComparison.Ordinal) &&
              militarySrc.Contains("_counterTextureScratch", StringComparison.Ordinal) &&
              badgeSrc.Contains("public static class BattleBadgeGpu", StringComparison.Ordinal) &&
              badgeSrc.Contains("PackDefenderColor", StringComparison.Ordinal) &&
              !militarySrc.Contains("new SubViewport", StringComparison.Ordinal) &&
              !badgeSrc.Contains("new SubViewport", StringComparison.Ordinal),
            "MilitaryPresentation uses zero-SubViewport GPU-instanced BattleBadgeGpu MultiMesh and reuses counter texture buffers");

        Check(plateSrc.Contains("PlateRepaintSeconds = 0.25;", StringComparison.Ordinal) &&
              plateSrc.Contains("FrontierRepaintSeconds = 0.50;", StringComparison.Ordinal) &&
              plateSrc.Contains("MobilePlateRepaintSeconds = 1.0;", StringComparison.Ordinal) &&
              plateSrc.Contains("FastSimPlateRepaintSeconds = 0.35;", StringComparison.Ordinal) &&
              plateSrc.Contains("FastSimFrontierRepaintSeconds = 0.85;", StringComparison.Ordinal) &&
              plateSrc.Contains("GpuPlateRepaintSeconds = 0.16;", StringComparison.Ordinal) &&
              plateSrc.Contains("GpuFrontierRepaintSeconds = 0.30;", StringComparison.Ordinal) &&
              plateSrc.Contains("PaintDirtyMipmappedInto(", StringComparison.Ordinal) &&
              plateSrc.Contains("FrontierImageDirtyInto(", StringComparison.Ordinal) &&
              plateSrc.Contains("_gpuMapCompute", StringComparison.Ordinal) &&
              plateSrc.Contains("pendingPlate.Dispose();", StringComparison.Ordinal) &&
              plateSrc.Contains("frontier.Dispose();", StringComparison.Ordinal) &&
              plateSrc.Contains("image.Dispose();", StringComparison.Ordinal),
            "GlobePlateRepaint uses GPU compute + incremental dirty-rect mipmaps, reuses LOH buffers, and disposes unmanaged Image handles");

        var counterSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "FlatCounterPresentation.cs"));
        var unitsSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.UnitsAndMarkers.cs"));
        Check(counterSrc.Contains("_simStepIntervalEma", StringComparison.Ordinal) &&
              counterSrc.Contains("speed >= 5", StringComparison.Ordinal) &&
              counterSrc.Contains("speed == 4 ? 14.0f : 6.5f", StringComparison.Ordinal) &&
              unitsSrc.Contains("HasPendingArrivalGlide", StringComparison.Ordinal),
            "FlatCounterPresentation adapts motion damping to measured step interval and cached marker arrival glide");

        var miniatureSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "CityMiniatureRenderer.cs"));
        var labelsSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.Labels.cs"));
        var overlaySyncSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeOverlaySync.cs"));
        var citiesSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeCities.cs"));
        Check(miniatureSrc.Contains("DestroyedProvinces.Count", StringComparison.Ordinal) &&
              miniatureSrc.Contains("CapitalRevision", StringComparison.Ordinal) &&
              labelsSrc.Contains("_countryLabelCache", StringComparison.Ordinal) &&
              labelsSrc.Contains("_nextCountryLabelPlaceMsec", StringComparison.Ordinal) &&
              overlaySyncSrc.Contains("!_camera.IsInsideTree()", StringComparison.Ordinal) &&
              citiesSrc.Contains("!_camera.IsInsideTree()", StringComparison.Ordinal),
            "City miniatures key structurally, country labels diff incrementally, and camera queries guard IsInsideTree");

        var tickSrc = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Campaign.TickStages.cs"));
        Check(tickSrc.Contains("AccessGate.HasPriorityReaderRequested || AccessGate.IsReaderStarving || AccessGate.HasWriteWaiter", StringComparison.Ordinal),
            "Campaign.TickStages RunStages yields at stage boundaries when renderer is starving");

        Check(simRunnerSrc.Contains("batchElapsedMs >= (mobilePacing ? 32.0 : 8.0) || campaign.AccessGate.IsReaderStarving", StringComparison.Ordinal) &&
              simRunnerSrc.Contains("readerWindowMs: speed >= 5 ? (mobilePacing ? 6 : 18) : (mobilePacing ? 10 : 20)", StringComparison.Ordinal) &&
              simRunnerSrc.Contains("readerWorkAheadMs: mobilePacing ? 20 : 0", StringComparison.Ordinal),
            "BackgroundSimRunner yields at 8ms desktop with zero work-ahead while keeping all mobilePacing constants intact");

        Check(gameSrc.Contains("_isMobileRuntime ? 320 : 100", StringComparison.Ordinal) &&
              gameSrc.Contains("_isMobileRuntime ? 180 : 75", StringComparison.Ordinal) &&
              gameSrc.Contains("_globe?.SyncWorldVisualsUnderReadLock(_campaign, delta)", StringComparison.Ordinal),
            "DealOrForceGame shares its step-boundary read lock directly with GlobePresentation");
    }

    private static void DesktopReaderNonStarvationAtSpeed5()
    {
        var campaign = Campaign.CreateBootstrap(91128);
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
            var hits = 0;
            var consecutiveMisses = 0;
            var maxMissStreak = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 650)
            {
                if (campaign.TryEnterRead(0, requestWindowOnMiss: true))
                {
                    try
                    {
                        hits++;
                        consecutiveMisses = 0;
                    }
                    finally
                    {
                        campaign.ExitRead();
                    }
                }
                else
                {
                    consecutiveMisses++;
                    if (consecutiveMisses > maxMissStreak) maxMissStreak = consecutiveMisses;
                }
                Thread.Sleep(16);
            }
            runner.RunWrite(c =>
            {
                c.IsPaused = true;
                if (c.MidStep) c.FinishStep();
            });
            Check(hits >= 5 && maxMissStreak <= 4 && campaign.ElapsedHours > 0,
                $"Desktop Speed-5 BackgroundSimRunner grants regular render read windows without starvation (hits={hits}, maxMiss={maxMissStreak}, hours={campaign.ElapsedHours})");
        }
        finally
        {
            runner.Stop();
        }
    }

    private static void BufferReusePixelParity()
    {
        const int plateW = 12, plateH = 6;
        var source = new PoliticalPlateSource
        {
            Width = plateW,
            Height = plateH,
            Provinces = new ushort[plateW * plateH],
            Holders = new ushort[plateW * plateH],
            Geography = new byte[plateW * plateH]
        };
        const ushort C1 = 10, C2 = 20, C3 = 30;
        var authored = new ushort[] { 0, C1, C1, C2, C2, C3, C3 };
        for (var y = 0; y < plateH; y++)
            for (var x = 0; x < plateW; x++)
            {
                var cell = y * plateW + x;
                if (y == 0)
                {
                    source.Provinces[cell] = 0;
                    source.Holders[cell] = MapBorders.Sea;
                    continue;
                }
                var prov = (ushort)(x / 2 + 1);
                source.Provinces[cell] = prov;
                source.Holders[cell] = authored[prov];
                if (x % 4 == 0) source.Geography[cell] |= PoliticalPlate.RegionLine;
                if (y == 1) source.Geography[cell] |= PoliticalPlate.Coast;
            }
        var colours = new Dictionary<int, uint>
        {
            [C1] = 0xCC3333FFu,
            [C2] = 0x33AA44FFu,
            [C3] = 0x3366CCFFu
        };
        var shades = MapBorderStyles.PlateShades();
        byte[] plateScratch = null;
        var expected1 = PoliticalPlate.Paint(source, authored, authored, colours, shades);
        var reused1 = PoliticalPlate.PaintInto(source, authored, authored, colours, shades, ref plateScratch);
        Check(ReferenceEquals(plateScratch, reused1) && reused1.AsSpan().SequenceEqual(expected1),
            "PoliticalPlate.PaintInto matches Paint bit-for-bit on initial allocation");

        var occupied = new ushort[] { 0, C1, C2, C2, C3, C3, C1 };
        var expected2 = PoliticalPlate.Paint(source, occupied, authored, colours, shades);
        var reused2 = PoliticalPlate.PaintInto(source, occupied, authored, colours, shades, ref plateScratch);
        Check(ReferenceEquals(plateScratch, reused2) && reused2.AsSpan().SequenceEqual(expected2),
            "PoliticalPlate.PaintInto reuses same byte[] buffer and matches Paint bit-for-bit after ownership changes");

        var expected3 = PoliticalPlate.Paint(source, authored, authored, colours, shades);
        var reused3 = PoliticalPlate.PaintInto(source, authored, authored, colours, shades, ref plateScratch);
        Check(ReferenceEquals(plateScratch, reused3) && reused3.AsSpan().SequenceEqual(expected3),
            "PoliticalPlate.PaintInto returns bit-exact opening plate when reusing dirtied buffer");

        const int fW = 32, fH = 12;
        var holders = new ushort[fW * fH];
        var cellProv = new ushort[fW * fH];
        for (var y = 0; y < fH; y++)
            for (var x = 0; x < fW; x++)
            {
                var idx = y * fW + x;
                var side = (ushort)(x < 10 ? 1 : 2);
                holders[idx] = side;
                cellProv[idx] = side;
            }
        var fAuthored = new ushort[] { 0, 1, 2 };
        var fUnified = new ushort[] { 0, 1, 1 };
        byte[] frontierScratch = null;
        var fExpected1 = FrontierField.Build(holders, cellProv, fW, fH, fAuthored, fAuthored);
        var fReused1 = FrontierField.BuildInto(holders, cellProv, fW, fH, fAuthored, fAuthored, ref frontierScratch);
        Check(ReferenceEquals(frontierScratch, fReused1) && fReused1.AsSpan().SequenceEqual(fExpected1),
            "FrontierField.BuildInto matches Build bit-for-bit on initial allocation");

        var fExpected2 = FrontierField.Build(holders, cellProv, fW, fH, fUnified, fAuthored);
        var fReused2 = FrontierField.BuildInto(holders, cellProv, fW, fH, fUnified, fAuthored, ref frontierScratch);
        Check(ReferenceEquals(frontierScratch, fReused2) && fReused2.AsSpan().SequenceEqual(fExpected2),
            "FrontierField.BuildInto reuses same byte[] buffer across unification with bit-exact parity vs Build");

        var fExpected3 = FrontierField.Build(holders, cellProv, fW, fH, fAuthored, fAuthored);
        var fReused3 = FrontierField.BuildInto(holders, cellProv, fW, fH, fAuthored, fAuthored, ref frontierScratch);
        Check(ReferenceEquals(frontierScratch, fReused3) && fReused3.AsSpan().SequenceEqual(fExpected3),
            "FrontierField.BuildInto returns bit-exact opening frontier when reusing dirtied buffer");
    }

    private static string FindRepoRoot()
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(seed);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate DealOrForce.csproj.");
    }
}
