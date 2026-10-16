using System;
using System.Linq;
using DealOrForce.Simulation;

static class OpportunityAtlasTests
{
    static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS atlas " + message); }

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(194);
        var nation = campaign.NationByTag("KOR");
        campaign.ConfigureSinglePlayer(nation.Id);
        var before = CampaignFingerprint.Of(campaign);
        var atlas = OpportunityAtlas.Read(campaign, nation);
        Check(CampaignFingerprint.Of(campaign) == before, "reading opportunities leaves campaign unchanged");
        Check(atlas.Formations.Length > 0 && atlas.Formations.All(r => FormableNations.Eligible(nation, r.Definition) && !r.Status.CanForm),
            "only eligible identities are included and missing topology never implies readiness");
        Check(atlas.Decisions.All(r => r.Block == DecisionCatalog.BlockReason(campaign, nation, r.Definition) &&
            r.Cost == DecisionCatalog.Cost(nation, r.Definition)), "decision availability and costs use authoritative rules");
        Check(atlas.Decisions.Select(r => r.Definition.Id).SequenceEqual(OpportunityAtlas.Read(campaign, nation).Decisions.Select(r => r.Definition.Id)),
            "unchanged input produces stable ordering");
        nation.Market.Clear();
        var goods = CommodityCatalog.All.Take(3).ToArray();
        nation.Market[goods[0]] = new MarketQuote { Commodity = goods[0], Demand = 10, Satisfaction = .4f };
        nation.Market[goods[1]] = new MarketQuote { Commodity = goods[1], Demand = 10, Satisfaction = .8f };
        nation.Market[goods[2]] = new MarketQuote { Commodity = goods[2], Demand = 0, Satisfaction = 0 };
        var shortages = OpportunityAtlas.Read(campaign, nation).Shortages;
        Check(shortages.Length == 2 && shortages[0].Good == goods[0], "worst shortages first; zero demand is not a deficit");
        Check(OpportunityAtlas.Read(campaign, nation, .6f).Shortages.Length == 1, "display threshold filters without changing market rules");
        nation.Market[goods[0]].Satisfaction = 1;
        Check(shortages[0].Satisfaction == .4f && OpportunityAtlas.Read(campaign, nation).Shortages.Length == 1,
            "snapshot values are detached and refreshed deficits disappear");
        nation.Treasury = 0;
        Check(OpportunityAtlas.Read(campaign, nation).Decisions.All(r => r.Block.Length > 0), "treasury loss updates readiness");
        nation.FormedNationId = "united_korea";
        Check(OpportunityAtlas.Read(campaign, nation).Formations.Length == 0, "proclaimed identity is not advertised again");
        Check(OpportunityAtlas.Read(null, null).Decisions.Length == 0, "no campaign yields an empty atlas");
        Check(DecisionCatalog.All.All(definition => !string.IsNullOrWhiteSpace(definition.Name) &&
                !string.IsNullOrWhiteSpace(definition.Description)),
            "every decision can fill an action card with a name and a description");
        var scripted = DecisionCatalog.All.Where(definition => definition.Kind == DecisionKind.Scripted).ToArray();
        Check(scripted.Length > 0 && scripted.All(definition => ScriptedEffects.Lines(definition.Effect).Count > 0),
            "every scripted decision prints at least one guaranteed result line on its action card");
    }
}
