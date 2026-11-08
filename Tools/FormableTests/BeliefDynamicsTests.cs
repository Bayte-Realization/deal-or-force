using System;
using System.Linq;
using DealOrForce.Simulation;

static class BeliefDynamicsTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS beliefs: " + label); }

    static Campaign Fixture(int seed = 270)
    {
        var c = new Campaign(seed) { ScenarioId = "belief_fixture" };
        var n = new Nation { Id = 0, Tag = "TST", Name = "Fixture", MapIndex = 500, Population = 20_000_000,
            Gdp = 100, Treasury = 20, Stability = .8f, DeclaredReligion = "catholic", CapitalStateMapIndex = 3001 };
        c.Nations.Add(n);
        for (var i = 0; i < 4; i++)
        {
            var state = new ScenarioState { Id = "belief_" + i, Name = "Belief region " + i, MapIndex = 3001 + i,
                NationId = n.Tag, NationMapIndex = n.MapIndex, Population = i == 1 ? 3_000_000 : 5_000_000,
                Literacy = .9f, Stability = .8f, StandardOfLiving = .5f, Radicalism = .1f };
            state.PopulationGroups.Add(new PopulationGroup { Culture = "german", Religion = "catholic", Size = i == 1 ? 2_400_000 : 4_000_000,
                Profession = "labourers", Class = SocialClass.Lower, Employment = .9f, Wealth = .4f, StandardOfLiving = .6f, Radicalism = .1f });
            state.PopulationGroups.Add(new PopulationGroup { Culture = "german", Religion = "irreligion", Size = i == 1 ? 600_000 : 1_000_000,
                Profession = "labourers", Class = SocialClass.Lower, Employment = .8f, Wealth = .5f, StandardOfLiving = .5f, Radicalism = .1f });
            c.States.Add(state);
        }
        var cells = Enumerable.Range(0, 8192).Select(i => (ushort)(3001 + i % 128 / 32)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, Enumerable.Repeat((ushort)500, 8192).ToArray(), 128, 64, _ => false));
        c.ConfigureSinglePlayer(n.Id); SovereignFinance.NormalizeAccounts(c);
        return c;
    }

    public static void Run()
    {
        Check(DemographyCatalog.Religion("atheism")?.Name == "Atheism", "atheism is an actual demographic catalogue entry");
        Check(!StateIdentity.IsSecular("atheism") && StateIdentity.IsAtheist("state_atheism"), "atheism and legacy state atheism are distinct from neutral secularism");
        var c = Fixture(); var n = c.Player; var region = c.States[0];
        var fp = CampaignFingerprint.Of(c); var random = c.Random.State;
        var choices = StateIdentity.ReligionChoices(c, n);
        Check(choices.Count(p => p.Id == "atheism") == 1 && choices.Count(p => p.Id == "irreligion") == 1, "both state choices exist exactly once before there are atheist residents");
        Check(CampaignFingerprint.Of(c) == fp && c.Random.State == random, "reading worldview choices is read-only");
        Check(StateIdentity.BlockReason(c, n, "atheism", "").Length == 0, "an atheist state can be declared as a government policy");
        var cash = n.Treasury; var stability = n.Stability;
        c.Commands.Enqueue(new DeclareStateIdentityCommand { IssuerId = n.Id, Religion = "atheism" }); c.Commands.ApplyAll(c);
        Check(n.DeclaredReligion == "atheism" && n.Stability < stability && n.Treasury == cash, "real declaration command changes identity with the existing stability cost");
        Check(StateIdentity.ReligionShare(c, n, "atheism") == 0, "declaration never instantly relabels people");
        Check(StateIdentity.RegionAlienation(c, n, region) > .9f, "a state worldview opposed by its residents has real identity friction");
        var sum = region.PopulationGroups.Sum(g => g.Size);
        var wealth = region.PopulationGroups.Sum(g => g.Size * (double)g.Wealth);
        var workers = region.PopulationGroups.Sum(g => g.Size * (double)g.Employment);
        var moved = BeliefDynamics.Review(c, n, region, 7);
        Check(moved > 0 && moved < sum / 100, "weekly conversion creates a small gradual atheist cohort");
        Check(region.PopulationGroups.Sum(g => g.Size) == sum && region.Population == sum, "conversion conserves every resident");
        Check(Math.Abs(region.PopulationGroups.Sum(g => g.Size * (double)g.Wealth) - wealth) < .2 &&
            Math.Abs(region.PopulationGroups.Sum(g => g.Size * (double)g.Employment) - workers) < .2, "conversion preserves weighted wealth and employment");
        Check(region.PopulationGroups.All(g => g.Culture == "german" && g.Profession == "labourers"), "belief conversion does not change culture or occupation");
        Check(StateIdentity.ReligionShare(c, n, "state_atheism") == StateIdentity.ReligionShare(c, n, "atheism"), "legacy atheist declarations count actual atheist residents");
        var count = region.PopulationGroups.Count;
        BeliefDynamics.Review(c, n, region, 7);
        Check(region.PopulationGroups.Count == count, "successive reviews reuse cohorts instead of creating duplicate groups");
        n.DeclaredReligion = "irreligion"; fp = CampaignFingerprint.Of(c);
        Check(BeliefDynamics.Review(c, n, region, 365) == 0 && CampaignFingerprint.Of(c) == fp, "neutral secularism neither converts nor creates atheists");
        n.DeclaredReligion = "atheism"; c.Defines.BeliefConversionEnabled = false; fp = CampaignFingerprint.Of(c);
        Check(BeliefDynamics.Review(c, n, region, 365) == 0 && CampaignFingerprint.Of(c) == fp, "conversion can be disabled without changing the census");
        c.Defines.BeliefConversionEnabled = true; c.Defines.BeliefConversionAnnual = float.NaN; fp = CampaignFingerprint.Of(c);
        Check(BeliefDynamics.Review(c, n, region, 7) == 0 && CampaignFingerprint.Of(c) == fp, "invalid rates cannot poison population data");
        c.Defines.BeliefConversionAnnual = .008f;
        var changed = CampaignFingerprint.Of(c); c.Defines.BeliefConversionAnnual = .009f;
        Check(CampaignFingerprint.Of(c) != changed, "non-default belief rules participate in the campaign fingerprint");
        c.Defines.BeliefConversionAnnual = .008f;
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "beliefs"), "beliefs");
        Check(restored.Success && restored.Campaign.Player.DeclaredReligion == "atheism" &&
            Math.Abs(StateIdentity.ReligionShare(restored.Campaign, restored.Campaign.Player, "atheism") - StateIdentity.ReligionShare(c, n, "atheism")) < 1e-7f,
            "save/snapshot round-trip retains atheist state and actual cohorts");
        var twinA = Fixture(271); var twinB = Fixture(271);
        twinA.Player.DeclaredReligion = twinB.Player.DeclaredReligion = "atheism";
        for (var week = 0; week < 52; week++)
        foreach (var pair in twinA.States.Zip(twinB.States))
        { BeliefDynamics.Review(twinA, twinA.Player, pair.First, 7); BeliefDynamics.Review(twinB, twinB.Player, pair.Second, 7); }
        Check(CampaignFingerprint.Of(twinA) == CampaignFingerprint.Of(twinB), "a year of conversion is deterministic on two identical worlds");
        var atheist = twinA.States[0].PopulationGroups.First(g => g.Religion == "atheism");
        twinA.Player.DeclaredReligion = "catholic"; var before = atheist.Size;
        BeliefDynamics.Review(twinA, twinA.Player, twinA.States[0], 7);
        Check(atheist.Size < before, "atheist residents can convert away under the same rules as every other worldview");
        RebelIntegration();
        DailyMovementPeopleCache();
    }

    static void DailyMovementPeopleCache()
    {
        var c = Fixture(273);
        var region = c.States[0];
        var definition = new MovementDefinition { Id = "daily_cache_proof" };
        definition.Peoples.Add("german"); definition.Faiths.Add("catholic");
        var start = MovementRules.PeopleOf(c, region, definition, out _);
        var random = c.Random.State;
        var stored = CampaignFingerprint.Of(c);
        _ = MovementRules.PeopleOf(c, region, definition, out _);
        Check(CampaignFingerprint.Of(c) == stored && c.Random.State == random,
            "reading a warm movement people cache changes no saved state or random stream");
        region.PopulationGroups[0].Size += 12345;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 24L);
        var next = MovementRules.PeopleOf(c, region, definition, out _);
        Check(next == start + 12345, "movement audience refreshes the next day, not only next week");
        var snapshot = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "daily-cache"), "daily-cache");
        Check(snapshot.Success && MovementRules.PeopleOf(snapshot.Campaign,
            snapshot.Campaign.States.First(s => s.MapIndex == region.MapIndex), definition, out _) == next,
            "a cold reloaded movement cache counts the same current population as the warm running campaign");
        c.Player.DeclaredReligion = "atheism";
        var faith = new MovementDefinition { Id = "faith_cache_proof" }; faith.Faiths.Add("catholic");
        MovementRules.PeopleOf(c, region, faith, out var faithfulBefore);
        var changed = BeliefDynamics.Review(c, c.Player, region, 7);
        MovementRules.PeopleOf(c, region, faith, out var faithfulAfter);
        Check(changed > 0 && faithfulAfter < faithfulBefore && faithfulAfter ==
            region.PopulationGroups.Where(g => g.Religion == "catholic").Sum(g => g.Size),
            "belief conversion invalidates movement faith counts immediately within the same day");
    }

    static void RebelIntegration()
    {
        var c = Fixture(272); var n = c.Player;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 200L * 24);
        c.Defines.CivilWarStrikesPlayers = true; n.Stability = .1f;
        foreach (var state in c.States)
        { state.Stability = .1f; state.Radicalism = .9f; foreach (var group in state.PopulationGroups) group.Religion = "atheism"; }
        Check(BeliefDynamics.RebelFaith(c, n, c.States) == "atheism", "a rooted angry atheist majority can identify a local opposition");
        Check(CivilWarOutbreak.EligibilityReason(c, n).Length == 0, "ordinary civil-war eligibility is still required");
        var report = CivilWarOutbreak.Break(c, n);
        Check(report?.Seceded is { DeclaredReligion: "atheism" }, "actual civil-war path can create an atheist rebel state");
        var question = c.PendingEvents.FirstOrDefault(e => e.NationId == n.Id && e.Options.Any(o => o.SecessionTargetId == report.Seceded.Id));
        Check(question is not null, "a human government gets the real rebellion response event rather than an automatic war");
        var fight = Array.FindIndex(question.Options, o => o.SecessionChoice == "fight");
        new ResolveCampaignEventCommand { IssuerId = n.Id, EventId = question.Id, OptionIndex = fight }.Apply(c);
        Check(c.Wars.Any(w => w.CivilConflict && !w.Ended), "the actual Send the army response starts a civil war against atheist rebels");
        Check(CivilWarOutbreak.EligibilityReason(c, n).Length > 0, "belief-driven opposition cannot bypass war/recovery protection to rise twice");
        var reverse = Fixture(273); reverse.Player.DeclaredReligion = "atheism";
        foreach (var state in reverse.States) state.Radicalism = .9f;
        Check(BeliefDynamics.RebelFaith(reverse, reverse.Player, reverse.States) == "catholic", "religious opposition to an atheist state follows the identical rule");
        foreach (var state in reverse.States) state.Radicalism = .1f;
        Check(BeliefDynamics.RebelFaith(reverse, reverse.Player, reverse.States) == "", "different beliefs alone do not invent an uprising");
    }
}
