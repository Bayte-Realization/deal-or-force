using System;
using System.Linq;
using DealOrForce.Simulation;

static class ResearchRouteTests
{
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS research route: " + label); }

    public static void Run()
    {
        TechnologyCatalog.ResetCatalog();
        var campaign = Campaign.CreateBootstrap(73);
        var nation = campaign.NationByTag("POL");
        campaign.ConfigureSinglePlayer(nation.Id);
        nation.Technologies.Clear(); nation.ResearchProgress.Clear(); nation.CurrentResearch = "";
        TechnologyDefinition Tech(string id, float cost, params string[] requires)
        {
            var t = new TechnologyDefinition { Id = id, Name = id, Cost = cost };
            t.Requires.AddRange(requires); return t;
        }
        var root = Tech("root", 10);
        var left = Tech("left", 20, "ROOT");
        var right = Tech("right", 30, "root");
        var leaf = Tech("leaf", 40, "left", "right", "left");
        var all = new[] { leaf, right, root, left, Tech("unrelated", 900) };
        var path = ResearchRoute.Build(all, nation, "LEAF");
        Check(path.Valid && path.Steps.Select(t => t.Id).SequenceEqual(new[] { "root", "left", "right", "leaf" }),
            "diamond and duplicate edges count shared prerequisites once in authored order");
        Check(path.Next == root, "nearest ready prerequisite is inspectable");
        Check(ResearchRoute.Build(all.Reverse(), nation, "leaf").Steps.SequenceEqual(path.Steps), "catalogue enumeration order does not choose the route");
        var price = ResearchRoute.Price(path, nation, t => t.Cost, 6);
        Check(price.RemainingPoints == 100 && price.Weeks == 17, "whole route price sums each project once and rounds weeks once");
        nation.ResearchProgress["root"] = 4; nation.ResearchProgress["right"] = 5;
        Check(ResearchRoute.Price(path, nation, t => t.Cost, 7).RemainingPoints == 91, "partial investments in several projects are deducted");
        Check(ResearchRoute.Price(path, nation, t => t.Cost / 2, 7).RemainingPoints == 41, "effective diffusion prices are supplied, not guessed from base costs");
        nation.Technologies.Add("ROOT");
        path = ResearchRoute.Build(all, nation, "leaf");
        Check(path.Steps.Select(t => t.Id).SequenceEqual(new[] { "left", "right", "leaf" }) && path.Next == left,
            "known branch is removed and next prerequisite updates");
        Check(ResearchRoute.Price(path, nation, t => t.Cost, 5).RemainingPoints == 85, "known projects are not charged even with stale invested points");
        nation.ResearchProgress["left"] = 200;
        Check(ResearchRoute.Price(path, nation, t => t.Cost, 5).RemainingPoints == 65, "over-invested project cannot pay for a different project");
        nation.ResearchProgress["left"] = -9;
        Check(ResearchRoute.Price(path, nation, t => t.Cost, 5).RemainingPoints == 85, "negative legacy progress cannot increase the route bill");
        Check(ResearchRoute.Price(path, nation, t => t.Cost, 0).Weeks is null, "zero pace is not a promised deadline");
        Check(ResearchRoute.Price(path, nation, t => t.Cost, float.NaN).Weeks is null, "non-finite pace is unavailable");
        nation.ResearchProgress["left"] = float.NaN;
        Check(double.IsNaN(ResearchRoute.Price(path, nation, t => t.Cost, 5).RemainingPoints), "corrupt investment is not displayed as a valid price");
        nation.ResearchProgress.Clear();
        Check(ResearchRoute.Price(path, nation, _ => float.PositiveInfinity, 5).Weeks is null, "non-finite project price is unavailable");
        Check(ResearchRoute.Price(path, nation, _ => float.MaxValue, float.Epsilon).Weeks == int.MaxValue, "huge mod projects do not overflow the estimate");
        nation.Technologies.Add("leaf");
        var complete = ResearchRoute.Build(all, nation, "leaf");
        Check(complete.Valid && complete.Steps.Length == 0 && complete.Next is null, "already known goal is complete without retroactively demanding old prerequisites");
        nation.Technologies.Clear();
        leaf.Requires.Add("missing"); leaf.Requires.Add("missing");
        var missing = ResearchRoute.Build(all, nation, "leaf");
        Check(!missing.Valid && missing.Missing.SequenceEqual(new[] { "missing" }) && missing.Next is null, "missing mod dependency is diagnosed once, without a misleading recommendation");
        Check(ResearchRoute.Price(missing, nation, t => t.Cost, 5).Weeks is null, "broken route cannot promise a completion time");
        leaf.Requires.RemoveAll(id => id == "missing"); root.Requires.Add("leaf");
        var cycle = ResearchRoute.Build(all, nation, "leaf");
        Check(!cycle.Valid && cycle.Cyclic.Length > 0 && cycle.Next is null, "cyclic mod route terminates and refuses a false next step");
        root.Requires.Clear();
        Check(!ResearchRoute.Build(all, nation, null).Valid && !ResearchRoute.Build(all, nation, "absent").Valid, "empty and missing targets are safe");
        var deep = Enumerable.Range(0, 4096).Select(i => Tech("deep_" + i, 1, i == 0 ? Array.Empty<string>() : new[] { "deep_" + (i - 1) })).ToArray();
        Check(ResearchRoute.Build(deep, nation, "deep_4095").Steps.Length == 4096, "deep mod graph does not recurse on the call stack");

        var before = CampaignFingerprint.Of(campaign); var rng = campaign.Random.State;
        foreach (var target in TechnologyCatalog.All)
        {
            var route = ResearchRoute.Build(TechnologyCatalog.All, nation, target.Id);
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Check(route.Valid && route.Next is not null && route.Steps.All(t => t.Requires.All(seen.Contains) && seen.Add(t.Id)),
                "base route is reachable, unique and prerequisite-first: " + target.Id);
            Check(target.Effects.Count > 0 && target.Effects.All(e => NationalModifiers.Knows(e.Key) && float.IsFinite(e.Value))
                && float.IsFinite(target.Cost) && target.Cost >= 1, "base discovery has a real effect and valid price: " + target.Id);
            _ = ResearchRoute.Price(route, nation, t => ResearchSystem.CostFor(campaign, nation, t), ResearchSystem.WeeklyPoints(campaign, nation));
        }
        Check(CampaignFingerprint.Of(campaign) == before && campaign.Random.State == rng, "all routes and price previews leave campaign and RNG unchanged");
        var described = new TechnologyDefinition { Id = "described", Name = "label", Description = "description" };
        described.Effects["organisation_recovery"] = .02f;
        var search = new[] { described, root };
        Check(ResearchWorkspace.Browse(search, nation, "", ResearchWorkspace.Filter.All, "полевые", key => key == "description" ? "Полевые исследования" : key).Single() == described,
            "search finds a localised description");
        Check(ResearchWorkspace.Browse(search, nation, "", ResearchWorkspace.Filter.All, "боевого духа", key => key == "Organisation recovery" ? "Восстановление боевого духа" : key).Single() == described,
            "search finds the localised modifier caption, retaining the legacy effect key");
        Check(ResearchWorkspace.Browse(search, nation, "", ResearchWorkspace.Filter.All, "ORGANISATION_RECOVERY", key => key).Single() == described, "modders can search by an effect ID");
        Console.WriteLine("RESEARCH_ROUTE_CATALOG count=" + TechnologyCatalog.All.Count);
        var template = System.IO.Path.GetFullPath(System.IO.Path.Combine(GameDataLoader.DataFolder(), "..", "mods", "_templates", "yaml-data"));
        var errors = new System.Collections.Generic.List<string>();
        try
        {
            using var document = StructuredData.ReadDocument(System.IO.Path.Combine(template, "common", "technologies", "70_research_routes.yml"));
            TechnologyCatalog.Merge(document.RootElement, errors);
            Check(errors.Count == 0 && TechnologyCatalog.Problems().Count == 0, "YAML research example merges without broken references or effect keys");
            var example = TechnologyCatalog.Of("yaml_research_liaison");
            var route = ResearchRoute.Build(TechnologyCatalog.All, nation, "yaml_supply_exchange");
            Check(route.Valid && route.Steps.Length == 4 && route.Next.Id == "digital_education", "YAML example builds a real cross-category research route");
            nation.Technologies.Clear(); NationalModifiers.Invalidate(nation);
            var paceBefore = ResearchSystem.WeeklyPoints(campaign, nation);
            var bonusBefore = NationalModifiers.Of(nation, "research_speed");
            nation.Technologies.Add(example.Id); NationalModifiers.Invalidate(nation);
            var expectedRatio = MathF.Max(.1f, 1 + bonusBefore + .02f) / MathF.Max(.1f, 1 + bonusBefore);
            Check(Math.Abs(NationalModifiers.Of(nation, "research_speed") - bonusBefore - .02f) < .0001f &&
                Math.Abs(ResearchSystem.WeeklyPoints(campaign, nation) / paceBefore - expectedRatio) < .0001f,
                "example discovery increases the existing weekly research formula by its declared effect");
            foreach (var language in new[] { "english", "russian" })
            {
                using var captions = StructuredData.ReadDocument(System.IO.Path.Combine(template, "localization", language, "71_research.yml"));
                Check(new[] { example, TechnologyCatalog.Of("yaml_supply_exchange") }.All(t =>
                    captions.RootElement.TryGetProperty(t.Name, out _) && captions.RootElement.TryGetProperty(t.Description, out _)),
                    "YAML research example supplies every visible caption in " + language);
            }
        }
        finally { TechnologyCatalog.ResetCatalog(); }
    }
}
