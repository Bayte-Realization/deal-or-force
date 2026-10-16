using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly Dictionary<StrategicMapMode, Button> _mapModeButtons = new();
    private Button _projectionButton = null!;
    private Button _showcasePanButton = null!;
    private StrategicMapMode _activeMapMode = StrategicMapMode.Political;
    private PanelContainer _mapControlsPanel;
    private HFlowContainer _mapControlsFlow;
    private Button _mapLayerSelector;
    private Control _mapModeDrawerLayer;
    private PanelContainer _mapModeDrawerPanel;
    private GridContainer _mapModeDrawerGrid;
    private readonly List<(string Setting, Button Button)> _mapVisualToggleButtons = new();
    private bool _mapControlsLayoutQueued;

    private void BuildMapControls()
    {
        var panel = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        _mapControlsPanel = panel;
        panel.Name = "MapControls";
        Ui.Place(panel, "hud.map_controls");
        var margin = Margin(panel, 8, 4);
        var row = new HFlowContainer
        {
            Name = "MapControlsFlow",
            Alignment = FlowContainer.AlignmentMode.Begin,
            ClipContents = true
        };
        _mapControlsFlow = row;
        row.AddThemeConstantOverride("h_separation", 4);
        row.AddThemeConstantOverride("v_separation", 4); margin.AddChild(row);
        row.MinimumSizeChanged += QueueMapControlsLayout;
        panel.Resized += QueueMapControlsLayout;
        panel.VisibilityChanged += () => { if (!panel.IsVisibleInTree()) CloseMapLayerDrawer(); else RefreshMapVisualToggles(); };

        _projectionButton = ActionButton("3D GLOBE", ToggleProjection, 104, 34, true);
        _projectionButton.Name = "MapProjection"; _projectionButton.ClipText = true;
        _projectionButton.TooltipText = Ui.Tip(Tr("Switch between the globe and an infinitely wrapping classic map (F2)."));
        row.AddChild(_projectionButton);

        // A bounded, named selector replaces the ever-growing icon strip. Every layer is
        // still in the drawer; adding layers never pushes weather or camera controls off-screen.
        _mapLayerSelector = ActionButton("Political", ToggleMapLayerDrawer, 166, 34);
        _mapLayerSelector.Name = "MapLayerSelector"; _mapLayerSelector.ClipText = true;
        _mapLayerSelector.TooltipText = Ui.Tip(Tr("Choose a map layer. The active layer is named here; every layer is available in the drawer."));
        row.AddChild(_mapLayerSelector);
        row.AddChild(MapVisualToggle("map_clouds", "CLOUDS", "Clouds over the world, seen from far away. Press to hide or show them.", 74));
        row.AddChild(MapVisualToggle("map_mist", "FOG", "The drifting mist over ground your country cannot see, close up. Press to hide or show it; what is hidden stays hidden.", 54));

        _showcasePanButton = ActionButton("PAN", ToggleShowcasePan, 54, 34);
        _showcasePanButton.Name = "ShowcasePan";
        _showcasePanButton.ClipText = true;
        _showcasePanButton.TooltipText = Ui.Tip(Tr("Automatic horizontal camera pan for showcases and time-lapses (F6). Press again to stop."));
        row.AddChild(_showcasePanButton);
        BuildMapLayerDrawer();

        _globe.ProjectionChanged += RefreshProjectionControls;
        _globe.MapModeChanged += UpdateMapModeButtons;
        _globe.MapModeChanged += mode =>
        {
            if (mode == StrategicMapMode.Resources) ShowResourceAtlas();
            else if (_resourceAtlasPanel is not null) _resourceAtlasPanel.Visible = false;
        };
        _globe.MapModeSubjectChanged += _ => UpdateMapModeTooltips();
        UpdateMapModeButtons(StrategicMapMode.Political);
        RefreshProjectionControls(_globe.FlatProjection);
    }

    private void RefreshProjectionControls(bool flat)
    {
        var choice = _globe is not null && _globe.BothProjections;
        if (_projectionButton is not null)
        {
            var source = flat ? "2D MAP" : "3D GLOBE";
            _projectionButton.Visible = choice; _projectionButton.Text = Tr(source);
            _projectionButton.SetMeta(LocSourceMeta, source); _projectionButton.SetMeta(LocAppliedMeta, _projectionButton.Text);
        }
        if (_mainProjectionButton is not null)
        {
            var source = flat ? "Map view: 2D classic" : "Map view: 3D globe";
            _mainProjectionButton.Visible = choice; _mainProjectionButton.Text = Tr(source);
            _mainProjectionButton.SetMeta(LocSourceMeta, source); _mainProjectionButton.SetMeta(LocAppliedMeta, _mainProjectionButton.Text);
        }
        if (_navigationHint is not null)
        {
            var source = flat
                ? "Right-drag to pan · wheel to zoom · WASD to move · the world wraps horizontally."
                : "Right-drag to turn the globe · wheel to zoom · WASD to move.";
            _navigationHint.Text = Tr(source);
            _navigationHint.SetMeta(LocSourceMeta, source); _navigationHint.SetMeta(LocAppliedMeta, _navigationHint.Text);
        }
    }

    private void BuildMapLayerDrawer()
    {
        _mapModeDrawerLayer = FullLayer("MapLayerDrawer"); _mapModeDrawerLayer.Visible = false;
        _mapModeDrawerLayer.MouseFilter = MouseFilterEnum.Stop; _hudLayer.AddChild(_mapModeDrawerLayer);
        _mapModeDrawerLayer.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            { CloseMapLayerDrawer(); _mapModeDrawerLayer.AcceptEvent(); }
        };
        _mapModeDrawerPanel = TexturedPanel(_mapModeDrawerLayer, PanelSurface.Command);
        _mapModeDrawerPanel.Name = "MapLayerDrawerPanel";
        var rows = PanelRows(_mapModeDrawerPanel, 12, 10, 8);
        var header = new HBoxContainer(); rows.AddChild(header);
        var title = Fixed(Title("MAP LAYERS", 15, Ui.Colour("text.accent")));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var close = ActionButton("×", CloseMapLayerDrawer, 32, 30); close.Name = "CloseMapLayerDrawer";
        header.AddChild(close);
        var scroll = new ScrollContainer
        {
            Name = "MapLayerDrawerScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto, FollowFocus = true,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        rows.AddChild(scroll);
        _mapModeDrawerGrid = new GridContainer { Name = "MapLayerGrid", Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mapModeDrawerGrid.AddThemeConstantOverride("h_separation", 6); _mapModeDrawerGrid.AddThemeConstantOverride("v_separation", 6);
        scroll.AddChild(_mapModeDrawerGrid);
        foreach (StrategicMapMode mode in System.Enum.GetValues<StrategicMapMode>()) AddMode(_mapModeDrawerGrid, mode);
        var hint = Title("Hover a layer for its meaning. Esc returns to the map.", 11, Ui.Colour("text.muted")); rows.AddChild(hint);
    }

    private void ToggleMapLayerDrawer()
    {
        if (_mapModeDrawerLayer.Visible) { CloseMapLayerDrawer(); return; }
        LayoutMapControls(); UpdateMapModeButtons(_activeMapMode);
        _mapModeDrawerLayer.Show(); _mapModeDrawerLayer.MoveToFront(); _globe.MapLayerDrawerInputBlocked = true;
        _mapModeButtons.GetValueOrDefault(_activeMapMode)?.GrabFocus();
    }

    private void CloseMapLayerDrawer()
    {
        if (_mapModeDrawerLayer is null) return;
        _mapModeDrawerLayer.Hide(); if (_globe is not null) _globe.MapLayerDrawerInputBlocked = false;
    }

    private bool HandleMapControlsInput(InputEvent input)
    {
        if (_mapModeDrawerLayer is not { Visible: true }) return false;
        if (!_mapModeDrawerLayer.IsVisibleInTree() || _eventLayer is { Visible: true } ||
            _pauseMenuLayer is { Visible: true } || _settingsLayer is { Visible: true } ||
            _handbookLayer is { Visible: true } || _handoverLayer is { Visible: true } || IsInstanceValid(_dealComposer))
        { CloseMapLayerDrawer(); return false; }
        if (input is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.Escape) { CloseMapLayerDrawer(); _mapLayerSelector.GrabFocus(); }
            if (key.Keycode is Key.Escape or Key.Space or Key.F2 or Key.F3 or Key.F7 or Key.F10) GetViewport().SetInputAsHandled();
        }
        // Tab, arrows and Enter are delivered to the drawer's actual controls. Campaign
        // hotkeys and raw WASD polling cannot run beneath this temporary surface.
        return true;
    }

    private void QueueMapControlsLayout()
    {
        if (_mapControlsLayoutQueued) return;
        _mapControlsLayoutQueued = true;
        Callable.From(() => { _mapControlsLayoutQueued = false; LayoutMapControls(); }).CallDeferred();
    }

    private void LayoutMapControls()
    {
        if (_mapControlsPanel is null || _mapControlsFlow is null) return;
        var rect = InterfaceSkin.Layout("hud.map_controls");
        var left = rect.AnchorLeft * Size.X + rect.OffsetLeft;
        var right = rect.AnchorRight * Size.X + rect.OffsetRight;
        var bottom = rect.AnchorBottom * Size.Y + rect.OffsetBottom;
        var height = Mathf.Max(44, _mapControlsFlow.GetCombinedMinimumSize().Y + 10);
        var bounds = ResponsiveHudLayout.Fit(new(left, bottom - height, Mathf.Max(180, right - left), height), Size.X, Size.Y);
        PlaceHudBounds(_mapControlsPanel, bounds);
        LayoutMapSelectionCards();
        if (_mapModeDrawerPanel is not null)
        {
            var drawerWidth = Mathf.Clamp(Ui.Metric("map_controls.drawer_width", 480), 200, 720);
            var drawerHeight = Mathf.Clamp(Ui.Metric("map_controls.drawer_height", 304), 160, Mathf.Max(160, Size.Y * .5f));
            var drawer = ResponsiveHudLayout.Fit(new(bounds.X + bounds.Width - drawerWidth,
                bounds.Y - drawerHeight - 6, drawerWidth, drawerHeight), Size.X, Size.Y);
            PlaceHudBounds(_mapModeDrawerPanel, drawer);
            _mapModeDrawerGrid.Columns = ResponsiveHudLayout.DrawerColumns(drawer.Width - 28,
                Ui.Metric("map_controls.drawer_cell_width", 190));
        }
        RefreshMapVisualToggles();
    }

    private static void PlaceHudBounds(Control panel, ResponsiveHudLayout.Bounds bounds)
    {
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        panel.OffsetLeft = bounds.X; panel.OffsetTop = bounds.Y;
        panel.OffsetRight = bounds.X + bounds.Width; panel.OffsetBottom = bounds.Y + bounds.Height;
    }

    // No dependency on a particular weather implementation: data declares availability,
    // the normal settings application owns its effect, and missing mod settings stay absent.
    private Button MapVisualToggle(string setting, string caption, string help, float width)
    {
        var button = ActionButton(caption, () =>
        {
            SettingsCatalog.Store(setting, !SettingsCatalog.Flag(setting)); ApplySetting(setting); RefreshMapVisualToggles();
        }, width, 34);
        button.Name = "MapVisualToggle_" + setting; button.ClipText = true; button.ToggleMode = true;
        Tip(button, help); button.AccessibilityName = Tr(caption); _mapVisualToggleButtons.Add((setting, button));
        return button;
    }

    private void RefreshMapVisualToggles()
    {
        _mapVisualToggleButtons.RemoveAll(entry => !IsInstanceValid(entry.Button));
        foreach (var (setting, button) in _mapVisualToggleButtons)
        {
            button.Visible = SettingsCatalog.Of(setting) is not null;
            var enabled = SettingsCatalog.Flag(setting);
            if (button.ButtonPressed == enabled && button.HasMeta("map_visual_styled")) continue;
            button.SetPressedNoSignal(enabled); StyleButton(button, enabled); button.SetMeta("map_visual_styled", true);
        }
    }

    /// <summary>
    /// The two layers that are about somebody say whose. Diplomacy and war are drawn for the
    /// country picked on the map, and a tooltip that did not say so would leave a player
    /// reading Brazil's alliances as their own.
    /// </summary>
    private void UpdateMapModeTooltips()
    {
        var subject = _globe.MapModeSubject;
        foreach (var pair in _mapModeButtons)
        {
            var tip = Tr(pair.Key.ToString()) + " \u2014 " + Tr(MapModeHelp(pair.Key));
            if (pair.Key is StrategicMapMode.Diplomatic or StrategicMapMode.Wars or StrategicMapMode.Tension && subject is not null)
                tip += "\n" + Simulation.Loc.Format(
                    "Seen from {0}. Pick another country on the map to see it from theirs; close the panel to come back to your own.",
                    subject.DisplayName);
            pair.Value.TooltipText = Ui.Tip(tip);
        }
    }

    /// <summary>
    /// The mark for one map layer.
    ///
    /// Drawn from the same set the top bar and the alert rail use, so a flag means a country
    /// wherever it appears and crossed swords mean a war. Terrain is a mountain with a tree
    /// at its foot and relief is a contour map: the ground and its height, not a country.
    /// </summary>
    private static string MapModeIcon(StrategicMapMode mode) => Simulation.CampaignUiCatalog.Icon("map." + mode, mode switch
    {
        StrategicMapMode.Political => "nation",
        StrategicMapMode.Diplomatic => "diplomacy",
        StrategicMapMode.Wars => "wars",
        StrategicMapMode.Population => "population",
        StrategicMapMode.Economy => "gdp",
        StrategicMapMode.Terrain => "terrain",
        StrategicMapMode.Resources => "industry",
        StrategicMapMode.Stability => "stability",
        StrategicMapMode.Unrest => "unrest",
        StrategicMapMode.Tension => "war",
        _ => "relief"
    });

    /// <summary>What a layer actually shows, for the tooltip under its mark.</summary>
    private static string MapModeHelp(StrategicMapMode mode) => mode switch
    {
        StrategicMapMode.Political => "Countries, who controls what, and every occupied province.",
        StrategicMapMode.Diplomatic => "Who is allied to whom, who guarantees whom, and who answers to whom.",
        StrategicMapMode.Wars => "Belligerents, the fronts between them and the ground that has changed hands.",
        StrategicMapMode.Population => "Total population by country, not population density. Brighter means more people; unknown countries stay neutral.",
        StrategicMapMode.Economy => "Total GDP by country, not treasury or regional output. Brighter means a larger economy; unknown countries stay neutral.",
        StrategicMapMode.Terrain => "The ground itself, with no politics on it. Hover a province for what it does to a march and a battle.",
        StrategicMapMode.Resources => "Regional resource potential. Choose a raw material and locate its deposits.",
        StrategicMapMode.Stability => "Every region's stability: green holds, amber wavers, red is coming apart. A capital in the red is how coups and civil wars start.",
        StrategicMapMode.Unrest => "Radicalism and the pull to leave, region by region: grey is quiet, amber restless, red close to rising or declaring independence.",
        StrategicMapMode.Tension => "How close each country is to shooting with this one: amber is friction and crisis, red is armed clashes, dark red is war. Talks in the Conflicts screen cool it.",
        _ => "Height and shade, from the moddable relief layer."
    };

    private void AddMode(Container row, StrategicMapMode mode)
    {
        var icon = MapModeIcon(mode);
        var button = ActionButton(mode == StrategicMapMode.Diplomatic ? "Diplomacy" : mode.ToString(),
            () => { _globe.SetMapMode(mode); CloseMapLayerDrawer(); }, 0, 34,
            mode == StrategicMapMode.Political,
            Simulation.SpriteCatalog.TextureOf("icon_" + icon,
                $"res://game/gfx/interface/icons/icon_{icon}.png"));
        button.Name = "MapMode" + mode;
        button.ClipText = true;
        button.ToggleMode = true;
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        button.CustomMinimumSize = new(0, 34); button.RemoveMeta("icon_caption_minimum");
        button.AccessibilityName = Tr(mode.ToString());
        button.TooltipText = Ui.Tip(Tr(mode.ToString()) + " \u2014 " + Tr(MapModeHelp(mode)));
        row.AddChild(button);
        _mapModeButtons[mode] = button;
    }

    private void UpdateMapModeButtons(StrategicMapMode active)
    {
        _activeMapMode = active;
        foreach (var pair in _mapModeButtons)
        {
            var selected = pair.Key == active;
            pair.Value.Text = Tr(pair.Key == StrategicMapMode.Diplomatic ? "Diplomacy" : pair.Key.ToString());
            pair.Value.SetPressedNoSignal(selected);
            StyleButton(pair.Value, selected);
        }
        if (_mapLayerSelector is not null)
        {
            var source = active == StrategicMapMode.Diplomatic ? "Diplomacy" : active.ToString();
            _mapLayerSelector.Text = Tr(source) + " ▴";
            _mapLayerSelector.SetMeta(LocAppliedMeta, _mapLayerSelector.Text);
            _mapLayerSelector.Icon = _mapModeButtons.GetValueOrDefault(active)?.Icon;
            _mapLayerSelector.ExpandIcon = true; _mapLayerSelector.AddThemeConstantOverride("icon_max_width", 21);
            _mapLayerSelector.AccessibilityName = Tr(source); StyleButton(_mapLayerSelector, false);
        }
        UpdateMapModeTooltips();
    }

    private void ToggleProjection()
    { if (_globe is not null && _globe.BothProjections) _globe.ToggleProjection(); }

    private void ToggleShowcasePan()
    {
        if (_globe is null) return;
        _globe.SetShowcasePan(!_globe.ShowcasePan);
        if (_showcasePanButton is null) return;
        _showcasePanButton.SetPressedNoSignal(_globe.ShowcasePan);
        StyleButton(_showcasePanButton, _globe.ShowcasePan);
        _showcasePanButton.Text = _globe.ShowcasePan ? "STOP" : "PAN";
    }

    private void HandleProjectionHotkey(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F2 })
        {
            ToggleProjection();
            GetViewport().SetInputAsHandled();
        }
    }
}
