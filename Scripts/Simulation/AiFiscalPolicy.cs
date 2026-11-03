using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Cash runway protected before discretionary AI gifts, purchases or projects.</summary>
    public int AiFiscalReserveWeeks { get; set; } = 8;
}

public enum AiFiscalReason { Ready, InvalidAccounts, OverdueBills, DebtWorkout, ShortRunway }

/// <summary>A live spending envelope, not another balance sheet or a saved AI memory.</summary>
public readonly record struct AiFiscalAssessment(
    AiFiscalReason Reason, float Cash, float WeeklyBalance, float Reserve,
    float Arrears, float ConstructionPayments, float DiplomaticPayments)
{
    public bool CanSpend => Reason == AiFiscalReason.Ready;
    public float DiscretionaryCash => CanSpend ? MathF.Max(0, Cash - Reserve) : 0;
}

/// <summary>
/// One fiscal test for the cabinet and foreign ministry. Principal is not subtracted from
/// cash: its servicing is already in the live budget. Existing unpaid bills are reserved
/// once, separately from the future flow, and construction is outside that weekly budget.
/// Reading this assessment never normalizes accounts, advances debt review or draws RNG.
/// </summary>
public static class AiFiscalPolicy
{
    public static bool AtWar(Campaign c, Nation n)
    {
        if (c is null || n is null) return false;
        foreach (var war in c.Wars)
            if (!war.Ended && war.Involves(n.Id)) return true;
        return false;
    }

    public static AiFiscalAssessment Assess(Campaign c, Nation n, float reserveOfGdp)
    {
        if (c is null || n is null || !float.IsFinite(n.Treasury) || !float.IsFinite(n.Gdp) || n.Gdp < 0 ||
            !float.IsFinite(reserveOfGdp) || reserveOfGdp < 0 || !float.IsFinite(n.PublicDebt) ||
            !float.IsFinite(n.PaymentArrears) || !float.IsFinite(n.DebtArrears) ||
            !float.IsFinite(n.ConstructionCapacity)) return Invalid();

        var budget = BudgetCore.Evaluate(n, n.Gdp / 52f, AtWar(c, n));
        if (!float.IsFinite(budget.Balance)) return Invalid();
        var weeks = Math.Clamp(c.Defines.AiFiscalReserveWeeks, 0, 104);
        var arrears = c.FeatureEnabled("public_debt")
            ? Math.Max(0d, n.PaymentArrears) + Math.Max(0d, n.DebtArrears) : 0;
        var capital = 0d;
        if (MechanicFeatures.SystemEnabled(c, nameof(ConstructionSystem))) foreach (var project in c.Construction)
        {
            if (project.NationId != n.Id) continue;
            if (!float.IsFinite(project.Cost) || !float.IsFinite(project.Paid)) return Invalid();
            capital += PaymentsWithin(c, n, project.Cost, project.Paid, project.DurationDays, weeks);
        }
        var promised = 0d;
        if (c.FeatureEnabled("deals")) foreach (var paper in c.DealOffers)
        {
            if (!paper.Pending || paper.ExpiresDay <= c.ElapsedDays || paper.FromId != n.Id) continue;
            if (paper.From is null || !float.IsFinite(paper.From.Cash) || paper.From.Cash < 0) return Invalid();
            // This is the sender's own existing promise, not an unaccepted incoming
            // demand. It remains a soft planning allowance, not an escrow or a new
            // settlement restriction. Do not pre-spend a reciprocal cheque either.
            promised += paper.From.Cash;
        }
        var cash = Math.Max(0, n.Treasury);
        // A conservative envelope for promises in the queue, not an exact completion forecast.
        var reserve = (double)n.Gdp * reserveOfGdp + arrears +
            Math.Max(0d, -budget.Balance) * weeks + capital + promised;
        if (!double.IsFinite(reserve) || reserve > float.MaxValue || capital > float.MaxValue) return Invalid();
        var tolerance = Math.Max(.000001d, n.Gdp * .0000001d);
        var reason = arrears > tolerance ? AiFiscalReason.OverdueBills :
            c.FeatureEnabled("public_debt") && n.DebtCondition >= DebtCondition.Default ? AiFiscalReason.DebtWorkout :
            cash + tolerance < reserve ? AiFiscalReason.ShortRunway : AiFiscalReason.Ready;
        return new(reason, cash, budget.Balance, (float)reserve, (float)arrears, (float)capital, (float)promised);
    }

    public static bool CanStartProject(Campaign c, Nation n, BuildingKind kind, AiFiscalAssessment assessment)
    {
        if (!assessment.CanSpend) return false;
        var definition = BuildingCatalog.Get(kind);
        var cost = ConstructionCost.Of(c, n, kind);
        var speed = NationalModifiers.Factor(n, "construction_speed");
        if (!float.IsFinite(cost) || cost < 0 || !float.IsFinite(speed) || speed <= 0) return false;
        var days = Math.Max(1, (int)MathF.Round(definition.Days / speed));
        var payment = PaymentsWithin(c, n, cost, 0, days, Math.Clamp(c.Defines.AiFiscalReserveWeeks, 0, 104));
        return assessment.DiscretionaryCash >= Math.Max(Math.Min(.05d, cost), payment);
    }

    /// <summary>What a site will draw from the treasury within the window: a day's share of its
    /// price at the country's construction pace (ConstructionCost.Pace), the rate ConstructionSystem
    /// pays it at, never more than is still owed.</summary>
    private static double PaymentsWithin(Campaign c, Nation n, float cost, float paid, int durationDays, int weeks) =>
        Math.Min(Math.Max(0d, (double)cost - paid),
            Math.Max(0d, cost) / Math.Max(1, durationDays) * ConstructionCost.Pace(c, n) * (weeks * 7));

    private static AiFiscalAssessment Invalid() => new(AiFiscalReason.InvalidAccounts, 0, 0, 0, 0, 0, 0);
}
