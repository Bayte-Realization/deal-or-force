using System;
using System.Linq;
using DealOrForce.Simulation;

static class ConciliationTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("conciliation: " + label);
        Console.WriteLine("PASS conciliation: " + label);
    }

    public static void Run()
    {
        var c = new Campaign(20261015);
        var nation = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "SWE", Name = "Sweden", IsHuman = true,
            Treasury = 50f, Gdp = 80f, Population = 1_000_000, Stability = 0.5f
        };
        var hot = new ScenarioState
        {
            MapIndex = 5, NationMapIndex = 1, Id = "north", Name = "North",
            Population = 200_000, SecessionPressure = 0.40f, Radicalism = 0.40f,
            PoliticalDivergence = 0.62f, GovernmentApproval = 0.30f, LawApproval = 0.40f
        };
        var quiet = new ScenarioState
        {
            MapIndex = 6, NationMapIndex = 1, Id = "south", Name = "South",
            Population = 200_000, SecessionPressure = 0f, Radicalism = 0.05f,
            PoliticalDivergence = 0.05f, GovernmentApproval = 0.80f, LawApproval = 0.80f
        };
        c.Nations.Add(nation);
        c.States.Add(hot);
        c.States.Add(quiet);
        c.InvalidateStateIndex();
        nation.DomesticCrisis = new DomesticCrisisState();

        Check(DomesticCrises.ConciliationRefusal(c, nation, quiet).Length > 0, "a quiet region is not sold a programme");
        Check(DomesticCrises.ConciliationRefusal(c, nation, hot).Length == 0, "a hot region of ours can be settled");
        var cost = DomesticCrises.ConciliationCost(c, nation, hot);
        Check(cost >= c.Defines.CrisisConciliationCostFloor && cost < nation.Treasury, "the price is a share of the country, and this treasury can pay it");

        var before = CampaignFingerprint.Of(c);
        new ConciliateRegionCommand { IssuerId = nation.Id, StateMapIndex = hot.MapIndex }.Apply(c);
        Check(nation.Treasury < 50f && nation.DomesticCrisis.ConciliationRegion == hot.MapIndex
            && nation.DomesticCrisis.ConciliationUntilDay == c.Defines.CrisisConciliationDays,
            "the payment starts a season on that region and nowhere else");
        Check(CampaignFingerprint.Of(c) != before, "an open settlement is part of the saved world");
        Check(DomesticCrises.ConciliationRefusal(c, nation, quiet).Contains("Another region", StringComparison.Ordinal)
            || DomesticCrises.ConciliationRefusal(c, nation, quiet).Length > 0,
            "a second region waits until this season ends");

        var pressure = hot.SecessionPressure;
        var divergence = hot.PoliticalDivergence;
        var approval = hot.GovernmentApproval;
        var finger = CampaignFingerprint.Of(c);
        Check(SituationReport.Read(c, nation).Any(item => item.Id == "conciliation" && item.Route == SituationRoute.Crises)
            && CampaignFingerprint.Of(c) == finger, "the situation room can see the settlement without touching it");
        DomesticCrises.ReviewConciliation(c, nation, 10);
        Check(hot.SecessionPressure < pressure && hot.PoliticalDivergence < divergence && hot.GovernmentApproval > approval,
            "ten days cool the pull to leave, the distance, and raise acceptance");
        Check(hot.SecessionPressure > 0f, "a season of money does not erase the grievance");

        nation.Treasury = 0f;
        nation.DomesticCrisis.ConciliationUntilDay = -1;
        nation.DomesticCrisis.ConciliationRegion = -1;
        Check(DomesticCrises.ConciliationRefusal(c, nation, hot).Length > 0, "an empty treasury is told no");
        var broke = nation.Treasury;
        new ConciliateRegionCommand { IssuerId = nation.Id, StateMapIndex = hot.MapIndex }.Apply(c);
        Check(nation.Treasury == broke && nation.DomesticCrisis.ConciliationUntilDay < 0, "a refused order changes nothing");
    }
}
