using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Bottom-right Map Plate (Revision 3): 6 sunken circular antique-brass medallions (Z X C V N + M),
/// optional framed minimap, and two-tab "Ещё" (M) drawer with 1-click live chip rows for
/// Map Look ("map_look") and Political Fill ("map_political_fill").
/// </summary>
internal sealed class ShellMapBarView
{
    private readonly ShellContext _ctx;

    private PanelContainer _bar;
    private HBoxContainer _medallionRow;
    private readonly Dictionary<StrategicMapMode, MedallionNodes> _quickButtons = new();
    private MedallionNodes _moreMedallion;

    // Two-tab More drawer (Modes | Layers)
    private PanelContainer _popover;
    private Button _modesTabBtn;
    private Button _layersTabBtn;
    private VBoxContainer _modesPage;
    private VBoxContainer _layersPage;
    private bool _showLayers;

    private readonly Dictionary<StrategicMapMode, Button> _allModeButtons = new();
    private readonly Dictionary<string, Button> _lookButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _fillButtons = new(StringComparer.Ordinal);
    private readonly List<LayerRowNodes> _layerRows = new();
    private Button _projectionBtn;

    // Live 256x128 political/land/sea corner minimap (on by default per ui_shell_minimap = true)
    private PanelContainer _minimapPanel;
    private Control _minimapCanvas;
    private ImageTexture _minimapTexture;
    private Campaign _minimapWorld;
    private int _minimapRevision = int.MinValue;
    private bool _minimapDragging;
    private (float Lat, float Lon, float Span) _lastCamView;

    private ShellLayoutProfile _profile;

    private sealed class MedallionNodes
    {
        public Button Button;
        public TextureRect Icon;
        public ColorRect ActiveUnderline;
        public PanelContainer Keycap;
    }

    private sealed class LayerRowNodes
    {
        public string Setting;
        public Button RowButton;
        public Control TogglePill;
    }

    private static readonly (StrategicMapMode Mode, string Hotkey, string Icon, string LabelKey)[] QuickModes =
    {
        (StrategicMapMode.Political, "Z", "icon_shell_political", "Political"),
        (StrategicMapMode.Diplomatic, "X", "icon_alliance", "Diplomacy"),
        (StrategicMapMode.Economy, "C", "icon_gdp", "Economy"),
        (StrategicMapMode.Resources, "V", "icon_terrain", "Resources"),
        // N is ALWAYS War in the bottom-right row per owner spec
        (StrategicMapMode.Wars, "N", "icon_shell_warmode", "Wars")
    };

    public bool IsPopoverOpen => _popover is { Visible: true };

    public ShellMapBarView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        _bar = new PanelContainer
        {
            Name = "ShellMapBar",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _bar.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 14, 8, 6, 8, 8, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_bar);

        _medallionRow = new HBoxContainer
        {
            Name = "ShellMapRow",
            Alignment = BoxContainer.AlignmentMode.Center
        };
        _medallionRow.AddThemeConstantOverride("separation", 8);
        _bar.AddChild(_medallionRow);

        foreach (var (mode, key, icon, labelKey) in QuickModes)
        {
            var captured = mode;
            var med = CreateMedallion("ShellMap_" + mode, icon, key,
                $"{Loc.Get(labelKey)} ({key})",
                () =>
                {
                    _ctx.SetMapMode?.Invoke(captured);
                    Refresh();
                });
            _medallionRow.AddChild(med.Button);
            _quickButtons[mode] = med;
        }

        _moreMedallion = CreateMedallion("ShellMap_More", "icon_shell_more", "M",
            $"{Loc.Get("More map modes and layers")} (M)", () => { _ctx.PlayCue?.Invoke("shell_drawer_slide"); TogglePopover(); });
        _medallionRow.AddChild(_moreMedallion.Button);

        BuildPopover();
        BuildMinimap();
    }

    private MedallionNodes CreateMedallion(string name, string iconName, string hotkey, string tip, Action onPress)
    {
        var normalBox = _ctx.NineSlice("round_bezel", 0, 0, 0, 0, 0, "shell.plate_top", "shell.engrave", 24);
        var activeBox = _ctx.NineSlice("round_bezel_active", 0, 0, 0, 0, 0, "shell.well_top", "shell.paper_muted", 24);

        var btn = new Button
        {
            Name = name,
            CustomMinimumSize = new Vector2(40, 40),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        btn.AddThemeStyleboxOverride("normal", normalBox);
        btn.AddThemeStyleboxOverride("hover", normalBox);
        btn.AddThemeStyleboxOverride("pressed", activeBox);
        btn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("shell_map_lens");
            onPress();
        };
        btn.TooltipText = Ui.Tip(tip);

        var icon = new TextureRect
        {
            Texture = _ctx.Icon(iconName),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ShellContext.ApplyIconRelief(icon, Ui.Colour("shell.icon"), 0.6f);
        icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        icon.OffsetLeft = 9; icon.OffsetRight = -9; icon.OffsetTop = 9; icon.OffsetBottom = -9;
        btn.AddChild(icon);

        var underline = new ColorRect
        {
            Color = Ui.Colour("shell.paper_muted"),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        btn.AddChild(underline);

        var kbd = _ctx.CreateKeycapBadge(hotkey, compact: true);
        btn.AddChild(kbd);

        return new MedallionNodes { Button = btn, Icon = icon, ActiveUnderline = underline, Keycap = kbd };
    }

    private void BuildPopover()
    {
        _popover = new PanelContainer
        {
            Name = "ShellMapMorePopover",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 32
        };
        _popover.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 14, 12, 10, 12, 12, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_popover);

        var rootCol = new VBoxContainer();
        rootCol.AddThemeConstantOverride("separation", 8);
        _popover.AddChild(rootCol);

        // Engraved folder tabs: Modes | Layers
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        rootCol.AddChild(tabs);

        _modesTabBtn = CreateFolderTab("Map modes", () => { _showLayers = false; RefreshPopover(); });
        _layersTabBtn = CreateFolderTab("shell.map_layers", () => { _showLayers = true; RefreshPopover(); });
        tabs.AddChild(_modesTabBtn);
        tabs.AddChild(_layersTabBtn);

        // Page 1: Map Modes + 1-Click Map Look + 1-Click Political Fill
        _modesPage = new VBoxContainer { Name = "ShellMoreModesPage" };
        _modesPage.AddThemeConstantOverride("separation", 6);
        rootCol.AddChild(_modesPage);

        BuildMapAppearanceChips(_modesPage);

        var modeGrid = new GridContainer { Columns = 2 };
        modeGrid.AddThemeConstantOverride("h_separation", 5);
        modeGrid.AddThemeConstantOverride("v_separation", 5);
        _modesPage.AddChild(modeGrid);

        foreach (StrategicMapMode mode in Enum.GetValues(typeof(StrategicMapMode)))
        {
            var captured = mode;
            var labelKey = mode == StrategicMapMode.Diplomatic ? "Diplomacy" : mode.ToString();
            var b = CreateChipButton(Loc.Get(labelKey), () =>
            {
                _ctx.SetMapMode?.Invoke(captured);
                Refresh();
            });
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            b.CustomMinimumSize = new Vector2(154, 28);
            modeGrid.AddChild(b);
            _allModeButtons[mode] = b;
        }

        _projectionBtn = CreateChipButton(Loc.Get("shell.proj_3d_globe") + " (F2)", () =>
        {
            _ctx.ToggleProjection?.Invoke();
            Refresh();
        });
        _projectionBtn.CustomMinimumSize = new Vector2(0, 28);
        _modesPage.AddChild(_projectionBtn);

        // Page 2: Visual Layers with tactile .tg switch pills
        _layersPage = new VBoxContainer { Name = "ShellMoreLayersPage", Visible = false };
        _layersPage.AddThemeConstantOverride("separation", 3);
        rootCol.AddChild(_layersPage);

        var layerDefs = new (string Setting, string LabelKey, string KeyBadge)[]
        {
            ("region_names", "Country names on the map", ""),
            ("city_names", "Cities on the map", ""),
            ("map_region_icons", "Region icons on the map", "B"),
            ("map_trade_traffic", "Trade traffic lines", ""),
            ("map_event_pulses", "Event pulses on the map", ""),
            ("map_clouds", "Clouds", ""),
            ("map_mist", "Fog-of-war mist", ""),
            ("ui_shell_minimap", "Corner minimap in new shell", "")
        };

        foreach (var (setting, labelKey, badge) in layerDefs)
            AddLayerToggleRow(_layersPage, setting, labelKey, badge);
    }

    private void BuildMapAppearanceChips(VBoxContainer parent)
    {
        // Row 1: Вид карты (map_look: realistic / paper / atlas / classic)
        var lookCap = new Label { Text = Loc.Get("shell.map_look_row") };
        lookCap.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        lookCap.AddThemeFontSizeOverride("font_size", 12);
        lookCap.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        parent.AddChild(lookCap);

        var lookRow = new HBoxContainer();
        lookRow.AddThemeConstantOverride("separation", 4);
        parent.AddChild(lookRow);

        var looks = new[]
        {
            ("realistic", "shell.look_realistic"),
            ("paper", "shell.look_paper"),
            ("atlas", "shell.look_atlas"),
            ("classic", "Classic")
        };
        foreach (var (val, key) in looks)
        {
            var capVal = val;
            var btn = CreateChipButton(Loc.Get(key), () => ApplyMapChoiceSetting("map_look", capVal));
            btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            btn.CustomMinimumSize = new Vector2(0, 26);
            lookRow.AddChild(btn);
            _lookButtons[val] = btn;
        }

        // Row 2: Заливка стран (map_political_fill: classic_opaque / balanced / translucent)
        var fillCap = new Label { Text = Loc.Get("shell.political_fill_row") };
        fillCap.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        fillCap.AddThemeFontSizeOverride("font_size", 12);
        fillCap.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        parent.AddChild(fillCap);

        var fillRow = new HBoxContainer();
        fillRow.AddThemeConstantOverride("separation", 4);
        parent.AddChild(fillRow);

        var fills = new[]
        {
            ("classic_opaque", "shell.fill_solid"),
            ("balanced", "shell.fill_balanced"),
            ("translucent", "shell.fill_translucent")
        };
        foreach (var (val, key) in fills)
        {
            var capVal = val;
            var btn = CreateChipButton(Loc.Get(key), () => ApplyMapChoiceSetting("map_political_fill", capVal));
            btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            btn.CustomMinimumSize = new Vector2(0, 26);
            fillRow.AddChild(btn);
            _fillButtons[val] = btn;
        }

        parent.AddChild(ShellContext.CreateRuledLine(31, "shell.ruled_ink", 1.2f, 4f));
    }

    private void ApplyMapChoiceSetting(string settingId, string value)
    {
        PlayerSettings.Set(settingId, value);
        if (SettingsCatalog.Of(settingId) is { } def && def.Values.Contains(value))
            SettingsCatalog.Store(settingId, value);
        else if (settingId == "map_look")
            SettingsCatalog.Store("map_look", value is "classic" ? "classic" : "atlas");

        _ctx.ApplySetting?.Invoke(settingId);
        RefreshPopover();
    }

    private void AddLayerToggleRow(VBoxContainer parent, string setting, string labelKey, string badge)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 29),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        var empty = new StyleBoxEmpty();
        var hover = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.06f) };
        btn.AddThemeStyleboxOverride("normal", empty);
        btn.AddThemeStyleboxOverride("hover", hover);

        var capSetting = setting;
        btn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            if (capSetting == "map_region_icons")
            {
                _ctx.ExplicitRegionIconsToggle = !_ctx.ExplicitRegionIconsToggle;
                SettingsCatalog.Store(capSetting, _ctx.ExplicitRegionIconsToggle);
            }
            else
            {
                SettingsCatalog.Store(capSetting, !SettingsCatalog.Flag(capSetting));
            }
            _ctx.ApplySetting?.Invoke(capSetting);
            Refresh();
        };

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 4; row.OffsetRight = -4;
        btn.AddChild(row);

        var pill = new Control
        {
            CustomMinimumSize = new Vector2(28, 15),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        var node = new LayerRowNodes { Setting = setting, RowButton = btn, TogglePill = pill };
        pill.Draw += () =>
        {
            var on = IsLayerSettingOn(capSetting);
            pill.DrawRect(new Rect2(0, 0, 28, 15), Ui.Colour("shell.well"));
            pill.DrawRect(new Rect2(0, 0, 28, 15), Ui.Colour("shell.engrave"), filled: false, width: 1f);
            var knobX = on ? 20.5f : 7.5f;
            var knobCol = on ? Ui.Colour("shell.paper") : Ui.Colour("shell.paper_ghost");
            pill.DrawCircle(new Vector2(knobX, 7.5f), 4.8f, knobCol);
        };
        row.AddChild(pill);

        var lbl = new Label
        {
            Text = Loc.Get(labelKey),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true
        };
        lbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        lbl.AddThemeFontSizeOverride("font_size", 12);
        lbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(lbl);

        if (!string.IsNullOrEmpty(badge))
        {
            var kb = _ctx.CreateKeycapBadge(badge, compact: true);
            kb.Visible = true;
            row.AddChild(kb);
        }

        parent.AddChild(btn);
        _layerRows.Add(node);
    }

    private bool IsLayerSettingOn(string setting) =>
        setting == "map_region_icons"
            ? _ctx.ExplicitRegionIconsToggle
            : SettingsCatalog.Flag(setting);

    private Button CreateFolderTab(string labelKey, Action onClick)
    {
        var b = new Button
        {
            Text = Loc.Get(labelKey),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 30),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        b.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        b.AddThemeFontSizeOverride("font_size", 14);
        b.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            onClick();
        };
        return b;
    }

    private Button CreateChipButton(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text,
            ClipText = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        b.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        b.AddThemeFontSizeOverride("font_size", 12);
        b.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            onClick();
        };
        return b;
    }

    private void BuildMinimap()
    {
        _minimapPanel = new PanelContainer
        {
            Name = "ShellMinimap",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _minimapPanel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_brass_9s", 10, 4, 4, 4, 4, "shell.well_top", "shell.brass", 2));
        _ctx.Root.AddChild(_minimapPanel);

        _minimapCanvas = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        _minimapCanvas.Draw += DrawMinimap;
        _minimapCanvas.GuiInput += OnMinimapInput;
        _minimapPanel.AddChild(_minimapCanvas);
        _minimapPanel.TooltipText = Ui.Tip(Loc.Get("Click on the minimap to jump the camera there."));
    }

    public void TogglePopover()
    {
        _popover.Visible = !_popover.Visible;
        if (_popover.Visible)
        {
            _popover.MoveToFront();
            RefreshPopover();
        }
        Refresh();
    }

    public void ClosePopover()
    {
        if (_popover is not null) _popover.Visible = false;
        Refresh();
    }

    public void Layout(ShellLayoutProfile profile)
    {
        _profile = profile;
        var compact = profile.Compact;
        ShellContext.PlaceRect(_bar, profile.MapBar);

        var medSz = profile.MapButtonSize;
        _medallionRow.AddThemeConstantOverride("separation", compact ? 4 : 8);
        foreach (var (_, med) in _quickButtons)
            LayoutSingleMedallion(med, medSz, compact);
        LayoutSingleMedallion(_moreMedallion, medSz, compact);

        var miniW = compact ? 196f : 260f;
        var miniH = compact ? 98f : 130f;
        ShellContext.PlaceRect(_minimapPanel, new ShellRect(
            profile.MapBar.Right - miniW,
            profile.MapBar.Y - miniH - 6f,
            miniW,
            miniH));

        var popW = compact ? 304f : 388f;
        var popH = compact ? 330f : 420f;
        ShellContext.PlaceRect(_popover, new ShellRect(
            Math.Max(12f, profile.MapBar.Right - popW),
            Math.Max(70f, profile.MapBar.Y - popH - 8f),
            popW,
            popH));
    }

    private static void LayoutSingleMedallion(MedallionNodes med, float sz, bool compact)
    {
        med.Button.CustomMinimumSize = new Vector2(sz, sz);
        var pad = compact ? 7f : 9f;
        med.Icon.OffsetLeft = pad;
        med.Icon.OffsetRight = -pad;
        med.Icon.OffsetTop = pad;
        med.Icon.OffsetBottom = -pad;
        med.ActiveUnderline.Position = new Vector2(sz * 0.28f, sz + 2f);
        med.ActiveUnderline.Size = new Vector2(sz * 0.44f, 2f);
        med.Keycap.Position = new Vector2(sz - 14f, sz - 14f);
    }

    public void Refresh()
    {
        var activeMode = _ctx.GetMapMode?.Invoke() ?? StrategicMapMode.Political;
        var normalBox = _ctx.NineSlice("round_bezel", 0, 0, 0, 0, 0, "shell.plate_top", "shell.engrave", 24);
        var activeBox = _ctx.NineSlice("round_bezel_active", 0, 0, 0, 0, 0, "shell.well_top", "shell.paper_muted", 24);

        foreach (var (mode, med) in _quickButtons)
        {
            var on = mode == activeMode;
            med.Button.AddThemeStyleboxOverride("normal", on ? activeBox : normalBox);
            med.Button.AddThemeStyleboxOverride("hover", on ? activeBox : normalBox);
            med.ActiveUnderline.Visible = on;
            med.Keycap.Visible = _ctx.AltKeycapsVisible;
        }

        var moreOn = IsPopoverOpen;
        _moreMedallion.Button.AddThemeStyleboxOverride("normal", moreOn ? activeBox : normalBox);
        _moreMedallion.ActiveUnderline.Visible = moreOn;
        _moreMedallion.Keycap.Visible = _ctx.AltKeycapsVisible;

        var showMini = SettingsCatalog.Flag("ui_shell_minimap");
        _minimapPanel.Visible = showMini;
        if (showMini) _minimapCanvas.QueueRedraw();

        if (IsPopoverOpen) RefreshPopover();
    }

    private void RefreshPopover()
    {
        _modesPage.Visible = !_showLayers;
        _layersPage.Visible = _showLayers;

        var tabOn = _ctx.NineSlice("tab_folder_9s", 10, 8, 4, 8, 4);
        var tabOff = _ctx.PressedWellStyle(padX: 8, padY: 4);
        _modesTabBtn.AddThemeStyleboxOverride("normal", !_showLayers ? tabOn : tabOff);
        _modesTabBtn.AddThemeColorOverride("font_color", Ui.Colour(!_showLayers ? "shell.paper" : "shell.paper_faint"));
        _layersTabBtn.AddThemeStyleboxOverride("normal", _showLayers ? tabOn : tabOff);
        _layersTabBtn.AddThemeColorOverride("font_color", Ui.Colour(_showLayers ? "shell.paper" : "shell.paper_faint"));

        var chipNormal = _ctx.NineSlice("button_umber_9s", 8, 8, 3, 8, 3);
        var chipActive = _ctx.PressedWellStyle(underlineLeft: true, padX: 8, padY: 3);

        var curMode = _ctx.GetMapMode?.Invoke() ?? StrategicMapMode.Political;
        foreach (var (m, btn) in _allModeButtons)
        {
            var on = m == curMode;
            btn.AddThemeStyleboxOverride("normal", on ? chipActive : chipNormal);
            btn.AddThemeStyleboxOverride("hover", on ? chipActive : chipNormal);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
        }

        var curLook = PlayerSettings.Text("map_look", SettingsCatalog.Chosen("map_look"));
        foreach (var (val, btn) in _lookButtons)
        {
            var on = string.Equals(curLook, val, StringComparison.OrdinalIgnoreCase);
            btn.AddThemeStyleboxOverride("normal", on ? chipActive : chipNormal);
            btn.AddThemeStyleboxOverride("hover", on ? chipActive : chipNormal);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
        }

        var curFill = SettingsCatalog.Of("map_political_fill") is not null
            ? SettingsCatalog.Chosen("map_political_fill")
            : PlayerSettings.Text("map_political_fill", "translucent");
        foreach (var (val, btn) in _fillButtons)
        {
            var on = string.Equals(curFill, val, StringComparison.OrdinalIgnoreCase);
            btn.AddThemeStyleboxOverride("normal", on ? chipActive : chipNormal);
            btn.AddThemeStyleboxOverride("hover", on ? chipActive : chipNormal);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
        }

        var isFlat = _ctx.IsFlatMap?.Invoke() ?? false;
        _projectionBtn.Text = $"{Loc.Get(isFlat ? "shell.proj_3d_globe" : "shell.proj_2d_map")} (F2)";
        _projectionBtn.AddThemeStyleboxOverride("normal", chipNormal);
        _projectionBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));

        foreach (var lr in _layerRows) lr.TogglePill.QueueRedraw();
    }

    public void TickMinimap()
    {
        if (_minimapPanel is not { Visible: true }) return;
        EnsureMinimapTextureCurrent();
        var cam = _ctx.GetCameraMinimapView?.Invoke() ?? (20f, 0f, 60f);
        if (MathF.Abs(cam.Latitude - _lastCamView.Lat) > 0.25f ||
            MathF.Abs(cam.Longitude - _lastCamView.Lon) > 0.25f ||
            MathF.Abs(cam.SpanDegrees - _lastCamView.Span) > 0.25f)
        {
            _lastCamView = cam;
            _minimapCanvas.QueueRedraw();
        }
    }

    private void EnsureMinimapTextureCurrent()
    {
        var world = _ctx.World;
        if (world?.ProvinceMap is not { Width: > 0, Height: > 0 } map) return;
        if (ReferenceEquals(_minimapWorld, world) && _minimapRevision == world.MapRevision && _minimapTexture is not null)
            return;

        _minimapWorld = world;
        _minimapRevision = world.MapRevision;

        const int mw = 256;
        const int mh = 128;
        var maxIdx = 1;
        for (var i = 0; i < world.Nations.Count; i++)
            if (world.Nations[i].MapIndex > maxIdx) maxIdx = world.Nations[i].MapIndex;

        var palR = new byte[maxIdx + 1];
        var palG = new byte[maxIdx + 1];
        var palB = new byte[maxIdx + 1];
        var playerMapIdx = world.Player?.MapIndex ?? -1;

        for (var i = 0; i < world.Nations.Count; i++)
        {
            var n = world.Nations[i];
            if (n.MapIndex <= 0 || n.MapIndex > maxIdx) continue;
            var col = Color.FromString(string.IsNullOrEmpty(n.Color) ? "#6e6c66" : n.Color, new Color(0.43f, 0.42f, 0.40f));
            // Slightly desaturate non-player fills for cartographic restraint; brighten player territory
            col = n.MapIndex == playerMapIdx
                ? col.Lightened(0.14f)
                : col.Lerp(new Color(0.28f, 0.28f, 0.27f), 0.22f);
            palR[n.MapIndex] = (byte)Math.Clamp((int)(col.R * 255f), 0, 255);
            palG[n.MapIndex] = (byte)Math.Clamp((int)(col.G * 255f), 0, 255);
            palB[n.MapIndex] = (byte)Math.Clamp((int)(col.B * 255f), 0, 255);
        }

        var cells = map.CellToProvince;
        var controllers = world.ProvinceController;
        var owners = map.ProvinceToNation;
        var ownerGrid = new int[mw * mh];

        for (var y = 0; y < mh; y++)
        {
            var sy = Math.Clamp((int)((y + 0.5f) / mh * map.Height), 0, map.Height - 1);
            var rowOff = sy * map.Width;
            for (var x = 0; x < mw; x++)
            {
                var sx = Math.Clamp((int)((x + 0.5f) / mw * map.Width), 0, map.Width - 1);
                var prov = cells[rowOff + sx];
                if (prov <= 0 || prov > map.ProvinceCount || map.IsWater(prov))
                {
                    ownerGrid[y * mw + x] = 0;
                    continue;
                }
                var ctrl = controllers is not null && prov < controllers.Length && controllers[prov] > 0
                    ? controllers[prov]
                    : (prov < owners.Length ? owners[prov] : 0);
                ownerGrid[y * mw + x] = ctrl > 0 && ctrl <= maxIdx ? ctrl : -1;
            }
        }

        var bytes = new byte[mw * mh * 4];
        for (var y = 0; y < mh; y++)
        {
            for (var x = 0; x < mw; x++)
            {
                var idx = y * mw + x;
                var o = ownerGrid[idx];
                var bOff = idx * 4;
                if (o == 0)
                {
                    // Deep neutral bathymetrical ocean with subtle latitude falloff
                    var latShade = 1f - 0.18f * MathF.Abs((y / (float)mh) * 2f - 1f);
                    bytes[bOff] = (byte)(17 * latShade);
                    bytes[bOff + 1] = (byte)(24 * latShade);
                    bytes[bOff + 2] = (byte)(32 * latShade);
                    bytes[bOff + 3] = 255;
                    continue;
                }

                byte r = o > 0 ? palR[o] : (byte)86;
                byte g = o > 0 ? palG[o] : (byte)85;
                byte b = o > 0 ? palB[o] : (byte)82;

                // Crisp 1px coastline & international border darkening
                var edge = (x > 0 && ownerGrid[idx - 1] != o) || (y > 0 && ownerGrid[idx - mw] != o);
                if (edge)
                {
                    r = (byte)(r * 0.52f);
                    g = (byte)(g * 0.52f);
                    b = (byte)(b * 0.52f);
                }

                bytes[bOff] = r;
                bytes[bOff + 1] = g;
                bytes[bOff + 2] = b;
                bytes[bOff + 3] = 255;
            }
        }

        using var img = Image.CreateFromData(mw, mh, false, Image.Format.Rgba8, bytes);
        if (_minimapTexture is null)
            _minimapTexture = ImageTexture.CreateFromImage(img);
        else
            _minimapTexture.Update(img);
        _minimapCanvas?.QueueRedraw();
    }

    private void DrawMinimap()
    {
        var w = _minimapCanvas.Size.X;
        var h = _minimapCanvas.Size.Y;
        if (w <= 4f || h <= 4f) return;

        EnsureMinimapTextureCurrent();
        if (_minimapTexture is not null)
            _minimapCanvas.DrawTextureRect(_minimapTexture, new Rect2(0, 0, w, h), tile: false);
        else
            _minimapCanvas.DrawRect(new Rect2(0, 0, w, h), new Color(0.07f, 0.10f, 0.13f, 1f));

        // Subtle graticule lines over ocean/land
        var gridCol = new Color(1f, 1f, 1f, 0.06f);
        _minimapCanvas.DrawLine(new Vector2(0, h * 0.5f), new Vector2(w, h * 0.5f), gridCol, 1f);
        _minimapCanvas.DrawLine(new Vector2(w * 0.5f, 0), new Vector2(w * 0.5f, h), gridCol, 1f);

        // Live camera viewport frame (.vp in style.css)
        var (lat, lon, span) = _ctx.GetCameraMinimapView?.Invoke() ?? (20f, 0f, 60f);
        var cx = ((lon + 180f) / 360f) * w;
        var cy = ((90f - lat) / 180f) * h;
        var vw = Math.Clamp((span / 360f) * w * 1.6f, 14f, w - 4f);
        var vh = Math.Clamp((span / 180f) * h, 10f, h - 4f);
        var rx = Math.Clamp(cx - vw * 0.5f, 1f, Math.Max(1f, w - vw - 1f));
        var ry = Math.Clamp(cy - vh * 0.5f, 1f, Math.Max(1f, h - vh - 1f));

        _minimapCanvas.DrawRect(new Rect2(rx - 1f, ry - 1f, vw + 2f, vh + 2f), new Color(0f, 0f, 0f, 0.7f), filled: false, width: 1f);
        _minimapCanvas.DrawRect(new Rect2(rx, ry, vw, vh), Ui.Colour("shell.paper"), filled: false, width: 1.5f);
    }

    private void OnMinimapInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _minimapDragging = mb.Pressed;
            if (mb.Pressed) PanCameraFromMinimapPoint(mb.Position);
            _minimapCanvas.AcceptEvent();
        }
        else if (ev is InputEventMouseMotion mm && _minimapDragging)
        {
            if ((mm.ButtonMask & MouseButtonMask.Left) == 0)
            {
                _minimapDragging = false;
                return;
            }
            PanCameraFromMinimapPoint(mm.Position);
            _minimapCanvas.AcceptEvent();
        }
    }

    private void PanCameraFromMinimapPoint(Vector2 localPos)
    {
        var w = MathF.Max(1f, _minimapCanvas.Size.X);
        var h = MathF.Max(1f, _minimapCanvas.Size.Y);
        var lon = Math.Clamp((localPos.X / w) * 360f - 180f, -180f, 180f);
        var lat = Math.Clamp(90f - (localPos.Y / h) * 180f, -78f, 78f);
        var keepDist = Math.Clamp(_ctx.GetViewDistance?.Invoke() ?? 6.5f, 3.2f, 12f);
        _ctx.FocusLatLon?.Invoke(lat, lon, keepDist);
        _minimapCanvas.QueueRedraw();
    }
}
