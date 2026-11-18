using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Full-screen 5-tab "Экономика" (Economy) workspace matching state-5.jpg (Revision 3):
/// docked inside FullWorkspace so the L-corner, Date Plate, and compact Bottom Strip remain active;
/// two-column Revenue & Spending sliders with inline quick presets, "Куда идут деньги" stacked
/// spending share bar, 6-month ruled treasury trajectory chart, right-column Balance & Debt quick
/// repayment controls, and brass-rimmed "Свести в ноль" primary CTA.
/// </summary>
internal sealed class ShellEconomyWorkspaceView
{
    private readonly ShellContext _ctx;

    private Control _layer;
    private PanelContainer _panel;
    private Label _subtitleLabel;
    private readonly Button[] _tabButtons = new Button[5];

    private VBoxContainer _revRows;
    private VBoxContainer _expRows;
    private Label _revSumLabel;
    private Label _expSumLabel;
    private HBoxContainer _spendingStackBar;
    private Control _treasuryChart;

    private Label _balanceBigLabel;
    private Control _balanceMeter;
    private Label _forecastNoteLabel;

    private Label _debtSummaryLabel;
    private Button _repayAllBtn;
    private readonly Button[] _repayQuarterBtns = new Button[4];
    private Label _repayAmountField;
    private Label _repayPreviewLabel;

    private Label _feedbackLabel;

    private int _selectedQuarter = 3; // 0..3 -> 25..100%
    private string _focusedLineId = "";
    private bool _syncingSliders;
    private ShellLayoutProfile _profile;

    private readonly Dictionary<string, BudgetRowNodes> _lineNodes = new(StringComparer.OrdinalIgnoreCase);

    private sealed class BudgetRowNodes
    {
        public BudgetLine Line;
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
        _layer = new Control
        {
            Name = "ShellEconomyWorkspaceLayer",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 25
        };
        _layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _ctx.Root.AddChild(_layer);

        // Soft map dim behind the workspace (MouseFilter Stop only inside the workspace rect / map area)
        var dim = new ColorRect
        {
            Color = Ui.Colour("shell.well", 0.45f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(dim);

        _panel = new PanelContainer
        {
            Name = "ShellEconomyWorkspacePanel",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _panel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_brass_9s", 16, 18, 12, 18, 12, "shell.body_top", "shell.brass_hi", 3));
        _layer.AddChild(_panel);

        var rootCol = new VBoxContainer();
        rootCol.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(rootCol);

        BuildHeader(rootCol);
        BuildBody(rootCol);
        BuildFooter(rootCol);
    }

    private void BuildHeader(VBoxContainer parent)
    {
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 12);
        parent.AddChild(head);

        var emb = new PanelContainer { CustomMinimumSize = new Vector2(46, 46), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        emb.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("round_bezel", 0, 9, 9, 9, 9, "shell.plate_top", "shell.brass", 24));
        var embIco = new TextureRect
        {
            Texture = _ctx.Icon("icon_economy"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.brass_hi")
        };
        emb.AddChild(embIco);
        head.AddChild(emb);

        var titleCol = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        titleCol.AddThemeConstantOverride("separation", 1);
        head.AddChild(titleCol);

        var h2 = new Label { Text = Loc.Get("Economy") };
        h2.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        h2.AddThemeFontSizeOverride("font_size", 24);
        h2.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        titleCol.AddChild(h2);

        _subtitleLabel = new Label { Text = Loc.Get("shell.budget_weekly_sub") };
        _subtitleLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _subtitleLabel.AddThemeFontSizeOverride("font_size", 12);
        _subtitleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        titleCol.AddChild(_subtitleLabel);

        // 5 engraved folder tabs matching state-5.jpg: Бюджет | Долг | Рынок | Стройка | Центробанк
        var tabs = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkEnd };
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

        for (var i = 0; i < tabSpecs.Length; i++)
        {
            var act = tabSpecs[i].Act;
            var on = i == 0;
            var b = new Button
            {
                Text = Loc.Get(tabSpecs[i].Key),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            b.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            b.AddThemeFontSizeOverride("font_size", 14);
            b.AddThemeStyleboxOverride("normal", on
                ? _ctx.NineSlice("tab_folder_9s", 10, 10, 5, 10, 5)
                : _ctx.PressedWellStyle(padX: 10, padY: 5));
            b.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
            b.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); act(); };
            tabs.AddChild(b);
            _tabButtons[i] = b;
        }

        var closeBtn = new Button
        {
            Text = "×",
            CustomMinimumSize = new Vector2(34, 34),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        closeBtn.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        closeBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        closeBtn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); Close(); };
        head.AddChild(closeBtn);
    }

    private void BuildBody(VBoxContainer parent)
    {
        var bodyBox = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var bodyStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.body_bottom"),
            BorderColor = new Color(0f, 0f, 0f, 0.6f),
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 12,
            ContentMarginBottom = 12
        };
        bodyStyle.SetBorderWidthAll(1);
        bodyBox.AddThemeStyleboxOverride("panel", bodyStyle);
        parent.AddChild(bodyBox);

        var mainSplit = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        mainSplit.AddThemeConstantOverride("separation", 22);
        bodyBox.AddChild(mainSplit);

        // Left area: 2-column Revenue / Spending + Spending Stack + 6-Month Treasury Chart
        var leftScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 2.6f,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        mainSplit.AddChild(leftScroll);

        var leftCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        leftCol.AddThemeConstantOverride("separation", 12);
        leftScroll.AddChild(leftCol);

        var twoCols = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        twoCols.AddThemeConstantOverride("separation", 20);
        leftCol.AddChild(twoCols);

        var revCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        revCol.AddThemeConstantOverride("separation", 2);
        revCol.AddChild(BuildSectionRule(Loc.Get("REVENUE"), out _revSumLabel));
        _revRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _revRows.AddThemeConstantOverride("separation", 2);
        revCol.AddChild(_revRows);
        twoCols.AddChild(revCol);

        var expCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        expCol.AddThemeConstantOverride("separation", 2);
        expCol.AddChild(BuildSectionRule(Loc.Get("SPENDING"), out _expSumLabel));
        _expRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _expRows.AddThemeConstantOverride("separation", 2);
        expCol.AddChild(_expRows);
        twoCols.AddChild(expCol);

        foreach (var line in BudgetCore.PlayerLines)
            BuildBudgetSliderRow(line);

        // Proportional spending share bar: "Куда идут деньги"
        leftCol.AddChild(BuildSectionRule(Loc.Get("shell.where_money_goes"), out var shareRight));
        shareRight.Text = Loc.Get("shell.share_of_spending");

        _spendingStackBar = new HBoxContainer { CustomMinimumSize = new Vector2(0, 42), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _spendingStackBar.AddThemeConstantOverride("separation", 2);
        leftCol.AddChild(_spendingStackBar);

        // Ruled 6-month treasury trajectory chart
        leftCol.AddChild(BuildSectionRule(Loc.Get("shell.six_month_treasury"), out var chartRight));
        chartRight.Text = Loc.Get("shell.unit_billion");

        _treasuryChart = new Control
        {
            CustomMinimumSize = new Vector2(0, 128),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _treasuryChart.Draw += () => ShellEconomyCharts.DrawTreasuryChart(_treasuryChart, _ctx.World, _ctx.World?.Player);
        leftCol.AddChild(_treasuryChart);

        // Right summary column: Balance + Debt quick repayment
        var rightCol = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(340, 0),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.0f
        };
        rightCol.AddThemeConstantOverride("separation", 12);
        mainSplit.AddChild(rightCol);

        BuildRightSummaryColumn(rightCol);
    }

    private void BuildBudgetSliderRow(BudgetLine line)
    {
        var parent = line.Side == BudgetSide.Revenue ? _revRows : _expRows;
        var wrap = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        wrap.AddThemeConstantOverride("separation", 2);

        var rowPanel = new PanelContainer { CustomMinimumSize = new Vector2(0, 34) };
        var rowStyle = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            BorderColor = Ui.Colour("shell.paper", 0.07f),
            BorderWidthBottom = 1,
            ContentMarginLeft = 4,
            ContentMarginRight = 4
        };
        rowPanel.AddThemeStyleboxOverride("panel", rowStyle);
        wrap.AddChild(rowPanel);

        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        rowPanel.AddChild(h);

        var nameBtn = new Button
        {
            Text = Loc.Get(line.Name),
            CustomMinimumSize = new Vector2(148, 0),
            Alignment = HorizontalAlignment.Left,
            ClipText = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        nameBtn.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        nameBtn.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
        nameBtn.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        nameBtn.AddThemeFontSizeOverride("font_size", 12);
        nameBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        var lineId = line.Id;
        nameBtn.Pressed += () =>
        {
            _focusedLineId = _focusedLineId == lineId ? "" : lineId;
            Refresh();
        };
        h.AddChild(nameBtn);

        var slider = new HSlider
        {
            MinValue = line.Minimum,
            MaxValue = line.Maximum,
            Step = 0.005,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(96, 20),
            Scrollable = false
        };
        slider.ValueChanged += val => OnRateChanged(lineId, (float)val);
        h.AddChild(slider);

        var rateLbl = new Label
        {
            CustomMinimumSize = new Vector2(56, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        rateLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        rateLbl.AddThemeFontSizeOverride("font_size", 13);
        rateLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        h.AddChild(rateLbl);

        var moneyLbl = new Label
        {
            CustomMinimumSize = new Vector2(68, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        moneyLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        moneyLbl.AddThemeFontSizeOverride("font_size", 12);
        moneyLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        h.AddChild(moneyLbl);

        // Inline quick-preset row shown when this budget row is focused (matching state-5.jpg .bquick)
        var quick = new HBoxContainer { Visible = false };
        quick.AddThemeConstantOverride("separation", 4);
        AddQuickDeltaChip(quick, "−5%", () => StepRate(line, -0.05f));
        AddQuickDeltaChip(quick, "+5%", () => StepRate(line, +0.05f));
        AddQuickDeltaChip(quick, "25%", () => SetFractionRate(line, 0.25f));
        AddQuickDeltaChip(quick, "50%", () => SetFractionRate(line, 0.50f));
        AddQuickDeltaChip(quick, "75%", () => SetFractionRate(line, 0.75f));
        wrap.AddChild(quick);

        parent.AddChild(wrap);
        _lineNodes[line.Id] = new BudgetRowNodes
        {
            Line = line,
            Slider = slider,
            RateLabel = rateLbl,
            MoneyLabel = moneyLbl,
            QuickBar = quick
        };
    }

    private void AddQuickDeltaChip(HBoxContainer parent, string label, Action onClick)
    {
        var b = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(44, 23),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        b.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_umber_9s", 8, 6, 2, 6, 2));
        b.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        b.AddThemeFontSizeOverride("font_size", 11);
        b.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        b.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };
        parent.AddChild(b);
    }

    private void StepRate(BudgetLine line, float delta)
    {
        if (_ctx.World?.Player is not { } n) return;
        var next = Math.Clamp(BudgetCore.RateOf(n, line) + delta, line.Minimum, line.Maximum);
        OnRateChanged(line.Id, next);
    }

    private void SetFractionRate(BudgetLine line, float frac)
    {
        var next = line.Minimum + (line.Maximum - line.Minimum) * frac;
        OnRateChanged(line.Id, next);
    }

    private void OnRateChanged(string lineId, float value)
    {
        if (_syncingSliders || _ctx.World?.Player is not { } n) return;
        _ctx.World.Commands.Enqueue(new SetPolicyRateCommand
        {
            IssuerId = n.Id,
            LineId = lineId,
            Value = value
        });
        _ctx.World.Commands.ApplyAll(_ctx.World);
        Refresh();
        _ctx.RefreshAll?.Invoke();
    }

    private void BuildRightSummaryColumn(VBoxContainer rightCol)
    {
        rightCol.AddChild(BuildSectionRule(Loc.Get("Balance"), out _));

        _balanceBigLabel = new Label { Text = "$0" };
        _balanceBigLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        _balanceBigLabel.AddThemeFontSizeOverride("font_size", 28);
        rightCol.AddChild(_balanceBigLabel);

        // Double ledger underline below total balance figure
        var dblRule = new Control { CustomMinimumSize = new Vector2(0, 5) };
        dblRule.Draw += () =>
        {
            var w = Math.Min(180f, dblRule.Size.X);
            dblRule.DrawLine(new Vector2(0, 0), new Vector2(w, 0), Ui.Colour("shell.paper_muted"), 1f);
            dblRule.DrawLine(new Vector2(0, 3), new Vector2(w, 3), Ui.Colour("shell.paper_muted"), 1f);
        };
        rightCol.AddChild(dblRule);

        _balanceMeter = new Control { CustomMinimumSize = new Vector2(0, 10) };
        _balanceMeter.Draw += () => ShellEconomyCharts.DrawBalanceMeter(_balanceMeter, _ctx.World?.Player);
        rightCol.AddChild(_balanceMeter);

        _forecastNoteLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _forecastNoteLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _forecastNoteLabel.AddThemeFontSizeOverride("font_size", 12);
        _forecastNoteLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        rightCol.AddChild(_forecastNoteLabel);

        rightCol.AddChild(BuildSectionRule(Loc.Get("shell.tab_debt"), out _debtSummaryLabel));

        _repayAllBtn = new Button
        {
            Text = Loc.Get("Repay all we can"),
            CustomMinimumSize = new Vector2(0, 36),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _repayAllBtn.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_umber_9s", 10, 10, 4, 10, 4));
        _repayAllBtn.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _repayAllBtn.AddThemeFontSizeOverride("font_size", 13);
        _repayAllBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        _repayAllBtn.Pressed += ExecuteQuickDebtRepay;
        rightCol.AddChild(_repayAllBtn);

        var qRow = new HBoxContainer();
        qRow.AddThemeConstantOverride("separation", 5);
        rightCol.AddChild(qRow);

        for (var i = 0; i < 4; i++)
        {
            var idx = i;
            var qb = new Button
            {
                Text = $"{(i + 1) * 25}%",
                CustomMinimumSize = new Vector2(48, 26),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            qb.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            qb.AddThemeFontSizeOverride("font_size", 11);
            qb.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); _selectedQuarter = idx; Refresh(); };
            qRow.AddChild(qb);
            _repayQuarterBtns[i] = qb;
        }

        qRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var amtWell = new PanelContainer { CustomMinimumSize = new Vector2(92, 26) };
        amtWell.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("well_recessed_9s", 8, 6, 2, 6, 2, "shell.well_top", "shell.engrave", 2));
        _repayAmountField = new Label { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _repayAmountField.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        _repayAmountField.AddThemeFontSizeOverride("font_size", 12);
        _repayAmountField.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        amtWell.AddChild(_repayAmountField);
        qRow.AddChild(amtWell);

        _repayPreviewLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _repayPreviewLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _repayPreviewLabel.AddThemeFontSizeOverride("font_size", 12);
        _repayPreviewLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        rightCol.AddChild(_repayPreviewLabel);

        _feedbackLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _feedbackLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _feedbackLabel.AddThemeFontSizeOverride("font_size", 12);
        _feedbackLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.state.positive"));
        rightCol.AddChild(_feedbackLabel);
    }

    private void BuildFooter(VBoxContainer parent)
    {
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 8);
        parent.AddChild(foot);

        var openGov = CreateFooterButton(Loc.Get("State Affairs"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("State Affairs"); });
        var openDebt = CreateFooterButton(Loc.Get("Sovereign finance"), primary: false, () => { Close(); _ctx.OpenSovereignFinance?.Invoke(); });
        foot.AddChild(openGov);
        foot.AddChild(openDebt);

        foot.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var zeroBtn = CreateFooterButton(Loc.Get("shell.balance_to_zero"), primary: true, () =>
        {
            var adjustTaxes = Input.IsKeyPressed(Key.Shift);
            _ctx.ExecuteZeroBudget?.Invoke(adjustTaxes);
            Refresh();
            _ctx.RefreshAll?.Invoke();
        });
        zeroBtn.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
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
            Text = text,
            CustomMinimumSize = new Vector2(primary ? 172 : 132, primary ? 38 : 34),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        var st = primary
            ? _ctx.NineSlice("button_brass_cta_9s", 10, 16, 5, 16, 5, "shell.plate_top", "shell.brass_hi", 3)
            : _ctx.NineSlice("button_umber_9s", 10, 12, 4, 12, 4, "shell.plate_bottom", "shell.engrave", 3);
        b.AddThemeStyleboxOverride("normal", st);
        b.AddThemeStyleboxOverride("hover", st);
        b.AddThemeFontOverride("font", _ctx.Font(primary ? "shell.serif_semibold" : "shell.sans"));
        b.AddThemeFontSizeOverride("font_size", primary ? 15 : 13);
        b.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        b.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };
        return b;
    }

    private HBoxContainer BuildSectionRule(string title, out Label rightLabel)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        var l = new Label { Text = title };
        l.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        l.AddThemeFontSizeOverride("font_size", 14);
        l.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        h.AddChild(l);

        var line = new ColorRect
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(12, 1),
            Color = Ui.Colour("shell.engrave")
        };
        h.AddChild(line);

        rightLabel = new Label();
        rightLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        rightLabel.AddThemeFontSizeOverride("font_size", 12);
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
    }

    public void Refresh()
    {
        if (!IsVisible || _ctx.World?.Player is not { } n) return;
        var world = _ctx.World;
        var atWar = world.Wars.Any(w => !w.Ended && w.Involves(n.Id));
        var weeklyOut = n.Gdp / 52f;

        _syncingSliders = true;
        var revTotal = 0f;
        var expTotal = 0f;
        var expShares = new List<(string Name, float Amount)>();

        foreach (var (id, node) in _lineNodes)
        {
            var rate = BudgetCore.RateOf(n, node.Line);
            if (rate > node.Slider.MaxValue) node.Slider.MaxValue = rate;
            node.Slider.SetValueNoSignal(rate);

            var amt = BudgetCore.Amount(n, node.Line, weeklyOut, atWar);
            node.RateLabel.Text = $"{(rate * 100f):0.#}%";
            node.MoneyLabel.Text = Fmt.Money(amt);
            node.QuickBar.Visible = _focusedLineId == id;

            if (node.Line.Side == BudgetSide.Revenue) revTotal += amt;
            else
            {
                expTotal += amt;
                if (amt > 0.0001f) expShares.Add((Loc.Get(node.Line.Name), amt));
            }
        }
        _syncingSliders = false;

        _revSumLabel.Text = Fmt.Money(revTotal);
        _expSumLabel.Text = Fmt.Money(expTotal);

        var bal = revTotal - expTotal;
        _balanceBigLabel.Text = Fmt.MoneyDelta(bal);
        _balanceBigLabel.AddThemeColorOverride("font_color",
            Ui.Colour(bal < -0.0005f ? "shell.state.negative_text" : "shell.state.positive"));
        _balanceMeter.QueueRedraw();

        var yrCash = n.Treasury + bal * 52f;
        var sheet = SovereignBalanceSheet.Read(world, n);
        var debtPct = (sheet.Principal / MathF.Max(0.01f, n.Gdp)) * 100f;
        _forecastNoteLabel.Text = Loc.Format("shell.year_budget_forecast_fmt",
            Fmt.Money(MathF.Max(0f, yrCash)), $"{debtPct:0.1}%", $"{(n.Inflation * 100f):0.1}%");

        ShellEconomyCharts.PopulateSpendingStack(_ctx, _spendingStackBar, expShares, expTotal);

        // Debt quick repayment controls
        _debtSummaryLabel.Text = $"{Fmt.Money(sheet.Principal)} · {debtPct:0.1}% {Loc.Get("GDP")}";
        var maxRepay = MathF.Max(0f, MathF.Min(sheet.Cash, sheet.Principal));
        var chosenAmt = maxRepay * ((_selectedQuarter + 1) * 0.25f);
        _repayAllBtn.Text = $"{Loc.Get("Repay all we can")} · {Fmt.Money(chosenAmt)}";
        _repayAllBtn.Disabled = chosenAmt < 0.01f || (_ctx.IsObserverMode?.Invoke() ?? false);
        _repayAmountField.Text = Fmt.Money(chosenAmt);
        _repayPreviewLabel.Text = Loc.Format("shell.after_repay_fmt",
            Fmt.Money(MathF.Max(0f, sheet.Cash - chosenAmt)),
            $"{((MathF.Max(0f, sheet.Principal - chosenAmt) / MathF.Max(0.01f, n.Gdp)) * 100f):0.1}%");

        var chipNorm = _ctx.NineSlice("button_umber_9s", 8, 6, 2, 6, 2);
        var chipOn = _ctx.PressedWellStyle(underlineBottom: true, padX: 6, padY: 2);
        for (var i = 0; i < 4; i++)
        {
            var on = i == _selectedQuarter;
            _repayQuarterBtns[i].AddThemeStyleboxOverride("normal", on ? chipOn : chipNorm);
            _repayQuarterBtns[i].AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
        }

        _treasuryChart.QueueRedraw();
    }

    private void ExecuteQuickDebtRepay()
    {
        if (_ctx.World?.Player is not { } n) return;
        var sheet = SovereignBalanceSheet.Read(_ctx.World, n);
        var maxRepay = MathF.Max(0f, MathF.Min(sheet.Cash, sheet.Principal));
        var amt = MathF.Floor(maxRepay * ((_selectedQuarter + 1) * 0.25f) * 100f) / 100f;
        if (amt < 0.01f) return;

        _ctx.PlayCue?.Invoke("save_complete");
        _ctx.World.Commands.Enqueue(new ManageDebtCommand
        {
            IssuerId = n.Id,
            Action = "repay",
            Amount = amt
        });
        _ctx.World.Commands.ApplyAll(_ctx.World);
        EconomySystem.Explain(_ctx.World);
        _feedbackLabel.Visible = true;
        _feedbackLabel.Text = Loc.Get("Financial operation completed. Budget balance excludes bond proceeds and principal repayments.");
        Refresh();
        _ctx.RefreshAll?.Invoke();
    }

}
