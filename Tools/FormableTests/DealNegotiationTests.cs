using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class DealNegotiationTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("negotiation: " + label);
        Console.WriteLine("PASS negotiation: " + label);
    }

    private static (Campaign c, Nation a, Nation b) Setup()
    {
        var c = new Campaign(26) { ScenarioId = "negotiation_fixture" };
        var a = new Nation { Id = 0, MapIndex = 501, Tag = "DLA", Name = "First", Treasury = 100, Population = 2000000,
            OutputScale = 1, CapitalStateMapIndex = 3001 };
        var b = new Nation { Id = 1, MapIndex = 502, Tag = "DLB", Name = "Second", Treasury = 100, Population = 2000000,
            OutputScale = 1, CapitalStateMapIndex = 3003 };
        c.Nations.Add(a); c.Nations.Add(b);
        for (var i = 0; i < 4; i++)
        {
            var owner = i < 2 ? a : b;
            c.States.Add(new ScenarioState { MapIndex = 3001 + i, NationMapIndex = owner.MapIndex,
                Id = "negotiation_" + i, NationId = owner.Tag, Name = "Region " + i, Population = 1000000 });
        }
        var states = Enumerable.Range(0, 24).Select(p => (ushort)(3001 + (p % 12) / 3)).ToArray();
        var owners = Enumerable.Range(0, 24).Select(p => (ushort)((p % 12) < 6 ? 501 : 502)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 12, 2, _ => false));
        foreach (var nation in c.Nations)
        {
            nation.CapitalStateMapIndex = nation.Id == a.Id ? 3001 : 3003;
            nation.CapitalProvinceId = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
                .First(p => c.ProvinceMap.ProvinceToState[p] == nation.CapitalStateMapIndex);
            var centre = c.ProvinceMap.CenterOf(nation.CapitalProvinceId);
            nation.CapitalLatitude = centre.Latitude; nation.CapitalLongitude = centre.Longitude;
        }
        SovereignFinance.NormalizeAccounts(c);
        c.ConfigureSinglePlayer(b.Id);
        return (c, a, b);
    }

    private static int Offer(Campaign c, Nation a, Nation b, float cash = 2)
    {
        var command = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, FromCash = cash };
        c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
        if (command.Outcome.PendingOfferId <= 0) throw new Exception("Expected delivered offer: " + command.Outcome.Note);
        return command.Outcome.PendingOfferId;
    }

    private static RespondDealCommand Answer(Campaign c, int issuer, int id, DealReply reply)
    {
        var command = new RespondDealCommand { IssuerId = issuer, OfferId = id, Reply = reply };
        c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
        return command;
    }

    private static void Day(Campaign c, int day)
    {
        // Advance only the real authoritative clock, without unrelated economy/war ticks.
        c.RestoreRuntimeState(c.Date, day, true, c.Random.State, c.Player.Id,
            c.HumanSeats.ToArray(), c.ActiveSeat, c.NextBattleId, c.MapRevision,
            c.InitialDeploymentCompleted, c.ProvinceController, c.ProvinceOccupation,
            c.ProvinceMap.ProvinceToNation, c.ProvinceMap.ProvinceToState);
    }

    public static void Run()
    {
        DeliveryAndConsent(); CleanupAndDeadlines(); RevalidateBeforeSigning();
        Counterproposals(); PersistenceAndNetwork(); LimitsAndBadData();
    }

    private static void DeliveryAndConsent()
    {
        var (c, a, b) = Setup(); var id = Offer(c, a, b);
        Check(a.Treasury == 100 && b.Treasury == 100 && !CampaignDiplomacy.Get(c, a.Id, b.Id).NonAggressionPact,
            "AI proposal delivers a paper without automatic money or treaty transfer");
        var paper = c.DealOffers.Single();
        Check(id == 1 && c.NextDealOfferId == 2 && paper.ExpiresDay == 30 && DealNegotiations.Incoming(c, b.Id) == 1,
            "offer has a unique number, deadline and addressed inbox");
        var hash = CampaignFingerprint.Of(c);
        Check(!Answer(c, a.Id, id, DealReply.Accept).Outcome.Accepted && hash == CampaignFingerprint.Of(c), "proposer cannot accept for recipient");
        Check(!Answer(c, b.Id, id, DealReply.Withdraw).Outcome.Accepted && hash == CampaignFingerprint.Of(c), "recipient cannot impersonate proposer withdrawal");
        Check(!Answer(c, b.Id, id, (DealReply)99).Outcome.Accepted && hash == CampaignFingerprint.Of(c), "undefined reply is an atomic no-op");
        Check(Answer(c, b.Id, id, DealReply.Accept).Outcome.Accepted && a.Treasury == 98 && b.Treasury == 102 && paper.State == DealOfferState.Accepted,
            "explicit recipient signature settles the payment once");
        hash = CampaignFingerprint.Of(c);
        Check(!Answer(c, b.Id, id, DealReply.Accept).Outcome.Accepted && hash == CampaignFingerprint.Of(c), "replayed signature cannot double-charge");
        (c, a, b) = Setup();
        var demand = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, ToCash = 20 }; demand.Apply(c);
        Check(demand.Outcome.PendingOfferId > 0 && !Ultimatum.AssessNegotiatedDeal(c, a, b, c.DealOffers.Single().Terms).Acceptable,
            "human may review an offer even when AI valuation would refuse it");
        Check(Answer(c, b.Id, demand.Outcome.PendingOfferId, DealReply.Accept).Outcome.Accepted && a.Treasury == 120 && b.Treasury == 80,
            "human consent is a choice, not an AI utility threshold");
        (c, a, b) = Setup(); c.AddHumanSeat(a.Id); Offer(c, a, b);
        Check(a.Treasury == 100 && b.Treasury == 100 && c.DealOffers.Single().Pending, "hotseat players also negotiate without inferred consent");
        Check(!Ultimatum.ResolveDeal(c, a, b, c.DealOffers.Single().Terms).Accepted, "legacy automatic API still refuses human targets");
    }

    private static void CleanupAndDeadlines()
    {
        foreach (var reply in new[] { DealReply.Decline, DealReply.Withdraw })
        {
            var (c, a, b) = Setup(); var id = Offer(c, a, b);
            Answer(c, reply == DealReply.Decline ? b.Id : a.Id, id, reply);
            Check(a.Treasury == 100 && b.Treasury == 100 && !c.DealOffers.Single().Pending, reply + " closes paper without transfers");
        }
        var (world, from, to) = Setup(); var paperId = Offer(world, from, to);
        Day(world, 29); DealNegotiations.Tick(world);
        Check(world.DealOffers.Single().Pending, "offer remains live before exact deadline");
        Day(world, 30);
        Check(!Answer(world, to.Id, paperId, DealReply.Accept).Outcome.Accepted && from.Treasury == 100 && world.DealOffers.Single().State == DealOfferState.Expired,
            "signature checks deadline even before administrative tick");
        (world, from, to) = Setup(); Offer(world, from, to); Day(world, 30); DealNegotiations.Tick(world);
        Check(world.DealOffers.Single().State == DealOfferState.Expired && DealNegotiations.Incoming(world, to.Id) == 0, "daily cleanup expires unattended offers");
        (world, from, to) = Setup(); paperId = Offer(world, from, to); world.SetFeature("deals", false);
        Check(!Answer(world, to.Id, paperId, DealReply.Accept).Outcome.Accepted && from.Treasury == 100, "disabled mechanic cannot be accepted");
        Answer(world, to.Id, paperId, DealReply.Decline);
        Check(world.DealOffers.Single().State == DealOfferState.Declined, "disabled mechanic still permits refusal cleanup");
        (world, from, to) = Setup(); Offer(world, from, to); world.ConfigureSinglePlayer(from.Id); DealNegotiations.Tick(world);
        Check(world.DealOffers.Single().State == DealOfferState.Invalidated, "recipient losing player control does not auto-sign");
    }

    private static void RevalidateBeforeSigning()
    {
        var (c, a, b) = Setup(); var id = Offer(c, a, b, 50); a.Treasury = 10;
        Check(!Answer(c, b.Id, id, DealReply.Accept).Outcome.Accepted && a.Treasury == 10 && b.Treasury == 100 && c.DealOffers.Single().State == DealOfferState.Invalidated,
            "spent offered funds invalidate complete package without partial payment");
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, -1f })
        {
            (c, a, b) = Setup(); var hash = CampaignFingerprint.Of(c);
            var bad = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, FromCash = value }; bad.Apply(c);
            Check(bad.Outcome.PendingOfferId == 0 && c.DealOffers.Count == 0 && hash == CampaignFingerprint.Of(c), "invalid cash cannot enter diplomatic queue: " + value);
        }
        foreach (var change in new[] { "occupation", "ownership", "capital", "shape", "war", "treaty", "gate" })
        {
            (c, a, b) = Setup(); var regions = new[] { 3002 };
            var offer = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, FromRegions = regions, FromCash = 2, FromNonAggression = true }; offer.Apply(c);
            if (offer.Outcome.PendingOfferId == 0) throw new Exception(offer.Outcome.Note);
            var paper = c.DealOffers.Single(); regions[0] = 3001;
            Check(paper.From.Regions.SequenceEqual(new[] { 3002 }), "submitted arrays are copied: " + change);
            var province = paper.FromProvinces.First();
            switch (change)
            {
                case "occupation": c.SetProvinceController(province, (ushort)b.MapIndex); break;
                case "ownership": c.ProvinceMap.ProvinceToNation[province] = (ushort)b.MapIndex; break;
                case "capital": a.CapitalStateMapIndex = 3002; break;
                case "shape": c.ProvinceMap.ProvinceToState[province] = 3001; break;
                case "war": c.Wars.Add(new StrategicWar { Id = 1, AttackerId = a.Id, DefenderId = b.Id }); break;
                case "treaty": CampaignDiplomacy.Get(c, a.Id, b.Id).NonAggressionPact = true; break;
                case "gate": c.Defines.FeatureCommands[nameof(ProposeDealCommand)] = "test_deal_gate"; c.SetFeature("test_deal_gate", false); break;
            }
            Check(!Answer(c, b.Id, paper.Id, DealReply.Accept).Outcome.Accepted && a.Treasury == 100 && b.Treasury == 100, "signature revalidates " + change + " without partial cash transfer");
        }
    }

    private static void Counterproposals()
    {
        var (c, a, b) = Setup(); var id = Offer(c, a, b); var original = c.DealOffers.Single();
        var refused = new ProposeDealCommand { IssuerId = b.Id, ToId = a.Id, ParentOfferId = id, ToCash = 20 };
        var hash = CampaignFingerprint.Of(c); refused.Apply(c);
        Check(!refused.Outcome.Accepted && refused.Outcome.PendingOfferId == 0 && original.Pending && hash == CampaignFingerprint.Of(c), "refused AI counterproposal leaves original live");
        var accepted = new ProposeDealCommand { IssuerId = b.Id, ToId = a.Id, ParentOfferId = id, FromCash = 1 }; accepted.Apply(c);
        Check(accepted.Outcome.Accepted && original.State == DealOfferState.Superseded && a.Treasury == 101 && b.Treasury == 99 && c.DealOffers.Single(o => o.Id != id).ParentOfferId == id,
            "AI-approved counter settles new terms and supersedes original");
        Check(!Answer(c, b.Id, id, DealReply.Accept).Outcome.Accepted && a.Treasury == 101, "superseded original cannot be signed afterwards");
        (c, a, b) = Setup(); c.AddHumanSeat(a.Id); id = Offer(c, a, b);
        var duplicate = new ProposeDealCommand { IssuerId = b.Id, ToId = a.Id, FromCash = 1 }; duplicate.Apply(c);
        Check(duplicate.Outcome.PendingOfferId == 0 && c.DealOffers.Count == 1, "reverse-direction duplicate must be explicit counterproposal");
        for (var depth = 1; depth <= c.Defines.DealCounterLimit; depth++)
        {
            var prior = c.DealOffers.Single(o => o.Id == id);
            var next = new ProposeDealCommand { IssuerId = prior.ToId, ToId = prior.FromId, ParentOfferId = id, FromCash = 1 }; next.Apply(c); id = next.Outcome.PendingOfferId;
            Check(id > prior.Id && prior.State == DealOfferState.Superseded && c.DealOffers.Single(o => o.Id == id).CounterDepth == depth, "human counter has a new ID and chain depth " + depth);
        }
        var last = c.DealOffers.Single(o => o.Id == id); hash = CampaignFingerprint.Of(c);
        var overflow = new ProposeDealCommand { IssuerId = last.ToId, ToId = last.FromId, ParentOfferId = id, FromCash = 1 }; overflow.Apply(c);
        Check(overflow.Outcome.PendingOfferId == 0 && last.Pending && hash == CampaignFingerprint.Of(c), "counter limit preserves last answerable offer");
    }

    private static void PersistenceAndNetwork()
    {
        var (c, a, b) = Setup(); Offer(c, a, b);
        var result = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "negotiation"), "negotiation");
        if (!result.Success) throw new Exception(result.Warning);
        var clone = result.Campaign; clone.AttachProvinceTopology(Setup().c.ProvinceMap);
        Check(clone.Defines.SpeedFactors.SequenceEqual(c.Defines.SpeedFactors), "saved speed table replaces defaults instead of appending them");
        Check(clone.DealOffers.Count == 1 && clone.NextDealOfferId == c.NextDealOfferId && clone.DealOffers[0].Pending && CampaignFingerprint.Of(clone) == CampaignFingerprint.Of(c),
            "authoritative snapshot preserves pending paper, ID and fingerprint");
        var command = new RespondDealCommand { IssuerId = b.Id, OfferId = 1, Reply = DealReply.Accept };
        Check(NetworkCommandCodec.TryEncode(command, out var type, out var json) && NetworkCommandCodec.TryDecode(type, json, out var decoded) &&
            decoded is RespondDealCommand answer && answer.IssuerId == b.Id && answer.OfferId == 1 && answer.Reply == DealReply.Accept,
            "reply uses actual whitelisted LAN codec");
        NetworkCommandCodec.TryDecode(type, json, out var onPeer); command.Apply(c); onPeer.Apply(clone);
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(clone), "host and peer apply encoded signature identically");
        var hash = CampaignFingerprint.Of(c); c.DealOffers[0].Reason = "Presentation in another language";
        Check(hash == CampaignFingerprint.Of(c), "localized receipt text is excluded from simulation fingerprint");
        var locale = Loc.Language; Loc.Language = "russian";
        Check(hash == CampaignFingerprint.Of(c), "language switch cannot change signed simulation state"); Loc.Language = locale;
        using var input = new MemoryStream(SaveSystem.ExportNetworkSnapshot(c, "negotiation"));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var old = JsonNode.Parse(gzip).AsObject(); old.Remove("DealOffers"); old.Remove("NextDealOfferId");
        using var output = new MemoryStream();
        using (var compress = new GZipStream(output, CompressionLevel.Fastest, true)) compress.Write(Encoding.UTF8.GetBytes(old.ToJsonString()));
        var imported = SaveSystem.ImportNetworkSnapshot(output.ToArray(), "negotiation");
        Check(imported.Success && imported.Campaign.DealOffers.Count == 0 && imported.Campaign.NextDealOfferId == 1, "older snapshots missing negotiation fields load with empty inbox");
    }

    private static void LimitsAndBadData()
    {
        var (c, a, b) = Setup(); c.Defines.DealHistoryLimit = 3;
        for (var i = 0; i < 10; i++) Answer(c, b.Id, Offer(c, a, b), DealReply.Decline);
        DealNegotiations.Tick(c);
        Check(c.DealOffers.Count == 3 && c.DealOffers.Min(o => o.Id) == 8 && c.NextDealOfferId == 11, "bounded history never recycles offer IDs");
        var active = Offer(c, a, b); c.Defines.DealHistoryLimit = 0; DealNegotiations.Tick(c);
        Check(c.DealOffers.Count == 1 && c.DealOffers[0].Id == active && c.DealOffers[0].Pending, "history trimming keeps answerable paper");
        var valid = DealNegotiations.Copy(c.DealOffers[0]);
        var nan = DealNegotiations.Copy(valid); nan.Id = 21; nan.From.Cash = float.NaN;
        var badEnum = DealNegotiations.Copy(valid); badEnum.Id = 22; badEnum.State = (DealOfferState)80;
        var self = DealNegotiations.Copy(valid); self.Id = 23; self.ToId = self.FromId;
        var badClock = DealNegotiations.Copy(valid); badClock.Id = 24; badClock.ExpiresDay = 0;
        DealNegotiations.Restore(c, new[] { valid, valid, nan, badEnum, self, badClock, null }, 1);
        Check(c.DealOffers.Count == 1 && c.NextDealOfferId > valid.Id && !ReferenceEquals(valid.From.Regions, c.DealOffers[0].From.Regions), "restore filters duplicate IDs and malformed papers, copying arrays");
        c.NextDealOfferId = int.MaxValue; Answer(c, b.Id, valid.Id, DealReply.Decline);
        var exhausted = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, FromCash = 2 }; exhausted.Apply(c);
        Check(exhausted.Outcome.PendingOfferId == 0 && c.NextDealOfferId == int.MaxValue, "exhausted numbering refuses instead of overflowing");
    }
}
