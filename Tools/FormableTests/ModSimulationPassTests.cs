using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Modding;
using DealOrForce.Simulation;

static class ModSimulationPassTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS core pass: " + message); }
    private static void Refused(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { Check(true, message); return; }
        throw new Exception("Not refused: " + message);
    }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261105);
        var context = new ModContext("pass_fixture", _ => { }) { Campaign = c };
        var before = CampaignFingerprint.Of(c);
        var builtIn = 0; var replacement = 0;
        ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++);
        Check(builtIn == 1, "unmodified campaign calls the built-in exactly once");
        using (context.ReplaceSimulationPass(SimulationPass.Construction, (_, _) => replacement++))
        {
            ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++);
            Check(builtIn == 1 && replacement == 1, "replacement suppresses the actual built-in call");
            var other = new ModContext("conflicting_fixture", _ => { }) { Campaign = c };
            Refused(() => other.ReplaceSimulationPass(SimulationPass.Construction, (_, _) => { }),
                "a second owner is refused, never silently replaces the first");
            other.ReleaseSimulationPasses();
            Check(ModSimulationPasses.Owner(c, SimulationPass.Construction) == "pass_fixture", "failed registration preserves the original owner");
            ModSimulationPasses.Run(new Campaign(1), SimulationPass.Construction, _ => builtIn++);
            Check(builtIn == 2 && replacement == 1, "a replacement cannot leak into another campaign");
        }
        ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++);
        Check(builtIn == 3 && ModSimulationPasses.Owner(c, SimulationPass.Construction) == "", "disposing a lease restores the built-in");
        using (context.ReplaceSimulationPass(SimulationPass.Construction, (_, original) => { replacement++; original(); }))
            ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++);
        Check(builtIn == 4 && replacement == 2, "decorator calls the real built-in once");
        Action escaped = null;
        using (context.ReplaceSimulationPass(SimulationPass.Construction, (_, original) => escaped = original))
            ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++);
        Refused(escaped, "saved continuations cannot execute after their pass");
        using (context.ReplaceSimulationPass(SimulationPass.Construction, (_, original) => { original(); original(); }))
            Refused(() => ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++), "a repeated original call is refused");
        Check(builtIn == 5, "an invalid second call never executes the built-in twice");
        using (context.ReplaceSimulationPass(SimulationPass.Construction, (_, original) => { original(); throw new Exception("after write"); }))
            Refused(() => ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++), "a failing replacement reports a named pass fault");
        Check(builtIn == 6, "failure after original does not fall back and repeat its side effects");
        using (context.ReplaceSimulationPass(SimulationPass.Construction, (_, _) => throw new Exception("before write")))
            Refused(() => ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++), "failure before original is not silently hidden");
        Check(builtIn == 6, "failed replacement does not unexpectedly run a suppressed built-in");
        using (context.ReplaceSimulationPass(SimulationPass.Construction, (_, original) =>
                   Task.Run(original).GetAwaiter().GetResult()))
            Refused(() => ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++), "background continuation is refused before writing state");
        using (context.ReplaceSimulationPass(SimulationPass.Construction, (_, _) =>
                   ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++)))
            Refused(() => ModSimulationPasses.Run(c, SimulationPass.Construction, _ => builtIn++), "recursive pass execution is refused");
        context.SealSimulationPasses();
        Refused(() => context.ReplaceSimulationPass(SimulationPass.Logistics, (_, _) => { }), "OnFrame/day callbacks cannot install new replacements");
        context.ReleaseSimulationPasses(); context.ReleaseSimulationPasses();
        Check(CampaignFingerprint.Of(c) == before, "registration, inspection and disposal are not serialized world mutations");
        Pipeline();
    }

    private static void Pipeline()
    {
        var baseline = Campaign.CreateBootstrap(20261105);
        var modified = Campaign.CreateBootstrap(20261105);
        var context = new ModContext("pipeline_fixture", _ => { }) { Campaign = modified };
        var calls = new int[Enum.GetValues<SimulationPass>().Length];
        foreach (var pass in Enum.GetValues<SimulationPass>())
            context.ReplaceSimulationPass(pass, (_, original) => { calls[(int)pass]++; original(); });
        context.SealSimulationPasses();
        baseline.IsPaused = modified.IsPaused = true;
        modified.Tick();
        Check(calls.All(n => n == 0), "paused Tick invokes neither built-in nor replacement passes");
        baseline.IsPaused = modified.IsPaused = false;
        baseline.Tick(); modified.Tick();
        Check(calls.All(n => n == 1), "all four extension points run once at the real simulation boundary");
        Check(CampaignFingerprint.Of(baseline) == CampaignFingerprint.Of(modified), "forwarding all originals preserves the complete world and random state");
        context.ReleaseSimulationPasses();
        baseline.Tick(); modified.Tick();
        Check(calls.All(n => n == 1) && CampaignFingerprint.Of(baseline) == CampaignFingerprint.Of(modified), "unload restores the unmodified pipeline without leftover callbacks");
    }
}
