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

    public bool IsVisible => _panel is { Visible: true };

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
            btn.AddThemeFontSizeOverride("font_size", 11);
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
            btn.TooltipText = TooltipFor(mode, sticky);
            btn.AddThemeStyleboxOverride("normal", on ? _activeStyle : _normalStyle);
            btn.AddThemeStyleboxOverride("hover", on ? _activeStyle : _hoverStyle);
            btn.AddThemeStyleboxOverride("pressed", _activeStyle);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.brass_hi" : "shell.paper_muted"));
            btn.AddThemeColorOverride("font_hover_color", Ui.Colour("shell.paper"));
        }
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
            TouchTapMode.Select => L("mobile.tap.select_short", "Select"),
            TouchTapMode.Order => stickyOrder
                ? L("mobile.tap.order_lock_short", "Order Lock")
                : L("mobile.tap.order_once_short", "Order 1×"),
            TouchTapMode.BoxSelect => L("mobile.tap.box_short", "Box"),
            TouchTapMode.Pan => L("mobile.tap.pan_short", "Pan"),
            _ => "Select"
        };
    }

    private static string TooltipFor(TouchTapMode mode, bool stickyOrder)
    {
        return mode switch
        {
            TouchTapMode.Select => L("mobile.tap.select_tip",
                "Select — tap to pick a country, region, or army.\nDrag to pan · Hold a province to issue a quick order."),
            TouchTapMode.Order => stickyOrder
                ? L("mobile.tap.order_lock_tip",
                    "Order Lock — every tap issues a march or context order\nwithout returning to Select.\nTap again or pick Select to exit.")
                : L("mobile.tap.order_once_tip",
                    "Order 1× — next tap issues 1 march or context order\n(like Right-Click) and returns to Select.\nTap again while active to lock multi-order mode."),
            TouchTapMode.BoxSelect => L("mobile.tap.box_tip",
                "Box — drag one finger to draw a selection box over armies.\nDrag with two fingers to pan or zoom the map."),
            TouchTapMode.Pan => L("mobile.tap.pan_tip",
                "Pan — move and zoom the map safely.\nTaps never select units or issue orders."),
            _ => ""
        };
    }
}
