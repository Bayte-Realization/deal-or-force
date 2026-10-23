using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Clouds and the fog-of-war mist, each one press away.
///
/// Both are atmosphere, and atmosphere is in the way when what a player wants is to read the
/// world's borders - on the country-select globe above all, which is a map being studied. So
/// each has a button where the map is: on the map bar in a campaign, and beside the picker's
/// own buttons before one. A press flips the option in the settings, which is where it is
/// remembered, so every button for it - and the settings screen - says the same thing.
/// </summary>
public partial class DealOrForceGame
{
    // Compatibility entry points for source overlays that used the older helper. The HUD
    // and picker share one registry now; this adapter does not add another row of buttons.
    private Button WeatherToggle(string setting, string label, string tooltip, float width) =>
        MapVisualToggle(setting, label, tooltip, width);

    private void RefreshWeatherToggles() => RefreshMapVisualToggles();
}
