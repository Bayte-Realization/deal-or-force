using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Bottom-right Sovereign Brass &amp; Obsidian Map Plate: 6 sunken circular medallions (Z X C V N + M)
/// with distinct brass hover/active states and bounded tooltips, delegated live corner minimap
/// (<see cref="ShellMinimapView"/>), and scrollable two-tab "More" (M) drawer with 1-click live chip
/// rows for Map Look and Political Fill.
/// </summary>
internal sealed class ShellMapBarView
{
    private readonly ShellContext _ctx;
    private readonly ShellMinimapView _minimap;

    private PanelContainer _bar;
    private HBoxContainer _medallionRow;
    private readonly Dictionary<StrategicMapMode, MedallionNodes> _quickButtons = new();
    private MedallionNodes _showcasePanMedallion;
    private MedallionNodes _moreMedallion;

    private PanelContainer _popover;
    private Button _modesTabBtn;
    private Button _layersTabBtn;
    private Button _popoverCloseBtn;
    private VBoxContainer _modesPage;
    private VBoxContainer _layersPage;
    private bool _showLayers;

    private readonly Dictionary<StrategicMapMode, Button> _allModeButtons = new();
    private readonly Dictionary<string, Button> _lookButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _fillButtons = new(StringComparer.Ordinal);
    private readonly List<LayerRowNodes> _layerRows = new();
    private Button _projectionBtn;
    private Button _showcasePanMoreBtn;

    private StyleBox _medNormalBox;
    private StyleBoxFlat _medHoverBox;
    private StyleBox _medActiveBox;
    private StyleBoxFlat _medActiveHoverBox;
    private StyleBoxFlat _chipHoverBox;
    private StyleBoxFlat _tabHoverBox;
    private StyleBoxEmpty _layerEmpty;
    private StyleBoxFlat _layerHover;

    private ShellLayoutProfile _profile;
    private bool _modalObscured;
    private StrategicMapMode? _lastMedallionMode;
    private bool? _lastMoreOn, _lastAltVisible, _lastShowMini, _lastShowcasePan, _lastBarCompact, _lastBarTouch;
    private string _lastLang = "";
    private int _lastPopoverSig;
    private bool _hasPopoverSig;

    private sealed class MedallionNodes
    {
        public string LabelKey;
        public string TipDescKey;
        public string Hotkey;
        public Button Button;
        public TextureRect Icon;
        public ColorRect ActiveUnderline;
        public PanelContainer Keycap;
    }

    private sealed class LayerRowNodes
    {
        public string Setting;
        public string LabelKey;
        public Button RowButton;
        public Label RowLabel;
        public Control TogglePill;
    }

    private static readonly (StrategicMapMode Mode, string Hotkey, string Icon, string LabelKey, string TipDescKey)[] QuickModes =
    {
        (StrategicMapMode.Political, "Z", "icon_shell_political", "Political", "shell.ws.shell.map_tip_political"),
        (StrategicMapMode.Diplomatic, "X", "icon_alliance", "Diplomacy", "shell.ws.shell.map_tip_diplomatic"),
        (StrategicMapMode.Economy, "C", "icon_gdp", "Economy", "shell.ws.shell.map_tip_economy"),
        (StrategicMapMode.Resources, "V", "icon_terrain", "Resources", "shell.ws.shell.map_tip_resources"),
        (StrategicMapMode.Wars, "N", "icon_shell_warmode", "Wars", "shell.ws.shell.map_tip_wars")
    };

    public bool IsPopoverOpen => _popover is { Visible: true };

    public ShellMapBarView(ShellContext ctx)
    {
        _ctx = ctx;
        _minimap = new ShellMinimapView(ctx);
    }

    public void Build()
    {
        _medNormalBox = _ctx.NineSlice("round_bezel", 0, 0, 0, 0, 0, "shell.plate_top", "shell.engrave", 24);
        _medHoverBox = Ui.ChromeBox("shell.plate_hover", 0.94f, "shell.brass_hi", 0.92f, cornerRadius: 24, borderWidth: 1);
        _medActiveBox = _ctx.NineSlice("round_bezel_active", 0, 0, 0, 0, 0, "shell.well_top", "shell.brass_hi", 24);
        _medActiveHoverBox = Ui.ChromeBox("shell.plate_hover", 0.96f, "shell.highlight", 0.95f, cornerRadius: 24, borderWidth: 2);
        _chipHoverBox = Ui.ChromeBox("shell.plate_hover", 0.90f, "shell.brass_hi", 0.82f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 3);
        _tabHoverBox = Ui.ChromeBox("shell.plate_hover", 0.86f, "shell.brass_hi", 0.78f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 4);

        _bar = new PanelContainer
        {
            Name = "ShellMapBar",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _bar.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 14, 8, 6, 8, 8, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_bar);
        ChromePlateArt.AttachSurfaceTexture(_bar, "blueprint", 0.26f);

        _medallionRow = new HBoxContainer
        {
            Name = "ShellMapRow",
            Alignment = BoxContainer.AlignmentMode.Center
        };
        _medallionRow.AddThemeConstantOverride("separation", 8);
        _bar.AddChild(_medallionRow);

        foreach (var (mode, key, icon, labelKey, tipDescKey) in QuickModes)
        {
            var captured = mode;
            var med = CreateMedallion("ShellMap_" + mode, icon, key, labelKey, tipDescKey, () =>
            {
                _ctx.SetMapMode?.Invoke(captured);
                Refresh();
            });
            _medallionRow.AddChild(med.Button);
            _quickButtons[mode] = med;
        }

        _showcasePanMedallion = CreateMedallion("ShellShowcasePanBtn", "icon_world", "F6",
            "shell.showcase_spin_short", "shell.showcase_spin_tip",
            () => { _ctx.ToggleShowcasePan?.Invoke(); Refresh(); });
        _medallionRow.AddChild(_showcasePanMedallion.Button);

        _moreMedallion = CreateMedallion("ShellMap_More", "icon_shell_more", "M",
            "More map modes and layers", "shell.ws.shell.map_tip_more",
            () => { _ctx.PlayCue?.Invoke("shell_drawer_slide"); TogglePopover(); });
        _medallionRow.AddChild(_moreMedallion.Button);

        BuildPopover();
        _minimap.Build();
        _ctx.AttachPlateFrame(_bar);
    }

    private MedallionNodes CreateMedallion(string name, string iconName, string hotkey, string labelKey, string tipDescKey, Action onPress)
    {
        var btn = new Button
        {
            Name = name,
            CustomMinimumSize = new Vector2(40, 40),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        btn.AddThemeStyleboxOverride("normal", _medNormalBox);
        btn.AddThemeStyleboxOverride("hover", _medHoverBox);
        btn.AddThemeStyleboxOverride("pressed", _medActiveBox);
        btn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("shell_map_lens");
            onPress();
        };
        btn.TooltipText = Ui.Tip($"{Loc.Get(labelKey)} ({hotkey})\n{Loc.Get(tipDescKey)}", 52);

        var icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _ctx.Icon(iconName),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ShellContext.ApplyIconRelief(icon, Ui.Colour("shell.icon"), 0.6f);
        icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        icon.OffsetLeft = 9; icon.OffsetRight = -9; icon.OffsetTop = 9; icon.OffsetBottom = -9;
        btn.AddChild(icon);

        var underline = new ColorRect
        {
            Color = Ui.Colour("shell.brass_hi"),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        btn.AddChild(underline);

        var kbd = _ctx.CreateKeycapBadge(hotkey, compact: true);
        btn.AddChild(kbd);

        return new MedallionNodes
        {
            LabelKey = labelKey,
            TipDescKey = tipDescKey,
            Hotkey = hotkey,
            Button = btn,
            Icon = icon,
            ActiveUnderline = underline,
            Keycap = kbd
        };
    }

    private void BuildPopover()
    {
        _popover = new PanelContainer
        {
            Name = "ShellMapMorePopover",
            Visible = false,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 32
        };
        _popover.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 14, 12, 10, 12, 10, "shell.plate_bottom", "shell.brass", 3));
        _popover.GuiInput += ev =>
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                ClosePopover();
                _popover.AcceptEvent();
            }
        };
        _ctx.Root.AddChild(_popover);

        var rootCol = new VBoxContainer { ClipContents = true };
        rootCol.AddThemeConstantOverride("separation", 8);
        _popover.AddChild(rootCol);

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        rootCol.AddChild(tabs);

        _modesTabBtn = CreateFolderTab("Map modes", () => { _showLayers = false; RefreshPopover(); });
        _layersTabBtn = CreateFolderTab("shell.map_layers", () => { _showLayers = true; RefreshPopover(); });
        tabs.AddChild(_modesTabBtn);
        tabs.AddChild(_layersTabBtn);

        var touchInit = _ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true);
        var closeBtn = new Button
        {
            Name = "CloseShellMapPopover",
            Text = "×",
            CustomMinimumSize = touchInit ? new Vector2(42, 42) : new Vector2(32, 32),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("Close"))
        };
        closeBtn.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        closeBtn.AddThemeFontSizeOverride("font_size", touchInit ? 20 : 17);
        closeBtn.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_umber_9s", 8, 4, 2, 4, 2));
        closeBtn.AddThemeStyleboxOverride("hover", _chipHoverBox);
        closeBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        closeBtn.Pressed += ClosePopover;
        tabs.AddChild(closeBtn);
        _popoverCloseBtn = closeBtn;

        // Bounded ScrollContainer with >= 14px right scrollbar clearance for 1280x720 EN/RU safety
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            ClipContents = true
        };
        rootCol.AddChild(scroll);

        var scrollPad = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        scrollPad.AddThemeConstantOverride("margin_right", 14);
        scroll.AddChild(scrollPad);

        var pagesHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        pagesHost.AddThemeConstantOverride("separation", 0);
        scrollPad.AddChild(pagesHost);

        _modesPage = new VBoxContainer { Name = "ShellMoreModesPage", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _modesPage.AddThemeConstantOverride("separation", 6);
        pagesHost.AddChild(_modesPage);

        BuildMapAppearanceChips(_modesPage);

        var modeGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
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
            b.CustomMinimumSize = new Vector2(118, touchInit ? 38 : 29);
            modeGrid.AddChild(b);
            _allModeButtons[mode] = b;
        }

        var chipH = touchInit ? 42 : 30;
        var viewRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        viewRow.AddThemeConstantOverride("separation", 5);
        _modesPage.AddChild(viewRow);

        var f2Suffix = touchInit ? "" : " (F2)";
        _projectionBtn = CreateChipButton(Loc.Get("shell.proj_3d_globe") + f2Suffix, () =>
        {
            _ctx.ToggleProjection?.Invoke();
            Refresh();
        });
        _projectionBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _projectionBtn.CustomMinimumSize = new Vector2(0, chipH);
        viewRow.AddChild(_projectionBtn);

        var f6Suffix = touchInit ? "" : " (F6)";
        _showcasePanMoreBtn = CreateChipButton(Loc.Get("shell.showcase_spin_more") + f6Suffix, () =>
        {
            _ctx.ToggleShowcasePan?.Invoke();
            Refresh();
        });
        _showcasePanMoreBtn.Name = "ShellShowcasePanMoreBtn";
        _showcasePanMoreBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _showcasePanMoreBtn.CustomMinimumSize = new Vector2(0, chipH);
        _showcasePanMoreBtn.TooltipText = Ui.Tip(Loc.Get("shell.showcase_spin_tip"), 52);
        viewRow.AddChild(_showcasePanMoreBtn);

        _layersPage = new VBoxContainer { Name = "ShellMoreLayersPage", Visible = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _layersPage.AddThemeConstantOverride("separation", 3);
        pagesHost.AddChild(_layersPage);

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

        parent.AddChild(ShellContext.CreateRuledLine(31, "shell.brass_dim", 1.2f, 4f));
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
        var touchRow = _ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true);
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, touchRow ? 40 : 30),
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _layerEmpty ??= new StyleBoxEmpty();
        _layerHover ??= Ui.ChromeBox("shell.plate_hover", 0.78f, "shell.brass_hi", 0.65f, cornerRadius: 2, borderWidth: 1);
        btn.AddThemeStyleboxOverride("normal", _layerEmpty);
        btn.AddThemeStyleboxOverride("hover", _layerHover);

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

        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 6; row.OffsetRight = -6;
        btn.AddChild(row);

        var pill = new Control
        {
            CustomMinimumSize = new Vector2(28, 15),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        pill.Draw += () =>
        {
            var on = IsLayerSettingOn(capSetting);
            pill.DrawRect(new Rect2(0, 0, 28, 15), Ui.Colour(on ? "shell.brass_shadow" : "shell.well"));
            pill.DrawRect(new Rect2(0, 0, 28, 15), Ui.Colour(on ? "shell.brass_hi" : "shell.engrave"), filled: false, width: 1f);
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
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
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
        _layerRows.Add(new LayerRowNodes { Setting = setting, LabelKey = labelKey, RowButton = btn, RowLabel = lbl, TogglePill = pill });
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
            ClipText = true,
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
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
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

    public void SetModalObscured(bool obscured)
    {
        _modalObscured = obscured;
        if (obscured)
        {
            if (_popover is not null) _popover.Visible = false;
            _minimap.SetVisible(false);
            return;
        }
        Refresh();
    }

    public void TogglePopover()
    {
        if (_modalObscured || (_ctx.IsAnyModalOpen?.Invoke() ?? false)) { ClosePopover(); return; }
        _popover.Visible = !_popover.Visible;
        if (_popover.Visible) { _popover.MoveToFront(); RefreshPopover(); }
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
        var touch = profile.TouchMode || (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true);
        var padX = compact || touch ? 6 : 8;
        if (_lastBarCompact != compact || _lastBarTouch != touch)
        {
            _lastBarCompact = compact;
            _lastBarTouch = touch;
            var padTop = compact || touch ? 5 : 6;
            var padBot = compact || touch ? 5 : 8;
            _bar.AddThemeStyleboxOverride("panel",
                _ctx.NineSlice("plate_umber_9s", 14, padX, padTop, padX, padBot, "shell.plate_bottom", "shell.border", 3));
        }
        ShellContext.PlaceRect(_bar, profile.MapBar);
        if (_popoverCloseBtn is not null)
        {
            _popoverCloseBtn.CustomMinimumSize = touch ? new Vector2(42, 42) : new Vector2(32, 32);
            _popoverCloseBtn.AddThemeFontSizeOverride("font_size", touch ? 20 : 17);
        }

        var sep = touch || compact ? 3 : 5;
        var baseMed = touch ? 42f : (compact ? 26f : 38f);
        var availW = MathF.Max(140f, profile.MapBar.W - padX * 2f);
        var medW = MathF.Min(baseMed, MathF.Floor((availW - 6f * sep) / 7f));
        var medH = touch ? MathF.Max(44f, medW) : medW;
        _medallionRow.AddThemeConstantOverride("separation", sep);
        foreach (var (_, med) in _quickButtons) LayoutSingleMedallion(med, medW, medH, compact);
        LayoutSingleMedallion(_showcasePanMedallion, medW, medH, compact);
        LayoutSingleMedallion(_moreMedallion, medW, medH, compact);
        if (_projectionBtn is not null) _projectionBtn.CustomMinimumSize = new Vector2(0, touch ? 42 : 30);
        if (_showcasePanMoreBtn is not null) _showcasePanMoreBtn.CustomMinimumSize = new Vector2(0, touch ? 42 : 30);

        _minimap.Layout(profile);

        var popW = compact ? 344f : 412f;
        var popH = compact ? 344f : 428f;
        ShellContext.PlaceRect(_popover, new ShellRect(Math.Max(12f, profile.MapBar.Right - popW), Math.Max(70f, profile.MapBar.Y - popH - 8f), popW, popH));
    }

    private static void LayoutSingleMedallion(MedallionNodes med, float w, float h, bool compact)
    {
        if (med is null) return;
        med.Button.CustomMinimumSize = new Vector2(w, h);
        var pad = compact && w < 36f ? 5f : (w >= 40f ? 9f : 8f);
        med.Icon.OffsetLeft = pad; med.Icon.OffsetRight = -pad;
        med.Icon.OffsetTop = pad; med.Icon.OffsetBottom = -pad;
        med.ActiveUnderline.Position = new Vector2(w * 0.22f, h + 1f);
        med.ActiveUnderline.Size = new Vector2(w * 0.56f, 2.5f);
        med.Keycap.Position = new Vector2(MathF.Max(0f, w - (med.Hotkey.Length > 1 ? 19f : 15f)), MathF.Max(0f, h - 14f));
    }

    public void Refresh()
    {
        var activeMode = _ctx.GetMapMode?.Invoke() ?? StrategicMapMode.Political;
        var moreOn = IsPopoverOpen;
        var spinOn = _ctx.IsShowcasePan?.Invoke() ?? false;
        var touch = _profile?.TouchMode ?? (_ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true));
        var altVis = _ctx.AltKeycapsVisible && !touch;
        var obscured = _modalObscured || (_ctx.IsAnyModalOpen?.Invoke() ?? false);
        if (obscured && _popover is { Visible: true }) { _popover.Visible = false; moreOn = false; }
        var showMini = !obscured && SettingsCatalog.Flag("ui_shell_minimap");
        var lang = Loc.Language;

        if (_lastLang != lang)
        {
            _lastLang = lang;
            foreach (var (_, med) in _quickButtons)
                med.Button.TooltipText = Ui.Tip($"{Loc.Get(med.LabelKey)} ({med.Hotkey})\n{Loc.Get(med.TipDescKey)}", 52);
            _moreMedallion.Button.TooltipText = Ui.Tip($"{Loc.Get(_moreMedallion.LabelKey)} ({_moreMedallion.Hotkey})\n{Loc.Get(_moreMedallion.TipDescKey)}", 52);
            _modesTabBtn.Text = Loc.Get("Map modes");
            _layersTabBtn.Text = Loc.Get("shell.map_layers");
            foreach (var lr in _layerRows) lr.RowLabel.Text = Loc.Get(lr.LabelKey);
            UpdateShowcasePanTooltip(spinOn);
        }
        else if (_lastShowcasePan != spinOn)
        {
            UpdateShowcasePanTooltip(spinOn);
        }

        if (_lastMedallionMode != activeMode || _lastMoreOn != moreOn || _lastAltVisible != altVis ||
            _lastShowMini != showMini || _lastShowcasePan != spinOn)
        {
            _lastMedallionMode = activeMode;
            _lastMoreOn = moreOn;
            _lastAltVisible = altVis;
            _lastShowMini = showMini;
            _lastShowcasePan = spinOn;

            foreach (var (mode, med) in _quickButtons)
                StyleMedallion(med, mode == activeMode, altVis);
            StyleMedallion(_showcasePanMedallion, spinOn, altVis, "shell.state.positive");
            StyleMedallion(_moreMedallion, moreOn, altVis);
            _minimap.SetVisible(showMini);
        }
        if (!obscured && moreOn) RefreshPopover();
    }

    private void StyleMedallion(MedallionNodes med, bool on, bool altVis, string activeIconToken = "shell.highlight")
    {
        if (med is null) return;
        med.Button.AddThemeStyleboxOverride("normal", on ? _medActiveBox : _medNormalBox);
        med.Button.AddThemeStyleboxOverride("hover", on ? _medActiveHoverBox : _medHoverBox);
        med.ActiveUnderline.Visible = on;
        med.ActiveUnderline.Color = Ui.Colour(on && activeIconToken != "shell.highlight" ? activeIconToken : "shell.brass_hi");
        med.Keycap.Visible = altVis;
        ShellContext.ApplyIconRelief(med.Icon, Ui.Colour(on ? activeIconToken : "shell.icon"), on ? 0.94f : 0.62f);
    }

    private void UpdateShowcasePanTooltip(bool spinOn)
    {
        if (_showcasePanMedallion?.Button is null) return;
        _showcasePanMedallion.Button.TooltipText = Ui.Tip(
            $"{Loc.Get(spinOn ? "shell.showcase_spin_stop" : "shell.showcase_spin_short")} (F6)\n" +
            $"{Loc.Get(spinOn ? "shell.showcase_spin_active_tip" : "shell.showcase_spin_tip")}", 52);
    }

    private void RefreshPopover()
    {
        var curMode = _ctx.GetMapMode?.Invoke() ?? StrategicMapMode.Political;
        var curLook = PlayerSettings.Text("map_look", SettingsCatalog.Chosen("map_look"));
        var curFill = SettingsCatalog.Of("map_political_fill") is not null
            ? SettingsCatalog.Chosen("map_political_fill")
            : PlayerSettings.Text("map_political_fill", "translucent");
        var isFlat = _ctx.IsFlatMap?.Invoke() ?? false;
        var touch = _profile?.TouchMode ?? (_ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true));
        var spinOn = _ctx.IsShowcasePan?.Invoke() ?? false;
        var hc = new HashCode();
        hc.Add(_showLayers);
        hc.Add((int)curMode);
        hc.Add(curLook, StringComparer.OrdinalIgnoreCase);
        hc.Add(curFill, StringComparer.OrdinalIgnoreCase);
        hc.Add(isFlat);
        hc.Add(touch);
        hc.Add(spinOn);
        hc.Add(Loc.Language, StringComparer.Ordinal);
        for (var i = 0; i < _layerRows.Count; i++)
            hc.Add(IsLayerSettingOn(_layerRows[i].Setting));
        var sig = hc.ToHashCode();
        if (_hasPopoverSig && _lastPopoverSig == sig) return;
        _hasPopoverSig = true;
        _lastPopoverSig = sig;

        if (_modesPage.Visible == _showLayers) _modesPage.Visible = !_showLayers;
        if (_layersPage.Visible != _showLayers) _layersPage.Visible = _showLayers;

        var tabOn = _ctx.NineSlice("tab_folder_9s", 10, 8, 4, 8, 4);
        var tabOff = _ctx.PressedWellStyle(padX: 8, padY: 4);
        _modesTabBtn.AddThemeStyleboxOverride("normal", !_showLayers ? tabOn : tabOff);
        _modesTabBtn.AddThemeStyleboxOverride("hover", !_showLayers ? tabOn : _tabHoverBox);
        _modesTabBtn.AddThemeColorOverride("font_color", Ui.Colour(!_showLayers ? "shell.paper" : "shell.paper_faint"));
        _layersTabBtn.AddThemeStyleboxOverride("normal", _showLayers ? tabOn : tabOff);
        _layersTabBtn.AddThemeStyleboxOverride("hover", _showLayers ? tabOn : _tabHoverBox);
        _layersTabBtn.AddThemeColorOverride("font_color", Ui.Colour(_showLayers ? "shell.paper" : "shell.paper_faint"));

        var chipNormal = _ctx.NineSlice("button_umber_9s", 8, 8, 3, 8, 3);
        var chipActive = Ui.ChromeBox("shell.well_top", 0.96f, "shell.brass_hi", 0.92f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 3, leftBorderWidth: 3);

        foreach (var (m, btn) in _allModeButtons)
        {
            var on = m == curMode;
            btn.AddThemeStyleboxOverride("normal", on ? chipActive : chipNormal);
            btn.AddThemeStyleboxOverride("hover", on ? chipActive : _chipHoverBox);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
        }

        foreach (var (val, btn) in _lookButtons)
        {
            var on = string.Equals(curLook, val, StringComparison.OrdinalIgnoreCase);
            btn.AddThemeStyleboxOverride("normal", on ? chipActive : chipNormal);
            btn.AddThemeStyleboxOverride("hover", on ? chipActive : _chipHoverBox);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
        }

        foreach (var (val, btn) in _fillButtons)
        {
            var on = string.Equals(curFill, val, StringComparison.OrdinalIgnoreCase);
            btn.AddThemeStyleboxOverride("normal", on ? chipActive : chipNormal);
            btn.AddThemeStyleboxOverride("hover", on ? chipActive : _chipHoverBox);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
        }

        var f2 = touch ? "" : " (F2)";
        var projText = $"{Loc.Get(isFlat ? "shell.proj_3d_globe" : "shell.proj_2d_map")}{f2}";
        if (_projectionBtn.Text != projText) _projectionBtn.Text = projText;
        _projectionBtn.AddThemeStyleboxOverride("normal", chipNormal);
        _projectionBtn.AddThemeStyleboxOverride("hover", _chipHoverBox);
        _projectionBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));

        if (_showcasePanMoreBtn is not null)
        {
            var f6 = touch ? "" : " (F6)";
            var panText = $"{Loc.Get(spinOn ? "shell.showcase_spin_more_on" : "shell.showcase_spin_more")}{f6}";
            if (_showcasePanMoreBtn.Text != panText) _showcasePanMoreBtn.Text = panText;
            var panTip = Ui.Tip(Loc.Get(spinOn ? "shell.showcase_spin_active_tip" : "shell.showcase_spin_tip"), 52);
            if (_showcasePanMoreBtn.TooltipText != panTip) _showcasePanMoreBtn.TooltipText = panTip;
            _showcasePanMoreBtn.AddThemeStyleboxOverride("normal", spinOn ? chipActive : chipNormal);
            _showcasePanMoreBtn.AddThemeStyleboxOverride("hover", spinOn ? chipActive : _chipHoverBox);
            _showcasePanMoreBtn.AddThemeColorOverride("font_color", Ui.Colour(spinOn ? "shell.brass_hi" : "shell.paper"));
        }
        foreach (var lr in _layerRows) lr.TogglePill.QueueRedraw();
    }

    public void TickMinimap(bool allowTextureRebuild = true) => _minimap.Tick(allowTextureRebuild);
}
