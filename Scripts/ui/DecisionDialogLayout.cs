using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>Opt-in layout for the three consequential diplomatic documents.
/// Reflow, don't scale the text back down. Scrollable bodies absorb longer content;
/// their action footers are siblings, so the last action never scrolls off the page.
/// Only a changed window/scale triggers a tree walk, not every simulation tick.
/// This node belongs to the dialog and owns no subscriptions on a surviving window.</summary>
public partial class DecisionDialogLayout : Node
{
    public Control Panel;
    public string LayoutKey;
    // Compact map tools reuse physical text/hit floors without becoming a modal.
    public bool PreservePlacement;
    public Control AvoidLeft, AvoidTop;
    private Vector2 _canvas, _pixelScale;
    private float _interfaceScale = -1;
    private double _nextCheck;
    private Vector2 _position, _size;
    private int _settleFrames;

    public override void _Ready() => Fit();

    public override void _Process(double delta)
    {
        // Font changes invalidate minimum sizes lazily. A PanelContainer can retain
        // the tall size it calculated before wrapped paragraphs got their real width.
        // Let the containers sort, then reassert the bounded rectangle (no tree walk).
        if (_settleFrames > 0) { _settleFrames--; Panel.Position = _position; Panel.Size = _size; }
        _nextCheck -= delta;
        if (_nextCheck > 0) return;
        _nextCheck = .15;
        Fit();
    }

    private void Fit()
    {
        if (Panel?.GetParent() is not Control parent || !Panel.IsVisibleInTree()) return;
        var canvas = parent.Size;
        var pixelScale = (GetViewport().GetStretchTransform() * parent.GetGlobalTransformWithCanvas()).Scale.Abs();
        var preference = Math.Clamp(SettingsCatalog.Number("interface_scale"), .5f, 2.5f);
        if (canvas == _canvas && pixelScale.IsEqualApprox(_pixelScale) && Math.Abs(preference - _interfaceScale) < .001f) return;
        _canvas = canvas; _pixelScale = pixelScale; _interfaceScale = preference;
        var scale = MathF.Min(pixelScale.X, pixelScale.Y);
        var authored = InterfaceSkin.Layout(LayoutKey ?? "modal.ultimatum");
        var left = canvas.X * authored.AnchorLeft + authored.OffsetLeft;
        var top = canvas.Y * authored.AnchorTop + authored.OffsetTop;
        var right = canvas.X * authored.AnchorRight + authored.OffsetRight;
        var bottom = canvas.Y * authored.AnchorBottom + authored.OffsetBottom;
        var fit = DecisionReadability.Fit(canvas.X, canvas.Y, scale, left, top, right - left, bottom - top,
            Ui.Metric("decision.minimum_width_pixels", 1100), Ui.Metric("decision.minimum_height_pixels", 760),
            Ui.Metric("decision.margin_pixels", 20));
        Panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        var local = parent.GetGlobalTransformWithCanvas().AffineInverse();
        var toolLeft = AvoidLeft is null ? 120 : MathF.Max(120, (local * AvoidLeft.GetGlobalRect().End).X + 12);
        var toolTop = AvoidTop is null ? 148 : MathF.Max(148, (local * AvoidTop.GetGlobalRect().End).Y + 76);
        _position = PreservePlacement ? new Vector2(toolLeft, toolTop) : new Vector2(fit.X, fit.Y);
        _size = PreservePlacement ? new Vector2(MathF.Min(620, canvas.X - toolLeft - 20), 180) : new Vector2(fit.Width, fit.Height);
        Panel.Position = _position; Panel.Size = _size;
        foreach (var control in Panel.FindChildren("*", "Control", true, false).OfType<Control>())
        {
            if (control is not (Label or Button or LineEdit)) continue;
            if (control.HasMeta("readability_label_width_pixels"))
                control.CustomMinimumSize = new Vector2(MathF.Max(170, control.GetMeta("readability_label_width_pixels").AsSingle() / MathF.Max(.01f, scale)), control.CustomMinimumSize.Y);
            if (!control.HasMeta("decision_authored_font"))
            {
                // LabelSettings outranks a theme override. The old negotiation fit changed
                // the latter and left every explanatory paragraph at eight physical pixels.
                var original = control is Label { LabelSettings: not null } label
                    ? label.LabelSettings.FontSize : control.GetThemeFontSize("font_size");
                control.SetMeta("decision_authored_font", original);
                if (control is Label { LabelSettings: not null } owned)
                    owned.LabelSettings = (LabelSettings)owned.LabelSettings.Duplicate();
            }
            var font = DecisionReadability.FontSize(control.GetMeta("decision_authored_font").AsInt32(), scale, preference,
                Ui.Metric("decision.minimum_font_pixels", 14));
            if (control is Label { LabelSettings: not null } paragraph) paragraph.LabelSettings.FontSize = font;
            else control.AddThemeFontSizeOverride("font_size", font);
            if (control is OptionButton choice) choice.GetPopup().AddThemeFontSizeOverride("font_size", font);
            if (control is not (Button or LineEdit)) continue;
            if (!control.HasMeta("decision_authored_height")) control.SetMeta("decision_authored_height", control.CustomMinimumSize.Y);
            control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X,
                DecisionReadability.TargetHeight(control.GetMeta("decision_authored_height").AsSingle(), scale, preference,
                    Ui.Metric("decision.minimum_target_pixels", 32)));
        }
        _settleFrames = 3;
    }
}
