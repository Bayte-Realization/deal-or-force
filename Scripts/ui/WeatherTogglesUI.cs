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
    private readonly List<(Button Button, string Setting)> _weatherToggles = new();

    /// <summary>A toggle for one weather option ("map_clouds" or "map_mist").</summary>
    private Button WeatherToggle(string setting, string label, string tooltip, float width)
    {
        var button = ActionButton(label, () => FlipWeather(setting), width, 34);
        button.Name = "WeatherToggle_" + setting;
        button.ToggleMode = true;
        button.TooltipText = Ui.Tip(Tr(tooltip));
        _weatherToggles.Add((button, setting));
        StyleWeatherToggle(button, setting);
        return button;
    }

    private void FlipWeather(string setting)
    {
        SettingsCatalog.Store(setting, !SettingsCatalog.Flag(setting));
        ApplySetting(setting);
        RefreshWeatherToggles();
    }

    /// <summary>Every weather button, after the option changed wherever it was changed from.</summary>
    private void RefreshWeatherToggles()
    {
        _weatherToggles.RemoveAll(entry => !IsInstanceValid(entry.Button));
        foreach (var (button, setting) in _weatherToggles) StyleWeatherToggle(button, setting);
    }

    private void StyleWeatherToggle(Button button, string setting)
    {
        var on = SettingsCatalog.Flag(setting);
        button.SetPressedNoSignal(on);
        StyleButton(button, on);
    }
}
