using System;
using System.Linq;
using DealOrForce.Simulation;

static class StrategicReportsTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS reports " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(222);
        var nation = c.NationByTag("KOR"); var other = c.NationByTag("JPN");
        c.ScenarioId = "reports_test";
        var state = c.States.First(s => s.NationMapIndex == nation.MapIndex);
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)state.MapIndex, 128).ToArray(),
            Enumerable.Repeat((ushort)nation.MapIndex, 128).ToArray(), 16, 8, _ => false));
        c.ConfigureSinglePlayer(nation.Id);
        new SetEmbargoCommand { IssuerId = nation.Id, TargetId = other.Id }.Apply(c);
        new SetEmbargoCommand { IssuerId = nation.Id, TargetId = other.Id }.Apply(c);
        Check(c.Chronicle.Entries.Count == 1, "successful embargo recorded once, no-op not recorded");
        new SetEmbargoCommand { IssuerId = nation.Id, TargetId = other.Id, Embargo = false }.Apply(c);
        Check(c.Chronicle.Entries.Last().Kind == "embargo_ended", "lifting embargo recorded");
        c.Chronicle.Record(c, "decision_taken", nation.Id, subject: "private", isPublic: false);
        Check(c.Chronicle.Read(nation.Id, nation.Id, false).Length == 3 &&
            c.Chronicle.Read(other.Id, nation.Id, false).Length == 2 &&
            c.Chronicle.Read(nation.Id, nation.Id, true).Length == 2, "country/world privacy filters");
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "reports"), "reports");
        Check(saved.Campaign is not null && saved.Campaign.Chronicle.Entries.SequenceEqual(c.Chronicle.Entries), "chronicle survives network/save payload roundtrip");
        saved.Campaign.Chronicle.Record(saved.Campaign, "test", nation.Id);
        Check(saved.Campaign.Chronicle.Entries.Last().Sequence > c.Chronicle.Entries.Last().Sequence, "sequence continues after restore");
        Check(new Campaign(222).Chronicle.Entries.Count == 0, "new worlds have no inherited chronicle");
        for (var i = 0; i < WorldChronicle.Capacity + 5; i++) c.Chronicle.Record(c, "test", nation.Id);
        Check(c.Chronicle.Entries.Count == WorldChronicle.Capacity, "retention bounded");

        nation.Market.Clear(); c.TradeRoutes.Clear(); c.TradeRoutesDay = 0;
        nation.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Demand = 100, Imports = 70, Satisfaction = .9f };
        c.TradeRoutes.Add(new TradeRoute { BuyerId = nation.Id, SellerId = other.Id, Commodity = Commodity.Food, Volume = 30 });
        c.TradeRoutes.Add(new TradeRoute { BuyerId = nation.Id, SellerId = other.Id, Commodity = Commodity.Food, Volume = 20 });
        // The opposite direction must not count as an import.
        c.TradeRoutes.Add(new TradeRoute { BuyerId = other.Id, SellerId = nation.Id, Commodity = Commodity.Food, Volume = 100 });
        var before = CampaignFingerprint.Of(c);
        var row = EconomicExposure.Read(c, nation).Single();
        Check(row.ImportShare == .7f && row.LargestDemandShare == .5f && row.UnattributedImports == 20 && row.Suppliers.Length == 1,
            "imports, concentration, aggregation and unattributed supply use physical routes");
        Check(CampaignFingerprint.Of(c) == before, "reports do not alter simulation");
        c.TradeRoutes[0].Volume = 1;
        Check(row.Suppliers[0].Volume == 50, "supplier snapshot detached from reused route objects");
        c.TradeRoutesDay = -1;
        row = EconomicExposure.Read(c, nation).Single();
        Check(row.Suppliers.Length == 0 && row.UnattributedImports == 70, "unavailable route attribution is never invented");
        nation.Market[Commodity.Food].Demand = 0;
        Check(EconomicExposure.Read(c, nation).Length == 0, "zero demand cannot divide by zero");
    }
}
