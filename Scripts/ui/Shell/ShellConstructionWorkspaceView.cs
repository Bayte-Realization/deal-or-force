using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Full-screen Sovereign Brass Construction workspace docked inside FullWorkspace:
/// 5-tab economy header with Construction active, 4-card KPI summary ribbon, responsive
/// 2-column split with searchable/categorized building catalogue + map-click placement CTA
/// on the left and live active construction queue on the right.
/// </summary>
internal sealed class ShellConstructionWorkspaceView
{
    private readonly ShellContext _ctx;

    private Control _layer;
    private PanelContainer _panel;
    private Label _subtitleLabel;
    private Button _closeBtn;
    private ConstructionKpiRibbonNodes _kpi;

    private OptionButton _regionSelect;
    private LineEdit _searchInput;
    private readonly Dictionary<ConstructionCategory, Button> _categoryBtns = new();
    private GridContainer _catalogGrid;
    private ScrollContainer _queueScroll;
    private Label _queueHeaderLabel;
    private Label _queueSummaryLabel;
    private VBoxContainer _queueList;

    private ConstructionCategory _activeCategory = ConstructionCategory.All;
    private int _selectedStateMapIndex = -1;
    private bool _syncingRegions;
    private int _localRev;
    private int _lastSig;
    private bool _hasSig;
    private ShellLayoutProfile _profile;

    public bool IsVisible => _layer is { Visible: true };

    public ShellConstructionWorkspaceView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        _layer = new Control
        {
            Name = "ShellConstructionWorkspaceLayer", Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 25
        };
        _layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _ctx.Root.AddChild(_layer);

        var dim = new ColorRect { Color = Ui.Colour("shell.well", 0.45f), MouseFilter = Control.MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(dim);

        _panel = new PanelContainer
        {
            Name = "ShellConstructionWorkspacePanel", ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _panel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_brass_9s", 16, 16, 12, 16, 12, "shell.body_top", "shell.brass_hi", 3));
        _layer.AddChild(_panel);
        ChromePlateArt.AttachSurfaceTexture(_panel, "linen", 0.30f);

        var rootCol = new VBoxContainer { ClipContents = true };
        rootCol.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(rootCol);

        BuildHeader(rootCol);
        _kpi = ShellConstructionCards.BuildKpiRibbon(_ctx, rootCol);
        BuildBody(rootCol);
    }

    private void BuildHeader(VBoxContainer parent)
    {
        var head = new HBoxContainer { ClipContents = true };
        head.AddThemeConstantOverride("separation", 10);
        parent.AddChild(head);

        var emb = new PanelContainer { CustomMinimumSize = new Vector2(44, 44), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        emb.AddThemeStyleboxOverride("panel", _ctx.NineSlice("round_bezel", 0, 9, 9, 9, 9, "shell.plate_top", "shell.brass", 24));
        emb.AddChild(new TextureRect
        {
            Texture = _ctx.Icon("icon_industry"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = Ui.Colour("shell.brass_hi")
        });
        head.AddChild(emb);

        var titleCol = new VBoxContainer { CustomMinimumSize = new Vector2(180, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ClipContents = true };
        titleCol.AddThemeConstantOverride("separation", 1);
        head.AddChild(titleCol);

        var h2 = new Label { Text = Loc.Get("shell.tab_build"), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        h2.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        h2.AddThemeFontSizeOverride("font_size", 22);
        h2.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        titleCol.AddChild(h2);

        _subtitleLabel = new Label { Text = "", ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _subtitleLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _subtitleLabel.AddThemeFontSizeOverride("font_size", 11);
        _subtitleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        titleCol.AddChild(_subtitleLabel);

        BuildHeaderTabs(head);
        BuildHeaderActions(head);
    }

    private void BuildHeaderTabs(HBoxContainer head)
    {
        var tabs = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkEnd, ClipContents = true };
        tabs.AddThemeConstantOverride("separation", 4);
        head.AddChild(tabs);

        var tabSpecs = new (string Key, Action Act)[]
        {
            ("Budget", () => { Close(); _ctx.OpenScreen?.Invoke("Economy"); }),
            ("shell.tab_debt", () => { Close(); _ctx.OpenSovereignFinance?.Invoke(); }),
            ("Market", () => { Close(); _ctx.OpenScreen?.Invoke("Trade"); }),
            ("shell.tab_build", () => { }),
            ("shell.tab_cbank", () => { Close(); _ctx.OpenScreen?.Invoke("State Affairs"); })
        };

        var tabHover = _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 10, 5, 10, 5);
        for (var i = 0; i < tabSpecs.Length; i++)
        {
            var act = tabSpecs[i].Act;
            var on = i == 3;
            var txt = Loc.Get(tabSpecs[i].Key);
            var b = new Button
            {
                Text = txt, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32),
                ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = Ui.Tip(txt),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
            };
            b.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            b.AddThemeFontSizeOverride("font_size", 13);
            var norm = on
                ? _ctx.NineSlice("tab_folder_9s", 10, 8, 5, 8, 5, "shell.body_bottom", "shell.brass_hi", 2)
                : _ctx.PressedWellStyle(padX: 8, padY: 5);
            b.AddThemeStyleboxOverride("normal", norm);
            b.AddThemeStyleboxOverride("hover", on ? norm : tabHover);
            b.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
            b.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); act(); };
            tabs.AddChild(b);
        }
    }

    private void BuildHeaderActions(HBoxContainer head)
    {
        var gpBtn = new Button
        {
            Name = "ShellConstructionOpenGrandProjectsButton", Text = Loc.Get("gp.ui.title"),
            CustomMinimumSize = new Vector2(0, 32), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("gp.ui.subtitle"))
        };
        gpBtn.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        gpBtn.AddThemeFontSizeOverride("font_size", 12);
        gpBtn.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_brass_cta_9s", 8, 10, 4, 10, 4, "shell.plate_top", "shell.brass_hi", 3));
        gpBtn.AddThemeStyleboxOverride("hover", _ctx.NineSlice("button_brass_cta_9s", 8, 10, 4, 10, 4, "shell.plate_hover", "shell.paper", 3));
        gpBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        gpBtn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); _ctx.OpenGrandProjects?.Invoke(); };
        head.AddChild(gpBtn);

        var isTouch = IsTouch();
        _closeBtn = new Button
        {
            Name = "CloseShellConstructionWorkspace", Text = "×",
            CustomMinimumSize = isTouch ? new Vector2(44, 44) : new Vector2(34, 34),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("Close"))
        };
        _closeBtn.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        _closeBtn.AddThemeFontSizeOverride("font_size", isTouch ? 22 : 18);
        _closeBtn.AddThemeStyleboxOverride("normal", _ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 0, 0, 0, 0));
        _closeBtn.AddThemeStyleboxOverride("hover", _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 0, 0, 0, 0));
        _closeBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        _closeBtn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); Close(); };
        head.AddChild(_closeBtn);
    }

    private void BuildBody(VBoxContainer parent)
    {
        var bodyBox = new PanelContainer
        {
            ClipContents = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        bodyBox.AddThemeStyleboxOverride("panel", _ctx.BorderedBox("shell.body_bottom", "shell.engrave", 1, 2, 12, 10, 10, 10));
        parent.AddChild(bodyBox);

        var split = new HBoxContainer
        {
            ClipContents = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        split.AddThemeConstantOverride("separation", 14);
        bodyBox.AddChild(split);

        BuildCatalogColumn(split);
        BuildQueueColumn(split);
    }

    private void BuildCatalogColumn(HBoxContainer split)
    {
        var leftWrap = new VBoxContainer
        {
            ClipContents = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.85f
        };
        leftWrap.AddThemeConstantOverride("separation", 8);
        split.AddChild(leftWrap);

        var controls = new HBoxContainer { ClipContents = true };
        controls.AddThemeConstantOverride("separation", 8);
        leftWrap.AddChild(controls);

        var regCap = new Label { Text = Loc.Get("shell.ws.build.region_label"), VerticalAlignment = VerticalAlignment.Center };
        regCap.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        regCap.AddThemeFontSizeOverride("font_size", 12);
        regCap.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        controls.AddChild(regCap);

        _regionSelect = new OptionButton
        {
            Name = "ShellConstructionRegionSelect", FitToLongestItem = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(180, 34),
            FocusMode = Control.FocusModeEnum.None
        };
        _regionSelect.ItemSelected += idx =>
        {
            if (_syncingRegions) return;
            _selectedStateMapIndex = _regionSelect.GetItemId((int)idx);
            _localRev++;
            Refresh();
        };
        controls.AddChild(_regionSelect);

        _searchInput = new LineEdit
        {
            Name = "ShellConstructionSearch",
            PlaceholderText = Loc.Get("shell.ws.build.search_placeholder"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(170, 34)
        };
        _searchInput.TextChanged += _ => { _localRev++; Refresh(); };
        controls.AddChild(_searchInput);

        var chips = new HBoxContainer { ClipContents = true };
        chips.AddThemeConstantOverride("separation", 5);
        leftWrap.AddChild(chips);
        foreach (var (cat, locKey) in ShellConstructionCards.Categories)
        {
            var captured = cat;
            var btn = new Button
            {
                Name = "ShellBuildCat_" + cat, Text = Loc.Get(locKey),
                CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
            };
            btn.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); _activeCategory = captured; _localRev++; Refresh(); };
            chips.AddChild(btn);
            _categoryBtns[cat] = btn;
        }

        var catalogScroll = new ScrollContainer
        {
            Name = "ShellConstructionCatalogScroll", ClipContents = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        leftWrap.AddChild(catalogScroll);

        var margin = new MarginContainer { ClipContents = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_right", 12);
        catalogScroll.AddChild(margin);

        _catalogGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _catalogGrid.AddThemeConstantOverride("h_separation", 8);
        _catalogGrid.AddThemeConstantOverride("v_separation", 8);
        margin.AddChild(_catalogGrid);
    }

    private void BuildQueueColumn(HBoxContainer split)
    {
        var rightWrap = new VBoxContainer
        {
            ClipContents = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.0f,
            CustomMinimumSize = new Vector2(280, 0)
        };
        rightWrap.AddThemeConstantOverride("separation", 6);
        split.AddChild(rightWrap);

        _queueHeaderLabel = new Label { Text = Loc.Format("shell.ws.build.queue_title_fmt", 0) };
        _queueHeaderLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        _queueHeaderLabel.AddThemeFontSizeOverride("font_size", 13);
        _queueHeaderLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        rightWrap.AddChild(_queueHeaderLabel);

        _queueSummaryLabel = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _queueSummaryLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _queueSummaryLabel.AddThemeFontSizeOverride("font_size", 11);
        _queueSummaryLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        rightWrap.AddChild(_queueSummaryLabel);

        _queueScroll = new ScrollContainer
        {
            Name = "ShellConstructionQueueScroll", ClipContents = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        rightWrap.AddChild(_queueScroll);

        var margin = new MarginContainer { ClipContents = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_right", 10);
        _queueScroll.AddChild(margin);

        _queueList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _queueList.AddThemeConstantOverride("separation", 6);
        margin.AddChild(_queueList);
    }

    public void Open()
    {
        _ctx.PlayCue?.Invoke("shell_drawer_slide");
        if (_ctx.GetSelectedState?.Invoke() is { } sel)
            _selectedStateMapIndex = sel.MapIndex;
        _layer.Visible = true;
        _layer.MoveToFront();
        _hasSig = false;
        Refresh();
    }

    public void Close()
    {
        if (_layer is not null) _layer.Visible = false;
        _ctx.RefreshAll?.Invoke();
    }

    public void Layout(ShellLayoutProfile profile)
    {
        _profile = profile;
        if (_panel is null) return;
        ShellContext.PlaceRect(_panel, profile.FullWorkspace);
        var isTouch = IsTouch();
        if (_closeBtn is not null)
        {
            _closeBtn.CustomMinimumSize = isTouch ? new Vector2(44, 44) : new Vector2(34, 34);
            _closeBtn.AddThemeFontSizeOverride("font_size", isTouch ? 22 : 18);
        }
        if (_kpi?.Root is not null)
            _kpi.Root.Columns = profile.Compact && profile.ViewportWidth < 1000f ? 2 : 4;
        if (_catalogGrid is not null)
            _catalogGrid.Columns = profile.Compact || profile.ViewportWidth < 1180f ? 1 : 2;
    }

    public void Refresh()
    {
        if (!IsVisible || _ctx.World is not { } world) return;
        var observer = ObserverUiRules.IsObserving(world, _ctx.IsObserverMode?.Invoke() ?? false);
        var n = ObserverUiRules.ResolveInspectedNation(world, _ctx.GetSelectedNation?.Invoke(), preferLanded: true);
        if (n is null) return;

        var hud = _ctx.HudSnapshot;
        var regions = OwnedLandRegions(world, n);
        var targetState = ResolveTargetState(regions, n);
        var schedule = ConstructionQueue.Schedule(world, n);
        var maxSlots = ConstructionCost.Limit(world, n, world.Defines.MaxConstructionProjects);
        var workingSlots = ConstructionCost.WorkingSites(world, n);
        var weeklyDraw = ConstructionQueue.WeeklyBill(world, n);

        var sig = ComputeSignature(world, n, hud, targetState, schedule, observer);
        if (_hasSig && _lastSig == sig) return;
        _hasSig = true;
        _lastSig = sig;

        _subtitleLabel.Text = Loc.Format("shell.ws.build.sub_fmt", schedule.Count, maxSlots, Fmt.Money(weeklyDraw));
        ShellConstructionCards.RefreshKpiRibbon(_kpi, world, n, hud, schedule.Count, maxSlots, workingSlots, weeklyDraw);
        SyncRegionSelector(regions, targetState);
        RefreshCategoryChips();
        RefreshCatalogGrid(world, n, regions, targetState, observer);
        RefreshQueueList(world, n, schedule, observer);
    }

    private int ComputeSignature(
        Campaign world, Nation n, CampaignHudSnapshot hud, ScenarioState targetState,
        List<ConstructionQueue.Estimate> schedule, bool observer)
    {
        var hc = new HashCode();
        hc.Add(n.Id); hc.Add(hud is { Version: > 0 } && hud.PlayerId == n.Id ? hud.Treasury : n.Treasury);
        hc.Add(n.ConstructionCapacity); hc.Add(targetState?.MapIndex ?? -1);
        hc.Add(_activeCategory); hc.Add(_searchInput?.Text ?? "", StringComparer.Ordinal);
        hc.Add(_localRev); hc.Add(observer); hc.Add(Loc.Language, StringComparer.Ordinal);
        hc.Add(world.ElapsedDays); hc.Add(world.MapRevision); hc.Add(schedule.Count);
        for (var i = 0; i < schedule.Count; i++)
        {
            var p = schedule[i].Project;
            hc.Add(p.Id); hc.Add((int)MathF.Round(p.Progress * 100f)); hc.Add(p.Waiting, StringComparer.Ordinal);
        }
        return hc.ToHashCode();
    }

    private static List<ScenarioState> OwnedLandRegions(Campaign world, Nation n)
    {
        var list = new List<ScenarioState>();
        foreach (var s in world.States)
        {
            if (LakeRegions.IsWaterState(s)) continue;
            if (s.NationMapIndex == n.MapIndex || (s.HeldById == n.Id && HeldRegions.Administered(world, s)))
                list.Add(s);
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private ScenarioState ResolveTargetState(List<ScenarioState> regions, Nation n)
    {
        if (regions.Count == 0) return null;
        var found = regions.FirstOrDefault(r => r.MapIndex == _selectedStateMapIndex)
            ?? regions.FirstOrDefault(r => r.MapIndex == n.CapitalStateMapIndex)
            ?? regions[0];
        _selectedStateMapIndex = found.MapIndex;
        return found;
    }

    private void SyncRegionSelector(List<ScenarioState> regions, ScenarioState target)
    {
        _syncingRegions = true;
        _regionSelect.Clear();
        for (var i = 0; i < regions.Count; i++)
            _regionSelect.AddItem(regions[i].DisplayName, regions[i].MapIndex);
        if (target is not null)
        {
            var idx = _regionSelect.GetItemIndex(target.MapIndex);
            if (idx >= 0) _regionSelect.Select(idx);
        }
        _syncingRegions = false;
    }

    private void RefreshCategoryChips()
    {
        var norm = _ctx.NineSlice("button_umber_9s", 8, 6, 3, 6, 3, "shell.plate_bottom", "shell.engrave", 2);
        var active = _ctx.PressedWellStyle(underlineBottom: true, padX: 6, padY: 3);
        foreach (var (cat, btn) in _categoryBtns)
        {
            var on = cat == _activeCategory;
            btn.AddThemeStyleboxOverride("normal", on ? active : norm);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
        }
    }

    private void RefreshCatalogGrid(Campaign world, Nation n, List<ScenarioState> regions, ScenarioState target, bool observer)
    {
        foreach (var ch in _catalogGrid.GetChildren()) { _catalogGrid.RemoveChild(ch); ch.QueueFree(); }

        var query = (_searchInput?.Text ?? "").Trim();
        var outputFactor = target is null ? 1f : MarketSystem.RegionOutputFactor(world, n, target);
        var isTouch = IsTouch();
        var actions = new ConstructionCardActions(
            PlaceOnMap: kind => { Close(); _ctx.BeginConstructionPlacement?.Invoke(kind); },
            BuildOne: kind => { if (target is not null) { _ctx.QueueConstructionInState?.Invoke(target.MapIndex, kind, 1); _localRev++; Refresh(); } },
            BuildThree: kind => { if (target is not null) { _ctx.QueueConstructionInState?.Invoke(target.MapIndex, kind, 3); _localRev++; Refresh(); } },
            DemolishOne: kind => { if (target is not null) { _ctx.DemolishBuildingInState?.Invoke(target.MapIndex, kind); _localRev++; Refresh(); } });

        var shown = 0;
        foreach (var kind in BuildingCatalog.All)
        {
            if (_activeCategory != ConstructionCategory.All && ShellConstructionCards.CategoryOf(kind) != _activeCategory)
                continue;
            var def = BuildingCatalog.Get(kind);
            var outlook = target is not null && _ctx.PreviewBuildingEffects is not null
                ? _ctx.PreviewBuildingEffects(target, kind, outputFactor)
                : (Text: "", Summary: "");
            if (query.Length > 0 &&
                !Loc.Get(def.Name).Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !outlook.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !Loc.Get(def.Description).Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            var standingHere = target?.Buildings.GetValueOrDefault(kind) ?? 0;
            var standingNation = 0;
            for (var i = 0; i < regions.Count; i++)
                standingNation += regions[i].Buildings.GetValueOrDefault(kind);

            _catalogGrid.AddChild(ShellConstructionCards.BuildBuildingCard(
                _ctx, world, n, target, kind, standingHere, standingNation, outlook, observer, isTouch, actions));
            shown++;
        }

        if (shown == 0)
        {
            var empty = new Label { Text = Loc.Get("shell.ws.build.no_matches") };
            empty.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
            empty.AddThemeFontSizeOverride("font_size", 12);
            empty.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
            _catalogGrid.AddChild(empty);
        }
    }

    private void RefreshQueueList(Campaign world, Nation n, List<ConstructionQueue.Estimate> schedule, bool observer)
    {
        foreach (var ch in _queueList.GetChildren()) { _queueList.RemoveChild(ch); ch.QueueFree(); }
        _queueHeaderLabel.Text = Loc.Format("shell.ws.build.queue_title_fmt", schedule.Count);
        _queueSummaryLabel.Text = ConstructionQueue.Summary(world, n);
        var isTouch = IsTouch();

        if (schedule.Count == 0)
        {
            _queueList.AddChild(ShellConstructionCards.BuildEmptyQueueCard(_ctx, isTouch));
            return;
        }

        foreach (var estimate in schedule)
        {
            var region = world.States.FirstOrDefault(s => s.MapIndex == estimate.Project.StateMapIndex);
            _queueList.AddChild(ShellConstructionCards.BuildQueueItemCard(
                _ctx, world, estimate, region, observer, isTouch,
                onFocusRegion: r => { _selectedStateMapIndex = r.MapIndex; _ctx.FocusState?.Invoke(r); _localRev++; Refresh(); },
                onCancel: id => { _ctx.CancelConstruction?.Invoke(id); _localRev++; Refresh(); }));
        }
    }

    private bool IsTouch() =>
        _profile?.TouchMode == true ||
        (_ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true));
}
