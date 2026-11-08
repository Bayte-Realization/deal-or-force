using DealOrForce.Modding;
using DealOrForce.Simulation;

namespace ExampleCSharpMod;

public sealed class ExampleMod : IGlobalityMod
{
    private bool _reportedPass;
    public void OnLoad(ModContext context)
    {
        context.Log("Example corecode mod loaded");
        // A real supported core replacement. Here we preserve the built-in work;
        // omitting original() would replace it, not run a second construction loop.
        context.ReplaceSimulationPass(SimulationPass.Construction, (_, original) =>
        {
            original();
            if (!_reportedPass) { context.Log("Construction pass forwarded once"); _reportedPass = true; }
        });
    }

    public void OnCampaignCreated(ModContext context)
    {
        var korea = context.Nation("KOR");
        if (korea is not null)
            context.Log($"Campaign ready: {korea.Name}, {korea.Population:N0} people");
    }

    public void OnDayAdvanced(ModContext context)
    {
        if (context.Day % 30 != 0) return;
        var korea = context.Nation("KOR");
        if (korea is not null)
            korea.Stability = Math.Clamp(korea.Stability + 0.002f, 0f, 1f);
    }
}
