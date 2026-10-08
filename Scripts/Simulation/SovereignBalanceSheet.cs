using System;

namespace DealOrForce.Simulation;

/// <summary>A read-only projection of fiscal stocks and the current budget, shared by UI and reports.</summary>
public readonly record struct SovereignBalanceSheet(
    float Cash, float Principal, float InterestArrears, float SpendingArrears,
    float BudgetBalance, float InterestPerWeek, float RevenuePerWeek,
    float Capacity, float NewCredit, float CreditUsedThisWeek)
{
    public float Arrears => InterestArrears + SpendingArrears;
    public float Obligations => Principal + Arrears;
    public float DebtRatio(float gdp) => Principal / MathF.Max(.01f, gdp);
    public float ServiceRatio => RevenuePerWeek > 0 ? InterestPerWeek / RevenuePerWeek : 0;
    public float PrimaryBalance => BudgetBalance + InterestPerWeek;
    public float FinancingNeed => MathF.Max(0, -BudgetBalance - Cash);
    public float UnfundedNextWeek => MathF.Max(0, FinancingNeed - NewCredit);
    public float CashAfterWeek => MathF.Max(0, Cash + BudgetBalance);
    public float ExpectedNewPrincipal => MathF.Min(FinancingNeed, NewCredit);
    public float? CashRunwayWeeks => BudgetBalance < 0 ? Cash / -BudgetBalance : null;

    public static SovereignBalanceSheet Read(Campaign c, Nation n)
    {
        var war = false;
        foreach (var conflict in c.Wars)
            if (!conflict.Ended && conflict.Involves(n.Id)) { war = true; break; }
        var output = n.Gdp / 52f;
        var budget = BudgetCore.Evaluate(n, output, war);
        var interest = 0f;
        foreach (var line in BudgetCore.All)
            if (line.Charge == BudgetBasis.Debt && line.Side == BudgetSide.Expense)
                interest += BudgetCore.Amount(n, line, output, war);
        return new(MathF.Max(0, n.Treasury), SovereignFinance.Outstanding(n),
            n.DebtArrears, n.PaymentArrears, budget.Balance, interest,
            MathF.Max(0, budget.Revenue), SovereignFinance.Capacity(c, n),
            SovereignFinance.AvailableCredit(c, n), n.DebtIssuedWeekly);
    }

    public static string ConditionName(DebtCondition condition) => condition switch
    {
        DebtCondition.Stressed => "Financial stress", DebtCondition.Default => "Sovereign default",
        DebtCondition.Insolvent => "State insolvency", DebtCondition.Restructuring => "Debt restructuring",
        _ => "Payments current"
    };
}
