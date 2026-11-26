using System;
using System.Collections.Generic;
using DealOrForce.Platform;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Sovereign Brass 2×2 Mobile Touch-Mode control bar (Select / Order 1× [Order Lock] / Box / Pan)
/// docked at top-right immediately left of <see cref="ShellDatePlateView"/> so the bottom-right
/// corner minimap remains 100% unobstructed. Built and shown ONLY on touch-primary platforms
/// during active campaigns (completely absent on Desktop, Main Menu, and Country Picker).
/// </summary>
internal sealed class ShellTouchModeBarView
{
    private const double ToastDurationSec = 3.5;
    private const double ToastFadeSec = 0.6;

    private readonly ShellContext _ctx;
    private PanelContainer _panel;
    private PanelContainer _toastPanel;
    private Label _toastLabel;
    private ShellLayoutProfile _lastProfile;
    private readonly Dictionary<TouchTapMode, Button> _buttons = new();
    private StyleBox _normalStyle;
    private StyleBoxFlat _hoverStyle;
    private StyleBoxFlat _activeStyle;

    private bool _modalObscured;
    private bool? _lastVisible;
    private TouchTapMode? _lastMode;
    private bool? _lastSticky;
    private string _lastLang = "";
    private double _toastRemaining;
    private TouchTapMode _toastMode;
    private bool _toastSticky;

    public bool IsVisible => _panel is { Visible: true };
    public bool HasActiveToast => _toastRemaining > 0.0 || _toastPanel is { Visible: true };

    public ShellTouchModeBarView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        var touch = _ctx.IsTouchMode?.Invoke() ?? (PlatformProfile.Current?.IsTouchPrimary == true);
        if (!touch) return;

        _normalStyle = _ctx.NineSlice("button_umber_9s", 8, 2, 3, 2, 3);
        _hoverStyle = Ui.ChromeBox("shell.plate_hover", 0.90f, "shell.brass_hi", 0.85f, cornerRadius: 3, borderWidth: 1, padX: 2, padY: 3);
        _activeStyle = Ui.ChromeBox("shell.well_top", 0.96f, "shell.brass_hi", 0.95f, cornerRadius: 3, borderWidth: 1, padX: 2, padY: 3, leftBorderWidth: 3);

        _panel = new PanelContainer
        {
            Name = "ShellTouchModeBar",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 12
        };
        _panel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 12, 6, 5, 6, 5, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_panel);
        ChromePlateArt.AttachSurfaceTexture(_panel, "blueprint", 0.22f);
        _ctx.AttachPlateFrame(_panel);

        var grid = new GridContainer
        {
            Name = "ShellTouchModeGrid",
            Columns = 2,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        _panel.AddChild(grid);

        var specs = new[]
        {
            (TouchTapMode.Select, "ShellTouchMode_Select"),
            (TouchTapMode.Order, "ShellTouchMode_Order"),
            (TouchTapMode.BoxSelect, "ShellTouchMode_BoxSelect"),
            (TouchTapMode.Pan, "ShellTouchMode_Pan")
        };

        foreach (var (mode, name) in specs)
        {
            var captured = mode;
            var btn = new Button
            {
                Name = name,
                Text = CaptionFor(captured, stickyOrder: false),
                TooltipText = TooltipFor(captured, stickyOrder: false),
                CustomMinimumSize = new Vector2(66, 42),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand
            };
            btn.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            btn.AddThemeFontSizeOverride("font_size", 10);
            btn.Pressed += () =>
            {
                _ctx.PlayCue?.Invoke("ui_select");
                if (captured == TouchTapMode.Order) _ctx.PressTouchOrderMode?.Invoke();
                else _ctx.SetTouchTapMode?.Invoke(captured);
                Refresh();
                var curMode = _ctx.GetTouchTapMode?.Invoke() ?? captured;
                var sticky = _ctx.IsTouchStickyOrder?.Invoke() ?? false;
                ShowModeToast(curMode, sticky);
            };
            grid.AddChild(btn);
            _buttons[captured] = btn;
        }

        _toastPanel = new PanelContainer
        {
            Name = "ShellTouchModeToast",
            Visible = false,
            ClipContents = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 13
        };
        _toastPanel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 10, 10, 6, 10, 6, "shell.plate_bottom", "shell.brass_dim", 2));
        _toastLabel = new Label
        {
            Name = "ShellTouchModeToastLabel",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(260, 0)
        };
        _toastLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _toastLabel.AddThemeFontSizeOverride("font_size", 11);
        _toastLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        _toastPanel.AddChild(_toastLabel);
        _ctx.Root.AddChild(_toastPanel);

        Refresh();
    }

    public void SetModalObscured(bool obscured)
    {
        if (_modalObscured == obscured) return;
        _modalObscured = obscured;
        if (obscured) HideToast();
        Refresh();
    }

    public void Layout(ShellLayoutProfile profile)
    {
        _lastProfile = profile;
        if (_panel is null) return;
        if (!profile.TouchMode || profile.TouchModeBar.W <= 0f || profile.TouchModeBar.H <= 0f)
        {
            _panel.Visible = false;
            HideToast();
            return;
        }
        ShellContext.PlaceRect(_panel, profile.TouchModeBar);
        LayoutToast(profile);
    }

    public void Refresh()
    {
        if (_panel is null) return;
        var touch = _ctx.IsTouchMode?.Invoke() ?? (PlatformProfile.Current?.IsTouchPrimary == true);
        var obscured = _modalObscured || (_ctx.IsAnyModalOpen?.Invoke() ?? false);
        var vis = touch && !obscured;
        var curMode = _ctx.GetTouchTapMode?.Invoke() ?? TouchTapMode.Select;
        var sticky = _ctx.IsTouchStickyOrder?.Invoke() ?? false;
        var lang = Loc.Language;

        if (_lastVisible == vis && _lastMode == curMode && _lastSticky == sticky && _lastLang == lang)
            return;

        var langChanged = _lastLang != lang;
        _lastVisible = vis;
        _lastMode = curMode;
        _lastSticky = sticky;
        _lastLang = lang;
        _panel.Visible = vis;
        if (!vis)
        {
            HideToast();
            return;
        }

        if (langChanged && _toastRemaining > 0.0 && _toastLabel is not null)
        {
            _toastLabel.Text = ToastFor(_toastMode, _toastSticky);
            LayoutToast(_lastProfile);
        }

        foreach (var (mode, btn) in _buttons)
        {
            var on = mode == curMode;
            btn.Text = CaptionFor(mode, sticky);
            btn.TooltipText = TooltipFor(mode, sticky);
            btn.AddThemeStyleboxOverride("normal", on ? _activeStyle : _normalStyle);
            btn.AddThemeStyleboxOverride("hover", on ? _activeStyle : _hoverStyle);
            btn.AddThemeStyleboxOverride("pressed", _activeStyle);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.brass_hi" : "shell.paper_muted"));
            btn.AddThemeColorOverride("font_hover_color", Ui.Colour("shell.paper"));
        }
    }

    public void Tick(double delta)
    {
        if (_toastPanel is null || _toastRemaining <= 0.0)
        {
            if (_toastPanel is { Visible: true }) _toastPanel.Visible = false;
            return;
        }
        var touch = _ctx.IsTouchMode?.Invoke() ?? (PlatformProfile.Current?.IsTouchPrimary == true);
        var obscured = _modalObscured || (_ctx.IsAnyModalOpen?.Invoke() ?? false);
        if (!touch || obscured || !IsVisible)
        {
            HideToast();
            return;
        }
        _toastRemaining -= Math.Max(0.0, delta);
        if (_toastRemaining <= 0.0)
        {
            HideToast();
            return;
        }
        var alpha = _toastRemaining < ToastFadeSec ? (float)(_toastRemaining / ToastFadeSec) : 1f;
        _toastPanel.Modulate = Colors.White with { A = alpha };
        if (!_toastPanel.Visible) _toastPanel.Visible = true;
    }

    private void ShowModeToast(TouchTapMode mode, bool stickyOrder)
    {
        if (_toastPanel is null || _toastLabel is null || !IsVisible) return;
        _toastMode = mode;
        _toastSticky = stickyOrder;
        _toastRemaining = ToastDurationSec;
        _toastLabel.Text = ToastFor(mode, stickyOrder);
        _toastPanel.Modulate = Colors.White;
        _toastPanel.Visible = true;
        LayoutToast(_lastProfile);
    }

    private void HideToast()
    {
        _toastRemaining = 0.0;
        if (_toastPanel is not null) _toastPanel.Visible = false;
    }

    private void LayoutToast(ShellLayoutProfile profile)
    {
        if (_toastPanel is null || _toastLabel is null || profile is null ||
            !profile.TouchMode || profile.TouchModeBar.W <= 0f || profile.TouchModeBar.H <= 0f)
            return;
        var toastW = MathF.Min(308f, MathF.Max(220f, profile.TouchModeBar.Right - 16f));
        if (profile.AlertColumn.W > 0f && profile.AlertColumn.X < profile.TouchModeBar.Right)
            toastW = MathF.Min(toastW, MathF.Max(180f, profile.AlertColumn.X - 16f));
        var toastX = MathF.Max(12f, profile.TouchModeBar.Right - toastW);
        var toastY = profile.TouchModeBar.Bottom + 6f;
        _toastLabel.CustomMinimumSize = new Vector2(MathF.Max(160f, toastW - 20f), 0f);
        _toastPanel.Position = new Vector2(toastX, toastY);
        _toastPanel.Size = new Vector2(toastW, 0f);
        _toastPanel.ResetSize();
    }

    private static string L(string key, string fallback)
    {
        var v = Loc.Get(key);
        return v == key ? fallback : v;
    }

    private static string CaptionFor(TouchTapMode mode, bool stickyOrder)
    {
        return mode switch
        {
            TouchTapMode.Select => L("mobile.mode.select.short", "Select"),
            TouchTapMode.Order => stickyOrder
                ? L("mobile.mode.order.sticky", "Order Lock")
                : L("mobile.mode.order.oneshot", "Order 1×"),
            TouchTapMode.BoxSelect => L("mobile.mode.box.short", "Box"),
            TouchTapMode.Pan => L("mobile.mode.pan.short", "Map"),
            _ => L("mobile.mode.select.short", "Select")
        };
    }

    private static string TooltipFor(TouchTapMode mode, bool stickyOrder)
    {
        return mode switch
        {
            TouchTapMode.Select => L("mobile.mode.select.tip", "Tap to select a province, nation, or army. Drag to pan. Hold to issue an order."),
            TouchTapMode.Order => stickyOrder
                ? L("mobile.mode.order.sticky.tip", L("mobile.mode.order.tip", "Order Lock: every tap keeps issuing orders continuously until you tap Order again or switch mode."))
                : L("mobile.mode.order.oneshot.tip", L("mobile.mode.order.tip", "Order 1×: next tap issues an order (like right-click) and returns to Select. Tap Order again to lock.")),
            TouchTapMode.BoxSelect => L("mobile.mode.box.tip", "Drag with one finger to draw a selection rectangle for divisions. Two fingers move the map."),
            TouchTapMode.Pan => L("mobile.mode.pan.tip", "Safe map navigation lock: taps and holds never select or issue orders while browsing the map."),
            _ => ""
        };
    }

    private static string ToastFor(TouchTapMode mode, bool stickyOrder)
    {
        return mode switch
        {
            TouchTapMode.Select => L("mobile.mode.select.toast", "Select mode: tap picks province, country, or army. Hold issues an order."),
            TouchTapMode.Order => stickyOrder
                ? L("mobile.mode.order.sticky.toast", "Order Lock: every tap issues orders until switched off. Tap again to return to 1×.")
                : L("mobile.mode.order.oneshot.toast", "Order 1×: next tap issues an order (like right-click) and returns to Select. Tap again to lock."),
            TouchTapMode.BoxSelect => L("mobile.mode.box.toast", "Box mode: drag one finger to box-select divisions. Two fingers move the map."),
            TouchTapMode.Pan => L("mobile.mode.pan.toast", "Map mode: safe camera navigation — taps never select or issue orders."),
            _ => ""
        };
    }
}
