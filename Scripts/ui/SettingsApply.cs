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
    /// <summary>
    /// Whether this is the sweep rather than one row changing.
    ///
    /// The sweep runs three times as the game comes up, and some options are expensive to
    /// obey - renaming every country on the map rebuilds two hundred and sixty-one pieces of
    /// 3D text. Doing that once at the end of the sweep is the same result for a sixth of
    /// the work.
    /// </summary>
    private bool _applyingEverySetting;

    /// <summary>Put every saved preference into effect. Called once, as the game comes up.</summary>
    private void ApplyAllSettings()
    {
        FollowLanguageCulture();
        _applyingEverySetting = true;
        try
        {
            foreach (var definition in SettingsCatalog.All) ApplySetting(definition.Id);
        }
        finally
        {
            _applyingEverySetting = false;
        }

        CountryNames.Refresh();
        _settingsNeedRestart = false;
    }

    private static bool _followingLanguageCulture;

    /// <summary>
    /// Write numbers the way the language being read writes them (Loc.Culture), from now on
    /// and whenever the language changes - from this screen, the console or a mod.
    ///
    /// The interface used the culture Windows runs in, so an English game on a Russian machine
    /// read "136,0" and "12,5 %" beside "$1.43T". Applied to the interface's own thread and as
    /// the default for any other, and to nothing the simulation parses: its data is read in the
    /// invariant culture whatever this says.
    /// </summary>
    private static void FollowLanguageCulture()
    {
        if (!_followingLanguageCulture)
        {
            _followingLanguageCulture = true;
            Loc.LanguageChanged += ApplyLanguageCulture;
        }
        ApplyLanguageCulture();
    }

    private static void ApplyLanguageCulture()
    {
        var culture = Loc.Culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private void ApplySetting(string id)
    {
        // A naming option is not one of a fixed list. `country_name_<tag>` is however many
        // countries the base game and its mods argue about the name of, so it is matched by
        // its shape rather than by a case per country - which is what makes a mod able to
        // add one without touching this file.
        if (id.StartsWith(CountryNames.VariantPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (_applyingEverySetting) return;
            CountryNames.Refresh();
            RefreshLocalisedText();
            return;
        }

        switch (id.ToLowerInvariant())
        {
            // ---- the game ------------------------------------------------------------
            case "autosave_interval":
                if (_campaign is not null)
                    _campaign.Defines.AutosaveIntervalDays = Whole("autosave_interval");
                break;

            case "event_interval":
                if (_campaign is not null)
                    _campaign.Defines.EventIntervalDays = Whole("event_interval");
                break;

            case "peace_by_provinces":
                if (_campaign is not null)
                    _campaign.Defines.PeaceWholeRegions = !SettingsCatalog.Flag("peace_by_provinces");
                break;

            case "save_format":
                if (_campaign is not null)
                    _campaign.Defines.SaveTextFormat = SettingsCatalog.Chosen("save_format") != "false";
                break;
            case "civil_wars_players":
                if (_campaign is not null)
                    _campaign.Defines.CivilWarStrikesPlayers = SettingsCatalog.Chosen("civil_wars_players") == "true";
                break;

            case "hours_per_tick":
                if (_campaign is not null)
                {
                    _campaign.Defines.HoursPerTick = Math.Clamp(Whole("hours_per_tick"), 1, 240);
                    _dayAccumulator = 0;
                    if (!_applyingEverySetting && _startupComplete) Refresh();
                }
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

            case "city_names":
                if (_globe is not null) _globe.CityLabelsEnabled = SettingsCatalog.Flag("city_names");
                break;

            case "movement_flags":
                // Every flag is asked for afresh on the next refresh, so the top bar, the
                // panels and the picker all change together.
                if (_campaign is not null && !_applyingEverySetting) Refresh();
                break;

            case "space_backdrop":
                if (_globe is not null)
                    _globe.SpaceBackdropEnabled = SettingsCatalog.Flag("space_backdrop");
                break;

            case "map_weather":
                if (_globe is not null) _globe.WeatherEnabled = SettingsCatalog.Flag("map_weather");
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
            case "window_size":
                ApplyWindowMode(SettingsCatalog.Chosen("window_mode"));
                break;

            case "render_scale":
                if (_globe is not null &&
                    float.TryParse(SettingsCatalog.Chosen("render_scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var share))
                    _globe.RenderScale = share;
                break;

            case "vsync":
                DisplayServer.WindowSetVsyncMode(SettingsCatalog.Flag("vsync")
                    ? DisplayServer.VSyncMode.Enabled
                    : DisplayServer.VSyncMode.Disabled);
                break;

            // Only when the player changes it: the start-up sweep would put "automatic" back
            // over a renderer the start-up guard stepped down to on its own.
            case "renderer":
                if (!_applyingEverySetting) GraphicsRenderer.Choose(this, SettingsCatalog.Chosen("renderer"));
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

    /// <summary>
    /// The window mode, which is a DisplayServer call and nothing else - so the main menu,
    /// which has no campaign behind it, applies it through the same code rather than a
    /// second copy that drifts.
    /// </summary>
    /// <summary>
    /// The windowed size, "2560x1440", kept inside the screen the window is on and centred
    /// there. A size bigger than the screen is the screen's usable area.
    /// </summary>
    internal static void ApplyWindowSize(string size)
    {
        var parts = (size ?? "").ToLowerInvariant().Split('x');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            return;
        var screen = DisplayServer.WindowGetCurrentScreen();
        var usable = DisplayServer.ScreenGetUsableRect(screen);
        width = Math.Clamp(width, 640, Math.Max(640, usable.Size.X));
        height = Math.Clamp(height, 360, Math.Max(360, usable.Size.Y));
        DisplayServer.WindowSetSize(new Vector2I(width, height));
        DisplayServer.WindowSetPosition(usable.Position + (usable.Size - new Vector2I(width, height)) / 2);
    }

    internal static void ApplyWindowMode(string mode)
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
                ApplyWindowSize(SettingsCatalog.Chosen("window_size"));
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
