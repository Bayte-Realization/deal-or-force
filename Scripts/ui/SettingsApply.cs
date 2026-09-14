using System;
using System.Globalization;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// What the game does about an option it recognises.
///
/// The screen is data and this is the other half of it. Kept apart from the drawing because
/// the two change for different reasons: adding a row to the screen is a line in a JSON
/// file, and teaching the game to obey a new one is a case here.
///
/// Every option is applied the moment it changes and again at startup, from the same code,
/// so there is no way for the screen and the running game to disagree about what is on.
/// </summary>
public partial class DealOrForceGame
{
    /// <summary>Put every saved preference into effect. Called once, as the game comes up.</summary>
    private void ApplyAllSettings()
    {
        foreach (var definition in SettingsCatalog.All) ApplySetting(definition.Id);
        _settingsNeedRestart = false;
    }

    private void ApplySetting(string id)
    {
        switch (id.ToLowerInvariant())
        {
            // ---- the game ------------------------------------------------------------
            case "autosave_interval":
                if (_campaign is not null)
                    _campaign.Defines.AutosaveIntervalDays = Whole("autosave_interval");
                break;

            case "hours_per_tick":
                if (_campaign is not null)
                    _campaign.Defines.HoursPerTick = Math.Clamp(Whole("hours_per_tick"), 1, 240);
                break;

            // `pause_on_event` and `confirm_declaration` are read where they are used
            // rather than pushed anywhere, because both are questions asked at the moment
            // something happens and there is nothing to keep in step.
            case "pause_on_event":
            case "confirm_declaration":
                break;

            // ---- the interface -------------------------------------------------------
            case "language":
                var language = SettingsCatalog.Chosen("language");
                if (language.Length > 0 &&
                    !string.Equals(language, Simulation.Loc.Language, StringComparison.OrdinalIgnoreCase))
                {
                    Simulation.Loc.Language = language;
                    RefreshLocalisedText();
                    if (_languageButton is not null && GodotObject.IsInstanceValid(_languageButton))
                    {
                        var caption = LanguageButtonLabel();
                        _languageButton.Text = caption;
                        _languageButton.SetMeta(LocAppliedMeta, caption);
                    }
                }
                break;

            case "tooltip_width":
                Ui.TooltipMeasure = Math.Clamp(Whole("tooltip_width"), 32, 200);
                break;

            case "dim_behind_events":
                if (_eventVeil is not null)
                    _eventVeil.Visible = SettingsCatalog.Flag("dim_behind_events");
                break;

            case "region_names":
                if (_globe is not null) _globe.CountryLabelsEnabled = SettingsCatalog.Flag("region_names");
                break;

            case "space_backdrop":
                if (_globe is not null)
                    _globe.SpaceBackdropEnabled = SettingsCatalog.Flag("space_backdrop");
                break;

            case "map_smoothing":
                if (_globe is not null && _globe.MapSmoothing != SettingsCatalog.Flag("map_smoothing"))
                    ToggleMapSmoothing();
                break;

            case "projection":
                var flat = string.Equals(SettingsCatalog.Chosen("projection"), "flat",
                    StringComparison.OrdinalIgnoreCase);
                _globe?.SetFlatMap(flat);
                break;

            // ---- what it looks like --------------------------------------------------
            case "window_mode":
                ApplyWindowMode(SettingsCatalog.Chosen("window_mode"));
                break;

            case "vsync":
                DisplayServer.WindowSetVsyncMode(SettingsCatalog.Flag("vsync")
                    ? DisplayServer.VSyncMode.Enabled
                    : DisplayServer.VSyncMode.Disabled);
                break;

            case "max_fps":
                Engine.MaxFps = Math.Max(0, Whole("max_fps"));
                break;

            case "interface_scale":
                // Godot scales the whole content of the window for us, which is the only
                // way to do this that does not need every panel in the game to know about
                // it. The window itself is unchanged; the interface inside it grows.
                var window = GetWindow();
                if (window is not null)
                    window.ContentScaleFactor = Math.Clamp(SettingsCatalog.Number("interface_scale"), 0.5f, 2.5f);
                break;

            // ---- sound ---------------------------------------------------------------
            case "volume_master":
            case "volume_music":
            case "volume_effects":
                ApplyVolumes();
                break;
        }
    }

    private static int Whole(string id) =>
        int.TryParse(SettingsCatalog.Chosen(id), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : (int)MathF.Round(SettingsCatalog.Number(id));

    private void ApplyWindowMode(string mode)
    {
        switch (mode.ToLowerInvariant())
        {
            case "fullscreen":
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
                break;
            case "windowed":
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
                break;
            default:
                // Borderless fullscreen: the picture of exclusive fullscreen with the
                // alt-tab of a window, which is what a strategy player with a wiki open on
                // the other monitor actually wants.
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true);
                break;
        }
    }
}
