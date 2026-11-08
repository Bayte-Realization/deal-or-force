using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private bool _campaignDossierLayoutWired;
    private int _campaignDossierLayoutRevision;

    private void LayoutCampaignDossier()
    {
        if (_sidePanel is null || _hudLayer is null || _hudNavigationPanel is null) return;
        if (!_campaignDossierLayoutWired)
        {
            _campaignDossierLayoutWired = true;
            _sidePanel.VisibilityChanged += () =>
            {
                var revision = ++_campaignDossierLayoutRevision;
                if (_sidePanel.Visible) SettleCampaignDossierLayout(revision);
            };
        }

        // The rail can grow to fit its controls while canvas stretching still scales the
        // authored dossier rectangle. Reapply that rectangle before reserving the actual
        // rail/header footprint, so a resize or language change never accumulates an inset.
        Ui.Place(_sidePanel, "hud.side_panel");
        if (_sidePanel.AnchorTop == 0) _sidePanel.OffsetTop += _hudHeaderExtra;
        if (_sidePanel.AnchorBottom == 0) _sidePanel.OffsetBottom += _hudHeaderExtra;

        var local = _hudLayer.GetGlobalTransformWithCanvas().AffineInverse();
        var railEnd = local * (_hudNavigationPanel.GetGlobalTransformWithCanvas() * _hudNavigationPanel.Size);
        var gap = Mathf.Clamp(Ui.Metric("hud.dock_gap", 8), 4, 24);
        var shift = Mathf.Max(0, railEnd.X + gap - _sidePanel.Position.X);
        // Move both edges atomically. Raising OffsetLeft first temporarily shrinks
        // a panel at its content minimum; Godot grows OffsetRight to compensate,
        // and then adding shift to that edge widens the panel a second time.
        _sidePanel.Position += new Vector2(shift, 0);
        if (_battleDetailControls is { Visible: true })
        {
            var scale = BattlePixelScale();
            var wanted = Mathf.Max(_sidePanel.Size.X,
                Ui.Metric("combat.dossier_minimum_width_pixels", 640) / scale);
            _sidePanel.OffsetRight = Mathf.Min(_hudLayer.Size.X - gap, _sidePanel.OffsetLeft + wanted);
        }
        if (_hudTopPanel is not null)
        {
            var headerEnd = local * (_hudTopPanel.GetGlobalTransformWithCanvas() * _hudTopPanel.Size);
            _sidePanel.OffsetTop += Mathf.Max(0, headerEnd.Y + gap - _sidePanel.Position.Y);
        }
    }

    private async void SettleCampaignDossierLayout(int revision)
    {
        // Nation content is filled after showing the panel. Its first intrinsic
        // minimum/flow sort can grow the PanelContainer after the last HUD layout,
        // even while the rail and the window have not changed. Settle that opening
        // too; previously only a later HUD resize or explicit layout corrected it.
        for (var frame = 0; frame < 4; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!GodotObject.IsInstanceValid(this) || revision != _campaignDossierLayoutRevision ||
                !_sidePanel.Visible) return;
            LayoutCampaignDossier();
        }
    }
}
