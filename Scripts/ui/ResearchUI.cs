using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Research on the country panel (item 62): what is being researched and how far along it is,
/// what a week of research amounts to, and what can be started next - one button each, saying
/// how long it would take and, under the cursor, what it would do.
/// </summary>
public partial class DealOrForceGame
{
    /// <summary>The research block of one's own country panel.</summary>
    private void AddResearchRows(VBoxContainer rows)
    {
        if (TechnologyCatalog.All.Count == 0 || _campaign?.Player is null) return;
        var nation = _campaign.Player;
        rows.AddChild(Title("RESEARCH", 11, Ui.Colour("text.caption")));

        var current = TechnologyCatalog.Of(nation.CurrentResearch);
        if (current is not null)
        {
            var cost = ResearchSystem.CostFor(_campaign, nation, current);
            var done = cost <= 0f ? 1f : M.Clamp01(nation.ResearchProgress.GetValueOrDefault(current.Id) / cost);
            var weeks = ResearchSystem.WeeksLeft(_campaign, nation, current);
            CardRow(rows, "Researching", $"{Tr(current.Name)} · {Fmt.Percent(done)}",
                TechnologyTooltip(current, weeks), "text.accent");
        }
        else
            CardRow(rows, "Researching", Tr("nothing yet - it starts with the week"), null, "text.muted");

        var pace = ResearchSystem.WeeklyPoints(_campaign, nation);
        CardRow(rows, "Research a week", pace.ToString("0.0"),
            Tr("Research pace depends on the economy, literacy, education, universities, laboratories, stability and previous discoveries."));
        var known = nation.Technologies.Select(id => TechnologyCatalog.Of(id)).Where(technology => technology is not null).ToList();
        CardRow(rows, "Technologies", Simulation.Loc.Format("{0} of {1} known", known.Count, TechnologyCatalog.All.Count),
            known.Count == 0 ? Tr("None yet.") : string.Join("\n", known.Select(technology => Tr(technology.Name))));

        rows.AddChild(ActionButton("Open technology tree", () => OpenScreen("Research"), 0, 32));
    }

    private void ChooseResearch(string technologyId)
    {
        if (_campaign?.Player is null) return;
        var choose = new SetResearchCommand { IssuerId = _campaign.Player.Id, TechnologyId = technologyId };
        _campaign.Commands.Enqueue(choose);
        _campaign.Commands.ApplyAll(_campaign);
        if (MapSelectionShowing) ShowMapSelectionContent();
        RefreshResearchScreen();
    }

    /// <summary>A technology, the way a tooltip reads it: what it is, what it does, what it costs.</summary>
    private string TechnologyTooltip(TechnologyDefinition technology, int weeks)
    {
        var effects = NationalModifiers.Lines(technology.Effects);
        var needs = technology.Requires.Count == 0
            ? ""
            : "\n" + Simulation.Loc.Format("Requires: {0}", string.Join(", ",
                technology.Requires.Select(id => Tr(TechnologyCatalog.Of(id)?.Name ?? id))));
        var time = weeks < 0 ? "" : "\n" + Simulation.Loc.Format("About {0} week(s) at this pace.", weeks);
        return $"{Tr(technology.Name)}\n{Tr(technology.Description)}\n\n{string.Join("\n", effects)}" +
               "\n" + Simulation.Loc.Format("Effective cost: {0} research points.",
                   ResearchSystem.CostFor(_campaign, _campaign.Player, technology).ToString("0")) +
               "\n" + ResearchDiffusionText(technology) + needs + time;
    }

    private string ResearchDiffusionText(TechnologyDefinition technology)
    {
        var diffusion = ResearchSystem.DiffusionFor(_campaign, _campaign.Player, technology);
        return Simulation.Loc.Format("Knowledge spread: world {0}, neighbours {1}, partners {2}; total discount {3}.",
            Fmt.Percent(diffusion.World), Fmt.Percent(diffusion.Neighbours),
            Fmt.Percent(diffusion.Partners), Fmt.Percent(diffusion.Total));
    }

    private static string EffectCaption(string effect) => effect switch
    {
        "output" => "Output",
        "research_speed" => "Research",
        "army_attack" => "Divisions' attack",
        "army_defense" => "Divisions' defence",
        "army_speed" => "March speed",
        "supply" => "Supply",
        _ => effect
    };
}
