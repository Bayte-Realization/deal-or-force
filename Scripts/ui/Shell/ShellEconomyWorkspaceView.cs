using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Full-screen 5-tab "Economy" workspace docked inside FullWorkspace:
/// top 4-card fiscal summary ribbon (Treasury, Weekly Flow, Debt/GDP, Inflation),
/// responsive 2-col/1-col Revenue &amp; Spending sliders with inline quick presets,
/// color-coded spending share bar, labeled 6-month treasury trajectory chart, prominent
/// Grand Projects CTA card, domestic supply-shortage alerts, and Debt quick repayment.
/// </summary>
internal sealed class ShellEconomyWorkspaceView
{
    private readonly ShellContext _ctx;

    private Control _layer;
    private PanelContainer _panel;
    private Label _subtitleLabel;
    private Button _closeBtn;
    private readonly Button[] _tabButtons = new Button[5];
    private EconomyFiscalRibbonNodes _ribbon;
    private EconomyBaseStripNodes _baseStrip;

    private GridContainer _budgetGrid;
    private VBoxContainer _revRows;
    private VBoxContainer _expRows;
    private Label _revSumLabel;
    private Label _expSumLabel;
    private EconomyTaxEfficiencyNodes _taxEfficiencyBanner;
    private HBoxContainer _spendingStackBar;
    private Control _treasuryChart;
    private VBoxContainer _productionMethodsBox;

    private ScrollContainer _rightScroll;
    private Label _balanceBigLabel;
    private Control _balanceMeter;
    private Label _forecastNoteLabel;
    private Label _gpCardStatusLabel;
    private VBoxContainer _shortagesBox;

    private Label _debtSummaryLabel;
    private Button _repayAllBtn;
    private Button _zeroBtn;
    private readonly Button[] _repayQuarterBtns = new Button[4];
    private Label _repayAmountField;
    private Label _repayPreviewLabel;
    private Label _feedbackLabel;

    private int _selectedQuarter = 3;
    private string _focusedLineId = "";
    private bool _syncingSliders;
    private ShellLayoutProfile _profile;
    private int _lastEcoSig;
    private bool _hasEcoSig;
    private int _localEcoRev;
    private readonly List<(string Name, float Amount)> _expSharesScratch = new(16);

    private readonly Dictionary<string, BudgetRowNodes> _lineNodes = new(StringComparer.OrdinalIgnoreCase);

    private sealed class BudgetRowNodes
    {
        public BudgetLine Line;
        public Button NameBtn;
        public HSlider Slider;
        public Label RateLabel;
        public Label MoneyLabel;
        public HBoxContainer QuickBar;
    }

    public bool IsVisible => _layer is { Visible: true };

    public ShellEconomyWorkspaceView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        _layer = new Control { Name = "ShellEconomyWorkspaceLayer", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 25 };
        _layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _ctx.Root.AddChild(_layer);

        var dim = new ColorRect { Color = Ui.Colour("shell.well", 0.45f), MouseFilter = Control.MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(dim);

        _panel = new PanelContainer { Name = "ShellEconomyWorkspacePanel", ClipContents = true, MouseFilter = Control.MouseFilterEnum.Stop };
        _panel.AddThemeStyleboxOverride("panel", _ctx.NineSlice("plate_brass_9s", 16, 16, 12, 16, 12, "shell.body_top", "shell.brass_hi", 3));
        _layer.AddChild(_panel);
        ChromePlateArt.AttachSurfaceTexture(_panel, "linen", 0.30f);

        var rootCol = new VBoxContainer { ClipContents = true };
        rootCol.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(rootCol);

        BuildHeader(rootCol);
        _ribbon = ShellEconomyCharts.BuildFiscalSummaryRibbon(_ctx, rootCol);
        _baseStrip = ShellEconomyCharts.BuildBaseMetricsStrip(_ctx, rootCol);
        BuildBody(rootCol);
        BuildFooter(rootCol);
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
            Texture = _ctx.Icon("icon_economy"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = Ui.Colour("shell.brass_hi")
        });
        head.AddChild(emb);

        var titleCol = new VBoxContainer { CustomMinimumSize = new Vector2(180, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ClipContents = true };
        titleCol.AddThemeConstantOverride("separation", 1);
        head.AddChild(titleCol);

        var h2 = new Label { Text = Loc.Get("Economy"), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        h2.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        h2.AddThemeFontSizeOverride("font_size", 22);
        h2.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        titleCol.AddChild(h2);

        _subtitleLabel = new Label { Text = Loc.Get("shell.budget_weekly_sub"), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _subtitleLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _subtitleLabel.AddThemeFontSizeOverride("font_size", 11);
        _subtitleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        titleCol.AddChild(_subtitleLabel);

        var tabs = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkEnd, ClipContents = true };
        tabs.AddThemeConstantOverride("separation", 4);
        head.AddChild(tabs);

        var tabSpecs = new (string Key, Action Act)[]
        {
            ("Budget", () => { }),
            ("shell.tab_debt", () => { Close(); _ctx.OpenSovereignFinance?.Invoke(); }),
            ("Market", () => { Close(); _ctx.OpenScreen?.Invoke("Trade"); }),
            ("shell.tab_build", () => { Close(); _ctx.OpenScreen?.Invoke("State Affairs"); }),
            ("shell.tab_cbank", () => { Close(); _ctx.OpenScreen?.Invoke("State Affairs"); })
        };

        var tabHover = _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 10, 5, 10, 5);
        for (var i = 0; i < tabSpecs.Length; i++)
        {
            var act = tabSpecs[i].Act;
            var on = i == 0;
            var txt = Loc.Get(tabSpecs[i].Key);
            var b = new Button
            {
                Text = txt, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32),
                ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = Ui.Tip(txt),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
            };
            b.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            b.AddThemeFontSizeOverride("font_size", 13);
            var norm = on ? _ctx.NineSlice("tab_folder_9s", 10, 8, 5, 8, 5, "shell.body_bottom", "shell.brass_hi", 2) : _ctx.PressedWellStyle(padX: 8, padY: 5);
            b.AddThemeStyleboxOverride("normal", norm);
            b.AddThemeStyleboxOverride("hover", on ? norm : tabHover);
            b.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
            b.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); act(); };
            tabs.AddChild(b);
            _tabButtons[i] = b;
        }

        var gpBtn = new Button
        {
            Name = "ShellOpenGrandProjectsButton", Text = Loc.Get("gp.ui.title"),
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

        var isTouch = _ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true);
        _closeBtn = new Button
        {
            Name = "CloseShellEconomyWorkspace",
            Text = "×",
            CustomMinimumSize = isTouch ? new Vector2(44, 44) : new Vector2(34, 34),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
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
        var bodyBox = new PanelContainer { ClipContents = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var bodyStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.body_bottom"), BorderColor = Ui.Colour("shell.engrave"),
            ContentMarginLeft = 14, ContentMarginRight = 10, ContentMarginTop = 10, ContentMarginBottom = 10
        };
        bodyStyle.SetBorderWidthAll(1);
        bodyStyle.SetCornerRadiusAll(2);
        bodyBox.AddThemeStyleboxOverride("panel", bodyStyle);
        parent.AddChild(bodyBox);

        var mainSplit = new HBoxContainer { ClipContents = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        mainSplit.AddThemeConstantOverride("separation", 16);
        bodyBox.AddChild(mainSplit);

        var leftScroll = new ScrollContainer
        {
            Name = "ShellEconomyLeftScroll", ClipContents = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 2.45f, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        mainSplit.AddChild(leftScroll);

        var leftMargin = new MarginContainer { ClipContents = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        leftMargin.AddThemeConstantOverride("margin_right", 16);
        leftScroll.AddChild(leftMargin);

        var leftCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        leftCol.AddThemeConstantOverride("separation", 10);
        leftMargin.AddChild(leftCol);

        _budgetGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _budgetGrid.AddThemeConstantOverride("h_separation", 18);
        _budgetGrid.AddThemeConstantOverride("v_separation", 10);
        leftCol.AddChild(_budgetGrid);

        var revCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        revCol.AddThemeConstantOverride("separation", 2);
        revCol.AddChild(BuildSectionRule(Loc.Get("REVENUE"), out _revSumLabel));
        _revRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _revRows.AddThemeConstantOverride("separation", 2);
        revCol.AddChild(_revRows);
        _taxEfficiencyBanner = ShellEconomyCharts.BuildTaxEfficiencyBanner(_ctx, revCol);
        _budgetGrid.AddChild(revCol);

        var expCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        expCol.AddThemeConstantOverride("separation", 2);
        expCol.AddChild(BuildSectionRule(Loc.Get("SPENDING"), out _expSumLabel));
        _expRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _expRows.AddThemeConstantOverride("separation", 2);
        expCol.AddChild(_expRows);
        _budgetGrid.AddChild(expCol);

        foreach (var line in BudgetCore.PlayerLines) BuildBudgetSliderRow(line);

        leftCol.AddChild(BuildSectionRule(Loc.Get("shell.where_money_goes"), out var shareRight));
        shareRight.Text = Loc.Get("shell.share_of_spending");

        _spendingStackBar = new HBoxContainer { CustomMinimumSize = new Vector2(0, 42), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _spendingStackBar.AddThemeConstantOverride("separation", 2);
        leftCol.AddChild(_spendingStackBar);

        leftCol.AddChild(BuildSectionRule(Loc.Get("shell.six_month_treasury"), out var chartRight));
        chartRight.Text = Loc.Get("shell.unit_billion");

        _treasuryChart = new Control { Name = "ShellEconomyTreasuryChart", CustomMinimumSize = new Vector2(0, 128), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _treasuryChart.Draw += () => ShellEconomyCharts.DrawTreasuryChart(_treasuryChart, _ctx, _ctx.World, _ctx.World?.Player);
        leftCol.AddChild(_treasuryChart);

        ShellEconomyProductionMethods.BuildSection(_ctx, leftCol, out _productionMethodsBox);

        _rightScroll = new ScrollContainer
        {
            Name = "ShellEconomyRightScroll", ClipContents = true, CustomMinimumSize = new Vector2(300, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.0f, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        mainSplit.AddChild(_rightScroll);

        var rightMargin = new MarginContainer { ClipContents = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rightMargin.AddThemeConstantOverride("margin_right", 15);
        _rightScroll.AddChild(rightMargin);

        var rightCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        rightCol.AddThemeConstantOverride("separation", 10);
        rightMargin.AddChild(rightCol);
        BuildRightSummaryColumn(rightCol);
    }

    private void BuildBudgetSliderRow(BudgetLine line)
    {
        var parent = line.Side == BudgetSide.Revenue ? _revRows : _expRows;
        var wrap = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        wrap.AddThemeConstantOverride("separation", 2);

        var rowPanel = new PanelContainer { CustomMinimumSize = new Vector2(0, 32), ClipContents = true };
        var rowStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.well", 0f), BorderColor = Ui.Colour("shell.paper", 0.08f),
            BorderWidthBottom = 1, ContentMarginLeft = 4, ContentMarginRight = 4
        };
        rowPanel.AddThemeStyleboxOverride("panel", rowStyle);
        wrap.AddChild(rowPanel);

        var h = new HBoxContainer { ClipContents = true };
        h.AddThemeConstantOverride("separation", 6);
        rowPanel.AddChild(h);

        var nameBtn = new Button
        {
            Text = "▸ " + Loc.Get(line.Name), CustomMinimumSize = new Vector2(116, 0),
            Alignment = HorizontalAlignment.Left, ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = Ui.Tip(Loc.Get(line.Name)),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
        };
        nameBtn.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        nameBtn.AddThemeStyleboxOverride("hover", _ctx.BorderedBox("shell.plate_hover", "shell.engrave", 1, 2, 4, 1, 4, 1));
        nameBtn.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        nameBtn.AddThemeFontSizeOverride("font_size", 12);
        nameBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        var lineId = line.Id;
        nameBtn.Pressed += () => { _focusedLineId = _focusedLineId == lineId ? "" : lineId; Refresh(); };
        h.AddChild(nameBtn);

        var slider = new HSlider
        {
            MinValue = line.Minimum, MaxValue = line.Maximum, Step = 0.005,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(76, 20), Scrollable = false
        };
        slider.ValueChanged += val => OnRateChanged(lineId, (float)val);
        h.AddChild(slider);

        var rateLbl = new Label { CustomMinimumSize = new Vector2(46, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        rateLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        rateLbl.AddThemeFontSizeOverride("font_size", 12);
        rateLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        h.AddChild(rateLbl);

        var moneyLbl = new Label { CustomMinimumSize = new Vector2(60, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        moneyLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        moneyLbl.AddThemeFontSizeOverride("font_size", 11);
        moneyLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        h.AddChild(moneyLbl);

        var quick = new HBoxContainer { Visible = false, ClipContents = true };
        quick.AddThemeConstantOverride("separation", 4);
        AddQuickDeltaChip(quick, "−5%", () => StepRate(line, -0.05f));
        AddQuickDeltaChip(quick, "+5%", () => StepRate(line, +0.05f));
        AddQuickDeltaChip(quick, "25%", () => SetFractionRate(line, 0.25f));
        AddQuickDeltaChip(quick, "50%", () => SetFractionRate(line, 0.50f));
        AddQuickDeltaChip(quick, "75%", () => SetFractionRate(line, 0.75f));
        wrap.AddChild(quick);

        parent.AddChild(wrap);
        _lineNodes[line.Id] = new BudgetRowNodes { Line = line, NameBtn = nameBtn, Slider = slider, RateLabel = rateLbl, MoneyLabel = moneyLbl, QuickBar = quick };
    }

    private void AddQuickDeltaChip(HBoxContainer parent, string label, Action onClick)
    {
        var b = new Button { Text = label, CustomMinimumSize = new Vector2(42, 22), MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None };
        b.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_umber_9s", 8, 5, 2, 5, 2));
        b.AddThemeStyleboxOverride("hover", _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 5, 2, 5, 2));
        b.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        b.AddThemeFontSizeOverride("font_size", 11);
        b.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        b.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };
        parent.AddChild(b);
    }

    private void StepRate(BudgetLine line, float delta)
    {
        if (ObserverUiRules.IsObserving(_ctx.World, _ctx.IsObserverMode?.Invoke() ?? false)) return;
        if (_ctx.World?.Player is not { } n) return;
        OnRateChanged(line.Id, Math.Clamp(BudgetCore.RateOf(n, line) + delta, line.Minimum, line.Maximum));
    }

    private void SetFractionRate(BudgetLine line, float frac) =>
        OnRateChanged(line.Id, line.Minimum + (line.Maximum - line.Minimum) * frac);

    private void OnRateChanged(string lineId, float value)
    {
        if (_syncingSliders || ObserverUiRules.IsObserving(_ctx.World, _ctx.IsObserverMode?.Invoke() ?? false)) return;
        if (_ctx.World?.Player is not { } n) return;
        _ctx.World.Commands.Enqueue(new SetPolicyRateCommand { IssuerId = n.Id, LineId = lineId, Value = value });
        _ctx.World.Commands.ApplyAll(_ctx.World);
        Refresh();
        _ctx.RefreshAll?.Invoke();
    }

    private void BuildRightSummaryColumn(VBoxContainer rightCol)
    {
        rightCol.AddChild(BuildSectionRule(Loc.Get("Balance"), out _));

        _balanceBigLabel = new Label { Text = "$0" };
        _balanceBigLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        _balanceBigLabel.AddThemeFontSizeOverride("font_size", 26);
        rightCol.AddChild(_balanceBigLabel);

        var dblRule = new Control { CustomMinimumSize = new Vector2(0, 5) };
        dblRule.Draw += () =>
        {
            var w = Math.Min(180f, dblRule.Size.X);
            dblRule.DrawLine(new Vector2(0, 0), new Vector2(w, 0), Ui.Colour("shell.paper_muted"), 1f);
            dblRule.DrawLine(new Vector2(0, 3), new Vector2(w, 3), Ui.Colour("shell.paper_muted"), 1f);
        };
        rightCol.AddChild(dblRule);

        _balanceMeter = new Control { CustomMinimumSize = new Vector2(0, 10) };
        _balanceMeter.Draw += () => ShellEconomyCharts.DrawBalanceMeter(_balanceMeter,
            ObserverUiRules.ResolveInspectedNation(_ctx.World, _ctx.GetSelectedNation?.Invoke(), preferLanded: true));
        rightCol.AddChild(_balanceMeter);

        _forecastNoteLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _forecastNoteLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _forecastNoteLabel.AddThemeFontSizeOverride("font_size", 11);
        _forecastNoteLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        rightCol.AddChild(_forecastNoteLabel);

        _gpCardStatusLabel = ShellEconomyCharts.BuildGrandProjectsCtaCard(_ctx, rightCol);

        rightCol.AddChild(BuildSectionRule(Loc.Get("shell.ws.eco.shortages_title"), out _));
        _shortagesBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _shortagesBox.AddThemeConstantOverride("separation", 4);
        rightCol.AddChild(_shortagesBox);

        rightCol.AddChild(BuildSectionRule(Loc.Get("shell.tab_debt"), out _debtSummaryLabel));

        _repayAllBtn = new Button
        {
            Text = Loc.Get("Repay all we can"), CustomMinimumSize = new Vector2(0, 34),
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
        };
        _repayAllBtn.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_umber_9s", 10, 10, 4, 10, 4));
        _repayAllBtn.AddThemeStyleboxOverride("hover", _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 3, 10, 4, 10, 4));
        _repayAllBtn.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        _repayAllBtn.AddThemeFontSizeOverride("font_size", 12);
        _repayAllBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        _repayAllBtn.Pressed += ExecuteQuickDebtRepay;
        rightCol.AddChild(_repayAllBtn);

        var qRow = new HBoxContainer { ClipContents = true };
        qRow.AddThemeConstantOverride("separation", 4);
        rightCol.AddChild(qRow);
        for (var i = 0; i < 4; i++)
        {
            var idx = i;
            var qb = new Button { Text = $"{(i + 1) * 25}%", CustomMinimumSize = new Vector2(44, 25), MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None };
            qb.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            qb.AddThemeFontSizeOverride("font_size", 11);
            qb.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); _selectedQuarter = idx; Refresh(); };
            qRow.AddChild(qb);
            _repayQuarterBtns[i] = qb;
        }

        qRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var amtWell = new PanelContainer { CustomMinimumSize = new Vector2(82, 25), ClipContents = true };
        amtWell.AddThemeStyleboxOverride("panel", _ctx.NineSlice("well_recessed_9s", 8, 6, 2, 6, 2, "shell.well_top", "shell.engrave", 2));
        _repayAmountField = new Label { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _repayAmountField.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        _repayAmountField.AddThemeFontSizeOverride("font_size", 11);
        _repayAmountField.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        amtWell.AddChild(_repayAmountField);
        qRow.AddChild(amtWell);

        _repayPreviewLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _repayPreviewLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _repayPreviewLabel.AddThemeFontSizeOverride("font_size", 11);
        _repayPreviewLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        rightCol.AddChild(_repayPreviewLabel);

        _feedbackLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _feedbackLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _feedbackLabel.AddThemeFontSizeOverride("font_size", 11);
        _feedbackLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.state.positive"));
        rightCol.AddChild(_feedbackLabel);
    }

    private void BuildFooter(VBoxContainer parent)
    {
        var foot = new HBoxContainer { ClipContents = true };
        foot.AddThemeConstantOverride("separation", 8);
        parent.AddChild(foot);

        foot.AddChild(CreateFooterButton(Loc.Get("State Affairs"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("State Affairs"); }));
        foot.AddChild(CreateFooterButton(Loc.Get("Sovereign finance"), primary: false, () => { Close(); _ctx.OpenSovereignFinance?.Invoke(); }));
        foot.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var zeroBtn = CreateFooterButton(Loc.Get("shell.balance_to_zero"), primary: true, () =>
        {
            if (ObserverUiRules.IsObserving(_ctx.World, _ctx.IsObserverMode?.Invoke() ?? false)) return;
            _ctx.ExecuteZeroBudget?.Invoke(Input.IsKeyPressed(Key.Shift));
            Refresh();
            _ctx.RefreshAll?.Invoke();
        });
        _zeroBtn = zeroBtn;
        zeroBtn.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                if (ObserverUiRules.IsObserving(_ctx.World, _ctx.IsObserverMode?.Invoke() ?? false)) return;
                _ctx.PlayCue?.Invoke("ui_select");
                _ctx.ExecuteZeroBudget?.Invoke(true);
                Refresh();
                _ctx.RefreshAll?.Invoke();
                zeroBtn.AcceptEvent();
            }
        };
        zeroBtn.TooltipText = Ui.Tip(Loc.Get("shell.balance_to_zero_tip"));
        foot.AddChild(zeroBtn);
    }

    private Button CreateFooterButton(string text, bool primary, Action onClick)
    {
        var b = new Button
        {
            Text = text, CustomMinimumSize = new Vector2(primary ? 168 : 126, primary ? 36 : 32),
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
        };
        b.AddThemeStyleboxOverride("normal", primary
            ? _ctx.NineSlice("button_brass_cta_9s", 10, 14, 5, 14, 5, "shell.plate_top", "shell.brass_hi", 3)
            : _ctx.NineSlice("button_umber_9s", 10, 10, 4, 10, 4, "shell.plate_bottom", "shell.engrave", 3));
        b.AddThemeStyleboxOverride("hover", primary
            ? _ctx.NineSlice("button_brass_cta_9s", 10, 14, 5, 14, 5, "shell.plate_hover", "shell.paper", 3)
            : _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 3, 10, 4, 10, 4));
        b.AddThemeFontOverride("font", _ctx.Font(primary ? "shell.serif_semibold" : "shell.sans"));
        b.AddThemeFontSizeOverride("font_size", primary ? 14 : 12);
        b.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        b.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };
        return b;
    }

    private HBoxContainer BuildSectionRule(string title, out Label rightLabel)
    {
        var h = new HBoxContainer { ClipContents = true };
        h.AddThemeConstantOverride("separation", 8);
        var l = new Label { Text = title };
        l.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        l.AddThemeFontSizeOverride("font_size", 13);
        l.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        h.AddChild(l);
        h.AddChild(new ColorRect { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(10, 1), Color = Ui.Colour("shell.engrave") });

        rightLabel = new Label();
        rightLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        rightLabel.AddThemeFontSizeOverride("font_size", 11);
        rightLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        h.AddChild(rightLabel);
        return h;
    }

    public void Open()
    {
        _ctx.PlayCue?.Invoke("shell_drawer_slide");
        _layer.Visible = true;
        _layer.MoveToFront();
        _feedbackLabel.Visible = false;
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
        if (_closeBtn is not null)
        {
            var isTouch = profile.TouchMode || (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true);
            _closeBtn.CustomMinimumSize = isTouch ? new Vector2(44, 44) : new Vector2(34, 34);
            _closeBtn.AddThemeFontSizeOverride("font_size", isTouch ? 22 : 18);
        }
        if (_baseStrip?.Root is not null)
            _baseStrip.Root.Columns = profile.Compact && profile.ViewportWidth < 1000f ? 2 : 4;
        if (_budgetGrid is not null) _budgetGrid.Columns = profile.Compact ? 1 : 2;
        if (_rightScroll is not null) _rightScroll.CustomMinimumSize = new Vector2(profile.Compact ? 280 : 336, 0);
    }

    public void Refresh()
    {
        var observer = ObserverUiRules.IsObserving(_ctx.World, _ctx.IsObserverMode?.Invoke() ?? false);
        var n = ObserverUiRules.ResolveInspectedNation(_ctx.World, _ctx.GetSelectedNation?.Invoke(), preferLanded: true);
        if (!IsVisible || _ctx.World is not { } world || n is null) return;
        var atWar = false;
        if (_ctx.HudSnapshot is { Version: > 0 } hs && hs.PlayerId == n.Id)
            atWar = hs.OpenPlayerWars.Length > 0;
        else
        {
            var openWars = Warfare.OpenWars(world);
            for (var i = 0; i < openWars.Count; i++)
                if (!openWars[i].Ended && openWars[i].Involves(n.Id)) { atWar = true; break; }
        }
        var weeklyOut = n.Gdp / 52f;

        var runningSites = 0;
        for (var i = 0; i < world.Construction.Count; i++)
            if (world.Construction[i].NationId == n.Id) runningSites++;
        var siteSlots = ConstructionCost.Limit(world, n, world.Defines.MaxConstructionProjects);
        var gpActive = 0;
        var gpDone = 0;
        for (var i = 0; i < world.GrandProjects.Count; i++)
        {
            var gp = world.GrandProjects[i];
            if (gp.HostNationId != n.Id && gp.PartnerNationId != n.Id) continue;
            if (gp.Status == GrandProjectStatus.Completed) gpDone++;
            else gpActive++;
        }
        var sheet = SovereignBalanceSheet.Read(world, n);
        var lang = Loc.Language;

        var ecoHc = new HashCode();
        ecoHc.Add(n.Id); ecoHc.Add(n.Treasury); ecoHc.Add(n.WeeklyBalance); ecoHc.Add(n.Gdp);
        ecoHc.Add(n.Population); ecoHc.Add(n.Inflation); ecoHc.Add(sheet.Principal); ecoHc.Add(sheet.Cash);
        ecoHc.Add(atWar); ecoHc.Add(runningSites); ecoHc.Add(siteSlots); ecoHc.Add(gpActive); ecoHc.Add(gpDone);
        ecoHc.Add(_selectedQuarter); ecoHc.Add(_focusedLineId, StringComparer.Ordinal);
        ecoHc.Add(_localEcoRev); ecoHc.Add(observer); ecoHc.Add(lang, StringComparer.Ordinal);
        ecoHc.Add(world.ElapsedDays); ecoHc.Add(world.MapRevision); ecoHc.Add(world.WarRevision);
        foreach (var (_, node) in _lineNodes) ecoHc.Add(BudgetCore.RateOf(n, node.Line));
        var ecoSig = ecoHc.ToHashCode();
        if (_hasEcoSig && _lastEcoSig == ecoSig) return;
        _hasEcoSig = true;
        _lastEcoSig = ecoSig;

        var gdpPerCapita = ShellCornerTelemetry.FormatGdpPerCapita(n.Gdp, n.Population);
        var baseSubtitle = Loc.Format("shell.aaa.eco_sub_base_fmt",
            Fmt.Money(n.Gdp), Fmt.People(n.Population), gdpPerCapita, runningSites, siteSlots);
        if (_subtitleLabel.Text != baseSubtitle) _subtitleLabel.Text = baseSubtitle;
        _subtitleLabel.TooltipText = Ui.Tip(baseSubtitle);

        _syncingSliders = true;
        var revTotal = 0f;
        var expTotal = 0f;
        _expSharesScratch.Clear();

        var isObsNow = ObserverUiRules.IsObserving(_ctx.World, _ctx.IsObserverMode?.Invoke() ?? false);
        if (_zeroBtn is not null) _zeroBtn.Disabled = isObsNow;
        foreach (var (id, node) in _lineNodes)
        {
            var rate = BudgetCore.RateOf(n, node.Line);
            if (rate > node.Slider.MaxValue) node.Slider.MaxValue = rate;
            node.Slider.SetValueNoSignal(rate);
            node.Slider.Editable = !isObsNow;
            foreach (var child in node.QuickBar.GetChildren())
                if (child is Button qb) qb.Disabled = isObsNow;

            var amt = BudgetCore.Amount(n, node.Line, weeklyOut, atWar);
            node.RateLabel.Text = $"{(rate * 100f):0.#}%";
            node.MoneyLabel.Text = Fmt.Money(amt);
            var focused = _focusedLineId == id;
            if (node.QuickBar.Visible != focused) node.QuickBar.Visible = focused;
            node.NameBtn.Text = (focused ? "▾ " : "▸ ") + Loc.Get(node.Line.Name);
            node.NameBtn.AddThemeColorOverride("font_color", Ui.Colour(focused ? "shell.paper" : "shell.paper_muted"));

            if (node.Line.Side == BudgetSide.Revenue) revTotal += amt;
            else { expTotal += amt; if (amt > 0.0001f) _expSharesScratch.Add((Loc.Get(node.Line.Name), amt)); }
        }
        _syncingSliders = false;

        _revSumLabel.Text = Fmt.Money(revTotal);
        _expSumLabel.Text = Fmt.Money(expTotal);
        ShellEconomyCharts.RefreshTaxEfficiencyBanner(_ctx, _taxEfficiencyBanner, n, weeklyOut, atWar);

        var bal = revTotal - expTotal;
        var debtPct = (sheet.Principal / MathF.Max(0.01f, n.Gdp)) * 100f;
        var inflPct = n.Inflation * 100f;

        ShellEconomyCharts.RefreshFiscalSummaryRibbon(_ribbon, n, sheet, revTotal, expTotal, bal, debtPct, inflPct);
        ShellEconomyCharts.RefreshBaseMetricsStrip(_baseStrip, world, n);

        _balanceBigLabel.Text = Fmt.MoneyDelta(bal);
        _balanceBigLabel.AddThemeColorOverride("font_color", Ui.Colour(bal < -0.0005f ? "shell.state.negative_text" : "shell.state.positive"));
        _balanceMeter.QueueRedraw();

        var yrCash = n.Treasury + bal * 52f;
        _forecastNoteLabel.Text = Loc.Format("shell.year_budget_forecast_fmt", Fmt.Money(MathF.Max(0f, yrCash)), $"{debtPct:0.1}%", $"{inflPct:0.1}%");

        _gpCardStatusLabel.Text = gpActive + gpDone > 0
            ? Loc.Format("shell.ws.eco.gp_cta_active_fmt", gpActive, gpDone)
            : Loc.Get("shell.ws.eco.gp_cta_none");

        ShellEconomyCharts.PopulateSupplyShortages(_ctx, _shortagesBox, n, () => { Close(); _ctx.OpenScreen?.Invoke("Trade"); });
        ShellEconomyCharts.PopulateSpendingStack(_ctx, _spendingStackBar, _expSharesScratch, expTotal);

        _debtSummaryLabel.Text = $"{Fmt.Money(sheet.Principal)} · {debtPct:0.1}% {Loc.Get("GDP")}";
        var maxRepay = MathF.Max(0f, MathF.Min(sheet.Cash, sheet.Principal));
        var chosenAmt = maxRepay * ((_selectedQuarter + 1) * 0.25f);
        _repayAllBtn.Text = $"{Loc.Get("Repay all we can")} · {Fmt.Money(chosenAmt)}";
        _repayAllBtn.Disabled = chosenAmt < 0.01f || observer;
        _repayAmountField.Text = Fmt.Money(chosenAmt);
        _repayPreviewLabel.Text = Loc.Format("shell.after_repay_fmt",
            Fmt.Money(MathF.Max(0f, sheet.Cash - chosenAmt)),
            $"{((MathF.Max(0f, sheet.Principal - chosenAmt) / MathF.Max(0.01f, n.Gdp)) * 100f):0.1}%");

        var chipNorm = _ctx.NineSlice("button_umber_9s", 8, 5, 2, 5, 2);
        var chipOn = _ctx.PressedWellStyle(underlineBottom: true, padX: 5, padY: 2);
        for (var i = 0; i < 4; i++)
        {
            var on = i == _selectedQuarter;
            _repayQuarterBtns[i].AddThemeStyleboxOverride("normal", on ? chipOn : chipNorm);
            _repayQuarterBtns[i].AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
        }

        _treasuryChart.TooltipText = Ui.Tip(Loc.Format("shell.ws.eco.chart_tip_fmt",
            Fmt.Money(n.Treasury), Fmt.MoneyDelta(bal), Fmt.Money(-MathF.Max(0.2f, sheet.NewCredit))));
        _treasuryChart.QueueRedraw();
        ShellEconomyProductionMethods.Refresh(_ctx, _productionMethodsBox, world, n, () => { _localEcoRev++; Refresh(); _ctx.RefreshAll?.Invoke(); });
    }

    private void ExecuteQuickDebtRepay()
    {
        if (ObserverUiRules.IsObserving(_ctx.World, _ctx.IsObserverMode?.Invoke() ?? false)) return;
        if (_ctx.World?.Player is not { } n) return;
        var sheet = SovereignBalanceSheet.Read(_ctx.World, n);
        var maxRepay = MathF.Max(0f, MathF.Min(sheet.Cash, sheet.Principal));
        var amt = MathF.Floor(maxRepay * ((_selectedQuarter + 1) * 0.25f) * 100f) / 100f;
        if (amt < 0.01f) return;

        _ctx.PlayCue?.Invoke("save_complete");
        _ctx.World.Commands.Enqueue(new ManageDebtCommand { IssuerId = n.Id, Action = "repay", Amount = amt });
        _ctx.World.Commands.ApplyAll(_ctx.World);
        EconomySystem.Explain(_ctx.World);
        _feedbackLabel.Visible = true;
        _feedbackLabel.Text = Loc.Get("Financial operation completed. Budget balance excludes bond proceeds and principal repayments.");
        Refresh();
        _ctx.RefreshAll?.Invoke();
    }
}
