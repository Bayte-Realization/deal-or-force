using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    /// <summary>Read the same palette functions as the map, not a second set of legend colours.
    /// Values are scale endpoints, not invented absolute GDP/population thresholds.</summary>
    public static IReadOnlyList<(string Label, Color Colour)> LayerLegend(StrategicMapMode mode) => mode switch
    {
        StrategicMapMode.Population => new[] { ("map.legend.lower_total", HeatRamp(0)), ("map.legend.higher_total", HeatRamp(1)), ("map.legend.unknown", Ui.Colour("map.neutral")) },
        StrategicMapMode.Economy => new[] { ("map.legend.lower_total", EconomicRamp(0)), ("map.legend.higher_total", EconomicRamp(1)), ("map.legend.unknown", Ui.Colour("map.neutral")) },
        StrategicMapMode.Stability => new[] { ("map.legend.low_stability", StabilityRamp(0)), ("map.legend.mid_stability", StabilityRamp(.5f)), ("map.legend.high_stability", StabilityRamp(1)) },
        StrategicMapMode.Unrest => new[] { ("map.legend.quiet", UnrestRamp(0)), ("map.legend.restless", UnrestRamp(.5f)), ("map.legend.unrest", UnrestRamp(1)) },
        StrategicMapMode.Tension => new[] { ("map.legend.crisis", Ui.Colour("state.warning")), ("map.legend.clashes", Ui.Colour("state.critical")), ("map.legend.war", Ui.Colour("map.at_war")) },
        StrategicMapMode.Diplomatic => new[] { ("map.legend.self", Ui.Colour("map.player")), ("map.legend.allied", Ui.Colour("map.allied")), ("map.legend.rival", Ui.Colour("map.rival")), ("map.legend.war", Ui.Colour("map.at_war")) },
        StrategicMapMode.Wars => new[] { ("map.legend.attacker", Ui.Colour("map.war_attacker")), ("map.legend.defender", Ui.Colour("map.war_defender")), ("map.legend.other_belligerent", Ui.Colour("map.war_bystander")) },
        _ => System.Array.Empty<(string, Color)>()
    };
}
