using System;

namespace DealOrForce.Simulation;

/// <summary>Wall-clock scheduling, not campaign time. Dropping excess real-time debt
/// slows a busy game; no campaign step, order, or saved state is skipped.</summary>
public static class SimulationPacing
{
    public static double Accumulate(double pending, double elapsed, double secondsPerStep, int stepsPerFrame)
    {
        if (!double.IsFinite(secondsPerStep) || secondsPerStep <= 0) return 0;
        if (!double.IsFinite(pending) || pending < 0) pending = 0;
        if (!double.IsFinite(elapsed) || elapsed < 0) elapsed = 0;
        // A stalled frame must not queue minutes of fast-forward. At most one frame's
        // allowed work is owed; simulation still advances by exactly TickHours per step.
        return Math.Min(pending + elapsed, secondsPerStep * Math.Max(1, stepsPerFrame));
    }
}
