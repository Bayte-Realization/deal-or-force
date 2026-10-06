using System;
using System.Text.Json;
using DealOrForce.Simulation;

static class SovereignFinanceTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS finance: " + label); }
    public static void Run()
    {
        var c = new Campaign(71);
        var n = new Nation { Id = 1, Tag = "TEST", Gdp = 100, Treasury = -5, Stability = .75f, IsHuman = true };
        c.Nations.Add(n);
        SovereignFinance.Prepare(c, n);
        Check(n.PublicDebt == 5 && n.Treasury == 0, "legacy overdraft migrates to separate principal once");
        SovereignFinance.Prepare(c, n);
        Check(n.PublicDebt == 5, "migration is idempotent");
        n.Treasury = 2; n.PublicDebt = 150;
        SovereignFinance.Settle(c, n, -.1f, .1f);
        Check(n.DebtCondition == DebtCondition.Current && n.DebtArrears == 0,
            "high debt does not cause default while payments are affordable");
        var interest = BudgetCore.Amount(n, BudgetCore.Find("debt_interest"), n.Gdp / 52, false);
        n.Treasury = 90;
        Check(Math.Abs(interest - BudgetCore.Amount(n, BudgetCore.Find("debt_interest"), n.Gdp / 52, false)) < .00001,
            "positive cash does not erase interest on outstanding debt");
        n.PublicDebt = 5; n.Treasury = 0; n.DebtCondition = DebtCondition.Current;
        SovereignFinance.Prepare(c, n);
        var prior = n.PublicDebt;
        SovereignFinance.Settle(c, n, -.05f, .01f);
        Check(n.PublicDebt > prior && n.Treasury >= 0 && n.DebtArrears == 0,
            "ordinary deficits can be financed without inventing arrears");
        var allowed = SovereignFinance.AvailableCredit(c, n);
        SovereignFinance.Borrow(c, n, 100);
        Check(SovereignFinance.AvailableCredit(c, n) < .00001 && n.DebtIssuedWeekly <= .15001,
            "repeated issuance shares one weekly credit allowance");
        Check(SovereignFinance.Borrow(c, n, float.NaN) == 0 && SovereignFinance.Repay(n, -10) == 0,
            "invalid or negative money commands cannot create funds");
        n.PublicDebt = 200; n.Treasury = 0; n.WeeklyTaxIncome = .1f;
        n.DebtArrears = 0; n.PaymentArrears = 0;
        SovereignFinance.Prepare(c, n);
        SovereignFinance.Settle(c, n, -.2f, .1f);
        Check(n.DebtCondition == DebtCondition.Stressed, "first missed debt payment receives grace");
        SovereignFinance.Prepare(c, n); SovereignFinance.Settle(c, n, -.2f, .1f);
        Check(n.DebtCondition == DebtCondition.Default, "persistent missed interest creates default");
        Check(SovereignFinance.Borrow(c, n, 1) == 0, "default closes ordinary new credit");
        for (var week = 0; week < 12; week++)
        { SovereignFinance.Prepare(c, n); SovereignFinance.Settle(c, n, -.2f, .1f); }
        Check(n.DebtCondition == DebtCondition.Insolvent && c.Nations.Contains(n),
            "persistent unserviceable obligations create insolvency, not country deletion");
        Check(SovereignFinance.StartRestructuring(c, n), "insolvent state can open negotiations");
        var debtBefore = n.PublicDebt; var progress = n.DebtRestructuringProgress;
        SovereignFinance.Settle(c, n, -.2f, .1f);
        Check(n.PublicDebt == debtBefore && n.DebtRestructuringProgress == progress,
            "unfunded restructuring cannot instantly wipe debt");
        n.Treasury = 20;
        for (var week = 0; week < c.Defines.DebtRestructuringWeeks; week++)
        { SovereignFinance.Prepare(c, n); SovereignFinance.Settle(c, n, .2f, .1f); }
        Check(n.DebtCondition != DebtCondition.Restructuring && n.PublicDebt > 0 && n.PublicDebt < debtBefore,
            "funded adjustment completes partial restructuring after months");
        Check(n.CreditConfidence <= .35f, "haircut has lasting lender-confidence cost");
        var json = JsonSerializer.Serialize(n, new JsonSerializerOptions { IncludeFields = true });
        var restored = JsonSerializer.Deserialize<Nation>(json, new JsonSerializerOptions { IncludeFields = true });
        Check(restored.PublicDebt == n.PublicDebt && restored.DebtRestructuringLastDay == n.DebtRestructuringLastDay,
            "balance sheet and negotiations survive save serialization");
        var fingerprint = CampaignFingerprint.Of(c); n.PublicDebt += 1;
        Check(fingerprint != CampaignFingerprint.Of(c), "principal affects lockstep fingerprint");
        c.SetFeature("public_debt", false); n.Treasury = -3;
        SovereignFinance.Prepare(c, n); SovereignFinance.Settle(c, n, -1, .1f);
        Check(n.Treasury == -4 && SovereignFinance.Outstanding(n) == 4,
            "feature disable retains legacy settlement without destroying saved debt");
    }
}
