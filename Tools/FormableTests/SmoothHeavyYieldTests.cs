using System;
using DealOrForce.Simulation;

/// <summary>
/// Smooth time asks a bounded step to return after a stage that reached its heavy ceiling.
/// A ceiling of zero makes every stage heavy, so one call is one stage, and that can be
/// shown on a bootstrap world. An unbounded step still runs to the end.
/// </summary>
static class SmoothHeavyYieldTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("Smooth heavy yield: " + label);
        Console.WriteLine("PASS smooth heavy yield: " + label);
    }

    public static void Run()
    {
        var whole = Open();
        Check(whole.StepFor(double.PositiveInfinity, 0) && !whole.MidStep,
            "unbounded StepFor finishes the step with a heavy ceiling of zero");
        var expected = CampaignFingerprint.Of(whole);
        var hours = whole.ElapsedHours;

        var zeroBudget = Open();
        Check(!zeroBudget.StepFor(0) && zeroBudget.MidStep, "a zero budget runs one stage");

        var heavy = Open();
        Check(!heavy.StepFor(60_000, 0) && heavy.MidStep, "a heavy ceiling of zero yields after one stage");
        Check(heavy.ElapsedHours == zeroBudget.ElapsedHours &&
              CampaignFingerprint.Of(heavy) == CampaignFingerprint.Of(zeroBudget),
            "heavy ceiling of zero matches one zero-budget stage");

        var calls = 1;
        while (!heavy.StepFor(60_000, 0))
            if (++calls > 4000) throw new Exception("Smooth heavy yield: the day did not finish.");
        Check(calls > 1 && !heavy.MidStep && heavy.ElapsedHours == hours &&
              CampaignFingerprint.Of(heavy) == expected,
            "yielding after every stage matches the unbounded step (" + calls + " calls)");
        Console.WriteLine("SMOOTH_YIELD calls=" + calls + " first_call=one-stage infinity_finishes=true");

        // The clock's own yields (Campaign.StageOverdue) with a slice of nothing: every staged
        // loop that asks yields at every item it asks about, so the step is cut as finely as it
        // can be. A yield is not part of the world, and this is the run that says so.
        var slice = Campaign.StageSliceMilliseconds;
        try
        {
            Campaign.StageSliceMilliseconds = 0;
            var fine = Open();
            var fineCalls = 0;
            while (!fine.StepFor(60_000, 0))
                if (++fineCalls > 400_000) throw new Exception("Smooth heavy yield: the finely cut day did not finish.");
            Check(fineCalls >= calls && !fine.MidStep && fine.ElapsedHours == hours &&
                  CampaignFingerprint.Of(fine) == expected,
                "yielding at every clock check matches the unbounded step (" + fineCalls + " calls)");
        }
        finally
        {
            Campaign.StageSliceMilliseconds = slice;
        }

        // And the frame clock the game runs (SimulationFrameClock), at the top speed with smooth
        // time on: however many frames it takes, the day it hands back is the same day.
        var framed = Open();
        var clock = new SimulationFrameClock();
        var frames = 0;
        while (framed.ElapsedHours < hours || framed.MidStep)
        {
            // Advance clock debt only until the single test step begins so CPU contention spanning
            // 4+ frames cannot accumulate enough debt to start a second step on completion.
            clock.RunFrame(framed, framed.MidStep ? 0.0 : 1.0 / 60.0, 5, smooth: true);
            if (++frames > 400_000) throw new Exception("Smooth heavy yield: the frame clock did not finish the day.");
        }
        Check(framed.ElapsedHours == hours && CampaignFingerprint.Of(framed) == expected,
            "the frame clock at the top speed matches the unbounded step (" + frames + " frames)");
    }

    static Campaign Open()
    {
        var campaign = Campaign.CreateBootstrap(61045);
        campaign.Defines.HoursPerTick = 24;
        campaign.IsPaused = false;
        return campaign;
    }
}
