using System;
using System.Collections.Generic;
using System.Text.Json;
using DealOrForce.Simulation;

static class ConstructionPlacementTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS construction placement: " + message); }
    private static void Merge(string json, List<string> errors)
    { using var doc = JsonDocument.Parse(json); BuildingRules.Merge(doc.RootElement, errors); }

    public static void Run()
    {
        var c = new Campaign(61106); c.Date = new GameDate(-299, 6, 1);
        var n = new Nation { Id = 0, Tag = "BUILD", MapIndex = 11, Treasury = 100, Population = 1000000 };
        var s = new ScenarioState { Id = "land", MapIndex = 222, NationId = n.Tag, NationMapIndex = n.MapIndex, Population = 1000000 };
        c.Nations.Add(n); c.States.Add(s);
        var errors = new List<string>();
        try
        {
            Merge("""{"civilian_industry":{"enabled":false}}""", errors);
            Check(errors.Count == 0 && ConstructionAdmission.Refusal(c, n, s, BuildingKind.CivilianIndustry).Length > 0,
                "underscored data ID disables a compiled modern building");
            new QueueConstructionCommand { IssuerId = n.Id, StateMapIndex = s.MapIndex, Building = BuildingKind.CivilianIndustry }.Apply(c);
            Check(c.Construction.Count == 0 && n.Treasury == 100, "disabled building is rejected at command boundary without charge");
            Check(BuildingCatalog.Declare("bronze_workshop", "Bronze workshop", .2f, 40, .02f, "A scenario-defined foundry.", jobs: .02f), "era-specific building is declared without a new enum");
            Check(BuildingCatalog.TryParse("bronze_workshop", out var bronze), "custom building has a stable data ID");
            Merge("""{"bronze_workshop":{"available_from":"0300-01-01 BCE","available_until":"0100-12-31 BCE","requires_technologies":["bronze_casting"]}}""", errors);
            Check(errors.Count == 0, "BCE date and mod technology rules load");
            var before = CampaignFingerprint.Of(c);
            var refusal = ConstructionAdmission.Refusal(c, n, s, bronze);
            Check(refusal.Contains("bronze_casting") && CampaignFingerprint.Of(c) == before, "missing custom research is explained without mutating the world");
            n.Technologies.Add("bronze_casting");
            Check(ConstructionAdmission.Refusal(c, n, s, bronze).Length == 0, "custom researched workshop is buildable inside its historical window");
            c.Date = new GameDate(-300, 1, 1);
            Check(ConstructionAdmission.Refusal(c, n, s, bronze).Length > 0, "before 300 BCE is too early");
            c.Date = new GameDate(-299, 1, 1);
            Check(ConstructionAdmission.Refusal(c, n, s, bronze).Length == 0, "first permitted BCE date is inclusive");
            c.Date = new GameDate(-99, 12, 31);
            Check(ConstructionAdmission.Refusal(c, n, s, bronze).Length == 0, "last permitted BCE date is inclusive");
            c.Date = new GameDate(-98, 1, 1);
            Check(ConstructionAdmission.Refusal(c, n, s, bronze).Length > 0, "expired workshop cannot be newly queued");
            Merge("""{"bronze_workshop":{"available_from":null,"available_until":null}}""", errors);
            Check(ConstructionAdmission.Refusal(c, n, s, bronze).Length == 0, "fantasy or timeless scenarios can clear date windows");
            Merge("""{"bronze_workshop":{"available_from":"not a date","enabled":false}}""", errors);
            Check(errors.Count == 1 && ConstructionAdmission.Refusal(c, n, s, bronze).Length == 0, "malformed override does not partially replace the working rule");
            before = CampaignFingerprint.Of(c);
            Check(ConstructionAdmission.Refusal(c, n, null, bronze).Length > 0 && ConstructionAdmission.Refusal(c, n, s, (BuildingKind)9999).Length > 0,
                "no ground and undeclared kinds are rejected");
            Check(CampaignFingerprint.Of(c) == before, "preview does not alter state or reserve a site");
            new QueueConstructionCommand { IssuerId = n.Id, StateMapIndex = s.MapIndex, Building = bronze }.Apply(c);
            Check(c.Construction.Count == 1 && c.Construction[0].StateMapIndex == s.MapIndex && c.Construction[0].Building == bronze,
                "one accepted map order queues the requested building in the requested region");
            s.NationMapIndex = 12;
            new QueueConstructionCommand { IssuerId = n.Id, StateMapIndex = s.MapIndex, Building = bronze }.Apply(c);
            Check(c.Construction.Count == 1, "foreign territory cannot be built on by forging a map order");
            s.NationMapIndex = n.MapIndex; n.Treasury = 0;
            Check(ConstructionAdmission.Refusal(c, n, s, bronze).Length > 0, "cash refusal uses the actual initial contribution, not full contract price");
            n.Treasury = 100; c.Defines.MaxConstructionProjects = 1;
            // Site count is scaled by ConstructionCost; pin the feature's base rules for this fixture.
            c.Defines.ConstructionSitesInParallel = false;
            Check(ConstructionAdmission.Refusal(c, n, s, bronze).Length > 0, "full national queue is refused");
        }
        finally { BuildingCatalog.ResetCatalog(); ProductionChains.ResetCatalog(); }
    }
}
