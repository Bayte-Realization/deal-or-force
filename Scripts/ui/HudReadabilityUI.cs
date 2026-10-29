using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private PanelContainer _hudTopPanel, _hudNavigationPanel;
    private VBoxContainer _hudTopStack, _hudIdentityBox;
    private HBoxContainer _hudTopRow, _hudStatRow;
    private VBoxContainer _hudClockBox;
    private Button _mediaButton = null!;
    private float _hudHeaderExtra;

    private void LayoutHudReadability()
    {
        if (_hudTopPanel is null || _hudStatRow is null) return;
        // Canvas stretching can keep Size.X at 1920 in a 1280px window. React to
        // the actual window as well; otherwise the second row never appears.
        var windowWidth = GetWindow()?.Size.X ?? Size.X;
        var required = _hudStatRow.GetCombinedMinimumSize().X;
        var slots = 1;
        foreach (var child in _hudTopRow.GetChildren())
            if (child is Control control && control != _hudStatRow && control.Visible)
            { required += control.GetCombinedMinimumSize().X; slots++; }
        required += _hudTopRow.GetThemeConstant("separation") * (slots - 1) + 24;
        var available = _hudTopPanel.Size.X > 1 ? _hudTopPanel.Size.X : Size.X - 16;
        var compact = System.Math.Min(Size.X, windowWidth) < Ui.Metric("hud.two_row_below", 1600) || required > available;
        _hudHeaderExtra = compact ? Ui.Metric("hud.second_row_height", 52) : 0;
        Ui.Place(_hudTopPanel, "hud.top"); _hudTopPanel.OffsetBottom += _hudHeaderExtra;
        if (compact && _hudStatRow.GetParent() != _hudTopStack)
            _hudStatRow.Reparent(_hudTopStack);
        else if (!compact && _hudStatRow.GetParent() != _hudTopRow)
        {
            _hudStatRow.Reparent(_hudTopRow);
            var idx = _hudIdentityBox is not null ? _hudIdentityBox.GetIndex() + 1 : 2;
            _hudTopRow.MoveChild(_hudStatRow, idx);
        }
        _hudClockBox.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.Fill;
        _hudIdentityBox.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.Fill;
        // Preserve the authored/modded rectangles; only the extra row shifts map-side docks.
        foreach (var panel in new Control[] { _hudNavigationPanel, _sidePanel, _movementPanel, _armyPanel, _supportLayer is PanelContainer ? _supportLayer : null,
                     _fleetPanel, _resourceAtlasPanel, _deskPanel })
            ShiftHudDock(panel);
        if (_situationLayer is not null) LayoutSituationRoom();
    }

    private void ShiftHudDock(Control panel)
    {
        if (panel is null || !panel.HasMeta("skin_layout")) return;
        var key = panel.GetMeta("skin_layout").AsString();
        if (!key.StartsWith("hud.")) return;
        Ui.Place(panel, key);
        if (panel.AnchorTop == 0) panel.OffsetTop += _hudHeaderExtra;
        if (panel.AnchorBottom == 0) panel.OffsetBottom += _hudHeaderExtra;
    }
}
