using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

/// <summary>
/// Trade agreements with terms (TradeTerms): what the terms do to trade by the formula, how a supply
/// guarantee is served and how it is broken, how a term ends, how an old on/off agreement loads,
/// that a government and a player meet the same rules on both sides of every offer, and that the
/// terms survive a save and a network command. `--trade-terms-only` (alias `--trade-agreements-only`).
/// </summary>
static class TradeTermsTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("trade terms: " + label);
        Console.WriteLine("PASS trade terms: " + label);
    }

    public static void Run()
    {
        OldAgreementsLoadAsDefaultTerms();
        EveryWriterKeepsTheFlagAndTheTerms();
        ReliefMovesOnlyTheGoodsItNames();
        AGuaranteeIsServedFirstAndWithinItsPromise();
        AGuaranteeStopsUnderWarEmbargoAndBan();
        AGuaranteeNotKeptIsCounted();
        MarkupAndDutyFollowTheFormula();
        TheCountHasNoColumn();
        CutsAndGuaranteesCostByCharacter();
        TheSameRulesForEitherSeat();
        ARefusalHoldsByEitherRoute();
        TheComputerAsksAPlayerOnlyWhatTheyWouldSign();
        ATradeOnlyDealBuysNoTruce();
        TermsRenewOrLapse();
        APlayerIsSentTheRenewal();
        LeavingCostsByWhatIsLeft();
        TermsTravelAndPersist();
    }

    // ------------------------------------------------------------------ fixture

    /// <summary>
    /// <paramref name="count"/> countries side by side on a strip, two regions each, food and oil
    /// markets; the last country is the player's seat. A test that needs a player elsewhere takes that
    /// seat with ConfigureSinglePlayer, because the clock (Day) restores who is human from the seats.
    /// </summary>
    private static Campaign World(int count = 3)
    {
        var c = new Campaign(31) { ScenarioId = "trade_terms_fixture" };
        for (var i = 0; i < count; i++)
        {
            var nation = new Nation
            {
                Id = i, MapIndex = 701 + i, Tag = "TT" + (char)('A' + i), Name = "Country " + i, Treasury = 100,
                Population = 2000000, OutputScale = 1, Gdp = 100f, Stability = 0.8f, WeeklyTaxIncome = 10f
            };
            c.Nations.Add(nation);
            for (var r = 0; r < 2; r++)
                c.States.Add(new ScenarioState { MapIndex = 5001 + 2 * i + r, NationMapIndex = nation.MapIndex,
                    Id = $"trade_terms_{i}_{r}", NationId = nation.Tag, Name = $"Region {i}.{r}", Population = 1000000 });
        }
        var width = 6 * count;
        var states = Enumerable.Range(0, width * 2).Select(p => (ushort)(5001 + (p % width) / 3)).ToArray();
        var owners = Enumerable.Range(0, width * 2).Select(p => (ushort)(701 + (p % width) / 6)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, width, 2, _ => false));
        foreach (var nation in c.Nations)
        {
            nation.CapitalStateMapIndex = 5001 + 2 * nation.Id;
            nation.CapitalProvinceId = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
                .First(p => c.ProvinceMap.ProvinceToState[p] == nation.CapitalStateMapIndex);
            var centre = c.ProvinceMap.CenterOf(nation.CapitalProvinceId);
            nation.CapitalLatitude = centre.Latitude; nation.CapitalLongitude = centre.Longitude;
        }
        SovereignFinance.NormalizeAccounts(c);
        c.ConfigureSinglePlayer(count - 1);
        return c;
    }

    private static void Quote(Nation n, Commodity good, float production, float demand, float price = 1f) =>
        n.Market[good] = new MarketQuote { Commodity = good, Production = production, Demand = demand, Price = price };

    private static void Day(Campaign c, int day) =>
        c.RestoreRuntimeState(c.Date, day, true, c.Random.State, c.Player.Id,
            c.HumanSeats.ToArray(), c.ActiveSeat, c.NextBattleId, c.MapRevision,
            c.InitialDeploymentCompleted, c.ProvinceController, c.ProvinceOccupation,
            c.ProvinceMap.ProvinceToNation, c.ProvinceMap.ProvinceToState);

    private static void Apply(Campaign c, ICampaignCommand command)
    {
        c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
    }

    private static TradeTerms Terms(int aId, int bId, float cutA, float cutB, int years = 0, string[] goodsA = null, string[] goodsB = null)
    {
        var t = new TradeTerms { CutA = cutA, CutB = cutB, Years = years, GoodsA = goodsA ?? Array.Empty<string>(), GoodsB = goodsB ?? Array.Empty<string>() };
        Check(TradeTerms.Sanitize(t, Math.Min(aId, bId), Math.Max(aId, bId)).Length == 0, "the fixture's terms are writable");
        return t;
    }

    private static string Id(Commodity good) => CommodityCatalog.IdOf(good);

    private static bool Invariant(Campaign c) =>
        c.Relations.Values.Concat(c.DormantRelations.Values).All(r => r.TradeAgreement == (r.Trade is not null));

    private static float Line(Breakdown why, string source) => why.Entries.Where(e => e.Source == source).Sum(e => e.Value);

    // ------------------------------------------------------------------ migration and invariants

    private static void OldAgreementsLoadAsDefaultTerms()
    {
        var c = World();
        var r = CampaignDiplomacy.Get(c, 0, 1);
        r.TradeAgreement = true; // a relation as a save from before terms wrote it: the flag alone
        Check(Math.Abs(TradeNetwork.AgreementFactor(c, r, 0, Commodity.Food) - 1.75f) < 1e-6f &&
              Math.Abs(TradeNetwork.AgreementFactor(c, r, 1, Commodity.Oil) - 1.75f) < 1e-6f,
            "an old on/off agreement ranks a partner exactly as the old flag did (K = 1.75)");
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "trade-terms"), "trade-terms");
        Check(loaded.Success, "a world with an old agreement saves and loads");
        var back = CampaignDiplomacy.Peek(loaded.Campaign, 0, 1);
        Check(back is { TradeAgreement: true, Trade: { CutA: 0.5f, CutB: 0.5f, EndsDay: -1, Years: 0 } } &&
              back.Trade.GoodsA.Length == 0 && back.Trade.GoodsB.Length == 0 && !back.Trade.HasGuarantee,
            "an old agreement loads with the old terms: half the tariff off everything, both ways, for good");
        loaded.Campaign.AttachProvinceTopology(c.ProvinceMap);
        Check(Math.Abs(TradeNetwork.AgreementFactor(loaded.Campaign, back, 0, Commodity.Food) - 1.75f) < 1e-6f,
            "and still ranks its partner by 1.75 after loading");
        var stray = CampaignDiplomacy.Get(c, 0, 2);
        stray.Trade = TradeTerms.Legacy();
        AgreementSystem.Tick(c);
        Check(stray.Trade is null && r.Trade is not null && Invariant(c),
            "the daily sweep gives a bare flag terms and drops terms without a flag");
    }

    private static void EveryWriterKeepsTheFlagAndTheTerms()
    {
        // One writer: no code outside AgreementSystem lifts or drops the flag by hand.
        var root = Directory.GetCurrentDirectory();
        while (root is not null && !Directory.Exists(Path.Combine(root, "Scripts", "Simulation"))) root = Path.GetDirectoryName(root);
        Check(root is not null, "the source tree is found");
        var writer = new Regex(@"\.\s*TradeAgreement\s*=(?!=)");
        var offenders = Directory.GetFiles(Path.Combine(root, "Scripts"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("AgreementsCore.cs", StringComparison.Ordinal) && !Path.GetFileName(f).StartsWith("TradeTerms", StringComparison.Ordinal))
            .Where(f => File.ReadLines(f).Any(line => writer.IsMatch(line) && !line.TrimStart().StartsWith("//")))
            .Select(Path.GetFileName).ToArray();
        Check(offenders.Length == 0, "only AgreementSystem writes the trade agreement flag (" + string.Join(", ", offenders) + ")");

        var c = World(5); // the fifth is the player's seat and stays out of it
        Quote(c.NationById(0), Commodity.Food, 0, 10); Quote(c.NationById(1), Commodity.Food, 20, 1);
        foreach (var n in c.Nations) foreach (var m in c.Nations) if (n.Id < m.Id) CampaignDiplomacy.Get(c, n.Id, m.Id).Opinion = 90f;
        // The deal table.
        var deal = new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side()) { Trade = Terms(0, 1, 0.5f, 0.5f, 5) };
        Check(Ultimatum.ResolveDeal(c, c.NationById(0), c.NationById(1), deal).Accepted && Invariant(c) &&
              CampaignDiplomacy.Peek(c, 0, 1).Trade.Years == 5, "a deal signs with its terms");
        // The button.
        var ask = new ProposeAgreementCommand { IssuerId = 0, TargetId = 2, Kind = AgreementKind.TradeAgreement };
        Apply(c, ask);
        Check(ask.Result == AgreementResult.Signed && Invariant(c) && CampaignDiplomacy.Peek(c, 0, 2).Trade.Preset == "ordinary",
            "the button signs the ordinary terms");
        // A peace treaty, a conference and an ultimatum sign the unequal treaty through the same door.
        AgreementSystem.Sign(c, CampaignDiplomacy.Get(c, 1, 3), AgreementKind.TradeAgreement,
            TradeTermsCatalog.Concession(c, c.NationById(1), c.NationById(3), 10));
        var unequal = CampaignDiplomacy.Peek(c, 1, 3).Trade;
        Check(Invariant(c) && unequal.CutOf(3, 1) == 1f && unequal.CutOf(1, 3) == 0f && unequal.Years == 10,
            "the loser of a war opens its whole market and the winner nothing, for ten years");
        // A union courts with free trade.
        var union = new ProposeAgreementCommand { IssuerId = 2, TargetId = 3, Kind = AgreementKind.TradeAgreement,
            Trade = TradeTermsCatalog.Build(c, TradeTermsCatalog.Find("free_trade"), c.NationById(2), c.NationById(3)) };
        Apply(c, union);
        Check(union.Result == AgreementResult.Signed && Invariant(c) && CampaignDiplomacy.Peek(c, 2, 3).Trade.CutA == 1f,
            "a union's courting signs free trade");
        // A sanction, a cancellation and a term running out all end it the same way.
        AgreementSystem.End(c, CampaignDiplomacy.Get(c, 2, 3), AgreementSystem.TradeEnd.Sanctioned);
        var cancel = new CancelAgreementCommand { IssuerId = 0, TargetId = 2, Kind = AgreementKind.TradeAgreement };
        Apply(c, cancel);
        Check(cancel.Cancelled && Invariant(c) && !CampaignDiplomacy.Peek(c, 2, 3).TradeAgreement && !CampaignDiplomacy.Peek(c, 0, 2).TradeAgreement,
            "a sanction and a cancellation take the terms with the flag");
        CampaignDiplomacy.Get(c, 0, 1).Opinion = -90f;
        Day(c, 365 * 5 + 1);
        AgreementSystem.Tick(c);
        Check(Invariant(c) && !CampaignDiplomacy.Peek(c, 0, 1).TradeAgreement, "an expiry takes the terms with the flag");
    }

    // ------------------------------------------------------------------ the market

    private static void ReliefMovesOnlyTheGoodsItNames()
    {
        var c = World(3);
        var buyer = c.NationById(0);
        Quote(buyer, Commodity.Food, 0, 10); Quote(buyer, Commodity.Oil, 0, 10);
        foreach (var seller in new[] { c.NationById(1), c.NationById(2) })
        {
            Quote(seller, Commodity.Food, 30, 1); Quote(seller, Commodity.Oil, 30, 1);
            CampaignDiplomacy.Get(c, 0, seller.Id).Opinion = 50f;
        }
        // Country 1 relieves... no: the buyer cuts its oil tariff for country 1 and its food tariff for country 2.
        AgreementSystem.Sign(c, CampaignDiplomacy.Get(c, 0, 1), AgreementKind.TradeAgreement, Terms(0, 1, 1f, 0f, 5, goodsA: new[] { Id(Commodity.Oil) }));
        AgreementSystem.Sign(c, CampaignDiplomacy.Get(c, 0, 2), AgreementKind.TradeAgreement, Terms(0, 2, 1f, 0f, 5, goodsA: new[] { Id(Commodity.Food) }));
        var oilWith1 = TradeNetwork.AgreementFactor(c, CampaignDiplomacy.Peek(c, 0, 1), 0, Commodity.Oil);
        var foodWith1 = TradeNetwork.AgreementFactor(c, CampaignDiplomacy.Peek(c, 0, 1), 0, Commodity.Food);
        Check(Math.Abs(oilWith1 - 2.15f) < 1e-6f && Math.Abs(foodWith1 - 1.35f) < 1e-6f,
            "a cut on oil ranks the partner 2.15 for oil and 1.35 for food (K = 1 + 0.35 + 0.80 x cut)");
        Check(Math.Abs(TradeNetwork.AgreementFactor(c, CampaignDiplomacy.Peek(c, 1, 2) ?? new DiplomaticRelation(), 1, Commodity.Oil) - 1f) < 1e-6f,
            "no agreement is K = 1");
        TradeNetwork.Route(c);
        var bought = c.TradeRoutes.Where(r => r.BuyerId == 0).ToList();
        Check(bought.Where(r => r.Commodity == Commodity.Oil).OrderByDescending(r => r.Volume).First().SellerId == 1 &&
              bought.Where(r => r.Commodity == Commodity.Food).OrderByDescending(r => r.Volume).First().SellerId == 2,
            "relief on oil moves the oil to its partner and leaves food to the one that relieved food");
        Check(buyer.Market[Commodity.Oil].Relieved > 0f && buyer.Market[Commodity.Food].Relieved > 0f,
            "each good records the part of its imports that came in relieved");
    }

    private static Campaign GuaranteeWorld(out Nation receiver, out Nation guarantor, out Nation giant, float share = 0.5f)
    {
        var c = World(3);
        receiver = c.NationById(0); guarantor = c.NationById(1); giant = c.NationById(2);
        // Rationed: ten to spare against forty-four wanted.
        Quote(receiver, Commodity.Food, 0, 4); Quote(guarantor, Commodity.Food, 11, 1); Quote(giant, Commodity.Food, 0, 40);
        foreach (var n in c.Nations) foreach (var m in c.Nations) if (n.Id < m.Id) CampaignDiplomacy.Get(c, n.Id, m.Id).Opinion = 40f;
        var t = Terms(0, 1, 1f, 0f, 5, goodsA: new[] { Id(Commodity.Food) });
        t.GuarantorId = guarantor.Id; t.GuaranteeGood = Id(Commodity.Food); t.GuaranteeShare = share;
        Check(TradeTerms.Sanitize(t, 0, 1).Length == 0, "a guarantee of half the spare food is writable");
        AgreementSystem.Sign(c, CampaignDiplomacy.Get(c, 0, 1), AgreementKind.TradeAgreement, t);
        return c;
    }

    private static float ImportsOf(Campaign c, int buyer, int seller, Commodity good) =>
        c.TradeRoutes.Where(r => r.BuyerId == buyer && r.SellerId == seller && r.Commodity == good).Sum(r => r.Volume);

    private static void AGuaranteeIsServedFirstAndWithinItsPromise()
    {
        // The same world without the guarantee: the small buyer gets its ration, not its need.
        var plain = GuaranteeWorld(out var r2, out var g2, out _);
        AgreementSystem.Sign(plain, CampaignDiplomacy.Get(plain, 0, 1), AgreementKind.TradeAgreement, Terms(0, 1, 1f, 0f, 5, goodsA: new[] { Id(Commodity.Food) }));
        TradeNetwork.Route(plain);
        var ration = ImportsOf(plain, r2.Id, g2.Id, Commodity.Food);
        Check(ration < 4f - 0.5f, $"without the guarantee the small buyer is rationed like everybody ({ration:0.00} of 4)");

        var c = GuaranteeWorld(out var receiver, out var guarantor, out var giant);
        TradeNetwork.Route(c);
        var got = ImportsOf(c, receiver.Id, guarantor.Id, Commodity.Food);
        // Its whole shortfall as the market counts it (the same deficit RunBuyers uses: the
        // shortfall times what the country can physically bring in), and nothing after it.
        Check(got > 2.5f * ration && receiver.Market[Commodity.Food].Imports <= 4f + 1e-3f,
            $"the guaranteed buyer is served ahead of a buyer ten times its size, up to its need ({got:0.00} against a ration of {ration:0.00})");
        Check(got <= 0.5f * 10f + 1e-3f, "and never more than the promised share of the spare");
        Check(ImportsOf(c, giant.Id, guarantor.Id, Commodity.Food) <= 10f - got + 1e-3f, "the queue shares only what the guarantee left");

        // A guarantor keeps what it told itself to keep: the reserve is out of the capacity first.
        var reserved = GuaranteeWorld(out var r3, out var g3, out _);
        g3.TradeSettings[TradeDesk.GoodKey(Commodity.Food, TradeDesk.ExportReserve)] = 0.8f;
        TradeNetwork.Route(reserved);
        Check(ImportsOf(reserved, r3.Id, g3.Id, Commodity.Food) <= 0.5f * 10f * 0.2f + 1e-3f,
            "the guarantor's export reserve holds: half of what it would sell, not half of what it makes");
        var t = CampaignDiplomacy.Peek(c, 0, 1).Trade;
        Check(t.Delivered >= 0.999f && t.ShortWeeks == 0, "a week kept in full leaves the record whole");
    }

    private static void AGuaranteeStopsUnderWarEmbargoAndBan()
    {
        foreach (var (name, spoil) in new (string, Action<Campaign>)[]
        {
            ("war", c => CampaignDiplomacy.Get(c, 0, 1).Stance = DiplomaticStance.AtWar),
            ("an embargo", c => CampaignDiplomacy.Get(c, 0, 1).EmbargoByB = true),
            ("a ban on the good", c => c.NationById(1).TradeSettings[TradeDesk.PartnerKey(c.NationById(0).Tag, Commodity.Food, TradeDesk.Forbid)] = 1f),
        })
        {
            var c = GuaranteeWorld(out var receiver, out var guarantor, out _);
            spoil(c);
            TradeNetwork.Route(c);
            var t = CampaignDiplomacy.Peek(c, 0, 1).Trade;
            Check(ImportsOf(c, receiver.Id, guarantor.Id, Commodity.Food) < 4f - 0.5f && t.ShortWeeks == 0 && t.Delivered >= 0.999f,
                $"no guarantee lane under {name}, and the week is not counted against the promise");
        }
    }

    private static void AGuaranteeNotKeptIsCounted()
    {
        // Three promises of half the spare from one seller: the third finds nothing left.
        var c = World(4);
        var seller = c.NationById(3);
        Quote(seller, Commodity.Oil, 11, 1);
        for (var i = 0; i < 3; i++)
        {
            Quote(c.NationById(i), Commodity.Oil, 0, 10);
            CampaignDiplomacy.Get(c, i, 3).Opinion = 40f;
            var t = Terms(i, 3, 1f, 0f, 5, goodsA: new[] { Id(Commodity.Oil) });
            t.GuarantorId = 3; t.GuaranteeGood = Id(Commodity.Oil); t.GuaranteeShare = 0.5f;
            AgreementSystem.Sign(c, CampaignDiplomacy.Get(c, i, 3), AgreementKind.TradeAgreement, t);
        }
        TradeNetwork.Route(c);
        var records = Enumerable.Range(0, 3).Select(i => CampaignDiplomacy.Peek(c, i, 3).Trade).ToArray();
        var broken = records.Single(t => t.Delivered < 0.999f);
        Check(Math.Abs(broken.Delivered - 0.75f) < 1e-4f && broken.ShortWeeks == 1,
            "a week with nothing delivered moves the record a quarter of the way (0.75) and counts a short week");
        for (var week = 0; week < 7; week++) TradeNetwork.Route(c);
        Check(broken.ShortWeeks == 8 && AgreementSystem.ExitCost(c, CampaignDiplomacy.Peek(c, Array.IndexOf(records, broken), 3),
                  Array.IndexOf(records, broken)).Free,
            "eight short weeks and the one let down may leave for free");
    }

    private static void MarkupAndDutyFollowTheFormula()
    {
        var c = World(3);
        var d = c.Defines;
        var buyer = c.NationById(0);
        buyer.TradePolicy = TradePolicy.Protectionism;
        Quote(buyer, Commodity.Food, 0, 10); Quote(c.NationById(1), Commodity.Food, 30, 1);
        CampaignDiplomacy.Get(c, 0, 1).Opinion = 50f;
        AgreementSystem.Sign(c, CampaignDiplomacy.Get(c, 0, 1), AgreementKind.TradeAgreement, Terms(0, 1, 0.5f, 0f, 5));
        TradeNetwork.Route(c);
        var q = buyer.Market[Commodity.Food];
        var fromPartner = ImportsOf(c, 0, 1, Commodity.Food);
        Check(fromPartner > 1f && Math.Abs(q.Relieved - fromPartner * 0.5f) < 1e-4f, "relieved = volume x the buyer's cut");
        var expectedMarkup = 1f + (d.ImportMarkup - 1f) * (1f - d.TradeAgreementMarkupRelief * q.Relieved / q.Imports);
        Check(Math.Abs(MarketSystem.ImportMarkupOf(d, q) - expectedMarkup) < 1e-6f && MarketSystem.ImportMarkupOf(d, q) < d.ImportMarkup,
            "markup = 1 + (ImportMarkup - 1) x (1 - relief x relieved / imports)");
        q.Price = 2f;
        Check(Math.Abs(MarketSystem.DutyOf(buyer, q) - (q.Imports - q.Relieved) * 2f * TradePolicyRules.TariffRate(TradePolicy.Protectionism)) < 1e-6f,
            "duty = (imports - relieved) x price x the importer's rate");
        var plain = new MarketQuote { Commodity = Commodity.Food, Imports = 10f, Price = 1f };
        Check(MarketSystem.ImportMarkupOf(d, plain) == d.ImportMarkup, "an import with no relief pays the whole markup");
    }

    // ------------------------------------------------------------------ the count

    private static Campaign TradingPair(out Nation x, out Nation y)
    {
        var c = World(3);
        x = c.NationById(0); y = c.NationById(1);
        Quote(x, Commodity.Food, 0, 10); Quote(x, Commodity.Oil, 30, 2);
        Quote(y, Commodity.Food, 30, 2); Quote(y, Commodity.Oil, 0, 10);
        CampaignDiplomacy.Get(c, 0, 1).Opinion = 30f;
        TradeNetwork.Route(c);
        return c;
    }

    private static void TheCountHasNoColumn()
    {
        var c = TradingPair(out var x, out var y);
        var terms = Terms(0, 1, 0.5f, 0.5f);
        var shared = Ultimatum.AssessNegotiatedDeal(c, x, y, new Ultimatum.Deal(new(), new()) { Trade = terms });
        var ours = Ultimatum.AssessNegotiatedDeal(c, x, y, new Ultimatum.Deal(new() { TradeAgreement = true }, new()));
        var theirs = Ultimatum.AssessNegotiatedDeal(c, x, y, new Ultimatum.Deal(new(), new() { TradeAgreement = true }));
        Check(shared.Valid && ours.Valid && theirs.Valid &&
              shared.ToValue == ours.ToValue && ours.ToValue == theirs.ToValue && shared.FromValue == ours.FromValue && ours.FromValue == theirs.FromValue,
            "a trade agreement written in either column, or shared, is the same points for each side");
        Check(Math.Abs(shared.ToValuation.Treaties - TradeTermsValue.For(c, y, x, terms)) < 1e-4f &&
              Math.Abs(shared.FromValuation.Treaties - TradeTermsValue.For(c, x, y, terms)) < 1e-4f,
            "the table's treaty line is the one count (TradeTermsValue) from each seat");
    }

    private static void CutsAndGuaranteesCostByCharacter()
    {
        var c = TradingPair(out var x, out var y);
        // x opens its market to y's food; y gives nothing.
        var oneSided = Terms(0, 1, 1f, 0f, 5);
        var gainer = new Breakdown(); gainer.Begin("", "");
        TradeTermsValue.For(c, y, x, oneSided, gainer);
        Check(Line(gainer, "We sell more") > 0f && Line(gainer, "Duty we forgo") == 0f,
            "a one-sided cut is a gain for the one whose goods it relieves and costs it nothing");
        float Cost(TradePolicy policy)
        {
            x.TradePolicy = policy;
            var why = new Breakdown(); why.Begin("", "");
            TradeTermsValue.For(c, x, y, oneSided, why);
            return Line(why, "Duty we forgo") + Line(why, "Our trade policy");
        }
        var open = Cost(TradePolicy.OpenMarkets);
        var closed = Cost(TradePolicy.Protectionism);
        x.TradePolicy = TradePolicy.BalancedTrade;
        Check(closed < open && closed < 0f, $"the same cut costs a protectionist more than an open market ({closed:0.0} against {open:0.0})");

        // A guarantee of food by y: it costs y more when y itself is short of food.
        var promise = Terms(0, 1, 1f, 0f, 5, goodsA: new[] { Id(Commodity.Food) });
        promise.GuarantorId = y.Id; promise.GuaranteeGood = Id(Commodity.Food); promise.GuaranteeShare = 0.25f;
        float Given()
        {
            var why = new Breakdown(); why.Begin("", "");
            TradeTermsValue.For(c, y, x, promise, why);
            return Line(why, "Our supply guarantee");
        }
        var spare = Given();
        Quote(y, Commodity.Food, 30, 40);
        var short_ = Given();
        Quote(y, Commodity.Food, 30, 2);
        Check(spare < 0f && short_ < spare - 10f, "a guarantee given costs the giver, and costs far more when it is short of the good itself");
        var received = new Breakdown(); received.Begin("", "");
        TradeTermsValue.For(c, x, y, promise, received);
        Check(Line(received, "Their supply guarantee") > 0f, "a guarantee received is worth something to the short buyer");

        // Length scales gains and costs: 2 years 0.7 / 0.8, 10 years 1.2 / 1.3.
        float LineAt(int years, string source)
        {
            var t = TradeTerms.Copy(oneSided); t.Years = years;
            var why = new Breakdown(); why.Begin("", "");
            TradeTermsValue.For(c, years == -1 ? y : x, years == -1 ? x : y, t, why);
            return Line(why, source);
        }
        var gain2 = LineAt(2, "We buy cheaper"); var gain10 = LineAt(10, "We buy cheaper");
        var cost2 = LineAt(2, "Duty we forgo") + LineAt(2, "Our trade policy");
        var cost10 = LineAt(10, "Duty we forgo") + LineAt(10, "Our trade policy");
        Check(gain2 > 0f && Math.Abs(gain10 / gain2 - 1.2f / 0.7f) < 1e-3f && cost2 < 0f && Math.Abs(cost10 / cost2 - 1.3f / 0.8f) < 1e-3f,
            "a longer term weighs both what it gains and what it costs more heavily, by the defines");
    }

    // ------------------------------------------------------------------ parity

    private static void TheSameRulesForEitherSeat()
    {
        foreach (var opinion in new[] { -60f, 0f, 25f, 80f })
        {
            var c = TradingPair(out var x, out var y);
            CampaignDiplomacy.Get(c, 0, 1).Opinion = opinion;
            var terms = Terms(0, 1, 0.5f, 0.25f, 5);
            var deal = DealAnswer.AgreementDeal(AgreementKind.TradeAgreement, terms);
            var asAi = Ultimatum.AssessNegotiatedDeal(c, x, y, deal);
            x.IsHuman = true;
            var asPlayer = Ultimatum.AssessNegotiatedDeal(c, x, y, deal);
            var verdictPlayer = DealAnswer.Decide(c, y, x, deal).Verdict;
            x.IsHuman = false;
            var verdictAi = DealAnswer.Decide(c, y, x, deal).Verdict;
            Check(asAi.ToValue == asPlayer.ToValue && asAi.FromValue == asPlayer.FromValue && verdictAi == verdictPlayer,
                $"a government answers the same terms from a player and from a computer alike (opinion {opinion})");
            var button = DealAnswer.GovernmentSignsAgreement(c, y, x, AgreementKind.TradeAgreement, terms);
            Check(button == asAi.Acceptable && button == (verdictAi == DealVerdict.Accept),
                $"the button's answer is the table's answer for the same terms (opinion {opinion})");
            var ordinary = TradeTermsCatalog.Ordinary(c, x, y);
            var table = Ultimatum.AssessNegotiatedDeal(c, x, y, new Ultimatum.Deal(new(), new()) { Trade = ordinary });
            var pressed = new ProposeAgreementCommand { IssuerId = x.Id, TargetId = y.Id, Kind = AgreementKind.TradeAgreement };
            Apply(c, pressed);
            Check((pressed.Result == AgreementResult.Signed) == table.Acceptable,
                $"pressing the button signs exactly when the table with the ordinary terms would (opinion {opinion})");
        }
    }

    private static void ARefusalHoldsByEitherRoute()
    {
        var c = TradingPair(out var x, out var y);
        CampaignDiplomacy.Get(c, 0, 1).Opinion = -80f;
        var button = new ProposeAgreementCommand { IssuerId = x.Id, TargetId = y.Id, Kind = AgreementKind.TradeAgreement };
        Apply(c, button);
        Check(button.Result == AgreementResult.Refused, "a disliked asker is refused at the button");
        Day(c, 30);
        var tradeOnly = new Ultimatum.Deal(new(), new()) { Trade = TradeTermsCatalog.Ordinary(c, x, y) };
        Check(!Ultimatum.ValidateNegotiatedDeal(c, x, y, tradeOnly, out var why) && why.Length > 0,
            "the same trade agreement cannot be put on the table instead within the pause");
        var paid = new Ultimatum.Deal(new() { Cash = 1f }, new()) { Trade = TradeTermsCatalog.Ordinary(c, x, y) };
        Check(Ultimatum.ValidateNegotiatedDeal(c, x, y, paid, out _), "money on the table makes it a different question, which may be put");

        var d = TradingPair(out var a, out var b);
        CampaignDiplomacy.Get(d, 0, 1).Opinion = -80f;
        var table = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, Trade = TradeTermsCatalog.Ordinary(d, a, b) };
        Apply(d, table);
        Day(d, 30);
        Check(!table.Outcome.Accepted && !AgreementSystem.CanPropose(d, a, b, AgreementKind.TradeAgreement, out _, preview: true),
            "a trade-only table refused buys the same pause at the button");
        Day(d, 30 + d.Defines.AgreementRefusalDays);
        Check(AgreementSystem.CanPropose(d, a, b, AgreementKind.TradeAgreement, out _, preview: true), "and the pause ends for both");
    }

    private static void TheComputerAsksAPlayerOnlyWhatTheyWouldSign()
    {
        foreach (var opinion in new[] { -80f, 80f })
        {
            var c = TradingPair(out var ai, out var player);
            CampaignDiplomacy.Get(c, 0, 1).Opinion = opinion;
            var candidates = TradeTermsComposer.Candidates(c, ai, player);
            var asGovernment = TradeTermsComposer.BestFor(c, ai, player, candidates);
            player.IsHuman = true;
            var asPlayer = TradeTermsComposer.BestFor(c, ai, player, TradeTermsComposer.Candidates(c, ai, player));
            Check((asGovernment is null) == (asPlayer is null) &&
                  (asPlayer is null || TradeTerms.SameTerms(asPlayer.Terms, asGovernment.Terms)),
                $"a player's country is asked exactly what it would be asked under the computer (opinion {opinion})");
            if (asPlayer is not null)
            {
                player.IsHuman = false;
                Check(DealAnswer.GovernmentSignsAgreement(c, player, ai, AgreementKind.TradeAgreement, asPlayer.Terms),
                    "and only what that country's government would sign");
            }
        }
    }

    private static void ATradeOnlyDealBuysNoTruce()
    {
        var c = TradingPair(out var x, out var y);
        CampaignDiplomacy.Get(c, 0, 1).Opinion = 90f;
        var relation = CampaignDiplomacy.Get(c, 0, 1);
        var before = relation.TruceUntil;
        var signed = Ultimatum.ResolveDeal(c, x, y, new Ultimatum.Deal(new(), new()) { Trade = TradeTermsCatalog.Ordinary(c, x, y) });
        Check(signed.Accepted && relation.TruceUntil == before, $"a trade agreement alone buys no protected peace ({signed.Note})");
        var d = TradingPair(out var a, out var b);
        CampaignDiplomacy.Get(d, 0, 1).Opinion = 90f;
        Check(Ultimatum.ResolveDeal(d, a, b, new Ultimatum.Deal(new() { Cash = 2f }, new()) { Trade = TradeTermsCatalog.Ordinary(d, a, b) }).Accepted &&
              CampaignDiplomacy.Peek(d, 0, 1).TruceUntil > d.ElapsedDays, "a settlement with money in it still does");
    }

    // ------------------------------------------------------------------ term and exit

    private static void TermsRenewOrLapse()
    {
        // Both still want it: renewed for the same length from today.
        var c = TradingPair(out var x, out var y);
        AgreementSystem.Sign(c, CampaignDiplomacy.Get(c, 0, 1), AgreementKind.TradeAgreement, Terms(0, 1, 0.5f, 0.5f, 2));
        CampaignDiplomacy.Get(c, 0, 1).Opinion = 90f;
        TradeNetwork.Route(c);
        Day(c, 730);
        AgreementSystem.Tick(c);
        var t = CampaignDiplomacy.Peek(c, 0, 1).Trade;
        Check(t is not null && t.SignedDay == 730 && t.EndsDay == 730 + 730 && t.Years == 2 && !t.Renewal,
            "two governments that both still want the terms renew them for the same length");
        Check(c.Chronicle.Read(0, 0, true).Any(e => e.Kind == "trade_agreement_renewed"), "the renewal is in the chronicle");

        // Neither wants it any more: it lapses, without blame.
        var d = TradingPair(out var a, out var b);
        AgreementSystem.Sign(d, CampaignDiplomacy.Get(d, 0, 1), AgreementKind.TradeAgreement, Terms(0, 1, 0.5f, 0.5f, 2));
        CampaignDiplomacy.Get(d, 0, 1).Opinion = -90f;
        Day(d, 730);
        AgreementSystem.Tick(d);
        var lapsed = CampaignDiplomacy.Peek(d, 0, 1);
        Check(!lapsed.TradeAgreement && lapsed.Trade is null && !lapsed.Reasons.Any(r => r.Id is "broke_trade_agreement" or "cancelled_trade_agreement") &&
              d.Chronicle.Read(0, 0, true).Any(e => e.Kind == "trade_agreement_expired"),
            "terms nobody wants run out on their day, recorded and unpunished");

        // The two smaller versions tried first: a smaller guarantee, then fewer goods, both for two years.
        var e = TradingPair(out var p, out var q);
        var big = Terms(0, 1, 0.5f, 0.5f, 5);
        big.GuarantorId = q.Id; big.GuaranteeGood = Id(Commodity.Food); big.GuaranteeShare = 0.4f;
        var smaller = TradeTermsLifecycle.Concessions(e, p, q, big).ToList();
        Check(smaller.Count == 2 && smaller.All(s => s.Years == 2 && s.Renewal) &&
              Math.Abs(smaller[0].GuaranteeShare - 0.3f) < 1e-4f && smaller[1].GoodsA.Length > 0 && smaller[1].GoodsB.Length > 0,
            "a lapse is first offered as a smaller guarantee, then as fewer goods, both for two years");
    }

    private static void APlayerIsSentTheRenewal()
    {
        var c = TradingPair(out var ai, out var player);
        c.ConfigureSinglePlayer(player.Id);
        CampaignDiplomacy.Get(c, 0, 1).Opinion = 90f;
        AgreementSystem.Sign(c, CampaignDiplomacy.Get(c, 0, 1), AgreementKind.TradeAgreement, Terms(0, 1, 0.5f, 0.5f, 1));
        CampaignDiplomacy.Get(c, 0, 1).Opinion = 90f;
        TradeNetwork.Route(c);
        var end = CampaignDiplomacy.Peek(c, 0, 1).Trade.EndsDay;
        Day(c, end - c.Defines.TradeRenewalNoticeDays - 1);
        AgreementSystem.Tick(c);
        Check(!c.DealOffers.Any(), "no renewal paper before the notice");
        Day(c, end - c.Defines.TradeRenewalNoticeDays);
        AgreementSystem.Tick(c);
        var paper = c.DealOffers.SingleOrDefault(o => o.Pending && o.FromId == ai.Id && o.ToId == player.Id);
        var held = CampaignDiplomacy.Peek(c, 0, 1);
        Check(paper is { Trade.Renewal: true }, "the computer side sends the same terms as a renewal paper thirty days ahead" +
            $" (offers {c.DealOffers.Count}, offered day {held.Trade?.RenewalOfferedDay}, worth to it " +
            $"{TradeTermsValue.For(c, ai, player, TradeTermsLifecycle.RenewalOf(held.Trade)):0.0}, " +
            $"valid {Ultimatum.ValidateNegotiatedDeal(c, ai, player, new Ultimatum.Deal(new(), new()) { Trade = TradeTermsLifecycle.RenewalOf(held.Trade) }, out var why)} {why})");
        AgreementSystem.Tick(c);
        Check(c.DealOffers.Count(o => o.FromId == ai.Id) == 1, "and sends it once");
        Day(c, end);
        AgreementSystem.Tick(c);
        Check(!CampaignDiplomacy.Peek(c, 0, 1).TradeAgreement, "an unanswered renewal lets the term lapse on its day");

        // With a standing answer the renewal is signed on delivery.
        var d = TradingPair(out var ai2, out var player2);
        d.ConfigureSinglePlayer(player2.Id);
        Apply(d, new SetAutoAnswerCommand { IssuerId = player2.Id, Kind = "trade_agreement", Mode = AutoAnswerMode.Beneficial });
        AgreementSystem.Sign(d, CampaignDiplomacy.Get(d, 0, 1), AgreementKind.TradeAgreement, Terms(0, 1, 0.5f, 0.5f, 1));
        CampaignDiplomacy.Get(d, 0, 1).Opinion = 95f;
        TradeNetwork.Route(d);
        var end2 = CampaignDiplomacy.Peek(d, 0, 1).Trade.EndsDay;
        Day(d, end2 - d.Defines.TradeRenewalNoticeDays);
        AgreementSystem.Tick(d);
        var renewed = CampaignDiplomacy.Peek(d, 0, 1).Trade;
        Check(renewed.SignedDay == end2 - d.Defines.TradeRenewalNoticeDays && renewed.EndsDay > end2,
            "a player's standing answer signs the renewal as it signs any paper");
    }

    private static void LeavingCostsByWhatIsLeft()
    {
        var c = TradingPair(out var x, out var y);
        var relation = CampaignDiplomacy.Get(c, 0, 1);
        AgreementSystem.Sign(c, relation, AgreementKind.TradeAgreement, Terms(0, 1, 0.5f, 0.5f, 10));
        Day(c, 365 * 5);
        var half = AgreementSystem.ExitCost(c, relation, x.Id);
        Day(c, 365 * 9 + 300);
        var late = AgreementSystem.ExitCost(c, relation, x.Id);
        Check(!half.Free && half.Opinion == "broke_trade_agreement" && Math.Abs(half.Scale - 0.5f) < 0.01f &&
              Math.Abs(late.Scale - c.Defines.TradeAgreementBreakOpinionMin) < 1e-4f,
            "leaving a term early is remembered by the share of it left, never below the floor");
        var forever = TradingPair(out var f1, out _);
        AgreementSystem.Sign(forever, CampaignDiplomacy.Get(forever, 0, 1), AgreementKind.TradeAgreement, Terms(0, 1, 0.5f, 0.5f));
        Check(AgreementSystem.ExitCost(forever, CampaignDiplomacy.Peek(forever, 0, 1), f1.Id) is { Free: false, Opinion: "cancelled_trade_agreement", Scale: 1f },
            "an agreement with no end costs what cancelling always cost");

        // The receiver of a guarantee not kept leaves free; a guarantor walking out on a short partner pays in name.
        var g = GuaranteeWorld(out var receiver, out var guarantor, out _);
        var held = CampaignDiplomacy.Get(g, 0, 1);
        held.Trade.ShortWeeks = g.Defines.TradeGuaranteeFreeExitWeeks;
        Apply(g, new CancelAgreementCommand { IssuerId = receiver.Id, TargetId = guarantor.Id, Kind = AgreementKind.TradeAgreement });
        Check(!held.TradeAgreement && !held.Reasons.Any(r => r.Id is "broke_trade_agreement" or "cancelled_trade_agreement"),
            "the one let down for eight weeks leaves without blame");
        var h = GuaranteeWorld(out var receiver2, out var guarantor2, out _);
        receiver2.Market[Commodity.Food].Satisfaction = 0.5f;
        var name = guarantor2.Notoriety;
        Apply(h, new CancelAgreementCommand { IssuerId = guarantor2.Id, TargetId = receiver2.Id, Kind = AgreementKind.TradeAgreement });
        Check(Math.Abs(guarantor2.Notoriety - name - h.Defines.TradeGuaranteeBreakNotoriety) < 1e-4f &&
              CampaignDiplomacy.Peek(h, 0, 1).Reasons.Any(r => r.Id == "broke_trade_agreement"),
            "a guarantor that walks out while its partner is short pays the breach and the notoriety");
    }

    // ------------------------------------------------------------------ persistence and determinism

    private static void TermsTravelAndPersist()
    {
        var terms = Terms(3, 7, 0.75f, 0.25f, 5, goodsA: new[] { Id(Commodity.Oil) });
        terms.GuarantorId = 7; terms.GuaranteeGood = Id(Commodity.Food); terms.GuaranteeShare = 0.2f;
        foreach (ICampaignCommand command in new ICampaignCommand[]
        {
            new ProposeAgreementCommand { IssuerId = 3, TargetId = 7, Kind = AgreementKind.TradeAgreement, Trade = terms },
            new ProposeDealCommand { IssuerId = 3, ToId = 7, FromCash = 1f, Trade = terms }
        })
        {
            ICampaignCommand back = null;
            Check(NetworkCommandCodec.TryEncode(command, out var type, out var json) &&
                  NetworkCommandCodec.TryDecode(type, json, out back), command.GetType().Name + " encodes and decodes");
            var decoded = back is ProposeAgreementCommand pa ? pa.Trade : ((ProposeDealCommand)back).Trade;
            Check(TradeTerms.SameTerms(decoded, terms), command.GetType().Name + " carries its terms across the wire");
        }

        var c = GuaranteeWorld(out _, out _, out _);
        TradeNetwork.Route(c);
        var live = CampaignDiplomacy.Peek(c, 0, 1).Trade;
        live.Delivered = 0.6f; live.ShortWeeks = 3;
        Day(c, 400);
        var paper = new ProposeDealCommand { IssuerId = 0, ToId = 2, FromCash = 1f, Trade = Terms(0, 2, 0.5f, 0.5f, 2) };
        Apply(c, paper);
        Check(c.DealOffers.Any(o => o.Trade is not null), "a paper to a player carries its trade terms");
        var fingerprint = CampaignFingerprint.Of(c);
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "trade-terms"), "trade-terms");
        Check(loaded.Success, "a world mid-term saves and loads");
        var back2 = CampaignDiplomacy.Peek(loaded.Campaign, 0, 1).Trade;
        Check(TradeTerms.SameTerms(back2, live) && back2.Delivered == 0.6f && back2.ShortWeeks == 3 &&
              back2.SignedDay == live.SignedDay && back2.EndsDay == live.EndsDay,
            "the terms, the delivery record and the short weeks round-trip");
        Check(loaded.Campaign.NationById(0).Market[Commodity.Food].Relieved == c.NationById(0).Market[Commodity.Food].Relieved &&
              c.NationById(0).Market[Commodity.Food].Relieved > 0f, "the week's relieved imports round-trip");
        Check(TradeTerms.SameTerms(loaded.Campaign.DealOffers.Single(o => o.Trade is not null).Trade,
                  c.DealOffers.Single(o => o.Trade is not null).Trade), "the paper's terms round-trip");
        Check(CampaignFingerprint.Of(loaded.Campaign) == fingerprint, "and the fingerprint is the same after loading");
        live.CutA = 0.25f;
        Check(CampaignFingerprint.Of(c) != fingerprint, "a changed cut changes the fingerprint");
        live.CutA = 1f;
        Check(CampaignFingerprint.Of(c) == fingerprint, "and changing it back restores it");

        // The same world twice: the same routes, the same records, the same fingerprint.
        ulong Run()
        {
            var w = GuaranteeWorld(out _, out _, out _);
            for (var week = 0; week < 4; week++) { TradeNetwork.Route(w); AgreementSystem.Tick(w); }
            return CampaignFingerprint.Of(w);
        }
        Check(Run() == Run(), "the same world routed twice gives the same fingerprint");
    }
}
