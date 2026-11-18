using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private PanelContainer _shellMapBar;
    private HBoxContainer _shellMapRow;
    private readonly Dictionary<StrategicMapMode, Button> _shellMapQuickButtons = new();
    private Button _shellMapMoreButton;

    // Two-tab "More" drawer (Modes | Layers)
    private PanelContainer _shellMapMorePopover;
    private Button _shellMoreModesTabBtn;
    private Button _shellMoreLayersTabBtn;
    private Control _shellMoreModesPage;
    private Control _shellMoreLayersPage;
    private readonly Dictionary<StrategicMapMode, Button> _shellMoreModeButtons = new();
    private readonly List<(string Setting, Button Button)> _shellMoreLayerButtons = new();
    private Button _shellMoreProjectionBtn;
    private bool _shellMoreShowLayers;

    // Optional corner minimap (off by default per ui_shell_minimap = false)
    private PanelContainer _shellMinimapPanel;
    private Control _shellMinimapCanvas;

    private static readonly (StrategicMapMode Mode, string Hotkey, string Icon)[] QuickMapModes =
    {
        (StrategicMapMode.Political, "Z", "nation"),
        (StrategicMapMode.Diplomatic, "X", "diplomacy"),
        (StrategicMapMode.Economy, "C", "gdp"),
        (StrategicMapMode.Resources, "V", "industry"),
        // N is ALWAYS War in the bottom-right row per owner spec
        (StrategicMapMode.Wars, "N", "wars")
    };

    private void BuildShellMapBar()
    {
        var bar = new PanelContainer
        {
            Name = "ShellMapBar",
            MouseFilter = MouseFilterEnum.Stop
        };
        bar.AddThemeStyleboxOverride("panel", ShellPlateStyle(cornerPlate: false));
        _newShellRoot.AddChild(bar);
        _shellMapBar = bar;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 5);
        margin.AddThemeConstantOverride("margin_bottom", 5);
        bar.AddChild(margin);

        var row = new HBoxContainer { Name = "ShellMapRow" };
        row.AddThemeConstantOverride("separation", 6);
        margin.AddChild(row);
        _shellMapRow = row;

        foreach (var (mode, hotkey, icon) in QuickMapModes)
        {
            var captured = mode;
            var btn = CreateShellRoundMapBtn("ShellMap_" + mode, "icon_" + icon,
                $"{Tr(mode == StrategicMapMode.Diplomatic ? "Diplomacy" : mode.ToString())} ({hotkey})",
                () => { _globe.SetMapMode(captured); RefreshShellMapBar(); });
            row.AddChild(btn);
            _shellMapQuickButtons[mode] = btn;
        }

        _shellMapMoreButton = CreateShellRoundMapBtn("ShellMap_More", "icon_shell_more",
            $"{Tr("More map modes and layers")} (M)", ToggleShellMapMorePopover);
        row.AddChild(_shellMapMoreButton);

        BuildShellMapMorePopover();
        BuildShellMinimap();
    }

    private Button CreateShellRoundMapBtn(string name, string spriteName, string tip, Action onPress)
    {
        var btn = new Button
        {
            Name = name,
            CustomMinimumSize = new Vector2(46, 46),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        btn.AddThemeStyleboxOverride("normal", ShellRoundButtonStyle(false, false));
        btn.AddThemeStyleboxOverride("hover", ShellRoundButtonStyle(false, true));
        btn.AddThemeStyleboxOverride("pressed", ShellRoundButtonStyle(true, false));
        btn.Pressed += () => { PlayStrategicCue("ui_select"); onPress(); };
        btn.TooltipText = Ui.Tip(tip);

        var icon = new TextureRect
        {
            Name = "Icon",
            Texture = ShellIconTexture(spriteName),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.paper"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        icon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        icon.OffsetLeft = 10; icon.OffsetRight = -10; icon.OffsetTop = 10; icon.OffsetBottom = -10;
        btn.AddChild(icon);
        return btn;
    }

    private void BuildShellMapMorePopover()
    {
        var pop = new PanelContainer
        {
            Name = "ShellMapMorePopover",
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop
        };
        pop.AddThemeStyleboxOverride("panel", ShellPlateStyle(cornerPlate: true));
        AttachBrassCornerOverlay(pop, topLeftOnly: false);
        _newShellRoot.AddChild(pop);
        _shellMapMorePopover = pop;

        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", 10);
        m.AddThemeConstantOverride("margin_right", 10);
        m.AddThemeConstantOverride("margin_top", 8);
        m.AddThemeConstantOverride("margin_bottom", 10);
        pop.AddChild(m);

        var vBox = new VBoxContainer();
        vBox.AddThemeConstantOverride("separation", 8);
        m.AddChild(vBox);

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 6);
        vBox.AddChild(tabs);

        _shellMoreModesTabBtn = ActionButton("Map modes", () => { _shellMoreShowLayers = false; UpdateShellMoreTabs(); }, 0, 28);
        _shellMoreModesTabBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        tabs.AddChild(_shellMoreModesTabBtn);

        _shellMoreLayersTabBtn = ActionButton("shell.map_layers", () => { _shellMoreShowLayers = true; UpdateShellMoreTabs(); }, 0, 28);
        _shellMoreLayersTabBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        tabs.AddChild(_shellMoreLayersTabBtn);

        // Page 1: All map modes + projection toggle
        var modesVBox = new VBoxContainer { Name = "ShellMoreModesPage" };
        modesVBox.AddThemeConstantOverride("separation", 6);
        vBox.AddChild(modesVBox);
        _shellMoreModesPage = modesVBox;

        var modeGrid = new GridContainer { Columns = 2 };
        modeGrid.AddThemeConstantOverride("h_separation", 6);
        modeGrid.AddThemeConstantOverride("v_separation", 6);
        modesVBox.AddChild(modeGrid);

        foreach (StrategicMapMode mode in Enum.GetValues(typeof(StrategicMapMode)))
        {
            var captured = mode;
            var label = mode == StrategicMapMode.Diplomatic ? "Diplomacy" : mode.ToString();
            var iconName = "icon_" + MapModeIcon(mode);
            var b = ActionButton(label, () =>
            {
                _globe.SetMapMode(captured);
                RefreshShellMapBar();
                CloseShellMapMorePopover();
            }, 156, 30, iconPath: SpriteCatalog.TextureOf(iconName, $"res://game/gfx/interface/icons/{iconName}.png"));
            modeGrid.AddChild(b);
            _shellMoreModeButtons[mode] = b;
        }

        _shellMoreProjectionBtn = ActionButton("3D GLOBE", () => { ToggleProjection(); RefreshShellMapBar(); }, 0, 30,
            iconPath: SpriteCatalog.TextureOf("icon_shell_3d", "res://game/gfx/interface/icons/icon_shell_3d.png"));
        modesVBox.AddChild(_shellMoreProjectionBtn);

        // Page 2: Visual layers
        var layersVBox = new VBoxContainer { Name = "ShellMoreLayersPage", Visible = false };
        layersVBox.AddThemeConstantOverride("separation", 5);
        vBox.AddChild(layersVBox);
        _shellMoreLayersPage = layersVBox;

        var layers = new[]
        {
            ("region_names", "Country names on the map"),
            ("city_names", "Cities on the map"),
            ("map_region_icons", "Region icons on the map"),
            ("map_trade_traffic", "Trade traffic lines"),
            ("map_event_pulses", "Event pulses on the map"),
            ("map_clouds", "Clouds"),
            ("map_mist", "Fog-of-war mist"),
            ("ui_shell_minimap", "Corner minimap in new shell")
        };

        foreach (var (setting, caption) in layers)
        {
            var capSetting = setting;
            var b = ActionButton(caption, () =>
            {
                SettingsCatalog.Store(capSetting, !SettingsCatalog.Flag(capSetting));
                ApplySetting(capSetting);
                RefreshShellMapBar();
            }, 318, 28);
            layersVBox.AddChild(b);
            _shellMoreLayerButtons.Add((setting, b));
        }
    }

    private void BuildShellMinimap()
    {
        var panel = new PanelContainer
        {
            Name = "ShellMinimapPanel",
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.AddThemeStyleboxOverride("panel", ShellPlateStyle(cornerPlate: false));
        _newShellRoot.AddChild(panel);
        _shellMinimapPanel = panel;

        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", 6);
        m.AddThemeConstantOverride("margin_right", 6);
        m.AddThemeConstantOverride("margin_top", 6);
        m.AddThemeConstantOverride("margin_bottom", 6);
        panel.AddChild(m);

        var canvas = new Control
        {
            CustomMinimumSize = new Vector2(220, 110),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        canvas.Draw += () =>
        {
            var size = canvas.Size;
            canvas.DrawRect(new Rect2(0, 0, size.X, size.Y), Ui.Colour("shell.well_top"));
            for (var x = 1; x < 6; x++)
                canvas.DrawLine(new Vector2(size.X * x / 6f, 0), new Vector2(size.X * x / 6f, size.Y), Ui.Colour("shell.rule"), 1f);
            for (var y = 1; y < 3; y++)
                canvas.DrawLine(new Vector2(0, size.Y * y / 3f), new Vector2(size.X, size.Y * y / 3f), Ui.Colour("shell.rule"), 1f);
            canvas.DrawLine(new Vector2(0, size.Y * 0.5f), new Vector2(size.X, size.Y * 0.5f), Ui.Colour("shell.brass_shadow"), 1.2f);
        };
        canvas.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb && _globe is not null)
            {
                var nx = Mathf.Clamp(mb.Position.X / MathF.Max(1f, canvas.Size.X), 0f, 1f);
                var ny = Mathf.Clamp(mb.Position.Y / MathF.Max(1f, canvas.Size.Y), 0f, 1f);
                var lon = nx * 360f - 180f;
                var lat = 90f - ny * 180f;
                _globe.FocusOn(lat, lon, 7.5f);
            }
        };
        Tip(canvas, "Click on the minimap to jump the camera there.");
        m.AddChild(canvas);
        _shellMinimapCanvas = canvas;
    }

    private void ToggleShellMapMorePopover()
    {
        if (_shellMapMorePopover is null) return;
        _shellMapMorePopover.Visible = !_shellMapMorePopover.Visible;
        if (_shellMapMorePopover.Visible)
        {
            UpdateShellMoreTabs();
            RefreshShellMapBar();
            LayoutShellMapMorePopover();
        }
    }

    private void CloseShellMapMorePopover()
    {
        if (_shellMapMorePopover is not null && IsInstanceValid(_shellMapMorePopover))
            _shellMapMorePopover.Visible = false;
    }

    private void UpdateShellMoreTabs()
    {
        if (_shellMoreModesPage is null || _shellMoreLayersPage is null) return;
        _shellMoreModesPage.Visible = !_shellMoreShowLayers;
        _shellMoreLayersPage.Visible = _shellMoreShowLayers;
        StyleButton(_shellMoreModesTabBtn, !_shellMoreShowLayers);
        StyleButton(_shellMoreLayersTabBtn, _shellMoreShowLayers);
    }

    private void LayoutShellMapBar(ShellLayoutProfile profile)
    {
        if (_shellMapBar is null) return;
        PlaceShellRect(_shellMapBar, profile.MapBar);

        var btnSz = profile.MapButtonSize - 4f;
        foreach (var btn in _shellMapQuickButtons.Values)
            btn.CustomMinimumSize = new Vector2(btnSz, btnSz);
        _shellMapMoreButton.CustomMinimumSize = new Vector2(btnSz, btnSz);

        var showMinimap = SettingsCatalog.Flag("ui_shell_minimap");
        _shellMinimapPanel.Visible = showMinimap;
        if (showMinimap)
        {
            var mmW = profile.Compact ? 210f : 250f;
            var mmH = profile.Compact ? 104f : 122f;
            PlaceShellRect(_shellMinimapPanel, new ShellRect(profile.MapBar.Right - mmW, profile.MapBar.Y - mmH - 8f, mmW, mmH));
        }

        if (_shellMapMorePopover is { Visible: true })
            LayoutShellMapMorePopover();
    }

    private void LayoutShellMapMorePopover()
    {
        if (_shellMapMorePopover is null || _shellMapBar is null) return;
        _shellMapMorePopover.ResetSize();
        var size = _shellMapMorePopover.GetCombinedMinimumSize();
        var w = MathF.Max(340f, size.X);
        var h = MathF.Max(220f, size.Y);
        var barRect = _shellMapBar.GetRect();
        var topOffset = SettingsCatalog.Flag("ui_shell_minimap") && _shellMinimapPanel is { Visible: true }
            ? _shellMinimapPanel.Position.Y - h - 8f
            : barRect.Position.Y - h - 8f;
        PlaceShellRect(_shellMapMorePopover, new ShellRect(
            MathF.Max(12f, barRect.End.X - w),
            MathF.Max(120f, topOffset),
            w, h));
    }

    private void RefreshShellMapBar()
    {
        if (_shellMapBar is null) return;
        foreach (var (mode, btn) in _shellMapQuickButtons)
        {
            var active = mode == _activeMapMode;
            btn.AddThemeStyleboxOverride("normal", ShellRoundButtonStyle(active, false));
            btn.AddThemeStyleboxOverride("hover", ShellRoundButtonStyle(active, true));
            if (btn.GetNodeOrNull<TextureRect>("Icon") is { } icon)
                icon.Modulate = active ? Ui.Colour("shell.ink") : Ui.Colour("shell.paper");
        }

        var moreActive = _shellMapMorePopover is { Visible: true }
            || !_shellMapQuickButtons.ContainsKey(_activeMapMode);
        _shellMapMoreButton.AddThemeStyleboxOverride("normal", ShellRoundButtonStyle(moreActive, false));
        if (_shellMapMoreButton.GetNodeOrNull<TextureRect>("Icon") is { } mIcon)
            mIcon.Modulate = moreActive ? Ui.Colour("shell.ink") : Ui.Colour("shell.paper");

        if (_shellMapMorePopover is { Visible: true })
        {
            foreach (var (mode, btn) in _shellMoreModeButtons)
                StyleButton(btn, mode == _activeMapMode);
            if (_shellMoreProjectionBtn is not null && _globe is not null)
            {
                _shellMoreProjectionBtn.Visible = _globe.BothProjections;
                _shellMoreProjectionBtn.Text = Tr(_globe.FlatProjection ? "2D MAP" : "3D GLOBE");
            }
            foreach (var (setting, btn) in _shellMoreLayerButtons)
            {
                var visible = SettingsCatalog.Of(setting) is not null;
                btn.Visible = visible;
                if (visible) StyleButton(btn, SettingsCatalog.Flag(setting));
            }
        }
    }

    private void RefreshShellMapBarLabels()
    {
        foreach (var (mode, hotkey, _) in QuickMapModes)
            if (_shellMapQuickButtons.TryGetValue(mode, out var btn))
                btn.TooltipText = Ui.Tip($"{Tr(mode == StrategicMapMode.Diplomatic ? "Diplomacy" : mode.ToString())} ({hotkey})");
        if (_shellMapMoreButton is not null)
            _shellMapMoreButton.TooltipText = Ui.Tip($"{Tr("More map modes and layers")} (M)");
    }
}
