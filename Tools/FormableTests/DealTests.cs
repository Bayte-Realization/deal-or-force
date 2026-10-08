using System;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;

static class DealTests
{
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("deal: " + label);
        Console.WriteLine("PASS deal: " + label);
    }

    private static (Campaign c, Nation a, Nation b) Setup(int width = 20, int height = 4)
    {
        var c = new Campaign(26) { ScenarioId = "deal_fixture" };
        var a = new Nation { Id = 0, MapIndex = 501, Tag = "DLA", Name = "First", Treasury = 100, Population = 2000000 };
        var b = new Nation { Id = 1, MapIndex = 502, Tag = "DLB", Name = "Second", Treasury = 100, Population = 2000000 };
        c.Nations.Add(a); c.Nations.Add(b);
        var states = new ushort[width * height]; var owners = new ushort[width * height];
        for (var i = 0; i < 4; i++)
        {
            var owner = i < 2 ? a : b;
            c.States.Add(new ScenarioState { MapIndex = 3001 + i, NationMapIndex = owner.MapIndex,
                NationId = owner.Tag, Id = "deal_" + i, Name = "Region " + i, Population = 1000000 });
        }
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            states[y * width + x] = (ushort)(3001 + x / (width / 4));
            owners[y * width + x] = (ushort)(x < width / 2 ? a.MapIndex : b.MapIndex);
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, width, height, _ => false));
        c.ConfigureSinglePlayer(a.Id);
        return (c, a, b);
    }

    public static void Benchmark()
    {
        var (c, a, b) = Setup(1280, 720);
        var gift = new Ultimatum.Deal(new Ultimatum.Side { Cash = .01f }, new Ultimatum.Side());
        for (var i = 0; i < 10; i++) Ultimatum.AssessDeal(c, a, b, gift);
        var before = CampaignFingerprint.Of(c);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) Ultimatum.AssessDeal(c, a, b, gift);
        watch.Stop();
        Check(before == CampaignFingerprint.Of(c), "cash-only benchmark previews remain read-only");
        Console.WriteLine($"DEAL_BENCH provinces={c.ProvinceMap.ProvinceCount} previews=1000 elapsed_ms={watch.Elapsed.TotalMilliseconds:0.00}");
        watch.Restart();
        for (var i = 0; i < 1000; i++) CheckAccepted(Ultimatum.ResolveDeal(c, a, b, gift));
        watch.Stop();
        Check(Math.Abs(a.Treasury - 90) < .01 && Math.Abs(b.Treasury - 110) < .01,
            "cash-only benchmark settlements preserve actual paid funds");
        Console.WriteLine($"DEAL_BENCH settlements=1000 elapsed_ms={watch.Elapsed.TotalMilliseconds:0.00}");
    }

    private static void CheckAccepted(Ultimatum.DealOutcome outcome)
    { if (!outcome.Accepted) throw new Exception(outcome.Note); }

    public static void Run()
    {
        var (c, a, b) = Setup();
        var gift = new Ultimatum.Deal(new Ultimatum.Side { Cash = 2 }, new Ultimatum.Side());
        var relation = CampaignDiplomacy.Get(c, a.Id, b.Id);
        relation.TruceUntil = c.ElapsedDays + 90;
        Check(Ultimatum.ValidateDeal(c, a, b, gift, out _), "a truce permits peaceful bargains");
        relation.NonAggressionPact = true;
        relation.Stance = DiplomaticStance.Allied;
        Check(Ultimatum.ValidateDeal(c, a, b, gift, out _), "allies with an existing pact can trade cash and land");
        foreach (var cash in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Check(!Ultimatum.ValidateDeal(c, a, b,
                new Ultimatum.Deal(new Ultimatum.Side { Cash = cash }, new Ultimatum.Side()), out _),
                "non-finite payments never reach the treasury");
        var before = CampaignFingerprint.Of(c);
        Check(!Ultimatum.ResolveDeal(c, a, b,
            new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side { Regions = new[] { 3003 } })).Accepted,
            "direct resolution cannot steal land without consent");
        Check(before == CampaignFingerprint.Of(c), "refusal is an atomic no-op");
        c.Relations.Clear();
        before = CampaignFingerprint.Of(c);
        Check(Ultimatum.DealAcceptable(c, a, b, gift), "a valid cash gift is acceptable");
        Check(c.Relations.Count == 0 && before == CampaignFingerprint.Of(c), "previews never materialize diplomatic state");

        var child = c.States.Single(s => s.MapIndex == 3003);
        child.ParentMapIndex = 3001;
        var toProvinces = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
            .Where(p => c.ProvinceMap.ProvinceToState[p] == child.MapIndex).ToArray();
        // A capital is not for sale (Deals): the swap is of a region that is not the seat of government.
        if (a.CapitalStateMapIndex == 3001)
            a.CapitalStateMapIndex = c.StatesOf(a).First(s => s.MapIndex != 3001).MapIndex;
        if (b.CapitalStateMapIndex == child.MapIndex)
            b.CapitalStateMapIndex = c.StatesOf(b).FirstOrDefault(s => s.MapIndex != child.MapIndex)?.MapIndex ?? 0;
        var swap = new Ultimatum.Deal(new Ultimatum.Side { Regions = new[] { 3001 } },
            new Ultimatum.Side { Regions = new[] { child.MapIndex } });
        Check(Ultimatum.ResolveDeal(c, a, b, swap).Accepted, "equally populated regions may be exchanged");
        Check(toProvinces.All(p => c.ProvinceMap.ProvinceToNation[p] == a.MapIndex),
            "both halves of a swap survive intermediate region reunification");
        Check(a.Notoriety == 0 && b.Notoriety == 0, "consensual land exchange is not recorded as conquest");
        var swapped = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "deal-swap"), "deal-swap");
        Check(swapped.Success, "an exchanged-border snapshot restores");
        swapped.Campaign.AttachProvinceTopology(Setup().c.ProvinceMap);
        Check(toProvinces.All(p => swapped.Campaign.ProvinceMap.ProvinceToNation[p] == a.MapIndex),
            "reattaching the authored map never reverses the signed swap");

        (c, a, b) = Setup();
        c.States.Add(new ScenarioState { MapIndex = 3999, NationMapIndex = a.MapIndex, Name = "No ground" });
        Check(!Ultimatum.ValidateDeal(c, a, b,
            new Ultimatum.Deal(new Ultimatum.Side { Regions = new[] { 3001, 3002 } }, new Ultimatum.Side()), out _),
            "an unmapped region record cannot let a country give away its last ground");

        (c, a, b) = Setup();
        var refused = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, ToRegions = new[] { 3003 } };
        before = CampaignFingerprint.Of(c);
        refused.Apply(c);
        Check(!refused.Outcome.Accepted && refused.Outcome.Note != "No deal was proposed.",
            "a refused command explains the actual acceptance shortfall");
        Check(before == CampaignFingerprint.Of(c), "a rejected command changes no campaign state");
        c.SetFeature("deals", false);
        var disabled = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, FromCash = 1 };
        before = CampaignFingerprint.Of(c);
        disabled.Apply(c);
        Check(!disabled.Outcome.Accepted && disabled.Outcome.Note != "No deal was proposed." &&
            before == CampaignFingerprint.Of(c), "a disabled mechanic explains rejection without charging");
        c.SetFeature("deals", true);
        c.Defines.DealMaxTreasuryShare = 2;
        b.Treasury = 10;
        Check(!Ultimatum.ValidateDeal(c, a, b,
            new Ultimatum.Deal(new Ultimatum.Side { Cash = 20 }, new Ultimatum.Side { Cash = 11 }), out _),
            "a modded maximum treasury share can never authorize unfunded cash");
        a.Treasury = float.MaxValue;
        b.Treasury = float.MaxValue;
        Check(!Ultimatum.ValidateDeal(c, a, b,
            new Ultimatum.Deal(new Ultimatum.Side { NonAggression = true }, new Ultimatum.Side { Cash = b.Treasury / 2 }), out _),
            "a transfer that cannot be represented without overflow is rejected");
        (c, a, b) = Setup();
        c.Defines.FeatureCommands[nameof(ProposeDealCommand)] = "test_deal_gate";
        c.SetFeature("test_deal_gate", false);
        Check(!Ultimatum.AssessDeal(c, a, b, gift).Valid,
            "the preview obeys a mod-defined command gate");
        c.SetFeature("test_deal_gate", true);
        Check(Ultimatum.AssessDeal(c, a, b, gift).Acceptable,
            "reenabling a command gate restores peaceful exchange");
        foreach (var p in Enumerable.Range(1, c.ProvinceMap.ProvinceCount)) c.ProvinceMap.Neighbors[p] = Array.Empty<int>();
        c.Fleets.Clear(); c.MarkMapChanged();
        Check(!CampaignDiplomacy.CanDeclareWar(c, a.Id, b.Id, out _) &&
            Ultimatum.ValidateDeal(c, a, b, gift, out _), "distant partners need no invasion capability to trade");
        c.Wars.Add(new StrategicWar { Id = 1, AttackerId = a.Id, DefenderId = b.Id });
        Check(!Ultimatum.ValidateDeal(c, a, b, gift, out _), "active opponents settle at a peace conference, not a deal bypass");
        c.Wars.Clear();
        b.IsHuman = true;
        Check(!Ultimatum.ValidateDeal(c, a, b, gift, out _), "human counterparties are never auto-consented");
        b.IsHuman = false;
        Check(!Ultimatum.ValidateDeal(c, a, b, new Ultimatum.Deal(null, new Ultimatum.Side()), out _) &&
            !Ultimatum.ValidateDeal(c, a, b, new Ultimatum.Deal(new Ultimatum.Side(), null), out _),
            "malformed sides reject rather than throw");
        Check(Ultimatum.ValidateDeal(c, a, b,
            new Ultimatum.Deal(new Ultimatum.Side { Cash = 2, Regions = null }, new Ultimatum.Side()), out _),
            "missing region arrays are treated as no territorial offer");
        Check(!Ultimatum.ValidateDeal(c, a, b,
            new Ultimatum.Deal(new Ultimatum.Side { Cash = 2 }, new Ultimatum.Side { Cash = 2 }), out _),
            "two cancelling payments cannot buy a free truce");
        var exchange = new Ultimatum.Deal(new Ultimatum.Side { Cash = 3 }, new Ultimatum.Side { Cash = 1 });
        var assessment = Ultimatum.AssessDeal(c, a, b, exchange);
        Check(assessment.Acceptable && assessment.FromTreasuryAfter == 98 && assessment.ToTreasuryAfter == 102,
            "cash previews show face-value balances, never diplomatic scores");
        Check(assessment.ToValuation.Total == assessment.ToValue && assessment.ToValuation.Cash > 0 &&
            assessment.ToValuation.Land == 0 && assessment.ToValuation.Treaties == 0,
            "the visible breakdown exactly adds to AI acceptance");
        var duplicateLand = new Ultimatum.Deal(new Ultimatum.Side { Cash = 10 },
            new Ultimatum.Side { Regions = new[] { 3003, 3003 } });
        var singleLand = duplicateLand with { To = new Ultimatum.Side { Regions = new[] { 3003 } } };
        Check(Ultimatum.AssessDeal(c, a, b, duplicateLand).ToValue == Ultimatum.AssessDeal(c, a, b, singleLand).ToValue,
            "duplicate region IDs never double the negotiated value");
        var stale = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, FromCash = 80 };
        Check(Ultimatum.AssessDeal(c, a, b,
            new Ultimatum.Deal(new Ultimatum.Side { Cash = 80 }, new Ultimatum.Side())).Acceptable,
            "a funded package can be reviewed");
        a.Treasury = 5;
        before = CampaignFingerprint.Of(c);
        stale.Apply(c);
        Check(!stale.Outcome.Accepted && before == CampaignFingerprint.Of(c),
            "queued packages revalidate funding instead of trusting an old preview");

        (c, a, b) = Setup();
        var embargo = CampaignDiplomacy.Get(c, a.Id, b.Id);
        embargo.EmbargoByA = true; embargo.EmbargoByB = true;
        var mutual = new Ultimatum.Deal(new Ultimatum.Side { LiftEmbargo = true },
            new Ultimatum.Side { LiftEmbargo = true });
        var mutualSummary = Ultimatum.DealSummary(c, a, b, mutual);
        Check(mutualSummary.Contains("First: lift embargo against Second") &&
            mutualSummary.Contains("Second: lift embargo against First"),
            "a reciprocal sanctions summary names who lifts which embargo");
        Check(Ultimatum.ResolveDeal(c, a, b, mutual).Accepted && !embargo.EmbargoByA && !embargo.EmbargoByB,
            "reciprocal directional embargo relief is not a duplicate treaty");
        Check(!Ultimatum.ValidateDeal(c, a, b, mutual, out _), "already lifted embargoes cannot be promised again");
        ReplayAndSnapshot();
        (c, a, b) = Setup();
        a.IsHuman = false;
        a.Population = 100000; a.Gdp = 1;
        b.Population = 5000000; b.Gdp = 200; b.Treasury = 0;
        c.Armies.Clear();
        CampaignDiplomacy.Get(c, a.Id, b.Id);
        var (unrelated, unrelatedBuyer, unrelatedSeller) = Setup();
        unrelatedBuyer.Gdp = 10000; unrelatedSeller.Gdp = 1;
        unrelatedBuyer.IsHuman = unrelatedSeller.IsHuman = true;
        AiStatecraft.Tick(unrelated);
        Check(AiStatecraft.BuyPeace(c, a, b) && b.Treasury > 0 && a.Treasury < 100,
            "a broke counterparty can receive an affordable AI peace offer despite another campaign's cached strength");
    }

    private static void ReplayAndSnapshot()
    {
        var (c, a, b) = Setup();
        var (peer, peerA, peerB) = Setup();
        var command = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id,
            FromCash = 9, ToCash = 1, FromNonAggression = true, FromTradeAgreement = true };
        ICampaignCommand copy = null;
        Check(NetworkCommandCodec.TryEncode(command, out var type, out var json) &&
            NetworkCommandCodec.TryDecode(type, json, out copy) && copy is ProposeDealCommand,
            "the real command wire format carries a complete exchange");
        c.Commands.Enqueue(command);
        Check(a.Treasury == 100 && b.Treasury == 100, "queueing a deal never mutates mid-hook");
        c.Commands.ApplyAll(c); peer.Commands.Enqueue(copy); peer.Commands.ApplyAll(peer);
        Check(command.Outcome.Accepted && a.Treasury == 92 && b.Treasury == 108,
            "the command boundary settles the agreed net cash exactly once");
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(peer),
            "decoded peer and local command produce the same deterministic campaign");
        var relation = CampaignDiplomacy.Peek(c, a.Id, b.Id);
        Check(relation.NonAggressionPact && relation.TradeAgreement && relation.TruceUntil == 180,
            "the package signs both arrangements and the configured truce");
        c.SetFeature("deals", false);
        var result = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "deal-tests"), "deal-tests");
        Check(result.Success, "a signed campaign snapshot restores successfully");
        var loaded = result.Campaign;
        var restoredRelation = CampaignDiplomacy.Peek(loaded, a.Id, b.Id);
        Check(loaded.NationById(a.Id).Treasury == 92 && loaded.NationById(b.Id).Treasury == 108 &&
            restoredRelation.NonAggressionPact && restoredRelation.TradeAgreement && restoredRelation.TruceUntil == 180,
            "save/network snapshots preserve paid cash, treaties and truce without replaying payment");
        Check(!loaded.FeatureEnabled("deals"), "saved scenario disable survives reconnect");
        var legacy = new Campaign(1, new Defines());
        legacy.Defines.Features.Remove("deals");
        Check(legacy.FeatureEnabled("deals"), "older saves missing the additive switch retain deals");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var summary = Ultimatum.DealSummary(c, a, b,
                new Ultimatum.Deal(new Ultimatum.Side { Cash = 2 }, new Ultimatum.Side()));
            Check(summary.Contains("Разовая выплата") && !summary.Contains("Reparations"),
                "a negotiated payment is not mislabeled as reparations");
            var reason = Ultimatum.DealRefusalNote(2.5f, true);
            Check(reason.Contains("дипломатических баллов") && !reason.Contains("$"),
                "diplomatic shortfall is localized as points rather than imaginary cash");
        }
        finally { Loc.Language = language; }
    }
}
