using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void ShowSovereignFinance(Nation n)
    {
        if (!_campaign.FeatureEnabled("public_debt")) return;
        DetailHeading("SOVEREIGN FINANCE");
        DetailRow("Public debt", Fmt.Money(n.PublicDebt),
            "Outstanding borrowing, separate from available cash. Interest remains due even when the treasury is positive.");
        DetailRow("Debt / GDP", Fmt.Percent(n.PublicDebt / MathF.Max(.01f, n.Gdp)),
            "This is an exposure measure, not an automatic default threshold.");
        DetailRow("Credit confidence", Fmt.Percent(n.CreditConfidence),
            "Gradually reflects stability, inflation, debt burden and unpaid obligations; affects lending capacity and the risk premium.");
        DetailRow("Risk premium", Fmt.Percent(n.DebtSpreadAnnual),
            "Annual sovereign spread added to the base interest rate.");
        DetailRow("Debt service", Fmt.Money(n.DebtInterestWeekly), "Interest due in the last economic week.");
        DetailRow("Debt arrears", Fmt.Money(n.DebtArrears), "Unpaid debt interest; persistent missed payments cause default.");
        DetailRow("Public payment arrears", Fmt.Money(n.PaymentArrears), "Unpaid primary spending. Distinct from contractual debt default.");
        string Condition() => n.DebtCondition switch
        {
            DebtCondition.Stressed => "Financial stress", DebtCondition.Default => "Sovereign default",
            DebtCondition.Insolvent => "State insolvency", DebtCondition.Restructuring => "Debt restructuring",
            _ => "Payments current"
        };
        DetailRow("Debt condition", Tr(Condition()),
            "Default follows missed payments after grace. Insolvency requires sustained distress; no country is liquidated.",
            n.DebtCondition >= DebtCondition.Default ? "state.negative" : "text.bright");
        var credit = SovereignFinance.AvailableCredit(_campaign, n);
        DetailRow("Available new credit", Fmt.Money(credit), "Lending is limited both by debt capacity and a shared weekly issuance allowance.");
        void Execute(string action, float amount = 0)
        {
            _campaign.Commands.Enqueue(new ManageDebtCommand { IssuerId = n.Id, Action = action, Amount = amount });
            _campaign.Commands.ApplyAll(_campaign); EconomySystem.Explain(_campaign); ShowEconomyScreen();
        }
        var amount = n.Gdp * .001f;
        var row = new HBoxContainer();
        var borrow = ActionButton("Issue bonds", () => Execute("borrow", amount), 0, 34,
            iconPath: "res://game/gfx/interface/icons/icon_coins.png");
        borrow.Disabled = credit <= .000001f;
        borrow.TooltipText = Ui.Tip(Simulation.Loc.Format("Raise up to {0}; constrained by remaining weekly credit.", Fmt.Money(amount)));
        row.AddChild(borrow);
        var repay = ActionButton("Repay debt", () => Execute("repay", amount), 0, 34,
            iconPath: "res://game/gfx/interface/icons/icon_treasury.png");
        repay.Disabled = n.PublicDebt <= 0 || n.Treasury <= 0;
        repay.TooltipText = Ui.Tip(Simulation.Loc.Format("Use up to {0} of available cash to repay principal.", Fmt.Money(amount)));
        row.AddChild(repay); _sideDetail.AddChild(row);
        var negotiate = ActionButton("Negotiate restructuring", () => Execute("restructure"), 0, 36,
            iconPath: "res://game/gfx/interface/icons/icon_treaty.png");
        negotiate.Disabled = n.DebtCondition is not (DebtCondition.Default or DebtCondition.Insolvent) ||
            _campaign.ElapsedDays - n.DebtRestructuringLastDay < 365;
        negotiate.TooltipText = Ui.Tip(Simulation.Loc.Format(
            "Requires default or insolvency. At least {0} funded weeks of fiscal adjustment; partial write-down, no cash grant, lasting confidence loss.",
            _campaign.Defines.DebtRestructuringWeeks));
        _sideDetail.AddChild(negotiate);
        if (n.DebtCondition == DebtCondition.Restructuring)
            DetailRow("Negotiation progress", $"{n.DebtRestructuringProgress} / {_campaign.Defines.DebtRestructuringWeeks}",
                "Progress pauses without funding or sufficient improvement in the primary budget.");
    }
}
