using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class SocialMemoryTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS social memory: " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(711);
        var a = c.NationByTag("GBR"); var b = c.NationByTag("CAN");
        void Day(int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
        Check(c.FeatureEnabled("historical_norms") && c.FeatureEnabled("public_reputation"), "built-in switches are available");
        Check(SocialMemory.Image(a, "nation:unknown") is null && !a.PublicImages.ContainsKey("nation:unknown"), "UI lookups never create beliefs");
        a.Stability = 0.8f; a.StandardOfLiving = 0.8f; a.ShortageIndex = 0f;
        var region = c.StatesOf(a).First(); region.StandardOfLiving = 0.8f; region.Radicalism = 0.1f;
        Day(7); SocialMemory.Tick(c);
        Day(3657); SocialMemory.Tick(c);
        Check(a.InstitutionalHabit > 0.65f && region.BorderHabit > 0.5f, "peaceful years establish habits");
        Check(region.BorderHabit > 0.8f, "the world as it was drawn is already an old border");
        Check(SocialMemory.BorderRelief(c, region) <= 0.25f, "old borders never erase all secession pressure");
        var age = a.InstitutionAgeDays; SocialMemory.Tick(c);
        Check(a.InstitutionAgeDays == age, "repeated daily calls do not age the world twice");
        var habitBonus = SocialMemory.StabilityEffect(c, a);
        a.StandardOfLiving = 0.2f;
        Check(habitBonus > 0f && SocialMemory.StabilityEffect(c, a) < 0f, "a lost standard of living outweighs institutional comfort");
        a.Government = "test_changed_government"; Day(3664); SocialMemory.Tick(c);
        Check(a.InstitutionAgeDays <= 7 && a.InstitutionalHabit < 0.01f, "new institutions reset their own habit");
        region.NationMapIndex = b.MapIndex; Day(3671); SocialMemory.Tick(c);
        Check(region.SettledBorderDays <= 7, "ownership change resets border memory");
        c.SetFeature("historical_norms", false); var disabledAge = a.InstitutionAgeDays;
        Day(7000); SocialMemory.Tick(c);
        Check(a.InstitutionAgeDays == disabledAge && SocialMemory.StabilityEffect(c, a) == 0f, "disabled habits freeze and exert no effect");
        var expectedDomestic = (a.Stability - 0.5f) * 1.3f + (a.StandardOfLiving - 0.5f) * 0.5f;
        Check(MathF.Abs(a.DomesticImage.Target - M.Clamp(expectedDomestic + a.DomesticImage.Incident, -1f, 1f)) < .0001f,
            "disabled expectations cannot leak into domestic reputation");
        c.SetFeature("historical_norms", true); SocialMemory.Tick(c);
        Check(a.InstitutionAgeDays == disabledAge, "reenabling does not invent disabled years");

        var image = new PublicImage(); var rules = new Defines();
        SocialMemory.AddIncident(image, 1f, "test", 0);
        Check(image.Esteem == 0f && image.Status == 0, "one incident cannot instantly create an idol");
        for (var day = 0; day < 2000; day += 7) image.Review(day, 0.9f, rules);
        Check(image.Status == 1 && image.Entrenchment > 0.5f, "consistent admiration becomes an entrenched image");
        image.Review(2002, -1f, rules);
        Check(image.Status == 1, "one reversal does not flash an established label");
        for (var day = 2009; day < 5000; day += 7) image.Review(day, -0.9f, rules);
        Check(image.Status == -1, "an idol can become stigmatised after sustained change");
        var oldEsteem = image.Esteem; image.Review(5010, float.NaN, rules);
        Check(!SocialMemory.AddIncident(image, float.PositiveInfinity, "bad", 1) && image.Esteem == oldEsteem, "non-finite input cannot contaminate an image");

        var towards = SocialMemory.Image(a, SocialMemory.NationKey(b), true);
        towards.Esteem = 0.8f;
        SocialMemory.Image(b, SocialMemory.NationKey(a), true).Esteem = -0.8f;
        Check(SocialMemory.Esteem(c, a, b) > 0f && SocialMemory.Esteem(c, b, a) < 0f, "different audiences can hold opposite views");
        var cultureCount = a.PublicImages.Count(p => p.Key.StartsWith("culture:"));
        c.Chronicle.Record(c, "war_started", a.Id, b.Id);
        Check(SocialMemory.Image(b, SocialMemory.NationKey(a)).Incident < 0f, "a public war has a witnessed reputational cost");
        Check(a.PublicImages.Count(p => p.Key.StartsWith("culture:")) == cultureCount, "government actions do not create collective ethnic blame");
        var incident = a.DomesticImage.Incident;
        c.Chronicle.Record(c, "war_started", a.Id, b.Id, isPublic: false);
        Check(a.DomesticImage.Incident == incident, "private facts do not become global reputational events");
        c.Chronicle.Record(c, "movement_designated", a.Id, b.Id);
        Check(towards.Incident < 0f, "designation changes the issuing audience's view of the target");
        c.SetFeature("public_reputation", false);
        Check(SocialMemory.Esteem(c, a, b) == 0f && SocialMemory.OpinionEffect(c, CampaignDiplomacy.Get(c, a.Id, b.Id)) == 0f, "disabled reputation has no diplomatic effect");
        c.SetFeature("public_reputation", true);
        var fingerprint = CampaignFingerprint.Of(c); towards.Incident -= 0.01f;
        Check(CampaignFingerprint.Of(c) != fingerprint, "pending belief changes participate in multiplayer fingerprints");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var restored = JsonSerializer.Deserialize<Nation>(JsonSerializer.Serialize(a, options), options);
        Check(restored.InstitutionAgeDays == a.InstitutionAgeDays && restored.PublicImages[SocialMemory.NationKey(b)].Incident == towards.Incident,
            "additive nation fields survive the save serializer");
        var legacy = JsonSerializer.Deserialize<Nation>("{\"Tag\":\"OLD\"}", options);
        Check(legacy.ExpectedLivingStandard == -1f && legacy.PublicImages.Count == 0, "legacy saves receive unknown history, not fabricated beliefs");
        var errors = new List<string>();
        using var patch = JsonDocument.Parse("{\"war_started\":{\"actor\":-0.25},\"invalid\":{\"actor\":\"oops\"}}");
        SocialIncidentCatalog.Merge(patch.RootElement, errors);
        Check(SocialIncidentCatalog.Find("war_started", out var fact) && fact.Actor == -0.25f && fact.Target == 0.06f,
            "mod overlays preserve omitted incident properties");
        Check(errors.Count == 1, "invalid mod weights produce useful diagnostics");
        SocialIncidentCatalog.ResetCatalog();
        Check(OutreachCatalog.Find("cultural_exchange") is { DurationDays: 120, CultureImpulse: > 0f },
            "public outreach definitions load from moddable game data");
        using var outreachPatch = JsonDocument.Parse("{\"cultural_exchange\":{\"nation_impulse\":0.2,\"duration_days\":\"oops\"}}");
        errors.Clear(); OutreachCatalog.Merge(outreachPatch.RootElement, errors);
        Check(errors.Count == 1 && OutreachCatalog.Find("cultural_exchange").DurationDays == 120 &&
            OutreachCatalog.Find("cultural_exchange").NationImpulse == .2f,
            "outreach overlays preserve omitted properties and report invalid values");
        OutreachCatalog.ResetCatalog();
        TestOutreach();
        TestScriptsAndPersistence();
    }

    static void TestOutreach()
    {
        var c = Campaign.CreateBootstrap(716);
        var a = c.NationByTag("GBR"); var b = c.NationByTag("CAN");
        var before = a.Treasury; var beliefs = b.PublicImages.Count;
        var command = new StartPublicOutreachCommand { IssuerId = a.Id, TargetId = b.Id, ActionId = "cultural_exchange" };
        Check(NetworkCommandCodec.TryEncode(command, out var type, out var payload) &&
            NetworkCommandCodec.TryDecode(type, payload, out var decoded) && decoded is StartPublicOutreachCommand,
            "outreach is a regular network-serializable campaign command");
        command.Apply(c);
        Check(command.Started && a.Outreach.Count == 1 && a.Treasury < before && b.PublicImages.Count == beliefs,
            "starting outreach pays once without instantly creating beliefs");
        var after = a.Treasury;
        Check(!PublicOutreachActions.Start(c, a, b, "public_dialogue") && a.Treasury == after,
            "different outreach types cannot bypass the shared audience lock");
        var image = a.Outreach[b.Tag];
        var fingerprint = CampaignFingerprint.Of(c); image.ProgressDays++;
        Check(fingerprint != CampaignFingerprint.Of(c), "outreach progress participates in fingerprints");
        image.ProgressDays--;
        Check(!PublicOutreachActions.Start(c, a, b, "unknown_action") && a.Treasury == after,
            "unknown actions fail without charging money");
        c.SetFeature("public_reputation", false);
        Check(!PublicOutreachActions.Start(c, b, a, "public_dialogue"), "disabled reputation blocks public programmes");
        void Day(int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
        Day(365); PublicOutreachActions.Tick(c);
        Check(image.ProgressDays == 0, "disabled outreach freezes paid work");
        c.SetFeature("public_reputation", true); PublicOutreachActions.Tick(c);
        Check(image.ProgressDays == 0 && image.CooldownUntilDay == 725, "reactivation cannot fast-forward a paid programme");
        Day(372); PublicOutreachActions.Tick(c);
        var belief = SocialMemory.Image(b, SocialMemory.NationKey(a));
        Check(image.ProgressDays == 7 && belief.Incident > 0f && belief.Esteem == 0f && belief.Status == 0,
            "funded contact contributes gradually rather than buying a status");
        Check(DemographyCatalog.PrimaryCultures(a.Tag).All(culture => SocialMemory.Image(b, "culture:" + culture)?.Incident > 0f),
            "cultural exchange affects named peoples separately from governments");
        var incident = belief.Incident; PublicOutreachActions.Tick(c);
        Check(belief.Incident == incident, "outreach cannot pay its effect twice on one day");
        Day(1000); PublicOutreachActions.Tick(c);
        Check(MathF.Abs(belief.Incident - image.NationImpulse) < .00001f && !image.Active,
            "a large time step cannot exceed the promised programme budget");
        Check(a.Outreach.Count == 0, "expired programme history is bounded by the cooldown");
        Check(PublicOutreachActions.Start(c, a, b, "public_dialogue"), "a new programme can start after cooldown");
        c.Wars.Add(new StrategicWar { AttackerId = a.Id, DefenderId = b.Id });
        var paid = a.Treasury; Day(1007); PublicOutreachActions.Tick(c);
        Check(a.Outreach[b.Tag].Cancelled && a.Treasury == paid, "war cancels contact without a false completion or free refund");
        Check(CampaignDiplomacy.AtWar(c, b.Id, a.Id) && !CampaignDiplomacy.AtWar(c, a.Id, a.Id),
            "allocation-free war lookup preserves opposite-side semantics");
        c.Wars[^1].Ended = true;
        Check(!CampaignDiplomacy.AtWar(c, a.Id, b.Id), "ended wars do not block contact");
        var actor = c.NationByTag("FRA"); var target = c.NationByTag("DEU");
        var action = OutreachCatalog.Find("public_dialogue");
        var sanctions = CampaignDiplomacy.Get(c, actor.Id, target.Id);
        if (sanctions.A == actor.Id) sanctions.EmbargoByA = true; else sanctions.EmbargoByB = true;
        var treasury = actor.Treasury;
        Check(!PublicOutreachActions.Start(c, actor, target, action.Id) && actor.Treasury == treasury,
            "embargo rejects outreach without charging");
        sanctions.EmbargoByA = sanctions.EmbargoByB = false;
        actor.Treasury = -1f;
        Check(!PublicOutreachActions.Start(c, actor, target, action.Id), "unfunded outreach is rejected");
        actor.Treasury = treasury; c.Defines.PublicOutreachMaxActive = 1;
        Check(PublicOutreachActions.Start(c, actor, target, action.Id) &&
            !PublicOutreachActions.Start(c, actor, c.NationByTag("USA"), action.Id),
            "modded programme capacity applies across different audiences");
        OutreachCatalog.Find(action.Id).NationImpulse = .4f;
        Check(actor.Outreach[target.Tag].NationImpulse == .24f,
            "existing paid programmes keep their agreed budget when definitions reload");
        OutreachCatalog.ResetCatalog();
    }

    static void TestScriptsAndPersistence()
    {
        var c = Campaign.CreateBootstrap(714);
        var a = c.NationByTag("GBR"); var b = c.NationByTag("CAN");
        var culture = DemographyCatalog.Cultures.Keys.First();
        void Day(int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
        ScriptedEffects.Effect Effect(string key, float value)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, float> { [key] = value }));
            var errors = new List<string>(); var effect = ScriptedEffects.Parse(doc.RootElement, "social test", errors);
            Check(errors.Count == 0, "script vocabulary accepts " + key);
            return effect;
        }
        var cultureKey = "culture:" + culture;
        Effect("reputation.culture." + culture + "_set", -1f).Apply(c, a);
        Effect("reputation.culture." + culture + "_set", 1f).Apply(c, a);
        Check(SocialMemory.Image(a, cultureKey).Incident == 1f && SocialMemory.Image(b, cultureKey) is null,
            "scripted culture views are audience-scoped and set can cross both bounds");
        for (int day = 7; day <= 1407; day += 7)
        {
            Day(day);
            SocialMemory.Image(a, cultureKey).Incident = 1f; // a sustained authored cultural narrative, not a single event
            SocialMemory.Tick(c);
        }
        Check(SocialMemory.Image(a, cultureKey).Status == 1, "sustained cultural narratives can establish idealisation");
        for (int day = 1414; day <= 4900; day += 7)
        {
            Day(day); SocialMemory.Image(a, cultureKey).Incident = -1f; SocialMemory.Tick(c);
        }
        Check(SocialMemory.Image(a, cultureKey).Status == -1, "cultural images can reverse rather than becoming permanent stereotypes");
        Effect("reputation.nation.can_set", .8f).Apply(c, a);
        Check(SocialMemory.Image(a, "nation:CAN").Incident == .8f, "scripted nation tags are canonicalised");
        var relation = CampaignDiplomacy.Get(c, a.Id, b.Id);
        relation.Opinion = 0; relation.Stance = DiplomaticStance.Neutral;
        var image = SocialMemory.Image(a, "nation:CAN"); image.Esteem = 0f;
        var baseWillingness = AllianceSystem.Willingness(c, a, b);
        image.Esteem = .8f;
        Check(MathF.Abs(AllianceSystem.Willingness(c, a, b) - baseWillingness - .8f * c.Defines.SocialAllianceWeight) < .0001f,
            "alliance willingness reads the asked society's directed view");
        var counts = a.PublicImages.Count;
        Effect("reputation.culture.invalid_culture_set", 1f).Apply(c, a);
        Effect("reputation.nation.NO_SUCH_TAG_set", 1f).Apply(c, a);
        Check(a.PublicImages.Count == counts, "unknown subjects do not fill saved images with junk");
        c.SetFeature("public_reputation", false);
        var pending = image.Incident; Effect("reputation.nation.CAN", -.7f).Apply(c, a);
        Check(image.Incident == pending, "disabled reputation rejects scripted impulses");
        var domesticPending = a.DomesticImage.Incident;
        Effect("social.domestic_incident_set", 1f).Apply(c, a);
        Check(a.DomesticImage.Incident == domesticPending, "disabled domestic reputation rejects scripted impulses");
        c.SetFeature("public_reputation", true);
        c.SetFeature("historical_norms", false);
        var expectation = a.ExpectedLivingStandard;
        Effect("social.expected_living_set", 1f).Apply(c, a);
        Check(a.ExpectedLivingStandard == expectation, "disabled expectations reject scripted updates");
        c.SetFeature("historical_norms", true);
        Check(PublicOutreachActions.Start(c, a, b, "cultural_exchange"), "snapshot fixture contains paid outreach");
        var outreach = a.Outreach[b.Tag];
        c.Defines.FeatureSystems[nameof(SocialMemory)] = "social_test_system";
        c.SetFeature("social_test_system", false); var age = a.InstitutionAgeDays;
        Day(9000); SocialMemory.Tick(c);
        Check(a.InstitutionAgeDays == age && SocialMemory.Esteem(c, a, b) == 0f && SocialMemory.StabilityEffect(c, a) == 0f,
            "a modded system gate disables all effects, not just ticking");
        c.SetFeature("social_test_system", true); SocialMemory.Tick(c);
        Check(a.InstitutionAgeDays == age, "reenabling a system gate does not invent dormant years");
        var left = c.StatesOf(a).First(); var right = c.StatesOf(b).First();
        var map = ProvinceTopology.Build(new ushort[] { (ushort)left.MapIndex, (ushort)right.MapIndex },
            new ushort[] { (ushort)a.MapIndex, (ushort)b.MapIndex }, 2, 1, _ => false);
        c.AttachProvinceTopology(map);
        var snapshot = SaveSystem.ExportNetworkSnapshot(c, "social tests");
        var restore = SaveSystem.ImportNetworkSnapshot(snapshot, "social tests");
        Check(restore.Success, "full campaign snapshot restores successfully");
        var loaded = restore.Campaign.NationById(a.Id);
        Check(loaded.InstitutionAgeDays == a.InstitutionAgeDays && loaded.ExpectedLivingStandard == a.ExpectedLivingStandard &&
            loaded.PublicImages[cultureKey].Status == -1 && loaded.PublicImages["nation:CAN"].Incident == image.Incident,
            "full snapshot retains habit, expectations, cultural labels and pending impulses");
        Check(loaded.Outreach[b.Tag].PaidCost == outreach.PaidCost && loaded.Outreach[b.Tag].PausedDay == outreach.PausedDay &&
            loaded.Outreach[b.Tag].CooldownUntilDay == outreach.CooldownUntilDay,
            "full snapshot retains paid outreach and its frozen clocks");
        Day(9007); SocialMemory.Tick(c);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(restore.Campaign, 9007L * 24);
        SocialMemory.Tick(restore.Campaign);
        Check(loaded.PublicImages[cultureKey].Esteem == SocialMemory.Image(a, cultureKey).Esteem &&
            loaded.InstitutionAgeDays == a.InstitutionAgeDays, "loaded social memory continues deterministically");
        Check(loaded.Outreach[b.Tag].ProgressDays == outreach.ProgressDays,
            "loaded outreach resumes the same progress without duplicate funding");
    }
}
