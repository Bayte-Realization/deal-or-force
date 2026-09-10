using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly Dictionary<StrategicMapMode, Button> _mapModeButtons = new();
    private Button _projectionButton = null!;
    private OptionButton _mapModeLegend = null!;

    private void BuildMapControls()
    {
        var panel = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        panel.Name = "MapControls";
        Ui.Place(panel, "hud.map_controls");

        // The strip artwork has a transparent bevel before its visible inner rail.
        // Eight pixels kept the controls inside the Control rectangle, but not inside
        // the rail the player actually reads as the panel boundary. Keep the whole
        // toolbar clear of that bevel, especially the projection button on the left.
        var margin = Margin(panel, 24, 6);
        margin.ClipContents = true;
        var row = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Begin,
            ClipContents = true
        };
        row.AddThemeConstantOverride("separation", 5);
        margin.AddChild(row);

        _projectionButton = ActionButton("3D GLOBE", ToggleProjection, 104, 34, true);
        _projectionButton.TooltipText = Tr("Switch between the globe and an infinitely wrapping classic map (F2).");
        row.AddChild(_projectionButton);

        AddMode(row, StrategicMapMode.Political, "POL", "Political: countries, controllers and occupied provinces.");
        AddMode(row, StrategicMapMode.Wars, "WAR", "Wars: belligerents, fronts and occupied territory.");
        AddMode(row, StrategicMapMode.Terrain, "TER", "Terrain: geographical surface without political colours. Hover a province for combat and movement effects.");

        // A dropdown keeps all seven layers reachable without pushing the strip past its artwork.
        _mapModeLegend = new OptionButton { CustomMinimumSize = new Vector2(130, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false,
            TooltipText = Tr("All map layers, including diplomatic, population, economy and the moddable relief layer.") };
        foreach (StrategicMapMode mode in System.Enum.GetValues<StrategicMapMode>())
            _mapModeLegend.AddItem(mode.ToString(), (int)mode);
        _mapModeLegend.ItemSelected += index => _globe.SetMapMode((StrategicMapMode)_mapModeLegend.GetItemId((int)index));
        row.AddChild(_mapModeLegend);

        _globe.ProjectionChanged += flat =>
        {
            _projectionButton.Text = flat ? "2D MAP" : "3D GLOBE";
            if (_mainProjectionButton is not null)
                _mainProjectionButton.Text = flat ? "Map view: 2D classic" : "Map view: 3D globe";
            if (_navigationHint is not null)
                _navigationHint.Text = flat
                    ? "Right-drag to pan · wheel to zoom · WASD to move · the world wraps horizontally."
                    : "Right-drag to turn the globe · wheel to zoom · WASD to move.";
        };
        _globe.MapModeChanged += UpdateMapModeButtons;
        UpdateMapModeButtons(StrategicMapMode.Political);
    }

    private void AddMode(Container row, StrategicMapMode mode, string caption, string tooltip)
    {
        var button = ActionButton(caption, () => _globe.SetMapMode(mode), 54, 34,
            mode == StrategicMapMode.Political);
        button.TooltipText = tooltip;
        row.AddChild(button);
        _mapModeButtons[mode] = button;
    }

    private void UpdateMapModeButtons(StrategicMapMode active)
    {
        foreach (var pair in _mapModeButtons)
        {
            var shortName = pair.Key switch
            {
                StrategicMapMode.Political => "POL",
                StrategicMapMode.Diplomatic => "DIP",
                StrategicMapMode.Wars => "WAR",
                StrategicMapMode.Population => "POP",
                StrategicMapMode.Economy => "GDP",
                StrategicMapMode.Terrain => "TER",
                _ => "MAP"
            };
            pair.Value.Text = pair.Key == active ? $"[{shortName}]" : shortName;
        }
        if (_mapModeLegend is not null) _mapModeLegend.Select(_mapModeLegend.GetItemIndex((int)active));
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
