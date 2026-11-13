using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// The layer guide inside the map-modes drawer (`MapControlsUI`).
///
/// WHY THE HEIGHT IS FIXED:
/// Each map-mode button calls <see cref="ExplainMapLayer"/> on `MouseEntered` and `FocusEntered`.
/// Different layers have different text lengths, some layers (`Diplomatic`, `Wars`, `Tension`)
/// show a perspective line while others do not, and some layers (`Economy`, `Stability`, etc.)
/// show a colour legend while others (`Relief`, `Terrain`, `Political`, `Resources`) have none.
///
/// If `_mapLayerGuide` or its children were allowed to change vertical minimum size on hover,
/// the `VBoxContainer` in `_mapModeDrawerPanel` would recompute the share given to
/// `MapLayerDrawerScroll` above it (or resize the bottom-anchored drawer itself). When the user
/// moved the mouse between `Economy` (3 legend keys) and `Relief` (0 legend keys), the button
/// grid shifted up and down right under the cursor, causing the buttons to "dodge" the pointer
/// and oscillate back and forth!
///
/// To guarantee 0px of button movement across all 11 layers in both English and Russian:
/// 1. `_mapLayerGuide` has a fixed `CustomMinimumSize.Y` (`MapLayerGuideHeight`) and `ClipContents = true`.
/// 2. `_mapLayerGuideText`, `_mapLayerGuideSubject`, and `_mapLayerGuideKeys` each reserve a
///    constant vertical slot (`CustomMinimumSize.Y`) regardless of how many lines wrap or
///    whether the perspective/legend row is populated for the hovered layer.
/// 3. `_mapLayerGuideSubject` stays `Visible = true` with an empty string when unused so the
///    `VBoxContainer` never adds or removes a separation gap on hover.
/// </summary>
public partial class DealOrForceGame
{
    private const float MapLayerGuideHeight = 148f;
    private const float MapLayerGuideTextHeight = 58f;
    private const float MapLayerGuideSubjectHeight = 18f;
    private const float MapLayerGuideLegendHeight = 36f;

    private VBoxContainer _mapLayerGuide;
    private Label _mapLayerGuideTitle, _mapLayerGuideText, _mapLayerGuideSubject;
    private HFlowContainer _mapLayerGuideKeys;
    private StrategicMapMode _explainedMapMode;

    private void BuildMapLayerGuide(VBoxContainer parent)
    {
        _mapLayerGuide = new VBoxContainer
        {
            Name = "MapLayerGuide",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkEnd,
            CustomMinimumSize = new Vector2(0, MapLayerGuideHeight),
            ClipContents = true
        };
        _mapLayerGuide.AddThemeConstantOverride("separation", 4);
        parent.AddChild(_mapLayerGuide);

        _mapLayerGuideTitle = SituationText("", 14, "text.accent");
        _mapLayerGuideTitle.Name = "MapLayerGuideTitle";
        _mapLayerGuideTitle.MaxLinesVisible = 1;

        _mapLayerGuideText = SituationText("", 13, "text.body_soft");
        _mapLayerGuideText.Name = "MapLayerGuideText";
        _mapLayerGuideText.MaxLinesVisible = 3;
        _mapLayerGuideText.CustomMinimumSize = new Vector2(0, MapLayerGuideTextHeight);

        _mapLayerGuideSubject = SituationText("", 12, "text.caption");
        _mapLayerGuideSubject.Name = "MapLayerGuideSubject";
        _mapLayerGuideSubject.MaxLinesVisible = 1;
        _mapLayerGuideSubject.CustomMinimumSize = new Vector2(0, MapLayerGuideSubjectHeight);

        _mapLayerGuideKeys = new HFlowContainer
        {
            Name = "MapLayerLegend",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, MapLayerGuideLegendHeight)
        };
        _mapLayerGuideKeys.AddThemeConstantOverride("h_separation", 12);
        _mapLayerGuideKeys.AddThemeConstantOverride("v_separation", 4);

        _mapLayerGuide.AddChild(_mapLayerGuideTitle);
        _mapLayerGuide.AddChild(_mapLayerGuideText);
        _mapLayerGuide.AddChild(_mapLayerGuideSubject);
        _mapLayerGuide.AddChild(_mapLayerGuideKeys);
    }

    private void ExplainMapLayer(StrategicMapMode mode)
    {
        if (_mapLayerGuide is null) return;
        _explainedMapMode = mode;
        _mapLayerGuideTitle.Text = Loc.Format("Layer guide: {0}", Tr(mode == StrategicMapMode.Diplomatic ? "Diplomacy" : mode.ToString()));
        _mapLayerGuideText.Text = Tr(MapModeHelp(mode));
        var subject = _globe?.MapModeSubject ?? _campaign?.Player;
        var hasSubject = subject is not null && mode is StrategicMapMode.Diplomatic or StrategicMapMode.Wars or StrategicMapMode.Tension;
        // Keep Visible = true so the VBoxContainer separation and slot height never change on hover;
        // LayoutAudit automatically skips empty/whitespace labels.
        _mapLayerGuideSubject.Visible = true;
        _mapLayerGuideSubject.Text = hasSubject ? Loc.Format("Perspective: {0}", subject.DisplayName) : "";
        var keys = GlobePresentation.LayerLegend(mode);
        while (_mapLayerGuideKeys.GetChildCount() > keys.Count)
        {
            var extra = _mapLayerGuideKeys.GetChild(_mapLayerGuideKeys.GetChildCount() - 1);
            _mapLayerGuideKeys.RemoveChild(extra);
            extra.QueueFree();
        }
        while (_mapLayerGuideKeys.GetChildCount() < keys.Count)
        {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 5);
            row.AddChild(new ColorRect { CustomMinimumSize = new(11, 11),
                SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
            var caption = Title("", 12, Ui.Colour("text.body_soft"));
            // HFlow packs each key by its minimum width. Word wrapping would report
            // a one-pixel minimum and turn every short caption into a vertical column.
            caption.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(caption); _mapLayerGuideKeys.AddChild(row);
        }
        for (var i = 0; i < keys.Count; i++)
        {
            var row = _mapLayerGuideKeys.GetChild<HBoxContainer>(i);
            row.GetChild<ColorRect>(0).Color = keys[i].Colour;
            var caption = row.GetChild<Label>(1);
            var text = Tr(keys[i].Label);
            caption.Text = text;
            caption.SetMeta(LocSourceMeta, keys[i].Label);
            caption.SetMeta(LocAppliedMeta, text);
        }
    }
}
