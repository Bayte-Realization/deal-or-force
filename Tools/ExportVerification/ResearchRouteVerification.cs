using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyResearchRoute(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        game.VerificationSelectPlayerCountry();
        var n = c.Player;
        // Fixture in this throwaway process only; no user save or preference is written.
        n.Technologies.Clear(); n.ResearchProgress.Clear(); n.CurrentResearch = "";
        n.ResearchProgress["industrial_automation"] = 17;
        game.Call("OpenScreen", "Research");
        game.VerificationInspectResearch("industrial_automation");
        GD.Print("[verify] applying explicit research command on pause");
        (game.FindChild("StartResearch", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2);
        Check(n.CurrentResearch == "industrial_automation" && c.IsPaused && n.ResearchProgress["industrial_automation"] == 17,
            "explicit research command starts immediately on pause and preserves investment");
        Check((game.FindChild("StartResearch", true, false) as Button).Disabled, "active project is not offered as another start");
        // End the command fixture before the four read-only screen comparisons.
        n.CurrentResearch = "";
        var before = CampaignFingerprint.Of(c); var rng = c.Random.State;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
            game.Call("OpenScreen", "Research");
            game.VerificationInspectResearch("fusion_power"); await Wait(.3);
            CheckFits(game.FindChild("ResearchPanel", true, false) as Control, "research route " + language + " " + size.X);
            foreach (var id in new[] { "ResearchZoomIn", "ResearchZoomOut", "ResearchZoomReset", "ResearchZoomLabel" })
                Check(ResearchNodes(game.VerificationResearchLayer).Count(node => node.Name == id) == 1, "only one zoom control exists: " + id);
            var search = game.FindChild("ResearchSearch", true, false) as LineEdit;
            CheckFits(search, "research search " + size.X);
            Check(search.Size.X > 150, "search field retains usable width");
            Check(!(game.FindChild("ResearchZoomIn", true, false) as Control).IsVisibleInTree(), "catalogue does not retain orphan graph zoom controls");
            var start = game.FindChild("StartResearch", true, false) as Button;
            Check(start.Disabled, "locked goal cannot bypass prerequisites");
            var path = ResearchRoute.Build(TechnologyCatalog.All, n, "fusion_power");
            var estimate = ResearchRoute.Price(path, n, t => ResearchSystem.CostFor(c, n, t), ResearchSystem.WeeklyPoints(c, n));
            var summary = game.FindChild("ResearchRouteSummary", true, false) as Label;
            Check(summary.Text.Contains(estimate.RemainingPoints.ToString("0.0")) && summary.Text.Contains(path.Steps.Length.ToString()),
                "route displays today's total after partial investment");
            var next = game.FindChild("ResearchRouteNext", true, false) as Button;
            (game.FindChild("ResearchDetailsScroll", true, false) as ScrollContainer).EnsureControlVisible(next);
            await Wait(.2);
            Check(next.IsVisibleInTree() && !next.Disabled, "next prerequisite is reachable");
            CheckFits(next, "next prerequisite " + size.X);
            await Capture("research-route-" + language + "-" + size.X);
            next.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.2);
            Check(game.VerificationResearchSelected == "industrial_automation" && n.CurrentResearch.Length == 0,
                "route navigation inspects the ready project without starting it");
            Check(!(game.FindChild("StartResearch", true, false) as Button).Disabled, "ready prerequisite exposes the explicit start action");
            search.Text = Loc.Get("Organisation recovery"); await Wait(.2);
            Check(ResearchWorkspace.Browse(TechnologyCatalog.All, n, "", ResearchWorkspace.Filter.All, search.Text, key => Loc.Get(key)).Length > 0,
                "translated modifier search finds a real discovery");
            search.Text = "";
            (game.FindChild("ResearchDependencyView", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            game.VerificationInspectResearch("fusion_power"); await Wait(.3);
            var zoom = game.FindChild("ResearchZoomIn", true, false) as Button;
            Check(zoom.IsVisibleInTree(), "dependency view exposes the single zoom toolbar");
            CheckFits(zoom, "research graph zoom " + size.X);
            var percent = (game.FindChild("ResearchZoomLabel", true, false) as Label).Text;
            zoom.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
            Check((game.FindChild("ResearchZoomLabel", true, false) as Label).Text != percent, "zoom action changes actual graph scale");
            (game.FindChild("ResearchShowPath", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.3);
            Check(ResearchNodes(game.VerificationResearchLayer).OfType<Button>().Count(b => b.Name.ToString().StartsWith("Technology_")) ==
                ResearchWorkspace.DependencyPath(TechnologyCatalog.All, "fusion_power").Count,
                "path view removes unrelated discoveries while retaining shared prerequisites");
            var graph = game.FindChild("ResearchTreeScroll", true, false) as ScrollContainer;
            Check(ResearchNodes(game.VerificationResearchLayer).OfType<Button>().Where(b => b.Name.ToString().StartsWith("Technology_")).All(b =>
                graph.GetGlobalRect().Encloses(b.GetGlobalRect())), "focused base route fits without hiding its goal at the right edge");
            await Capture("research-graph-" + language + "-" + size.X);
            (game.FindChild("ResearchCatalogueView", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng, "research inspection and navigation leave campaign and RNG untouched");
        }
        game.Call("CloseResearchScreen");
    }

    private static IEnumerable<Node> ResearchNodes(Node root)
    {
        var pending = new Stack<Node>(); pending.Push(root);
        for (var visited = 0; visited < 4096 && pending.TryPop(out var node); visited++)
        {
            yield return node;
            foreach (var child in node.GetChildren()) pending.Push(child);
        }
    }
}

public partial class DealOrForceGame
{
    internal void VerificationInspectResearch(string id) => SelectResearchTechnology(id, true);
    internal string VerificationResearchSelected => _researchSelected;
    internal Control VerificationResearchLayer => _researchLayer;
}
