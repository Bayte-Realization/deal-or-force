using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private string RadicalismTooltip(ScenarioState state)
    {
        var owner = _campaign.Nations.Find(n => n.MapIndex == state.NationMapIndex);
        var forecast = RadicalismCore.Forecast(_campaign, owner, state);
        var rows = forecast.Entries.Select(e => Tr(e.Source) + ": " +
            Simulation.Loc.Format("{0:+0.000;-0.000;0.000} pp/week", e.Value * 100));
        return Tr("Radicalism: weekly forecast") + "\n" + string.Join("\n", rows) + "\n" +
               Tr("Total") + ": " + Simulation.Loc.Format("{0:+0.000;-0.000;0.000} pp/week", forecast.Total * 100) +
               "\n\n" + Tr("Improve living conditions and supply, control inflation, fund welfare or choose Social cohesion. Recovery is gradual; war and occupation slow it. Forecast excludes one-off events and scripted national modifiers.");
    }
}
