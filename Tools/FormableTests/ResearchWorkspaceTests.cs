using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class ResearchWorkspaceTests
{
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS research workspace: " + label); }

    public static void Run()
    {
        TechnologyCatalog.ResetCatalog();
        var campaign = Campaign.CreateBootstrap(73);
        var nation = campaign.NationByTag("POL");
        campaign.ConfigureSinglePlayer(nation.Id);
        nation.Technologies.Clear(); nation.ResearchProgress.Clear(); nation.CurrentResearch = "industrial_automation";
        var all = TechnologyCatalog.All;
        var automation = TechnologyCatalog.Of("industrial_automation");
        var education = TechnologyCatalog.Of("digital_education");
        var locked = TechnologyCatalog.Of("autonomous_distribution");
        Check(ResearchWorkspace.Status(nation, automation) == ResearchWorkspace.State.Active, "active project has an explicit state");
        Check(ResearchWorkspace.Status(nation, education) == ResearchWorkspace.State.Available, "available project is inspectable without starting it");
        Check(ResearchWorkspace.Status(nation, locked) == ResearchWorkspace.State.Blocked, "missing prerequisites are explicit");
        Check(ResearchWorkspace.CanStart(campaign, nation, education) && !ResearchWorkspace.CanStart(campaign, nation, automation)
            && !ResearchWorkspace.CanStart(campaign, nation, locked), "start availability distinguishes available, active and locked projects");
        TechnologyDefinition[] Browse(string category = "", ResearchWorkspace.Filter filter = ResearchWorkspace.Filter.All, string query = "")
            => ResearchWorkspace.Browse(all, nation, category, filter, query, text => text);
        Check(Browse().Length == all.Count, "default catalogue includes every branch");
        Check(Browse("INDUSTRY").All(technology => technology.Category == "industry"), "category matching is case insensitive");
        Check(Browse(query: " INDUSTRIAL_AUTOMATION ").Single() == automation, "search by ID trims whitespace and ignores case");
        var translated = ResearchWorkspace.Browse(all, nation, "", ResearchWorkspace.Filter.All, "ОбРаЗоВаНиЕ", text => text == education.Name ? "Цифровое образование" : text);
        Check(translated.Single() == education, "search uses the displayed localised name");
        Check(Browse(query: "no_such_technology_739").Length == 0, "empty results do not fall back to unrelated technologies");
        Check(Browse(filter: ResearchWorkspace.Filter.Available).Contains(automation), "ready filter retains active project");
        Check(Browse(filter: ResearchWorkspace.Filter.Blocked).All(technology => !ResearchSystem.Available(nation, technology)), "blocked filter only shows missing prerequisites");
        nation.ResearchProgress[education.Id] = 17;
        Check(Browse(filter: ResearchWorkspace.Filter.Invested).Single() == education, "invested filter finds a paused project");
        nation.Technologies.Add(education.Id);
        Check(ResearchWorkspace.Status(nation, education) == ResearchWorkspace.State.Known, "completion takes precedence over invested state");
        Check(Browse(filter: ResearchWorkspace.Filter.Known).Single() == education && Browse(filter: ResearchWorkspace.Filter.Invested).Length == 0, "known discoveries are not unfinished invested projects");
        nation.Technologies.Clear(); nation.Technologies.Add(automation.Id);
        Check(Browse(filter: ResearchWorkspace.Filter.Known).Single() == automation, "same-size known set replacement updates results");

        var root = new TechnologyDefinition { Id = "root", Category = "society" };
        var branch = new TechnologyDefinition { Id = "branch", Category = "industry" }; branch.Requires.Add("root");
        var leaf = new TechnologyDefinition { Id = "leaf", Category = "military" }; leaf.Requires.AddRange(new[] { "branch", "missing" });
        var other = new TechnologyDefinition { Id = "other" };
        var definitions = new[] { root, branch, leaf, other };
        Check(ResearchWorkspace.DependencyPath(definitions, "LEAF").SetEquals(new[] { "leaf", "branch", "root" }), "dependency path crosses categories and excludes unrelated nodes");
        root.Requires.Add("leaf");
        Check(ResearchWorkspace.DependencyPath(definitions, "leaf").Count == 3, "cyclic mod dependencies terminate");
        Check(ResearchWorkspace.DependencyPath(definitions, "missing").Count == 0 && ResearchWorkspace.DependencyPath(definitions, null).Count == 0, "missing and empty selection are safe");
        Check(ResearchWorkspace.Weeks(12, 5) == 3 && ResearchWorkspace.Weeks(-1, 5) == 0, "time estimate rounds up and never becomes negative");
        Check(ResearchWorkspace.Weeks(12, 0) is null && ResearchWorkspace.Weeks(float.NaN, 1) is null && ResearchWorkspace.Weeks(1, float.PositiveInfinity) is null, "unavailable estimates are not misleading negative weeks");
        Check(ResearchWorkspace.Weeks(float.MaxValue, .00001f) == int.MaxValue, "very long mod projects do not overflow");

        var before = CampaignFingerprint.Of(campaign);
        for (var i = 0; i < 20; i++)
        {
            foreach (var filter in Enum.GetValues<ResearchWorkspace.Filter>()) _ = Browse(filter: filter);
            _ = ResearchWorkspace.DependencyPath(all, locked.Id);
            _ = ResearchSystem.DiffusionFor(campaign, nation, locked);
        }
        Check(before == CampaignFingerprint.Of(campaign), "repeated browsing, paths and price previews preserve complete campaign fingerprint");
        var command = new SetResearchCommand { IssuerId = nation.Id, TechnologyId = education.Id };
        command.Apply(campaign);
        Check(command.Set && nation.CurrentResearch == education.Id && nation.ResearchProgress[education.Id] == 17, "actual command switches project without losing invested points");
        campaign.SetFeature("research", false);
        before = CampaignFingerprint.Of(campaign);
        _ = Browse(); _ = ResearchWorkspace.DependencyPath(all, locked.Id);
        Check(before == CampaignFingerprint.Of(campaign), "disabled mechanics remain read-only and inspectable");
        var rejected = new SetResearchCommand { IssuerId = nation.Id, TechnologyId = "digital_education" };
        rejected.Apply(campaign);
        Check(!rejected.Set && before == CampaignFingerprint.Of(campaign), "disabled research command does not spend progress or mutate the campaign");
        Check(!ResearchWorkspace.CanStart(campaign, nation, education), "disabled research is not offered as a working action");
        campaign.SetFeature("research", true);
        nation.CurrentResearch = "";
        campaign.Defines.FeatureCommands[nameof(SetResearchCommand)] = "research_orders";
        campaign.SetFeature("research_orders", false);
        Check(!ResearchWorkspace.CanStart(campaign, nation, education), "scenario command binding disables the action independently of the research system");
        campaign.SetFeature("research_orders", true);
        Check(ResearchWorkspace.CanStart(campaign, nation, education), "scenario can re-enable research orders without rebuilding the catalogue");
        TechnologyCatalog.ResetCatalog();
    }
}
