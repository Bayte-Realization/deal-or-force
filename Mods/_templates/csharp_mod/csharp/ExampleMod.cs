using DealOrForce.Modding;

namespace ExampleCSharpMod;

public sealed class ExampleMod : IGlobalityMod
{
    public void OnLoad(ModContext context) => context.Log("Example C# mod loaded");

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
