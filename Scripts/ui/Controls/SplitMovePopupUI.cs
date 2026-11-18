using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Controls;
using Godot;

namespace DealOrForce;

/// <summary>
/// "First where, then how many" cursor popup on Alt+RMB (AoH #8):
/// displays 25/50/75/100% quarters on A S D F (with blue focus ring), division slider,
/// direct entry, and immediately splits/sends the requested fraction to the clicked province.
/// </summary>
public sealed partial class SplitMovePopupControl : Control
{
    private readonly QuickAmountWidget _widget;
    private int _targetProvinceId = -1;
    private string _targetName = "";
    private int _totalDivisions;
    private Action<int, int> _onConfirm;
    private Action _onClose;

    public bool IsPopupOpen => Visible && _widget.Visible;

    public SplitMovePopupControl()
    {
        Name = "SplitMovePopupLayer";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true })
            {
                ClosePopup();
                AcceptEvent();
            }
            else if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                ClosePopup();
                AcceptEvent();
            }
        };

        _widget = new QuickAmountWidget(showCloseButton: true);
        _widget.Name = "SplitMoveQuickAmount";
        AddChild(_widget);
    }

    public void OpenAt(Vector2 screenPos, int targetProvinceId, string targetName, int totalDivisions,
        Action<int, int> onConfirm, Action onClose = null)
    {
        _targetProvinceId = targetProvinceId;
        _targetName = string.IsNullOrEmpty(targetName) ? $"#{targetProvinceId}" : targetName;
        _totalDivisions = Math.Max(1, totalDivisions);
        _onConfirm = onConfirm;
        _onClose = onClose;

        var defaultDivs = QuickAmountRules.QuarterInteger(_totalDivisions, 2); // Default 50%
        _widget.Configure(
            title: Loc.Format("Split & march to {0}", _targetName),
            min: 1,
            max: _totalDivisions,
            step: 1,
            initialValue: defaultDivs,
            integerOnly: true,
            valueFormatter: val =>
            {
                var divs = Math.Clamp((int)MathF.Round(val), 1, _totalDivisions);
                var pct = (int)MathF.Round(divs * 100f / Math.Max(1, _totalDivisions));
                return Loc.Format("{0} div. ({1}%)", divs, pct);
            },
            previewProvider: null,
            onApply: val =>
            {
                var count = Math.Clamp((int)MathF.Round(val), 1, _totalDivisions);
                var provinceId = _targetProvinceId;
                var cb = _onConfirm;
                ClosePopup();
                cb?.Invoke(provinceId, count);
            },
            onEverywhere: null,
            everywhereCaption: null,
            everywhereTooltip: null,
            onClose: ClosePopup,
            applyCaption: Loc.Format("Split & march to {0}", _targetName));

        _widget.SetPresets(null);
        Visible = true;
        MoveToFront();

        // Position near cursor, clamped safely inside viewport
        var vp = GetViewportRect().Size;
        var cardSize = _widget.GetCombinedMinimumSize();
        var w = MathF.Max(cardSize.X, 344f);
        var h = MathF.Max(cardSize.Y, 178f);
        var x = Math.Clamp(screenPos.X + 14f, 12f, MathF.Max(12f, vp.X - w - 12f));
        var y = Math.Clamp(screenPos.Y - 24f, 56f, MathF.Max(56f, vp.Y - h - 16f));
        _widget.Position = new Vector2(x, y);
        _widget.TakeKeyboardFocus();
    }

    public bool HandleKey(InputEventKey key)
    {
        if (!IsPopupOpen) return false;
        return _widget.HandleKeyboardShortcut(key);
    }

    public void ClosePopup()
    {
        if (!Visible) return;
        _widget.SetFocusHighlight(false);
        Visible = false;
        _onClose?.Invoke();
    }
}
