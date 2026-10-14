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

    private Label _stateFiscalFeedback = null!;
    private HBoxContainer _stateBudgetControls = null!;
    private HBoxContainer _stateCabinetControls = null!;
    private Label _stateBudgetLine = null!;
    private OptionButton _stateBuildingPicker = null!;
    private OptionButton _stateRegionPicker = null!;
    private Label _stateConstructionQueue = null!;
    private VBoxContainer _stateConstructionRows = null!;
    private int _stateRegionListNationId = -1;
    private Label _stateGovernanceFeedback = null!;
    private readonly Dictionary<GovernmentFocus, Button> _stateFocusButtons = new();

    /// <summary>
    /// A slider for every budget line the data declares, and nothing built by hand.
    ///
    /// Four of them used to have controls of their own in two other sections, which made
    /// income tax look like a different kind of thing from the five taxes beside it. They
    /// are twelve rows of one file and they are twelve rows of one screen. A line a mod
    /// adds gets its slider here for free, which is the whole point of the budget being
    /// a file.
    /// </summary>
    private readonly Dictionary<string, HSlider> _policySliders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Label> _policyReadings = new(StringComparer.OrdinalIgnoreCase);
    private VBoxContainer _policyRevenueControls = null!;
    private VBoxContainer _policyExpenseControls = null!;
    private Label _policyFeedback = null!;
    private Label _policyCollection = null!;

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
        toLedger.TooltipText = Ui.Tip(Tr("The figures behind these decisions: charts, history and rankings, for any country."));
        header.AddChild(toLedger);
        var debt = ActionButton("Public debt", OpenSovereignFinance, 0, 32);
        debt.Name = "BudgetOpenSovereignFinance";
        debt.TooltipText = Ui.Tip(Tr("Cash, borrowing, overdue bills and restructuring in one financial workspace."));
        header.AddChild(debt);
        header.AddChild(HandbookButton("budget"));
        header.AddChild(ActionButton("×", CloseStateAffairs, 38, 32));

        _stateAffairsSummary = Title("", 13, Ui.Colour("text.body_soft"));
        _stateAffairsSummary.CustomMinimumSize = new Vector2(0, 34);
        rows.AddChild(_stateAffairsSummary);

        StateSection(rows, "THE WEEK'S BUDGET",
            "What the country raised, what it spent, and every lever that decides both.");

        // Who is running it. Above the levers rather than below them, because it is the
        // question of whether a player is going to touch the levers at all.
        _budgetDelegated = new CheckButton { Text = Tr("Let the treasury run itself") };
        _budgetDelegated.TooltipText = Ui.Tip(Tr(
            "Hand tax and spending to your own civil service - the same staff that governs every " +
            "other country in the world. Once a month it taxes what a thinning treasury needs, " +
            "spends on the army when there is a war or a threat, buys quiet when the country is " +
            "unhappy and can afford it, and builds with the rest. It does not take decisions and " +
            "it does not touch the central bank; those stay yours. Take it back whenever you like."));
        _budgetDelegated.Toggled += OnBudgetDelegationToggled;
        rows.AddChild(_budgetDelegated);

        BuildStateControls(rows);
    }

    private CheckButton _budgetDelegated;

    private void OnBudgetDelegationToggled(bool delegated)
    {
        if (_campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetDelegationCommand
        {
            IssuerId = _campaign.Player.Id,
            Portfolio = Delegation.Budget,
            Delegated = delegated
        });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshStateAffairs();
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
        MarkFirstStep("budget");
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
    /// <summary>
    /// One line of the construction picker, in the player's language.
    ///
    /// Written by a method rather than inline because a dropdown's items are the one part of
    /// this interface the language refresh cannot reach: every Label carries the key it was
    /// built from and is re-translated when the language changes, and an OptionButton item
    /// carries nothing. The list is rewritten on every refresh instead, which is cheap - the
    /// picker has seventeen rows - and is the only thing that keeps "Agricultural complex"
    /// from sitting in the middle of a Russian screen.
    /// </summary>
    private static string BuildingPickerItem(BuildingKind kind)
    {
        var definition = BuildingCatalog.Get(kind);
        return Simulation.Loc.Format("{0} · {1}",
            Simulation.Loc.Get(definition.Name), Fmt.Money(definition.Cost));
    }

    /// <summary>Put the picker back into the player's language, keeping what was chosen.</summary>
    private void RefreshBuildingPicker()
    {
        if (_stateBuildingPicker is null) return;
        for (var index = 0; index < _stateBuildingPicker.ItemCount; index++)
        {
            var kind = (BuildingKind)(int)_stateBuildingPicker.GetItemMetadata(index);
            var wanted = BuildingPickerItem(kind);
            if (_stateBuildingPicker.GetItemText(index) != wanted)
                _stateBuildingPicker.SetItemText(index, wanted);
        }
    }

    private void RefreshStateAffairs()
    {
        if (_budgetDelegated is not null && _campaign?.Player is { } governing)
        {
            _budgetDelegated.ButtonPressed = Delegation.Holds(governing, Delegation.Budget);
            _budgetDelegated.Text = Tr(Delegation.Holds(governing, Delegation.Budget)
                ? "The treasury is running itself"
                : "Let the treasury run itself");
        }

        if (_stateAffairsLayer is null || !_stateAffairsLayer.Visible || _campaign?.Player is null) return;
        var nation = _campaign.Player;

        _stateAffairsTitle.Text = Simulation.Loc.Format("STATE AFFAIRS · {0}", nation.DisplayName.ToUpperInvariant());
        _stateAffairsSummary.Text =
            Simulation.Loc.Format("Treasury {0} · public debt {1} · balance {2} a week · stability {3:P0} · standard of living {4:P0}",
                Fmt.Money(nation.Treasury), Fmt.Money(SovereignFinance.Outstanding(nation)), Fmt.MoneyDelta(nation.WeeklyBalance), nation.Stability, nation.StandardOfLiving);
        var spirits = DescribeModifiers(nation);
        if (!string.IsNullOrEmpty(spirits)) _stateAffairsSummary.Text += "\n" + spirits;

        RefreshBuildingPicker();
        _stateBudgetControls.Visible = true;
        _stateCabinetControls.Visible = true;

        _updatingStateAffairs = true;
        RefreshCentralBank(nation);
        RefreshPolicyRates(nation);
        RefreshPolitics(nation);
        RefreshAddress(nation);
        _updatingStateAffairs = false;

        RefreshStateBudget(nation);
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
        // What the week actually came to. One line, and the first thing on the screen,
        // because it is the answer to the question anybody opens a budget to ask.
        _stateBudgetLine = RowLabel("", 13, Ui.Colour("text.body_soft"));
        _stateBudgetLine.TooltipText = Ui.Tip(Tr("The treasury is what the country has; the balance is what " +
            "this week added to it or took out of it. Both settle once an economic week, so a slider " +
            "moved now shows up here at the next one."));
        rows.AddChild(_stateBudgetLine);
        _stateFiscalFeedback = RowLabel("", 11, Ui.Colour("text.accent_dim"));
        rows.AddChild(_stateFiscalFeedback);
    }

    /// <summary>
    /// The building queue, which is a queue rather than a policy and so goes at the end with
    /// the other things a player sets up once and leaves running.
    /// </summary>
    private void BuildConstructionControls(VBoxContainer rows)
    {
        _stateBudgetControls = new HBoxContainer { Visible = false };
        _stateBudgetControls.AddThemeConstantOverride("separation", 22);
        rows.AddChild(_stateBudgetControls);

        var construction = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        construction.AddThemeConstantOverride("separation", 4);
        _stateBudgetControls.AddChild(construction);

        var order = new HBoxContainer();
        order.AddThemeConstantOverride("separation", 7);
        construction.AddChild(order);

        _stateBuildingPicker = new OptionButton { CustomMinimumSize = new Vector2(230, 30) };
        foreach (var kind in BuildingCatalog.All)
        {
            var definition = BuildingCatalog.Get(kind);
            _stateBuildingPicker.AddItem(BuildingPickerItem(kind));
            _stateBuildingPicker.SetItemMetadata(_stateBuildingPicker.ItemCount - 1, (int)kind);
            _stateBuildingPicker.SetItemTooltip(_stateBuildingPicker.ItemCount - 1,
                Waterfront.NeedsWater(kind)
                    ? Simulation.Loc.Get(definition.Description) + "\n\n" +
                      Tr("Needs a coast, a lake or a navigable river. On inland water it is worth less.")
                    : Simulation.Loc.Get(definition.Description));
        }
        order.AddChild(_stateBuildingPicker);

        _stateRegionPicker = new OptionButton { CustomMinimumSize = new Vector2(180, 30) };
        order.AddChild(_stateRegionPicker);

        order.AddChild(ActionButton("Queue", OnStateQueueConstruction, 88, 30));

        _stateConstructionQueue = Title("", 11, Ui.Colour("text.body_soft"));
        _stateConstructionQueue.CustomMinimumSize = new Vector2(0, 18);
        construction.AddChild(_stateConstructionQueue);

        // One row per site, each with a way out of it. The queue used to be a single line
        // of text listing four of twelve projects, which told a player what was running and
        // gave them nothing to do about it - and with twelve slots and no way to clear one,
        // a wrong order was permanent.
        _stateConstructionRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _stateConstructionRows.AddThemeConstantOverride("separation", 2);
        construction.AddChild(_stateConstructionRows);
    }
    private void BuildStateControls(VBoxContainer rows)
    {
        // The order is the order somebody works in.
        BuildStateBudget(rows);
        BuildPolicyRates(rows);
        BuildPoliticsControls(rows);
        BuildAddressControls(rows);

        // Trade moved to its own screen: policy, instructions, the market and the partners
        // belong together, and a country's dealings with the world are not a sub-paragraph
        // of its dealings with itself. The controls live in TradeScreenUI now.

        StateSection(rows, "THE CABINET",
            "What the government is for this year. One priority at a time, each with a weekly cost and a " +
            "systemic effect - and every other government in the world is choosing from the same list.");
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
        foreach (var focus in Simulation.GovernmentFocusCatalog.All)
            AddGovernmentFocusButton(GovernmentFocusRules.ShortName(focus), focus);
        _stateGovernanceFeedback = Title("", 11, Ui.Colour("text.accent_dim"));
        _stateGovernanceFeedback.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _stateCabinetControls.AddChild(_stateGovernanceFeedback);

        StateSection(rows, "CONSTRUCTION",
            "Sites the country has open. A project draws on the treasury, on materials and on " +
            "construction capacity for as long as it runs, and twelve may run at once.");
        BuildConstructionControls(rows);
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
        // First, because it is the one lever on this screen that is not a tax and not a
        // budget - and because the rate it shows is what everything below is borrowed at.
        BuildCentralBank(rows);

        StateSection(rows, "REVENUE",
            "Everything the state levies. Each one raises money and costs something else - investment, " +
            "living standards, or the patience of the people paying - and all of them are charged " +
            "against the same week's output, so stacking them collects less than the sum. Hover any " +
            "row to see what it is worth and what it is doing.");
        _policyRevenueControls = new VBoxContainer { Visible = false };
        _policyRevenueControls.AddThemeConstantOverride("separation", 3);
        rows.AddChild(_policyRevenueControls);
        _policyCollection = RowLabel("", 11, Ui.Colour("state.negative"));
        _policyCollection.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _policyCollection.Visible = false;
        rows.AddChild(_policyCollection);

        StateSection(rows, "SPENDING",
            "Everything the state buys. Soldiers, transfers and health are felt this year; roads and " +
            "laboratories are felt in ten. Hover any row to see what it costs and what it buys.");
        _policyExpenseControls = new VBoxContainer { Visible = false };
        _policyExpenseControls.AddThemeConstantOverride("separation", 3);
        rows.AddChild(_policyExpenseControls);

        _policyFeedback = RowLabel("", 11, Ui.Colour("text.accent_dim"));
        rows.AddChild(_policyFeedback);

        foreach (var line in Simulation.BudgetCore.PlayerLines)
            AddPolicySlider(line);
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
            // The wheel scrolls the screen. Seventeen sliders sit inside a scrolling panel,
            // and a wheel that changed the rate under the pointer meant a player reading
            // their way down the budget raised their own taxes on the way past.
            Scrollable = false
        };
        var id = line.Id;
        slider.ValueChanged += value => OnPolicyRateChanged(id, value);
        parent.AddChild(SliderRow(line.Name, slider, out var reading));
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
            // A scenario or a scripted effect may set a rate past what the slider offers,
            // and a slider that silently shows its maximum instead is a control lying about
            // the country it is attached to. It grows to fit rather than crop.
            if (rate > slider.MaxValue) slider.MaxValue = rate;
            slider.SetValueNoSignal(rate);
            if (!_policyReadings.TryGetValue(line.Id, out var reading)) continue;
            var amount = Simulation.BudgetCore.Amount(nation, line, weeklyOutput, atWar);
            reading.Text = Simulation.Loc.Format("{0:P1} · {1}", rate, Fmt.Money(amount));
            // Written fresh every refresh rather than once at build time: the whole value of
            // it is that it quotes the country as it is now, and the country changes.
            var explanation = PolicyTooltip(nation, line);
            slider.TooltipText = Ui.Tip(explanation);
            reading.TooltipText = Ui.Tip(explanation);
        }

        // What the levies ask for against what arrives. Silent while everything is being
        // collected, because a line reading "100%" every week is a line nobody sees by the
        // time it matters - and the moment it matters is the moment a player has stacked
        // taxes and is wondering why the treasury did not move.
        var compliance = Simulation.BudgetCore.ComplianceAt(nation, weeklyOutput, atWar);
        _policyCollection.Visible = compliance < 0.999f;
        if (_policyCollection.Visible)
            _policyCollection.Text = Simulation.Loc.Format(
                "Every levy is charged against the same week's output: {0:P0} of what is asked for is " +
                "actually collected. A further tax on top of these raises less than the first one did.",
                compliance);
    }
    private void AddGovernmentFocusButton(string caption, GovernmentFocus focus)
    {
        var button = ActionButton(caption, () => SetStateGovernmentFocus(focus), 94, 30,
            focus == GovernmentFocus.Balanced);
        button.TooltipText = Ui.Tip(GovernmentFocusRules.Description(focus));
        _stateCabinetControls.AddChild(button);
        _stateFocusButtons[focus] = button;
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
        var slots = _campaign.Defines.MaxConstructionProjects;
        if (running >= slots)
        {
            _stateFiscalFeedback.Text = Simulation.Loc.Format(
                "All {0} sites are already under way. Something has to finish - or be called off - before anything else starts.",
                slots);
            return;
        }
        if (nation.Treasury < MathF.Min(0.05f, definition.Cost))
        {
            _stateFiscalFeedback.Text = Simulation.Loc.Format("The treasury holds {0} and cannot open a {1} site.", Fmt.Money(nation.Treasury), Fmt.Money(definition.Cost));
            return;
        }
        var dry = Waterfront.Refusal(_campaign, stateMapIndex, kind);
        if (dry.Length > 0)
        {
            _stateFiscalFeedback.Text = dry;
            return;
        }

        _campaign.Commands.Enqueue(new QueueConstructionCommand
        {
            IssuerId = nation.Id, StateMapIndex = stateMapIndex, Building = kind
        });
        _campaign.Commands.ApplyAll(_campaign);
        _stateFiscalFeedback.Text = Waterfront.OfRegion(_campaign, stateMapIndex) == WaterAccess.Inland &&
            Waterfront.NeedsWater(kind)
            ? Simulation.Loc.Format("{0} begun in {1} · {2} over {3} days. Inland water: it will be worth {4} of a coastal one.",
                Simulation.Loc.Get(definition.Name), _stateRegionPicker.GetItemText(_stateRegionPicker.Selected),
                Fmt.Money(definition.Cost), definition.Days,
                Fmt.Percent(Waterfront.Yield(_campaign, WaterAccess.Inland)))
            : Simulation.Loc.Format("{0} begun in {1} · {2} over {3} days.",
                Simulation.Loc.Get(definition.Name), _stateRegionPicker.GetItemText(_stateRegionPicker.Selected),
                Fmt.Money(definition.Cost), definition.Days);
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
            foreach (var state in _campaign.StatesOf(nation).OrderBy(state => state.DisplayName, StringComparer.CurrentCulture))
            {
                _stateRegionPicker.AddItem(state.DisplayName);
                _stateRegionPicker.SetItemMetadata(_stateRegionPicker.ItemCount - 1, state.MapIndex);
            }
            if (_stateRegionPicker.ItemCount > 0) _stateRegionPicker.Selected = 0;
        }

        var projects = _campaign.Construction.Where(project => project.NationId == nation.Id).ToList();
        ClearRuntimeChildren(_stateConstructionRows);
        if (projects.Count == 0)
        {
            _stateConstructionQueue.Text = Simulation.Loc.Format(
                "Nothing under construction. {0} sites may run at once.",
                _campaign.Defines.MaxConstructionProjects);
            return;
        }
        _stateConstructionQueue.Text = Simulation.Loc.Format("{0} of {1} sites running",
            projects.Count, _campaign.Defines.MaxConstructionProjects);
        foreach (var project in projects.OrderByDescending(project => project.Progress))
        {
            var place = _campaign.States.FirstOrDefault(state => state.MapIndex == project.StateMapIndex);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _stateConstructionRows.AddChild(row);
            var text = Title(Simulation.Loc.Format("{0} in {1} {2:P0}",
                Simulation.Loc.Get(BuildingCatalog.Get(project.Building).Name),
                place?.Name ?? "?", project.Progress), 12, Ui.Colour("text.body_soft"));
            text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(text);
            var captured = project.Id;
            var drop = ActionButton("×", () => CancelConstruction(captured), 30, 24);
            drop.TooltipText = Ui.Tip(Tr("Call this site off. What has already been spent stays spent; the rest is never taken."));
            row.AddChild(drop);
        }
    }
    private void SetStateTradePolicy(TradePolicy policy)
    {
        if (_campaign?.Player is null || _campaign.Player?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetTradePolicyCommand { IssuerId = _campaign.Player.Id, Policy = policy });
        _campaign.Commands.ApplyAll(_campaign);
        // The feedback is the trade screen's own, because that is where the buttons are.
        if (_tradePolicyFeedback is not null)
            _tradePolicyFeedback.Text = Simulation.Loc.Format("{0} selected · effective at the next market clearing.", TradePolicyRules.Name(policy));
        RefreshTradeScreen();
    }
    private void SetStateGovernmentFocus(GovernmentFocus focus)
    {
        if (_campaign?.Player is null || _campaign.Player?.Id != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetGovernmentFocusCommand { IssuerId = _campaign.Player.Id, Focus = focus });
        _campaign.Commands.ApplyAll(_campaign);
        _stateGovernanceFeedback.Text = Simulation.Loc.Format("{0} adopted.", GovernmentFocusRules.Name(focus));
        RefreshStateAffairs();
    }}
