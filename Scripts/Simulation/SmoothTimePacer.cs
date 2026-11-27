using System;
using System.Runtime;

namespace DealOrForce.Simulation;

/// <summary>
/// User-configurable Smooth Time 2.0 pacing profile. Controls ONLY wall-clock step spacing,
/// worker write-lock slices, and main-thread HUD/fenced-read cadence — never simulation order,
/// RNG, saved state, or <see cref="CampaignFingerprint"/>.
/// </summary>
public enum SmoothTimeMode
{
    /// <summary>Raw uncapped stepping with no smooth pacing or frame-budget shielding.</summary>
    Off = 0,
    /// <summary>Balanced default: adaptive EMA day cadence, short write slices, and responsive camera.</summary>
    Adaptive = 1,
    /// <summary>Strict 60 FPS camera lock with ultra-even metronomic day pacing and aggressive HUD shielding.</summary>
    Cinema60Fps = 2,
    /// <summary>Maximum Speed-8 throughput with lock-free safety bounds and no artificial inter-day wait.</summary>
    MaxThroughput = 3
}

/// <summary>Worker-thread slicing, snapshot coalescing, and backpressure parameters for one speed/mode pair.</summary>
public readonly record struct WorkerSlicePolicy(
    double StageSliceMs,
    double WriteSliceMs,
    double ReaderYieldAfterMs,
    int ReaderWindowMs,
    int ReaderWorkAheadMs,
    double SnapshotMinIntervalMs,
    double MapChangeCoalesceMs,
    double HudSnapshotMinIntervalMs,
    int MaxBatchSteps,
    int MaxVisualLeadSnapshots,
    double MaxBackpressureWaitMs,
    bool LowerWorkerPriority);

/// <summary>Main-thread presentation and fenced-read shield parameters for one speed/mode pair.</summary>
public readonly record struct MainThreadFramePolicy(
    ulong LiveReadIntervalMs,
    ulong FastHudRefreshGapMs,
    double FrameShieldBudgetMs,
    int MaxDeferredFencedFrames,
    int CountryNameRefreshDays);

/// <summary>
/// Smooth Time 2.0 adaptive real-time pacer and frame-shield policy engine.
/// Pure simulation-layer C# with zero Godot dependencies and zero static mutable state.
///
/// Tracks an asymmetric exponential moving average of actual day execution cost plus a positive
/// spike-jitter envelope so heavy weekly/monthly ticks (economy, demography, AI, landless
/// movements) are absorbed into a steady cadence instead of stalling and then firing multi-day
/// catch-up bursts. All pacing is wall-clock only and leaves campaign determinism bit-identical.
/// </summary>
public sealed class SmoothTimePacer
{
    public const string LegacySettingId = "smooth_ticks";
    public const string ModeSettingId = "smooth_time_mode";

    private const int SpeedFastTier = 6;
    private const int SpeedHigh = 7;
    private const int SpeedMax = 8;
    private const int MinTopSpeed = 5;
    private const int MinFastSpeed = 4;

    private const double EmaAttackAlpha = 0.25;
    private const double EmaDecayAlpha = 0.08;
    private const double JitterAttackAlpha = 0.30;
    private const double JitterDecayAlpha = 0.10;
    private const double WorkSampleCeilingMs = 250.0;

    private const double AdaptiveDesktopMinDayMs = 14.0;
    private const double AdaptiveDesktopMaxDayMs = 45.0;
    private const double AdaptiveMobileMinDayMs = 22.0;
    private const double AdaptiveMobileMaxDayMs = 60.0;
    private const double AdaptiveJitterWeight = 0.35;

    private const double CinemaDesktopMinDayMs = 24.0;
    private const double CinemaDesktopMaxDayMs = 75.0;
    private const double CinemaMobileMinDayMs = 34.0;
    private const double CinemaMobileMaxDayMs = 95.0;
    private const double CinemaWorkScale = 1.15;
    private const double CinemaJitterWeight = 0.65;

    private const double DebtNegFloorFactor = 0.35;
    private const double DebtPosCeilingFactor = 0.20;
    private const double MinTargetFactor = 0.50;
    private const double ExplicitOvershootCapFactor = 0.50;
    private const double ExplicitSpanCapFactor = 4.0;
    private const double ExplicitEmaKeep = 0.65;
    private const double ExplicitEmaNew = 0.35;

    private const double StageAdaptiveDeskMax = 4.5;
    private const double StageAdaptiveDeskHigh = 3.5;
    private const double StageAdaptiveDeskNorm = 2.5;
    private const double StageAdaptiveMobMax = 6.5;
    private const double StageAdaptiveMobHigh = 5.0;
    private const double StageAdaptiveMobNorm = 3.0;

    private const double StageCinemaDesk = 2.0;
    private const double StageCinemaMob = 3.0;
    private const double StageMaxDeskMax = 8.0;
    private const double StageMaxDeskHigh = 6.0;
    private const double StageMaxDeskNorm = 3.5;
    private const double StageMaxMobMax = 12.0;
    private const double StageMaxMobHigh = 8.0;
    private const double StageMaxMobNorm = 4.5;
    private const double StageOffDeskMax = 10.0;
    private const double StageOffMobMax = 14.0;

    private const double SliceAdaptiveDeskMax = 8.0;
    private const double SliceAdaptiveDeskHigh = 7.0;
    private const double SliceAdaptiveDeskNorm = 5.0;
    private const double SliceAdaptiveMobMax = 14.0;
    private const double SliceAdaptiveMobHigh = 11.0;
    private const double SliceAdaptiveMobNorm = 6.0;

    private const double SliceCinemaDeskMax = 4.5;
    private const double SliceCinemaDeskHigh = 4.0;
    private const double SliceCinemaDeskNorm = 3.0;
    private const double SliceCinemaMobMax = 8.0;
    private const double SliceCinemaMobHigh = 6.5;
    private const double SliceCinemaMobNorm = 4.0;

    private const double SliceMaxDeskMax = 16.0;
    private const double SliceMaxDeskHigh = 12.0;
    private const double SliceMaxDeskNorm = 6.0;
    private const double SliceMaxMobMax = 36.0;
    private const double SliceMaxMobHigh = 18.0;
    private const double SliceMaxMobNorm = 6.0;

    private const double SliceOffDeskMax = 24.0;
    private const double SliceOffMobMax = 56.0;

    private const double YieldAdaptiveDesk = 4.0;
    private const double YieldAdaptiveMob = 10.0;
    private const double YieldCinemaDesk = 2.0;
    private const double YieldCinemaMob = 5.0;
    private const double YieldFastDesk = 8.0;
    private const double YieldFastMob = 24.0;
    private const double YieldOffMob = 32.0;

    private const int WindowDeskAdaptive = 16;
    private const int WindowDeskCinema = 18;
    private const int WindowDeskFast = 18;
    private const int WindowDeskNormal = 20;
    private const int WindowMobAdaptive = 12;
    private const int WindowMobCinema = 14;
    private const int WindowMobMax = 6;
    private const int WindowMobNormal = 10;
    private const int WorkAheadMobAdaptive = 10;
    private const int WorkAheadMobCinema = 0;
    private const int WorkAheadMobFast = 20;

    private const double SnapAdaptiveDesk = 20.0;
    private const double SnapAdaptiveMob = 28.0;
    private const double SnapCinemaDesk = 26.0;
    private const double SnapCinemaMob = 34.0;
    private const double SnapFastDesk = 14.0;
    private const double SnapFastMob = 24.0;
    private const double SnapNormal = 12.0;

    private const double MapCoalesceAdaptiveDesk = 16.0;
    private const double MapCoalesceAdaptiveMob = 24.0;
    private const double MapCoalesceCinemaDesk = 24.0;
    private const double MapCoalesceCinemaMob = 33.0;
    private const double MapCoalesceFastDesk = 12.0;
    private const double MapCoalesceFastMob = 20.0;

    private const double HudSnapAdaptiveDesk = 90.0;
    private const double HudSnapAdaptiveMob = 160.0;
    private const double HudSnapCinemaDesk = 150.0;
    private const double HudSnapCinemaMob = 240.0;
    private const double HudSnapFastDesk = 60.0;
    private const double HudSnapFastMob = 120.0;

    private const int BatchStepsFast = 8;
    private const int BatchStepsOff = 16;
    private const int LeadAdaptive = 2;
    private const int LeadCinema = 1;
    private const int LeadLegacySpeed7 = 3;
    private const double BackpressureAdaptiveMs = 24.0;
    private const double BackpressureCinemaMs = 34.0;
    private const double BackpressureLegacyMs = 36.0;

    private const ulong LiveReadAdaptiveDeskMax = 140UL;
    private const ulong LiveReadAdaptiveDeskNorm = 90UL;
    private const ulong LiveReadAdaptiveMobMax = 360UL;
    private const ulong LiveReadAdaptiveMobNorm = 220UL;
    private const ulong LiveReadCinemaDeskMax = 200UL;
    private const ulong LiveReadCinemaDeskNorm = 120UL;
    private const ulong LiveReadCinemaMobMax = 440UL;
    private const ulong LiveReadCinemaMobNorm = 280UL;
    private const ulong LiveReadFastDeskMax = 100UL;
    private const ulong LiveReadFastDeskNorm = 75UL;
    private const ulong LiveReadFastMobMax = 320UL;
    private const ulong LiveReadFastMobNorm = 180UL;

    private const ulong HudGapAdaptiveDesk = 160UL;
    private const ulong HudGapAdaptiveMob = 260UL;
    private const ulong HudGapCinemaDesk = 220UL;
    private const ulong HudGapCinemaMob = 360UL;
    private const ulong HudGapFastDesk = 125UL;
    private const ulong HudGapFastMob = 200UL;

    private const double ShieldAdaptiveDeskMs = 6.5;
    private const double ShieldAdaptiveMobMs = 8.5;
    private const double ShieldCinemaDeskMs = 4.5;
    private const double ShieldCinemaMobMs = 6.0;
    private const double ShieldMaxDeskMs = 10.5;
    private const double ShieldMaxMobMs = 13.0;
    private const int MaxDeferredAdaptive = 2;
    private const int MaxDeferredCinema = 3;
    private const int CountryNameRefreshSmoothDays = 7;

    private const float SyncMobBaseCap = 5.0f;
    private const float SyncMobFastCap = 24.0f;
    private const float SyncMobSmoothFastCap = 22.0f;
    private const float SyncMobUncheckedCap = 56.0f;
    private const float SyncOffFloor = 12.0f;
    private const float SyncOffScale = 1.5f;
    private const float SyncMaxFastScale = 1.6f;
    private const float SyncMaxMobFast = 14.0f;
    private const float SyncAdaptiveDeskTopCap = 11.5f;
    private const float SyncAdaptiveDeskFastScale = 1.25f;
    private const float SyncAdaptiveMobTopCap = 13.0f;
    private const float SyncAdaptiveMobFastCap = 9.0f;
    private const float SyncCinemaDeskBase = 5.0f;
    private const float SyncCinemaDeskFast = 6.0f;
    private const float SyncCinemaDeskTop = 7.0f;
    private const float SyncCinemaMobBase = 4.0f;
    private const float SyncCinemaMobFast = 5.5f;
    private const float SyncCinemaMobTop = 7.5f;

    private const double SyncStageCinemaDesk = 1.5;
    private const double SyncStageCinemaMob = 1.0;
    private const double SyncStageAdaptiveDesk = 2.0;
    private const double SyncStageAdaptiveMob = 1.25;
    private const double SyncStageFast = 2.5;

    private const double AccumAdaptiveSpeed7Factor = 1.12;
    private const double AccumAdaptiveNormFactor = 1.08;
    private const double AccumCinemaFactor = 1.02;
    private const double AccumFastSpeed7Factor = 1.60;
    private const double AccumFastSpeed7FloorSec = 0.032;
    private const double AccumFastNormFactor = 1.15;

    private const double DampAdaptiveKeep = 0.15;
    private const double DampAdaptiveCap = 0.20;
    private const double DampFastKeep = 0.25;
    private const double DampFastCap = 0.35;

    private const int SyncCapAdaptiveHigh = 2;
    private const int SyncCapAdaptiveNorm = 3;
    private const long GcLowLatencyDesktopHeapCapBytes = 3_221_225_472L;
    private const long GcLowLatencyMobileHeapCapBytes = 704_643_072L;

    private double _emaWorkMs;
    private double _emaJitterMs;
    private double _emaExplicitSpanMs;
    private double _pacedDebtMs;
    private GCLatencyMode _savedGcLatencyMode = GCLatencyMode.Interactive;
    private bool _gcLowLatencyActive;

    /// <summary>Smoothed wall-clock simulation work cost per completed day (milliseconds).</summary>
    public double EmaWorkMs => _emaWorkMs;

    /// <summary>Smoothed positive spike envelope above <see cref="EmaWorkMs"/> (milliseconds).</summary>
    public double EmaJitterMs => _emaJitterMs;

    /// <summary>True while sustained low-latency GC mode is active for smooth high-speed stepping.</summary>
    public bool GcLowLatencyActive => _gcLowLatencyActive;

    /// <summary>Clear adaptive telemetry on pause, speed change, mode switch, or campaign reset.</summary>
    public void Reset()
    {
        _emaWorkMs = 0.0;
        _emaJitterMs = 0.0;
        _emaExplicitSpanMs = 0.0;
        _pacedDebtMs = 0.0;
    }

    /// <summary>
    /// Enters or exits <see cref="GCLatencyMode.SustainedLowLatency"/> while unpaused at high speed
    /// in smooth modes so foreground Gen2 collections do not stall the camera; restores the prior
    /// latency mode on pause, save preparation, mode change, heap headroom pressure, or worker stop.
    /// Skipped on Android/iOS runtimes where modifying GC latency mode is unsupported.
    /// </summary>
    public void SyncGcLatencyMode(bool wantLowLatency, bool mobilePacing)
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return;
        var heapCap = mobilePacing ? GcLowLatencyMobileHeapCapBytes : GcLowLatencyDesktopHeapCapBytes;
        var safeWant = wantLowLatency && GC.GetTotalMemory(forceFullCollection: false) < heapCap;
        if (safeWant == GcLowLatencyActive) return;
        try
        {
            if (safeWant)
            {
                var current = GCSettings.LatencyMode;
                if (current is GCLatencyMode.SustainedLowLatency or GCLatencyMode.NoGCRegion) return;
                _savedGcLatencyMode = current;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                _gcLowLatencyActive = true;
            }
            else
            {
                GCSettings.LatencyMode = _savedGcLatencyMode;
                _gcLowLatencyActive = false;
            }
        }
        catch (PlatformNotSupportedException)
        {
            _gcLowLatencyActive = false;
        }
        catch
        {
            _gcLowLatencyActive = false;
        }
    }

    public static SmoothTimeMode ParseMode(string value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "off" => SmoothTimeMode.Off,
        "cinema_60fps" or "cinema" or "strict" => SmoothTimeMode.Cinema60Fps,
        "max_throughput" or "throughput" or "uncapped" => SmoothTimeMode.MaxThroughput,
        _ => SmoothTimeMode.Adaptive
    };

    /// <summary>
    /// Resolves effective Smooth Time 2.0 mode while preserving legacy <c>smooth_ticks</c>
    /// compatibility: disabling the master toggle forces <see cref="SmoothTimeMode.Off"/>.
    /// </summary>
    public static SmoothTimeMode ResolveMode(bool smoothTicksEnabled, string configuredMode) =>
        smoothTicksEnabled ? ParseMode(configuredMode) : SmoothTimeMode.Off;

    public static string FormatSpeedTooltipSuffix(SmoothTimeMode mode)
    {
        var labelKey = mode switch
        {
            SmoothTimeMode.Cinema60Fps => "smooth_mode.cinema_60fps",
            SmoothTimeMode.MaxThroughput => "smooth_mode.max_throughput",
            SmoothTimeMode.Off => "smooth_mode.off",
            _ => "smooth_mode.adaptive"
        };
        return Loc.Format("Smooth time: {0}", Loc.Get(labelKey));
    }

    /// <summary>Record actual wall-clock simulation work time for a completed step or day.</summary>
    public void ObserveStepWork(double workMs, bool openedNewDay)
    {
        if (!double.IsFinite(workMs) || workMs <= 0.0 || !openedNewDay) return;
        var sample = Math.Min(workMs, WorkSampleCeilingMs);
        if (_emaWorkMs <= 0.0)
        {
            _emaWorkMs = sample;
            _emaJitterMs = 0.0;
            return;
        }
        var overshoot = Math.Max(0.0, sample - _emaWorkMs);
        var jitterAlpha = overshoot > _emaJitterMs ? JitterAttackAlpha : JitterDecayAlpha;
        _emaJitterMs = _emaJitterMs * (1.0 - jitterAlpha) + overshoot * jitterAlpha;
        var workAlpha = sample > _emaWorkMs ? EmaAttackAlpha : EmaDecayAlpha;
        _emaWorkMs = _emaWorkMs * (1.0 - workAlpha) + sample * workAlpha;
    }

    /// <summary>
    /// Computes the adaptive target wall-clock spacing between completed Speed-8 days (milliseconds).
    /// Returns 0 when Speed-8 runs uncapped (<see cref="SmoothTimeMode.MaxThroughput"/> or
    /// <see cref="SmoothTimeMode.Off"/> without an explicit override).
    /// </summary>
    public double ComputeTargetDayIntervalMs(
        SmoothTimeMode mode, int speed, bool mobilePacing, double explicitMinDayIntervalMs)
    {
        var explicitFloor = double.IsFinite(explicitMinDayIntervalMs) && explicitMinDayIntervalMs > 0.0
            ? explicitMinDayIntervalMs
            : 0.0;
        if (speed < SpeedMax) return 0.0;
        if (mode is SmoothTimeMode.Off or SmoothTimeMode.MaxThroughput) return explicitFloor;

        var minFloor = mode == SmoothTimeMode.Cinema60Fps
            ? (mobilePacing ? CinemaMobileMinDayMs : CinemaDesktopMinDayMs)
            : (mobilePacing ? AdaptiveMobileMinDayMs : AdaptiveDesktopMinDayMs);
        var maxCeiling = mode == SmoothTimeMode.Cinema60Fps
            ? (mobilePacing ? CinemaMobileMaxDayMs : CinemaDesktopMaxDayMs)
            : (mobilePacing ? AdaptiveMobileMaxDayMs : AdaptiveDesktopMaxDayMs);
        var desired = EmaWorkMs <= 0.0
            ? minFloor
            : mode == SmoothTimeMode.Cinema60Fps
                ? EmaWorkMs * CinemaWorkScale + EmaJitterMs * CinemaJitterWeight
                : EmaWorkMs + EmaJitterMs * AdaptiveJitterWeight;
        var adaptiveTarget = Math.Clamp(desired, minFloor, maxCeiling);
        return Math.Max(explicitFloor, adaptiveTarget);
    }

    /// <summary>
    /// Computes how many milliseconds the worker should pause after finishing a Speed-8 day so
    /// light days match the adaptive cadence and heavy weekly/monthly ticks never trigger multi-day
    /// catch-up bursts. Updates internal bounded debt.
    /// </summary>
    public double ComputePostDayWaitMs(
        SmoothTimeMode mode, int speed, bool mobilePacing,
        double explicitMinDayIntervalMs, double elapsedSinceLastDayMs)
    {
        var targetMs = ComputeTargetDayIntervalMs(mode, speed, mobilePacing, explicitMinDayIntervalMs);
        if (targetMs <= 0.0)
        {
            _pacedDebtMs = 0.0;
            return 0.0;
        }
        if (!double.IsFinite(elapsedSinceLastDayMs) || elapsedSinceLastDayMs < 0.0)
            return 0.0;

        // Legacy explicit-interval smoothing path for Off/MaxThroughput with caller-supplied floor.
        if (mode is SmoothTimeMode.Off or SmoothTimeMode.MaxThroughput)
        {
            var overshoot = _emaExplicitSpanMs > targetMs
                ? Math.Min(targetMs * ExplicitOvershootCapFactor, _emaExplicitSpanMs - targetMs)
                : 0.0;
            var adjusted = Math.Max(targetMs * MinTargetFactor, targetMs - overshoot);
            var wait = Math.Max(0.0, adjusted - elapsedSinceLastDayMs);
            var span = Math.Min(elapsedSinceLastDayMs + wait, targetMs * ExplicitSpanCapFactor);
            _emaExplicitSpanMs = _emaExplicitSpanMs <= 0.0
                ? span
                : _emaExplicitSpanMs * ExplicitEmaKeep + span * ExplicitEmaNew;
            return wait;
        }

        // Anti-burst bounded debt: a heavy weekly tick slightly softens the next pause at most
        // once, never banking multi-day catch-up debt that teleports the calendar forward.
        var delta = elapsedSinceLastDayMs - targetMs;
        _pacedDebtMs = Math.Clamp(
            _pacedDebtMs + delta,
            -targetMs * DebtNegFloorFactor,
            targetMs * DebtPosCeilingFactor);
        var adjustedTarget = Math.Clamp(
            targetMs - _pacedDebtMs,
            targetMs * MinTargetFactor,
            targetMs);
        var waitMs = Math.Max(0.0, adjustedTarget - elapsedSinceLastDayMs);
        if (waitMs > 0.0) _pacedDebtMs *= MinTargetFactor;
        return waitMs;
    }

    public static WorkerSlicePolicy WorkerPolicy(SmoothTimeMode mode, int speed, bool mobilePacing)
    {
        var isMax = speed >= SpeedMax;
        var isHigh = speed == SpeedHigh;
        return mode switch
        {
            SmoothTimeMode.Cinema60Fps => new WorkerSlicePolicy(
                StageSliceMs: mobilePacing ? StageCinemaMob : StageCinemaDesk,
                WriteSliceMs: isMax
                    ? (mobilePacing ? SliceCinemaMobMax : SliceCinemaDeskMax)
                    : isHigh
                        ? (mobilePacing ? SliceCinemaMobHigh : SliceCinemaDeskHigh)
                        : (mobilePacing ? SliceCinemaMobNorm : SliceCinemaDeskNorm),
                ReaderYieldAfterMs: mobilePacing ? YieldCinemaMob : YieldCinemaDesk,
                ReaderWindowMs: mobilePacing ? WindowMobCinema : WindowDeskCinema,
                ReaderWorkAheadMs: mobilePacing ? WorkAheadMobCinema : 0,
                SnapshotMinIntervalMs: isMax || isHigh ? (mobilePacing ? SnapCinemaMob : SnapCinemaDesk) : SnapNormal,
                MapChangeCoalesceMs: isMax || isHigh ? (mobilePacing ? MapCoalesceCinemaMob : MapCoalesceCinemaDesk) : 0.0,
                HudSnapshotMinIntervalMs: isMax || isHigh ? (mobilePacing ? HudSnapCinemaMob : HudSnapCinemaDesk) : 0.0,
                MaxBatchSteps: 1,
                MaxVisualLeadSnapshots: isMax || isHigh ? LeadCinema : 0,
                MaxBackpressureWaitMs: BackpressureCinemaMs,
                LowerWorkerPriority: true),

            SmoothTimeMode.Adaptive => new WorkerSlicePolicy(
                StageSliceMs: isMax
                    ? (mobilePacing ? StageAdaptiveMobMax : StageAdaptiveDeskMax)
                    : isHigh
                        ? (mobilePacing ? StageAdaptiveMobHigh : StageAdaptiveDeskHigh)
                        : (mobilePacing ? StageAdaptiveMobNorm : StageAdaptiveDeskNorm),
                WriteSliceMs: isMax
                    ? (mobilePacing ? SliceAdaptiveMobMax : SliceAdaptiveDeskMax)
                    : isHigh
                        ? (mobilePacing ? SliceAdaptiveMobHigh : SliceAdaptiveDeskHigh)
                        : (mobilePacing ? SliceAdaptiveMobNorm : SliceAdaptiveDeskNorm),
                ReaderYieldAfterMs: mobilePacing ? YieldAdaptiveMob : YieldAdaptiveDesk,
                ReaderWindowMs: mobilePacing ? WindowMobAdaptive : WindowDeskAdaptive,
                ReaderWorkAheadMs: mobilePacing ? WorkAheadMobAdaptive : 0,
                SnapshotMinIntervalMs: isMax ? (mobilePacing ? SnapAdaptiveMob : SnapAdaptiveDesk) : SnapNormal,
                MapChangeCoalesceMs: isMax || isHigh ? (mobilePacing ? MapCoalesceAdaptiveMob : MapCoalesceAdaptiveDesk) : 0.0,
                HudSnapshotMinIntervalMs: isMax || isHigh ? (mobilePacing ? HudSnapAdaptiveMob : HudSnapAdaptiveDesk) : 0.0,
                MaxBatchSteps: 1,
                MaxVisualLeadSnapshots: isMax || isHigh ? LeadAdaptive : 0,
                MaxBackpressureWaitMs: BackpressureAdaptiveMs,
                LowerWorkerPriority: true),

            SmoothTimeMode.MaxThroughput => new WorkerSlicePolicy(
                StageSliceMs: isMax
                    ? (mobilePacing ? StageMaxMobMax : StageMaxDeskMax)
                    : isHigh
                        ? (mobilePacing ? StageMaxMobHigh : StageMaxDeskHigh)
                        : (mobilePacing ? StageMaxMobNorm : StageMaxDeskNorm),
                WriteSliceMs: isMax
                    ? (mobilePacing ? SliceMaxMobMax : SliceMaxDeskMax)
                    : isHigh
                        ? (mobilePacing ? SliceMaxMobHigh : SliceMaxDeskHigh)
                        : (mobilePacing ? SliceMaxMobNorm : SliceMaxDeskNorm),
                ReaderYieldAfterMs: isMax ? (mobilePacing ? YieldFastMob : YieldFastDesk) : YieldFastDesk,
                ReaderWindowMs: isMax ? (mobilePacing ? WindowMobMax : WindowDeskFast) : (mobilePacing ? WindowMobNormal : WindowDeskNormal),
                ReaderWorkAheadMs: mobilePacing ? WorkAheadMobFast : 0,
                SnapshotMinIntervalMs: isMax ? (mobilePacing ? SnapFastMob : SnapFastDesk) : SnapNormal,
                MapChangeCoalesceMs: isMax ? (mobilePacing ? MapCoalesceFastMob : MapCoalesceFastDesk) : 0.0,
                HudSnapshotMinIntervalMs: isMax ? (mobilePacing ? HudSnapFastMob : HudSnapFastDesk) : 0.0,
                MaxBatchSteps: isMax ? BatchStepsFast : isHigh ? 2 : 1,
                MaxVisualLeadSnapshots: isHigh ? LeadLegacySpeed7 : 0,
                MaxBackpressureWaitMs: BackpressureLegacyMs,
                LowerWorkerPriority: false),

            _ => new WorkerSlicePolicy(
                StageSliceMs: isMax
                    ? (mobilePacing ? StageOffMobMax : StageOffDeskMax)
                    : isHigh
                        ? (mobilePacing ? StageMaxMobHigh : StageMaxDeskHigh)
                        : (mobilePacing ? StageMaxMobNorm : StageMaxDeskNorm),
                WriteSliceMs: isMax
                    ? (mobilePacing ? SliceOffMobMax : SliceOffDeskMax)
                    : isHigh
                        ? (mobilePacing ? SliceMaxMobHigh : SliceMaxDeskHigh)
                        : (mobilePacing ? SliceMaxMobNorm : SliceMaxDeskNorm),
                ReaderYieldAfterMs: isMax ? (mobilePacing ? YieldOffMob : YieldFastDesk) : YieldFastDesk,
                ReaderWindowMs: isMax ? (mobilePacing ? WindowMobMax : WindowDeskFast) : (mobilePacing ? WindowMobNormal : WindowDeskNormal),
                ReaderWorkAheadMs: mobilePacing ? WorkAheadMobFast : 0,
                SnapshotMinIntervalMs: isMax ? (mobilePacing ? SnapFastMob : SnapFastDesk) : SnapNormal,
                MapChangeCoalesceMs: 0.0,
                HudSnapshotMinIntervalMs: 0.0,
                MaxBatchSteps: isMax ? BatchStepsOff : isHigh ? 2 : 1,
                MaxVisualLeadSnapshots: isHigh ? LeadLegacySpeed7 : 0,
                MaxBackpressureWaitMs: BackpressureLegacyMs,
                LowerWorkerPriority: false)
        };
    }

    public static MainThreadFramePolicy MainThreadPolicy(SmoothTimeMode mode, int speed, bool mobileRuntime)
    {
        var isMax = speed >= SpeedMax;
        return mode switch
        {
            SmoothTimeMode.Cinema60Fps => new MainThreadFramePolicy(
                LiveReadIntervalMs: isMax
                    ? (mobileRuntime ? LiveReadCinemaMobMax : LiveReadCinemaDeskMax)
                    : (mobileRuntime ? LiveReadCinemaMobNorm : LiveReadCinemaDeskNorm),
                FastHudRefreshGapMs: mobileRuntime ? HudGapCinemaMob : HudGapCinemaDesk,
                FrameShieldBudgetMs: mobileRuntime ? ShieldCinemaMobMs : ShieldCinemaDeskMs,
                MaxDeferredFencedFrames: MaxDeferredCinema,
                CountryNameRefreshDays: CountryNameRefreshSmoothDays),

            SmoothTimeMode.Adaptive => new MainThreadFramePolicy(
                LiveReadIntervalMs: isMax
                    ? (mobileRuntime ? LiveReadAdaptiveMobMax : LiveReadAdaptiveDeskMax)
                    : (mobileRuntime ? LiveReadAdaptiveMobNorm : LiveReadAdaptiveDeskNorm),
                FastHudRefreshGapMs: mobileRuntime ? HudGapAdaptiveMob : HudGapAdaptiveDesk,
                FrameShieldBudgetMs: mobileRuntime ? ShieldAdaptiveMobMs : ShieldAdaptiveDeskMs,
                MaxDeferredFencedFrames: MaxDeferredAdaptive,
                CountryNameRefreshDays: CountryNameRefreshSmoothDays),

            SmoothTimeMode.MaxThroughput => new MainThreadFramePolicy(
                LiveReadIntervalMs: isMax
                    ? (mobileRuntime ? LiveReadFastMobMax : LiveReadFastDeskMax)
                    : (mobileRuntime ? LiveReadFastMobNorm : LiveReadFastDeskNorm),
                FastHudRefreshGapMs: mobileRuntime ? HudGapFastMob : HudGapFastDesk,
                FrameShieldBudgetMs: mobileRuntime ? ShieldMaxMobMs : ShieldMaxDeskMs,
                MaxDeferredFencedFrames: 1,
                CountryNameRefreshDays: CountryNameRefreshSmoothDays),

            _ => new MainThreadFramePolicy(
                LiveReadIntervalMs: isMax
                    ? (mobileRuntime ? LiveReadFastMobMax : LiveReadFastDeskMax)
                    : (mobileRuntime ? LiveReadFastMobNorm : LiveReadFastDeskNorm),
                FastHudRefreshGapMs: mobileRuntime ? HudGapFastMob : HudGapFastDesk,
                FrameShieldBudgetMs: 0.0,
                MaxDeferredFencedFrames: 0,
                CountryNameRefreshDays: 1)
        };
    }

    public static double SyncStageSliceMs(SmoothTimeMode mode, int speed, bool mobileRuntime) => mode switch
    {
        SmoothTimeMode.Cinema60Fps => mobileRuntime ? SyncStageCinemaMob : SyncStageCinemaDesk,
        SmoothTimeMode.Adaptive => mobileRuntime && speed < SpeedHigh ? SyncStageAdaptiveMob : SyncStageAdaptiveDesk,
        SmoothTimeMode.MaxThroughput => mobileRuntime && speed < SpeedHigh ? SyncStageAdaptiveMob : SyncStageFast,
        _ => SyncStageFast
    };

    public static double SyncFrameBudgetMs(Defines defines, int speed, SmoothTimeMode mode, bool mobileProfile)
    {
        var baseSlice = defines.FrameSimulationBudgetMs;
        if (baseSlice <= 0f) return 0.0;
        var fastSlice = defines.FastSpeedSimulationBudgetMs;
        var smoothFastSlice = defines.SmoothFastSpeedSimulationBudgetMs;
        var uncheckedFastSlice = defines.UncheckedSpeedSimulationBudgetMs;
        if (mobileProfile)
        {
            baseSlice = Math.Min(baseSlice, SyncMobBaseCap);
            fastSlice = Math.Min(fastSlice, SyncMobFastCap);
            smoothFastSlice = Math.Min(smoothFastSlice, SyncMobSmoothFastCap);
            uncheckedFastSlice = Math.Min(uncheckedFastSlice, SyncMobUncheckedCap);
        }
        var topSpeed = Math.Max(MinTopSpeed, defines.MaxSpeed);
        var fastSpeed = Math.Max(MinFastSpeed, topSpeed - 2);
        return mode switch
        {
            SmoothTimeMode.Cinema60Fps => speed >= topSpeed
                ? Math.Min(smoothFastSlice, mobileProfile ? SyncCinemaMobTop : SyncCinemaDeskTop)
                : speed >= fastSpeed
                    ? Math.Min(smoothFastSlice, mobileProfile ? SyncCinemaMobFast : SyncCinemaDeskFast)
                    : Math.Min(baseSlice, mobileProfile ? SyncCinemaMobBase : SyncCinemaDeskBase),
            SmoothTimeMode.Adaptive => speed >= topSpeed
                ? Math.Max(baseSlice, Math.Min(smoothFastSlice, mobileProfile ? SyncAdaptiveMobTopCap : SyncAdaptiveDeskTopCap))
                : speed >= fastSpeed
                    ? Math.Max(baseSlice, Math.Min(smoothFastSlice, mobileProfile ? SyncAdaptiveMobFastCap : baseSlice * SyncAdaptiveDeskFastScale))
                    : baseSlice,
            SmoothTimeMode.MaxThroughput => speed >= topSpeed
                ? Math.Max(baseSlice, smoothFastSlice)
                : speed >= fastSpeed
                    ? Math.Max(baseSlice, Math.Min(smoothFastSlice, mobileProfile ? SyncMaxMobFast : baseSlice * SyncMaxFastScale))
                    : baseSlice,
            _ => speed >= topSpeed
                ? Math.Max(fastSlice, uncheckedFastSlice)
                : speed >= fastSpeed
                    ? Math.Max(baseSlice, fastSlice)
                    : Math.Max(SyncOffFloor, baseSlice * SyncOffScale)
        };
    }

    public static bool ShouldYieldAfterHeavyStep(SmoothTimeMode mode, int speed) => mode switch
    {
        SmoothTimeMode.Cinema60Fps => true,
        SmoothTimeMode.Adaptive => true,
        SmoothTimeMode.MaxThroughput => speed < SpeedFastTier,
        _ => false
    };

    public static int SyncMaxStepsPerFrame(Defines defines, SmoothTimeMode mode, int speed)
    {
        var definedCap = Math.Max(1, defines.MaxDaysPerFrame);
        return mode switch
        {
            SmoothTimeMode.Cinema60Fps => 1,
            SmoothTimeMode.Adaptive => Math.Min(definedCap, speed >= SpeedHigh ? SyncCapAdaptiveHigh : SyncCapAdaptiveNorm),
            _ => definedCap
        };
    }

    public static double AccumulateSmoothDebt(
        double pending, double elapsed, double secondsPerStep, int stepsPerFrame,
        SmoothTimeMode mode, int speed)
    {
        var raw = SimulationPacing.Accumulate(pending, elapsed, secondsPerStep, stepsPerFrame);
        if (raw <= 0.0 || secondsPerStep <= 0.0) return 0.0;
        return mode switch
        {
            SmoothTimeMode.Cinema60Fps => Math.Min(raw, secondsPerStep * AccumCinemaFactor),
            SmoothTimeMode.Adaptive => Math.Min(raw, secondsPerStep * (speed >= SpeedHigh ? AccumAdaptiveSpeed7Factor : AccumAdaptiveNormFactor)),
            _ => raw
        };
    }

    public static double AccumulateWorkerStepDebt(
        double accumulator, double dt, double stepInterval, SmoothTimeMode mode, int speed)
    {
        if (stepInterval <= 0.0) return 0.0;
        var cap = mode switch
        {
            SmoothTimeMode.Cinema60Fps => stepInterval * AccumCinemaFactor,
            SmoothTimeMode.Adaptive => stepInterval * (speed >= SpeedHigh ? AccumAdaptiveSpeed7Factor : AccumAdaptiveNormFactor),
            _ => speed == SpeedHigh ? Math.Max(stepInterval * AccumFastSpeed7Factor, AccumFastSpeed7FloorSec) : stepInterval * AccumFastNormFactor
        };
        return Math.Min(accumulator + dt, cap);
    }

    public static double DampWorkerAccumulator(
        double accumulator, double stepInterval, double stepWorkSeconds, bool continuedStep,
        SmoothTimeMode mode, int speed)
    {
        if (speed >= SpeedMax) return 0.0;
        if (!continuedStep && (stepInterval <= 0.0 || stepWorkSeconds <= stepInterval)) return accumulator;
        return mode switch
        {
            SmoothTimeMode.Cinema60Fps => 0.0,
            SmoothTimeMode.Adaptive => Math.Min(accumulator * DampAdaptiveKeep, stepInterval * DampAdaptiveCap),
            _ => Math.Min(accumulator * DampFastKeep, stepInterval * DampFastCap)
        };
    }
}
