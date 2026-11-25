using System;
using System.Collections.Generic;
using DealOrForce.Simulation.Controls;
using Godot;

namespace DealOrForce;

/// <summary>
/// Hold-Alt live keycap overlay displaying paper-and-ink shortcut badges over visible HUD controls
/// from the canonical HotkeyCatalog (keyed by HotkeyEntry.HudTarget).
/// </summary>
public sealed partial class AltHotkeyOverlay : Control
{
    private readonly Dictionary<string, Func<Control>> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (PanelContainer Card, Label Label)> _pool = new(StringComparer.OrdinalIgnoreCase);
    private StyleBoxFlat _badgeStyle;
    private bool _showing;

    public AltHotkeyOverlay()
    {
        Name = "AltHotkeyOverlay";
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        RebuildStyle();
    }

    public void RegisterTarget(string hudTarget, Func<Control> resolver)
    {
        if (string.IsNullOrWhiteSpace(hudTarget) || resolver is null) return;
        _targets[hudTarget] = resolver;
    }

    public void ClearTargets()
    {
        _targets.Clear();
    }

    public void RebuildStyle()
    {
        _badgeStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("controls.keycap_paper"),
            BorderColor = Ui.Colour("controls.keycap_border"),
            ShadowColor = Ui.Colour("controls.keycap_shadow", 0.55f),
            ShadowSize = 2,
            ShadowOffset = new Vector2(0, 1),
            ContentMarginLeft = 5,
            ContentMarginRight = 5,
            ContentMarginTop = 1,
            ContentMarginBottom = 1
        };
        _badgeStyle.SetBorderWidthAll(1);
        _badgeStyle.SetCornerRadiusAll(3);
    }

    private ulong _lastRefreshMsec;
    private readonly Dictionary<string, Rect2> _lastRects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastCombos = new(StringComparer.Ordinal);

    public void SetShowing(bool show)
    {
        if (!show)
        {
            if (!_showing) return;
            _showing = false;
            Visible = false;
            foreach (var item in _pool.Values)
                if (item.Card.Visible) item.Card.Visible = false;
            return;
        }

        var firstShow = !_showing;
        if (!firstShow)
        {
            var nowMsec = Time.GetTicksMsec();
            if (nowMsec - _lastRefreshMsec < 180) return;
            _lastRefreshMsec = nowMsec;
        }
        else
        {
            _lastRefreshMsec = Time.GetTicksMsec();
            _showing = true;
            Visible = true;
            MoveToFront();
        }

        foreach (var entry in HotkeyCatalog.All)
        {
            if (string.IsNullOrEmpty(entry.HudTarget) || !entry.HasVisibleButton ||
                !_targets.TryGetValue(entry.HudTarget, out var getter))
            {
                if (_pool.TryGetValue(entry.Id, out var hiddenBadge) && hiddenBadge.Card.Visible)
                    hiddenBadge.Card.Visible = false;
                continue;
            }
            var target = getter?.Invoke();
            if (target is null || !GodotObject.IsInstanceValid(target) || !target.IsVisibleInTree())
            {
                if (_pool.TryGetValue(entry.Id, out var hiddenBadge) && hiddenBadge.Card.Visible)
                    hiddenBadge.Card.Visible = false;
                continue;
            }

            var rect = target.GetGlobalRect();
            if (rect.Size.X < 8f || rect.Size.Y < 8f)
            {
                if (_pool.TryGetValue(entry.Id, out var hiddenBadge) && hiddenBadge.Card.Visible)
                    hiddenBadge.Card.Visible = false;
                continue;
            }

            if (!_pool.TryGetValue(entry.Id, out var badge))
            {
                var card = new PanelContainer
                {
                    MouseFilter = MouseFilterEnum.Ignore,
                    Visible = false
                };
                card.AddThemeStyleboxOverride("panel", _badgeStyle);
                var lbl = new Label
                {
                    MouseFilter = MouseFilterEnum.Ignore,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                lbl.AddThemeFontSizeOverride("font_size", 10);
                lbl.AddThemeColorOverride("font_color", Ui.Colour("controls.keycap_ink"));
                card.AddChild(lbl);
                AddChild(card);
                badge = (card, lbl);
                _pool[entry.Id] = badge;
            }

            if (!_lastCombos.TryGetValue(entry.Id, out var prevCombo) || !string.Equals(prevCombo, entry.DisplayCombo, StringComparison.Ordinal))
            {
                _lastCombos[entry.Id] = entry.DisplayCombo;
                badge.Label.Text = entry.DisplayCombo;
            }
            if (!badge.Card.Visible) badge.Card.Visible = true;
            if (firstShow || !_lastRects.TryGetValue(entry.Id, out var prevRect) || prevRect != rect)
            {
                _lastRects[entry.Id] = rect;
                var size = badge.Card.GetCombinedMinimumSize();
                var targetPos = new Vector2(
                    MathF.Round(rect.End.X - size.X - 2f),
                    MathF.Round(rect.Position.Y + 2f));
                if (badge.Card.Position != targetPos) badge.Card.Position = targetPos;
            }
        }
    }
}
