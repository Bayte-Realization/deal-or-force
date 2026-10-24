using DealOrForce.Simulation;

/// <summary>
/// The trade screen's three strategic lines name a seller, or say there is none.
/// Reading them does not route the week or spend the depot.
/// </summary>
public static class StrategicCoverTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new System.Exception("FAIL strategic cover: " + text);
        System.Console.WriteLine("PASS strategic cover: " + text);
    }

    public static void Run()
    {
        var language = Loc.Language;
        try
        {
            Loc.Language = "english";
            var campaign = new Campaign(4404) { ScenarioId = "strategic_cover" };
            campaign.Nations.Add(new Nation
            {
                Id = 0, Tag = "SC0", Name = "Buyer", MapIndex = 1, Population = 1000000,
                IsHuman = true, StockpilesSeeded = true
            });
            campaign.Nations.Add(new Nation
            {
                Id = 1, Tag = "SC1", Name = "Harbour", MapIndex = 2, Population = 1000000
            });
            Check(campaign.Nations[0].Id == 0 && campaign.Nations[1].Id == 1,
                "nation id 0 stands at index 0");
            campaign.TradeRoutes.Add(new TradeRoute
            {
                SellerId = 1, BuyerId = 0, Commodity = Commodity.Oil, Volume = 8f, Value = 3f
            });
            campaign.Nations[0].Market[Commodity.Oil] = new MarketQuote
            {
                Commodity = Commodity.Oil, Production = 2f, Imports = 8f, Demand = 10f
            };
            campaign.Nations[0].Stockpiles["Oil"] = 20f;
            campaign.Nations[0].DailyStockpileBurn["Oil"] = 4f;
            campaign.Nations[0].Stockpiles["Arms"] = 0f;
            campaign.Nations[0].DailyStockpileBurn["Arms"] = 1f;

            var routes = campaign.TradeRoutes.Count;
            var oil = campaign.Nations[0].Stockpiles["Oil"];
            var hash = CampaignFingerprint.Of(campaign);
            var lines = CommodityLeverage.CoverLines(campaign, campaign.Nations[0]);
            Check(lines.Length == 3, "oil, arms and electronics each have a line");
            Check(lines[0].Contains("Harbour") && lines[0].Contains("80%") &&
                  lines[0].Contains("The depot covers 5 days."),
                "oil names its seller, the import share and the days of cover: " + lines[0]);
            Check(lines[1].Contains("Nobody is selling it.") && lines[1].Contains("The depot covers 0 days."),
                "arms with no route says nobody is selling it: " + lines[1]);
            Check(lines[2].Contains("Nobody is selling it.") && lines[2].Contains("The depot is not drawing it."),
                "electronics with no draw says the depot is not drawing it: " + lines[2]);
            Check(CampaignFingerprint.Of(campaign) == hash &&
                  campaign.TradeRoutes.Count == routes &&
                  campaign.Nations[0].Stockpiles["Oil"] == oil &&
                  campaign.Nations[0].DailyStockpileBurn["Oil"] == 4f,
                "reading the strategic lines does not change the fingerprint");
            Loc.Language = "russian";
            var russian = CommodityLeverage.CoverLines(campaign, campaign.Nations[0]);
            Check(russian[0].Contains("Нефть") && russian[0].Contains("Harbour") &&
                  russian[0].Contains("Склада хватит на 5 дн."),
                "the oil line reads in Russian: " + russian[0]);
            Check(russian[1].Contains("Никто это не продаёт.") &&
                  CampaignFingerprint.Of(campaign) == hash,
                "a missing seller reads in Russian and the fingerprint stays: " + russian[1]);
        }
        finally { Loc.Language = language; }
    }
}
