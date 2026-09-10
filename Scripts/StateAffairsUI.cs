using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// State Affairs: everything the player can actually change about their own country.
///
/// All of this used to live in the ledger, behind its tabs, which put most of the running
/// of a country inside a screen whose name promises statistics. A player looking for the
/// tax rate had no reason to open a page of charts, and a player reading the charts kept
/// finding sliders that changed the world underneath them.
///
/// So they are apart now, and the split is along a line that means something: the ledger
/// reports, State Affairs governs. Nothing here is read-only, and nothing here is about
/// another country - the ledger will happily show you Brazil, and no screen in this game
/// will let you set the Brazilian tax rate.
/// </summary>
public partial class DealOrForceGame
{
    private Control _stateAffairsLayer = null!;
    private Label _stateAffairsTitle = null!;
    private Label _stateAffairsSummary = null!;
    private bool _updatingStateAffairs;

    private Label _statePolicyFeedback = null!;
    private Label _stateFiscalFeedback = null!;
    private HBoxContainer _stateBudgetControls = null!;
    private HBoxContainer _stateTradeControls = null!;
    private HBoxContainer _stateCabinetControls = null!;
    private HBoxContainer _stateSocialControls = null!;
    private HSlider _stateTaxRate = null!;
    private HSlider _stateMilitarySpending = null!;
    private Label _stateTaxReading = null!;
    private Label _stateMilitaryReading = null!;
    private Label _stateBudgetLine = null!;
    private OptionButton _stateBuildingPicker = null!;
    private OptionButton _stateRegionPicker = null!;
    private Label _stateConstructionQueue = null!;
    private int _stateRegionListNationId = -1;
    private HSlider _stateWelfareSpending = null!;
    private HSlider _stateEducationSpending = null!;
    private Label _stateGovernanceFeedback = null!;
    private readonly Dictionary<TradePolicy, Button> _stateTradeButtons = new();
    private readonly Dictionary<GovernmentFocus, Button> _stateFocusButtons = new();

    /// <summary>
    /// A slider for every budget line the data declares and this screen has not already
    /// built by hand.
    ///
    /// Four lines - taxation, the armed forces, welfare, education - keep their own
    /// controls above, laid out the way a player already knows them. Everything else,
    /// including every line a mod adds, gets a slider here without a line of code being
    /// written for it, which is the whole point of the budget being a file.
    /// </summary>
    private readonly Dictionary<string, HSlider> _policySliders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Label> _policyReadings = new(StringComparer.OrdinalIgnoreCase);
    private VBoxContainer _policyRevenueControls = null!;
    private VBoxContainer _policyExpenseControls = null!;
    private Label _policyFeedback = null!;

    private void BuildStateAffairsInterface()
    {
        _stateAffairsLayer = FullLayer("StateAffairs");
        _stateAffairsLayer.Visible = false;
        _hudLayer.AddChild(_stateAffairsLayer);

        var shade = new ColorRect
        {
            Color = new Color(0.015f, 0.026f, 0.041f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _stateAffairsLayer.AddChild(shade);

        var panel = TexturedPanel(_stateAffairsLayer);
        Ui.Place(panel, "modal.ledger");

        var rows = ScrollablePanelRows(panel, 22, 16, 10);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        _stateAffairsTitle = Title("STATE AFFAIRS", 25, Ui.Colour("text.accent"));
        _stateAffairsTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_stateAffairsTitle);
        var toLedger = ActionButton("Open the ledger", () =>
        {
            CloseStateAffairs();
            OpenLedger();
        }, 148, 32);
        toLedger.TooltipText = Tr("The figures behind these decisions: charts, history and rankings, for any country.");
        header.AddChild(toLedger);
        header.AddChild(ActionButton("×", CloseStateAffairs, 38, 32));

        _stateAffairsSummary = Title("", 13, Ui.Colour("text.body_soft"));
        _stateAffairsSummary.CustomMinimumSize = new Vector2(0, 34);
        rows.AddChild(_stateAffairsSummary);

        StateSection(rows, "THE BUDGET AND WHAT IS BEING BUILT",
            "What the state takes each week, what it spends on the armed forces, and the sites it has open.");
        BuildStateControls(rows);
    }

    /// <summary>A heading and a sentence saying what the controls under it are for.</summary>
    private void StateSection(VBoxContainer rows, string heading, string explanation)
    {
        rows.AddChild(new ColorRect
        {
            Color = Ui.Colour("surface.line"),
            CustomMinimumSize = new Vector2(0, 1)
        });
        rows.AddChild(Title(heading, 12, Ui.Colour("text.accent")));
        var note = Title(explanation, 11, Ui.Colour("text.caption"));
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(note);
    }

    private void OpenStateAffairs()
    {
        _openScreen = "State Affairs";
        _sidePanel.Visible = false;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_warLedgerLayer is not null) _warLedgerLayer.Visible = false;
        _stateAffairsLayer.Visible = true;
        _stateAffairsLayer.MoveToFront();
        UpdateScreenButtons();
        RefreshStateAffairs();
    }

    private void CloseStateAffairs()
    {
        if (_stateAffairsLayer is null) return;
        _stateAffairsLayer.Visible = false;
        if (_openScreen == "State Affairs") _openScreen = "";
        UpdateScreenButtons();
    }

    /// <summary>
    /// Put the controls where the country actually is.
    ///
    /// Sliders are set without firing their signals, because a slider that reported the
    /// value back as a command would queue an order every time the screen refreshed, which
    /// at speed five is five orders a second.
    /// </summary>
    private void RefreshStateAffairs()
    {
        if (_stateAffairsLayer is null || !_stateAffairsLayer.Visible || _campaign?.Player is null) return;
        var nation = _campaign.Player;

        _stateAffairsTitle.Text = Simulation.Loc.Format("STATE AFFAIRS · {0}", nation.Name.ToUpperInvariant());
        _stateAffairsSummary.Text =
            Simulation.Loc.Format("Treasury {0}  ·  balance {1} a week  ·  stability {2:P0}  ·  war support {3:P0}  ·  literacy {4:P0}  ·  standard of living {5:P0}", Fmt.Money(nation.Treasury), Fmt.Money(nation.WeeklyBalance), nation.Stability, nation.WarSupport, nation.Literacy, nation.StandardOfLiving);
        var spirits = DescribeModifiers(nation);
        if (!string.IsNullOrEmpty(spirits)) _stateAffairsSummary.Text += "\n" + spirits;

        _stateBudgetControls.Visible = true;
        _stateTradeControls.Visible = true;
        _stateCabinetControls.Visible = true;
        _stateSocialControls.Visible = true;

        _updatingStateAffairs = true;
        _stateTaxRate.SetValueNoSignal(nation.TaxRate);
        _stateMilitarySpending.SetValueNoSignal(nation.MilitarySpending);
        _stateWelfareSpending.SetValueNoSignal(nation.WelfareSpending);
        _stateEducationSpending.SetValueNoSignal(nation.EducationSpending);
        _updatingStateAffairs = true;
        RefreshPolicyRates(nation);
        _updatingStateAffairs = false;

        RefreshStateBudget(nation);
        foreach (var (policy, button) in _stateTradeButtons)
            ApplyLedgerButtonState(button, policy == nation.TradePolicy);
        foreach (var (focus, button) in _stateFocusButtons)
            ApplyLedgerButtonState(button, focus == nation.GovernmentFocus);
        _stateGovernanceFeedback.Text = Simulation.Loc.Format("{0} · welfare {1:P1} · education {2:P1}", GovernmentFocusRules.Name(nation.GovernmentFocus), nation.WelfareSpending, nation.EducationSpending);
    }

    /// <summary>
    /// The economy screen: what the state takes, what it spends, and what it is building.
    ///
    /// It held one slider and a wrapped sentence before. The construction half is the part
    /// the game was already promising - the strategic outliner says "open Economy to queue
    /// a project" and there was nothing on the screen to queue one with.
    /// </summary>
    private void BuildStateBudget(VBoxContainer rows)
    {
        _stateBudgetControls = new HBoxContainer { Visible = false };
        _stateBudgetControls.AddThemeConstantOverride("separation", 22);
        rows.AddChild(_stateBudgetControls);

        // ------------------------------------------------------------------ the budget
        var budget = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        budget.AddThemeConstantOverride("separation", 4);
        _stateBudgetControls.AddChild(budget);
        budget.AddChild(RowLabel("BUDGET", 12, Ui.Colour("text.accent")));

        _stateTaxRate = new HSlider
        {
            MinValue = 0.05,
            MaxValue = 0.55,
            Step = 0.01,
            CustomMinimumSize = new Vector2(190, 26),
            TooltipText = Tr("Higher taxes raise weekly revenue but slowly reduce living standards and private growth.")
        };
        _stateTaxRate.ValueChanged += OnStateTaxChanged;
        budget.AddChild(SliderRow("TAXATION", _stateTaxRate, out _stateTaxReading));

        _stateMilitarySpending = new HSlider
        {
            MinValue = 0.0,
            MaxValue = 0.30,
            Step = 0.005,
            CustomMinimumSize = new Vector2(190, 26),
            TooltipText = Tr("The share of output spent on the armed forces: reinforcement rate and readiness, " +
                          "against everything else the money could have done.")
        };
        _stateMilitarySpending.ValueChanged += OnStateMilitaryChanged;
        budget.AddChild(SliderRow("ARMED FORCES", _stateMilitarySpending, out _stateMilitaryReading));

        _stateBudgetLine = RowLabel("", 12, Ui.Colour("text.body_soft"));
        budget.AddChild(_stateBudgetLine);
        _stateFiscalFeedback = RowLabel("", 11, Ui.Colour("text.accent_dim"));
        budget.AddChild(_stateFiscalFeedback);

        // ------------------------------------------------------------ what is being built
        var construction = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        construction.AddThemeConstantOverride("separation", 4);
        _stateBudgetControls.AddChild(construction);
        construction.AddChild(RowLabel("CONSTRUCTION", 12, Ui.Colour("text.accent")));

        var order = new HBoxContainer();
        order.AddThemeConstantOverride("separation", 7);
        construction.AddChild(order);

        _stateBuildingPicker = new OptionButton { CustomMinimumSize = new Vector2(230, 30) };
        foreach (var kind in BuildingCatalog.All)
        {
            var definition = BuildingCatalog.Get(kind);
            _stateBuildingPicker.AddItem(Simulation.Loc.Format("{0} · {1}", definition.Name, Fmt.Money(definition.Cost)));
            _stateBuildingPicker.SetItemMetadata(_stateBuildingPicker.ItemCount - 1, (int)kind);
            _stateBuildingPicker.SetItemTooltip(_stateBuildingPicker.ItemCount - 1, definition.Description);
        }
        order.AddChild(_stateBuildingPicker);

        _stateRegionPicker = new OptionButton { CustomMinimumSize = new Vector2(180, 30) };
        order.AddChild(_stateRegionPicker);

        order.AddChild(ActionButton("Queue", OnStateQueueConstruction, 88, 30));

        _stateConstructionQueue = Title("", 11, Ui.Colour("text.body_soft"));
        _stateConstructionQueue.CustomMinimumSize = new Vector2(0, 36);
        construction.AddChild(_stateConstructionQueue);
    }
    private void BuildStateControls(VBoxContainer rows)
    {
        BuildStateBudget(rows);

        StateSection(rows, "TRADE",
            "How open the country is to the world market. A change takes effect at the next weekly clearing.");
        _stateTradeControls = new HBoxContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _stateTradeControls.AddThemeConstantOverride("separation", 7);
        rows.AddChild(_stateTradeControls);
        var tradeCaption = Title("TRADE POLICY", 12, Ui.Colour("text.caption"));
        tradeCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        tradeCaption.CustomMinimumSize = new Vector2(98, 0);
        tradeCaption.VerticalAlignment = VerticalAlignment.Center;
        _stateTradeControls.AddChild(tradeCaption);
        AddTradePolicyButton("Open markets", TradePolicy.OpenMarkets,
            "More imports and exports, minimal tariffs; best access during peaceful global supply.");
        AddTradePolicyButton("Balanced", TradePolicy.BalancedTrade,
            "Moderate access and tariffs without a strong structural bias.");
        AddTradePolicyButton("Autarky", TradePolicy.StrategicAutarky,
            "Restricts trade and raises tariffs; safer politically, but shortages become harder to cover.");
        _statePolicyFeedback = Title("", 11, Ui.Colour("text.accent_dim"));
        _statePolicyFeedback.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _stateTradeControls.AddChild(_statePolicyFeedback);

        StateSection(rows, "THE CABINET",
            "What the government is for this year. One priority at a time, each with a weekly cost and a " +
            "systemic effect - and the AI governments of the world are choosing from the same list.");
        _stateCabinetControls = new HBoxContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _stateCabinetControls.AddThemeConstantOverride("separation", 7);
        rows.AddChild(_stateCabinetControls);
        var cabinetCaption = Title("CABINET", 12, Ui.Colour("text.caption"));
        cabinetCaption.CustomMinimumSize = new Vector2(78, 0);
        cabinetCaption.VerticalAlignment = VerticalAlignment.Center;
        _stateCabinetControls.AddChild(cabinetCaption);
        AddGovernmentFocusButton("Balanced", GovernmentFocus.Balanced);
        AddGovernmentFocusButton("Growth", GovernmentFocus.EconomicExpansion);
        AddGovernmentFocusButton("Cohesion", GovernmentFocus.SocialCohesion);
        AddGovernmentFocusButton("Science", GovernmentFocus.EducationAndScience);
        AddGovernmentFocusButton("Security", GovernmentFocus.NationalSecurity);
        _stateGovernanceFeedback = Title("", 11, Ui.Colour("text.accent_dim"));
        _stateGovernanceFeedback.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _stateCabinetControls.AddChild(_stateGovernanceFeedback);

        StateSection(rows, "WELFARE AND EDUCATION",
            "Transfers raise living standards and cool radicalism; schooling raises literacy, and literacy " +
            "raises productivity for the rest of the campaign. Both are paid every week.");
        _stateSocialControls = new HBoxContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _stateSocialControls.AddThemeConstantOverride("separation", 9);
        rows.AddChild(_stateSocialControls);
        AddSocialSpendingSlider("WELFARE", "Public transfers improve regional living standards and reduce radicalism, but increase weekly expenses.",
            0.18f, out _stateWelfareSpending, OnStateWelfareChanged);
        AddSocialSpendingSlider("EDUCATION", "Education spending raises national literacy and the long-run productivity target every economic week.",
            0.15f, out _stateEducationSpending, OnStateEducationChanged);

        BuildPolicyRates(rows);
    }

    /// <summary>
    /// Everything else a government raises and spends, straight out of `common/budget`.
    ///
    /// Nothing here names a tax. The screen asks the catalogue what lines exist, skips the
    /// four that already have controls of their own further up, and builds a slider for
    /// each of the rest - which means a mod that adds a wealth tax gets a wealth tax slider,
    /// with its own name, its own limits and its own explanation, without touching this
    /// file. That is the difference between a game with a budget in it and a game whose
    /// budget is data.
    /// </summary>
    private void BuildPolicyRates(VBoxContainer rows)
    {
        StateSection(rows, "REVENUE",
            "What the state levies besides income tax. Every one of them raises money and costs " +
            "something else - investment, living standards, or the patience of the people paying.");
        _policyRevenueControls = new VBoxContainer { Visible = false };
        _policyRevenueControls.AddThemeConstantOverride("separation", 3);
        rows.AddChild(_policyRevenueControls);

        StateSection(rows, "SPENDING",
            "What the state buys besides soldiers, schools and transfers. Health and pensions are " +
            "felt this year; roads and laboratories are felt in ten.");
        _policyExpenseControls = new VBoxContainer { Visible = false };
        _policyExpenseControls.AddThemeConstantOverride("separation", 3);
        rows.AddChild(_policyExpenseControls);

        _policyFeedback = RowLabel("", 11, Ui.Colour("text.accent_dim"));
        rows.AddChild(_policyFeedback);

        foreach (var line in Simulation.BudgetCore.PlayerLines)
        {
            // The four with hand-built controls above. Building a second slider for them
            // would give a player two of everything and no clue which one wins.
            if (line.Store != Simulation.BudgetStore.Policy) continue;
            AddPolicySlider(line);
        }
    }

    private void AddPolicySlider(Simulation.BudgetLine line)
    {
        var parent = line.Side == Simulation.BudgetSide.Revenue
            ? _policyRevenueControls
            : _policyExpenseControls;

        var slider = new HSlider
        {
            MinValue = line.Minimum,
            MaxValue = line.Maximum,
            Step = 0.005,
            CustomMinimumSize = new Vector2(210, 26),
            TooltipText = Tr(line.Description)
        };
        var id = line.Id;
        slider.ValueChanged += value => OnPolicyRateChanged(id, value);
        parent.AddChild(SliderRow(Tr(line.Name).ToUpperInvariant(), slider, out var reading));
        _policySliders[id] = slider;
        _policyReadings[id] = reading;
    }

    private void OnPolicyRateChanged(string lineId, double value)
    {
        if (_updatingStateAffairs || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new Simulation.SetPolicyRateCommand
        {
            IssuerId = _campaign.Player.Id,
            LineId = lineId,
            Value = (float)value
        });
        _campaign.Commands.ApplyAll(_campaign);
        var line = Simulation.BudgetCore.Find(lineId);
        _policyFeedback.Text = Simulation.Loc.Format("{0} set to {1}. The budget updates on the next economic week.",
            Tr(line?.Name ?? lineId), value.ToString("P1", System.Globalization.CultureInfo.CurrentCulture));
        RefreshStateAffairs();
    }

    /// <summary>
    /// Put the sliders where the country actually has them, and say what each is worth this
    /// week beside it - a rate with no money against it is a number a player cannot price.
    /// </summary>
    private void RefreshPolicyRates(Nation nation)
    {
        if (_policyRevenueControls is null) return;
        _policyRevenueControls.Visible = true;
        _policyExpenseControls.Visible = true;

        var atWar = _campaign is not null &&
                    _campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        var weeklyOutput = nation.Gdp / 52f;

        foreach (var line in Simulation.BudgetCore.PlayerLines)
        {
            if (!_policySliders.TryGetValue(line.Id, out var slider)) continue;
            var rate = Simulation.BudgetCore.RateOf(nation, line);
            slider.SetValueNoSignal(rate);
            if (!_policyReadings.TryGetValue(line.Id, out var reading)) continue;
            var amount = Simulation.BudgetCore.Amount(nation, line, weeklyOutput, atWar);
            reading.Text = Simulation.Loc.Format("{0:P1} · {1}", rate, Fmt.Money(amount));
        }
    }
    private void AddTradePolicyButton(string caption, TradePolicy policy, string tooltip)
    {
        var button = ActionButton(caption, () => SetStateTradePolicy(policy), 114, 30,
            policy == TradePolicy.BalancedTrade);
        button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        button.TooltipText = tooltip;
        _stateTradeControls.AddChild(button);
        _stateTradeButtons[policy] = button;
    }
    private void AddGovernmentFocusButton(string caption, GovernmentFocus focus)
    {
        var button = ActionButton(caption, () => SetStateGovernmentFocus(focus), 94, 30,
            focus == GovernmentFocus.Balanced);
        button.TooltipText = GovernmentFocusRules.Description(focus);
        _stateCabinetControls.AddChild(button);
        _stateFocusButtons[focus] = button;
    }
    private void AddSocialSpendingSlider(string caption, string tooltip, float maximum,
        out HSlider slider, Godot.Range.ValueChangedEventHandler handler)
    {
        var label = Title(caption, 11, Ui.Colour("text.caption"));
        label.CustomMinimumSize = new Vector2(80, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        _stateSocialControls.AddChild(label);
        slider = new HSlider
        {
            MinValue = 0,
            MaxValue = maximum,
            Step = 0.005,
            CustomMinimumSize = new Vector2(225, 28),
            TooltipText = tooltip
        };
        slider.ValueChanged += handler;
        _stateSocialControls.AddChild(slider);
    }
    private void OnStateTaxChanged(double value)
    {
        if (_updatingStateAffairs || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetTaxRateCommand { IssuerId = _campaign.Player.Id, Value = (float)value });
        _campaign.Commands.ApplyAll(_campaign);
        _stateFiscalFeedback.Text = Simulation.Loc.Format("Tax rate set to {0:P0}. Budget and growth update on the next economic week.", value);
        RefreshStateAffairs();
    }
    private void OnStateMilitaryChanged(double value)
    {
        if (_updatingStateAffairs || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetMilitarySpendingCommand
        {
            IssuerId = _campaign.Player.Id, Value = (float)value
        });
        _campaign.Commands.ApplyAll(_campaign);
        _stateFiscalFeedback.Text = Simulation.Loc.Format("Armed forces set to {0:P1} of output. Reinforcement and readiness follow from the next economic week.", value);
        RefreshStateAffairs();
    }
    /// <summary>
    /// Order a building, and say why when it cannot be ordered.
    ///
    /// The command refuses silently on both of its conditions, which is right for the AI
    /// giving it eight times a day and wrong for a button: a control that does nothing and
    /// says nothing reads as broken. So the two refusals are spoken here.
    /// </summary>
    private void OnStateQueueConstruction()
    {
        var nation = _campaign?.Player;
        if (nation is null) return;
        if (_stateBuildingPicker.Selected < 0 || _stateRegionPicker.Selected < 0)
        {
            _stateFiscalFeedback.Text = Tr("Choose what to build and where.");
            return;
        }

        var kind = (BuildingKind)(int)_stateBuildingPicker.GetItemMetadata(_stateBuildingPicker.Selected);
        var stateMapIndex = (int)_stateRegionPicker.GetItemMetadata(_stateRegionPicker.Selected);
        var definition = BuildingCatalog.Get(kind);

        var running = _campaign.Construction.Count(project => project.NationId == nation.Id);
        if (running >= 12)
        {
            _stateFiscalFeedback.Text = Tr("Twelve projects are already under way. Something has to finish before anything else starts.");
            return;
        }
        if (nation.Treasury < MathF.Min(0.05f, definition.Cost))
        {
            _stateFiscalFeedback.Text = Simulation.Loc.Format("The treasury holds {0} and cannot open a {1} site.", Fmt.Money(nation.Treasury), Fmt.Money(definition.Cost));
            return;
        }

        _campaign.Commands.Enqueue(new QueueConstructionCommand
        {
            IssuerId = nation.Id, StateMapIndex = stateMapIndex, Building = kind
        });
        _campaign.Commands.ApplyAll(_campaign);
        _stateFiscalFeedback.Text = Simulation.Loc.Format("{0} begun in {1} · {2} over {3} days.", definition.Name, _stateRegionPicker.GetItemText(_stateRegionPicker.Selected), Fmt.Money(definition.Cost), definition.Days);
        RefreshStateAffairs();
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
    private void RefreshStateBudget(Nation nation)
    {
        var tax = (float)_stateTaxRate.Value;
        var military = (float)_stateMilitarySpending.Value;
        _stateTaxReading.Text = Simulation.Loc.Format("{0:P0} · {1} a week", tax, Fmt.Money(EconomySystem.WeeklyTaxAt(nation, tax)));
        _stateMilitaryReading.Text = Simulation.Loc.Format("{0:P1} · −{1} a week", military, Fmt.Money(EconomySystem.WeeklyMilitaryAt(nation, military)));

        // The budget is settled once an economic week, so on day zero there is no week to
        // report. Printing its zeroes as "balance +$0, outgoings $0" is a country with no
        // expenses, which is a stranger claim than saying nothing has happened yet.
        var balance = nation.WeeklyBalance;
        if (nation.WeeklyExpenses <= 0f)
        {
            _stateBudgetLine.Text = Simulation.Loc.Format("Treasury {0}  ·  no economic week has closed yet", Fmt.Money(nation.Treasury));
            _stateBudgetLine.AddThemeColorOverride("font_color", Ui.Colour("text.body_soft"));
        }
        else
        {
            _stateBudgetLine.Text = Simulation.Loc.Format(
                "Treasury {0}  ·  balance {1}{2} a week  ·  income {3}  ·  outgoings {4}",
                Fmt.Money(nation.Treasury), balance < 0f ? "−" : "+", Fmt.Money(MathF.Abs(balance)),
                Fmt.Money(nation.WeeklyTaxIncome + nation.WeeklyTariffIncome),
                Fmt.Money(nation.WeeklyExpenses));
            _stateBudgetLine.AddThemeColorOverride("font_color",
                balance < 0f ? Ui.Colour("state.negative") : Ui.Colour("state.positive"));
        }

        // The list of places only changes when the country does, and rebuilding a dropdown
        // under the player's cursor would throw away what they had picked.
        if (_stateRegionListNationId != nation.Id)
        {
            _stateRegionListNationId = nation.Id;
            _stateRegionPicker.Clear();
            foreach (var state in _campaign.StatesOf(nation).OrderBy(state => state.Name, StringComparer.CurrentCulture))
            {
                _stateRegionPicker.AddItem(state.Name);
                _stateRegionPicker.SetItemMetadata(_stateRegionPicker.ItemCount - 1, state.MapIndex);
            }
            if (_stateRegionPicker.ItemCount > 0) _stateRegionPicker.Selected = 0;
        }

        var projects = _campaign.Construction.Where(project => project.NationId == nation.Id).ToList();
        if (projects.Count == 0)
        {
            _stateConstructionQueue.Text = Tr("Nothing under construction. Twelve sites may run at once.");
            return;
        }
        _stateConstructionQueue.Text = Simulation.Loc.Format("{0} of 12 sites running · ", projects.Count) +
            string.Join("  ·  ", projects
                .OrderByDescending(project => project.Progress)
                .Take(4)
                .Select(project =>
                {
                    var place = _campaign.States.FirstOrDefault(state => state.MapIndex == project.StateMapIndex);
                    return Simulation.Loc.Format("{0} in {1} {2:P0}",
                        BuildingCatalog.Get(project.Building).Name, place?.Name ?? "?", project.Progress);
                }));
    }
    private void SetStateTradePolicy(TradePolicy policy)
    {
        if (_campaign?.Player is null || _campaign.Player?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetTradePolicyCommand { IssuerId = _campaign.Player.Id, Policy = policy });
        _campaign.Commands.ApplyAll(_campaign);
        _statePolicyFeedback.Text = Simulation.Loc.Format("{0} selected · effective at the next market clearing.", TradePolicyRules.Name(policy));
        RefreshStateAffairs();
    }
    private void OnStateWelfareChanged(double value)
    {
        if (_updatingStateAffairs || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetWelfareSpendingCommand { IssuerId = _campaign.Player.Id, Value = (float)value });
        _campaign.Commands.ApplyAll(_campaign);
        _stateGovernanceFeedback.Text = Simulation.Loc.Format("Welfare set to {0:P1} of weekly output.", value);
        RefreshStateAffairs();
    }
    private void OnStateEducationChanged(double value)
    {
        if (_updatingStateAffairs || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetEducationSpendingCommand { IssuerId = _campaign.Player.Id, Value = (float)value });
        _campaign.Commands.ApplyAll(_campaign);
        _stateGovernanceFeedback.Text = Simulation.Loc.Format("Education set to {0:P1} of weekly output.", value);
        RefreshStateAffairs();
    }
    private void SetStateGovernmentFocus(GovernmentFocus focus)
    {
        if (_campaign?.Player is null || _campaign.Player?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetGovernmentFocusCommand { IssuerId = _campaign.Player.Id, Focus = focus });
        _campaign.Commands.ApplyAll(_campaign);
        _stateGovernanceFeedback.Text = Simulation.Loc.Format("{0} adopted.", GovernmentFocusRules.Name(focus));
        RefreshStateAffairs();
    }}
