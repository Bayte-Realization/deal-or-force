using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Step 6 Left Dossier (Country, Region, Foreign Country) in the Wave 2 campaign shell:
/// chamfered antique-brass frame docked strictly at x = rail.Right + 8 .. 544 (58..390 at 720p),
/// natural-aspect flag or brass-ringed category emblem, Source Serif 4 title + subtitle,
/// Back/Pin/Close header buttons, 3 inlaid KPI headline cards, flush folder tabs joined cleanly
/// to the body well, explicit 16px scrollbar clearance with ClipContents, and a responsive
/// two-row footer with brass-rimmed primary CTA.
/// </summary>
internal sealed class ShellWorkPanelView
{
    public enum DossierMode { None, Country, Region }

    private readonly ShellContext _ctx;

    private PanelContainer _panel;
    private Control _emblemHost;
    private TextureRect _flagTex;
    private Control _flagEdge;
    private PanelContainer _roundEmblem;
    private TextureRect _roundEmblemIcon;
    private Label _titleLabel;
    private Label _subtitleLabel;
    private HBoxContainer _headerActions;
    private Button _backBtn;
    private Button _pinBtn;
    private Button _closeBtn;

    private HBoxContainer _figuresRow;
    private readonly Label[] _figMainLabels = new Label[3];
    private readonly Label[] _figSubLabels = new Label[3];

    private HBoxContainer _tabsRow;
    private readonly List<Button> _tabButtons = new();
    private ScrollContainer _bodyScroll;
    private MarginContainer _bodyMargin;
    private VBoxContainer _bodyRows;
    private VBoxContainer _footerHost;
    private Label _feedbackLabel;

    private DossierMode _mode = DossierMode.None;
    private int _activeTab;
    private bool _pinned;
    private int _targetNationId = -1;
    private int _targetStateMapIndex = -1;
    private int _targetProvinceId = -1;
    private ShellLayoutProfile _profile;
    private string _lastBuildSig = "";
    private string _lastTabSig = "";
    private (ProvinceTopology Map, int Rev, Dictionary<int, int> Counts) _stateProvinceCache;
    private readonly Dictionary<int, int> _rankByNationCache = new();
    private (Campaign World, int Day, int Rev) _rankCacheStamp;
    private readonly List<(string Caption, bool Primary, Action OnClick)> _pendingFooterActions = new();

    public bool IsVisible => _panel is { Visible: true };
    public bool IsPinned => _pinned;
    public DossierMode Mode => _mode;

    public ShellWorkPanelView(ShellContext ctx) => _ctx = ctx;

    public void Build()
    {
        _panel = new PanelContainer { Name = "ShellWorkPanel", Visible = false, ClipContents = true, MouseFilter = Control.MouseFilterEnum.Stop };
        _panel.AddThemeStyleboxOverride("panel", _ctx.NineSlice("plate_brass_9s", 16, 14, 12, 14, 10, "shell.body_top", "shell.brass_hi", 3));
        _ctx.Root.AddChild(_panel);
        ChromePlateArt.AttachSurfaceTexture(_panel, "linen", 0.30f);

        var frame = new Control { Name = "WorkPanelFrame", ClipContents = true, CustomMinimumSize = Vector2.Zero, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _panel.AddChild(frame);

        if (_ctx.LoadTexture(SpriteCatalog.TextureOf("shell_dossier_header", "res://game/gfx/interface/shell/dossier_paper_header.png")) is { } hdrTex)
        {
            var hdrBg = new TextureRect
            {
                Texture = hdrTex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = Ui.Colour("shell.paper", 0.20f), MouseFilter = Control.MouseFilterEnum.Ignore, AnchorRight = 1f, OffsetBottom = 96f
            };
            frame.AddChild(hdrBg);
        }

        var rootCol = new VBoxContainer { ClipContents = true };
        rootCol.AddThemeConstantOverride("separation", 6);
        rootCol.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        frame.AddChild(rootCol);

        BuildHeader(rootCol);
        BuildFiguresRow(rootCol);
        rootCol.AddChild(ShellContext.CreateRuledLine(11, "shell.ruled_stroke", 1.5f, 4f));

        // Flush tab strip joined directly to the recessed body well (zero seam)
        var tabsAndBody = new VBoxContainer { Name = "WorkPanelTabsAndBody", ClipContents = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        tabsAndBody.AddThemeConstantOverride("separation", 0);
        rootCol.AddChild(tabsAndBody);

        _tabsRow = new HBoxContainer { ClipContents = true };
        _tabsRow.AddThemeConstantOverride("separation", 3);
        tabsAndBody.AddChild(_tabsRow);

        var bodyWell = new PanelContainer { Name = "WorkPanelBodyWell", ClipContents = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var bodyBox = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.body_bottom"), BorderColor = Ui.Colour("shell.engrave"),
            ContentMarginLeft = 10, ContentMarginRight = 4, ContentMarginTop = 8, ContentMarginBottom = 8
        };
        bodyBox.SetBorderWidthAll(1);
        bodyBox.SetCornerRadiusAll(2);
        bodyWell.AddThemeStyleboxOverride("panel", bodyBox);
        tabsAndBody.AddChild(bodyWell);

        _bodyScroll = new ScrollContainer { Name = "WorkPanelBodyScroll", ClipContents = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        bodyWell.AddChild(_bodyScroll);

        // Explicit >=16px right scrollbar clearance so controls never collide with scrollbar/border
        _bodyMargin = new MarginContainer { Name = "WorkPanelBodyMargin", ClipContents = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _bodyMargin.AddThemeConstantOverride("margin_right", 16);
        _bodyMargin.AddThemeConstantOverride("margin_bottom", 4);
        _bodyScroll.AddChild(_bodyMargin);

        _bodyRows = new VBoxContainer { Name = "WorkPanelBodyRows", ClipContents = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _bodyRows.AddThemeConstantOverride("separation", 5);
        _bodyMargin.AddChild(_bodyRows);

        _feedbackLabel = new Label { Text = "", Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _feedbackLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _feedbackLabel.AddThemeFontSizeOverride("font_size", 11);
        _feedbackLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.state.warning_text"));
        rootCol.AddChild(_feedbackLabel);

        _footerHost = new VBoxContainer { Name = "WorkPanelFooterHost", ClipContents = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _footerHost.AddThemeConstantOverride("separation", 5);
        rootCol.AddChild(_footerHost);
    }

    private void BuildHeader(VBoxContainer parent)
    {
        var head = new HBoxContainer { ClipContents = true };
        head.AddThemeConstantOverride("separation", 8);
        parent.AddChild(head);

        _emblemHost = new Control { CustomMinimumSize = new Vector2(54, 44), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ClipContents = true };
        head.AddChild(_emblemHost);

        _flagTex = new TextureRect
        {
            Material = FlagClothPresentation.Material,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _emblemHost.AddChild(_flagTex);

        _flagEdge = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _flagEdge.Draw += () =>
        {
            var sz = _flagEdge.Size;
            if (sz.X > 2 && sz.Y > 2) _flagEdge.DrawRect(new Rect2(0, 0, sz.X, sz.Y), Ui.Colour("shell.brass_hi", 0.85f), filled: false, width: 1f);
        };
        _emblemHost.AddChild(_flagEdge);

        _roundEmblem = new PanelContainer { Visible = false };
        _roundEmblem.AddThemeStyleboxOverride("panel", _ctx.NineSlice("round_bezel", 0, 10, 10, 10, 10, "shell.plate_top", "shell.brass", 24));
        _roundEmblem.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _emblemHost.AddChild(_roundEmblem);

        _roundEmblemIcon = new TextureRect { Texture = _ctx.Icon("icon_region"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = Ui.Colour("shell.icon") };
        _roundEmblem.AddChild(_roundEmblemIcon);

        var titles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        titles.AddThemeConstantOverride("separation", 1);
        head.AddChild(titles);

        _titleLabel = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.Off, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _titleLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        _titleLabel.AddThemeFontSizeOverride("font_size", 21);
        _titleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        titles.AddChild(_titleLabel);

        _subtitleLabel = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 2, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _subtitleLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _subtitleLabel.AddThemeFontSizeOverride("font_size", 11);
        _subtitleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        titles.AddChild(_subtitleLabel);

        var touchInit = _ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true);
        _headerActions = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        _headerActions.AddThemeConstantOverride("separation", touchInit ? 6 : 4);
        head.AddChild(_headerActions);

        _backBtn = CreateHeaderIconButton("icon_shell_back", Loc.Get("Back"), () => _ctx.NavigateBack?.Invoke(), touchInit);
        _pinBtn = CreateHeaderIconButton("icon_shell_pin", Loc.Get("Pin or unpin headline"), () => { _pinned = !_pinned; UpdatePinButtonState(); }, touchInit);
        _closeBtn = CreateHeaderIconButton("icon_shell_close", Loc.Get("Close"), Close, touchInit);
        _closeBtn.Name = "CloseShellWorkPanel";
        _headerActions.AddChild(_backBtn);
        _headerActions.AddChild(_pinBtn);
        _headerActions.AddChild(_closeBtn);
    }

    private Button CreateHeaderIconButton(string iconName, string tip, Action onClick, bool touch)
    {
        var sz = touch ? 42f : 32f;
        var inset = touch ? 9f : 7f;
        var btn = new Button { CustomMinimumSize = new Vector2(sz, sz), SizeFlagsVertical = Control.SizeFlags.ShrinkBegin, MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None, TooltipText = Ui.Tip(tip) };
        btn.AddThemeStyleboxOverride("normal", _ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 0, 0, 0, 0));
        btn.AddThemeStyleboxOverride("hover", _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 0, 0, 0, 0));
        btn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle());
        btn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };

        var tex = new TextureRect { Texture = _ctx.Icon(iconName), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = Ui.Colour("shell.paper_muted"), MouseFilter = Control.MouseFilterEnum.Ignore };
        tex.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        tex.OffsetLeft = inset; tex.OffsetRight = -inset; tex.OffsetTop = inset; tex.OffsetBottom = -inset;
        btn.AddChild(tex);
        return btn;
    }

    private void BuildFiguresRow(VBoxContainer parent)
    {
        _figuresRow = new HBoxContainer { Name = "WorkPanelHeadlineKpis", ClipContents = true };
        _figuresRow.AddThemeConstantOverride("separation", 5);
        parent.AddChild(_figuresRow);

        var kpiStyle = _ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 3, 7, 4, 7, 4);
        for (var i = 0; i < 3; i++)
        {
            var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
            card.AddThemeStyleboxOverride("panel", kpiStyle);
            _figuresRow.AddChild(card);

            var stack = new VBoxContainer { ClipContents = true };
            stack.AddThemeConstantOverride("separation", 0);
            card.AddChild(stack);

            var mainLbl = new Label { VerticalAlignment = VerticalAlignment.Center, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
            mainLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            mainLbl.AddThemeFontSizeOverride("font_size", i == 0 ? 16 : 14);
            mainLbl.AddThemeColorOverride("font_color", Ui.Colour(i == 0 ? "shell.paper" : "shell.paper_muted"));
            stack.AddChild(mainLbl);
            _figMainLabels[i] = mainLbl;

            var subLbl = new Label { VerticalAlignment = VerticalAlignment.Center, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
            subLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
            subLbl.AddThemeFontSizeOverride("font_size", 10);
            subLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
            stack.AddChild(subLbl);
            _figSubLabels[i] = subLbl;
        }
    }

    public void OpenCountry(Nation nation)
    {
        if (nation is null) return;
        if (_mode == DossierMode.Region) _ctx.ClearRegionSelection?.Invoke();
        _mode = DossierMode.Country;
        if (_targetNationId != nation.Id) _activeTab = 0;
        _targetNationId = nation.Id;
        _feedbackLabel.Visible = false;
        _lastBuildSig = "";
        _panel.Visible = true;
        Refresh();
    }

    public void OpenRegion(Nation owner, ScenarioState state, int provinceId)
    {
        if (state is null && owner is not null) { OpenCountry(owner); return; }
        if (state is null) return;
        _mode = DossierMode.Region;
        if (_targetStateMapIndex != state.MapIndex) _activeTab = 0;
        _targetNationId = owner?.Id ?? -1;
        _targetStateMapIndex = state.MapIndex;
        _targetProvinceId = provinceId;
        _feedbackLabel.Visible = false;
        _lastBuildSig = "";
        _panel.Visible = true;
        Refresh();
    }

    public void Close()
    {
        if (_mode == DossierMode.Region) _ctx.ClearRegionSelection?.Invoke();
        _mode = DossierMode.None;
        _pinned = false;
        if (_panel is not null) _panel.Visible = false;
        _ctx.RefreshAll?.Invoke();
    }

    public void ApplyDock(ShellLayoutProfile profile, ShellDockGeometry dock)
    {
        var compactChanged = _profile is null || _profile.Compact != profile.Compact || _profile.TouchMode != profile.TouchMode;
        _profile = profile;
        if (_panel is null) return;
        _panel.CustomMinimumSize = Vector2.Zero;
        ShellContext.PlaceRect(_panel, dock.LeftPanel);

        var compact = profile.Compact;
        var touch = profile.TouchMode || (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true);
        _titleLabel.AddThemeFontSizeOverride("font_size", compact ? 17 : 21);
        _figMainLabels[0].AddThemeFontSizeOverride("font_size", compact ? 14 : 16);
        _figMainLabels[1].AddThemeFontSizeOverride("font_size", compact ? 13 : 14);
        _figMainLabels[2].AddThemeFontSizeOverride("font_size", compact ? 13 : 14);

        var btnSize = touch ? new Vector2(42, 42) : new Vector2(32, 32);
        var inset = touch ? 9f : 7f;
        _headerActions?.AddThemeConstantOverride("separation", touch ? 6 : 4);
        foreach (var hb in new[] { _backBtn, _pinBtn, _closeBtn })
        {
            if (hb is null) continue;
            hb.CustomMinimumSize = btnSize;
            if (hb.GetChildCount() > 0 && hb.GetChild(0) is TextureRect tex)
            {
                tex.OffsetLeft = inset; tex.OffsetRight = -inset;
                tex.OffsetTop = inset; tex.OffsetBottom = -inset;
            }
        }
        _emblemHost.CustomMinimumSize = compact ? new Vector2(46, 38) : new Vector2(54, 44);
        if (compactChanged) { _lastTabSig = ""; _lastBuildSig = ""; }
    }

    private int _lastBuildHash;

    public void Refresh()
    {
        if (!IsVisible || _ctx.World is not { } world) return;
        var canBack = _ctx.CanNavigateBack?.Invoke() ?? false;
        if (_backBtn.Disabled == canBack) _backBtn.Disabled = !canBack;
        UpdatePinButtonState();

        var staffMask = 0;
        if (_mode == DossierMode.Country && world.Player?.Id == _targetNationId)
            for (var i = 0; i < DealOrForceGame.StaffPortfolios.Length; i++)
                if (Delegation.Holds(world.Player, DealOrForceGame.StaffPortfolios[i].Portfolio))
                    staffMask |= 1 << i;
        var hs = _ctx.HudSnapshot is { Version: > 0 } snap && snap.PlayerId >= 0 ? snap : null;
        var inboxCount = _mode == DossierMode.Country && world.Player?.Id == _targetNationId
            ? (hs is not null ? hs.IncomingDiplomaticCount : (_ctx.GetIncomingDiplomaticCount?.Invoke() ?? 0)) : 0;
        var observerMode = _ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        var compactFlag = _profile?.Compact == true ? 1 : 0;
        var prestigeSig = _mode == DossierMode.Country ? (int)MathF.Round((world.NationById(_targetNationId) ?? world.Player)?.Prestige ?? 0f) : 0;
        var hc = new HashCode();
        hc.Add((int)_mode); hc.Add(_targetNationId); hc.Add(world.Player?.Id ?? -1); hc.Add(observerMode);
        hc.Add(_targetStateMapIndex); hc.Add(_targetProvinceId); hc.Add(_activeTab); hc.Add(staffMask);
        hc.Add(inboxCount); hc.Add(compactFlag); hc.Add(prestigeSig);
        hc.Add(world.ElapsedDays); hc.Add(world.MapRevision); hc.Add(world.WarRevision);
        hc.Add(Loc.Language, StringComparer.Ordinal);
        var buildHash = hc.ToHashCode();
        if (_lastBuildSig == "ok" && _lastBuildHash == buildHash) return;
        _lastBuildSig = "ok";
        _lastBuildHash = buildHash;

        if (_mode == DossierMode.Country)
        {
            var nation = world.NationById(_targetNationId) ?? world.Player;
            if (nation is not null) PopulateCountry(world, nation);
        }
        else if (_mode == DossierMode.Region)
        {
            var state = world.StateByMapIndex(_targetStateMapIndex);
            var owner = _targetNationId >= 0 ? world.NationById(_targetNationId) : null;
            if (owner is null && state is not null)
            {
                for (var i = 0; i < world.Nations.Count; i++)
                    if (world.Nations[i].MapIndex == state.NationMapIndex) { owner = world.Nations[i]; break; }
            }
            if (state is not null) PopulateRegion(world, owner, state);
        }
    }

    private void PopulateCountry(Campaign world, Nation nation)
    {
        var isOwn = world.Player?.Id == nation.Id;
        ShowFlagEmblem(nation.Tag);
        _titleLabel.Text = nation.DisplayName;
        var ruler = Rulers.HeadOfState(world, nation);
        var overallRank = ResolveOverallRank(world, nation.Id);
        var rankText = overallRank > 0 ? $"№ {overallRank}" : "№ —";
        _subtitleLabel.Text = $"{rankText} · {Loc.Get(PoliticsCore.GovernmentOf(nation).Name)} · {Loc.Get(ruler.Title)} {ruler.Name}";

        var rawDivs = 0f;
        for (var i = 0; i < world.Armies.Count; i++)
            if (world.Armies[i].NationId == nation.Id) rawDivs += world.Armies[i].Divisions;
        var armyDivs = (int)MathF.Ceiling(rawDivs);
        var stabPct = (int)MathF.Round(nation.Stability * 100f);
        var stabTok = nation.Stability < 0.35f ? "shell.state.negative_text" : nation.Stability < 0.55f ? "shell.state.warning_text" : "shell.state.positive";
        SetFigures(
            (Fmt.People(nation.Population), Loc.Get("Population"), "shell.paper"),
            (Fmt.Money(nation.Gdp), Loc.Get("GDP"), "shell.brass_hi"),
            ($"{stabPct}% · {armyDivs} {Loc.Get("shell.div_short")}", $"{Loc.Get("Stability")} · {Loc.Get("Army")}", stabTok));

        SetFolderTabs(isOwn
            ? new[] { "Overview", "Society", "shell.tab_staff", "Diplomacy" }
            : new[] { "Overview", "Society", "Decisions", "Diplomacy" });
        ClearBodyAndFooter();

        if (_activeTab == 0)
            ShellCountryDossierView.PopulateOverview(_ctx, _bodyRows, world, nation, isOwn, OpenCountry, () => { _activeTab = 2; _lastBuildSig = ""; Refresh(); });
        else if (_activeTab == 1)
            ShellCountryDossierView.PopulateSociety(_ctx, _bodyRows, world, nation);
        else if (_activeTab == 2)
        {
            if (isOwn) ShellCountryDossierView.PopulateStaff(_ctx, _bodyRows, world, nation, () => { _lastBuildSig = ""; Refresh(); });
            else ShellCountryDossierView.PopulateDecisions(_ctx, _bodyRows, world, nation);
        }
        else
            ShellCountryDossierView.PopulateDiplomacy(_ctx, _bodyRows, _feedbackLabel, world, nation, isOwn, OpenCountry, () => { _lastBuildSig = ""; Refresh(); _ctx.RefreshAll?.Invoke(); });

        if (isOwn)
        {
            QueueFooterAction(Loc.Get("Laws"), false, () => { Close(); _ctx.OpenScreen?.Invoke("Laws"); });
            QueueFooterAction(Loc.Get("Economy"), false, () => { Close(); _ctx.OpenScreen?.Invoke("Economy"); });
            QueueFooterAction(Loc.Get("Trade"), false, () => { Close(); _ctx.OpenScreen?.Invoke("Trade"); });
            QueueFooterAction(Loc.Get("Military"), false, () => { Close(); _ctx.OpenScreen?.Invoke("Military"); });
            QueueFooterAction(Loc.Get("Decisions"), true, () => { Close(); _ctx.OpenScreen?.Invoke("Decisions"); });
        }
        else
        {
            var capturedNation = nation;
            QueueFooterAction(Loc.Get("Reports"), false, () => { Close(); _ctx.OpenScreen?.Invoke("Ledger"); });
            QueueFooterAction(Loc.Get("Trade"), false, () => { Close(); _ctx.OpenScreen?.Invoke("Trade"); });
            QueueFooterAction(Loc.Get("Diplomacy"), true, () => { Close(); _ctx.OpenDiplomacyFor?.Invoke(capturedNation); });
        }
        BuildFooterButtons();
    }

    private void PopulateRegion(Campaign world, Nation owner, ScenarioState state)
    {
        ShowRoundCategoryEmblem("icon_region");
        var isOwn = owner is not null && world.Player?.Id == owner.Id;

        _titleLabel.Text = state.DisplayName;
        var provCount = CountStateProvinces(world, state.MapIndex);
        _subtitleLabel.Text = $"{owner?.DisplayName ?? "—"} · {provCount} {Loc.Get("shell.provinces_count")}";

        var estGdp = RegionalStatistics.EstimatedGdp(world, state);
        var gdpShare = owner is not null && owner.Gdp > 0.01f ? estGdp / owner.Gdp : 0f;
        SetFigures(
            (Fmt.People(state.Population), Loc.Get("Population"), "shell.paper"),
            (Fmt.Money(estGdp), $"{(int)MathF.Round(gdpShare * 100f)}% {Loc.Get("GDP")}", "shell.brass_hi"),
            ($"{(int)MathF.Round(state.Stability * 100f)}%", Loc.Get("Stability"),
                state.Stability < 0.35f ? "shell.state.negative_text" : state.Stability < 0.55f ? "shell.state.warning_text" : "shell.state.positive"));

        SetFolderTabs(new[] { "Overview", "Population", "shell.tab_build", "shell.tab_recruit" });
        ClearBodyAndFooter();

        if (owner is not null)
        {
            var capturedOwner = owner;
            AddCountryComparisonRow(owner, "", Loc.Get("shell.to_country_link"), "shell.paper_muted", () => OpenCountry(capturedOwner));
        }

        ShellRegionVisualsView.PopulateRegion(
            _ctx, _bodyRows, _feedbackLabel, world, owner, state, _targetProvinceId, provCount, _activeTab, isOwn,
            () => { _lastBuildSig = ""; Refresh(); });

        if (isOwn)
        {
            QueueFooterAction(Loc.Get("Economy"), false, () => { Close(); _ctx.OpenScreen?.Invoke("Economy"); });
            QueueFooterAction(Loc.Get("shell.btn_recruit"), true, () => { _ctx.OpenRecruitment?.Invoke(); });
        }
        else if (owner is not null)
        {
            var capturedOwner = owner;
            QueueFooterAction(Loc.Get("Country"), false, () => OpenCountry(capturedOwner));
            QueueFooterAction(Loc.Get("Diplomacy"), true, () => { Close(); _ctx.OpenDiplomacyFor?.Invoke(capturedOwner); });
        }
        BuildFooterButtons();
    }

    private int ResolveOverallRank(Campaign world, int nationId)
    {
        var week = world.ElapsedDays / 7;
        if (world.RenderSnapshot is { Version: > 0 } snap &&
            snap.RankWeek == week &&
            snap.RankMapRev == world.MapRevision &&
            snap.RankWarRev == world.WarRevision &&
            snap.NationRanksById is { Count: > 0 } snapRanks)
        {
            return snapRanks.GetValueOrDefault(nationId, 0);
        }

        if (!ReferenceEquals(_rankCacheStamp.World, world) || _rankCacheStamp.Day != world.ElapsedDays || _rankCacheStamp.Rev != world.MapRevision)
        {
            if (world.MidStep)
            {
                if (world.RenderSnapshot is { Version: > 0 } fbSnap && fbSnap.NationRanksById is { Count: > 0 } fbRanks)
                    return fbRanks.GetValueOrDefault(nationId, 0);
                return _rankByNationCache.GetValueOrDefault(nationId, 1);
            }
            var weights = new CountryRankings.Weights(
                Ui.Metric("rankings.weight.economy", 42), Ui.Metric("rankings.weight.population", 14),
                Ui.Metric("rankings.weight.field_power", 12), Ui.Metric("rankings.weight.technology", 17),
                Ui.Metric("rankings.weight.prestige", 15));
            _rankByNationCache.Clear();
            foreach (var r in CountryRankings.Read(world, weights)) _rankByNationCache[r.NationId] = r.OverallRank;
            _rankCacheStamp = (world, world.ElapsedDays, world.MapRevision);
        }
        return _rankByNationCache.GetValueOrDefault(nationId, 1);
    }

    private int CountStateProvinces(Campaign world, int stateMapIndex)
    {
        if (world.ProvinceMap is not { } map) return 1;
        if (!ReferenceEquals(_stateProvinceCache.Map, map) || _stateProvinceCache.Rev != world.MapRevision)
        {
            var counts = _stateProvinceCache.Counts ?? new Dictionary<int, int>();
            counts.Clear();
            var arr = map.ProvinceToState;
            for (var p = 1; p <= map.ProvinceCount && p < arr.Length; p++)
                counts[arr[p]] = counts.GetValueOrDefault(arr[p]) + 1;
            _stateProvinceCache = (map, world.MapRevision, counts);
        }
        return Math.Max(1, _stateProvinceCache.Counts.GetValueOrDefault(stateMapIndex));
    }

    private void ShowFlagEmblem(string tag)
    {
        _roundEmblem.Visible = false;
        _flagTex.Visible = true;
        var hostSz = _emblemHost.CustomMinimumSize;
        var tex = _ctx.FlagTexture(tag);
        _flagTex.Texture = tex;
        var fit = ShellLayout.FitFlag(tex?.GetWidth() ?? 300, tex?.GetHeight() ?? 200, hostSz.X - 2f, hostSz.Y - 6f, tag);
        var pos = new Vector2((hostSz.X - fit.Width) * 0.5f, (hostSz.Y - fit.Height) * 0.5f);
        _flagTex.Position = pos;
        _flagTex.Size = new Vector2(fit.Width, fit.Height);
        _flagEdge.Visible = !fit.CutoutSilhouette;
        _flagEdge.Position = pos;
        _flagEdge.Size = new Vector2(fit.Width, fit.Height);
        _flagEdge.QueueRedraw();
    }

    private void ShowRoundCategoryEmblem(string iconName)
    {
        _flagTex.Visible = false;
        _flagEdge.Visible = false;
        _roundEmblem.Visible = true;
        _roundEmblemIcon.Texture = _ctx.Icon(iconName);
    }

    private void SetFigures((string Main, string Sub, string ColorTok) f0, (string Main, string Sub, string ColorTok) f1, (string Main, string Sub, string ColorTok) f2)
    {
        var arr = new[] { f0, f1, f2 };
        for (var i = 0; i < 3; i++)
        {
            _figMainLabels[i].Text = arr[i].Main;
            _figMainLabels[i].AddThemeColorOverride("font_color", Ui.Colour(arr[i].ColorTok));
            _figSubLabels[i].Text = arr[i].Sub;
        }
    }

    private void SetFolderTabs(string[] tabKeys)
    {
        if (_activeTab >= tabKeys.Length) _activeTab = 0;
        var touch = _profile?.TouchMode == true || (_ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true));
        var tabSig = string.Join("|", tabKeys) + $"|{_activeTab}|{_profile?.Compact ?? false}|{touch}|{Loc.Language}";
        if (_lastTabSig == tabSig && _tabButtons.Count == tabKeys.Length) return;
        _lastTabSig = tabSig;

        foreach (var ch in _tabsRow.GetChildren()) { _tabsRow.RemoveChild(ch); ch.QueueFree(); }
        _tabButtons.Clear();

        var onStyle = _ctx.NineSlice("tab_folder_9s", 10, 8, 5, 8, 5, "shell.body_bottom", "shell.brass_hi", 2);
        var offStyle = _ctx.PressedWellStyle(padX: 8, padY: 5);
        var offHover = _ctx.BorderedBox("shell.plate_hover", "shell.engrave", 1, 2, 8, 5, 8, 5);

        for (var i = 0; i < tabKeys.Length; i++)
        {
            var idx = i;
            var on = i == _activeTab;
            var compact = _profile?.Compact == true;
            var text = Loc.Get(tabKeys[i]);
            var b = new Button
            {
                Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, touch ? 38 : (compact ? 28 : 32)),
                ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = Ui.Tip(text),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
            };
            b.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            b.AddThemeFontSizeOverride("font_size", compact ? 12 : 13);
            b.AddThemeStyleboxOverride("normal", on ? onStyle : offStyle);
            b.AddThemeStyleboxOverride("hover", on ? onStyle : offHover);
            b.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
            b.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); _activeTab = idx; _lastBuildSig = ""; Refresh(); };
            _tabsRow.AddChild(b);
            _tabButtons.Add(b);
        }
    }

    private void ClearBodyAndFooter()
    {
        foreach (var ch in _bodyRows.GetChildren()) { _bodyRows.RemoveChild(ch); ch.QueueFree(); }
        foreach (var ch in _footerHost.GetChildren()) { _footerHost.RemoveChild(ch); ch.QueueFree(); }
        _pendingFooterActions.Clear();
    }

    private void AddCountryComparisonRow(Nation nation, string midStat, string rightStat, string rightColorTok, Action onClick)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 30), ClipContents = true,
            TooltipText = Ui.Tip($"{nation.DisplayName} · {rightStat}"),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
        };
        btn.AddThemeStyleboxOverride("normal", _ctx.BorderedBox("shell.plate_bottom", "shell.engrave", 1, 2, 6, 0, 6, 0));
        btn.AddThemeStyleboxOverride("hover", _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 6, 0, 6, 0));
        btn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        row.AddThemeConstantOverride("separation", 7);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 6; row.OffsetRight = -6;
        btn.AddChild(row);

        row.AddChild(ShellRegionKpiCardsView.BuildMiniFlagFrame(_ctx, nation.Tag, "shell.border", nation.DisplayName));

        var nm = new Label { Text = nation.DisplayName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        nm.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        nm.AddThemeFontSizeOverride("font_size", 12);
        nm.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(nm);

        if (!string.IsNullOrEmpty(midStat))
        {
            var m = new Label { Text = midStat, HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(52, 0) };
            m.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            m.AddThemeFontSizeOverride("font_size", 11);
            m.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
            row.AddChild(m);
        }

        var r = new Label { Text = rightStat, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        r.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        r.AddThemeFontSizeOverride("font_size", 11);
        r.AddThemeColorOverride("font_color", Ui.Colour(rightColorTok));
        row.AddChild(r);
        _bodyRows.AddChild(btn);
    }

    private void QueueFooterAction(string caption, bool primary, Action onClick) =>
        _pendingFooterActions.Add((caption, primary, onClick));

    private void BuildFooterButtons()
    {
        if (_pendingFooterActions.Count == 0) return;
        if (_pendingFooterActions.Count <= 3)
        {
            var row = new HBoxContainer { ClipContents = true };
            row.AddThemeConstantOverride("separation", 6);
            foreach (var act in _pendingFooterActions) row.AddChild(CreateFooterButton(act.Caption, act.Primary, act.OnClick));
            _footerHost.AddChild(row);
            return;
        }

        var secondary = _pendingFooterActions.Where(a => !a.Primary).ToList();
        var primary = _pendingFooterActions.Where(a => a.Primary).ToList();
        if (secondary.Count > 0)
        {
            var topRow = new HBoxContainer { ClipContents = true };
            topRow.AddThemeConstantOverride("separation", 5);
            foreach (var act in secondary) topRow.AddChild(CreateFooterButton(act.Caption, false, act.OnClick));
            _footerHost.AddChild(topRow);
        }
        if (primary.Count > 0)
        {
            var ctaRow = new HBoxContainer { ClipContents = true };
            ctaRow.AddThemeConstantOverride("separation", 6);
            foreach (var act in primary) ctaRow.AddChild(CreateFooterButton(act.Caption, true, act.OnClick));
            _footerHost.AddChild(ctaRow);
        }
    }

    private Button CreateFooterButton(string caption, bool primary, Action onClick)
    {
        var compact = _profile?.Compact == true;
        var touch = _profile?.TouchMode == true || (_ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true));
        var btnH = touch ? (primary ? 44 : 40) : (primary ? (compact ? 32 : 36) : (compact ? 28 : 31));
        var btn = new Button
        {
            Text = caption, CustomMinimumSize = new Vector2(0, btnH),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = Ui.Tip(caption),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
        };
        btn.AddThemeStyleboxOverride("normal", primary
            ? _ctx.NineSlice("button_brass_cta_9s", 10, 8, 4, 8, 4, "shell.plate_top", "shell.brass_hi", 3)
            : _ctx.NineSlice("button_umber_9s", 10, 6, 3, 6, 3, "shell.plate_bottom", "shell.engrave", 3));
        btn.AddThemeStyleboxOverride("hover", primary
            ? _ctx.NineSlice("button_brass_cta_9s", 10, 8, 4, 8, 4, "shell.plate_hover", "shell.paper", 3)
            : _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 3, 6, 3, 6, 3));
        btn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle(padX: 8, padY: 4));
        btn.AddThemeFontOverride("font", _ctx.Font(primary ? "shell.serif_semibold" : "shell.sans"));
        btn.AddThemeFontSizeOverride("font_size", primary ? (compact ? 13 : 14) : (compact ? 11 : 12));
        btn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        btn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };
        return btn;
    }

    private void UpdatePinButtonState()
    {
        if (_pinBtn is null) return;
        _pinBtn.AddThemeStyleboxOverride("normal", _pinned
            ? _ctx.PressedWellStyle(underlineBottom: true)
            : _ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 0, 0, 0, 0));
    }
}
