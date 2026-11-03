using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _researchRouteBox;
    private Label _researchRouteSummary, _researchRouteSteps;
    private Button _researchRouteNext;
    private ResearchRoute.Path _researchRoute;
    private Nation _researchRouteNation;
    private string _researchRouteTarget = "";
    private string[] _researchRouteKnown = Array.Empty<string>();
    private int _researchRouteRevision = -1;

    private void FitResearchRoute()
    {
        if (!_researchPathOnly || !_researchGraph || !_researchLayer.Visible ||
            _researchScroll.Size.X <= 40 || _researchScroll.Size.Y <= 40) return;
        // Fit the focused chain, not all 136 projects; never shrink below the shared
        // readable zoom limit. Larger modded routes keep their ordinary scrollbars.
        var room = _researchScroll.Size - new Vector2(24, 24);
        _researchZoom = Math.Clamp(Math.Min(room.X / _researchExtent.X, room.Y / _researchExtent.Y), TreeViewport.MinZoom, 1f);
        _researchViewport.CustomMinimumSize = _researchExtent * _researchZoom;
        _researchTree.Scale = Vector2.One * _researchZoom;
        _researchScroll.ScrollHorizontal = _researchScroll.ScrollVertical = 0;
        UpdateResearchZoomLabel();
    }

    private void BuildResearchRoute()
    {
        _researchRouteBox = new VBoxContainer { Name = "ResearchRoute" };
        _researchRouteBox.AddThemeConstantOverride("separation", 6);
        _researchDetails.AddChild(_researchRouteBox);
        _researchRouteBox.AddChild(Title("ROUTE TO DISCOVERY", 12, Ui.Colour("text.caption")));
        _researchRouteSummary = Title("", 13, Ui.Colour("text.body"));
        _researchRouteSummary.Name = "ResearchRouteSummary";
        _researchRouteBox.AddChild(_researchRouteSummary);
        _researchRouteSteps = Title("", 13, Ui.Colour("text.muted"));
        _researchRouteSteps.Name = "ResearchRouteSteps";
        _researchRouteBox.AddChild(_researchRouteSteps);
        _researchRouteNext = ActionButton("Inspect next prerequisite", () =>
        {
            // Inspection only. Starting still requires the existing explicit Start button,
            // whose command rechecks prerequisites and feature gates.
            if (_researchRoute?.Next is { } next) SelectResearchTechnology(next.Id, true);
        }, 0, 34);
        _researchRouteNext.Name = "ResearchRouteNext";
        _researchRouteBox.AddChild(_researchRouteNext);
    }

    private void RefreshResearchRoute(TechnologyDefinition target, float pace)
    {
        var nation = _campaign.Player;
        // Day, funding and prices do not change the graph. Equal-size known-list replacements
        // do: a mod may remove one discovery and grant another in the same operation.
        if (_researchRoute is null || _researchRouteNation != nation || _researchRouteTarget != target.Id
            || _researchRouteRevision != TechnologyCatalog.Revision || !_researchRouteKnown.SequenceEqual(nation.Technologies))
        {
            _researchRoute = ResearchRoute.Build(TechnologyCatalog.All, nation, target.Id);
            _researchRouteNation = nation; _researchRouteTarget = target.Id;
            _researchRouteRevision = TechnologyCatalog.Revision;
            _researchRouteKnown = nation.Technologies.ToArray();
        }
        var path = _researchRoute;
        _researchRouteBox.Visible = !path.Valid || path.Steps.Length > 1;
        if (!_researchRouteBox.Visible) return;
        _researchRouteNext.Visible = path.Next is not null && !string.Equals(path.Next.Id, target.Id, StringComparison.OrdinalIgnoreCase);
        if (!path.Valid)
        {
            _researchRouteSummary.Text = Tr("This route contains missing or cyclic prerequisites. Check the content package.");
            _researchRouteSteps.Text = string.Join(", ", path.Missing.Concat(path.Cyclic));
            _researchRouteSummary.TooltipText = _researchRouteSteps.TooltipText = "";
            return;
        }
        // Share the workspace's per-refresh diffusion cache. An off-page prerequisite may
        // not yet be priced by the visible catalogue cards; never price it twice here.
        float Cost(TechnologyDefinition step)
        {
            if (!_researchCosts.TryGetValue(step.Id, out var cost))
                _researchCosts[step.Id] = cost = ResearchSystem.DiffusionFor(_campaign, nation, step);
            return cost.EffectiveCost;
        }
        var estimate = ResearchRoute.Price(path, nation, Cost, pace);
        _researchRouteSummary.Text = !double.IsFinite(estimate.RemainingPoints) ? Tr("Route estimate unavailable.") : Loc.Format("{0} discoveries remaining · {1} points in total",
            path.Steps.Length, estimate.RemainingPoints.ToString("0.0")) + "\n" +
            (estimate.Weeks.HasValue ? Loc.Format("About {0} week(s) for the route at today's pace.", estimate.Weeks.Value)
                : Tr("No research progress at the current pace."));
        var caveat = Tr("Not an automatic queue. Known discoveries and invested points are deducted. Prices and research pace may change along the way.");
        _researchRouteSummary.TooltipText = Ui.Tip(caveat);
        var lines = path.Steps.Select((step, index) => $"{index + 1}. {Tr(step.Name)}").ToArray();
        _researchRouteSteps.Text = string.Join("\n", lines.Take(4)) +
            (lines.Length > 4 ? "\n" + Loc.Format("… and {0} more discoveries", lines.Length - 4) : "");
        _researchRouteSteps.TooltipText = Ui.Tip(string.Join("\n", lines) + "\n\n" + caveat);
        _researchRouteNext.TooltipText = Ui.Tip(path.Next is null ? "" : Tr(path.Next.Name));
    }
}
