using System;
using System.Linq;
using DealOrForce.Simulation;

static class StagedStepBoundaryTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception("Staged step boundary: " + message);
        Console.WriteLine("PASS staged step boundary: " + message);
    }

    private static Campaign World()
    {
        var campaign = Campaign.CreateBootstrap(61044);
        var player = campaign.NationByTag("KOR");
        campaign.ConfigureSinglePlayer(player.Id);
        var state = campaign.StatesOf(player).First();
        campaign.AttachProvinceTopology(ProvinceTopology.Build(
            Enumerable.Repeat((ushort)state.MapIndex, 16).ToArray(),
            Enumerable.Repeat((ushort)player.MapIndex, 16).ToArray(), 4, 4, _ => false));
        campaign.Armies.Clear();
        campaign.ObserverMode = true;
        campaign.Defines.HoursPerTick = 24;
        campaign.Defines.EventIntervalDays = 0;
        campaign.IsPaused = false;
        return campaign;
    }

    public static void Run()
    {
        ExternalOrderFinishesDay();
        SnapshotIncludesCompletionHook();
        TransportReceivesStableOrder();
        PausedAndQueuedStepsCompleteOnce();
    }

    private static void ExternalOrderFinishesDay()
    {
        var campaign = World();
        Check(!campaign.StepFor(0) && campaign.MidStep,
            "a bounded frame can leave a day in progress");
        campaign.Commands.Enqueue(new SetTaxRateCommand { IssuerId = campaign.Player.Id, Value = .22f });
        campaign.Commands.ApplyAll(campaign);
        Check(!campaign.MidStep && campaign.AssistanceLastTickDay == 1 && campaign.Player.TaxRate == .22f,
            "an external order finishes the outstanding day before applying its change");
    }

    private static void SnapshotIncludesCompletionHook()
    {
        var campaign = World();
        var completed = 0;
        campaign.StepCompleted += world =>
        {
            Check(!world.MidStep, "completion hooks receive a fully finished world");
            completed++;
            world.Commands.Enqueue(new SetTaxRateCommand { IssuerId = world.Player.Id, Value = .23f });
            world.Commands.ApplyAll(world);
        };
        campaign.StepFor(0);
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(campaign, "staged"), "staged");
        Check(completed == 1 && loaded.Success && loaded.Campaign.Player.TaxRate == .23f,
            "a snapshot that finishes a day includes its completion hook's changes");
        campaign.FinishStep();
        Check(completed == 1, "finishing an already completed day never repeats its hook");
    }

    private static void TransportReceivesStableOrder()
    {
        var campaign = World();
        var completed = false;
        var stable = false;
        campaign.StepCompleted += _ => completed = true;
        campaign.Commands.SubmitInterceptor = _ =>
        {
            stable = completed && !campaign.MidStep && campaign.AssistanceLastTickDay == 1;
            return true;
        };
        campaign.StepFor(0);
        campaign.Commands.Enqueue(new SetTaxRateCommand { IssuerId = campaign.Player.Id, Value = .24f });
        Check(stable && campaign.Commands.Count == 0,
            "network transport sees day completion before it broadcasts a new order");
    }

    private static void PausedAndQueuedStepsCompleteOnce()
    {
        var campaign = World();
        var completed = 0;
        campaign.StepCompleted += _ => completed++;
        campaign.Commands.Enqueue(new SetTaxRateCommand { IssuerId = campaign.Player.Id, Value = .25f });
        var whole = World();
        whole.Commands.Enqueue(new SetTaxRateCommand { IssuerId = whole.Player.Id, Value = .25f });
        whole.Tick();
        var expected = CampaignFingerprint.Of(whole);
        var frames = 0;
        // StepFor(0) deliberately executes one yield at a time. This is a finite-work
        // guard, not a frame-time SLA. Finer diplomatic stages raised this map-attached
        // fixture from 886 to 1092 yields, also in the pre-reunion-fix assembly. A
        // separate bounded probe verified the exact whole-step fingerprint in both.
        // Keep a finite-work guard and exact state comparison, not a budget of frames.
        const int maxSlices = 2000;
        while (!campaign.StepFor(0))
            if (++frames > maxSlices) throw new Exception("Staged step did not finish within its bounded passes.");
        Console.WriteLine($"STAGED_BOUNDARY_SLICES passes={frames + 1} limit={maxSlices}");
        Check(CampaignFingerprint.Of(campaign) == expected,
            "the queued staged day has exactly the whole-step world, not only the expected tax");
        Check(completed == 1 && campaign.ElapsedDays == 1 && campaign.AssistanceLastTickDay == 1 && campaign.Player.TaxRate == .25f,
            "an order queued before a staged day applies without reentering or discarding the day");
        campaign.IsPaused = true;
        campaign.Tick();
        campaign.StepFor(0);
        campaign.FinishStep();
        Check(completed == 1 && campaign.ElapsedDays == 1,
            "a paused clock emits no phantom completion hook");
        campaign.IsPaused = false;
        campaign.StepFor(0);
        campaign.IsPaused = true;
        campaign.FinishStep();
        Check(completed == 2 && campaign.ElapsedDays == 2 && campaign.AssistanceLastTickDay == 2 && !campaign.MidStep,
            "pausing a day already begun still completes that day exactly once");
    }
}
