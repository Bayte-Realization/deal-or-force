using System;
using System.Linq;
using DealOrForce.Simulation;

static class SituationReportTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception("situation: " + label); Console.WriteLine("PASS situation: " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(820);
        var n = c.NationByTag("GBR"); var rival = c.NationByTag("FRA");
        c.ConfigureSinglePlayer(n.Id); c.Wars.Clear(); c.Armies.Clear(); c.Organizations.Clear(); c.Construction.Clear();
        n.Market.Clear(); n.Stability = .9f; n.CoupPlotDay = -1;
        n.DomesticCrisis.CoupPressure = 0; n.DebtArrears = n.PaymentArrears = 0; n.Treasury = 1000;
        var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        var quiet = SituationReport.Read(c, n);
        Check(quiet.Any(i => i.Id == "idle_construction" && i.Priority == SituationPriority.Opportunity), "idle building capacity is an opportunity, not a crisis");
        Check(quiet.All(i => i.DeadlineDay < 0), "ordinary conditions have no invented deadline");
        _ = SituationReport.Read(c, n);
        Check(before == CampaignFingerprint.Of(c) && c.Random.State == random, "reading twice changes neither simulation nor random stream");
        Check(c.StatesOf(n).All(s => s.ResidentAwareness < 0), "old-save awareness is not initialized by the interface");

        n.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Demand = 100, Production = 40,
            Imports = 10, Exports = 0, Satisfaction = .5f };
        var shortage = SituationReport.Read(c, n).Single(i => i.Id == "shortage:Food");
        Check(shortage.Priority == SituationPriority.Critical && shortage.Route == SituationRoute.Market && shortage.Good == Commodity.Food,
            "severe shortage links to the precise good");
        Check(shortage.Facts.Length == 4 && shortage.Causes.Length == 2 && shortage.Consequences.Length == 1,
            "market warning exposes current facts without claiming an unmeasured cause");
        n.Market[Commodity.Food].Satisfaction = .92f;
        Check(SituationReport.Read(c, n).All(i => i.Id != shortage.Id), "recovered shortage disappears");
        c.Defines.SituationShortageWarning = float.NaN; c.Defines.SituationShortageCritical = float.PositiveInfinity;
        n.Market[Commodity.Food].Satisfaction = .8f;
        Check(SituationReport.Read(c, n).Single(i => i.Id == shortage.Id).Priority == SituationPriority.Warning,
            "invalid presentation thresholds use finite fallbacks");

        var offer = new DealOffer { Id = 77, FromId = rival.Id, ToId = n.Id, CreatedDay = c.ElapsedDays,
            ExpiresDay = c.ElapsedDays + 30, State = DealOfferState.Pending };
        c.DealOffers.Add(offer);
        var pending = SituationReport.Read(c, n).Single(i => i.Id == "deal:77");
        Check(pending.Priority == SituationPriority.Awaiting && pending.TargetId == 77 && pending.DeadlineDay == offer.ExpiresDay,
            "inbox warning reads the real offer ID and deadline");
        DomesticCrisesTests.Day(c, offer.ExpiresDay - 2);
        Check(SituationReport.Read(c, n).First().Id == "deal:77", "near-expiry consent outranks noncritical conditions");
        Check(SituationReport.Read(c, n, observer: true).All(i => i.Route != SituationRoute.Inbox), "observer is not asked to consent on behalf of a country");
        c.SetFeature("deals", false);
        Check(SituationReport.Read(c, n).Any(i => i.Id == "deal:77"), "existing offer remains reviewable for decline with deals disabled");
        DomesticCrisesTests.Day(c, offer.ExpiresDay);
        Check(SituationReport.Read(c, n).All(i => i.Id != "deal:77"), "expired paper is not represented as actionable");

        var org = new InternationalOrganization { Id = 19, Name = "Test council", CharterId = "collective_defence",
            Cohesion = .2f, Motion = new OrganizationMotion { Id = 3, EndsDay = c.ElapsedDays + 10 } };
        org.Members.Add(n.Id); c.Organizations.Add(org);
        Check(SituationReport.Read(c, n).Any(i => i.Id == "vote:19:3" && i.DeadlineDay == org.Motion.EndsDay), "assembly exposes an uncast vote");
        org.Motion.Votes[n.Id] = false;
        Check(SituationReport.Read(c, n).All(i => i.Id != "vote:19:3"), "both yes and no count as an already recorded vote");
        Check(SituationReport.Read(c, n).Any(i => i.Id == "organization:19" && i.Priority == SituationPriority.Critical), "cohesion failure is distinct from membership");
        c.SetFeature("organizations", false);
        Check(SituationReport.Read(c, n).All(i => i.Route != SituationRoute.Organization), "disabled institutions produce no active alerts");

        n.CoupPlotDay = c.ElapsedDays;
        Check(SituationReport.Read(c, n).Any(i => i.Id == "military_plot"), "actual plot is surfaced before a regime change");
        c.SetFeature("coups", false);
        Check(SituationReport.Read(c, n).All(i => i.Id != "military_plot"), "disabled military plots do not advertise a transition");
        c.Defines.CivilWarStrikesPlayers = true; c.Defines.CivilWarEarliestDay = 0; n.Stability = .01f;
        var civil = SituationReport.Read(c, n).Single(i => i.Id == "civil_war");
        Check(civil.DeadlineDay < 0 && civil.Facts.Length == 1 && civil.Consequences[0].Contains("not a countdown"),
            "civil outbreak warning is weekly probability, never a countdown");
        c.Defines.CivilWarStrikesPlayers = false;
        Check(SituationReport.Read(c, n).All(i => i.Id != "civil_war"), "player protection uses the actual simulation gate");

        n.DebtArrears = 2;
        var fiscal = SituationReport.Read(c, n).Single(i => i.Id == "financing");
        Check(fiscal.Priority == SituationPriority.Critical && fiscal.Facts.Length == 4, "overdue liabilities are explained separately from cash");
        c.SetFeature("public_debt", false);
        Check(SituationReport.Read(c, n).All(i => i.Id != "financing"), "disabled sovereign finance is not presented as an active crisis");
        c.SetFeature("construction", false);
        Check(SituationReport.Read(c, n).All(i => i.Id != "idle_construction"), "disabled building capacity creates no idle warning");

        for (var i = 0; i < 30; i++)
            c.DealOffers.Add(new DealOffer { Id = 100 + i, FromId = rival.Id, ToId = n.Id,
                ExpiresDay = c.ElapsedDays + 1 + i, State = DealOfferState.Pending });
        c.Defines.SituationItemLimit = 8;
        var bounded = SituationReport.Read(c, n);
        Check(bounded.Count <= 8 && bounded.Select(i => i.Id).Distinct().Count() == bounded.Count, "large report is bounded and identity-stable");
        Check(bounded.Where(i => i.DeadlineDay >= 0).Select(i => i.DeadlineDay).SequenceEqual(
            bounded.Where(i => i.DeadlineDay >= 0).Select(i => i.DeadlineDay).OrderBy(i => i)), "equally ranked letters retain deadline order");

        Attention();
        SoundCatalog.Load(); var cue = SoundCatalog.Get("situation_alert");
        Check(cue.Yielding && cue.CooldownMs == 12000 && cue.VolumeDb == -6, "attention sound is quiet, throttled and yields to strategic messages");
        Check(HandbookCatalog.ForScreen("Situation") == "situation_room", "new screen has a moddable handbook entry");
    }
    private static void Attention()
    {
        var item = new SituationItem("stable", SituationPriority.Critical, SituationArea.Economy, "First", "A number",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<SituationFact>(), SituationRoute.Finance, "Inspect");
        var attention = new SituationAttention();
        Check(!attention.Observe(1, new[] { item }), "opening a campaign does not sound all old emergencies");
        Check(!attention.Observe(1, new[] { item with { Title = "Другой язык", Summary = "A new number" } }), "language or numeric text change is not a new alarm");
        _ = attention.Observe(1, Array.Empty<SituationItem>());
        Check(attention.Observe(1, new[] { item }), "a cleared emergency returning can sound again");
        Check(!attention.Observe(2, new[] { item }), "hotseat change primes rather than alarming");
        attention.Reset();
        Check(!attention.Observe(2, new[] { item }), "new campaign resets presentation memory");
    }
}
