using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void LayoutMapSelectionCards()
    {
        var gap = Mathf.Clamp(Ui.Metric("hud.dock_gap", 8), 4, 24);
        var headerBottom = _hudTopPanel is null ? 90 : _hudTopPanel.Position.Y + _hudTopPanel.Size.Y + gap;
        var compact = Size.X < Ui.Metric("hud.two_row_below", 1600);
        if (_countryCard is { Visible: true })
        {
            Ui.Place(_countryCard, "hud.country_card");
            _countryCard.OffsetTop = Mathf.Max(_countryCard.OffsetTop, headerBottom);
        }
        if (_regionCard is not { Visible: true }) { FitOutliner(); return; }
        var rect = InterfaceSkin.Layout("hud.region_card");
        var left = rect.AnchorLeft * Size.X + rect.OffsetLeft;
        var right = Mathf.Min(Size.X - 8, rect.AnchorRight * Size.X + rect.OffsetRight);
        var top = rect.AnchorTop * Size.Y + rect.OffsetTop;
        var bottom = rect.AnchorBottom * Size.Y + rect.OffsetBottom;
        if (compact) { left = _hudNavigationPanel is null ? 106 : _hudNavigationPanel.Position.X + _hudNavigationPanel.Size.X + gap; right = Size.X - 8; }
        else if (_countryCard is { Visible: true }) left = Mathf.Max(left, _countryCard.Position.X + _countryCard.Size.X + gap);
        var desired = new ResponsiveHudLayout.Bounds(left, top, Mathf.Max(1, right - left), Mathf.Max(1, bottom - top));
        var toolbar = _mapControlsPanel is { Visible: true }
            ? new ResponsiveHudLayout.Bounds(_mapControlsPanel.Position.X, _mapControlsPanel.Position.Y,
                _mapControlsPanel.Size.X, _mapControlsPanel.Size.Y)
            : new ResponsiveHudLayout.Bounds(Size.X, Size.Y, 0, 0);
        PlaceHudBounds(_regionCard, ResponsiveHudLayout.AboveToolbar(desired, toolbar, Size.X, Size.Y, headerBottom, gap));
        if (compact && _countryCard is { Visible: true }) _countryCard.OffsetBottom = _regionCard.Position.Y - gap;
        FitOutliner();
    }

    private Button MusicShortcut()
    {
        var button = ActionButton("", OpenSoundtrackLibrary, 44, 32);
        button.Name = "OpenMediaLibrary";
        button.Icon = AtlasSurfaceSkin.MusicGlyph();
        button.SetMeta("atlas_round_button", true);
        button.SetMeta("atlas_music_button", true);
        button.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        button.ExpandIcon = true;
        button.AddThemeConstantOverride("icon_max_width", 20);
        button.TooltipText = Ui.Tip(Tr("Soundtrack player: choose a recording, pause or skip. Music continues when this window closes."));
        button.AccessibilityName = Tr("Soundtrack player");
        StyleButton(button, false);
        return button;
    }
}
