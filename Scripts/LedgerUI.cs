using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _ledgerLayer = null!;
    private Label _ledgerTitle = null!;
    private Label _ledgerSummary = null!;
    private Label _ledgerPolicyFeedback = null!;
    private Label _ledgerFiscalFeedback = null!;
    private HBoxContainer _ledgerFiscalControls = null!;
    private HBoxContainer _ledgerTradeControls = null!;
    private HBoxContainer _ledgerGovernmentControls = null!;
    private HBoxContainer _ledgerSocialControls = null!;
    private HSlider _ledgerTaxRate = null!;
    private HSlider _ledgerMilitarySpending = null!;
    private Label _ledgerTaxReading = null!;
    private Label _ledgerMilitaryReading = null!;
    private Label _ledgerBudgetLine = null!;
    private OptionButton _ledgerBuildingPicker = null!;
    private OptionButton _ledgerStatePicker = null!;
    private Label _ledgerConstructionQueue = null!;
    private int _ledgerStateListNationId = -1;
    private HSlider _ledgerWelfareSpending = null!;
    private HSlider _ledgerEducationSpending = null!;
    private Label _ledgerGovernanceFeedback = null!;
    private StrategicLineChart _ledgerLineA = null!;
    private StrategicLineChart _ledgerLineB = null!;
    private StrategicBarChart _ledgerBarsA = null!;
    private StrategicBarChart _ledgerBarsB = null!;
    private string _ledgerTab = "Overview";
    private int _ledgerViewedNationId = -1;
    private bool _updatingLedger;
    private readonly Dictionary<string, Button> _ledgerTabButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<TradePolicy, Button> _ledgerTradeButtons = new();
    private readonly Dictionary<GovernmentFocus, Button> _ledgerFocusButtons = new();

    // Properties rather than fields: a mod's skin is layered in after this class is first
    // touched, and a `static readonly` would have frozen the shipped palette before then.
    private static Color Gold => Ui.Colour("series.gold");
    private static Color Blue => Ui.Colour("series.blue");
    private static Color Green => Ui.Colour("series.green");
    private static Color Red => Ui.Colour("series.red");
    private static Color Violet => Ui.Colour("series.violet");
    private static Color Orange => Ui.Colour("series.orange");

    private void BuildLedgerInterface()
    {
        _ledgerLayer = FullLayer("NationalLedger");
        _ledgerLayer.Visible = false;
        _hudLayer.AddChild(_ledgerLayer);

        var shade = new ColorRect
        {
            Color = new Color(0.015f, 0.026f, 0.041f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _ledgerLayer.AddChild(shade);

        var panel = TexturedPanel(_ledgerLayer);
        Ui.Place(panel, "modal.ledger");

        var rows = PanelRows(panel, 22, 16, 9);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        _ledgerTitle = Title("NATIONAL LEDGER", 25, Ui.Colour("text.accent"));
        _ledgerTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_ledgerTitle);

        var playerView = ActionButton("Player nation", () =>
        {
            _ledgerViewedNationId = _campaign.Player.Id;
            RefreshLedger();
        }, 116, 32, true);
        playerView.TooltipText = "Return every chart to the country you currently control.";
        header.AddChild(playerView);
        var selectedView = ActionButton("Map selection", () =>
        {
            _ledgerViewedNationId = (_selectedNation ?? _campaign.Player).Id;
            RefreshLedger();
        }, 124, 32);
        selectedView.TooltipText = "Compare the country most recently selected on the map. Policies remain read-only for foreign countries.";
        header.AddChild(selectedView);
        header.AddChild(ActionButton("×", CloseLedger, 38, 32));

        var tabs = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        tabs.AddThemeConstantOverride("separation", 6);
        rows.AddChild(tabs);
        foreach (var tab in new[] { "Overview", "Government", "Economy", "Market", "Population", "Military", "Rankings" })
        {
            var captured = tab;
            var button = ActionButton(tab, () =>
            {
                _ledgerTab = captured;
                RefreshLedger();
            }, 116, 32, tab == "Overview");
            button.TooltipText = tab switch
            {
                "Overview" => "The country's long-term pulse and current national balance.",
                "Government" => "Set a cabinet priority and fund welfare and education. Every choice has a weekly cost and a systemic effect.",
                "Economy" => "Fiscal history, construction base and controls that directly affect revenue and stability.",
                "Market" => "Prices, shortages and trade policy. Policy changes apply at the next weekly market clearing.",
                "Population" => "Population growth, living standards, classes and the largest states.",
                "Military" => "Field strength, reserves, formations and active-war performance.",
                _ => "Compare this country with the largest powers in the current world."
            };
            tabs.AddChild(button);
            _ledgerTabButtons[tab] = button;
        }

        _ledgerSummary = Title("", 13, Ui.Colour("text.body_soft"));
        _ledgerSummary.CustomMinimumSize = new Vector2(0, 34);
        rows.AddChild(_ledgerSummary);

        BuildLedgerPolicyControls(rows);

        var grid = new GridContainer
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        grid.AddThemeConstantOverride("h_separation", 9);
        grid.AddThemeConstantOverride("v_separation", 9);
        rows.AddChild(grid);
        _ledgerLineA = new StrategicLineChart();
        _ledgerLineB = new StrategicLineChart();
        _ledgerBarsA = new StrategicBarChart();
        _ledgerBarsB = new StrategicBarChart();
        grid.AddChild(_ledgerLineA);
        grid.AddChild(_ledgerLineB);
        grid.AddChild(_ledgerBarsA);
        grid.AddChild(_ledgerBarsB);
    }

    /// <summary>
    /// A label for a horizontal row, which must not wrap.
    ///
    /// <see cref="Title"/> wraps by word, which is right in a column and catastrophic in a
    /// row: an HBoxContainer offers a label no width, so the label takes one character per
    /// line, becomes a thousand pixels tall, and drags the whole row down the screen with
    /// it. That is what the economy panel was doing - a sentence of hint text set
    /// vertically down the middle of the page, with the charts squeezed out below it.
    /// </summary>
    private static Label RowLabel(string text, int size, Color colour, float minimumWidth = 0f)
    {
        var label = Title(text, size, colour);
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.VerticalAlignment = VerticalAlignment.Center;
        if (minimumWidth > 0f) label.CustomMinimumSize = new Vector2(minimumWidth, 0);
        return label;
    }

    /// <summary>A caption, a slider, and a live reading of what moving it would do.</summary>
    private static HBoxContainer SliderRow(string caption, HSlider slider, out Label reading)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 9);
        row.AddChild(RowLabel(caption, 12, Ui.Colour("text.caption"), 104));
        slider.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        slider.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(slider);
        reading = RowLabel("", 12, Ui.Colour("text.body"), 172);
        reading.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(reading);
        return row;
    }

    /// <summary>
    /// The economy screen: what the state takes, what it spends, and what it is building.
    ///
    /// It held one slider and a wrapped sentence before. The construction half is the part
    /// the game was already promising - the strategic outliner says "open Economy to queue
    /// a project" and there was nothing on the screen to queue one with.
    /// </summary>
    private void BuildLedgerEconomyControls(VBoxContainer rows)
    {
        _ledgerFiscalControls = new HBoxContainer { Visible = false };
        _ledgerFiscalControls.AddThemeConstantOverride("separation", 22);
        rows.AddChild(_ledgerFiscalControls);

        // ------------------------------------------------------------------ the budget
        var budget = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        budget.AddThemeConstantOverride("separation", 4);
        _ledgerFiscalControls.AddChild(budget);
        budget.AddChild(RowLabel("BUDGET", 12, Ui.Colour("text.accent")));

        _ledgerTaxRate = new HSlider
        {
            MinValue = 0.05,
            MaxValue = 0.55,
            Step = 0.01,
            CustomMinimumSize = new Vector2(190, 26),
            TooltipText = "Higher taxes raise weekly revenue but slowly reduce living standards and private growth."
        };
        _ledgerTaxRate.ValueChanged += OnLedgerTaxChanged;
        budget.AddChild(SliderRow("TAXATION", _ledgerTaxRate, out _ledgerTaxReading));

        _ledgerMilitarySpending = new HSlider
        {
            MinValue = 0.0,
            MaxValue = 0.30,
            Step = 0.005,
            CustomMinimumSize = new Vector2(190, 26),
            TooltipText = "The share of output spent on the armed forces: reinforcement rate and readiness, " +
                          "against everything else the money could have done."
        };
        _ledgerMilitarySpending.ValueChanged += OnLedgerMilitaryChanged;
        budget.AddChild(SliderRow("ARMED FORCES", _ledgerMilitarySpending, out _ledgerMilitaryReading));

        _ledgerBudgetLine = RowLabel("", 12, Ui.Colour("text.body_soft"));
        budget.AddChild(_ledgerBudgetLine);
        _ledgerFiscalFeedback = RowLabel("", 11, Ui.Colour("text.accent_dim"));
        budget.AddChild(_ledgerFiscalFeedback);

        // ------------------------------------------------------------ what is being built
        var construction = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        construction.AddThemeConstantOverride("separation", 4);
        _ledgerFiscalControls.AddChild(construction);
        construction.AddChild(RowLabel("CONSTRUCTION", 12, Ui.Colour("text.accent")));

        var order = new HBoxContainer();
        order.AddThemeConstantOverride("separation", 7);
        construction.AddChild(order);

        _ledgerBuildingPicker = new OptionButton { CustomMinimumSize = new Vector2(230, 30) };
        foreach (var kind in BuildingCatalog.All)
        {
            var definition = BuildingCatalog.Get(kind);
            _ledgerBuildingPicker.AddItem($"{definition.Name} · {Fmt.Money(definition.Cost)}");
            _ledgerBuildingPicker.SetItemMetadata(_ledgerBuildingPicker.ItemCount - 1, (int)kind);
            _ledgerBuildingPicker.SetItemTooltip(_ledgerBuildingPicker.ItemCount - 1, definition.Description);
        }
        order.AddChild(_ledgerBuildingPicker);

        _ledgerStatePicker = new OptionButton { CustomMinimumSize = new Vector2(180, 30) };
        order.AddChild(_ledgerStatePicker);

        order.AddChild(ActionButton("Queue", OnLedgerQueueConstruction, 88, 30));

        _ledgerConstructionQueue = Title("", 11, Ui.Colour("text.body_soft"));
        _ledgerConstructionQueue.CustomMinimumSize = new Vector2(0, 36);
        construction.AddChild(_ledgerConstructionQueue);
    }

    private void BuildLedgerPolicyControls(VBoxContainer rows)
    {
        BuildLedgerEconomyControls(rows);

        _ledgerTradeControls = new HBoxContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _ledgerTradeControls.AddThemeConstantOverride("separation", 7);
        rows.AddChild(_ledgerTradeControls);
        var tradeCaption = Title("TRADE POLICY", 12, Ui.Colour("text.caption"));
        tradeCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        tradeCaption.CustomMinimumSize = new Vector2(98, 0);
        tradeCaption.VerticalAlignment = VerticalAlignment.Center;
        _ledgerTradeControls.AddChild(tradeCaption);
        AddTradePolicyButton("Open markets", TradePolicy.OpenMarkets,
            "More imports and exports, minimal tariffs; best access during peaceful global supply.");
        AddTradePolicyButton("Balanced", TradePolicy.BalancedTrade,
            "Moderate access and tariffs without a strong structural bias.");
        AddTradePolicyButton("Autarky", TradePolicy.StrategicAutarky,
            "Restricts trade and raises tariffs; safer politically, but shortages become harder to cover.");
        _ledgerPolicyFeedback = Title("", 11, Ui.Colour("text.accent_dim"));
        _ledgerPolicyFeedback.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _ledgerTradeControls.AddChild(_ledgerPolicyFeedback);

        _ledgerGovernmentControls = new HBoxContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _ledgerGovernmentControls.AddThemeConstantOverride("separation", 7);
        rows.AddChild(_ledgerGovernmentControls);
        var cabinetCaption = Title("CABINET", 12, Ui.Colour("text.caption"));
        cabinetCaption.CustomMinimumSize = new Vector2(78, 0);
        cabinetCaption.VerticalAlignment = VerticalAlignment.Center;
        _ledgerGovernmentControls.AddChild(cabinetCaption);
        AddGovernmentFocusButton("Balanced", GovernmentFocus.Balanced);
        AddGovernmentFocusButton("Growth", GovernmentFocus.EconomicExpansion);
        AddGovernmentFocusButton("Cohesion", GovernmentFocus.SocialCohesion);
        AddGovernmentFocusButton("Science", GovernmentFocus.EducationAndScience);
        AddGovernmentFocusButton("Security", GovernmentFocus.NationalSecurity);
        _ledgerGovernanceFeedback = Title("", 11, Ui.Colour("text.accent_dim"));
        _ledgerGovernanceFeedback.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _ledgerGovernmentControls.AddChild(_ledgerGovernanceFeedback);

        _ledgerSocialControls = new HBoxContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _ledgerSocialControls.AddThemeConstantOverride("separation", 9);
        rows.AddChild(_ledgerSocialControls);
        AddSocialSpendingSlider("WELFARE", "Public transfers improve regional living standards and reduce radicalism, but increase weekly expenses.",
            0.18f, out _ledgerWelfareSpending, OnLedgerWelfareChanged);
        AddSocialSpendingSlider("EDUCATION", "Education spending raises national literacy and the long-run productivity target every economic week.",
            0.15f, out _ledgerEducationSpending, OnLedgerEducationChanged);
    }

    private void AddTradePolicyButton(string caption, TradePolicy policy, string tooltip)
    {
        var button = ActionButton(caption, () => SetLedgerTradePolicy(policy), 114, 30,
            policy == TradePolicy.BalancedTrade);
        button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        button.TooltipText = tooltip;
        _ledgerTradeControls.AddChild(button);
        _ledgerTradeButtons[policy] = button;
    }

    private void AddGovernmentFocusButton(string caption, GovernmentFocus focus)
    {
        var button = ActionButton(caption, () => SetLedgerGovernmentFocus(focus), 94, 30,
            focus == GovernmentFocus.Balanced);
        button.TooltipText = GovernmentFocusRules.Description(focus);
        _ledgerGovernmentControls.AddChild(button);
        _ledgerFocusButtons[focus] = button;
    }

    private void AddSocialSpendingSlider(string caption, string tooltip, float maximum,
        out HSlider slider, Godot.Range.ValueChangedEventHandler handler)
    {
        var label = Title(caption, 11, Ui.Colour("text.caption"));
        label.CustomMinimumSize = new Vector2(80, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        _ledgerSocialControls.AddChild(label);
        slider = new HSlider
        {
            MinValue = 0,
            MaxValue = maximum,
            Step = 0.005,
            CustomMinimumSize = new Vector2(225, 28),
            TooltipText = tooltip
        };
        slider.ValueChanged += handler;
        _ledgerSocialControls.AddChild(slider);
    }

    private void OpenLedger()
    {
        _openScreen = "Ledger";
        _sidePanel.Visible = false;
        _ledgerViewedNationId = _ledgerViewedNationId < 0 ? _campaign.Player.Id : _ledgerViewedNationId;
        _ledgerLayer.Visible = true;
        // The ledger is a modal analysis workspace. It is authored before the
        // strategic outliner, so explicitly bring it above later HUD siblings.
        _ledgerLayer.MoveToFront();
        RefreshLedger();
    }

    private void CloseLedger()
    {
        _ledgerLayer.Visible = false;
        if (_openScreen == "Ledger") _openScreen = "";
    }

    private void OnLedgerTaxChanged(double value)
    {
        if (_updatingLedger || _campaign?.Player is null || ViewedLedgerNation()?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetTaxRateCommand { IssuerId = _campaign.Player.Id, Value = (float)value });
        _campaign.Commands.ApplyAll(_campaign);
        _ledgerFiscalFeedback.Text = $"Tax rate set to {value:P0}. Budget and growth update on the next economic week.";
        RefreshLedger();
    }

    private void OnLedgerMilitaryChanged(double value)
    {
        if (_updatingLedger || _campaign?.Player is null || ViewedLedgerNation()?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetMilitarySpendingCommand
        {
            IssuerId = _campaign.Player.Id, Value = (float)value
        });
        _campaign.Commands.ApplyAll(_campaign);
        _ledgerFiscalFeedback.Text = $"Armed forces set to {value:P1} of output. " +
                                     "Reinforcement and readiness follow from the next economic week.";
        RefreshLedger();
    }

    /// <summary>
    /// Order a building, and say why when it cannot be ordered.
    ///
    /// The command refuses silently on both of its conditions, which is right for the AI
    /// giving it eight times a day and wrong for a button: a control that does nothing and
    /// says nothing reads as broken. So the two refusals are spoken here.
    /// </summary>
    private void OnLedgerQueueConstruction()
    {
        var nation = _campaign?.Player;
        if (nation is null || ViewedLedgerNation()?.Id != nation.Id) return;
        if (_ledgerBuildingPicker.Selected < 0 || _ledgerStatePicker.Selected < 0)
        {
            _ledgerFiscalFeedback.Text = "Choose what to build and where.";
            return;
        }

        var kind = (BuildingKind)(int)_ledgerBuildingPicker.GetItemMetadata(_ledgerBuildingPicker.Selected);
        var stateMapIndex = (int)_ledgerStatePicker.GetItemMetadata(_ledgerStatePicker.Selected);
        var definition = BuildingCatalog.Get(kind);

        var running = _campaign.Construction.Count(project => project.NationId == nation.Id);
        if (running >= 12)
        {
            _ledgerFiscalFeedback.Text = $"Twelve projects are already under way. " +
                                         "Something has to finish before anything else starts.";
            return;
        }
        if (nation.Treasury < MathF.Min(0.05f, definition.Cost))
        {
            _ledgerFiscalFeedback.Text = $"The treasury holds {Fmt.Money(nation.Treasury)} and cannot open " +
                                         $"a {Fmt.Money(definition.Cost)} site.";
            return;
        }

        _campaign.Commands.Enqueue(new QueueConstructionCommand
        {
            IssuerId = nation.Id, StateMapIndex = stateMapIndex, Building = kind
        });
        _campaign.Commands.ApplyAll(_campaign);
        _ledgerFiscalFeedback.Text = $"{definition.Name} begun in " +
                                     $"{_ledgerStatePicker.GetItemText(_ledgerStatePicker.Selected)} · " +
                                     $"{Fmt.Money(definition.Cost)} over {definition.Days} days.";
        RefreshLedger();
    }

    /// <summary>
    /// The live half of the economy screen: what the sliders are worth, what the week
    /// costs, where the money can go and what is already being built.
    ///
    /// The readings come off the sliders rather than off the country, so dragging one
    /// answers immediately - the country itself only changes on the next economic week,
    /// and a control that shows last week's number while you move it is a control nobody
    /// trusts. The figures themselves are the simulation's own, not a second copy.
    /// </summary>
    private void RefreshLedgerEconomyControls(Nation nation)
    {
        var tax = (float)_ledgerTaxRate.Value;
        var military = (float)_ledgerMilitarySpending.Value;
        _ledgerTaxReading.Text = $"{tax:P0} · {Fmt.Money(EconomySystem.WeeklyTaxAt(nation, tax))} a week";
        _ledgerMilitaryReading.Text = $"{military:P1} · " +
                                      $"−{Fmt.Money(EconomySystem.WeeklyMilitaryAt(nation, military))} a week";

        // The budget is settled once an economic week, so on day zero there is no week to
        // report. Printing its zeroes as "balance +$0, outgoings $0" is a country with no
        // expenses, which is a stranger claim than saying nothing has happened yet.
        var balance = nation.WeeklyBalance;
        if (nation.WeeklyExpenses <= 0f)
        {
            _ledgerBudgetLine.Text = $"Treasury {Fmt.Money(nation.Treasury)}  ·  " +
                                     "no economic week has closed yet";
            _ledgerBudgetLine.AddThemeColorOverride("font_color", Ui.Colour("text.body_soft"));
        }
        else
        {
            _ledgerBudgetLine.Text = $"Treasury {Fmt.Money(nation.Treasury)}  ·  balance " +
                                     $"{(balance < 0f ? "−" : "+")}{Fmt.Money(MathF.Abs(balance))} a week  ·  " +
                                     $"income {Fmt.Money(nation.WeeklyTaxIncome + nation.WeeklyTariffIncome)}  ·  " +
                                     $"outgoings {Fmt.Money(nation.WeeklyExpenses)}";
            _ledgerBudgetLine.AddThemeColorOverride("font_color",
                balance < 0f ? Ui.Colour("state.negative") : Ui.Colour("state.positive"));
        }

        // The list of places only changes when the country does, and rebuilding a dropdown
        // under the player's cursor would throw away what they had picked.
        if (_ledgerStateListNationId != nation.Id)
        {
            _ledgerStateListNationId = nation.Id;
            _ledgerStatePicker.Clear();
            foreach (var state in _campaign.StatesOf(nation).OrderBy(state => state.Name, StringComparer.CurrentCulture))
            {
                _ledgerStatePicker.AddItem(state.Name);
                _ledgerStatePicker.SetItemMetadata(_ledgerStatePicker.ItemCount - 1, state.MapIndex);
            }
            if (_ledgerStatePicker.ItemCount > 0) _ledgerStatePicker.Selected = 0;
        }

        var projects = _campaign.Construction.Where(project => project.NationId == nation.Id).ToList();
        if (projects.Count == 0)
        {
            _ledgerConstructionQueue.Text = "Nothing under construction. Twelve sites may run at once.";
            return;
        }
        _ledgerConstructionQueue.Text = $"{projects.Count} of 12 sites running · " +
            string.Join("  ·  ", projects
                .OrderByDescending(project => project.Progress)
                .Take(4)
                .Select(project =>
                {
                    var place = _campaign.States.FirstOrDefault(state => state.MapIndex == project.StateMapIndex);
                    return $"{BuildingCatalog.Get(project.Building).Name} in {place?.Name ?? "?"} {project.Progress:P0}";
                }));
    }

    private void SetLedgerTradePolicy(TradePolicy policy)
    {
        if (_campaign?.Player is null || ViewedLedgerNation()?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetTradePolicyCommand { IssuerId = _campaign.Player.Id, Policy = policy });
        _campaign.Commands.ApplyAll(_campaign);
        _ledgerPolicyFeedback.Text = $"{TradePolicyRules.Name(policy)} selected · effective at the next market clearing.";
        RefreshLedger();
    }

    private void OnLedgerWelfareChanged(double value)
    {
        if (_updatingLedger || _campaign?.Player is null || ViewedLedgerNation()?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetWelfareSpendingCommand { IssuerId = _campaign.Player.Id, Value = (float)value });
        _campaign.Commands.ApplyAll(_campaign);
        _ledgerGovernanceFeedback.Text = $"Welfare set to {value:P1} of weekly output.";
        RefreshLedger();
    }

    private void OnLedgerEducationChanged(double value)
    {
        if (_updatingLedger || _campaign?.Player is null || ViewedLedgerNation()?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetEducationSpendingCommand { IssuerId = _campaign.Player.Id, Value = (float)value });
        _campaign.Commands.ApplyAll(_campaign);
        _ledgerGovernanceFeedback.Text = $"Education set to {value:P1} of weekly output.";
        RefreshLedger();
    }

    private void SetLedgerGovernmentFocus(GovernmentFocus focus)
    {
        if (_campaign?.Player is null || ViewedLedgerNation()?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetGovernmentFocusCommand { IssuerId = _campaign.Player.Id, Focus = focus });
        _campaign.Commands.ApplyAll(_campaign);
        _ledgerGovernanceFeedback.Text = $"{GovernmentFocusRules.Name(focus)} adopted.";
        RefreshLedger();
    }

    private Nation ViewedLedgerNation() => _campaign?.NationById(_ledgerViewedNationId) ?? _campaign?.Player;

    /// <summary>Active national modifiers, newest first, with how long each has left.</summary>
    private string DescribeModifiers(Nation nation)
    {
        if (nation.Modifiers.Count == 0) return "";

        var parts = nation.Modifiers
            .OrderByDescending(active => active.GrantedDay)
            .Select(active =>
            {
                var definition = ModifierCatalog.Get(active.Id);
                var label = definition?.Name ?? active.Id;
                if (active.IsPermanent) return label;
                var days = active.DaysRemaining(_campaign.ElapsedDays);
                return $"{label} ({days}d)";
            });

        return "In effect:  " + string.Join("   ·   ", parts);
    }

    private void RefreshLedger()
    {
        if (_campaign?.Player is null || _ledgerLayer is null || !_ledgerLayer.Visible) return;
        var nation = ViewedLedgerNation() ?? _campaign.Player;
        var samples = _campaign.History.ForNation(nation.Id);
        var days = samples.Select(sample => sample.Day).ToArray();
        var first = samples.Count > 0 ? samples[0] : default;
        var gdpTrend = samples.Count > 1 && MathF.Abs(first.Gdp) > 0.001f ? nation.Gdp / first.Gdp - 1f : 0f;
        var populationTrend = samples.Count > 1 && first.PopulationMillions > 0.001f
            ? nation.Population / 1_000_000f / first.PopulationMillions - 1f : 0f;
        _ledgerTitle.Text = $"NATIONAL LEDGER · {nation.Name.ToUpperInvariant()}";
        _ledgerSummary.Text = $"{_ledgerTab.ToUpperInvariant()}  ·  GDP {SignedPercent(gdpTrend)}  ·  population {SignedPercent(populationTrend)}  ·  " +
                              $"market access {nation.MarketAccess:P0}  ·  treasury {Fmt.Money(nation.Treasury)}  ·  " +
                              $"{samples.Count} weekly observation{(samples.Count == 1 ? "" : "s")}";

        // Named conditions belong where the player already reads the country's state -
        // a modifier nobody can see is a number changing for no visible reason.
        var spirits = DescribeModifiers(nation);
        if (!string.IsNullOrEmpty(spirits)) _ledgerSummary.Text += "\n" + spirits;

        var canChangePolicy = nation.Id == _campaign.Player.Id;
        _ledgerFiscalControls.Visible = _ledgerTab == "Economy" && canChangePolicy;
        _ledgerTradeControls.Visible = _ledgerTab == "Market" && canChangePolicy;
        _ledgerGovernmentControls.Visible = _ledgerTab == "Government" && canChangePolicy;
        _ledgerSocialControls.Visible = _ledgerTab == "Government" && canChangePolicy;
        _updatingLedger = true;
        _ledgerTaxRate.SetValueNoSignal(nation.TaxRate);
        _ledgerMilitarySpending.SetValueNoSignal(nation.MilitarySpending);
        _ledgerWelfareSpending.SetValueNoSignal(nation.WelfareSpending);
        _ledgerEducationSpending.SetValueNoSignal(nation.EducationSpending);
        _updatingLedger = false;
        if (_ledgerTab == "Economy") RefreshLedgerEconomyControls(nation);
        foreach (var (tab, button) in _ledgerTabButtons)
            ApplyLedgerButtonState(button, tab == _ledgerTab);
        foreach (var (policy, button) in _ledgerTradeButtons)
            ApplyLedgerButtonState(button, policy == nation.TradePolicy);
        foreach (var (focus, button) in _ledgerFocusButtons)
            ApplyLedgerButtonState(button, focus == nation.GovernmentFocus);
        if (_ledgerTab == "Government")
            _ledgerGovernanceFeedback.Text = $"{GovernmentFocusRules.Name(nation.GovernmentFocus)} · " +
                                             $"welfare {nation.WelfareSpending:P1} · education {nation.EducationSpending:P1}";
        if (!canChangePolicy && _ledgerTab is "Government" or "Economy" or "Market")
            _ledgerSummary.Text += "  ·  foreign policy is read-only";

        switch (_ledgerTab)
        {
            case "Economy": ShowLedgerEconomy(nation, samples, days); break;
            case "Government": ShowLedgerGovernment(nation, samples, days); break;
            case "Market": ShowLedgerMarket(nation, samples, days); break;
            case "Population": ShowLedgerPopulation(nation, samples, days); break;
            case "Military": ShowLedgerMilitary(nation, samples, days); break;
            case "Rankings": ShowLedgerRankings(nation, samples, days); break;
            default: ShowLedgerOverview(nation, samples, days); break;
        }
    }

    private void ShowLedgerOverview(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Economic power", "GDP and treasury, billions", days, new[]
        {
            Series("GDP", Gold, samples, sample => sample.Gdp),
            Series("Treasury", Green, samples, sample => sample.Treasury)
        }, StrategicChartFormat.MoneyBillions);
        _ledgerLineB.SetData("Population", "Recorded every simulation week", days,
            new[] { Series("Population", Blue, samples, sample => sample.PopulationMillions) }, StrategicChartFormat.Millions);
        _ledgerBarsA.SetData("National pulse", "Hover any bar for its current value", new[]
        {
            new StrategicBarEntry("Stability", nation.Stability, Green),
            new StrategicBarEntry("Living standard", nation.StandardOfLiving, Gold),
            new StrategicBarEntry("Literacy", nation.Literacy, Blue),
            new StrategicBarEntry("Market access", nation.MarketAccess, Violet),
            new StrategicBarEntry("War support", CurrentWarSupport(nation), Red)
        }, StrategicChartFormat.Percent);
        _ledgerBarsB.SetData("Weekly public balance", "Every value affects the treasury", new[]
        {
            new StrategicBarEntry("Tax revenue", nation.WeeklyTaxIncome, Green),
            new StrategicBarEntry("Tariffs", nation.WeeklyTariffIncome, Blue),
            new StrategicBarEntry("Trade balance", nation.WeeklyTradeBalance, Violet),
            new StrategicBarEntry("Expenses", -nation.WeeklyExpenses, Red),
            new StrategicBarEntry("Net balance", nation.WeeklyBalance, nation.WeeklyBalance >= 0 ? Gold : Red)
        }, StrategicChartFormat.MoneyBillions);
    }

    private void ShowLedgerEconomy(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Gross domestic product", "Policy, construction, war and shortages alter this curve", days,
            new[] { Series("GDP", Gold, samples, sample => sample.Gdp) }, StrategicChartFormat.MoneyBillions);
        _ledgerLineB.SetData("Weekly budget", "Revenue, expenses and resulting balance", days, new[]
        {
            Series("Income", Green, samples, sample => sample.WeeklyIncome),
            Series("Expenses", Red, samples, sample => sample.WeeklyExpenses),
            Series("Balance", Blue, samples, sample => sample.WeeklyBalance)
        }, StrategicChartFormat.MoneyBillions);
        _ledgerBarsA.SetData("Fiscal controls", $"Tax {nation.TaxRate:P0} · military {nation.MilitarySpending:P0}", new[]
        {
            new StrategicBarEntry("Tax rate", nation.TaxRate, Gold, "Raises public revenue; excessive taxation weighs on private growth."),
            new StrategicBarEntry("Military share", nation.MilitarySpending, Red, "Improves reinforcement and fleet readiness but increases expenditure."),
            new StrategicBarEntry("Stability", nation.Stability, Green),
            new StrategicBarEntry("Productivity", nation.Productivity / 1.5f, Blue, $"Raw productivity {nation.Productivity:0.00}")
        }, StrategicChartFormat.Percent);
        var states = _campaign.StatesOf(nation);
        _ledgerBarsB.SetData("National building base", "Completed buildings by type", Enum.GetValues<BuildingKind>()
            .Select(kind => new StrategicBarEntry(BuildingCatalog.Get(kind).Name,
                states.Sum(state => state.Buildings.GetValueOrDefault(kind)), BuildingColor(kind))).ToArray(),
            StrategicChartFormat.Number);
    }

    private void ShowLedgerGovernment(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Social development", "Policy effects accumulate at each weekly economic tick", days, new[]
        {
            Series("Living standard", Gold, samples, sample => sample.StandardOfLiving),
            Series("Literacy", Blue, samples, sample => sample.Literacy),
            Series("Stability", Green, samples, sample => sample.Stability)
        }, StrategicChartFormat.Percent);
        _ledgerLineB.SetData("Public finances", "Social programmes compete with defence and construction for money", days, new[]
        {
            Series("Income", Green, samples, sample => sample.WeeklyIncome),
            Series("Expenses", Red, samples, sample => sample.WeeklyExpenses),
            Series("Balance", Blue, samples, sample => sample.WeeklyBalance)
        }, StrategicChartFormat.MoneyBillions);
        _ledgerBarsA.SetData("Cabinet direction", GovernmentFocusRules.Description(nation.GovernmentFocus), new[]
        {
            new StrategicBarEntry("Stability", nation.Stability, Green),
            new StrategicBarEntry("Living standard", nation.StandardOfLiving, Gold),
            new StrategicBarEntry("Literacy", nation.Literacy, Blue),
            new StrategicBarEntry("Productivity", nation.Productivity / 1.5f, Violet, $"Raw productivity {nation.Productivity:0.00}"),
            new StrategicBarEntry("War support", CurrentWarSupport(nation), Red)
        }, StrategicChartFormat.Percent);
        _ledgerBarsB.SetData("Weekly allocation", "Shares of total national output; the treasury pays a calibrated public cost", new[]
        {
            new StrategicBarEntry("Tax rate", nation.TaxRate, Gold),
            new StrategicBarEntry("Welfare", nation.WelfareSpending, Green),
            new StrategicBarEntry("Education", nation.EducationSpending, Blue),
            new StrategicBarEntry("Military", nation.MilitarySpending, Red)
        }, StrategicChartFormat.Percent);
    }

    private void ShowLedgerMarket(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerPolicyFeedback.Text = $"Current: {TradePolicyRules.Name(nation.TradePolicy)} · tariffs " +
                                     $"{TradePolicyRules.TariffRate(nation.TradePolicy):P0}";
        _ledgerLineA.SetData("Market health", "Access rises when domestic output and imports cover demand", days, new[]
        {
            Series("Access", Green, samples, sample => sample.MarketAccess),
            Series("Shortage", Red, samples, sample => sample.ShortageIndex)
        }, StrategicChartFormat.Percent);
        _ledgerLineB.SetData("Commodity price index", "100 equals each good's scenario base price", days,
            CommodityCatalog.All.Select(commodity => Series(CommodityCatalog.Get(commodity).Name,
                CommodityColor(commodity), samples, sample => sample.PriceIndex(commodity))).ToArray(), StrategicChartFormat.Index);
        _ledgerBarsA.SetData("Demand satisfaction", "Shortages reduce growth and living standards", CommodityCatalog.All.Select(commodity =>
        {
            var quote = nation.Market[commodity];
            return new StrategicBarEntry(CommodityCatalog.Get(commodity).Name, quote.Satisfaction,
                quote.IsShortage ? Red : Green, $"Production {quote.Production:0.0} · demand {quote.Demand:0.0}");
        }).ToArray(), StrategicChartFormat.Percent);
        _ledgerBarsB.SetData("Net commodity flow", "Positive is surplus; negative requires imports", CommodityCatalog.All.Select(commodity =>
        {
            var quote = nation.Market[commodity];
            return new StrategicBarEntry(CommodityCatalog.Get(commodity).Name, quote.Net,
                quote.Net >= 0f ? Green : Red, $"Imports {quote.Imports:0.0} · exports {quote.Exports:0.0}");
        }).ToArray(), StrategicChartFormat.Number);
    }

    private void ShowLedgerPopulation(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Population", "Millions of inhabitants", days,
            new[] { Series("Population", Blue, samples, sample => sample.PopulationMillions) }, StrategicChartFormat.Millions);
        _ledgerLineB.SetData("Social development", "Living standards, literacy and stability", days, new[]
        {
            Series("Living standard", Gold, samples, sample => sample.StandardOfLiving),
            Series("Literacy", Blue, samples, sample => sample.Literacy),
            Series("Stability", Green, samples, sample => sample.Stability)
        }, StrategicChartFormat.Percent);
        var states = _campaign.StatesOf(nation);
        _ledgerBarsA.SetData("Social classes", "Actual population groups in owned states", Enum.GetValues<SocialClass>()
            .Select((socialClass, index) => new StrategicBarEntry(SocialClassName(socialClass),
                states.Sum(state => state.PopulationGroups.Where(group => group.Class == socialClass).Sum(group => group.Size)) / 1_000_000f,
                new[] { Orange, Blue, Violet, Gold }[index % 4])).ToArray(), StrategicChartFormat.Millions);
        _ledgerBarsB.SetData("Largest states", "Population centres determine construction and social weight", states
            .OrderByDescending(state => state.Population).Take(7)
            .Select(state => new StrategicBarEntry(state.Name, state.Population / 1_000_000f, Blue,
                $"Living standard {state.StandardOfLiving:P0} · literacy {state.Literacy:P0}")).ToArray(),
            StrategicChartFormat.Millions);
    }

    private void ShowLedgerMilitary(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Operational power", "Divisions weighted by strength, organisation and supply", days,
            new[] { Series("Power", Red, samples, sample => sample.MilitaryPower) }, StrategicChartFormat.Number);
        _ledgerLineB.SetData("Manpower reserve", "Millions available for reinforcement", days,
            new[] { Series("Reserve", Gold, samples, sample => sample.ManpowerMillions) }, StrategicChartFormat.Millions);
        var armies = _campaign.Armies.Where(army => army.NationId == nation.Id).OrderByDescending(army => army.Power).Take(8).ToArray();
        _ledgerBarsA.SetData("Field formations", "Current combat power, not raw division count", armies.Select(army =>
            new StrategicBarEntry(army.Name, army.Power, army.CutOff ? Red : Green,
                $"{army.Divisions:0.0} divisions · organisation {army.Organisation:P0} · supply {army.SupplyLevel:P0}")).ToArray(),
            StrategicChartFormat.Number);
        var wars = _campaign.Wars.Where(war => !war.Ended && war.Involves(nation.Id)).ToArray();
        _ledgerBarsB.SetData("Active wars", "Signed score is shown from this country's perspective", wars.Select(war =>
        {
            var score = war.AttackerId == nation.Id ? war.WarScore : -war.WarScore;
            return new StrategicBarEntry(war.Name, score, score >= 0f ? Green : Red,
                $"Support {(war.AttackerId == nation.Id ? war.AttackerWarSupport : war.DefenderWarSupport):P0}");
        }).ToArray(), StrategicChartFormat.Number);
    }

    private void ShowLedgerRankings(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Selected-country GDP", "Use Map selection to compare another country", days,
            new[] { Series("GDP", Gold, samples, sample => sample.Gdp) }, StrategicChartFormat.MoneyBillions);
        _ledgerLineB.SetData("Selected-country population", "Historical trend for the viewed country", days,
            new[] { Series("Population", Blue, samples, sample => sample.PopulationMillions) }, StrategicChartFormat.Millions);
        _ledgerBarsA.SetData("Largest economies", "Current world GDP ranking", _campaign.Nations
            .OrderByDescending(candidate => candidate.Gdp).Take(8)
            .Select(candidate => new StrategicBarEntry(candidate.Tag, candidate.Gdp,
                candidate.Id == nation.Id ? Gold : Blue, candidate.Name)).ToArray(), StrategicChartFormat.MoneyBillions);
        _ledgerBarsB.SetData("Largest populations", "Current world population ranking", _campaign.Nations
            .OrderByDescending(candidate => candidate.Population).Take(8)
            .Select(candidate => new StrategicBarEntry(candidate.Tag, candidate.Population / 1_000_000f,
                candidate.Id == nation.Id ? Gold : Violet, candidate.Name)).ToArray(), StrategicChartFormat.Millions);
    }

    private static StrategicChartSeries Series(string name, Color color, IReadOnlyList<NationHistorySample> samples,
        Func<NationHistorySample, float> selector) => new(name, color, samples.Select(selector).ToArray());

    private float CurrentWarSupport(Nation nation)
    {
        var supports = _campaign.Wars.Where(war => !war.Ended && war.Involves(nation.Id))
            .Select(war => war.AttackerId == nation.Id ? war.AttackerWarSupport : war.DefenderWarSupport).ToArray();
        return supports.Length == 0 ? nation.WarSupport : supports.Min();
    }

    private static string SignedPercent(float value) => value >= 0f ? $"+{value:P1}" : $"{value:P1}";
    private static string SocialClassName(SocialClass socialClass) => socialClass switch
    {
        SocialClass.MiddleClass => "Middle class",
        _ => socialClass.ToString()
    };

    private static Color CommodityColor(Commodity commodity) => commodity switch
    {
        Commodity.Food => Green,
        Commodity.ConsumerGoods => Violet,
        Commodity.IndustrialMaterials => Ui.Colour("goods.industrial_materials"),
        Commodity.Energy => Orange,
        Commodity.Arms => Red,
        Commodity.Coal => Ui.Colour("goods.coal"),
        Commodity.Oil => Ui.Colour("goods.oil"),
        Commodity.IronOre => Ui.Colour("goods.iron_ore"),
        Commodity.RareEarths => Ui.Colour("goods.rare_earths"),
        Commodity.Machinery => Gold,
        _ => Blue
    };

    private static Color BuildingColor(BuildingKind kind) => kind switch
    {
        BuildingKind.CivilianIndustry => Blue,
        BuildingKind.Infrastructure => Ui.Colour("building.infrastructure"),
        BuildingKind.University => Violet,
        BuildingKind.Hospital => Green,
        BuildingKind.Agriculture => Orange,
        BuildingKind.Barracks => Red,
        BuildingKind.ExtractionIndustry => Ui.Colour("building.extraction"),
        BuildingKind.PowerPlant => Ui.Colour("building.power_plant"),
        BuildingKind.ArmsIndustry => Ui.Colour("building.arms_industry"),
        _ => Gold
    };

    private static void ApplyLedgerButtonState(Button button, bool selected) => StyleButton(button, selected);
}
