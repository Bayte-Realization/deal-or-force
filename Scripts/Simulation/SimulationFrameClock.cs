using System;
using System.Diagnostics;

namespace DealOrForce.Simulation;

/// <summary>
/// How much of one frame the simulation may have, and the loop that spends it - the part of
/// <c>DealOrForceGame._Process</c> that decides when a step starts, when it is cut short, and when
/// the frame goes back to drawing.
///
/// It lived inside the game node, where nothing but a running game could exercise it, and so a
/// hitch report could only be answered by playing. Here it compiles into SimBench as well, which
/// drives this same object frame by frame on a save (<c>--profile-save</c>, "Frames at speed N"):
/// the bench's worst frame is the worst frame of this scheduler, not of a copy of it.
///
/// What stays in the game: the network flags, the profiler scope the console reads, autosave and
/// what a fault does to the screen. They arrive as the two callbacks, so the loop itself has one
/// author.
///
/// None of this touches the world. The world is the steps, and a step is the same whether one
/// frame or forty carried it; the clock only decides how many frames that is.
/// </summary>
public sealed class SimulationFrameClock
{
    /// <summary>Real seconds owed to the simulation and not yet spent. Not saved.</summary>
    public double Pending { get; private set; }

    private double _stepSeconds;
    private int _deferredUiFrames;

    /// <summary>Forget the owed time: a pause, a load, a fault, a speed the player just changed.</summary>
    public void Reset()
    {
        Pending = 0;
        _deferredUiFrames = 0;
    }

    /// <summary>
    /// The slice of a frame the simulation may use at this speed, in milliseconds.
    ///
    /// With smooth time on the slice stays near the frame: the stages are small enough to stop
    /// between, so a short slice costs nothing but a few more frames for the heavy day. With it
    /// off a step runs to its end anyway, and the slice only says how many may start.
    /// </summary>
    public static double FrameBudgetMs(Defines defines, int speed, bool smooth) =>
        FrameBudgetMs(defines, speed, smooth ? SmoothTimeMode.Adaptive : SmoothTimeMode.Off, mobileProfile: false);

    public static double FrameBudgetMs(Defines defines, int speed, bool smooth, bool mobileProfile) =>
        FrameBudgetMs(defines, speed, smooth ? SmoothTimeMode.Adaptive : SmoothTimeMode.Off, mobileProfile);

    public static double FrameBudgetMs(Defines defines, int speed, SmoothTimeMode mode, bool mobileProfile = false) =>
        SmoothTimePacer.SyncFrameBudgetMs(defines, speed, mode, mobileProfile);

    /// <summary>
    /// One frame of simulation: owe the frame's time, then start or carry on steps until the
    /// slice is spent, the frame's cap of steps is reached, or a stage that alone used half the
    /// slice ends the call. Returns the steps finished.
    /// </summary>
    /// <param name="beforeCall">Just before each StepFor; the game raises its network flag here.</param>
    /// <param name="afterCall">
    /// Just after each StepFor, with whether the step finished. Returning false ends the frame
    /// (the game: a fault). Exceptions are not caught here: the game's handler resets this clock.
    /// </param>
    public int RunFrame(Campaign campaign, double deltaSeconds, int speed, bool smooth,
        Action beforeCall = null, Func<bool, bool> afterCall = null) =>
        RunFrame(campaign, deltaSeconds, speed, smooth ? SmoothTimeMode.Adaptive : SmoothTimeMode.Off,
            beforeCall, afterCall, spentElsewhereMs: 0.0, mobileProfile: false);

    public int RunFrame(Campaign campaign, double deltaSeconds, int speed, bool smooth,
        Action beforeCall, Func<bool, bool> afterCall, double spentElsewhereMs, bool mobileProfile = false) =>
        RunFrame(campaign, deltaSeconds, speed, smooth ? SmoothTimeMode.Adaptive : SmoothTimeMode.Off,
            beforeCall, afterCall, spentElsewhereMs, mobileProfile);

    public int RunFrame(Campaign campaign, double deltaSeconds, int speed, SmoothTimeMode smoothMode,
        Action beforeCall, Func<bool, bool> afterCall, double spentElsewhereMs, bool mobileProfile = false)
    {
        if (campaign is null) return 0;
        speed = Math.Clamp(speed, 1, campaign.Defines.MaxSpeed);
        campaign.Speed = speed;
        var smooth = smoothMode != SmoothTimeMode.Off;
        // A step the smoothed clock has begun is carried on even under pause: pausing stops the
        // next step, never half of this one.
        if (campaign.IsPaused && !campaign.MidStep) { Pending = 0; _deferredUiFrames = 0; return 0; }
        var secondsPerStep = campaign.Defines.SecondsForSpeed(speed) * campaign.TickScale;
        if (secondsPerStep != _stepSeconds) Pending = 0;
        _stepSeconds = secondsPerStep;
        Pending = SmoothTimePacer.AccumulateSmoothDebt(
            Pending, deltaSeconds, secondsPerStep, campaign.Defines.MaxDaysPerFrame, smoothMode, speed);

        var baseSlice = campaign.Defines.FrameSimulationBudgetMs;
        var cap = SmoothTimePacer.SyncMaxStepsPerFrame(campaign.Defines, smoothMode, speed);
        var frameBudget = FrameBudgetMs(campaign.Defines, speed, smoothMode, mobileProfile);
        var remainingBudget = frameBudget > 0 ? frameBudget - Math.Max(0.0, spentElsewhereMs) : 0.0;
        if (baseSlice > 0f && spentElsewhereMs > 0.0 && remainingBudget < 1.25 &&
            (campaign.MidStep || Pending >= secondsPerStep) && _deferredUiFrames < 2)
        {
            _deferredUiFrames++;
            return 0;
        }
        _deferredUiFrames = 0;
        var effectiveBudget = frameBudget > 0 ? Math.Max(1.0, remainingBudget) : 0.0;
        var budget = Stopwatch.StartNew();
        var steps = 0;
        var yieldOnHeavy = SmoothTimePacer.ShouldYieldAfterHeavyStep(smoothMode, speed);
        Campaign.FrameStarted = Stopwatch.GetTimestamp();
        try
        {
            const double MinNextStepHeadroomMs = 1.5;
            while ((campaign.MidStep || Pending >= secondsPerStep) && steps < cap &&
                   (steps == 0 || (effectiveBudget > 0 && budget.Elapsed.TotalMilliseconds + MinNextStepHeadroomMs <= effectiveBudget)))
            {
                if (!campaign.MidStep) Pending -= secondsPerStep;
                beforeCall?.Invoke();
                var heavyCall = false;
                bool finished;
                using (SimProfiler.Measure("campaign.day"))
                {
                    if (baseSlice > 0f)
                    {
                        var slice = Math.Max(1.0, effectiveBudget - budget.Elapsed.TotalMilliseconds);
                        var callStarted = yieldOnHeavy ? Stopwatch.GetTimestamp() : 0L;
                        finished = smooth ? campaign.StepFor(slice, slice * 0.5) : campaign.StepFor(slice);
                        if (yieldOnHeavy)
                            heavyCall = Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds >= slice * 0.5;
                    }
                    else if (smooth)
                        finished = campaign.StepFor(1.0, 0.5);
                    else if (campaign.MidStep)
                        finished = campaign.StepFor(double.PositiveInfinity);
                    else
                    {
                        campaign.Tick();
                        finished = true;
                    }
                }
                if (afterCall is not null && !afterCall(finished)) break;
                if (!finished) break;
                steps++;
                if (heavyCall) break;
            }
        }
        finally
        {
            Campaign.FrameStarted = 0;
        }
        return steps;
    }
}
