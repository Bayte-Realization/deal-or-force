using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

static class AiFiscalPolicyTests
{
    private static int failures;
    private static void Check(bool ok, string message)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "AI fiscal: " + message);
        if (!ok) failures++;
    }

    public static void Run()
    {
        failures = 0;
        UnpaidBills();
        ShortRunway();
        AidReserve();
        ProductiveInvestment();
        HealthyAndWartimeAid();
        PeacePurchase();
        ReadOnlyAssessment();
        ExistingCommitments();
        ProjectAffordability();
        FeatureAndRuleOverrides();
        OffensiveWarDeferral();
        DiplomaticCommitments();
        DisabledConstruction();
        InvalidInputs();
        if (failures > 0) throw new Exception($"AI fiscal: {failures} failed checks");
    }

    private static (Campaign C, Nation Ours, Nation Other) Fixture()
    {
        var c = new Campaign(20261015) { ScenarioId = "ai_fiscal_fixture" };
        var ours = new Nation { Id = 0, MapIndex = 501, Tag = "FIS", Population = 1000000,
            Gdp = 100, Treasury = 20, Stability = .8f, TaxRate = .3f,
            MilitarySpending = .04f, WelfareSpending = .055f, EducationSpending = .045f,
            Literacy = .9f, StandardOfLiving = .8f, ConstructionCapacity = 1 };
        var other = new Nation { Id = 1, MapIndex = 502, Tag = "OTH", Population = 1000000,
            Gdp = 100, Treasury = .1f, Stability = .8f, TaxRate = .2f };
        c.Nations.Add(ours); c.Nations.Add(other);
        c.States.Add(new ScenarioState { Id = "fiscal_region", MapIndex = 3001,
            NationMapIndex = 501, NationId = "FIS", Population = 1000000 });
        c.States.Add(new ScenarioState { Id = "other_region", MapIndex = 3002,
            NationMapIndex = 502, NationId = "OTH", Population = 1000000 });
        var map = ProvinceTopology.Build(new ushort[] { 3001, 3001, 3002, 3002 },
            new ushort[] { 501, 501, 502, 502 }, 4, 1, _ => false);
        c.AttachProvinceTopology(map);
        return (c, ours, other);
    }

    private static void UnpaidBills()
    {
        var (c, n, _) = Fixture();
        n.Treasury = 13; n.Stability = .4f; n.PaymentArrears = 2;
        n.DebtCondition = DebtCondition.Default;
        var tax = n.TaxRate; var welfare = n.WelfareSpending; var education = n.EducationSpending;
        AiStatecraft.GovernTheBudget(c, n);
        Check(n.TaxRate >= tax, "unpaid public bills do not trigger a tax rebate just because cash exceeds twelve percent of GDP");
        Check(n.WelfareSpending <= welfare && n.EducationSpending <= education,
            "a defaulting cabinet does not promise additional recurring social spending");
        Check(c.Construction.Count == 0, "unpaid obligations take priority over a new discretionary construction project");
    }

    private static void ShortRunway()
    {
        var (c, n, _) = Fixture();
        n.Treasury = 4; n.PublicDebt = 2000; n.DebtMechanicActive = true;
        var tax = n.TaxRate;
        Check(BudgetCore.Evaluate(n, n.Gdp / 52, false).Balance < -1,
            "fixture has a real interest-driven deficit, not a stale WeeklyBalance field");
        AiStatecraft.GovernTheBudget(c, n);
        Check(n.TaxRate > tax, "a short cash runway prompts adjustment before the treasury is already empty");
        Check(c.Construction.Count == 0, "four billion in cash does not fund construction when the current budget burns it within weeks");
    }

    private static void AidReserve()
    {
        var (c, donor, recipient) = Fixture();
        donor.Treasury = 6.25f;
        var relation = CampaignDiplomacy.Get(c, donor.Id, recipient.Id);
        relation.Stance = DiplomaticStance.Allied; relation.Opinion = 80;
        relation.TradeAgreement = relation.NonAggressionPact = true;
        var before = donor.Treasury;
        AiStatecraft.TryFriendlyMove(c, donor, recipient);
        Check(donor.Treasury >= donor.Gdp * c.Defines.AiAidReserveOfGdp,
            "a discretionary aid package preserves the reserve after payment, not only before it");
        Check(donor.Treasury == before && relation.AidSentDay < c.ElapsedDays,
            "an unaffordable full package is deferred without inventing a payment or consuming its cooldown");

        donor.Treasury = 20; donor.PaymentArrears = 1; relation.AidSentDay = -9999;
        before = donor.Treasury;
        AiStatecraft.TryFriendlyMove(c, donor, recipient);
        Check(donor.Treasury == before, "a donor pays its own overdue public bills before making discretionary foreign gifts");
    }

    private static void ProductiveInvestment()
    {
        var (c, n, _) = Fixture();
        n.Market[Commodity.Energy] = new MarketQuote
        { Commodity = Commodity.Energy, Demand = 20, Production = 1, Satisfaction = .05f, Price = 4 };
        Check(AiBuilder.Choose(c, n, c.StatesOf(n), out var wanted, out _) && wanted == BuildingKind.PowerPlant,
            "existing production planner identifies a power plant as the remedy for an energy shortage");
        AiStatecraft.GovernTheBudget(c, n);
        Check(c.Construction.Count == 1 && c.Construction[0].Building == BuildingKind.PowerPlant,
            "the actual monthly cabinet uses that production plan instead of always queuing generic civilian industry");
    }

    private static void HealthyAndWartimeAid()
    {
        var (c, donor, recipient) = Fixture();
        var relation = CampaignDiplomacy.Get(c, donor.Id, recipient.Id);
        relation.Stance = DiplomaticStance.Allied; relation.Opinion = 80;
        relation.TradeAgreement = relation.NonAggressionPact = true;
        c.Wars.Add(new StrategicWar { Id = 1, AttackerId = 2, DefenderId = donor.Id });
        var allCash = donor.Treasury + recipient.Treasury;
        var before = donor.Treasury;
        Check(AiStatecraft.TryFriendlyMove(c, donor, recipient) && donor.Treasury < before && recipient.Treasury > .1f,
            "being at war does not forbid a solvent ally from sending an affordable package");
        Check(MathF.Abs(donor.Treasury + recipient.Treasury - allCash) < .000001f,
            "normal aid conserves the two real treasury ledgers");

        (c, donor, recipient) = Fixture();
        donor.PublicDebt = 100; donor.Treasury = 20;
        Check(AiFiscalPolicy.Assess(c, donor, .06f).CanSpend,
            "ordinary serviced debt does not disqualify a solvent country from productive policy");
    }

    private static void PeacePurchase()
    {
        var (c, buyer, seller) = Fixture();
        c.ConfigureSinglePlayer(seller.Id);
        buyer.Treasury = 2.25f; seller.Gdp = 500;
        var relation = CampaignDiplomacy.Get(c, buyer.Id, seller.Id);
        relation.Stance = DiplomaticStance.Rival; relation.Opinion = -70;
        var before = buyer.Treasury;
        var allowance = AiFiscalPolicy.Assess(c, buyer, c.Defines.AiTreasuryFloor).DiscretionaryCash;
        Check(AiStatecraft.BuyPeace(c, buyer, seller) && c.DealOffers.Count == 1,
            "a weak government can deliver a hostile-neighbour pact offer to a human instead of signing for them");
        var paper = c.DealOffers.SingleOrDefault();
        Check(paper is { Pending: true } && paper.From.Cash > 0 && paper.From.Cash <= allowance,
            "a peace purchase is capped by the current operating envelope, not half the treasury");
        Check(buyer.Treasury == before && !relation.NonAggressionPact,
            "delivery reserves no money and creates no pact before the human reply");
        var accept = new RespondDealCommand { IssuerId = seller.Id, OfferId = paper?.Id ?? -1, Reply = DealReply.Accept };
        accept.Apply(c);
        Check(accept.Outcome.Accepted && relation.NonAggressionPact &&
            buyer.Treasury >= buyer.Gdp * c.Defines.AiTreasuryFloor,
            "the ordinary human signature settles the capped payment without invading the reserve");

        (c, buyer, seller) = Fixture(); seller.Gdp = 500; buyer.PaymentArrears = 1;
        CampaignDiplomacy.Get(c, buyer.Id, seller.Id).Stance = DiplomaticStance.Rival;
        Check(!AiStatecraft.BuyPeace(c, buyer, seller) && c.DealOffers.Count == 0,
            "unpaid public bills defer a cash-for-pact proposal without queuing an impossible promise");
    }

    private static void ReadOnlyAssessment()
    {
        var (c, n, _) = Fixture();
        n.PublicDebt = 100; n.PaymentArrears = 2; n.DebtArrears = 1;
        var hash = CampaignFingerprint.Of(c); var random = c.Random.State;
        var fiscal = AiFiscalPolicy.Assess(c, n, .02f);
        var expected = 2 + 3 + MathF.Max(0, -fiscal.WeeklyBalance) * c.Defines.AiFiscalReserveWeeks;
        Check(fiscal.Reason == AiFiscalReason.OverdueBills && MathF.Abs(fiscal.Reserve - expected) < .00001f,
            "arrears enter the reserve once and principal contributes only its existing budget interest flow");
        Check(hash == CampaignFingerprint.Of(c) && random == c.Random.State && !n.DebtInitialized && c.Relations.Count == 0,
            "a fiscal assessment does not initialize debt, create relations, draw randomness or alter campaign authority");
        n.WeeklyBalance = -999;
        Check(AiFiscalPolicy.Assess(c, n, .02f) == fiscal,
            "assessment reads current policies and budget definitions rather than last week's displayed balance");
    }

    private static void ExistingCommitments()
    {
        var (c, n, _) = Fixture();
        n.ConstructionCapacity = 2;
        c.Construction.Add(new ConstructionProject { Id = 1, NationId = n.Id, Cost = 4, Paid = 1, DurationDays = 200 });
        c.Construction.Add(new ConstructionProject { Id = 2, NationId = 1, Cost = 400, DurationDays = 1 });
        var fiscal = AiFiscalPolicy.Assess(c, n, .02f);
        var expected = MathF.Min(3, 4 / 200f * 2 * 7 * c.Defines.AiFiscalReserveWeeks);
        Check(MathF.Abs(fiscal.ConstructionPayments - expected) < .00001f,
            "the envelope protects the owner's remaining queued capital payments, not another country's projects");
        c.Construction[0].Paid = 4;
        Check(AiFiscalPolicy.Assess(c, n, .02f).ConstructionPayments == 0,
            "fully paid construction does not reserve its original cost again");
    }

    private static void ProjectAffordability()
    {
        var (c, n, _) = Fixture();
        n.Treasury = 3.06f;
        var reserve = AiFiscalPolicy.Assess(c, n, c.Defines.AiBuildTreasuryFloor);
        Check(reserve.CanSpend && !AiFiscalPolicy.CanStartProject(c, n, BuildingKind.CivilianIndustry, reserve),
            "a token deposit does not authorize a capital queue with no money for its near-term payments");
        AiStatecraft.GovernTheBudget(c, n);
        Check(c.Construction.Count == 0, "the real monthly investment step observes capital affordability");
        n.Treasury = 20; n.Market.Clear();
        AiStatecraft.GovernTheBudget(c, n);
        Check(c.Construction.Count == 1 && c.Construction[0].Building == BuildingKind.CivilianIndustry,
            "a healthy economy without a shortage keeps the normal civilian-industry fallback");
    }

    private static void FeatureAndRuleOverrides()
    {
        var (c, n, _) = Fixture();
        var baseline = CampaignFingerprint.Of(c);
        c.Defines.AiFiscalReserveWeeks = 4;
        Check(CampaignFingerprint.Of(c) != baseline,
            "different modded fiscal horizons are detected before peers make different spending decisions");
        c.Defines.AiFiscalReserveWeeks = 8;
        Check(CampaignFingerprint.Of(c) == baseline,
            "the compiled default horizon preserves the legacy fingerprint byte stream");
        n.PaymentArrears = 2; n.DebtCondition = DebtCondition.Default;
        c.FeatureOverrides["public_debt"] = false;
        Check(AiFiscalPolicy.Assess(c, n, .02f).CanSpend,
            "dormant arrears and default labels do not block AI when the public-debt mechanic is disabled");
        c.FeatureOverrides["public_debt"] = true; n.PaymentArrears = 0; n.DebtCondition = DebtCondition.Current;
        n.PublicDebt = 1000; n.Treasury = 4;
        c.Defines.AiFiscalReserveWeeks = 0;
        Check(AiFiscalPolicy.Assess(c, n, .02f).CanSpend, "a mod may explicitly turn off the forward deficit horizon");
        c.Defines.AiFiscalReserveWeeks = 8;
        Check(!AiFiscalPolicy.Assess(c, n, .02f).CanSpend, "the same current accounts respect an eight-week deficit horizon");
    }

    private static void OffensiveWarDeferral()
    {
        var (c, n, _) = Fixture();
        n.PaymentArrears = 2; n.WarPlanTargetId = 1; n.WarPlanSinceDay = 0;
        c.Defines.AiMayDeclareWar = true;
        var prior = AiStatecraft.Tally.GetValueOrDefault("war_fiscal_OverdueBills");
        typeof(AiStatecraft).GetMethod("Warmaking", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { c, n });
        Check(AiStatecraft.Tally.GetValueOrDefault("war_fiscal_OverdueBills") == prior + 1 && c.Wars.Count == 0,
            "the strategic cabinet explains why unpaid obligations defer a new offensive war");
        Check(n.WarPlanTargetId == 1 && n.WarPlanSinceDay == 0,
            "a temporary fiscal deferral does not erase the government's existing strategic plan");
    }

    private static void InvalidInputs()
    {
        var (c, n, _) = Fixture();
        n.Treasury = float.NaN;
        Check(AiFiscalPolicy.Assess(c, n, .02f).Reason == AiFiscalReason.InvalidAccounts,
            "non-finite treasury fails closed rather than becoming a spending allowance");
        AiStatecraft.GovernTheBudget(c, n);
        Check(c.Construction.Count == 0 && float.IsFinite(n.TaxRate), "invalid accounts cannot poison tax policy or create projects");
        n.Treasury = 20;
        Check(AiFiscalPolicy.Assess(c, n, float.NaN).Reason == AiFiscalReason.InvalidAccounts,
            "an invalid modded reserve rule fails closed");
    }

    private static void DiplomaticCommitments()
    {
        var (c, n, other) = Fixture();
        var original = AiFiscalPolicy.Assess(c, n, .02f);
        var paper = new DealOffer { Id = 1, FromId = n.Id, ToId = other.Id,
            From = new Ultimatum.Side { Cash = 3 }, To = new Ultimatum.Side(),
            State = DealOfferState.Pending, ExpiresDay = c.ElapsedDays + 10 };
        c.DealOffers.Add(paper);
        var pending = AiFiscalPolicy.Assess(c, n, .02f);
        Check(MathF.Abs(pending.DiscretionaryCash - (original.DiscretionaryCash - 3)) < .00001f && n.Treasury == 20,
            "an outgoing pending cash promise reduces new discretionary headroom without moving or locking treasury money");
        paper.State = DealOfferState.Accepted;
        Check(AiFiscalPolicy.Assess(c, n, .02f) == original,
            "executed or closed papers are not counted a second time as future promises");
        paper.State = DealOfferState.Pending; paper.ExpiresDay = c.ElapsedDays;
        Check(AiFiscalPolicy.Assess(c, n, .02f) == original, "an expired paper no longer constrains new voluntary spending");
        paper.ExpiresDay = c.ElapsedDays + 10; paper.FromId = other.Id; paper.ToId = n.Id;
        Check(AiFiscalPolicy.Assess(c, n, .02f) == original,
            "incoming requests do not silently reserve a country's money before that country agrees to them");
        paper.FromId = n.Id; paper.ToId = other.Id; paper.From.Cash = 18;
        var relation = CampaignDiplomacy.Get(c, n.Id, other.Id);
        relation.Stance = DiplomaticStance.Allied; relation.Opinion = 80;
        relation.TradeAgreement = relation.NonAggressionPact = true;
        AiStatecraft.TryFriendlyMove(c, n, other);
        Check(n.Treasury == 20 && other.Treasury == .1f,
            "a new voluntary gift cannot spend the same operating cash already promised on an open outgoing paper");
    }

    private static void DisabledConstruction()
    {
        var (c, n, _) = Fixture();
        c.Construction.Add(new ConstructionProject { NationId = n.Id, Cost = 10, DurationDays = 1 });
        c.FeatureOverrides["construction"] = false;
        var disabled = AiFiscalPolicy.Assess(c, n, .02f);
        Check(disabled.ConstructionPayments == 0,
            "a dormant queue does not reserve nonexistent payments while its construction system is disabled");
    }
}
