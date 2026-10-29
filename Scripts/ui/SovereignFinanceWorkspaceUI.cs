using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _sovereignFinanceLayer;
    private Label _financeTitle, _financeState, _financeFeedback, _financePreview;
    private Label _financeCash, _financePrincipal, _financeArrears;
    private readonly Dictionary<string, Label> _financeValues = new();
    private readonly Dictionary<string, ProgressBar> _financeGauges = new();
    private readonly Dictionary<string, Button> _financeActions = new();
    private SpinBox _financeAmount;
    private Button _financeClose;
    private bool _financeFromBudget;
    private Control _financeReturnFocus;
    private int _financeNation = -1;
    private string _financePendingAction = "";
    private FinanceConfirmation _financeConfirmation;

    private readonly record struct FinanceConfirmation(int Nation, int Day, float Amount,
        float Cash, float Principal, float Arrears, float Credit, DebtCondition Condition);

    private void BindDebtHeadline()
    {
        _publicDebt.Box.FocusMode = FocusModeEnum.All;
        _publicDebt.Box.MouseDefaultCursorShape = CursorShape.PointingHand;
        _publicDebt.Box.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } ||
                input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter })
            { OpenSovereignFinance(); _publicDebt.Box.AcceptEvent(); }
        };
        _publicDebt.Box.FocusEntered += () => _publicDebt.Caption.AddThemeColorOverride("font_color", Ui.Colour("text.accent"));
        _publicDebt.Box.FocusExited += () => _publicDebt.Caption.AddThemeColorOverride("font_color", Ui.Colour("text.caption"));
    }

    private void RefreshDebtHeadline(Nation nation)
    {
        if (_publicDebt is null) return; // Authored UI mods may omit individual metrics.
        _publicDebt.Box.Visible = nation.Movement is not { Landless: true };
        var principal = SovereignFinance.Outstanding(nation);
        var overdue = nation.DebtArrears + nation.PaymentArrears;
        var caption = overdue > .000001f ? Tr("Overdue payments") :
            Loc.Format("{0} of GDP", Fmt.Percent(principal / MathF.Max(.01f, nation.Gdp)));
        var explanation = Loc.Format(
            "Principal: {0}\nDebt interest overdue: {1}\nPublic bills overdue: {2}\nState: {3}\nClick or press Enter to manage sovereign finance.",
            Fmt.MoneyExact(principal), Fmt.MoneyExact(nation.DebtArrears), Fmt.MoneyExact(nation.PaymentArrears),
            Tr(SovereignBalanceSheet.ConditionName(nation.DebtCondition)));
        WriteStat(_publicDebt, Fmt.Money(principal), explanation, caption,
            overdue > .000001f || nation.DebtCondition >= DebtCondition.Default ? -1 : 0,
            overdue > .000001f ? Tr("hud.footer.overdue") : caption);
        _publicDebt.Box.TooltipText = Ui.Tip(explanation);
        _publicDebt.Value.MouseFilter = MouseFilterEnum.Pass;
        _publicDebt.Delta.MouseFilter = MouseFilterEnum.Pass;
        _publicDebt.Delta.TooltipText = Ui.Tip(explanation);
    }

    private void OpenSovereignFinance()
    {
        if (_campaign?.Player is null || !_hudLayer.Visible || _pauseMenuLayer is { Visible: true }) return;
        if (_situationLayer is not null) _situationLayer.Visible = false;
        if (_sovereignFinanceLayer is null) BuildSovereignFinanceWorkspace();
        _financeReturnFocus = GetViewport().GuiGetFocusOwner();
        _financeFromBudget = _stateAffairsLayer is { Visible: true };
        foreach (var other in new[] { _stateAffairsLayer, _ledgerLayer, _warLedgerLayer, _diplomacyLayer,
                     _policyLayer, _conflictsLayer, _productionLayer, _tradeScreenLayer, _supportLayer, _researchLayer })
            if (other is not null) other.Visible = false;
        _sidePanel.Visible = false;
        SetArmyPanelVisible(false);
        _openScreen = "Economy";
        _sovereignFinanceLayer.Visible = true;
        _sovereignFinanceLayer.MoveToFront();
        _financePendingAction = "";
        _financeNation = _campaign.Player.Id;
        _financeAmount.SetValueNoSignal(Math.Max(.01, _campaign.Player.Gdp * .001));
        _financeFeedback.Text = "";
        RefreshSovereignFinanceWorkspace();
        // Opening with the mouse should not make the exit look hovered/selected.
        // Keyboard navigation starts from a neutral target inside the workspace.
        _sovereignFinanceLayer.FocusMode = FocusModeEnum.All;
        _sovereignFinanceLayer.GrabFocus();
        UpdateScreenButtons();
    }

    private void CloseSovereignFinance()
    {
        if (_sovereignFinanceLayer is null) return;
        _sovereignFinanceLayer.Visible = false;
        _financePendingAction = "";
        _openScreen = "";
        if (_financeFromBudget) OpenScreen("State Affairs");
        if (IsInstanceValid(_financeReturnFocus) && _financeReturnFocus.IsVisibleInTree()) _financeReturnFocus.GrabFocus();
        _financeReturnFocus = null;
        UpdateScreenButtons();
    }

    private void BuildSovereignFinanceWorkspace()
    {
        _sovereignFinanceLayer = FullLayer("SovereignFinanceWorkspace");
        _sovereignFinanceLayer.Visible = false;
        _hudLayer.AddChild(_sovereignFinanceLayer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _sovereignFinanceLayer.AddChild(shade);
        var panel = TexturedPanel(_sovereignFinanceLayer);
        panel.Name = "SovereignFinancePanel";
        Ui.Place(panel, "modal.diplomacy");
        var rows = PanelRows(panel, 22, 16, 10);
        var budget = ActionButton("Open the budget", () =>
        { _sovereignFinanceLayer.Hide(); _financeFromBudget = false; OpenScreen("State Affairs"); }, 0, 32);
        budget.TooltipText = Ui.Tip(Tr("Adjust taxes and spending. Borrowing finances a deficit; it does not repair it."));
        _financeClose = ActionButton("×", CloseSovereignFinance, 38, 32);
        _financeClose.Name = "CloseSovereignFinance";
        _financeClose.TooltipText = Ui.Tip(Tr("Close"));
        var head = ScreenHeader(rows, "SOVEREIGN FINANCE",
            "Cash, borrowed principal, overdue obligations, credit capacity and bond issuance.",
            false, budget, HandbookButton("budget"), _financeClose);
        _financeTitle = HeaderTitle(head);
        _financeTitle.Name = "FinanceTitle";
        _financeState = WrappedUltimatumText("", 13); _financeState.Name = "FinanceCondition"; rows.AddChild(_financeState);
        var metrics = new HBoxContainer(); metrics.AddThemeConstantOverride("separation", 10); rows.AddChild(metrics);
        _financeCash = FinanceMetric(metrics, "Treasury", "FinanceCash", "Available money, not the country's accumulated obligations.");
        _financePrincipal = FinanceMetric(metrics, "Public debt", "FinancePrincipal", "Borrowed principal. Positive cash does not erase bonds or their interest.");
        _financeArrears = FinanceMetric(metrics, "Overdue payments", "FinanceArrears", "Unpaid interest plus unpaid primary spending. These are shown separately below.");
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 14); rows.AddChild(body);
        var balance = FinanceColumn(body, "FinanceBalance", .95f);
        FinanceHeading(balance, "BALANCE SHEET");
        FinanceRow(balance, "Debt / GDP", "ratio", "Debt relative to annual output. A high ratio alone is not a default.");
        FinanceRow(balance, "Debt arrears", "interest_arrears", "Unpaid debt interest; persistent missed payments cause default.");
        FinanceRow(balance, "Public payment arrears", "spending_arrears", "Unpaid primary spending. Distinct from contractual debt default.");
        FinanceRow(balance, "Total obligations", "obligations", "Principal and all arrears.");
        FinanceRow(balance, "Debt service", "interest", "Interest under the current budget and sovereign risk premium, per economic week.");
        FinanceRow(balance, "Interest / revenue", "service", "The share of current government revenue required by debt interest, not a share of GDP.");
        FinanceGauge(balance, "Credit confidence", "confidence");
        FinanceRow(balance, "Risk premium", "spread", "Annual sovereign spread added to the base interest rate.");
        FinanceRow(balance, "Missed interest weeks", "missed", "The default grace clock counts missed interest, not the number of days with an empty treasury.");
        var flow = FinanceColumn(body, "FinanceFlow", 1.1f);
        FinanceHeading(flow, "CASH AND FUNDING");
        FinanceRow(flow, "Weekly budget balance", "balance", "Income minus spending including interest; borrowing is financing, not revenue.");
        FinanceRow(flow, "Primary budget balance", "primary", "The budget before debt interest. It shows whether the state can stop adding new obligations.");
        FinanceRow(flow, "Cash runway", "runway", "Weeks of cash at the current deficit, before new borrowing. Policy, war, construction and prices can change it.");
        FinanceRow(flow, "Total credit capacity", "capacity", "An aggregate lending ceiling based on output, confidence and national modifiers.");
        FinanceRow(flow, "Available new credit", "credit", "The smaller of remaining debt capacity and the shared issuance allowance for this economic week.");
        FinanceRow(flow, "Issued this week", "issued", "Bonds issued this week, automatically or by you. Both count against the weekly limit.");
        FinanceRow(flow, "Funding gap at current limits", "gap", "Current deficit minus cash and remaining credit. This is an estimate, not a guaranteed future settlement.");
        var note = WrappedUltimatumText("Cash is spent first. New borrowing is limited. Unfunded interest becomes debt arrears; other unfunded spending becomes public bills. A surplus services arrears before it builds cash.", 12);
        flow.AddChild(note);
        var actions = FinanceColumn(body, "FinanceManagement", 1.05f);
        FinanceHeading(actions, "MANAGE OBLIGATIONS");
        actions.AddChild(WrappedUltimatumText("Amount in billions of dollars; 0.01 = $10 million. Review the result before confirming.", 12));
        _financeAmount = new SpinBox { Name = "FinanceAmount", MinValue = .01, MaxValue = 1_000_000,
            Step = .01, Value = .01, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _financeAmount.ValueChanged += _ => { _financePendingAction = ""; _financeFeedback.Text = ""; RefreshSovereignFinanceWorkspace(); };
        actions.AddChild(_financeAmount);
        foreach (var (id, caption) in new[] { ("borrow", "Issue bonds"), ("repay", "Repay debt"),
                     ("arrears", "Pay overdue bills"), ("restructure", "Negotiate restructuring") })
        {
            var captured = id;
            var button = ActionButton(caption, () => ReviewFinanceAction(captured), 0, 36);
            button.Name = "FinanceAction_" + id;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.body", 13));
            actions.AddChild(button); _financeActions[id] = button;
        }
        _financePreview = WrappedUltimatumText("", 12); _financePreview.Name = "FinanceActionPreview"; actions.AddChild(_financePreview);
        FinanceRow(actions, "Negotiation progress", "restructuring", "Progress pauses without funding or sufficient improvement in the primary budget.");
        actions.AddChild(WrappedUltimatumText("Repaying principal does not pay overdue bills.", 12));
        _financeFeedback = WrappedUltimatumText("", 12); _financeFeedback.Name = "FinanceFeedback"; rows.AddChild(_financeFeedback);
    }

    private Label FinanceMetric(HBoxContainer parent, string caption, string name, string description)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel.SetMeta("atlas_section_ground", "surface.raised");
        panel.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.raised"));
        AtlasSurfaceSkin.Attach(panel, AtlasSurfaceSkin.Level.Raised, "surface.raised");
        parent.AddChild(panel);
        var rows = PanelRows(panel, 9, 7, 3);
        rows.AddChild(Title(caption, 12, Ui.Colour("text.caption")));
        var value = Title("—", 23, Ui.Colour("text.bright"));
        value.Name = name;
        value.AddThemeFontOverride("font", PresentationFonts.Tabular());
        rows.AddChild(value);
        rows.AddChild(WrappedUltimatumText(description, 11)); return value;
    }

    private VBoxContainer FinanceColumn(Control parent, string name, float ratio)
    {
        var column = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = ratio };
        column.SetMeta("atlas_section_ground", "surface.inset");
        column.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(column, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        parent.AddChild(column); return PolicyColumn(column, name, true);
    }

    private void FinanceHeading(VBoxContainer parent, string caption) => SectionRule(parent, caption);

    private void FinanceRow(VBoxContainer parent, string caption, string key, string tip)
    {
        var panel = new PanelContainer(); panel.AddThemeStyleboxOverride("panel", ShellRowStyle(_financeValues.Count % 2 == 0, readOnly: true));
        panel.TooltipText = Ui.Tip(Tr(tip)); parent.AddChild(panel);
        var rows = PanelRows(panel, 7, 4, 2);
        rows.AddChild(WrappedUltimatumText(caption, 11));
        var value = Title("—", 15, Ui.Colour("text.bright"));
        value.Name = "FinanceValue_" + key;
        value.AutowrapMode = TextServer.AutowrapMode.Off;
        value.AddThemeFontOverride("font", PresentationFonts.Tabular());
        rows.AddChild(value);
        _financeValues[key] = value;
    }

    private void FinanceGauge(VBoxContainer parent, string caption, string key)
    {
        FinanceRow(parent, caption, key, "Gradually reflects stability, inflation, debt burden and unpaid obligations; affects lending capacity and the risk premium.");
        var gauge = new ProgressBar { Name = "FinanceGauge_" + key, MinValue = 0, MaxValue = 1,
            ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6) };
        gauge.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Colour("text.accent") });
        gauge.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.overlay") });
        parent.AddChild(gauge); _financeGauges[key] = gauge;
    }

    private void FinanceValue(string key, string value, string colour = "text.bright")
    {
        var label = _financeValues[key]; label.Text = value;
        label.AddThemeColorOverride("font_color", Ui.Colour(colour));
    }

    private void RefreshSovereignFinanceWorkspace()
    {
        if (_sovereignFinanceLayer is not { Visible: true } || _campaign?.Player is not { } n) return;
        if (_financeNation != n.Id)
        { _financeNation = n.Id; _financePendingAction = ""; _financeFeedback.Text = ""; }
        var s = SovereignBalanceSheet.Read(_campaign, n);
        _financeTitle.Text = Tr("SOVEREIGN FINANCE") + " · " + n.DisplayName;
        _financeTitle.TooltipText = Ui.Tip(_financeTitle.Text);
        _financeState.Text = Tr(SovereignBalanceSheet.ConditionName(n.DebtCondition)) + " · " +
            (_campaign.FeatureEnabled("public_debt") ? Tr("Cash and debt are separate accounts") : Tr("Debt mechanic disabled; saved obligations remain"));
        _financeState.AddThemeColorOverride("font_color", Ui.Colour(n.DebtCondition >= DebtCondition.Default ? "state.negative" : "text.body_soft"));
        _financeCash.Text = Fmt.Money(s.Cash); _financePrincipal.Text = Fmt.Money(s.Principal); _financeArrears.Text = Fmt.Money(s.Arrears);
        _financeArrears.AddThemeColorOverride("font_color", Ui.Colour(s.Arrears > 0 ? "state.negative" : "text.bright"));
        FinanceValue("ratio", Fmt.Percent(s.DebtRatio(n.Gdp)));
        FinanceValue("interest_arrears", Fmt.Money(s.InterestArrears)); FinanceValue("spending_arrears", Fmt.Money(s.SpendingArrears));
        FinanceValue("obligations", Fmt.Money(s.Obligations)); FinanceValue("interest", Loc.Format("{0} a week", Fmt.Money(s.InterestPerWeek)));
        FinanceValue("service", s.RevenuePerWeek > 0 ? Fmt.Percent(s.ServiceRatio) : Tr("No current revenue"));
        FinanceValue("confidence", Fmt.Percent(n.CreditConfidence)); _financeGauges["confidence"].Value = n.CreditConfidence;
        FinanceValue("spread", Fmt.Percent(n.DebtSpreadAnnual));
        FinanceValue("missed", Loc.Format("{0} / {1} weeks", n.DebtMissedWeeks, _campaign.Defines.DebtDefaultGraceWeeks));
        FinanceValue("balance", Fmt.MoneyDelta(s.BudgetBalance), s.BudgetBalance < 0 ? "state.negative" : "state.positive");
        FinanceValue("primary", Fmt.MoneyDelta(s.PrimaryBalance), s.PrimaryBalance < 0 ? "state.negative" : "state.positive");
        FinanceValue("runway", s.CashRunwayWeeks is { } weeks ? Loc.Format("{0:0.#} weeks", weeks) : Tr("Cash not declining"));
        FinanceValue("capacity", Fmt.Money(s.Capacity)); FinanceValue("credit", Fmt.Money(s.NewCredit));
        FinanceValue("issued", Fmt.Money(s.CreditUsedThisWeek)); FinanceValue("gap", Fmt.Money(s.FundingGapAtCurrentLimits), s.FundingGapAtCurrentLimits > 0 ? "state.warning" : "text.bright");
        FinanceValue("restructuring", n.DebtCondition == DebtCondition.Restructuring
            ? Loc.Format("{0} / {1} weeks", n.DebtRestructuringProgress, _campaign.Defines.DebtRestructuringWeeks) : "—");
        var amount = (float)_financeAmount.Value;
        foreach (var (id, button) in _financeActions)
        {
            var reason = FinanceActionRefusal(n, s, id, amount);
            button.Disabled = reason.Length > 0;
            button.Text = Tr(id switch { "borrow" => "Issue bonds", "repay" => "Repay debt", "arrears" => "Pay overdue bills", _ => "Negotiate restructuring" });
            if (_financePendingAction == id) button.Text = Tr("Confirm") + " · " + button.Text;
            button.TooltipText = Ui.Tip(reason.Length > 0 ? Tr(reason) : "");
            StyleButton(button, _financePendingAction == id);
        }
        var borrowing = MathF.Min(amount, s.NewCredit);
        var cashAfterBorrow = s.Cash + borrowing;
        var repayment = MathF.Min(amount, MathF.Min(s.Cash, s.Principal));
        var overdue = MathF.Min(amount, MathF.Min(s.Cash, s.Arrears));
        _financePreview.Text = Loc.Format("Feasible borrowing {0}: cash {1}, principal {2}\nFeasible principal repayment {3}: cash {4}, principal {5}\nFeasible overdue payment {6}: cash {7}, arrears {8}\nIf a feasible amount is smaller than your request, reduce the input before confirming.",
            Fmt.Money(borrowing), Fmt.Money(cashAfterBorrow), Fmt.Money(s.Principal + borrowing),
            Fmt.Money(repayment), Fmt.Money(s.Cash - repayment), Fmt.Money(s.Principal - repayment),
            Fmt.Money(overdue), Fmt.Money(s.Cash - overdue), Fmt.Money(s.Arrears - overdue));
    }

    private string FinanceActionRefusal(Nation n, SovereignBalanceSheet sheet, string action, float amount)
    {
        if (_observerMode) return "Observer mode cannot issue financial orders.";
        if (!_campaign.FeatureEnabled("public_debt")) return "Debt mechanic disabled; saved obligations remain";
        if (!MechanicFeatures.Allows(_campaign, new ManageDebtCommand { IssuerId = n.Id, Action = action }))
            return "This action is disabled in this campaign.";
        if (n.Movement is { Landless: true }) return "A landless movement has no sovereign bond market.";
        if (action == "restructure")
        {
            if (n.DebtCondition is not (DebtCondition.Default or DebtCondition.Insolvent)) return "Restructuring requires default or insolvency.";
            if (_campaign.ElapsedDays - n.DebtRestructuringLastDay < 365) return "The restructuring cooldown has not expired.";
            return "";
        }
        if (!float.IsFinite(amount) || amount <= 0) return "Enter an amount above zero.";
        if (action == "borrow" && sheet.NewCredit < amount) return "Requested issue exceeds available credit. Reduce the amount; the weekly allowance is shared.";
        if (action == "repay" && (sheet.Cash < amount || sheet.Principal < amount)) return "Principal repayment must fit both available cash and outstanding principal.";
        if (action == "arrears" && (sheet.Cash < amount || sheet.Arrears < amount)) return "Overdue payment must fit both available cash and unpaid bills.";
        return "";
    }

    private void ReviewFinanceAction(string action)
    {
        var n = _campaign.Player;
        var sheet = SovereignBalanceSheet.Read(_campaign, n);
        var amount = (float)_financeAmount.Value;
        var refusal = FinanceActionRefusal(n, sheet, action, amount);
        if (refusal.Length > 0)
        {
            _financeFeedback.Text = Tr(refusal);
            _financePendingAction = "";
            PlayStrategicCue("action_denied");
            return;
        }
        var now = new FinanceConfirmation(n.Id, _campaign.ElapsedDays, amount, sheet.Cash, sheet.Principal,
            sheet.Arrears, sheet.NewCredit, n.DebtCondition);
        if (_financePendingAction != action || now != _financeConfirmation)
        {
            _financePendingAction = action; _financeConfirmation = now;
            _financeFeedback.Text = action == "restructure" ? Loc.Format(
                "Review: at least {0} funded economic weeks, fees and a lasting confidence penalty. No cash grant. Click again to begin negotiations.", _campaign.Defines.DebtRestructuringWeeks)
                : Loc.Format("{0}: {1}. Press again to confirm.", Tr(action switch
                    { "borrow" => "Issue bonds", "repay" => "Repay debt", _ => "Pay overdue bills" }), Fmt.Money(amount));
            RefreshSovereignFinanceWorkspace(); return;
        }
        _campaign.Commands.Enqueue(new ManageDebtCommand { IssuerId = n.Id, Action = action, Amount = amount });
        _campaign.Commands.ApplyAll(_campaign);
        _financePendingAction = "";
        EconomySystem.Explain(_campaign);
        PlayStrategicCue("save_complete");
        _financeFeedback.Text = Tr("Financial operation completed. Budget balance excludes bond proceeds and principal repayments.");
        RefreshSovereignFinanceWorkspace(); Refresh();
    }
}
