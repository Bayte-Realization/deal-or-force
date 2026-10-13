using System;
using System.IO;
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
        n.Treasury = -2; n.DebtArrears = .4f; n.PaymentArrears = .3f;
        n.DebtCondition = DebtCondition.Default; n.DebtMissedWeeks = 9;
        n.DebtIssuedWeekly = .07f;
        var confidence = n.CreditConfidence;
        SovereignFinance.NormalizeAccounts(c);
        Check(n.PublicDebt == 7 && n.Treasury == 0 && n.DebtArrears == .4f && n.PaymentArrears == .3f,
            "initialized legacy overdraft adds to principal without erasing either arrears ledger");
        Check(n.DebtMissedWeeks == 9 && n.DebtCondition == DebtCondition.Default &&
            n.DebtIssuedWeekly == .07f && n.CreditConfidence == confidence,
            "migration advances no review, grace clock or weekly credit allowance");
        SovereignFinance.NormalizeAccounts(c);
        Check(n.PublicDebt == 7, "second normalization cannot charge the same deficit twice");
        n.DebtArrears = n.PaymentArrears = 0; n.DebtCondition = DebtCondition.Current;
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
        Check(n.Treasury == -4 && SovereignFinance.Outstanding(n) == n.PublicDebt + 4,
            "feature disable retains legacy settlement and never hides existing principal");
        var dormantPrincipal = n.PublicDebt;
        c.SetFeature("public_debt", true);
        Check(n.Treasury == 0 && n.PublicDebt == dormantPrincipal + 4,
            "reenabling the mechanic migrates only the additional legacy overdraft immediately");
        c.SetFeature("public_debt", true);
        Check(n.PublicDebt == dormantPrincipal + 4, "repeating the feature switch is idempotent");
        VerifyWorldMigration();
        var late = c.AddNation(new Nation { Tag = "LATE", Treasury = -2, PublicDebt = 3 });
        Check(late.DebtInitialized && late.Treasury == 0 && late.PublicDebt == 5,
            "a state created after bookmark initialization immediately receives separate sovereign books");
        VerifyAccountOperations();
        VerifyWorkspaceTranslations();
    }

    static void VerifyWorldMigration()
    {
        var c = new Campaign(714) { ScenarioId = "debt_fixture" };
        c.Nations.Add(new Nation { Id = 0, MapIndex = 501, Tag = "HUM", Treasury = 10, PublicDebt = 30, Gdp = 100,
            OutputScale = 1, IsHuman = true, DebtInitialized = true });
        // OLD was saved before public debt existed - its books never opened - so its overdraft is
        // legacy and the load opens them. MID's books are open and it is part-way through a week:
        // its overdraft is spending since the last settlement, which its next economic week books,
        // and a load that booked it early made the loaded world differ from the saved one.
        c.Nations.Add(new Nation { Id = 1, MapIndex = 502, Tag = "OLD", Treasury = -5, PublicDebt = 20, Gdp = 100,
            OutputScale = 1, DebtInitialized = false, DebtCondition = DebtCondition.Default,
            DebtArrears = 2, PaymentArrears = 3, DebtMissedWeeks = 8, DebtIssuedWeekly = .12f });
        c.States.Add(new ScenarioState { MapIndex = 3001, NationMapIndex = 501, Name = "One" });
        c.Nations.Add(new Nation { Id = 2, MapIndex = 503, Tag = "MID", Treasury = -1.5f, PublicDebt = 7, Gdp = 100,
            OutputScale = 1, DebtInitialized = true });
        c.States.Add(new ScenarioState { MapIndex = 3002, NationMapIndex = 502, Name = "Two" });
        c.States.Add(new ScenarioState { MapIndex = 3003, NationMapIndex = 503, Name = "Three" });
        var map = ProvinceTopology.Build(new ushort[] { 3001, 3001, 3002, 3002, 3003, 3003 },
            new ushort[] { 501, 501, 502, 502, 503, 503 }, 6, 1, _ => false);
        c.AttachProvinceTopology(map); c.ConfigureSinglePlayer(0);
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "finance"), "finance");
        Check(restored.Success, "the real save codec can restore a campaign containing legacy deficits: " + restored.Warning);
        var old = restored.Campaign.NationByTag("OLD");
        Check(old.Treasury == 0 && old.PublicDebt == 25 && old.DebtArrears == 2 && old.PaymentArrears == 3 &&
            old.DebtMissedWeeks == 8 && old.DebtIssuedWeekly == .12f,
            "load migrates nonplayer countries before their first economic week and retains crisis history");
        var midWeek = restored.Campaign.NationByTag("MID");
        Check(midWeek.Treasury == -1.5f && midWeek.PublicDebt == 7,
            "a country with open books loads with the week's overdraft it was saved with");
        restored.Campaign.AttachProvinceTopology(map);
        var twice = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(restored.Campaign, "finance"), "finance");
        Check(twice.Success && twice.Campaign.NationByTag("OLD").PublicDebt == 25,
            "save-load-save-load does not duplicate legacy principal");
        var p = c.Nations[0];
        SovereignFinance.Prepare(c, p); p.Treasury = 0; p.PublicDebt = 200;
        var debt = p.PublicDebt;
        SovereignFinance.Settle(c, p, -10, 1);
        Check(p.PublicDebt == debt && p.DebtArrears == 1 && p.PaymentArrears == 9 && p.Treasury == 0,
            "unfundable new deficits become bills and missed interest, never unlimited migrated loans");
        p.Treasury = float.NegativeInfinity; SovereignFinance.NormalizeAccounts(c, p);
        Check(float.IsNegativeInfinity(p.Treasury) && p.PublicDebt == debt,
            "migration leaves corrupt nonfinite values visible to numeric-integrity protection");
    }

    static void VerifyAccountOperations()
    {
        var c = new Campaign(715);
        var a = new Nation { Id = 0, Tag = "FIN", Gdp = 100, Treasury = 4, PublicDebt = 50,
            DebtArrears = 3, PaymentArrears = 5, DebtCondition = DebtCondition.Default, DebtMissedWeeks = 6 };
        var b = new Nation { Id = 1, Tag = "JOIN", Gdp = 50, Treasury = -2, PublicDebt = 10,
            DebtArrears = 1, PaymentArrears = 2, DebtCondition = DebtCondition.Restructuring,
            DebtRestructuringProgress = 25, CreditConfidence = .2f, DebtIssuedWeekly = .1f };
        c.Nations.Add(a); c.Nations.Add(b); SovereignFinance.NormalizeAccounts(c);
        Check(SovereignFinance.PayArrears(a, 4) == 4 && a.DebtArrears == 0 && a.PaymentArrears == 4 &&
            a.PublicDebt == 50 && a.Treasury == 0, "manual overdue payment prioritizes interest without repaying principal or printing money");
        Check(SovereignFinance.PayArrears(a, float.NaN) == 0 && SovereignFinance.PayArrears(a, 100) == 0,
            "overdue payments respect finite requests and actual cash");
        a.Treasury = 4;
        SovereignFinance.MergeAccounts(c, a, b);
        Check(a.PublicDebt == 62 && a.DebtArrears == 1 && a.PaymentArrears == 6 && a.Treasury == 4 &&
            b.PublicDebt == 0 && b.DebtArrears == 0 && b.PaymentArrears == 0 && b.Treasury == 0,
            "state union conserves debt, both arrears ledgers and savings including legacy deficits");
        Check(a.DebtCondition == DebtCondition.Default && a.DebtMissedWeeks == 6 &&
            a.DebtRestructuringProgress == 0 && a.CreditConfidence == .2f && a.DebtIssuedWeekly == .1f,
            "union retains credit distress and cannot inherit a nearly completed free haircut");
        var effect = new ScriptedEffects.Effect(); effect.Add("treasury", -6); effect.Add("treasury", 2);
        effect.Apply(c, a);
        Check(a.Treasury == 0 && a.PublicDebt == 62, "compound charges and refunds finish before any overdraft conversion");
        effect = new ScriptedEffects.Effect(); effect.Add("treasury", -3); effect.Apply(c, a);
        Check(a.Treasury == 0 && a.PublicDebt == 65 && a.DebtIssuedWeekly == .1f,
            "legacy cash effects convert immediately without resetting the bond issuance allowance");
        var before = CampaignFingerprint.Of(c);
        var sheet = SovereignBalanceSheet.Read(c, a);
        Check(before == CampaignFingerprint.Of(c) && sheet.Principal == 65 && sheet.Obligations == 72,
            "inspecting the financial projection is read-only and distinguishes principal from unpaid bills");
        sheet = new SovereignBalanceSheet(2, 50, 1, 3, -5, 1, 4, 100, 1, 0);
        Check(sheet.FinancingNeed == 3 && sheet.ExpectedNewPrincipal == 1 && sheet.FundingGapAtCurrentLimits == 2 &&
            sheet.PrimaryBalance == -4 && sheet.CashRunwayWeeks == .4f && sheet.ServiceRatio == .25f,
            "liquidity, primary deficit, interest burden and the funding gap have distinct units");
        sheet = sheet with { BudgetBalance = 0 };
        Check(sheet.CashRunwayWeeks is null && sheet.FinancingNeed == 0,
            "a balanced budget reports no exhaustion horizon instead of infinity");
        var orders = new Campaign(716);
        var mover = orders.AddNation(new Nation { Tag = "OFFICE", Gdp = 100, Treasury = 1,
            Movement = new MovementState { Landless = true } });
        new ManageDebtCommand { IssuerId = mover.Id, Action = "borrow", Amount = .02f }.Apply(orders);
        Check(mover.PublicDebt == 0 && mover.Treasury == 1,
            "the real command rejects sovereign issuance by a landless movement, not just its UI");
        mover.Movement = null;
        orders.Defines.FeatureCommands[nameof(ManageDebtCommand)] = "finance_orders";
        orders.SetFeature("finance_orders", false);
        new ManageDebtCommand { IssuerId = mover.Id, Action = "borrow", Amount = .02f }.Apply(orders);
        Check(mover.PublicDebt == 0 && mover.Treasury == 1,
            "financial commands respect an author's additional feature gate");
    }

    static void VerifyWorkspaceTranslations()
    {
        var language = Loc.Language;
        try
        {
            Loc.ResetCatalog(); Loc.Language = "russian";
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(),
                "localization", "russian", "152_sovereign_workspace.json")));
            var count = 0;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                Check(Loc.Get(entry.Name) == entry.Value.GetString() && entry.Name != entry.Value.GetString(),
                    "financial workspace translation resolves: " + entry.Name.Split('\n')[0]);
                count++;
            }
            Check(count >= 50, "headline, cash-flow explanations, action previews and refusal reasons are localized");
        }
        finally { Loc.Language = language; }
    }
}
