using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

static class SupplyHedgeTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("supply hedge: " + name);
        Console.WriteLine("PASS supply hedge: " + name);
    }

    public static void Run()
    {
        DefaultsAddNoFingerprintBytes();
        TheIndexMatchesAScanAndReadingDoesNotRebuildIt();
        TheShortageNamesTheSameSupplierAsTheExposureCard();
        AnAiSignsWithTheOtherSupplier();
        APlayerKeepsTheLandUntilTheySign();
        TheRuleCanBeSwitchedOff();
    }

    static Campaign World(out Nation buyer, out Nation monopolist, out Nation alternate)
    {
        var campaign = new Campaign(21);
        buyer = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "BUY", Name = "Buyer", Population = 2_000_000,
            Stability = 0.8f, Gdp = 100f, Treasury = 80f,
            CapitalLatitude = 52f, CapitalLongitude = 13f
        };
        monopolist = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "MON", Name = "Monopolist", Population = 2_000_000,
            Stability = 0.8f, Gdp = 100f, Treasury = 80f,
            CapitalLatitude = 52.4f, CapitalLongitude = 13.2f
        };
        alternate = new Nation
        {
            Id = 2, MapIndex = 3, Tag = "ALT", Name = "Alternate", Population = 2_000_000,
            Stability = 0.8f, Gdp = 100f, Treasury = 80f,
            CapitalLatitude = -33f, CapitalLongitude = 151f
        };
        campaign.Nations.Add(buyer);
        campaign.Nations.Add(monopolist);
        campaign.Nations.Add(alternate);
        Check(buyer.Id == 0 && campaign.Nations[0] == buyer, "nation id 0 stays at index 0");
        buyer.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Production = 0f, Demand = 10f, Price = 1f };
        monopolist.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Production = 40f, Demand = 1f, Price = 1f };
        alternate.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Production = 15f, Demand = 1f, Price = 1f };
        var bond = CampaignDiplomacy.Get(campaign, buyer.Id, monopolist.Id);
        bond.TradeAgreement = true;
        bond.Opinion = 80f;
        bond.LastActionDay = -30;
        var warm = CampaignDiplomacy.Get(campaign, buyer.Id, alternate.Id);
        warm.Opinion = 50f;
        warm.LastActionDay = -30;
        var states = new ushort[] { 9001, 9002, 9003, 9001 };
        var owners = new ushort[] { 1, 2, 3, 1 };
        var map = ProvinceTopology.Build(states, owners, 2, 2, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap)).SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController)).SetValue(campaign, (ushort[])map.ProvinceToNation.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation)).SetValue(campaign, new float[map.ProvinceCount + 1]);
        Check(campaign.HasOwnedLand(buyer) && campaign.HasOwnedLand(monopolist) && campaign.HasOwnedLand(alternate),
            "each country keeps a province of its own");
        return campaign;
    }

    static void DefaultsAddNoFingerprintBytes()
    {
        var left = new Campaign(3);
        var right = new Campaign(3);
        Check(left.Defines.AiSupplyHedges && left.Defines.AiSupplyHedgeShare == 0.75f &&
            left.Defines.AiSupplyHedgeSatisfaction == 0.875f && left.Defines.AiSupplyHedgeRetryDays == 180,
            "the hedge numbers load as the compiled defaults");
        var before = CampaignFingerprint.Of(left);
        Check(before == CampaignFingerprint.Of(right), "two untouched worlds still match");
        left.Defines.AiSupplyHedges = false;
        Check(CampaignFingerprint.Of(left) != before, "switching the hedge off is visible to peers");
        left.Defines.AiSupplyHedges = true;
        Check(CampaignFingerprint.Of(left) == before, "the default hedge rule adds no fingerprint bytes");
    }

    static void TheIndexMatchesAScanAndReadingDoesNotRebuildIt()
    {
        var campaign = World(out var buyer, out var monopolist, out _);
        var builds = TradeNetwork.ImportIndexBuilds;
        TradeNetwork.Route(campaign);
        Check(TradeNetwork.ImportIndexBuilds == builds + 1, "one finished market week files the routes once");
        Check(TradeNetwork.IndexReady(campaign), "the index is ready after the week closes");
        var filed = TradeNetwork.RoutesTouching(campaign, buyer.Id);
        var scanned = campaign.TradeRoutes.Where(route => route.SellerId == buyer.Id || route.BuyerId == buyer.Id).ToArray();
        Check(filed.Count == scanned.Length, "the filed list has the same routes a full scan would find");
        for (var index = 0; index < scanned.Length; index++)
            Check(ReferenceEquals(filed[index], scanned[index]), "filed route " + index + " is the same record as the scan");
        Check(filed.Count > 0 && filed.All(route => route.SellerId == monopolist.Id),
            "the buyer's food comes from the near partner with the trade agreement");
        var other = new Campaign(22);
        other.Nations.Add(new Nation { Id = 0, MapIndex = 1, Tag = "ISO_A", Name = "A", Population = 1_000_000, Treasury = 10f });
        other.Nations.Add(new Nation { Id = 1, MapIndex = 2, Tag = "ISO_B", Name = "B", Population = 1_000_000, Treasury = 10f });
        other.Nations[0].Market[Commodity.Coal] = new MarketQuote { Commodity = Commodity.Coal, Production = 20f, Demand = 1f, Price = 1f };
        other.Nations[1].Market[Commodity.Coal] = new MarketQuote { Commodity = Commodity.Coal, Production = 0f, Demand = 10f, Price = 1f };
        TradeNetwork.Route(other);
        var still = TradeNetwork.RoutesTouching(campaign, buyer.Id);
        Check(still.Count == filed.Count && ReferenceEquals(still[0], filed[0]) && still[0].SellerId == monopolist.Id,
            "another market does not rewrite this country's filed routes");
        var after = TradeNetwork.RouteScans;
        for (var pass = 0; pass < 8; pass++)
        {
            _ = TradeNetwork.RoutesTouching(campaign, buyer.Id);
            _ = EconomicExposure.Read(campaign, buyer);
            _ = EconomicExposure.Cause(campaign, buyer, Commodity.Food);
        }
        Check(TradeNetwork.RouteScans == after, "reading the filed routes does not walk the world list");
        Check(TradeNetwork.ImportIndexBuilds == builds + 2, "those reads do not file the week again");
    }

    static void TheShortageNamesTheSameSupplierAsTheExposureCard()
    {
        var campaign = World(out var buyer, out var monopolist, out _);
        TradeNetwork.Route(campaign);
        var quote = buyer.Market[Commodity.Food];
        quote.Satisfaction = quote.Demand <= 0.001f ? 1f : Math.Clamp((quote.Production + quote.Imports) / quote.Demand, 0f, 1f);
        var row = EconomicExposure.Read(campaign, buyer).Single(item => item.Good == Commodity.Food);
        Check(row.Suppliers[0].NationId == monopolist.Id && row.LargestImportShare + 0.0001f >= 0.75f,
            "the exposure card's largest supplier is the monopolist (" + row.LargestImportShare.ToString("0.00") + ")");
        var language = Loc.Language;
        var before = CampaignFingerprint.Of(campaign);
        try
        {
            Loc.Language = "english";
            var expected = Loc.Format("{0} supplies {1:P0} of the imports of this good.", monopolist.DisplayName, row.LargestImportShare);
            Check(EconomicExposure.Cause(campaign, buyer, Commodity.Food) == expected,
                "the cause uses the exposure card's supplier and share");
            quote.Satisfaction = 0.4f;
            var item = SituationReport.Read(campaign, buyer).Single(entry => entry.Id == "shortage:Food");
            Check(item.Causes.Length == 2 && item.Causes[1] == expected && item.Facts.Length == 4,
                "the situation room names that supplier and keeps the same facts");
            Check(!item.Causes[1].Contains("trade bottleneck", StringComparison.Ordinal),
                "the long undifferentiated shortage sentence is gone");
            Loc.Language = "russian";
            var russian = EconomicExposure.Cause(campaign, buyer, Commodity.Food);
            Check(russian.Contains(monopolist.DisplayName, StringComparison.Ordinal) &&
                russian.Contains("импорта", StringComparison.Ordinal),
                "the Russian cause names the supplier");
            var again = SituationReport.Read(campaign, buyer).Single(entry => entry.Id == "shortage:Food");
            Check(again.Causes[1] == russian, "the situation room uses that same Russian sentence");
        }
        finally { Loc.Language = language; }
        Check(CampaignFingerprint.Of(campaign) == before, "reading the shortage does not change the fingerprint");
    }

    static void AnAiSignsWithTheOtherSupplier()
    {
        var campaign = World(out var buyer, out var monopolist, out var alternate);
        TradeNetwork.Route(campaign);
        var quote = buyer.Market[Commodity.Food];
        quote.Satisfaction = 0.4f;
        var owners = (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
        var cash = buyer.Treasury + monopolist.Treasury + alternate.Treasury;
        Check(AiStatecraft.TryHedgeSupply(campaign, buyer), "the short buyer asks for a second supplier");
        var withAlternate = CampaignDiplomacy.Peek(campaign, buyer.Id, alternate.Id);
        var withMonopolist = CampaignDiplomacy.Peek(campaign, buyer.Id, monopolist.Id);
        Check(withAlternate?.TradeAgreement == true, "the agreement is with the other supplier");
        Check(withMonopolist?.TradeAgreement == true, "the existing agreement with the monopolist stays");
        Check(campaign.DealOffers.Count == 0, "two governments settle the agreement without a leftover paper");
        Check(campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners), "no province changes hands");
        Check(buyer.Treasury + monopolist.Treasury + alternate.Treasury == cash, "no cash moves");
        Check(!AiStatecraft.TryHedgeSupply(campaign, buyer), "the same good is not hedged again inside the pause");
        Check(buyer.DecisionAvailableDay["supply_hedge/" + (int)Commodity.Food] > campaign.ElapsedDays,
            "the pause is on the book");
        var plain = World(out var quiet, out _, out _);
        TradeNetwork.Route(plain);
        plain.NationById(0).Market[Commodity.Food].Satisfaction = 0.4f;
        var hashed = CampaignFingerprint.Of(plain);
        quiet.DecisionAvailableDay["supply_hedge/" + (int)Commodity.Food] = 180;
        Check(CampaignFingerprint.Of(plain) != hashed, "a hedge clock is part of the fingerprint");
    }

    static void APlayerKeepsTheLandUntilTheySign()
    {
        var campaign = World(out var buyer, out _, out var alternate);
        alternate.IsHuman = true;
        TradeNetwork.Route(campaign);
        buyer.Market[Commodity.Food].Satisfaction = 0.4f;
        var owners = (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
        var buyerCash = buyer.Treasury;
        var alternateCash = alternate.Treasury;
        Check(AiStatecraft.TryHedgeSupply(campaign, buyer), "the paper is offered to the player who can sell");
        var paper = campaign.DealOffers.Single(offer => offer.Pending && offer.ToId == alternate.Id);
        Check(paper.From.TradeAgreement && !paper.To.TradeAgreement && paper.From.Cash == 0f && paper.To.Cash == 0f &&
            (paper.From.Regions?.Length ?? 0) == 0 && (paper.To.Regions?.Length ?? 0) == 0 &&
            paper.FromProvinces.Length == 0 && paper.ToProvinces.Length == 0,
            "the paper is a trade agreement and no land");
        Check(CampaignDiplomacy.Peek(campaign, buyer.Id, alternate.Id)?.TradeAgreement != true,
            "nothing is signed before the player agrees");
        Check(buyer.Treasury == buyerCash && alternate.Treasury == alternateCash, "treasuries wait for the signature");
        Check(campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners), "the player's provinces stay");
        var signed = DealNegotiations.Respond(campaign, alternate.Id, paper.Id, DealReply.Accept);
        Check(signed.Accepted, "the player can sign the agreement (" + signed.Note + ")");
        Check(CampaignDiplomacy.Peek(campaign, buyer.Id, alternate.Id)?.TradeAgreement == true, "signing writes the agreement");
        Check(buyer.Treasury == buyerCash && alternate.Treasury == alternateCash, "signing the agreement moves no cash");
        Check(campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners), "signing the agreement moves no land");
    }

    static void TheRuleCanBeSwitchedOff()
    {
        var campaign = World(out var buyer, out _, out var alternate);
        campaign.Defines.AiSupplyHedges = false;
        TradeNetwork.Route(campaign);
        buyer.Market[Commodity.Food].Satisfaction = 0.4f;
        Check(!AiStatecraft.TryHedgeSupply(campaign, buyer), "the switch leaves the week alone");
        Check(CampaignDiplomacy.Peek(campaign, buyer.Id, alternate.Id)?.TradeAgreement != true, "no agreement is written");
        Check(campaign.DealOffers.Count == 0, "no paper is sent");
    }
}
