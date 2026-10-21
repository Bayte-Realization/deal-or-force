using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class PoliticalIdentityTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("political identity: " + message); Console.WriteLine("PASS political identity: " + message); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(701); var n = c.Player;
        n.Cabinet.Clear(); n.CabinetPeople.Clear(); n.LastCabinetChangeDay = -9999; n.Treasury = 100000;
        Check(Rulers.CandidatePool(c, n, int.MaxValue).Count == 32 && Rulers.CandidatePool(c, n, -1).Count == 0,
            "candidate generation is bounded before allocating a list");
        var before = CampaignFingerprint.Of(c); var money = n.Treasury;
        foreach (var slot in new[] { int.MinValue, -1, 32, 100000, int.MaxValue })
            new RecruitPoliticianCommand { IssuerId = n.Id, CandidateSlot = slot }.Apply(c);
        Check(before == CampaignFingerprint.Of(c) && money == n.Treasury, "invalid candidate slots change no state or treasury");
        var candidate = Rulers.CandidatePool(c, n)[0];
        var portfolio = candidate.PortfolioId;
        new RecruitPoliticianCommand { IssuerId = n.Id, CandidateSlot = 0, ExpectedName = "a stale candidate" }.Apply(c);
        Check(before == CampaignFingerprint.Of(c), "a stale candidate identity cannot appoint somebody else");
        Check(PoliticalAppointments.MayRecruit(c, n, 0, false, out _, out var price, out _), "fresh candidate passes the same UI and command gate");
        new RecruitPoliticianCommand { IssuerId = n.Id, CandidateSlot = 0, ExpectedName = candidate.Name }.Apply(c);
        Check(n.CabinetPeople.TryGetValue(candidate.PortfolioId, out var serving) && serving.Name == candidate.Name &&
            serving.BirthYear == candidate.BirthYear && Rulers.MinisterPersonName(n, candidate.PortfolioId) == candidate.Name,
            "the recruited politician, not a different procedural name, holds the portfolio");
        Check(Math.Abs(n.Treasury - (money - price)) < .01f, "recruitment pays its quoted price once");
        var name = Rulers.MinisterPersonName(n, candidate.PortfolioId);
        n.RulerGeneration += 7;
        Check(Rulers.MinisterPersonName(n, candidate.PortfolioId) == name, "a new candidate generation does not rename serving ministers");
        var frozen = CampaignFingerprint.Of(c); money = n.Treasury;
        new RecruitPoliticianCommand { IssuerId = n.Id, CandidateSlot = 1 }.Apply(c);
        Check(frozen == CampaignFingerprint.Of(c) && money == n.Treasury, "recruit command cannot bypass cabinet cooldown");

        n.LastCabinetChangeDay = -9999; candidate = Rulers.CandidatePool(c, n)[1];
        n.Treasury = Rulers.CabinetAppointmentCost(c, n) * 1.25f;
        Check(PoliticalAppointments.MayRecruit(c, n, 1, false, out _, out _, out _) &&
            !PoliticalAppointments.MayRecruit(c, n, 1, true, out _, out _, out var reason) && reason.Length > 0,
            "leadership promotion checks its larger 1.5x price, not the minister price");
        frozen = CampaignFingerprint.Of(c);
        new RecruitPoliticianCommand { IssuerId = n.Id, CandidateSlot = 1, AsNationalLeader = true }.Apply(c);
        Check(frozen == CampaignFingerprint.Of(c), "unaffordable promotion is read-only");

        var options = new JsonSerializerOptions { IncludeFields = true, IgnoreReadOnlyProperties = true };
        var loaded = JsonSerializer.Deserialize<Nation>(JsonSerializer.Serialize(n, options), options);
        Check(loaded.CabinetPeople[portfolio].Name == name && loaded.CabinetPeople[portfolio].BirthYear == serving.BirthYear,
            "minister identity survives the serialized Nation save payload");
        var legacy = JsonSerializer.Deserialize<Nation>("{\"Id\":41,\"Cabinet\":{}}", options);
        Check(legacy.CabinetPeople.Count == 0, "an old save without personnel fields gets an empty dictionary");
        var oldName = serving.Name; serving.Name += " changed";
        Check(CampaignFingerprint.Of(c) != frozen, "minister identity participates in the campaign fingerprint");
        serving.Name = oldName;

        n.HeadOfStateName = "A long-lived ruler"; n.HeadOfStateBirthYear = c.Date.Year - 108;
        Check(Rulers.HeadOfState(c, n).Age == 108, "ruler age is not frozen at 98 by a display clamp");
        serving.BirthYear = c.Date.Year - c.Defines.RulerMaximumAge - 1;
        n.Treasury = 100000;
        PoliticalAppointments.ReviewMinisters(c, n, 30);
        Check(!n.CabinetPeople.ContainsKey(portfolio) && !n.Cabinet.ContainsKey(portfolio),
            "a deceased minister leaves a real vacancy instead of keeping eternal effects");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "minister_died"), "minister death is an observable chronicle fact");
        c.Defines.RulersEnabled = false; frozen = CampaignFingerprint.Of(c);
        new RecruitPoliticianCommand { IssuerId = n.Id, CandidateSlot = 0 }.Apply(c);
        new RecruitPoliticianCommand { IssuerId = n.Id, RefreshPool = true }.Apply(c);
        new AppointMinisterCommand { IssuerId = n.Id, PortfolioId = portfolio, OptionId = candidate.OptionId }.Apply(c);
        Check(frozen == CampaignFingerprint.Of(c), "disabled rulers reject personnel commands");

        var timeline = Campaign.CreateBootstrap(992); var local = timeline.Player;
        var slate = Rulers.CandidatePool(timeline, local);
        timeline.Date.Year++;
        var older = Rulers.CandidatePool(timeline, local);
        Check(slate.Zip(older).All(p => p.First.Name == p.Second.Name && p.First.BirthYear == p.Second.BirthYear &&
            p.Second.Age == p.First.Age + 1), "unappointed candidates age instead of rejuvenating at New Year");
        timeline.Date.Year = 2075;
        var future = Rulers.CandidatePool(timeline, local);
        Check(future.All(p => p.Age is >= 35 and <= 69) && future[0].Name != slate[0].Name,
            "future generations bring new adults rather than immortal opening candidates");
        timeline.Date.Year = 1800;
        Check(Rulers.CandidatePool(timeline, local).All(p => p.Age is >= 35 and <= 69),
            "candidate cohorts also work before the modern scenario epoch");
        timeline.Defines.PoliticianCandidateCohortYears = 0;
        Check(Rulers.CandidatePool(timeline, local).Count == 4, "invalid cohort period is safely clamped");

        var oldCabinet = Rulers.AllPortfolios.First(p => p.Options.Count > 0);
        local.Cabinet[oldCabinet.Id] = oldCabinet.Options[0].Id; local.CabinetPeople.Clear();
        var legacyName = Rulers.MinisterPersonName(local, oldCabinet.Id);
        PoliticalAppointments.ReviewMinisters(timeline, local, 30);
        Check(local.CabinetPeople[oldCabinet.Id].Name == legacyName,
            "a legacy portfolio adopts its existing name on a simulation review");
        local.RulerGeneration++;
        Check(Rulers.MinisterPersonName(local, oldCabinet.Id) == legacyName,
            "migrated ministers also survive a ruler change without being renamed");
        var samePolicy = Rulers.CandidatePool(timeline, local)[0];
        local.Cabinet[samePolicy.PortfolioId] = samePolicy.OptionId;
        local.LastCabinetChangeDay = -9999; local.Treasury = 100000;
        Check(!PoliticalAppointments.MayRecruit(timeline, local, 0, false, out _, out _, out _) &&
            PoliticalAppointments.MayRecruit(timeline, local, 0, true, out _, out _, out _),
            "a cabinet policy already in use does not prohibit promoting a politician to national leadership");
        timeline.RestoreRuntimeState(new GameDate(2024, 1, 18), 17, true, timeline.Random.State,
            local.Id, new[] { local.Id }, 0, 0, 0, true, null, null, null, null);
        var bot = timeline.NationByTag("FRA"); bot.Cabinet.Clear(); bot.CabinetPeople.Clear();
        bot.LastCabinetChangeDay = -9999; bot.Treasury = 100000;
        var aiMoney = bot.Treasury; var aiPrice = Rulers.CabinetAppointmentCost(timeline, bot);
        Rulers.Tick(timeline);
        Check(bot.Cabinet.Count == 1 && bot.CabinetPeople.Count == 1 &&
            Math.Abs(bot.Treasury - (aiMoney - aiPrice)) < .02f,
            "AI appointments use the same priced command and create a mortal named person");
        var actualPortfolio = bot.Cabinet.Keys.Single();
        Check(!PoliticalAppointments.MayPromoteMinister(timeline, bot, actualPortfolio, out _, out _),
            "an existing minister cannot bypass promotion cooldown either");
        bot.LastCabinetChangeDay = -9999;
        var actualPerson = bot.CabinetPeople[actualPortfolio];
        actualPerson.TraitId = "hawk";
        Check(PoliticalAppointments.MayPromoteMinister(timeline, bot, actualPortfolio, out var promotionPrice, out _),
            "an eligible existing minister exposes the same promotion price as the command");
        aiMoney = bot.Treasury;
        new AppointMinisterCommand { IssuerId = bot.Id, PortfolioId = actualPortfolio, PromoteToLeader = true }.Apply(timeline);
        var installed = Rulers.HasDualExecutive(bot) ? Rulers.HeadOfGovernment(timeline, bot) : Rulers.HeadOfState(timeline, bot);
        Check(installed.Name == actualPerson.Name && installed.BirthYear == actualPerson.BirthYear && installed.TraitId == actualPerson.TraitId &&
            Math.Abs(bot.Treasury - (aiMoney - promotionPrice)) < .02f,
            "promotion preserves an existing minister's identity, age and temperament and charges exactly once");
        bot.HeadOfStateName = "Young head of state"; bot.HeadOfStateBirthYear = timeline.Date.Year - 40;
        bot.HeadOfGovernmentBirthYear = timeline.Date.Year - timeline.Defines.RulerMaxAge - 1;
        Rulers.Tick(timeline);
        Check(timeline.Chronicle.Entries.Any(e => e.Kind == "government_head_died" && e.ActorId == bot.Id),
            "death of a government head has its own observable succession fact");
        var item = timeline.Chronicle.Entries.Last(e => e.Kind == "government_head_died" && e.ActorId == bot.Id);
        Check(Loc.Format("chronicle.government_head_died", item.ActorName, item.TargetName, item.Subject, item.Place).Contains(item.Place),
            "succession text names the successor instead of an empty counterpart slot");
    }
}
