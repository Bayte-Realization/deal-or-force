using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }

    static void Main()
    {
        Check(SimulationPacing.Accumulate(0, .01, .1, 4) == .01, "ordinary frames accumulate exactly");
        Check(SimulationPacing.Accumulate(12, 20, .1, 4) == .4, "a stall cannot queue unlimited fast-forward");
        Check(SimulationPacing.Accumulate(double.NaN, double.PositiveInfinity, .1, 4) == 0, "invalid wall time does not poison the clock");
        Check(SimulationPacing.Accumulate(0, 1, 0, 4) == 0, "invalid cadence is safe");
        var skinErrors = new List<string>();
        InterfaceSkin.Merge("{\"metrics\":{\"formation.title_size\":19}}", "test", skinErrors);
        InterfaceSkin.Merge("{\"metrics\":{\"formation.title_size\":1e99}}", "test", skinErrors);
        Check(skinErrors.Count == 1 && InterfaceSkin.Metric("formation.title_size", 0) == 19, "overflowing mod metrics are diagnosed without corrupting the previous skin");
        InterfaceSkin.ResetCatalog();
        var campaign = Campaign.CreateBootstrap(20261001);
        campaign.IsPaused = false;
        foreach (var hours in new[] { 1, 3, 6, 12, 24, 48, 72 })
        {
            var before = campaign.ElapsedHours;
            campaign.Defines.HoursPerTick = hours;
            campaign.Tick();
            Check(campaign.ElapsedHours - before == hours, $"one simulation step advances exactly {hours} hours");
        }
        campaign = Campaign.CreateBootstrap(20261001);
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var map, out var message), message);
        campaign.AttachProvinceTopology(map);
        campaign.Wars.Clear();
        var founder = campaign.NationByTag("KOR");
        var partner = campaign.NationByTag("PRK");
        campaign.ConfigureSinglePlayer(founder.Id);
        founder.Stability = partner.Stability = .9f;
        founder.Treasury = 10000f;
        var definition = FormableNations.Find("united_korea");
        Check(definition.PeacefulAccession && definition.SubjectIntegration, "data enables treaty and subject routes");
        Check(!FormableNations.Evaluate(campaign, founder, definition).CanForm, "sovereign territory without consent blocks formation");
        partner.SubjectType = SubjectCatalog.All.First().Id; partner.OverlordId = founder.Id;
        partner.LibertyDesire = .1f;
        Check(FormableNations.Evaluate(campaign, founder, definition).CanForm, "a loyal subject can take the configured integration route");
        partner.LibertyDesire = .9f;
        Check(!FormableNations.Evaluate(campaign, founder, definition).CanForm, "a disloyal subject cannot be silently integrated");
        partner.LibertyDesire = .1f; partner.OverlordId = campaign.NationByTag("JPN").Id;
        Check(!FormableNations.Evaluate(campaign, founder, definition).CanForm, "a foreign subject cannot be signed away");
        partner.SubjectType = ""; partner.OverlordId = 0;
        var relation = CampaignDiplomacy.Get(campaign, founder.Id, partner.Id);
        relation.Opinion = 0; relation.Stance = DiplomaticStance.Neutral;
        Check(PeacefulFormation.Refusal(campaign, founder, partner, definition).Length > 0, "poor relations block talks");
        relation.Opinion = 100; relation.Stance = DiplomaticStance.Allied; relation.TradeAgreement = true;
        partner.Government = founder.Government; partner.Ideology = founder.Ideology;
        Check(PeacefulFormation.CulturalAffinity(founder, partner) == 1f, "divided Korean states share a people");
        Check(PeacefulFormation.Invite(campaign, founder, partner, definition), "a willing AI signs a treaty");
        Check(FormableNations.Evaluate(campaign, founder, definition).CanForm, "signed treaty supplies required land without invasion");
        partner.DecisionAvailableDay[PeacefulFormation.ConsentKey(founder.Id, definition.Id)] = campaign.ElapsedDays;
        Check(!FormableNations.Evaluate(campaign, founder, definition).CanForm, "expired consent is not a permanent claim");
        Check(PeacefulFormation.Accept(campaign, founder, partner, definition), "renewed explicit consent restores the route");
        var province = ProvinceOwnership.ProvincesOf(partner.Tag).First(p => !map.WaterByProvince[p]);
        var controller = campaign.ProvinceController[province];
        campaign.ProvinceController[province] = 0;
        Check(!FormableNations.Evaluate(campaign, founder, definition).CanForm, "occupied partner land blocks proclamation");
        campaign.ProvinceController[province] = controller;
        var encoded = NetworkCommandCodec.TryEncode(new InviteFormationCommand { IssuerId = founder.Id, PartnerId = partner.Id, FormableId = definition.Id }, out var kind, out var json);
        Check(encoded && NetworkCommandCodec.TryDecode(kind, json, out _), "invitation survives network command roundtrip");
        var pending = new PendingCampaignEvent { NationId = partner.Id, Options = new[] { new CampaignEventOption { UnionFounderId = founder.Id, UnionFormableId = definition.Id } } };
        var options = new JsonSerializerOptions { IncludeFields = true };
        var restored = JsonSerializer.Deserialize<PendingCampaignEvent>(JsonSerializer.Serialize(pending, options), options);
        Check(restored.Options[0].UnionFounderId == founder.Id && restored.Options[0].UnionFormableId == definition.Id, "pending treaty choice remains editable save data");
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(campaign, ""), "").Campaign;
        Check(PeacefulFormation.Consented(loaded, loaded.NationByTag("KOR"), loaded.NationByTag("PRK"), definition), "consent survives real campaign save snapshot");
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var loadedMap, out message), message);
        loaded.AttachProvinceTopology(loadedMap);
        var humanFounder = loaded.NationByTag("PRK");
        var humanPartner = loaded.NationByTag("KOR");
        loaded.ConfigureSinglePlayer(humanPartner.Id);
        humanFounder.Treasury = 10000;
        var scoped = definition with { Id = "scoped_test", RequiredTags = Array.Empty<string>(), RequiredProvinces = new[] { province } };
        Check(PeacefulFormation.Accept(loaded, humanPartner, humanFounder, scoped), "a scoped treaty explicitly authorises its one province");
        var partnerLand = loadedMap.ProvinceToNation.Count(o => o == humanFounder.MapIndex);
        PeacefulFormation.Integrate(loaded, humanPartner, scoped);
        Check(loadedMap.ProvinceToNation.Count(o => o == humanFounder.MapIndex) == partnerLand - 1 &&
              loaded.Armies.Any(a => a.NationId == humanFounder.Id), "partial accession transfers only declared land and leaves the partner's army intact");
        loaded.AddHumanSeat(humanFounder.Id);
        Check(PeacefulFormation.Refusal(loaded, humanFounder, humanPartner, definition).Contains("multiplayer"), "multiplayer player seats cannot be merged");
        loaded.ConfigureSinglePlayer(humanPartner.Id);
        var expired = new PendingCampaignEvent { NationId = humanPartner.Id, ExpiresDay = loaded.ElapsedDays,
            Options = new[] { new CampaignEventOption { UnionFounderId = humanFounder.Id, UnionFormableId = definition.Id } } };
        loaded.PendingEvents.Add(expired);
        EventSystem.Resolve(loaded, expired, 0);
        Check(!PeacefulFormation.Consented(loaded, humanFounder, humanPartner, definition), "an expired human proposal cannot grant consent");
        var humanRefusal = PeacefulFormation.Refusal(loaded, humanFounder, humanPartner, definition);
        Check(PeacefulFormation.Invite(loaded, humanFounder, humanPartner, definition), "a human receives a proposal instead of automatic consent: " + humanRefusal);
        var proposal = loaded.PendingEvents.Single(e => e.Options.Any(o => o.UnionFounderId == humanFounder.Id));
        Check(!PeacefulFormation.Consented(loaded, humanFounder, humanPartner, definition), "pending invitation does not count as consent");
        EventSystem.Resolve(loaded, proposal, 0);
        Check(PeacefulFormation.Consented(loaded, humanFounder, humanPartner, definition), "the player's chosen treaty grants consent");
        new FormNationCommand { IssuerId = humanFounder.Id, FormableId = definition.Id }.Apply(loaded);
        Check(loaded.Player.Id == humanFounder.Id && loaded.HumanSeats.SequenceEqual(new[] { humanFounder.Id }), "a consenting single player continues as the joined state");
        founder.ProductionLines.Add(new ProductionLine { Good = Commodity.Arms, Units = 3 });
        partner.ProductionLines.Add(new ProductionLine { Good = Commodity.Arms, Units = 2 });
        partner.ResearchProgress["union_test_progress"] = .4f;
        partner.Technologies.Add("union_test_technology");
        var rawAnnual = founder.Gdp / founder.OutputScale + partner.Gdp / partner.OutputScale;
        var population = campaign.States.Sum(s => s.Population);
        var treasury = founder.Treasury + partner.Treasury;
        var reserve = founder.ManpowerReserve + partner.ManpowerReserve;
        var cost = FormableNations.Evaluate(campaign, founder, definition).Cost;
        var formations = campaign.Armies.Count;
        var support = campaign.SupportFormations.Count;
        var fleets = campaign.Fleets.Count;
        var partnerArmyIds = campaign.Armies.Where(a => a.NationId == partner.Id).Select(a => a.Id).ToArray();
        new FormNationCommand { IssuerId = founder.Id, FormableId = definition.Id }.Apply(campaign);
        Check(founder.FormedNationId == definition.Id, "treaty route proclaims the new state");
        Check(campaign.States.Sum(s => s.Population) == population, "integration conserves all regional population");
        Check(MathF.Abs(founder.Treasury - (treasury - cost)) < .02f && partner.Treasury == 0, "integration conserves savings minus the displayed cost");
        Check(MathF.Abs(founder.ManpowerReserve - reserve) < 8f && partner.ManpowerReserve == 0, "integration conserves available manpower");
        Check(campaign.Armies.Count == formations && campaign.SupportFormations.Count == support && campaign.Fleets.Count == fleets, "integration deletes no military assets");
        Check(partnerArmyIds.All(id => campaign.Armies.Any(a => a.Id == id && a.NationId == founder.Id)), "partner formations retain identity and join the founder");
        Check(founder.ProductionLines.Single(l => l.Good == Commodity.Arms).Units == 5 && partner.ProductionLines.Count == 0, "production allocations join instead of remaining on an empty country");
        Check(founder.Technologies.Contains("union_test_technology") && founder.ResearchProgress["union_test_progress"] == .4f, "completed technology and unfinished research both survive");
        Check(MathF.Abs(founder.Gdp / founder.OutputScale - rawAnnual) < .02f, "merged economy preserves output calibration");
        Check(campaign.StatesOf(partner).Length == 0 && partner.CapitalStateMapIndex == 0, "joined country leaves no phantom capital");
        var taken = map.ProvinceToNation.Count(owner => owner == founder.MapIndex);
        new FormNationCommand { IssuerId = founder.Id, FormableId = definition.Id }.Apply(campaign);
        Check(map.ProvinceToNation.Count(owner => owner == founder.MapIndex) == taken, "repeated proclamation cannot transfer land twice");

        campaign = Campaign.CreateBootstrap(20261001);
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out map, out message), message);
        campaign.AttachProvinceTopology(map);
        var parent = campaign.NationByTag("RUS");
        campaign.ConfigureSinglePlayer(parent.Id);
        var region = campaign.StatesOf(parent).First(s => s.Name.Contains("Tatar", StringComparison.OrdinalIgnoreCase));
        var report = SecessionEvents.Force(campaign, region, "tatar");
        Check(report is not null, "a separatist country actually forms");
        var choice = campaign.PendingEvents.Single(e => e.NationId == parent.Id && e.Options.Any(o => o.DeclareWarOn == report.Seceded.Id));
        Check(!CampaignDiplomacy.AtWar(campaign, parent.Id, report.Seceded.Id), "the player is asked before a reclamation war");
        EventSystem.Resolve(campaign, choice, Array.FindIndex(choice.Options, o => o.DeclareWarOn == report.Seceded.Id));
        Check(CampaignDiplomacy.AtWar(campaign, parent.Id, report.Seceded.Id), "Send the army starts the real secession war (103)");
        using var overlay = JsonDocument.Parse("{\"united_korea\":{\"peaceful_accession\":false}}");
        var errors = new List<string>();
        FormableNations.Merge(overlay.RootElement, errors);
        Check(errors.Count == 0 && !FormableNations.Find("united_korea").PeacefulAccession && FormableNations.Find("united_korea").SubjectIntegration, "mods can override one route without rewriting the nation");
        FormableNations.ResetCatalog();
        AiFoundations();
        EditorFoundations();
        Console.WriteLine("Campaign upgrade checks complete.");
    }

    static void SetDay(Campaign campaign, int day)
    {
        // Use the real restore clock rather than running unrelated AI/economy in a
        // focused diplomacy test. Restore does not replace the attached topology.
        campaign.RestoreRuntimeState(campaign.Date, day, true, campaign.Random.State,
            campaign.Player.Id, campaign.HumanSeats.ToArray(), campaign.ActiveSeat, campaign.NextBattleId,
            campaign.MapRevision, campaign.InitialDeploymentCompleted, campaign.ProvinceController,
            campaign.ProvinceOccupation, campaign.ProvinceMap.ProvinceToNation, campaign.ProvinceMap.ProvinceToState);
    }

    static void AiFoundations()
    {
        var campaign = Campaign.CreateBootstrap(20261001);
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var map, out var message), message);
        campaign.AttachProvinceTopology(map);
        campaign.Wars.Clear();
        campaign.Defines.UnionAiReviewDays = 1;
        campaign.Defines.UnionAiEarliestDay = 10;
        var definition = FormableNations.Find("united_korea");
        Check(definition.AiFoundation, "AI formation is enabled through the layered catalogue");
        var koreas = new[] { campaign.NationByTag("KOR"), campaign.NationByTag("PRK") };
        var founder = koreas.OrderByDescending(n => PeacefulFormation.Territory(campaign, definition)
            .Count(p => !map.WaterByProvince[p] && map.ProvinceToNation[p] == n.MapIndex)).ThenBy(n => n.Id).First();
        var partner = koreas.Single(n => n != founder);
        founder.Stability = partner.Stability = .95f;
        founder.Gdp = 200; partner.Gdp = 100;
        founder.Treasury = partner.Treasury = 1000;
        partner.Ideology = founder.Ideology; partner.Government = founder.Government;
        var relation = CampaignDiplomacy.Get(campaign, founder.Id, partner.Id);
        relation.Opinion = 100; relation.Stance = DiplomaticStance.Allied; relation.TradeAgreement = true;
        campaign.ConfigureSinglePlayer(campaign.NationByTag("JPN").Id);
        var random = campaign.Random.State;
        SetDay(campaign, 9);
        PeacefulFormationAi.Tick(campaign);
        Check(!PeacefulFormation.Consented(campaign, founder, partner, definition), "AI respects the initial grace period");
        SetDay(campaign, 10);
        campaign.Defines.UnionAiReviewDays = 0;
        PeacefulFormationAi.Tick(campaign);
        Check(!founder.DecisionAvailableDay.ContainsKey("union_ai_review"), "mods can switch automatic union diplomacy off");
        campaign.Defines.UnionAiReviewDays = 1;
        campaign.ConfigureSinglePlayer(founder.Id);
        PeacefulFormationAi.Tick(campaign);
        Check(!PeacefulFormation.Consented(campaign, founder, partner, definition) &&
            !founder.DecisionAvailableDay.ContainsKey("union_ai_review"), "the leading human country is never governed by union AI");
        campaign.ConfigureSinglePlayer(campaign.NationByTag("JPN").Id);
        founder.Treasury = partner.Treasury = 0;
        SetDay(campaign, 11);
        PeacefulFormationAi.Tick(campaign);
        Check(!PeacefulFormation.Consented(campaign, founder, partner, definition), "AI will not spend away its treasury reserve");
        founder.Treasury = partner.Treasury = 1000;
        var province = PeacefulFormation.Territory(campaign, definition).First(p => !map.WaterByProvince[p]);
        var controller = campaign.ProvinceController[province];
        campaign.ProvinceController[province] = 0;
        SetDay(campaign, 12);
        PeacefulFormationAi.Tick(campaign);
        Check(!PeacefulFormation.Consented(campaign, founder, partner, definition), "occupation blocks AI's diplomatic foundation project");
        campaign.ProvinceController[province] = controller;
        campaign.ConfigureSinglePlayer(partner.Id);
        SetDay(campaign, 13);
        PeacefulFormationAi.Tick(campaign);
        var invitation = campaign.PendingEvents.Single(e => e.NationId == partner.Id &&
            e.Options.Any(o => o.UnionFormableId == definition.Id));
        Check(!PeacefulFormation.Consented(campaign, founder, partner, definition) && string.IsNullOrEmpty(founder.FormedNationId),
            "AI invites the player through a real event, never by automatic annexation");
        PeacefulFormationAi.Tick(campaign);
        Check(campaign.PendingEvents.Count(e => e.NationId == partner.Id && e.Options.Any(o => o.UnionFormableId == definition.Id)) == 1,
            "repeated AI ticks on the same day do not duplicate proposals");
        var snapshot = SaveSystem.ExportNetworkSnapshot(campaign, "");
        var loaded = SaveSystem.ImportNetworkSnapshot(snapshot, "").Campaign;
        Check(loaded.NationById(founder.Id).DecisionAvailableDay["union_ai_review"] == 14 &&
            loaded.PendingEvents.Any(e => e.NationId == partner.Id && e.Options.Any(o => o.UnionFormableId == definition.Id)),
            "AI's review deadline and unanswered proposal survive a real snapshot");
        // The vanilla campaign reloads balance from active game/mod data. These
        // one-day/early-game settings are test-only, not authored scenario rules.
        Check(loaded.Defines.UnionAiReviewDays == 90 && loaded.Defines.UnionAiEarliestDay == 180,
            "vanilla reload uses the mod-readable union balance rather than test-only overrides");
        loaded.Defines.UnionAiReviewDays = 1;
        loaded.Defines.UnionAiEarliestDay = 10;
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var loadedMap, out message), message);
        loaded.AttachProvinceTopology(loadedMap);
        var loadedFounder = loaded.NationById(founder.Id);
        var loadedPartner = loaded.NationById(partner.Id);
        var loadedInvitation = loaded.PendingEvents.Single(e => e.NationId == partner.Id &&
            e.Options.Any(o => o.UnionFormableId == definition.Id));
        loadedPartner.Stability = .1f;
        EventSystem.Resolve(loaded, loadedInvitation, 0);
        Check(!PeacefulFormation.Consented(loaded, loadedFounder, loadedPartner, definition),
            "a stale invitation cannot grant consent after the partner loses its stable mandate");
        loadedPartner.Stability = .95f;
        loaded.ConfigureSinglePlayer(loaded.NationByTag("JPN").Id);
        foreach (var key in loadedPartner.DecisionAvailableDay.Keys.Where(k => k.StartsWith("union_offer/")).ToArray())
            loadedPartner.DecisionAvailableDay.Remove(key);
        SetDay(loaded, 20);
        var competitor = loaded.NationByTag("JPN");
        var competingConsent = PeacefulFormation.ConsentKey(competitor.Id, "competing_test");
        loadedPartner.DecisionAvailableDay[competingConsent] = 100;
        PeacefulFormationAi.Tick(loaded);
        Check(!PeacefulFormation.Consented(loaded, loadedFounder, loadedPartner, definition),
            "AI respects a live accession promise to another independent founder");
        loadedPartner.DecisionAvailableDay.Remove(competingConsent);
        SetDay(loaded, 21);
        PeacefulFormationAi.Tick(loaded);
        Check(PeacefulFormation.Consented(loaded, loadedFounder, loadedPartner, definition) &&
            string.IsNullOrEmpty(loadedFounder.FormedNationId), "two AI countries sign a treaty without annexing on the invitation pass: " +
            $"interval={loaded.Defines.UnionAiReviewDays}, grace={loaded.Defines.UnionAiEarliestDay}, review={loadedFounder.DecisionAvailableDay.GetValueOrDefault("union_ai_review")}, " +
            $"refusal={PeacefulFormation.Refusal(loaded, loadedFounder, loadedPartner, definition)}, score={PeacefulFormation.Willingness(loaded, loadedFounder, loadedPartner)}, formed={loadedFounder.FormedNationId}");
        SetDay(loaded, 22);
        AiStatecraft.Tick(loaded);
        Check(loadedFounder.FormedNationId == definition.Id && loaded.Player.Tag == "JPN",
            "AI independently proclaims its union on a later review and leaves unrelated player control intact");
        EventSystem.Resolve(campaign, invitation, 1);
        SetDay(campaign, 14);
        PeacefulFormationAi.Tick(campaign);
        Check(!PeacefulFormation.Consented(campaign, founder, partner, definition) &&
            !campaign.PendingEvents.Any(e => e.NationId == partner.Id && e.Options.Any(o => o.UnionFormableId == definition.Id)),
            "a player's refusal invokes the real invitation cooldown");
        // A later explicit treaty is consent, not a request the AI may infer from friendship.
        Check(PeacefulFormation.Accept(campaign, founder, partner, definition), "the player can later sign an explicit treaty");
        var population = campaign.States.Sum(s => s.Population);
        SetDay(campaign, 15);
        PeacefulFormationAi.Tick(campaign);
        Check(founder.FormedNationId == definition.Id && campaign.Player.Id == founder.Id &&
            campaign.States.Sum(s => s.Population) == population, "AI proclaims a valid treaty foundation and preserves the consenting player's campaign");
        Check(campaign.Random.State == random, "foundation diplomacy consumes no random stream and is lockstep-safe");
    }

    static void EditorFoundations()
    {
        var before = FormableNations.Find("united_korea");
        ContentWorkbench.Validate("common/formable_nations/test.json", "{\"united_korea\":{\"ai_foundation\":false}}");
        Check(ReferenceEquals(before, FormableNations.Find("united_korea")) && before.AiFoundation,
            "editor validates a partial union overlay without changing the live catalogue");
        ContentWorkbench.Validate("common/formable_nations/test.json", "{\"united_korea\":{\"disabled\":true}}");
        Check(FormableNations.Find("united_korea") is not null, "validating a deletion cannot remove a live country");
        var rejected = false;
        try { ContentWorkbench.Validate("common/formable_nations/test.json", "{\"united_korea\":{\"ai_foundation\":\"yes\"}}"); }
        catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected, "editor rejects malformed AI switches before writing to the mod");
        rejected = false;
        try { ContentWorkbench.Validate("common/formable_nations/test.json", "{\"united_korea\":{\"minimum_stability\":1.5}}"); }
        catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected && ReferenceEquals(before, FormableNations.Find("united_korea")),
            "editor rejects invalid formation balance without corrupting live data");
    }
}
