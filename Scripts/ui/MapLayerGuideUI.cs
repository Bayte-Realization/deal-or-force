using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _mapLayerGuide;
    private Label _mapLayerGuideTitle, _mapLayerGuideText, _mapLayerGuideSubject;
    private HFlowContainer _mapLayerGuideKeys;
    private StrategicMapMode _explainedMapMode;

    private void BuildMapLayerGuide(VBoxContainer parent)
    {
        _mapLayerGuide = new VBoxContainer { Name = "MapLayerGuide", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mapLayerGuide.AddThemeConstantOverride("separation", 5); parent.AddChild(_mapLayerGuide);
        _mapLayerGuideTitle = SituationText("", 14, "text.accent"); _mapLayerGuideTitle.Name = "MapLayerGuideTitle";
        _mapLayerGuideText = SituationText("", 13, "text.body_soft"); _mapLayerGuideText.Name = "MapLayerGuideText";
        _mapLayerGuideSubject = SituationText("", 12, "text.caption"); _mapLayerGuideSubject.Name = "MapLayerGuideSubject";
        _mapLayerGuideKeys = new HFlowContainer { Name = "MapLayerLegend", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mapLayerGuideKeys.AddThemeConstantOverride("h_separation", 12); _mapLayerGuideKeys.AddThemeConstantOverride("v_separation", 4);
        _mapLayerGuide.AddChild(_mapLayerGuideTitle); _mapLayerGuide.AddChild(_mapLayerGuideText);
        _mapLayerGuide.AddChild(_mapLayerGuideSubject); _mapLayerGuide.AddChild(_mapLayerGuideKeys);
    }

    private void ExplainMapLayer(StrategicMapMode mode)
    {
        if (_mapLayerGuide is null) return;
        _explainedMapMode = mode;
        _mapLayerGuideTitle.Text = Loc.Format("Layer guide: {0}", Tr(mode == StrategicMapMode.Diplomatic ? "Diplomacy" : mode.ToString()));
        _mapLayerGuideText.Text = Tr(MapModeHelp(mode));
        var subject = _globe?.MapModeSubject ?? _campaign?.Player;
        _mapLayerGuideSubject.Visible = subject is not null && mode is StrategicMapMode.Diplomatic or StrategicMapMode.Wars or StrategicMapMode.Tension;
        _mapLayerGuideSubject.Text = subject is null ? "" : Loc.Format("Perspective: {0}", subject.DisplayName);
        foreach (var child in _mapLayerGuideKeys.GetChildren()) { _mapLayerGuideKeys.RemoveChild(child); child.QueueFree(); }
        foreach (var key in GlobePresentation.LayerLegend(mode))
        {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 5);
            row.AddChild(new ColorRect { Color = key.Colour, CustomMinimumSize = new(11, 11),
                SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
            var caption = Title(Tr(key.Label), 12, Ui.Colour("text.body_soft"));
            // HFlow packs each key by its minimum width. Word wrapping would report
            // a one-pixel minimum and turn every short caption into a vertical column.
            caption.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(caption); _mapLayerGuideKeys.AddChild(row);
        }
        QueueMapControlsLayout();
    }
}
