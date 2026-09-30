using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class TechnologyTreeTests
{
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS research tree: " + label); }
    public static void Run()
    {
        var errors = new List<string>();
        TechnologyCatalog.ResetCatalog(errors);
        Check(errors.Count == 0 && TechnologyCatalog.Problems().Count == 0, "catalogue has valid effects and an acyclic dependency graph");
        Check(TechnologyCatalog.All.Count >= 61, "61 technologies across four research branches");
        var layout = TechnologyTreeLayout.Build(TechnologyCatalog.All);
        Check(layout.Length == TechnologyCatalog.All.Count && layout.Select(n => (n.Column, n.Row)).Distinct().Count() == layout.Length, "each card owns a distinct grid slot");
        var slots = layout.ToDictionary(n => n.Id);
        Check(TechnologyCatalog.All.All(t => t.Requires.All(id => slots[id].Column < slots[t.Id].Column)), "automatic columns respect all cross-category prerequisites");
        Check(TechnologyTreeLayout.Build(TechnologyCatalog.All, "society").All(n => TechnologyCatalog.Of(n.Id).Category == "society"), "category filtering retains only its branch");
        Check(layout.SequenceEqual(TechnologyTreeLayout.Build(TechnologyCatalog.All)), "layout is deterministic");
        var campaign = Campaign.CreateBootstrap(73);
        var nation = campaign.NationByTag("KOR");
        campaign.ConfigureSinglePlayer(nation.Id);
        nation.Technologies.Clear(); nation.ResearchProgress.Clear(); nation.CurrentResearch = "";
        var locked = new SetResearchCommand { IssuerId = nation.Id, TechnologyId = "autonomous_distribution" };
        locked.Apply(campaign);
        Check(!locked.Set, "locked project cannot be started");
        var root = new SetResearchCommand { IssuerId = nation.Id, TechnologyId = "industrial_automation" };
        root.Apply(campaign);
        nation.ResearchProgress["industrial_automation"] = 17;
        var change = new SetResearchCommand { IssuerId = nation.Id, TechnologyId = "digital_education" };
        change.Apply(campaign);
        Check(root.Set && change.Set && nation.CurrentResearch == "digital_education" && nation.ResearchProgress["industrial_automation"] == 17, "switch preserves progress and updates the real project");
        nation.Technologies.Add("industrial_automation");
        Check(ResearchSystem.Available(nation, TechnologyCatalog.Of("precision_agriculture")), "new project unlocks from existing technology");
        _ = ResearchSystem.Bonus(nation, "output");
        try
        {
            using var overlay = JsonDocument.Parse("""
            {"industrial_automation":{"effects":{"output":0.123},"tree_column":3,"tree_row":2,"icon":"custom_sprite"},
             "test_custom":{"category":"oceanography","tree_column":3,"tree_row":2,"requires":["industrial_automation"]}}
            """);
            TechnologyCatalog.Merge(overlay.RootElement, errors);
            Check(Math.Abs(ResearchSystem.Bonus(nation, "output") - .123f) < .0001, "effect overlay invalidates research bonus cache");
            Check(TechnologyCatalog.Of("industrial_automation").Icon == "custom_sprite" && TechnologyCatalog.Of("industrial_automation").TreeColumn == 3, "mod overrides icon and explicit position");
            Check(TechnologyCatalog.Problems().Count == 0, "custom categories accepted");
            var manual = TechnologyTreeLayout.Build(TechnologyCatalog.All);
            Check(manual.Select(n => (n.Column, n.Row)).Distinct().Count() == manual.Length, "colliding mod positions separate automatically");
            using var malformed = JsonDocument.Parse("""{"test_custom":{"cost":"bad","tree_column":-3,"effects":{"output":"bad"}}} """);
            TechnologyCatalog.Merge(malformed.RootElement, errors);
            Check(errors.Count == 3 && TechnologyCatalog.Of("test_custom").TreeColumn == 3, "malformed numbers diagnosed without crashing or destroying previous coordinates");
            using var clear = JsonDocument.Parse("""{"test_custom":{"tree_column":null,"tree_row":null,"requires":["test_custom"]}}""");
            TechnologyCatalog.Merge(clear.RootElement);
            Check(TechnologyCatalog.Of("test_custom").TreeColumn is null && TechnologyCatalog.Problems().Count > 0 && TechnologyTreeLayout.Build(TechnologyCatalog.All).Length == TechnologyCatalog.All.Count, "null restores auto-layout and a cyclic mod remains inspectable");
        }
        finally { TechnologyCatalog.ResetCatalog(); }
    }
}
