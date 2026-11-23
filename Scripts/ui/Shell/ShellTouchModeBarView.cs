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
    private readonly ShellContext _ctx;
    private PanelContainer _panel;
    private readonly Dictionary<TouchTapMode, Button> _buttons = new();
    private StyleBox _normalStyle;
    private StyleBoxFlat _hoverStyle;
    private StyleBoxFlat _activeStyle;

    private bool _modalObscured;
    private bool? _lastVisible;
    private TouchTapMode? _lastMode;
    private bool? _lastSticky;
    private string _lastLang = "";

    public ShellTouchModeBarView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        var touch = _ctx.IsTouchMode?.Invoke() ?? (PlatformProfile.Current?.IsTouchPrimary == true);
        if (!touch) return;

        _normalStyle = _ctx.NineSlice("button_umber_9s", 8, 5, 3, 5, 3);
        _hoverStyle = Ui.ChromeBox("shell.plate_hover", 0.90f, "shell.brass_hi", 0.85f, cornerRadius: 3, borderWidth: 1, padX: 5, padY: 3);
        _activeStyle = Ui.ChromeBox("shell.well_top", 0.96f, "shell.brass_hi", 0.95f, cornerRadius: 3, borderWidth: 1, padX: 5, padY: 3, leftBorderWidth: 3);

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
                CustomMinimumSize = new Vector2(72, 42),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand
            };
            btn.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.Pressed += () =>
            {
                _ctx.PlayCue?.Invoke("ui_select");
                if (captured == TouchTapMode.Order) _ctx.PressTouchOrderMode?.Invoke();
                else _ctx.SetTouchTapMode?.Invoke(captured);
                Refresh();
            };
            grid.AddChild(btn);
            _buttons[captured] = btn;
        }
        Refresh();
    }

    public void SetModalObscured(bool obscured)
    {
        if (_modalObscured == obscured) return;
        _modalObscured = obscured;
        Refresh();
    }

    public void Layout(ShellLayoutProfile profile)
    {
        if (_panel is null) return;
        if (!profile.TouchMode || profile.TouchModeBar.W <= 0f || profile.TouchModeBar.H <= 0f)
        {
            _panel.Visible = false;
            return;
        }
        ShellContext.PlaceRect(_panel, profile.TouchModeBar);
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

        _lastVisible = vis;
        _lastMode = curMode;
        _lastSticky = sticky;
        _lastLang = lang;
        _panel.Visible = vis;
        if (!vis) return;

        foreach (var (mode, btn) in _buttons)
        {
            var on = mode == curMode;
            btn.Text = CaptionFor(mode, sticky);
            btn.AddThemeStyleboxOverride("normal", on ? _activeStyle : _normalStyle);
            btn.AddThemeStyleboxOverride("hover", on ? _activeStyle : _hoverStyle);
            btn.AddThemeStyleboxOverride("pressed", _activeStyle);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.brass_hi" : "shell.paper_muted"));
            btn.AddThemeColorOverride("font_hover_color", Ui.Colour("shell.paper"));
        }
    }

    private static string CaptionFor(TouchTapMode mode, bool stickyOrder)
    {
        static string L(string key, string fallback) { var v = Loc.Get(key); return v == key ? fallback : v; }
        return mode switch
        {
            TouchTapMode.Select => L("mobile.tap.select_short", "Select"),
            TouchTapMode.Order => stickyOrder
                ? L("mobile.tap.order_lock_short", "Order Lock")
                : L("mobile.tap.order_once_short", "Order 1×"),
            TouchTapMode.BoxSelect => L("mobile.tap.box_short", "Box"),
            TouchTapMode.Pan => L("mobile.tap.pan_short", "Pan"),
            _ => "Select"
        };
    }
}
