using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class CoreRefactorTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("core refactor: " + text); Console.WriteLine("PASS core refactor: " + text); }

    public static void Run()
    {
        PolicyKeys();
        ManagedRules();
        AidReviews();
    }

    static void PolicyKeys()
    {
        var basic = new PoliticalPolicy();
        Check(PoliticalPolicies.Slot(basic) == "nation:", "default policy slot remains unchanged");
        var first = new PoliticalPolicy { Category = "energy", Scope = "foreign" };
        var second = new PoliticalPolicy { Scope = "foreign", Category = "energy", Modifiers = first.Modifiers };
        Check(first == second && PoliticalPolicies.Slot(first, "TAG") == "foreign:energy:TAG",
            "initialization order leaves record equality and foreign target intact");
        var clone = first with { Scope = "region", Category = "education" };
        Check(PoliticalPolicies.Slot(clone, region: 37) == "region:education:37" &&
            PoliticalPolicies.Slot(first, "TAG") == "foreign:energy:TAG",
            "record clones refresh their own slot without altering the original");
        var key = first with { Scope = "nation" };
        var text = JsonSerializer.Serialize(key);
        Check(!text.Contains("SlotKey") && !text.Contains("_scope") && !text.Contains("_category"),
            "derived policy key does not enter the public data schema");
        var canonical = PoliticalPolicyCatalog.NationalCategories.First();
        _ = PoliticalPolicyCatalog.Baseline(canonical);
        for (var i = 0; i < 1000; i++) _ = PoliticalPolicies.Slot(canonical);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var hits = 0;
        for (var i = 0; i < 10000; i++)
        {
            if (ReferenceEquals(PoliticalPolicies.Slot(canonical), canonical.SlotKey)) hits++;
            _ = PoliticalPolicyCatalog.Baseline(canonical);
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(hits == 10000 && bytes == 0, "10000 warm national slot/baseline lookups allocate zero bytes");
    }

    static void ManagedRules()
    {
        Check(typeof(DealOrForce.Rules.AssaultRules).Assembly.GetName().Name == "DealOrForce.Rules",
            "the game rule is supplied by the production F# assembly");
        Check(Enum.GetNames<AssaultReadiness.Reason>().SequenceEqual(Enum.GetNames<DealOrForce.Rules.Reason>()),
            "F# reasons retain the existing public C# contract");
        var c = new Campaign(20261018);
        var army = new Army { OrderKind = ArmyOrder.AdvanceFront, PlanActive = true, Organisation = 1, SupplyLevel = 1 };
        for (var i = 0; i < 1000; i++) _ = AssaultReadiness.Assess(c, army);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var ready = 0;
        for (var i = 0; i < 10000; i++) if (AssaultReadiness.Assess(c, army).Ready) ready++;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(ready == 10000 && bytes == 0, "10000 live C# to F# assessments allocate zero bytes");
    }

    static Campaign Fixture(int variation)
    {
        var c = new Campaign(20261018);
        for (var id = 0; id < 8; id++)
            c.AddNation(new Nation { Tag = "OPT" + id, MapIndex = 501 + id, Population = 1000000,
                Gdp = 100, Treasury = id < 2 ? 100 : variation % 3 == 0 ? 50 : .01f,
                WeeklyBalance = id < 2 ? 1 : -1, Stability = .8f, WarSupport = .8f });
        c.ConfigureSinglePlayer(0);
        c.Defines.AidIntervalDays = 1;
        c.Defines.AiAidReserveOfGdp = .01f;
        foreach (var n in c.Nations.Skip(2))
            foreach (var d in c.Nations.Take(2))
            {
                var relation = CampaignDiplomacy.Get(c, n.Id, d.Id);
                relation.Stance = DiplomaticStance.Allied;
                relation.Opinion = .9f;
            }
        if (variation % 4 == 1) c.Nations[2].Treasury = float.NaN;
        if (variation % 4 == 2) c.Defines.AiAidCrisisTreasuryOfGdp = float.NaN;
        if (variation % 4 == 3)
        {
            c.Wars.Add(new StrategicWar { Id = 5, AttackerId = 3, DefenderId = 7 });
            c.Wars.Add(new StrategicWar { Id = 2, AttackerId = 3, DefenderId = 6 });
        }
        // NationById is intentionally indexed: retain the campaign's Id == index invariant.
        if (variation % 2 == 1) c.Nations[6].Gdp = .001f;
        return c;
    }

    static void AidReviews()
    {
        var exercised = false;
        for (var variant = 0; variant < 12; variant++)
        {
            var old = Fixture(variant); var current = Fixture(variant);
            for (var day = 0; day < 20; day++)
            {
                typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(old, (long)day * 24);
                typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(current, (long)day * 24);
                var cadence = variant % 3 + 1;
                LegacyReview(old, cadence, 0);
                ForeignAssistance.ReviewDonors(current, cadence, 0);
                if (current.AssistanceRequests.Count > 0) exercised = true;
                Check(CampaignFingerprint.Of(old) == CampaignFingerprint.Of(current),
                    $"aid review preserves the authoritative world: variant {variant}, day {day}");
                var request = old.AssistanceRequests.FirstOrDefault(r => r.State == ForeignAssistanceState.Pending);
                if (request is not null)
                {
                    _ = ForeignAssistance.Respond(old, request.DonorId, request.Id, ForeignAssistanceReply.Accept);
                    _ = ForeignAssistance.Respond(current, request.DonorId, request.Id, ForeignAssistanceReply.Accept);
                }
            }
        }
        Check(exercised, "aid equivalence includes real requests and replies, not only rejected candidates");
    }

    // Frozen review loop from ea4510cf. Payment validation/execution is deliberately
    // the actual shared executor: this oracle isolates traversal and the early gate.
    static void LegacyReview(Campaign c, int cadence, float threshold)
    {
        foreach (var requester in c.Nations.Where(n => !n.IsHuman).OrderBy(n => n.Id))
        {
            if (((long)c.ElapsedDays + requester.Id) % cadence != 0 ||
                c.AssistanceRequests.Any(r => r.RequesterId == requester.Id && r.State == ForeignAssistanceState.Pending)) continue;
            var amount = (double)requester.Gdp * c.Defines.AidShareOfRecipientGdp;
            if (!double.IsFinite(amount) || amount < .01 || amount > float.MaxValue) continue;
            var warId = c.Wars.Where(w => !w.Ended && w.Involves(requester.Id)).OrderBy(w => w.Id).FirstOrDefault()?.Id ?? -1;
            Nation best = null; var bestScore = float.NegativeInfinity;
            foreach (var donor in c.Nations.OrderBy(n => n.Id))
            {
                var assessment = ForeignAssistance.CanSubmit(c, requester.Id, donor.Id, (float)amount, warId);
                if (!assessment.Allowed || assessment.Willingness < threshold || assessment.Willingness <= bestScore) continue;
                best = donor; bestScore = assessment.Willingness;
            }
            if (best is not null) ForeignAssistance.Submit(c, requester.Id, best.Id, (float)amount, warId);
        }
    }
}
