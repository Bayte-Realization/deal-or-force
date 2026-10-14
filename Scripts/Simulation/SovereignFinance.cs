using System;

namespace DealOrForce.Simulation;

public enum DebtCondition { Current, Stressed, Default, Insolvent, Restructuring }

public sealed partial class Defines
{
    public float DebtBaseCapacity { get; set; } = .35f;
    public float DebtConfidenceCapacity { get; set; } = 1.1f;
    public float DebtWeeklyIssueShare { get; set; } = .0015f;
    public float DebtMaximumSpread { get; set; } = .16f;
    public int DebtDefaultGraceWeeks { get; set; } = 2;
    public int DebtInsolvencyWeeks { get; set; } = 12;
    public int DebtRestructuringWeeks { get; set; } = 26;
    public float DebtHaircut { get; set; } = .30f;
    public float DebtRestructuringFeeShare { get; set; } = .00002f;
}

public sealed partial class Nation
{
    public bool DebtInitialized;
    public bool DebtMechanicActive;
    public float PublicDebt;
    public float DebtArrears;
    public float PaymentArrears;
    public float CreditConfidence = .75f;
    public float DebtSpreadAnnual;
    public float DebtIssuedWeekly;
    public float DebtInterestWeekly;
    public int DebtMissedWeeks;
    public int DebtCrisisWeeks;
    public int DebtRestructuringProgress;
    public int DebtRestructuringLastDay = -9999;
    public DebtCondition DebtCondition;
}

/// <summary>A deliberately aggregate sovereign balance sheet, not a bond-by-bond market.</summary>
public static class SovereignFinance
{
    /// <summary>Import an overdraft as already-incurred principal, without advancing the credit review.</summary>
    public static void NormalizeAccounts(Campaign c, Nation n)
    {
        n.DebtMechanicActive = c.FeatureEnabled("public_debt");
        if (!n.DebtMechanicActive) return;
        // A previously initialized save can also contain a legacy scripted cash charge,
        // or an overdraft accumulated while this feature was disabled. Clearing the cash
        // balance, not just an initialization flag, makes the conversion idempotent.
        if (n.Treasury < 0 && float.IsFinite(n.Treasury) &&
            float.IsFinite(n.PublicDebt) && float.IsFinite(n.PublicDebt - n.Treasury))
        {
            n.PublicDebt -= n.Treasury;
            n.Treasury = 0;
        }
        n.DebtInitialized = true;
    }

    public static void NormalizeAccounts(Campaign c)
    {
        foreach (var n in c.Nations) NormalizeAccounts(c, n);
    }

    public static void Prepare(Campaign c, Nation n, bool review = true)
    {
        // Explaining the budget to a player (review: false) reads the books as they stand. It
        // used to open them as well - booking an overdraft as debt - and to set the week's
        // spread, so the world depended on when the interface last drew: in multiplayer, on
        // whose screen. The week's own review does both.
        if (!review) return;
        NormalizeAccounts(c, n);
        if (!n.DebtMechanicActive) return;
        var ratio = n.PublicDebt / MathF.Max(.01f, n.Gdp);
        var target = M.Clamp01(.35f + n.Stability * .55f - MathF.Max(0, n.Inflation - .04f) * .7f
            - ratio * .22f - (n.DebtArrears + n.PaymentArrears) / MathF.Max(.01f, n.Gdp) * 3f);
        if (n.DebtCondition >= DebtCondition.Default) target *= .35f;
        if (review) n.CreditConfidence += (target - n.CreditConfidence) * .08f;
        n.DebtSpreadAnnual = Math.Clamp(((1 - n.CreditConfidence) * .05f + MathF.Max(0, ratio - .6f) * .04f) *
            NationalModifiers.Factor(n, "borrowing_cost"), 0, Math.Clamp(c.Defines.DebtMaximumSpread, 0, 1));
        if (review) n.DebtIssuedWeekly = 0;
    }

    // Disabled mechanics keep the saved principal. An overdraft is additional exposure,
    // not an alternative value that hides the bonds issued before the switch was changed.
    public static float Outstanding(Nation n) => MathF.Max(0, n.PublicDebt) + MathF.Max(0, -n.Treasury);

    public static float Capacity(Campaign c, Nation n) => MathF.Max(0, n.Gdp) *
        MathF.Max(0, c.Defines.DebtBaseCapacity + MathF.Max(0, c.Defines.DebtConfidenceCapacity) * n.CreditConfidence) *
        NationalModifiers.Factor(n, "credit_capacity");

    public static float AvailableCredit(Campaign c, Nation n)
    {
        if (!c.FeatureEnabled("public_debt") || n.DebtCondition >= DebtCondition.Default) return 0;
        return MathF.Max(0, MathF.Min(Capacity(c, n) - n.PublicDebt,
            n.Gdp * Math.Clamp(c.Defines.DebtWeeklyIssueShare, 0, .05f) - n.DebtIssuedWeekly));
    }

    public static float Borrow(Campaign c, Nation n, float requested)
    {
        if (!float.IsFinite(requested) || requested <= 0) return 0;
        NormalizeAccounts(c, n);
        var raised = MathF.Min(requested, AvailableCredit(c, n));
        n.PublicDebt += raised; n.Treasury += raised; n.DebtIssuedWeekly += raised;
        return raised;
    }

    public static void Settle(Campaign c, Nation n, float balance, float interest)
    {
        if (!n.DebtMechanicActive) { n.Treasury += balance; return; }
        n.DebtInterestWeekly = MathF.Max(0, interest);
        n.Treasury += balance;
        // Borrow sees nonnegative cash: this is new spending, not a legacy overdraft.
        // NormalizeAccounts must never turn this week's unfunded bills into unlimited credit.
        if (n.Treasury < 0)
        {
            var shortfall = -n.Treasury;
            n.Treasury = 0;
            Borrow(c, n, shortfall);
            n.Treasury -= shortfall;
        }
        var unpaid = MathF.Max(0, -n.Treasury);
        n.Treasury = MathF.Max(0, n.Treasury);
        // Essential primary spending has priority; any unfunded interest becomes contractual arrears.
        var missedInterest = MathF.Min(unpaid, n.DebtInterestWeekly);
        n.DebtArrears += missedInterest;
        n.PaymentArrears += unpaid - missedInterest;
        PayArrears(n, n.Treasury);
        var tolerance = MathF.Max(.000001f, n.Gdp * .0000001f);
        n.DebtMissedWeeks = n.DebtArrears > tolerance ? n.DebtMissedWeeks + 1 : 0;
        var crisis = n.DebtArrears + n.PaymentArrears > tolerance;
        n.DebtCrisisWeeks = crisis ? n.DebtCrisisWeeks + 1 : Math.Max(0, n.DebtCrisisWeeks - 2);
        var revenue = MathF.Max(tolerance, n.WeeklyTaxIncome + n.WeeklyTariffIncome);
        var unsustainable = n.PublicDebt > Capacity(c, n) && n.DebtInterestWeekly > revenue * .30f ||
            n.DebtArrears + n.PaymentArrears > revenue * 4f;
        var before = n.DebtCondition;
        if (before == DebtCondition.Restructuring)
        {
            var fee = MathF.Max(0, n.Gdp * c.Defines.DebtRestructuringFeeShare);
            // Fiscal adjustment and funding take time; an unfunded process does not magically complete.
            if (n.Treasury >= fee && balance + interest >= -revenue * .15f)
            {
                n.Treasury -= fee;
                n.DebtRestructuringProgress++;
            }
            if (n.DebtRestructuringProgress >= Math.Clamp(c.Defines.DebtRestructuringWeeks, 4, 260))
            {
                n.PublicDebt = (n.PublicDebt + n.DebtArrears) * (1 - Math.Clamp(c.Defines.DebtHaircut, 0, .7f));
                n.DebtArrears = 0; n.DebtMissedWeeks = 0;
                n.CreditConfidence = MathF.Min(n.CreditConfidence, .35f);
                n.DebtCondition = n.PaymentArrears > tolerance ? DebtCondition.Stressed : DebtCondition.Current;
                n.DebtRestructuringLastDay = c.ElapsedDays;
                c.Chronicle.Record(c, "debt_restructured", n.Id);
            }
        }
        else if (n.DebtCrisisWeeks >= Math.Clamp(c.Defines.DebtInsolvencyWeeks, 4, 104) && unsustainable)
            n.DebtCondition = DebtCondition.Insolvent;
        else if (n.DebtMissedWeeks >= Math.Clamp(c.Defines.DebtDefaultGraceWeeks, 1, 26))
            n.DebtCondition = DebtCondition.Default;
        else n.DebtCondition = crisis || n.CreditConfidence < .4f ? DebtCondition.Stressed : DebtCondition.Current;
        if (n.DebtCondition != before)
        {
            if (n.DebtCondition == DebtCondition.Default) c.Chronicle.Record(c, "sovereign_default", n.Id);
            if (n.DebtCondition == DebtCondition.Insolvent) c.Chronicle.Record(c, "sovereign_insolvency", n.Id);
        }
        var distress = n.DebtCondition >= DebtCondition.Default ? .002f : crisis ? .0005f : 0;
        n.StabilityGovernance = M.Clamp(n.StabilityGovernance - distress, -.20f, .20f);
        n.Productivity = MathF.Max(.05f, n.Productivity * (1 - distress));
        // A conservative AI negotiates rather than spending a decade in an unresolvable crisis.
        if (!n.IsHuman && n.DebtCondition == DebtCondition.Insolvent) StartRestructuring(c, n);
    }

    public static bool StartRestructuring(Campaign c, Nation n)
    {
        if (!c.FeatureEnabled("public_debt") || n.DebtCondition is not (DebtCondition.Default or DebtCondition.Insolvent) ||
            c.ElapsedDays - n.DebtRestructuringLastDay < 365) return false;
        n.DebtCondition = DebtCondition.Restructuring; n.DebtRestructuringProgress = 0;
        n.CreditConfidence = MathF.Min(n.CreditConfidence, .25f);
        c.Chronicle.Record(c, "debt_negotiations", n.Id);
        return true;
    }

    public static float Repay(Nation n, float requested)
    {
        if (!float.IsFinite(requested) || requested <= 0) return 0;
        var paid = MathF.Min(requested, MathF.Min(MathF.Max(0, n.Treasury), MathF.Max(0, n.PublicDebt)));
        n.PublicDebt -= paid; n.Treasury -= paid;
        return paid;
    }

    /// <summary>Service overdue interest first, then unpaid primary bills; never issue cash.</summary>
    public static float PayArrears(Nation n, float requested)
    {
        if (!float.IsFinite(requested) || requested <= 0) return 0;
        var available = MathF.Min(requested, MathF.Max(0, n.Treasury));
        var interest = MathF.Min(available, MathF.Max(0, n.DebtArrears));
        var bills = MathF.Min(available - interest, MathF.Max(0, n.PaymentArrears));
        var paid = interest + bills;
        n.DebtArrears -= interest; n.PaymentArrears -= bills; n.Treasury -= paid;
        return paid;
    }

    /// <summary>A voluntary whole-state union inherits obligations as well as savings.</summary>
    public static void MergeAccounts(Campaign c, Nation successor, Nation absorbed)
    {
        NormalizeAccounts(c, successor); NormalizeAccounts(c, absorbed);
        successor.Treasury += absorbed.Treasury; absorbed.Treasury = 0;
        successor.PublicDebt += absorbed.PublicDebt; absorbed.PublicDebt = 0;
        successor.DebtArrears += absorbed.DebtArrears; absorbed.DebtArrears = 0;
        successor.PaymentArrears += absorbed.PaymentArrears; absorbed.PaymentArrears = 0;
        successor.DebtIssuedWeekly += absorbed.DebtIssuedWeekly; absorbed.DebtIssuedWeekly = 0;
        successor.DebtMissedWeeks = Math.Max(successor.DebtMissedWeeks, absorbed.DebtMissedWeeks);
        successor.DebtCrisisWeeks = Math.Max(successor.DebtCrisisWeeks, absorbed.DebtCrisisWeeks);
        successor.CreditConfidence = MathF.Min(successor.CreditConfidence, absorbed.CreditConfidence);
        successor.DebtSpreadAnnual = MathF.Max(successor.DebtSpreadAnnual, absorbed.DebtSpreadAnnual);
        // A restructuring agreement is specific to its debtor. A union must negotiate
        // its new balance sheet again rather than inheriting nearly-complete free relief.
        var incoming = absorbed.DebtCondition == DebtCondition.Restructuring ? DebtCondition.Default : absorbed.DebtCondition;
        var existing = successor.DebtCondition == DebtCondition.Restructuring ? DebtCondition.Default : successor.DebtCondition;
        successor.DebtCondition = incoming > existing ? incoming : existing;
        successor.DebtRestructuringProgress = 0;
        successor.DebtRestructuringLastDay = Math.Max(successor.DebtRestructuringLastDay, absorbed.DebtRestructuringLastDay);
    }
}

public sealed class ManageDebtCommand : ICampaignCommand
{
    public int IssuerId { get; set; }
    public string Action { get; set; } = "";
    public float Amount { get; set; }
    public void Apply(Campaign c)
    {
        var n = c.NationById(IssuerId);
        if (n is null || n.Movement is { Landless: true } || !c.FeatureEnabled("public_debt") ||
            !MechanicFeatures.Allows(c, this)) return;
        SovereignFinance.NormalizeAccounts(c, n);
        if (Action == "borrow") SovereignFinance.Borrow(c, n, Amount);
        else if (Action == "repay") SovereignFinance.Repay(n, Amount);
        else if (Action == "arrears") SovereignFinance.PayArrears(n, Amount);
        else if (Action == "restructure") SovereignFinance.StartRestructuring(c, n);
        c.MarkStrategicChange();
    }
}
