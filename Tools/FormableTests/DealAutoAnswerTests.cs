using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

/// <summary>
/// Standing answers to incoming offers (DealAnswer, docs/DEALS_REWORK_2026_11_14_RU.md step 1):
/// "ask" changes nothing, "beneficial" signs only what the game's own count puts at least
/// AutoAcceptMargin in the player's favour, "decline" declines, land is never given by a rule,
/// and a government's answer is exactly the one it always gave.
/// </summary>
static class DealAutoAnswerTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("deal auto-answer: " + label);
        Console.WriteLine("PASS deal auto-answer: " + label);
    }

    /// <summary><paramref name="count"/> countries side by side, two regions each; the last one is the player.</summary>
    private static Campaign World(int count = 2)
    {
        var c = new Campaign(26) { ScenarioId = "auto_answer_fixture" };
        for (var i = 0; i < count; i++)
        {
            var nation = new Nation
            {
                Id = i, MapIndex = 601 + i, Tag = "DA" + (char)('A' + i), Name = "Country " + i, Treasury = 100,
                Population = 2000000, OutputScale = 1, CapitalStateMapIndex = 4001 + 2 * i
            };
            c.Nations.Add(nation);
            for (var r = 0; r < 2; r++)
                c.States.Add(new ScenarioState { MapIndex = 4001 + 2 * i + r, NationMapIndex = nation.MapIndex,
                    Id = $"auto_answer_{i}_{r}", NationId = nation.Tag, Name = $"Region {i}.{r}", Population = 1000000 });
        }
        var width = 6 * count;
        var states = Enumerable.Range(0, width * 2).Select(p => (ushort)(4001 + (p % width) / 3)).ToArray();
        var owners = Enumerable.Range(0, width * 2).Select(p => (ushort)(601 + (p % width) / 6)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, width, 2, _ => false));
        foreach (var nation in c.Nations)
        {
            nation.CapitalStateMapIndex = 4001 + 2 * nation.Id;
            nation.CapitalProvinceId = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
                .First(p => c.ProvinceMap.ProvinceToState[p] == nation.CapitalStateMapIndex);
            var centre = c.ProvinceMap.CenterOf(nation.CapitalProvinceId);
            nation.CapitalLatitude = centre.Latitude; nation.CapitalLongitude = centre.Longitude;
        }
        SovereignFinance.NormalizeAccounts(c);
        c.ConfigureSinglePlayer(count - 1);
        return c;
    }

    private static Nation N(Campaign c, int id) => c.NationById(id);

    private static ProposeDealCommand Propose(Campaign c, int from, int to, float fromCash = 0, float toCash = 0,
        int[] fromRegions = null, int[] toRegions = null, bool fromPact = false, bool toPact = false,
        bool fromTrade = false, bool toTrade = false)
    {
        var command = new ProposeDealCommand
        {
            IssuerId = from, ToId = to, FromCash = fromCash, ToCash = toCash,
            FromRegions = fromRegions ?? Array.Empty<int>(), ToRegions = toRegions ?? Array.Empty<int>(),
            FromNonAggression = fromPact, ToNonAggression = toPact, FromTradeAgreement = fromTrade, ToTradeAgreement = toTrade
        };
        c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
        return command;
    }

    private static int Delivered(ProposeDealCommand command) => command.Outcome.PendingOfferId > 0
        ? command.Outcome.PendingOfferId : throw new Exception("expected a delivered paper: " + command.Outcome.Note);

    private static SetAutoAnswerCommand Rule(Campaign c, int nation, string kind, AutoAnswerMode mode)
    {
        var command = new SetAutoAnswerCommand { IssuerId = nation, Kind = kind, Mode = mode };
        c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
        return command;
    }

    private static void Day(Campaign c, int day) =>
        c.RestoreRuntimeState(c.Date, day, true, c.Random.State, c.Player.Id,
            c.HumanSeats.ToArray(), c.ActiveSeat, c.NextBattleId, c.MapRevision,
            c.InitialDeploymentCompleted, c.ProvinceController, c.ProvinceOccupation,
            c.ProvinceMap.ProvinceToNation, c.ProvinceMap.ProvinceToState);

    public static void Run()
    {
        DataAndClassification();
        AskIsTheOldBehaviour();
        BeneficialSignsOnlyAboveTheMargin();
        DeclineCloses();
        LandIsNeverGivenByRule();
        WaitingPapersAreAnsweredInBatches();
        AcceptanceRevalidates();
        CommandThroughCodecAndFingerprint();
        OldSnapshotsAnswerAsk();
        HotseatRulesAreEachPlayersOwn();
        GovernmentsAnswerAsBefore();
        AgreementOffersFollowTheRules();
        AgreementRefusalAndLapsePauses();
        ARuleAnswerIsAnAnswerForTheAsker();
        AFailedSignatureIsNotADecline();
        InboxMessagesAndArmedConfirmation();
    }

    private static void DataAndClassification()
    {
        var ids = DealAnswerKinds.All.Select(k => k.Id).ToArray();
        Check(new[] { "land", "treaty_for_money", "non_aggression", "trade_agreement", "gift" }.All(ids.Contains),
            "the shipped answer kinds load from common/deals");
        Check(!DealAnswerKinds.Find("land").Allows(AutoAnswerMode.Beneficial) && DealAnswerKinds.Find("gift").Allows(AutoAnswerMode.Beneficial),
            "land allows only ask and decline; a gift allows all three");
        Check(DealAnswerKinds.All.All(k => k.Default == AutoAnswerMode.Ask), "every shipped kind defaults to ask");
        Ultimatum.Deal D(Ultimatum.Side from, Ultimatum.Side to) => new(from, to);
        Check(DealAnswerKinds.Classify(D(new() { Cash = 3 }, new() { Regions = new[] { 4001 }, NonAggression = true }))?.Id == "land",
            "a mixed deal that takes a region is land, whatever else is on it");
        Check(DealAnswerKinds.Classify(D(new() { NonAggression = true }, new() { Regions = new[] { 4001 }, ReleaseRegions = true }))?.Id == "land",
            "releasing a region is land too");
        Check(DealAnswerKinds.Classify(D(new() { Cash = 3 }, new() { NonAggression = true }))?.Id == "treaty_for_money",
            "money for our pact is a treaty for money");
        Check(DealAnswerKinds.Classify(D(new() { Cash = 3 }, new() { LiftEmbargo = true }))?.Id == "treaty_for_money",
            "money for lifting our embargo is a treaty for money");
        Check(DealAnswerKinds.Classify(D(new() { NonAggression = true }, new()))?.Id == "non_aggression" &&
              DealAnswerKinds.Classify(D(new(), new() { NonAggression = true }))?.Id == "non_aggression",
            "a bare pact is a pact, whichever column it was put in");
        Check(DealAnswerKinds.Classify(D(new() { TradeAgreement = true }, new()))?.Id == "trade_agreement",
            "a bare trade agreement is a trade agreement");
        Check(DealAnswerKinds.Classify(D(new() { Cash = 3 }, new()))?.Id == "gift" &&
              DealAnswerKinds.Classify(D(new() { Cash = 3, TradeAgreement = true }, new()))?.Id == "gift",
            "money or promises asked for nothing are a gift");
        Check(DealAnswerKinds.Classify(D(new() { NonAggression = true }, new() { Cash = 3 })) is null,
            "paying for their pact matches no kind and so always waits for the player");

        // A mod cannot open the door: "beneficial" on a land-shaped kind is struck at load.
        var errors = new List<string>();
        using (var mod = JsonDocument.Parse("""
            { "answer_kinds": [ { "id": "test_land_sale", "priority": 5,
              "match": { "recipient_gives_any": ["regions"], "proposer_gives_any": ["cash"] },
              "modes": ["ask", "beneficial", "decline"], "default": "beneficial" } ] }
            """))
            DealAnswerKinds.Merge(mod.RootElement, errors);
        var sale = DealAnswerKinds.Find("test_land_sale");
        Check(sale is not null && !sale.Allows(AutoAnswerMode.Beneficial) && sale.Default == AutoAnswerMode.Ask && errors.Count >= 1,
            "a data kind that could give land away loses 'beneficial' and its default falls back to ask");
        DealAnswerKinds.ResetCatalog();
        Check(DealAnswerKinds.Find("test_land_sale") is null, "reloading drops the test kind");
    }

    private static void AskIsTheOldBehaviour()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        Check(DealAnswer.ModeFor(player, DealAnswerKinds.Find("gift")) == AutoAnswerMode.Ask && player.AutoAnswer.Count == 0,
            "a player who set nothing is asked about everything");
        var offer = Propose(c, ai.Id, player.Id, fromCash: 20);
        Check(offer.Outcome.PendingOfferId > 0 && c.DealOffers.Single().Pending && !c.DealOffers.Single().ByRule &&
              ai.Treasury == 100 && player.Treasury == 100 && DealNegotiations.Incoming(c, player.Id) == 1,
            "with 'ask' a generous paper waits in the inbox exactly as before");
        Day(c, 1); DealNegotiations.Tick(c);
        Check(c.DealOffers.Single().Pending && player.AutoAnswerLog.Count == 0, "the daily sweep leaves an 'ask' paper alone");
    }

    private static void BeneficialSignsOnlyAboveTheMargin()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        Check(Rule(c, player.Id, "gift", AutoAnswerMode.Beneficial).Applied, "a player can set gifts to 'beneficial'");
        var generous = Propose(c, ai.Id, player.Id, fromCash: 10);
        var paper = c.DealOffers.Single();
        Check(generous.Outcome.Accepted && paper.State == DealOfferState.Accepted && paper.ByRule &&
              ai.Treasury == 90 && player.Treasury == 110 && DealNegotiations.Incoming(c, player.Id) == 0,
            "a gift well above the margin is signed at delivery, and never sits in the inbox");
        var note = player.AutoAnswerLog.Single();
        Check(note.Accepted && note.Kind == "gift" && note.OfferId == paper.Id && note.ProposerId == ai.Id &&
              note.Mode == AutoAnswerMode.Beneficial && note.Benefit >= c.Defines.AutoAcceptMargin,
            "the answer is written to the player's 'decided by rule' list with the benefit it was signed at");

        // 2 billion to a treasury of 110 is worth about 3.2 points: below the margin of 5.
        Day(c, 1);
        var small = Propose(c, ai.Id, player.Id, fromCash: 2);
        var waiting = c.DealOffers.Single(o => o.Id == small.Outcome.PendingOfferId);
        var decision = DealAnswer.Decide(c, player, waiting);
        Check(small.Outcome.PendingOfferId > 0 && waiting.Pending && !waiting.ByRule &&
              decision.Verdict == DealVerdict.Wait && decision.Benefit > 0 && decision.Benefit < decision.Margin,
            "a gift that is positive but under the margin stays in the inbox, with its shortfall readable");
        Day(c, 2); DealNegotiations.Tick(c);
        Check(waiting.Pending && player.AutoAnswerLog.Count == 1, "the daily sweep does not sign it either");
        c.Defines.AutoAcceptMargin = 1f;
        Day(c, 3); DealNegotiations.Tick(c);
        Check(waiting.State == DealOfferState.Accepted && waiting.ByRule && player.AutoAnswerLog.Count == 2,
            "the margin is data: lowered to 1, the next sweep signs the same paper");
    }

    private static void DeclineCloses()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        Rule(c, player.Id, "non_aggression", AutoAnswerMode.Decline);
        var pact = Propose(c, ai.Id, player.Id, fromPact: true);
        var paper = c.DealOffers.Single();
        Check(!pact.Outcome.Accepted && pact.Outcome.PendingOfferId == 0 && paper.State == DealOfferState.Declined && paper.ByRule &&
              paper.Reason.Contains("standing rule") && !CampaignDiplomacy.Get(c, ai.Id, player.Id).NonAggressionPact,
            "'decline' closes the paper at once with the standing-rule reason, and nothing is signed");
        Check(player.AutoAnswerLog.Single() is { Accepted: false, Kind: "non_aggression", Mode: AutoAnswerMode.Decline },
            "the refusal is in the player's list");
        var gift = Propose(c, ai.Id, player.Id, fromCash: 5, fromTrade: true);
        Check(gift.Outcome.PendingOfferId > 0 && c.DealOffers.Single(o => o.Id == gift.Outcome.PendingOfferId).Pending,
            "a rule for one kind leaves every other kind with the player");
    }

    private static void LandIsNeverGivenByRule()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        var before = CampaignFingerprint.Of(c);
        var refused = Rule(c, player.Id, "land", AutoAnswerMode.Beneficial);
        Check(!refused.Applied && refused.Reason.Length > 0 && player.AutoAnswer.Count == 0 && CampaignFingerprint.Of(c) == before,
            "'beneficial' for land is refused by the command and changes nothing");
        Check(Rule(c, player.Id, "land", AutoAnswerMode.Decline).Applied, "land may be declined by rule");
        Rule(c, player.Id, "land", AutoAnswerMode.Ask);
        Check(player.AutoAnswer.Count == 0, "setting a kind back to its default leaves no entry behind");

        // Even a rule that somehow says "beneficial" for every kind - an edited save, a mod - signs no land.
        foreach (var kind in DealAnswerKinds.All) player.AutoAnswer[kind.Id] = (int)AutoAnswerMode.Beneficial;
        var richSale = Propose(c, ai.Id, player.Id, fromCash: 90, toRegions: new[] { 4004 });
        var paper = c.DealOffers.Single();
        Check(richSale.Outcome.PendingOfferId > 0 && paper.Pending && !paper.ByRule &&
              c.StateByMapIndex(4004).NationMapIndex == player.MapIndex && player.Treasury == 100,
            "a land sale worth a fortune by the count still waits for the player");
        Check(DealAnswer.Decide(c, player, paper).Verdict == DealVerdict.Wait, "Decide checks land on the terms, not only on the data");
    }

    private static void WaitingPapersAreAnsweredInBatches()
    {
        var c = World(6); var player = N(c, 5);
        for (var i = 0; i < 5; i++) Propose(c, i, player.Id, fromCash: 10);
        Check(DealNegotiations.Incoming(c, player.Id) == 5, "five gifts wait while the player answers by hand");
        c.Defines.AutoAnswerDailyBatch = 2;
        Rule(c, player.Id, "gift", AutoAnswerMode.Beneficial);
        Check(DealNegotiations.Incoming(c, player.Id) == 5, "setting a rule answers nothing by itself; the day does");
        Day(c, 1); DealNegotiations.Tick(c);
        var signed = c.DealOffers.Where(o => o.State == DealOfferState.Accepted).Select(o => o.Id).OrderBy(id => id).ToArray();
        Check(signed.SequenceEqual(new[] { 1, 2 }) && DealNegotiations.Incoming(c, player.Id) == 3,
            "the next day the rule signs no more than the batch, oldest first");
        Day(c, 2); DealNegotiations.Tick(c);
        Day(c, 3); DealNegotiations.Tick(c);
        Check(DealNegotiations.Incoming(c, player.Id) == 0 && player.Treasury == 150 && player.AutoAnswerLog.Count == 5,
            "the rest follow on the next days");
    }

    private static void AcceptanceRevalidates()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        var paperId = Propose(c, ai.Id, player.Id, fromCash: 50).Outcome.PendingOfferId;
        ai.Treasury = 10;
        Rule(c, player.Id, "gift", AutoAnswerMode.Beneficial);
        Day(c, 1); DealNegotiations.Tick(c);
        var paper = c.DealOffers.Single(o => o.Id == paperId);
        Check(paper.Pending && !paper.ByRule && ai.Treasury == 10 && player.Treasury == 100 &&
              DealAnswer.Decide(c, player, paper) is { Verdict: DealVerdict.Wait, Assessment.Valid: false },
            "money spent since the offer: the rule's count finds the deal invalid and signs nothing, no partial payment");

        c = World(); ai = N(c, 0); player = N(c, 1);
        paperId = Delivered(Propose(c, ai.Id, player.Id, fromRegions: new[] { 4002 }));
        paper = c.DealOffers.Single(o => o.Id == paperId);
        c.SetProvinceController(paper.FromProvinces.First(), (ushort)player.MapIndex);
        Rule(c, player.Id, "gift", AutoAnswerMode.Beneficial);
        Day(c, 1); DealNegotiations.Tick(c);
        Check(paper.Pending && c.StateByMapIndex(4002).NationMapIndex == ai.MapIndex && player.AutoAnswerLog.Count == 0,
            "an offered region occupied since the offer is not signed for by the rule");

        // The count can pass while the ground changed shape: the signature's own check stops it.
        c = World(); ai = N(c, 0); player = N(c, 1);
        paperId = Delivered(Propose(c, ai.Id, player.Id, fromCash: 10, fromRegions: new[] { 4002 }));
        paper = c.DealOffers.Single(o => o.Id == paperId);
        c.ProvinceMap.ProvinceToState[paper.FromProvinces.First()] = 4001;
        Rule(c, player.Id, "gift", AutoAnswerMode.Beneficial);
        Day(c, 1); DealNegotiations.Tick(c);
        Check(paper.State == DealOfferState.Invalidated && paper.ByRule && ai.Treasury == 100 && player.Treasury == 100 &&
              c.StateByMapIndex(4002).NationMapIndex == ai.MapIndex && player.AutoAnswerLog.Single().Accepted == false,
            "a region that changed shape since the offer invalidates it under the rule, as it would by hand" +
            (paper.State == DealOfferState.Invalidated ? "" : $" [{paper.State} {DealAnswer.Decide(c, player, paper).Verdict} {DealAnswer.Decide(c, player, paper).Assessment?.Reason} {paper.Reason}]"));

        c = World(); ai = N(c, 0); player = N(c, 1);
        paperId = Propose(c, ai.Id, player.Id, fromCash: 20).Outcome.PendingOfferId;
        c.SetFeature("deals", false);
        Rule(c, player.Id, "gift", AutoAnswerMode.Beneficial);
        Day(c, 1); DealNegotiations.Tick(c);
        Check(c.DealOffers.Single().State == DealOfferState.Invalidated && player.Treasury == 100 && player.AutoAnswerLog.Count == 0,
            "with deals switched off the rule signs nothing (the paper is closed as before)");
    }

    private static void CommandThroughCodecAndFingerprint()
    {
        var c = World(); var player = N(c, 1);
        var before = CampaignFingerprint.Of(c);
        var command = new SetAutoAnswerCommand { IssuerId = player.Id, Kind = "trade_agreement", Mode = AutoAnswerMode.Decline };
        ICampaignCommand decoded = null;
        Check(NetworkCommandCodec.TryEncode(command, out var type, out var json) &&
              NetworkCommandCodec.TryDecode(type, json, out decoded) &&
              decoded is SetAutoAnswerCommand back && back.IssuerId == player.Id && back.Kind == "trade_agreement" &&
              back.Mode == AutoAnswerMode.Decline,
            "the rule travels through the whitelisted network codec");
        var peerResult = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "auto_answer"), "auto_answer");
        if (!peerResult.Success) throw new Exception(peerResult.Warning);
        var peer = peerResult.Campaign; peer.AttachProvinceTopology(World().ProvinceMap);
        command.Apply(c); decoded.Apply(peer);
        Check(CampaignFingerprint.Of(c) != before, "changing a rule changes the fingerprint");
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(peer) &&
              peer.NationById(player.Id).AutoAnswer.GetValueOrDefault("trade_agreement") == (int)AutoAnswerMode.Decline,
            "host and peer apply the encoded rule identically");
        var unknown = new SetAutoAnswerCommand { IssuerId = player.Id, Kind = "no_such_kind", Mode = AutoAnswerMode.Decline };
        var undefined = new SetAutoAnswerCommand { IssuerId = player.Id, Kind = "gift", Mode = (AutoAnswerMode)7 };
        var hash = CampaignFingerprint.Of(c); unknown.Apply(c); undefined.Apply(c);
        Check(!unknown.Applied && !undefined.Applied && hash == CampaignFingerprint.Of(c), "an unknown kind or mode is an atomic no-op");
        Rule(c, player.Id, "trade_agreement", AutoAnswerMode.Ask);
        Check(CampaignFingerprint.Of(c) == before, "taking the rule back returns the world to the fingerprint it had");
    }

    private static void OldSnapshotsAnswerAsk()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        Rule(c, player.Id, "gift", AutoAnswerMode.Beneficial);
        Propose(c, ai.Id, player.Id, fromCash: 10);
        var bytes = SaveSystem.ExportNetworkSnapshot(c, "auto_answer");
        var same = SaveSystem.ImportNetworkSnapshot(bytes, "auto_answer");
        if (!same.Success) throw new Exception(same.Warning);
        same.Campaign.AttachProvinceTopology(World().ProvinceMap);
        var loaded = same.Campaign.NationById(player.Id);
        Check(loaded.AutoAnswer.GetValueOrDefault("gift") == (int)AutoAnswerMode.Beneficial && loaded.AutoAnswerLog.Count == 1 &&
              same.Campaign.DealOffers.Single().ByRule && CampaignFingerprint.Of(same.Campaign) == CampaignFingerprint.Of(c),
            "a snapshot keeps the rules, the list and the paper's 'by rule' mark, with the same fingerprint");

        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var old = JsonNode.Parse(gzip).AsObject();
        var stripped = 0;
        foreach (var nation in old["Nations"]!.AsArray())
        {
            var o = nation!.AsObject();
            foreach (var key in o.Select(p => p.Key).Where(k => k is "AutoAnswer" or "AutoAnswerLog").ToArray()) { o.Remove(key); stripped++; }
        }
        foreach (var paper in old["DealOffers"]!.AsArray()) paper!.AsObject().Remove("ByRule");
        using var output = new MemoryStream();
        using (var compress = new GZipStream(output, CompressionLevel.Fastest, true)) compress.Write(Encoding.UTF8.GetBytes(old.ToJsonString()));
        var imported = SaveSystem.ImportNetworkSnapshot(output.ToArray(), "auto_answer");
        Check(stripped >= 2 && imported.Success && imported.Campaign.NationById(player.Id).AutoAnswer.Count == 0 &&
              DealAnswer.ModeFor(imported.Campaign.NationById(player.Id), DealAnswerKinds.Find("gift")) == AutoAnswerMode.Ask &&
              !imported.Campaign.DealOffers.Single().ByRule,
            "a snapshot written before the rules existed loads with every kind on 'ask'");
    }

    private static void HotseatRulesAreEachPlayersOwn()
    {
        var c = World(3); var ai = N(c, 0); var first = N(c, 1); var second = N(c, 2);
        c.AddHumanSeat(first.Id);
        Rule(c, first.Id, "gift", AutoAnswerMode.Decline);
        Rule(c, second.Id, "gift", AutoAnswerMode.Beneficial);
        var toFirst = Propose(c, ai.Id, first.Id, fromCash: 10);
        Check(c.DealOffers.Single(o => o.FromId == ai.Id && o.ToId == first.Id).State == DealOfferState.Declined && first.Treasury == 100,
            "the first player's rule declines the gift addressed to the first player");
        Day(c, 1);
        Propose(c, ai.Id, second.Id, fromCash: 10);
        Check(c.DealOffers.Single(o => o.FromId == ai.Id && o.ToId == second.Id).State == DealOfferState.Accepted && second.Treasury == 110,
            "the second player's rule signs the gift addressed to the second player");
        Day(c, 2);
        var between = Propose(c, first.Id, second.Id, fromCash: 10);
        Check(between.Outcome.Accepted && first.Treasury == 90 && second.Treasury == 120 &&
              first.AutoAnswerLog.Count == 1 && second.AutoAnswerLog.Count == 2,
            "between two players only the addressee's rule answers");
        Check(!toFirst.Outcome.Accepted, "the proposer is told the gift was declined");
    }

    private static void GovernmentsAnswerAsBefore()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        // Even if a government's field were filled in, it does not read it.
        foreach (var kind in DealAnswerKinds.All) ai.AutoAnswer[kind.Id] = (int)AutoAnswerMode.Decline;
        var tables = new List<Ultimatum.Deal>
        {
            new(new() { Cash = 5 }, new()), new(new(), new() { Cash = 5 }), new(new() { NonAggression = true }, new()),
            new(new() { TradeAgreement = true }, new() { Cash = 1 }), new(new() { Cash = 40 }, new() { Regions = new[] { 4002 } }),
            new(new() { Regions = new[] { 4004 } }, new() { Cash = 2 }), new(new(), new() { NonAggression = true })
        };
        var agreed = 0;
        foreach (var deal in tables)
        {
            var assessment = Ultimatum.AssessNegotiatedDeal(c, player, ai, deal);
            var decision = DealAnswer.Decide(c, ai, player, deal);
            if (assessment.Acceptable) agreed++;
            Check(decision.Verdict == (assessment.Acceptable ? DealVerdict.Accept : DealVerdict.Decline) &&
                  DealAnswer.GovernmentAccepts(assessment) == assessment.Acceptable,
                $"a government answers '{Ultimatum.DealSummary(c, player, ai, deal)}' exactly by its old rule");
        }
        Check(agreed > 0 && agreed < tables.Count, "the sample holds deals a government signs and deals it refuses");
        var relation = CampaignDiplomacy.Get(c, ai.Id, player.Id);
        Check(DealAnswer.GovernmentSignsAgreement(c, ai, player, AgreementKind.TradeAgreement) ==
              (AgreementSystem.Willingness(c, ai, player, AgreementKind.TradeAgreement) >= AgreementSystem.Needed(c, AgreementKind.TradeAgreement)) &&
              relation.AgreementLapsedBy < 0,
            "a government's answer to an agreement is its old willingness bar");
    }

    private static void AgreementOffersFollowTheRules()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        Rule(c, player.Id, "trade_agreement", AutoAnswerMode.Beneficial);
        var trade = new ProposeAgreementCommand { IssuerId = ai.Id, TargetId = player.Id, Kind = AgreementKind.TradeAgreement };
        c.Commands.Enqueue(trade); c.Commands.ApplyAll(c);
        var relation = CampaignDiplomacy.Get(c, ai.Id, player.Id);
        Check(trade.Result == AgreementResult.Signed && relation.TradeAgreement && relation.AgreementOfferedBy < 0,
            "a computer's trade agreement offer is signed by a 'beneficial' rule, through the same signature as by hand");
        Check(player.AutoAnswerLog.Single() is { Accepted: true, Kind: "trade_agreement", OfferId: 0 } note &&
              note.Agreement == (int)AgreementKind.TradeAgreement && note.Benefit >= c.Defines.AutoAcceptMargin,
            "the agreement answer is in the player's list with its benefit");

        Rule(c, player.Id, "non_aggression", AutoAnswerMode.Decline);
        Day(c, 20);
        var pact = new ProposeAgreementCommand { IssuerId = ai.Id, TargetId = player.Id, Kind = AgreementKind.NonAggressionPact };
        c.Commands.Enqueue(pact); c.Commands.ApplyAll(c);
        Check(pact.Result == AgreementResult.Refused && !relation.NonAggressionPact && relation.AgreementRefusedDay == 20 &&
              relation.AgreementOfferedBy < 0,
            "a pact offer is declined by a 'decline' rule, with the ordinary refusal pause");

        // An offer that waited before the rule existed is answered by the next day's sweep.
        c = World(); ai = N(c, 0); player = N(c, 1);
        var waiting = new ProposeAgreementCommand { IssuerId = ai.Id, TargetId = player.Id, Kind = AgreementKind.TradeAgreement };
        c.Commands.Enqueue(waiting); c.Commands.ApplyAll(c);
        relation = CampaignDiplomacy.Get(c, ai.Id, player.Id);
        Check(waiting.Result == AgreementResult.Offered && relation.AgreementOfferedBy == ai.Id,
            "with 'ask' the offer waits on the player's table");
        Rule(c, player.Id, "trade_agreement", AutoAnswerMode.Decline);
        Day(c, 1); AgreementSystem.Tick(c);
        Check(!relation.TradeAgreement && relation.AgreementOfferedBy < 0 && relation.AgreementRefusedDay == 1 &&
              player.AutoAnswerLog.Single().Accepted == false,
            "the daily sweep answers an offer that waited before the rule was set");
    }

    private static void AgreementRefusalAndLapsePauses()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        Apply(c, new ProposeAgreementCommand { IssuerId = ai.Id, TargetId = player.Id, Kind = AgreementKind.NonAggressionPact });
        var no = new AnswerAgreementCommand { IssuerId = player.Id, TargetId = ai.Id, Accept = false };
        Apply(c, no);
        Check(no.Result == AgreementResult.Refused &&
              !AgreementSystem.CanPropose(c, ai, player, AgreementKind.NonAggressionPact, out _, preview: true),
            "the inbox's Decline (Accept=false) refuses and buys the usual pause");

        c = World(); ai = N(c, 0); player = N(c, 1);
        Apply(c, new ProposeAgreementCommand { IssuerId = ai.Id, TargetId = player.Id, Kind = AgreementKind.TradeAgreement });
        var lapseDay = c.Defines.AgreementReplyDays + 1;
        Day(c, lapseDay); AgreementSystem.Tick(c);
        var relation = CampaignDiplomacy.Get(c, ai.Id, player.Id);
        Check(relation.AgreementOfferedBy < 0 && relation.AgreementRefusedDay == -9999 && relation.AgreementLapsedBy == ai.Id,
            "an unanswered offer lapses without becoming a refusal, and the lapse is remembered");
        Check(!AgreementSystem.CanPropose(c, ai, player, AgreementKind.TradeAgreement, out var why, preview: true) && why.Length > 0 &&
              !AgreementSystem.CanProposeQuiet(c, ai, player, AgreementKind.TradeAgreement, preview: true),
            "the asker cannot put the same question on the table again the next month");
        Check(AgreementSystem.CanPropose(c, player, ai, AgreementKind.TradeAgreement, out _, preview: true),
            "the player who let it lapse may still ask for it");
        Day(c, lapseDay + c.Defines.AgreementLapseCooldownDays);
        Check(AgreementSystem.CanPropose(c, ai, player, AgreementKind.TradeAgreement, out _, preview: true),
            "after the lapse pause the asker may ask again");
    }

    /// <summary>
    /// A paper the player's rule declines at delivery is neither waiting nor signed. The asker must
    /// still read it as answered, or the crisis grant and the bought peace go out every week.
    /// </summary>
    private static void ARuleAnswerIsAnAnswerForTheAsker()
    {
        var c = new Campaign(21);
        var patron = new Nation { Id = 0, MapIndex = 1, Tag = "PAT", Name = "Patron", Population = 5_000_000,
            Stability = 0.9f, Gdp = 200f, Treasury = 500f, CapitalStateMapIndex = 10 };
        var client = new Nation { Id = 1, MapIndex = 2, Tag = "CLI", Name = "Client", Population = 5_000_000,
            Stability = 0.1f, Gdp = 200f, Treasury = 40f, CapitalStateMapIndex = 20 };
        c.Nations.Add(patron); c.Nations.Add(client);
        c.States.Add(new ScenarioState { MapIndex = 10, NationMapIndex = 1, Population = 5_000_000, Name = "Patron home" });
        c.States.Add(new ScenarioState { MapIndex = 20, NationMapIndex = 2, Population = 5_000_000, Name = "Client home" });
        c.Defines.CivilWarMinimumRegions = 1;
        c.Defines.CivilWarMinimumPopulation = 1000;
        c.Defines.CivilWarStabilityThreshold = 0.32f;
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 10, 20 }, new ushort[] { 1, 2 }, 2, 1, _ => false));
        var guarantee = CampaignDiplomacy.Get(c, patron.Id, client.Id);
        guarantee.Stance = DiplomaticStance.Neutral; guarantee.Opinion = 20f; guarantee.GuaranteeByA = true;
        c.ConfigureSinglePlayer(client.Id);
        Check(Rule(c, client.Id, "gift", AutoAnswerMode.Decline).Applied, "the player declines gifts by rule");
        Check(AiStatecraft.TryOfferCrisisGrant(c, patron) && c.DealOffers.Single() is { State: DealOfferState.Declined, ByRule: true } &&
              patron.DecisionAvailableDay.GetValueOrDefault("crisis_grant/" + client.Id) > c.ElapsedDays,
            "a crisis grant declined by rule at delivery sets the patron's retry day, as a waiting paper did");
        Check(!AiStatecraft.TryOfferCrisisGrant(c, patron) && c.DealOffers.Count == 1 && client.AutoAnswerLog.Count == 1,
            "the next week the patron does not offer the same grant again");

        c = new Campaign(20261015) { ScenarioId = "auto_answer_peace" };
        var buyer = new Nation { Id = 0, MapIndex = 501, Tag = "FIS", Population = 1000000, Gdp = 100, Treasury = 2.25f,
            Stability = .8f, TaxRate = .3f, MilitarySpending = .04f, WelfareSpending = .055f, EducationSpending = .045f,
            Literacy = .9f, StandardOfLiving = .8f, ConstructionCapacity = 1 };
        var seller = new Nation { Id = 1, MapIndex = 502, Tag = "OTH", Population = 1000000, Gdp = 500, Treasury = .1f,
            Stability = .8f, TaxRate = .2f };
        c.Nations.Add(buyer); c.Nations.Add(seller);
        c.States.Add(new ScenarioState { Id = "fiscal_region", MapIndex = 3001, NationMapIndex = 501, NationId = "FIS", Population = 1000000 });
        c.States.Add(new ScenarioState { Id = "other_region", MapIndex = 3002, NationMapIndex = 502, NationId = "OTH", Population = 1000000 });
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 3001, 3001, 3002, 3002 },
            new ushort[] { 501, 501, 502, 502 }, 4, 1, _ => false));
        c.ConfigureSinglePlayer(seller.Id);
        var rivals = CampaignDiplomacy.Get(c, buyer.Id, seller.Id);
        rivals.Stance = DiplomaticStance.Rival; rivals.Opinion = -70;
        var kind = DealAnswerKinds.Classify(new Ultimatum.Deal(new Ultimatum.Side { Cash = 1, NonAggression = true }, new Ultimatum.Side()));
        Check(kind is not null && Rule(c, seller.Id, kind.Id, AutoAnswerMode.Decline).Applied,
            "the player declines a pact bought for cash by rule");
        Check(AiStatecraft.BuyPeace(c, buyer, seller) && c.DealOffers.Single() is { State: DealOfferState.Declined, ByRule: true } &&
              DealNegotiations.AnsweredByRuleToday(c, buyer.Id, seller.Id),
            "a peace offer declined by rule at delivery is this week's action, like a delivered one");
        Check(!AiStatecraft.BuyPeace(c, buyer, seller) && c.DealOffers.Count == 1 && seller.AutoAnswerLog.Count == 1,
            "the buyer does not send the same cheque again while the answer stands");
        Day(c, Math.Clamp(c.Defines.DealOfferDays, 1, 365));
        Check(!DealNegotiations.AnsweredByRuleToday(c, buyer.Id, seller.Id) && AiStatecraft.BuyPeace(c, buyer, seller) &&
              c.DealOffers.Count == 2,
            "after as long as a paper would have waited, the buyer may ask again");
    }

    /// <summary>A rule that said yes to a paper that could no longer be signed did not decline it.</summary>
    private static void AFailedSignatureIsNotADecline()
    {
        var c = World(); var ai = N(c, 0); var player = N(c, 1);
        var paperId = Delivered(Propose(c, ai.Id, player.Id, fromCash: 10, fromRegions: new[] { 4002 }));
        var paper = c.DealOffers.Single(o => o.Id == paperId);
        c.ProvinceMap.ProvinceToState[paper.FromProvinces.First()] = 4001;
        Rule(c, player.Id, "gift", AutoAnswerMode.Beneficial);
        Day(c, 1); DealNegotiations.Tick(c);
        var note = player.AutoAnswerLog.Single();
        Check(paper.State == DealOfferState.Invalidated && !note.Accepted && DealAnswer.CouldNotSign(note),
            "a paper the rule accepted but that could not be signed is told apart from a decline");

        c = World(); ai = N(c, 0); player = N(c, 1);
        Rule(c, player.Id, "non_aggression", AutoAnswerMode.Decline);
        Propose(c, ai.Id, player.Id, fromPact: true);
        Check(!DealAnswer.CouldNotSign(player.AutoAnswerLog.Single()), "a refusal under 'decline' is still a decline");

        var repo = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        var ui = File.ReadAllText(Path.Combine(repo, "Scripts", "ui", "DealAnswerUI.cs"));
        Check(ui.Contains("DealAnswer.CouldNotSign(note) ? Tr(\"could not be signed\")", StringComparison.Ordinal),
            "the 'Decided by rule' list says 'could not be signed' for such a paper");
        foreach (var language in new[] { "english", "russian" })
        {
            var table = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "game", "localization", language, "340_deal_answers.json")))!;
            Check(table["could not be signed"]?.GetValue<string>() is { Length: > 0 }, $"'could not be signed' is translated ({language})");
        }
    }

    /// <summary>
    /// The inbox's own wiring, read from the source (the UI needs the engine): the answer to a press
    /// is set after the rebuild that clears messages, and an armed "sign if beneficial" belongs to
    /// one opening, one player and one campaign.
    /// </summary>
    private static void InboxMessagesAndArmedConfirmation()
    {
        var repo = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        var answers = File.ReadAllText(Path.Combine(repo, "Scripts", "ui", "DealAnswerUI.cs"));
        var inbox = File.ReadAllText(Path.Combine(repo, "Scripts", "ui", "DealInboxUI.cs"));
        static string Body(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) throw new Exception("deal auto-answer: missing " + signature);
            // A method ends where the next member at class indentation begins.
            var ends = new[] { "\n    private ", "\n    internal ", "\n    public ", "\n    /// " }
                .Select(marker => source.IndexOf(marker, start + signature.Length, StringComparison.Ordinal))
                .Where(index => index >= 0).DefaultIfEmpty(source.Length).Min();
            return source[start..ends];
        }
        foreach (var handler in new[] { "AnswerAgreementOffer", "ChooseStandingAnswer" })
        {
            var body = Body(answers, $"private void {handler}(");
            Check(!body.Contains("_dealInboxMessage =", StringComparison.Ordinal) && body.Contains("ShowDealInboxOutcome(", StringComparison.Ordinal),
                $"{handler} shows its answer after the inbox refresh");
        }
        var show = Body(inbox, "private void ShowDealInboxOutcome(");
        var firstRefresh = show.IndexOf("RefreshDealInbox(force: true);", StringComparison.Ordinal);
        var set = show.IndexOf("_dealInboxMessage = message;", StringComparison.Ordinal);
        var lastRefresh = show.LastIndexOf("RefreshDealInbox(force: true);", StringComparison.Ordinal);
        Check(firstRefresh >= 0 && set > firstRefresh && lastRefresh > set,
            "the message is set between the clearing rebuild and the one that shows it");
        Check(Body(inbox, "internal void OpenDealInbox(").Contains("_dealAnswerConfirmKind = \"\";", StringComparison.Ordinal),
            "opening the inbox disarms 'Confirm: sign if beneficial'");
        var refresh = Body(inbox, "private void RefreshDealInbox(");
        var disarm = refresh.IndexOf("_dealAnswerConfirmKind = \"\";", StringComparison.Ordinal);
        Check(disarm >= 0 && disarm < refresh.IndexOf("var key = DealInboxKey();", StringComparison.Ordinal) &&
              refresh.Contains("_dealAnswerSeat != _campaign.Player.Id", StringComparison.Ordinal) &&
              refresh.Contains("ReferenceEquals(shown, _campaign)", StringComparison.Ordinal),
            "another seat or another campaign finds the confirmation disarmed");
    }

    private static void Apply(Campaign c, ICampaignCommand command)
    {
        c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
    }
}
