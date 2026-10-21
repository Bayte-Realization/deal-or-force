using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class PeaceBeneficiaryTests
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL peace beneficiary: " + label);
        Console.WriteLine("PASS: peace beneficiary " + label);
    }

    static string Snapshot(object value) => JsonSerializer.Serialize(value, Json);
    static string Condition(Campaign c) => Snapshot(c.Armies.Select(a => new
    {
        a.Id, a.NationId, a.Personnel, a.MaxPersonnel, a.Recovering, a.Strength,
        a.Organisation, a.Equipment, a.Cohesion, a.Experience, a.Divisions
    }).ToArray());
    static string Authority(Campaign c) => Snapshot(new
    {
        c.Nations, c.States, c.Armies, c.Battles, c.Relations,
        Owners = c.ProvinceMap.ProvinceToNation, c.ProvinceController, c.ProvinceOccupation,
        Ended = c.Wars.Select(w => new { w.Id, w.Ended, w.WinnerId, w.Reparations }).ToArray(), c.Random.State
    });

    static Campaign Fixture(bool humanEnemy = false)
    {
        var c = PartialFrontWithdrawalTests.Fixture();
        c.ScenarioId = "peace_beneficiary_fixture";
        var war = c.Wars[0];
        war.StartedDay = c.ElapsedDays - 1000; war.WarScore = 100;
        war.DefenderWarSupport = 0; war.AttackerWarSupport = 1;
        war.AttackerAllies.Add(2);
        c.Nations.Add(new Nation { Id = 3, MapIndex = 503, Tag = "PB3", Name = "Neutral",
            Population = 1_000_000, Gdp = 25, Treasury = 19, OutputScale = 1 });
        c.Nations.Add(new Nation { Id = 4, MapIndex = 504, Tag = "PB4", Name = "Landless ally",
            Population = 1_000_000, Gdp = 25, Treasury = 23, OutputScale = 1 });
        war.AttackerAllies.Add(4);
        // Reuse the real eight-province raster, giving the ally and neutral actual owned land.
        StatePartition.Annex(c, new[] { 1 }, c.Nations[2], asConquest: false);
        StatePartition.Annex(c, new[] { 5 }, c.Nations[3], asConquest: false);
        c.Nations[0].FoundedCultures.Add("russian");
        c.Nations[1].FoundedCultures.Add("french");
        c.Nations[2].FoundedCultures.Add("english");
        foreach (var state in c.States)
        {
            state.PopulationGroups.Clear();
            state.PopulationGroups.Add(new PopulationGroup { Culture = state.MapIndex == 2007 ? "english" : "french", Size = state.Population });
        }
        c.Nations[0].Treasury = 31; c.Nations[0].WarSupport = .35f;
        c.Nations[1].Treasury = 47; c.Nations[1].WarSupport = .75f;
        c.Nations[2].Treasury = 59; c.Nations[2].WarSupport = .4f;
        c.Defines.PeaceCostPerProvince = 1;
        c.Defines.PeaceWholeRegions = true;
        c.Defines.PeaceRoundUpShare = .5f;
        c.ConfigureSinglePlayer(0);
        if (humanEnemy) c.AddHumanSeat(1);
        SovereignFinance.NormalizeAccounts(c); EconomySystem.Explain(c);
        return c;
    }

    static PeaceDemand Demand(Campaign c, int beneficiary = 2, params int[] provinces)
    {
        var d = new PeaceDemand { WarId = c.Wars[0].Id, ProposerId = 0, BeneficiaryId = beneficiary };
        foreach (var p in provinces) d.Provinces.Add(p);
        return d;
    }

    static T Wire<T>(T command) where T : class, ICampaignCommand
    {
        ICampaignCommand decoded = null;
        Check(NetworkCommandCodec.TryEncode(command, out var type, out var json) &&
            NetworkCommandCodec.TryDecode(type, json, out decoded) && decoded is T,
            command.GetType().Name + " traverses the actual network codec");
        return (T)decoded;
    }

    static Campaign Restore(Campaign c)
    {
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "peace-beneficiary"), "peace-beneficiary");
        Check(restored.Success, "actual network snapshot imports: " + restored.Warning);
        restored.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        return restored.Campaign;
    }

    static void QueriesAndRounding()
    {
        var c = Fixture(); var war = c.Wars[0]; var demand = Demand(c, 2, 7);
        var fp = CampaignFingerprint.Of(c); var random = c.Random.State; var draft = Snapshot(demand);
        Check(PeaceConference.Beneficiaries(c, war, 0).Select(n => n.Id).SequenceEqual(new[] { 0, 2 }),
            "recipient choices contain surviving allies with owned land in stable id order");
        Check(PeaceConference.Beneficiaries(c, war, 1).Select(n => n.Id).SequenceEqual(new[] { 1 }) &&
            PeaceConference.Beneficiaries(c, war, 3).Count == 0,
            "recipient choices follow the proposer's coalition and reject outsiders");
        Check(PeaceConference.Beneficiary(c, demand) == c.Nations[2], "explicit recipient lookup returns the actual ally");
        _ = PeaceConference.Cost(c, war, demand); _ = PeaceConference.BlockReason(c, war, demand);
        _ = PeaceConference.Round(c, war, demand); _ = PeaceConference.Claimable(c, war, 0);
        Check(CampaignFingerprint.Of(c) == fp && c.Random.State == random && Snapshot(demand) == draft,
            "recipient lists, pricing, refusal, rounding and map queries preserve fingerprint, RNG and draft");

        // Make the two real provinces one whole region, so rounding must copy the recipient too.
        c.ProvinceMap.ProvinceToState[8] = c.ProvinceMap.ProvinceToState[7];
        c.States.Single(s => s.MapIndex == 2007).Population += c.States.Single(s => s.MapIndex == 2008).Population;
        c.States.RemoveAll(s => s.MapIndex == 2008); c.InvalidateStateIndex(); c.MarkMapChanged();
        var rounded = PeaceConference.Round(c, war, demand);
        Check(rounded.BeneficiaryId == 2 && rounded.ProposerId == 0 && rounded.Provinces.SetEquals(new[] { 7, 8 }) &&
            demand.Provinces.SetEquals(new[] { 7 }), "whole-region expansion preserves recipient and leaves the brush draft intact");
        var command = Wire(WrittenPeaceCommand.From(rounded));
        Check(command.BeneficiaryId == 2 && command.IssuerId == 0 && command.Provinces.SequenceEqual(new[] { 7, 8 }),
            "From and wire serialization preserve recipient separately from budget author");
        command.Apply(c);
        Check(command.Delivered && war.Ended && war.WinnerId == 0 &&
            new[] { 7, 8 }.All(p => c.ProvinceMap.ProvinceToNation[p] == c.Nations[2].MapIndex),
            "the actual command awards the entire rounded region to its named ally");
    }

    static void WrittenPackage()
    {
        var c = Fixture(); var war = c.Wars[0]; var d = Demand(c, 2, 7);
        d.Reparations = d.TradeConcessions = d.Humiliate = true;
        var proposerCash = c.Nations[0].Treasury; var allyCash = c.Nations[2].Treasury;
        var loserCash = c.Nations[1].Treasury; var totalCash = c.Nations.Sum(n => n.Treasury);
        var proposerSupport = c.Nations[0].WarSupport; var allySupport = c.Nations[2].WarSupport;
        var condition = Condition(c); var random = c.Random.State;
        var command = Wire(WrittenPeaceCommand.From(d));
        c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
        Check(command.Delivered && war.Ended && war.WinnerId == 0 && c.ProvinceMap.ProvinceToNation[7] == 502,
            "signed package gives land to the ally while retaining the proposing war winner");
        Check(c.Nations[0].Treasury == proposerCash && c.Nations[2].Treasury > allyCash &&
            c.Nations[1].Treasury < loserCash && Math.Abs(c.Nations[2].Treasury - allyCash - war.Reparations) < .0001f &&
            Math.Abs(c.Nations.Sum(n => n.Treasury) - totalCash) < .0001f,
            "one indemnity moves existing money from enemy to beneficiary and conserves cash");
        Check(CampaignDiplomacy.Peek(c, 2, 1).TradeAgreement && !CampaignDiplomacy.Peek(c, 0, 1).TradeAgreement &&
            c.Nations[0].WarSupport == proposerSupport && c.Nations[2].WarSupport > allySupport,
            "the same beneficiary receives compelled trade and the positive humiliation bonus");
        Check(Condition(c) == condition && c.Random.State == random && c.Battles[0].Ended && c.Armies.All(a => !a.InBattle),
            "peace closes the battle without healing formations or drawing random numbers");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "peace_signed" && e.ActorId == 0 && e.TargetId == 1),
            "peace chronicle keeps the proposer as diplomatic author");
    }

    static void InvalidRecipients()
    {
        foreach (var recipient in new[] { -2, 99999, 1, 3, 4 })
        {
            var c = Fixture(); var war = c.Wars[0]; var d = Demand(c, recipient, 7);
            d.Reparations = d.TradeConcessions = true;
            var before = Authority(c); var fp = CampaignFingerprint.Of(c);
            var command = Wire(WrittenPeaceCommand.From(d)); command.Apply(c);
            Check(!command.Delivered && command.Note.Length > 0 && Authority(c) == before && CampaignFingerprint.Of(c) == fp,
                "missing, enemy, neutral or landless recipient refuses before effects: " + recipient);
            Check(!PeaceConference.Resolve(c, war, d) && Authority(c) == before,
                "direct authoritative resolution also rejects invalid recipient: " + recipient);
        }
        {
            var c = Fixture(); var d = Demand(c, 2, 7); d.ReleaseRegions = true;
            var before = Authority(c); var command = WrittenPeaceCommand.From(d); command.Apply(c);
            Check(!command.Delivered && Authority(c) == before,
                "independence cannot be redirected into somebody else's territorial award");
        }
        {
            var c = Fixture(); c.ConfigureSinglePlayer(2); var d = Demand(c, 2, 7);
            var before = Authority(c); var command = WrittenPeaceCommand.From(d); command.Apply(c);
            Check(!command.Delivered && !c.Wars[0].Ended && Authority(c) == before,
                "an NPC cannot settle the war over its human coalition ally's head");
        }
        {
            var c = Fixture(); c.Defines.PeaceAlliedBenefits = false;
            var before = Authority(c); var command = WrittenPeaceCommand.From(Demand(c, 2, 7)); command.Apply(c);
            Check(PeaceConference.Beneficiaries(c, c.Wars[0], 0).Select(n => n.Id).SequenceEqual(new[] { 0 }) &&
                !command.Delivered && Authority(c) == before,
                "campaign switch removes allied choices and rejects stale allied commands");
        }
    }

    static void PendingOfferAndPersistence()
    {
        var client = Fixture(humanEnemy: true); var host = Restore(client);
        var draft = Demand(client, 2, 7); draft.Reparations = draft.TradeConcessions = true;
        string sentType = null, sentJson = null;
        client.Commands.SubmitInterceptor = command => NetworkCommandCodec.TryEncode(command, out sentType, out sentJson);
        var before = CampaignFingerprint.Of(client); var beforeAuthority = Authority(client);
        client.Commands.Enqueue(WrittenPeaceCommand.From(draft)); client.Commands.ApplyAll(client);
        Check(sentType == nameof(WrittenPeaceCommand) && client.Commands.Count == 0 &&
            CampaignFingerprint.Of(client) == before && Authority(client) == beforeAuthority,
            "client command submission uses the transport seam without applying peace locally");
        Check(NetworkCommandCodec.TryDecode(sentType, sentJson, out var delivered), "host decodes the submitted peace package");
        host.Commands.Enqueue(delivered); host.Commands.ApplyAll(host);
        var war = host.Wars[0];
        Check(!war.Ended && war.PendingOffer is { BeneficiaryId: 2, Reparations: true, TradeConcessions: true } &&
            war.PendingOfferFromId == 0 && host.ProvinceMap.ProvinceToNation[7] == 501,
            "human opponent receives the saved recipient and package before any territory moves");
        var pendingFp = CampaignFingerprint.Of(host);
        war.PendingOffer.BeneficiaryId = 0;
        Check(CampaignFingerprint.Of(host) != pendingFp, "changing only explicit pending beneficiary changes authority fingerprint");
        war.PendingOffer.BeneficiaryId = -1; var legacyFp = CampaignFingerprint.Of(host);
        war.PendingOffer.BeneficiaryId = 0;
        Check(CampaignFingerprint.Of(host) != legacyFp, "explicit nation zero is distinct from the omitted legacy recipient");
        war.PendingOffer.BeneficiaryId = 2;

        var restored = Restore(host);
        Check(restored.Wars[0].PendingOffer is { BeneficiaryId: 2 } && CampaignFingerprint.Of(restored) == pendingFp,
            "pending recipient and fingerprint survive the actual network snapshot");
        var folder = Path.Combine(Path.GetTempPath(), "dof-peace-beneficiary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            host.Defines.SaveTextFormat = true;
            SaveSystem.Save(folder, "pending", host, "peace-beneficiary");
            var saved = SaveSystem.Load(folder, "pending", "peace-beneficiary");
            Check(saved.Success, "actual disk save of pending settlement loads: " + saved.Warning);
            saved.Campaign.AttachProvinceTopology(host.ProvinceMap.CloneForOverrides());
            Check(saved.Campaign.Wars[0].PendingOffer is { BeneficiaryId: 2 } &&
                CampaignFingerprint.Of(saved.Campaign) == CampaignFingerprint.Of(host),
                "pending recipient and authoritative state survive disk round trip");
        }
        finally { Directory.Delete(folder, recursive: true); }

        var preview = PeaceOffers.Describe(restored, restored.Wars[0]);
        Check(preview.Contains(restored.Nations[2].DisplayName), "offer description identifies the actual beneficiary");
        var wrongBefore = Authority(restored);
        Wire(new AnswerPeaceOfferCommand { IssuerId = 2, WarId = war.Id, Accept = true }).Apply(restored);
        Check(Authority(restored) == wrongBefore && restored.Wars[0].PendingOffer is { BeneficiaryId: 2 },
            "the proposing coalition's beneficiary cannot answer its own offer");

        string answerType = null, answerJson = null;
        restored.Commands.SubmitInterceptor = command => NetworkCommandCodec.TryEncode(command, out answerType, out answerJson);
        restored.Commands.Enqueue(new AnswerPeaceOfferCommand { IssuerId = 1, WarId = war.Id, Accept = true });
        var decodedAnswer = NetworkCommandCodec.TryDecode(answerType, answerJson, out var answer);
        Check(!restored.Wars[0].Ended && decodedAnswer,
            "human acceptance follows the same client transport seam");
        host.Commands.Enqueue(answer); host.Commands.ApplyAll(host);
        Check(war.Ended && war.WinnerId == 0 && war.PendingOffer is null &&
            host.ProvinceMap.ProvinceToNation[7] == 502 && CampaignDiplomacy.Peek(host, 2, 1).TradeAgreement,
            "accepting the real pending command resolves the preserved beneficiary package on the host");
    }

    static void LegacyAndPricing()
    {
        {
            var c = Fixture(); var war = c.Wars[0];
            Check(NetworkCommandCodec.TryDecode(nameof(WrittenPeaceCommand),
                "{\"IssuerId\":0,\"WarId\":" + war.Id + ",\"Provinces\":[7],\"Reparations\":true}", out var legacy),
                "old wire demand lacking the beneficiary field still decodes");
            Check(((WrittenPeaceCommand)legacy).BeneficiaryId == -1 &&
                PeaceConference.Beneficiary(c, new PeaceDemand { ProposerId = 0 }) == c.Nations[0],
                "omitted beneficiary defaults to the proposer, including nation zero");
            var allyCash = c.Nations[2].Treasury; var proposerCash = c.Nations[0].Treasury;
            legacy.Apply(c);
            Check(war.Ended && c.ProvinceMap.ProvinceToNation[7] == 500 && c.Nations[0].Treasury > proposerCash &&
                c.Nations[2].Treasury == allyCash, "old demands retain legacy self-award behavior");
        }
        {
            var c = Fixture(); var war = c.Wars[0]; var self = Demand(c, 0, 7); var ally = Demand(c, 2, 7);
            var region = c.States.Single(s => s.MapIndex == 2007);
            Check(Reintegration.IsKindredRegion(c, c.Nations[2], region) && !Reintegration.IsKindredRegion(c, c.Nations[0], region),
                "pricing fixture has actual kindred inhabitants for ally and a different proposing culture");
            Check(PeaceConference.Cost(c, war, ally) < PeaceConference.Cost(c, war, self),
                "territory price applies the actual recipient's kindred discount");
            c.SetProvinceController(7, 500); c.ProvinceOccupation[7] = 1;
            var heldSelf = PeaceConference.Cost(c, war, self); var heldAlly = PeaceConference.Cost(c, war, ally);
            Check(Math.Abs(heldAlly / heldSelf - Math.Clamp(c.Defines.PeaceCostReunificationFactor, .1f, 1f)) < .0001f,
                "recipient identity changes culture pricing while occupation credit stays with the proposing side");
            c.Nations[2].Population = 100_000_000;
            var subject = Demand(c); subject.Subjugate = "puppet";
            Check(SubjectCatalog.Knows(subject.Subjugate) && Math.Abs(PeaceConference.Cost(c, war, subject) -
                PeaceConference.SubjugationCost(c, c.Nations[2], c.Nations[1], subject.Subjugate)) < .0001f,
                "subjugation prices the defeated country against its actual future overlord");
            var command = WrittenPeaceCommand.From(subject); command.Apply(c);
            Check(command.Delivered && war.Ended && war.WinnerId == 0 && c.Nations[1].AnswersTo(2) && !c.Nations[1].AnswersTo(0),
                "subject relationship goes to recipient while proposer retains war authorship");
        }
        {
            var c = Fixture(); var war = c.Wars[0];
            war.WarScore = 0; war.StartedDay = c.ElapsedDays; war.DefenderWarSupport = 1;
            var demand = Demand(c, 2, 7); demand.Unconditional = true;
            Check(PeaceConference.Budget(c, war, 0) == 0 && PeaceConference.Cost(c, war, demand) > 0,
                "zero-budget fixture has no author score to spend");
            var before = Authority(c); var command = Wire(WrittenPeaceCommand.From(demand)); command.Apply(c);
            Check(!command.Delivered && Authority(c) == before && !war.Ended,
                "a client cannot smuggle unconditional authority through a beneficiary command");
        }
        {
            var c = Fixture(); var war = c.Wars[0]; var release = Demand(c, 0, 7); release.ReleaseRegions = true;
            var count = c.Nations.Count; var command = WrittenPeaceCommand.From(release); command.Apply(c);
            var born = c.Nations.Last();
            Check(command.Delivered && war.Ended && c.Nations.Count == count + 1 && war.AnnexedProvinces == 0 &&
                born.Id != 0 && born.Id != 2 && !born.IsSubject && c.ProvinceMap.ProvinceToNation[7] == born.MapIndex,
                "release with the default recipient remains a real independent country");
        }
    }

    public static void Run()
    {
        QueriesAndRounding(); WrittenPackage(); InvalidRecipients();
        PendingOfferAndPersistence(); LegacyAndPricing();
    }
}
