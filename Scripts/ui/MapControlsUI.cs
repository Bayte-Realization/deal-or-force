using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly Dictionary<StrategicMapMode, Button> _mapModeButtons = new();
    private Button _projectionButton = null!;

    private void BuildMapControls()
    {
        var panel = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        panel.Name = "MapControls";
        Ui.Place(panel, "hud.map_controls");

        // The strip artwork has a transparent bevel before its visible inner rail.
        // Eight pixels kept the controls inside the Control rectangle, but not inside
        // the rail the player actually reads as the panel boundary. Keep the whole
        // toolbar clear of that bevel, especially the projection button on the left.
        var margin = Margin(panel, 8, 4);
        margin.ClipContents = true;
        var row = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Begin,
            ClipContents = true
        };
        row.AddThemeConstantOverride("separation", 5);
        margin.AddChild(row);

        _projectionButton = ActionButton("3D GLOBE", ToggleProjection, 104, 34, true);
        _projectionButton.TooltipText = Ui.Tip(Tr("Switch between the globe and an infinitely wrapping classic map (F2)."));
        row.AddChild(_projectionButton);

        // One mark per layer, the name and what it shows in the tooltip.
        //
        // This was three abbreviations and a dropdown: "POL", "WAR", "TER", and the other
        // four layers two clicks away behind a list. An abbreviation is a word a player has
        // to learn before it tells them anything, it half-translates into every other
        // language, and hiding four of seven layers meant most of what the map can show was
        // never found. Seven buttons of forty pixels are narrower than that arrangement was.
        foreach (StrategicMapMode mode in System.Enum.GetValues<StrategicMapMode>())
            AddMode(row, mode);

        _globe.ProjectionChanged += flat =>
        {
            _projectionButton.Text = Tr(flat ? "2D MAP" : "3D GLOBE");
            if (_mainProjectionButton is not null)
                _mainProjectionButton.Text = flat ? "Map view: 2D classic" : "Map view: 3D globe";
            if (_navigationHint is not null)
                _navigationHint.Text = flat
                    ? "Right-drag to pan · wheel to zoom · WASD to move · the world wraps horizontally."
                    : "Right-drag to turn the globe · wheel to zoom · WASD to move.";
        };
        _globe.MapModeChanged += UpdateMapModeButtons;
        _globe.MapModeSubjectChanged += _ => UpdateMapModeTooltips();
        UpdateMapModeButtons(StrategicMapMode.Political);
    }

    /// <summary>
    /// The two layers that are about somebody say whose. Diplomacy and war are drawn for the
    /// country picked on the map, and a tooltip that did not say so would leave a player
    /// reading Brazil's alliances as their own.
    /// </summary>
    private void UpdateMapModeTooltips()
    {
        var subject = _globe.MapModeSubject;
        foreach (var pair in _mapModeButtons)
        {
            var tip = Tr(pair.Key.ToString()) + " \u2014 " + Tr(MapModeHelp(pair.Key));
            if (pair.Key is StrategicMapMode.Diplomatic or StrategicMapMode.Wars && subject is not null)
                tip += "\n" + Simulation.Loc.Format(
                    "Seen from {0}. Pick another country on the map to see it from theirs; close the panel to come back to your own.",
                    subject.DisplayName);
            pair.Value.TooltipText = Ui.Tip(tip);
        }
    }

    /// <summary>
    /// The mark for one map layer.
    ///
    /// Drawn from the same set the top bar and the alert rail use, so a flag means a country
    /// wherever it appears and crossed swords mean a war. Terrain is a mountain with a tree
    /// at its foot and relief is a contour map: the ground and its height, not a country.
    /// </summary>
    private static string MapModeIcon(StrategicMapMode mode) => mode switch
    {
        StrategicMapMode.Political => "nation",
        StrategicMapMode.Diplomatic => "diplomacy",
        StrategicMapMode.Wars => "wars",
        StrategicMapMode.Population => "population",
        StrategicMapMode.Economy => "economy",
        StrategicMapMode.Terrain => "terrain",
        _ => "relief"
    };

    /// <summary>What a layer actually shows, for the tooltip under its mark.</summary>
    private static string MapModeHelp(StrategicMapMode mode) => mode switch
    {
        StrategicMapMode.Political => "Countries, who controls what, and every occupied province.",
        StrategicMapMode.Diplomatic => "Who is allied to whom, who guarantees whom, and who answers to whom.",
        StrategicMapMode.Wars => "Belligerents, the fronts between them and the ground that has changed hands.",
        StrategicMapMode.Population => "Where the people are. Dark is empty, bright is crowded.",
        StrategicMapMode.Economy => "What each region produces, against what it needs.",
        StrategicMapMode.Terrain => "The ground itself, with no politics on it. Hover a province for what it does to a march and a battle.",
        _ => "Height and shade, from the moddable relief layer."
    };

    private void AddMode(Container row, StrategicMapMode mode)
    {
        var icon = MapModeIcon(mode);
        var button = ActionButton("", () => _globe.SetMapMode(mode), 40, 34,
            mode == StrategicMapMode.Political,
            Simulation.SpriteCatalog.TextureOf("icon_" + icon,
                $"res://game/gfx/interface/icons/icon_{icon}.png"));
        button.TooltipText = Ui.Tip(Tr(mode.ToString()) + " \u2014 " + Tr(MapModeHelp(mode)));
        row.AddChild(button);
        _mapModeButtons[mode] = button;
    }

    private void UpdateMapModeButtons(StrategicMapMode active)
    {
        // Which one is on is now a matter of how the button is drawn rather than of square
        // brackets around an abbreviation.
        foreach (var pair in _mapModeButtons) StyleButton(pair.Value, pair.Key == active);
        UpdateMapModeTooltips();
    }

    private void ToggleProjection() => _globe.ToggleProjection();

    private void HandleProjectionHotkey(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F2 })
        {
            ToggleProjection();
            GetViewport().SetInputAsHandled();
        }
    }
}
