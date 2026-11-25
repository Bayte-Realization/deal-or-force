using System;
using System.Linq;
using DealOrForce.Simulation;

static class CampaignIsolationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("campaign isolation: " + note); Console.WriteLine("PASS campaign isolation: " + note); }

    static Campaign World(bool mapped, Commodity good)
    {
        var c = new Campaign(20261018);
        c.AddNation(new Nation { Tag = "ISO_A", MapIndex = 501, Population = 1000000 });
        c.AddNation(new Nation { Tag = "ISO_B", MapIndex = 502, Population = 1000000 });
        c.States.Add(new ScenarioState { Id = "iso_a", MapIndex = 3001, NationMapIndex = 501, Population = 1000000 });
        c.States.Add(new ScenarioState { Id = "iso_b", MapIndex = 3002, NationMapIndex = 502, Population = 1000000 });
        c.InvalidateStateIndex();
        if (mapped)
        {
            const int width = 8, height = 3;
            var regions = new ushort[width * height]; var owners = new ushort[regions.Length];
            regions[width + 2] = 3001; regions[width + 3] = 3002;
            owners[width + 2] = 501; owners[width + 3] = 502;
            c.AttachProvinceTopology(ProvinceTopology.Build(regions,owners,width,height,_=>false));
        }
        c.Nations[0].Market[good] = new MarketQuote { Commodity = good, Production = 20, Demand = 1, Price = 1 };
        c.Nations[1].Market[good] = new MarketQuote { Commodity = good, Production = 0, Demand = 10, Price = 1 };
        return c;
    }

    public static void Run()
    {
        var mapped = World(true,Commodity.Food);
        Check(CampaignDiplomacy.SharesLandBorder(mapped,501,502), "the mapped world's two countries really touch");
        var bare = World(false,Commodity.Food);
        Check(!CampaignDiplomacy.SharesLandBorder(bare,501,502),
            "a world still bootstrapping without a map does not borrow another world's borders");
        Check(CampaignDiplomacy.SharesLandBorder(mapped,501,502),
            "asking the mapless world does not destroy the mapped world's border answer");
        TradeNetwork.Route(mapped);
        var original = mapped.TradeRoutes.Single();
        var value = (original.Commodity, original.Volume, original.Value);
        var second = World(false,Commodity.Coal);
        TradeNetwork.Route(second);
        Check(!ReferenceEquals(original,second.TradeRoutes.Single()),
            "different campaigns never share mutable route records");
        Check((original.Commodity,original.Volume,original.Value) == value,
            "clearing another market cannot silently change an already published route");
        TradeNetwork.Route(mapped);
        Check(ReferenceEquals(original,mapped.TradeRoutes.Single()),
            "one campaign still reuses its own route record instead of allocating every week");

        var eagerA = World(false,Commodity.Food); var eagerB = World(false,Commodity.Coal);
        eagerB.States[0].Buildings[BuildingKind.Infrastructure] = 10;
        eagerB.States[1].Buildings[BuildingKind.Infrastructure] = 10;
        TradeNetwork.Route(eagerA); var expectedA = Routes(eagerA);
        var reachA = TradeNetwork.ReachOf(eagerA,eagerA.Nations[0]);
        TradeNetwork.Route(eagerB); var expectedB = Routes(eagerB);
        Check(TradeNetwork.ReachOf(eagerA,eagerA.Nations[0]) == reachA &&
            TradeNetwork.ReachOf(eagerB,eagerB.Nations[0]) > reachA,
            "reading transport reach uses this campaign even after another market cleared");
        var stagedA = World(false,Commodity.Food); var stagedB = World(false,Commodity.Coal);
        stagedB.States[0].Buildings[BuildingKind.Infrastructure] = 10;
        stagedB.States[1].Buildings[BuildingKind.Infrastructure] = 10;
        using var first = TradeNetwork.RouteStages(stagedA).GetEnumerator();
        using var next = TradeNetwork.RouteStages(stagedB).GetEnumerator();
        var moreA = true; var moreB = true; var steps = 0;
        while (moreA || moreB)
        {
            if (++steps > 200) throw new Exception("Trade stages did not finish");
            if (moreA) moreA = first.MoveNext();
            if (moreB) moreB = next.MoveNext();
        }
        Check(Routes(stagedA) == expectedA && Routes(stagedB) == expectedB,
            "two markets alternating at commodity boundaries match their eager routes and amounts");
        Check(stagedA.TradeRoutesDay == stagedA.ElapsedDays && stagedB.TradeRoutesDay == stagedB.ElapsedDays,
            "each staged market publishes completion in its own world");
        var resumed = World(false,Commodity.Food);
        using var paused = TradeNetwork.RouteStages(resumed).GetEnumerator();
        Check(paused.MoveNext(), "a market can suspend after preparation");
        TradeNetwork.Route(eagerB);
        while (paused.MoveNext()) { }
        Check(Routes(resumed) == expectedA,
            "a whole second market between frames cannot overwrite the paused world's prepared reach");

        BuyerSlotsFollowDemand();
        ReturningBuyerSeesNewSupplier();
    }

    static void BuyerSlotsFollowDemand()
    {
        var campaign = World(false, Commodity.Food);
        var seller = campaign.Nations[0];
        var firstBuyer = campaign.Nations[1];
        for (var i = 0; i < 12; i++)
            campaign.AddNation(new Nation { Tag = $"ISO_P{i}", MapIndex = 503 + i, Population = 100_000 });
        var secondBuyer = campaign.AddNation(new Nation { Tag = "ISO_LATE", MapIndex = 515, Population = 1_000_000 });
        CampaignDiplomacy.Get(campaign, seller.Id, firstBuyer.Id).EmbargoByA = true;
        TradeNetwork.Route(campaign);
        Check(campaign.TradeRoutes.Count == 0, "an embargo blocks the original buyer's market row");

        firstBuyer.Market[Commodity.Food].Demand = 0;
        secondBuyer.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Demand = 10, Price = 1 };
        TradeNetwork.Route(campaign);
        Check(campaign.TradeRoutes.Count == 1 && campaign.TradeRoutes[0].BuyerId == secondBuyer.Id,
            "the new buyer reuses a market row without inheriting the old buyer's embargo");

        firstBuyer.Market[Commodity.Food].Demand = 10;
        TradeNetwork.Route(campaign);
        Check(campaign.TradeRoutes.Count == 1 && campaign.TradeRoutes[0].BuyerId == secondBuyer.Id,
            "two buyers with distant IDs keep their own embargo and access");
    }

    static void ReturningBuyerSeesNewSupplier()
    {
        var campaign = World(false, Commodity.Food);
        var originalSeller = campaign.Nations[0];
        var buyer = campaign.Nations[1];
        originalSeller.CapitalLongitude = 45f;
        TradeNetwork.Route(campaign);
        Check(campaign.TradeRoutes.Single().SellerId == originalSeller.Id, "the only exporter supplies the buyer");

        var quote = buyer.Market[Commodity.Food];
        buyer.Market.Clear();
        var closerSeller = campaign.AddNation(new Nation
        {
            Tag = "ISO_NEAR", MapIndex = 503, Population = 1_000_000, CapitalLongitude = 1f
        });
        campaign.States.Add(new ScenarioState { Id = "iso_near", MapIndex = 3003, NationMapIndex = closerSeller.MapIndex, Population = 1_000_000 });
        campaign.InvalidateStateIndex();
        closerSeller.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Production = 20, Demand = 1, Price = 1 };
        TradeNetwork.Route(campaign);
        Check(campaign.TradeRoutes.Count == 0, "the buyer is absent during the new exporter's first market");

        buyer.Market[Commodity.Food] = quote;
        TradeNetwork.Route(campaign);
        Check(campaign.TradeRoutes.Single().SellerId == closerSeller.Id,
            "the returning buyer uses real distances to exporters who appeared in its absence");
    }

    static string Routes(Campaign c) => string.Join(";",c.TradeRoutes.Select(r =>
        $"{r.SellerId}/{r.BuyerId}/{r.Commodity}/{r.Volume:R}/{r.Value:R}"));
}
