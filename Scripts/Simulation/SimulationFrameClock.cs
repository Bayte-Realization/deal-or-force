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

    /// <summary>Forget the owed time: a pause, a load, a fault, a speed the player just changed.</summary>
    public void Reset() => Pending = 0;

    /// <summary>
    /// The slice of a frame the simulation may use at this speed, in milliseconds.
    ///
    /// With smooth time on the slice stays near the frame: the stages are small enough to stop
    /// between, so a short slice costs nothing but a few more frames for the heavy day. With it
    /// off a step runs to its end anyway, and the slice only says how many may start.
    /// </summary>
    public static double FrameBudgetMs(Defines defines, int speed, bool smooth)
    {
        var baseSlice = defines.FrameSimulationBudgetMs;
        var fastSlice = defines.FastSpeedSimulationBudgetMs;
        var smoothFastSlice = defines.SmoothFastSpeedSimulationBudgetMs;
        var uncheckedFastSlice = defines.UncheckedSpeedSimulationBudgetMs;
        return baseSlice <= 0f ? 0f
            : smooth
                ? (speed >= 5 ? Math.Max(baseSlice, smoothFastSlice)
                    : speed == 4 ? Math.Max(baseSlice, Math.Min(smoothFastSlice, baseSlice * 1.5f))
                    : baseSlice)
                : (speed >= 5 ? Math.Max(fastSlice, uncheckedFastSlice)
                    : speed == 4 ? Math.Max(baseSlice, fastSlice)
                    : Math.Max(27f, baseSlice * 3f));
    }

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
        Action beforeCall = null, Func<bool, bool> afterCall = null)
    {
        if (campaign is null) return 0;
        campaign.Speed = speed;
        // A step the smoothed clock has begun is carried on even under pause: pausing stops the
        // next step, never half of this one.
        if (campaign.IsPaused && !campaign.MidStep) { Pending = 0; return 0; }
        // Real seconds one step of the simulation is worth. The speed setting is still in
        // days, because that is what a player thinks in; a campaign stepping six hours at a
        // time simply takes four steps to spend one day's worth of them.
        var secondsPerStep = campaign.Defines.SecondsForSpeed(speed) * campaign.TickScale;
        // A new cadence must not reinterpret time queued under the old one (including
        // console edits). A 24-hour remainder used to become 24 hourly steps at once.
        if (secondsPerStep != _stepSeconds) Pending = 0;
        _stepSeconds = secondsPerStep;
        Pending = SimulationPacing.Accumulate(Pending, deltaSeconds, secondsPerStep, campaign.Defines.MaxDaysPerFrame);

        // Time is given a slice of the frame rather than a number of days: a speed the machine
        // cannot reach becomes a slower campaign instead of an unresponsive one. See the long
        // note in DealOrForceGame._Process for the history.
        var budget = Stopwatch.StartNew();
        var baseSlice = campaign.Defines.FrameSimulationBudgetMs;
        var cap = campaign.Defines.MaxDaysPerFrame;
        var frameBudget = FrameBudgetMs(campaign.Defines, speed, smooth);
        var steps = 0;
        // The frame's own start, for the stages' heavy-piece rule (Campaign.HeavyNext): a step
        // begun late in a frame must know the frame is not fresh.
        Campaign.FrameStarted = Stopwatch.GetTimestamp();
        try
        {
            while ((campaign.MidStep || Pending >= secondsPerStep) && steps < cap &&
                   (steps == 0 || budget.Elapsed.TotalMilliseconds < frameBudget))
            {
                if (!campaign.MidStep) Pending -= secondsPerStep;
                beforeCall?.Invoke();
                var heavyCall = false;
                bool finished;
                using (SimProfiler.Measure("campaign.day"))
                {
                    if (baseSlice > 0f)
                    {
                        var slice = Math.Max(1.0, frameBudget - budget.Elapsed.TotalMilliseconds);
                        var callStarted = smooth ? Stopwatch.GetTimestamp() : 0L;
                        // Half the slice is the heavy ceiling. Smooth time off passes no ceiling,
                        // so the same budget still runs every stage that fits and the step's result
                        // is unchanged.
                        finished = smooth ? campaign.StepFor(slice, slice * 0.5) : campaign.StepFor(slice);
                        if (smooth)
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
                // The rest of this step belongs to the next frame.
                if (!finished) break;
                steps++;
                // The step finished on a stage that already took half the slice. The next step
                // waits for another frame.
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
