using System.Collections.Generic;
using Godot;

namespace ByDealOrForce;

public partial class GlobalityGame
{
    private readonly Dictionary<StrategicMapMode, Button> _mapModeButtons = new();
    private Button _projectionButton = null!;
    private Label _mapModeLegend = null!;

    private void BuildMapControls()
    {
        var panel = TexturedPanel(_hudLayer);
        panel.Name = "MapControls";
        panel.AnchorLeft = 0f;
        panel.AnchorRight = 0f;
        panel.AnchorTop = 1f;
        panel.AnchorBottom = 1f;
        panel.OffsetLeft = 14f;
        panel.OffsetRight = 660f;
        panel.OffsetTop = -154f;
        panel.OffsetBottom = -106f;

        var margin = Margin(panel, 8, 6);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin };
        row.AddThemeConstantOverride("separation", 5);
        margin.AddChild(row);

        _projectionButton = ActionButton("3D GLOBE", ToggleProjection, 104, 34, true);
        _projectionButton.TooltipText = "Switch between the globe and an infinitely wrapping classic map (F2).";
        row.AddChild(_projectionButton);

        AddMode(row, StrategicMapMode.Political, "POL", "Political: countries, controllers and occupied provinces.");
        AddMode(row, StrategicMapMode.Diplomatic, "DIP", "Diplomatic: relations with the player country.");
        AddMode(row, StrategicMapMode.Wars, "WAR", "Wars: belligerents, fronts and occupied territory.");
        AddMode(row, StrategicMapMode.Population, "POP", "Population: logarithmic population density by country.");
        AddMode(row, StrategicMapMode.Economy, "GDP", "Economy: logarithmic GDP scale by country.");

        _mapModeLegend = Title("POLITICAL", 12, new Color("#d7c57b"));
        _mapModeLegend.CustomMinimumSize = new Vector2(92, 0);
        _mapModeLegend.HorizontalAlignment = HorizontalAlignment.Center;
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
                _ => "MAP"
            };
            pair.Value.Text = pair.Key == active ? $"[{shortName}]" : shortName;
        }
        if (_mapModeLegend is not null) _mapModeLegend.Text = active.ToString().ToUpperInvariant();
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
