using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private PanelContainer _doctrineScreenPanel;
    private Control _doctrineCatalogueColumn, _doctrinePreviewColumn;
    private readonly Dictionary<string, Button> _doctrineBookButtons = new(StringComparer.Ordinal);
    private Vector2 _doctrineComfortViewport;
    private float _doctrineComfortScale = -1, _doctrineComfortPreference = -1;
    private float _doctrineSummaryWeekly = float.NaN;
    private string _doctrineSummaryCountryName = "";

    private float DoctrinePixelScale()
    {
        var host = _doctrineLayer is { Visible: true } ? _doctrineLayer : _hudLayer;
        var pixels = (GetViewport().GetStretchTransform() * host.GetGlobalTransformWithCanvas()).Scale.Abs();
        return MathF.Max(.01f, MathF.Min(pixels.X, pixels.Y));
    }

    private float DoctrineUnits(float pixels) => pixels * MathF.Max(1, SettingsCatalog.Number("interface_scale")) / DoctrinePixelScale();

    private bool UpdateDoctrineComfortGeometry()
    {
        var scale = DoctrinePixelScale(); var preference = SettingsCatalog.Number("interface_scale");
        var viewport = GetViewport().GetVisibleRect().Size;
        var changed = viewport != _doctrineComfortViewport || MathF.Abs(scale - _doctrineComfortScale) > .001f ||
            MathF.Abs(preference - _doctrineComfortPreference) > .001f;
        _doctrineComfortViewport = viewport; _doctrineComfortScale = scale; _doctrineComfortPreference = preference;
        if (_doctrineScreenPanel is null) return changed;
        // Restore the authored/modded rectangle first. The translated navigation is a
        // separate physical-pixel rail and can be wider than its reference-resolution box.
        Ui.Place(_doctrineScreenPanel, "modal.doctrines");
        if (_hudNavigationPanel is not null)
        {
            var local = _doctrineLayer.GetGlobalTransformWithCanvas().AffineInverse();
            var railEnd = local * (_hudNavigationPanel.GetGlobalTransformWithCanvas() * _hudNavigationPanel.Size);
            _doctrineScreenPanel.OffsetLeft += MathF.Max(0, railEnd.X + 12 / scale - _doctrineScreenPanel.Position.X);
        }
        var available = _doctrineScreenPanel.Size.X * scale;
        _doctrineCatalogueColumn.CustomMinimumSize = new Vector2(Math.Clamp(available * .25f, 250, 350) / scale, 0);
        _doctrinePreviewColumn.CustomMinimumSize = new Vector2(Math.Clamp(available * .22f, 230, 320) / scale, 0);
        // The diagram has its own scroll; it must not force an outer vertical scroll.
        if (_doctrineWindow is not null) _doctrineWindow.CustomMinimumSize = new Vector2(0, DoctrineUnits(220));
        return changed;
    }

    private void FitDoctrineTypography(Node root)
    {
        var scale = DoctrinePixelScale();
        var preference = Math.Clamp(SettingsCatalog.Number("interface_scale"), .5f, 2.5f);
        Fit(root);
        void Fit(Node node)
        {
            if (node is Control control && control is Label or Button)
            {
                if (!control.HasMeta("doctrine_base_font")) control.SetMeta("doctrine_base_font", control.GetThemeFontSize("font_size"));
                var authored = control.GetMeta("doctrine_base_font").AsInt32();
                var minimum = Ui.Metric(authored >= 20 ? "doctrine.minimum_heading_pixels" :
                    authored <= 12 ? "doctrine.minimum_caption_pixels" : "doctrine.minimum_font_pixels",
                    authored >= 20 ? 20 : authored <= 12 ? 12 : 14);
                var font = DecisionReadability.FontSize(authored, scale, preference, minimum);
                if (control.GetThemeFontSize("font_size") != font) control.AddThemeFontSizeOverride("font_size", font);
                if (control is Label label)
                {
                    if (label.Name.ToString().StartsWith("Title_", StringComparison.Ordinal) || label.Name.ToString().StartsWith("Price_", StringComparison.Ordinal))
                    {
                        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                        label.ClipText = false;
                        label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
                    }
                }
                else
                {
                    if (!control.HasMeta("doctrine_base_height")) control.SetMeta("doctrine_base_height", control.CustomMinimumSize.Y);
                    var height = DecisionReadability.TargetHeight(control.GetMeta("doctrine_base_height").AsSingle(), scale, preference,
                        Ui.Metric("doctrine.minimum_target_pixels", 32));
                    if (control.Name.ToString().StartsWith("ScreenBook_", StringComparison.Ordinal)) height = DoctrineUnits(Ui.Metric("doctrine.book_height_pixels", 80));
                    if (MathF.Abs(control.CustomMinimumSize.Y - height) > .1f) control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X, height);
                }
            }
            foreach (var child in node.GetChildren()) Fit(child);
        }
    }
}
