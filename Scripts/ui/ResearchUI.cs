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

        var pace = nation.ResearchPerWeek > 0f ? nation.ResearchPerWeek : ResearchSystem.WeeklyPoints(_campaign, nation);
        CardRow(rows, "Research a week", pace.ToString("0.0"),
            Tr("From the economy, literacy and what is spent on education, and from what research has already given research."));
        var known = nation.Technologies.Select(id => TechnologyCatalog.Of(id)).Where(technology => technology is not null).ToList();
        CardRow(rows, "Technologies", Simulation.Loc.Format("{0} of {1} known", known.Count, TechnologyCatalog.All.Count),
            known.Count == 0 ? Tr("None yet.") : string.Join("\n", known.Select(technology => Tr(technology.Name))));

        // What can be started next, the one being researched marked. Pressing another keeps what
        // was put into this one: switching costs nothing but time.
        var choices = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        choices.AddThemeConstantOverride("h_separation", 4);
        choices.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(choices);
        foreach (var technology in ResearchSystem.AvailableTo(nation))
        {
            var weeks = ResearchSystem.WeeksLeft(_campaign, nation, technology);
            var caption = weeks < 0
                ? Tr(technology.Name)
                : Simulation.Loc.Format("{0} ({1} wk)", Tr(technology.Name), weeks);
            var captured = technology.Id;
            var button = ActionButton(caption, () => ChooseResearch(captured), 0, 26,
                technology.Id == nation.CurrentResearch);
            button.TooltipText = Ui.Tip(TechnologyTooltip(technology, weeks));
            choices.AddChild(button);
        }
    }

    private void ChooseResearch(string technologyId)
    {
        if (_campaign?.Player is null) return;
        var choose = new SetResearchCommand { IssuerId = _campaign.Player.Id, TechnologyId = technologyId };
        _campaign.Commands.Enqueue(choose);
        _campaign.Commands.ApplyAll(_campaign);
        if (MapSelectionShowing) ShowMapSelectionContent();
    }

    /// <summary>A technology, the way a tooltip reads it: what it is, what it does, what it costs.</summary>
    private string TechnologyTooltip(TechnologyDefinition technology, int weeks)
    {
        var effects = technology.Effects
            .Select(effect => $"{Tr(EffectCaption(effect.Key))} {effect.Value * 100f:+0.#;-0.#}%");
        var needs = technology.Requires.Count == 0
            ? ""
            : "\n" + Simulation.Loc.Format("Requires: {0}", string.Join(", ",
                technology.Requires.Select(id => Tr(TechnologyCatalog.Of(id)?.Name ?? id))));
        var time = weeks < 0 ? "" : "\n" + Simulation.Loc.Format("About {0} week(s) at this pace.", weeks);
        return $"{Tr(technology.Name)}\n{Tr(technology.Description)}\n\n{string.Join("\n", effects)}" +
               "\n" + Simulation.Loc.Format("Costs {0} points, less for every country that already has it.",
                   ResearchSystem.CostFor(_campaign, _campaign.Player, technology).ToString("0")) +
               needs + time;
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
