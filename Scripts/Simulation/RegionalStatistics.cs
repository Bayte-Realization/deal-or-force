using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class ScenarioState
{
    // Last regional recipe output valued at the prices available during that market pass.
    // Negative means an older save or a region not yet simulated, not an economy of zero.
    public float RegionalOutputValue = -1f;
}

public static class RegionalStatistics
{
    public const string GdpExplanation = "Estimated share of national GDP, based on the region's latest production value. Before production data is available, population shares are used. Public statistics are not hidden by fog of war.";
    public static float EstimatedGdp(Campaign campaign, ScenarioState state)
    {
        if (state is null) return 0;
        var nation = campaign.Nations.Find(n => n.MapIndex == state.NationMapIndex);
        if (nation is null || !float.IsFinite(nation.Gdp)) return 0;
        var states = campaign.StatesOf(nation);
        var hasOutput = states.All(s => float.IsFinite(s.RegionalOutputValue) && s.RegionalOutputValue >= 0);
        double total = hasOutput ? states.Sum(s => (double)s.RegionalOutputValue) : 0;
        var weight = (double)state.RegionalOutputValue;
        if (total <= 0)
        {
            total = states.Sum(s => (double)Math.Max(0, s.Population));
            weight = Math.Max(0, state.Population);
        }
        return total > 0 ? (float)(Math.Max(0, nation.Gdp) * weight / total) : 0;
    }
}
