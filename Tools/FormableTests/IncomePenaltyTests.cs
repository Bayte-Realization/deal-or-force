using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

/// <summary>
/// Timed income penalties (IncomePenalties) and the money ledger (MoneyLedger): a consequence
/// takes a share of gross revenue for a time instead of a lump from the treasury, and every
/// treasury movement is written down a week at a time.
/// </summary>
static class IncomePenaltyTests
{
    private static int failures;
    private static ProvinceTopology lastMap;
    private static void SetDay(Campaign c, int days) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, days * 24L);
    private static void Check(bool ok, string message)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "Income penalty: " + message);
        if (!ok) failures++;
    }

    public static void Run()
    {
        failures = 0;
        ReducesGrossIncomeNotExpenses();
        ExpiresOnItsDay();
        StackingIsCapped();
        CapIsTheCampaignsOwn();
        LumpConvertsToTheSameOrder();
        HardCodedCrisisNoLongerTakesALump();
        FingerprintSeesAPenalty();
        SaveRoundTrip();
        OldSnapshotLoadsWithNone();
        ConvertedEventImposesAPenalty();
        EveryContentSourceIsNamed();
        EffectGrammarRefusesNonsense();
        LedgerAddsUpToTheTreasury();
        LedgerKeepsInAndOutApart();
        BudgetLinesAreRecorded();
        OrganizationFundLedger();
        if (failures > 0) throw new Exception($"Income penalty: {failures} failed checks");
    }

    private static (Campaign C, Nation N, Nation Other) Fixture()
    {
        var c = new Campaign(20261115) { ScenarioId = "income_penalty_fixture" };
        var n = new Nation { Id = 0, MapIndex = 601, Tag = "PEN", Population = 1000000,
            Gdp = 520, Treasury = 20, Stability = .8f, TaxRate = .3f,
            MilitarySpending = .04f, WelfareSpending = .055f, EducationSpending = .045f,
            Literacy = .9f, StandardOfLiving = .8f, ConstructionCapacity = 1, IsHuman = true };
        var other = new Nation { Id = 1, MapIndex = 602, Tag = "OTP", Population = 1000000,
            Gdp = 100, Treasury = 5, Stability = .8f, TaxRate = .2f };
        c.Nations.Add(n); c.Nations.Add(other);
        c.States.Add(new ScenarioState { Id = "penalty_region", MapIndex = 3101,
            NationMapIndex = 601, NationId = "PEN", Population = 1000000 });
        c.States.Add(new ScenarioState { Id = "other_penalty_region", MapIndex = 3102,
            NationMapIndex = 602, NationId = "OTP", Population = 1000000 });
        var map = ProvinceTopology.Build(new ushort[] { 3101, 3101, 3102, 3102 },
            new ushort[] { 601, 601, 602, 602 }, 4, 1, _ => false);
        c.AttachProvinceTopology(map); c.ConfigureSinglePlayer(0);
        lastMap = map;
        return (c, n, other);
    }

    private static void ReducesGrossIncomeNotExpenses()
    {
        var (c, n, _) = Fixture();
        var before = BudgetCore.Evaluate(n, n.Gdp / 52f, false);
        Check(before.Revenue > 0f && before.IncomePenalty == 0f, "a country with no penalty pays none");
        IncomePenalties.Add(c, n, .2f, 70, "test");
        var after = BudgetCore.Evaluate(n, n.Gdp / 52f, false);
        Check(MathF.Abs(after.IncomePenalty - before.Revenue * .2f) < 1e-4f, "the penalty is its share of gross revenue");
        Check(MathF.Abs(after.Revenue - before.Revenue * .8f) < 1e-4f, "revenue falls by the penalty");
        Check(after.Expenses == before.Expenses, "expenses are untouched");
        Check(MathF.Abs(after.GrossRevenue - before.Revenue) < 1e-4f, "gross revenue is still reported before the penalty");
        Check(after.Taxation == before.Taxation, "the taxation figure is gross, so the penalty is not shown twice");
    }

    private static void ExpiresOnItsDay()
    {
        var (c, n, _) = Fixture();
        SetDay(c, 10);
        IncomePenalties.Add(c, n, .1f, 30, "test");
        Check(n.IncomePenalties is { Count: 1 } && n.IncomePenalties[0].UntilDay == 40, "a 30-day penalty runs to day 40");
        SetDay(c, 39);
        IncomePenalties.Expire(c, n);
        Check(n.IncomePenalties is { Count: 1 }, "it still applies on day 39");
        SetDay(c, 40);
        IncomePenalties.Expire(c, n);
        Check(n.IncomePenalties is null, "it is gone on day 40, and the list with it");
        Check(BudgetCore.Evaluate(n, n.Gdp / 52f, false).IncomePenalty == 0f, "an expired penalty takes nothing");
    }

    private static void StackingIsCapped()
    {
        var (c, n, _) = Fixture();
        IncomePenalties.Add(c, n, .1f, 100, "a");
        IncomePenalties.Add(c, n, .15f, 100, "b");
        Check(MathF.Abs(IncomePenalties.Share(n, c.Defines.IncomePenaltyCap) - .25f) < 1e-6f, "penalties add up");
        IncomePenalties.Add(c, n, .3f, 100, "c");
        IncomePenalties.Add(c, n, .3f, 100, "d");
        Check(MathF.Abs(IncomePenalties.Share(n, c.Defines.IncomePenaltyCap) - c.Defines.IncomePenaltyCap) < 1e-6f,
            "the sum is held at the cap");
        var budget = BudgetCore.Evaluate(n, n.Gdp / 52f, false);
        Check(MathF.Abs(budget.IncomePenalty - budget.GrossRevenue * c.Defines.IncomePenaltyCap) < 1e-4f,
            "the budget takes no more than the cap");
        for (var index = 0; index < 40; index++) IncomePenalties.Add(c, n, .01f, 50 + index, "many");
        Check(n.IncomePenalties.Count <= c.Defines.IncomePenaltyMaxEntries, "the list stays bounded");
        Check(!IncomePenalties.Add(c, n, -.1f, 10, "x") && !IncomePenalties.Add(c, n, .1f, 0, "x") &&
              !IncomePenalties.Add(c, n, float.NaN, 10, "x"), "nonsense penalties are refused");
    }

    // The cap used to be a process-wide static set by the weekly tick, so a budget read before a
    // loaded campaign's first week used the previous campaign's rule. Two campaigns with different
    // caps, read in turn, must each take their own share - and so must a saved and loaded one.
    private static void CapIsTheCampaignsOwn()
    {
        var (loose, a, _) = Fixture();
        loose.Defines.IncomePenaltyCap = .8f;
        var (tight, b, _) = Fixture();
        tight.Defines.IncomePenaltyCap = .3f;
        IncomePenalties.Add(loose, a, .5f, 100, "a1");
        IncomePenalties.Add(loose, a, .5f, 100, "a2");
        IncomePenalties.Add(tight, b, .5f, 100, "b1");
        IncomePenalties.Add(tight, b, .5f, 100, "b2");
        var first = BudgetCore.Evaluate(b, b.Gdp / 52f, false);
        var second = BudgetCore.Evaluate(a, a.Gdp / 52f, false);
        var third = BudgetCore.Evaluate(b, b.Gdp / 52f, false);
        Check(MathF.Abs(first.IncomePenalty - first.GrossRevenue * .3f) < 1e-4f &&
              MathF.Abs(third.IncomePenalty - third.GrossRevenue * .3f) < 1e-4f,
            "a campaign with a cap of 30% takes 30%, whatever campaign was read before it");
        Check(MathF.Abs(second.IncomePenalty - second.GrossRevenue * .8f) < 1e-4f,
            "a campaign with a cap of 80% takes 80% in the same process");

        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(loose, "cap"), "cap");
        var back = restored.Campaign?.NationByTag("PEN");
        Check(back is not null && MathF.Abs(IncomePenalties.Share(back) - .8f) < 1e-6f,
            "a loaded campaign keeps its cap before its first economy week");

        tight.Defines.IncomePenaltyCap = .2f;
        IncomePenalties.Expire(tight, b);
        Check(MathF.Abs(IncomePenalties.Share(b) - .2f) < 1e-6f, "the weekly tick brings the penalties to the campaign's current cap");
    }

    private static void LumpConvertsToTheSameOrder()
    {
        var (c, n, _) = Fixture();
        n.WeeklyGrossIncome = 2f;
        IncomePenalties.FromLoss(c, n, 4f, 70, "lump");
        var p = n.IncomePenalties.Single();
        var expected = p.Share * n.WeeklyGrossIncome * (p.UntilDay - p.StartDay) / 7f;
        Check(MathF.Abs(expected - 4f) < .05f, $"a lump of 4 becomes {p.Share:P1} for {p.UntilDay - p.StartDay} days, the same expected loss");
        var (c2, big, _) = Fixture();
        big.WeeklyGrossIncome = 1f;
        IncomePenalties.FromLoss(c2, big, 200f, 70, "huge");
        var h = big.IncomePenalties.Single();
        Check(h.Share <= c2.Defines.IncomePenaltySingleMax + 1e-6f && h.UntilDay - h.StartDay <= c2.Defines.IncomePenaltyMaxDays,
            "a lump too large for one penalty is held at the single maximum and the longest length");
        var (c3, fresh, _) = Fixture();
        fresh.WeeklyGrossIncome = 0f;
        Check(IncomePenalties.WeeklyBase(c3, fresh) > 0f, "before the first week the reference revenue share is the base");
    }

    private static void HardCodedCrisisNoLongerTakesALump()
    {
        var (c, n, _) = Fixture();
        n.WeeklyGrossIncome = 1.5f;
        var treasury = n.Treasury;
        TreasuryDynamics.Strike(c, n, 1f, true);
        Check(n.Treasury == treasury, "a financial crisis takes nothing from the treasury at once");
        Check(n.IncomePenalties is { Count: 1 } && n.IncomePenalties[0].Source == "Financial crisis",
            "it cuts income for a time instead");
    }

    private static void FingerprintSeesAPenalty()
    {
        var (c, n, _) = Fixture();
        var before = CampaignFingerprint.Of(c);
        IncomePenalties.Add(c, n, .05f, 30, "test");
        var with = CampaignFingerprint.Of(c);
        Check(with != before, "adding a penalty changes the fingerprint");
        SetDay(c, 30);
        IncomePenalties.Expire(c, n);
        SetDay(c, 0);
        Check(CampaignFingerprint.Of(c) == before, "a country with none fingerprints as it did before penalties existed");
    }

    private static void SaveRoundTrip()
    {
        var (c, n, _) = Fixture();
        SetDay(c, 14);
        IncomePenalties.Add(c, n, .07f, 120, "Event 'sanctions_bite'");
        MoneyLedger.Close(c, n);
        MoneyLedger.Book(n, MoneyFlow.Tribute, -1.25f);
        MoneyLedger.Close(c, n);
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "penalty"), "penalty");
        Check(restored.Success, "the campaign saves and loads: " + restored.Warning);
        var back = restored.Campaign?.NationByTag("PEN");
        var p = back?.IncomePenalties?.SingleOrDefault();
        Check(p is not null && p.Share == .07f && p.UntilDay == 134 && p.StartDay == 14 && p.Source == "Event 'sanctions_bite'",
            "the penalty survives a save with its share, end and source");
        Check(back is not null && MoneyLedger.LastOut(back, MoneyFlow.Tribute) == -1.25f && back.LedgerOpen,
            "the closed week of the ledger survives a save");
        if (back is null) return;
        restored.Campaign.AttachProvinceTopology(lastMap);
        var withPenalty = CampaignFingerprint.Of(restored.Campaign);
        var kept = back.IncomePenalties;
        back.IncomePenalties = null;
        Check(CampaignFingerprint.Of(restored.Campaign) != withPenalty, "the restored penalty is part of the loaded world's fingerprint");
        back.IncomePenalties = kept;
    }

    private static void OldSnapshotLoadsWithNone()
    {
        var (c, n, _) = Fixture();
        IncomePenalties.Add(c, n, .07f, 120, "test");
        MoneyLedger.Close(c, n);
        var bytes = SaveSystem.ExportNetworkSnapshot(c, "old");
        string json;
        using (var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
        using (var reader = new StreamReader(input)) json = reader.ReadToEnd();
        var root = JsonNode.Parse(json)!.AsObject();
        var stripped = 0;
        foreach (var nation in root["Nations"]!.AsArray())
        {
            var obj = nation!.AsObject();
            foreach (var key in obj.Select(pair => pair.Key).Where(key => key == "IncomePenalties" || key.StartsWith("Ledger") ||
                         key is "WeeklyGrossIncome" or "WeeklyIncomePenalty").ToList())
            { obj.Remove(key); stripped++; }
        }
        Check(stripped > 0, "the fixture's save carried the new fields, so stripping them makes an old save");
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        using (var writer = new StreamWriter(gzip)) writer.Write(root.ToJsonString());
        var restored = SaveSystem.ImportNetworkSnapshot(output.ToArray(), "old");
        Check(restored.Success, "an old snapshot loads: " + restored.Warning);
        var back = restored.Campaign?.NationByTag("PEN");
        Check(back is not null && back.IncomePenalties is null, "it loads with no penalty");
        Check(back is not null && BudgetCore.Evaluate(back, back.Gdp / 52f, false).IncomePenalty == 0f, "and takes nothing");
        if (back is null) return;
        back.Treasury += 3f;
        MoneyLedger.Close(restored.Campaign, back);
        Check(MoneyLedger.LastIn(back, MoneyFlow.Other) == 0f, "an old save's first week invents no remainder");
    }

    private static void ConvertedEventImposesAPenalty()
    {
        var sanctions = EventCatalog.All.FirstOrDefault(e => e.Id == "sanctions_bite");
        var option = sanctions?.Options.FirstOrDefault(o => o.Title == "Find other buyers");
        Check(option is not null, "the sanctions event and its 'Find other buyers' option are in the catalogue");
        if (option is null) return;
        Check(option.Effect.IncomePenalties.Count == 1 && !option.Effect.Entries.Any(e => e.Key.StartsWith("treasury")),
            "the option carries an income penalty and no treasury charge");
        var (c, n, _) = Fixture();
        var treasury = n.Treasury;
        option.Effect.Apply(c, n);
        Check(n.Treasury == treasury, "taking it leaves the treasury alone");
        Check(n.IncomePenalties is { Count: 1 } && MathF.Abs(n.IncomePenalties[0].Share - .04f) < 1e-6f &&
              n.IncomePenalties[0].UntilDay == c.ElapsedDays + 150, "it imposes 4% of gross income for 150 days");
        Check(IncomePenalties.Describe(n.IncomePenalties[0].Source) == Loc.Get(sanctions.Title),
            "the ledger names the event it came from");
        Check(ScriptedEffects.Lines(option.Effect).Any(line => line.Contains("150")), "the option's effect lines say so");
        var dated = EventCatalog.All.FirstOrDefault(e => e.Id == "financial_crisis_2008");
        Check(dated is null || dated.Effects.IncomePenalties.Count == 1 || dated.Options.Any(o => o.Effect.IncomePenalties.Count == 1),
            "the 2008 crisis is an income penalty too");
    }

    // Decisions, promises and modifiers share the effect grammar, so each can impose a penalty;
    // the ledger row names it as the catalogue does, never as the raw parse context.
    private static void EveryContentSourceIsNamed()
    {
        var decision = DecisionCatalog.All.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Name));
        Check(decision.Id is not null, "the decision catalogue has a named decision");
        if (decision.Id is not null)
        {
            var errors = new System.Collections.Generic.List<string>();
            using var json = System.Text.Json.JsonDocument.Parse("{ \"income_penalty\": { \"share\": 0.05, \"days\": 90 } }");
            var effect = ScriptedEffects.Parse(json.RootElement, $"decision '{decision.Id}'", errors);
            var (c, n, _) = Fixture();
            effect.Apply(c, n);
            var caption = n.IncomePenalties is { Count: 1 } ? IncomePenalties.Describe(n.IncomePenalties[0].Source) : "";
            Check(errors.Count == 0 && caption == Loc.Get(decision.Name), $"a decision's penalty is named after the decision ({caption})");
        }
        var promise = PromiseCatalog.All.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name));
        Check(promise.Id is not null, "the promise catalogue has a named promise");
        if (promise.Id is not null)
        {
            Check(IncomePenalties.Describe($"promise '{promise.Id}'") == Loc.Get(promise.Name), "a promise's penalty is named after the promise");
            var broken = IncomePenalties.Describe($"promise '{promise.Id}' failure");
            Check(broken == Loc.Format("Broken promise: {0}", Loc.Get(promise.Name)), "a broken promise's penalty says it was broken");
        }
        var modifier = ModifierCatalog.All.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Name));
        Check(modifier is not null, "the modifier catalogue has a named modifier");
        if (modifier is not null)
            Check(IncomePenalties.Describe($"Modifier '{modifier.Id}'") == Loc.Get(modifier.Name), "a modifier's penalty is named after the modifier");
        Check(IncomePenalties.Describe("decision 'no_such_decision'") == "no_such_decision",
            "a source the catalogue no longer has reads as its id, not as the parse context");
        Check(IncomePenalties.Describe("Financial crisis") == Loc.Get("Financial crisis"), "a plain caption is shown as it is");
    }

    private static void EffectGrammarRefusesNonsense()
    {
        var errors = new System.Collections.Generic.List<string>();
        using var good = System.Text.Json.JsonDocument.Parse("{ \"income_penalty\": { \"share\": 0.08, \"days\": 120 }, \"stability\": -0.01 }");
        var effect = ScriptedEffects.Parse(good.RootElement, "test", errors);
        Check(errors.Count == 0 && effect.IncomePenalties.Count == 1 && effect.Entries.Count == 1, "a well-formed penalty parses beside other keys");
        using var bad = System.Text.Json.JsonDocument.Parse("{ \"income_penalty\": { \"share\": 1.5, \"days\": 120 }, \"income_penalty_x\": 1 }");
        ScriptedEffects.Parse(bad.RootElement, "test", errors);
        Check(errors.Count == 2, "a share above one and an unknown key are both reported");
    }

    private static void LedgerAddsUpToTheTreasury()
    {
        var (c, n, other) = Fixture();
        MoneyLedger.Close(c, n);
        MoneyLedger.Close(c, other);
        var start = n.Treasury;
        { var ledgerBefore = n.Treasury; n.Treasury -= 2f; MoneyLedger.Book(n, MoneyFlow.Tribute, n.Treasury - ledgerBefore); }
        { var ledgerBefore = other.Treasury; other.Treasury += 2f; MoneyLedger.Book(other, MoneyFlow.Tribute, other.Treasury - ledgerBefore); }
        var budget = BudgetCore.Evaluate(n, n.Gdp / 52f, false, MoneyLedger.BudgetWeek(n));
        MoneyLedger.BookPenalty(n, budget.IncomePenalty);
        SovereignFinance.Settle(c, n, budget.Balance, 0f);
        n.Treasury += .75f; // a payment nobody wrote down
        SetDay(c, 7);
        MoneyLedger.Close(c, n);
        Check(MathF.Abs(MoneyLedger.LastIn(n, MoneyFlow.Other) - .75f) < .001f, "an unrecorded payment lands in Other, not nowhere");
        var rows = MoneyLedgerView.ForNation(c, n);
        var total = rows.Single(r => r.Total).Weekly;
        Check(MathF.Abs(total - (n.Treasury - start)) < .001f, "the ledger's lines add up to the change in the treasury");
        Check(rows.Any(r => r.Weekly < 0f && r.Caption == Loc.Get("Tribute paid")), "tribute paid is a row of its own");
        Check(rows.Where(r => !r.Total && !r.Note).All(r => !string.IsNullOrWhiteSpace(r.Explanation)), "every row says what it is");
        Check(rows.Count(r => r.Group == MoneyLedgerView.GroupIncome || r.Group == MoneyLedgerView.GroupSpending) == BudgetCore.All.Count,
            "every budget line is shown");
    }

    private static void LedgerKeepsInAndOutApart()
    {
        var (c, n, _) = Fixture();
        MoneyLedger.Close(c, n);
        MoneyLedger.Book(n, MoneyFlow.Military, -3f);
        MoneyLedger.Book(n, MoneyFlow.Military, 1f);
        n.Treasury -= 2f;
        MoneyLedger.Close(c, n);
        Check(MoneyLedger.LastIn(n, MoneyFlow.Military) == 1f && MoneyLedger.LastOut(n, MoneyFlow.Military) == -3f,
            "a refund and a purchase on the same line are both shown");
        Check(MoneyLedger.LastWeek(n, MoneyFlow.Military) == -2f, "their net is the line's week");
    }

    private static void BudgetLinesAreRecorded()
    {
        var (c, n, _) = Fixture();
        IncomePenalties.Add(c, n, .1f, 70, "test");
        var lines = new float[BudgetCore.All.Count];
        var budget = BudgetCore.Evaluate(n, n.Gdp / 52f, false, lines);
        Check(MathF.Abs(lines.Sum() - budget.IncomePenalty - budget.Balance) < 1e-3f, "the recorded lines less the penalty are the week's balance");
        var plain = BudgetCore.Evaluate(n, n.Gdp / 52f, false);
        Check(plain.Balance == budget.Balance, "recording the lines changes nothing");
    }

    private static void OrganizationFundLedger()
    {
        var (c, n, _) = Fixture();
        var org = new InternationalOrganization { Id = 1, Name = "Test fund", Members = new() { n.Id }, Budget = 1f };
        OrganizationLedger.Close(c, org);
        OrganizationLedger.Book(org, OrganizationFlow.Dues, .5f); org.Budget += .5f;
        OrganizationLedger.Book(org, OrganizationFlow.Grants, -.2f); org.Budget -= .2f;
        org.Budget += .1f;
        OrganizationLedger.Close(c, org);
        var rows = MoneyLedgerView.ForOrganization(c, org);
        Check(MathF.Abs(rows.Single(r => r.Total).Weekly - .4f) < 1e-5f, "the fund's lines add up to its change, remainder included");
        Check(rows.Any(r => r.Caption == Loc.Get("Grants to members") && r.Weekly == -.2f), "grants are a row");
        Check(rows.Any(r => r.Note && r.Caption.Trim() == n.DisplayName), "each member's dues are listed");
    }
}
